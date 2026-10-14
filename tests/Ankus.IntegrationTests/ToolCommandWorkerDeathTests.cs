using System.Diagnostics;
using System.Globalization;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// A dead test-owned postmaster wakes both the worker latch and a tracked shutdown wait with their exact death outcomes.
    /// </summary>
    [TestMethod]
    public async Task BackgroundWorkerPostmasterDeathStopsWaiters()
    {
        CancellationToken token = context.CancellationToken;
        string output = await PublishPackageConsumerAsync("WorkerDeath", "ankus_worker_death", WorkerDeathSource, token);
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token, sharedPreload: true,
            additionalConfiguration: ["max_worker_processes = 4", "max_parallel_workers = 0", "max_logical_replication_workers = 0", "autovacuum = off"]);
        string record = "ankus-death-" + Guid.NewGuid().ToString("N");
        var processes = new List<Process>();
        // codeql[cs/missed-using-statement] The postmaster is found partway through the test and disposed after the cluster recovers.
        Process? postmaster = null;
        bool killed = false;
        try
        {
            int[] workers;
            await using (NpgsqlConnection connection = await cluster.OpenConnectionAsync(token))
            {
                await ExecutePackageGucAsync(connection, "CREATE EXTENSION ankus_worker_death");
                await using var start = new NpgsqlCommand("SELECT worker_death_waiters($1)", connection);
                start.Parameters.AddWithValue(record);
                workers = Assert.IsInstanceOfType<int[]>(await start.ExecuteScalarAsync(token));
                Assert.HasCount(2, workers);
                Assert.HasCount(2, workers.Distinct());
                Assert.IsTrue(workers.All(process => process > 0 && process != connection.ProcessID));
                var elapsed = Stopwatch.StartNew();
                while (true)
                {
                    await using var waiting = new NpgsqlCommand("""
                        SELECT count(*)::integer FROM pg_stat_activity
                        WHERE (pid = $1 AND wait_event_type = 'Extension')
                           OR (pid = $2 AND wait_event = $3)
                        """, connection);
                    waiting.Parameters.AddWithValue(workers[0]);
                    waiting.Parameters.AddWithValue(workers[1]);
                    waiting.Parameters.AddWithValue(s_installation.Version.Major >= 17 ? "BgworkerShutdown" : "BgWorkerShutdown");
                    if (Assert.IsInstanceOfType<int>(await waiting.ExecuteScalarAsync(token)) == 2)
                    {
                        break;
                    }

                    TimeSpan duration = elapsed.Elapsed;
                    Assert.IsLessThan(TimeSpan.FromSeconds(30), duration,
                        duration >= TimeSpan.FromSeconds(30) ? cluster.ReadServerLog() : null);
                    await Task.Delay(25, token);
                }

                await using var children = new NpgsqlCommand("SELECT pid FROM pg_stat_activity WHERE pid <> pg_backend_pid()", connection);
                await using NpgsqlDataReader reader = await children.ExecuteReaderAsync(token);
                while (await reader.ReadAsync(token))
                {
                    processes.Add(Process.GetProcessById(reader.GetInt32(0)));
                }

                Assert.Contains(workers[0], processes.Select(static process => process.Id));
                Assert.Contains(workers[1], processes.Select(static process => process.Id));
                string[] identity = await File.ReadAllLinesAsync(Path.Join(cluster.DataDirectory, "postmaster.pid"), token);
                Assert.AreEqual(Path.GetFullPath(cluster.DataDirectory), Path.GetFullPath(identity[1]));
                int postmasterId = int.Parse(identity[0], CultureInfo.InvariantCulture);
                Assert.IsGreaterThan(0, postmasterId);
                Assert.AreNotEqual(Environment.ProcessId, postmasterId);
                Assert.AreNotEqual(connection.ProcessID, postmasterId);
                Assert.DoesNotContain(postmasterId, processes.Select(static process => process.Id));
                postmaster = Process.GetProcessById(postmasterId);
                Assert.AreEqual("postgres", postmaster.ProcessName);
            }

            string latchReady = Path.Join(cluster.DataDirectory, record + ".latch.ready");
            string observerReady = Path.Join(cluster.DataDirectory, record + ".observer.ready");
            Assert.AreEqual(workers[0].ToString(CultureInfo.InvariantCulture), await File.ReadAllTextAsync(latchReady, token));
            Assert.AreEqual(workers[1].ToString(CultureInfo.InvariantCulture), await File.ReadAllTextAsync(observerReady, token));
            postmaster.Kill();
            killed = true;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await postmaster.WaitForExitAsync(timeout.Token);
            await Task.WhenAll(processes.Select(process => process.WaitForExitAsync(timeout.Token)));
            Assert.AreEqual("False|False", await File.ReadAllTextAsync(Path.Join(cluster.DataDirectory, record + ".latch.result"), token));
            Assert.AreEqual("PostmasterDied|False", await File.ReadAllTextAsync(Path.Join(cluster.DataDirectory, record + ".observer.result"), token));
        }
        finally
        {
            if (killed)
            {
                foreach (Process process in processes.Where(static process => !process.HasExited))
                {
                    process.Kill();
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                    await process.WaitForExitAsync(timeout.Token);
                }

                // Recover the owned cluster so PostgreSQL reclaims stale shared memory before normal disposal.
                using var recoveryTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await PackageProcessRunner.RunCheckedAsync(cluster.Installation.PgCtlPath,
                    ["start", "-D", cluster.DataDirectory, "-l", cluster.LogFilePath, "-w", "-t", "30"],
                    s_environment, recoveryTimeout.Token, captureOutput: false);
                await WaitForWorkerClusterRestartAsync(cluster, postmaster!.Id, recoveryTimeout.Token);
                await using NpgsqlConnection recovered = await cluster.OpenConnectionAsync(recoveryTimeout.Token);
                Assert.AreEqual(42, await PackageGucScalarAsync(recovered, "SELECT 42"));
            }

            // codeql[cs/missed-using-statement] Each worker process is found partway through the test and disposed after the cluster recovers.
            foreach (Process process in processes)
            {
                process.Dispose();
            }

            postmaster?.Dispose();
        }
    }

    /// <summary>
    /// Requires the replacement postmaster's ready state rather than Windows pg_ctl's recent stale PID file.
    /// </summary>
    private static async Task WaitForWorkerClusterRestartAsync(PostgresTestCluster cluster, int previousProcess, CancellationToken token)
    {
        string previous = previousProcess.ToString(CultureInfo.InvariantCulture);
        string path = Path.Join(cluster.DataDirectory, "postmaster.pid");
        while (true)
        {
            string[] identity;
            try
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                identity = (await reader.ReadToEndAsync(token)).ReplaceLineEndings("\n").Split('\n');
            }
            catch (Exception error) when (error is FileNotFoundException ||
                (OperatingSystem.IsWindows() && error is UnauthorizedAccessException or IOException))
            {
                // The new postmaster has not written its file yet. On Windows the old file also denies access while it
                // is pending deletion, and PostgreSQL may hold it while replacing it.
                identity = [];
            }

            if (identity.Length >= 8 && identity[0] != previous && identity[7].Trim() == "ready")
            {
                Assert.AreEqual(Path.GetFullPath(cluster.DataDirectory), Path.GetFullPath(identity[1]));
                Assert.IsGreaterThan(0, int.Parse(identity[0], CultureInfo.InvariantCulture));
                return;
            }

            await Task.Delay(25, token);
        }
    }

    /// <summary>
    /// Retains externally observable files after SQL connections become unavailable on postmaster death.
    /// </summary>
    private const string WorkerDeathSource = """
        using System.Globalization;
        using Ankus;

        public static class DeathWaiters
        {
            [PgBackgroundWorker]
            public static void LatchWorker(nuint argument)
            {
                PgBackgroundWorker.Connect(null);
                string record = PgBackgroundWorker.Extra;
                File.WriteAllText(record + ".latch.ready", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                bool continued;
                do
                {
                    continued = PgBackgroundWorker.Wait();
                }
                while (continued);

                File.WriteAllText(record + ".latch.result", $"{continued}|{PgBackgroundWorker.CanContinue}");
            }

            [PgBackgroundWorker]
            public static void ChildWorker(nuint argument)
            {
                PgBackgroundWorker.Connect(null);
                while (PgBackgroundWorker.Wait())
                {
                }
            }

            [PgBackgroundWorker]
            public static void ObserverWorker(nuint argument)
            {
                PgBackgroundWorker.Connect(null);
                string record = PgBackgroundWorker.Extra;
                using PgBackgroundWorkerHandle child = Start(nameof(ChildWorker), record);
                File.WriteAllText(record + ".observer.ready", Environment.ProcessId.ToString(CultureInfo.InvariantCulture));
                PgBackgroundWorkerStatus status = child.WaitForShutdown();
                File.WriteAllText(record + ".observer.result", $"{status}|{PgBackgroundWorker.CanContinue}");
            }

            [PgFunction]
            public static int[] WorkerDeathWaiters(string record)
            {
                using PgBackgroundWorkerHandle latch = Start(nameof(LatchWorker), record);
                using PgBackgroundWorkerHandle observer = Start(nameof(ObserverWorker), record);
                return [latch.GetState().ProcessId!.Value, observer.GetState().ProcessId!.Value];
            }

            private static PgBackgroundWorkerHandle Start(string entry, string record)
            {
                if (!PgBackgroundWorker.TryStart(new("death " + entry, "WorkerDeath", entry)
                {
                    DatabaseAccess = true,
                    NotifyProcessId = Environment.ProcessId,
                    Extra = record,
                }, out PgBackgroundWorkerHandle? worker))
                {
                    throw new InvalidOperationException("A death-observation worker slot was unavailable.");
                }

                try
                {
                    PgBackgroundWorkerState started = worker.WaitForStartup();
                    if (started.Status != PgBackgroundWorkerStatus.Started)
                    {
                        throw new InvalidOperationException("The death-observation worker did not start: " + started);
                    }

                    return worker;
                }
                catch
                {
                    worker.Terminate();
                    worker.Dispose();
                    throw;
                }
            }
        }
        """;
}
