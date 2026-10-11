using System.Text.Json;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Proves resolved aggregate signatures with real state transitions, grouping, windows, workers, and errors.
/// </summary>
public sealed partial class AggregateTests
{
    private static readonly string[] s_polymorphicEmptyRows = ["VALUES(NULL::text),(NULL)", "SELECT 'x'::text WHERE false"];
    private static readonly string[] s_polymorphicGroupedValues = ["1:héllo1", "2:héllo2", "3:héllo3"];

    /// <summary>
    /// Matches pgrx's first-value aggregate while retaining domains, enums, records, arrays and large values.
    /// </summary>
    /// <param name="expression">The non-null typed value.</param>
    [TestMethod]
    [DataRow("42")]
    [DataRow("repeat('héllo 🐘',10000)")]
    [DataRow("'owned'::pg_temp.aggregate_enum")]
    [DataRow("42::pg_temp.aggregate_domain")]
    [DataRow("ROW(42,repeat('x',10000))::pg_temp.aggregate_pair")]
    [DataRow("'[2:3][-1:0]={{1,NULL},{3,4}}'::integer[]")]
    public Task PolymorphicAggregateStatesPreserveValuesAndResolvedTypes(string expression)
        => Run(nameof(PolymorphicAggregateStatesPreserveValuesAndResolvedTypes), async (connection, transaction, token) =>
        {
            await PreparePolymorphicAggregates(connection, transaction, token);
            await using var command = new NpgsqlCommand($"""
                WITH input AS (SELECT {expression} AS value FROM generate_series(1,4))
                SELECT aggregate_values.first_poly(value)::text, ({expression})::text,
                    pg_typeof(aggregate_values.first_poly(value))::oid, pg_typeof({expression})::oid,
                    aggregate_values.owned_poly(value)::text, pg_typeof(aggregate_values.owned_poly(value))::oid
                FROM input
                """, connection, transaction);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.IsTrue(await reader.ReadAsync(token));
            Assert.AreEqual(reader.GetString(1), reader.GetString(0));
            Assert.AreEqual(reader.GetString(1), reader.GetString(4));
            Assert.AreEqual(reader.GetFieldValue<uint>(3), reader.GetFieldValue<uint>(2));
            Assert.AreEqual(reader.GetFieldValue<uint>(3), reader.GetFieldValue<uint>(5));
            await reader.DisposeAsync();
            int[] counts = await Scalar<int[]>(connection, transaction, "SELECT aggregate_values.poly_aggregate_counts()", token);
            Assert.AreEqual(3, counts[0], "PostgreSQL seeds the first strict state without invoking Transition.");
            Assert.AreEqual(1, counts[3]);
            Assert.AreEqual(counts[3], counts[4]);
            Assert.IsFalse(await Scalar<bool>(connection, transaction, "SELECT aggregate_values.poly_aggregate_owner_alive()", token));
        });

    /// <summary>
    /// Preserves empty arrays, non-one bounds, NULL elements and unregistered element identities as aggregate states.
    /// </summary>
    /// <param name="expression">The typed array.</param>
    [TestMethod]
    [DataRow("'{}'::integer[]")]
    [DataRow("'[2:3][-1:0]={{1,NULL},{3,4}}'::integer[]")]
    [DataRow("ARRAY['owned',NULL,'other']::pg_temp.aggregate_enum[]")]
    [DataRow("ARRAY[ROW(42,repeat('x',10000)),NULL]::pg_temp.aggregate_pair[]")]
    public Task PolymorphicArrayAggregateStatesPreserveShapeAndCells(string expression)
        => Run(nameof(PolymorphicArrayAggregateStatesPreserveShapeAndCells), async (connection, transaction, token) =>
        {
            await PreparePolymorphicAggregates(connection, transaction, token);
            string expected = await Scalar<string>(connection, transaction, $"SELECT datatype.poly_array_shape({expression})", token);
            Assert.AreEqual(expected, await Scalar<string>(connection, transaction,
                $"SELECT datatype.poly_array_shape(aggregate_values.first_poly_array({expression})) FROM generate_series(1,4)", token));
        });

    /// <summary>
    /// Resolves textual initial conditions with actual element types and preserves nested catalog-call results.
    /// </summary>
    [TestMethod]
    public Task PolymorphicAggregateInitialStateResolvesArrayTypes()
        => Run(nameof(PolymorphicAggregateInitialStateResolvesArrayTypes), async (connection, transaction, token) =>
        {
            await PreparePolymorphicAggregates(connection, transaction, token);
            Assert.AreEqual("[2:7]={1,NULL,1,NULL,1,NULL}", await Scalar<string>(connection, transaction,
                "SELECT aggregate_values.concat_poly('[2:3]={1,NULL}'::integer[])::text FROM generate_series(1,3)", token));
            Assert.AreEqual("{owned,NULL,owned,NULL}", await Scalar<string>(connection, transaction,
                "SELECT aggregate_values.concat_poly(ARRAY['owned',NULL]::pg_temp.aggregate_enum[])::text FROM generate_series(1,2)", token));
            Assert.AreEqual("{}", await Scalar<string>(connection, transaction,
                "SELECT aggregate_values.concat_poly(value)::text FROM (SELECT ARRAY[1] AS value WHERE false) input", token));
        });

    /// <summary>
    /// Nested scalar callbacks retain values under the aggregate owner after their own temporary storage is deleted.
    /// </summary>
    [TestMethod]
    public Task PolymorphicAggregateOwnershipSurvivesNestedScalarCalls()
        => Run(nameof(PolymorphicAggregateOwnershipSurvivesNestedScalarCalls), async (connection, transaction, token) =>
        {
            await PreparePolymorphicAggregates(connection, transaction, token);
            Assert.AreEqual(new string('x', 10000) + "31", await Scalar<string>(connection, transaction,
                "SELECT aggregate_values.nested_poly(repeat('x',10000) || value ORDER BY value) FROM generate_series(1,31) value", token));
            int[] counts = await Scalar<int[]>(connection, transaction, "SELECT aggregate_values.poly_aggregate_counts()", token);
            Assert.AreEqual(1, counts[3]);
            Assert.AreEqual(1, counts[4]);
        });

    /// <summary>
    /// Empty and all-NULL inputs remain NULL; filtering and grouping retain only the selected group's last value.
    /// </summary>
    [TestMethod]
    public Task PolymorphicAggregatesPreserveNullEmptyFilterAndGroupSemantics()
        => Run(nameof(PolymorphicAggregatesPreserveNullEmptyFilterAndGroupSemantics), async (connection, transaction, token) =>
        {
            await PreparePolymorphicAggregates(connection, transaction, token);
            foreach (string rows in s_polymorphicEmptyRows)
            {
                Assert.IsTrue(await Scalar<bool>(connection, transaction,
                    $"SELECT aggregate_values.first_poly(value) IS NULL AND aggregate_values.owned_poly(value) IS NULL FROM ({rows}) input(value)", token));
            }

            Assert.AreEqual(7, await Scalar<int>(connection, transaction,
                "SELECT aggregate_values.first_poly(value) FROM (VALUES(NULL::integer),(7),(NULL),(9)) input(value)", token));
            Assert.AreSequenceEqual(s_polymorphicGroupedValues, await Scalar<string[]>(connection, transaction, """
                SELECT array_agg(key || ':' || value ORDER BY key) FROM (
                    SELECT key, aggregate_values.owned_poly('héllo' || key ORDER BY ord) FILTER(WHERE ord % 2 = 1) AS value
                    FROM generate_series(1,3) key CROSS JOIN generate_series(1,31) ord GROUP BY key) groups
                """, token));
            int[] counts = await Scalar<int[]>(connection, transaction, "SELECT aggregate_values.poly_aggregate_counts()", token);
            Assert.IsGreaterThan(0, counts[3]);
            Assert.AreEqual(counts[3], counts[4]);
        });

    /// <summary>
    /// Moving inverse and restart paths retain earlier frame results and nullable values after input storage expires.
    /// </summary>
    /// <param name="exclude">Whether PostgreSQL restarts excluded frames.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task PolymorphicMovingAggregatesRetainValuesAcrossFrames(bool exclude)
        => Run(nameof(PolymorphicMovingAggregatesRetainValuesAcrossFrames), async (connection, transaction, token) =>
        {
            await PreparePolymorphicAggregates(connection, transaction, token);
            string window = exclude ? "ROWS 1 PRECEDING EXCLUDE CURRENT ROW" : "ROWS 1 PRECEDING";
            string?[] expected = exclude ? [null, new('a', 10000), null, new('c', 10000)]
                : [new('a', 10000), new('a', 10000), null, new('c', 10000)];
            Assert.AreSequenceEqual(expected, await Scalar<string?[]>(connection, transaction, $"""
                SELECT array_agg(value ORDER BY ord) FROM (
                    SELECT ord,aggregate_values.moving_poly(CASE WHEN ord=2 THEN NULL ELSE repeat(chr(96+ord),10000) END)
                        OVER(ORDER BY ord {window}) AS value FROM generate_series(1,4) ord) frames
                """, token));
            int[] counts = await Scalar<int[]>(connection, transaction, "SELECT aggregate_values.poly_aggregate_counts()", token);
            if (!exclude)
            {
                Assert.IsGreaterThan(0, counts[2], "A passing window result must exercise inverse transitions.");
            }
        });

    /// <summary>
    /// Ordered sets use real type operators, requested direction, and collation without managed type mappings.
    /// </summary>
    /// <param name="values">The typed values.</param>
    /// <param name="direction">The requested SQL direction.</param>
    /// <param name="expected">The first value in that ordering.</param>
    [TestMethod]
    [DataRow("VALUES('z'::text),('a'),(NULL)", "COLLATE \"C\" ASC", "a")]
    [DataRow("VALUES('z'::text),('a'),(NULL)", "COLLATE \"C\" DESC", "z")]
    [DataRow("VALUES('owned'::pg_temp.aggregate_enum),('other'),(NULL)", "ASC", "owned")]
    [DataRow("VALUES(ROW(2,'b')::pg_temp.aggregate_pair),(ROW(1,'a')::pg_temp.aggregate_pair)", "ASC", "(1,a)")]
    public Task PolymorphicOrderedAggregatesUsePostgresComparison(string values, string direction, string expected)
        => Run(nameof(PolymorphicOrderedAggregatesUsePostgresComparison), async (connection, transaction, token) =>
        {
            await PreparePolymorphicAggregates(connection, transaction, token);
            Assert.AreEqual(expected, await Scalar<string>(connection, transaction,
                $"SELECT aggregate_values.ordered_poly() WITHIN GROUP(ORDER BY value {direction})::text FROM ({values}) input(value)", token));
            int[] counts = await Scalar<int[]>(connection, transaction, "SELECT aggregate_values.poly_aggregate_counts()", token);
            Assert.AreEqual(1, counts[3]);
            Assert.AreEqual(1, counts[4]);
        });

    /// <summary>
    /// Real workers combine resolved polymorphic states and retain the same value as serial execution.
    /// </summary>
    [TestMethod]
    public Task PolymorphicParallelAggregatesCombineActualWorkerStates()
        => Run(nameof(PolymorphicParallelAggregatesCombineActualWorkerStates), async (connection, transaction, token) =>
        {
            await PrepareParallelInput(connection, transaction, token);
            const string query = "SELECT aggregate_values.first_poly(CASE WHEN value % 7=0 THEN NULL ELSE repeat('owned',1000) END) FROM aggregate_values.parallel_input";

            // A strict aggregate adopts the first partial state and combines only later ones, so count combinations
            // in an execution where both workers ran. Concurrent tests can briefly hold the worker slots.
            int launched = 0;
            for (int attempt = 0; attempt < 20 && launched < 2; attempt++)
            {
                await Execute(connection, transaction, "SELECT aggregate_values.poly_aggregate_reset()", token);
                string plan = await Scalar<string>(connection, transaction, "EXPLAIN(ANALYZE,FORMAT JSON) " + query, token);
                using JsonDocument document = JsonDocument.Parse(plan);
                JsonElement root = document.RootElement[0].GetProperty("Plan");
                Assert.IsTrue(HasPartialAggregate(root));
                launched = WorkersLaunched(root);
                if (launched < 2)
                {
                    await Task.Delay(100, token);
                }
            }

            Assert.AreEqual(2, launched, "Both planned workers must run to give the leader states to combine.");
            int[] counts = await Scalar<int[]>(connection, transaction, "SELECT aggregate_values.poly_aggregate_counts()", token);
            Assert.IsGreaterThan(0, counts[1], "The leader must invoke the generated polymorphic combine callback.");
            Assert.AreEqual(string.Concat(Enumerable.Repeat("owned", 1000)), await Scalar<string>(connection, transaction, query, token));
        });

    /// <summary>
    /// Wrong resolved outputs and NULL domain results fail cleanly and leave the same backend usable.
    /// </summary>
    [TestMethod]
    public Task PolymorphicAggregateErrorsEnforceResultTypesAndRecover()
        => Run(nameof(PolymorphicAggregateErrorsEnforceResultTypesAndRecover), async (connection, transaction, token) =>
        {
            await PreparePolymorphicAggregates(connection, transaction, token);
            await Execute(connection, transaction, "CREATE DOMAIN pg_temp.aggregate_required AS integer NOT NULL", token);
            foreach ((string sql, string expected) in new[]
            {
                ("SELECT aggregate_values.wrong_poly(value) FROM generate_series(1,4) value", "42804"),
                ("SELECT aggregate_values.null_poly(value::pg_temp.aggregate_required) FROM generate_series(1,4) value", "23502"),
                ("SELECT aggregate_values.borrowed_poly(repeat('owned',1000)) FROM generate_series(1,4)", "38000"),
            })
            {
                await transaction.SaveAsync("poly_error", token);
                PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, transaction, sql, token));
                Assert.AreEqual(expected, error.SqlState);
                await transaction.RollbackAsync("poly_error", token);
                Assert.AreEqual(42, await Scalar<int>(connection, transaction, "SELECT aggregate_values.owned_poly(42)", token));
                Assert.AreEqual(connection.ProcessID, await Scalar<int>(connection, transaction, "SELECT pg_backend_pid()", token));
            }
        });

    /// <summary>
    /// Creates independent unmapped types and clears lifetime observations in the current transaction.
    /// </summary>
    private static Task<int> PreparePolymorphicAggregates(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        => Execute(connection, transaction, """
            CREATE TYPE pg_temp.aggregate_enum AS ENUM ('owned','other');
            CREATE DOMAIN pg_temp.aggregate_domain AS integer;
            CREATE TYPE pg_temp.aggregate_pair AS (number integer,text text);
            SELECT aggregate_values.poly_aggregate_reset();
            """, token);
}
