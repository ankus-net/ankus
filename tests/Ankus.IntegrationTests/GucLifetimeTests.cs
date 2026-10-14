using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Measures retained native configuration allocations and the lifetime of copied managed payloads.
/// </summary>
/// <param name="context">The test cancellation and diagnostic context.</param>
[TestClass]
[DoNotParallelize]
public sealed class GucLifetimeTests(TestContext context)
{
    private const int PayloadLength = 65536;
    private const int MeasuredCycles = 64;

    /// <summary>
    /// Native current and stacked payloads survive collection of every managed hook snapshot.
    /// </summary>
    [TestMethod]
    public async Task ManagedSnapshotsCollectWhileNativeStackRetainsBytes()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(connection, "LOAD 'Ankus.TestExtension'; SET ankus_lifetime.text = 'b'");
        await AssertCurrentAsync(connection, 'b');
        await AssertCollectedAsync(connection);
        await ExecuteAsync(connection, "BEGIN; SET ankus_lifetime.text = 'c'; SET LOCAL ankus_lifetime.text = 'd'; SAVEPOINT nested; SET LOCAL ankus_lifetime.text = 'e'");
        await AssertCurrentAsync(connection, 'e');
        await AssertCollectedAsync(connection);
        await ExecuteAsync(connection, "ROLLBACK TO nested");
        await AssertCurrentAsync(connection, 'd');
        await AssertCollectedAsync(connection);
        await ExecuteAsync(connection, "COMMIT");
        await AssertCurrentAsync(connection, 'c');
        await AssertCollectedAsync(connection);
        await ExecuteAsync(connection, "BEGIN; SET ankus_lifetime.text = 'f'; SET LOCAL ankus_lifetime.text = 'g'; ROLLBACK");
        await AssertCurrentAsync(connection, 'c');
        await AssertCollectedAsync(connection);
        await ExecuteAsync(connection, "RESET ankus_lifetime.text");
        await AssertCurrentAsync(connection, 'a');
        await AssertCollectedAsync(connection);
    }

    /// <summary>
    /// Repeated successful, validation-only, rejected, and encoding-failure calls leave bounded native storage.
    /// </summary>
    /// <param name="latin1">Whether to exercise native conversion and its failure paths in a LATIN1 database.</param>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task WarmedNativeAllocationsStabilizeAcrossStateAndErrorPaths(bool latin1)
    {
        string database = "guc_lifetime_" + Guid.NewGuid().ToString("N");
        await using NpgsqlConnection administrator = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(administrator, $"CREATE DATABASE {database} TEMPLATE template0 ENCODING '{(latin1 ? "LATIN1" : "UTF8")}' LC_COLLATE 'C' LC_CTYPE 'C'");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Database = database, Pooling = false };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(context.CancellationToken);
            await ExecuteAsync(connection, "CREATE SCHEMA datatype; CREATE EXTENSION ankus_test WITH SCHEMA datatype; LOAD 'Ankus.TestExtension'; CREATE FUNCTION pg_temp.guc_lifetime_target() RETURNS integer LANGUAGE sql AS 'SELECT 1'");
            int notices = 0;
            int detailLength = 0;
            connection.Notice += (_, args) =>
            {
                if (args.Notice.MessageText == "Lifetime log.")
                {
                    notices++;
                    detailLength = args.Notice.Detail?.Length ?? 0;
                }
            };

            long[] witness = Assert.IsInstanceOfType<long[]>(await ScalarAsync(connection, "SELECT datatype.guc_lifetime_malloc_witness(4194304)"));
            Assert.HasCount(3, witness);
            Assert.IsGreaterThanOrEqualTo(4194304L, witness[1] - witness[0]);
            Assert.IsLessThanOrEqualTo(PayloadLength, witness[2] - witness[0]);

            await RunCyclesAsync(connection, latin1, 16);
            await AssertCollectedAsync(connection);
            (long? GucBytes, _, long MallocBytes) = await AllocationsAsync(connection);
            long[] callsBefore = await CallsAsync(connection);
            int noticesBefore = notices;
            for (int batch = 0; batch < 3; batch++)
            {
                await RunCyclesAsync(connection, latin1, MeasuredCycles);
                await AssertCollectedAsync(connection);
                (long? GucBytes, long TemporaryContexts, long MallocBytes) current = await AllocationsAsync(connection);
                context.WriteLine($"LATIN1={latin1}, batch={batch}: GUC context bytes {GucBytes} -> {current.GucBytes}, temporary contexts {current.TemporaryContexts}, glibc bytes {MallocBytes} -> {current.MallocBytes}.");
                if (GucBytes.HasValue)
                {
                    Assert.AreEqual(GucBytes, current.GucBytes, "Identical restored GUC state must retain identical native allocations.");
                }

                Assert.AreEqual(0L, current.TemporaryContexts, "Hook, logging, and read contexts must not survive completed operations.");
                long maximumGrowth = (long)MeasuredCycles * PayloadLength / 8;
                Assert.IsLessThanOrEqualTo(maximumGrowth, current.MallocBytes - MallocBytes,
                    "Allocator noise allowance is one eighth of a batch with one leaked payload per cycle.");
            }

            long[] callsAfter = await CallsAsync(connection);
            Assert.IsGreaterThan(callsBefore[0], callsAfter[0]);
            Assert.IsGreaterThan(callsBefore[1], callsAfter[1]);
            Assert.IsGreaterThan(callsBefore[2], callsAfter[2]);
            Assert.AreEqual(3L * MeasuredCycles, callsAfter[3] - callsBefore[3]);
            Assert.AreEqual(3 * MeasuredCycles, notices - noticesBefore);
            Assert.AreEqual(PayloadLength, detailLength);
            Assert.AreEqual(42, await ScalarAsync(connection, "SELECT 42"));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", administrator);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Executes complete native state and diagnostic cycles with identical final retained state.
    /// </summary>
    private async Task RunCyclesAsync(NpgsqlConnection connection, bool latin1, int count)
    {
        for (int iteration = 0; iteration < count; iteration++)
        {
            await ExecuteAsync(connection, "SET ankus_lifetime.text = 'b'; BEGIN; SET ankus_lifetime.text = 'c'; SET LOCAL ankus_lifetime.text = 'd'; SAVEPOINT nested; SET LOCAL ankus_lifetime.text = 'e'; ROLLBACK TO nested; RELEASE nested; COMMIT");
            await AssertCurrentAsync(connection, 'c');
            await ExecuteAsync(connection, "BEGIN; SET ankus_lifetime.text = 'f'; SET LOCAL ankus_lifetime.text = 'g'; ROLLBACK; RESET ankus_lifetime.text");
            await AssertCurrentAsync(connection, 'a');
            long[] beforeValidation = await CallsAsync(connection);
            await ExecuteAsync(connection, "ALTER FUNCTION pg_temp.guc_lifetime_target() SET ankus_lifetime.text = 'v'; ALTER FUNCTION pg_temp.guc_lifetime_target() RESET ankus_lifetime.text");
            long[] afterValidation = await CallsAsync(connection);
            Assert.AreEqual(beforeValidation[0] + 1, afterValidation[0]);
            Assert.AreEqual(beforeValidation[1], afterValidation[1], "Validation must not assign the provisional checked value or extra.");
            Assert.AreEqual(beforeValidation[3] + 1, afterValidation[3]);
            await AssertFailureAsync(connection, 1, "22003");
            await AssertFailureAsync(connection, 2, "38000");
            if (latin1)
            {
                await AssertFailureAsync(connection, 3, "22P05");
                await AssertFailureAsync(connection, 4, "22P05");
                await AssertFailureAsync(connection, 5, "22P05");
                await AssertFailureAsync(connection, 6, "22P05");
            }

            await ExecuteAsync(connection, "SET ankus_lifetime.mode = '7'; SET ankus_lifetime.text = 'é'; SET ankus_lifetime.mode = '0'");
            await AssertCurrentAsync(connection, 'é');
            await ExecuteAsync(connection, "RESET ankus_lifetime.text");
            await AssertCurrentAsync(connection, 'a');
        }
    }

    /// <summary>
    /// Verifies an owned failure leaves native state intact and the same session available.
    /// </summary>
    private async Task AssertFailureAsync(NpgsqlConnection connection, int mode, string sqlState)
    {
        await ExecuteAsync(connection, $"SET ankus_lifetime.mode = '{mode}'");
        string sql = mode == 4 ? "SHOW ankus_lifetime.text" : "SET ankus_lifetime.text = 'z'";
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(connection, sql));
        Assert.AreEqual(sqlState, error.SqlState);
        if (mode == 1)
        {
            Assert.AreEqual("Lifetime rejected.", error.MessageText);
            Assert.AreEqual(new string('x', PayloadLength), error.Detail);
            Assert.AreEqual("Use an accepted value.", error.Hint);
        }
        else if (mode == 2)
        {
            Assert.AreEqual("Lifetime check exception.", error.MessageText);
        }
        else
        {
            Assert.Contains("UTF8", error.MessageText);
            Assert.Contains("LATIN1", error.MessageText);
        }

        await ExecuteAsync(connection, "SET ankus_lifetime.mode = '0'");
        await AssertCurrentAsync(connection, 'a');
    }

    /// <summary>
    /// Compares the entire show result with an independently constructed SQL value and checks typed storage.
    /// </summary>
    private async Task AssertCurrentAsync(NpgsqlConnection connection, char value)
    {
        Assert.IsTrue(Assert.IsInstanceOfType<bool>(await ScalarAsync(connection, $"SELECT current_setting('ankus_lifetime.text') = repeat('{value}', {PayloadLength})")));
        Assert.AreEqual($"{PayloadLength}:{value}:{value}", await ScalarAsync(connection, "SELECT datatype.guc_lifetime_value()"));
    }

    /// <summary>
    /// Requires every snapshot category to have been observed and every original object to be collectible.
    /// </summary>
    private async Task AssertCollectedAsync(NpgsqlConnection connection)
    {
        int[] masks = Assert.IsInstanceOfType<int[]>(await ScalarAsync(connection, "SELECT datatype.guc_lifetime_collect()"));
        Assert.AreSequenceEqual<int>([255, 0], masks);
    }

    /// <summary>
    /// Reads native GUC context bytes where available, surviving temporary contexts, and libc allocated bytes.
    /// </summary>
    private async Task<(long? GucBytes, long TemporaryContexts, long MallocBytes)> AllocationsAsync(NpgsqlConnection connection)
    {
        await using var command = new NpgsqlCommand("""
            SELECT
                (SELECT used_bytes FROM ankus_test_memory.contexts WHERE name = 'GUCMemoryContext'),
                (SELECT count(*) FROM ankus_test_memory.contexts WHERE name LIKE 'Ankus configuration %'),
                datatype.guc_lifetime_malloc_bytes()
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
        Assert.IsTrue(await reader.ReadAsync(context.CancellationToken));
        long? gucBytes = reader.IsDBNull(0) ? null : reader.GetInt64(0);
        if (PostgresFixture.Cluster.Installation.Version.Major >= 16)
        {
            Assert.IsNotNull(gucBytes, "PostgreSQL 16 and later retain GUC storage in GUCMemoryContext.");
        }
        else
        {
            Assert.IsNull(gucBytes, "Older servers allocate GUC storage directly with malloc; libc accounting must observe it.");
        }

        return (gucBytes, reader.GetInt64(1), reader.GetInt64(2));
    }

    /// <summary>
    /// Reads independently copied bounded hook counters.
    /// </summary>
    private async Task<long[]> CallsAsync(NpgsqlConnection connection) =>
        Assert.IsInstanceOfType<long[]>(await ScalarAsync(connection, "SELECT datatype.guc_lifetime_calls()"));

    /// <summary>
    /// Executes one scalar command with the test cancellation token.
    /// </summary>
    private async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(context.CancellationToken);
    }

    /// <summary>
    /// Executes one command with the test cancellation token.
    /// </summary>
    private async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }
}
