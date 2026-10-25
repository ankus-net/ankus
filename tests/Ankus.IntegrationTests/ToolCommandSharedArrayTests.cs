using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// A preloaded extension sizes a lock-protected array from <c>max_worker_processes</c>, as pglogical sizes its
    /// worker table. Backends see each other's claims, an index outside the array fails without harming the session,
    /// and a different server setting gives a different length. A backend's exit callbacks release its slot after a
    /// normal disconnect and after a termination, newest first, logging each release, with a failing callback reported
    /// as a warning.
    /// </summary>
    [TestMethod]
    public async Task SharedArraysAreSizedAtStartup()
    {
        CancellationToken token = context.CancellationToken;
        string output = await PublishPackageConsumerAsync("SharedArray", "ankus_shared_array", SharedArraySource, token);
        foreach (int workers in new[] { 13, 3 })
        {
            await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token, sharedPreload: true,
                additionalConfiguration: ["max_worker_processes = " + workers]);
            await using NpgsqlConnection first = await cluster.OpenConnectionAsync(token);
            await using NpgsqlConnection second = await cluster.OpenConnectionAsync(token);
            await ExecutePackageGucAsync(first, "CREATE EXTENSION ankus_shared_array");
            Assert.AreEqual(workers, await PackageGucScalarAsync(first, "SELECT worker_slots()"));
            Assert.AreEqual($"0|7|{workers}|{workers - 1}", await PackageGucScalarAsync(second, "SELECT describe_slots()"));
            Assert.AreEqual(0, await PackageGucScalarAsync(first, "SELECT claim_slot()"));
            Assert.AreEqual(1, await PackageGucScalarAsync(second, "SELECT claim_slot()"));
            Assert.AreEqual(first.ProcessID, await PackageGucScalarAsync(second, "SELECT slot_process(0)"));
            Assert.AreEqual(second.ProcessID, await PackageGucScalarAsync(first, "SELECT slot_process(1)"));
            Assert.AreEqual($"2|7|{workers - 2}|{workers - 1}", await PackageGucScalarAsync(first, "SELECT describe_slots()"));
            PostgresException outside = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                PackageGucScalarAsync(first, $"SELECT slot_process({workers})"));
            Assert.Contains($"less than the length, {workers}", outside.MessageText);
            Assert.AreEqual(0, await PackageGucScalarAsync(first, "SELECT slot_process(2)"));
            if (workers != 13)
            {
                continue;
            }

            await using (NpgsqlConnection leaving = await cluster.OpenConnectionAsync(token))
            {
                Assert.AreEqual(2, await PackageGucScalarAsync(leaving, "SELECT claim_slot_until_exit(false)"));
                Assert.AreEqual($"{leaving.ProcessID}|2", await PackageGucScalarAsync(first, "SELECT slot_state(2)"));
            }

            await WaitForSlotAsync(first, "0|0", token);
            await using NpgsqlConnection terminated = await cluster.OpenConnectionAsync(token);
            Assert.AreEqual(2, await PackageGucScalarAsync(terminated, "SELECT claim_slot_until_exit(true)"));
            await PackageGucScalarAsync(first, $"SELECT pg_terminate_backend({terminated.ProcessID})");
            await WaitForSlotAsync(first, "0|1", token);
            Assert.AreEqual($"2|7|{workers - 2}|{workers - 1}", await PackageGucScalarAsync(first, "SELECT describe_slots()"));

            // Every callback runs before the first failure is reported, so the warning follows the release the slot shows.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            while (!cluster.ReadServerLog().Contains("exit callback failed", StringComparison.Ordinal))
            {
                await Task.Delay(10, timeout.Token);
            }

            string log = cluster.ReadServerLog();
            Assert.Contains("LOG:  released slot 2 at exit code 0", log);
            Assert.Contains("LOG:  released slot 2 at exit code 1", log);
        }
    }

    /// <summary>
    /// Waits for slot 2's process and generation, which an exit callback writes after the backend disconnects.
    /// </summary>
    private async Task WaitForSlotAsync(NpgsqlConnection connection, string expected, CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(30);
        string? state;
        while ((state = (string?)await PackageGucScalarAsync(connection, "SELECT slot_state(2)")) != expected)
        {
            Assert.IsLessThan(deadline, DateTime.UtcNow, $"Slot 2 stayed {state} instead of {expected}.");
            await Task.Delay(50, token);
        }
    }

    private const string SharedArraySource = """
        using Ankus;
        using Ankus.Postgres;

        public readonly record struct WorkerTable(int Claimed, long Generation);

        public readonly record struct WorkerSlot(int ProcessId, long Generation);

        public static class SharedArrayFunctions
        {
            private static readonly PgLwLock<WorkerTable, WorkerSlot> Workers = new("ankus_shared_array.workers");

            [PgModuleLoad]
            public static void Load()
            {
                int length;
                unsafe
                {
                    length = NativeGlobals.max_worker_processes;
                }

                PgSharedMemory.Initialize(Workers, length, static () => new WorkerTable(0, 7), static index => new WorkerSlot(0, index));
            }

            [PgFunction]
            public static int WorkerSlots()
            {
                using PgLwLockShareGuard<WorkerTable, WorkerSlot> guard = Workers.Share();
                return guard.Length;
            }

            [PgFunction]
            public static int ClaimSlot()
            {
                using PgLwLockExclusiveGuard<WorkerTable, WorkerSlot> guard = Workers.Exclusive();
                return guard.Mutate(static (ref header, slots) =>
                {
                    for (int index = 0; index < slots.Length; index++)
                    {
                        if (slots[index].ProcessId == 0)
                        {
                            slots[index] = slots[index] with { ProcessId = System.Environment.ProcessId };
                            header = header with { Claimed = header.Claimed + 1 };
                            return index;
                        }
                    }

                    return -1;
                });
            }

            [PgFunction]
            public static string DescribeSlots()
            {
                using PgLwLockShareGuard<WorkerTable, WorkerSlot> guard = Workers.Share();
                return guard.Read(static (in header, slots) =>
                {
                    int free = 0;
                    foreach (WorkerSlot slot in slots)
                    {
                        free += slot.ProcessId == 0 ? 1 : 0;
                    }

                    return header.Claimed + "|" + header.Generation + "|" + free + "|" + slots[slots.Length - 1].Generation;
                });
            }

            [PgFunction]
            public static int ClaimSlotUntilExit(bool failFirst)
            {
                int index = ClaimSlot();
                _ = PgProcess.OnExit(code => Release(index, code));
                if (failFirst)
                {
                    _ = PgProcess.OnExit(static _ => throw new System.InvalidOperationException("exit callback failed"));
                }

                return index;
            }

            [PgFunction]
            public static string SlotState(int index)
            {
                using PgLwLockShareGuard<WorkerTable, WorkerSlot> guard = Workers.Share();
                WorkerSlot slot = guard[index];
                return slot.ProcessId + "|" + slot.Generation;
            }

            private static void Release(int index, int code)
            {
                using PgLwLockExclusiveGuard<WorkerTable, WorkerSlot> guard = Workers.Exclusive();
                guard[index] = new WorkerSlot(0, code);
                guard.Header = guard.Header with { Claimed = guard.Header.Claimed - 1 };
                PgLog.Log($"released slot {index} at exit code {code}");
            }

            [PgFunction]
            public static int SlotProcess(int index)
            {
                using PgLwLockShareGuard<WorkerTable, WorkerSlot> guard = Workers.Share();
                return guard[index].ProcessId;
            }
        }
        """;
}
