using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Executes the ported pgrx bad_ideas sample and verifies how each bad idea is contained.
/// </summary>
/// <param name="context">The current cancellation and diagnostic context.</param>
[TestClass]
public sealed class BadIdeasExampleTests(TestContext context)
{
    /// <summary>
    /// A managed ERROR can be swallowed like pgrx's caught <c>error!</c>; warnings continue; unguarded and task
    /// exceptions become ERROR in the same backend; disposal runs before the error is reported.
    /// </summary>
    [TestMethod]
    public Task BadIdeasSampleConvertsManagedFailuresToErrors() => RunInstalledAsync(async (connection, token) =>
    {
        var notices = new List<string>();
        connection.Notice += (_, arguments) => notices.Add($"{arguments.Notice.InvariantSeverity}:{arguments.Notice.MessageText}");
        Assert.IsTrue(await ScalarAsync<bool>(connection, "SELECT error('swallowed')", token));
        Assert.IsTrue(await ScalarAsync<bool>(connection, "SELECT warning('careful')", token));
        Assert.AreSequenceEqual<string>(["WARNING:careful"], notices);
        notices.Clear();

        await AssertFailureAsync(connection, "SELECT crash_postgres()", "38000", "oh no!", token);
        await AssertFailureAsync(connection, "SELECT task_panic()", "38000", "oh no, from a task!", token);
        await AssertFailureAsync(connection, "SELECT drop_struct()", "38000", "unable to open table: no such relation", token);
        Assert.AreSequenceEqual<string>(["INFO:before foo drop", "INFO:Foo was dropped"], notices);
    });

    /// <summary>
    /// Interrupt checks let statement timeouts and cancellation stop the loop; the session continues afterward.
    /// </summary>
    [TestMethod]
    public Task BadIdeasSampleLoopIsInterruptible() => RunInstalledAsync(async (connection, token) =>
    {
        await ExecuteAsync(connection, "SET statement_timeout = '200ms'", token);
        PostgresException timeout = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(connection, "SELECT loop_forever()", token));
        Assert.AreEqual("57014", timeout.SqlState, timeout.MessageText);
        await ExecuteAsync(connection, "RESET statement_timeout", token);

        await using NpgsqlConnection administrator = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        Task loop = ExecuteAsync(connection, "SELECT loop_forever()", token);
        while (!await ScalarAsync<bool>(administrator,
            $"SELECT EXISTS(SELECT FROM pg_stat_activity WHERE pid = {backend} AND state = 'active' AND query LIKE '%loop_forever%')", token))
        {
            await Task.Delay(20, token);
        }

        Assert.IsTrue(await ScalarAsync<bool>(administrator, $"SELECT pg_cancel_backend({backend})", token));
        PostgresException canceled = await Assert.ThrowsExactlyAsync<PostgresException>(() => loop);
        Assert.AreEqual("57014", canceled.SqlState, canceled.MessageText);
    });

    /// <summary>
    /// A pre-commit exception rejects the transaction; both outcomes occur and only committed rows remain.
    /// </summary>
    [TestMethod]
    public Task BadIdeasSampleRandomAbortRollsBackRejectedCommits() => RunInstalledAsync(async (connection, token) =>
    {
        var notices = new List<string>();
        connection.Notice += (_, arguments) => notices.Add(arguments.Notice.MessageText);
        await ExecuteAsync(connection, "CREATE TEMP TABLE random_abort_rows(value integer)", token);
        int committed = 0;
        int aborted = 0;
        for (int attempt = 0; attempt < 200 && (committed == 0 || aborted == 0); attempt++)
        {
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token);
            await ExecuteAsync(connection, $"INSERT INTO random_abort_rows VALUES ({attempt}); SELECT random_abort()", token);
            PostgresException? rejected = null;
            try
            {
                await transaction.CommitAsync(token);
            }
            catch (PostgresException error)
            {
                rejected = error;
            }

            if (rejected is null)
            {
                committed++;
            }
            else
            {
                Assert.AreEqual("38000", rejected.SqlState, rejected.MessageText);
                Assert.AreEqual("aborting transaction", rejected.MessageText);
                aborted++;
            }
        }

        Assert.IsGreaterThan(0, committed);
        Assert.IsGreaterThan(0, aborted);
        Assert.AreEqual(committed, await ScalarAsync<long>(connection, "SELECT count(*) FROM random_abort_rows", token));
        Assert.AreSequenceEqual(Enumerable.Repeat("in xact callback pre-commit", committed + aborted), notices);
    });

    /// <summary>
    /// The file function writes with the server's permissions, so the sample revokes it from PUBLIC.
    /// </summary>
    [TestMethod]
    public Task BadIdeasSampleRestrictsFileWrites() => RunInstalledAsync(async (connection, token) =>
    {
        string directory = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs", "bad-ideas-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string path = Path.Join(directory, "written.bin");
            await using (var write = new NpgsqlCommand("SELECT write_file($1, $2)", connection))
            {
                write.Parameters.AddWithValue(path);
                write.Parameters.AddWithValue(new byte[] { 0, 1, 2, 255 });
                Assert.AreEqual(4L, await write.ExecuteScalarAsync(token));
            }

            Assert.AreSequenceEqual(new byte[] { 0, 1, 2, 255 }, await File.ReadAllBytesAsync(path, token));
            await AssertFailureAsync(connection, $"SELECT write_file('{Path.Join(directory, "missing", "file.bin")}', '\\x00')", "38000", null, token);
            string role = "bad_ideas_" + Guid.NewGuid().ToString("N");
            await ExecuteAsync(connection, $"CREATE ROLE {role}; SET ROLE {role}", token);
            try
            {
                await AssertFailureAsync(connection, $"SELECT write_file('{path}', '\\x01')", "42501", null, token);
                Assert.IsTrue(await ScalarAsync<bool>(connection, "SELECT warning('still callable')", token));
            }
            finally
            {
                await ExecuteAsync(connection, $"RESET ROLE; DROP ROLE {role}", CancellationToken.None);
            }

            Assert.AreSequenceEqual(new byte[] { 0, 1, 2, 255 }, await File.ReadAllBytesAsync(path, token));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    });

    /// <summary>
    /// FATAL and PANIC survive a catch block; an exception on an extension-created thread ends the process and
    /// PostgreSQL recovers the cluster. Each case runs in an isolated server.
    /// </summary>
    /// <param name="function">The terminal sample function.</param>
    /// <param name="expected">Text the server log must contain.</param>
    /// <param name="crashRecovery">Whether PostgreSQL must recover the whole cluster.</param>
    [TestMethod]
    [DataRow("fatal", "FATAL:  XX000: ", false)]
    [DataRow("panic", "PANIC:  XX000: ", true)]
    [DataRow("crash_postgres_from_thread", "Failed process was running: SELECT crash_postgres_from_thread", true)]
    public async Task BadIdeasSampleTerminalFailuresCannotBeCaught(string function, string expected, bool crashRecovery)
    {
        CancellationToken token = context.CancellationToken;
        using IDisposable recoverySlot = await CrashRecovery.ReserveAsync(token);
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(token);
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using (NpgsqlConnection setup = await cluster.OpenConnectionAsync(token))
        {
            await ExecuteAsync(setup, "CREATE EXTENSION ankus_bad_ideas", token);
        }

        string marker = "bad-ideas-" + function + '-' + Guid.NewGuid().ToString("N");
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await ExecuteAsync(connection, "SET log_error_verbosity = verbose", token);
        string sql = function == "crash_postgres_from_thread" ? "SELECT crash_postgres_from_thread()" : $"SELECT {function}('{marker}')";
        _ = await Assert.ThrowsAsync<NpgsqlException>(() => ExecuteAsync(connection, sql, token));
        Assert.AreEqual(System.Data.ConnectionState.Closed, connection.State);
        if (crashRecovery)
        {
            using CancellationTokenSource recovery = CrashRecovery.CreateDeadline(token);
            _ = await CrashRecovery.WaitAsync(cluster, recovery.Token);
            await AssertRecoveredAsync(cluster, recovery.Token);
        }
        else
        {
            await AssertRecoveredAsync(cluster, token);
        }

        string log = cluster.ReadServerLog();
        Assert.Contains(function == "crash_postgres_from_thread" ? expected : expected + marker, log);
        if (function == "crash_postgres_from_thread" && !OperatingSystem.IsWindows())
        {
            Assert.Contains("oh no, from a thread!", log);
        }
    }

    private static async Task AssertRecoveredAsync(PostgresTestCluster cluster, CancellationToken token)
    {
        while (true)
        {
            token.ThrowIfCancellationRequested();
            try
            {
                await using NpgsqlConnection recovered = await cluster.OpenConnectionAsync(token);
                Assert.IsTrue(await ScalarAsync<bool>(recovered, "SELECT warning('recovered')", token));
                return;
            }
            catch (NpgsqlException failure) when (failure is not PostgresException or PostgresException
            {
                SqlState: PostgresErrorCodes.CannotConnectNow or PostgresErrorCodes.AdminShutdown or PostgresErrorCodes.CrashShutdown
            })
            {
                await Task.Delay(50, token);
            }
        }
    }

    /// <summary>
    /// Runs autocommit statements in a fresh database with the sample installed, then removes the database.
    /// </summary>
    private async Task RunInstalledAsync(Func<NpgsqlConnection, CancellationToken, Task> test)
    {
        CancellationToken token = context.CancellationToken;
        string database = "bad_ideas_" + Guid.NewGuid().ToString("N");
        await using NpgsqlConnection administrator = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await ExecuteAsync(administrator, $"CREATE DATABASE {database} TEMPLATE template0", token);
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Database = database, Pooling = false };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(token);
            int backend = connection.ProcessID;
            await ExecuteAsync(connection, "CREATE EXTENSION ankus_bad_ideas", token);
            await test(connection, token);
            Assert.AreEqual(backend, await ScalarAsync<int>(connection, "SELECT pg_backend_pid()", token));
        }
        finally
        {
            await ExecuteAsync(administrator, $"DROP DATABASE {database} WITH (FORCE)", CancellationToken.None);
        }
    }

    private static async Task AssertFailureAsync(NpgsqlConnection connection, string sql, string sqlState, string? message, CancellationToken token)
    {
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(connection, sql, token));
        Assert.AreEqual(sqlState, error.SqlState, error.MessageText);
        if (message is not null)
        {
            Assert.AreEqual(message, error.MessageText);
        }
    }

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }
}
