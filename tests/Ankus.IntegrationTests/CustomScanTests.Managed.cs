using System.Globalization;
using System.Text.Json;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class CustomScanTests
{
    /// <summary>
    /// A managed provider's rows pass through PostgreSQL's own conditions and projection, keep SQL NULL, and EXPLAIN
    /// names the provider and the scanned table.
    /// </summary>
    [TestMethod]
    public async Task ManagedProviderRowsPassThroughPostgresConditions()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(5, token);
        const string Query = "SELECT value * 2, label FROM numbers_a WHERE value <> 2 ORDER BY value";
        using (JsonDocument plan = await ExplainAsync(connection, Query, false, token))
        {
            JsonElement scan = FindNode(plan.RootElement[0].GetProperty("Plan"), "Custom Scan");
            Assert.AreEqual("number_scan", scan.GetProperty("Custom Plan Provider").GetString());
            Assert.AreEqual("numbers_a", scan.GetProperty("Relation Name").GetString());
            Assert.AreEqual("(value <> 2)", scan.GetProperty("Filter").GetString());
        }

        long[] before = await NumberCountsAsync(connection, token);
        var rows = new List<(long Value, string? Label)>();
        await using (var command = new NpgsqlCommand(Query, connection))
        await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
        {
            while (await reader.ReadAsync(token))
            {
                rows.Add((reader.GetInt64(0), reader.IsDBNull(1) ? null : reader.GetString(1)));
            }
        }

        Assert.AreSequenceEqual([(2L, "number 1"), (6L, null), (8L, "number 4"), (10L, "number 5")], rows);
        long[] after = await NumberCountsAsync(connection, token);
        Assert.AreEqual(1, after[0] - before[0]);
        Assert.AreEqual(1, after[1] - before[1]);
    }

    /// <summary>
    /// EXPLAIN without ANALYZE begins the scan only to explain it, and EXPLAIN ANALYZE adds the provider's properties
    /// after its rows are read; every begun state is disposed.
    /// </summary>
    [TestMethod]
    public async Task ManagedProviderExplainsItsScan()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(4, token);
        long[] before = await NumberCountsAsync(connection, token);
        using (JsonDocument plan = await ExplainAsync(connection, "SELECT * FROM numbers_a", false, token))
        {
            JsonElement scan = FindNode(plan.RootElement[0].GetProperty("Plan"), "Custom Scan");
            Assert.AreEqual(4, scan.GetProperty("Numbers").GetInt64());
            Assert.AreEqual(0, scan.GetProperty("Numbers Read").GetInt64());
        }

        using (JsonDocument plan = await ExplainAsync(connection, "SELECT * FROM numbers_a", true, token))
        {
            JsonElement scan = FindNode(plan.RootElement[0].GetProperty("Plan"), "Custom Scan");
            Assert.AreEqual(4, scan.GetProperty("Actual Rows").GetDouble());
            Assert.AreEqual(4, scan.GetProperty("Numbers Read").GetInt64());
        }

        long[] after = await NumberCountsAsync(connection, token);
        Assert.AreSequenceEqual<long>([2, 2, 0, 1, 0, 0, 0], [.. after.Zip(before, static (current, previous) => current - previous)]);
    }

    /// <summary>
    /// The inner side of a nested loop and a subquery restart the scan in place for each outer row.
    /// </summary>
    [TestMethod]
    public async Task ManagedProviderRescansInPlace()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(3, token);
        await ExecuteAsync(connection, "SET enable_material = off", token);
        long[] before = await NumberCountsAsync(connection, token);
        Assert.AreEqual("1:1,2:1,2:2,3:1,3:2,3:3", await ScalarAsync<string>(connection, """
            SELECT string_agg(g || ':' || value, ',' ORDER BY g, value)
            FROM generate_series(1, 3) g, LATERAL (SELECT value FROM numbers_a WHERE value <= g OFFSET 0) n
            """, token));
        Assert.AreEqual("1,2,3", await ScalarAsync<string>(connection,
            "SELECT string_agg((SELECT count(*) FROM numbers_a WHERE value <= g)::text, ',' ORDER BY g) FROM generate_series(1, 3) g", token));
        long[] after = await NumberCountsAsync(connection, token);
        Assert.AreEqual(2, after[0] - before[0]);
        Assert.AreEqual(2, after[1] - before[1]);
        Assert.AreEqual(6, after[2] - before[2]);
    }

    /// <summary>
    /// A provider that reads backward lets a cursor without <c>SCROLL</c> move both ways over its rows, with PostgreSQL
    /// still applying the query's condition, and a scrollable cursor needs no stored copy of them.
    /// </summary>
    [TestMethod]
    public async Task ManagedProviderReadsBackward()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(6, token);
        using (JsonDocument plan = await ExplainAsync(connection, "DECLARE numbers SCROLL CURSOR FOR SELECT value FROM numbers_a", false, token))
        {
            Assert.AreEqual("Custom Scan", plan.RootElement[0].GetProperty("Plan").GetProperty("Node Type").GetString());
        }

        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token);
        await ExecuteAsync(connection, "DECLARE numbers CURSOR FOR SELECT value FROM numbers_a WHERE value <> 4", token);
        string[] fetches = ["FETCH 3", "FETCH PRIOR", "FETCH LAST", "FETCH BACKWARD 2", "FETCH FORWARD ALL", "FETCH BACKWARD ALL", "FETCH NEXT",
            "FETCH ABSOLUTE 2", "FETCH RELATIVE 3"];
        var results = new List<string>();
        foreach (string fetch in fetches)
        {
            results.Add(await FetchAsync(connection, fetch + " FROM numbers", token));
        }

        Assert.AreSequenceEqual(["1,2,3", "2", "6", "5,3", "5,6", "6,5,3,2,1", "1", "2", "6"], results);
        await transaction.CommitAsync(token);
    }

    /// <summary>
    /// Rows a provider returns in order of a column satisfy <c>ORDER BY</c> and <c>LIMIT</c> on it without a sort; an
    /// order the provider does not offer still sorts.
    /// </summary>
    /// <param name="query">The query over the provider's table.</param>
    /// <param name="sorted">Whether PostgreSQL must sort the provider's rows.</param>
    /// <param name="expected">The query's rows.</param>
    [TestMethod]
    [DataRow("SELECT value FROM numbers_a ORDER BY value", false, "1,2,3,4,5")]
    [DataRow("SELECT value FROM numbers_a WHERE value > 2 ORDER BY value LIMIT 2", false, "3,4")]
    [DataRow("SELECT value FROM numbers_a ORDER BY value DESC", true, "5,4,3,2,1")]
    [DataRow("SELECT value FROM numbers_a ORDER BY value NULLS FIRST", true, "1,2,3,4,5")]
    public async Task ManagedProviderOrderedRowsSkipSorts(string query, bool sorted, string expected)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(5, token);
        using (JsonDocument plan = await ExplainAsync(connection, query, false, token))
        {
            JsonElement root = plan.RootElement[0].GetProperty("Plan");
            _ = FindNode(root, "Custom Scan");
            Assert.AreEqual(sorted, Nodes(root).Any(static node => node.GetProperty("Node Type").GetString() == "Sort"));
        }

        Assert.AreEqual(expected, await FetchAsync(connection, query, token));
    }

    /// <summary>
    /// A merge join reads its inner rows again for each repeated outer key by marking and restoring the provider's
    /// position, with no sort or stored copy of those rows.
    /// </summary>
    [TestMethod]
    public async Task ManagedProviderRestoresMarksForMergeJoins()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(6, token);
        await ExecuteAsync(connection, "SET enable_hashjoin = off; SET enable_nestloop = off; SET enable_material = off;", token);
        const string Query = """
            SELECT outer_value.v || ':' || n.value
            FROM (VALUES (2), (2), (3), (5), (5), (5), (9)) outer_value(v) JOIN numbers_a n ON n.value = outer_value.v
            """;
        using (JsonDocument plan = await ExplainAsync(connection, Query, false, token))
        {
            JsonElement join = FindNode(plan.RootElement[0].GetProperty("Plan"), "Merge Join");
            JsonElement inner = join.GetProperty("Plans").EnumerateArray()
                .Single(static child => child.GetProperty("Parent Relationship").GetString() == "Inner");
            Assert.AreEqual("Custom Scan", inner.GetProperty("Node Type").GetString());
        }

        long[] before = await NumberCountsAsync(connection, token);
        Assert.AreEqual("2:2,2:2,3:3,5:5,5:5,5:5", await FetchAsync(connection, Query, token));
        long[] after = await NumberCountsAsync(connection, token);
        Assert.IsGreaterThan(0L, after[4] - before[4]);
        Assert.AreEqual(3, after[5] - before[5]);
    }

    /// <summary>
    /// An early stop and a cursor closed after one row end the scan and dispose its state.
    /// </summary>
    [TestMethod]
    public async Task ManagedProviderDisposesStoppedScans()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(1000, token);
        long[] before = await NumberCountsAsync(connection, token);
        Assert.AreEqual(1L, await ScalarAsync<long>(connection, "SELECT value FROM numbers_a LIMIT 1", token));
        await using (NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token))
        {
            await ExecuteAsync(connection, "DECLARE numbers CURSOR FOR SELECT value FROM numbers_a", token);
            Assert.AreEqual(1L, await ScalarAsync<long>(connection, "FETCH 1 FROM numbers", token));
            await ExecuteAsync(connection, "CLOSE numbers", token);
            await transaction.CommitAsync(token);
        }

        long[] after = await NumberCountsAsync(connection, token);
        Assert.AreEqual(2, after[0] - before[0]);
        Assert.AreEqual(2, after[1] - before[1]);
    }

    /// <summary>
    /// A provider exception and a NULL in a NOT NULL column become PostgreSQL errors, the failed scan's state is
    /// disposed while the query aborts, and the same session keeps working.
    /// </summary>
    /// <param name="setup">The table or setting that makes the scan fail.</param>
    /// <param name="query">The failing query.</param>
    /// <param name="message">The expected error message.</param>
    [TestMethod]
    [DataRow("SET ankus_trace_scan.numbers_fail_at = 3", "SELECT count(*) FROM numbers_a", "The numbers scan failed at 3.")]
    [DataRow("CREATE TEMP TABLE numbers_strict(value bigint, label text NOT NULL)", "SELECT max(length(label))::bigint FROM numbers_strict",
        "Custom scan rows cannot hold NULL in NOT NULL column 'label'.")]
    public async Task ManagedProviderFailuresRecover(string setup, string query, string message)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(5, token);
        await ExecuteAsync(connection, setup, token);
        long[] before = await NumberCountsAsync(connection, token);
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ScalarAsync<long>(connection, query, token));
        Assert.AreEqual(message, error.MessageText);
        long[] after = await NumberCountsAsync(connection, token);
        Assert.AreEqual(1, after[0] - before[0]);
        Assert.AreEqual(1, after[1] - before[1]);
        await ExecuteAsync(connection, "RESET ankus_trace_scan.numbers_fail_at", token);
        Assert.AreEqual(5L, await ScalarAsync<long>(connection, "SELECT count(*) FROM numbers_a", token));
    }

    /// <summary>
    /// A provider learns which columns PostgreSQL reads, from the query's output or its conditions, and may leave the
    /// others unset even when they are <c>NOT NULL</c>; a column that is read, including through a whole-row
    /// reference, must still hold a value.
    /// </summary>
    /// <param name="query">The query over a table whose NOT NULL label the provider fills only when it is read.</param>
    /// <param name="expected">The query's result, or null when it must fail.</param>
    [TestMethod]
    [DataRow("SELECT count(*) FROM numbers_strict", 5L)]
    [DataRow("SELECT sum(value)::bigint FROM numbers_strict WHERE value > 2", 12L)]
    [DataRow("SELECT count(*) FROM numbers_strict WHERE label <> 'x'", null)]
    [DataRow("SELECT count(numbers_strict) FROM numbers_strict", null)]
    public async Task ManagedProviderSkipsUnreadColumns(string query, long? expected)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(5, token);
        await ExecuteAsync(connection, "CREATE TEMP TABLE numbers_strict(value bigint, label text NOT NULL)", token);
        using (JsonDocument plan = await ExplainAsync(connection, query, false, token))
        {
            _ = FindNode(plan.RootElement[0].GetProperty("Plan"), "Custom Scan");
        }

        if (expected is long value)
        {
            Assert.AreEqual(value, await ScalarAsync<long>(connection, query, token));
            return;
        }

        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => ScalarAsync<long>(connection, query, token));
        Assert.AreEqual("Custom scan rows cannot hold NULL in NOT NULL column 'label'.", error.MessageText);
    }

    /// <summary>
    /// The planner reports which columns a query reads, so a provider can price a path by the columns it must produce;
    /// the sample charges twice as much when its label is read.
    /// </summary>
    [TestMethod]
    public async Task ManagedProviderPricesReadColumns()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(1000, token);
        double[] costs = new double[3];
        string[] queries = ["SELECT value FROM numbers_a", "SELECT label FROM numbers_a", "SELECT count(*) FROM numbers_a WHERE label > 'a'"];
        for (int index = 0; index < queries.Length; index++)
        {
            using JsonDocument plan = await ExplainAsync(connection, queries[index], false, token);
            costs[index] = FindNode(plan.RootElement[0].GetProperty("Plan"), "Custom Scan").GetProperty("Total Cost").GetDouble();
        }

        Assert.AreEqual(1.0, costs[0], 0.000001);
        Assert.AreEqual(2.0, costs[1], 0.000001);
        Assert.IsGreaterThan(2.0, costs[2]);
    }

    /// <summary>
    /// Rows keep their columns in place around a dropped column.
    /// </summary>
    [TestMethod]
    public async Task ManagedProviderRowsSkipDroppedColumns()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(2, token);
        await ExecuteAsync(connection, "CREATE TEMP TABLE numbers_dropped(gone integer, value bigint, label text); ALTER TABLE numbers_dropped DROP COLUMN gone;", token);
        Assert.AreEqual("(1,\"number 1\"),(2,\"number 2\")", await ScalarAsync<string>(connection,
            "SELECT string_agg(numbers_dropped::text, ',' ORDER BY value) FROM numbers_dropped", token));
    }

    /// <summary>
    /// Tables whose system columns, row locks or modification the query needs keep PostgreSQL's ordinary scans, as do
    /// tables when the provider offers no path.
    /// </summary>
    /// <param name="query">The statement to explain.</param>
    /// <param name="count">The provider's setting.</param>
    [TestMethod]
    [DataRow("SELECT ctid FROM numbers_a", 5)]
    [DataRow("SELECT * FROM numbers_a FOR UPDATE", 5)]
    [DataRow("UPDATE numbers_a SET label = 'x'", 5)]
    [DataRow("DELETE FROM numbers_a", 5)]
    [DataRow("SELECT * FROM numbers_a", 0)]
    [DataRow("SELECT * FROM other_numbers", 5)]
    public async Task ManagedProviderLeavesOtherScansAlone(string query, int count)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await OpenNumbersAsync(count, token);
        await ExecuteAsync(connection, "CREATE TEMP TABLE other_numbers(value bigint, label text)", token);
        using JsonDocument plan = await ExplainAsync(connection, query, false, token);
        Assert.DoesNotContain("number_scan", plan.RootElement.GetRawText());
    }

    /// <summary>
    /// A provider name that another module registered first rejects the library load on every attempt, keeps the
    /// other module's methods, and leaves ordinary planning working.
    /// </summary>
    [TestMethod]
    public async Task ManagedProviderNameCollisionRejectsLoad()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await ExecuteAsync(connection, "CREATE TEMP TABLE numbers_a(value bigint, label text); SET ankus_trace_scan.numbers = 5;", token);
        long original = await ScalarAsync<long>(connection, "SELECT datatype.custom_scan_registry_register('number_scan', false)", token);
        Assert.AreNotEqual(0L, original);
        for (int attempt = 0; attempt < 2; attempt++)
        {
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ExecuteAsync(connection, "SELECT customscan.trace_scan_reset()", token));
            Assert.AreEqual("42710", error.SqlState);
            Assert.AreEqual("extensible node type \"number_scan\" already exists", error.MessageText);
            Assert.AreEqual(original, await ScalarAsync<long>(connection, "SELECT datatype.custom_scan_registry_lookup('number_scan', false)", token));
            using (JsonDocument plan = await ExplainAsync(connection, "SELECT * FROM numbers_a", false, token))
            {
                Assert.AreEqual("Seq Scan", plan.RootElement[0].GetProperty("Plan").GetProperty("Node Type").GetString());
            }

            Assert.AreEqual(0L, await ScalarAsync<long>(connection, "SELECT count(*) FROM numbers_a", token));
        }
    }

    /// <summary>
    /// Opens a connection that has loaded the provider, with an empty numbers table and the provider's row count.
    /// </summary>
    private static async Task<NpgsqlConnection> OpenNumbersAsync(int count, CancellationToken token)
    {
        NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        try
        {
            await ExecuteAsync(connection, $"""
                SELECT customscan.trace_scan_reset();
                CREATE TEMP TABLE numbers_a(value bigint, label text);
                SET ankus_trace_scan.numbers = {count};
                """, token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Finds the first plan node of a type, searching the plan depth first.
    /// </summary>
    private static JsonElement FindNode(JsonElement node, string type)
        => Nodes(node).FirstOrDefault(value => value.GetProperty("Node Type").GetString() == type) is { ValueKind: JsonValueKind.Object } found
            ? found
            : throw new AssertFailedException($"The plan has no {type} node.");

    /// <summary>
    /// Lists a plan node and its descendants in depth-first order.
    /// </summary>
    private static IEnumerable<JsonElement> Nodes(JsonElement node)
    {
        yield return node;
        if (node.TryGetProperty("Plans", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                foreach (JsonElement nested in Nodes(child))
                {
                    yield return nested;
                }
            }
        }
    }

    /// <summary>
    /// Reads the numbers provider's lifecycle counters from the backend that ran the query.
    /// </summary>
    private static async Task<long[]> NumberCountsAsync(NpgsqlConnection connection, CancellationToken token)
        => await ScalarAsync<long[]>(connection, "SELECT customscan.number_scan_counts()", token);

    /// <summary>
    /// Joins the first column of every row a statement returns with commas.
    /// </summary>
    private static async Task<string> FetchAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        var values = new List<string>();
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
        while (await reader.ReadAsync(token))
        {
            values.Add(Convert.ToString(reader.GetValue(0), CultureInfo.InvariantCulture)!);
        }

        return string.Join(',', values);
    }

    /// <summary>
    /// Runs statements on the connection.
    /// </summary>
    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Returns the first column of the first row as the expected type.
    /// </summary>
    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }
}
