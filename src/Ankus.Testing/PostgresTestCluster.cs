using System.Globalization;
using System.Runtime.ExceptionServices;
using System.Text;
using Ankus.PgConfig;
using Npgsql;

namespace Ankus.Testing;

/// <summary>
/// Owns a pgrx-style, per-invocation PostgreSQL cluster and runs backend tests in rollback-only transactions.
/// Dispose the cluster after all parallel test sessions finish. Normal process exit also attempts shutdown.
/// </summary>
public sealed class PostgresTestCluster : IAsyncDisposable
{
    /// <summary>
    /// The allowance for a <c>pg_ctl status</c> check, which only reads the server's PID file.
    /// </summary>
    private static readonly TimeSpan s_statusTimeout = TimeSpan.FromSeconds(30);

    private readonly PostgresTestClusterOptions _options;
    private readonly IReadOnlyDictionary<string, string?> _environment;

    /// <summary>
    /// Whether the server runs under Valgrind, from the options or ankus test --valgrind.
    /// </summary>
    private readonly bool _useValgrind;
    private readonly PostgresTestLog _log;
    private readonly PostgresServerAccount _account;
    private readonly Lock _shutdownLock = new();
    private Task? _shutdownTask;

    /// <summary>
    /// Requires definitive native shutdown until pg_ctl confirms the attempted server is stopped.
    /// </summary>
    private bool _requiresShutdown;

    private PostgresTestCluster(PostgresTestClusterOptions options, int port)
    {
        _options = options;
        _environment = new Dictionary<string, string?>(options.ProcessEnvironment);
        _useValgrind = options.UseValgrind || TestCommandContext.UseValgrind;
        _account = PostgresServerAccount.Create(options.RunAs ?? TestCommandContext.RunAs);
        Port = port;
        string invocation = $"{options.Installation.Version.Major}-{Environment.ProcessId}-{Guid.NewGuid():N}";
        string? session = TestCommandContext.SessionDirectory;
        DataDirectory = Path.GetFullPath(Path.Join(TestCommandContext.DataDirectory ?? options.DataDirectoryBase, invocation));
        LogFilePath = Path.GetFullPath(Path.Join(options.LogDirectory, $"{invocation}.log"));
        // Another account writes its server log beside the data directory it owns; reads copy it to the retained log.
        _log = _account.Name is null ? new(LogFilePath) : new(LogFilePath, _account, DataDirectory + ".log");
        SocketDirectory = OperatingSystem.IsWindows()
            ? null
            : session is null || _account.Name is not null
                ? Path.Join(SelectSocketRoot(Path.GetTempPath(), _account.Name is not null, OperatingSystem.IsMacOS()), $"ak-{Guid.NewGuid():N}")
                : Path.Join(session, $"s-{Guid.NewGuid():N}");
    }

    /// <summary>
    /// Selects the parent of a cluster's Unix socket directory. PostgreSQL's socket path must fit the platform's
    /// <c>sun_path</c>, so a temporary directory too long for it, such as a CI work directory, gives way to <c>/tmp</c>,
    /// where PostgreSQL's own test clusters keep their sockets. Another account cannot use the caller's private
    /// temporary directory, and macOS temporary directories are always too long.
    /// </summary>
    /// <param name="temporary">The caller's temporary directory.</param>
    /// <param name="otherAccount">Whether another account runs the server.</param>
    /// <param name="macOS">Whether the platform limits socket paths to 103 bytes, as macOS does, rather than Linux's 107.</param>
    /// <returns>The directory beneath which the socket directory is created.</returns>
    internal static string SelectSocketRoot(string temporary, bool otherAccount, bool macOS)
    {
        if (otherAccount || macOS)
        {
            return "/tmp";
        }

        // The longest socket path is the socket directory plus "/.s.PGSQL.65535".
        int longest = Encoding.UTF8.GetByteCount(Path.Join(temporary, "ak-" + new string('0', 32), ".s.PGSQL.65535"));
        return longest <= 107 ? temporary : "/tmp";
    }

    /// <summary>
    /// Gets the installation whose tools and backend serve this cluster.
    /// </summary>
    public PostgresInstallation Installation => _options.Installation;

    /// <summary>
    /// Gets the unique PGDATA directory, removed after a successful shutdown.
    /// </summary>
    public string DataDirectory { get; }

    /// <summary>
    /// Gets the Unix socket directory, or null when Windows uses loopback TCP only.
    /// </summary>
    public string? SocketDirectory { get; }

    /// <summary>
    /// Gets the loopback TCP port selected for this invocation.
    /// </summary>
    public int Port { get; }

    /// <summary>
    /// Gets the retained server log path.
    /// </summary>
    /// <remarks>
    /// On Windows, <see cref="ReadServerLog"/> refreshes this file with native file and Event Log messages.
    /// Disposal also refreshes the file to retain shutdown diagnostics.
    /// </remarks>
    public string LogFilePath { get; }

    /// <summary>
    /// Initializes a fresh cluster, waits for readiness, and creates its test database.
    /// A failed startup attempts shutdown and retains the server log in the reported diagnostic.
    /// </summary>
    /// <remarks>
    /// If another process claims an automatically selected port before PostgreSQL binds it, startup retries with a new
    /// cluster and port, up to three attempts within the same startup timeout. Explicit ports are never replaced;
    /// collisions and other startup failures are reported immediately.
    /// Failed attempts remove their data and socket directories and retain their server logs.
    /// </remarks>
    /// <param name="options">The installation and invocation settings.</param>
    /// <param name="cancellationToken">Cancels startup.</param>
    /// <returns>The ready cluster, owned by the caller.</returns>
    public static Task<PostgresTestCluster> StartAsync(
        PostgresTestClusterOptions options,
        CancellationToken cancellationToken = default)
        => StartAsync(options, beforeStart: null, cancellationToken);

    /// <summary>
    /// Starts a cluster with a per-invocation handoff callback for deterministic startup contention tests.
    /// </summary>
    /// <param name="options">The installation and invocation settings.</param>
    /// <param name="beforeStart">Runs before startup with the automatic-port reservation, or null for an explicit port.</param>
    /// <param name="cancellationToken">Cancels the complete initialization and readiness operation.</param>
    /// <returns>The ready cluster, owned by the caller.</returns>
    internal static async Task<PostgresTestCluster> StartAsync(
        PostgresTestClusterOptions options,
        Action<PostgresTestCluster, PortReservation?>? beforeStart,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(options.Installation);
        TestCommandContext.ValidateInstallation(options.Installation);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.DatabaseName);
        ArgumentException.ThrowIfNullOrWhiteSpace(options.UserName);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.StartupTimeout, TimeSpan.Zero);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(options.ShutdownTimeout, TimeSpan.Zero);
        if (options.Port is int port)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(port, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(port, 65535);
        }

        bool useValgrind = options.UseValgrind || TestCommandContext.UseValgrind;
        if (useValgrind && OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Valgrind requires a supported Unix platform; native Windows PostgreSQL cannot run under Valgrind.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (useValgrind)
        {
            try
            {
                (await ProcessRunner.RunAsync("valgrind", ["--version"], options.ProcessEnvironment, cancellationToken).ConfigureAwait(false))
                    .EnsureSuccess("valgrind", ["--version"]);
            }
            catch (System.ComponentModel.Win32Exception error)
            {
                throw new InvalidOperationException("Install Valgrind and make its executable available on PATH before using Valgrind.", error);
            }
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(useValgrind ? options.StartupTimeout + TimeSpan.FromMinutes(1) : options.StartupTimeout);
        const int MaximumAttempts = 3;
        for (int attempt = 1; ; attempt++)
        {
            timeout.Token.ThrowIfCancellationRequested();
            // A configured port is used as given; otherwise a free one stays reserved until the server binds it.
            int? configuredPort = options.Port;
            using PortReservation? reservation = configuredPort is null ? PortReservation.Create() : null;
            int selectedPort = configuredPort ?? reservation!.Port;
            var cluster = new PostgresTestCluster(options, selectedPort);
            AppDomain.CurrentDomain.ProcessExit += cluster.OnProcessExit;
            try
            {
                await cluster.InitializeAsync(reservation, beforeStart, timeout.Token).ConfigureAwait(false);
                return cluster;
            }
            catch (Exception error)
            {
                string log = string.Empty;
                Exception? diagnosticFailure = null;
                try
                {
                    log = cluster.ReadServerLog();
                }
                // codeql[cs/catch-of-all-exceptions] A startup failure is reported together with any cleanup or log failure that followed it.
                catch (Exception logError)
                {
                    diagnosticFailure = logError;
                }

                try
                {
                    await cluster.DisposeAsync().ConfigureAwait(false);
                }
                // codeql[cs/catch-of-all-exceptions] A startup failure is reported together with any cleanup or log failure that followed it.
                catch (Exception cleanupError)
                {
                    Exception[] failures = diagnosticFailure is null ? [error, cleanupError] : [error, diagnosticFailure, cleanupError];
                    throw new AggregateException($"Startup and cleanup failed. Log: {cluster.LogFilePath}\n{log}", failures);
                }

                if (diagnosticFailure is not null)
                {
                    throw new AggregateException($"Startup and diagnostic collection failed. Log: {cluster.LogFilePath}", error, diagnosticFailure);
                }

                if (error is OperationCanceledException)
                {
                    throw;
                }

                if (options.Port is null && attempt < MaximumAttempts && cluster.HasPortCollision(log))
                {
                    timeout.Token.ThrowIfCancellationRequested();
                    continue;
                }

                throw new InvalidOperationException(
                    $"PostgreSQL startup failed: {error.Message}\nLog: {cluster.LogFilePath}\n{log}", error);
            }
        }
    }

    /// <summary>
    /// Opens an unpooled connection to the test database. The caller owns the connection.
    /// </summary>
    /// <param name="cancellationToken">Cancels connection establishment.</param>
    /// <returns>An open connection.</returns>
    public Task<NpgsqlConnection> OpenConnectionAsync(CancellationToken cancellationToken = default)
        => OpenConnectionAsync(_options.DatabaseName, "ankus-setup", cancellationToken);

    /// <summary>
    /// Executes a SQL test function inside PostgreSQL and rolls its transaction back afterward.
    /// Expected errors must match the server's primary message exactly, as in pgrx.
    /// </summary>
    /// <param name="schema">The SQL schema containing the test function.</param>
    /// <param name="functionName">The zero-argument SQL test function.</param>
    /// <param name="expectedError">An expected server error message, or null for success.</param>
    /// <param name="cancellationToken">Cancels the test.</param>
    /// <returns>A task that completes when the backend test and rollback finish.</returns>
    public Task RunTestAsync(
        string schema,
        string functionName,
        string? expectedError = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrEmpty(schema);
        ArgumentException.ThrowIfNullOrEmpty(functionName);
        return RunInTransactionAsync($"{schema}.{functionName}", async (connection, transaction, token) =>
        {
            string sql = $"SELECT {QuoteIdentifier(schema)}.{QuoteIdentifier(functionName)}()";
            await using var command = new NpgsqlCommand(sql, connection, transaction);
            try
            {
                await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
            }
            catch (PostgresException error) when (expectedError is not null && error.MessageText == expectedError)
            {
                return;
            }

            if (expectedError is not null)
            {
                throw new InvalidOperationException($"Expected PostgreSQL error '{expectedError}', but the test succeeded.");
            }
        }, cancellationToken);
    }

    /// <summary>
    /// Runs a test with its own connection and transaction, rolling back on success, failure, or cancellation.
    /// The callback must not commit or replace the transaction. Parallel callbacks use independent sessions.
    /// </summary>
    /// <param name="testName">The test name used in failure diagnostics.</param>
    /// <param name="test">The test operation.</param>
    /// <param name="cancellationToken">Cancels the connection and test operation.</param>
    /// <returns>A task that completes after rollback.</returns>
    public async Task RunInTransactionAsync(
        string testName,
        Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> test,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(testName);
        ArgumentNullException.ThrowIfNull(test);
        string sessionName = $"ankus-{Guid.NewGuid():N}";
        int backend = 0;
        try
        {
            await using NpgsqlConnection connection = await OpenConnectionAsync(
                _options.DatabaseName, sessionName, cancellationToken).ConfigureAwait(false);
            backend = connection.ProcessID;
            connection.Notice += static (_, notice) => EchoTestMessage(notice.Notice);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            await test(connection, transaction, cancellationToken).ConfigureAwait(false);
            // Npgsql's transaction disposal rolls back without the canceled test token.
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        // codeql[cs/catch-of-all-exceptions] Every test failure is reported with the server log of its session.
        catch (Exception error)
        {
            string sessionLog = ReadSessionLog(sessionName, backend);
            // A lost connection may mean the backend died; the postmaster reports that shortly after the client notices.
            for (int attempt = 0; attempt < 40 && backend != 0 && error is NpgsqlException and not PostgresException &&
                !sessionLog.Contains($"(PID {backend})", StringComparison.Ordinal); attempt++)
            {
                await Task.Delay(50, CancellationToken.None).ConfigureAwait(false);
                sessionLog = ReadSessionLog(sessionName, backend);
            }

            throw new PostgresTestException(testName, sessionLog, error);
        }
    }

    /// <summary>
    /// Gets a writer that receives test messages in this asynchronous flow instead of standard output.
    /// </summary>
    internal static AsyncLocal<TextWriter?> TestMessageWriter { get; } = new();

    /// <summary>
    /// Writes a message the session receives that contains <c>TMSG: </c>, as pgrx echoes such server log lines to
    /// the test output while the test runs.
    /// </summary>
    /// <param name="notice">The INFO, NOTICE or WARNING message.</param>
    private static void EchoTestMessage(PostgresNotice notice)
    {
        if (notice.MessageText.Contains("TMSG: ", StringComparison.Ordinal))
        {
            (TestMessageWriter.Value ?? Console.Out).WriteLine(notice.InvariantSeverity + ":  " + notice.MessageText);
        }
    }

    /// <summary>
    /// Reads the server log, including startup and shutdown diagnostics.
    /// </summary>
    /// <returns>The available log text.</returns>
    public string ReadServerLog() => _log.Read();

    /// <summary>
    /// Stops PostgreSQL in fast mode and removes owned data and socket directories, retaining logs.
    /// Shutdown uses its own timeout. If shutdown fails, directories remain available for recovery.
    /// A later disposal retries a faulted or canceled shutdown after the native server recovers.
    /// After successful shutdown, a diagnostic read failure is reported after owned directories are removed.
    /// </summary>
    /// <returns>A task that completes after shutdown and cleanup.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_shutdownLock)
        {
            if (_shutdownTask is null || _shutdownTask.IsFaulted || _shutdownTask.IsCanceled)
            {
                _shutdownTask = StopAsync();
            }

            return new ValueTask(_shutdownTask);
        }
    }

    private async Task InitializeAsync(PortReservation? reservation, Action<PostgresTestCluster, PortReservation?>? beforeStart, CancellationToken cancellationToken)
    {
        await _account.CreateDirectoryAsync(Path.GetDirectoryName(DataDirectory)!, cancellationToken).ConfigureAwait(false);
        Directory.CreateDirectory(Path.GetDirectoryName(LogFilePath)!);
        if (SocketDirectory is not null)
        {
            await _account.CreateDirectoryAsync(SocketDirectory, cancellationToken).ConfigureAwait(false);
            if (_account.Name is null && !OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(SocketDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
            }
        }

        var arguments = new List<string>
        {
            "-D", DataDirectory, "--auth=trust", "--encoding=UTF8", "--no-sync", "--username", _options.UserName,
            "-L", _options.SharedDirectory ?? Installation.SharedDirectory,
        };
        arguments.AddRange(await PostgresLocale.GetInitDbArgumentsAsync(_environment, cancellationToken).ConfigureAwait(false));
        await _account.RunCheckedAsync(Installation.InitDbPath, arguments, _environment, cancellationToken).ConfigureAwait(false);

        var configuration = new StringBuilder();
        // Like PostgreSQL's own TAP clusters, a disposable test cluster skips fsync: crash recovery still replays the
        // operating system's cached WAL, and only durability across an operating system crash is given up.
        // Configuration supplied by the test follows and can enable it again.
        configuration.AppendLine("fsync = off");
        configuration.AppendLine("log_min_messages = info");
        configuration.AppendLine("log_min_duration_statement = 1000");
        foreach (string setting in _options.PostgreSqlConfiguration)
        {
            configuration.AppendLine(setting);
        }

        // Routing and diagnostic identity belong to the harness, independent of extension settings.
        configuration.AppendLine(CultureInfo.InvariantCulture, $"port = {Port}");
        configuration.AppendLine("listen_addresses = '127.0.0.1'");
        configuration.AppendLine(CultureInfo.InvariantCulture,
            $"unix_socket_directories = {QuoteSetting(SocketDirectory ?? string.Empty)}");
        configuration.AppendLine("log_destination = 'stderr'");
        configuration.AppendLine("logging_collector = off");
        configuration.AppendLine("log_line_prefix = '[%m] [%p] [%c] [%a]: '");
        await _account.WriteFileAsync(
            Path.Join(DataDirectory, "postgresql.auto.conf"), configuration.ToString(), cancellationToken).ConfigureAwait(false);

        beforeStart?.Invoke(this, reservation);
        reservation?.Dispose();
        cancellationToken.ThrowIfCancellationRequested();
        _requiresShutdown = true;
        string[] startupArguments =
        [
            "start", "-D", DataDirectory, "-l", _log.NativeFilePath, "-w", "-t", GetTimeoutSeconds(_options.StartupTimeout),
        ];
        IReadOnlyDictionary<string, string?> startupEnvironment = _environment;
        if (OperatingSystem.IsWindows())
        {
            // Command-line configuration takes effect before loading configuration files,
            // retaining startup errors as well as the service token's Event Log routing.
            startupArguments = [.. startupArguments, "-o", "-c event_source=" + _log.EventSource];
        }
        else if (_useValgrind)
        {
            // pg_ctl inserts -D before -o arguments, so PGDATA names the data directory and Valgrind receives its own
            // options before the shell-quoted PostgreSQL executable, as pgrx's USE_VALGRIND wrapper arranges.
            string? suppressions = await Installation.GetValgrindSuppressionsPathAsync(cancellationToken).ConfigureAwait(false);
            string executable = ShellQuote(Path.Join(Installation.BinDirectory, "postgres"));
            startupArguments =
            [
                "start", "-l", _log.NativeFilePath, "-w", "-t", GetTimeoutSeconds(_options.StartupTimeout + TimeSpan.FromMinutes(1)),
                "-p", "valgrind", "-o",
                "--tool=memcheck --leak-check=no --gen-suppressions=all --time-stamp=yes " +
                "--error-markers=VALGRINDERROR-BEGIN,VALGRINDERROR-END --trace-children=yes " +
                (suppressions is null ? string.Empty : ShellQuote("--suppressions=" + suppressions) + " ") + executable,
            ];
            var environment = new Dictionary<string, string?>(_environment) { ["PGDATA"] = DataDirectory };
            if (!environment.ContainsKey("DOTNET_GCRegionRange") && string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DOTNET_GCRegionRange")))
            {
                // Memcheck shares a bounded address space with its client; the runtime's default reservation can exceed it.
                environment["DOTNET_GCRegionRange"] = "800000000";
            }

            startupEnvironment = environment;
        }

        await _account.RunCheckedAsync(
            Installation.PgCtlPath,
            startupArguments,
            startupEnvironment,
            cancellationToken,
            captureOutput: !OperatingSystem.IsWindows()).ConfigureAwait(false);

        await using NpgsqlConnection connection = await OpenConnectionAsync("postgres", "ankus-setup", cancellationToken)
            .ConfigureAwait(false);
        // PostgreSQL 13 and 14 copy the template database through a checkpoint, which can exceed an ordinary
        // command timeout on a busy disk. Bootstrap shares the configured startup budget.
        await using var command = new NpgsqlCommand($"CREATE DATABASE {QuoteIdentifier(_options.DatabaseName)}", connection)
        {
            CommandTimeout = GetConnectionTimeoutSeconds(_options.StartupTimeout),
        };
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<NpgsqlConnection> OpenConnectionAsync(string database, string sessionName, CancellationToken cancellationToken)
    {
        lock (_shutdownLock)
        {
            ObjectDisposedException.ThrowIf(_shutdownTask is not null, this);
        }

        var builder = new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = Port,
            Database = database,
            Username = _options.UserName,
            Pooling = false,
            IncludeErrorDetail = true,
            Enlist = false,
            ApplicationName = sessionName,
            Timeout = GetConnectionTimeoutSeconds(_options.StartupTimeout),
            CommandTimeout = 30,
            SslMode = SslMode.Disable,
        };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task StopAsync()
    {
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        if (_requiresShutdown)
        {
            using var timeout = new CancellationTokenSource(_options.ShutdownTimeout);
            // A status check reads postmaster.pid and returns at once, so it gets its own allowance; a short shutdown
            // timeout on a slow host must not cancel the check before pg_ctl has even started.
            using var probe = new CancellationTokenSource(s_statusTimeout);
            ProcessResult status = await _account.RunAsync(
                Installation.PgCtlPath, ["status", "-D", DataDirectory], _environment, probe.Token).ConfigureAwait(false);
            if (status.ExitCode == 0)
            {
                TimeSpan fastDuration = TimeSpan.FromTicks(Math.Min(
                    _options.ShutdownTimeout.Ticks / 2,
                    TimeSpan.FromSeconds(5).Ticks));
                using var fastTimeout = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
                fastTimeout.CancelAfter(fastDuration);
                ProcessResult? fast = null;
                try
                {
                    fast = await _account.RunAsync(
                        Installation.PgCtlPath,
                        ["stop", "-D", DataDirectory, "-m", "fast", "-w", "-t", GetTimeoutSeconds(fastDuration)],
                        _environment,
                        fastTimeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!timeout.IsCancellationRequested)
                {
                    // The fast stop used up its share of the timeout, so an immediate stop follows.
                }

                if (fast?.ExitCode != 0)
                {
                    string[] immediateArguments =
                    [
                        "stop", "-D", DataDirectory, "-m", "immediate", "-w", "-t", GetTimeoutSeconds(_options.ShutdownTimeout),
                    ];
                    ProcessResult immediate = await _account.RunAsync(
                        Installation.PgCtlPath,
                        immediateArguments,
                        _environment,
                        timeout.Token).ConfigureAwait(false);
                    if (immediate.ExitCode != 0)
                    {
                        using var stoppedProbe = new CancellationTokenSource(s_statusTimeout);
                        ProcessResult stopped = await _account.RunAsync(
                            Installation.PgCtlPath, ["status", "-D", DataDirectory], _environment, stoppedProbe.Token).ConfigureAwait(false);
                        if (stopped.ExitCode != 3)
                        {
                            immediate.EnsureSuccess(Installation.PgCtlPath, immediateArguments);
                        }
                    }
                }
            }
            else if (status.ExitCode != 3)
            {
                status.EnsureSuccess(Installation.PgCtlPath, ["status", "-D", DataDirectory]);
            }
        }

        _requiresShutdown = false;
        var failures = new List<Exception>();
        try
        {
            _ = ReadServerLog();
        }
        // codeql[cs/catch-of-all-exceptions] Every cleanup step still runs, and all of their failures are reported together.
        catch (Exception error)
        {
            failures.Add(error);
        }

        DeleteOwnedDirectory(DataDirectory, failures);
        if (_account.Name is not null)
        {
            DeleteOwnedDirectory(DataDirectory + ".log", failures);
        }

        if (SocketDirectory is not null)
        {
            DeleteOwnedDirectory(SocketDirectory, failures);
        }

        if (failures.Count == 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException("PostgreSQL stopped, but diagnostic collection or owned-directory cleanup failed.", failures);
        }
    }

    /// <summary>
    /// Removes one stopped invocation's directory while allowing its other owned resources to be reclaimed.
    /// </summary>
    /// <param name="path">The invocation's data or socket directory, or another account's native server log.</param>
    /// <param name="failures">Receives original cleanup exceptions without replacing diagnostic failures.</param>
    private void DeleteOwnedDirectory(string path, List<Exception> failures)
    {
        try
        {
            _account.DeleteDirectory(path);
        }
        // codeql[cs/catch-of-all-exceptions] Every cleanup step still runs, and all of their failures are reported together.
        catch (Exception error)
        {
            failures.Add(error);
        }
    }

    private void OnProcessExit(object? sender, EventArgs arguments)
    {
        try
        {
            DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        // codeql[cs/catch-of-all-exceptions] An exception in a process exit handler would end the process, so the failure is printed instead.
        catch (Exception error)
        {
            Console.Error.WriteLine($"PostgreSQL cleanup failed for '{DataDirectory}': {error}");
        }
    }

    /// <summary>
    /// Selects the session's own lines and the postmaster's report about its backend, with that report's details.
    /// </summary>
    /// <param name="sessionName">The session's application name.</param>
    /// <param name="backend">The session's backend process ID, or zero when it never connected.</param>
    /// <returns>The selected log lines.</returns>
    private string ReadSessionLog(string sessionName, int backend)
    {
        string marker = $"[{sessionName}]: ";
        string? report = backend == 0 ? null : $"(PID {backend})";
        string? reporter = null;
        var result = new StringBuilder();
        bool include = false;
        foreach (string line in ReadServerLog().Split('\n'))
        {
            if (line.StartsWith('['))
            {
                // log_line_prefix is '[%m] [%p] [%c] [%a]: ', so the second field is the writing process.
                int process = line.IndexOf("] [", StringComparison.Ordinal);
                int processEnd = process < 0 ? -1 : line.IndexOf(']', process + 3);
                string? writer = processEnd < 0 ? null : line[(process + 3)..processEnd];
                int prefixEnd = line.IndexOf("]: ", StringComparison.Ordinal);
                string message = prefixEnd < 0 ? string.Empty : line[(prefixEnd + 3)..];
                bool detail = message.StartsWith("DETAIL:", StringComparison.Ordinal) || message.StartsWith("HINT:", StringComparison.Ordinal);
                if (report is not null && line.Contains(report, StringComparison.Ordinal))
                {
                    reporter = writer;
                    include = true;
                }
                else
                {
                    include = line.Contains(marker, StringComparison.Ordinal) || (detail && reporter is not null && writer == reporter);
                    reporter = detail ? reporter : null;
                }
            }

            if (include)
            {
                result.AppendLine(line);
            }
        }

        return result.ToString();
    }

    private static string QuoteIdentifier(string value) => $"\"{value.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";

    private bool HasPortCollision(string log)
        => log.Contains("could not bind IPv4 address \"127.0.0.1\":", StringComparison.Ordinal) &&
            log.Contains($"Is another postmaster already running on port {Port}?", StringComparison.Ordinal) &&
            log.Contains("could not create any TCP/IP sockets", StringComparison.Ordinal);

    private static string QuoteSetting(string value)
        => $"'{value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "''", StringComparison.Ordinal)}'";

    /// <summary>
    /// Quotes a word for the POSIX shell that <c>pg_ctl</c> starts the server through.
    /// </summary>
    private static string ShellQuote(string value) => "'" + value.Replace("'", "'\"'\"'", StringComparison.Ordinal) + "'";

    private static string GetTimeoutSeconds(TimeSpan timeout)
        => Math.Ceiling(timeout.TotalSeconds).ToString(CultureInfo.InvariantCulture);

    private static int GetConnectionTimeoutSeconds(TimeSpan timeout)
        => (int)Math.Clamp(Math.Ceiling(timeout.TotalSeconds), 1, int.MaxValue);
}
