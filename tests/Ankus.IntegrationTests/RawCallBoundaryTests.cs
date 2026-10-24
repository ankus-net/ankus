using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies generated native calls and global accesses after their binding check and global addresses are reused.
/// </summary>
/// <param name="context">The per-test cancellation context.</param>
[TestClass]
public sealed class RawCallBoundaryTests(TestContext context)
{
    private const string Rejected = "PostgreSQL memory contexts require an active backend callback.";

    /// <summary>
    /// Pointer and integer globals read and write the native storage itself, as their original addresses and
    /// PostgreSQL's own setting report.
    /// </summary>
    [TestMethod]
    public Task GlobalsReadAndWriteNativeStorage()
        => ScalarAsync("SELECT raw_call_boundary.globals_read_and_write_in_place()", "True|True|True|True|True|True");

    /// <summary>
    /// A thread with no backend callback cannot call or reach a global, even after the backend thread cached the
    /// binding and the global's address, and nothing it attempted changes the backend's values.
    /// </summary>
    [TestMethod]
    public Task WorkerThreadsCannotReuseCachedAccess()
        => ScalarAsync("SELECT raw_call_boundary.worker_threads_are_rejected()", $"{Rejected}|{Rejected}|{Rejected}|{Rejected}|True");

    /// <summary>
    /// An ERROR from a generated call reaches managed code with its SQLSTATE and message, and after the subtransaction
    /// rolls it back the same callback keeps calling and reading globals.
    /// </summary>
    [TestMethod]
    public Task RaisingCallsRecoverInSubtransactions()
        => ScalarAsync("SELECT raw_call_boundary.raising_calls_recover()", "22P02|invalid input syntax for type integer: \"x\"|True");

    /// <summary>
    /// After an ERROR that no subtransaction recovers, a generated call and every global access fail with that ERROR
    /// instead of reusing the cached binding or address, PostgreSQL fails the statement, and the session recovers.
    /// </summary>
    [TestMethod]
    public async Task UnrecoveredFailuresRefuseCachedAccess()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await using var command = new NpgsqlCommand("SELECT raw_call_boundary.unrecovered_failure_blocks_access()", connection);
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual(PostgresErrorCodes.InvalidTextRepresentation, error.SqlState);
        Assert.AreEqual("invalid input syntax for type integer: \"x\"", error.MessageText);
        command.CommandText = "SELECT raw_call_boundary.failure_observations()";
        Assert.AreEqual("22P02|22P02|22P02|22P02|22P02", await command.ExecuteScalarAsync(token));
        command.CommandText = "SELECT raw_call_boundary.globals_read_and_write_in_place()";
        Assert.AreEqual("True|True|True|True|True|True", await command.ExecuteScalarAsync(token));
        Assert.AreEqual(backend, connection.ProcessID);
    }

    /// <summary>
    /// Each operation the cost measurement repeats runs to completion and reports a time.
    /// </summary>
    /// <param name="kind">The measured operation.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    public async Task MeasuredOperationsComplete(int kind)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT raw_call_boundary.measure($1, 1000)", connection);
        command.Parameters.AddWithValue(kind);
        double nanoseconds = Assert.IsInstanceOfType<double>(await command.ExecuteScalarAsync(context.CancellationToken));
        Assert.IsGreaterThanOrEqualTo(0.0, nanoseconds);
    }

    private async Task ScalarAsync(string sql, string expected)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand(sql, connection);
        Assert.AreEqual(expected, await command.ExecuteScalarAsync(context.CancellationToken));
    }
}
