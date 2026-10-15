using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Executes generated aggregate functions against PostgreSQL values, state lifetimes, and recovery boundaries.
/// </summary>
/// <param name="context">The current test context.</param>
[TestClass]
[DoNotParallelize]
public sealed partial class AggregateTests(TestContext context)
{
    /// <summary>
    /// Distinguishes empty, all-null, mixed-null and singleton transition behavior.
    /// </summary>
    /// <param name="values">The typed input subquery.</param>
    /// <param name="expected">The expected aggregate value.</param>
    /// <param name="calls">The exact transition count.</param>
    [TestMethod]
    [DataRow("VALUES(1),(1),(2)", 4, 3)]
    [DataRow("VALUES(7)", 7, 1)]
    [DataRow("VALUES(NULL::integer),(NULL),(NULL)", 0, 3)]
    [DataRow("VALUES(1),(NULL),(3)", 4, 3)]
    [DataRow("SELECT 1 WHERE false", 0, 0)]
    public Task OrdinarySumPreservesEmptyAndNullableInputSemantics(string values, int expected, int calls)
        => Run(nameof(OrdinarySumPreservesEmptyAndNullableInputSemantics), async (connection, transaction, token) =>
        {
            await Reset(connection, transaction, "normal", token);
            Assert.AreEqual(expected, await Scalar<int>(connection, transaction,
                $"SELECT aggregate_values.sum_values(v) FROM ({values}) AS input(v)", token));
            Assert.AreEqual(calls, (await Status(connection, transaction, token))[4]);
        });

    /// <summary>
    /// JSON and JSONB states keep the first non-NULL value through strict, immutable, parallel-safe transitions,
    /// as pgrx's FirstJson and FirstJsonB aggregates do.
    /// </summary>
    /// <param name="type">The SQL JSON type.</param>
    /// <param name="first">The first value's text as the type outputs it.</param>
    [TestMethod]
    [DataRow("json", "{\"foo\":\"one\"}")]
    [DataRow("jsonb", "{\"foo\": \"one\"}")]
    public Task JsonStatesKeepTheFirstValue(string type, string first)
        => Run(nameof(JsonStatesKeepTheFirstValue), async (connection, transaction, token) =>
        {
            Assert.AreEqual(first, await Scalar<string>(connection, transaction,
                $"SELECT aggregate_values.first_{type}(v)::text FROM unnest(ARRAY[NULL, '{{\"foo\":\"one\"}}'::{type}, '{{\"foo\":\"two\"}}']) AS v",
                token));
            Assert.AreEqual($"{type}|t|i|s", await Scalar<string>(connection, transaction, $"""
                SELECT concat_ws('|', aggregate.aggtranstype::regtype, transition.proisstrict, transition.provolatile, transition.proparallel)
                FROM pg_aggregate aggregate JOIN pg_proc transition ON transition.oid = aggregate.aggtransfn
                WHERE aggregate.aggfnoid = 'aggregate_values.first_{type}({type})'::regprocedure
                """, token));
        });

    /// <summary>
    /// An aggregate keeps its state in a custom type from another schema, as pgrx's <c>aggregate_demo_custom_state</c>
    /// does, and an empty input finalizes to zero.
    /// </summary>
    [TestMethod]
    public Task CustomStateFromAnotherSchemaSums()
        => Run(nameof(CustomStateFromAnotherSchemaSums), async (connection, transaction, token) =>
        {
            Assert.AreEqual(4, await Scalar<int>(connection, transaction,
                "SELECT aggregate_values.demo_sum_state(value) FROM unnest(ARRAY[1, 1, 2]) AS value", token));
            Assert.AreEqual(0, await Scalar<int>(connection, transaction,
                "SELECT aggregate_values.demo_sum_state(value) FROM unnest('{}'::integer[]) AS value", token));
            Assert.AreEqual("custom_values.number", await Scalar<string>(connection, transaction, """
                SELECT aggtranstype::regtype::text FROM pg_aggregate
                WHERE aggfnoid = 'aggregate_values.demo_sum_state(integer)'::regprocedure
                """, token));
        });

    /// <summary>
    /// Strict transition dispatch seeds state once and skips rows containing any NULL input.
    /// </summary>
    [TestMethod]
    public Task StrictTransitionsSeedAndSkipWithoutManagedCalls()
        => Run(nameof(StrictTransitionsSeedAndSkipWithoutManagedCalls), async (connection, transaction, token) =>
        {
            await Reset(connection, transaction, "normal", token);
            Assert.AreEqual(5, await Scalar<int>(connection, transaction,
                "SELECT aggregate_values.strict_sum(v) FROM (VALUES(NULL::integer),(2),(3)) AS input(v)", token));
            Assert.AreEqual(1, (await Status(connection, transaction, token))[4]);
            await Reset(connection, transaction, "normal", token);
            Assert.AreEqual(149, await Scalar<int>(connection, transaction,
                "SELECT aggregate_values.dot_values(a,b) FROM (VALUES(2,3),(NULL,7),(5,NULL),(11,13)) AS input(a,b)", token));
            Assert.AreEqual(2, (await Status(connection, transaction, token))[4]);
            await using var command = new NpgsqlCommand("""
                SELECT aggregate_values.strict_sum(v) FROM (VALUES(NULL::integer),(NULL)) AS input(v)
                """, connection, transaction);
            Assert.AreSame(DBNull.Value, await command.ExecuteScalarAsync(token));
        });

    /// <summary>
    /// A NULL transition result remains sticky for a strict callback and can recover in a non-strict one.
    /// </summary>
    [TestMethod]
    public Task NullStateReturnControlsLaterTransitionsAndFinalCalls()
        => Run(nameof(NullStateReturnControlsLaterTransitionsAndFinalCalls), async (connection, transaction, token) =>
        {
            await Reset(connection, transaction, "normal", token);
            await using var command = new NpgsqlCommand("""
                SELECT aggregate_values.sticky_sum(v ORDER BY ord) FROM (VALUES(1,3),(2,-999),(3,7)) AS input(ord,v)
                """, connection, transaction);
            Assert.AreSame(DBNull.Value, await command.ExecuteScalarAsync(token));
            int[] sticky = await Status(connection, transaction, token);
            Assert.AreEqual(2, sticky[4]);
            Assert.AreEqual(0, sticky[5]);
            await Reset(connection, transaction, "normal", token);
            Assert.AreEqual(7, await Scalar<int>(connection, transaction,
                "SELECT aggregate_values.nullable_sum(v ORDER BY ord) FROM (VALUES(1,3),(2,-999),(3,7)) AS input(ord,v)", token));
            int[] recovered = await Status(connection, transaction, token);
            Assert.AreEqual(3, recovered[4]);
            Assert.AreEqual(1, recovered[5]);
            await Reset(connection, transaction, "normal", token);
            Assert.AreEqual(42, await Scalar<int>(connection, transaction,
                "SELECT aggregate_values.nullable_sum(v) FROM (SELECT 1 WHERE false) AS input(v)", token));
            int[] empty = await Status(connection, transaction, token);
            Assert.AreEqual(0, empty[4]);
            Assert.AreEqual(1, empty[5]);
        });

    /// <summary>
    /// Zero-argument, variadic and quoted text initial states retain their distinct SQL contracts.
    /// </summary>
    [TestMethod]
    public Task ZeroVariadicAndTextStatesPreserveSqlValues()
        => Run(nameof(ZeroVariadicAndTextStatesPreserveSqlValues), async (connection, transaction, token) =>
        {
            Assert.AreEqual(3L, await Scalar<long>(connection, transaction,
                "SELECT aggregate_values.count_rows(*) FROM (VALUES(NULL),(NULL),(NULL)) AS input(v)", token));
            Assert.AreEqual(0L, await Scalar<long>(connection, transaction,
                "SELECT aggregate_values.count_rows(*) FROM (SELECT 1 WHERE false) AS input(v)", token));
            Assert.AreEqual(14L, await Scalar<long>(connection, transaction,
                "SELECT aggregate_values.variadic_sum(v,v+1,NULL) FROM (VALUES(2),(4)) AS input(v)", token));
            Assert.AreEqual(1010L, await Scalar<long>(connection, transaction, """
                SELECT aggregate_values.variadic_sum(VARIADIC v)
                FROM (VALUES(ARRAY[2,NULL,3]),(ARRAY[]::integer[]),(NULL::integer[]),(ARRAY[5])) AS input(v)
                """, token));
            Assert.AreEqual("a'b\\café:abc<NULL>", await Scalar<string>(connection, transaction, """
                SELECT aggregate_values.text_values(v ORDER BY ord)
                FROM (VALUES(3,'c'),(1,'a'),(4,NULL),(2,'b')) AS input(ord,v)
                """, token));
            Assert.AreEqual("", await Scalar<string>(connection, transaction,
                "SELECT aggregate_values.empty_text(v) FROM (SELECT ''::text WHERE false) AS input(v)", token));
            Assert.AreEqual("a'b\\café:", await Scalar<string>(connection, transaction,
                "SELECT agginitval FROM pg_aggregate WHERE aggfnoid='aggregate_values.text_values(text)'::regprocedure", token));
        });

    /// <summary>
    /// PostgreSQL applies FILTER and DISTINCT before invoking the generated transition.
    /// </summary>
    [TestMethod]
    public Task FilterAndDistinctSelectActualTransitionInputs()
        => Run(nameof(FilterAndDistinctSelectActualTransitionInputs), async (connection, transaction, token) =>
        {
            await Reset(connection, transaction, "normal", token);
            Assert.AreEqual(3, await Scalar<int>(connection, transaction, """
                SELECT aggregate_values.sum_values(DISTINCT v ORDER BY v) FILTER(WHERE v>0)
                FROM (VALUES(1),(1),(2),(-999),(NULL)) AS input(v)
                """, token));
            Assert.AreEqual(2, (await Status(connection, transaction, token))[4]);
        });

    /// <summary>
    /// Interleaved grouping-set states cannot be cached under the support function's call site.
    /// </summary>
    [TestMethod]
    public Task GroupingSetsKeepIndependentManagedStates()
        => Run(nameof(GroupingSetsKeepIndependentManagedStates), async (connection, transaction, token) =>
        {
            await Reset(connection, transaction, "normal", token);
            string[] expected = ["0:1:1:5", "0:1:2:7", "0:2:1:11", "1:1:-:12", "1:2:-:11", "3:-:-:23"];
            Assert.AreSequenceEqual(expected, await Scalar<string[]>(connection, transaction, """
                SELECT array_agg(g||':'||coalesce(a::text,'-')||':'||coalesce(b::text,'-')||':'||total ORDER BY g,a,b)
                FROM (SELECT grouping(a,b) AS g,a,b,aggregate_values.managed_sum(v) AS total
                    FROM (VALUES(1,1,2),(1,1,3),(1,2,7),(2,1,11)) AS input(a,b,v)
                    GROUP BY GROUPING SETS((a,b),(a),())) AS result
                """, token));
            await AssertReleased(connection, transaction, 6, token);
        });

    /// <summary>
    /// Native reset releases reused and replaced states, with cleanup SPI deliberately restricted.
    /// </summary>
    /// <param name="mode">The state allocation and cleanup action.</param>
    /// <param name="created">The expected number of managed payloads.</param>
    /// <param name="denied">The expected restricted cleanup calls.</param>
    [TestMethod]
    [DataRow("normal", 1, 0)]
    [DataRow("replace", 3, 0)]
    [DataRow("cleanup_sql", 1, 1)]
    [DataRow("resources", 1, 0)]
    public Task ManagedStateLivesUntilResetAndReleasesExactlyOnce(string mode, int created, int denied)
        => Run(nameof(ManagedStateLivesUntilResetAndReleasesExactlyOnce), async (connection, transaction, token) =>
        {
            await Reset(connection, transaction, mode, token);
            Assert.AreEqual(6L, await Scalar<long>(connection, transaction,
                "SELECT aggregate_values.managed_sum(v) FROM (VALUES(1),(2),(3)) AS input(v)", token));
            await AssertReleased(connection, transaction, created, token);
            Assert.AreEqual(denied, (await Status(connection, transaction, token))[8]);
            Assert.AreEqual("Aggregate:oid:disposed:inactive", await Scalar<string>(connection, transaction,
                "SELECT aggregate_values.aggregate_retained()", token));
            Assert.AreEqual(0L, await Scalar<long>(connection, transaction,
                "SELECT count(*) FROM pg_cursors WHERE statement='SELECT generate_series(1,100)'", token));
        });

    /// <summary>
    /// Attached aggregate state rejects worker access while newly constructed managed state remains usable.
    /// </summary>
    [TestMethod]
    public Task AttachedStateIsBoundToTheBackendThread()
        => Run(nameof(AttachedStateIsBoundToTheBackendThread), async (connection, transaction, token) =>
        {
            await Reset(connection, transaction, "worker", token);
            Assert.AreEqual(6L, await Scalar<long>(connection, transaction,
                "SELECT aggregate_values.managed_sum(v ORDER BY v) FROM (VALUES(1),(2),(3)) AS input(v)", token));
            string[] expected = ["accessible", "thread protected", "thread protected"];
            Assert.AreSequenceEqual(expected, await Trace(connection, transaction, token));
            await AssertReleased(connection, transaction, 1, token);
        });

    /// <summary>
    /// Moving callbacks produce exact frame results and restart after the inverse function returns NULL.
    /// </summary>
    /// <param name="mode">Whether inverse removal of 20 asks PostgreSQL to restart.</param>
    [TestMethod]
    [DataRow("normal")]
    [DataRow("restart")]
    public Task MovingInverseProducesExactFramesAndDeterministicRestart(string mode)
        => Run(nameof(MovingInverseProducesExactFramesAndDeterministicRestart), async (connection, transaction, token) =>
        {
            await Reset(connection, transaction, mode, token);
            int[] expected = [1, 21, 320, 4300];
            Assert.AreSequenceEqual(expected, await Scalar<int[]>(connection, transaction, """
                SELECT array_agg(total ORDER BY ord) FROM (
                    SELECT ord,aggregate_values.moving_sum(v) OVER(ORDER BY ord ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) AS total
                    FROM (VALUES(1,1),(2,20),(3,300),(4,4000)) AS input(ord,v)) AS result
                """, token));
            string[] expectedTrace = mode == "restart"
                ? ["M:1", "M:20", "I:1", "M:300", "I:20", "M:300", "M:4000"]
                : ["M:1", "M:20", "I:1", "M:300", "I:20", "M:4000"];
            Assert.AreSequenceEqual(expectedTrace, await Trace(connection, transaction, token));
            int[] status = await Status(connection, transaction, token);
            Assert.AreEqual(2, status[6]);
            Assert.AreEqual(mode == "restart" ? 1 : 0, status[7]);
        });

    /// <summary>
    /// Independent managed window contexts survive one aggregate restarting and final calls do not dispose live state.
    /// </summary>
    [TestMethod]
    public Task ManagedWindowRestartPreservesOtherStateAndEarlierResults()
        => Run(nameof(ManagedWindowRestartPreservesOtherStateAndEarlierResults), async (connection, transaction, token) =>
        {
            await Reset(connection, transaction, "restart", token);
            string[] expected = ["1:10", "21:210", "320:3200", "4300:43000"];
            Assert.AreSequenceEqual(expected, await Scalar<string[]>(connection, transaction, """
                SELECT array_agg(a||':'||b ORDER BY ord) FROM (
                    SELECT ord,aggregate_values.managed_sum(v) OVER w AS a,aggregate_values.managed_sum(v*10) OVER w AS b
                    FROM (VALUES(1,1),(2,20),(3,300),(4,4000)) AS input(ord,v)
                    WINDOW w AS(ORDER BY ord ROWS BETWEEN 1 PRECEDING AND CURRENT ROW)) AS result
                """, token));
            await AssertReleased(connection, transaction, 3, token);
            Assert.AreEqual(1, (await Status(connection, transaction, token))[7]);
            Assert.AreEqual("Window:no oid:disposed:inactive", await Scalar<string>(connection, transaction,
                "SELECT aggregate_values.aggregate_retained()", token));
        });

    /// <summary>
    /// Single-row and cumulative windows distinguish fresh initialization from inverse removal and ordinary transitions.
    /// </summary>
    [TestMethod]
    public Task NonoverlappingAndUnboundedFramesSelectNativePaths()
        => Run(nameof(NonoverlappingAndUnboundedFramesSelectNativePaths), async (connection, transaction, token) =>
        {
            await Reset(connection, transaction, "normal", token);
            int[] singleton = [1, 20, 300];
            Assert.AreSequenceEqual(singleton, await Scalar<int[]>(connection, transaction, """
                SELECT array_agg(total ORDER BY ord) FROM (
                    SELECT ord,aggregate_values.moving_sum(v) OVER(ORDER BY ord ROWS BETWEEN CURRENT ROW AND CURRENT ROW) AS total
                    FROM (VALUES(1,1),(2,20),(3,300)) AS input(ord,v)) AS result
                """, token));
            Assert.AreEqual(0, (await Status(connection, transaction, token))[6]);
            string[] moving = ["M:1", "M:20", "M:300"];
            Assert.AreSequenceEqual(moving, await Trace(connection, transaction, token));
            await Reset(connection, transaction, "normal", token);
            int[] cumulative = [1, 21, 321];
            Assert.AreSequenceEqual(cumulative, await Scalar<int[]>(connection, transaction, """
                SELECT array_agg(total ORDER BY ord) FROM (
                    SELECT ord,aggregate_values.moving_sum(v) OVER(ORDER BY ord ROWS UNBOUNDED PRECEDING) AS total
                    FROM (VALUES(1,1),(2,20),(3,300)) AS input(ord,v)) AS result
                """, token));
            string[] ordinary = ["T:1", "T:20", "T:300"];
            Assert.AreSequenceEqual(ordinary, await Trace(connection, transaction, token));
        });

    /// <summary>
    /// Callback failures, cancellation and invalid moving NULL results unwind before PostgreSQL recovers.
    /// </summary>
    /// <param name="mode">The failure action.</param>
    /// <param name="sqlState">The expected server diagnostic.</param>
    [TestMethod]
    [DataRow("transition_error", "P7801")]
    [DataRow("final_error", "P7802")]
    [DataRow("wait", "57014")]
    [DataRow("moving_null", "22004")]
    public Task AggregateErrorsReleaseStateAndRecover(string mode, string sqlState)
        => Run(nameof(AggregateErrorsReleaseStateAndRecover), async (connection, transaction, token) =>
        {
            await Reset(connection, transaction, mode, token);
            if (mode == "wait")
            {
                await Execute(connection, transaction, "SET LOCAL statement_timeout='100ms'", token);
            }

            await transaction.SaveAsync("aggregate_error", token);
            string sql = mode == "moving_null"
                ? "SELECT aggregate_values.moving_sum(v) OVER(ORDER BY v ROWS BETWEEN 1 PRECEDING AND CURRENT ROW) FROM (VALUES(1),(20)) AS input(v)"
                : "SELECT aggregate_values.managed_sum(v ORDER BY v) FROM (VALUES(1),(2),(3)) AS input(v)";
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, transaction, sql, token));
            Assert.AreEqual(sqlState, error.SqlState);
            if (mode == "transition_error")
            {
                Assert.AreEqual("aggregate transition failed", error.MessageText);
                Assert.AreEqual("owned aggregate detail", error.Detail);
                Assert.AreEqual("retry valid inputs", error.Hint);
            }

            await StatementTimeoutCleanup.RetryAsync(() => transaction.RollbackAsync("aggregate_error", token));
            await StatementTimeoutCleanup.RetryAsync(() => Execute(connection, transaction, "SET LOCAL statement_timeout=0", token));
            await AssertReleased(connection, transaction, mode == "moving_null" ? 0 : 1, token);
            await Reset(connection, transaction, "normal", token);
            Assert.AreEqual(42L, await Scalar<long>(connection, transaction,
                "SELECT aggregate_values.managed_sum(v) FROM (VALUES(20),(22)) AS input(v)", token));
            await AssertReleased(connection, transaction, 1, token);
        });

    /// <summary>
    /// Even scalar support functions reject ordinary SQL invocation before managed dispatch.
    /// </summary>
    [TestMethod]
    public Task AggregateSupportFunctionsRequireNativeAggregateContext()
        => Run(nameof(AggregateSupportFunctionsRequireNativeAggregateContext), async (connection, transaction, token) =>
        {
            await Reset(connection, transaction, "normal", token);
            await transaction.SaveAsync("ordinary", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, transaction,
                "SELECT aggregate_values.sum_values_transition(0,7)", token));
            Assert.AreEqual("55000", error.SqlState);
            await transaction.RollbackAsync("ordinary", token);
            Assert.AreEqual(0, (await Status(connection, transaction, token))[4]);
            Assert.AreEqual(7, await Scalar<int>(connection, transaction,
                "SELECT aggregate_values.sum_values(v) FROM (VALUES(7)) AS input(v)", token));
        });

    private Task Run(string name, Func<NpgsqlConnection, NpgsqlTransaction, CancellationToken, Task> action)
        => PostgresFixture.Cluster.RunInTransactionAsync(name, action, context.CancellationToken);

    private static Task<int> Reset(NpgsqlConnection connection, NpgsqlTransaction transaction, string mode, CancellationToken token)
        => Execute(connection, transaction, $"SELECT aggregate_values.aggregate_reset('{mode}')", token);

    private static Task<int[]> Status(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        => Scalar<int[]>(connection, transaction, "SELECT aggregate_values.aggregate_status()", token);

    private static Task<string[]> Trace(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
        => Scalar<string[]>(connection, transaction, "SELECT aggregate_values.aggregate_trace()", token);

    private static async Task AssertReleased(NpgsqlConnection connection, NpgsqlTransaction transaction, int created, CancellationToken token)
    {
        int[] status = await Status(connection, transaction, token);
        Assert.AreEqual(created, status[0], "Created payload count.");
        Assert.AreEqual(created, status[1], "Every native owner must dispose its payload exactly once.");
        Assert.AreEqual(0, status[2], "No managed aggregate payload remains live.");
        Assert.AreEqual(0, status[3], "No native owner may dispose the payload twice.");
    }

    private static async Task<int> Execute(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return await command.ExecuteNonQueryAsync(token);
    }

    private static async Task<T> Scalar<T>(NpgsqlConnection connection, NpgsqlTransaction transaction, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection, transaction);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }
}
