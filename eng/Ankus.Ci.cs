#:property TargetFramework=net10.0
#:property PackAsTool=false
#:property PublishAot=false
#:project ../src/Ankus.PgConfig/Ankus.PgConfig.csproj

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security;
using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ankus.PgConfig;

const string RuntimeRepository = "ankus-net/runtime";

string repositoryRoot = FindRepositoryRoot();
Directory.SetCurrentDirectory(repositoryRoot);

try
{
    if (args.Length == 0)
    {
        throw new InvalidOperationException("Specify an automation command.");
    }

    switch (args[0])
    {
        case "metadata":
            ValidateRuntimeIdentity(repositoryRoot);
            WriteRuntimeOutputs(SelectRuntimeLine(args, 1));
            break;

        case "runtime-info":
        {
            ValidateRuntimeIdentity(repositoryRoot);
            RuntimeLine line = SelectRuntimeLine(args, 1);
            Console.WriteLine($"repository={RuntimeRepository}");
            Console.WriteLine($"base={line.Base}");
            Console.WriteLine($"commit={line.Commit}");
            Console.WriteLine($"runtime_version={line.Version}");
            Console.WriteLine($"compiler_version={line.CompilerVersion}");
            Console.WriteLine($"staging={line.StagingDirectory}");
            break;
        }

        case "select-sdk":
            RequireArguments(args, 2);
            SelectSdk(repositoryRoot, args[1]);
            break;

        case "release-metadata":
            ValidateRuntimeIdentity(repositoryRoot);
            WriteOutput("version", GetReleaseVersion());
            break;

        case "quality":
            BuildSolution(repositoryRoot);
            Run(GetDotNetHost(), ["run", "--project", "src/Ankus.DocGenerator", "--configuration", "Release", "--no-build", "--", "--check"]);
            Run("corepack", ["enable"]);
            Dictionary<string, string?> continuousIntegration = new()
            {
                ["CI"] = "true",
            };
            Run("pnpm", ["install", "--frozen-lockfile"], Path.Join(repositoryRoot, "docs"), environment: continuousIntegration);
            Run("pnpm", ["exec", "astro", "build"], Path.Join(repositoryRoot, "docs"), environment: continuousIntegration);
            Run("pnpm", ["check"], Path.Join(repositoryRoot, "docs"), environment: continuousIntegration);
            break;

        case "runtime-build":
        {
            RequireLineArguments(args, 4);
            ValidateRuntimeIdentity(repositoryRoot);
            RuntimeLine line = SelectRuntimeLine(args, 4);
            IsolateRuntimeBuildPackages(repositoryRoot);
            InstallRuntimePrerequisites();
            BuildRuntime(repositoryRoot, args[1], args[2], line);
            StageRuntime(repositoryRoot, args[1], args[2], args[3], line);
            break;
        }

        case "runtime-pack":
        {
            RequireLineArguments(args, 2);
            ValidateRuntimeIdentity(repositoryRoot);
            RuntimeLine line = SelectRuntimeLine(args, 2);
            RemoveStagingFeedPackages();
            VerifyStagedRuntime(repositoryRoot, args[1], line);
            PackRuntime(
                repositoryRoot,
                args[1],
                line,
                GetStagedRuntimePath(repositoryRoot, args[1], line),
                GetStagedCompilerPath(repositoryRoot, args[1], line),
                GetStagedRuntimeSourcePath(repositoryRoot, args[1], line));
            break;
        }

        case "runtime-test":
        case "runtime-test-build":
            RequireArguments(args, 3);
            ValidateRuntimeIdentity(repositoryRoot);
            RemoveStagingFeedPackages();
            VerifyStagedRuntimes(repositoryRoot, args[1]);
            InstallPostgreSql(repositoryRoot, args[2]);
            ConfigureHeaderFrontend(repositoryRoot);
            ConfigureMacOsSdk();
            ConfigureBindingCache(repositoryRoot);
            ConfigureNuGetHttpCache();
            BuildTests(repositoryRoot);
            if (OperatingSystem.IsLinux())
            {
                VerifyBindingHeaderManifest(repositoryRoot, args[2]);
            }

            if (args[0] == "runtime-test")
            {
                RunRuntimeTests(repositoryRoot);
            }

            break;

        case "runtime-test-run":
            RequireArguments(args, 1);
            ValidateRuntimeIdentity(repositoryRoot);
            RunRuntimeTests(repositoryRoot);
            break;

        case "native-analysis-run":
            RequireArguments(args, 1);
            ValidateRuntimeIdentity(repositoryRoot);
            ConfigureRunAsAccount();
            // The assembly fixture publishes the Hello sample and composes every native fixture from the test extension's
            // generated bridge; this test also compiles the worker signal fixture. CodeQL traces those compilations.
            RunTestModule(repositoryRoot, $"tests/Ankus.IntegrationTests/bin/Release/{ExtensionTestFramework()}/Ankus.IntegrationTests.dll",
                "FullyQualifiedName~BackgroundWorkersRegisterAndShareState");
            break;

        case "target-export":
            RequireArguments(args, 4);
            ValidateRuntimeIdentity(repositoryRoot);
            RemoveStagingFeedPackages();
            VerifyStagedRuntimes(repositoryRoot, args[1]);
            InstallPostgreSql(repositoryRoot, args[2]);
            ConfigureHeaderFrontend(repositoryRoot);
            ConfigureMacOsSdk();
            ConfigureBindingCache(repositoryRoot);
            ExportTarget(repositoryRoot, args[1], args[2], args[3]);
            break;

        case "target-build":
            RequireArguments(args, 4);
            ValidateRuntimeIdentity(repositoryRoot);
            RemoveStagingFeedPackages();
            VerifyStagedRuntime(repositoryRoot, RuntimeInformation.RuntimeIdentifier);
            VerifyStagedRuntime(repositoryRoot, args[2]);
            ConfigureMacOsSdk();
            BuildForTarget(repositoryRoot, args[1], args[2], args[3]);
            break;

        case "target-verify":
            RequireArguments(args, 3);
            InstallPostgreSql(repositoryRoot, args[2]);
            VerifyTargetPublication(args[1], Environment.GetEnvironmentVariable("AnkusPgConfigPath")
                ?? throw new InvalidOperationException("No PostgreSQL installation was selected."));
            break;

        case "header-frontend-check":
            RequireArguments(args, 2);
            VerifyHeaderFrontend(args[1]);
            break;

        case "header-frontend-setup":
            ConfigureHeaderFrontend(repositoryRoot);
            break;

        case "postgresql-check":
            RequireArguments(args, 3);
            VerifyPostgreSqlVersion(args[1]);
            VerifyPostgreSqlHeaders(args[2], args[1]);
            break;

        case "windows-toolchain":
            RequireArguments(args, 1);
            ConfigureWindowsToolchain();
            break;

        case "unit-test":
            RequireArguments(args, 1);
            RunUnitTests(repositoryRoot);
            break;

        case "prepare-reports":
            RequireArguments(args, 1);
            PrepareReports(repositoryRoot);
            break;

        case "runner-cleanup":
            RequireArguments(args, 1);
            CleanRunnerWorkspace(repositoryRoot);
            break;

        case "release-managed":
            RequireArguments(args, 2);
            PackManaged(repositoryRoot, args[1]);
            break;

        case "release-runtime":
        {
            RequireLineArguments(args, 4);
            ValidateRuntimeIdentity(repositoryRoot);
            RuntimeLine line = SelectRuntimeLine(args, 4);
            InstallRuntimePrerequisites();
            BuildRuntime(repositoryRoot, args[1], args[2], line);
            PackRuntime(
                repositoryRoot,
                args[3],
                line,
                GetBuiltRuntimePath(repositoryRoot, args[1], args[2]),
                GetBuiltCompilerPath(repositoryRoot, args[1], args[2]),
                Path.Join(repositoryRoot, "runtime"));
            break;
        }

        case "release-verify":
            RequireArguments(args, 2);
            _ = VerifyPackages(repositoryRoot, args[1]);
            break;

        case "publish":
            RequireArguments(args, 2);
            PublishPackages(repositoryRoot, args[1]);
            break;

        default:
            throw new InvalidOperationException($"Unknown automation command '{args[0]}'.");
    }
}
catch (Exception exception) when (exception is ArgumentException or IOException or InvalidOperationException)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}

return 0;

static string FindRepositoryRoot()
{
    DirectoryInfo? directory = new(Directory.GetCurrentDirectory());

    while (directory is not null)
    {
        if (File.Exists(Path.Join(directory.FullName, "Ankus.slnx")))
        {
            return directory.FullName;
        }

        directory = directory.Parent;
    }

    throw new DirectoryNotFoundException("Could not locate the Ankus repository root.");
}

static void RequireArguments(string[] commandArguments, int count)
{
    if (commandArguments.Length != count)
    {
        throw new InvalidOperationException($"Command '{commandArguments[0]}' expects {count - 1} arguments.");
    }
}

// Runtime commands take their usual arguments and may name a runtime line after them.
static void RequireLineArguments(string[] commandArguments, int count)
{
    if (commandArguments.Length != count && commandArguments.Length != count + 1)
    {
        throw new InvalidOperationException($"Command '{commandArguments[0]}' expects {count - 1} arguments and an optional runtime line.");
    }
}

// Compiles every C# source in the repository without the patched runtime: the solution against PostgreSQL 18's headers,
// and each automation app, including this one, which `dotnet run` compiled before it started. A CI checkout has no
// outputs, so a plain build compiles everything; a rebuild would also clean a project that the solution builds under
// two sets of properties while other projects read its first output.
static void BuildSolution(string repositoryRoot)
{
    ValidateRuntimeIdentity(repositoryRoot);
    InstallPostgreSql(repositoryRoot, "18");
    ConfigureHeaderFrontend(repositoryRoot);
    Run(GetDotNetHost(), ["restore", "Ankus.slnx", "-p:PublishAot=false"]);
    Run(GetDotNetHost(), ["build", "Ankus.slnx", "--configuration", "Release", "--no-restore", "-p:PublishAot=false"]);
    foreach (string app in Directory.EnumerateFiles(Path.Join(repositoryRoot, "eng"), "*.cs")
        .Where(static app => !Path.GetFileName(app).Equals("Ankus.Ci.cs", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
    {
        Run(GetDotNetHost(), ["build", app]);
    }
}

static string GetDotNetHost()
{
    string? hostPath = Environment.GetEnvironmentVariable("DOTNET_HOST_PATH");

    if (!string.IsNullOrWhiteSpace(hostPath))
    {
        return hostPath;
    }

    string? root = Environment.GetEnvironmentVariable("DOTNET_ROOT");

    if (!string.IsNullOrWhiteSpace(root))
    {
        return Path.Join(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    }

    return OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet";
}

// Like pgrx's `cargo pgrx cross pgrx-target`, the target machine measures its installation once; another
// architecture builds from that archive, and the target loads the result.
static void ExportTarget(string repositoryRoot, string runtimeIdentifier, string version, string archive)
{
    string pgConfig = Environment.GetEnvironmentVariable("AnkusPgConfigPath")
        ?? throw new InvalidOperationException("No PostgreSQL installation was selected.");
    Run(GetDotNetHost(), ["msbuild", TargetSampleProject(repositoryRoot), "-restore", "-nologo", "-t:AnkusExportTargetInfo",
        "-p:Configuration=Release", "-p:RuntimeIdentifier=" + runtimeIdentifier, "-p:AnkusPostgresMajor=" + version,
        "-p:AnkusPgConfigPath=" + pgConfig, "-p:AnkusTargetInfoOutput=" + Path.GetFullPath(archive)]);
}

static void BuildForTarget(string repositoryRoot, string archive, string runtimeIdentifier, string output)
{
    // Clang and libclang are unusable here, so the bindings can only come from the archive.
    string missing = Path.Join(repositoryRoot, "artifacts", "no-clang", "clang");
    Run(GetDotNetHost(), ["publish", TargetSampleProject(repositoryRoot), "-nologo", "-c", "Release", "-r", runtimeIdentifier,
        "--self-contained", "true", "-o", Path.GetFullPath(output), "-p:AnkusTargetInfo=" + Path.GetFullPath(archive),
        "-p:AnkusClangPath=" + missing, "-p:AnkusLibClangPath=" + missing],
        environment: new Dictionary<string, string?> { ["AnkusPgConfigPath"] = null, ["AnkusPostgresMajor"] = null });
    PublishedExtension manifest = PublishedExtension.Read(output);
    string architecture = ReadNativeArchitecture(Path.Join(output, manifest.Library));
    if (manifest.RuntimeIdentifier != runtimeIdentifier || !runtimeIdentifier.EndsWith("-" + architecture, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"The {runtimeIdentifier} build produced a {manifest.RuntimeIdentifier} manifest and a {architecture} library.");
    }

    Console.WriteLine($"Built {manifest.Library} for {runtimeIdentifier} from {Path.GetFileName(archive)}.");
}

// Reads the CPU of a Mach-O or ELF library without platform tools.
static string ReadNativeArchitecture(string library)
{
    byte[] header = new byte[20];
    using (FileStream stream = File.OpenRead(library))
    {
        stream.ReadExactly(header);
    }

    if (BitConverter.ToUInt32(header, 0) == 0xFEEDFACF)
    {
        return BitConverter.ToUInt32(header, 4) switch
        {
            0x01000007 => "x64",
            0x0100000C => "arm64",
            uint cpu => throw new InvalidDataException($"Unexpected Mach-O CPU type 0x{cpu:X8}."),
        };
    }

    if (header[0] == 0x7F && header[1] == (byte)'E' && header[2] == (byte)'L' && header[3] == (byte)'F')
    {
        return BitConverter.ToUInt16(header, 18) switch
        {
            62 => "x64",
            183 => "arm64",
            ushort machine => throw new InvalidDataException($"Unexpected ELF machine {machine}."),
        };
    }

    throw new InvalidDataException($"'{library}' is not a Mach-O or ELF library.");
}

static void VerifyTargetPublication(string publication, string pgConfig)
{
    PostgresInstallation installation = PostgresInstallation.CreateAsync(pgConfig).GetAwaiter().GetResult();
    if (installation.Version.Major < 18)
    {
        throw new NotSupportedException("Loading a publication from its own directory requires PostgreSQL 18's extension_control_path.");
    }

    publication = Path.GetFullPath(publication);
    PublishedExtension manifest = PublishedExtension.Read(publication);
    if (manifest.RuntimeIdentifier != RuntimeInformation.RuntimeIdentifier)
    {
        throw new InvalidOperationException($"The publication targets {manifest.RuntimeIdentifier}, not this {RuntimeInformation.RuntimeIdentifier} machine.");
    }

    string root = Directory.CreateTempSubdirectory("ankus-target-verify-").FullName;
    try
    {
        string shared = Path.Join(root, "share");
        Directory.CreateDirectory(Path.Join(shared, "extension"));
        foreach (string file in new[] { manifest.Control, manifest.Sql })
        {
            File.Copy(Path.Join(publication, "extension", file), Path.Join(shared, "extension", file));
        }

        var cluster = new PostgresDevelopmentCluster(installation, Path.Join(root, "home"));
        cluster.StartAsync(new PostgresDevelopmentOptions
        {
            Port = 28700 + installation.Version.Major,
            Settings = new Dictionary<string, string>
            {
                ["dynamic_library_path"] = publication,
                ["extension_control_path"] = shared,
            },
        }).GetAwaiter().GetResult();
        try
        {
            string connection = cluster.GetConnectionStringAsync("postgres").GetAwaiter().GetResult();
            string extension = Path.GetFileNameWithoutExtension(manifest.Control);
            string result = Capture(installation.PsqlPath, ["--dbname", connection, "--no-psqlrc", "--tuples-only", "--no-align",
                "--set", "ON_ERROR_STOP=1", "--command", $"CREATE EXTENSION \"{extension}\"", "--command", "SELECT add(19, 23)"]);
            if (!result.Split('\n').Select(static line => line.Trim()).Contains("42"))
            {
                throw new InvalidOperationException($"The {manifest.RuntimeIdentifier} publication returned '{result}'.");
            }

            Console.WriteLine($"Loaded {manifest.Library} into PostgreSQL {installation.Version}: {result.Trim()}");
        }
        finally
        {
            cluster.StopAsync().GetAwaiter().GetResult();
        }
    }
    finally
    {
        Directory.Delete(root, recursive: true);
    }
}

static string TargetSampleProject(string repositoryRoot)
    => Path.Join(repositoryRoot, "samples", "Ankus.Examples.Hello", "Ankus.Examples.Hello.csproj");

static void ValidateRuntimeIdentity(string repositoryRoot)
{
    foreach (RuntimeLine line in RuntimeLine.All)
    {
        if (!AutomationPatterns.RuntimeCommit().IsMatch(line.Commit))
        {
            throw new InvalidOperationException($"The {line.TargetFramework} runtime commit must be a full lowercase Git commit ID.");
        }

        if (!AutomationPatterns.RuntimeVersion().IsMatch(line.Version))
        {
            throw new InvalidOperationException($"The {line.TargetFramework} runtime package version is invalid.");
        }

        // The SDK and the runtime packages read each line's versions from this file.
        RequireText(repositoryRoot, "src/Ankus.Sdk/targets/Ankus.NativeAot.Lines.props", $"'$(_AnkusNativeAotLine)' == '{line.TargetFramework}'");
        RequireText(repositoryRoot, "src/Ankus.Sdk/targets/Ankus.NativeAot.Lines.props", $">{line.Version}<");
        RequireText(repositoryRoot, "src/Ankus.Sdk/targets/Ankus.NativeAot.Lines.props", $">{line.CompilerVersion}<");
    }
}

static void RequireText(string repositoryRoot, string relativePath, string expected)
{
    string path = Path.Join(repositoryRoot, relativePath);
    string contents = File.ReadAllText(path);

    if (!contents.Contains(expected, StringComparison.Ordinal))
    {
        throw new InvalidOperationException($"{relativePath} does not contain the pinned value {expected}.");
    }
}

static string GetReleaseVersion()
{
    string tag = Environment.GetEnvironmentVariable("RELEASE_TAG")
        ?? throw new InvalidOperationException("RELEASE_TAG is required.");

    if (!AutomationPatterns.ReleaseTag().IsMatch(tag))
    {
        throw new InvalidOperationException("Release tags must use vMAJOR.MINOR.PATCH or a valid prerelease suffix.");
    }

    return tag[1..];
}

static void WriteRuntimeOutputs(RuntimeLine line)
{
    WriteOutput("repository", RuntimeRepository);
    WriteOutput("base", line.Base);
    WriteOutput("commit", line.Commit);
    WriteOutput("runtime_version", line.Version);
    WriteOutput("compiler_version", line.CompilerVersion);
    WriteOutput("staging", line.StagingDirectory);
}

// An optional argument names the runtime line by its target framework; net10.0 when absent.
static RuntimeLine SelectRuntimeLine(string[] args, int index)
    => args.Length > index ? RuntimeLine.Find(args[index]) : RuntimeLine.Default;

// Selects another SDK for this workspace only, such as a .NET 11 preview for an acceptance run. Projects that carry their
// own global.json keep their selection.
static void SelectSdk(string repositoryRoot, string version)
{
    string path = Path.Join(repositoryRoot, "global.json");
    System.Text.Json.Nodes.JsonObject root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(path))?.AsObject()
        ?? throw new InvalidOperationException("global.json must contain an object.");
    root["sdk"] = new System.Text.Json.Nodes.JsonObject
    {
        ["version"] = version,
        ["rollForward"] = "disable",
        ["allowPrerelease"] = version.Contains('-', StringComparison.Ordinal),
    };
    File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);
    Console.WriteLine($"Selected .NET SDK {version} for {repositoryRoot}.");
}

static void WriteOutput(string name, string value)
{
    string outputPath = Environment.GetEnvironmentVariable("GITHUB_OUTPUT")
        ?? throw new InvalidOperationException("GITHUB_OUTPUT is required.");
    File.AppendAllText(outputPath, $"{name}={value}{Environment.NewLine}");
}

// The runtime repository restores from dnceng staging feeds, which carry builds of official package versions, such as
// Microsoft.NETCore.App.Runtime 10.0.13, before or beside nuget.org's signed copies. Its build and probes therefore get
// their own package folder, so no consumer restore on the runner finds a staging copy in the shared cache.
static void IsolateRuntimeBuildPackages(string repositoryRoot)
{
    string? toolCache = Environment.GetEnvironmentVariable("RUNNER_TOOL_CACHE");
    string packages = string.IsNullOrWhiteSpace(toolCache)
        ? Path.Join(repositoryRoot, "runtime", ".packages")
        : Path.Join(toolCache, "ankus-runtime-nuget");
    Directory.CreateDirectory(packages);
    Environment.SetEnvironmentVariable("NUGET_PACKAGES", packages);
    Console.WriteLine($"Runtime build packages: {packages}");
}

// Earlier runtime builds shared the runner's package cache, so remove any staging-feed copy before a consumer restores.
static void RemoveStagingFeedPackages()
{
    string packages = Environment.GetEnvironmentVariable("NUGET_PACKAGES") is { Length: > 0 } configured ? configured
        : Capture(GetDotNetHost(), ["nuget", "locals", "global-packages", "--list"]).Split(':', 2) is [_, string listed] ? listed.Trim()
        : throw new InvalidOperationException("Could not find the NuGet global packages folder.");
    if (!Directory.Exists(packages))
    {
        return;
    }

    // .NET treats .nupkg.metadata as hidden on Unix because of its leading dot; hidden files are skipped by default.
    var options = new EnumerationOptions
    {
        RecurseSubdirectories = true,
        MaxRecursionDepth = 2,
        IgnoreInaccessible = true,
        AttributesToSkip = 0,
    };
    List<string> staging = [];
    foreach (string metadata in Directory.EnumerateFiles(packages, ".nupkg.metadata", options))
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(metadata));
        if (document.RootElement.TryGetProperty("source", out JsonElement source) &&
            source.GetString() is string feed && feed.StartsWith("https://pkgs.dev.azure.com/dnceng/", StringComparison.OrdinalIgnoreCase))
        {
            staging.Add(Path.GetDirectoryName(metadata)!);
        }
    }

    // Delete after enumerating, so no directory is removed while it is being read.
    foreach (string directory in staging)
    {
        Directory.Delete(directory, recursive: true);
    }

    Console.WriteLine($"Removed {staging.Count} staging-feed packages from {packages}.");
}

static void BuildRuntime(string repositoryRoot, string platform, string architecture, RuntimeLine line)
{
    VerifyPlatform(platform, architecture);
    (string cCompiler, string cppCompiler, IReadOnlyDictionary<string, string?>? environment) = GetRuntimeToolchain();
    string runtimeRoot = Path.Join(repositoryRoot, "runtime");
    List<string> arguments =
    [
        "-s",
        "clr.nativeaotruntime+clr.nativeaotlibs+clr.alljits",
        "-c",
        "Release",
        "-arch",
        architecture,
        "/p:ManagePackageVersionsCentrally=false",
    ];

    if (OperatingSystem.IsWindows())
    {
        Run(Path.Join(runtimeRoot, "build.cmd"), arguments, runtimeRoot, true);
    }
    else
    {
        Run(Path.Join(runtimeRoot, "build.sh"), arguments, runtimeRoot, environment: environment);
    }

    string runtimeDotNet = Path.Join(runtimeRoot, OperatingSystem.IsWindows() ? "dotnet.cmd" : "dotnet.sh");
    Run(runtimeDotNet,
    [
        "publish",
        Path.Join(runtimeRoot, "src", "coreclr", "tools", "aot", "ILCompiler", "ILCompiler_publish.csproj"),
        "--configuration", "Release",
        "-p:UseNativeAotForComponents=true",
        $"-p:IlcSdkPath={GetBuiltRuntimePath(repositoryRoot, platform, architecture)}{Path.DirectorySeparatorChar}",
        $"-p:RuntimeFrameworkVersion={line.CompilerVersion}",
        "/p:ManagePackageVersionsCentrally=false",
    ], runtimeRoot, OperatingSystem.IsWindows(), environment);

    if (!OperatingSystem.IsWindows())
    {
        VerifyNativeHostBehavior(repositoryRoot, platform, architecture, cCompiler, cppCompiler, environment);
    }
}

static (string CCompiler, string CppCompiler, IReadOnlyDictionary<string, string?>? Environment) GetRuntimeToolchain()
{
    if (!OperatingSystem.IsMacOS())
    {
        return ("clang", "clang++", null);
    }

    string cCompiler = Capture("xcrun", ["--find", "clang"]);
    string cppCompiler = Capture("xcrun", ["--find", "clang++"]);
    string sdkRoot = Capture("xcrun", ["--sdk", "macosx", "--show-sdk-path"]);
    string compilerDirectory = Path.GetDirectoryName(cCompiler)
        ?? throw new InvalidOperationException($"Could not determine the Apple Clang directory from '{cCompiler}'.");
    string path = Environment.GetEnvironmentVariable("PATH")
        ?? throw new InvalidOperationException("PATH is required to build the runtime.");
    Dictionary<string, string?> environment = new()
    {
        ["CC"] = cCompiler,
        ["CXX"] = cppCompiler,
        ["PATH"] = compilerDirectory + Path.PathSeparator + path,
        ["SDKROOT"] = sdkRoot,
    };

    Console.WriteLine($"Selected Apple Clang for the macOS runtime build: {cCompiler}");
    return (cCompiler, cppCompiler, environment);
}

// Prove that a newly built fork runtime preserves native host shutdown, thread cleanup, signal masks and signal routing.
// Each probe publishes from its own directory, whose global.json selects the SDK for that runtime line's framework.
static void VerifyNativeHostBehavior(
    string repositoryRoot,
    string platform,
    string architecture,
    string cCompiler,
    string cppCompiler,
    IReadOnlyDictionary<string, string?>? environment)
{
    string runtimeIdentifier = $"{platform}-{architecture}";
    string probeRoot = Path.Join(repositoryRoot, "runtime", "eng", "ankus", "fork-probes", "host-shutdown");
    string output = Path.Join(repositoryRoot, "artifacts", "runtime-checks", runtimeIdentifier);
    Run(GetDotNetHost(),
    [
        "publish", Path.Join(probeRoot, "NativeHostShutdownProbe.csproj"),
        "--configuration", "Release", "--runtime", runtimeIdentifier, "--output", output,
        $"-p:IlcSdkPath={GetBuiltRuntimePath(repositoryRoot, platform, architecture)}{Path.DirectorySeparatorChar}",
        $"-p:IlcToolsPath={GetBuiltCompilerPath(repositoryRoot, platform, architecture)}{Path.DirectorySeparatorChar}",
    ], probeRoot, environment: environment);

    string host = Path.Join(output, "host");
    List<string> compilerArguments =
    [
        "-std=gnu17", "-O2", "-Wall", "-Wextra", "-Werror", Path.Join(probeRoot, "host.c"),
        "-pthread", "-o", host,
    ];
    if (OperatingSystem.IsLinux())
    {
        compilerArguments.Add("-ldl");
    }

    Run(cCompiler, compilerArguments, environment: environment);
    string library = "NativeHostShutdownProbe" + (OperatingSystem.IsMacOS() ? ".dylib" : ".so");
    Run(host, [Path.Join(output, library)]);

    string signalProbe = Path.Join(repositoryRoot, "runtime", "eng", "ankus", "fork-probes", "signal-mask", "host.cpp");
    string signalHost = Path.Join(output, "signal-mask-host");
    List<string> signalArguments =
    [
        "-std=c++17", "-O2", "-Wall", "-Wextra", "-Werror",
        "-I" + Path.Join(repositoryRoot, "runtime", "src", "coreclr", "nativeaot", "Runtime", "unix"),
        signalProbe, Path.Join(GetBuiltRuntimePath(repositoryRoot, platform, architecture), "libRuntime.WorkstationGC.a"),
        "-o", signalHost,
    ];
    if (OperatingSystem.IsLinux())
    {
        signalArguments.Add("-ldl");
    }

    Run(cppCompiler, signalArguments, environment: environment);
    Run(signalHost, []);
    Run(signalHost, [Path.Join(output, library)]);

    // Runtime threads must leave the host's process signals pending for the host thread.
    string hostSignalRoot = Path.Join(repositoryRoot, "runtime", "eng", "ankus", "fork-probes", "host-signals");
    string hostSignalOutput = Path.Join(output, "host-signals");
    Run(GetDotNetHost(),
    [
        "publish", Path.Join(hostSignalRoot, "NativeHostSignalProbe.csproj"),
        "--configuration", "Release", "--runtime", runtimeIdentifier, "--output", hostSignalOutput,
        $"-p:IlcSdkPath={GetBuiltRuntimePath(repositoryRoot, platform, architecture)}{Path.DirectorySeparatorChar}",
        $"-p:IlcToolsPath={GetBuiltCompilerPath(repositoryRoot, platform, architecture)}{Path.DirectorySeparatorChar}",
    ], hostSignalRoot, environment: environment);

    string hostSignalHost = Path.Join(hostSignalOutput, "host");
    List<string> hostSignalArguments =
    [
        "-std=gnu17", "-O2", "-Wall", "-Wextra", "-Werror", Path.Join(hostSignalRoot, "host.c"),
        "-pthread", "-o", hostSignalHost,
    ];
    if (OperatingSystem.IsLinux())
    {
        hostSignalArguments.Add("-ldl");
    }

    Run(cCompiler, hostSignalArguments, environment: environment);
    Run(hostSignalHost, [Path.Join(hostSignalOutput, "NativeHostSignalProbe" + (OperatingSystem.IsMacOS() ? ".dylib" : ".so"))]);
}

static void VerifyPlatform(string platform, string architecture)
{
    bool matches = platform switch
    {
        "linux" => OperatingSystem.IsLinux() && architecture is "x64" or "arm64",
        "osx" => OperatingSystem.IsMacOS() && architecture is "x64" or "arm64",
        "windows" => OperatingSystem.IsWindows() && architecture == "x64",
        _ => false,
    };

    bool matchesArchitecture = (architecture, RuntimeInformation.ProcessArchitecture) is
        ("x64", Architecture.X64) or ("arm64", Architecture.Arm64);

    if (!matches || !matchesArchitecture)
    {
        throw new PlatformNotSupportedException($"The {platform}-{architecture} runtime and compiler require a runner with the same operating system and architecture.");
    }
}

static void InstallRuntimePrerequisites()
{
    if (!OperatingSystem.IsLinux())
    {
        return;
    }

    if (Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "self-hosted")
    {
        Run("clang", ["--version"]);
        Run("cmake", ["--version"]);
        Run("ninja", ["--version"]);
        return;
    }

    Run("sudo", ["apt-get", "update"]);
    Run("sudo",
    [
        "apt-get",
        "install",
        "--yes",
        "build-essential",
        "clang",
        "cmake",
        "cpio",
        "curl",
        "git",
        "libicu-dev",
        "libkrb5-dev",
        "liblttng-ust-dev",
        "libssl-dev",
        "lld",
        "lldb",
        "llvm",
        "ninja-build",
        "pigz",
        "python-is-python3",
    ]);
}

static string GetBuiltRuntimePath(string repositoryRoot, string platform, string architecture)
{
    return Path.Join(repositoryRoot, "runtime", "artifacts", "bin", "coreclr", $"{platform}.{architecture}.Release", "aotsdk");
}

static string GetBuiltCompilerPath(string repositoryRoot, string platform, string architecture)
{
    return Path.Join(repositoryRoot, "runtime", "artifacts", "bin", "coreclr", $"{platform}.{architecture}.Release", "ilc-published");
}

static string GetStagedRuntimePath(string repositoryRoot, string runtimeIdentifier, RuntimeLine? line = null)
{
    return Path.Join(repositoryRoot, "artifacts", (line ?? RuntimeLine.Default).StagingDirectory, runtimeIdentifier, "aotsdk");
}

static string GetStagedCompilerPath(string repositoryRoot, string runtimeIdentifier, RuntimeLine? line = null)
{
    return Path.Join(repositoryRoot, "artifacts", (line ?? RuntimeLine.Default).StagingDirectory, runtimeIdentifier, "compiler");
}

static string GetStagedRuntimeSourcePath(string repositoryRoot, string runtimeIdentifier, RuntimeLine? line = null)
{
    return Path.Join(repositoryRoot, "artifacts", (line ?? RuntimeLine.Default).StagingDirectory, runtimeIdentifier, "source");
}

static void StageRuntime(string repositoryRoot, string platform, string architecture, string runtimeIdentifier, RuntimeLine line)
{
    string source = GetBuiltRuntimePath(repositoryRoot, platform, architecture);
    string destination = GetStagedRuntimePath(repositoryRoot, runtimeIdentifier, line);
    string compilerSource = GetBuiltCompilerPath(repositoryRoot, platform, architecture);
    string compilerDestination = GetStagedCompilerPath(repositoryRoot, runtimeIdentifier, line);

    if (!Directory.Exists(source))
    {
        throw new DirectoryNotFoundException($"Runtime output was not found at {source}.");
    }

    CopyDirectory(source, destination);

    if (!Directory.Exists(compilerSource))
    {
        throw new DirectoryNotFoundException($"Compiler output was not found at {compilerSource}.");
    }

    CopyDirectory(compilerSource, compilerDestination);

    string runtimeSource = Path.Join(repositoryRoot, "runtime");
    string stagedSource = GetStagedRuntimeSourcePath(repositoryRoot, runtimeIdentifier, line);
    Directory.CreateDirectory(stagedSource);
    string[] metadataFiles = ["LICENSE.TXT", "THIRD-PARTY-NOTICES.TXT"];

    foreach (string fileName in metadataFiles)
    {
        string sourceFile = Path.Join(runtimeSource, fileName);

        if (!File.Exists(sourceFile))
        {
            throw new FileNotFoundException("Required runtime package metadata was not found.", sourceFile);
        }

        File.Copy(sourceFile, Path.Join(stagedSource, fileName), true);
    }
}

// Checks the default line, and the extensions' own line when an acceptance run retargets them; both build in that run.
static void VerifyStagedRuntimes(string repositoryRoot, string runtimeIdentifier)
{
    VerifyStagedRuntime(repositoryRoot, runtimeIdentifier);
    if (ExtensionTestFramework() != RuntimeLine.Default.TargetFramework)
    {
        VerifyStagedRuntime(repositoryRoot, runtimeIdentifier, RuntimeLine.Find(ExtensionTestFramework()));
    }
}

static void VerifyStagedRuntime(string repositoryRoot, string runtimeIdentifier, RuntimeLine? line = null)
{
    string path = GetStagedRuntimePath(repositoryRoot, runtimeIdentifier, line);
    string sourcePath = GetStagedRuntimeSourcePath(repositoryRoot, runtimeIdentifier, line);
    string compilerPath = GetStagedCompilerPath(repositoryRoot, runtimeIdentifier, line);
    string compilerExecutable = Path.Join(compilerPath, runtimeIdentifier == "win-x64" ? "ilc.exe" : "ilc");

    if (!File.Exists(Path.Join(path, "System.Private.CoreLib.dll"))
        || !File.Exists(compilerExecutable)
        || !File.Exists(Path.Join(sourcePath, "LICENSE.TXT"))
        || !File.Exists(Path.Join(sourcePath, "THIRD-PARTY-NOTICES.TXT")))
    {
        throw new DirectoryNotFoundException($"The staged {runtimeIdentifier} runtime is incomplete.");
    }

    if (!OperatingSystem.IsWindows())
    {
        // Downloaded workflow artifacts do not preserve Unix executable permissions.
        File.SetUnixFileMode(compilerExecutable, File.GetUnixFileMode(compilerExecutable) | UnixFileMode.UserExecute);
    }
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);

    foreach (string sourceDirectory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
    {
        string relativePath = Path.GetRelativePath(source, sourceDirectory);
        Directory.CreateDirectory(Path.Join(destination, relativePath));
    }

    foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        string relativePath = Path.GetRelativePath(source, sourceFile);
        string destinationFile = Path.Join(destination, relativePath);
        File.Copy(sourceFile, destinationFile, true);

        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(destinationFile, File.GetUnixFileMode(sourceFile));
        }
    }
}

static void InstallPostgreSql(string repositoryRoot, string version)
{
    VerifyPostgreSqlVersion(version);

    if (OperatingSystem.IsLinux())
    {
        InstallPostgreSqlLinux(repositoryRoot, version);
        SelectPostgreSql(version, $"/usr/lib/postgresql/{version}/bin/pg_config");
        return;
    }

    if (OperatingSystem.IsMacOS())
    {
        string formula = $"postgresql@{version}";
        string variable = $"PG{version}_PG_CONFIG";
        string? configured = Environment.GetEnvironmentVariable(variable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            string selected = Path.GetFullPath(configured);
            VerifyPostgreSqlHeaders(selected, version);
            SelectPostgreSql(version, selected);
            return;
        }

        if (Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "self-hosted")
        {
            Run("brew", ["install", formula], environment: new Dictionary<string, string?>
            {
                ["HOMEBREW_NO_AUTO_UPDATE"] = "1",
            });
        }

        string? brew = FindOnPath("brew");
        string pgConfig = brew is null
            ? FindOnPath("pg_config") ?? throw new FileNotFoundException(
                $"The macOS runner does not contain PostgreSQL {version}. Set {variable} to its pg_config path.")
            : Path.Join(Capture(brew, ["--prefix", formula]), "bin", "pg_config");
        VerifyPostgreSqlHeaders(pgConfig, version);
        SelectPostgreSql(version, pgConfig);
        return;
    }

    if (OperatingSystem.IsWindows())
    {
        string root = Environment.GetEnvironmentVariable("PGROOT")
            ?? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PostgreSQL", version);
        string pgConfig = Path.Join(root, "bin", "pg_config.exe");

        if (!File.Exists(pgConfig))
        {
            throw new FileNotFoundException($"The Windows runner does not contain PostgreSQL {version}.", pgConfig);
        }

        PostgresVersion actualVersion = VerifyPostgreSqlHeaders(pgConfig, version);
        int minimumMinor = version switch
        {
            "16" => 15,
            "17" => 11,
            "18" => 6,
            _ => 0,
        };
        if (actualVersion.Stage == PostgresReleaseStage.Stable && actualVersion.Minor < minimumMinor)
        {
            throw new InvalidOperationException($"The complete test suite requires PostgreSQL {version}.{minimumMinor} or later in major {version}; the runner provides {actualVersion}. Update the runner installation or select it through PGROOT.");
        }

        Console.WriteLine($"Using preinstalled {actualVersion}.");
        SelectPostgreSql(version, pgConfig);
        return;
    }

    throw new PlatformNotSupportedException("PostgreSQL installation is not defined for this runner.");
}

static string? FindOnPath(string command)
{
    string? path = Environment.GetEnvironmentVariable("PATH");
    if (string.IsNullOrWhiteSpace(path))
    {
        return null;
    }

    return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
        .Select(directory => Path.Join(directory, command))
        .FirstOrDefault(File.Exists);
}

static void SelectPostgreSql(string version, string pgConfig)
{
    WriteEnvironment("ANKUS_TEST_PG_CONFIG", pgConfig);
    WriteEnvironment("AnkusPostgresMajor", version);
    WriteEnvironment("AnkusPgConfigPath", pgConfig);
}

static void VerifyPostgreSqlVersion(string version)
{
    if (version is not ("13" or "14" or "15" or "16" or "17" or "18" or "19"))
    {
        throw new ArgumentOutOfRangeException(nameof(version), version, "PostgreSQL 13 through 19 are supported.");
    }
}

static void ConfigureWindowsToolchain()
{
    if (!OperatingSystem.IsWindows())
    {
        throw new PlatformNotSupportedException("The Windows C++ toolchain requires Windows.");
    }

    string locator = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "Microsoft Visual Studio", "Installer", "vswhere.exe");
    string installation = Capture(locator,
    [
        "-latest", "-products", "*", "-version", "[17.9,)",
        "-requires", "Microsoft.VisualStudio.Component.VC.Tools.x86.x64", "-property", "installationPath",
    ]);
    string setup = Path.Join(installation, "VC", "Auxiliary", "Build", "vcvarsall.bat");
    if (string.IsNullOrWhiteSpace(installation) || !File.Exists(setup))
    {
        throw new InvalidOperationException("Visual Studio 2022 17.9 or later with the C++ x64 tools is required.");
    }

    // cmd.exe needs its own quoting rules for a batch path containing spaces.
    var start = new ProcessStartInfo(Path.Join(Environment.SystemDirectory, "cmd.exe"))
    {
        Arguments = $"/d /s /c \"\"{setup}\" x64 >nul && set\"",
    };
    string configured = CaptureProcess(start);
    foreach (string line in configured.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
    {
        int separator = line.IndexOf('=');
        if (separator <= 0)
        {
            continue;
        }

        string name = line[..separator];
        string value = line[(separator + 1)..];
        if (Environment.GetEnvironmentVariable(name) != value)
        {
            WriteEnvironment(name, value);
        }
    }

    // Reuse this exact developer environment in every child Native AOT build.
    // Otherwise the SDK runs vcvarsall again for each project instance.
    WriteEnvironment("IlcUseEnvironmentalTools", "true");
    Console.WriteLine($"Selected MSVC {Environment.GetEnvironmentVariable("VCToolsVersion")} for x64.");
}

static void ConfigureHeaderFrontend(string repositoryRoot)
{
    string directory;
    if (OperatingSystem.IsLinux())
    {
        directory = FindLinuxHeaderFrontendDirectory() ?? InstallLinuxHeaderFrontend(repositoryRoot);
    }
    else if (OperatingSystem.IsMacOS() &&
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "self-hosted")
    {
        string resourceDirectory = Capture("clang", ["-print-resource-dir"]);
        directory = Path.GetFullPath(Path.Join(resourceDirectory, "..", "..", "..", "bin"));
    }
    else if (OperatingSystem.IsMacOS())
    {
        Run("brew", ["install", "llvm@20"], environment: new Dictionary<string, string?>
        {
            ["HOMEBREW_NO_AUTO_UPDATE"] = "1",
        });
        directory = Path.Join(Capture("brew", ["--prefix", "llvm@20"]), "bin");
    }
    else if (OperatingSystem.IsWindows())
    {
        directory = Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "LLVM", "bin");
    }
    else
    {
        throw new PlatformNotSupportedException("Header frontend installation is not defined for this runner.");
    }

    string compiler = Path.Join(directory, OperatingSystem.IsWindows() ? "clang-cl.exe" : "clang");
    VerifyHeaderFrontend(compiler);
    Environment.SetEnvironmentVariable("PATH", directory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH"));
    string pathFile = Environment.GetEnvironmentVariable("GITHUB_PATH")
        ?? throw new InvalidOperationException("GITHUB_PATH is required.");
    File.AppendAllText(pathFile, directory + Environment.NewLine);
}

static string? FindLinuxHeaderFrontendDirectory()
{
    string? selected = FindOnPath("clang");
    if (selected is not null && HeaderFrontendMajor(selected) >= 20)
    {
        string resourceDirectory = Capture(selected, ["-print-resource-dir"]);
        return Path.GetFullPath(Path.Join(resourceDirectory, "..", "..", "..", "bin"));
    }

    if (!Directory.Exists("/usr/lib"))
    {
        return null;
    }

    return Directory.EnumerateDirectories("/usr/lib", "llvm-*")
        .Select(static path => (Path: path, Major: ParseLlvmDirectoryMajor(path)))
        .Where(static candidate => candidate.Major >= 20 && File.Exists(Path.Join(candidate.Path, "bin", "clang")))
        .OrderByDescending(static candidate => candidate.Major)
        .Select(static candidate => Path.Join(candidate.Path, "bin"))
        .FirstOrDefault();
}

static int ParseLlvmDirectoryMajor(string path)
{
    string name = Path.GetFileName(path);
    return name.StartsWith("llvm-", StringComparison.Ordinal) &&
        int.TryParse(name.AsSpan("llvm-".Length), out int major) ? major : 0;
}

static int HeaderFrontendMajor(string compiler)
{
    string version = Capture(compiler, ["-dumpversion"]);
    int separator = version.IndexOf('.');
    ReadOnlySpan<char> major = separator < 0 ? version.AsSpan() : version.AsSpan(0, separator);
    return int.TryParse(major, out int value) ? value : 0;
}

static string InstallLinuxHeaderFrontend(string repositoryRoot)
{
    Dictionary<string, string> operatingSystem = File.ReadAllLines("/etc/os-release")
        .Select(static line => line.Split('=', 2))
        .Where(static parts => parts.Length == 2)
        .ToDictionary(static parts => parts[0], static parts => parts[1].Trim('"'), StringComparer.Ordinal);
    string codeName = operatingSystem.GetValueOrDefault("VERSION_CODENAME")
        ?? throw new InvalidOperationException("VERSION_CODENAME is missing from /etc/os-release.");
    string temporaryDirectory = Path.Join(repositoryRoot, "artifacts", "ci");
    Directory.CreateDirectory(temporaryDirectory);
    string keyPath = Path.Join(temporaryDirectory, "llvm.asc");
    string sourcePath = Path.Join(temporaryDirectory, "llvm.list");
    using (HttpClient client = new())
    {
        File.WriteAllBytes(keyPath, client.GetByteArrayAsync("https://apt.llvm.org/llvm-snapshot.gpg.key").GetAwaiter().GetResult());
    }

    File.WriteAllText(sourcePath,
        $"deb [signed-by=/usr/share/keyrings/llvm.gpg] https://apt.llvm.org/{codeName}/ llvm-toolchain-{codeName}-20 main{Environment.NewLine}");
    Run("sudo", ["gpg", "--dearmor", "--yes", "--output", "/usr/share/keyrings/llvm.gpg", keyPath]);
    Run("sudo", ["install", "-m", "644", sourcePath, "/etc/apt/sources.list.d/llvm.list"]);
    Run("sudo", ["apt-get", "update"]);
    Run("sudo", ["apt-get", "install", "--yes", "--no-install-recommends", "clang-20", "libclang-20-dev"]);
    return "/usr/lib/llvm-20/bin";
}

static void ConfigureMacOsSdk()
{
    if (!OperatingSystem.IsMacOS())
    {
        return;
    }

    string sdkRoot = Capture("xcrun", ["--sdk", "macosx", "--show-sdk-path"]);
    WriteEnvironment("SDKROOT", sdkRoot);
    Console.WriteLine($"Selected the macOS SDK: {sdkRoot}");
}

static void VerifyHeaderFrontend(string compiler)
{
    Run(compiler, ["--version"]);
    string help = Capture(compiler, ["-cc1", "--help"]);
    if (!help.Split('\n').Any(static line => line.TrimStart().StartsWith("-skip-function-bodies ", StringComparison.Ordinal)))
    {
        throw new InvalidOperationException($"The header collector requires Clang 20 or later with -skip-function-bodies support. Select a supported LLVM toolchain instead of '{compiler}'.");
    }
}

static void ConfigureBindingCache(string repositoryRoot)
{
    string? directory = Environment.GetEnvironmentVariable("AnkusBindingCacheDirectory");
    if (string.IsNullOrWhiteSpace(directory))
    {
        directory = Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "self-hosted"
            ? Path.Join(Environment.GetEnvironmentVariable("RUNNER_TOOL_CACHE")
                ?? throw new InvalidOperationException("RUNNER_TOOL_CACHE is required for the dedicated runner."), "ankus-binding-cache")
            : Path.Join(repositoryRoot, "artifacts", "binding-cache");
    }

    Directory.CreateDirectory(directory);
    WriteEnvironment("AnkusBindingCacheDirectory", Path.GetFullPath(directory));
}

// Runners on one host run as the same account and would share its NuGet HTTP cache. NuGet's audit can then find a
// cached response still open in another runner's restore, and the build treats that as an error. Each dedicated
// runner keeps its own HTTP cache, which survives between its jobs.
static void ConfigureNuGetHttpCache()
{
    if (Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "self-hosted" ||
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("NUGET_HTTP_CACHE_PATH")))
    {
        return;
    }

    string directory = Path.Join(Environment.GetEnvironmentVariable("RUNNER_TOOL_CACHE")
        ?? throw new InvalidOperationException("RUNNER_TOOL_CACHE is required for the dedicated runner."), "nuget-http-cache");
    Directory.CreateDirectory(directory);
    WriteEnvironment("NUGET_HTTP_CACHE_PATH", Path.GetFullPath(directory));
}

static void CleanRunnerWorkspace(string repositoryRoot)
{
    if (Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "self-hosted")
    {
        return;
    }

    StopJobPostgreSqlServers();

    // Runners on one host share the user's build servers, so a compiler server still working for another runner's
    // job can refuse to stop. It holds nothing in this checkout, so the refusal is reported and cleaning continues.
    if (RunForExitCode(GetDotNetHost(), ["build-server", "shutdown"], repositoryRoot) != 0)
    {
        Console.WriteLine("::warning::A build server did not stop; another job on this host may still be using it.");
    }

    // A runtime build runs on the SDK the runtime checkout installs for itself, whose servers keep that SDK's files
    // open; Windows then cannot delete the checkout. Only this job uses that SDK, so its servers must stop.
    string runtimeRoot = Path.Join(repositoryRoot, "runtime");
    string runtimeHost = Path.Join(runtimeRoot, ".dotnet", OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet");
    if (File.Exists(runtimeHost))
    {
        Run(runtimeHost, ["build-server", "shutdown"], runtimeRoot);
    }

    Run("git", ["clean", "-ffdx"], repositoryRoot);
    RemoveAbandonedPackageTestDirectories();
}

// A cancelled or killed suite never reaches its own cleanup, so its package-test directory stays behind and the
// disk eventually fills. Runners on one host share the temporary directory, so only directories older than any
// job can run are removed; a live suite's directory is always newer.
static void RemoveAbandonedPackageTestDirectories()
{
    DateTime cutoff = DateTime.UtcNow.AddHours(-12);
    foreach (string directory in Directory.EnumerateDirectories(Path.GetTempPath(), "ankus package tests *"))
    {
        try
        {
            if (Directory.GetLastWriteTimeUtc(directory) < cutoff)
            {
                Directory.Delete(directory, recursive: true);
                Console.WriteLine($"Removed abandoned package-test directory {Path.GetFileName(directory)}.");
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"Could not remove {Path.GetFileName(directory)}: {error.Message}");
        }
    }
}

// Shuts down the PostgreSQL servers this job left running, as a cancelled or timed-out job does. The runner would
// otherwise kill them outright, which leaves each server's System V shared memory segment allocated until reboot;
// macOS allows only 32, so leaked segments eventually stop every new server from starting.
static void StopJobPostgreSqlServers()
{
    string? trackingId = Environment.GetEnvironmentVariable("RUNNER_TRACKING_ID");
    if (OperatingSystem.IsWindows() || string.IsNullOrEmpty(trackingId))
    {
        return;
    }

    string marker = "RUNNER_TRACKING_ID=" + trackingId;
    Dictionary<int, int> servers = FindPostgreSqlProcesses();
    List<int> postmasters = [.. servers
        .Where(server => !servers.ContainsKey(server.Value) && HasEnvironmentEntry(server.Key, marker))
        .Select(static server => server.Key)];
    string[] signals = ["INT", "QUIT"];
    foreach (string signal in signals)
    {
        // SIGINT requests a fast shutdown and SIGQUIT an immediate one; both exit through PostgreSQL's own cleanup.
        foreach (int postmaster in postmasters)
        {
            Console.WriteLine($"Stopping PostgreSQL server {postmaster} with SIG{signal}.");
            SignalProcess(postmaster, signal);
        }

        DateTime deadline = DateTime.UtcNow.AddSeconds(15);
        postmasters.RemoveAll(static postmaster => !IsProcessRunning(postmaster));
        while (postmasters.Count > 0 && DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            postmasters.RemoveAll(static postmaster => !IsProcessRunning(postmaster));
        }

        if (postmasters.Count == 0)
        {
            return;
        }
    }

    Console.Error.WriteLine($"PostgreSQL servers {string.Join(", ", postmasters)} did not stop.");
}

// Lists this account's PostgreSQL processes with their parent process IDs.
static Dictionary<int, int> FindPostgreSqlProcesses()
{
    var processes = new Dictionary<int, int>();
    if (OperatingSystem.IsLinux())
    {
        foreach (string directory in Directory.EnumerateDirectories("/proc"))
        {
            try
            {
                if (int.TryParse(Path.GetFileName(directory), out int process)
                    && File.ReadAllText(Path.Join(directory, "comm")).TrimEnd('\n') == "postgres")
                {
                    // The command name in /proc/<pid>/stat is parenthesized and may contain spaces; the parent follows the state.
                    string status = File.ReadAllText(Path.Join(directory, "stat"));
                    processes[process] = int.Parse(status[(status.LastIndexOf(')') + 2)..].Split(' ')[1], CultureInfo.InvariantCulture);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The process exited or belongs to another account.
            }
        }

        return processes;
    }

    foreach (string line in Capture("ps", ["-A", "-o", "pid=,ppid=,ucomm="]).Split('\n'))
    {
        string[] fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (fields is [string process, string parent, "postgres"])
        {
            processes[int.Parse(process, CultureInfo.InvariantCulture)] = int.Parse(parent, CultureInfo.InvariantCulture);
        }
    }

    return processes;
}

// Determines whether a process was started with an environment entry, which the runner uses to identify a job's processes.
static bool HasEnvironmentEntry(int process, string entry)
{
    try
    {
        if (OperatingSystem.IsLinux())
        {
            return File.ReadAllText($"/proc/{process}/environ").Split('\0').Contains(entry);
        }

        // macOS ps -E appends the environment of the account's own processes to the command line.
        return (" " + Capture("ps", ["-E", "-ww", "-o", "command=", "-p", process.ToString(CultureInfo.InvariantCulture)]) + " ")
            .Contains(" " + entry + " ", StringComparison.Ordinal);
    }
    catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
    {
        return false;
    }
}

static void SignalProcess(int process, string signal)
{
    try
    {
        _ = Capture("kill", ["-s", signal, process.ToString(CultureInfo.InvariantCulture)]);
    }
    catch (InvalidOperationException)
    {
        // The server exited before the signal arrived.
    }
}

static bool IsProcessRunning(int process)
{
    try
    {
        using Process running = Process.GetProcessById(process);
        return !running.HasExited;
    }
    catch (ArgumentException)
    {
        return false;
    }
}

static void InstallPostgreSqlLinux(string repositoryRoot, string version)
{
    if (Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "self-hosted")
    {
        string pgConfig = $"/usr/lib/postgresql/{version}/bin/pg_config";
        VerifyPostgreSqlHeaders(pgConfig, version);
        Run("valgrind", ["--version"]);
        Run("valgrind", ["--error-exitcode=1", "/bin/true"]);
        return;
    }

    Run("sudo", ["apt-get", "update"]);
    Run("sudo", ["apt-get", "install", "--yes", "ca-certificates", "gnupg"]);

    Dictionary<string, string> operatingSystem = File.ReadAllLines("/etc/os-release")
        .Select(static line => line.Split('=', 2))
        .Where(static parts => parts.Length == 2)
        .ToDictionary(static parts => parts[0], static parts => parts[1].Trim('"'), StringComparer.Ordinal);
    string codeName = operatingSystem.GetValueOrDefault("VERSION_CODENAME")
        ?? throw new InvalidOperationException("VERSION_CODENAME is missing from /etc/os-release.");
    string temporaryDirectory = Path.Join(repositoryRoot, "artifacts", "ci");
    Directory.CreateDirectory(temporaryDirectory);
    string keyPath = Path.Join(temporaryDirectory, "postgresql.asc");
    string sourcePath = Path.Join(temporaryDirectory, "pgdg.list");

    using (HttpClient client = new())
    {
        byte[] key = client.GetByteArrayAsync("https://www.postgresql.org/media/keys/ACCC4CF8.asc").GetAwaiter().GetResult();
        File.WriteAllBytes(keyPath, key);
    }

    string components = version == "19" ? "main 19" : "main";
    File.WriteAllText(sourcePath, $"deb [signed-by=/usr/share/keyrings/postgresql.gpg] https://apt.postgresql.org/pub/repos/apt {codeName}-pgdg {components}{Environment.NewLine}");
    Run("sudo", ["gpg", "--dearmor", "--yes", "--output", "/usr/share/keyrings/postgresql.gpg", keyPath]);
    Run("sudo", ["install", "-m", "644", sourcePath, "/etc/apt/sources.list.d/pgdg.list"]);
    Run("sudo", ["apt-get", "update"]);
    Run("sudo", ["apt-get", "install", "--yes", $"postgresql-{version}", $"postgresql-server-dev-{version}", "valgrind", "libc6-dbg"]);
}

static PostgresVersion VerifyPostgreSqlHeaders(string pgConfig, string version)
{
    PostgresInstallation installation = PostgresInstallation.CreateAsync(pgConfig).GetAwaiter().GetResult();
    if (installation.Label != "pg" + version)
    {
        throw new InvalidOperationException($"Expected PostgreSQL {version}, but the installation provides {installation.Version}.");
    }

    Console.WriteLine($"Using PostgreSQL {installation.Version}.");
    string header = Path.Join(installation.ServerIncludeDirectory, "postgres.h");
    if (!File.Exists(header))
    {
        throw new FileNotFoundException("The runner requires PostgreSQL server headers.", header);
    }

    return installation.Version;
}

static void WriteEnvironment(string name, string value)
{
    Environment.SetEnvironmentVariable(name, value);
    string environmentPath = Environment.GetEnvironmentVariable("GITHUB_ENV")
        ?? throw new InvalidOperationException("GITHUB_ENV is required.");
    File.AppendAllText(environmentPath, $"{name}={value}{Environment.NewLine}");
}

// The integration and Hello sample tests compile against repository extensions, so they follow an acceptance run's
// AnkusExtensionTargetFramework; the other test projects always target net10.0.
static string ExtensionTestFramework()
    => Environment.GetEnvironmentVariable("AnkusExtensionTargetFramework") is { Length: > 0 } framework ? framework : "net10.0";

static void RunRuntimeTests(string repositoryRoot)
{
    ConfigureRunAsAccount();
    using WindowsSuiteSlot? slot = WindowsSuiteSlot.Acquire();
    string integrationTestModule = $"tests/Ankus.IntegrationTests/bin/Release/{ExtensionTestFramework()}/Ankus.IntegrationTests.dll";
    Task integrationTests = Task.Run(() => RunTestModule(repositoryRoot, integrationTestModule));
    Task unitTests = Task.Run(() => RunUnitTestModules(repositoryRoot));
    Task.WhenAll(integrationTests, unitTests).GetAwaiter().GetResult();
}

// Like pgrx's runas job, run-as cases start servers as another account through passwordless sudo.
// GitHub-hosted Linux runners create the account; dedicated runners are provisioned once, as the
// contributing guide describes, and a runner without it reports the cases inconclusive with a warning.
static void ConfigureRunAsAccount()
{
    const string Account = "ankus-runas";
    if (OperatingSystem.IsWindows() || !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ANKUS_TEST_RUNAS_ACCOUNT")))
    {
        return;
    }

    if (!CanRunAs(Account) && OperatingSystem.IsLinux() &&
        Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted")
    {
        Run("sudo", ["-n", "useradd", "--system", "--user-group", "--home-dir", "/nonexistent", "--no-create-home",
            "--shell", "/usr/sbin/nologin", Account]);
    }

    if (CanRunAs(Account))
    {
        Environment.SetEnvironmentVariable("ANKUS_TEST_RUNAS_ACCOUNT", Account);
        Console.WriteLine($"Run-as cases start servers as {Account}.");
    }
    else
    {
        Console.WriteLine($"::warning::Run-as cases are inconclusive: this runner cannot run commands as {Account} through passwordless sudo.");
    }
}

static bool CanRunAs(string account)
{
    using Process process = new()
    {
        StartInfo = new ProcessStartInfo("sudo", ["-n", "-u", account, "--", "true"])
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = "/",
        },
    };
    try
    {
        process.Start();
    }
    catch (System.ComponentModel.Win32Exception)
    {
        return false;
    }

    process.StandardOutput.ReadToEnd();
    process.StandardError.ReadToEnd();
    process.WaitForExit();
    return process.ExitCode == 0;
}

static void RunUnitTests(string repositoryRoot)
{
    BuildTests(repositoryRoot);
    RunUnitTestModules(repositoryRoot);
}

// Keep private machine identifiers out of uploaded files as well as masked job logs.
static void PrepareReports(string repositoryRoot)
{
    string identifiers = Environment.GetEnvironmentVariable("ANKUS_RUNNER_PRIVATE_IDENTIFIERS") ?? string.Empty;
    if (Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "self-hosted" &&
        string.IsNullOrWhiteSpace(identifiers))
    {
        throw new InvalidOperationException("Configure the runner privacy secret before uploading dedicated-runner reports.");
    }

    string[] replacements = [.. identifiers.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .SelectMany(static value => new[] { value, SecurityElement.Escape(value) ?? value })
        .Distinct(StringComparer.Ordinal)
        .OrderByDescending(static value => value.Length)];
    string destination = Path.Join(repositoryRoot, "artifacts", "ci-reports");
    if (Directory.Exists(destination))
    {
        Directory.Delete(destination, true);
    }

    if (Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted")
    {
        SaveBuildTimings(repositoryRoot);
    }

    foreach ((string directory, string pattern) in new[] { ("test-results", "*.trx"), ("test-results", "*.log"), ("test-logs", "*.log") })
    {
        string source = Path.Join(repositoryRoot, "artifacts", directory);
        if (!Directory.Exists(source))
        {
            continue;
        }

        string target = Path.Join(destination, directory);
        Directory.CreateDirectory(target);
        foreach (string file in Directory.EnumerateFiles(source, pattern))
        {
            string contents = File.ReadAllText(file);
            if (Path.GetExtension(file).Equals(".trx", StringComparison.OrdinalIgnoreCase))
            {
                contents = AutomationPatterns.TrxComputerName().Replace(contents, "computerName=\"[private]\"");
            }

            foreach (string value in replacements)
            {
                contents = contents.Replace(value, "[private]", StringComparison.Ordinal);
            }

            File.WriteAllText(Path.Join(target, Path.GetFileName(file)), contents);
        }
    }

    // Show where the suite spent its time directly in the job log.
    string timings = Path.Join(destination, "test-results", "process-timings.log");
    if (File.Exists(timings))
    {
        Console.WriteLine("Integration test process timings:");
        Console.WriteLine(File.ReadAllText(timings));
    }
}

// Replay existing logs without rebuilding. Persist only target/task names and
// durations; binary logs and their environment/property payloads stay local.
static void SaveBuildTimings(string repositoryRoot)
{
    string logs = Path.Join(repositoryRoot, "artifacts", "test-logs");
    Directory.CreateDirectory(logs);
    string[] roots = [logs,
        .. Directory.EnumerateDirectories(Path.GetTempPath(), "ankus package tests *")];
    List<string> report = [];
    EnumerationOptions options = new() { RecurseSubdirectories = true, AttributesToSkip = FileAttributes.ReparsePoint };
    string[] files = [.. Directory.EnumerateFiles(Path.Join(repositoryRoot, "artifacts"), "*.binlog")
        .Concat(roots.SelectMany(root => Directory.EnumerateFiles(root, "*.binlog", options)))
        .Order(StringComparer.Ordinal)];
    foreach (string file in files)
    {
        report.Add("Build: " + Path.GetFileName(file));
        string output;
        try
        {
            output = Capture(GetDotNetHost(), ["msbuild", file, "-nologo", "-verbosity:quiet", "-clp:PerformanceSummary"]);
        }
        catch (InvalidOperationException)
        {
            report.Add("Timing replay unavailable; the build log may be incomplete after cancellation.");
            continue;
        }

        bool include = false;
        int rows = 0;
        foreach (string text in output.Split('\n', StringSplitOptions.TrimEntries))
        {
            if (text is "Target Performance Summary:" or "Task Performance Summary:")
            {
                include = true;
                report.Add(text);
            }
            else if (include && AutomationPatterns.BuildTiming().IsMatch(text))
            {
                report.Add(text);
                rows++;
            }
        }

        if (rows == 0)
        {
            report.Add("No completed target/task timings were recorded; the build log may be incomplete.");
        }
    }

    if (files.Length > 0)
    {
        report.AddRange(ReadBindingTimings(repositoryRoot, files).Split('\n'));
    }

    File.WriteAllLines(Path.Join(logs, "build-timings.log"), report);
}

// Only fixed phase metadata reaches job logs or upload reports, never command
// arguments or binary-log properties. Replay failure does not change test outcomes.
static string ReadBindingTimings(string repositoryRoot, IReadOnlyList<string> files)
{
    try
    {
        return Capture(GetDotNetHost(), ["run", "--file",
            Path.Join(repositoryRoot, "eng", "Ankus.BuildTimings.cs"), "--", .. files]);
    }
    catch (InvalidOperationException)
    {
        return "Binding task timing reader unavailable; existing target/task summaries remain authoritative.";
    }
}

static void BuildTests(string repositoryRoot)
{
    string logs = Path.Join(repositoryRoot, "artifacts", "test-logs");
    Directory.CreateDirectory(logs);
    string binlog = Path.Join(logs, "build-tests-" + Guid.NewGuid().ToString("N") + ".binlog");
    Run(GetDotNetHost(), ["build", "Ankus.slnx", "--configuration", "Release", "-m",
        "-clp:PerformanceSummary", "-bl:" + binlog], repositoryRoot);
    if (Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted")
    {
        Console.WriteLine(ReadBindingTimings(repositoryRoot, [binlog]));
    }
}

static void VerifyBindingHeaderManifest(string repositoryRoot, string version)
{
    string pgConfig = Environment.GetEnvironmentVariable("AnkusPgConfigPath")
        ?? throw new InvalidOperationException("AnkusPgConfigPath is required after PostgreSQL selection.");
    Run(GetDotNetHost(),
    [
        "run", "--project", "src/Ankus.Build", "--configuration", "Release", "--no-build", "--",
        "binding-header-manifest", version, pgConfig,
        Path.Join(repositoryRoot, "src", "Ankus.Build", "Bindings", $"pg{version}.h"), "--check",
    ], repositoryRoot);
}

static void RunUnitTestModules(string repositoryRoot)
{
    string[] testModules =
    [
        "tests/Ankus.Build.Tests/bin/Release/net10.0/Ankus.Build.Tests.dll",
        $"tests/Ankus.Examples.Hello.Tests/bin/Release/{ExtensionTestFramework()}/Ankus.Examples.Hello.Tests.dll",
        "tests/Ankus.Generators.Tests/bin/Release/net10.0/Ankus.Generators.Tests.dll",
        "tests/Ankus.PgConfig.Tests/bin/Release/net10.0/Ankus.PgConfig.Tests.dll",
        "tests/Ankus.Runtime.Tests/bin/Release/net10.0/Ankus.Runtime.Tests.dll",
    ];

    foreach (string testModule in testModules)
    {
        RunTestModule(repositoryRoot, testModule);
    }
}

static void RunTestModule(string repositoryRoot, string testModule, string? filter = null)
{
    string path = Path.Join(repositoryRoot, testModule.Replace('/', Path.DirectorySeparatorChar));

    if (!File.Exists(path))
    {
        throw new FileNotFoundException("A required test module was not built.", path);
    }

    List<string> arguments =
    [
        "test",
        "--test-modules",
        testModule,
        "--root-directory",
        repositoryRoot,
        "--minimum-expected-tests",
        "1",
        "--report-trx",
        "--results-directory",
        Path.Join(repositoryRoot, "artifacts", "test-results"),
    ];
    if (filter is not null)
    {
        arguments.AddRange(["--filter", filter]);
    }

    if (Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") == "github-hosted")
    {
        // Completed case names and durations survive a timeout that prevents the final TRX from being written.
        arguments.AddRange(["--output", "Detailed", "--show-stdout", "Failed", "--show-stderr", "Failed"]);
    }

    Run(GetDotNetHost(), arguments, repositoryRoot);
}

static void PackManaged(string repositoryRoot, string packageVersion)
{
    Run(GetDotNetHost(), ["restore", "Ankus.slnx", "-m:1", "-p:PublishAot=false"], repositoryRoot);
    string[] projects =
    [
        "src/Ankus.Generators/Ankus.Generators.csproj",
        "src/Ankus.PgConfig/Ankus.PgConfig.csproj",
        "src/Ankus.Runtime/Ankus.Runtime.csproj",
        "src/Ankus.Sdk/Ankus.Sdk.csproj",
        "src/Ankus.Testing/Ankus.Testing.csproj",
        "src/Ankus.Templates/Ankus.Templates.csproj",
        "src/Ankus.Tool/Ankus.Tool.csproj",
    ];

    foreach (string project in projects)
    {
        Run(GetDotNetHost(),
        [
            "pack",
            project,
            "--configuration",
            "Release",
            "--no-restore",
            "--output",
            "artifacts/packages",
            "-m:1",
            $"-p:PackageVersion={packageVersion}",
        ], repositoryRoot);
    }
}

static void PackRuntime(
    string repositoryRoot,
    string runtimeIdentifier,
    RuntimeLine line,
    string runtimeSdkPath,
    string compilerToolsPath,
    string runtimeSourcePath)
{
    string normalizedSdkPath = Path.EndsInDirectorySeparator(runtimeSdkPath)
        ? runtimeSdkPath
        : runtimeSdkPath + Path.DirectorySeparatorChar;
    Run(GetDotNetHost(),
    [
        "pack",
        "src/Ankus.NativeAot.Runtime/Ankus.NativeAot.Runtime.csproj",
        "--configuration",
        "Release",
        "--output",
        "artifacts/packages",
        "-m:1",
        $"-p:AnkusRuntimeLine={line.TargetFramework}",
        $"-p:AnkusRuntimeIdentifier={runtimeIdentifier}",
        $"-p:AnkusRuntimeSdkPath={normalizedSdkPath}",
        $"-p:AnkusRuntimeSourcePath={runtimeSourcePath}",
    ], repositoryRoot);

    string normalizedCompilerPath = Path.EndsInDirectorySeparator(compilerToolsPath)
        ? compilerToolsPath
        : compilerToolsPath + Path.DirectorySeparatorChar;
    Run(GetDotNetHost(),
    [
        "pack",
        "src/Ankus.NativeAot.Compiler/Ankus.NativeAot.Compiler.csproj",
        "--configuration",
        "Release",
        "--output",
        "artifacts/packages",
        "-m:1",
        $"-p:AnkusRuntimeLine={line.TargetFramework}",
        $"-p:AnkusCompilerRuntimeIdentifier={runtimeIdentifier}",
        $"-p:AnkusCompilerToolsPath={normalizedCompilerPath}",
        $"-p:AnkusRuntimeSourcePath={runtimeSourcePath}",
    ], repositoryRoot);
}

static void PublishPackages(string repositoryRoot, string packageVersion)
{
    string packageDirectory = Path.Join(repositoryRoot, "artifacts", "packages");
    string[] packages = VerifyPackages(repositoryRoot, packageVersion);
    string apiKey = Environment.GetEnvironmentVariable("NUGET_API_KEY")
        ?? throw new InvalidOperationException("NUGET_API_KEY is required.");

    foreach (string package in packages)
    {
        Run(GetDotNetHost(),
        [
            "nuget",
            "push",
            Path.Join(packageDirectory, package),
            "--api-key",
            apiKey,
            "--source",
            "https://api.nuget.org/v3/index.json",
            "--skip-duplicate",
        ], repositoryRoot, sensitiveValue: apiKey);
    }
}

// Checks that the release has exactly the expected packages and that each one's manifest names its file, the MIT
// license and the Ankus repository at the commit being released. A rehearsal runs the same checks without publishing.
static string[] VerifyPackages(string repositoryRoot, string packageVersion)
{
    string packageDirectory = Path.Join(repositoryRoot, "artifacts", "packages");
    string[] runtimeIdentifiers = ["linux-arm64", "linux-x64", "osx-arm64", "osx-x64", "win-x64"];
    string[] packages =
    [
        .. RuntimeLine.All.SelectMany(line => runtimeIdentifiers.SelectMany(rid => new[]
        {
            $"Ankus.NativeAot.Runtime.{rid}.{line.Version}.nupkg",
            $"Ankus.NativeAot.Compiler.{rid}.{line.Version}.nupkg",
        })),
        $"Ankus.Generators.{packageVersion}.nupkg",
        $"Ankus.PgConfig.{packageVersion}.nupkg",
        $"Ankus.Runtime.{packageVersion}.nupkg",
        $"Ankus.Testing.{packageVersion}.nupkg",
        $"Ankus.Templates.{packageVersion}.nupkg",
        $"Ankus.Tool.{packageVersion}.nupkg",
        $"Ankus.Sdk.{packageVersion}.nupkg",
    ];
    string[] actual =
    [
        .. Directory.GetFiles(packageDirectory, "*.nupkg", SearchOption.TopDirectoryOnly)
            .Select(static path => Path.GetFileName(path)
                ?? throw new InvalidOperationException($"Package path has no file name: {path}"))
            .Order(StringComparer.Ordinal),
    ];
    string[] expected = [.. packages.Order(StringComparer.Ordinal)];

    if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
    {
        throw new InvalidOperationException($"Package set does not match. Expected: {string.Join(", ", expected)}. Actual: {string.Join(", ", actual)}.");
    }

    string? commit = Environment.GetEnvironmentVariable("GITHUB_SHA");
    foreach (string package in packages)
    {
        using ZipArchive archive = ZipFile.OpenRead(Path.Join(packageDirectory, package));
        ZipArchiveEntry manifest = archive.Entries.SingleOrDefault(static entry =>
                !entry.FullName.Contains('/', StringComparison.Ordinal) && entry.FullName.EndsWith(".nuspec", StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"{package} has no root manifest.");
        XElement metadata;
        using (Stream stream = manifest.Open())
        {
            metadata = XDocument.Load(stream).Root?.Elements().SingleOrDefault(static element => element.Name.LocalName == "metadata")
                ?? throw new InvalidOperationException($"{package} has no manifest metadata.");
        }

        string Value(string name) => metadata.Elements().SingleOrDefault(element => element.Name.LocalName == name)?.Value
            ?? throw new InvalidOperationException($"{package} has no {name}.");
        XElement repository = metadata.Elements().SingleOrDefault(static element => element.Name.LocalName == "repository")
            ?? throw new InvalidOperationException($"{package} has no repository.");
        if (Value("id") + "." + Value("version") + ".nupkg" != package)
        {
            throw new InvalidOperationException($"{package} declares {Value("id")} {Value("version")}.");
        }

        if (Value("license") != "MIT" || (string?)repository.Attribute("url") != "https://github.com/ankus-net/ankus")
        {
            throw new InvalidOperationException($"{package} does not declare the MIT license and Ankus repository.");
        }

        if (!string.IsNullOrEmpty(commit) && (string?)repository.Attribute("commit") != commit)
        {
            throw new InvalidOperationException($"{package} was packed from {(string?)repository.Attribute("commit")}, not {commit}.");
        }
    }

    return packages;
}

static void Run(
    string fileName,
    IReadOnlyList<string> arguments,
    string? workingDirectory = null,
    bool useShellExecute = false,
    IReadOnlyDictionary<string, string?>? environment = null,
    string? sensitiveValue = null)
{
    int exitCode = RunForExitCode(fileName, arguments, workingDirectory, useShellExecute, environment, sensitiveValue);
    if (exitCode != 0)
    {
        throw new InvalidOperationException($"Command failed with exit code {exitCode}: {fileName}");
    }
}

// Runs a command, echoing its output, and returns its exit code for the caller to judge.
static int RunForExitCode(
    string fileName,
    IReadOnlyList<string> arguments,
    string? workingDirectory = null,
    bool useShellExecute = false,
    IReadOnlyDictionary<string, string?>? environment = null,
    string? sensitiveValue = null)
{
    using Process process = new();
    process.StartInfo.FileName = fileName;
    process.StartInfo.WorkingDirectory = workingDirectory ?? Directory.GetCurrentDirectory();
    process.StartInfo.UseShellExecute = useShellExecute;

    if (!useShellExecute)
    {
        if (Path.GetFileNameWithoutExtension(fileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
        {
            process.StartInfo.Environment["MSBUILDDISABLENODEREUSE"] = "1";
            process.StartInfo.Environment.Remove("Platform");
        }

        process.StartInfo.RedirectStandardOutput = true;
        process.StartInfo.RedirectStandardError = true;
        process.OutputDataReceived += static (_, eventArguments) =>
        {
            if (eventArguments.Data is not null)
            {
                Console.Out.WriteLine(eventArguments.Data);
            }
        };
        process.ErrorDataReceived += static (_, eventArguments) =>
        {
            if (eventArguments.Data is not null)
            {
                Console.Error.WriteLine(eventArguments.Data);
            }
        };
    }

    foreach (string argument in arguments)
    {
        process.StartInfo.ArgumentList.Add(argument);
    }

    if (environment is not null)
    {
        foreach ((string name, string? value) in environment)
        {
            process.StartInfo.Environment[name] = value;
        }
    }

    Console.WriteLine($"> {fileName} {string.Join(' ', arguments.Select(argument => QuoteArgument(argument == sensitiveValue ? "***" : argument)))}");

    if (!process.Start())
    {
        throw new InvalidOperationException($"Command could not start: {fileName}");
    }

    if (!useShellExecute)
    {
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
    }

    process.WaitForExit();
    return process.ExitCode;
}

static string Capture(string fileName, IReadOnlyList<string> arguments)
{
    var start = new ProcessStartInfo(fileName);
    foreach (string argument in arguments)
    {
        start.ArgumentList.Add(argument);
    }

    return CaptureProcess(start);
}

static string CaptureProcess(ProcessStartInfo start)
{
    start.UseShellExecute = false;
    start.RedirectStandardOutput = true;
    start.RedirectStandardError = true;
    using Process process = new() { StartInfo = start };
    process.Start();
    Task<string> output = process.StandardOutput.ReadToEndAsync();
    Task<string> error = process.StandardError.ReadToEndAsync();
    process.WaitForExit();
    Task.WaitAll(output, error);

    if (process.ExitCode != 0)
    {
        throw new InvalidOperationException($"Command failed: {start.FileName}{Environment.NewLine}{error.Result}");
    }

    return output.Result.Trim();
}

static string QuoteArgument(string argument)
{
    return argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;
}

/// <summary>
/// Limits complete Windows test execution without GitHub concurrency groups that replace pending jobs.
/// </summary>
internal sealed class WindowsSuiteSlot(Mutex mutex) : IDisposable
{
    private readonly Mutex _mutex = mutex;
    private bool _disposed;

    /// <summary>
    /// Acquires one of the two machine-wide suite slots for a self-hosted Windows runner.
    /// </summary>
    /// <returns>The acquired slot, or null outside self-hosted Windows execution.</returns>
    internal static WindowsSuiteSlot? Acquire()
    {
        if (!OperatingSystem.IsWindows() || Environment.GetEnvironmentVariable("RUNNER_ENVIRONMENT") != "self-hosted")
        {
            return null;
        }

        Mutex[] slots =
        [
            new(false, @"Global\Ankus.CI.Windows.FullSuite.0"),
            new(false, @"Global\Ankus.CI.Windows.FullSuite.1"),
        ];
        Console.WriteLine("Waiting for one of two Windows full-suite slots.");
        int index;
        try
        {
            index = WaitHandle.WaitAny(slots);
        }
        catch (AbandonedMutexException exception)
        {
            index = exception.MutexIndex;
        }

        for (int candidate = 0; candidate < slots.Length; candidate++)
        {
            if (candidate != index)
            {
                slots[candidate].Dispose();
            }
        }

        Console.WriteLine($"Acquired Windows full-suite slot {index + 1} of 2.");
        return new(slots[index]);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _mutex.ReleaseMutex();
        _mutex.Dispose();
        _disposed = true;
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// Describes a patched runtime line: the extension target framework it serves, the fork commit it builds, and the package
/// and compiler versions it ships. The SDK reads the same versions from Ankus.NativeAot.Lines.props.
/// </summary>
/// <param name="TargetFramework">The extension target framework.</param>
/// <param name="Base">The upstream release tag the fork commit is based on.</param>
/// <param name="Commit">The fork commit to build.</param>
/// <param name="Version">The patched runtime and compiler host package version.</param>
/// <param name="CompilerVersion">The upstream ILCompiler and framework version.</param>
internal sealed record RuntimeLine(string TargetFramework, string Base, string Commit, string Version, string CompilerVersion)
{
    /// <summary>
    /// Gets the line for net10.0, which commands use when no line is named.
    /// </summary>
    internal static RuntimeLine Default { get; } = new("net10.0", "v10.0.13", "72c3927a21db0faa1205127265bda90fdf2874dd",
        "10.0.13-ankus.1", "10.0.13");

    /// <summary>
    /// Gets every supported line.
    /// </summary>
    internal static IReadOnlyList<RuntimeLine> All { get; } =
    [
        Default,
        new("net11.0", "v11.0.0-rc.2.26504.105", "9692d0ff7adccba4178cc65bdc5d9bd093c031cc",
            "11.0.0-rc.2.26504.105-ankus.1", "11.0.0-rc.2.26504.105"),
    ];

    /// <summary>
    /// Gets the artifacts directory this line stages into; net10.0 keeps the original location.
    /// </summary>
    internal string StagingDirectory => this == Default ? "nativeaot" : "nativeaot-" + TargetFramework;

    /// <summary>
    /// Finds the line that serves a target framework.
    /// </summary>
    /// <param name="targetFramework">The extension target framework.</param>
    /// <returns>The runtime line.</returns>
    internal static RuntimeLine Find(string targetFramework)
        => All.FirstOrDefault(line => line.TargetFramework == targetFramework)
            ?? throw new InvalidOperationException($"No Ankus runtime line serves {targetFramework}.");
}

/// <summary>
/// Provides the generated regular expressions used by repository automation.
/// </summary>
internal static partial class AutomationPatterns
{
    /// <summary>
    /// Matches a performance row with an MSBuild target/task name and no command or property payload.
    /// </summary>
    [GeneratedRegex(@"^\d+ ms\s+[A-Za-z_][A-Za-z0-9_.]*\s+\d+ calls$", RegexOptions.CultureInvariant)]
    internal static partial Regex BuildTiming();

    /// <summary>
    /// Matches a full lowercase Git commit ID.
    /// </summary>
    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.CultureInvariant)]
    internal static partial Regex RuntimeCommit();

    /// <summary>
    /// Matches an Ankus runtime package version.
    /// </summary>
    [GeneratedRegex("^[0-9]+\\.[0-9]+\\.[0-9]+-[0-9A-Za-z.-]+$", RegexOptions.CultureInvariant)]
    internal static partial Regex RuntimeVersion();

    /// <summary>
    /// Matches an Ankus release tag.
    /// </summary>
    [GeneratedRegex("^v[0-9]+\\.[0-9]+\\.[0-9]+(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$", RegexOptions.CultureInvariant)]
    internal static partial Regex ReleaseTag();

    /// <summary>
    /// Matches the machine identity recorded by the TRX schema.
    /// </summary>
    [GeneratedRegex("computerName=\"[^\"]*\"", RegexOptions.CultureInvariant)]
    internal static partial Regex TrxComputerName();
}
