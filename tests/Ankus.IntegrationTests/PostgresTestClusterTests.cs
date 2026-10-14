using System.Diagnostics;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Exercises cluster ownership, transaction isolation, expected errors, and failure diagnostics against a real server.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
public sealed class PostgresTestClusterTests(TestContext context)
{
    /// <summary>
    /// A test whose backend dies reports the postmaster's account of that backend, not only the session's own lines.
    /// </summary>
    [TestMethod]
    public async Task FailedTestReportsItsBackendsTermination()
    {
        CancellationToken token = context.CancellationToken;
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(token);
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        int backend = 0;
        PostgresTestException error = await Assert.ThrowsExactlyAsync<PostgresTestException>(() =>
            cluster.RunInTransactionAsync("killed backend", async (connection, transaction, cancellation) =>
            {
                backend = connection.ProcessID;
                using (Process process = Process.GetProcessById(backend))
                {
                    process.Kill();
                    await process.WaitForExitAsync(cancellation);
                }

                await using var command = new NpgsqlCommand("SELECT 1", connection, transaction);
                await command.ExecuteScalarAsync(cancellation);
            }, token));
        Assert.Contains($"(PID {backend})", error.ServerLog);
        Assert.Contains("DETAIL:  Failed process was running:", error.ServerLog);
    }

    /// <summary>
    /// Verifies backend test functions leave no committed rows after successful execution.
    /// </summary>
    [TestMethod]
    public async Task BackendFunctionWritesAreRolledBack()
    {
        await PostgresFixture.Cluster.RunTestAsync("tests", "insert_probe", cancellationToken: context.CancellationToken);

        Assert.AreEqual(0L, await ReadProbeCountAsync());
    }

    /// <summary>
    /// Concurrent test callbacks use separate sessions that cannot see each other's uncommitted rows, and both roll back.
    /// </summary>
    [TestMethod]
    public async Task ConcurrentCallbacksAreIsolatedAndRolledBack()
    {
        TaskCompletionSource[] inserted = [new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously)];
        int[] backends = new int[2];
        long[] visible = new long[2];
        Task Run(int index) => PostgresFixture.Cluster.RunInTransactionAsync("concurrent-" + index, async (connection, transaction, token) =>
        {
            backends[index] = connection.ProcessID;
            await using var command = new NpgsqlCommand($"INSERT INTO tests.rollback_probe VALUES ({9001 + index})", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            inserted[index].SetResult();
            await inserted[1 - index].Task.WaitAsync(token);
            command.CommandText = "SELECT count(*) FROM tests.rollback_probe WHERE value IN (9001, 9002)";
            visible[index] = Assert.IsInstanceOfType<long>(await command.ExecuteScalarAsync(token));
        }, context.CancellationToken);
        await Task.WhenAll(Run(0), Run(1));
        Assert.AreNotEqual(backends[0], backends[1]);
        Assert.AreSequenceEqual([1L, 1L], visible);
        Assert.AreEqual(0L, await ReadProbeCountAsync());
    }

    /// <summary>
    /// Verifies a test-host assertion failure also rolls back database changes and preserves the original exception.
    /// </summary>
    [TestMethod]
    public async Task FailedCallbackRollsBackWrites()
    {
        var expected = new InvalidOperationException("intentional test failure");
        PostgresTestException error = await Assert.ThrowsExactlyAsync<PostgresTestException>(() =>
            PostgresFixture.Cluster.RunInTransactionAsync("failed-callback", async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("INSERT INTO tests.rollback_probe VALUES (7)", connection, transaction);
                await command.ExecuteNonQueryAsync(token);
                throw expected;
            }, context.CancellationToken));

        Assert.AreSame(expected, error.InnerException);
        Assert.AreEqual("failed-callback", error.TestName);
        Assert.AreEqual(0L, await ReadProbeCountAsync());
    }

    /// <summary>
    /// Verifies expected server errors are accepted and a fresh backend remains usable afterward.
    /// </summary>
    [TestMethod]
    public async Task ExpectedErrorMatchesAndClusterRemainsUsable()
    {
        await PostgresFixture.Cluster.RunTestAsync("tests", "fail_probe", "division by zero", context.CancellationToken);

        Assert.AreEqual(0L, await ReadProbeCountAsync());
    }

    /// <summary>
    /// Verifies wrong expected messages report the actual SQLSTATE and the failing session's PostgreSQL log.
    /// </summary>
    [TestMethod]
    public async Task WrongExpectedErrorIncludesServerDiagnostics()
    {
        PostgresTestException error = await Assert.ThrowsExactlyAsync<PostgresTestException>(() =>
            PostgresFixture.Cluster.RunTestAsync("tests", "fail_probe", "not the actual error", context.CancellationToken));

        PostgresException databaseError = Assert.IsInstanceOfType<PostgresException>(error.InnerException);
        Assert.AreEqual(PostgresErrorCodes.DivisionByZero, databaseError.SqlState);
        Assert.AreEqual("division by zero", databaseError.MessageText);
        Assert.Contains("division by zero", error.ServerLog);
        Assert.Contains("fail_probe", error.ServerLog);
    }

    /// <summary>
    /// Verifies that an expected error cannot silently pass when the backend function succeeds.
    /// </summary>
    [TestMethod]
    public async Task MissingExpectedErrorFailsAndRollsBack()
    {
        PostgresTestException error = await Assert.ThrowsExactlyAsync<PostgresTestException>(() =>
            PostgresFixture.Cluster.RunTestAsync("tests", "insert_probe", "expected failure", context.CancellationToken));

        Assert.IsInstanceOfType<InvalidOperationException>(error.InnerException);
        Assert.Contains("but the test succeeded", error.Message);
        Assert.AreEqual(0L, await ReadProbeCountAsync());
    }

    /// <summary>
    /// Verifies cancellation after a database write preserves cancellation and rolls back without the canceled token.
    /// </summary>
    [TestMethod]
    public async Task CanceledCallbackRollsBackWrites()
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        await Assert.ThrowsAsync<OperationCanceledException>(() => PostgresFixture.Cluster.RunInTransactionAsync(
            "canceled-callback", async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("INSERT INTO tests.rollback_probe VALUES (9)", connection, transaction);
                await command.ExecuteNonQueryAsync(token);
                await cancellation.CancelAsync();
                token.ThrowIfCancellationRequested();
            }, cancellation.Token));

        Assert.AreEqual(0L, await ReadProbeCountAsync());
    }

    /// <summary>
    /// Clusters skip fsync and use pgrx's logging defaults, INFO messages and statements slower than a second, with a
    /// C collation and UTF-8; settings supplied by the test follow the defaults and override them.
    /// </summary>
    [TestMethod]
    public async Task ClustersSkipFsyncUnlessConfigured()
    {
        // pgrx's C locale rules: C on Windows, C collation with UTF-8 character classes on macOS, and C.UTF-8 on other
        // Unix systems that provide it.
        string locale = "C|C";
        if (OperatingSystem.IsMacOS())
        {
            locale = "C|UTF-8";
        }
        else if (!OperatingSystem.IsWindows())
        {
            ProcessResult locales = await PackageProcessRunner.RunAsync("locale", ["-a"], new Dictionary<string, string?>(),
                context.CancellationToken);
            if (locales.ExitCode == 0 && locales.StandardOutput.Split('\n').Any(static value => value.Trim() is "C.UTF-8" or "C.utf8"))
            {
                locale = "C.UTF-8|C.UTF-8";
            }
        }

        await using (NpgsqlConnection fixture = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken))
        await using (var show = new NpgsqlCommand("""
            SELECT concat_ws('|', current_setting('fsync'), current_setting('log_min_messages'),
                current_setting('log_min_duration_statement'), current_setting('log_statement'),
                current_setting('server_encoding'), datcollate, datctype)
              FROM pg_database WHERE datname = current_database()
            """, fixture))
        {
            Assert.AreEqual("off|info|1s|none|UTF8|" + locale, await show.ExecuteScalarAsync(context.CancellationToken));
        }

        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        var options = new PostgresTestClusterOptions
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            StartupTimeout = defaults.StartupTimeout,
            PostgreSqlConfiguration = [.. defaults.PostgreSqlConfiguration, "fsync = on", "log_min_messages = debug1"],
        };
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand(
            "SELECT current_setting('fsync') || '|' || current_setting('log_min_messages')", connection);
        Assert.AreEqual("on|debug1", await command.ExecuteScalarAsync(context.CancellationToken));
    }

    /// <summary>
    /// Messages containing <c>TMSG: </c> reach the test output while the test runs, as pgrx echoes them; other
    /// messages do not.
    /// </summary>
    [TestMethod]
    public async Task TestMessagesAreEchoedWhileTheTestRuns()
    {
        using var output = new StringWriter();
        PostgresTestCluster.TestMessageWriter.Value = output;
        try
        {
            await PostgresFixture.Cluster.RunInTransactionAsync(nameof(TestMessagesAreEchoedWhileTheTestRuns), async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    DO $$
                    BEGIN
                        RAISE INFO 'TMSG: first';
                        RAISE NOTICE 'not a test message';
                        RAISE WARNING 'TMSG: second';
                    END
                    $$
                    """, connection, transaction);
                await command.ExecuteNonQueryAsync(token);
                Assert.AreEqual("INFO:  TMSG: first" + Environment.NewLine + "WARNING:  TMSG: second" + Environment.NewLine, output.ToString());
            }, context.CancellationToken);
        }
        finally
        {
            PostgresTestCluster.TestMessageWriter.Value = null;
        }
    }

    /// <summary>
    /// A cluster can run as another Unix account through sudo, as cargo pgrx test --runas does: that account owns the
    /// data, socket and native log, runs the server, and the retained log is read back after its storage is removed.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task ClusterRunsAsAnotherAccount()
    {
        CancellationToken token = context.CancellationToken;
        string account = IntegrationEnvironment.RequireRunAsAccount();
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(token);
        string dataBase = await IntegrationEnvironment.CreateAccountDirectoryAsync(account, token);
        try
        {
            PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
            {
                Installation = defaults.Installation,
                DataDirectoryBase = dataBase,
                LogDirectory = defaults.LogDirectory,
                StartupTimeout = defaults.StartupTimeout,
                RunAs = account,
            }, token);
            await using (cluster)
            {
                Assert.StartsWith(dataBase + "/", cluster.DataDirectory);
                Assert.StartsWith("/tmp/ak-", cluster.SocketDirectory);
                await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
                await using var command = new NpgsqlCommand("SELECT pg_backend_pid(), current_setting('data_directory')", connection);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                int backend = reader.GetInt32(0);
                Assert.AreEqual(cluster.DataDirectory, reader.GetString(1));
                ProcessResult owner = await ProcessRunner.RunAsync("ps", ["-o", "user=", "-p",
                    backend.ToString(System.Globalization.CultureInfo.InvariantCulture)], new Dictionary<string, string?>(), token);
                Assert.AreEqual(account, owner.StandardOutput.Trim());
                Assert.IsFalse(File.Exists(Path.Join(cluster.DataDirectory, "PG_VERSION")), "The data directory is private to the account.");
                Assert.Contains("database system is ready to accept connections", cluster.ReadServerLog());
            }

            Assert.IsFalse(Directory.Exists(cluster.DataDirectory));
            Assert.IsFalse(File.Exists(cluster.DataDirectory + ".log"));
            Assert.IsFalse(Directory.Exists(cluster.SocketDirectory));
            Assert.IsEmpty(Directory.GetFileSystemEntries(dataBase));
            Assert.Contains("database system is shut down", await File.ReadAllTextAsync(cluster.LogFilePath, token));
        }
        finally
        {
            await IntegrationEnvironment.DeleteAccountDirectoryAsync(account, dataBase);
        }
    }

    /// <summary>
    /// The socket directory stays in the temporary directory while PostgreSQL's longest socket path fits Linux's 107-byte
    /// limit, measured in UTF-8 bytes, and otherwise moves to <c>/tmp</c>, as it always does on macOS and for another account.
    /// </summary>
    /// <param name="nameLength">The characters in the temporary directory's single name, between two separators.</param>
    /// <param name="multibyte">Whether the name uses two-byte UTF-8 characters.</param>
    /// <param name="otherAccount">Whether another account runs the server.</param>
    /// <param name="macOS">Whether the platform is macOS.</param>
    /// <param name="keepsTemporary">Whether the temporary directory is selected.</param>
    [TestMethod]
    [DataRow(3, false, false, false, true)]
    [DataRow(55, false, false, false, true)]
    [DataRow(56, false, false, false, false)]
    [DataRow(27, true, false, false, true)]
    [DataRow(28, true, false, false, false)]
    [DataRow(3, false, true, false, false)]
    [DataRow(3, false, false, true, false)]
    public void SocketRootFitsThePlatformLimit(int nameLength, bool multibyte, bool otherAccount, bool macOS, bool keepsTemporary)
    {
        string temporary = "/" + new string(multibyte ? 'é' : 'a', nameLength) + "/";
        Assert.AreEqual(keepsTemporary ? temporary : "/tmp", PostgresTestCluster.SelectSocketRoot(temporary, otherAccount, macOS));
    }

    /// <summary>
    /// Verifies multiple clusters in one process have independent ports and data, and disposal stops only the owned server.
    /// </summary>
    [TestMethod]
    public async Task DisposeStopsOwnedClusterAndPreservesLogs()
    {
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        Assert.AreNotEqual(PostgresFixture.Cluster.Port, cluster.Port);
        Assert.AreNotEqual(PostgresFixture.Cluster.DataDirectory, cluster.DataDirectory);
        Assert.IsTrue(Directory.Exists(cluster.DataDirectory));

        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(context.CancellationToken);
        string connectionString = connection.ConnectionString;
        await connection.CloseAsync();
        await cluster.DisposeAsync();
        await cluster.DisposeAsync();

        Assert.IsFalse(Directory.Exists(cluster.DataDirectory));
        Assert.IsFalse(Directory.Exists(cluster.SocketDirectory));
        Assert.Contains("database system is shut down", cluster.ReadServerLog());
        await using var stoppedConnection = new NpgsqlConnection(connectionString);
        await Assert.ThrowsAsync<NpgsqlException>(() => stoppedConnection.OpenAsync(context.CancellationToken));
        await Assert.ThrowsExactlyAsync<ObjectDisposedException>(() => cluster.OpenConnectionAsync(context.CancellationToken));
        Assert.AreEqual(0L, await ReadProbeCountAsync());
    }

    /// <summary>
    /// Verifies invalid PostgreSQL settings fail startup, remove the invocation's data, and retain the startup diagnostic.
    /// </summary>
    [TestMethod]
    public async Task FailedStartupCleansDataAndRetainsDiagnostic()
    {
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        string root = Path.Join(defaults.DataDirectoryBase, $"failure-{Guid.NewGuid():N}");
        var options = new PostgresTestClusterOptions
        {
            Installation = defaults.Installation,
            SharedDirectory = defaults.SharedDirectory,
            DataDirectoryBase = root,
            LogDirectory = defaults.LogDirectory,
            PostgreSqlConfiguration = ["ankus_invalid_configuration = 'invalid'"],
        };
        try
        {
            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                PostgresTestCluster.StartAsync(options, context.CancellationToken));

            Assert.Contains("ankus_invalid_configuration", error.Message);
            Assert.IsEmpty(Directory.EnumerateFileSystemEntries(root));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private async Task<long> ReadProbeCountAsync()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT count(*) FROM tests.rollback_probe", connection);
        return Assert.IsInstanceOfType<long>(await command.ExecuteScalarAsync(context.CancellationToken));
    }
}
