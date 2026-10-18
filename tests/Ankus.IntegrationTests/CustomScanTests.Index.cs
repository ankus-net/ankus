using System.Text.Json;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Exercises native index capabilities through custom plans and the real PostgreSQL executor.
/// </summary>
public sealed partial class CustomScanTests
{
    /// <summary>
    /// Duplicate merge groups invoke the custom mark/restore methods and preserve every projected pair.
    /// </summary>
    /// <param name="indexOnly">Whether the covering indexes supply all selected values without heap fetches.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TraceScanMarksAndRestoresIndexChildren(bool indexOnly)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenAsync(token);
        await using var command = new NpgsqlCommand("""
            CREATE TEMP TABLE trace_mark_left(key integer, id integer, label text);
            CREATE TEMP TABLE trace_mark_right(key integer, id integer, label text);
            INSERT INTO trace_mark_left VALUES (1,100,'left one'),(1,101,NULL),(2,200,'left two'),(3,300,'unmatched');
            INSERT INTO trace_mark_right VALUES (1,10,'right'),(1,11,NULL),(2,20,'two'),(2,21,'café'),(4,40,'unmatched'),(NULL,99,'null key');
            CREATE INDEX ON trace_mark_left(key) INCLUDE (id,label);
            CREATE INDEX ON trace_mark_right(key) INCLUDE (id,label);
            """, connection);
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "VACUUM (ANALYZE) trace_mark_left";
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "VACUUM (ANALYZE) trace_mark_right";
        await command.ExecuteNonQueryAsync(token);
        await SelectIndexPathsAsync(connection, indexOnly, token);
        command.CommandText = "SET enable_hashjoin = off; SET enable_nestloop = off; SET enable_material = off";
        await command.ExecuteNonQueryAsync(token);
        const string Query = """
            SELECT l.id, r.id, l.label || '/' || r.label
            FROM trace_mark_left l JOIN trace_mark_right r ON l.key = r.key
            ORDER BY l.id,r.id
            """;
        using (JsonDocument plan = await ExplainAsync(connection, Query, true, token))
        {
            context.WriteLine(plan.RootElement.GetRawText());
            JsonElement join = Assert.ContainsSingle(PlanNodes(plan.RootElement[0].GetProperty("Plan"), "Merge Join"));
            JsonElement inner = join.GetProperty("Plans")[1];
            Assert.IsEmpty(PlanNodes(inner, "Materialize"));
            Assert.IsEmpty(PlanNodes(inner, "Sort"));
            JsonElement scan = Assert.ContainsSingle(TraceNodes(inner));
            Assert.IsGreaterThan(0L, scan.GetProperty("Trace Marks").GetInt64());
            Assert.IsGreaterThan(0L, scan.GetProperty("Trace Restores").GetInt64());
            JsonElement[] scans = [.. TraceNodes(join)];
            Assert.HasCount(2, scans);
            foreach (JsonElement child in scans.Select(static traced => traced.GetProperty("Plans")[0]))
            {
                Assert.AreEqual(indexOnly ? "Index Only Scan" : "Index Scan", child.GetProperty("Node Type").GetString());
                if (indexOnly)
                {
                    Assert.AreEqual(0L, child.GetProperty("Heap Fetches").GetInt64());
                }
            }
        }

        command.CommandText = Query;
        var rows = new List<(int Left, int Right, string? Label)>();
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        Assert.AreSequenceEqual<(int, int, string?)>(
            [(100, 10, "left one/right"), (100, 11, null), (101, 10, null), (101, 11, null),
             (200, 20, "left two/two"), (200, 21, "left two/café")], rows);
        long[] counts = await CountsAsync(connection, token);
        Assert.AreEqual(4L, counts[1]);
        Assert.AreEqual(4L, counts[4]);
        Assert.AreEqual(4L, counts[7]);
        Assert.AreEqual(counts[3], counts[6]);
    }

    /// <summary>
    /// An ordered custom index cursor forwards real direction changes without an intervening sorting node.
    /// </summary>
    /// <param name="indexOnly">Whether the selected index child reads covered values only.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TraceScanReadsIndexChildrenBackward(bool indexOnly)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenAsync(token);
        await CreateIndexInputAsync(connection, indexOnly, token);
        const string Query = "SELECT value,label FROM trace_input ORDER BY value";
        using (JsonDocument plan = await ExplainAsync(connection, Query, false, token))
        {
            JsonElement root = plan.RootElement[0].GetProperty("Plan");
            Assert.IsEmpty(PlanNodes(root, "Sort"));
            JsonElement scan = Assert.ContainsSingle(TraceNodes(root));
            Assert.AreEqual(indexOnly ? "Index Only Scan" : "Index Scan", scan.GetProperty("Plans")[0].GetProperty("Node Type").GetString());
        }

        await ResetAsync(connection, token);
        await using var command = new NpgsqlCommand("BEGIN; DECLARE traced SCROLL CURSOR FOR " + Query, connection);
        await command.ExecuteNonQueryAsync(token);
        Assert.AreEqual<(int, string?)>((int.MinValue, "minimum"), await ReadIndexCursorAsync(connection, "FORWARD", token));
        Assert.AreEqual<(int, string?)>((1, null), await ReadIndexCursorAsync(connection, "FORWARD", token));
        Assert.AreEqual<(int, string?)>((int.MinValue, "minimum"), await ReadIndexCursorAsync(connection, "BACKWARD", token));
        Assert.AreEqual<(int, string?)>((1, null), await ReadIndexCursorAsync(connection, "FORWARD", token));
        command.CommandText = "CLOSE traced; COMMIT";
        await command.ExecuteNonQueryAsync(token);
        Assert.AreSequenceEqual<long>([1, 1, 1, 4, 1, 0, 4, 1], await CountsAsync(connection, token));
    }

    /// <summary>
    /// Native index conditions see each new outer parameter when the custom node rescans its child.
    /// </summary>
    /// <param name="indexOnly">Whether the parameterized path uses a covering index-only child.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TraceScanRescansParameterizedIndexChildren(bool indexOnly)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenAsync(token);
        await CreateIndexInputAsync(connection, indexOnly, token);
        await using var command = new NpgsqlCommand("SET enable_material = off; " + DisableMemoizeSql(), connection);
        await command.ExecuteNonQueryAsync(token);
        const string Query = """
            SELECT threshold,value,label FROM (VALUES (0),(1),(2)) AS limits(threshold)
            CROSS JOIN LATERAL (SELECT value,label FROM trace_input WHERE value > threshold OFFSET 0) AS matched
            ORDER BY threshold,value
            """;
        using (JsonDocument plan = await ExplainAsync(connection, Query, true, token))
        {
            context.WriteLine(plan.RootElement.GetRawText());
            JsonElement scan = Assert.ContainsSingle(TraceNodes(plan.RootElement[0].GetProperty("Plan")));
            JsonElement child = scan.GetProperty("Plans")[0];
            Assert.AreEqual(indexOnly ? "Index Only Scan" : "Index Scan", child.GetProperty("Node Type").GetString());
            Assert.IsTrue(child.TryGetProperty("Index Cond", out _));
            Assert.AreEqual(3L, scan.GetProperty("Actual Loops").GetInt64());
            Assert.AreEqual(3L, scan.GetProperty("Trace Rescans").GetInt64());
            Assert.AreEqual(6L, scan.GetProperty("Trace Rows").GetInt64());
        }

        command.CommandText = Query;
        var rows = new List<(int Threshold, int Value, string? Label)>();
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                rows.Add((reader.GetInt32(0), reader.GetInt32(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        Assert.AreSequenceEqual<(int, int, string?)>(
            [(0, 1, null), (0, 2, "café"), (0, int.MaxValue, "maximum"),
             (1, 2, "café"), (1, int.MaxValue, "maximum"), (2, int.MaxValue, "maximum")], rows);
        long[] counts = await CountsAsync(connection, token);
        Assert.AreEqual(6L, counts[5]);
        Assert.AreEqual(2L, counts[7]);
        Assert.AreEqual(counts[3], counts[6]);
    }

    /// <summary>
    /// Managed and native index qualifications unwind and release ownership before an exact healthy same-session result.
    /// </summary>
    /// <param name="indexOnly">Whether the failing scan uses an index-only child.</param>
    /// <param name="managed">Whether managed code supplies the deliberate error.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task TraceScanIndexErrorsUnwindAndRecover(bool indexOnly, bool managed)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenAsync(token);
        await CreateIndexInputAsync(connection, indexOnly, token);
        int backend = connection.ProcessID;
        string predicate = managed ? "datatype.custom_scan_accept(value)" : "100::bigint / (value::bigint - 2) IS NOT NULL";
        string query = $"SELECT value,label FROM trace_input WHERE value > 0 AND {predicate} ORDER BY value";
        using (JsonDocument plan = await ExplainAsync(connection, query, false, token))
        {
            JsonElement scan = Assert.ContainsSingle(TraceNodes(plan.RootElement[0].GetProperty("Plan")));
            Assert.AreEqual(indexOnly ? "Index Only Scan" : "Index Scan", scan.GetProperty("Plans")[0].GetProperty("Node Type").GetString());
        }

        await ResetAsync(connection, token);
        await using var command = new NpgsqlCommand(query, connection);
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteNonQueryAsync(token));
        Assert.AreEqual(managed ? "P7521" : "22012", error.SqlState);
        Assert.AreEqual(managed ? "managed custom scan child failure" : "division by zero", error.MessageText);
        Assert.AreEqual(managed ? "trace child detail" : null, error.Detail);
        Assert.AreEqual(managed ? "trace child hint" : null, error.Hint);
        Assert.AreSequenceEqual<long>([1, 1, 1, 2, 0, 0, 2, 1], await CountsAsync(connection, token));
        await ResetAsync(connection, token);
        command.CommandText = "SELECT pg_backend_pid(),value,label FROM trace_input WHERE value = 2";
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
        {
            Assert.IsTrue(await reader.ReadAsync(token));
            Assert.AreEqual(backend, reader.GetInt32(0));
            Assert.AreEqual(2, reader.GetInt32(1));
            Assert.AreEqual("café", reader.GetString(2));
            Assert.IsFalse(await reader.ReadAsync(token));
        }

        Assert.AreSequenceEqual<long>([1, 1, 1, 2, 1, 0, 2, 1], await CountsAsync(connection, token));
    }

    /// <summary>
    /// Parallel index children retain their real worker distribution and provider-owned shared observations.
    /// </summary>
    /// <param name="indexOnly">Whether the parallel child can return covered values without a heap fetch.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TraceScanRunsParallelIndexChildren(bool indexOnly)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenAsync(token);
        string table = "tests.trace_index_" + Guid.NewGuid().ToString("N");
        await using var command = new NpgsqlCommand($"""
            CREATE TABLE {table} AS SELECT generate_series(1,30000) AS value;
            CREATE INDEX ON {table}(value);
            ALTER TABLE {table} SET (parallel_workers = 2);
            """, connection);
        try
        {
            await command.ExecuteNonQueryAsync(token);
            await VacuumUntilAllVisibleAsync(connection, table, token);
            await SelectIndexPathsAsync(connection, indexOnly, token);
            command.CommandText = """
                SET max_parallel_workers_per_gather = 2;
                SET min_parallel_table_scan_size = 0;
                SET min_parallel_index_scan_size = 0;
                SET parallel_setup_cost = 0;
                SET parallel_tuple_cost = 0;
                SET parallel_leader_participation = off;
                """;
            await command.ExecuteNonQueryAsync(token);
            string query = $"SELECT datatype.custom_scan_process(value),value FROM {table} WHERE value > 0";
            using (JsonDocument plan = await ExplainAsync(connection, query, true, token))
            {
                context.WriteLine(plan.RootElement.GetRawText());
                JsonElement root = plan.RootElement[0].GetProperty("Plan");
                int launched = WorkersLaunched(root);
                Assert.IsGreaterThan(0, launched);
                JsonElement scan = Assert.ContainsSingle(TraceNodes(root));
                JsonElement child = scan.GetProperty("Plans")[0];
                Assert.AreEqual(indexOnly ? "Index Only Scan" : "Index Scan", child.GetProperty("Node Type").GetString());
                Assert.IsTrue(scan.GetProperty("Parallel Aware").GetBoolean());
                Assert.IsTrue(child.GetProperty("Parallel Aware").GetBoolean());
                Assert.AreEqual(30000L, scan.GetProperty("Shared Trace Rows").GetInt64());
                Assert.AreEqual(30000L + launched, scan.GetProperty("Shared Trace Calls").GetInt64());
                Assert.AreEqual(launched, scan.GetProperty("Trace Worker Attachments").GetInt64());
                Assert.AreEqual(launched, scan.GetProperty("Trace Worker Shutdowns").GetInt64());
                Assert.AreEqual(1L, scan.GetProperty("Trace DSM Generation").GetInt64());
                Assert.AreEqual(0L, scan.GetProperty("Trace Marks").GetInt64());
                Assert.AreEqual(0L, scan.GetProperty("Trace Restores").GetInt64());
                if (indexOnly)
                {
                    Assert.AreEqual(0L, child.GetProperty("Heap Fetches").GetInt64());
                }
            }

            command.CommandText = query;
            var seen = new HashSet<int>();
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
            {
                while (await reader.ReadAsync(token))
                {
                    Assert.AreNotEqual(connection.ProcessID, reader.GetInt32(0));
                    Assert.IsTrue(seen.Add(reader.GetInt32(1)));
                }
            }

            Assert.AreSequenceEqual(Enumerable.Range(1, 30000), seen.Order());
        }
        finally
        {
            command.CommandText = $"DROP TABLE IF EXISTS {table}";
            await command.ExecuteNonQueryAsync(token);
        }
    }

    /// <summary>
    /// Makes every selected value available from the index and establishes heap visibility before execution.
    /// </summary>
    private static async Task CreateIndexInputAsync(NpgsqlConnection connection, bool indexOnly, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("CREATE INDEX ON trace_input(value) INCLUDE (label)", connection);
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "VACUUM (ANALYZE) trace_input";
        await command.ExecuteNonQueryAsync(token);
        await SelectIndexPathsAsync(connection, indexOnly, token);
    }

    /// <summary>
    /// Vacuums a table until every page is all-visible, so an index-only scan needs no heap fetch.
    /// </summary>
    /// <remarks>
    /// Parallel workers cannot read a temporary table, so this one is shared, and VACUUM marks a page all-visible only
    /// once no transaction in the database can still see it otherwise. A snapshot held by a test running beside this one
    /// can delay that.
    /// </remarks>
    private static async Task VacuumUntilAllVisibleAsync(NpgsqlConnection connection, string table, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"VACUUM (ANALYZE) {table}", connection);
        await using var check = new NpgsqlCommand($"SELECT relallvisible >= relpages FROM pg_class WHERE oid = '{table}'::regclass",
            connection);
        DateTime deadline = DateTime.UtcNow + TimeSpan.FromMinutes(2);
        while (true)
        {
            await command.ExecuteNonQueryAsync(token);
            if (await check.ExecuteScalarAsync(token) is true)
            {
                return;
            }

            Assert.IsLessThan(deadline, DateTime.UtcNow, $"VACUUM did not make every page of {table} all-visible.");
            await Task.Delay(250, token);
        }
    }

    /// <summary>
    /// Selects the real access-path family whose optional capabilities the test must exercise.
    /// </summary>
    private static async Task SelectIndexPathsAsync(NpgsqlConnection connection, bool indexOnly, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"""
            SET enable_seqscan = off;
            SET enable_bitmapscan = off;
            SET enable_indexonlyscan = {(indexOnly ? "on" : "off")};
            """, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Reads exactly one cursor row and releases its reader before the next direction change.
    /// </summary>
    private static async Task<(int Value, string? Label)> ReadIndexCursorAsync(NpgsqlConnection connection, string direction, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"FETCH {direction} 1 FROM traced", connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
        Assert.IsTrue(await reader.ReadAsync(token));
        int value = reader.GetInt32(0);
        string? label = reader.IsDBNull(1) ? null : reader.GetString(1);
        Assert.IsFalse(await reader.ReadAsync(token));
        return (value, label);
    }
}
