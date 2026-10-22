using System.Diagnostics;
using System.Globalization;
using System.Text;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// A canceled worker transaction rolls back before recovery while a swallowed terminal report still ends the worker.
    /// </summary>
    /// <param name="mode">Zero propagates cancellation, one swallows it, two swallows FATAL and four cancels a transaction's latch wait.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(4)]
    [DataRow(6)]
    public async Task WorkerTransactionsRecoverCancellationButRetainTerminalReports(int mode)
    {
        CancellationToken token = context.CancellationToken;
        string output = await GetSharedPublicationAsync(s_workerCancellationPublication, token);
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token, sharedPreload: true,
            additionalConfiguration: ["max_worker_processes = 4", "max_parallel_workers = 0", "max_logical_replication_workers = 0", "log_error_verbosity = verbose"]);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await ExecutePackageGucAsync(connection, "CREATE EXTENSION ankus_worker_cancellation; CREATE TABLE worker_cancel_values(value integer)");
        int process = Assert.IsInstanceOfType<int>(await PackageGucScalarAsync(connection,
            "SELECT cancellation_start(" + mode.ToString(CultureInfo.InvariantCulture) + ")"));
        Assert.IsGreaterThan(0, process);
        Assert.AreNotEqual(connection.ProcessID, process);
        string pid = process.ToString(CultureInfo.InvariantCulture);
        try
        {
            await WaitForCancellationWorkerAsync(connection, cluster, process,
                "cancellation_pid() = " + pid, requireAlive: false);
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection,
                "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE pid = " + pid + ")")));
            await ExecutePackageGucAsync(connection, "SELECT cancellation_release()");
            if (mode == 6)
            {
                // Termination ends the running statement as PostgreSQL's die() does, rather than waiting for the sleep.
                await WaitForCancellationWorkerAsync(connection, cluster, process,
                    "EXISTS(SELECT FROM pg_stat_activity WHERE pid = " + pid + " AND wait_event = 'PgSleep' AND xact_start IS NOT NULL)");
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection, "SELECT pg_terminate_backend(" + pid + ")")));
                await WaitForCancellationWorkerAsync(connection, cluster, process,
                    "NOT EXISTS(SELECT FROM pg_stat_activity WHERE pid = " + pid + ")", requireAlive: false);
                Assert.AreEqual(0L, await PackageGucScalarAsync(connection, "SELECT count(*) FROM worker_cancel_values"));
                string log = cluster.ReadServerLog();
                // PostgreSQL 14 added the worker-specific message; PostgreSQL 13's ProcessInterrupts reports the connection.
                Assert.Contains(s_installation.Version.Major >= 14
                    ? "FATAL:  57P01: terminating background worker \"Ankus cancellation worker\" due to administrator command"
                    : "FATAL:  57P01: terminating connection due to administrator command", log);
                Assert.DoesNotContain("worker cancellation recovered", log);
            }
            else if (mode == 2)
            {
                await WaitForCancellationWorkerAsync(connection, cluster, process,
                    "NOT EXISTS(SELECT FROM pg_stat_activity WHERE pid = " + pid + ")", requireAlive: false);
                Assert.AreEqual(0L, await PackageGucScalarAsync(connection, "SELECT count(*) FROM worker_cancel_values"));
                string log = cluster.ReadServerLog();
                Assert.Contains("FATAL:  P7861: owned worker transaction fatal", log);
                Assert.Contains("owned worker fatal detail", log);
                Assert.DoesNotContain("worker cancellation recovered", log);
            }
            else
            {
                await WaitForCancellationWorkerAsync(connection, cluster, process,
                    "EXISTS(SELECT FROM pg_stat_activity WHERE pid = " + pid + " AND wait_event = '" +
                    (mode == 4 ? "Extension" : "PgSleep") + "' AND xact_start IS NOT NULL)");
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection, "SELECT pg_cancel_backend(" + pid + ")")));
                await WaitForCancellationWorkerAsync(connection, cluster, process, "(cancellation_status())[4] = 1");
                int[] status = Assert.IsInstanceOfType<int[]>(await PackageGucScalarAsync(connection, "SELECT cancellation_status()"));
                Assert.AreSequenceEqual([mode == 0 ? 0 : 1, 1, 1, 1], status);
                Assert.AreSequenceEqual([2], Assert.IsInstanceOfType<int[]>(await PackageGucScalarAsync(connection,
                    "SELECT array_agg(value ORDER BY value) FROM worker_cancel_values")));
                Assert.AreEqual(process, await PackageGucScalarAsync(connection, "SELECT cancellation_pid()"));
                Assert.Contains("worker cancellation recovered", cluster.ReadServerLog());
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection, "SELECT pg_reload_conf()")));
                await WaitForCancellationWorkerAsync(connection, cluster, process, "cancellation_reloaded() = 1");
                Assert.AreEqual(process, await PackageGucScalarAsync(connection, "SELECT cancellation_pid()"));
            }
        }
        finally
        {
            bool alive = Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection,
                "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE pid = " + pid + ")"));
            if (alive)
            {
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection, "SELECT pg_terminate_backend(" + pid + ")")));
            }

            await WaitForCancellationWorkerAsync(connection, cluster, process,
                "NOT EXISTS(SELECT FROM pg_stat_activity WHERE pid = " + pid + ")", requireAlive: false);
        }

        Assert.AreEqual(42, await PackageGucScalarAsync(connection, "SELECT 42"));
    }

    /// <summary>
    /// Cancellation during an idle latch wait leaves the same worker able to wait, log and commit again.
    /// </summary>
    [TestMethod]
    public async Task WorkerIdleWaitRecoversRepeatedCancellation()
        => await WorkerIdleCancellationAsync(poll: false);

    /// <summary>
    /// Explicit checks during managed work recover cancellation twice without poisoning an idle worker's lifetime.
    /// </summary>
    [TestMethod]
    public async Task WorkerIdleInterruptChecksRecoverRepeatedCancellation()
        => await WorkerIdleCancellationAsync(poll: true);

    /// <summary>
    /// In a LATIN1 database a worker's client encoding is LATIN1, so its reports between transactions, an error captured
    /// after its transaction has ended and an idle cancellation all convert text with no transaction available, and the
    /// worker keeps running.
    /// </summary>
    [TestMethod]
    public async Task WorkerDiagnosticsInLatin1DatabaseConvertOutsideTransactions()
        => await WorkerIdleCancellationAsync(poll: false, latin1: true);

    /// <summary>
    /// Cancels an idle worker at the requested native boundary and observes successful later logging and transactions.
    /// </summary>
    /// <param name="poll">Whether the worker checks interrupts while doing managed work instead of waiting on its latch.</param>
    /// <param name="latin1">Whether the worker runs in a LATIN1 database and first reports non-ASCII diagnostics.</param>
    private async Task WorkerIdleCancellationAsync(bool poll, bool latin1 = false)
    {
        CancellationToken token = context.CancellationToken;
        string output = await GetSharedPublicationAsync(s_workerCancellationPublication, token);
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token, sharedPreload: true,
            additionalConfiguration: ["max_worker_processes = 4", "max_parallel_workers = 0", "max_logical_replication_workers = 0", "log_error_verbosity = verbose"]);
        await using NpgsqlConnection administrator = await cluster.OpenConnectionAsync(token);
        await using NpgsqlConnection connection = latin1 ? await OpenLatin1DatabaseAsync(administrator, token) : administrator;
        await ExecutePackageGucAsync(connection, "CREATE EXTENSION ankus_worker_cancellation; CREATE TABLE worker_cancel_values(value integer)");
        (int Native, int Server) mark = EncodedServerLog.Mark(cluster);
        int process = Assert.IsInstanceOfType<int>(await PackageGucScalarAsync(connection,
            latin1 ? "SELECT cancellation_start(7)" : poll ? "SELECT cancellation_start(5)" : "SELECT cancellation_start(3)"));
        Assert.IsGreaterThan(0, process);
        Assert.AreNotEqual(connection.ProcessID, process);
        string pid = process.ToString(CultureInfo.InvariantCulture);
        try
        {
            await WaitForCancellationWorkerAsync(connection, cluster, process, "cancellation_pid() = " + pid, requireAlive: false);
            await ExecutePackageGucAsync(connection, "SELECT cancellation_release()");
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                string count = attempt.ToString(CultureInfo.InvariantCulture);
                await WaitForCancellationWorkerAsync(connection, cluster, process,
                    "cancellation_waiting() = " + count + " AND EXISTS(SELECT FROM pg_stat_activity WHERE pid = " + pid +
                    (poll ? " AND wait_event IS NULL" : " AND wait_event = 'Extension'") + " AND xact_start IS NULL)");
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection, "SELECT pg_cancel_backend(" + pid + ")")));
                await WaitForCancellationWorkerAsync(connection, cluster, process, "(cancellation_status())[4] = " + count);
                Assert.AreSequenceEqual([latin1 ? s_installation.Version.Major >= 14 ? 1 : 2 : 0, attempt, attempt, attempt],
                    Assert.IsInstanceOfType<int[]>(await PackageGucScalarAsync(connection, "SELECT cancellation_status()")));
                Assert.AreEqual((long)attempt, await PackageGucScalarAsync(connection, "SELECT count(*) FROM worker_cancel_values"));
                Assert.AreEqual(process, await PackageGucScalarAsync(connection, "SELECT cancellation_pid()"));
            }

            Assert.AreSequenceEqual([1, 2], Assert.IsInstanceOfType<int[]>(await PackageGucScalarAsync(connection,
                "SELECT array_agg(value ORDER BY value) FROM worker_cancel_values")));
            Assert.Contains("worker idle cancellation recovered", cluster.ReadServerLog());
            if (latin1)
            {
                await EncodedServerLog.AssertReportedAsync(cluster, mark, "worker diagnostics café", Encoding.Latin1, token);
            }

            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection, "SELECT pg_reload_conf()")));
            await WaitForCancellationWorkerAsync(connection, cluster, process, "cancellation_reloaded() = 1");
        }
        finally
        {
            bool alive = Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection,
                "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE pid = " + pid + ")"));
            if (alive)
            {
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection, "SELECT pg_terminate_backend(" + pid + ")")));
            }

            await WaitForCancellationWorkerAsync(connection, cluster, process,
                "NOT EXISTS(SELECT FROM pg_stat_activity WHERE pid = " + pid + ")", requireAlive: false);
        }

        Assert.AreEqual(42, await PackageGucScalarAsync(connection, "SELECT 42"));
    }

    /// <summary>
    /// Creates a LATIN1 database and connects to it, so a worker started from it uses that encoding.
    /// </summary>
    /// <param name="administrator">A connection to the cluster's default database.</param>
    /// <param name="token">Cancels the setup.</param>
    /// <returns>An open connection to the new database.</returns>
    private async Task<NpgsqlConnection> OpenLatin1DatabaseAsync(NpgsqlConnection administrator, CancellationToken token)
    {
        string database = "worker_latin1_" + Guid.NewGuid().ToString("N");
        await ExecutePackageGucAsync(administrator,
            $"CREATE DATABASE {database} TEMPLATE template0 ENCODING 'LATIN1' LC_COLLATE 'C' LC_CTYPE 'C'");
        var builder = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Database = database, Pooling = false };
        var connection = new NpgsqlConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    /// <summary>
    /// Observes actual worker activity and fails promptly if the worker exits before its requested state.
    /// </summary>
    /// <param name="connection">The independent controlling backend.</param>
    /// <param name="cluster">The owning server and diagnostic log.</param>
    /// <param name="process">The worker process under observation.</param>
    /// <param name="condition">The fixed test predicate to observe through SQL.</param>
    /// <param name="requireAlive">Whether an early worker exit contradicts the expected state.</param>
    private async Task WaitForCancellationWorkerAsync(NpgsqlConnection connection, PostgresTestCluster cluster, int process,
        string condition, bool requireAlive = true)
    {
        var elapsed = Stopwatch.StartNew();
        while (!Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection, "SELECT " + condition)))
        {
            TimeSpan duration = elapsed.Elapsed;
            Assert.IsLessThan(TimeSpan.FromSeconds(30), duration,
                duration >= TimeSpan.FromSeconds(30) ? cluster.ReadServerLog() : null);
            if (requireAlive)
            {
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection,
                    "SELECT EXISTS(SELECT FROM pg_stat_activity WHERE pid = " + process.ToString(CultureInfo.InvariantCulture) + ")")),
                    $"{cluster.ReadServerLog()}");
            }

            await Task.Delay(25, context.CancellationToken);
        }
    }

    /// <summary>
    /// Exercises a real worker transaction cancellation without replacing PostgreSQL's SIGINT cancellation handler.
    /// </summary>
    private const string WorkerCancellationSource = """
        using Ankus;

        public static class CancellationWorker
        {
            private static readonly PgAtomic<int> Process = new("ankus_worker_cancel.process");
            private static readonly PgAtomic<int> Gate = new("ankus_worker_cancel.gate");
            private static readonly PgAtomic<int> Swallowed = new("ankus_worker_cancel.swallowed");
            private static readonly PgAtomic<int> Canceled = new("ankus_worker_cancel.canceled");
            private static readonly PgAtomic<int> Cleanup = new("ankus_worker_cancel.cleanup");
            private static readonly PgAtomic<int> Complete = new("ankus_worker_cancel.complete");
            private static readonly PgAtomic<int> Reloaded = new("ankus_worker_cancel.reloaded");
            private static readonly PgAtomic<int> Waiting = new("ankus_worker_cancel.waiting");

            [PgModuleLoad]
            public static void Load()
            {
                PgSharedMemory.Initialize(Process);
                PgSharedMemory.Initialize(Gate);
                PgSharedMemory.Initialize(Swallowed);
                PgSharedMemory.Initialize(Canceled);
                PgSharedMemory.Initialize(Cleanup);
                PgSharedMemory.Initialize(Complete);
                PgSharedMemory.Initialize(Reloaded);
                PgSharedMemory.Initialize(Waiting);
            }

            [PgFunction(Name = "cancellation_start")]
            public static int Start(int mode)
            {
                string database = Spi.ExecuteScalar<string>("SELECT current_database()::text");
                if (!PgBackgroundWorker.TryStart(new("Ankus cancellation worker", "WorkerCancellation", nameof(Run))
                    { DatabaseAccess = true, Extra = database, Argument = (nuint)mode, NotifyProcessId = Environment.ProcessId },
                    out PgBackgroundWorkerHandle? worker))
                {
                    throw new InvalidOperationException("No cancellation worker slot was available.");
                }

                using (worker)
                {
                    PgBackgroundWorkerState state = worker.WaitForStartup();
                    return state.Status == PgBackgroundWorkerStatus.Started && state.ProcessId is int process
                        ? process : throw new InvalidOperationException("The cancellation worker failed to start.");
                }
            }

            [PgFunction(Name = "cancellation_release")]
            public static void Release() => Gate.Exchange(1);

            [PgFunction(Name = "cancellation_status")]
            public static int[] Status() => [Swallowed.Value, Canceled.Value, Cleanup.Value, Complete.Value];

            [PgFunction(Name = "cancellation_pid")]
            public static int Pid() => Process.Value;

            [PgFunction(Name = "cancellation_reloaded")]
            public static int ReloadCount() => Reloaded.Value;

            [PgFunction(Name = "cancellation_waiting")]
            public static int WaitingAttempt() => Waiting.Value;

            [PgBackgroundWorker]
            public static void Run(nuint mode)
            {
                PgBackgroundWorker.Connect(PgBackgroundWorker.Extra);
                Process.Exchange(Environment.ProcessId);
                while (Gate.Value == 0 && PgBackgroundWorker.Wait(TimeSpan.FromMilliseconds(10)))
                {
                }

                if (mode == 3 || mode == 5 || mode == 7)
                {
                    if (mode == 7)
                    {
                        RunEncodedDiagnostics();
                    }

                    RunIdleCancellation(mode == 5);
                    return;
                }

                int swallowed = 0;
                int canceled = 0;
                int cleanup = 0;
                try
                {
                    PgBackgroundWorker.RunTransaction(() =>
                    {
                        Spi.Execute("INSERT INTO worker_cancel_values VALUES (1)");
                        try
                        {
                            if (mode == 2)
                            {
                                PgLog.Write(PgLogLevel.Fatal, new PgDiagnostic("owned worker transaction fatal")
                                    { SqlState = "P7861", Detail = "owned worker fatal detail" });
                            }
                            else if (mode == 4)
                            {
                                // Other signals also set the latch, so only cancellation or termination ends the wait.
                                while (PgBackgroundWorker.Wait())
                                {
                                }
                            }
                            else
                            {
                                Spi.Execute("SELECT pg_sleep(60)");
                            }
                        }
                        catch (Exception) when (mode != 0)
                        {
                            swallowed++;
                        }
                        finally
                        {
                            cleanup++;
                        }
                    });
                }
                catch (PgQueryCanceledException exception) when (exception.Diagnostic.SqlState == "57014" &&
                    exception.Diagnostic.Message == "canceling statement due to user request")
                {
                    canceled++;
                }

                PgInterrupts.Check();
                if (!PgBackgroundWorker.CanContinue || !PgBackgroundWorker.Wait(TimeSpan.Zero))
                {
                    throw new InvalidOperationException("The canceled worker lost its postmaster.");
                }

                PgLog.Write(PgLogLevel.Notice, "worker cancellation recovered");
                PgBackgroundWorker.RunTransaction(() =>
                {
                    Spi.Execute("INSERT INTO worker_cancel_values VALUES (2)");
                    if (Spi.ExecuteScalar<long>("SELECT count(*) FROM worker_cancel_values") != 1)
                    {
                        throw new InvalidOperationException("The canceled transaction committed its row.");
                    }
                });
                Swallowed.Exchange(swallowed);
                Canceled.Exchange(canceled);
                Cleanup.Exchange(cleanup);
                Complete.Exchange(1);
                while (PgBackgroundWorker.Wait())
                {
                    if ((PgBackgroundWorker.ConsumeSignals() & PgBackgroundWorkerSignals.Reload) != 0)
                    {
                        PgBackgroundWorker.ReloadConfiguration();
                        Reloaded.Exchange(1);
                    }
                }
            }

            private static void RunEncodedDiagnostics()
            {
                // Run from a LATIN1 database, the worker's reports and captured errors convert text after its
                // transaction has ended.
                PgLog.Notice("worker diagnostics café");
                try
                {
                    PgBackgroundWorker.RunTransaction(static () => _ = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit,
                        static () => throw new PgException("P7862", "worker pre-commit café")));
                }
                catch (PgException exception) when (exception.SqlState == "P7862" &&
                    exception.Message is "worker pre-commit café" or "worker pre-commit caf\\xe9")
                {
                    // PostgreSQL 13 cannot convert without raising outside a transaction, so it escapes the byte instead.
                    Swallowed.Exchange(exception.Message == "worker pre-commit café" ? 1 : 2);
                }
            }

            private static void RunIdleCancellation(bool poll)
            {
                for (int attempt = 1; attempt <= 2; attempt++)
                {
                    int canceled = 0;
                    int cleanup = 0;
                    try
                    {
                        Waiting.Exchange(attempt);
                        if (poll)
                        {
                            while (PgBackgroundWorker.CanContinue)
                            {
                                PgInterrupts.Check();
                                Thread.Sleep(1);
                            }
                        }
                        else
                        {
                            // Other signals also set the latch, so only cancellation or termination ends the wait.
                            while (PgBackgroundWorker.Wait())
                            {
                            }
                        }
                    }
                    catch (PgQueryCanceledException exception) when (exception.Diagnostic.SqlState == "57014" &&
                        exception.Diagnostic.Message == "canceling statement due to user request")
                    {
                        canceled = 1;
                    }
                    finally
                    {
                        cleanup = 1;
                    }

                    if (canceled != 1 || cleanup != 1)
                    {
                        throw new InvalidOperationException("The idle wait did not receive cancellation.");
                    }

                    PgInterrupts.Check();
                    if (!PgBackgroundWorker.CanContinue || !PgBackgroundWorker.Wait(TimeSpan.Zero))
                    {
                        throw new InvalidOperationException("The idle canceled worker lost its postmaster.");
                    }

                    PgLog.Notice("worker idle cancellation recovered");
                    int value = attempt;
                    PgBackgroundWorker.RunTransaction(() => Spi.Execute("INSERT INTO worker_cancel_values VALUES ($1)", SpiParameter.Create(value)));
                    Canceled.Exchange(attempt);
                    Cleanup.Exchange(attempt);
                    Complete.Exchange(attempt);
                }

                while (PgBackgroundWorker.Wait())
                {
                    if ((PgBackgroundWorker.ConsumeSignals() & PgBackgroundWorkerSignals.Reload) != 0)
                    {
                        PgBackgroundWorker.ReloadConfiguration();
                        Reloaded.Exchange(1);
                    }
                }
            }
        }
        """;
}
