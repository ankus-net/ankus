using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Executes pgrx's operators example through the published operators sample.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
[DoNotParallelize]
public sealed class OperatorsExampleTests(TestContext context)
{
    /// <summary>
    /// Mirrors pgrx's PgVarlena test and checks every generated operator, field order, extremes and packed storage.
    /// </summary>
    [TestMethod]
    public Task OperatorsSampleComparesPackedValues()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(OperatorsSampleComparesPackedValues), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, "CREATE EXTENSION ankus_operators", token);
            Assert.AreEqual("1;2;3;[1, 2, 3, 4, 5]", await Scalar<string>(connection, transaction,
                "SELECT '1;2;3;[1,2,3,4,5]'::pgvarlenathing::text", token));
            Assert.IsTrue(await Scalar<bool>(connection, transaction,
                "SELECT '1;2;3;[1,2,3,4,5]'::pgvarlenathing < '2;2;3;[1,2,3,4,5]'::pgvarlenathing", token));
            Assert.IsTrue(await Scalar<bool>(connection, transaction,
                "SELECT '2;2;3;[1,2,3,4,5]'::pgvarlenathing > '1;2;3;[1,2,3,4,5]'::pgvarlenathing", token));
            Assert.IsTrue(await Scalar<bool>(connection, transaction,
                "SELECT '1;2;3;[1,2,3,4,5]'::pgvarlenathing = '1;2;3;[1,2,3,4,5]'::pgvarlenathing", token));

            Assert.AreEqual("t,f,t,t,f,t,t,t,f,t", await Scalar<string>(connection, transaction, """
                SELECT concat_ws(',',
                    '1;2;3;[1,2,3,4,5]'::pgvarlenathing < '1;3;0;[0,0,0,0,0]',
                    '1;3;3;[1,2,3,4,5]'::pgvarlenathing < '1;2;9;[9,9,9,9,9]',
                    '1;2;-1;[9,9,9,9,9]'::pgvarlenathing < '1;2;0;[0,0,0,0,0]',
                    '1;2;3;[1,2,3,4,5]'::pgvarlenathing < '1;2;3;[1,2,3,4,6]',
                    '1;2;3;[1,2,3,5,0]'::pgvarlenathing < '1;2;3;[1,2,3,4,255]',
                    '18446744073709551615;0;0;[0,0,0,0,0]'::pgvarlenathing > '0;0;0;[0,0,0,0,0]',
                    '1;2;3;[1,2,3,4,5]'::pgvarlenathing <> '1;2;3;[1,2,3,4,6]',
                    '1;2;3;[1,2,3,4,5]'::pgvarlenathing <= '1;2;3;[1,2,3,4,5]',
                    '1;2;3;[1,2,3,4,5]'::pgvarlenathing >= '1;2;3;[1,2,3,4,6]',
                    '+1;2;3;[1, 2, 3, 4, 5]'::pgvarlenathing = '1;2;3;[1,2,3,4,5]')
                """, token));
            Assert.AreEqual("-1,0,1,t", await Scalar<string>(connection, transaction, """
                SELECT concat_ws(',', sign(pgvarlenathing_cmp('1;2;3;[1,2,3,4,5]', '1;2;3;[1,2,3,4,6]')),
                    pgvarlenathing_cmp('1;2;3;[1,2,3,4,5]', '1;2;3;[1, 2, 3, 4, 5]'),
                    sign(pgvarlenathing_cmp('1;2;4;[0,0,0,0,0]', '1;2;3;[9,9,9,9,9]')),
                    pgvarlenathing_hash('1;2;3;[1,2,3,4,5]') = pgvarlenathing_hash('+1;+2;+3;[+1, 2, 3, 4, 5]'))
                """, token));
            Assert.AreEqual("18446744073709551615;0;-2147483648;[255, 0, 0, 0, 255]", await Scalar<string>(connection, transaction,
                "SELECT '18446744073709551615;0;-2147483648;[255,0,0,0,255]'::pgvarlenathing::text", token));
            Assert.IsTrue(await Scalar<bool>(connection, transaction, """
                SELECT '7;8;-9;[1,2,3,4,5]'::pgvarlenathing::text::pgvarlenathing = '7;8;-9;[1,2,3,4,5]'::pgvarlenathing
                """, token));
            Assert.AreEqual(29, await Scalar<int>(connection, transaction, "SELECT pg_column_size('1;2;3;[1,2,3,4,5]'::pgvarlenathing)", token));

            await Execute(connection, transaction, """
                CREATE TABLE varlena_things(id integer, v pgvarlenathing);
                INSERT INTO varlena_things
                    SELECT i, format('%s;%s;%s;[%s,0,0,0,%s]', i % 7, i % 3, i % 5 - 2, i % 11, i % 13)::pgvarlenathing
                    FROM generate_series(1, 500) AS i;
                CREATE INDEX varlena_things_btree ON varlena_things USING btree (v);
                CREATE INDEX varlena_things_hash ON varlena_things USING hash (v);
                ANALYZE varlena_things;
                """, token);
            Assert.AreEqual("1;1;-1;[1, 0, 0, 0, 1]|1;1;42;[1, 0, 0, 0, 1]|1;1;-1;[1, 0, 0, 0, 1]", await Scalar<string>(connection, transaction,
                "SELECT v::text || '|' || pg_varlena_thing_with_c(v, 42)::text || '|' || v::text FROM varlena_things WHERE id = 1", token));
            long expected = await Scalar<long>(connection, transaction,
                "SELECT count(*) FROM varlena_things WHERE v = '1;1;-1;[1,0,0,0,1]'", token);
            Assert.AreEqual(1L, expected);
            await Execute(connection, transaction, "SET LOCAL enable_seqscan = off; SET LOCAL enable_bitmapscan = off", token);
            Assert.Contains("varlena_things_btree", await Plan(connection, transaction,
                "SELECT count(*) FROM varlena_things WHERE v < '1;0;0;[0,0,0,0,0]'", token));
            Assert.AreEqual(expected, await Scalar<long>(connection, transaction,
                "SELECT count(*) FROM varlena_things WHERE v = '1;1;-1;[1,0,0,0,1]'", token));
            Assert.IsTrue(await Scalar<bool>(connection, transaction, """
                SELECT bool_and(previous IS NULL OR previous <= v) FROM (
                    SELECT v, lag(v) OVER (ORDER BY v) AS previous FROM varlena_things) AS ordered
                """, token));
            await Execute(connection, transaction, "DROP INDEX varlena_things_btree", token);
            Assert.Contains("varlena_things_hash", await Plan(connection, transaction,
                "SELECT count(*) FROM varlena_things WHERE v = '1;1;-1;[1,0,0,0,1]'", token));
            Assert.AreEqual(expected, await Scalar<long>(connection, transaction,
                "SELECT count(*) FROM varlena_things WHERE v = '1;1;-1;[1,0,0,0,1]'", token));
        }, context.CancellationToken);

    /// <summary>
    /// Rejects malformed packed text with PostgreSQL's invalid-input SQLSTATE and recovers in the same backend.
    /// </summary>
    /// <param name="text">The rejected input text.</param>
    [TestMethod]
    [DataRow("1;2;3")]
    [DataRow("1;2;3;[1,2,3,4]")]
    [DataRow("1;2;3;[1,2,3,4,5,6]")]
    [DataRow("1;2;3;[1,2,3,4,5];4")]
    [DataRow("1;2;3;1,2,3,4,5")]
    [DataRow("-0;2;3;[1,2,3,4,5]")]
    [DataRow("18446744073709551616;2;3;[1,2,3,4,5]")]
    [DataRow("1;2;2147483648;[1,2,3,4,5]")]
    [DataRow("1;2;3;[1,2,3,4,256]")]
    [DataRow("1;2;3;[-0,2,3,4,5]")]
    [DataRow(" 1;2;3;[1,2,3,4,5]")]
    [DataRow("1;2;3;[ 1,2,3,4,5]")]
    public Task OperatorsSampleRejectsMalformedPackedText(string text)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(OperatorsSampleRejectsMalformedPackedText), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, "CREATE EXTENSION ankus_operators", token);
            await transaction.SaveAsync("malformed", token);
            await using (var command = new NpgsqlCommand("SELECT $1::pgvarlenathing", connection, transaction))
            {
                command.Parameters.AddWithValue(text);
                PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
                Assert.AreEqual("22P02", error.SqlState);
                Assert.AreEqual($"invalid input syntax for type pgvarlenathing: \"{text}\"", error.MessageText);
            }

            await transaction.RollbackAsync("malformed", token);
            Assert.AreEqual("1;2;3;[1, 2, 3, 4, 5]", await Scalar<string>(connection, transaction,
                "SELECT '1;2;3;[1,2,3,4,5]'::pgvarlenathing::text", token));
            Assert.AreEqual(connection.ProcessID, await Scalar<int>(connection, transaction, "SELECT pg_backend_pid()", token));
        }, context.CancellationToken);

    /// <summary>
    /// Orders text like Rust strings, matches PostgreSQL's byte order and uses the generated B-tree and hash classes.
    /// </summary>
    [TestMethod]
    public Task OperatorsSampleOrdersAndHashesThings()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(OperatorsSampleOrdersAndHashesThings), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, "CREATE EXTENSION ankus_operators", token);
            Assert.AreEqual("t,t,t,f,t,t", await Scalar<string>(connection, transaction, """
                SELECT concat_ws(',',
                    '{"Value":"a"}'::thing < '{"Value":"b"}'::thing,
                    '{"Value":"a"}'::thing = '{"Value":"a"}'::thing,
                    '{"Value":"a"}'::thing <> '{"Value":"A"}'::thing,
                    '{"Value":"b"}'::thing <= '{"Value":"a"}'::thing,
                    '{"Value":"｡"}'::thing < '{"Value":"😀"}'::thing,
                    ('{"Value":"a"}'::thing = NULL::thing) IS NULL)
                """, token));
            Assert.AreEqual("{\"Value\":\"｡\"}", await Scalar<string>(connection, transaction,
                """SELECT '{"Value":"｡"}'::thing::text""", token));
            Assert.IsLessThan(0, await Scalar<int>(connection, transaction,
                """SELECT thing_cmp('{"Value":"｡"}', '{"Value":"😀"}')""", token));
            Assert.AreEqual(0, await Scalar<int>(connection, transaction, """SELECT thing_cmp('{"Value":"a"}', '{"Value":"a"}')""", token));
            Assert.IsTrue(await Scalar<bool>(connection, transaction,
                """SELECT thing_hash('{"Value":"same"}') = thing_hash('{ "Value" : "same" }')""", token));

            await Execute(connection, transaction, """
                CREATE TABLE things(value thing);
                INSERT INTO things SELECT json_build_object('Value', v)::text::thing
                FROM unnest(ARRAY['b', 'a', '', 'A', U&'\FF61', U&'\+01F600', U&'\00E9', 'aa', 'a', U&'\E000']) AS v;
                CREATE INDEX things_btree ON things USING btree (value);
                CREATE INDEX things_hash ON things USING hash (value);
                ANALYZE things;
                """, token);
            string expectedOrder = await Scalar<string>(connection, transaction,
                """SELECT string_agg(value::text::json->>'Value', '|' ORDER BY (value::text::json->>'Value') COLLATE "C") FROM things""", token);
            Assert.AreEqual(expectedOrder, await Scalar<string>(connection, transaction,
                "SELECT string_agg(value::text::json->>'Value', '|' ORDER BY value) FROM things", token));
            Assert.AreEqual("|A|a|a|aa|b|é|\uE000|｡|😀", expectedOrder);
            Assert.AreEqual(9L, await Scalar<long>(connection, transaction, "SELECT count(DISTINCT value) FROM things", token));

            await Execute(connection, transaction, "SET LOCAL enable_seqscan = off; SET LOCAL enable_bitmapscan = off", token);
            Assert.Contains("things_btree", await Plan(connection, transaction,
                """SELECT count(*) FROM things WHERE value > '{"Value":"b"}'""", token));
            Assert.AreEqual(4L, await Scalar<long>(connection, transaction,
                """SELECT count(*) FROM things WHERE value > '{"Value":"b"}'""", token));
            await Execute(connection, transaction, "DROP INDEX things_btree; SET LOCAL enable_sort = off", token);
            Assert.Contains("things_hash", await Plan(connection, transaction,
                """SELECT count(*) FROM things WHERE value = '{"Value":"a"}'""", token));
            Assert.AreEqual(2L, await Scalar<long>(connection, transaction,
                """SELECT count(*) FROM things WHERE value = '{"Value":"a"}'""", token));
            Assert.Contains("HashAggregate", await Plan(connection, transaction, "SELECT value, count(*) FROM things GROUP BY value", token));
            Assert.AreEqual(9L, await Scalar<long>(connection, transaction,
                "SELECT count(*) FROM (SELECT value FROM things GROUP BY value) AS groups", token));
        }, context.CancellationToken);

    /// <summary>
    /// Declares the manual operator with pgrx's defaults and the derived operators with pgrx's planner options.
    /// </summary>
    [TestMethod]
    public Task OperatorsSampleDeclaresManualAndDerivedOperators()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(OperatorsSampleDeclaresManualAndDerivedOperators), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, "CREATE EXTENSION ankus_operators", token);
            Assert.AreEqual("t,f", await Scalar<string>(connection, transaction, """
                SELECT concat_ws(',', '{"value":1}'::mytype = '{"value":1}'::mytype, '{"value":1}'::mytype = '{"value":2}'::mytype)
                """, token));
            Assert.AreEqual("""{"value":-7}""", await Scalar<string>(connection, transaction, """SELECT '{"value":-7}'::mytype::text""", token));
            Assert.AreEqual("my_eq:0:0:-:-:f:f:v:u:t", await Scalar<string>(connection, transaction, """
                SELECT concat_ws(':', o.oprcode::text, o.oprcom::text, o.oprnegate::text, o.oprrest::text, o.oprjoin::text,
                    o.oprcanhash, o.oprcanmerge, p.provolatile, p.proparallel, p.proisstrict)
                FROM pg_operator o JOIN pg_proc p ON p.oid = o.oprcode
                WHERE o.oid = '=(mytype,mytype)'::regoperator
                """, token));
            Assert.AreSequenceEqual<string>([
                "<:>:>=:scalarltsel:scalarltjoinsel:f:f", "<=:>=:>:scalarlesel:scalarlejoinsel:f:f",
                "<>:<>:=:neqsel:neqjoinsel:f:f", "=:=:<>:eqsel:eqjoinsel:t:t",
                ">:<:<=:scalargtsel:scalargtjoinsel:f:f", ">=:<=:<:scalargesel:scalargejoinsel:f:f",
            ], await Scalar<string[]>(connection, transaction, """
                SELECT array_agg(concat_ws(':', o.oprname, c.oprname, n.oprname, o.oprrest::text, o.oprjoin::text, o.oprcanhash, o.oprcanmerge)
                    ORDER BY o.oprname COLLATE "C")
                FROM pg_operator o JOIN pg_operator c ON c.oid = o.oprcom JOIN pg_operator n ON n.oid = o.oprnegate
                WHERE o.oprleft = 'thing'::regtype AND o.oprright = 'thing'::regtype
                """, token));
            Assert.AreSequenceEqual<string>(["pgvarlenathing_btree_ops:btree", "pgvarlenathing_hash_ops:hash", "thing_btree_ops:btree",
                "thing_hash_ops:hash"], await Scalar<string[]>(connection, transaction, """
                SELECT array_agg(c.opcname || ':' || a.amname ORDER BY c.opcname COLLATE "C")
                FROM pg_opclass c JOIN pg_am a ON a.oid = c.opcmethod
                WHERE c.opcdefault AND c.opcintype IN ('thing'::regtype, 'pgvarlenathing'::regtype)
                """, token));
            Assert.IsTrue(await Scalar<bool>(connection, transaction, """
                SELECT bool_and(p.provolatile = 'i' AND p.proparallel = 's' AND p.proisstrict) FROM pg_proc p
                WHERE p.proname IN ('thing_eq', 'thing_ne', 'thing_lt', 'thing_le', 'thing_gt', 'thing_ge', 'thing_cmp', 'thing_hash',
                    'pgvarlenathing_eq', 'pgvarlenathing_cmp', 'pgvarlenathing_hash')
                """, token));

            await transaction.SaveAsync("text_operators", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                Execute(connection, transaction, """SELECT '"a"'::thing""", token));
            Assert.AreEqual("22P02", error.SqlState);
            Assert.AreEqual("Invalid JSON custom-type value.", error.MessageText);
            await transaction.RollbackAsync("text_operators", token);
            Assert.AreEqual(connection.ProcessID, await Scalar<int>(connection, transaction, "SELECT pg_backend_pid()", token));
        }, context.CancellationToken);

    /// <summary>
    /// Returns the textual plan for a query.
    /// </summary>
    private static async Task<string> Plan(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand("EXPLAIN (COSTS OFF) " + sql, connection, transaction);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
        List<string> lines = [];
        while (await reader.ReadAsync(token))
        {
            lines.Add(reader.GetString(0));
        }

        return string.Join('\n', lines);
    }

    /// <summary>
    /// Returns one typed observation.
    /// </summary>
    private static async Task<T> Scalar<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }

    /// <summary>
    /// Completes DDL or an error-producing operation before the next assertion.
    /// </summary>
    private static async Task Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        await command.ExecuteNonQueryAsync(token);
    }
}
