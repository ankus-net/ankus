using System.Text.Json;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Executes generated equality, order and hash families through real PostgreSQL indexes and plans.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class CustomOperatorTests(TestContext context)
{
    /// <summary>
    /// Uses exact user interfaces, including extreme compare results and equality that ignores stored metadata.
    /// </summary>
    [TestMethod]
    public async Task CustomOperatorsPreserveLogicalEqualityAndExtremeSigns()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("true|false|true|true|false|false|-2147483648|2147483647|0", await Scalar<string>(connection, """
            SELECT concat_ws('|',(a=b)::text,(a<>b)::text,(a<c)::text,(a<=c)::text,(a>c)::text,(a>=c)::text,key_cmp(a,c),key_cmp(c,a),key_cmp(a,b))
            FROM(VALUES('Alpha#1'::key,'ALPHA#99'::key,'beta#2'::key)) row(a,b,c)
            """));
        Assert.IsTrue(await Scalar<bool>(connection, """
            SELECT key_send('Alpha#1')<>key_send('ALPHA#99')
                AND key_hash('Alpha#1')=key_hash('ALPHA#99')
                AND 'collision-a#1'::key<>'collision-b#2'::key
                AND key_hash('collision-a#1')=7 AND key_hash('collision-b#2')=7
                AND ARRAY['Alpha#1'::key,NULL]=ARRAY['ALPHA#99'::key,NULL]
                AND ARRAY[]::key[]=ARRAY[]::key[]
            """));
    }

    /// <summary>
    /// Declared nonlexical ordering is used by SQL sorting and both directions of a real btree index.
    /// </summary>
    [TestMethod]
    public async Task CustomOperatorsUseNonstandardIndexedOrdering()
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, """
            CREATE TEMP TABLE ordered_items(value derived_ops.ordered);
            INSERT INTO ordered_items SELECT make_ordered(text) FROM unnest(ARRAY['foo','Bar','bar','Foo','']) text;
            INSERT INTO ordered_items VALUES(NULL);
            CREATE INDEX ordered_items_btree ON ordered_items USING btree(value);
            ANALYZE ordered_items; SET enable_seqscan=off;
            """);
        const string ascending = "SELECT coalesce(ordered_text(value),'NULL') FROM ordered_items ORDER BY value ASC NULLS LAST";
        await AssertPlan(connection, ascending, "Index Only Scan", "ordered_items_btree", direction: "Forward");
        Assert.AreSequenceEqual(["Foo", "Bar", "", "bar", "foo", "NULL"], await Strings(connection, ascending));
        const string descending = "SELECT coalesce(ordered_text(value),'NULL') FROM ordered_items ORDER BY value DESC NULLS FIRST";
        await AssertPlan(connection, descending, "Index Only Scan", "ordered_items_btree", direction: "Backward");
        Assert.AreSequenceEqual(["NULL", "foo", "bar", "", "Bar", "Foo"], await Strings(connection, descending));
        Assert.AreEqual("Foo,Bar,,bar,foo", await Scalar<string>(connection,
            "SELECT string_agg(ordered_text(value),',' ORDER BY value) FROM ordered_items"));
    }

    /// <summary>
    /// Each btree strategy finds the independently specified rows, including the equality boundary.
    /// </summary>
    /// <param name="operation">The strategy operator.</param>
    /// <param name="expected">The exact matching row identities.</param>
    [TestMethod]
    [DataRow("<", "1,2")]
    [DataRow("<=", "1,2,3")]
    [DataRow("=", "3")]
    [DataRow(">=", "3,4")]
    [DataRow(">", "4")]
    public async Task CustomOperatorBtreeUsesEveryStrategy(string operation, string expected)
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, """
            CREATE TEMP TABLE btree_items(id int,value key);
            INSERT INTO btree_items VALUES(1,'Alpha#1'),(2,'ALPHA#99'),(3,'beta#2'),(4,'gamma#3'),(5,NULL);
            CREATE INDEX btree_items_index ON btree_items USING btree(value);
            ANALYZE btree_items; SET enable_seqscan=off; SET enable_bitmapscan=off;
            """);
        string query = $"SELECT id::text FROM btree_items WHERE value {operation} 'BETA#9'::key ORDER BY id";
        await AssertPlan(connection, query, "Index Scan", "btree_items_index", indexedPredicate: true);
        Assert.AreEqual(expected, string.Join(',', await Strings(connection, query)));
    }

    /// <summary>
    /// Hash indexes recheck equality after collisions and remain correct through updates and deletes.
    /// </summary>
    [TestMethod]
    public async Task CustomOperatorHashIndexRechecksCollisions()
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, """
            CREATE TEMP TABLE hash_items(id int,value key);
            INSERT INTO hash_items VALUES(1,'Alpha#1'),(2,'ALPHA#99'),(3,'collision-a#3'),(4,'collision-b#4'),(5,NULL);
            CREATE INDEX hash_items_index ON hash_items USING hash(value);
            ANALYZE hash_items; SET enable_seqscan=off; SET enable_bitmapscan=off;
            """);
        const string collision = "SELECT id::text FROM hash_items WHERE value='collision-a#0'::key ORDER BY id";
        await AssertPlan(connection, collision, "Index Scan", "hash_items_index", indexedPredicate: true);
        Assert.AreSequenceEqual(["3"], await Strings(connection, collision));
        Assert.AreSequenceEqual(["1", "2"], await Strings(connection, "SELECT id::text FROM hash_items WHERE value='alpha#0'::key ORDER BY id"));
        await Execute(connection, "UPDATE hash_items SET value='beta#33' WHERE id=3; DELETE FROM hash_items WHERE id=1");
        Assert.IsEmpty(await Strings(connection, collision));
        Assert.AreSequenceEqual(["3"], await Strings(connection, "SELECT id::text FROM hash_items WHERE value='BETA#0'::key"));
        Assert.AreSequenceEqual(["2"], await Strings(connection, "SELECT id::text FROM hash_items WHERE value='alpha#0'::key"));
    }

    /// <summary>
    /// Sorted and hashed grouping agree on logical equality, distinct values and the NULL group.
    /// </summary>
    /// <param name="hashed">Whether to require hashed aggregation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CustomOperatorGroupingPreservesEqualityAndNull(bool hashed)
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, "CREATE TEMP TABLE group_items(value key); INSERT INTO group_items VALUES('Alpha#1'),('ALPHA#99'),('beta#2'),(NULL),(NULL)");
        await Execute(connection, hashed ? "SET enable_hashagg=on; SET enable_sort=off" : "SET enable_hashagg=off");
        const string grouped = "SELECT coalesce(upper(split_part(value::text,'#',1)),'NULL')||':'||count(*)::text FROM group_items GROUP BY value";
        await AssertPlanProperty(connection, grouped, "Strategy", hashed ? "Hashed" : "Sorted");
        string[] actual = await Strings(connection, grouped);
        Array.Sort(actual, StringComparer.Ordinal);
        Assert.AreSequenceEqual(["ALPHA:2", "BETA:1", "NULL:2"], actual);
        Assert.AreEqual(2L, await Scalar<long>(connection, "SELECT count(DISTINCT value) FROM group_items"));
        Assert.AreEqual(3L, await Scalar<long>(connection, "SELECT count(*) FROM(SELECT DISTINCT value FROM group_items) distinct_values"));
        await Execute(connection, "TRUNCATE group_items");
        Assert.AreEqual(0L, await Scalar<long>(connection, "SELECT count(*) FROM(SELECT value FROM group_items GROUP BY value) empty_groups"));
        await Execute(connection, "INSERT INTO group_items VALUES('single#7')");
        Assert.AreSequenceEqual(["SINGLE:1"], await Strings(connection, grouped));
    }

    /// <summary>
    /// Actual hash and merge joins preserve duplicate pair multiplicity, normalized equality and collision rejection.
    /// </summary>
    /// <param name="algorithm">The selected join implementation.</param>
    [TestMethod]
    [DataRow("Hash Join")]
    [DataRow("Merge Join")]
    public async Task CustomOperatorFamiliesExecuteRealJoins(string algorithm)
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, """
            CREATE TEMP TABLE join_left(id int,value key); CREATE TEMP TABLE join_right(id int,value key);
            INSERT INTO join_left VALUES(1,'a#1'),(2,'A#2'),(3,'b#3'),(4,NULL),(5,'collision-a#5');
            INSERT INTO join_right VALUES(10,'A#9'),(11,'a#5'),(12,'B#3'),(13,NULL),(14,'collision-b#2'),(15,'collision-a#9');
            ANALYZE join_left; ANALYZE join_right; SET enable_nestloop=off;
            """);
        await Execute(connection, algorithm == "Hash Join" ? "SET enable_mergejoin=off; SET enable_hashjoin=on" : "SET enable_hashjoin=off; SET enable_mergejoin=on");
        const string query = "SELECT l.id::text||':'||r.id::text FROM join_left l JOIN join_right r ON l.value=r.value ORDER BY l.id,r.id";
        await AssertPlan(connection, query, algorithm);
        Assert.AreSequenceEqual(["1:10", "1:11", "2:10", "2:11", "3:12", "5:15"], await Strings(connection, query));
        Assert.AreSequenceEqual(["1:10", "1:11", "2:10", "2:11", "3:12", "5:15"], await Strings(connection,
            query.Replace("l.value=r.value", "r.value=l.value", StringComparison.Ordinal)));
    }

    /// <summary>
    /// A unique btree constraint rejects byte-distinct equal keys while preserving the rows and session.
    /// </summary>
    [TestMethod]
    public async Task CustomOperatorUniqueIndexUsesLogicalEquality()
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        await Execute(connection, "CREATE TEMP TABLE unique_items(value key UNIQUE); INSERT INTO unique_items VALUES('Alpha#1'),(NULL),(NULL)");
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, "INSERT INTO unique_items VALUES('ALPHA#99')"));
        Assert.AreEqual("23505", error.SqlState);
        await Execute(connection, "INSERT INTO unique_items VALUES('beta#2')");
        Assert.AreEqual(4L, await Scalar<long>(connection, "SELECT count(*) FROM unique_items"));
        Assert.AreEqual("Alpha#1", await Scalar<string>(connection, "SELECT value::text FROM unique_items WHERE value='ALPHA#0'::key"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Native enum opt-in uses CLR numeric order while unmarked enums retain declaration order.
    /// </summary>
    [TestMethod]
    public async Task CustomOperatorEnumsKeepTheirDistinctStorageContracts()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("Second,Last,First", await Scalar<string>(connection,
            "SELECT string_agg(value::text,',' ORDER BY value) FROM unnest(enum_range(NULL::priority)) value"));
        Assert.AreEqual("First,Second,Last", await Scalar<string>(connection,
            "SELECT string_agg(value::text,',' ORDER BY value) FROM unnest(enum_range(NULL::declared_priority)) value"));
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT 'First'::priority > 'Last'::priority AND 'Second'::priority < 'Last'::priority"));
        Assert.AreEqual("\"Low\",\"Middle\",\"High\"", await Scalar<string>(connection, """
            SELECT string_agg(value::text,',' ORDER BY value) FROM unnest(ARRAY['"High"'::base_priority,'"Low"','"Middle"']) value
            """));
        Assert.AreEqual(851917799, await Scalar<int>(connection, "SELECT base_priority_hash('\"High\"')"));
        Assert.AreEqual(-1536329377, await Scalar<int>(connection, "SELECT base_priority_hash('\"Low\"')"));
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT (SELECT typtype='e' FROM pg_type WHERE oid='priority'::regtype) AND (SELECT typtype='b' FROM pg_type WHERE oid='base_priority'::regtype)"));
    }

    /// <summary>
    /// Generated callbacks preserve packed bits, full codec values and live wrapper aliases.
    /// </summary>
    [TestMethod]
    public async Task CustomOperatorStorageModesRetainValuesAndAliases()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("True|True|True|7|42", await Scalar<string>(connection, "SELECT packed_alias('7:42')"));
        Assert.IsTrue(await Scalar<bool>(connection, """
            SELECT '7:42'::packed='7:99'::packed AND packed_send('7:42')<>packed_send('7:99')
                AND packed_hash('7:42')=packed_hash('7:99') AND '-2147483648:0'::packed < '2147483647:255'::packed
                AND '-9223372036854775808'::full < '9223372036854775807'::full
                AND encode(full_send('-9223372036854775808'),'hex')='8000000000000000'
                AND full_hash('-1')=851917799
            """));
        PostgresException identity = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<object>(connection, "SELECT '7:42'::packed='7'::full"));
        Assert.AreEqual("42883", identity.SqlState);
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT '7:42'::packed='7:0'::packed"));
    }

    /// <summary>
    /// Tagged variants share logical key operators while retaining their distinct types and non-key state.
    /// </summary>
    [TestMethod]
    public async Task CustomOperatorTaggedRootsPreserveConcreteState()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("true|true|true|text:7:keep|count:7:99|true", await Scalar<string>(connection, """
            SELECT concat_ws('|',(a=b)::text,(tagged_hash(a)=tagged_hash(b))::text,(a<c)::text,
                tagged_describe(a),tagged_describe(b),(tagged_send(a)<>tagged_send(b))::text)
            FROM(VALUES('{"$type":"text","Key":7,"Note":"keep"}'::tagged,
                '{"$type":2,"Key":7,"Count":99}'::tagged,
                '{"$type":2,"Key":9,"Count":1}'::tagged)) row(a,b,c)
            """));
        Assert.AreSequenceEqual(["text:1:first", "count:2:77"], await Strings(connection, """
            SELECT tagged_describe(value) FROM(VALUES('{"$type":2,"Key":2,"Count":77}'::tagged),
                ('{"$type":"text","Key":1,"Note":"first"}'::tagged)) row(value) ORDER BY value
            """));
    }

    /// <summary>
    /// Strictness bypasses every managed contract for NULL while present zero remains an ordinary value.
    /// </summary>
    [TestMethod]
    public async Task CustomOperatorNullsBypassManagedContracts()
    {
        await using NpgsqlConnection connection = await Open();
        await Scalar<int>(connection, "SELECT operator_calls(true)");
        Assert.IsTrue(await Scalar<bool>(connection, """
            SELECT (NULL::fault='-1'::fault) IS NULL AND ('-1'::fault=NULL::fault) IS NULL
                AND (NULL::fault=NULL::fault) IS NULL AND fault_cmp(NULL,'-2') IS NULL
                AND fault_hash(NULL) IS NULL
            """));
        Assert.AreEqual(0, await Scalar<int>(connection, "SELECT operator_calls(false)"));
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT '0'::fault='0'::fault AND '0'::fault IS NOT NULL"));
        Assert.IsGreaterThan(0, await Scalar<int>(connection, "SELECT operator_calls(false)"));
    }

    /// <summary>
    /// Exact operator and access-method metadata point to their own type, helpers and strategies.
    /// </summary>
    [TestMethod]
    public async Task CustomOperatorCatalogRetainsFamiliesAndPlannerContracts()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreSequenceEqual(["<:>:>=:scalarltsel:scalarltjoinsel:false:false", "<=:>=:>:scalarlesel:scalarlejoinsel:false:false",
            "<>:<>:=:neqsel:neqjoinsel:false:false", "=:=:<>:eqsel:eqjoinsel:true:true", ">:<:<=:scalargtsel:scalargtjoinsel:false:false",
            ">=:<=:<:scalargesel:scalargejoinsel:false:false"], await Strings(connection, """
            SELECT o.oprname||':'||c.oprname||':'||n.oprname||':'||r.proname||':'||j.proname||':'||o.oprcanhash::text||':'||o.oprcanmerge::text
            FROM pg_operator o JOIN pg_operator c ON c.oid=o.oprcom JOIN pg_operator n ON n.oid=o.oprnegate
            JOIN pg_proc r ON r.oid=o.oprrest JOIN pg_proc j ON j.oid=o.oprjoin
            WHERE o.oprleft='key'::regtype AND o.oprright='key'::regtype ORDER BY o.oprname COLLATE "C"
            """));
        Assert.AreSequenceEqual(["btree:1:<", "btree:2:<=", "btree:3:=", "btree:4:>=", "btree:5:>", "hash:1:="], await Strings(connection, """
            SELECT a.amname||':'||m.amopstrategy::text||':'||o.oprname FROM pg_opclass c JOIN pg_am a ON a.oid=c.opcmethod
            JOIN pg_amop m ON m.amopfamily=c.opcfamily JOIN pg_operator o ON o.oid=m.amopopr
            WHERE c.opcintype='key'::regtype AND c.opcdefault ORDER BY a.amname,m.amopstrategy
            """));
        Assert.AreSequenceEqual(["btree:1:key_cmp", "hash:1:key_hash"], await Strings(connection, """
            SELECT a.amname||':'||p.amprocnum::text||':'||f.proname FROM pg_opclass c JOIN pg_am a ON a.oid=c.opcmethod
            JOIN pg_amproc p ON p.amprocfamily=c.opcfamily JOIN pg_proc f ON f.oid=p.amproc
            WHERE c.opcintype='key'::regtype ORDER BY a.amname,p.amprocnum
            """));
        Assert.AreEqual(8L, await Scalar<long>(connection, """
            SELECT count(*) FROM pg_proc WHERE pronamespace='derived_ops'::regnamespace
                AND proname IN('key_eq','key_ne','key_lt','key_le','key_gt','key_ge','key_cmp','key_hash')
                AND proisstrict AND provolatile='i' AND proparallel='s'
            """));
    }

    /// <summary>
    /// Preserves exact custom and ordinary errors before a successful next operation on the same backend.
    /// </summary>
    /// <param name="sql">The failing callback operation.</param>
    /// <param name="state">The exact SQLSTATE.</param>
    /// <param name="message">The exact primary diagnostic.</param>
    [TestMethod]
    [DataRow("SELECT '-1'::fault='0'::fault", "P8101", "derived equality failed")]
    [DataRow("SELECT value FROM(VALUES('-2'::fault),('0'::fault)) row(value) ORDER BY value", "P8102", "derived ordering failed")]
    [DataRow("SELECT fault_hash('-3')", "P8103", "derived hashing failed")]
    [DataRow("SELECT '-4'::fault='0'::fault", "38000", "ordinary derived failure")]
    public async Task CustomOperatorErrorsRecoverInSameBackend(string sql, string state, string message)
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, sql));
        Assert.AreEqual(state, error.SqlState);
        Assert.AreEqual(message, error.MessageText);
        await using var command = new NpgsqlCommand("SELECT operator_guarded($1)", connection);
        command.Parameters.AddWithValue(sql);
        Assert.AreEqual(state + "|True", await command.ExecuteScalarAsync(context.CancellationToken));
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT 'good#1'::key='GOOD#2'::key"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Failed support callbacks leave index creation and inserts atomic while later index work succeeds.
    /// </summary>
    /// <param name="method">The access method invoking the failing support routine.</param>
    /// <param name="bad">The error-selecting value.</param>
    /// <param name="state">The exact expected SQLSTATE.</param>
    [TestMethod]
    [DataRow("btree", -2, "P8102")]
    [DataRow("hash", -3, "P8103")]
    public async Task CustomOperatorIndexErrorsPreserveRowsAndRecovery(string method, int bad, string state)
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        await Execute(connection, $"CREATE TEMP TABLE index_faults(value fault); INSERT INTO index_faults VALUES('0'),('{bad}')");
        PostgresException build = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection,
            $"CREATE INDEX fault_index ON index_faults USING {method}(value)"));
        Assert.AreEqual(state, build.SqlState);
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT to_regclass('pg_temp.fault_index') IS NULL AND (SELECT count(*) FROM index_faults)=2"));
        await Execute(connection, $"DELETE FROM index_faults WHERE value::text='{bad}'");
        Assert.AreSequenceEqual(["0"], await Strings(connection, "SELECT value::text FROM index_faults"));
        await Execute(connection, $"TRUNCATE index_faults; INSERT INTO index_faults VALUES('0'); CREATE INDEX fault_index ON index_faults USING {method}(value)");
        PostgresException insert = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, $"INSERT INTO index_faults VALUES('{bad}')"));
        Assert.AreEqual(state, insert.SqlState);
        await Execute(connection, "INSERT INTO index_faults VALUES('7')");
        Assert.AreEqual(2L, await Scalar<long>(connection, "SELECT count(*) FROM index_faults"));
        await Execute(connection, "SET enable_seqscan=off; SET enable_bitmapscan=off; SET enable_indexonlyscan=off");
        const string recovered = "SELECT value::text FROM index_faults WHERE value='7'::fault";
        await AssertPlan(connection, recovered, "Index Scan", "fault_index", indexedPredicate: true);
        Assert.AreSequenceEqual(["7"], await Strings(connection, recovered));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Opens a fresh backend with explicitly selected custom operators in its lookup path.
    /// </summary>
    private async Task<NpgsqlConnection> Open()
    {
        NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await Execute(connection, "SET search_path=derived_ops,pg_catalog");
        return connection;
    }

    /// <summary>
    /// Executes statements whose results are not part of the assertion.
    /// </summary>
    private async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }

    /// <summary>
    /// Reads one exact typed SQL scalar.
    /// </summary>
    private async Task<T> Scalar<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(context.CancellationToken));
    }

    /// <summary>
    /// Reads all independently checked text rows without dropping duplicates.
    /// </summary>
    private async Task<string[]> Strings(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
        var result = new List<string>();
        while (await reader.ReadAsync(context.CancellationToken))
        {
            result.Add(reader.GetString(0));
        }

        return [.. result];
    }

    /// <summary>
    /// Requires an actual executor plan node and, for index probes, the intended index identity.
    /// </summary>
    private async Task AssertPlan(NpgsqlConnection connection, string sql, string node, string? index = null,
        bool indexedPredicate = false, string? direction = null)
    {
        using JsonDocument plan = JsonDocument.Parse(await Scalar<string>(connection, "EXPLAIN(FORMAT JSON,COSTS OFF) " + sql));
        Assert.Contains(item => item.GetProperty("Node Type").GetString() == node && Has(item, "Index Name", index) &&
            (!indexedPredicate || HasCondition(item)) && Has(item, "Scan Direction", direction),
            PlanNodes(plan.RootElement[0].GetProperty("Plan")), plan.RootElement.ToString());
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
    /// Verifies aggregation strategy without confusing an enclosing plain aggregate with the grouping node.
    /// </summary>
    private async Task AssertPlanProperty(NpgsqlConnection connection, string sql, string property, string expected)
    {
        using JsonDocument plan = JsonDocument.Parse(await Scalar<string>(connection, "EXPLAIN(FORMAT JSON,COSTS OFF) " + sql));
        Assert.Contains(item => item.TryGetProperty(property, out JsonElement value) && value.GetString() == expected,
            PlanNodes(plan.RootElement[0].GetProperty("Plan")), plan.RootElement.ToString());
    }

    /// <summary>
    /// Traverses a PostgreSQL plan while preserving every nested node.
    /// </summary>
    private static IEnumerable<JsonElement> PlanNodes(JsonElement plan)
    {
        yield return plan;
        if (plan.TryGetProperty("Plans", out JsonElement children))
        {
            foreach (JsonElement child in children.EnumerateArray())
            {
                foreach (JsonElement nested in PlanNodes(child))
                {
                    yield return nested;
                }
            }
        }
    }
}
