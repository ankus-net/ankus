using System.Text.Json;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Proves internal values across native callbacks, managed cleanup, windows, workers, and error recovery.
/// </summary>
public sealed partial class AggregateTests
{
    /// <summary>
    /// Real C callbacks distinguish SQL NULL, a present zero pointer, and a writable native Int64.
    /// </summary>
    /// <param name="mode">The native fixture's NULL, zero, or live pointer input.</param>
    /// <param name="expected">The exact callback result.</param>
    [TestMethod]
    [DataRow(0, -1L)]
    [DataRow(1, 0L)]
    [DataRow(2, 42L)]
    public Task InternalNativePointersPreserveNullZeroAndWritableValues(int mode, long expected)
        => Run(nameof(InternalNativePointersPreserveNullZeroAndWritableValues), async (connection, transaction, token) =>
        {
            Assert.AreEqual(expected, await Scalar<long>(connection, transaction,
                $"SELECT tests.internal_invoke('internal_values.internal_native_read(internal)',{mode})", token));
        });

    /// <summary>
    /// Ordinary generated functions can serve as native internal state producers and consumers.
    /// </summary>
    [TestMethod]
    public Task InternalFunctionStateSurvivesCallbacksAndReleasesAtQueryEnd()
        => Run(nameof(InternalFunctionStateSurvivesCallbacksAndReleasesAtQueryEnd), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, """
                SELECT internal_values.internal_reset();
                CREATE AGGREGATE pg_temp.internal_total(integer)
                    (SFUNC=internal_values.internal_step,STYPE=internal,FINALFUNC=internal_values.internal_final);
                """, token);
            Assert.AreEqual(5050L, await Scalar<long>(connection, transaction,
                "SELECT pg_temp.internal_total(value) FROM generate_series(1,100) value", token));
            await AssertInternalReleased(connection, transaction, token);
            Assert.AreEqual(DBNull.Value, await Scalar<object>(connection, transaction,
                "SELECT pg_temp.internal_total(value) FROM (SELECT 1 AS value WHERE false) input", token));
            Assert.IsTrue(await Scalar<bool>(connection, transaction, "SELECT internal_values.internal_reset_owner()", token));
            await AssertInternalReleased(connection, transaction, token);
        });

    /// <summary>
    /// Throwing disposal consumes its payload before context invalidation and permits native cleanup to be retried.
    /// </summary>
    [TestMethod]
    public Task InternalThrowingCleanupRejectsReleasedStateAndRecovers()
        => Run(nameof(InternalThrowingCleanupRejectsReleasedStateAndRecovers), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, "SELECT internal_values.internal_reset()", token);
            Assert.AreEqual("P7924|True|42", await Scalar<string>(connection, transaction,
                "SELECT internal_values.internal_throwing_cleanup()", token));
            int[] counts = await AssertInternalReleased(connection, transaction, token);
            Assert.AreSequenceEqual([1, 1, 1, 0, 0], counts);
        });

    /// <summary>
    /// Native callers consume live internal state from streaming and materialized SETOF and TABLE callbacks.
    /// </summary>
    /// <param name="table">Whether each row also contains a position column.</param>
    /// <param name="materialize">Whether PostgreSQL requests a materialized store.</param>
    /// <param name="early">Whether the caller stops after the first row.</param>
    [TestMethod]
    [DataRow(false, false, false)]
    [DataRow(false, false, true)]
    [DataRow(false, true, false)]
    [DataRow(false, true, true)]
    [DataRow(true, false, false)]
    [DataRow(true, false, true)]
    [DataRow(true, true, false)]
    [DataRow(true, true, true)]
    public Task InternalSetStatesOutliveRowsAndReleaseWithTheirOwner(bool table, bool materialize, bool early)
        => Run(nameof(InternalSetStatesOutliveRowsAndReleaseWithTheirOwner), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, "SELECT internal_values.internal_reset()", token);
            string function = table ? "internal_rows" : "internal_states";
            Assert.AreEqual(early ? 1 : 4, await Scalar<int>(connection, transaction,
                $"SELECT tests.internal_set_invoke('internal_values.{function}(internal)', 'internal_values.internal_final(internal)', {materialize}, {early})", token));
            int[] counts = await AssertInternalReleased(connection, transaction, token);
            Assert.AreEqual(early && !materialize ? 1 : 2, counts[0]);
        });

    /// <summary>
    /// General managed internal states work through independent groups, NULLs, and advancing window owners.
    /// </summary>
    [TestMethod]
    public Task InternalAggregateStatesPreserveGroupsAndMovingWindows()
        => Run(nameof(InternalAggregateStatesPreserveGroupsAndMovingWindows), async (connection, transaction, token) =>
        {
            await Execute(connection, transaction, "SELECT internal_values.internal_reset()", token);
            Assert.AreSequenceEqual([2550L, 2500L], await Scalar<long[]>(connection, transaction, """
                SELECT array_agg(total ORDER BY key) FROM (
                    SELECT value%2 AS key,internal_values.state_total(value) AS total
                    FROM generate_series(1,100) value GROUP BY key) groups
                """, token));
            Assert.AreSequenceEqual([1L, 1L, 3L, 7L], await Scalar<long[]>(connection, transaction, """
                SELECT array_agg(total ORDER BY position) FROM (
                    SELECT position,internal_values.state_total(value) OVER(ORDER BY position ROWS 1 PRECEDING) AS total
                    FROM (VALUES(1,1),(2,NULL),(3,3),(4,4)) input(position,value)) frames
                """, token));
            Assert.AreEqual(DBNull.Value, await Scalar<object>(connection, transaction,
                "SELECT internal_values.state_total(value) FROM (SELECT 1 AS value WHERE false) input", token));
            await AssertInternalReleased(connection, transaction, token);
        });

    /// <summary>
    /// Actual workers serialize values and rebuild temporary states before copying into the leader's owner.
    /// </summary>
    [TestMethod]
    public Task InternalParallelStatesSerializeDeserializeAndCombineAcrossWorkers()
        => Run(nameof(InternalParallelStatesSerializeDeserializeAndCombineAcrossWorkers), async (connection, transaction, token) =>
        {
            await PrepareParallelInput(connection, transaction, token);
            await Execute(connection, transaction, "SELECT internal_values.internal_reset()", token);
            const string query = "SELECT internal_values.state_total(value) FROM aggregate_values.parallel_input";
            string plan = await Scalar<string>(connection, transaction, "EXPLAIN(ANALYZE,FORMAT JSON) " + query, token);
            using (JsonDocument document = JsonDocument.Parse(plan))
            {
                JsonElement root = document.RootElement[0].GetProperty("Plan");
                Assert.IsGreaterThan(0, WorkersLaunched(root));
                Assert.IsTrue(HasPartialAggregate(root));
            }

            Assert.AreEqual(450015000L, await Scalar<long>(connection, transaction, query, token));
            int[] counts = await AssertInternalReleased(connection, transaction, token);
            Assert.IsGreaterThan(0, counts[3]);
            Assert.IsGreaterThan(0, counts[4]);
        });

    /// <summary>
    /// Errors and rejected borrowed worker state release roots and preserve the same backend for the next query.
    /// </summary>
    /// <param name="failure">The support callback failure mode.</param>
    /// <param name="sqlState">The expected PostgreSQL diagnostic.</param>
    [TestMethod]
    [DataRow(-1, "P7921")]
    [DataRow(-2, "P7922")]
    [DataRow(-3, "P7923")]
    [DataRow(-4, "38000")]
    public Task InternalAggregateErrorsReleaseOwnedStateAndRecover(int failure, string sqlState)
        => Run(nameof(InternalAggregateErrorsReleaseOwnedStateAndRecover), async (connection, transaction, token) =>
        {
            await PrepareParallelInput(connection, transaction, token);
            await Execute(connection, transaction, "SELECT internal_values.internal_reset()", token);
            await transaction.SaveAsync("internal_failure", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<long>(connection, transaction,
                $"SELECT internal_values.state_total({failure}) FROM aggregate_values.parallel_input", token));
            Assert.AreEqual(sqlState, error.SqlState);
            await transaction.RollbackAsync("internal_failure", token);
            if (failure == -4)
            {
                Assert.Contains("destination aggregate memory context", error.MessageText);
            }

            Assert.AreEqual(42L, await Scalar<long>(connection, transaction, "SELECT internal_values.state_total(42)", token));
            Assert.AreEqual(connection.ProcessID, await Scalar<int>(connection, transaction, "SELECT pg_backend_pid()", token));
            await AssertInternalReleased(connection, transaction, token);
        });

    /// <summary>
    /// Value-type payloads read back by value with their exact type, and another type is rejected.
    /// </summary>
    [TestMethod]
    public Task InternalValuePayloadsKeepTheirExactType()
        => Run(nameof(InternalValuePayloadsKeepTheirExactType), async (connection, transaction, token) =>
            Assert.AreEqual("5|6|True|Internal state contains System.Int32; System.Int64 was requested.",
                await Scalar<string>(connection, transaction, "SELECT internal_values.internal_value_payloads()", token)));

    /// <summary>
    /// Rejects managed reads of native pointers without dereferencing them and recovers in the same process.
    /// </summary>
    [TestMethod]
    public Task InternalNativeTypeErrorsPreserveBackendRecovery()
        => Run(nameof(InternalNativeTypeErrorsPreserveBackendRecovery), async (connection, transaction, token) =>
        {
            await transaction.SaveAsync("internal_native_error", token);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<long>(connection, transaction,
                "SELECT tests.internal_invoke('internal_values.internal_wrong_read(internal)',2)", token));
            Assert.AreEqual("38000", error.SqlState);
            Assert.Contains("native pointer", error.MessageText);
            await transaction.RollbackAsync("internal_native_error", token);
            Assert.AreEqual(42L, await Scalar<long>(connection, transaction,
                "SELECT tests.internal_invoke('internal_values.internal_native_read(internal)',2)", token));
            Assert.AreEqual(connection.ProcessID, await Scalar<int>(connection, transaction, "SELECT pg_backend_pid()", token));
        });

    /// <summary>
    /// Checks every leader-owned payload was invalidated and disposed exactly once.
    /// </summary>
    private static async Task<int[]> AssertInternalReleased(NpgsqlConnection connection, NpgsqlTransaction transaction, CancellationToken token)
    {
        int[] counts = await Scalar<int[]>(connection, transaction, "SELECT internal_values.internal_counts()", token);
        Assert.IsGreaterThan(0, counts[0]);
        Assert.AreEqual(counts[0], counts[1]);
        Assert.AreEqual(counts[0], counts[2]);
        return counts;
    }
}
