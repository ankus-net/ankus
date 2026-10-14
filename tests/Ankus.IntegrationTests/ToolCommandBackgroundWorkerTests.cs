using System.Diagnostics;
using System.Globalization;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// The worker scaffold runs its ordinary tests plus a preloaded independent worker through the packaged fixture.
    /// </summary>
    [TestMethod]
    public async Task NewBackgroundWorkerSolutionRunsManagedAndBackendTests()
    {
        CancellationToken token = context.CancellationToken;
        string output = Path.Join(CreateDirectory(), "worker solution");
        ProcessResult created = await InvokeAsync(["new", "Acme.WorkerProbe", "--background-worker", "-o", output], token);
        created.EnsureSuccess(s_tool, ["new", "--background-worker"]);
        Assert.IsTrue(File.Exists(Path.Join(output, "src", "Acme.WorkerProbe", "Workers.cs")));
        Assert.IsFalse(File.Exists(Path.Join(output, ".editorconfig")));
        string suite = Path.Join(output, "src", "Acme.WorkerProbe", "pg_regress");
        string setup = "-- This setup file runs when the regression database is created or recreated.\n" +
            "-- Create the extension before running the ordinary SQL tests.\nCREATE EXTENSION acme_worker_probe;\n";
        Assert.AreEqual(setup, (await File.ReadAllTextAsync(Path.Join(suite, "sql", "setup.sql"), token)).ReplaceLineEndings("\n"));
        Assert.AreEqual(setup, (await File.ReadAllTextAsync(Path.Join(suite, "expected", "setup.out"), token)).ReplaceLineEndings("\n"));
        ProcessResult tests = await PackageProcessRunner.RunAsync("dotnet",
            ["test", "--report-trx", "-p:AnkusPostgresMajor=" + MajorText()], s_environment, token, workingDirectory: output);
        tests.EnsureSuccess("dotnet", ["test"]);
        string trx = Directory.GetFiles(output, "*.trx", SearchOption.AllDirectories).Single();
        XDocument report = XDocument.Load(trx);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        XElement counters = report.Descendants(ns + "Counters").Single();
        Assert.AreEqual("8", counters.Attribute("total")!.Value);
        Assert.AreEqual("8", counters.Attribute("passed")!.Value);
        Assert.AreEqual("0", counters.Attribute("failed")!.Value);
        Assert.Contains("WorkerRunsInAnotherPostgresProcess",
            report.Descendants(ns + "UnitTestResult").Select(element => element.Attribute("testName")!.Value));
        string project = Path.Join(output, "src", "Acme.WorkerProbe");
        Assert.IsEmpty(Directory.GetDirectories(Path.Join(project, "bin", "ankus-test-publish")));
        Assert.IsNotEmpty(Directory.GetFiles(Path.Join(project, "bin", "ankus-test-logs"), "*.log"));
    }

    /// <summary>
    /// The published public sample observes database metadata in another process and exposes its committed result.
    /// </summary>
    [TestMethod]
    public async Task BackgroundWorkerSamplePublishesSharedObservations()
    {
        CancellationToken token = context.CancellationToken;
        string source = await File.ReadAllTextAsync(Path.Join(IntegrationEnvironment.RepositoryRoot,
            "samples", "Ankus.Examples.BackgroundWorkers", "DatabaseObserver.cs"), token);
        string output = await PublishPackageConsumerAsync("Ankus.Examples.BackgroundWorkers", "ankus_background_workers",
            "using Ankus;\n" + source, token);
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token, sharedPreload: true);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await ExecutePackageGucAsync(connection, "CREATE EXTENSION ankus_background_workers");
        long previous = Assert.IsInstanceOfType<long>(await PackageGucScalarAsync(connection, "SELECT completed_observations()"));
        long before = Assert.IsInstanceOfType<long>(await PackageGucScalarAsync(connection, "SELECT count(*) FROM pg_database"));
        await ExecutePackageGucAsync(connection, "CREATE DATABASE worker_observer_extra");
        long expected = checked(before + 1);
        Assert.AreEqual(expected, await PackageGucScalarAsync(connection, "SELECT count(*) FROM pg_database"));
        Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection, "SELECT pg_reload_conf()")));
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (Assert.IsInstanceOfType<long>(await PackageGucScalarAsync(connection, "SELECT completed_observations()")) <= previous ||
            Assert.IsInstanceOfType<long>(await PackageGucScalarAsync(connection, "SELECT observed_databases()")) != expected)
        {
            TimeSpan duration = elapsed.Elapsed;
            Assert.IsLessThan(TimeSpan.FromSeconds(30), duration,
                duration >= TimeSpan.FromSeconds(30) ? cluster.ReadServerLog() : null);
            await Task.Delay(25, token);
        }

        int process = Assert.IsInstanceOfType<int>(await PackageGucScalarAsync(connection, "SELECT observer_process()"));
        Assert.IsGreaterThan(0, process);
        Assert.AreNotEqual(connection.ProcessID, process);
        Assert.AreEqual(expected, await PackageGucScalarAsync(connection, "SELECT observed_databases()"));
        Assert.Contains("Database observer 42 started.", cluster.ReadServerLog());
    }

    /// <summary>
    /// Published workers attach shared storage, execute recoverable transactions, commit one after termination as pgrx's
    /// worker does, and preserve native process lifecycle semantics.
    /// </summary>
    [TestMethod]
    public async Task BackgroundWorkersRegisterAndShareState()
    {
        CancellationToken token = context.CancellationToken;
        string output = await PublishPackageConsumerAsync("BackgroundWorkers", "ankus_worker_probe", BackgroundWorkerSource, token);
        string signalFixture = Path.Join(output, "Ankus.WorkerSignals" + Path.GetExtension(PublishedExtension.Read(output).Library));
        await AllocatorFixtureCompiler.CompileModuleAsync(s_installation,
            Path.Join(IntegrationEnvironment.RepositoryRoot, "tests", "Ankus.IntegrationTests", "Native", "worker_signal_fixture.c"),
            signalFixture, true, token);
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token, sharedPreload: true,
            additionalConfiguration: ["max_worker_processes = 4", "max_parallel_workers = 0", "max_logical_replication_workers = 0", "log_error_verbosity = verbose"]);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await ExecutePackageGucAsync(connection, "CREATE EXTENSION ankus_worker_probe; CREATE TABLE worker_values (value integer PRIMARY KEY DEFERRABLE INITIALLY DEFERRED)");
        await ExecutePackageGucAsync(connection, """
            CREATE FUNCTION worker_send_child(integer) RETURNS void
            AS 'Ankus.WorkerSignals', 'ankus_test_worker_child_signal' LANGUAGE c STRICT;
            """);
        int staticPid = Assert.IsInstanceOfType<int>(await PackageGucScalarAsync(connection, "SELECT worker_static_pid()"));
        Assert.IsGreaterThan(0, staticPid);
        Assert.AreNotEqual(connection.ProcessID, staticPid);
        Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection, "SELECT worker_preload_guard()")));
        foreach (int mode in new[] { 1, 2 })
        {
            await ExecutePackageGucAsync(connection, "TRUNCATE worker_values");
            string result = Assert.IsInstanceOfType<string>(await PackageGucScalarAsync(connection, $"SELECT worker_round_trip({mode})"));
            string[] fields = result.Split('|');
            Assert.HasCount(6, fields);
            int workerPid = int.Parse(fields[0], CultureInfo.InvariantCulture);
            Assert.IsGreaterThan(0, workerPid);
            Assert.AreNotEqual(connection.ProcessID, workerPid);
            Assert.AreNotEqual(staticPid, workerPid);
            Assert.AreEqual("2047", fields[1]);
            Assert.AreEqual("28", fields[2]);
            Assert.AreEqual("1", fields[3]);
            Assert.AreEqual("2", fields[5]);
            ulong argument = ulong.Parse(fields[4], CultureInfo.InvariantCulture);
            if (mode == 1)
            {
                Assert.AreEqual(ulong.MaxValue, argument);
            }
            else
            {
                Assert.AreEqual(Convert.ToUInt64(await PackageGucScalarAsync(connection,
                    "SELECT oid::bigint FROM pg_database WHERE datname = current_database()"), CultureInfo.InvariantCulture), argument);
            }

            Assert.AreEqual(2L, await PackageGucScalarAsync(connection, "SELECT count(*) FROM worker_values"));
            Assert.AreEqual(29L, await PackageGucScalarAsync(connection, "SELECT sum(value) FROM worker_values"));
            Assert.AreEqual(42, await PackageGucScalarAsync(connection, "SELECT 42"));
        }

        Assert.AreEqual(3, await PackageGucScalarAsync(connection, "SELECT worker_slots()"));
        Assert.AreEqual(3, await PackageGucScalarAsync(connection, "SELECT worker_slots()"));
        Assert.AreEqual("2|Stopped", await PackageGucScalarAsync(connection, "SELECT worker_restart()"));
        Assert.AreEqual("Stopped", await PackageGucScalarAsync(connection, "SELECT worker_failure()"));
        Assert.Contains("owned worker startup failure", cluster.ReadServerLog());
        Assert.Contains("owned worker detail", cluster.ReadServerLog());
        Assert.Contains("worker restart requested", cluster.ReadServerLog());
        PostgresException late = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            PackageGucScalarAsync(connection, "SELECT worker_register_late()"));
        Assert.AreEqual("55000", late.SqlState);
        Assert.Contains("shared preload", late.MessageText);
        Assert.AreEqual(42, await PackageGucScalarAsync(connection, "SELECT 42"));
        Assert.AreEqual(staticPid, await PackageGucScalarAsync(connection, "SELECT worker_static_pid()"));
        foreach ((int field, string text) in new (int, string)[]
        {
            (0, new string('a', 96)), (1, new string('a', 96)), (2, new string('a', 1024)),
            (3, new string('a', 96)), (4, new string('a', 128)),
        })
        {
            await using var command = new NpgsqlCommand("SELECT worker_text_boundary($1, $2)", connection);
            command.Parameters.AddWithValue(field);
            command.Parameters.AddWithValue(text);
            PostgresException size = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
            Assert.AreEqual("22023", size.SqlState);
            Assert.Contains("without truncation", size.MessageText);
            Assert.AreEqual(42, await PackageGucScalarAsync(connection, "SELECT 42"));
        }

        foreach ((int field, string text) in new (int, string)[]
        {
            (0, new string('a', 95)), (1, new string('a', 95)), (4, new string('a', 127)),
            (4, new string('π', 63) + "a"),
        })
        {
            await using var command = new NpgsqlCommand("SELECT worker_text_boundary($1, $2)", connection);
            command.Parameters.AddWithValue(field);
            command.Parameters.AddWithValue(text);
            Assert.AreEqual("Started|Stopped", await command.ExecuteScalarAsync(token));
        }

        Assert.AreEqual("Untracked|Untracked|Started|Untracked|Stopped", await PackageGucScalarAsync(connection, "SELECT worker_untracked()"));
        PostgresException lossy = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            PackageGucScalarAsync(connection, "SELECT worker_text_boundary(0, 'worker π')"));
        Assert.AreEqual("38000", lossy.SqlState);
        Assert.Contains("unchanged", lossy.MessageText);
        Assert.AreEqual(42, await PackageGucScalarAsync(connection, "SELECT 42"));
        Assert.AreEqual(3, await PackageGucScalarAsync(connection, "SELECT worker_slots()"));
        foreach (bool dispose in new[] { false, true })
        {
            int process = Assert.IsInstanceOfType<int>(await PackageGucScalarAsync(connection,
                dispose ? "SELECT worker_detach(true)" : "SELECT worker_detach(false)"));
            Assert.IsGreaterThan(0, process);
            Assert.AreNotEqual(connection.ProcessID, process);
            Assert.AreEqual(process, await PackageGucScalarAsync(connection, "SELECT worker_detached_liveness()"));
            Assert.AreEqual(1, await PackageGucScalarAsync(connection, "SELECT worker_detached_stop()"));
        }

        // The final shared value precedes process exit and the postmaster reclaiming its worker slot.
        var elapsed = Stopwatch.StartNew();
        int available;
        do
        {
            available = Assert.IsInstanceOfType<int>(await PackageGucScalarAsync(connection, "SELECT worker_slots()"));
            if (available == 3)
            {
                break;
            }

            await Task.Delay(25, token);
        }
        while (elapsed.Elapsed < TimeSpan.FromSeconds(30));

        Assert.AreEqual(3, available);
    }

    /// <summary>
    /// Exercises public worker declarations and APIs through an independently published SDK consumer.
    /// </summary>
    private const string BackgroundWorkerSource = """
        using System.Diagnostics;
        using System.Globalization;
        using Ankus;
        using Ankus.Postgres;

        public static class Workers
        {
            private static bool s_dynamicPreloadRejected;
            private static readonly PgAtomic<int> StaticPid = new("ankus_worker.static_pid");
            private static readonly PgAtomic<int> Ready = new("ankus_worker.ready");
            private static readonly PgAtomic<int> Stopped = new("ankus_worker.stopped");
            private static readonly PgAtomic<int> Errors = new("ankus_worker.errors");
            private static readonly PgAtomic<long> Sum = new("ankus_worker.sum");
            private static readonly PgAtomic<ulong> Argument = new("ankus_worker.argument");
            private static readonly PgAtomic<int> Restarts = new("ankus_worker.restarts");
            private static readonly PgAtomic<int> DetachedStop = new("ankus_worker.detached_stop");
            private static readonly PgAtomic<long> Heartbeat = new("ankus_worker.heartbeat");
            private static readonly PgAtomic<int> ChildSignals = new("ankus_worker.children");

            [PgModuleLoad]
            public static void Load()
            {
                PgSharedMemory.Initialize(StaticPid);
                PgSharedMemory.Initialize(Ready);
                PgSharedMemory.Initialize(Stopped);
                PgSharedMemory.Initialize(Errors);
                PgSharedMemory.Initialize(Sum);
                PgSharedMemory.Initialize(Argument);
                PgSharedMemory.Initialize(Restarts);
                PgSharedMemory.Initialize(DetachedStop);
                PgSharedMemory.Initialize(Heartbeat);
                PgSharedMemory.Initialize(ChildSignals);
                try
                {
                    PgBackgroundWorker.TryStart(new("early dynamic worker", "BackgroundWorkers", nameof(SlotWorker)), out _);
                    throw new InvalidOperationException("Dynamic registration was accepted during shared preload.");
                }
                catch (PgException exception) when (exception.SqlState == "55000")
                {
                    s_dynamicPreloadRejected = true;
                }

                PgBackgroundWorker.Register(new("Ankus static worker", "BackgroundWorkers", nameof(StaticWorker))
                {
                    StartTime = PgBackgroundWorkerStartTime.RecoveryFinished,
                    Argument = 73,
                });
            }

            [PgBackgroundWorker]
            public static void StaticWorker(nuint argument)
            {
                if (argument != 73 || PgBackgroundWorker.Name != "Ankus static worker")
                {
                    throw new InvalidOperationException("Static worker identity changed.");
                }

                StaticPid.Exchange(Environment.ProcessId);
                while (PgBackgroundWorker.Wait())
                {
                    if ((PgBackgroundWorker.ConsumeSignals() & PgBackgroundWorkerSignals.Reload) != 0)
                    {
                        PgBackgroundWorker.ReloadConfiguration();
                    }
                }
            }

            [PgBackgroundWorker]
            public static void ReportWorker(nuint argument)
            {
                unsafe
                {
                    try
                    {
                        if (PgBackgroundWorker.Name != "Ankus worker" || PgBackgroundWorker.Type != "Ankus reporting" ||
                            !PgBackgroundWorker.Extra.StartsWith("payload café 🐘|", StringComparison.Ordinal))
                        {
                            throw new InvalidOperationException("Dynamic worker identity changed.");
                        }

                        Argument.Exchange((ulong)argument);
                        string database = PgBackgroundWorker.Extra.Split('|')[1];
                        int errors = 0;
                        try
                        {
                            Spi.Execute("SELECT 42");
                        }
                        catch (InvalidOperationException)
                        {
                            errors |= 8;
                        }

                        if (argument == nuint.MaxValue)
                        {
                            PgBackgroundWorker.Connect(database);
                        }
                        else
                        {
                            PgBackgroundWorker.Connect((uint)argument);
                        }

                        PgBackgroundWorker.AttachSignalHandlers(PgBackgroundWorkerSignals.Interrupt | PgBackgroundWorkerSignals.Child);
                        try
                        {
                            PgBackgroundWorker.Connect(database);
                        }
                        catch (PgException exception) when (exception.SqlState == "55000")
                        {
                            errors |= 16;
                        }

                        PgBackgroundWorker.RunTransaction(() =>
                        {
                            if (Spi.ExecuteScalar<string>("SELECT current_database()::text") != database)
                            {
                                throw new InvalidOperationException("Worker selected another database.");
                            }

                            Spi.Execute("INSERT INTO worker_values VALUES (11)");
                        });
                        var managed = new FormatException("worker transaction failure");
                        try
                        {
                            PgBackgroundWorker.RunTransaction(() =>
                            {
                                Spi.Execute("INSERT INTO worker_values VALUES (13)");
                                throw managed;
                            });
                        }
                        catch (FormatException exception) when (ReferenceEquals(exception, managed))
                        {
                            errors |= 1;
                        }

                        try
                        {
                            PgBackgroundWorker.RunTransaction(() => Spi.Execute("SELECT 1 / 0"));
                        }
                        catch (PgException exception) when (exception.SqlState == "22012")
                        {
                            errors |= 2;
                        }

                        bool olderParallel = PgBackgroundWorker.RunTransaction(() =>
                            Spi.ExecuteScalar<int>("SELECT current_setting('server_version_num')::integer") < 170000);
                        try
                        {
                            PgBackgroundWorker.RunTransaction(() =>
                            {
                                NativeMethods.EnterParallelMode();
                                try
                                {
                                    Spi.Query("SELECT 1 / 0", readOnly: true, limit: 1);
                                }
                                catch (PgException exception) when (exception.SqlState == "22012")
                                {
                                    if (!olderParallel)
                                    {
                                        NativeMethods.ExitParallelMode();
                                    }
                                }
                            });
                            if (!olderParallel)
                            {
                                errors |= 256;
                            }
                        }
                        catch (PgException exception) when (olderParallel && exception.SqlState == "22012")
                        {
                            errors |= 256;
                        }

                        try
                        {
                            PgBackgroundWorker.RunTransaction(() => Spi.Execute("INSERT INTO worker_values VALUES (11)"));
                        }
                        catch (PgException exception) when (exception.SqlState == "23505")
                        {
                            errors |= 4;
                        }

                        long sum = PgBackgroundWorker.RunTransaction(() =>
                        {
                            if (NativeMethods.IsInParallelMode())
                            {
                                throw new InvalidOperationException("Worker transaction retained a failed parallel scope.");
                            }

                            Spi.Execute("INSERT INTO worker_values VALUES (17)");
                            return Spi.ExecuteScalar<long>("SELECT sum(value) FROM worker_values");
                        });
                        Errors.Exchange(errors);
                        Sum.Exchange(sum);
                        if (!PgBackgroundWorker.CanContinue)
                        {
                            throw new InvalidOperationException("Worker lost its live postmaster before reporting readiness.");
                        }

                        Ready.Exchange(Environment.ProcessId);
                        while (PgBackgroundWorker.Wait())
                        {
                            PgBackgroundWorkerSignals signals = PgBackgroundWorker.ConsumeSignals(
                                PgBackgroundWorkerSignals.Reload | PgBackgroundWorkerSignals.Interrupt);
                            if ((signals & PgBackgroundWorkerSignals.Reload) != 0)
                            {
                                if (NativeGlobals.ConfigReloadPending == 0)
                                {
                                    throw new InvalidOperationException("PostgreSQL did not observe the reload signal.");
                                }

                                PgBackgroundWorker.ReloadConfiguration();
                                if (NativeGlobals.ConfigReloadPending != 0)
                                {
                                    throw new InvalidOperationException("Completed configuration reload remained pending.");
                                }

                                errors |= 32;
                            }

                            if ((signals & PgBackgroundWorkerSignals.Interrupt) != 0)
                            {
                                errors |= 64;
                            }

                            if (PgBackgroundWorker.ConsumeSignals(PgBackgroundWorkerSignals.Child) != PgBackgroundWorkerSignals.None)
                            {
                                if (PgBackgroundWorker.ConsumeSignals(PgBackgroundWorkerSignals.Child) != PgBackgroundWorkerSignals.None)
                                {
                                    throw new InvalidOperationException("Child signal remained set after consumption.");
                                }

                                errors |= 128;
                                ChildSignals.Add(1);
                            }

                            Errors.Exchange(errors);
                        }

                        if (NativeGlobals.ShutdownRequestPending == 0 || PgBackgroundWorker.CanContinue ||
                            PgBackgroundWorker.ConsumeSignals(PgBackgroundWorkerSignals.Terminate) != PgBackgroundWorkerSignals.None ||
                            PgBackgroundWorker.Wait(TimeSpan.Zero))
                        {
                            throw new InvalidOperationException("Consuming a termination observation must not undo the shutdown request.");
                        }

                        Errors.Exchange(errors | 512);
                        PgBackgroundWorker.RunTransaction(() => Spi.Execute("UPDATE worker_values SET value = value + 1 WHERE value = 17"));
                        Errors.Exchange(errors | 512 | 1024);
                    }
                    finally
                    {
                        Stopped.Exchange(1);
                    }
                }
            }

            [PgBackgroundWorker]
            public static void SlotWorker(nuint argument)
            {
                while (PgBackgroundWorker.Wait())
                {
                }
            }

            [PgBackgroundWorker]
            public static void RestartWorker(nuint argument)
            {
                if (Restarts.Add(1) == 1)
                {
                    throw new PgException("P7851", "worker restart requested");
                }

                Ready.Exchange(Environment.ProcessId);
                while (PgBackgroundWorker.Wait())
                {
                }
            }

            [PgBackgroundWorker]
            public static void FailWorker(nuint argument)
                => throw new PgException("P7852", "owned worker startup failure", detail: "owned worker detail");

            [PgFunction]
            public static bool WorkerPreloadGuard() => s_dynamicPreloadRejected;

            [PgFunction]
            public static int WorkerStaticPid()
            {
                WaitUntil(() => StaticPid.Value != 0);
                return StaticPid.Value;
            }

            [PgFunction]
            public static string WorkerRoundTrip(int mode)
            {
                Ready.Exchange(0);
                Stopped.Exchange(0);
                Errors.Exchange(0);
                ChildSignals.Exchange(0);
                string database = Spi.ExecuteScalar<string>("SELECT current_database()::text");
                nuint argument = mode == 1 ? nuint.MaxValue : Spi.ExecuteScalar<uint>("SELECT oid FROM pg_database WHERE datname = current_database()");
                var options = new PgBackgroundWorkerOptions("Ankus worker", "BackgroundWorkers", nameof(ReportWorker))
                {
                    Type = "Ankus reporting", Extra = "payload café 🐘|" + database, DatabaseAccess = true,
                    Argument = argument, NotifyProcessId = Environment.ProcessId,
                };
                if (!PgBackgroundWorker.TryStart(options, out PgBackgroundWorkerHandle? worker))
                {
                    throw new InvalidOperationException("No worker slot was available.");
                }

                using (worker)
                {
                    try
                    {
                        PgBackgroundWorkerState started = worker.WaitForStartup();
                        if (started.Status != PgBackgroundWorkerStatus.Started || started.ProcessId is not int pid)
                        {
                            throw new InvalidOperationException("Worker failed to start: " + started.Status);
                        }

                        WaitUntil(() =>
                        {
                            if (worker.GetState().Status == PgBackgroundWorkerStatus.Stopped)
                            {
                                throw new InvalidOperationException("Worker stopped before publishing readiness; inspect its server diagnostic.");
                            }

                            return Ready.Value == pid;
                        });
                        if (worker.GetState() != started)
                        {
                            throw new InvalidOperationException("Worker state changed unexpectedly.");
                        }

                        if (!Spi.ExecuteScalar<bool>(Spi.Sql($"SELECT pg_cancel_backend({pid})")))
                        {
                            throw new InvalidOperationException("Worker interrupt could not be sent.");
                        }

                        WaitUntil(() => (Errors.Value & 64) != 0);
                        if (!Spi.ExecuteScalar<bool>("SELECT pg_reload_conf()"))
                        {
                            throw new InvalidOperationException("Configuration reload could not be sent.");
                        }

                        WaitUntil(() => (Errors.Value & 32) != 0);

                        for (int child = 1; child <= 2; child++)
                        {
                            Spi.Execute(Spi.Sql($"SELECT worker_send_child({pid})"));
                            WaitUntil(() => ChildSignals.Value == child);
                        }

                        worker.Terminate();
                        if (worker.WaitForShutdown() != PgBackgroundWorkerStatus.Stopped)
                        {
                            throw new InvalidOperationException("Worker did not stop.");
                        }

                        return string.Create(CultureInfo.InvariantCulture,
                            $"{pid}|{Errors.Value}|{Sum.Value}|{Stopped.Value}|{Argument.Value}|{ChildSignals.Value}");
                    }
                    finally
                    {
                        worker.Terminate();
                        worker.WaitForShutdown();
                    }
                }
            }

            [PgFunction]
            public static int WorkerSlots()
            {
                var workers = new List<PgBackgroundWorkerHandle>();
                try
                {
                    for (int index = 0; index < 8; index++)
                    {
                        if (!PgBackgroundWorker.TryStart(new("slot worker", "BackgroundWorkers", nameof(SlotWorker))
                            { NotifyProcessId = Environment.ProcessId }, out PgBackgroundWorkerHandle? worker))
                        {
                            return workers.Count;
                        }

                        workers.Add(worker);
                        if (worker.WaitForStartup().Status != PgBackgroundWorkerStatus.Started)
                        {
                            throw new InvalidOperationException("Slot worker failed to start.");
                        }
                    }

                    throw new InvalidOperationException("Expected the configured worker slot limit.");
                }
                finally
                {
                    foreach (PgBackgroundWorkerHandle worker in workers)
                    {
                        worker.Terminate();
                    }

                    foreach (PgBackgroundWorkerHandle worker in workers)
                    {
                        worker.WaitForShutdown();
                        worker.Dispose();
                    }
                }
            }

            [PgFunction]
            public static string WorkerRestart()
            {
                Ready.Exchange(0);
                Restarts.Exchange(0);
                if (!PgBackgroundWorker.TryStart(new("restarting worker", "BackgroundWorkers", nameof(RestartWorker))
                    { NotifyProcessId = Environment.ProcessId, RestartDelay = TimeSpan.FromSeconds(1) }, out PgBackgroundWorkerHandle? worker))
                {
                    throw new InvalidOperationException("No restart worker slot.");
                }

                using (worker)
                {
                    try
                    {
                        // The worker publishes its PID as soon as it runs, but the postmaster records
                        // the registration's PID only after its fork returns.
                        WaitUntil(() => Ready.Value != 0 && worker.GetState().ProcessId != 0);
                        if (worker.GetState().ProcessId != Ready.Value)
                        {
                            throw new InvalidOperationException("Restarted PID did not match the registration.");
                        }

                        worker.Terminate();
                        return Restarts.Value.ToString(CultureInfo.InvariantCulture) + "|" + worker.WaitForShutdown();
                    }
                    finally
                    {
                        worker.Terminate();
                        worker.WaitForShutdown();
                    }
                }
            }

            [PgFunction]
            public static string WorkerFailure()
            {
                if (!PgBackgroundWorker.TryStart(new("failing worker", "BackgroundWorkers", nameof(FailWorker))
                    { NotifyProcessId = Environment.ProcessId }, out PgBackgroundWorkerHandle? worker))
                {
                    throw new InvalidOperationException("No failing worker slot.");
                }

                using (worker)
                {
                    return worker.WaitForShutdown().ToString();
                }
            }

            [PgFunction]
            public static void WorkerRegisterLate()
                => PgBackgroundWorker.Register(new("late worker", "BackgroundWorkers", nameof(SlotWorker)));

            [PgFunction]
            public static string WorkerTextBoundary(int field, string text)
            {
                var options = new PgBackgroundWorkerOptions(field == 0 ? text : "boundary worker",
                    field == 2 ? text : "BackgroundWorkers", field == 3 ? text : nameof(SlotWorker))
                {
                    Type = field == 1 ? text : "boundary type",
                    Extra = field == 4 ? text : string.Empty,
                    NotifyProcessId = Environment.ProcessId,
                };
                if (!PgBackgroundWorker.TryStart(options, out PgBackgroundWorkerHandle? worker))
                {
                    throw new InvalidOperationException("No boundary worker slot.");
                }

                using (worker)
                {
                    try
                    {
                        PgBackgroundWorkerStatus started = worker.WaitForStartup().Status;
                        worker.Terminate();
                        return started + "|" + worker.WaitForShutdown();
                    }
                    finally
                    {
                        worker.Terminate();
                        worker.WaitForShutdown();
                    }
                }
            }

            [PgFunction]
            public static string WorkerUntracked()
            {
                if (!PgBackgroundWorker.TryStart(new("untracked worker", "BackgroundWorkers", nameof(SlotWorker)),
                    out PgBackgroundWorkerHandle? worker))
                {
                    throw new InvalidOperationException("No untracked worker slot.");
                }

                using (worker)
                {
                    try
                    {
                        PgBackgroundWorkerState startup = worker.WaitForStartup();
                        PgBackgroundWorkerStatus shutdown = worker.WaitForShutdown();
                        WaitUntil(() => worker.GetState().Status == PgBackgroundWorkerStatus.Started);
                        PgBackgroundWorkerStatus started = worker.GetState().Status;
                        worker.Terminate();
                        PgBackgroundWorkerStatus terminated = worker.WaitForShutdown();
                        WaitUntil(() => worker.GetState().Status == PgBackgroundWorkerStatus.Stopped);
                        return startup.Status + "|" + shutdown + "|" + started + "|" + terminated + "|" + worker.GetState().Status;
                    }
                    finally
                    {
                        worker.Terminate();
                        WaitUntil(() => worker.GetState().Status == PgBackgroundWorkerStatus.Stopped);
                    }
                }
            }

            [PgBackgroundWorker]
            public static void DetachedWorker(nuint argument)
            {
                Ready.Exchange(Environment.ProcessId);
                while (PgBackgroundWorker.Wait(TimeSpan.FromMilliseconds(10)) && DetachedStop.Value == 0)
                {
                    Heartbeat.Add(1);
                }

                Stopped.Exchange(1);
            }

            [PgFunction]
            public static int WorkerDetach(bool dispose)
            {
                Ready.Exchange(0);
                Stopped.Exchange(0);
                DetachedStop.Exchange(0);
                Heartbeat.Exchange(0);
                if (!PgBackgroundWorker.TryStart(new("detached worker", "BackgroundWorkers", nameof(DetachedWorker))
                    { NotifyProcessId = Environment.ProcessId }, out PgBackgroundWorkerHandle? worker))
                {
                    throw new InvalidOperationException("No detached worker slot.");
                }

                PgBackgroundWorkerState startup = worker.WaitForStartup();
                if (startup.Status != PgBackgroundWorkerStatus.Started)
                {
                    throw new InvalidOperationException("Detached worker did not start.");
                }

                WaitUntil(() => Ready.Value == startup.ProcessId);
                if (dispose)
                {
                    worker.Dispose();
                }

                return Ready.Value;
            }

            [PgFunction]
            public static int WorkerDetachedLiveness()
            {
                long previous = Heartbeat.Value;
                WaitUntil(() => Heartbeat.Value > previous);
                return Ready.Value;
            }

            [PgFunction]
            public static int WorkerDetachedStop()
            {
                DetachedStop.Exchange(1);
                WaitUntil(() => Stopped.Value == 1);
                return Stopped.Value;
            }

            private static void WaitUntil(Func<bool> ready)
            {
                var elapsed = Stopwatch.StartNew();
                while (!ready())
                {
                    if (elapsed.Elapsed > TimeSpan.FromSeconds(30))
                    {
                        throw new TimeoutException("The background worker did not reach its expected state.");
                    }

                    Spi.Execute("SELECT pg_sleep(0.01)");
                }
            }
        }
        """;
}
