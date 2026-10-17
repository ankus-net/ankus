using System.Globalization;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// A worker's writes reach the shared statistics while it keeps running, so autovacuum and <c>pg_stat_user_tables</c>
    /// see them, and <c>pg_stat_activity</c> shows the worker idle between transactions. The counts used to stay in the
    /// worker's own memory until it exited.
    /// </summary>
    [TestMethod]
    public async Task BackgroundWorkerStatisticsAreVisibleWhileItRuns()
    {
        CancellationToken token = context.CancellationToken;
        string output = await PublishPackageConsumerAsync("WorkerStatistics", "ankus_worker_statistics", WorkerStatisticsSource, token);
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token,
            additionalConfiguration: ["max_worker_processes = 4", "max_parallel_workers = 0", "max_logical_replication_workers = 0"]);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await ExecutePackageGucAsync(connection, "CREATE EXTENSION ankus_worker_statistics; CREATE TABLE worker_rows (value integer);");
        string database = Assert.IsInstanceOfType<string>(await PackageGucScalarAsync(connection, "SELECT current_database()::text"));
        await using var start = new NpgsqlCommand("SELECT start_statistics_worker($1)", connection);
        start.Parameters.AddWithValue(database);
        int worker = Assert.IsInstanceOfType<int>(await start.ExecuteScalarAsync(token));
        try
        {
            string observed = "";
            for (int attempt = 0; attempt < 300 && observed != "100|idle"; attempt++)
            {
                // Each autocommit query reads a fresh statistics snapshot.
                await using var query = new NpgsqlCommand($"""
                    SELECT concat_ws('|', (SELECT n_tup_ins FROM pg_stat_user_tables WHERE relname = 'worker_rows'),
                        (SELECT state FROM pg_stat_activity WHERE pid = {worker.ToString(CultureInfo.InvariantCulture)}))
                    """, connection);
                observed = Assert.IsInstanceOfType<string>(await query.ExecuteScalarAsync(token));
                if (observed != "100|idle")
                {
                    await Task.Delay(100, token);
                }
            }

            Assert.AreEqual("100|idle", observed, "The worker's inserts and idle state while it runs; the table has " +
                $"{await PackageGucScalarAsync(connection, "SELECT count(*) FROM worker_rows")} rows.\n{cluster.ReadServerLog()}");
            Assert.AreEqual(1L, await PackageGucScalarAsync(connection,
                $"SELECT count(*) FROM pg_stat_activity WHERE pid = {worker.ToString(CultureInfo.InvariantCulture)}"));
        }
        finally
        {
            await ExecutePackageGucAsync(connection, $"SELECT pg_terminate_backend({worker.ToString(CultureInfo.InvariantCulture)})");
        }
    }

    /// <summary>
    /// Starts a worker that inserts rows in one transaction and then waits until it is terminated.
    /// </summary>
    private const string WorkerStatisticsSource = """
        using System.Globalization;
        using Ankus;

        public static class Statistics
        {
            [PgBackgroundWorker]
            public static void Write(nuint rows)
            {
                PgBackgroundWorker.Connect(PgBackgroundWorker.Extra, null);
                PgBackgroundWorker.RunTransaction(() => Spi.Execute(
                    string.Create(CultureInfo.InvariantCulture, $"INSERT INTO worker_rows SELECT generate_series(1, {rows})")));
                while (PgBackgroundWorker.Wait())
                {
                }
            }

            [PgFunction]
            public static int StartStatisticsWorker(string database)
            {
                if (!PgBackgroundWorker.TryStart(new("statistics probe", "WorkerStatistics", nameof(Write))
                {
                    DatabaseAccess = true,
                    StartTime = PgBackgroundWorkerStartTime.RecoveryFinished,
                    Argument = 100,
                    Extra = database,
                    NotifyProcessId = Environment.ProcessId,
                }, out PgBackgroundWorkerHandle? worker))
                {
                    throw new InvalidOperationException("A statistics worker slot was unavailable.");
                }

                using (worker)
                {
                    return worker.WaitForStartup().ProcessId ?? throw new InvalidOperationException("The statistics worker did not start.");
                }
            }
        }
        """;
}
