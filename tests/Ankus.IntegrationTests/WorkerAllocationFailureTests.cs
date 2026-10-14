using System.Diagnostics;
using System.Globalization;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies actual native worker allocation failures, registration side effects, ownership and same-session recovery.
/// </summary>
/// <param name="context">The current test context.</param>
[TestClass]
[DoNotParallelize]
public sealed class WorkerAllocationFailureTests(TestContext context)
{
    /// <summary>
    /// Each failed registration releases its owner and diagnostics before a real replacement worker can run.
    /// </summary>
    /// <param name="mode">The selected native allocation or identity boundary.</param>
    /// <param name="state">The expected PostgreSQL SQLSTATE.</param>
    /// <param name="message">The exact transported native diagnostic.</param>
    /// <param name="owners">The number of worker owners created and deleted during failure.</param>
    /// <param name="allocations">The number of selected-context allocation calls during failure.</param>
    [TestMethod]
    [DataRow(1, "53200", "controlled worker owner allocation failure", 0, 0)]
    [DataRow(2, "53200", "controlled worker entry allocation failure", 1, 1)]
    [DataRow(3, "53200", "controlled PostgreSQL worker handle allocation failure", 1, 2)]
    [DataRow(4, "54000", "Ankus background-worker handle identities are exhausted", 0, 0)]
    public Task WorkerRegistrationAllocationFailuresRecover(int mode, string state, string message, int owners, int allocations)
        => PostgresFixture.Cluster.RunInTransactionAsync($"{nameof(WorkerAllocationFailureTests)}_{mode}",
            async (connection, transaction, token) =>
            {
                int backend = connection.ProcessID;
                for (int invocation = 0; invocation < 2; invocation++)
                {
                    string identity = Guid.NewGuid().ToString("N");
                    string failed = Path.Join(PostgresFixture.Cluster.DataDirectory, $"ankus-worker-fault-{identity}-failed.ready");
                    string retry = Path.Join(PostgresFixture.Cluster.DataDirectory, $"ankus-worker-fault-{identity}-retry.ready");
                    try
                    {
                        await using var command = new NpgsqlCommand("SELECT tests.worker_allocation_fault($1, $2)", connection, transaction);
                        command.Parameters.AddWithValue(mode);
                        command.Parameters.AddWithValue(identity);
                        string report = Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token));
                        string[] fields = report.Split('|');
                        Assert.HasCount(14, fields);
                        Assert.AreSequenceEqual([state, message,
                            mode == 4 ? "" : "worker registration allocation failed",
                            mode == 4 ? "" : "inspect registration before retrying"], fields.Take(4));
                        int[] values = [.. fields.Skip(4).Select(value => int.Parse(value, CultureInfo.InvariantCulture))];
                        Assert.AreEqual(owners, values[0]);
                        Assert.AreEqual(owners, values[1]);
                        Assert.AreEqual(allocations, values[2]);
                        Assert.AreEqual(mode == 3 ? 1 : 0, values[3]);
                        int published = values[4];
                        int replacement = values[5];
                        Assert.IsGreaterThan(0, replacement);
                        Assert.AreNotEqual(backend, replacement);
                        Assert.AreEqual(mode == 3 ? 2 : 1, values[6]);
                        Assert.AreEqual(owners + 1, values[7]);
                        Assert.AreEqual(owners + 1, values[8]);
                        Assert.AreEqual(allocations + 2, values[9]);
                        Assert.AreEqual($"{replacement.ToString(CultureInfo.InvariantCulture)}|42", await File.ReadAllTextAsync(retry, token));
                        await RequireWorkerExitAsync(replacement, token);
                        if (mode == 3)
                        {
                            Assert.IsGreaterThan(0, published);
                            Assert.AreNotEqual(backend, published);
                            Assert.AreNotEqual(replacement, published);
                            Assert.AreEqual($"{published.ToString(CultureInfo.InvariantCulture)}|42", await File.ReadAllTextAsync(failed, token));
                            await RequireWorkerExitAsync(published, token);
                        }
                        else
                        {
                            Assert.AreEqual(0, published);
                            Assert.IsFalse(File.Exists(failed));
                        }
                    }
                    finally
                    {
                        File.Delete(failed);
                        File.Delete(retry);
                    }
                }

                await using var recovered = new NpgsqlCommand("""
                    SELECT pg_backend_pid(), 42, count(*)::integer
                    FROM ankus_test_memory.contexts WHERE name = 'Ankus background-worker handle'
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await recovered.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(backend, reader.GetInt32(0));
                Assert.AreEqual(42, reader.GetInt32(1));
                Assert.AreEqual(0, reader.GetInt32(2));
                Assert.IsFalse(await reader.ReadAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Requires actual worker exit after graceful termination even when registration could not return a handle.
    /// </summary>
    private static async Task RequireWorkerExitAsync(int processId, CancellationToken token)
    {
        Process process;
        try
        {
            process = Process.GetProcessById(processId);
        }
        catch (ArgumentException)
        {
            return;
        }

        using (process)
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
            Assert.IsTrue(process.HasExited);
        }
    }
}
