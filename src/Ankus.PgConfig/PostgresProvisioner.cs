using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace Ankus.PgConfig;

/// <summary>
/// Installs selected PostgreSQL releases in an Ankus home without changing system installations or databases.
/// </summary>
/// <param name="client">The caller-owned client used for upstream HTTPS downloads.</param>
/// <param name="homeDirectory">The Ankus home, defaulting to ANKUS_HOME or the current user's ~/.ankus directory.</param>
public sealed class PostgresProvisioner(HttpClient client, string? homeDirectory = null)
{
    private static readonly string[] s_buildEnvironment =
        ["CC", "CFLAGS", "CPPFLAGS", "LDFLAGS", "LIBS", "PKG_CONFIG_PATH", "ICU_CFLAGS", "ICU_LIBS"];
    private readonly PostgresDistributionClient _distributions = new(client ?? throw new ArgumentNullException(nameof(client)));
    private readonly string _home = new PostgresRegistry(homeDirectory).HomeDirectory;

    /// <summary>
    /// Downloads the latest releases, builds Unix sources or extracts Windows x64 binaries, and validates each installation.
    /// A Windows release without EDB binaries is built from source with meson and Visual Studio's compiler.
    /// Existing completed installations with matching build options are reused. Registration is a separate atomic operation.
    /// </summary>
    /// <param name="majors">The PostgreSQL majors to install, from 13 through 19.</param>
    /// <param name="options">Source build options.</param>
    /// <param name="progress">Optional progress messages.</param>
    /// <param name="cancellationToken">Cancels downloads or terminates the active native build and its children.</param>
    /// <returns>Validated installations in ascending major order.</returns>
    public async Task<IReadOnlyList<PostgresInstallation>> InstallAsync(IReadOnlyCollection<int> majors,
        PostgresProvisionOptions? options = null, IProgress<string>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(majors);
        if (majors.Count == 0)
        {
            throw new ArgumentException("Specify at least one PostgreSQL major to install.", nameof(majors));
        }

        options ??= new PostgresProvisionOptions();
        ValidateOptions(options, OperatingSystem.IsWindows());
        options = new PostgresProvisionOptions
        {
            Jobs = options.Jobs,
            ConfigureFlags = [.. options.ConfigureFlags],
            EnableValgrind = options.EnableValgrind,
            Release = options.Release,
        };
        if (OperatingSystem.IsWindows() && RuntimeInformation.OSArchitecture != Architecture.X64)
        {
            throw new PlatformNotSupportedException("PostgreSQL binary downloads require Windows x64.");
        }

        IReadOnlyDictionary<int, PostgresVersion> releases = await _distributions.GetLatestAsync(majors, cancellationToken).ConfigureAwait(false);
        string root = Path.Join(_home, "postgres");
        Directory.CreateDirectory(root);
        // Leave the lock inode in place, including after a failed build.
        using var installationLock = new FileStream(Path.Join(root, "install.lock"), FileMode.OpenOrCreate,
            FileAccess.ReadWrite, FileShare.None);
        var installations = new List<PostgresInstallation>();
        foreach (PostgresVersion version in releases.OrderBy(static pair => pair.Key).Select(static pair => pair.Value))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string identity = GetBuildIdentity(options);
            string destination = Path.Join(root, $"{version}-{identity}");
            string marker = Path.Join(destination, ".ankus-installation");
            if (Directory.Exists(destination))
            {
                if (!File.Exists(marker) || await File.ReadAllTextAsync(marker, cancellationToken).ConfigureAwait(false) != version.ToString())
                {
                    throw new IOException($"Refusing to replace an existing PostgreSQL directory: {destination}");
                }

                installations.Add(await ValidateInstallationAsync(destination, version, cancellationToken).ConfigureAwait(false));
                progress?.Report($"Using PostgreSQL {version}: {destination}");
                continue;
            }

            string scratch = Path.Join(root, ".build-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(scratch);
            bool moved = false;
            try
            {
                progress?.Report($"Downloading PostgreSQL {version}.");
                string distribution = Path.Join(scratch, "distribution");
                bool binaries = OperatingSystem.IsWindows();
                try
                {
                    await _distributions.DownloadAsync(version, distribution, binaries, cancellationToken, progress).ConfigureAwait(false);
                }
                catch (PostgresArchiveUnavailableException unavailable)
                {
                    RequireWindowsBuildTools(unavailable.Message, version, ExecutableLocator.Find);
                    progress?.Report($"{unavailable.Message} Building it from source with meson.");
                    binaries = false;
                    await _distributions.DownloadAsync(version, distribution, windowsBinaries: false, cancellationToken, progress).ConfigureAwait(false);
                }

                string staged = distribution;
                if (!binaries)
                {
                    progress?.Report($"Building PostgreSQL {version} with {options.Jobs} jobs.");
                    if (OperatingSystem.IsWindows())
                    {
                        // Windows installations find their files relative to their programs, so they move as built.
                        staged = Path.Join(scratch, "install");
                        await BuildWindowsAsync(distribution, staged, destination, options, cancellationToken).ConfigureAwait(false);
                    }
                    else
                    {
                        string stagingRoot = Path.Join(scratch, "install");
                        staged = Path.Join(stagingRoot, Path.GetRelativePath(Path.GetPathRoot(destination)!, destination));
                        await BuildAsync(distribution, destination, stagingRoot, options, cancellationToken).ConfigureAwait(false);
                    }
                }

                await ValidateInstallationAsync(staged, version, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(staged, destination);
                moved = true;
                PostgresInstallation installation = await ValidateInstallationAsync(destination, version, cancellationToken).ConfigureAwait(false);
                if (!OperatingSystem.IsWindows())
                {
                    // The source tree is removed below; keep its Valgrind suppressions in the PGXS tree, where
                    // Valgrind startup finds them, as pgrx's kept source tree provides them.
                    await InstallValgrindSuppressionsAsync(distribution, installation, cancellationToken).ConfigureAwait(false);
                }

                await File.WriteAllTextAsync(marker, version.ToString(), cancellationToken).ConfigureAwait(false);
                installations.Add(installation);
                moved = false;
                progress?.Report($"Installed PostgreSQL {version}: {destination}");
            }
            finally
            {
                if (moved)
                {
                    Directory.Delete(destination, recursive: true);
                }

                Directory.Delete(scratch, recursive: true);
            }
        }

        return installations;
    }

    /// <summary>
    /// Copies PostgreSQL's <c>src/tools/valgrind.supp</c> from a source tree into the installation's PGXS tree.
    /// </summary>
    internal static async Task InstallValgrindSuppressionsAsync(string source, PostgresInstallation installation, CancellationToken cancellationToken)
    {
        string suppressions = Path.Join(source, "src", "tools", "valgrind.supp");
        if (!File.Exists(suppressions))
        {
            return;
        }

        string pgxs = Path.GetFullPath(await PostgresInstallation.QueryAsync(installation.PgConfigPath, ["--pgxs"], cancellationToken).ConfigureAwait(false));
        string tools = Path.Join(Path.GetDirectoryName(Path.GetDirectoryName(pgxs)!)!, "tools");
        Directory.CreateDirectory(tools);
        File.Copy(suppressions, Path.Join(tools, "valgrind.supp"), overwrite: true);
    }

    /// <summary>
    /// Validates build choices before downloading or modifying installation directories.
    /// </summary>
    internal static void ValidateOptions(PostgresProvisionOptions options, bool windows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Jobs, 1);
        ArgumentNullException.ThrowIfNull(options.ConfigureFlags);
        if (windows && (options.ConfigureFlags.Count != 0 || options.EnableValgrind))
        {
            throw new ArgumentException("Configure flags and Valgrind instrumentation require a Unix source build.", nameof(options));
        }

        foreach (string flag in options.ConfigureFlags)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(flag);
            string name = flag.Split('=', 2)[0];
            if (!(name.StartsWith("--enable-", StringComparison.Ordinal) || name.StartsWith("--disable-", StringComparison.Ordinal) ||
                name.StartsWith("--with-", StringComparison.Ordinal) || name.StartsWith("--without-", StringComparison.Ordinal)) || flag.Contains('\0'))
            {
                throw new ArgumentException($"Unsupported PostgreSQL configure argument: {flag}", nameof(options));
            }
        }
    }

    /// <summary>
    /// Keeps distinct build options in independent directories while allowing job-count changes to reuse binaries.
    /// </summary>
    internal static string GetBuildIdentity(PostgresProvisionOptions options)
    {
        string inputs = string.Join('\0', ["1", RuntimeInformation.RuntimeIdentifier,
            options.EnableValgrind.ToString(CultureInfo.InvariantCulture), .. options.Release ? ["release"] : Array.Empty<string>(),
            .. options.ConfigureFlags,
            .. s_buildEnvironment.Select(static name => name + "=" + Environment.GetEnvironmentVariable(name))]);
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(inputs)))[..16];
    }

    private static async Task BuildAsync(string source, string destination, string stagingRoot,
        PostgresProvisionOptions options, CancellationToken cancellationToken)
    {
        string flags = GetPreprocessorFlags(Environment.GetEnvironmentVariable("CPPFLAGS"), options);
        string? pkgConfigPath = Environment.GetEnvironmentVariable("PKG_CONFIG_PATH");
        if (OperatingSystem.IsMacOS() && !options.ConfigureFlags.Contains("--without-icu") &&
            Environment.GetEnvironmentVariable("ICU_CFLAGS") is null && Environment.GetEnvironmentVariable("ICU_LIBS") is null &&
            ExecutableLocator.Find("brew") is string brew)
        {
            string prefix = (await RunAsync(brew, ["--prefix", "icu4c"], source, flags, pkgConfigPath, cancellationToken).ConfigureAwait(false)).Trim();
            string path = Path.Join(prefix, "lib", "pkgconfig");
            if (Path.IsPathRooted(prefix) && Directory.Exists(path))
            {
                pkgConfigPath = string.IsNullOrEmpty(pkgConfigPath) ? path : pkgConfigPath + Path.PathSeparator + path;
            }
        }

        string[] configure = GetConfigureArguments(destination, options);

        // The source tree is deleted after a failed build, so the output and config.log are kept beside the installation.
        string log = destination + ".build.log";
        File.Delete(log);
        await RunAsync(Path.Join(source, "configure"), configure, source, flags, pkgConfigPath, cancellationToken, log).ConfigureAwait(false);
        string jobs = options.Jobs.ToString(CultureInfo.InvariantCulture);
        await RunAsync("make", ["-j", jobs, "world-bin"], source, flags, pkgConfigPath, cancellationToken, log).ConfigureAwait(false);
        await RunAsync("make", ["-j", jobs, "install-world-bin", $"DESTDIR={stagingRoot}"], source, flags, pkgConfigPath, cancellationToken, log).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds a Windows source tree with meson, which sets up Visual Studio's compiler environment itself, and installs
    /// it into <paramref name="prefix"/>.
    /// </summary>
    private static async Task BuildWindowsAsync(string source, string prefix, string destination, PostgresProvisionOptions options,
        CancellationToken cancellationToken)
    {
        string meson = ExecutableLocator.Find("meson") ?? throw new InvalidOperationException("meson is not on PATH.");
        string build = Path.Join(source, "build");
        string log = destination + ".build.log";
        File.Delete(log);
        await RunAsync(meson, GetMesonSetupArguments(source, build, prefix, options), source, "", null, cancellationToken, log).ConfigureAwait(false);
        await RunAsync(meson, ["compile", "-C", build, "-j", options.Jobs.ToString(CultureInfo.InvariantCulture)], source, "", null,
            cancellationToken, log).ConfigureAwait(false);
        await RunAsync(meson, ["install", "-C", build, "--quiet"], source, "", null, cancellationToken, log).ConfigureAwait(false);
    }

    /// <summary>
    /// Checks for the programs a Windows source build needs besides Visual Studio's C++ tools, which meson finds itself.
    /// </summary>
    /// <param name="unavailable">Why a source build is needed.</param>
    /// <param name="version">The release to build.</param>
    /// <param name="find">Finds a program on <c>PATH</c>.</param>
    /// <exception cref="InvalidOperationException">A program is missing.</exception>
    internal static void RequireWindowsBuildTools(string unavailable, PostgresVersion version, Func<string, string?> find)
    {
        ArgumentNullException.ThrowIfNull(find);
        string[] missing = [.. new (string Name, string?[] Paths)[]
            {
                ("meson", [find("meson")]),
                ("perl (Strawberry Perl)", [find("perl")]),
                ("win_bison or bison (winflexbison)", [find("win_bison"), find("bison")]),
                ("win_flex or flex (winflexbison)", [find("win_flex"), find("flex")]),
            }.Where(static tool => tool.Paths.All(static path => path is null)).Select(static tool => tool.Name)];
        if (missing.Length != 0)
        {
            throw new InvalidOperationException($"{unavailable} Building it from source needs Visual Studio's C++ tools and these " +
                $"programs on PATH: {string.Join(", ", missing)}. Install them, or register an existing build with " +
                $"'ankus init --pg{version.Major} /path/to/pg_config.exe'.");
        }
    }

    /// <summary>
    /// Builds meson's setup arguments for a Windows source build: debug information, and for development builds the
    /// assertions and allocated-memory randomization of a Unix build.
    /// </summary>
    /// <param name="source">The source tree.</param>
    /// <param name="build">The build directory.</param>
    /// <param name="prefix">The installation directory.</param>
    /// <param name="options">The build options.</param>
    /// <returns>The arguments in order.</returns>
    internal static string[] GetMesonSetupArguments(string source, string build, string prefix, PostgresProvisionOptions options)
        => ["setup", build, source, "--prefix=" + prefix, "--vsenv", "--buildtype=debugoptimized",
            "-Dcassert=" + (options.Release ? "false" : "true"),
            .. options.Release ? Array.Empty<string>() : ["-Dc_args=-DRANDOMIZE_ALLOCATED_MEMORY=1"]];

    /// <summary>
    /// Describes a failed build command with its cause on the first line: configure's <c>configure: error:</c> line, or
    /// the first compiler error. Everything else in the output is detail.
    /// </summary>
    /// <param name="command">The command's file name, such as <c>configure</c>.</param>
    /// <param name="exitCode">The command's exit code.</param>
    /// <param name="output">The end of the command's standard output.</param>
    /// <param name="error">The end of the command's standard error.</param>
    /// <param name="log">The file that keeps the output and <c>config.log</c>, or null.</param>
    /// <returns>The failure message.</returns>
    internal static string DescribeFailure(string command, int exitCode, string output, string error, string? log)
    {
        const string Configure = "configure: error: ";
        string detail = (string.IsNullOrWhiteSpace(error) ? output : error).Trim();
        string? reason = detail.ReplaceLineEndings("\n").Split('\n').Select(static line => line.Trim())
            .FirstOrDefault(static line => line.StartsWith(Configure, StringComparison.Ordinal) || line.Contains("error:", StringComparison.Ordinal));
        if (reason?.StartsWith(Configure, StringComparison.Ordinal) == true)
        {
            reason = reason[Configure.Length..];
        }

        var message = new StringBuilder(string.Create(CultureInfo.InvariantCulture, $"PostgreSQL's {command} failed ({exitCode})"));
        message.Append(reason is null ? "." : ": " + reason).Append('\n').Append(detail);
        if (log is not null)
        {
            message.Append(CultureInfo.InvariantCulture, $"\nThe output and config.log are in {log}.");
        }

        return message.ToString();
    }

    /// <summary>
    /// Finds pkg-config in Homebrew's and MacPorts' directories. A non-interactive SSH session on macOS has only the system
    /// directories on <c>PATH</c>, where neither installs it, so configure could not find a pkg-config that is installed.
    /// </summary>
    /// <param name="directories">The directories to search, in order.</param>
    /// <returns>The pkg-config path, or null.</returns>
    internal static string? FindPkgConfig(IEnumerable<string> directories)
        => directories.Select(static directory => Path.Join(directory, "pkg-config")).FirstOrDefault(File.Exists);

    /// <summary>
    /// Builds the preprocessor flags: development builds check assertions and randomize allocated memory as
    /// <c>cargo pgrx init</c>'s do, and release builds leave both out.
    /// </summary>
    /// <param name="inherited">The caller's <c>CPPFLAGS</c>, if any.</param>
    /// <param name="options">The build options.</param>
    /// <returns>The flags passed to configure and make.</returns>
    internal static string GetPreprocessorFlags(string? inherited, PostgresProvisionOptions options)
        => (inherited ?? "") + (options.Release ? "" : " -DUSE_ASSERT_CHECKING=1 -DRANDOMIZE_ALLOCATED_MEMORY=1") +
            (options.EnableValgrind ? " -DUSE_VALGRIND=1" : "");

    /// <summary>
    /// Builds configure's arguments. Every build keeps debug symbols; development builds also enable assertions.
    /// </summary>
    /// <param name="destination">The installation directory.</param>
    /// <param name="options">The build options.</param>
    /// <returns>The arguments in order.</returns>
    internal static string[] GetConfigureArguments(string destination, PostgresProvisionOptions options)
        => [$"--prefix={destination}", "--enable-debug", .. options.Release ? Array.Empty<string>() : ["--enable-cassert"], .. options.ConfigureFlags];

    private static async Task<string> RunAsync(string executable, string[] arguments, string directory,
        string preprocessorFlags, string? pkgConfigPath, CancellationToken cancellationToken, string? log = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        foreach (string variable in new[] { "MAKEFLAGS", "MAKELEVEL", "MFLAGS", "DESTDIR", "LIBRARY_PATH",
            "DYLD_FALLBACK_LIBRARY_PATH", "DEBUG", "OPT_LEVEL", "TARGET", "PROFILE", "OUT_DIR", "HOST", "NUM_JOBS" })
        {
            start.Environment.Remove(variable);
        }

        start.Environment["CPPFLAGS"] = preprocessorFlags;
        start.Environment["PKG_CONFIG_PATH"] = pkgConfigPath;
        if (!OperatingSystem.IsWindows() && Environment.GetEnvironmentVariable("PKG_CONFIG") is null && ExecutableLocator.Find("pkg-config") is null &&
            FindPkgConfig(["/opt/homebrew/bin", "/usr/local/bin", "/opt/local/bin"]) is string pkgConfig)
        {
            start.Environment["PKG_CONFIG"] = pkgConfig;
        }

        using var process = new Process { StartInfo = start };
        process.Start();
        process.StandardInput.Close();
        Task<string> output = ReadTailAsync(process.StandardOutput);
        Task<string> error = ReadTailAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The build finished while cancellation was being delivered.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw;
        }

        string stdout = await output.ConfigureAwait(false);
        string stderr = await error.ConfigureAwait(false);
        if (process.ExitCode != 0)
        {
            string command = Path.GetFileName(executable);
            if (log is not null)
            {
                string configLog = Path.Join(directory, "config.log");
                await File.WriteAllTextAsync(log, $"{command} {string.Join(' ', arguments)}\n\n--- standard output\n{stdout}\n--- standard error\n{stderr}\n" +
                    (File.Exists(configLog) ? "--- config.log\n" + await File.ReadAllTextAsync(configLog, CancellationToken.None).ConfigureAwait(false) : ""),
                    CancellationToken.None).ConfigureAwait(false);
            }

            throw new InvalidOperationException(DescribeFailure(command, process.ExitCode, stdout, stderr, log));
        }

        return stdout;
    }

    private static async Task<string> ReadTailAsync(StreamReader reader)
    {
        const int Limit = 16384;
        var tail = new StringBuilder();
        char[] buffer = new char[4096];
        int count;
        while ((count = await reader.ReadAsync(buffer, CancellationToken.None).ConfigureAwait(false)) != 0)
        {
            tail.Append(buffer, 0, count);
            if (tail.Length > Limit)
            {
                tail.Remove(0, tail.Length - Limit);
            }
        }

        return tail.ToString();
    }

    private static async Task<PostgresInstallation> ValidateInstallationAsync(string directory, PostgresVersion version,
        CancellationToken cancellationToken)
    {
        string executable = OperatingSystem.IsWindows() ? "pg_config.exe" : "pg_config";
        PostgresInstallation installation = await PostgresInstallation.CreateAsync(Path.Join(directory, "bin", executable),
            cancellationToken).ConfigureAwait(false);
        if (installation.Version != version)
        {
            throw new InvalidDataException($"Downloaded PostgreSQL reports {installation.Version}, expected {version}.");
        }

        foreach (string path in new[] { installation.LibraryDirectory, installation.SharedDirectory, installation.IncludeDirectory })
        {
            RequireOwnedPath(directory, path);
            if (!Directory.Exists(path))
            {
                throw new InvalidDataException($"The downloaded PostgreSQL installation is missing a directory: {path}");
            }
        }

        foreach (string path in new[] { installation.InitDbPath, installation.PgCtlPath, installation.PsqlPath,
            Path.Join(installation.BinDirectory, OperatingSystem.IsWindows() ? "postgres.exe" : "postgres"),
            Path.Join(installation.ServerIncludeDirectory, "postgres.h") })
        {
            RequireOwnedPath(directory, path);
            if (!File.Exists(path))
            {
                throw new InvalidDataException($"The downloaded PostgreSQL installation is missing a local development file: {path}");
            }
        }

        return installation;
    }

    /// <summary>
    /// Requires a path reported by an installation's pg_config to lie within the installation directory.
    /// </summary>
    /// <param name="directory">The installation directory.</param>
    /// <param name="path">The reported path.</param>
    /// <remarks>
    /// pg_config derives its paths from its own location with symbolic links resolved, so a directory reached through
    /// a link, such as macOS's <c>/tmp</c>, reports paths under the link's target. Both sides are compared resolved.
    /// </remarks>
    /// <exception cref="InvalidDataException">The path lies outside the directory.</exception>
    internal static void RequireOwnedPath(string directory, string path)
    {
        string relative = Path.GetRelativePath(ResolveLinks(directory), ResolveLinks(path));
        if (Path.IsPathRooted(relative) || relative == ".." || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        {
            throw new InvalidDataException($"The downloaded PostgreSQL installation refers outside its directory: {path}");
        }
    }

    /// <summary>
    /// Resolves the symbolic links among a path's existing components, as realpath does.
    /// </summary>
    /// <param name="path">The path to resolve.</param>
    /// <returns>The absolute path with every link among its existing components replaced by its target.</returns>
    internal static string ResolveLinks(string path)
    {
        string pending = Path.GetFullPath(path);
        // Follow at most as many links as the kernel would for one path, so a cycle ends the search.
        for (int followed = 0; followed < 40; followed++)
        {
            string current = Path.GetPathRoot(pending)!;
            string[] components = pending[current.Length..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
            int index = 0;
            while (index < components.Length && new DirectoryInfo(Path.Join(current, components[index])).LinkTarget is null)
            {
                current = Path.Join(current, components[index]);
                index++;
            }

            if (index == components.Length)
            {
                return pending;
            }

            string target = new DirectoryInfo(Path.Join(current, components[index])).LinkTarget!;
            pending = Path.Join([Path.GetFullPath(target, current), .. components[(index + 1)..]]);
        }

        return pending;
    }
}
