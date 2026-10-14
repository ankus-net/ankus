using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Ankus.Build;

/// <summary>
/// Compiles verified companions once per content contract and copies leased artifacts to each consumer.
/// </summary>
internal static class NativeBindingCompilationCommand
{
    private static readonly string[] s_artifactExtensions = [".dll", ".xml", ".pdb"];
    private const string BindingConfiguration = "Release";

    private const string CompilerInputs = """
        <Target Name="_RecordAnkusCompilerInputs" BeforeTargets="CoreCompile">
          <Error Condition="'$(NETCoreSdkVersion)' != '$(AnkusSelectedSdkVersion)'" Text="The binding compiler resolved a different .NET SDK." />
          <ItemGroup>
            <_AnkusAnalyzerDirectory Include="@(Analyzer->'%(RootDir)%(Directory)')" />
            <_AnkusTargetingPackData Include="@(ResolvedTargetingPack->'%(Path)/data/')" />
            <_AnkusCompilerInput Include="@(ReferencePath);@(Analyzer);@(Compile);@(EditorConfigFiles)" />
            <_AnkusCompilerInput Include="%(_AnkusAnalyzerDirectory.Identity)**/*" />
            <_AnkusCompilerInput Include="%(_AnkusTargetingPackData.Identity)**/*" />
          </ItemGroup>
          <WriteLinesToFile File="compiler-inputs.txt" Lines="@(_AnkusCompilerInput->'%(FullPath)')" Overwrite="true" />
        </Target>
        """;

    /// <summary>
    /// Resolves the consumer's exact SDK and runtime reference, then reuses or builds content-verified artifacts.
    /// </summary>
    internal static async Task RunAsync(string[] arguments, CancellationToken cancellationToken = default)
    {
        if (arguments.Length != 8)
        {
            throw new ArgumentException("Expected binding-compile <source-directory> <runtime-assembly> <target-framework> <configuration> <sdk-version> <sdk-directory> <consumer-assets> <cache-directory>.", nameof(arguments));
        }

        string source = Path.GetFullPath(arguments[0]);
        string runtime = Path.GetFullPath(arguments[1]);
        string generator = Path.GetFullPath(typeof(NativeBindingCompilationCommand).Assembly.Location);
        string sdk = Path.GetFullPath(arguments[5]);
        NativeBindingRestoreSettings restore = await NativeBindingRestoreSettings.ReadAsync(arguments[6], cancellationToken);
        string packages = restore.Packages;
        string cache = string.IsNullOrEmpty(arguments[7])
            ? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ankus", "bindings")
            : Path.GetFullPath(arguments[7]);
        string hostDirectory = Path.GetFullPath(Path.Join(sdk, "..", ".."));
        string host = Path.Join(hostDirectory, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
        string assembly = (await File.ReadAllTextAsync(Path.Join(source, "native-binding.assembly-name"), cancellationToken)).Trim();
        if (assembly.Length == 0 || assembly.Any(static value => !char.IsAsciiLetterOrDigit(value) && value != '.'))
        {
            throw new FormatException("The binding assembly name is invalid.");
        }

        // MSBuild uses its own runtime configuration, which may differ from this helper's runtime.
        // Inventory the selected installation's host and shared runtimes too: servicing or adding
        // a runtime can change framework resolution without changing any SDK files.
        string[] installation = [.. Directory.GetFiles(sdk, "*", SearchOption.AllDirectories)
            .Concat(Directory.GetFiles(Path.Join(hostDirectory, "host", "fxr"), "*", SearchOption.AllDirectories))
            .Concat(Directory.GetFiles(Path.Join(hostDirectory, "shared", "Microsoft.NETCore.App"), "*", SearchOption.AllDirectories))
            .Append(host).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)];
        string[] explicitFiles = [Path.Join(source, "native-binding.g.cs"), runtime];
        var start = new ProcessStartInfo(host) { UseShellExecute = false };
        // Generated companions have their own build contract. Do not inherit arbitrary
        // environment properties that can redirect SDK imports or compiler tasks.
        string[] environmentNames = ["PATH", "HOME", "USERPROFILE", "APPDATA", "LOCALAPPDATA", "TEMP", "TMP", "TMPDIR",
            "SystemRoot", "SystemDrive", "COMSPEC", "ProgramFiles", "ProgramFiles(x86)", "ProgramW6432",
            "HTTP_PROXY", "HTTPS_PROXY", "ALL_PROXY", "NO_PROXY", "http_proxy", "https_proxy", "all_proxy", "no_proxy",
            "SSL_CERT_FILE", "SSL_CERT_DIR", "LANG", "LC_ALL", "DOTNET_ROOT", "DOTNET_ROOT_X64", "DOTNET_ROOT_ARM64"];
        KeyValuePair<string, string?>[] environment = [.. start.Environment.Where(pair => environmentNames.Contains(pair.Key, StringComparer.OrdinalIgnoreCase))];
        start.Environment.Clear();
        foreach (KeyValuePair<string, string?> pair in environment)
        {
            start.Environment.Add(pair);
        }

        start.Environment["MSBuildSDKsPath"] = Path.Join(sdk, "Sdks");
        start.Environment["DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"] = Path.GetDirectoryName(host);
        string architectureRoot = "DOTNET_ROOT_" + RuntimeInformation.ProcessArchitecture.ToString().ToUpperInvariant();
        start.Environment["DOTNET_ROOT"] = hostDirectory;
        start.Environment[architectureRoot] = hostDirectory;
        start.Environment["DOTNET_HOST_PATH"] = host;
        // Only the hash of inherited settings participates in identity; values are not persisted.
        string settings = JsonSerializer.Serialize(start.Environment.OrderBy(static pair => pair.Key, StringComparer.Ordinal));
        // The project is closed: its text comes from this helper, its SDK is pinned,
        // and its source and runtime reference are explicit. Resolve its compiler
        // inputs only on a miss; the cache still verifies recorded dependencies.
        NativeBindingCacheFile[] toolchain = await NativeBindingCache.SnapshotAsync(installation.Concat(restore.Configurations), cancellationToken);
        NativeBindingCacheFile[] selected = await NativeBindingCache.SnapshotAsync([generator, runtime, explicitFiles[0]], cancellationToken);
        string packs = Path.Join(hostDirectory, "packs");
        NativeBindingCacheFile? frameworkPacks = Directory.Exists(packs) ? NativeBindingCache.SnapshotDirectory(packs) : null;
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            Inputs = toolchain,
            Generator = selected[0].Hash,
            Runtime = selected[1].Hash,
            Source = selected[2].Hash,
            Assembly = assembly,
            Framework = arguments[2],
            SdkVersion = arguments[4],
            FrameworkPacks = frameworkPacks,
            Restore = restore,
            Environment = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings))),
        }))));

        bool compiled = false;
        await using NativeBindingCacheLease lease = await NativeBindingCache.GetAsync(cache, key, async (stage, token) =>
        {
            return await CompileCompanionAsync(stage, token);
        }, cancellationToken);

        await VerifySelectedAsync(cancellationToken);
        string destination = Path.Join(source, "compiled");
        Directory.CreateDirectory(destination);
        foreach (string name in s_artifactExtensions.Select(extension => assembly + extension))
        {
            string target = Path.Join(destination, name);
            string artifact = Path.Join(lease.Directory, name);
            if (!File.Exists(target) || await NativeBindingCache.HashAsync(target, cancellationToken) != await NativeBindingCache.HashAsync(artifact, cancellationToken))
            {
                File.Copy(artifact, target, overwrite: true);
            }
        }

        string reference = Path.Join(source, "native-binding.assembly-path");
        string content = Path.Join(destination, assembly + ".dll") + "\n";
        if (!File.Exists(reference) || await File.ReadAllTextAsync(reference, cancellationToken) != content)
        {
            await File.WriteAllTextAsync(reference, content, cancellationToken);
        }

        Console.WriteLine($"Managed binding compilation: {(compiled ? "built" : "reused")} {assembly}");

        async Task VerifySelectedAsync(CancellationToken token)
        {
            NativeBindingCacheFile[] current = await NativeBindingCache.SnapshotAsync(selected.Select(static input => input.Path), token);
            if (!selected.SequenceEqual(current))
            {
                throw new IOException("The binding generator, source or runtime reference changed during compilation.");
            }
        }

        async Task<IReadOnlyList<NativeBindingCacheFile>> CompileCompanionAsync(string stage, CancellationToken token)
        {
            string work = NativeBuildDirectory.PhysicalPath(Directory.CreateTempSubdirectory("ankus-binding-compile-"));
            try
            {
                // Give the explicit reference an owned logical location, just like generated source.
                // Reusing a companion must not depend on a former consumer's package directory.
                string runtimeReference = Path.Join(Directory.CreateDirectory(Path.Join(work, "runtime")).FullName, Path.GetFileName(runtime));
                File.Copy(runtime, runtimeReference);
                if (await NativeBindingCache.HashAsync(runtimeReference, token) != selected[1].Hash)
                {
                    throw new IOException("The binding runtime reference changed while it was being staged.");
                }

                var project = XDocument.Parse(NativeBindingSourceCommand.CreateProject(work));
                XElement root = project.Root!;
                root.AddFirst(new XElement("PropertyGroup",
                    Property("AnkusBindingTargetFramework", arguments[2]),
                    Property("AnkusBindingAssemblyName", assembly),
                    Property("AnkusRuntimeAssembly", runtimeReference),
                    // The generated companion contains only deterministic declarations. Its
                    // bytes and cache identity must not vary with the consumer's build label.
                    Property("Configuration", BindingConfiguration),
                    Property("UseSharedCompilation", "false"),
                    Property("AnkusSelectedSdkVersion", arguments[4]),
                    Property("MSBuildUserExtensionsPath", Path.Join(work, "user-extensions")),
                    Property("RestorePackagesPath", packages)));
                root.Add(XElement.Parse(CompilerInputs));
                root.Add(restore.CreateTarget());
                await File.WriteAllTextAsync(Path.Join(work, "Ankus.NativeBindings.csproj"), project.ToString(), token);
                File.Copy(explicitFiles[0], Path.Join(work, "native-binding.g.cs"));
                if (await NativeBindingCache.HashAsync(Path.Join(work, "native-binding.g.cs"), token) != selected[2].Hash)
                {
                    throw new IOException("The binding source changed while it was being staged.");
                }

                await File.WriteAllTextAsync(Path.Join(work, "global.json"), JsonSerializer.Serialize(new
                {
                    sdk = new
                    {
                        version = arguments[4],
                        rollForward = "disable",
                        allowPrerelease = true
                    },
                }), token);
                start.WorkingDirectory = work;
                foreach (string argument in new[] { "exec", Path.Join(sdk, "MSBuild.dll"),
                        "Ankus.NativeBindings.csproj", "-nologo", "-verbosity:minimal", "-nodeReuse:false" })
                {
                    start.ArgumentList.Add(argument);
                }

                // NuGet and credential-provider plugins need the consumer's environment, including
                // custom configuration substitutions. Only compilation uses the closed environment.
                var restoreStart = new ProcessStartInfo(host) { UseShellExecute = false, WorkingDirectory = work };
                restoreStart.Environment["MSBuildSDKsPath"] = Path.Join(sdk, "Sdks");
                restoreStart.Environment["DOTNET_MSBUILD_SDK_RESOLVER_CLI_DIR"] = Path.GetDirectoryName(host);
                restoreStart.Environment["DOTNET_ROOT"] = hostDirectory;
                restoreStart.Environment[architectureRoot] = hostDirectory;
                restoreStart.Environment["DOTNET_HOST_PATH"] = host;
                // Concurrent generated-binding restores may share the consumer's NuGet package
                // cache, but NuGet's vulnerability metadata replacement file is not safe to
                // update concurrently on every filesystem. Keep that transient HTTP state owned
                // by this compiler workspace while retaining the consumer's package/source policy.
                restoreStart.Environment["NUGET_HTTP_CACHE_PATH"] = Path.Join(work, "nuget-http-cache");
                foreach (string argument in start.ArgumentList)
                {
                    restoreStart.ArgumentList.Add(argument);
                }

                restoreStart.ArgumentList.Add("-target:Restore");
                Console.WriteLine("Managed binding compilation: preparing compiler inputs");
                await CompileAsync(restoreStart, token);
                restore.Verify(await NativeBindingRestoreSettings.ReadAsync(Path.Join(work, "obj", "project.assets.json"), token));
                start.ArgumentList.Add("-target:Compile");
                start.ArgumentList.Add("-property:SkipCompilerExecution=true");
                await CompileAsync(start, token);
                start.ArgumentList.Remove("-property:SkipCompilerExecution=true");
                start.ArgumentList.Remove("-target:Compile");
                start.ArgumentList.Add("-target:Build");
                NativeBindingCacheFile[] compiler = await NativeBindingCompilerInputs.ReadAsync(Path.Join(work, "compiler-inputs.txt"), token);
                NativeBindingCacheFile[] packageInputs = await ReadPackagesAsync(work, token);
                NativeBindingCacheFile[] dependencies = [.. toolchain, .. compiler, .. packageInputs];
                // Restore policy is consumer-specific; compiler inputs need not be. Only
                // consult this second cache after restoring and resolving the exact inputs.
                // Package relocation must not recompile byte-identical framework references
                // and analyzers. The request cache above still tracks their physical files.
                string compilationKey = await CompilationKeyAsync(work, [.. compiler, .. packageInputs], token);
                await using NativeBindingCacheLease compilation = await NativeBindingCache.GetAsync(
                    Path.Join(cache, "compilations"), compilationKey, async (destination, compilerToken) =>
                    {
                        compiled = true;
                        start.ArgumentList.Add("-bl:" + Path.Join(source, "binding-compile-" + Guid.NewGuid().ToString("N") + ".binlog"));
                        await CompileAsync(start, compilerToken);
                        string output = Path.Join(work, "bin", BindingConfiguration, arguments[2]);
                        foreach (string name in s_artifactExtensions.Select(extension => assembly + extension))
                        {
                            File.Copy(Path.Join(output, name), Path.Join(destination, name));
                        }

                        NativeBindingCacheFile[] observed = await NativeBindingCompilerInputs.ReadAsync(
                            Path.Join(work, "compiler-inputs.txt"), compilerToken);
                        NativeBindingCacheFile[] observedPackages = await ReadPackagesAsync(work, compilerToken);
                        if (!compiler.SequenceEqual(observed) || !packageInputs.SequenceEqual(observedPackages))
                        {
                            throw new IOException("The binding compiler inputs changed after preparation.");
                        }

                        // Check SDK stability before publication without retaining a previous
                        // consumer's disposable workspace or package paths as dependencies.
                        return toolchain[..installation.Length];
                    }, token);
                NativeBindingCacheFile[] actual = await NativeBindingCompilerInputs.ReadAsync(Path.Join(work, "compiler-inputs.txt"), token);
                if (!compiler.SequenceEqual(actual))
                {
                    throw new IOException("The binding compiler inputs changed after preparation.");
                }

                await VerifySelectedAsync(token);
                foreach (string name in s_artifactExtensions.Select(extension => assembly + extension))
                {
                    File.Copy(Path.Join(compilation.Directory, name), Path.Join(stage, name));
                }

                return [.. dependencies.Where(input => !input.Path.StartsWith(work + Path.DirectorySeparatorChar, StringComparison.Ordinal))];
            }
            finally
            {
                await NativeBuildDirectory.DeleteAsync(work);
            }
        }

        async Task<string> CompilationKeyAsync(string work, NativeBindingCacheFile[] compiler, CancellationToken token)
        {
            string[] packageRoots = [packages, .. restore.Fallbacks];
            var identity = new List<NativeBindingCacheFile>(compiler.Length);
            foreach (NativeBindingCacheFile input in compiler)
            {
                string path = LogicalPath(input.Path);
                string hash = input.Hash;
                if (input.Path.StartsWith(work + Path.DirectorySeparatorChar, StringComparison.Ordinal) &&
                    Path.GetExtension(input.Path) == ".editorconfig")
                {
                    // ProjectDir is an owned physical workspace; PathMap fixes the source
                    // location seen by the compiler. All other analyzer settings are retained.
                    string configuration = (await File.ReadAllTextAsync(input.Path, token))
                        .Replace(work, "/_/Ankus.Postgres", StringComparison.Ordinal);
                    hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configuration)));
                }

                identity.Add(new(path, hash, input.IsDirectory));
            }

            return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
            {
                Inputs = identity.OrderBy(static input => input.Path, StringComparer.Ordinal).ThenBy(static input => input.Hash, StringComparer.Ordinal),
                Tools = toolchain[..installation.Length],
                Generator = selected[0].Hash,
                Assembly = assembly,
                Framework = arguments[2],
                SdkVersion = arguments[4],
                Environment = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(settings))),
            }))));

            string LogicalPath(string path)
            {
                if (path.StartsWith(work + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                {
                    return "/_/Ankus.Postgres/" + Path.GetRelativePath(work, path).Replace('\\', '/');
                }

                foreach (string root in packageRoots)
                {
                    if (path.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.Ordinal))
                    {
                        return "/_/packages/" + Path.GetRelativePath(root, path).Replace('\\', '/');
                    }
                }

                return path;
            }
        }
    }

    private static XElement Property(string name, string value) => new(name, NativeBindingSourceCommand.EscapeProperty(value));

    private static async Task<NativeBindingCacheFile[]> ReadPackagesAsync(string work, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(Path.Join(work, "obj", "project.assets.json"));
        using JsonDocument assets = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        string[] folders = [.. assets.RootElement.GetProperty("packageFolders").EnumerateObject().Select(static folder => folder.Name)];
        var files = new List<string>();
        var directories = new List<NativeBindingCacheFile>();
        foreach (JsonProperty library in assets.RootElement.GetProperty("libraries").EnumerateObject())
        {
            if (library.Value.GetProperty("type").GetString() != "package")
            {
                continue;
            }

            string path = library.Value.GetProperty("path").GetString()!;
            string[] candidates = [.. folders.Select(folder => Path.Join(folder, path))];
            string directory = candidates.First(Directory.Exists);
            files.AddRange(Directory.GetFiles(directory, "*", SearchOption.AllDirectories));
            directories.AddRange(candidates.Select(NativeBindingCache.SnapshotDirectory));
        }

        return [.. directories, .. await NativeBindingCache.SnapshotAsync(files.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal), cancellationToken)];
    }

    private static async Task CompileAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using Process process = Process.Start(start) ?? throw new InvalidOperationException("Cannot start the binding compiler.");
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
            }

            await process.WaitForExitAsync(CancellationToken.None);
            throw;
        }

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException($"The binding compiler exited with {process.ExitCode}.");
        }
    }
}
