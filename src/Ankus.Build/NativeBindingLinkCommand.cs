using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Compiles the guarded native bodies selected by an extension's actual Native AOT object.
/// </summary>
internal static class NativeBindingLinkCommand
{
    /// <summary>
    /// Validates the complete companion and native target before publishing the selected native object.
    /// </summary>
    internal static async Task RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length is < 7 or > 8)
        {
            throw new ArgumentException("Expected binding-link <records> <pg_config> <output-directory> <ilc-object> <compiler> <windows-library-directories> <target-triple> [cache-directory].", nameof(arguments));
        }

        cancellationToken.ThrowIfCancellationRequested();
        NativeHeaderRecords records;
        await using (FileStream stream = File.OpenRead(arguments[0]))
        {
            records = await JsonSerializer.DeserializeAsync<NativeHeaderRecords>(stream, NativeBindingRecordWorker.JsonOptions, cancellationToken)
                ?? throw new FormatException("Native binding records are missing.");
        }

        byte[] image = await File.ReadAllBytesAsync(arguments[3], cancellationToken);
        int major = records.Headers.Target.PostgresVersion / 10000;
        string source = NativeBindingCallImports.Generate(records, NativeBindingResources.ReadHeaders(major), image);
        NativeObjectImports imports = NativeObjectSymbols.Read(image, NativeBindingCallImports.Prefix);
        int globalCount = NativeBindingGlobalImports.Select(image).Count;
        int indirectCount = NativeBindingIndirectImports.Select(image).Count;
        int callbackCount = NativeBindingCallbackImports.Select(image).Count;
        string output = Path.GetFullPath(arguments[2]);
        Directory.CreateDirectory(output);
        if (imports.Symbols.Count + globalCount + indirectCount + callbackCount == 0)
        {
            await PublishEmptyAsync(output, cancellationToken);
            Console.WriteLine("Native binding calls: no referenced bodies.");
            return;
        }

        PostgresInstallation installation = arguments[1].Length == 0
            ? await PostgresInstallation.DiscoverAsync(major, cancellationToken)
            : await PostgresInstallation.CreateAsync(arguments[1], cancellationToken);
        if (installation.Version.Major != major)
        {
            throw new InvalidOperationException("The native call installation does not match the companion.");
        }

        string cache = arguments.Length == 8 && arguments[7].Length != 0 ? Path.GetFullPath(arguments[7])
            : Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ankus", "bindings");
        await PublishAsync(output, Path.Join(cache, "native"), source, async (file, artifact, token) =>
        {
            string compiler = arguments[4];
            var options = new List<string>();
            if (OperatingSystem.IsWindows())
            {
                compiler = Path.Join(Path.GetDirectoryName(compiler) ?? "", "cl.exe");
                options.AddRange(["/nologo", "/std:c11", "/c", "/O2", "/MT", "/WX", "/Fo" + artifact]);
                options.AddRange(["/I" + installation.ServerIncludeDirectory, "/I" + installation.IncludeDirectory,
                    "/I" + Path.Join(installation.ServerIncludeDirectory, "port", "win32"),
                    "/I" + Path.Join(installation.ServerIncludeDirectory, "port", "win32_msvc")]);
                options.AddRange(WindowsToolchain.GetIncludeDirectories(arguments[5]).Select(static path => "/I" + path));
            }
            else
            {
                options.AddRange(await installation.GetPreprocessorArgumentsAsync(token));
                if (arguments[6].Length != 0)
                {
                    options.Add("--target=" + arguments[6]);
                }

                options.AddRange(["-std=gnu11", "-c", "-O2", "-fPIC", "-Wall", "-Wextra", "-Werror",
                    "-isystem", installation.ServerIncludeDirectory, "-isystem", installation.IncludeDirectory, "-o", artifact]);
            }

            options.Add(file);
            await NativeBindingLayoutCommand.RunProcessAsync(compiler, options, Path.GetDirectoryName(file)!, token);
        }, cancellationToken);
        Console.WriteLine($"Native binding calls: compiled {imports.Symbols.Count + globalCount + indirectCount + callbackCount} referenced bodies.");
    }

    /// <summary>
    /// Publishes an immutable source/object pair by atomically replacing only its linker manifest after compilation.
    /// </summary>
    internal static async Task PublishAsync(string output, string cache, string source, Func<string, string, CancellationToken, Task> compile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(compile);
        cancellationToken.ThrowIfCancellationRequested();
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(output);
        string stage = Directory.CreateTempSubdirectory("ankus-native-call-").FullName;
        try
        {
            string file = Path.Join(stage, "native-calls.c");
            string artifact = Path.Join(stage, OperatingSystem.IsWindows() ? "native-calls.obj" : "native-calls.o");
            await File.WriteAllTextAsync(file, source, cancellationToken);
            await compile(file, artifact, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            string sourceHash = await NativeBindingCache.HashAsync(file, cancellationToken);
            string artifactHash = await NativeBindingCache.HashAsync(artifact, cancellationToken);
            string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sourceHash + artifactHash)));
            // The Native AOT linker opens this retained object after the helper has exited.
            await using NativeBindingCacheLease lease = await NativeBindingCache.GetAsync(cache, key,
                (destination, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    File.Copy(file, Path.Join(destination, Path.GetFileName(file)));
                    File.Copy(artifact, Path.Join(destination, Path.GetFileName(artifact)));
                    return Task.FromResult<IReadOnlyList<NativeBindingCacheFile>>([]);
                }, cancellationToken, copyBeforeRelease: false);
            await PublishManifestAsync(output, Path.Join(lease.Directory, Path.GetFileName(artifact)) + "\n", cancellationToken);
        }
        finally
        {
            await NativeBuildDirectory.DeleteAsync(stage);
        }
    }

    /// <summary>
    /// Removes all selected bodies through the same atomic manifest boundary without invoking a compiler.
    /// </summary>
    internal static async Task PublishEmptyAsync(string output, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(output);
        await PublishManifestAsync(output, "", cancellationToken);
    }

    private static async Task PublishManifestAsync(string output, string content, CancellationToken cancellationToken)
    {
        string destination = Path.Join(output, "native-call-libraries.txt");
        if (File.Exists(destination) && await File.ReadAllTextAsync(destination, cancellationToken) == content)
        {
            return;
        }

        // The candidate must share the destination filesystem even when compilation
        // uses a short system-temporary path on another volume.
        string candidate = Path.Join(output, "native-call-manifest-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(candidate, content, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(candidate, destination, overwrite: true);
        }
        finally
        {
            File.Delete(candidate);
        }
    }
}
