using System.Globalization;
using System.Runtime.InteropServices;
using Ankus.PgConfig;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

/// <summary>
/// Resolves local PostgreSQL and publishes the sample for the test host's native runtime identifier.
/// </summary>
internal static class IntegrationEnvironment
{
    private static readonly SemaphoreSlim s_stagingLock = new(1, 1);
    private static bool s_nativeExtensionFilesInstalled;
    private static PostgresInstallation? s_installation;
    private static PostgresTestInstallation? s_stagedInstallation;

    /// <summary>
    /// Gets the readiness budget for server startup and for crash recovery, which repeats startup's data-directory
    /// sync, WAL redo and checkpoint on the same shared disk. It matches PostgreSQL's TAP default timeout.
    /// </summary>
    internal static TimeSpan StartupTimeout { get; } = TimeSpan.FromSeconds(180);

    /// <summary>
    /// Gets the configured Native AOT publication and package-consumer concurrency for this machine.
    /// </summary>
    internal static int PackageTestConcurrency { get; } = ReadPackageTestConcurrency();

    /// <summary>
    /// Gets the smallest processor budget among the concurrent compiler slots.
    /// </summary>
    internal static int BuildProcessorCount { get; } = Math.Max(1, Environment.ProcessorCount / PackageTestConcurrency);

    /// <summary>
    /// Gets the largest processor budget, including slots assigned a remainder processor.
    /// </summary>
    internal static int MaximumBuildProcessorCount { get; } = Math.Max(1,
        (Environment.ProcessorCount - 1) / PackageTestConcurrency + 1);

    /// <summary>
    /// Gets the repository root from the test binary's build location.
    /// </summary>
    internal static string RepositoryRoot
    {
        get
        {
            for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
            {
                if (File.Exists(Path.Join(directory.FullName, "Ankus.slnx")))
                {
                    return directory.FullName;
                }
            }

            throw new DirectoryNotFoundException("Run these integration tests from the Ankus repository build output.");
        }
    }

    /// <summary>
    /// Gets the host-native publish directory containing the sample library and its extension installation files.
    /// </summary>
    internal static string NativeOutputDirectory
        => Path.Join(RepositoryRoot, "artifacts", "native", RuntimeInformation.RuntimeIdentifier);

    /// <summary>
    /// Resolves ancestor aliases before comparing ownership, including macOS's temporary-directory aliases.
    /// </summary>
    /// <param name="directory">The existing directory to resolve.</param>
    /// <returns>The absolute directory path with symbolic-link ancestors resolved.</returns>
    internal static string PhysicalDirectory(DirectoryInfo directory)
    {
        if (directory.Parent is null)
        {
            return directory.FullName;
        }

        DirectoryInfo resolved = (DirectoryInfo?)directory.ResolveLinkTarget(returnFinalTarget: true) ?? directory;
        return resolved.Parent is DirectoryInfo parent
            ? Path.Join(PhysicalDirectory(parent), resolved.Name)
            : resolved.FullName;
    }

    /// <summary>
    /// Creates cluster settings using an automatically discovered local installation.
    /// </summary>
    /// <param name="cancellationToken">Cancels PostgreSQL discovery.</param>
    /// <returns>The cluster settings for this environment.</returns>
    internal static async Task<PostgresTestClusterOptions> CreateOptionsAsync(CancellationToken cancellationToken)
    {
        PostgresInstallation installation = await GetInstallationAsync(cancellationToken);
        string nativePath = NativeOutputDirectory.Replace("\\", "/", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal);
        char pathSeparator = OperatingSystem.IsWindows() ? ';' : ':';
        List<string> configuration = [$"dynamic_library_path = '{nativePath}{pathSeparator}$libdir'"];
        if (installation.Version.Major >= 18)
        {
            configuration.Insert(0, $"extension_control_path = '{nativePath}'");
        }
        else
        {
            installation = await PrepareExtensionInstallationAsync(NativeOutputDirectory, cancellationToken);
        }

        return new PostgresTestClusterOptions
        {
            Installation = installation,
            DataDirectoryBase = Path.Join(RepositoryRoot, "artifacts", "test-pgdata"),
            LogDirectory = Path.Join(RepositoryRoot, "artifacts", "test-logs"),
            StartupTimeout = StartupTimeout,
            PostgreSqlConfiguration = configuration,
        };
    }

    /// <summary>
    /// Makes extension control files visible to PostgreSQL releases that predate extension_control_path.
    /// </summary>
    /// <param name="publishDirectory">The publish directory containing extension control and SQL files.</param>
    /// <param name="cancellationToken">Cancels PostgreSQL staging.</param>
    /// <returns>The source installation for PostgreSQL 18 or later; otherwise an isolated staged installation.</returns>
    internal static async Task<PostgresInstallation> PrepareExtensionInstallationAsync(
        string publishDirectory,
        CancellationToken cancellationToken)
    {
        PostgresInstallation installation = await GetInstallationAsync(cancellationToken);
        if (installation.Version.Major >= 18)
        {
            return installation;
        }

        await s_stagingLock.WaitAsync(cancellationToken);
        try
        {
            if (s_stagedInstallation is null)
            {
                string stageRoot = Path.Join(RepositoryRoot, "artifacts", "test-postgresql", Guid.NewGuid().ToString("N"));
                s_stagedInstallation = await PostgresTestInstallation.StageAsync(installation, stageRoot, cancellationToken);
            }

            string source = Path.GetFullPath(publishDirectory);
            string nativeSource = Path.GetFullPath(NativeOutputDirectory);
            bool isNativeOutput = string.Equals(source, nativeSource,
                OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
            if (!isNativeOutput || !s_nativeExtensionFilesInstalled)
            {
                s_stagedInstallation.InstallExtensionFiles(publishDirectory);
                s_nativeExtensionFilesInstalled |= isNativeOutput;
            }

            return s_stagedInstallation.Installation;
        }
        finally
        {
            s_stagingLock.Release();
        }
    }

    /// <summary>
    /// Removes a staged pre-18 PostgreSQL installation after every cluster has stopped.
    /// </summary>
    internal static async Task CleanupAsync()
    {
        await s_stagingLock.WaitAsync();
        try
        {
            if (s_stagedInstallation is not null)
            {
                await s_stagedInstallation.DisposeAsync();
                s_stagedInstallation = null;
                s_nativeExtensionFilesInstalled = false;
            }
        }
        finally
        {
            s_stagingLock.Release();
        }
    }

    /// <summary>
    /// Returns the Unix account that passwordless sudo can run test servers as, or reports the test inconclusive.
    /// </summary>
    /// <remarks>
    /// Like pgrx's dedicated <c>--runas</c> job, these cases need an environment where <c>sudo -n -u ACCOUNT</c>
    /// succeeds and the account can read the selected PostgreSQL installation, such as a distribution package.
    /// </remarks>
    /// <returns>The account named by <c>ANKUS_TEST_RUNAS_ACCOUNT</c>.</returns>
    internal static string RequireRunAsAccount()
    {
        string? account = Environment.GetEnvironmentVariable("ANKUS_TEST_RUNAS_ACCOUNT");
        if (string.IsNullOrEmpty(account))
        {
            Assert.Inconclusive("Set ANKUS_TEST_RUNAS_ACCOUNT to an account that passwordless sudo can run servers as.");
        }

        return account;
    }

    /// <summary>
    /// Creates a directory owned by another account, beneath a directory every account can write.
    /// </summary>
    /// <param name="account">The owning account.</param>
    /// <param name="cancellationToken">Cancels creation.</param>
    /// <returns>The directory's absolute path.</returns>
    internal static async Task<string> CreateAccountDirectoryAsync(string account, CancellationToken cancellationToken)
    {
        string path = Path.Join("/tmp", "ankus-runas-" + Guid.NewGuid().ToString("N"));
        ProcessResult result = await ProcessRunner.RunAsync("sudo", ["-n", "-u", account, "--", "mkdir", "--", path],
            new Dictionary<string, string?>(), cancellationToken, workingDirectory: "/");
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
        return path;
    }

    /// <summary>
    /// Deletes a directory owned by another account.
    /// </summary>
    /// <param name="account">The owning account.</param>
    /// <param name="path">The directory's absolute path.</param>
    /// <returns>A task completing after deletion.</returns>
    internal static async Task DeleteAccountDirectoryAsync(string account, string path)
    {
        ProcessResult result = await ProcessRunner.RunAsync("sudo", ["-n", "-u", account, "--", "rm", "-rf", "--", path],
            new Dictionary<string, string?>(), CancellationToken.None, workingDirectory: "/");
        Assert.AreEqual(0, result.ExitCode, result.StandardError);
    }

    /// <summary>
    /// Publishes a host-native sample library as part of ordinary test initialization.
    /// </summary>
    /// <param name="cancellationToken">Cancels the Native AOT build.</param>
    /// <returns>The absolute path to the extension library.</returns>
    internal static async Task<string> PublishSampleAsync(CancellationToken cancellationToken)
    {
        PostgresInstallation installation = await GetInstallationAsync(cancellationToken);
        (string Directory, string Name)[] extensions =
        [
            ("tests", "Ankus.TestExtension"),
            ("samples", "Ankus.Examples.Enums"),
            ("samples", "Ankus.Examples.Arrays"),
            ("samples", "Ankus.Examples.Bytea"),
            ("samples", "Ankus.Examples.Json"),
            ("samples", "Ankus.Examples.Numeric"),
            ("samples", "Ankus.Examples.DateTime"),
            ("samples", "Ankus.Examples.Errors"),
            ("samples", "Ankus.Examples.CustomTypes"),
            ("samples", "Ankus.Examples.CustomScans"),
            ("samples", "Ankus.Examples.Composites"),
            ("samples", "Ankus.Examples.Operators"),
            ("samples", "Ankus.Examples.Sets"),
            ("samples", "Ankus.Examples.Spi"),
            ("samples", "Ankus.Examples.Strings"),
            ("samples", "Ankus.Examples.Ranges"),
            ("samples", "Ankus.Examples.Triggers"),
            ("samples", "Ankus.Examples.EventTriggers"),
            ("samples", "Ankus.Examples.Aggregates"),
            ("samples", "Ankus.Examples.GenericAggregates"),
            ("samples", "Ankus.Examples.Schemas"),
            ("samples", "Ankus.Examples.CustomSql"),
            ("samples", "Ankus.Examples.Initialization"),
            ("samples", "Ankus.Examples.Configuration"),
            ("samples", "Ankus.Examples.MemoryContexts"),
            ("samples", "Ankus.Examples.SharedMemory"),
            ("samples", "Ankus.Examples.TryCatch"),
            ("samples", "Ankus.Examples.Subtransactions"),
            ("samples", "Ankus.Examples.Threads"),
            ("samples", "Ankus.Examples.Hooks"),
            ("samples", "Ankus.Examples.RewriteManip"),
            ("samples", "Ankus.Examples.Notify"),
            ("samples", "Ankus.Examples.PglzInspect"),
            ("samples", "Ankus.Examples.WalDecoder"),
            ("samples", "Ankus.Examples.Subscriber"),
            ("samples", "Ankus.Examples.CustomLibraryName"),
            ("samples", "Ankus.Examples.VersionedLibrary"),
            ("samples", "Ankus.Examples.VersionedCustomLibraryName"),
            ("samples", "Ankus.Examples.TypeVariants"),
            ("samples", "Ankus.Examples.BadIdeas"),
            ("samples", "Ankus.Examples.MinimalRuntime"),
            ("tests", "Ankus.GucOnlyExtension"),
            ("tests", "Ankus.GucPrefixExtension"),
            ("tests", "Ankus.GucUnicodePrefixExtension"),
            ("tests", "Ankus.GucHooksExtension"),
            ("tests", "Ankus.PreloadExtension"),
            ("tests", "Ankus.GucAssignExtension"),
            ("tests", "Ankus.GucShowExtension"),
            ("samples", "Ankus.Examples.Hello"),
        ];
        string publishRoot = Path.Join(RepositoryRoot, "artifacts", "native-publish", RuntimeInformation.RuntimeIdentifier);
        if (Directory.Exists(publishRoot))
        {
            Directory.Delete(publishRoot, true);
        }

        Directory.CreateDirectory(publishRoot);
        (string Directory, string Name) first = extensions[0];
        string firstProject = Path.Join(RepositoryRoot, first.Directory, first.Name, first.Name + ".csproj");
        await ProcessRunner.RunCheckedAsync("dotnet",
            ["msbuild", firstProject, "-target:ResolveProjectReferences", "-property:Configuration=Release",
                "-property:RuntimeIdentifier=" + RuntimeInformation.RuntimeIdentifier, "-property:SelfContained=true",
                "-property:BuildProjectReferences=true",
                "-property:AnkusPostgresMajor=" + installation.Version.Major.ToString(CultureInfo.InvariantCulture),
                "-property:AnkusPgConfigPath=" + installation.PgConfigPath, "-m", "-nr:false",
                "-bl:" + Path.Join(publishRoot, "references-{}.binlog")],
            new Dictionary<string, string?> { ["MSBUILDDISABLENODEREUSE"] = "1" }, cancellationToken);
        ParallelOptions options = new()
        {
            CancellationToken = cancellationToken,
            MaxDegreeOfParallelism = PackageTestConcurrency,
        };
        await Parallel.ForEachAsync(extensions, options, async (extension, token) =>
        {
            await PublishExtensionAsync(extension.Directory, extension.Name, Path.Join(publishRoot, extension.Name),
                installation, token);
        });

        if (Directory.Exists(NativeOutputDirectory))
        {
            Directory.Delete(NativeOutputDirectory, true);
        }

        foreach (string publication in extensions.Select(extension => Path.Join(publishRoot, extension.Name)))
        {
            if (installation.Version.Major < 18)
            {
                // Each publication owns a separate manifest; the combined native folder retains only the last one.
                await PrepareExtensionInstallationAsync(publication, cancellationToken);
            }

            CopyDirectory(publication, NativeOutputDirectory);
        }

        s_nativeExtensionFilesInstalled = installation.Version.Major < 18;
        Directory.Delete(publishRoot, true);
        return RequireFile(Path.Join(NativeOutputDirectory, PublishedExtension.Read(NativeOutputDirectory).Library));
    }

    private static int ReadPackageTestConcurrency()
    {
        string? value = Environment.GetEnvironmentVariable("ANKUS_PACKAGE_TEST_CONCURRENCY");
        if (string.IsNullOrWhiteSpace(value))
        {
            // A Native AOT publication fans out into several compiler and linker processes.
            return Math.Max(1, (Environment.ProcessorCount + 1) / 2);
        }

        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int concurrency) || concurrency < 1)
        {
            throw new InvalidOperationException("ANKUS_PACKAGE_TEST_CONCURRENCY must be a positive integer.");
        }

        return concurrency;
    }

    /// <summary>
    /// Removes test logs and build logs from earlier runs once they reach the fixtures' retention age. Every run adds
    /// hundreds of server logs and a binary log per publish beneath the repository's test-log directory, which only the
    /// tests write, so its subdirectories expire like the fixtures' own logs.
    /// </summary>
    internal static void PruneTestLogs()
    {
        string root = Path.Join(RepositoryRoot, "artifacts", "test-logs");
        if (!Directory.Exists(root))
        {
            return;
        }

        // The server account may own a directory this account cannot read; skip it rather than fail the run.
        var everything = new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 };
        DateTime cutoff = DateTime.UtcNow - TestLogRetention.Retention;
        foreach (string path in Directory.EnumerateFiles(root, "*", everything).Where(path => File.GetLastWriteTimeUtc(path) < cutoff))
        {
            TryDelete(() => File.Delete(path));
        }

        // Removing a file refreshes its directory's write time, so an emptied directory goes regardless of age. This
        // runs before any test writes, and deepest first, so a directory its subdirectories leave empty goes as well.
        foreach (string path in Directory.EnumerateDirectories(root, "*", everything).OrderDescending(StringComparer.Ordinal)
            .Where(path => !Directory.EnumerateFileSystemEntries(path).Any()))
        {
            TryDelete(() => Directory.Delete(path));
        }

        static void TryDelete(Action delete)
        {
            try
            {
                delete();
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Another run may hold the log open, or the server account may own it; a later run tries again.
            }
        }
    }

    private static async Task PublishExtensionAsync(string directory, string name, string output,
        PostgresInstallation installation, CancellationToken cancellationToken)
    {
        string project = Path.Join(RepositoryRoot, directory, name, name + ".csproj");
        string logs = Path.Join(RepositoryRoot, "artifacts", "test-logs", "publish");
        Directory.CreateDirectory(logs);
        // Parallel.ForEachAsync owns publication concurrency; nested MSBuild nodes would multiply it.
        List<string> arguments = ["publish", project, "--configuration", "Release", "--runtime", RuntimeInformation.RuntimeIdentifier,
            "--no-restore",
            "--self-contained", "true", "--output", output,
            "-m:1",
            "-nr:false",
            "-p:BuildProjectReferences=false",
            "-p:AnkusPostgresMajor=" + installation.Version.Major.ToString(CultureInfo.InvariantCulture),
            "-p:AnkusPgConfigPath=" + installation.PgConfigPath,
            "-bl:" + Path.Join(logs, name + "-{}.binlog")];

        await PackageProcessRunner.RunCheckedAsync(
            "dotnet",
            arguments,
            new Dictionary<string, string?>
            {
                ["MSBUILDDISABLENODEREUSE"] = "1",
            },
            cancellationToken);

        RequireFile(Path.Join(output, PublishedExtension.Read(output).Library));
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string sourceFile in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destinationFile = Path.Join(destination, Path.GetRelativePath(source, sourceFile));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(sourceFile, destinationFile, true);
        }
    }

    /// <summary>
    /// Gets the selected source installation before any test-owned relocation.
    /// </summary>
    /// <param name="cancellationToken">Cancels discovery and PostgreSQL inspection.</param>
    /// <returns>The selected installation, whose files tests must not modify.</returns>
    internal static async Task<PostgresInstallation> GetInstallationAsync(CancellationToken cancellationToken)
    {
        if (s_installation is not null)
        {
            return s_installation;
        }

        string? pgConfig = Environment.GetEnvironmentVariable("ANKUS_TEST_PG_CONFIG");
        PostgresInstallation installation = string.IsNullOrWhiteSpace(pgConfig)
            ? await PostgresInstallation.DiscoverAsync(cancellationToken)
            : await PostgresInstallation.CreateAsync(pgConfig, cancellationToken);
        s_installation = installation;
        return installation;
    }

    private static string RequireFile(string path)
        => File.Exists(path) ? path : throw new FileNotFoundException("The Native AOT extension was not published.", path);
}
