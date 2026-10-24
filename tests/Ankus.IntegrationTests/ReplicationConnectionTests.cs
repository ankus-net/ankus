using System.Text;
using Ankus.Testing;
using Npgsql;
using Npgsql.Replication;
using Npgsql.Replication.Internal;
using NpgsqlTypes;

namespace Ankus.IntegrationTests;

/// <summary>
/// Opens replication connections from inside a backend to the cluster it runs in, through PostgreSQL's own
/// <c>libpqwalreceiver</c>, and runs SQL, copies and logical streams over them.
/// </summary>
/// <param name="context">The current cancellation and diagnostic context.</param>
[TestClass]
public sealed class ReplicationConnectionTests(TestContext context)
{
    /// <summary>
    /// A query returns typed values as text with SQL NULL and non-ASCII text intact, and a replication command such as
    /// <c>IDENTIFY_SYSTEM</c> reports this cluster's identity and database.
    /// </summary>
    [TestMethod]
    public async Task QueriesAndReplicationCommandsReturnRows()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        string connectionString = ConnectionString(connection);
        int version = await ScalarAsync<int>(connection, "SELECT current_setting('server_version_num')::integer", token);
        Assert.AreEqual(version + ":1|<null>|café 😀", await ScalarAsync<string>(connection,
            "SELECT datatype.replication_query(@connection, 'SELECT 1, NULL::text, ''café 😀''', 3)", token, connectionString));
        string identity = await ScalarAsync<string>(connection,
            "SELECT datatype.replication_query(@connection, 'IDENTIFY_SYSTEM', 4)", token, connectionString);
        string[] fields = identity[(identity.IndexOf(':', StringComparison.Ordinal) + 1)..].Split('|');
        Assert.AreEqual(await ScalarAsync<string>(connection, "SELECT system_identifier::text FROM pg_control_system()", token), fields[0]);
        Assert.AreEqual(await ScalarAsync<string>(connection, "SELECT current_database()::text", token), fields[3]);
    }

    /// <summary>
    /// An ordinary connection opens for a role without the REPLICATION attribute, which a replication connection
    /// refuses, runs as a client backend with replication's text settings, and copies a table.
    /// </summary>
    [TestMethod]
    public async Task OrdinaryConnectionsNeedNoReplicationRole()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        string role = "ordinary_" + Guid.NewGuid().ToString("N");
        string table = "tests.ordinary_copy_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, $"""
            CREATE ROLE {role} LOGIN;
            CREATE TABLE {table}(id integer, name text);
            INSERT INTO {table} VALUES (1, 'café 😀'), (2, NULL);
            GRANT USAGE ON SCHEMA tests TO {role};
            GRANT SELECT ON {table} TO {role};
            """, token);
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString) { Username = role, Password = null };
            await using var plain = new NpgsqlConnection(builder.ConnectionString);
            string connectionString = ConnectionString(plain);
            Assert.AreEqual($"{role}|client backend|\"\"|UTF8|ISO, MDY|postgres|3", await ScalarAsync<string>(connection,
                "SELECT datatype.ordinary_query(@connection, 'SELECT current_user, backend_type, current_setting(''search_path''), " +
                "current_setting(''client_encoding''), current_setting(''DateStyle''), current_setting(''IntervalStyle''), " +
                "current_setting(''extra_float_digits'') FROM pg_stat_activity WHERE pid = pg_backend_pid()', 7)", token, connectionString));
            Assert.AreEqual("1\tcafé 😀;2\t\\N", await ScalarAsync<string>(connection,
                $"SELECT datatype.ordinary_copy(@connection, '{table}')", token, connectionString));
            PostgresException refused = await Assert.ThrowsExactlyAsync<PostgresException>(() => ScalarAsync<string>(connection,
                "SELECT datatype.replication_query(@connection, 'SELECT 1', 1)", token, connectionString));
            Assert.AreEqual(PostgresErrorCodes.ConnectionFailure, refused.SqlState);
            Assert.StartsWith("could not connect for replication:", refused.MessageText);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE {table}; REVOKE USAGE ON SCHEMA tests FROM {role}; DROP ROLE {role}", token);
        }
    }

    /// <summary>
    /// A command that fails on the server throws its SQLSTATE and message without failing this backend, and the
    /// connection keeps working. PostgreSQL 13 does not pass the SQLSTATE on.
    /// </summary>
    [TestMethod]
    public async Task RemoteErrorsLeaveTheConnectionUsable()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        string expected = (PostgresFixture.Cluster.Installation.Version.Major >= 14 ? "22012" : "none") + "|division by zero|42";
        Assert.AreEqual(expected, await ScalarAsync<string>(connection, "SELECT datatype.replication_remote_error(@connection)", token,
            ConnectionString(connection)));
        Assert.AreEqual(1, await ScalarAsync<int>(connection, "SELECT 1", token));
    }

    /// <summary>
    /// <c>COPY ... TO STDOUT</c> streams each row, ends, and leaves the connection ready for the next command.
    /// </summary>
    [TestMethod]
    public async Task CopiesStreamRowsAndEnd()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        string table = "tests.replication_copy_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, $"CREATE TABLE {table}(id integer, name text); INSERT INTO {table} VALUES (1, 'one'), (2, NULL), (3, 'drei é');", token);
        try
        {
            Assert.AreEqual("1\tone;2\t\\N;3\tdrei é|after", await ScalarAsync<string>(connection,
                $"SELECT datatype.replication_copy(@connection, '{table}')", token, ConnectionString(connection)));
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE {table}", token);
        }
    }

    /// <summary>
    /// A disposed connection refuses further use, and disposing it again does nothing.
    /// </summary>
    [TestMethod]
    public async Task DisposedConnectionsRefuseUse()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        Assert.AreEqual("ObjectDisposedException", await ScalarAsync<string>(connection,
            "SELECT datatype.replication_use_after_dispose(@connection)", token, ConnectionString(connection)));
    }

    /// <summary>
    /// A server that cannot be reached raises a connection failure, and the session keeps working.
    /// </summary>
    [TestMethod]
    public async Task UnreachableServersRaiseConnectionFailures()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => ScalarAsync<string>(connection,
            "SELECT datatype.replication_query('host=127.0.0.1 port=1 dbname=postgres connect_timeout=2', 'SELECT 1', 1)", token));
        Assert.AreEqual("08006", error.SqlState);
        Assert.StartsWith("could not connect for replication", error.MessageText);
        Assert.AreEqual(1, await ScalarAsync<int>(connection, "SELECT 1", token));
    }

    /// <summary>
    /// A logical slot's committed changes stream in order, and the status update the backend sends confirms them, so
    /// the slot's confirmed position moves past them.
    /// </summary>
    [TestMethod]
    public async Task LogicalStreamsDeliverChangesAndAcceptFeedback()
        => await RunLogicalAsync(async (connection, token) =>
        {
            // A slot cannot be created in a transaction that has written, so each statement commits on its own.
            await ExecuteAsync(connection, "CREATE TABLE stream_rows(id integer PRIMARY KEY, name text)", token);
            await ExecuteAsync(connection, "SELECT pg_create_logical_replication_slot('ankus_stream', 'test_decoding')", token);
            await ExecuteAsync(connection, "INSERT INTO stream_rows VALUES (1, 'one')", token);
            await ExecuteAsync(connection, "INSERT INTO stream_rows VALUES (2, 'two'), (3, 'three')", token);
            string before = await ScalarAsync<string>(connection,
                "SELECT confirmed_flush_lsn::text FROM pg_replication_slots WHERE slot_name = 'ankus_stream'", token);
            string changes = await ScalarAsync<string>(connection,
                "SELECT datatype.replication_stream(@connection, 'ankus_stream', 3)", token, ConnectionString(connection));
            Assert.AreEqual("table public.stream_rows: INSERT: id[integer]:1 name[text]:'one';" +
                "table public.stream_rows: INSERT: id[integer]:2 name[text]:'two';" +
                "table public.stream_rows: INSERT: id[integer]:3 name[text]:'three'", changes);
            Assert.IsTrue(await ScalarAsync<bool>(connection,
                $"SELECT confirmed_flush_lsn > '{before}'::pg_lsn FROM pg_replication_slots WHERE slot_name = 'ankus_stream'", token));
        });

    /// <summary>
    /// A wait for a message that never comes honors the statement timeout; the error disposes the connection, which
    /// releases the slot, and the session keeps working.
    /// </summary>
    [TestMethod]
    public async Task WaitsHonorCancellationAndReleaseTheSlot()
        => await RunLogicalAsync(async (connection, token) =>
        {
            await ExecuteAsync(connection, "SELECT pg_create_logical_replication_slot('ankus_idle', 'test_decoding')", token);
            await ExecuteAsync(connection, "SET statement_timeout = '500ms'", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => ScalarAsync<string>(connection,
                "SELECT datatype.replication_wait_forever(@connection, 'ankus_idle')", token, ConnectionString(connection)));
            Assert.AreEqual("57014", error.SqlState);
            await ExecuteAsync(connection, "RESET statement_timeout", token);
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (await ScalarAsync<bool>(connection, "SELECT active FROM pg_replication_slots WHERE slot_name = 'ankus_idle'", token))
            {
                // The server notices the closed connection when its WAL sender next reads.
                Assert.IsLessThan(deadline, DateTime.UtcNow, "The slot stayed active after the connection closed.");
                await Task.Delay(100, token);
            }
        });

    /// <summary>
    /// A remote <c>COPY</c> fills a local table through PostgreSQL's <c>COPY FROM</c>, matching the named columns and
    /// filling defaults, with SQL NULL and non-ASCII text intact, and the connection then runs commands again. A row
    /// that breaks a constraint fails the copy with its usual error, and the session keeps working.
    /// </summary>
    [TestMethod]
    public async Task CopiesFillLocalTables()
        => await WithCopyTablesAsync(async (connection, copy, target, token) =>
        {
            Assert.AreEqual("3|ready", await ScalarAsync<string>(connection, copy, token, ConnectionString(connection)));
            Assert.AreEqual("1:one:default;2:<null>:default;3:café 😀:default", await ScalarAsync<string>(connection,
                $"SELECT string_agg(id || ':' || coalesce(name, '<null>') || ':' || extra, ';' ORDER BY id) FROM {target}", token));
            await ExecuteAsync(connection, $"TRUNCATE {target}; UPDATE {target.Replace("target", "source", StringComparison.Ordinal)} SET id = -id WHERE id = 3", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ScalarAsync<string>(connection, copy, token, ConnectionString(connection)));
            Assert.AreEqual(PostgresErrorCodes.CheckViolation, error.SqlState);
            Assert.AreEqual(0L, await ScalarAsync<long>(connection, $"SELECT count(*) FROM {target}", token));
        });

    /// <summary>
    /// Copying needs <c>INSERT</c> on the table and, as for the built-in subscriber, refuses a table whose row-level
    /// security applies to the user, because <c>COPY FROM</c> does not apply policies.
    /// </summary>
    [TestMethod]
    public async Task CopiesCheckPrivilegesAndRowSecurity()
        => await WithCopyTablesAsync(async (connection, copy, target, token) =>
        {
            string connectionString = ConnectionString(connection);
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token);
            string role = "copy_user_" + Guid.NewGuid().ToString("N");
            await ExecuteAsync(connection, $"CREATE ROLE {role}; GRANT USAGE ON SCHEMA datatype TO {role}; SET LOCAL ROLE {role}", token);
            await transaction.SaveAsync("denied", token);
            PostgresException denied = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ScalarAsync<string>(connection, copy, token, connectionString));
            Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            Assert.AreEqual($"permission denied for table {target["tests.".Length..]}", denied.MessageText);
            await transaction.RollbackAsync("denied", token);
            await ExecuteAsync(connection, $"RESET ROLE; GRANT INSERT ON {target} TO {role}; ALTER TABLE {target} ENABLE ROW LEVEL SECURITY; SET LOCAL ROLE {role}", token);
            PostgresException secured = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ScalarAsync<string>(connection, copy, token, connectionString));
            Assert.AreEqual(PostgresErrorCodes.FeatureNotSupported, secured.SqlState);
            Assert.AreEqual($"user \"{role}\" cannot replicate into relation with row-level security enabled: \"{target["tests.".Length..]}\"",
                secured.MessageText);
            await transaction.RollbackAsync(token);
        });

    /// <summary>
    /// Creates a three-row source table and an empty target with its columns in another order, a constraint and a
    /// default, runs the test with the statement that copies one into the other, and drops both.
    /// </summary>
    private async Task WithCopyTablesAsync(Func<NpgsqlConnection, string, string, CancellationToken, Task> test)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        string suffix = Guid.NewGuid().ToString("N");
        string source = "tests.copy_source_" + suffix;
        string target = "tests.copy_target_" + suffix;
        await ExecuteAsync(connection, $"CREATE TABLE {source}(id integer, name text, ignored text)", token);
        await ExecuteAsync(connection, $"CREATE TABLE {target}(name text, id integer PRIMARY KEY CHECK (id > 0), extra text DEFAULT 'default')", token);
        try
        {
            await ExecuteAsync(connection, $"INSERT INTO {source} VALUES (1, 'one', 'x'), (2, NULL, 'y'), (3, 'café 😀', 'z')", token);
            // The OID is resolved here so that a test running as another role needs no schema privilege.
            string relation = await ScalarAsync<string>(connection, $"SELECT '{target}'::regclass::oid::text", token);
            await test(connection, $"SELECT datatype.replication_copy_into(@connection, 'COPY {source} (id, name) TO STDOUT', " +
                $"{relation}, 'id,name')", target, token);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE {source}, {target}", token);
        }
    }

    /// <summary>
    /// Builds a libpq connection string for the server and database a client connection uses.
    /// </summary>
    /// <summary>
    /// An output plugin reads the catalog at startup through <c>PgTransaction.Run</c>. The SQL decoding functions call
    /// it inside their transaction; a WAL sender calls it outside one, so Run starts and commits its own. A failed read
    /// rolls that transaction back, and the next stream starts normally.
    /// </summary>
    [TestMethod]
    public async Task OutputPluginStartupRunsItsOwnTransaction()
        => await RunLogicalAsync(async (connection, token) =>
        {
            bool trusted = await ScalarAsync<bool>(connection, "SELECT EXISTS (SELECT FROM pg_settings WHERE name = 'output_plugin_libraries')", token);
            if (trusted)
            {
                await ExecuteAsync(connection, "SET output_plugin_libraries = 'pgoutput', 'test_decoding', 'Ankus.TestExtension'", token);
            }

            await ExecuteAsync(connection, "CREATE TABLE plugin_rows(id integer)", token);
            await ExecuteAsync(connection, "SELECT pg_create_logical_replication_slot('ankus_transaction', 'Ankus.TestExtension')", token);
            await ExecuteAsync(connection, "INSERT INTO plugin_rows VALUES (1)", token);
            Assert.AreEqual($"in a transaction, top transaction context:{connection.Database};change", await ScalarAsync<string>(connection,
                "SELECT string_agg(data, ';' ORDER BY ordinality) FROM pg_logical_slot_peek_changes('ankus_transaction', NULL, NULL) WITH ORDINALITY",
                token));
            PostgresException failed = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                StreamFirstMessageAsync(connection, "ankus_transaction", trusted, fail: true, token));
            Assert.AreEqual(PostgresErrorCodes.DivisionByZero, failed.SqlState);
            Assert.AreEqual($"outside a transaction, no top transaction context:{connection.Database}",
                await StreamFirstMessageAsync(connection, "ankus_transaction", trusted, fail: false, token));
        });

    private static async Task<string> StreamFirstMessageAsync(NpgsqlConnection connection, string slot, bool trusted, bool fail,
        CancellationToken token)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString)
        {
            Options = (trusted ? "-c output_plugin_libraries=pgoutput,test_decoding,Ankus.TestExtension " : "") +
                (fail ? "-c ankus_transaction_test.fail=on" : ""),
            Pooling = false,
        };
        await using var replication = new LogicalReplicationConnection(builder.ConnectionString);
        await replication.Open(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        await foreach (XLogDataMessage message in replication.StartLogicalReplication(
            new TestPluginSlot(new ReplicationSlotOptions(slot, default(NpgsqlLogSequenceNumber))), timeout.Token))
        {
            using var buffer = new MemoryStream();
            await message.Data.CopyToAsync(buffer, timeout.Token);
            return Encoding.ASCII.GetString(buffer.ToArray());
        }

        throw new InvalidOperationException("The stream ended before its first message.");
    }

    private static string ConnectionString(NpgsqlConnection connection)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        string text = $"host={Quote(builder.Host!)} port={builder.Port} dbname={Quote(builder.Database!)} user={Quote(builder.Username!)}";
        return builder.Password is { Length: > 0 } password ? text + " password=" + Quote(password) : text;
    }

    private static string Quote(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Starts a cluster with logical WAL, installs the test extension and runs the test in its default database.
    /// </summary>
    private async Task RunLogicalAsync(Func<NpgsqlConnection, CancellationToken, Task> test)
    {
        CancellationToken token = context.CancellationToken;
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(token);
        PostgresTestClusterOptions options = new()
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            PostgreSqlConfiguration = [.. defaults.PostgreSqlConfiguration, "wal_level = logical", "max_replication_slots = 8", "max_wal_senders = 8"],
        };
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await ExecuteAsync(connection, "CREATE SCHEMA datatype; CREATE EXTENSION ankus_test WITH SCHEMA datatype;", token);
        await test(connection, token);
    }

    /// <summary>
    /// Returns the first column of the first row, binding <c>@connection</c> when given.
    /// </summary>
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken token, string? connectionString = null)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        if (connectionString is not null)
        {
            command.Parameters.AddWithValue("connection", connectionString);
        }

        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    private sealed class TestPluginSlot(ReplicationSlotOptions options) : LogicalReplicationSlot("Ankus.TestExtension", options);
}
