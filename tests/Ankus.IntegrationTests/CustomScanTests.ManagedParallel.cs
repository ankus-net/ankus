using System.Text.Json;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies a managed provider's parallel scans through PostgreSQL's workers and their shared memory.
/// </summary>
public sealed partial class CustomScanTests
{
    /// <summary>
    /// The workers of a parallel scan each claim part of the integers through the provider's shared memory, so the
    /// Gather returns every integer once, all from worker processes.
    /// </summary>
    [TestMethod]
    public async Task ManagedProviderRunsInParallel()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(3000, token);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token);
        string table = await CreateManagedParallelInputAsync(connection, token);
        long[] before = await NumberCountsAsync(connection, token);
        using (JsonDocument plan = await ExplainAsync(connection, $"SELECT value FROM {table}", true, token))
        {
            JsonElement root = plan.RootElement[0].GetProperty("Plan");
            int launched = WorkersLaunched(root);
            Assert.IsGreaterThan(0, launched);
            JsonElement scan = FindNode(root, "Custom Scan");
            Assert.IsTrue(scan.GetProperty("Parallel Aware").GetBoolean());
            Assert.AreEqual(launched, scan.GetProperty("Actual Loops").GetInt64());
            Assert.AreEqual(3000, scan.GetProperty("Actual Rows").GetDouble() * launched, 0.5 * launched);
        }

        await using (var command = new NpgsqlCommand($"SELECT datatype.custom_scan_process(value::integer), value FROM {table}", connection))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
        {
            var seen = new HashSet<long>();
            while (await reader.ReadAsync(token))
            {
                Assert.AreNotEqual(connection.ProcessID, reader.GetInt32(0));
                Assert.IsTrue(seen.Add(reader.GetInt64(1)));
            }

            Assert.AreSequenceEqual(Enumerable.Range(1, 3000).Select(static value => (long)value), seen.Order());
        }

        long[] after = await NumberCountsAsync(connection, token);
        Assert.AreEqual(2, after[6] - before[6]);
        await transaction.RollbackAsync(token);
    }

    /// <summary>
    /// Each process returns its integers in order, so PostgreSQL merges the workers' rows for <c>ORDER BY</c> instead
    /// of sorting them.
    /// </summary>
    [TestMethod]
    public async Task ManagedProviderParallelRowsKeepTheirOrder()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(500, token);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token);
        string table = await CreateManagedParallelInputAsync(connection, token);
        string filter = table + "_keep";

        // A costly condition makes dividing the rows among workers worth a merge.
        await ExecuteAsync(connection, $"""
            CREATE FUNCTION {filter}(value bigint) RETURNS boolean LANGUAGE plpgsql IMMUTABLE PARALLEL SAFE COST 10000
            AS $$BEGIN RETURN value % 7 <> 0; END$$
            """, token);
        string query = $"SELECT value FROM {table} WHERE {filter}(value) ORDER BY value";
        using (JsonDocument plan = await ExplainAsync(connection, query, false, token))
        {
            JsonElement root = plan.RootElement[0].GetProperty("Plan");
            _ = FindNode(root, "Gather Merge");
            Assert.IsTrue(FindNode(root, "Custom Scan").GetProperty("Parallel Aware").GetBoolean());
            Assert.DoesNotContain("Sort", Nodes(root).Select(static node => node.GetProperty("Node Type").GetString()));
        }

        Assert.AreEqual(string.Join(',', Enumerable.Range(1, 500).Where(static value => value % 7 != 0)), await FetchAsync(connection, query, token));
        await transaction.RollbackAsync(token);
    }

    /// <summary>
    /// Restarting a Gather starts new workers, and the provider initializes its shared memory again each time, so every
    /// round reads every integer once.
    /// </summary>
    [TestMethod]
    public async Task ManagedProviderReinitializesParallelScans()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(2000, token);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token);
        string table = await CreateManagedParallelInputAsync(connection, token);
        await ExecuteAsync(connection, "SET LOCAL enable_material = off; " + DisableMemoizeSql(true), token);
        string query = $"""
            SELECT iteration || ':' || count(*) || ':' || count(DISTINCT value)
            FROM (VALUES (1), (2), (3)) AS rounds(iteration)
            CROSS JOIN LATERAL (SELECT value FROM {table} OFFSET iteration * 0) AS rows
            GROUP BY iteration ORDER BY iteration
            """;
        using (JsonDocument plan = await ExplainAsync(connection, query, true, token))
        {
            JsonElement gather = Assert.ContainsSingle(PlanNodes(plan.RootElement[0].GetProperty("Plan"), "Gather"));
            Assert.AreEqual(3L, gather.GetProperty("Actual Loops").GetInt64());
            Assert.IsGreaterThan(0, gather.GetProperty("Workers Launched").GetInt32());
        }

        long[] before = await NumberCountsAsync(connection, token);
        Assert.AreEqual("1:2000:2000,2:2000:2000,3:2000:2000", await FetchAsync(connection, query, token));
        long[] after = await NumberCountsAsync(connection, token);
        Assert.AreEqual(3, after[6] - before[6]);
        await transaction.RollbackAsync(token);
    }

    /// <summary>
    /// A parallel provider that cannot rescan begins a new state for each round of a restarted Gather. PostgreSQL
    /// restarts the leader's scan only after it reinitializes the shared memory, at the first read of the round, so
    /// Ankus attaches the memory to the new state then; the leader, which reads too, never repeats an integer the
    /// workers claimed.
    /// </summary>
    [TestMethod]
    public async Task ManagedParallelProviderWithoutRescanKeepsSharedMemory()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(2000, token);
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token);
        string table = await CreateManagedParallelInputAsync(connection, token, "split_", leader: true);
        await ExecuteAsync(connection, "SET LOCAL enable_material = off; " + DisableMemoizeSql(true), token);
        string query = $"""
            SELECT iteration || ':' || count(*) || ':' || count(DISTINCT value)
            FROM (VALUES (1), (2), (3)) AS rounds(iteration)
            CROSS JOIN LATERAL (SELECT value FROM {table} OFFSET iteration * 0) AS rows
            GROUP BY iteration ORDER BY iteration
            """;
        using (JsonDocument plan = await ExplainAsync(connection, query, false, token))
        {
            JsonElement root = plan.RootElement[0].GetProperty("Plan");
            _ = Assert.ContainsSingle(PlanNodes(root, "Gather"));
            Assert.AreEqual("split_scan", FindNode(root, "Custom Scan").GetProperty("Custom Plan Provider").GetString());
        }

        long[] before = await ScalarAsync<long[]>(connection, "SELECT customscan.split_scan_counts()", token);
        Assert.AreEqual("1:2000:2000,2:2000:2000,3:2000:2000", await FetchAsync(connection, query, token));
        long[] after = await ScalarAsync<long[]>(connection, "SELECT customscan.split_scan_counts()", token);

        // The nested loop restarts the Gather before every round, the first included: one state begins with the scan
        // and one for each restart, and each is attached and disposed.
        Assert.AreSequenceEqual<long>([4, 4, 4], [.. after.Zip(before, static (current, previous) => current - previous)]);
        await transaction.RollbackAsync(token);
    }

    /// <summary>
    /// An exception in a worker's scan becomes the query's error with the provider's message, and the session keeps
    /// working after the transaction rolls back.
    /// </summary>
    [TestMethod]
    public async Task ManagedProviderParallelFailuresRecover()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(3000, token);
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token))
        {
            string table = await CreateManagedParallelInputAsync(connection, token);
            await ExecuteAsync(connection, "SET LOCAL ankus_trace_scan.numbers_fail_at = 1700", token);
            using (JsonDocument plan = await ExplainAsync(connection, $"SELECT count(*) FROM {table}", false, token))
            {
                Assert.IsTrue(FindNode(plan.RootElement[0].GetProperty("Plan"), "Custom Scan").GetProperty("Parallel Aware").GetBoolean());
            }

            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ScalarAsync<long>(connection, $"SELECT count(*) FROM {table}", token));
            Assert.AreEqual("The numbers scan failed at 1700.", error.MessageText);
            await transaction.RollbackAsync(token);
        }

        Assert.AreEqual(3000L, await ScalarAsync<long>(connection, "SELECT count(*) FROM numbers_a", token));
    }

    /// <summary>
    /// Creates an ordinary table, which unlike a temporary one can be scanned in parallel, and makes parallel plans
    /// cheap for the rest of the transaction. Unless the leader participates, it only gathers, so workers produce every
    /// row.
    /// </summary>
    private static async Task<string> CreateManagedParallelInputAsync(NpgsqlConnection connection, CancellationToken token,
        string prefix = "numbers_parallel_", bool leader = false)
    {
        string table = "tests." + prefix + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, $"""
            CREATE TABLE {table}(value bigint, label text);
            SET LOCAL max_parallel_workers_per_gather = 2;
            SET LOCAL parallel_setup_cost = 0;
            SET LOCAL parallel_tuple_cost = 0;
            SET LOCAL parallel_leader_participation = {(leader ? "on" : "off")};
            """, token);
        return table;
    }
}
