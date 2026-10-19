using System.Diagnostics;
using System.Text.Json;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Collects transitive record layouts beside the selected headers' validated signature contracts.
/// </summary>
internal static class NativeBindingRecordCommand
{
    /// <summary>
    /// Publishes a complete record contract from the selected compiler's serialized declaration AST.
    /// </summary>
    internal static async Task<NativeHeaderRecords> RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length is < 4 or > 9)
        {
            throw new ArgumentException("Expected binding-records <symbol-list> <major> <pg_config> <output-directory> [clang] [windows-library-directories] [runtime-identifier] [target-triple] [libclang].", nameof(arguments));
        }

        cancellationToken.ThrowIfCancellationRequested();
        string[] headerArguments = arguments.Length == 9 ? arguments[..8] : arguments;
        NativeHeaderCatalog headers = await NativeBindingHeaderCommand.RunAsync(headerArguments, cancellationToken);
        int major = headers.Target.PostgresVersion / 10000;
        PostgresInstallation installation = arguments[2].Length == 0
            ? await PostgresInstallation.DiscoverAsync(major, cancellationToken)
            : await PostgresInstallation.CreateAsync(arguments[2], cancellationToken);
        string compiler = FindCompiler(await ClangSelection.ResolveAsync(arguments.Length >= 5 ? arguments[4] : null, cancellationToken));
        string library = arguments.Length == 9 && arguments[8].Length != 0 ? Path.GetFullPath(arguments[8])
            : await FindLibraryAsync(compiler, headers.Target.ClangMajor, cancellationToken);
        string output = Path.GetFullPath(arguments[3]);
        string ast = Path.Join(output, "native-header-records.ast");
        await NativeBindingHeaderCommand.InspectAsync(installation, headerArguments, Path.Join(output, "native-header-types.c"),
            Path.Join(output, "native-header-records.txt"), output, cancellationToken, dumpAst: false, serializedAst: ast);
        var symbols = headers.Symbols.ToDictionary(static pair => pair.Key,
            static pair => new NativeHeaderRequest(pair.Key, pair.Value.NativeName, pair.Value.IsFunction), StringComparer.Ordinal);
        var request = new NativeRecordRequest(ast, library, headers.Target, symbols);
        NativeRecordGraph graph = await NativeBindingRecordWorker.InspectAsync(request, output, cancellationToken);
        var records = new NativeHeaderRecords(headers, graph);
        NativeBindingSignatureValidation.Validate(records);
        string temporary = Path.Join(output, "native-records-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            await using (FileStream stream = File.Create(temporary))
            {
                await JsonSerializer.SerializeAsync(stream, records, NativeBindingRecordWorker.JsonOptions, cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, Path.Join(output, "native-records.json"), overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }

        Console.WriteLine($"PG{major}: collected {graph.Declarations.Count} transitive native declarations and {graph.Types.Count} types for {graph.Roots.Count} symbols.");
        return records;
    }

    /// <summary>
    /// Resolves the same explicitly selected compiler or PATH entry before choosing its adjacent library.
    /// </summary>
    internal static string FindCompiler(string compiler)
    {
        if (compiler.Contains(Path.DirectorySeparatorChar) || compiler.Contains(Path.AltDirectorySeparatorChar) || Path.IsPathRooted(compiler))
        {
            return Resolve(compiler);
        }

        string name = OperatingSystem.IsWindows() && !compiler.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? compiler + ".exe" : compiler;
        foreach (string candidate in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).Select(directory => Path.Join(directory, name)))
        {
            if (File.Exists(candidate))
            {
                return Resolve(candidate);
            }
        }

        throw new FileNotFoundException("Cannot locate the selected Clang executable.", compiler);

        static string Resolve(string path)
        {
            var file = new FileInfo(Path.GetFullPath(path));
            if (!file.Exists)
            {
                throw new FileNotFoundException("Cannot locate the selected Clang executable.", file.FullName);
            }

            return file.FullName;
        }
    }

    /// <summary>
    /// Requires libclang from the selected compiler installation, so an unrelated system library is never loaded.
    /// </summary>
    internal static async Task<string> FindLibraryAsync(string compiler, int major, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(compiler)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add("-print-resource-dir");
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot query the selected Clang installation.");
        using var output = new MemoryStream();
        using var errors = new MemoryStream();
        Task copy = NativeBindingHeaderCommand.CopyAsync(process.StandardOutput.BaseStream, output, 64 * 1024, cancellationToken);
        Task diagnostics = NativeBindingHeaderCommand.CopyAsync(process.StandardError.BaseStream, errors, 64 * 1024, cancellationToken);
        Task exit = process.WaitForExitAsync(cancellationToken);
        try
        {
            var pending = new List<Task> { copy, diagnostics, exit };
            while (pending.Count != 0)
            {
                Task completed = await Task.WhenAny(pending);
                await completed;
                pending.Remove(completed);
            }

            if (process.ExitCode != 0)
            {
                throw new InvalidOperationException("Cannot query Clang resources: " + System.Text.Encoding.UTF8.GetString(errors.ToArray()));
            }
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            await Task.WhenAll(copy, diagnostics);
            throw;
        }

        string resources = System.Text.Encoding.UTF8.GetString(output.ToArray()).Trim();
        if (!Path.IsPathFullyQualified(resources) || !Directory.Exists(resources))
        {
            throw new InvalidOperationException("Clang did not report its absolute resource directory.");
        }

        string lib = Path.GetFullPath(Path.Join(resources, "..", ".."));
        string bin = Path.GetFullPath(Path.Join(lib, "..", "bin"));
        string[] candidates = OperatingSystem.IsWindows() ? [Path.Join(bin, "libclang.dll")]
            : OperatingSystem.IsMacOS() ? [Path.Join(lib, "libclang.dylib")]
            : [Path.Join(lib, "libclang.so"), Path.Join(lib, "libclang.so.1"), Path.Join(lib, $"libclang-{major}.so.1"), Path.Join(lib, $"libclang.so.{major}")];
        return candidates.FirstOrDefault(File.Exists) ?? throw new FileNotFoundException("A matching libclang library was not found beside the selected compiler. Supply its explicit path.");
    }
}
