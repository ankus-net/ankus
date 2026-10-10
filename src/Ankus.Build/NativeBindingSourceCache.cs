using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Reuses semantic collection only after observing current preprocessing and repeating native contract verification.
/// </summary>
internal static class NativeBindingSourceCache
{
    /// <summary>
    /// Names the location-independent source artifacts copied into each consuming project.
    /// </summary>
    internal static IReadOnlyList<string> Artifacts { get; } = Array.AsReadOnly<string>(
    [
        "native-records.json", "native-availability.json", "native-binding.g.cs", "native-binding.assembly-name", "native-binding.identity",
        "native-layout.c", "native-layout.txt", "native-layout.json", "native-node-availability.json", "native-node-declarations.json",
    ]);

    /// <summary>
    /// Copies content-verified sources after checking a private snapshot against the current native compiler and headers.
    /// </summary>
    internal static async Task CopyAsync(int major, string[] arguments, string cache, CancellationToken cancellationToken)
    {
        string directory = Directory.CreateTempSubdirectory("ankus-source-").FullName;
        try
        {
            PostgresInstallation installation = arguments[1].Length == 0
                ? await PostgresInstallation.DiscoverAsync(major, cancellationToken)
                : await PostgresInstallation.CreateAsync(arguments[1], cancellationToken);
            string compiler = NativeBindingRecordCommand.FindCompiler(arguments.Length >= 8 && arguments[7].Length != 0 ? arguments[7]
                : OperatingSystem.IsWindows() ? "clang-cl.exe" : "clang");
            string[] frontend = ["", arguments[0], arguments[1], directory, compiler,
                arguments.Length >= 5 ? arguments[4] : "", arguments.Length >= 6 ? arguments[5] : "", arguments.Length >= 7 ? arguments[6] : ""];
            List<string> options = await NativeBindingHeaderCommand.CreateArgumentsAsync(installation, frontend, cancellationToken);
            NativeBindingPreprocessed manifest = await NativeBindingPreprocessor.ObserveAsync(compiler, options,
                NativeBindingResources.ReadManifest(major), directory, cancellationToken);
            string library = arguments.Length >= 9 && arguments[8].Length != 0 ? Path.GetFullPath(arguments[8])
                : await NativeBindingRecordCommand.FindLibraryAsync(compiler, manifest.ClangMajor, cancellationToken);
            if (!File.Exists(library))
            {
                throw new FileNotFoundException("Cannot locate the selected libclang library.", library);
            }

            NativeBindingCacheFile[] tools = await NativeBindingCache.SnapshotAsync(ToolFiles(compiler, library), cancellationToken);
            string helperPath = PhysicalPath(typeof(NativeBindingSourceCache).Assembly.Location);
            NativeBindingCacheFile helper = tools.Single(input => input.Path == helperPath);
            NativeBindingCacheFile[] nativeTools = [.. tools.Where(input => input.Path != helperPath)];
            (NativeHeaderCatalogs catalogs, string catalogHash) = await DeriveCatalogsAsync(major, installation, compiler, library, options,
                Path.Combine(cache, "catalogs"), Key(new { Manifest = manifest, Tools = nativeTools, Generator = helper.Hash, Compiler = compiler,
                    Library = library, Options = options, Runtime = Environment.Version.ToString() }), directory, cancellationToken);
            NativeBindingCatalog catalog = catalogs.Types;
            NativeBindingNodeRoots roots = NativeBindingNodeRecords.CreateRoots(catalog, NativeBindingResources.ReadHeaders(major),
                NativeBindingHeaderHelpers.RequiredTypes);
            string headers = NativeBindingHeaderTarget.GenerateSource(roots.Source, major);
            NativeBindingPreprocessed observation = await NativeBindingPreprocessor.ObserveAsync(compiler, options, headers, directory, cancellationToken);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                Catalogs = catalogHash,
                Observation = observation,
                NativeCompiler = arguments.Length >= 4 ? arguments[3] : "",
                Tools = nativeTools,
                Generator = helper.Hash,
                Compiler = compiler,
                Library = library,
                Options = options,
                Runtime = Environment.Version.ToString(),
            }))));
            bool produced = false;
            string snapshot = Path.Combine(directory, "sources");
            Directory.CreateDirectory(snapshot);
            await using (NativeBindingCacheLease lease = await NativeBindingCache.GetAsync(Path.Combine(cache, "sources"), key, async (stage, token) =>
            {
                produced = true;
                string[] selected = [arguments[0], arguments[1], stage,
                    arguments.Length >= 4 ? arguments[3] : "", frontend[5], frontend[6], frontend[7], compiler, library];
                NativeBindingCollection collection = await NativeBindingCollectionCommand.RunAsync(catalog, catalogs.Raw, selected, token);
                NativeHeaderRecords records = collection.Records;
                NativeBindingSelectedNodes nodes = NativeBindingNodeAvailability.Read(catalog, records.Graph);
                NativeBindingLayout layout = await NativeBindingLayoutCommand.MeasureAsync(nodes, selected[..7], token);
                string[] calls = [.. records.Headers.Symbols.Where(pair => pair.Value.IsFunction &&
                    records.Graph.Types[records.Graph.Types[records.Graph.Roots[pair.Key]].Canonical].Function is { HasPrototype: true, IsVariadic: false })
                    .Select(static pair => pair.Key)];
                string[] globals = [.. collection.Availability.Available.Where(static request => !request.IsFunction).Select(static request => request.Name)];
                NativeBindingSource binding = NativeBindingRecordCSharp.Generate(records, nodes.Catalog, layout, calls, globals);
                await File.WriteAllTextAsync(Path.Combine(stage, Artifacts[0]), JsonSerializer.Serialize(records, NativeBindingRecordWorker.JsonOptions) + "\n", token);
                await File.WriteAllTextAsync(Path.Combine(stage, Artifacts[1]), JsonSerializer.Serialize(collection.Availability) + "\n", token);
                await File.WriteAllTextAsync(Path.Combine(stage, Artifacts[2]), binding.Source, token);
                await File.WriteAllTextAsync(Path.Combine(stage, Artifacts[3]), binding.AssemblyName + "\n", token);
                await File.WriteAllTextAsync(Path.Combine(stage, Artifacts[4]), binding.AbiIdentity + "\n", token);
                await VerifyObservationAsync(token);
                // Every tool's current bytes are bound into the key and verified again
                // before publishing or consuming the artifacts. Recording them as additional
                // dependencies would repeat the same reads while holding the shared entry lock.
                return [];
            }, cancellationToken))
            {
                // Keep the shared entry stable only until its artifacts have been copied.
                // Each consumer can then run its own native checks without blocking peers.
                foreach (string artifact in Artifacts)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    File.Copy(Path.Combine(lease.Directory, artifact), Path.Combine(snapshot, artifact));
                }
            }

            if (!produced)
            {
                await using FileStream stream = File.OpenRead(Path.Combine(snapshot, Artifacts[0]));
                NativeHeaderRecords records = await JsonSerializer.DeserializeAsync<NativeHeaderRecords>(stream,
                    NativeBindingRecordWorker.JsonOptions, cancellationToken) ?? throw new FormatException("Missing cached native declarations.");
                NativeBindingSelectedNodes nodes = NativeBindingNodeAvailability.Read(catalog, records.Graph);
                NativeBindingNodeRoots currentRoots = NativeBindingNodeRecords.CreateRoots(nodes.Catalog,
                    NativeBindingResources.ReadHeaders(catalog.PostgresMajor), NativeBindingHeaderHelpers.RequiredTypes);
                await NativeBindingCollectionCommand.VerifyAsync(records, currentRoots.Source, installation, arguments, directory, cancellationToken);
                string[] selected = [arguments[0], arguments[1], directory,
                    arguments.Length >= 4 ? arguments[3] : "", frontend[5], frontend[6], frontend[7]];
                NativeBindingLayout layout = await NativeBindingLayoutCommand.MeasureAsync(nodes, selected, cancellationToken);
                _ = NativeBindingNodeRecords.Create(records.Graph, nodes.Catalog, layout);
                foreach (string artifact in Artifacts.Skip(5))
                {
                    if (await NativeBindingCache.HashAsync(Path.Combine(directory, artifact), cancellationToken) !=
                        await NativeBindingCache.HashAsync(Path.Combine(snapshot, artifact), cancellationToken))
                    {
                        throw new IOException("Current native node observations disagree with the cached companion contract.");
                    }
                }

                await VerifyObservationAsync(cancellationToken);
            }

            string output = Path.GetFullPath(arguments[2]);
            Directory.CreateDirectory(output);
            foreach (string name in Artifacts)
            {
                string source = Path.Combine(snapshot, name);
                string destination = Path.Combine(output, name);
                if (!File.Exists(destination) || await NativeBindingCache.HashAsync(destination, cancellationToken) !=
                    await NativeBindingCache.HashAsync(source, cancellationToken))
                {
                    File.Copy(source, destination, overwrite: true);
                }
            }

            Console.WriteLine(produced ? "Native binding sources: collected and verified." : "Native binding sources: reused after native verification.");

            async Task VerifyObservationAsync(CancellationToken token)
            {
                NativeBindingCacheFile[] current = await NativeBindingCache.SnapshotAsync(ToolFiles(compiler, library), token);
                if (observation != await NativeBindingPreprocessor.ObserveAsync(compiler, options, headers, directory, token) ||
                    !tools.SequenceEqual(current))
                {
                    throw new IOException("Native headers or compiler inputs changed during binding collection.");
                }
            }
        }
        finally
        {
            if (Directory.Exists(directory))
            {
                await NativeBuildDirectory.DeleteAsync(directory);
            }
        }
    }

    /// <summary>
    /// Derives the major's catalogs from the selected headers as pgrx's bindgen does, reusing a shared derivation for the
    /// same preprocessed manifest, toolchain and generator.
    /// </summary>
    /// <returns>The catalogs and the hash of their serialized form, which identifies them in later keys.</returns>
    private static async Task<(NativeHeaderCatalogs Catalogs, string Hash)> DeriveCatalogsAsync(int major, PostgresInstallation installation,
        string compiler, string library, List<string> options, string cache, string key, string directory, CancellationToken cancellationToken)
    {
        const string Artifact = "native-catalogs.json";
        byte[] bytes;
        await using (NativeBindingCacheLease lease = await NativeBindingCache.GetAsync(cache, key, async (stage, token) =>
        {
            string work = Directory.CreateDirectory(Path.Combine(directory, "catalogs")).FullName;
            NativeHeaderCatalogs derived = await NativeBindingHeaderCatalogCommand.BuildAsync(compiler,
                [.. options, OperatingSystem.IsWindows() ? "/Zs" : "-fsyntax-only"], NativeBindingResources.ReadManifest(major), major,
                installation.ServerIncludeDirectory, library, work, token);
            await File.WriteAllTextAsync(Path.Combine(stage, Artifact), JsonSerializer.Serialize(derived) + "\n", token);
            return [];
        }, cancellationToken))
        {
            bytes = await File.ReadAllBytesAsync(Path.Combine(lease.Directory, Artifact), cancellationToken);
        }

        NativeHeaderCatalogs catalogs = JsonSerializer.Deserialize<NativeHeaderCatalogs>(bytes)
            ?? throw new FormatException("Missing derived native catalogs.");
        return catalogs.Types.PostgresMajor == major && catalogs.Raw.PostgresMajor == major
            ? (catalogs, Convert.ToHexString(SHA256.HashData(bytes)))
            : throw new FormatException("The derived native catalogs have the wrong major.");
    }

    private static string Key<T>(T value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value))));

    private static IEnumerable<string> ToolFiles(string compiler, string library)
    {
        string[] directories = [Path.GetDirectoryName(compiler)!, Path.GetDirectoryName(library)!];
        return directories.Distinct(StringComparer.Ordinal).SelectMany(static directory => Directory.GetFiles(directory))
            .Where(static file => file.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) || file.EndsWith(".dylib", StringComparison.Ordinal) ||
                Path.GetFileName(file).Contains(".so", StringComparison.Ordinal))
            .Append(compiler).Append(library).Append(typeof(NativeBindingSourceCache).Assembly.Location)
            .Select(PhysicalPath)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
    }

    /// <summary>
    /// Resolves selected tool links consistently for content observation and dependency classification.
    /// </summary>
    /// <param name="file">The selected compiler, library or generator file.</param>
    /// <returns>The final link target or absolute file path.</returns>
    private static string PhysicalPath(string file)
        => new FileInfo(file).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(file);
}
