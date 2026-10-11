using System.Diagnostics;

namespace Ankus.PgConfig;

/// <summary>
/// Describes one PostgreSQL installation by querying its authoritative
/// <c>pg_config</c> executable.
/// </summary>
public sealed class PostgresInstallation
{
    private PostgresInstallation(
        string pgConfigPath,
        string versionText,
        string binDirectory,
        string libraryDirectory,
        string sharedDirectory,
        string includeDirectory,
        string serverIncludeDirectory,
        string preprocessorFlags)
    {
        PgConfigPath = pgConfigPath;
        VersionText = versionText;
        Version = PostgresVersion.Parse(versionText);
        BinDirectory = binDirectory;
        LibraryDirectory = libraryDirectory;
        SharedDirectory = sharedDirectory;
        IncludeDirectory = includeDirectory;
        ServerIncludeDirectory = serverIncludeDirectory;
        PreprocessorFlags = preprocessorFlags;
        PreprocessorArguments = UnixCompilerArguments.Split(preprocessorFlags);
    }

    /// <summary>
    /// Describes an installation on another machine from its exported information. Header directories and search
    /// flags name the captured copies; the remaining directories keep the target's values, which packages mirror.
    /// </summary>
    private PostgresInstallation(PostgresTargetInfo target)
        : this(target.ManifestPath, target.VersionText, target.BinDirectory, target.LibraryDirectory, target.SharedDirectory,
            target.MapPath(target.IncludeDirectory), target.MapPath(target.ServerIncludeDirectory), target.PreprocessorFlags)
    {
        TargetInfo = target;
        PreprocessorArguments = target.MapIncludeArguments(PreprocessorArguments);
    }

    /// <summary>
    /// Gets the exported information this installation was read from, or null for an installation on this machine.
    /// </summary>
    /// <remarks>
    /// Such an installation can supply headers and build values to compile and package an extension, but has no
    /// executables or server on this machine.
    /// </remarks>
    public PostgresTargetInfo? TargetInfo { get; }

    /// <summary>
    /// Gets the <c>pg_config --version</c> output.
    /// </summary>
    internal string VersionText { get; }

    /// <summary>
    /// Gets the absolute path to <c>pg_config</c>.
    /// </summary>
    public string PgConfigPath { get; }

    /// <summary>
    /// Gets the PostgreSQL version reported by this installation.
    /// </summary>
    public PostgresVersion Version { get; }

    /// <summary>
    /// Gets the installation directory containing PostgreSQL executables.
    /// </summary>
    public string BinDirectory { get; }

    /// <summary>
    /// Gets PostgreSQL's native extension library directory.
    /// </summary>
    public string LibraryDirectory { get; }

    /// <summary>
    /// Gets PostgreSQL's architecture-independent shared-data directory.
    /// </summary>
    public string SharedDirectory { get; }

    /// <summary>
    /// Gets the public header directory, including dependencies of the server headers.
    /// </summary>
    public string IncludeDirectory { get; }

    /// <summary>
    /// Gets the server header directory for compiling native extension boundaries against this installation.
    /// </summary>
    public string ServerIncludeDirectory { get; }

    /// <summary>
    /// Gets the preprocessor arguments reported by this installation's build configuration.
    /// </summary>
    public string PreprocessorFlags { get; }

    /// <summary>
    /// Gets the preprocessor arguments reported by this installation, split without shell expansion.
    /// </summary>
    public IReadOnlyList<string> PreprocessorArguments { get; }

    /// <summary>
    /// Resolves preprocessor arguments for native compilation on the current platform.
    /// </summary>
    /// <param name="cancellationToken">Cancels native SDK discovery.</param>
    /// <returns>PostgreSQL's arguments with the active macOS SDK selected when compiling on macOS.</returns>
    /// <remarks>
    /// On macOS, SDKROOT selects an absolute installed SDK path; otherwise the active Apple developer tools select it.
    /// Historical SDK roots from PostgreSQL's build machine are replaced. Other arguments and the recorded properties remain unchanged.
    /// </remarks>
    /// <exception cref="DirectoryNotFoundException">An explicit macOS SDK path does not identify an installed directory.</exception>
    public Task<IReadOnlyList<string>> GetPreprocessorArgumentsAsync(CancellationToken cancellationToken = default)
        => NativeCompilerArguments.CreateAsync(PreprocessorArguments, cancellationToken);

    /// <summary>
    /// Gets the Ankus version selector for this installation, such as <c>pg18</c>.
    /// </summary>
    public string Label => Version.Label;

    /// <summary>
    /// Gets the platform-correct path to PostgreSQL's <c>initdb</c> executable.
    /// </summary>
    public string InitDbPath => GetExecutablePath("initdb");

    /// <summary>
    /// Gets the platform-correct path to PostgreSQL's <c>pg_ctl</c> executable.
    /// </summary>
    public string PgCtlPath => GetExecutablePath("pg_ctl");

    /// <summary>
    /// Gets the platform-correct path to PostgreSQL's <c>createdb</c> executable.
    /// </summary>
    public string CreateDbPath => GetExecutablePath("createdb");

    /// <summary>
    /// Gets the platform-correct path to PostgreSQL's <c>dropdb</c> executable.
    /// </summary>
    public string DropDbPath => GetExecutablePath("dropdb");

    /// <summary>
    /// Gets the platform-correct path to PostgreSQL's <c>psql</c> executable.
    /// </summary>
    public string PsqlPath => GetExecutablePath("psql");

    /// <summary>
    /// Locates this installation's PostgreSQL regression driver in its PGXS tree or Windows executable directory.
    /// Ordinary installation discovery does not require the regression tools to be installed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the PGXS query.</param>
    /// <returns>The absolute path to the installed pg_regress executable.</returns>
    /// <remarks>
    /// On Windows, include this installation's BinDirectory in the child process PATH so the driver can load its PostgreSQL DLLs.
    /// </remarks>
    /// <exception cref="FileNotFoundException">The selected installation does not contain its regression driver.</exception>
    public async Task<string> GetRegressionDriverPathAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfTarget();
        string pgxs = Path.GetFullPath(await QueryAsync(PgConfigPath, "--pgxs", cancellationToken).ConfigureAwait(false));
        string? sourceDirectory = Path.GetDirectoryName(Path.GetDirectoryName(pgxs));
        if (sourceDirectory is null)
        {
            throw new FormatException("PostgreSQL's PGXS path does not identify an installed development tree.");
        }

        string driver = Path.Combine(sourceDirectory, "test", "regress", OperatingSystem.IsWindows() ? "pg_regress.exe" : "pg_regress");
        if (!File.Exists(driver) && OperatingSystem.IsWindows())
        {
            // PostgreSQL's MSVC installer places executable projects in bin; Meson uses the PGXS tree.
            string executable = Path.Combine(BinDirectory, "pg_regress.exe");
            if (File.Exists(executable))
            {
                return executable;
            }
        }

        if (!File.Exists(driver))
        {
            throw new FileNotFoundException("The selected PostgreSQL installation is missing pg_regress. Install its development and regression tools.", driver);
        }

        return driver;
    }

    /// <summary>
    /// Locates PostgreSQL's Valgrind suppressions for this installation, which Valgrind startup passes to Memcheck as
    /// pgrx does when it keeps the PostgreSQL source tree.
    /// </summary>
    /// <param name="cancellationToken">Cancels the PGXS query.</param>
    /// <returns>
    /// The <c>src/tools/valgrind.supp</c> file that <c>ankus init</c> installs in the PGXS tree, else the one in the
    /// source tree that PGXS records as <c>abs_top_srcdir</c> when it still exists, else null.
    /// </returns>
    public async Task<string?> GetValgrindSuppressionsPathAsync(CancellationToken cancellationToken = default)
    {
        ThrowIfTarget();
        string pgxs = Path.GetFullPath(await QueryAsync(PgConfigPath, "--pgxs", cancellationToken).ConfigureAwait(false));
        string? sourceDirectory = Path.GetDirectoryName(Path.GetDirectoryName(pgxs));
        if (sourceDirectory is null)
        {
            return null;
        }

        string installed = Path.Combine(sourceDirectory, "tools", "valgrind.supp");
        if (File.Exists(installed))
        {
            return installed;
        }

        string global = Path.Combine(sourceDirectory, "Makefile.global");
        if (!File.Exists(global))
        {
            return null;
        }

        foreach (string line in await File.ReadAllLinesAsync(global, cancellationToken).ConfigureAwait(false))
        {
            string[] assignment = line.Split('=', 2, StringSplitOptions.TrimEntries);
            if (assignment.Length == 2 && assignment[0] == "abs_top_srcdir" && assignment[1].Length != 0 && Path.IsPathRooted(assignment[1]))
            {
                string recorded = Path.Combine(assignment[1], "src", "tools", "valgrind.supp");
                return File.Exists(recorded) ? recorded : null;
            }
        }

        return null;
    }

    /// <summary>
    /// Discovers PostgreSQL 18 from persisted configuration, managed installations, PATH, and platform installation locations.
    /// </summary>
    /// <param name="cancellationToken">Cancels <c>pg_config</c> queries.</param>
    /// <returns>The discovered PostgreSQL installation.</returns>
    public static Task<PostgresInstallation> DiscoverAsync(CancellationToken cancellationToken = default)
        => DiscoverAsync(18, cancellationToken);

    /// <summary>
    /// Discovers a specific PostgreSQL major without requiring environment variables.
    /// Nonstandard installations can be registered as pgXX paths in the Ankus home's <c>config.json</c>.
    /// ANKUS_HOME selects that home, defaulting to <c>~/.ankus</c>.
    /// </summary>
    /// <param name="major">The required PostgreSQL major version.</param>
    /// <param name="cancellationToken">Cancels installation queries.</param>
    /// <returns>The first matching installation in discovery order.</returns>
    public static Task<PostgresInstallation> DiscoverAsync(int major, CancellationToken cancellationToken = default)
        => DiscoverAsync(major, homeDirectory: null, cancellationToken);

    /// <summary>
    /// Discovers a specific PostgreSQL major using an explicit Ankus home for registrations and managed installations.
    /// </summary>
    /// <param name="major">The required PostgreSQL major version.</param>
    /// <param name="homeDirectory">The selected home, or null to use ANKUS_HOME and the ordinary default.</param>
    /// <param name="cancellationToken">Cancels installation queries.</param>
    /// <returns>The registered installation or the first matching discovery candidate.</returns>
    public static async Task<PostgresInstallation> DiscoverAsync(int major, string? homeDirectory, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(major, 13);
        var registry = new PostgresRegistry(homeDirectory);
        if (registry.GetPath(major) is not null)
        {
            return await registry.GetAsync(major, cancellationToken).ConfigureAwait(false);
        }

        foreach (string candidate in PostgresDiscovery.GetCandidates(major, registry.HomeDirectory).Distinct(StringComparer.Ordinal))
        {
            if (File.Exists(candidate))
            {
                PostgresInstallation installation = await CreateAsync(candidate, cancellationToken).ConfigureAwait(false);
                if (installation.Version.Major == major)
                {
                    return installation;
                }
            }
        }

        throw new FileNotFoundException(
            $"PostgreSQL {major} was not found. Run 'ankus init --pg{major} /path/to/pg_config'.");
    }

    /// <summary>
    /// Creates an installation descriptor by querying an explicit
    /// <c>pg_config</c> executable, or by reading exported target information.
    /// </summary>
    /// <param name="pgConfigPath">Path to <c>pg_config</c>, or to an extracted <see cref="PostgresTargetInfo.ManifestFileName"/>.</param>
    /// <param name="cancellationToken">Cancels <c>pg_config</c> queries.</param>
    /// <returns>The queried PostgreSQL installation.</returns>
    public static async Task<PostgresInstallation> CreateAsync(
        string pgConfigPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pgConfigPath);
        if (string.Equals(Path.GetFileName(pgConfigPath), PostgresTargetInfo.ManifestFileName, StringComparison.Ordinal))
        {
            return new PostgresInstallation(PostgresTargetInfo.Open(pgConfigPath));
        }

        string? resolvedPath = ExecutableLocator.Find(pgConfigPath);
        if (resolvedPath is null)
        {
            throw new FileNotFoundException($"The specified pg_config executable was not found. Path: '{pgConfigPath}'.", pgConfigPath);
        }

        Task<string> version = QueryAsync(resolvedPath, "--version", cancellationToken);
        Task<string> binDirectory = QueryAsync(resolvedPath, "--bindir", cancellationToken);
        Task<string> libraryDirectory = QueryAsync(resolvedPath, "--pkglibdir", cancellationToken);
        Task<string> sharedDirectory = QueryAsync(resolvedPath, "--sharedir", cancellationToken);
        Task<string> includeDirectory = QueryAsync(resolvedPath, "--includedir", cancellationToken);
        Task<string> serverIncludeDirectory = QueryAsync(resolvedPath, "--includedir-server", cancellationToken);
        Task<string> preprocessorFlags = QueryAsync(resolvedPath, "--cppflags", cancellationToken);
        await Task.WhenAll(version, binDirectory, libraryDirectory, sharedDirectory, includeDirectory, serverIncludeDirectory, preprocessorFlags).ConfigureAwait(false);

        return new PostgresInstallation(
            resolvedPath,
            (await version.ConfigureAwait(false)).Trim(),
            Path.GetFullPath(await binDirectory.ConfigureAwait(false)),
            Path.GetFullPath(await libraryDirectory.ConfigureAwait(false)),
            Path.GetFullPath(await sharedDirectory.ConfigureAwait(false)),
            Path.GetFullPath(await includeDirectory.ConfigureAwait(false)),
            Path.GetFullPath(await serverIncludeDirectory.ConfigureAwait(false)),
            await preprocessorFlags.ConfigureAwait(false));
    }

    private string GetExecutablePath(string name)
    {
        ThrowIfTarget();
        string executableName = OperatingSystem.IsWindows() ? $"{name}.exe" : name;
        return Path.Combine(BinDirectory, executableName);
    }

    private void ThrowIfTarget()
    {
        if (TargetInfo is not null)
        {
            throw new InvalidOperationException(
                $"PostgreSQL {Version} described by '{PgConfigPath}' is installed on another machine; run its tools there.");
        }
    }

    private static Task<string> QueryAsync(string pgConfigPath, string argument, CancellationToken cancellationToken)
        => QueryAsync(pgConfigPath, [argument], cancellationToken);

    /// <summary>
    /// Queries an installation or SDK tool without invoking a shell and joins the process before cancellation returns.
    /// </summary>
    internal static async Task<string> QueryAsync(string pgConfigPath, IReadOnlyList<string> arguments, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = pgConfigPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        Task<string> standardOutput = Task.FromResult(string.Empty);
        Task<string> standardError = Task.FromResult(string.Empty);
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{pgConfigPath}'.");
        }

        try
        {
            standardOutput = ReadQueryOutputAsync(process.StandardOutput);
            standardError = ReadQueryOutputAsync(process.StandardError);
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The query exited while cancellation was being delivered.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll((Task)standardOutput, standardError).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            throw;
        }

        string rawOutput = await standardOutput.ConfigureAwait(false);
        bool preprocessorFlags = arguments is ["--cppflags"];
        string output = preprocessorFlags ? rawOutput.TrimEnd('\r', '\n') : rawOutput.Trim();
        string error = (await standardError.ConfigureAwait(false)).Trim();

        if (process.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"'{pgConfigPath} {string.Join(' ', arguments)}' exited with code {process.ExitCode}: {error}{Environment.NewLine}{output}");
        }

        if (output.Length == 0 && !preprocessorFlags)
        {
            throw new InvalidOperationException($"'{pgConfigPath} {string.Join(' ', arguments)}' returned no output.");
        }

        return output;
    }

    /// <summary>
    /// Drains Windows process pipes on dedicated readers until the query exits or is killed and reaped.
    /// </summary>
    private static Task<string> ReadQueryOutputAsync(StreamReader reader)
        => OperatingSystem.IsWindows()
            ? Task.Factory.StartNew(reader.ReadToEnd, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            : reader.ReadToEndAsync(CancellationToken.None);
}
