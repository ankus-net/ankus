using System.Buffers.Binary;
using System.Text.Json;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Exercises declaration-owned SQL replacements through real typed values, native errors and index scans.
/// </summary>
/// <param name="context">The current test context.</param>
[TestClass]
public sealed class DeclarationSqlTests(TestContext context)
{
    private const string IndexedType = "declaration_sql.\"éééééééééééééééééééééééééééééé\"";

    /// <summary>
    /// Consumer-named I/O preserves exact storage and NULL without cross-wiring type export tokens.
    /// </summary>
    /// <param name="kind">The independently serialized storage contract.</param>
    [TestMethod]
    [DataRow("json")]
    [DataRow("text")]
    [DataRow("native")]
    public async Task ReplacementTypeSqlPreservesTextBinaryAndArrays(string kind)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        string input = kind == "json" ? "{\"Number\":42}" : kind == "text" ? "N:42" : "16909060";
        string expected = input;
        Assert.AreEqual(expected, await Scalar<string>(connection,
            $"SELECT declaration_sql.{kind}_echo(declaration_sql.{kind}_input('{input}'::cstring))::text"));
        Assert.AreSequenceEqual(Payload(kind), await Scalar<byte[]>(connection,
            $"SELECT declaration_sql.{kind}_send(declaration_sql.{kind}_echo('{input}'::declaration_sql.{kind}_value))"));
        string nullValue = kind == "native" ? "declaration_sql.native_null()" : $"NULL::declaration_sql.{kind}_value";
        Assert.IsTrue(await Scalar<bool>(connection,
            $"SELECT declaration_sql.{kind}_echo({nullValue}) IS NULL AND declaration_sql.{kind}_send({nullValue}) IS NULL"));
        string zero = kind == "json" ? "{\"Number\":0}" : kind == "text" ? "N:0" : "0";
        string negative = kind == "json" ? "{\"Number\":-7}" : kind == "text" ? "N:-7" : "-7";
        await Execute(connection, $"""
            CREATE TEMP TABLE declaration_array(value declaration_sql.{kind}_value[]);
            INSERT INTO declaration_array SELECT array_fill({nullValue},ARRAY[2,2],ARRAY[2,-1]);
            UPDATE declaration_array SET value[2][-1]='{input}'::declaration_sql.{kind}_value;
            UPDATE declaration_array SET value[3][-1]='{zero}'::declaration_sql.{kind}_value;
            UPDATE declaration_array SET value[3][0]='{negative}'::declaration_sql.{kind}_value;
            """);
        await using var arrays = new NpgsqlCommand($"""
            WITH output AS(SELECT declaration_sql.{kind}_array(value) AS value FROM declaration_array)
            SELECT array_dims(value),value[2][-1]::text,value[2][0] IS NULL,value[3][-1]::text,value[3][0]::text,
                pg_typeof(value)='declaration_sql.{kind}_value[]'::regtype,
                cardinality(declaration_sql.{kind}_array(ARRAY[]::declaration_sql.{kind}_value[])),
                declaration_sql.{kind}_array(NULL) IS NULL FROM output
            """, connection);
        await using (NpgsqlDataReader reader = await arrays.ExecuteReaderAsync(context.CancellationToken))
        {
            Assert.IsTrue(await reader.ReadAsync(context.CancellationToken));
            Assert.AreEqual("[2:3][-1:0]", reader.GetString(0));
            Assert.AreEqual(expected, reader.GetString(1));
            Assert.IsTrue(reader.GetBoolean(2));
            Assert.AreEqual(zero, reader.GetString(3));
            Assert.AreEqual(negative, reader.GetString(4));
            Assert.IsTrue(reader.GetBoolean(5));
            Assert.AreEqual(0, reader.GetInt32(6));
            Assert.IsTrue(reader.GetBoolean(7));
            Assert.IsFalse(await reader.ReadAsync(context.CancellationToken));
        }

        Assert.AreEqual($"{kind}_input:{kind}_output:{kind}_receive:{kind}_send:-1:false", await Scalar<string>(connection, $"""
            SELECT i.proname||':'||o.proname||':'||r.proname||':'||s.proname||':'||t.typlen::text||':'||t.typbyval::text
            FROM pg_type t JOIN pg_proc i ON i.oid=t.typinput JOIN pg_proc o ON o.oid=t.typoutput
            JOIN pg_proc r ON r.oid=t.typreceive JOIN pg_proc s ON s.oid=t.typsend
            WHERE t.oid='declaration_sql.{kind}_value'::regtype
            """));
        Assert.IsTrue(await Scalar<bool>(connection,
            $"SELECT to_regprocedure('declaration_sql.{kind}_value_in(cstring)') IS NULL"));
        if (kind == "native")
        {
            Assert.AreEqual("A5:16909060", await Scalar<string>(connection,
                "SELECT declaration_sql.native_describe(declaration_sql.native_echo('16909060'))"));
        }
    }

    /// <summary>
    /// Text-only replacement retains generated JSON and typed callbacks without adding binary registrations.
    /// </summary>
    [TestMethod]
    public async Task ReplacementTextOnlyTypePreservesJsonAndIdentity()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        Assert.AreEqual("{\"Number\":0}", await Scalar<string>(connection, """
            SELECT declaration_sql.plain_echo(declaration_sql.plain_input('{"Number":0}'::cstring))::text
            """));
        Assert.AreEqual("{\"Number\":-2147483648}", await Scalar<string>(connection, """
            SELECT declaration_sql.plain_echo('{"Number":-2147483648}')::text
            """));
        Assert.IsTrue(await Scalar<bool>(connection, """
            SELECT declaration_sql.plain_echo(NULL) IS NULL AND typreceive=0 AND typsend=0 AND typlen=-1 AND NOT typbyval
                AND typinput='declaration_sql.plain_input(cstring)'::regprocedure
                AND typoutput='declaration_sql.plain_output(declaration_sql.plain_value)'::regprocedure
                AND to_regprocedure('declaration_sql.plain_value_in(cstring)') IS NULL
            FROM pg_type WHERE oid='declaration_sql.plain_value'::regtype
            """));
        PostgresException wrong = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection,
            "SELECT declaration_sql.json_echo('7'::declaration_sql.native_value)"));
        Assert.AreEqual("42883", wrong.SqlState);
        Assert.AreEqual("function declaration_sql.json_echo(declaration_sql.native_value) does not exist", wrong.MessageText);
        Assert.AreEqual("{\"Number\":7}", await Scalar<string>(connection,
            "SELECT declaration_sql.plain_echo('{\"Number\":7}')::text"));
    }

    /// <summary>
    /// Independent native/CBOR payloads and COPY framing survive receive/send with a separate SQL NULL row.
    /// </summary>
    /// <param name="kind">The storage contract.</param>
    [TestMethod]
    [DataRow("json")]
    [DataRow("text")]
    [DataRow("native")]
    public async Task ReplacementTypeBinaryReceiveUsesIndependentBytes(string kind)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await Execute(connection, $"CREATE TEMP TABLE declaration_binary(id bigint GENERATED ALWAYS AS IDENTITY,value declaration_sql.{kind}_value)");
        byte[] fixture = CopyData(Payload(kind), null);
        await Import(connection, fixture);
        string text = kind == "json" ? "{\"Number\":42}" : kind == "text" ? "N:42" : "16909060";
        Assert.AreSequenceEqual([text, "<NULL>"], await Strings(connection,
            "SELECT coalesce(value::text,'<NULL>') FROM declaration_binary ORDER BY id"));
        using var output = new MemoryStream();
        await using (Stream copy = await connection.BeginRawBinaryCopyAsync(
            "COPY(SELECT value FROM declaration_binary ORDER BY id) TO STDOUT(FORMAT BINARY)", context.CancellationToken))
        {
            await copy.CopyToAsync(output, context.CancellationToken);
        }

        Assert.AreSequenceEqual(fixture, output.ToArray());
        if (kind == "native")
        {
            Assert.AreEqual("A5:16909060", await Scalar<string>(connection,
                "SELECT declaration_sql.native_describe(value) FROM declaration_binary WHERE value IS NOT NULL"));
        }
    }

    /// <summary>
    /// A malformed later binary row rolls back earlier received rows and preserves same-backend recovery.
    /// </summary>
    /// <param name="kind">The storage contract.</param>
    /// <param name="suffix">Whether the invalid payload is trailing rather than truncated.</param>
    [TestMethod]
    [DataRow("json", false)]
    [DataRow("json", true)]
    [DataRow("text", false)]
    [DataRow("text", true)]
    [DataRow("native", false)]
    [DataRow("native", true)]
    public async Task ReplacementTypeBinaryErrorsRollbackAndRecover(string kind, bool suffix)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int process = connection.ProcessID;
        await Execute(connection, $"CREATE TEMP TABLE declaration_binary(id bigint GENERATED ALWAYS AS IDENTITY,value declaration_sql.{kind}_value)");
        byte[] valid = Payload(kind);
        await Import(connection, CopyData(valid));
        byte[] invalid = suffix ? [.. valid, 0] : valid[..^1];
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Import(connection, CopyData(valid, invalid)));
        Assert.AreEqual("22P03", error.SqlState);
        Assert.AreEqual(kind == "native" ? "Invalid native-layout custom-type value." : "Invalid CBOR custom-type value.", error.MessageText);
        Assert.AreEqual(1L, await Scalar<long>(connection, "SELECT count(*) FROM declaration_binary"));
        Assert.AreSequenceEqual(valid, await Scalar<byte[]>(connection,
            $"SELECT declaration_sql.{kind}_send(value) FROM declaration_binary"));
        await Import(connection, CopyData(valid, null));
        string expected = kind == "json" ? "{\"Number\":42}" : kind == "text" ? "N:42" : "16909060";
        Assert.AreSequenceEqual([expected, expected, "<NULL>"], await Strings(connection,
            "SELECT coalesce(value::text,'<NULL>') FROM declaration_binary ORDER BY id"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Type input/output failures retain exact diagnostics and complete codec cleanup before recovery.
    /// </summary>
    /// <param name="sql">The failing conversion.</param>
    /// <param name="state">The exact SQLSTATE.</param>
    /// <param name="message">The exact primary message.</param>
    /// <param name="textFinally">The expected completed custom parser count.</param>
    [TestMethod]
    [DataRow("SELECT 'invalid'::declaration_sql.json_value", "22P02", "Invalid JSON custom-type value.", 0)]
    [DataRow("SELECT 'invalid'::declaration_sql.plain_value", "22P02", "Invalid JSON custom-type value.", 0)]
    [DataRow("SELECT 'invalid'::declaration_sql.text_value", "P8301", "replacement text input failed", 1)]
    [DataRow("SELECT declaration_sql.text_input('N:-2'::cstring)::text", "P8302", "replacement text output failed", 1)]
    [DataRow("SELECT 'invalid'::declaration_sql.native_value", "22P02", "replacement native input failed", 0)]
    [DataRow("SELECT declaration_sql.native_input(NULL::cstring)", "22004", "replacement native input needs text", 0)]
    [DataRow("SELECT '{NULL}'::declaration_sql.native_value[]", "22004", "replacement native input needs text", 0)]
    public async Task ReplacementTypeTextErrorsRecover(string sql, string state, string message, int textFinally)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int process = connection.ProcessID;
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, sql));
        Assert.AreEqual(state, error.SqlState);
        Assert.AreEqual(message, error.MessageText);
        if (state == "P8301")
        {
            Assert.AreEqual("expected N:number", error.Detail);
            Assert.AreEqual("supply a prefixed integer", error.Hint);
        }

        Assert.AreSequenceEqual([textFinally, 0], await Scalar<int[]>(connection, "SELECT declaration_sql.cleanup_counts()"));
        Assert.AreEqual("N:42", await Scalar<string>(connection, "SELECT declaration_sql.text_echo('N:42')::text"));
        Assert.AreEqual("A5:42", await Scalar<string>(connection, "SELECT declaration_sql.native_describe(declaration_sql.native_echo('42'))"));
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT declaration_sql.native_echo(declaration_sql.native_null()) IS NULL"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Literal and manually supplied enum order remain independent of numeric mapping and nominal type identity.
    /// </summary>
    [TestMethod]
    public async Task ReplacementEnumSqlPreservesLabelsAndIdentity()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int process = connection.ProcessID;
        Assert.AreSequenceEqual(["bêta:-2", ":0", "alpha:8"], await Strings(connection, """
            SELECT value::text||':'||declaration_sql.mood_number(declaration_sql.mood_echo(value))::text
            FROM unnest(enum_range(NULL::declaration_sql.mood)) value ORDER BY value
            """));
        Assert.AreSequenceEqual(["Blue:-7", "Red:31"], await Strings(connection, """
            SELECT value::text||':'||declaration_sql.manual_number(value)::text
            FROM unnest(enum_range(NULL::declaration_sql.manual_mood)) value ORDER BY value
            """));
        Assert.IsTrue(await Scalar<bool>(connection, """
            WITH output AS(SELECT declaration_sql.mood_array('[2:3][-1:0]={{"",NULL},{bêta,alpha}}') AS value)
            SELECT array_dims(value)='[2:3][-1:0]' AND value[2][-1]::text='' AND value[2][0] IS NULL
                AND value[3][-1]::text='bêta' AND value[3][0]::text='alpha'
                AND pg_typeof(value)='declaration_sql.mood[]'::regtype
                AND cardinality(declaration_sql.mood_array('{}'))=0 AND declaration_sql.mood_array(NULL) IS NULL
                AND declaration_sql.mood_echo(NULL) IS NULL AND declaration_sql.manual_number(NULL) IS NULL FROM output
            """));
        PostgresException wrong = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection,
            "SELECT declaration_sql.mood_echo('Red'::declaration_sql.manual_mood)"));
        Assert.AreEqual("42883", wrong.SqlState);
        Assert.AreEqual("function declaration_sql.mood_echo(declaration_sql.manual_mood) does not exist", wrong.MessageText);
        PostgresException invalid = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection,
            "SELECT 'missing'::declaration_sql.mood"));
        Assert.AreEqual("22P02", invalid.SqlState);
        Assert.AreEqual("invalid input value for enum declaration_sql.mood: \"missing\"", invalid.MessageText);
        Assert.AreEqual(8, await Scalar<int>(connection, "SELECT declaration_sql.mood_number('alpha')"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Parent replace/disable modes execute retained, replaced and independently supplied helpers.
    /// </summary>
    [TestMethod]
    public async Task ReplacementAggregateSqlRetainsIndependentHelpers()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int process = connection.ProcessID;
        Assert.AreEqual(13, await Scalar<int>(connection,
            "SELECT declaration_sql.literal_total(value) FROM(VALUES(1),(NULL),(2)) input(value)"));
        Assert.AreEqual(10, await Scalar<int>(connection,
            "SELECT declaration_sql.literal_total(value) FROM(SELECT 1 AS value WHERE false) input"));
        Assert.AreEqual(10, await Scalar<int>(connection,
            "SELECT declaration_sql.literal_total(value) FROM(VALUES(NULL::integer),(NULL)) input(value)"));
        Assert.AreEqual(6, await Scalar<int>(connection,
            "SELECT declaration_sql.visible_total(value) FROM(VALUES(1),(NULL),(2),(3)) input(value)"));
        Assert.AreEqual(306, await Scalar<int>(connection,
            "SELECT declaration_sql.supplied_total(value) FROM(VALUES(1),(NULL),(2),(3)) input(value)"));
        Assert.AreSequenceEqual([0, 5], await Scalar<int[]>(connection, "SELECT declaration_sql.cleanup_counts()"));
        Assert.IsTrue(await Scalar<bool>(connection, """
            SELECT to_regprocedure('declaration_sql.replaced_total(integer)') IS NULL
                AND to_regprocedure('declaration_sql.hidden_total(integer)') IS NULL
                AND to_regprocedure('declaration_sql.supplied_parent(integer)') IS NULL
                AND to_regprocedure('declaration_sql.replaced_step(integer,integer)') IS NULL
            """));
        Assert.AreSequenceEqual(["literal_total:custom_step:c", "supplied_total:supplied_step:sql", "visible_total:hidden_step:c"], await Strings(connection, """
            SELECT p.proname||':'||h.proname||':'||l.lanname FROM pg_proc p JOIN pg_aggregate a ON a.aggfnoid=p.oid
            JOIN pg_proc h ON h.oid=a.aggtransfn JOIN pg_language l ON l.oid=h.prolang
            WHERE p.pronamespace='declaration_sql'::regnamespace ORDER BY p.proname
            """));
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection,
            "SELECT declaration_sql.literal_total(value ORDER BY position) FROM(VALUES(1,1),(2,-99)) input(position,value)"));
        Assert.AreEqual("P8304", error.SqlState);
        Assert.AreEqual("replacement aggregate failed", error.MessageText);
        Assert.AreSequenceEqual([0, 7], await Scalar<int[]>(connection, "SELECT declaration_sql.cleanup_counts()"));
        Assert.AreEqual(14, await Scalar<int>(connection,
            "SELECT declaration_sql.literal_total(value) FROM(VALUES(4)) input(value)"));
        Assert.AreSequenceEqual([0, 8], await Scalar<int[]>(connection, "SELECT declaration_sql.cleanup_counts()"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Disabled families leave executable comparison/hash helpers and operators without an accidental default class.
    /// </summary>
    [TestMethod]
    public async Task DisabledFamilySqlRetainsExecutableHelpers()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        Assert.AreEqual("-1:0:1:34", await Scalar<string>(connection, """
            SELECT declaration_sql.unindexed_cmp('{"Number":1}','{"Number":2}')::text||':'||
                declaration_sql.unindexed_cmp('{"Number":2}','{"Number":2}')::text||':'||
                declaration_sql.unindexed_cmp('{"Number":3}','{"Number":2}')::text||':'||
                declaration_sql.unindexed_hash('{"Number":2}')::text
            """));
        Assert.IsTrue(await Scalar<bool>(connection, """
            SELECT '{"Number":1}'::declaration_sql.unindexed OPERATOR(declaration_sql.<) '{"Number":2}'::declaration_sql.unindexed
                AND '{"Number":2}'::declaration_sql.unindexed OPERATOR(declaration_sql.=) '{"Number":2}'::declaration_sql.unindexed
                AND declaration_sql.unindexed_cmp(NULL,'{"Number":2}') IS NULL AND declaration_sql.unindexed_hash(NULL) IS NULL
                AND NOT EXISTS(SELECT 1 FROM pg_opclass WHERE opcintype='declaration_sql.unindexed'::regtype)
                AND NOT EXISTS(SELECT 1 FROM pg_opfamily WHERE opfnamespace='declaration_sql'::regnamespace
                    AND opfname IN('unindexed_btree_ops','unindexed_hash_ops'))
            """));
        await Execute(connection, "CREATE TEMP TABLE unindexed_values(value declaration_sql.unindexed); INSERT INTO unindexed_values VALUES('{\"Number\":2}')");
        foreach (string method in new[] { "btree", "hash" })
        {
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection,
                $"CREATE INDEX absent_{method} ON unindexed_values USING {method}(value)"));
            Assert.AreEqual("42704", error.SqlState);
            Assert.AreEqual($"data type declaration_sql.unindexed has no default operator class for access method \"{method}\"", error.MessageText);
        }

        Assert.AreEqual(34, await Scalar<int>(connection, "SELECT declaration_sql.unindexed_hash(value) FROM unindexed_values"));
    }

    /// <summary>
    /// Long Unicode helper tokens bind executable custom classes whose index probes exclude hash collisions.
    /// </summary>
    /// <param name="method">The independently replaced access method.</param>
    [TestMethod]
    [DataRow("btree")]
    [DataRow("hash")]
    public async Task ReplacementFamilySqlRetainsHelpersAndExecutesIndexes(string method)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int process = connection.ProcessID;
        await Execute(connection, $$"""
            CREATE TEMP TABLE indexed_values(id integer,value {{IndexedType}});
            INSERT INTO indexed_values VALUES(1,'{"Number":0}'),(2,'{"Number":2}'),(3,'{"Number":2}'),(4,'{"Number":5}'),(5,NULL);
            CREATE INDEX declaration_index ON indexed_values USING {{method}}(value declaration_sql.literal_{{method}}_ops);
            ANALYZE indexed_values; SET enable_seqscan=off; SET enable_bitmapscan=off;
            """);
        string operation = method == "btree" ? ">=" : "=";
        string query = $$"""
            SELECT id::text FROM indexed_values WHERE value OPERATOR(declaration_sql.{{operation}}) '{"Number":2}'::{{IndexedType}} ORDER BY id
            """;
        await AssertIndexPlan(connection, query);
        Assert.AreSequenceEqual(method == "btree" ? ["2", "3", "4"] : ["2", "3"], await Strings(connection, query));
        Assert.AreSequenceEqual(["5"], await Strings(connection, "SELECT id::text FROM indexed_values WHERE value IS NULL"));
        Assert.IsTrue(await Scalar<bool>(connection, $"""
            SELECT NOT c.opcdefault AND c.opcname='literal_{method}_ops' AND f.opfname=c.opcname
                AND p.amprocnum=1 AND p.amproclefttype=c.opcintype AND p.amprocrighttype=c.opcintype
                AND octet_length(h.proname)<=63 AND h.proname LIKE 'ankus_%_{(method == "btree" ? "cmp" : "hash")}'
                AND h.pronamespace='declaration_sql'::regnamespace
            FROM pg_opclass c JOIN pg_opfamily f ON f.oid=c.opcfamily JOIN pg_am a ON a.oid=c.opcmethod
            JOIN pg_amproc p ON p.amprocfamily=f.oid JOIN pg_proc h ON h.oid=p.amproc
            WHERE c.opcintype='{IndexedType}'::regtype AND a.amname='{method}'
            """));
        int bad = method == "btree" ? -99 : -98;
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection,
            $$"""INSERT INTO indexed_values VALUES(6,'{"Number":{{bad}}}')"""));
        Assert.AreEqual(method == "btree" ? "P8305" : "P8306", error.SqlState);
        Assert.AreEqual(method == "btree" ? "replacement comparison failed" : "replacement hash failed", error.MessageText);
        Assert.AreEqual(5L, await Scalar<long>(connection, "SELECT count(*) FROM indexed_values"));
        await Execute(connection, "INSERT INTO indexed_values VALUES(7,'{\"Number\":2}'); REINDEX INDEX declaration_index");
        await AssertIndexPlan(connection, query);
        Assert.AreSequenceEqual(method == "btree" ? ["2", "3", "4", "7"] : ["2", "3", "7"], await Strings(connection, query));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// The complete shared extension installs in LATIN1 while replacement types and long-name index helpers remain executable.
    /// </summary>
    [TestMethod]
    [DoNotParallelize]
    public async Task DeclarationSqlLatin1InstallationPreservesTypesAndFamilies()
    {
        string database = "declaration_latin1_" + Guid.NewGuid().ToString("N");
        await using NpgsqlConnection administrator = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await Execute(administrator, $"CREATE DATABASE {database} TEMPLATE template0 ENCODING 'LATIN1' LC_COLLATE 'C' LC_CTYPE 'C'");
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Database = database, Pooling = false };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(context.CancellationToken);
            int process = connection.ProcessID;
            await Execute(connection, "CREATE EXTENSION ankus_test");
            Assert.AreEqual("LATIN1", await Scalar<string>(connection, "SHOW server_encoding"));
            Assert.AreEqual("bêta:-2", await Scalar<string>(connection,
                "SELECT declaration_sql.mood_echo('bêta')::text||':'||declaration_sql.mood_number('bêta')::text"));
            Assert.AreEqual("{\"Number\":42}", await Scalar<string>(connection, """
                SELECT declaration_sql.json_echo(declaration_sql.json_input('{"Number":42}'::cstring))::text
                """));
            Assert.AreSequenceEqual(Payload("json"), await Scalar<byte[]>(connection, """
                SELECT declaration_sql.json_send(declaration_sql.json_echo('{"Number":42}'))
                """));
            foreach (string method in new[] { "btree", "hash" })
            {
                await Execute(connection, $$"""
                    CREATE TEMP TABLE latin1_values(id integer,value {{IndexedType}});
                    INSERT INTO latin1_values VALUES(1,'{"Number":1}'),(2,'{"Number":2}'),(3,'{"Number":2}'),(4,NULL);
                    CREATE INDEX declaration_index ON latin1_values USING {{method}}(value declaration_sql.literal_{{method}}_ops);
                    ANALYZE latin1_values; SET enable_seqscan=off; SET enable_bitmapscan=off;
                    """);
                string operation = method == "btree" ? ">=" : "=";
                string query = $$"""
                    SELECT id::text FROM latin1_values WHERE value OPERATOR(declaration_sql.{{operation}}) '{"Number":2}'::{{IndexedType}} ORDER BY id
                    """;
                await AssertIndexPlan(connection, query);
                Assert.AreSequenceEqual(["2", "3"], await Strings(connection, query));
                if (method == "hash")
                {
                    PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection,
                        "INSERT INTO latin1_values VALUES(5,'{\"Number\":-98}')"));
                    Assert.AreEqual("P8306", error.SqlState);
                    Assert.AreEqual("replacement hash failed", error.MessageText);
                    Assert.AreEqual(4L, await Scalar<long>(connection, "SELECT count(*) FROM latin1_values"));
                    Assert.AreSequenceEqual(["2", "3"], await Strings(connection, query));
                }

                await Execute(connection, "DROP TABLE latin1_values");
            }

            Assert.AreEqual("bêta", await Scalar<string>(connection, "SELECT declaration_sql.mood_echo('bêta')::text"));
            Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", administrator);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Returns independently specified CBOR or packed native bytes without calling a production serializer.
    /// </summary>
    private static byte[] Payload(string kind) => kind == "native"
        ? BitConverter.IsLittleEndian ? [0xA5, 4, 3, 2, 1] : [0xA5, 1, 2, 3, 4]
        : [0xA1, 0x66, 0x4E, 0x75, 0x6D, 0x62, 0x65, 0x72, 0x18, 0x2A];

    /// <summary>
    /// Frames independent payloads and SQL NULL fields using PostgreSQL's documented binary COPY protocol.
    /// </summary>
    private static byte[] CopyData(params byte[]?[] values)
    {
        byte[] copy = new byte[21 + values.Sum(static value => 6 + (value?.Length ?? 0))];
        ReadOnlySpan<byte> signature = [80, 71, 67, 79, 80, 89, 10, 255, 13, 10, 0];
        signature.CopyTo(copy);
        int offset = 19;
        foreach (byte[]? value in values)
        {
            BinaryPrimitives.WriteInt16BigEndian(copy.AsSpan(offset), 1);
            BinaryPrimitives.WriteInt32BigEndian(copy.AsSpan(offset + 2), value?.Length ?? -1);
            value?.CopyTo(copy, offset + 6);
            offset += 6 + (value?.Length ?? 0);
        }

        BinaryPrimitives.WriteInt16BigEndian(copy.AsSpan(offset), -1);
        return copy;
    }

    /// <summary>
    /// Completes COPY disposal so receive errors are observed before assertions resume.
    /// </summary>
    private async Task Import(NpgsqlConnection connection, byte[] bytes)
    {
        await using Stream copy = await connection.BeginRawBinaryCopyAsync(
            "COPY declaration_binary(value) FROM STDIN(FORMAT BINARY)", context.CancellationToken);
        await copy.WriteAsync(bytes, context.CancellationToken);
    }

    /// <summary>
    /// Executes a statement whose returned values are asserted separately.
    /// </summary>
    private async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }

    /// <summary>
    /// Reads one exact typed scalar.
    /// </summary>
    private async Task<T> Scalar<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(context.CancellationToken));
    }

    /// <summary>
    /// Reads every ordered text row including duplicate witnesses.
    /// </summary>
    private async Task<string[]> Strings(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
        var rows = new List<string>();
        while (await reader.ReadAsync(context.CancellationToken))
        {
            rows.Add(reader.GetString(0));
        }

        return [.. rows];
    }

    /// <summary>
    /// Requires the plan to use the named constrained index scan; compatible planner settings alone do not pass.
    /// </summary>
    private async Task AssertIndexPlan(NpgsqlConnection connection, string sql)
    {
        using JsonDocument plan = JsonDocument.Parse(await Scalar<string>(connection, "EXPLAIN(FORMAT JSON,COSTS OFF) " + sql));
        Assert.Contains(node => node.GetProperty("Node Type").GetString() == "Index Scan" &&
            node.TryGetProperty("Index Name", out JsonElement name) && name.GetString() == "declaration_index" &&
            node.TryGetProperty("Index Cond", out JsonElement condition) && !string.IsNullOrWhiteSpace(condition.GetString()),
            PlanNodes(plan.RootElement[0].GetProperty("Plan")), plan.RootElement.ToString());
    }

    /// <summary>
    /// Enumerates scans below enclosing sort or aggregate nodes.
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
