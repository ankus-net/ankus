using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Ankus.PgConfig;

/// <summary>
/// Manages one persistent, local-only development cluster per PostgreSQL major in an Ankus home.
/// Stopping a server preserves its databases. Existing unowned directories are never initialized or modified.
/// </summary>
public sealed partial class PostgresDevelopmentCluster
{
    private readonly PostgresInstallation _installation;
    private readonly PostgresRegistry _registry;
    private readonly string _root;

    /// <summary>
    /// Collects this server's diagnostics when this instance starts or attaches to its retained identity.
    /// </summary>
    private PostgresServerLog? _log;

    /// <summary>
    /// Describes a development cluster without creating directories or starting processes.
    /// </summary>
    /// <param name="installation">The PostgreSQL installation that runs the cluster.</param>
    /// <param name="homeDirectory">The Ankus home, defaulting to ANKUS_HOME or ~/.ankus.</param>
    public PostgresDevelopmentCluster(PostgresInstallation installation, string? homeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(installation);
        ArgumentOutOfRangeException.ThrowIfLessThan(installation.Version.Major, 13);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(installation.Version.Major, 19);
        _installation = installation;
        _registry = new PostgresRegistry(homeDirectory);
        _root = Path.Join(_registry.HomeDirectory, "clusters");
        DataDirectory = Path.Join(_root, installation.Label);
        LogFilePath = Path.Join(_root, installation.Label + ".log");
    }

    /// <summary>
    /// Gets the persistent data directory for this PostgreSQL major.
    /// </summary>
    public string DataDirectory { get; }

    /// <summary>
    /// Gets the retained server log path outside the data directory.
    /// On Windows, ReadServerLog refreshes the snapshot with stderr and this server's Application events.
    /// </summary>
    public string LogFilePath { get; }

    /// <summary>
    /// Reports whether the managed cluster is running. A missing cluster is stopped and is not created.
    /// </summary>
    /// <param name="cancellationToken">Cancels the native status query.</param>
    /// <returns>True when pg_ctl reports a running server.</returns>
    public async Task<bool> IsRunningAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.Exists(DataDirectory))
        {
            return false;
        }

        RequireOwnedCluster();
        return await QueryRunningAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Initializes a missing cluster and starts it, preserving existing databases and an already running server.
    /// Failed or canceled startup stops any server launched by this operation while retaining initialized data.
    /// </summary>
    /// <param name="options">Port, timeout, instrumentation, and literal PostgreSQL configuration settings.</param>
    /// <param name="cancellationToken">Cancels initialization or startup.</param>
    /// <returns>True when a server was started, or false when it was already running.</returns>
    public async Task<bool> StartAsync(PostgresDevelopmentOptions? options = null, CancellationToken cancellationToken = default)
    {
        string configuration = CreateConfiguration(_installation.Version.Major, options ?? new PostgresDevelopmentOptions(),
            options?.Port ?? _registry.GetPort(_installation.Version.Major));
        int timeout = options?.TimeoutSeconds ?? 60;
        cancellationToken.ThrowIfCancellationRequested();
        bool useValgrind = options?.UseValgrind == true;
        if (useValgrind && OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Valgrind requires a supported Unix platform; native Windows PostgreSQL cannot run under Valgrind.");
        }

        Directory.CreateDirectory(_root);
        using FileStream operationLock = AcquireLock();
        if (Path.Exists(DataDirectory))
        {
            RequireOwnedCluster();
            if (await QueryRunningAsync(cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        string? suppressions = null;
        if (useValgrind)
        {
            try
            {
                await RunCheckedAsync("valgrind", ["--version"], cancellationToken).ConfigureAwait(false);
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                throw new InvalidOperationException("Install Valgrind and make its executable available on PATH before using Valgrind startup.", error);
            }

            suppressions = await _installation.GetValgrindSuppressionsPathAsync(cancellationToken).ConfigureAwait(false);
        }

        if (!Path.Exists(DataDirectory))
        {
            await InitializeAsync(cancellationToken).ConfigureAwait(false);
        }

        await ReplaceFileAsync(Path.Join(DataDirectory, "ankus.conf"), configuration, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        _log = PrepareLog();

        // Tools that follow the server's output, such as log tailers, read the file it writes from here rather than
        // reproducing Ankus's naming, which differs on Windows. The .ankus-cluster marker stays the label alone,
        // which every Ankus version checks before it uses a cluster.
        await ReplaceFileAsync(Path.Join(DataDirectory, ".ankus-log"), _log.NativeFilePath + "\n", cancellationToken).ConfigureAwait(false);
        try
        {
            string[] arguments = ["start", "-l", _log.NativeFilePath, "-w", "-t",
                (useValgrind ? timeout + 60 : timeout).ToString(CultureInfo.InvariantCulture)];
            Dictionary<string, string?>? environment = null;
            if (useValgrind)
            {
                // pg_ctl inserts -D before -o arguments. Supply PGDATA instead so Valgrind
                // receives its own options before the shell-quoted PostgreSQL executable.
                arguments = [.. arguments, "-p", "valgrind", "-o", ValgrindArguments(_installation, suppressions)];
                environment = new Dictionary<string, string?> { ["PGDATA"] = DataDirectory };
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_GCRegionRange")))
                {
                    // Memcheck shares a bounded virtual address space with its client. The
                    // runtime's host-sized reservation can exceed it before any SQL executes.
                    // Use a 32 GiB region range only for instrumentation; honor explicit settings.
                    environment["DOTNET_GCRegionRange"] = "800000000";
                }
            }
            else
            {
                arguments = [.. arguments, "-D", DataDirectory];
                if (OperatingSystem.IsWindows())
                {
                    arguments = [.. arguments, "-o", "-c event_source=" + _log.EventSource];
                }
            }

            if (useValgrind)
            {
                // Keep pg_ctl alive beyond our deadline so cancellation can terminate its
                // instrumented child even before PostgreSQL has written postmaster.pid.
                using var startupTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                startupTimeout.CancelAfter(TimeSpan.FromSeconds(timeout));
                try
                {
                    await RunCheckedAsync(_installation.PgCtlPath, arguments, startupTimeout.Token, environment: environment).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException($"PostgreSQL did not start within {timeout.ToString(CultureInfo.InvariantCulture)} seconds.");
                }
            }
            else
            {
                await RunCheckedAsync(_installation.PgCtlPath, arguments, cancellationToken,
                    allowDescendants: OperatingSystem.IsWindows()).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _ = ReadServerLog(cancellationToken);
            return true;
        }
        catch (Exception startupError)
        {
            Exception? cleanupFailure = null;
            try
            {
                if (await QueryRunningAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    try
                    {
                        await StopRunningAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (InvalidOperationException)
                    {
                        // Killing a canceled process tree may still be finishing when status
                        // observes its server. pg_ctl reports an unclean exit as a stop failure.
                        if (await QueryRunningAsync(CancellationToken.None).ConfigureAwait(false))
                        {
                            throw;
                        }
                    }
                }
            }
            // codeql[cs/catch-of-all-exceptions] A startup failure is reported together with any cleanup or log failure that followed it.
            catch (Exception cleanupError)
            {
                cleanupFailure = cleanupError;
            }

            string diagnostics = string.Empty;
            Exception? diagnosticFailure = null;
            try
            {
                diagnostics = ReadServerLog(CancellationToken.None);
            }
            // codeql[cs/catch-of-all-exceptions] A startup failure is reported together with any cleanup or log failure that followed it.
            catch (Exception logError)
            {
                diagnosticFailure = logError;
            }

            if (cleanupFailure is not null || diagnosticFailure is not null)
            {
                var failures = new List<Exception> { startupError };
                if (cleanupFailure is not null)
                {
                    failures.Add(cleanupFailure);
                }

                if (diagnosticFailure is not null)
                {
                    failures.Add(diagnosticFailure);
                }

                throw new AggregateException($"PostgreSQL startup, cleanup or diagnostic collection failed. Server log: {LogFilePath}\n{diagnostics}", failures);
            }

            if (startupError is OperationCanceledException)
            {
                throw;
            }

            throw new InvalidOperationException($"PostgreSQL startup failed. Server log: {LogFilePath}\n{startupError.Message}\n{diagnostics}", startupError);
        }
    }

    /// <summary>
    /// Stops a running managed server with fast shutdown, preserving its data. A missing or stopped cluster is unchanged.
    /// </summary>
    /// <param name="cancellationToken">Cancels the shutdown command.</param>
    /// <returns>True when a running server was stopped.</returns>
    public async Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!Path.Exists(DataDirectory))
        {
            return false;
        }

        RequireOwnedCluster();
        using FileStream operationLock = AcquireLock();
        if (!await QueryRunningAsync(cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        await StopRunningAsync(cancellationToken).ConfigureAwait(false);
        _ = ReadServerLog(CancellationToken.None);
        return true;
    }

    /// <summary>
    /// Refreshes and reads this cluster's retained diagnostics without starting or stopping a server.
    /// </summary>
    /// <param name="cancellationToken">Cancels diagnostic collection.</param>
    /// <returns>Native stderr and this server's retained Windows event messages, or an empty missing log.</returns>
    public string ReadServerLog(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_log is null && OperatingSystem.IsWindows() && Path.Exists(DataDirectory))
        {
            RequireOwnedCluster();
            string identityPath = Path.Join(DataDirectory, ".ankus-log-identity");
            if (File.Exists(identityPath))
            {
                _log = new(LogFilePath, ReadLogIdentity(identityPath));
            }
        }

        return _log?.Read(cancellationToken) ?? PostgresServerLog.ReadFile(LogFilePath);
    }

    /// <summary>
    /// Reuses a cluster-owned provider identity across CLI invocations and server restarts.
    /// </summary>
    /// <returns>The native and retained diagnostic collector.</returns>
    private PostgresServerLog PrepareLog()
    {
        Guid identity = Guid.NewGuid();
        if (OperatingSystem.IsWindows())
        {
            string path = Path.Join(DataDirectory, ".ankus-log-identity");
            if (File.Exists(path))
            {
                identity = ReadLogIdentity(path);
            }
            else
            {
                string staging = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    File.WriteAllText(staging, identity.ToString("N"));
                    File.Move(staging, path);
                }
                finally
                {
                    File.Delete(staging);
                }
            }
        }

        return new(LogFilePath, identity);
    }

    /// <summary>
    /// Rejects a damaged owned identity instead of attaching to another server's event provider.
    /// </summary>
    /// <param name="path">The owned identity marker.</param>
    /// <returns>The validated server identity.</returns>
    private static Guid ReadLogIdentity(string path)
    {
        if (!Guid.TryParseExact(File.ReadAllText(path), "N", out Guid identity) || identity == Guid.Empty)
        {
            throw new InvalidDataException("The development cluster's diagnostic identity is invalid.");
        }

        return identity;
    }

    /// <summary>
    /// Formats Memcheck's options and the shell-quoted server executable for <c>pg_ctl -o</c>, with pgrx's options and
    /// PostgreSQL's suppressions when the installation provides them.
    /// </summary>
    /// <param name="installation">The installation whose server runs under Memcheck.</param>
    /// <param name="suppressions">PostgreSQL's suppressions file, or null.</param>
    /// <returns>The options string.</returns>
    private static string ValgrindArguments(PostgresInstallation installation, string? suppressions)
        => "--tool=memcheck --leak-check=no --gen-suppressions=all --time-stamp=yes " +
            "--error-markers=VALGRINDERROR-BEGIN,VALGRINDERROR-END --trace-children=yes " +
            (suppressions is null ? string.Empty : ShellQuote("--suppressions=" + suppressions) + " ") +
            ShellQuote(Path.Join(installation.BinDirectory, "postgres"));

    /// <summary>
    /// Quotes a word for the POSIX shell that <c>pg_ctl</c> starts the server through.
    /// </summary>
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Validates and formats literal settings before any filesystem changes or subprocesses.
    /// </summary>
    internal static string CreateConfiguration(int major, PostgresDevelopmentOptions options, int? defaultPort = null)
    {
        int port = options.Port ?? defaultPort ?? 28800 + major;
        ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.TimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.TimeoutSeconds, 600);
        ArgumentNullException.ThrowIfNull(options.Settings);
        var configuration = new StringBuilder();
        foreach ((string name, string value) in options.Settings)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(name);
            ArgumentNullException.ThrowIfNull(value);
            if (!(char.IsAsciiLetter(name[0]) || name[0] == '_') ||
                name.Any(static character => !(char.IsAsciiLetterOrDigit(character) || character is '_' or '.')) ||
                value.IndexOfAny(['\0', '\r', '\n']) >= 0)
            {
                throw new ArgumentException($"Invalid PostgreSQL setting: {name}", nameof(options));
            }

            if (name.ToLowerInvariant() is "data_directory" or "config_file" or "hba_file" or "ident_file" or
                "external_pid_file" or "port" or "listen_addresses" or "unix_socket_directories" or
                "log_destination" or "logging_collector" or "event_source" or "include" or "include_dir" or "include_if_exists")
            {
                throw new ArgumentException($"Ankus manages the PostgreSQL setting '{name}'. Use the port option to select a TCP port.", nameof(options));
            }

            configuration.Append(name).Append(" = '").Append(value.Replace("\\", "\\\\", StringComparison.Ordinal)
                .Replace("'", "''", StringComparison.Ordinal)).AppendLine("'");
        }

        configuration.AppendLine(CultureInfo.InvariantCulture, $"port = {port}");
        configuration.AppendLine("listen_addresses = '127.0.0.1'");
        configuration.AppendLine("unix_socket_directories = ''");
        configuration.AppendLine("log_destination = 'stderr'");
        configuration.AppendLine("logging_collector = off");
        return configuration.ToString();
    }

    private static async Task ReplaceFileAsync(string path, string content, CancellationToken cancellationToken)
    {
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, content, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            File.Delete(temporary);
        }
    }

    private FileStream AcquireLock()
        => new(Path.Join(_root, _installation.Label + ".lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);

    private void RequireOwnedCluster()
    {
        string marker = Path.Join(DataDirectory, ".ankus-cluster");
        string version = Path.Join(DataDirectory, "PG_VERSION");
        if ((File.GetAttributes(DataDirectory) & FileAttributes.ReparsePoint) != 0 ||
            !File.Exists(marker) || !File.Exists(version) ||
            File.ReadAllText(marker) != _installation.Label ||
            File.ReadAllText(version).Trim() != _installation.Version.Major.ToString(CultureInfo.InvariantCulture))
        {
            throw new InvalidOperationException($"Refusing an unowned or incompatible PostgreSQL data directory: {DataDirectory}");
        }
    }

    private async Task InitializeAsync(CancellationToken cancellationToken)
    {
        string staging = Path.Join(_root, ".init-" + Guid.NewGuid().ToString("N"));
        try
        {
            await RunCheckedAsync(_installation.InitDbPath,
                ["-D", staging, "--auth=trust", "--encoding=UTF8", "--locale=C", "--username=postgres",
                    "-L", _installation.SharedDirectory], cancellationToken).ConfigureAwait(false);
            await File.AppendAllTextAsync(Path.Join(staging, "postgresql.conf"),
                "\n# Settings supplied by ankus start.\ninclude = 'ankus.conf'\n", cancellationToken).ConfigureAwait(false);
            await File.WriteAllTextAsync(Path.Join(staging, ".ankus-cluster"), _installation.Label, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(staging, DataDirectory);
        }
        finally
        {
            if (Directory.Exists(staging))
            {
                Directory.Delete(staging, recursive: true);
            }
        }
    }

    private async Task<bool> QueryRunningAsync(CancellationToken cancellationToken)
    {
        (int code, string output) = await RunAsync(_installation.PgCtlPath, ["status", "-D", DataDirectory], cancellationToken).ConfigureAwait(false);
        return code switch
        {
            0 => true,
            3 => false,
            _ => throw new InvalidOperationException($"PostgreSQL status failed ({code}): {output}"),
        };
    }

    /// <summary>
    /// Requests a fast shutdown and waits for pg_ctl's default limit, which honors PGCTLTIMEOUT as cargo pgrx stop does.
    /// </summary>
    private Task StopRunningAsync(CancellationToken cancellationToken)
        => RunCheckedAsync(_installation.PgCtlPath, ["stop", "-D", DataDirectory, "-m", "fast", "-w"], cancellationToken);

    private async Task RunCheckedAsync(string executable, string[] arguments, CancellationToken token, bool allowDescendants = false,
        IReadOnlyDictionary<string, string?>? environment = null)
    {
        (int code, string output) = await RunAsync(executable, arguments, token, allowDescendants, environment: environment).ConfigureAwait(false);
        if (code != 0)
        {
            throw new InvalidOperationException($"PostgreSQL command '{Path.GetFileName(executable)}' failed ({code}): {output}");
        }
    }

    private async Task<(int Code, string Output)> RunAsync(string executable, string[] arguments, CancellationToken token,
        bool allowDescendants = false, string? input = null, bool postgresClient = false,
        IReadOnlyDictionary<string, string?>? environment = null, bool sudo = false)
    {
        token.ThrowIfCancellationRequested();
        // Windows pg_ctl passes inherited handles to its persistent server. Shell execution
        // isolates it from caller pipes; server diagnostics still go to the explicit -l log.
        bool detached = OperatingSystem.IsWindows() && allowDescendants;
        var start = new ProcessStartInfo(executable)
        {
            UseShellExecute = detached,
            WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = !detached,
            RedirectStandardError = !detached,
            RedirectStandardInput = !detached,
            // Another account may be unable to enter the cluster root; its commands use absolute paths.
            WorkingDirectory = sudo ? "/" : _root,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        if (!detached)
        {
            start.Environment.Remove("PGDATA");
            start.Environment.Remove("PGHOST");
            start.Environment.Remove("PGPORT");
        }

        if (postgresClient)
        {
            foreach (string key in start.Environment.Keys.Where(static key => key.StartsWith("PG", StringComparison.OrdinalIgnoreCase)).ToArray())
            {
                start.Environment.Remove(key);
            }

            start.Environment["PGCLIENTENCODING"] = "UTF8";
            start.StandardInputEncoding = new UTF8Encoding(false, true);
            start.StandardOutputEncoding = Encoding.UTF8;
            start.StandardErrorEncoding = Encoding.UTF8;
        }

        if (environment is not null)
        {
            foreach ((string name, string? value) in environment)
            {
                start.Environment[name] = value;
            }
        }

        using var process = new Process { StartInfo = start };
        process.Start();
        if (!detached && input is null)
        {
            process.StandardInput.Close();
        }

        Task<string> output = detached ? Task.FromResult(string.Empty) : process.StandardOutput.ReadToEndAsync(CancellationToken.None);
        Task<string> error = detached ? Task.FromResult(string.Empty) : process.StandardError.ReadToEndAsync(CancellationToken.None);
        try
        {
            if (input is not null)
            {
                await process.StandardInput.WriteAsync(input.AsMemory(), token).ConfigureAwait(false);
                process.StandardInput.Close();
            }

            await process.WaitForExitAsync(token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                // Descendants of sudo belong to another account, so cancellation stops sudo itself.
                process.Kill(entireProcessTree: !sudo);
            }
            catch (InvalidOperationException) when (process.HasExited)
            {
                // The process exited before cancellation was delivered.
            }

            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            await Task.WhenAll(output, error).ConfigureAwait(false);
            throw;
        }

        return (process.ExitCode, await output.ConfigureAwait(false) + await error.ConfigureAwait(false));
    }
}
