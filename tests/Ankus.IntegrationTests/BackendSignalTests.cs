using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies that PostgreSQL's process signals reach only the backend thread while runtime threads run beside it.
/// </summary>
/// <param name="context">The per-test cancellation context.</param>
[TestClass]
public sealed class BackendSignalTests(TestContext context)
{
    /// <summary>
    /// Signals sent while the backend thread blocks them stay pending for it and run its handlers once unblocked.
    /// </summary>
    /// <remarks>
    /// PostgreSQL 13 wakes a latch from its <c>SIGUSR1</c> handler. Run on another thread, that handler can miss the
    /// backend's wait, and a parallel leader then sleeps with full worker queues.
    /// </remarks>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task BlockedBackendSignalsWaitForTheBackendThread()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int backend = connection.ProcessID;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            await using var command = new NpgsqlCommand("SELECT datatype.held_backend_signals()", connection);
            bool[] observed = Assert.IsInstanceOfType<bool[]>(await command.ExecuteScalarAsync(context.CancellationToken));
            Assert.AreSequenceEqual([true, true, true, true], observed);
        }

        await using var identity = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        Assert.AreEqual(backend, await identity.ExecuteScalarAsync(context.CancellationToken));
    }

    /// <summary>
    /// A routed handler restored as a plain function still runs safely, holds blocked signals for the backend thread,
    /// and regains its sender information when the runtime next starts a thread.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.OSX)]
    public async Task PlainRestoredHandlerKeepsRoutingSignals()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        int backend = connection.ProcessID;
        await using var command = new NpgsqlCommand("SELECT datatype.restored_plain_backend_signal_handler()", connection);
        bool[] observed = Assert.IsInstanceOfType<bool[]>(await command.ExecuteScalarAsync(context.CancellationToken));
        Assert.AreSequenceEqual([true, true, true, true], observed);
        await using var identity = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
        Assert.AreEqual(backend, await identity.ExecuteScalarAsync(context.CancellationToken));
    }
}
