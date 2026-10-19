using System.Text.Json;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Collects and independently verifies the shared node, function and global declaration graph.
/// </summary>
internal static class NativeBindingCollectionCommand
{
    /// <summary>
    /// Measures complete declaration identities using Clang independently of the native layout compiler.
    /// </summary>
    internal static Task<NativeBindingCollection> RunAsync(NativeBindingCatalog catalog, string[] arguments, CancellationToken cancellationToken)
        => RunAsync(catalog, NativeBindingResources.ReadRawCatalog(catalog.PostgresMajor), arguments, cancellationToken);

    /// <summary>
    /// Measures complete declaration identities for a given function and global inventory.
    /// </summary>
    internal static async Task<NativeBindingCollection> RunAsync(NativeBindingCatalog catalog, NativeBindingRawCatalog inventory, string[] arguments,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int major = catalog.PostgresMajor;
        IReadOnlyList<NativeHeaderRequest> required = NativeBindingHeaderHelpers.Requests(inventory);
        PostgresInstallation installation = arguments[1].Length == 0
            ? await PostgresInstallation.DiscoverAsync(major, cancellationToken)
            : await PostgresInstallation.CreateAsync(arguments[1], cancellationToken);
        if (installation.Version.Major != major)
        {
            throw new InvalidOperationException($"Expected PostgreSQL {major}, but the selected installation is {installation.Label}.");
        }

        string compiler = await ClangSelection.ResolveAsync(arguments.Length >= 8 ? arguments[7] : null, cancellationToken);
        // Windows process working directories cannot exceed MAX_PATH, even when managed file APIs support longer paths.
        string directory = Directory.CreateTempSubdirectory("ankus-node-").FullName;
        try
        {
            string includes = NativeBindingResources.ReadHeaders(major);
            string headers = NativeBindingHeaderTarget.GenerateSource(includes, major);
            string file = Path.Join(directory, "native-node-types.c");
            await File.WriteAllTextAsync(file, headers, cancellationToken);
            string[] frontend = ["", arguments[0], arguments[1], directory, compiler,
                arguments.Length >= 5 ? arguments[4] : "", arguments.Length >= 6 ? arguments[5] : "", arguments.Length >= 7 ? arguments[6] : ""];
            string observations = Path.Join(directory, "native-node-target.json");
            await NativeBindingHeaderCommand.InspectAsync(installation, frontend, file, observations, directory, cancellationToken);
            NativeBindingAvailability availability;
            NativeBindingCatalog selected;
            await using (FileStream stream = File.OpenRead(observations))
            {
                using JsonDocument document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 512 }, cancellationToken);
                availability = NativeBindingHeaderAvailability.Read(document.RootElement, inventory, required);
                selected = NativeBindingNodeTags.Select(catalog, NativeBindingNodeTags.Read(document.RootElement));
            }

            NativeBindingNodeRoots roots = NativeBindingNodeRecords.CreateRoots(selected, includes, NativeBindingHeaderHelpers.RequiredTypes);
            headers = NativeBindingHeaderTarget.GenerateSource(roots.Source, major);
            NativeHeaderRequest[] requests = [.. roots.Requests, .. availability.Available];
            await File.WriteAllTextAsync(file, NativeBindingHeaderParser.GenerateSource(headers, requests), cancellationToken);
            await NativeBindingHeaderCommand.InspectAsync(installation, frontend, file, observations, directory, cancellationToken);
            NativeHeaderTarget target;
            IReadOnlyDictionary<string, NativeHeaderSymbol> symbols;
            await using (FileStream stream = File.OpenRead(observations))
            {
                using JsonDocument document = await JsonDocument.ParseAsync(stream, new JsonDocumentOptions { MaxDepth = 512 }, cancellationToken);
                target = NativeBindingHeaderTarget.Read(document.RootElement, major);
                symbols = NativeBindingHeaderParser.Read(document.RootElement, requests);
                NativeBindingAvailability current = NativeBindingHeaderAvailability.Read(document.RootElement, inventory, required);
                if (!current.Available.SequenceEqual(availability.Available) || !current.Absent.SequenceEqual(availability.Absent))
                {
                    throw new InvalidOperationException("Native declaration availability changed during collection.");
                }
            }

            string library = arguments.Length == 9 && arguments[8].Length != 0 ? Path.GetFullPath(arguments[8])
                : await NativeBindingRecordCommand.FindLibraryAsync(NativeBindingRecordCommand.FindCompiler(compiler), target.ClangMajor, cancellationToken);
            string ast = Path.Join(directory, "native-node-types.ast");
            await NativeBindingHeaderCommand.InspectAsync(installation, frontend, file, Path.Join(directory, "native-node-compile.txt"),
                directory, cancellationToken, dumpAst: false, serializedAst: ast);
            var request = new NativeRecordRequest(ast, library, target, requests.ToDictionary(static value => value.Name, StringComparer.Ordinal));
            NativeRecordGraph graph = await NativeBindingRecordWorker.InspectAsync(request, directory, cancellationToken);
            var records = new NativeHeaderRecords(new(target, symbols), graph);
            await VerifyAsync(records, roots.Source, installation, arguments, directory, cancellationToken);
            Console.WriteLine($"PG{major}: verified {graph.Declarations.Count} shared native declarations, {availability.Available.Count} available inventory entries and {availability.Absent.Count} absent entries.");
            return new(records, availability);
        }
        finally
        {
            // Workers and compiler streams have joined before releasing their large temporary AST artifacts.
            await NativeBuildDirectory.DeleteAsync(directory);
        }
    }

    /// <summary>
    /// Rechecks every collected declaration and physical bitfield with the current native compiler and headers.
    /// </summary>
    internal static async Task VerifyAsync(NativeHeaderRecords records, string headers, PostgresInstallation installation,
        string[] arguments, string directory, CancellationToken cancellationToken)
    {
        string definitions = NativeBindingHeaderHelpers.Definitions(records.Headers.Target.PostgresVersion / 10000, records.Headers.Symbols.Values);
        if (definitions.Length != 0)
        {
            string helperSource = Path.Join(directory, "native-helper-checks.c");
            await File.WriteAllTextAsync(helperSource, definitions + headers, cancellationToken);
            string compiler = arguments.Length >= 4 && arguments[3].Length != 0 ? arguments[3]
                : OperatingSystem.IsWindows() ? "cl.exe" : "cc";
            string[] frontend = ["", arguments[0], arguments[1], directory, compiler,
                arguments.Length >= 5 ? arguments[4] : "", arguments.Length >= 6 ? arguments[5] : "", arguments.Length >= 7 ? arguments[6] : ""];
            if (OperatingSystem.IsWindows() && Path.GetFileNameWithoutExtension(compiler).Equals("cl", StringComparison.OrdinalIgnoreCase))
            {
                frontend[7] = "";
            }

            await NativeBindingHeaderCommand.InspectAsync(installation, frontend, helperSource, Path.Join(directory, "native-helper-checks.txt"),
                directory, cancellationToken, dumpAst: false, inspectBodies: true);
        }

        string checks = NativeBindingRecordChecks.Generate(records, headers) + NativeBindingRecordChecks.ExecutableEntryPoint;
        string verification = Path.Join(directory, "native-record-checks.c");
        await File.WriteAllTextAsync(verification, checks, cancellationToken);
        string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "checks.exe" : "checks");
        await NativeBindingLayoutCommand.CompileProbeAsync(installation, arguments.Length > 7 ? arguments[..7] : arguments,
            verification, executable, directory, cancellationToken, requireC11: true);
    }
}

/// <summary>
/// Couples independently verified native declarations with their complete selected-header availability partition.
/// </summary>
/// <param name="Records">The shared node, function and global signature and storage graph.</param>
/// <param name="Availability">Every raw inventory request, including declarations absent on this target.</param>
internal sealed record NativeBindingCollection(NativeHeaderRecords Records, NativeBindingAvailability Availability);
