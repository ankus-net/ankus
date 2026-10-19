using System.Globalization;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Applies row changes through <c>PgRelationWriter</c> inside PostgreSQL, as a logical replication apply worker does,
/// and checks the table, its constraints and its triggers.
/// </summary>
/// <param name="context">The current cancellation and diagnostic context.</param>
[TestClass]
public sealed class RelationWriterTests(TestContext context)
{
    private const string ReplicatedColumns = "id integer PRIMARY KEY, name text, amount numeric DEFAULT 7, note text DEFAULT 'default', payload bytea";

    /// <summary>
    /// Inserts, updates and deletes by the primary key, and finds rows by it, with each change visible to the next.
    /// </summary>
    [TestMethod]
    public async Task WritersApplyChangesByKey()
        => await WithTableAsync("id integer PRIMARY KEY, name text", async (connection, table, token) =>
        {
            Assert.AreEqual("True|True|False|2|zwei|True", await ScalarAsync<string>(connection,
                $"SELECT datatype.writer_apply('{table}'::regclass::oid)", token));
            Assert.AreEqual("1:one;2:zwei", await ScalarAsync<string>(connection,
                $"SELECT string_agg(id || ':' || name, ';' ORDER BY id) FROM {table}", token));
        });

    /// <summary>
    /// Rows in logical replication's form go through each type's input or receive function. An insert fills columns
    /// it does not send with their defaults while a sent NULL stays NULL, and an update keeps the values of columns it
    /// does not send.
    /// </summary>
    [TestMethod]
    public async Task ReplicatedRowsUseInputFunctionsAndDefaults()
        => await WithTableAsync(ReplicatedColumns, async (connection, table, token) =>
        {
            Assert.AreEqual("True|True|False|uno", await ScalarAsync<string>(connection,
                $"SELECT datatype.writer_apply_replicated('{table}'::regclass::oid)", token));
            Assert.AreEqual("1|uno|7|<null>|\\x00ff", await ScalarAsync<string>(connection,
                $"SELECT string_agg(id || '|' || name || '|' || amount || '|' || coalesce(note, '<null>') || '|' || payload::text, ';') FROM {table}", token));
        });

    /// <summary>
    /// An invalid text value, a binary value with bytes left over, and text that is not valid in the database
    /// encoding fail with PostgreSQL's errors, and the session keeps working.
    /// </summary>
    /// <param name="column">The column to set.</param>
    /// <param name="binary">Whether the value is in binary form.</param>
    /// <param name="value">The value as a <c>bytea</c> literal.</param>
    /// <param name="sqlState">The expected SQLSTATE.</param>
    [TestMethod]
    [DataRow("id", false, "\\x78", "22P02")]
    [DataRow("id", true, "\\x0000000102", "22P03")]
    [DataRow("name", false, "\\xff", "22021")]
    public async Task InvalidReplicatedValuesFail(string column, bool binary, string value, string sqlState)
        => await WithTableAsync(ReplicatedColumns, async (connection, table, token) =>
        {
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(connection,
                $"SELECT datatype.writer_insert_replicated('{table}'::regclass::oid, '{column}', {binary}, '{value}'::bytea)", token));
            Assert.AreEqual(sqlState, error.SqlState, error.MessageText);
            Assert.AreEqual(0L, await ScalarAsync<long>(connection, $"SELECT count(*) FROM {table}", token));
        });

    /// <summary>
    /// A table whose replica identity is <c>FULL</c> and that has no key matches a row by all of its columns.
    /// </summary>
    [TestMethod]
    public async Task TablesWithoutKeysMatchWholeRows()
        => await WithTableAsync("id integer, name text", async (connection, table, token) =>
        {
            await ExecuteAsync(connection, $"ALTER TABLE {table} REPLICA IDENTITY FULL; INSERT INTO {table} VALUES (1, 'one')", token);
            Assert.IsTrue(await ScalarAsync<bool>(connection, $"SELECT datatype.writer_update_without_key('{table}'::regclass::oid)", token));
            Assert.AreEqual("uno", await ScalarAsync<string>(connection, $"SELECT name FROM {table}", token));
        });

    /// <summary>
    /// A unique index and a check constraint reject rows with their usual errors, and the session keeps working.
    /// </summary>
    /// <param name="id">The row's id.</param>
    /// <param name="sqlState">The expected SQLSTATE.</param>
    [TestMethod]
    [DataRow(1, "23505")]
    [DataRow(-1, "23514")]
    public async Task ConstraintsRejectRows(int id, string sqlState)
        => await WithTableAsync("id integer PRIMARY KEY CHECK (id > 0), name text", async (connection, table, token) =>
        {
            await ExecuteAsync(connection, $"INSERT INTO {table} VALUES (1, 'one')", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ExecuteAsync(connection, $"SELECT datatype.writer_insert('{table}'::regclass::oid, {id}, 'dup')", token));
            Assert.AreEqual(sqlState, error.SqlState);
            Assert.AreEqual(1L, await ScalarAsync<long>(connection, $"SELECT count(*) FROM {table}", token));
        });

    /// <summary>
    /// Ordinary row triggers fire for the writer's changes, before and after each row, unless
    /// <c>session_replication_role</c> is <c>replica</c>, when only triggers enabled for replicas fire.
    /// </summary>
    [TestMethod]
    public async Task RowTriggersFollowTheReplicationRole()
        => await WithTableAsync("id integer PRIMARY KEY, name text", async (connection, table, token) =>
        {
            string log = table + "_log";
            await ExecuteAsync(connection, $"""
                CREATE TABLE {log}(event text);
                CREATE FUNCTION {log}_before() RETURNS trigger LANGUAGE plpgsql AS
                    $$BEGIN INSERT INTO {log} VALUES ('before ' || NEW.id); NEW.name := upper(NEW.name); RETURN NEW; END$$;
                CREATE FUNCTION {log}_after() RETURNS trigger LANGUAGE plpgsql AS
                    $$BEGIN INSERT INTO {log} VALUES ('after ' || NEW.id); RETURN NULL; END$$;
                CREATE TRIGGER before_row BEFORE INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION {log}_before();
                CREATE TRIGGER after_row AFTER INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION {log}_after();
                """, token);
            await ExecuteAsync(connection, $"SELECT datatype.writer_insert('{table}'::regclass::oid, 1, 'origin')", token);
            await ExecuteAsync(connection, "SET session_replication_role = replica", token);
            await ExecuteAsync(connection, $"SELECT datatype.writer_insert('{table}'::regclass::oid, 2, 'replica')", token);
            await ExecuteAsync(connection, "RESET session_replication_role", token);
            Assert.AreEqual("before 1;after 1", await ScalarAsync<string>(connection, $"SELECT string_agg(event, ';' ORDER BY event DESC) FROM {log}", token));
            Assert.AreEqual("1:ORIGIN;2:replica", await ScalarAsync<string>(connection,
                $"SELECT string_agg(id || ':' || name, ';' ORDER BY id) FROM {table}", token));
            await ExecuteAsync(connection, $"DROP TABLE {log}; DROP FUNCTION {log}_before() CASCADE; DROP FUNCTION {log}_after() CASCADE;", token);
        });

    /// <summary>
    /// A change's <c>AFTER</c> triggers fire as soon as the change completes, as the built-in apply worker fires them,
    /// without waiting for the writer to be disposed.
    /// </summary>
    [TestMethod]
    public async Task AfterTriggersFireAsEachChangeCompletes()
        => await WithTableAsync("id integer PRIMARY KEY, name text", async (connection, table, token) =>
        {
            string log = table + "_log";
            await ExecuteAsync(connection, $"""
                CREATE TABLE {log}(id integer);
                CREATE FUNCTION {log}_after() RETURNS trigger LANGUAGE plpgsql AS
                    $$BEGIN INSERT INTO {log} VALUES (NEW.id); RETURN NULL; END$$;
                CREATE TRIGGER after_row AFTER INSERT ON {table} FOR EACH ROW EXECUTE FUNCTION {log}_after();
                """, token);
            Assert.AreEqual("1|2|2", await ScalarAsync<string>(connection,
                $"SELECT datatype.writer_after_each_change('{table}'::regclass::oid, '{log}'::regclass::oid)", token));
            await ExecuteAsync(connection, $"DROP TABLE {log}; DROP FUNCTION {log}_after() CASCADE;", token);
        });

    /// <summary>
    /// A writer checks the role's table privilege and, as the built-in apply worker does, refuses a table whose
    /// row-level security applies to the role, because the executor's replication helpers do not apply policies.
    /// </summary>
    [TestMethod]
    public async Task WritersCheckPrivilegesAndRowSecurity()
        => await WithTableAsync("id integer PRIMARY KEY, name text", async (connection, table, token) =>
        {
            await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token);
            string role = "writer_user_" + Guid.NewGuid().ToString("N");
            string relation = await ScalarAsync<string>(connection, $"SELECT '{table}'::regclass::oid::text", token);
            await ExecuteAsync(connection, $"CREATE ROLE {role}; GRANT USAGE ON SCHEMA datatype TO {role}; SET LOCAL ROLE {role}", token);
            await transaction.SaveAsync("denied", token);
            PostgresException denied = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ExecuteAsync(connection, $"SELECT datatype.writer_insert({relation}, 1, 'one')", token));
            Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, denied.SqlState);
            Assert.AreEqual($"permission denied for table {table["tests.".Length..]}", denied.MessageText);
            await transaction.RollbackAsync("denied", token);

            await ExecuteAsync(connection, $"RESET ROLE; GRANT INSERT ON {table} TO {role}; ALTER TABLE {table} ENABLE ROW LEVEL SECURITY; SET LOCAL ROLE {role}", token);
            await transaction.SaveAsync("secured", token);
            PostgresException secured = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ExecuteAsync(connection, $"SELECT datatype.writer_insert({relation}, 1, 'one')", token));
            Assert.AreEqual(PostgresErrorCodes.FeatureNotSupported, secured.SqlState);
            Assert.AreEqual($"user \"{role}\" cannot replicate into relation with row-level security enabled: \"{table["tests.".Length..]}\"",
                secured.MessageText);
            await transaction.RollbackAsync("secured", token);

            await ExecuteAsync(connection, $"RESET ROLE; ALTER TABLE {table} DISABLE ROW LEVEL SECURITY; SET LOCAL ROLE {role}", token);
            await ExecuteAsync(connection, $"SELECT datatype.writer_insert({relation}, 1, 'one')", token);
            await ExecuteAsync(connection, "RESET ROLE", token);
            Assert.AreEqual(1L, await ScalarAsync<long>(connection, $"SELECT count(*) FROM {table}", token));
            await transaction.RollbackAsync(token);
        });

    /// <summary>
    /// A transaction cannot commit while a writer is open; the transaction rolls back and the session keeps working.
    /// </summary>
    [TestMethod]
    public async Task OpenWritersBlockCommit()
        => await WithTableAsync("id integer PRIMARY KEY, name text", async (connection, table, token) =>
        {
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ExecuteAsync(connection, $"SELECT datatype.writer_left_open('{table}'::regclass::oid)", token));
            Assert.AreEqual("25000", error.SqlState);
            Assert.AreEqual("a relation writer is still open at commit", error.MessageText);
            Assert.AreEqual(0L, await ScalarAsync<long>(connection, $"SELECT count(*) FROM {table}", token));
            await ExecuteAsync(connection, $"SELECT datatype.writer_insert('{table}'::regclass::oid, 1, 'after')", token);
            Assert.AreEqual(1L, await ScalarAsync<long>(connection, $"SELECT count(*) FROM {table}", token));
        });

    /// <summary>
    /// Finding a row reports the transaction that wrote it; without commit timestamps the time is unknown and the
    /// origin is zero.
    /// </summary>
    [TestMethod]
    public async Task FoundRowsReportTheirTransaction()
        => await WithTableAsync("id integer PRIMARY KEY, name text", async (connection, table, token) =>
        {
            await ExecuteAsync(connection, $"INSERT INTO {table} VALUES (1, 'one')", token);
            string xmin = await ScalarAsync<string>(connection, $"SELECT xmin::text FROM {table}", token);
            Assert.AreEqual("0|False|" + xmin, await ScalarAsync<string>(connection,
                $"SELECT datatype.writer_find_commit('{table}'::regclass::oid, 1)", token));
        });

    /// <summary>
    /// A writer frees each change's memory before the next, as the apply worker resets its per-message context, so a
    /// long transaction's memory does not grow with its rows. <c>pg_backend_memory_contexts</c> starts in
    /// PostgreSQL 14.
    /// </summary>
    [TestMethod]
    public async Task WriterMemoryStaysBounded()
        => await WithTableAsync("id integer PRIMARY KEY, name text", async (connection, table, token) =>
        {
            if (await ScalarAsync<int>(connection, "SELECT current_setting('server_version_num')::integer", token) < 140000)
            {
                return;
            }

            string[] used = (await ScalarAsync<string>(connection, $"SELECT datatype.writer_memory('{table}'::regclass::oid, 50000)", token)).Split('|');
            long growth = long.Parse(used[1], CultureInfo.InvariantCulture) - long.Parse(used[0], CultureInfo.InvariantCulture);
            Assert.IsLessThan(1024 * 1024, growth, $"Memory grew by {growth} bytes over 49,000 rows.");
            Assert.AreEqual(50000L, await ScalarAsync<long>(connection, $"SELECT count(*) FROM {table} WHERE name = 'row ' || id", token));
        });

    /// <summary>
    /// Writers for one table share a descriptor until the table changes, whether this transaction or another session
    /// changes it.
    /// </summary>
    [TestMethod]
    public async Task WritersSeeTableChanges()
        => await WithTableAsync("id integer PRIMARY KEY, name text", async (connection, table, token) =>
        {
            Assert.AreEqual("True|False|id,name,extra", await ScalarAsync<string>(connection,
                $"SELECT datatype.writer_descriptor_after_alter('{table}'::regclass::oid, 'ALTER TABLE {table} ADD COLUMN extra integer')", token));
            Assert.AreEqual("1:one:5", await ScalarAsync<string>(connection, $"SELECT id || ':' || name || ':' || extra FROM {table}", token));
            await using NpgsqlConnection other = await PostgresFixture.Cluster.OpenConnectionAsync(token);
            await ExecuteAsync(other, $"ALTER TABLE {table} RENAME COLUMN name TO label; ALTER TABLE {table} DROP COLUMN extra", token);
            Assert.AreEqual("id,label", await ScalarAsync<string>(connection, $"SELECT datatype.writer_columns('{table}'::regclass::oid)", token));
        });

    /// <summary>
    /// Creates a table with the given columns, runs the test, and drops the table.
    /// </summary>
    private async Task WithTableAsync(string columns, Func<NpgsqlConnection, string, CancellationToken, Task> test)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        string table = "tests.writer_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, $"CREATE TABLE {table}({columns})", token);
        try
        {
            await test(connection, table, token);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE {table}", token);
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
