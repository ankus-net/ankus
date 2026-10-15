using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies actual PostgreSQL statement cancellation survives managed catch blocks and nested recovery.
/// </summary>
/// <param name="context">The per-test cancellation context.</param>
[TestClass]
public sealed class CancellationTests(TestContext context)
{
    /// <summary>
    /// A canceled query retains SQLSTATE and its original message, executes finally, and leaves the session recoverable.
    /// </summary>
    /// <param name="mode">The guarded callback path.</param>
    /// <param name="replace">Whether managed code attempts to replace the cancellation.</param>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    [DataRow(3, false)]
    [DataRow(3, true)]
    [DataRow(4, false)]
    [DataRow(4, true)]
    [DataRow(5, false)]
    [DataRow(5, true)]
    [DataRow(6, false)]
    [DataRow(6, true)]
    public async Task StatementCancellationCannotBeSwallowed(int mode, bool replace)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await using var command = new NpgsqlCommand("SELECT datatype.cancel_observations()", connection);
        Assert.AreEqual(0, await command.ExecuteScalarAsync(token));
        command.CommandText = "SET statement_timeout = '200ms'";
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT datatype.cancel_catch($1, $2)";
        command.Parameters.AddWithValue(mode);
        command.Parameters.AddWithValue(replace);
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual(PostgresErrorCodes.QueryCanceled, error.SqlState);
        Assert.AreEqual("canceling statement due to statement timeout", error.MessageText);
        command.Parameters.Clear();
        command.CommandText = "SET statement_timeout = 0";
        await StatementTimeoutCleanup.RetryAsync(() => command.ExecuteNonQueryAsync(token));
        command.CommandText = "SELECT datatype.cancel_observations()";
        Assert.AreEqual(7, await command.ExecuteScalarAsync(token));
        command.CommandText = "SELECT 42";
        Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
        Assert.AreEqual(backend, connection.ProcessID);
    }

    /// <summary>
    /// Repeated explicit subtransaction rollback removes retained frame failures before the next poll.
    /// </summary>
    [TestMethod]
    public async Task PollingResumesAfterExplicitRollback()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int backend = connection.ProcessID;
        await using var command = new NpgsqlCommand("SELECT datatype.poll_after_rollback()", connection);
        Assert.AreEqual(3, await command.ExecuteScalarAsync(context.CancellationToken));
        command.CommandText = "SELECT datatype.cancel_observations(), 42";
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
        Assert.IsTrue(await reader.ReadAsync(context.CancellationToken));
        Assert.AreEqual(0, reader.GetInt32(0));
        Assert.AreEqual(42, reader.GetInt32(1));
        Assert.AreEqual(backend, connection.ProcessID);
    }
}
