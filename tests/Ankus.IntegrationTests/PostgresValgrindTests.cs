using System.Globalization;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies instrumented development and test servers using native process evidence and persistent PostgreSQL data.
/// </summary>
/// <param name="context">The current cancellation context.</param>
[TestClass]
public sealed class PostgresValgrindTests(TestContext context)
{
    private readonly string _root = Directory.CreateTempSubdirectory("ankus valgrind café's ").FullName;

    /// <summary>
    /// Removes this test's temporary files after its owned server has stopped.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// A test cluster runs under Memcheck when asked, as pgrx's <c>USE_VALGRIND</c> test servers do, and its retained log
    /// carries Memcheck's report.
    /// </summary>
    [RetryPortCollisionTestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task TestClustersRunUnderValgrind()
    {
        CancellationToken token = context.CancellationToken;
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(token);
        string log;
        await using (PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            StartupTimeout = defaults.StartupTimeout,
            UseValgrind = true,
        }, token))
        {
            await cluster.RunInTransactionAsync(nameof(TestClustersRunUnderValgrind), async (connection, transaction, cancellation) =>
            {
                await using var command = new NpgsqlCommand("SELECT pg_backend_pid()", connection, transaction);
                int backend = Assert.IsInstanceOfType<int>(await command.ExecuteScalarAsync(cancellation));
                string maps = await File.ReadAllTextAsync($"/proc/{backend.ToString(CultureInfo.InvariantCulture)}/maps", cancellation);
                Assert.Contains("vgpreload_memcheck", maps);
            }, token);
            log = cluster.LogFilePath;
        }

        string text = await File.ReadAllTextAsync(log, token);
        Assert.Contains("Memcheck, a memory error detector", text);
        Assert.Contains("ERROR SUMMARY:", text);
    }

    /// <summary>
    /// Memcheck runs the selected executable, preserves exact settings and data, and does not replace a running server.
    /// </summary>
    [RetryPortCollisionTestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task ValgrindStartupPreservesDataAndReportsInstrumentation()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation source = await IntegrationEnvironment.GetInstallationAsync(token);
        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(source, Path.Join(_root, "installation's files"), token);
        var cluster = new PostgresDevelopmentCluster(owner.Installation, Path.Join(_root, "home"));
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        const string Setting = " café='\\value # ; $(echo test) ";
        var instrumented = new PostgresDevelopmentOptions
        {
            Port = port,
            UseValgrind = true,
            Settings = new Dictionary<string, string> { ["probe.text"] = Setting },
        };
        try
        {
            Assert.IsTrue(await cluster.StartAsync(new PostgresDevelopmentOptions { Port = port }, token));
            await using (NpgsqlConnection seed = await OpenAsync(port, token))
            {
                await using var setup = new NpgsqlCommand("CREATE TABLE retained(value integer); INSERT INTO retained VALUES(42)", seed);
                await setup.ExecuteNonQueryAsync(token);
            }

            Assert.IsTrue(await cluster.StopAsync(token));
            File.Delete(cluster.LogFilePath);
            Assert.IsTrue(await cluster.StartAsync(instrumented, token));
            Assert.IsTrue(await cluster.IsRunningAsync(token));
            await using (NpgsqlConnection connection = await OpenAsync(port, token))
            {
                await using var setup = new NpgsqlCommand("SELECT current_setting('probe.text')", connection);
                Assert.AreEqual(Setting, await setup.ExecuteScalarAsync(token));
                await using var pid = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
                int backend = Assert.IsInstanceOfType<int>(await pid.ExecuteScalarAsync(token));
                string maps = await File.ReadAllTextAsync($"/proc/{backend.ToString(CultureInfo.InvariantCulture)}/maps", token);
                Assert.Contains("vgpreload_memcheck", maps);
                Assert.IsFalse(await cluster.StartAsync(new PostgresDevelopmentOptions { Port = 1 }, token));
                Assert.AreEqual(backend, await pid.ExecuteScalarAsync(token));
            }

            Assert.IsTrue(await cluster.StopAsync(token));
            string log = await File.ReadAllTextAsync(cluster.LogFilePath, token);
            Assert.Contains("Memcheck, a memory error detector", log);
            Assert.Contains("ERROR SUMMARY:", log);
            Assert.IsTrue(await cluster.StartAsync(new PostgresDevelopmentOptions { Port = port }, token));
            await using NpgsqlConnection restarted = await OpenAsync(port, token);
            await using var retained = new NpgsqlCommand("SELECT value FROM retained", restarted);
            Assert.AreEqual(42, await retained.ExecuteScalarAsync(token));
            await using var current = new NpgsqlCommand("SELECT pg_backend_pid()", restarted);
            int nativeBackend = Assert.IsInstanceOfType<int>(await current.ExecuteScalarAsync(token));
            Assert.DoesNotContain("vgpreload_memcheck", await File.ReadAllTextAsync($"/proc/{nativeBackend.ToString(CultureInfo.InvariantCulture)}/maps", token));
            Assert.IsFalse(await cluster.StartAsync(instrumented, token));
            Assert.AreEqual(nativeBackend, await current.ExecuteScalarAsync(token));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Actual native memory errors reach the server log with their stack and delimiters while subsequent SQL remains usable.
    /// </summary>
    [RetryPortCollisionTestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task ValgrindReportsNativeErrorsAndPreservesDiagnostics()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(token);
        string library = await CompileErrorProbeAsync(installation, token);
        var cluster = new PostgresDevelopmentCluster(installation, _root);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        try
        {
            Assert.IsTrue(await cluster.StartAsync(new PostgresDevelopmentOptions { Port = port }, token));
            await using (NpgsqlConnection seed = await OpenAsync(port, token))
            {
                await using var setup = new NpgsqlCommand("CREATE FUNCTION valgrind_error_probe() RETURNS integer AS '" +
                    library.Replace("'", "''", StringComparison.Ordinal) + "', 'valgrind_error_probe' LANGUAGE c STRICT", seed);
                await setup.ExecuteNonQueryAsync(token);
            }

            Assert.IsTrue(await cluster.StopAsync(token));
            File.Delete(cluster.LogFilePath);
            Assert.IsTrue(await cluster.StartAsync(new PostgresDevelopmentOptions { Port = port, UseValgrind = true }, token));
            await using (NpgsqlConnection connection = await OpenAsync(port, token))
            {
                await using var fault = new NpgsqlCommand("SELECT valgrind_error_probe()", connection);
                Assert.AreEqual(42, await fault.ExecuteScalarAsync(token));
                await using var recovery = new NpgsqlCommand("SELECT 43", connection);
                Assert.AreEqual(43, await recovery.ExecuteScalarAsync(token));
            }

            Assert.IsTrue(await cluster.StopAsync(token));
            string log = await File.ReadAllTextAsync(cluster.LogFilePath, token);
            string diagnostic = Assert.ContainsSingle(log.Split("VALGRINDERROR-BEGIN", StringSplitOptions.None)
                .Skip(1).Select(static report => report.Split("VALGRINDERROR-END", StringSplitOptions.None)[0])
                .Where(static report => report.Contains("Invalid read of size 1", StringComparison.Ordinal) &&
                    report.Contains("valgrind_error_probe", StringComparison.Ordinal)), log);
            Assert.Contains("free", diagnostic);
            Assert.Contains("VALGRINDERROR-END", log);
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Valgrind startup passes PostgreSQL's suppressions from the installation's PGXS tree, as pgrx passes its source
    /// tree's file, so a suppressed error is counted rather than reported; without that file, the source tree PGXS
    /// records is used when it still exists.
    /// </summary>
    [RetryPortCollisionTestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task ValgrindUsesPostgresSuppressions()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation source = await IntegrationEnvironment.GetInstallationAsync(token);
        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(source, Path.Join(_root, "installation"), token);
        PostgresInstallation installation = owner.Installation;
        string pgxs = (await ProcessRunner.RunAsync(installation.PgConfigPath, ["--pgxs"], new Dictionary<string, string?>(), token))
            .StandardOutput.Trim();
        string pgxsSource = Path.GetDirectoryName(Path.GetDirectoryName(pgxs))!;
        string installed = Path.Join(pgxsSource, "tools", "valgrind.supp");

        // An ankus init installation already has suppressions in PGXS; start this staged copy without them.
        if (File.Exists(installed))
        {
            File.Delete(installed);
        }

        string sourceTree = Path.Join(_root, "postgres source's tree");
        Directory.CreateDirectory(Path.Join(sourceTree, "src", "tools"));
        string recorded = Path.Join(sourceTree, "src", "tools", "valgrind.supp");
        await File.WriteAllTextAsync(recorded, string.Empty, token);
        string global = Path.Join(pgxsSource, "Makefile.global");
        string[] lines = await File.ReadAllLinesAsync(global, token);
        await File.WriteAllLinesAsync(global, lines.Select(line => line.StartsWith("abs_top_srcdir", StringComparison.Ordinal)
            ? "abs_top_srcdir = " + sourceTree : line), token);
        Assert.AreEqual(recorded, await installation.GetValgrindSuppressionsPathAsync(token));
        File.Delete(recorded);
        Assert.IsNull(await installation.GetValgrindSuppressionsPathAsync(token));

        Directory.CreateDirectory(Path.GetDirectoryName(installed)!);
        await File.WriteAllTextAsync(installed, """
            {
               ankus_probe_read_after_free
               Memcheck:Addr1
               fun:valgrind_error_probe
            }
            """, token);
        Assert.AreEqual(installed, await installation.GetValgrindSuppressionsPathAsync(token));
        string library = await CompileErrorProbeAsync(installation, token);
        var cluster = new PostgresDevelopmentCluster(installation, Path.Join(_root, "home"));
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        try
        {
            Assert.IsTrue(await cluster.StartAsync(new PostgresDevelopmentOptions { Port = port, UseValgrind = true }, token));
            await using (NpgsqlConnection connection = await OpenAsync(port, token))
            {
                await using var setup = new NpgsqlCommand("CREATE FUNCTION valgrind_error_probe() RETURNS integer AS '" +
                    library.Replace("'", "''", StringComparison.Ordinal) + "', 'valgrind_error_probe' LANGUAGE c STRICT", connection);
                await setup.ExecuteNonQueryAsync(token);
                await using var fault = new NpgsqlCommand("SELECT valgrind_error_probe()", connection);
                Assert.AreEqual(42, await fault.ExecuteScalarAsync(token));
            }

            Assert.IsTrue(await cluster.StopAsync(token));
            string log = await File.ReadAllTextAsync(cluster.LogFilePath, token);
            Assert.DoesNotContain("valgrind_error_probe", string.Concat(log.Split("VALGRINDERROR-BEGIN").Skip(1)
                .Select(static report => report.Split("VALGRINDERROR-END")[0])));
            Assert.MatchesRegex(@"ERROR SUMMARY: \d+ errors from \d+ contexts \(suppressed: [1-9]", log);
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Compiles a C function that reads freed memory, which Memcheck reports as an invalid read.
    /// </summary>
    private async Task<string> CompileErrorProbeAsync(PostgresInstallation installation, CancellationToken token)
    {
        string source = Path.Join(_root, "memory-probe.c");
        string library = Path.Join(_root, "memory-probe.so");
        await File.WriteAllTextAsync(source, """
            #include "postgres.h"
            #include "fmgr.h"
            #include <stdlib.h>
            PG_MODULE_MAGIC;
            PG_FUNCTION_INFO_V1(valgrind_error_probe);
            Datum valgrind_error_probe(PG_FUNCTION_ARGS);
            static volatile unsigned char observed;
            Datum valgrind_error_probe(PG_FUNCTION_ARGS)
            {
                unsigned char *allocation = malloc(16);
                if (allocation == NULL)
                    elog(ERROR, "cannot allocate diagnostic witness");
                allocation[0] = 42;
                volatile unsigned char *released = allocation;
                free(allocation);
                /* Keep the deliberate fault observable to Memcheck's optimizer. */
                observed = *released;
                PG_RETURN_INT32(42);
            }
            """, token);
        ProcessResult compilation = await ProcessRunner.RunAsync("cc",
            ["-shared", "-fPIC", "-g", "-I", installation.ServerIncludeDirectory, "-I", installation.IncludeDirectory, source, "-o", library],
            new Dictionary<string, string?>(), token);
        Assert.AreEqual(0, compilation.ExitCode, compilation.StandardOutput + compilation.StandardError);
        return library;
    }

    /// <summary>
    /// A failed instrumented bind retains initialized data, releases the operation lock, and permits a healthy restart.
    /// </summary>
    [RetryPortCollisionTestMethod]
    [OSCondition(OperatingSystems.Linux)]
    public async Task ValgrindStartupFailureRecovers()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(token);
        var cluster = new PostgresDevelopmentCluster(installation, _root);
        using PortReservation reservation = PortReservation.Create();
        var options = new PostgresDevelopmentOptions { Port = reservation.Port, TimeoutSeconds = 5, UseValgrind = true };
        try
        {
            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cluster.StartAsync(options, token));
            Assert.Contains(cluster.LogFilePath, error.Message);
            Assert.IsFalse(await cluster.IsRunningAsync(token));
            Assert.IsTrue(File.Exists(Path.Join(cluster.DataDirectory, "PG_VERSION")));
            Assert.IsEmpty(Directory.GetDirectories(Path.GetDirectoryName(cluster.DataDirectory)!, ".init-*"));
            string log = await File.ReadAllTextAsync(cluster.LogFilePath, token);
            Assert.Contains("Memcheck, a memory error detector", log);
            Assert.Contains("could not create any TCP/IP sockets", log);
            reservation.Dispose();
            var recovered = new PostgresDevelopmentOptions
            {
                Port = options.Port,
                TimeoutSeconds = (int)IntegrationEnvironment.StartupTimeout.TotalSeconds,
                UseValgrind = true,
            };
            Assert.IsTrue(await cluster.StartAsync(recovered, token));
            await using NpgsqlConnection connection = await OpenAsync(options.Port!.Value, token);
            await using var command = new NpgsqlCommand("SELECT 19 + 23", connection);
            Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Timeout and cancellation during a witnessed native preload stop the instrumented server and allow restart.
    /// </summary>
    /// <param name="cancel">Whether to cancel explicitly instead of exhausting the startup timeout.</param>
    [RetryPortCollisionTestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ValgrindInterruptedStartupStopsOwnedServer(bool cancel)
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(token);
        string source = Path.Join(_root, "gate.c");
        string library = Path.Join(_root, "gate.so");
        await File.WriteAllTextAsync(source, """
            #include "postgres.h"
            #include "fmgr.h"
            #include <unistd.h>
            PG_MODULE_MAGIC;
            void _PG_init(void);
            void _PG_init(void)
            {
                FILE *marker = fopen("valgrind-started", "w");
                if (marker == NULL)
                    elog(ERROR, "cannot write startup witness");
                fclose(marker);
                while (access("valgrind-release", F_OK) != 0)
                    pg_usleep(10000L);
            }
            """, token);
        ProcessResult compilation = await ProcessRunner.RunAsync("cc",
            ["-shared", "-fPIC", "-I", installation.ServerIncludeDirectory, "-I", installation.IncludeDirectory, source, "-o", library],
            new Dictionary<string, string?>(), token);
        Assert.AreEqual(0, compilation.ExitCode, compilation.StandardOutput + compilation.StandardError);
        var cluster = new PostgresDevelopmentCluster(installation, _root);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(token);
        var options = new PostgresDevelopmentOptions
        {
            Port = port, UseValgrind = true, TimeoutSeconds = cancel ? 60 : 5,
            Settings = new Dictionary<string, string> { ["shared_preload_libraries"] = library },
        };
        Task<bool>? starting = null;
        try
        {
            Assert.IsTrue(await cluster.StartAsync(new PostgresDevelopmentOptions { Port = port }, token));
            Assert.IsTrue(await cluster.StopAsync(token));
            File.Delete(cluster.LogFilePath);
            starting = cluster.StartAsync(options, cancellation.Token);
            string witness = Path.Join(cluster.DataDirectory, "valgrind-started");
            while (!File.Exists(witness) && !starting.IsCompleted)
            {
                await Task.Delay(20, token);
            }

            Assert.IsTrue(File.Exists(witness), "Startup must reach the native preload before interruption.");
            if (cancel)
            {
                await cancellation.CancelAsync();
                await Assert.ThrowsAsync<OperationCanceledException>(() => starting);
            }
            else
            {
                InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => starting);
                Assert.Contains(cluster.LogFilePath, error.Message);
            }

            Assert.IsFalse(await cluster.IsRunningAsync(token));
            Assert.IsFalse(File.Exists(Path.Join(cluster.DataDirectory, "valgrind-release")));
            Assert.Contains("Memcheck, a memory error detector", await File.ReadAllTextAsync(cluster.LogFilePath, token));
            Assert.IsTrue(await cluster.StartAsync(new PostgresDevelopmentOptions { Port = port }, token));
            await using NpgsqlConnection connection = await OpenAsync(port, token);
            await using var command = new NpgsqlCommand("SELECT 19 + 23", connection);
            Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
        }
        finally
        {
            if (Directory.Exists(cluster.DataDirectory))
            {
                await File.WriteAllTextAsync(Path.Join(cluster.DataDirectory, "valgrind-release"), "release", CancellationToken.None);
            }

            if (starting is not null)
            {
                // Observe a failed start before taking the same cluster's shutdown lock.
                Task pendingStartup = starting;
                await pendingStartup.ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }

            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Invalid settings and pre-cancellation produce no data directories or instrumentation processes.
    /// </summary>
    [TestMethod]
    public async Task ValgrindInvalidRequestsPreserveState()
    {
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(context.CancellationToken);
        var cluster = new PostgresDevelopmentCluster(installation, _root);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => cluster.StartAsync(
            new PostgresDevelopmentOptions { UseValgrind = true, Port = 0 }, context.CancellationToken));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => cluster.StartAsync(
            new PostgresDevelopmentOptions { UseValgrind = true }, canceled.Token));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    /// <summary>
    /// Unsupported native Windows instrumentation fails explicitly before initializing or changing a cluster.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task ValgrindRejectsWindowsBeforeChanges()
    {
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(context.CancellationToken);
        var cluster = new PostgresDevelopmentCluster(installation, _root);
        PlatformNotSupportedException error = await Assert.ThrowsExactlyAsync<PlatformNotSupportedException>(() => cluster.StartAsync(
            new PostgresDevelopmentOptions { UseValgrind = true }, context.CancellationToken));
        Assert.Contains("Valgrind", error.Message);
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    private static async Task<NpgsqlConnection> OpenAsync(int port, CancellationToken token)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = port, Username = "postgres", Database = "postgres", Pooling = false,
        }.ConnectionString);
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
