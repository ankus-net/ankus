using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Creates, uses and drops replication origins through <c>PgReplicationOrigin</c> and checks them against
/// PostgreSQL's own <c>pg_replication_origin</c> functions and views, on a cluster that tracks commit timestamps.
/// </summary>
/// <param name="context">The current cancellation and diagnostic context.</param>
[TestClass]
public sealed class ReplicationOriginTests(TestContext context)
{
    private const string CommitTime = "2026-01-02 03:04:05.123456+00";

    /// <summary>
    /// A transaction applied on a session origin's behalf commits with that origin and the source's commit time, and
    /// advances the origin. One session at a time may use an origin, and errors leave each session working.
    /// </summary>
    [TestMethod]
    public async Task SessionOriginsMarkAppliedCommits()
        => await RunAsync(async (cluster, connection, token) =>
        {
            await ExecuteAsync(connection, "CREATE TABLE applied(id integer PRIMARY KEY, name text)", token);
            int origin = await ScalarAsync<int>(connection, "SELECT datatype.origin_create('ankus_source')", token);
            Assert.AreEqual(origin, await ScalarAsync<int>(connection,
                "SELECT roident::integer FROM pg_replication_origin WHERE roname = 'ankus_source'", token));
            Assert.AreEqual(origin, await ScalarAsync<int>(connection, "SELECT datatype.origin_find('ankus_source')", token));
            Assert.IsTrue(await ScalarAsync<bool>(connection, "SELECT datatype.origin_find('ankus_missing') IS NULL", token));
            await ExpectErrorAsync(connection, "SELECT datatype.origin_apply_without_session()", "55000", "no replication origin is configured", token);

            await ExecuteAsync(connection, $"SELECT datatype.origin_setup_session({origin})", token);
            Assert.AreEqual(origin, await ScalarAsync<int>(connection, "SELECT datatype.origin_session()", token));
            Assert.IsTrue(await ScalarAsync<bool>(connection, "SELECT pg_replication_origin_session_is_setup()", token));
            await ExpectErrorAsync(connection, $"SELECT datatype.origin_setup_session({origin})", "55000",
                "cannot setup replication origin when one is already setup", token);
            await ExecuteAsync(connection, $"SELECT datatype.origin_apply('applied'::regclass::oid, '1/2A3B4C5D', '{CommitTime}', 1)", token);
            Assert.AreEqual("1/2A3B4C5D", await ScalarAsync<string>(connection, "SELECT pg_replication_origin_progress('ankus_source', true)::text", token));
            Assert.AreEqual("1/2A3B4C5D", await ScalarAsync<string>(connection, "SELECT datatype.origin_progress(0)", token));
            Assert.AreEqual("1/2A3B4C5D", await ScalarAsync<string>(connection, $"SELECT datatype.origin_progress({origin})", token));
            Assert.IsTrue(await ScalarAsync<bool>(connection, $"SELECT pg_xact_commit_timestamp(xmin) = '{CommitTime}' FROM applied", token));
            Assert.IsTrue(await ScalarAsync<bool>(connection,
                $"SELECT datatype.origin_commit_time('applied'::regclass::oid, 1) = '{CommitTime}'", token));
            string xmin = await ScalarAsync<string>(connection, "SELECT xmin::text FROM applied", token);
            Assert.AreEqual($"{origin}|True|{xmin}", await ScalarAsync<string>(connection,
                "SELECT datatype.writer_find_commit('applied'::regclass::oid, 1)", token));

            await using NpgsqlConnection other = await cluster.OpenConnectionAsync(token);
            await ExpectErrorAsync(other, $"SELECT datatype.origin_setup_session({origin})", "55006", null, token);
            await ExpectErrorAsync(other, "SELECT datatype.origin_drop('ankus_source', false)", "55006", null, token);
            await ExecuteAsync(connection, "SELECT datatype.origin_reset_session()", token);
            Assert.AreEqual(0, await ScalarAsync<int>(connection, "SELECT datatype.origin_session()", token));
            Assert.IsFalse(await ScalarAsync<bool>(connection, "SELECT pg_replication_origin_session_is_setup()", token));
            await ExecuteAsync(other, $"SELECT datatype.origin_setup_session({origin})", token);
            Assert.IsTrue(await ScalarAsync<bool>(other, "SELECT pg_replication_origin_session_is_setup()", token));
            await ExecuteAsync(other, "SELECT datatype.origin_reset_session()", token);
            await ExecuteAsync(connection, "SELECT datatype.origin_drop('ankus_source', false)", token);
        });

    /// <summary>
    /// Advancing moves an origin forward, and backward only when asked; names are checked as PostgreSQL's SQL functions
    /// check them; and dropping removes the origin.
    /// </summary>
    [TestMethod]
    public async Task OriginsAdvanceAndDrop()
        => await RunAsync(async (_, connection, token) =>
        {
            int origin = await ScalarAsync<int>(connection, "SELECT datatype.origin_create('ankus_advanced')", token);
            Assert.AreEqual("0/0", await ScalarAsync<string>(connection, $"SELECT datatype.origin_progress({origin})", token));
            await ExecuteAsync(connection, $"SELECT datatype.origin_advance({origin}, '0/2000', false)", token);
            Assert.AreEqual("0/2000", await ScalarAsync<string>(connection, $"SELECT datatype.origin_progress({origin})", token));
            await ExecuteAsync(connection, $"SELECT datatype.origin_advance({origin}, '0/1000', false)", token);
            Assert.AreEqual("0/2000", await ScalarAsync<string>(connection, $"SELECT datatype.origin_progress({origin})", token));
            await ExecuteAsync(connection, $"SELECT datatype.origin_advance({origin}, '0/1000', true)", token);
            // PostgreSQL 19 pads the text form's low half to eight digits, so the positions are compared as values.
            Assert.IsTrue(await ScalarAsync<bool>(connection,
                "SELECT remote_lsn = '0/1000'::pg_lsn FROM pg_replication_origin_status WHERE external_id = 'ankus_advanced'", token));
            await ExpectErrorAsync(connection, "SELECT datatype.origin_advance(4242, '0/1000', false)", "42704",
                "replication origin with ID 4242 does not exist", token);
            await ExpectErrorAsync(connection, "SELECT datatype.origin_progress(0)", "55000", "no replication origin is configured", token);
            await ExpectErrorAsync(connection, "SELECT datatype.origin_create('pg_ankus')", "42939",
                "replication origin name \"pg_ankus\" is reserved", token);
            await ExpectErrorAsync(connection, "SELECT datatype.origin_create('ankus_advanced')", "23505", null, token);

            await ExecuteAsync(connection, "SELECT datatype.origin_drop('ankus_advanced', false)", token);
            Assert.IsTrue(await ScalarAsync<bool>(connection, "SELECT datatype.origin_find('ankus_advanced') IS NULL", token));
            Assert.AreEqual(0L, await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_replication_origin_status", token));
            await ExecuteAsync(connection, "SELECT datatype.origin_drop('ankus_advanced', true)", token);
            await ExpectErrorAsync(connection, "SELECT datatype.origin_drop('ankus_advanced', false)", "42704",
                "replication origin \"ankus_advanced\" does not exist", token);
        });

    /// <summary>
    /// Starts a cluster that tracks commit timestamps, installs the test extension and runs the test in its default
    /// database.
    /// </summary>
    private async Task RunAsync(Func<PostgresTestCluster, NpgsqlConnection, CancellationToken, Task> test)
    {
        CancellationToken token = context.CancellationToken;
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(token);
        PostgresTestClusterOptions options = new()
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            PostgreSqlConfiguration = [.. defaults.PostgreSqlConfiguration, "track_commit_timestamp = on"],
        };
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await ExecuteAsync(connection, "CREATE SCHEMA datatype; CREATE EXTENSION ankus_test WITH SCHEMA datatype;", token);
        await test(cluster, connection, token);
    }

    /// <summary>
    /// Runs a statement that must fail with the given SQLSTATE and, when given, message, then checks the session still
    /// works.
    /// </summary>
    private static async Task ExpectErrorAsync(NpgsqlConnection connection, string sql, string sqlState, string? message, CancellationToken token)
    {
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(connection, sql, token));
        Assert.AreEqual(sqlState, error.SqlState, error.MessageText);
        if (message is not null)
        {
            Assert.AreEqual(message, error.MessageText);
        }

        Assert.AreEqual(1, await ScalarAsync<int>(connection, "SELECT 1", token));
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
