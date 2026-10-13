using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// A preloaded extension sizes a lock-protected array from <c>max_worker_processes</c>, as pglogical sizes its
    /// worker table. Backends see each other's claims, an index outside the array fails without harming the session,
    /// and a different server setting gives a different length.
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
            public static int SlotProcess(int index)
            {
                using PgLwLockShareGuard<WorkerTable, WorkerSlot> guard = Workers.Share();
                return guard[index].ProcessId;
            }
        }
        """;
}
