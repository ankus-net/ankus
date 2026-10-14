using System.Text.Json;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Executes generated families over independently stored manual datums and read-only managed views.
/// </summary>
/// <param name="context">The current cancellation context.</param>
[TestClass]
public sealed class MappedOperatorTests(TestContext context)
{
    /// <summary>
    /// Selects a read-only logical view rather than another converter registered for the same SQL type.
    /// </summary>
    [TestMethod]
    public async Task MappedOperatorsUseSelectedReaders()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("0|0|0", await Scalar<string>(connection, "SELECT counts()"));
        Assert.AreEqual("17", await Scalar<string>(connection, "SELECT make(17)::text"));
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT key_eq('17','19')"));
        Assert.AreEqual("1|2|1", await Scalar<string>(connection, "SELECT counts()"));
        Assert.AreEqual(await Scalar<uint>(connection, "SELECT 'key'::regtype::oid"),
            await Scalar<uint>(connection, "SELECT captured_oid()"));
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT expired(false)"));
        Assert.AreEqual(1017, await Scalar<int>(connection, "SELECT alias('17')"));
        Assert.AreEqual(17, await Scalar<int>(connection, "SELECT word('17')"));
        Assert.AreEqual("1|2|1", await Scalar<string>(connection, "SELECT counts()"));
        Assert.AreEqual("true|false|false|true|false|true|2147483647|-2147483648|0|12382|12382|12345", await Scalar<string>(connection, """
            SELECT concat_ws('|',(a=b)::text,(a<>b)::text,(a<c)::text,(a>c)::text,(a<=c)::text,(a>=c)::text,
                key_cmp(a,c),key_cmp(c,a),key_cmp(a,b),key_hash(a),key_hash('39'),key_hash(c))
            FROM(VALUES('17'::key,'19'::key,'22'::key)) input(a,b,c)
            """));
        Assert.AreEqual("17,19,22", await Scalar<string>(connection, """
            SELECT string_agg(value::text,',' ORDER BY id) FROM(VALUES(1,'17'::key),(2,'19'),(3,'22')) input(id,value)
            """));
    }

    /// <summary>
    /// Each default btree strategy preserves logical boundaries and all original row identities.
    /// </summary>
    /// <param name="operation">The operator strategy.</param>
    /// <param name="expected">The independently expected rows in physical identity order.</param>
    [TestMethod]
    [DataRow("<", "4:31")]
    [DataRow("<=", "3:22,4:31")]
    [DataRow("=", "3:22")]
    [DataRow(">=", "1:17,2:19,3:22,5:0")]
    [DataRow(">", "1:17,2:19,5:0")]
    public async Task MappedOperatorsUseEveryBtreeStrategy(string operation, string expected)
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, RowsSql + "CREATE INDEX mapped_btree ON items USING btree(value); ANALYZE items; SET enable_seqscan=off; SET enable_bitmapscan=off;");
        string query = $"SELECT id::text||':'||value::text FROM items WHERE value {operation} '29'::key ORDER BY id";
        await Plan(connection, query, "Index Scan", "mapped_btree", condition: true);
        Assert.AreEqual(expected, string.Join(',', await Strings(connection, query)));
        Assert.IsEmpty(await Strings(connection, "SELECT id::text FROM items WHERE value='70'::key"));
    }

    /// <summary>
    /// The default index follows descending managed order in both physical scan directions.
    /// </summary>
    [TestMethod]
    public async Task MappedOperatorsUseBothBtreeDirections()
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, """
            CREATE TEMP TABLE items(value key); INSERT INTO items VALUES('17'),('22'),('31'),('0'),(NULL);
            CREATE INDEX mapped_order ON items USING btree(value); SET enable_seqscan=off;
            """);
        const string forward = "SELECT coalesce(value::text,'NULL') FROM items ORDER BY value NULLS LAST";
        const string backward = "SELECT coalesce(value::text,'NULL') FROM items ORDER BY value DESC NULLS FIRST";
        await Plan(connection, forward, "Index Only Scan", "mapped_order", direction: "Forward");
        await Plan(connection, backward, "Index Only Scan", "mapped_order", direction: "Backward");
        Assert.AreSequenceEqual(["31", "22", "17", "0", "NULL"], await Strings(connection, forward));
        Assert.AreSequenceEqual(["NULL", "0", "17", "22", "31"], await Strings(connection, backward));
    }

    /// <summary>
    /// Hash collisions retain equality rechecks while uniqueness uses logical rather than raw equality.
    /// </summary>
    [TestMethod]
    public async Task MappedOperatorsRecheckCollisionsAndLogicalUniqueness()
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, RowsSql + "CREATE INDEX mapped_hash ON items USING hash(value); SET enable_seqscan=off; SET enable_bitmapscan=off;");
        const string query = "SELECT id::text||':'||value::text FROM items WHERE value='10'::key ORDER BY id";
        await Plan(connection, query, "Index Scan", "mapped_hash", condition: true);
        Assert.AreSequenceEqual(["1:17", "2:19"], await Strings(connection, query));
        Assert.AreSequenceEqual(["4:31"], await Strings(connection, "SELECT id::text||':'||value::text FROM items WHERE value='30'::key"));
        Assert.IsEmpty(await Strings(connection, "SELECT id::text FROM items WHERE value='70'::key"));
        await Execute(connection, "CREATE TEMP TABLE unique_items(value key UNIQUE); INSERT INTO unique_items VALUES('17'),(NULL),(NULL)");
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, "INSERT INTO unique_items VALUES('19')"));
        Assert.AreEqual("23505", error.SqlState);
        Assert.AreSequenceEqual(["17", "NULL", "NULL"], await Strings(connection,
            "SELECT coalesce(value::text,'NULL') FROM unique_items ORDER BY value NULLS LAST"));
    }

    /// <summary>
    /// Both grouping strategies and distinct preserve logical keys, multiplicity and NULL.
    /// </summary>
    /// <param name="hashed">Whether the executor must use hashed aggregation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MappedOperatorGroupingUsesLogicalKeys(bool hashed)
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, RowsSql + "INSERT INTO items VALUES(7,NULL);" +
            (hashed ? "SET enable_sort=off; SET enable_hashagg=on" : "SET enable_hashagg=off"));
        const string query = "SELECT coalesce((word(value)/10)::text,'NULL')||':'||count(*)::text FROM items GROUP BY value";
        using JsonDocument plan = JsonDocument.Parse(await Scalar<string>(connection, "EXPLAIN(FORMAT JSON) " + query));
        Assert.Contains(node => node.TryGetProperty("Strategy", out JsonElement strategy) && strategy.GetString() == (hashed ? "Hashed" : "Sorted"), Nodes(plan.RootElement[0].GetProperty("Plan")));
        string[] grouped = await Strings(connection, query);
        Array.Sort(grouped, StringComparer.Ordinal);
        Assert.AreSequenceEqual(["0:1", "1:2", "2:1", "3:1", "NULL:2"], grouped);
        Assert.AreSequenceEqual(["0", "1", "2", "3", "NULL"], await Strings(connection, """
            SELECT coalesce((word(value)/10)::text,'NULL') COLLATE "C" AS logical_key
            FROM(SELECT DISTINCT value FROM items) entries ORDER BY logical_key
            """));
        await Execute(connection, "TRUNCATE items");
        Assert.IsEmpty(await Strings(connection, query));
        await Execute(connection, "INSERT INTO items VALUES(1,'17')");
        Assert.AreSequenceEqual(["1:1"], await Strings(connection, query));
    }

    /// <summary>
    /// Real join nodes retain duplicate pairs and reject keys sharing the same hash.
    /// </summary>
    /// <param name="algorithm">The requested join node.</param>
    [TestMethod]
    [DataRow("Hash Join")]
    [DataRow("Merge Join")]
    public async Task MappedOperatorFamiliesExecuteJoins(string algorithm)
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, RowsSql + """
            CREATE TEMP TABLE other(id integer,value key); INSERT INTO other VALUES(8,'18'),(9,'39'),(10,NULL);
            SET enable_nestloop=off;
            """ + (algorithm == "Hash Join" ? "SET enable_mergejoin=off" : "SET enable_hashjoin=off"));
        const string query = "SELECT a.id::text||':'||b.id::text FROM items a JOIN other b ON a.value=b.value ORDER BY a.id,b.id";
        await Plan(connection, query, algorithm);
        Assert.AreSequenceEqual(["1:8", "2:8", "4:9"], await Strings(connection, query));
    }

    /// <summary>
    /// Native by-reference readers detach both components and release captured callback handles.
    /// </summary>
    [TestMethod]
    public async Task MappedOperatorReadersDetachByReferenceInputs()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT datum_mappings.complex_eq('2.5,17','2.5,99')"));
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT expired(true)"));
        Assert.AreSequenceEqual([2.5, 99.0], await Scalar<double[]>(connection, "SELECT detached_pair()"));
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT datum_mappings.complex_eq('2.5,17','3.5,17')"));
        Assert.AreEqual("2.5,17|2.5,99", await Scalar<string>(connection,
            "SELECT '2.5,17'::datum_mappings.complex::text||'|'||'2.5,99'::datum_mappings.complex::text"));
    }

    /// <summary>
    /// NULL bypasses converter construction and cached constructor failures do not affect other mappings.
    /// </summary>
    [TestMethod]
    public async Task MappedOperatorNullsBypassLazyFactory()
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, "SELECT fail_factory()");
        Assert.IsTrue(await Scalar<bool>(connection, """
            SELECT key_eq(NULL,'9011') IS NULL AND key_cmp('9011',NULL) IS NULL AND key_hash(NULL) IS NULL
            """));
        Assert.AreEqual("0|0|0", await Scalar<string>(connection, "SELECT counts()"));
        foreach (string sql in new[] { "SELECT key_eq('17','19')", "SELECT key_hash('17')" })
        {
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, sql));
            Assert.AreEqual("38000", error.SqlState);
            Assert.AreEqual("mapped operator factory failed", error.MessageText);
        }

        Assert.AreEqual("1|0|0", await Scalar<string>(connection, "SELECT counts()"));
        Assert.AreEqual(42, await Scalar<int>(connection, "SELECT word(make(42))"));
        Assert.AreEqual(connection.ProcessID, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Reader and value-contract failures retain exact diagnostics and leave the backend usable.
    /// </summary>
    /// <param name="sql">The failing generated callback.</param>
    /// <param name="state">The required SQLSTATE.</param>
    /// <param name="message">The required message.</param>
    /// <param name="detail">The optional owned detail.</param>
    /// <param name="hint">The optional owned hint.</param>
    [TestMethod]
    [DataRow("SELECT key_eq('9011','17')", "P8901", "mapped operator reader failed", "native word", "use another word")]
    [DataRow("SELECT key_eq('9021','17')", "38000", "ordinary mapped operator reader failed", null, null)]
    [DataRow("SELECT key_eq('9031','17')", "P8903", "mapped equality failed", "logical key", "use another key")]
    [DataRow("SELECT key_cmp('9041','17')", "P8904", "mapped comparison failed", "logical key", "use another key")]
    [DataRow("SELECT key_hash('9051')", "P8905", "mapped hash failed", "logical key", "use another key")]
    public async Task MappedOperatorErrorsPreserveDiagnosticsAndRecover(string sql, string state, string message, string? detail, string? hint)
    {
        await using NpgsqlConnection connection = await Open();
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, sql));
        Assert.AreEqual(state, error.SqlState);
        Assert.AreEqual(message, error.MessageText);
        Assert.AreEqual(detail, error.Detail);
        Assert.AreEqual(hint, error.Hint);
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT expired(false)"));
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT key_eq('17','19')"));
        Assert.AreEqual(connection.ProcessID, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Index build and insertion failures preserve exact rows before a successful constrained reindex probe.
    /// </summary>
    /// <param name="method">The index access method.</param>
    /// <param name="bad">The value that fails its support function.</param>
    /// <param name="state">The required SQLSTATE.</param>
    [TestMethod]
    [DataRow("btree", "9041", "P8904")]
    [DataRow("hash", "9051", "P8905")]
    public async Task MappedOperatorIndexErrorsPreserveRowsAndRecover(string method, string bad, string state)
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, $"CREATE TEMP TABLE items(id integer,value key); INSERT INTO items VALUES(0,'0'),(1,'{bad}')");
        PostgresException build = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, $"CREATE INDEX mapped_fault ON items USING {method}(value)"));
        Assert.AreEqual(state, build.SqlState);
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT to_regclass('pg_temp.mapped_fault') IS NULL"));
        Assert.AreSequenceEqual(["0:0", "1:" + bad], await Strings(connection, "SELECT id::text||':'||value::text FROM items ORDER BY id"));
        await Execute(connection, $"TRUNCATE items; INSERT INTO items VALUES(0,'0'); CREATE INDEX mapped_fault ON items USING {method}(value)");
        PostgresException insert = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, $"INSERT INTO items VALUES(1,'{bad}')"));
        Assert.AreEqual(state, insert.SqlState);
        Assert.AreSequenceEqual(["0:0"], await Strings(connection, "SELECT id::text||':'||value::text FROM items"));
        await Execute(connection, "INSERT INTO items VALUES(2,'17'); REINDEX INDEX mapped_fault; SET enable_seqscan=off; SET enable_bitmapscan=off");
        const string query = "SELECT id::text||':'||value::text FROM items WHERE value='19'::key";
        await Plan(connection, query, "Index Scan", "mapped_fault", condition: true);
        Assert.AreSequenceEqual(["2:17"], await Strings(connection, query));
        Assert.AreSequenceEqual(["0:0", "2:17"], await Strings(connection, "SELECT id::text||':'||value::text FROM items ORDER BY id"));
        Assert.AreEqual(connection.ProcessID, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Catalog contracts retain the exact manual base type, operand OIDs and all default strategies.
    /// </summary>
    [TestMethod]
    public async Task MappedOperatorCatalogRetainsExactContracts()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT typtype='b' AND typlen=4 AND typbyval FROM pg_type WHERE oid='key'::regtype"));
        Assert.AreSequenceEqual(["key_cmp:integer:2", "key_eq:boolean:2", "key_ge:boolean:2", "key_gt:boolean:2",
            "key_hash:integer:1", "key_le:boolean:2", "key_lt:boolean:2", "key_ne:boolean:2"], await Strings(connection, """
            SELECT proname||':'||prorettype::regtype::text||':'||pronargs::text FROM pg_proc
            WHERE pronamespace='mapped_ops'::regnamespace AND proname IN('key_eq','key_ne','key_lt','key_le','key_gt','key_ge','key_cmp','key_hash')
                AND proisstrict AND provolatile='i' AND proparallel='s'
                AND proargtypes[0]='key'::regtype AND (pronargs=1 OR proargtypes[1]='key'::regtype)
            ORDER BY proname COLLATE "C"
            """));
        Assert.AreSequenceEqual(["btree:1:<", "btree:2:<=", "btree:3:=", "btree:4:>=", "btree:5:>", "hash:1:="], await Strings(connection, """
            SELECT a.amname||':'||m.amopstrategy::text||':'||o.oprname FROM pg_opclass c JOIN pg_am a ON a.oid=c.opcmethod
            JOIN pg_amop m ON m.amopfamily=c.opcfamily JOIN pg_operator o ON o.oid=m.amopopr
            WHERE c.opcintype='key'::regtype AND c.opcdefault AND m.amoplefttype='key'::regtype AND m.amoprighttype='key'::regtype
            ORDER BY a.amname,m.amopstrategy
            """));
        Assert.AreSequenceEqual(["btree:1:key_cmp", "hash:1:key_hash"], await Strings(connection, """
            SELECT a.amname||':'||p.amprocnum::text||':'||f.proname FROM pg_opclass c JOIN pg_am a ON a.oid=c.opcmethod
            JOIN pg_amproc p ON p.amprocfamily=c.opcfamily JOIN pg_proc f ON f.oid=p.amproc
            WHERE c.opcintype='key'::regtype AND p.amproclefttype='key'::regtype AND p.amprocrighttype='key'::regtype
            ORDER BY a.amname,p.amprocnum
            """));
    }

    /// <summary>
    /// A distinct backend reuses committed hash buckets and observes independently checked mutations and reindexing.
    /// </summary>
    [TestMethod]
    public async Task MappedOperatorHashesSurviveFreshBackends()
    {
        string table = "mapped_persist_" + Guid.NewGuid().ToString("N");
        await using NpgsqlConnection first = await Open();
        try
        {
            await Execute(first, $"""
                CREATE TABLE mapped_ops.{table}(id integer,value key);
                INSERT INTO mapped_ops.{table} VALUES(1,'17'),(2,'19'),(3,'31'),(4,'22'),(5,NULL);
                CREATE INDEX {table}_hash ON mapped_ops.{table} USING hash(value);
                """);
            uint index = await Scalar<uint>(first, $"SELECT 'mapped_ops.{table}_hash'::regclass::oid");
            await using NpgsqlConnection second = await Open();
            Assert.AreNotEqual(first.ProcessID, second.ProcessID);
            Assert.IsFalse(new NpgsqlConnectionStringBuilder(first.ConnectionString).Pooling);
            Assert.IsFalse(new NpgsqlConnectionStringBuilder(second.ConnectionString).Pooling);
            Assert.AreEqual(index, await Scalar<uint>(second, $"SELECT 'mapped_ops.{table}_hash'::regclass::oid"));
            foreach (NpgsqlConnection connection in new[] { first, second })
            {
                Assert.AreSequenceEqual([12382, 12382, 12382, 12345], await Scalar<int[]>(connection,
                    "SELECT ARRAY[key_hash('17'),key_hash('19'),key_hash('31'),key_hash('22')]"));
                await Execute(connection, "SET enable_seqscan=off; SET enable_bitmapscan=off");
            }

            string query = $"SELECT id::text||':'||value::text FROM mapped_ops.{table} WHERE value='10'::key ORDER BY id";
            await Plan(second, query, "Index Scan", table + "_hash", condition: true);
            Assert.AreSequenceEqual(["1:17", "2:19"], await Strings(second, query));
            await Execute(second, $"UPDATE mapped_ops.{table} SET value='28' WHERE id=2; DELETE FROM mapped_ops.{table} WHERE id=3; INSERT INTO mapped_ops.{table} VALUES(6,'18'); REINDEX INDEX mapped_ops.{table}_hash");
            await Plan(first, query, "Index Scan", table + "_hash", condition: true);
            Assert.AreSequenceEqual(["1:17", "6:18"], await Strings(first, query));
            Assert.AreSequenceEqual(["2:28", "4:22"], await Strings(first,
                $"SELECT id::text||':'||value::text FROM mapped_ops.{table} WHERE value='20'::key ORDER BY id"));
            Assert.AreSequenceEqual(["1:17", "2:28", "4:22", "5:NULL", "6:18"], await Strings(first,
                $"SELECT id::text||':'||coalesce(value::text,'NULL') FROM mapped_ops.{table} ORDER BY id"));
        }
        finally
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            await using NpgsqlConnection cleanup = await PostgresFixture.Cluster.OpenConnectionAsync(timeout.Token);
            await using var command = new NpgsqlCommand($"DROP TABLE IF EXISTS mapped_ops.{table}", cleanup);
            await command.ExecuteNonQueryAsync(timeout.Token);
        }
    }

    /// <summary>
    /// Supplies raw-distinct equal keys, an unequal collision, zero and SQL NULL.
    /// </summary>
    private const string RowsSql = "CREATE TEMP TABLE items(id integer,value key); INSERT INTO items VALUES(1,'17'),(2,'19'),(3,'22'),(4,'31'),(5,'0'),(6,NULL);";

    /// <summary>
    /// Opens a fresh backend with the explicit mapped operator schema.
    /// </summary>
    private async Task<NpgsqlConnection> Open()
    {
        NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await Execute(connection, "SET search_path=mapped_ops,pg_catalog");
        return connection;
    }

    /// <summary>
    /// Executes statements without discarding an asserted scalar result.
    /// </summary>
    private async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }

    /// <summary>
    /// Decodes the explicitly requested managed scalar or array type.
    /// </summary>
    private async Task<T> Scalar<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
        Assert.IsTrue(await reader.ReadAsync(context.CancellationToken));
        return await reader.GetFieldValueAsync<T>(0, context.CancellationToken);
    }

    /// <summary>
    /// Preserves every independently expected result row.
    /// </summary>
    private async Task<string[]> Strings(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(context.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    /// <summary>
    /// Requires the selected executor node and a genuine index condition or scan direction.
    /// </summary>
    private async Task Plan(NpgsqlConnection connection, string sql, string node, string? index = null, bool condition = false, string? direction = null)
    {
        using JsonDocument plan = JsonDocument.Parse(await Scalar<string>(connection, "EXPLAIN(FORMAT JSON) " + sql));
        Assert.Contains(item => item.GetProperty("Node Type").GetString() == node && Has(item, "Index Name", index) &&
            (!condition || HasCondition(item)) && Has(item, "Scan Direction", direction),
            Nodes(plan.RootElement[0].GetProperty("Plan")), plan.RootElement.ToString());
    }

    /// <summary>
    /// Checks a plan node's text property, when a value is expected.
    /// </summary>
    private static bool Has(JsonElement item, string property, string? expected)
        => expected is null || item.TryGetProperty(property, out JsonElement value) && value.GetString() == expected;

    /// <summary>
    /// Checks that a plan node probes its index with a condition.
    /// </summary>
    private static bool HasCondition(JsonElement item)
        => item.TryGetProperty("Index Cond", out JsonElement condition) && !string.IsNullOrWhiteSpace(condition.GetString());

    /// <summary>
    /// Traverses nested executor plans.
    /// </summary>
    private static IEnumerable<JsonElement> Nodes(JsonElement plan)
    {
        yield return plan;
        if (plan.TryGetProperty("Plans", out JsonElement children))
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
}
