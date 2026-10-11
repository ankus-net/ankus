using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies native cleanup without interrupting PostgreSQL exception-subtransaction recovery.
/// </summary>
/// <param name="context">The current test cancellation context.</param>
[TestClass]
public sealed partial class CallbackSubtransactionCleanupTests(TestContext context)
{
    private const string Resources = """
        SELECT ARRAY[
            (SELECT count(*) FROM ankus_test_memory.contexts WHERE name = 'SPI Plan'),
            (SELECT count(*) FROM pg_cursors)]
        """;

    /// <summary>
    /// A caller that rolls back while its error is still pending, as logical replication's apply worker does, re-throws
    /// that error after an abort callback catches a backend error of its own: a LATIN1 client cannot receive the
    /// callback's warning.
    /// </summary>
    [TestMethod]
    public async Task CaughtCallbackErrorKeepsCallersPendingError()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(
            "SET client_encoding=LATIN1; BEGIN; SELECT datatype.transaction_callback_register_caught_abort_error()", connection);
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT tests.pending_error_cleanup('SELECT 1')";
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteNonQueryAsync(token));
        Assert.AreEqual("P0001", error.SqlState);
        Assert.AreEqual("pending failure survives cleanup", error.MessageText);
        command.CommandText = "ROLLBACK";
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT datatype.transaction_callback_caught_abort_error()";
        Assert.AreEqual("22P05", await command.ExecuteScalarAsync(token));
    }

    /// <summary>
    /// Iterator failures retain their primary diagnostics and release ownership before the exception handler resumes.
    /// </summary>
    /// <param name="openCursor">Whether the iterator retains a cursor alongside its prepared plan.</param>
    /// <param name="executorFailure">Whether division fails before iterator cleanup.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public Task IteratorCleanupPreservesExceptionSubtransactionRecovery(bool openCursor, bool executorFailure)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(IteratorCleanupPreservesExceptionSubtransactionRecovery),
            async (connection, transaction, token) =>
            {
                int backend = connection.ProcessID;
                var notices = new List<PostgresNotice>();
                connection.Notice += (_, args) => notices.Add(args.Notice);
                await using var command = new NpgsqlCommand(string.Empty, connection, transaction);
                long[] baseline = await PrepareCaptureAsync(command, token);
                command.CommandText = "SELECT set_values.set_reset()";
                await command.ExecuteNonQueryAsync(token);
                string source = $"set_values.set_owned_resources_with_cleanup_error({openCursor})";
                string query = executorFailure
                    ? $"SELECT 1/(CASE WHEN value='Low'::datatype.enum_mood THEN 0 ELSE 1 END) FROM (SELECT {source} AS value) input"
                    : $"SELECT value::text FROM {source} value";
                for (int index = 0; index < 10; index++)
                {
                    int previousLogLength = PostgresFixture.Cluster.ReadServerLog().Length;
                    string[] error = await CaptureAsync(command, query, token);
                    Assert.AreSequenceEqual(executorFailure
                        ? ["22012", "division by zero", string.Empty, string.Empty]
                        : ["42704", "ExtensibleNodeMethods \"ankus_missing_iterator_cleanup\" was not registered", string.Empty, string.Empty], error);
                    command.CommandText = "SELECT set_values.set_status()";
                    int[] status = Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token));
                    Assert.HasCount(13, status);
                    Assert.AreEqual(index + 1, status[12]);
                    Assert.AreEqual(executorFailure ? 0 : index + 1, status[8]);
                    Assert.AreEqual(executorFailure ? index + 1 : 0, status[9]);
                    Assert.AreEqual(0, status[10]);
                    Assert.AreEqual(0, status[11]);
                    command.CommandText = "SELECT set_values.set_native_cleanup_caught()";
                    Assert.AreEqual(index + 1, await command.ExecuteScalarAsync(token));
                    AssertNativeCleanupWarning(PostgresFixture.Cluster, previousLogLength, backend, executorFailure,
                        "ExtensibleNodeMethods \"ankus_missing_iterator_cleanup\" was not registered");
                    await AssertRecoveredAsync(command, baseline, backend, token);
                }

                command.CommandText = $"SELECT value::text FROM set_values.set_owned_resources({openCursor}) value";
                Assert.AreEqual("Low", await command.ExecuteScalarAsync(token));
                command.CommandText = "SELECT set_values.set_status()";
                Assert.AreEqual(11, Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token))[12]);
                await AssertRecoveredAsync(command, baseline, backend, token);
                AssertNoInterruptedAbort(notices);
            }, context.CancellationToken);

    /// <summary>
    /// Aggregate transition errors remain catchable after exact state and SPI-resource cleanup.
    /// </summary>
    /// <param name="transitionFailure">Whether transition fails before aggregate state cleanup.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task AggregateCleanupPreservesExceptionSubtransactionRecovery(bool transitionFailure)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(AggregateCleanupPreservesExceptionSubtransactionRecovery),
            async (connection, transaction, token) =>
            {
                int backend = connection.ProcessID;
                var notices = new List<PostgresNotice>();
                connection.Notice += (_, args) => notices.Add(args.Notice);
                await using var command = new NpgsqlCommand(string.Empty, connection, transaction);
                long[] baseline = await PrepareCaptureAsync(command, token);
                string mode = transitionFailure ? "resources_native_transition" : "resources_native";
                for (int index = 0; index < 10; index++)
                {
                    int previousLogLength = PostgresFixture.Cluster.ReadServerLog().Length;
                    command.CommandText = $"SELECT aggregate_values.aggregate_reset('{mode}')";
                    await command.ExecuteNonQueryAsync(token);
                    string[] error = await CaptureAsync(command,
                        "SELECT aggregate_values.managed_sum(v ORDER BY v) FROM (VALUES(1),(2),(3)) AS input(v)", token);
                    Assert.AreSequenceEqual(transitionFailure
                        ? ["P7801", "aggregate transition failed", "owned aggregate detail", "retry valid inputs"]
                        : ["42704", "ExtensibleNodeMethods \"ankus_missing_aggregate_cleanup\" was not registered", string.Empty, string.Empty], error);
                    command.CommandText = "SELECT aggregate_values.aggregate_status()";
                    int[] status = Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token));
                    Assert.AreSequenceEqual([1, 1, 0, 0], status[..4]);
                    Assert.AreEqual(transitionFailure ? 2 : 3, status[4]);
                    command.CommandText = "SELECT aggregate_values.aggregate_trace()";
                    Assert.AreSequenceEqual(["42704"], Assert.IsInstanceOfType<string[]>(await command.ExecuteScalarAsync(token)));
                    AssertNativeCleanupWarning(PostgresFixture.Cluster, previousLogLength, backend, transitionFailure,
                        "ExtensibleNodeMethods \"ankus_missing_aggregate_cleanup\" was not registered");
                    await AssertRecoveredAsync(command, baseline, backend, token);
                }

                command.CommandText = "SELECT aggregate_values.aggregate_reset('normal')";
                await command.ExecuteNonQueryAsync(token);
                command.CommandText = "SELECT aggregate_values.managed_sum(v) FROM (VALUES(42)) AS input(v)";
                Assert.AreEqual(42L, await command.ExecuteScalarAsync(token));
                command.CommandText = "SELECT aggregate_values.aggregate_status()";
                Assert.AreSequenceEqual([1, 1, 0, 0], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token))[..4]);
                await AssertRecoveredAsync(command, baseline, backend, token);
                AssertNoInterruptedAbort(notices);
            }, context.CancellationToken);

    /// <summary>
    /// A secondary memory callback failure remains visible while the original SQL diagnostic and native drain survive.
    /// </summary>
    /// <param name="cleanupThrows">Whether the newer memory callback reports its own managed error.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task MemoryCleanupPreservesPrimaryErrorAndRemainingCallbacks(bool cleanupThrows)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(MemoryCleanupPreservesPrimaryErrorAndRemainingCallbacks),
            async (connection, transaction, token) =>
            {
                int backend = connection.ProcessID;
                var notices = new List<PostgresNotice>();
                connection.Notice += (_, args) => notices.Add(args.Notice);
                await using var command = new NpgsqlCommand(string.Empty, connection, transaction);
                long[] baseline = await PrepareCaptureAsync(command, token);
                string query = $"""
                    SELECT datatype.memory_callback_prepare(2, {cleanupThrows});
                    DO $failure$ BEGIN
                        RAISE EXCEPTION 'primary callback failure' USING
                            ERRCODE = '23514', DETAIL = 'primary detail', HINT = 'primary hint';
                    END $failure$
                    """;
                for (int index = 0; index < 10; index++)
                {
                    int previousLogLength = PostgresFixture.Cluster.ReadServerLog().Length;
                    Assert.AreSequenceEqual(["23514", "primary callback failure", "primary detail", "primary hint"],
                        await CaptureAsync(command, query, token));
                    command.CommandText = "SELECT datatype.memory_callback_implicit_state()";
                    Assert.AreEqual("B73,A73|False,False|False|stale", await command.ExecuteScalarAsync(token));
                    command.CommandText = "SELECT count(*) FROM ankus_test_memory.contexts WHERE ident = 'implicit memory callback'";
                    Assert.AreEqual(0L, await command.ExecuteScalarAsync(token));
                    if (cleanupThrows)
                    {
                        string cleanup = CompletionLog(PostgresFixture.Cluster, previousLogLength, backend);
                        Assert.Contains("WARNING: 22023: callback caf\\xC3\\xA9", cleanup);
                        Assert.Contains("DETAIL: detail na\\xC3\\xAFve", cleanup);
                        Assert.Contains("HINT: hint d\\xC3\\xA9j\\xC3\\xA0", cleanup);
                    }
                    else
                    {
                        Assert.IsEmpty(CompletionLog(PostgresFixture.Cluster, previousLogLength, backend));
                    }

                    await AssertRecoveredAsync(command, baseline, backend, token);
                }

                AssertNoInterruptedAbort(notices);
            }, context.CancellationToken);

    /// <summary>
    /// Fabricated cancellation stays a warning without arming PostgreSQL cancellation during callback drain.
    /// </summary>
    [TestMethod]
    public Task CleanupCancellationFinishesRollbackBeforeInterruptProcessing()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CleanupCancellationFinishesRollbackBeforeInterruptProcessing),
            async (connection, transaction, token) =>
            {
                int backend = connection.ProcessID;
                var notices = new List<PostgresNotice>();
                connection.Notice += (_, args) => notices.Add(args.Notice);
                await using var command = new NpgsqlCommand(string.Empty, connection, transaction);
                long[] baseline = await PrepareCaptureAsync(command, token);
                command.CommandText = "SET LOCAL log_min_messages = warning";
                await command.ExecuteNonQueryAsync(token);
                int previousLogLength = PostgresFixture.Cluster.ReadServerLog().Length;
                await transaction.SaveAsync("callback_cancel", token);
                const string query = "SELECT datatype.memory_callback_prepare_cancellation(2); " +
                    "DO $$ BEGIN RAISE EXCEPTION 'primary failure' USING ERRCODE='23514'; END $$";
                Assert.AreSequenceEqual(["23514", "primary failure", string.Empty, string.Empty],
                    await CaptureAsync(command, query, token));

                await transaction.ReleaseAsync("callback_cancel", token);
                Assert.Contains("WARNING: 57014: cleanup cancellation",
                    CompletionLog(PostgresFixture.Cluster, previousLogLength, backend));
                command.CommandText = "SELECT datatype.memory_callback_implicit_state()";
                Assert.AreEqual("B73,A73|False,False|False|stale", await command.ExecuteScalarAsync(token));
                command.CommandText = "SELECT tests.raw_call_holdoffs()";
                Assert.AreEqual(0L, await command.ExecuteScalarAsync(token));
                await AssertRecoveredAsync(command, baseline, backend, token);
                AssertNoInterruptedAbort(notices);
            }, context.CancellationToken);

    /// <summary>
    /// Managed SPI and explicit subtransactions retain the primary error and permit further SQL after complete callback drain.
    /// </summary>
    /// <param name="spi">Whether SPI owns the failed subtransaction.</param>
    /// <param name="cleanupThrows">Whether the secondary callback also throws.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public Task ManagedSubtransactionCleanupPreservesPrimaryErrorAndRecovery(bool spi, bool cleanupThrows)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ManagedSubtransactionCleanupPreservesPrimaryErrorAndRecovery),
            async (connection, transaction, token) =>
            {
                int backend = connection.ProcessID;
                var notices = new List<PostgresNotice>();
                connection.Notice += (_, args) => notices.Add(args.Notice);
                await using var command = new NpgsqlCommand(Resources, connection, transaction);
                long[] baseline = Assert.IsInstanceOfType<long[]>(await command.ExecuteScalarAsync(token));
                for (int index = 0; index < 10; index++)
                {
                    int previousLogLength = PostgresFixture.Cluster.ReadServerLog().Length;
                    command.CommandText = "SELECT datatype.callback_subtransaction_recovery($1, $2)";
                    command.Parameters.AddWithValue(spi);
                    command.Parameters.AddWithValue(cleanupThrows);
                    string[] result;
                    try
                    {
                        result = Assert.IsInstanceOfType<string[]>(await command.ExecuteScalarAsync(token));
                    }
                    finally
                    {
                        command.Parameters.Clear();
                    }

                    Assert.AreSequenceEqual<string>(["23514", "managed primary cleanup failure", "managed primary detail",
                        "managed primary hint", "B73,A73|False,False|False|stale", "42"], result);
                    if (cleanupThrows)
                    {
                        string cleanup = CompletionLog(PostgresFixture.Cluster, previousLogLength, backend);
                        Assert.Contains("WARNING: 22023: callback caf\\xC3\\xA9", cleanup);
                        Assert.Contains("DETAIL: detail na\\xC3\\xAFve", cleanup);
                        Assert.Contains("HINT: hint d\\xC3\\xA9j\\xC3\\xA0", cleanup);
                    }
                    else
                    {
                        Assert.IsEmpty(CompletionLog(PostgresFixture.Cluster, previousLogLength, backend));
                    }

                    await AssertRecoveredAsync(command, baseline, backend, token);
                }

                AssertNoInterruptedAbort(notices);
            }, context.CancellationToken);

    /// <summary>
    /// A failing native report hook cannot run while durable cleanup is written through PostgreSQL's emergency log path.
    /// </summary>
    /// <param name="preparedTransaction">Whether PostgreSQL prepares the transaction instead of committing it.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task CleanupEmergencyLogBypassesFailingHookAndPreservesCommit(bool preparedTransaction)
        => AssertCompletionCleanupAsync("datatype.memory_callback_prepare(1, true)", preparedTransaction, failingHook: true);

    /// <summary>
    /// Durable cleanup reporting does not allocate PostgreSQL diagnostic state.
    /// </summary>
    /// <param name="stage">One for context creation or two for diagnostic copying.</param>
    /// <param name="preparedTransaction">Whether PostgreSQL prepares instead of committing the transaction.</param>
    [TestMethod]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(2, false)]
    [DataRow(2, true)]
    public Task CompletionReportingDoesNotAllocatePostgreSqlDiagnostics(int stage, bool preparedTransaction)
        => AssertCompletionCleanupAsync(FormattableString.Invariant($"tests.completion_reporting_allocation_fault({stage})"),
            preparedTransaction, allocationStage: stage);

    /// <summary>
    /// Caught terminal reports preserve durable commit or preparation without attempting an illegal abort.
    /// </summary>
    /// <param name="level">The terminal logging level requested by the callback.</param>
    /// <param name="preparedTransaction">Whether PostgreSQL prepares the transaction instead of committing it.</param>
    [TestMethod]
    [DataRow((int)PgLogLevel.Fatal, false)]
    [DataRow((int)PgLogLevel.Fatal, true)]
    [DataRow((int)PgLogLevel.Panic, false)]
    [DataRow((int)PgLogLevel.Panic, true)]
    public Task CleanupTerminalReportPreservesCommitWithSafeSeverity(int level, bool preparedTransaction)
        => AssertTerminalCleanupAsync(FormattableString.Invariant($"datatype.memory_callback_prepare_terminal({level})"), level,
            preparedTransaction: preparedTransaction);

    /// <summary>
    /// A read-only commit writes no commit record, so a caught FATAL cleanup report remains FATAL instead of PANIC.
    /// </summary>
    [TestMethod]
    public Task CleanupTerminalReportAfterReadOnlyCommitRemainsFatal()
        => AssertTerminalCleanupAsync(FormattableString.Invariant($"datatype.memory_callback_prepare_terminal({(int)PgLogLevel.Fatal})"),
            (int)PgLogLevel.Fatal, write: false);

    /// <summary>
    /// A terminal cleanup report while a parallel worker commits stays FATAL: the worker exits, the leader receives the
    /// report as PostgreSQL's ERROR from a parallel worker, and the cluster keeps running.
    /// </summary>
    [TestMethod]
    public async Task CleanupTerminalReportInParallelWorkerCommitRemainsFatal()
    {
        CancellationToken token = context.CancellationToken;
        using IDisposable recoverySlot = await CrashRecovery.ReserveAsync(token);
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(token);
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using NpgsqlConnection observer = await cluster.OpenConnectionAsync(token);
        await using (var setup = new NpgsqlCommand("CREATE SCHEMA datatype; CREATE EXTENSION ankus_test WITH SCHEMA datatype", observer))
        {
            await setup.ExecuteNonQueryAsync(token);
        }

        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        int leader = connection.ProcessID;
        int logStart = cluster.ReadServerLog().Length;
        // A single-copy Gather runs the call only in the worker; PostgreSQL 16 renamed the setting.
        string forceParallel = connection.PostgreSqlVersion.Major >= 16 ? "debug_parallel_query" : "force_parallel_mode";
        await using var command = new NpgsqlCommand($"SET {forceParallel} = on; SET parallel_setup_cost = 0; " +
            "SET parallel_tuple_cost = 0; SET max_parallel_workers_per_gather = 1", connection);
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT datatype.memory_callback_prepare_terminal_in_worker()";
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));

        Assert.AreEqual("ERROR", error.InvariantSeverity);
        Assert.AreEqual("P7806", error.SqlState);
        Assert.AreEqual("terminal cleanup café", error.MessageText);
        Assert.Contains("parallel worker", error.Where ?? string.Empty);
        Assert.AreEqual(leader, connection.ProcessID);
        command.CommandText = "SELECT 42";
        Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
        await using var observerCheck = new NpgsqlCommand("SELECT 42", observer);
        Assert.AreEqual(42, await observerCheck.ExecuteScalarAsync(token));
        string log = cluster.ReadServerLog()[logStart..];
        Assert.Contains("FATAL:  terminal cleanup café", log);
        Assert.Contains("ERROR:  terminal cleanup café", log);
        Assert.DoesNotContain("PANIC", log);
        Assert.DoesNotContain("reinitializing", log);
        Assert.DoesNotContain("TRAP: failed Assert", log);
    }

    /// <summary>
    /// An emit_log_hook that raises ERROR while a terminal cleanup report is written cannot make it recoverable: PostgreSQL
    /// promotes the hook's error to the pending FATAL, so the backend still exits, reporting the hook's error.
    /// </summary>
    [TestMethod]
    public Task CleanupTerminalReportKeepsFatalWhenLogHookFails()
        => AssertTerminalCleanupAsync(FormattableString.Invariant($"datatype.memory_callback_prepare_terminal({(int)PgLogLevel.Fatal})"),
            (int)PgLogLevel.Fatal, write: false, failingReport: true);

    /// <summary>
    /// When building the complete terminal cleanup report fails, the backend still ends with the requested severity,
    /// SQLSTATE and message: FATAL after a read-only commit and PANIC after a durable one, whose write survives recovery.
    /// </summary>
    /// <param name="write">Whether the transaction writes and therefore commits durably.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TerminalCleanupReportFailureFallsBackToTransportedMessage(bool write)
    {
        CancellationToken token = context.CancellationToken;
        using IDisposable recoverySlot = await CrashRecovery.ReserveAsync(token);
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(token);
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using NpgsqlConnection observer = await cluster.OpenConnectionAsync(token);
        await using var setup = new NpgsqlCommand("CREATE SCHEMA tests; CREATE TABLE terminal_fallback(value integer); " +
            AllocatorFaultFixtureCompiler.CompletionReportingSql, observer);
        await setup.ExecuteNonQueryAsync(token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        string marker = "terminal-fallback-" + Guid.NewGuid().ToString("N");
        await using var command = new NpgsqlCommand($"SET application_name='{marker}'; SET log_error_verbosity=verbose; BEGIN" +
            (write ? "; INSERT INTO terminal_fallback VALUES(73)" : string.Empty) + "; SELECT tests.completion_terminal_fault()", connection);
        int logStart = cluster.ReadServerLog().Length;
        Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
        command.CommandText = "COMMIT";
        NpgsqlException failure = await Assert.ThrowsAsync<NpgsqlException>(() => command.ExecuteNonQueryAsync(token));
        string severity = write ? "PANIC" : "FATAL";
        if (failure is PostgresException error)
        {
            Assert.AreEqual(severity, error.InvariantSeverity);
            Assert.AreEqual("P7808", error.SqlState);
            Assert.AreEqual("terminal fallback report", error.MessageText);
        }

        using CancellationTokenSource deadline = CrashRecovery.CreateDeadline(token);
        string log = write ? await CrashRecovery.WaitAsync(cluster, deadline.Token) : cluster.ReadServerLog();
        Assert.Contains($"[{marker}]: {severity}:  P7808: terminal fallback report", log[logStart..]);
        Assert.DoesNotContain("controlled terminal report construction failure", log[logStart..]);
        await using NpgsqlConnection recovered = await cluster.OpenConnectionAsync(deadline.Token);
        await using var query = new NpgsqlCommand("SELECT count(*) FROM terminal_fallback", recovered);
        Assert.AreEqual(write ? 1L : 0L, await query.ExecuteScalarAsync(deadline.Token));
    }

    /// <summary>
    /// Caught terminal intent from iterator or aggregate disposal terminates safely after an executor failure starts rollback.
    /// </summary>
    /// <param name="aggregate">Whether an aggregate owns cleanup rather than an iterator.</param>
    /// <param name="level">The callback's requested FATAL or PANIC severity.</param>
    [TestMethod]
    [DataRow(false, (int)PgLogLevel.Fatal)]
    [DataRow(false, (int)PgLogLevel.Panic)]
    [DataRow(true, (int)PgLogLevel.Fatal)]
    [DataRow(true, (int)PgLogLevel.Panic)]
    public Task ExecutorCleanupTerminalReportPreservesSeverityAndRollsBackWrites(bool aggregate, int level)
        => AssertTerminalCleanupAsync("42", level, aggregate
                ? "SELECT aggregate_values.managed_sum(value) FROM (VALUES(1),(2),(3)) AS input(value)"
                : FormattableString.Invariant($"SELECT 1/value FROM (SELECT set_values.set_terminal_cleanup({level}) AS value) AS input"),
            aggregate ? "SELECT aggregate_values.aggregate_reset('" +
                (level == (int)PgLogLevel.Fatal ? "cleanup_terminal_fatal" : "cleanup_terminal_panic") + "')" : null);

    /// <summary>
    /// Verifies emergency cleanup diagnostics after a durable transaction boundary.
    /// </summary>
    /// <param name="preparation">The fixture call retaining the callback until completion.</param>
    /// <param name="preparedTransaction">Whether the irreversible completion prepares the transaction.</param>
    /// <param name="failingHook">Whether a native emit-log hook would reject ordinary reporting.</param>
    /// <param name="allocationStage">The staged PostgreSQL allocation failure that must remain unused.</param>
    private async Task AssertCompletionCleanupAsync(string preparation, bool preparedTransaction,
        bool failingHook = false, int allocationStage = 0)
    {
        CancellationToken token = context.CancellationToken;
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(token);
        if (preparedTransaction)
        {
            options = new()
            {
                Installation = options.Installation,
                DataDirectoryBase = options.DataDirectoryBase,
                LogDirectory = options.LogDirectory,
                StartupTimeout = options.StartupTimeout,
                PostgreSqlConfiguration = [.. options.PostgreSqlConfiguration, "max_prepared_transactions=10"],
            };
        }

        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using NpgsqlConnection observer = await cluster.OpenConnectionAsync(token);
        await using var setup = new NpgsqlCommand("CREATE SCHEMA datatype; CREATE EXTENSION ankus_test WITH SCHEMA datatype; " +
            "CREATE SCHEMA tests; CREATE TABLE callback_reporter_commit(value integer); " +
            NativeRawCallFixtureCompiler.InstallationSql + AllocatorFaultFixtureCompiler.CompletionReportingSql, observer);
        await setup.ExecuteNonQueryAsync(token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await using var command = new NpgsqlCommand("SET log_min_messages=warning" +
            (failingHook ? "; SELECT tests.log_prefix_arm('callback café')" : string.Empty), connection);
        await command.ExecuteNonQueryAsync(token);
        int logStart = cluster.ReadServerLog().Length;
        command.CommandText = "BEGIN; INSERT INTO callback_reporter_commit VALUES(73); SELECT " + preparation;
        Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
        command.CommandText = preparedTransaction ? "PREPARE TRANSACTION 'cleanup_reporter_prepared'" : "COMMIT";
        await command.ExecuteNonQueryAsync(token);
        Assert.AreEqual(System.Data.ConnectionState.Open, connection.State);
        command.CommandText = "SELECT pg_backend_pid()";
        Assert.AreEqual(backend, await command.ExecuteScalarAsync(token));
        if (allocationStage != 0)
        {
            command.CommandText = "SELECT tests.completion_reporting_allocation_remaining()";
            Assert.AreEqual(allocationStage, await command.ExecuteScalarAsync(token));
        }

        if (failingHook)
        {
            command.CommandText = "SELECT tests.log_prefix_calls()";
            Assert.AreEqual(0L, await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT tests.log_prefix_active()";
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            command.CommandText = "SELECT tests.log_prefix_restore()";
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
        }

        string log = CompletionLog(cluster, logStart, backend);
        Assert.Contains("WARNING: 22023: callback caf\\xC3\\xA9", log);
        if (allocationStage == 0)
        {
            Assert.Contains("DETAIL: detail na\\xC3\\xAFve", log);
            Assert.Contains("HINT: hint d\\xC3\\xA9j\\xC3\\xA0", log);
        }

        string completeLog = cluster.ReadServerLog()[logStart..];
        Assert.DoesNotContain("PANIC:", completeLog);
        Assert.DoesNotContain("reinitializing", completeLog);
        if (preparedTransaction)
        {
            await using var prepared = new NpgsqlCommand("SELECT count(*) FROM pg_prepared_xacts WHERE gid='cleanup_reporter_prepared'", observer);
            Assert.AreEqual(1L, await prepared.ExecuteScalarAsync(token));
            prepared.CommandText = "COMMIT PREPARED 'cleanup_reporter_prepared'";
            await prepared.ExecuteNonQueryAsync(token);
        }

        await using var query = new NpgsqlCommand("SELECT ARRAY(SELECT value FROM callback_reporter_commit)", observer);
        Assert.AreSequenceEqual([73], Assert.IsInstanceOfType<int[]>(await query.ExecuteScalarAsync(token)));
    }

    /// <summary>
    /// Verifies exact terminal severity and transaction durability after cleanup terminates a backend.
    /// </summary>
    /// <param name="preparation">The fixture call retaining the callback until cleanup.</param>
    /// <param name="level">The requested FATAL or PANIC level.</param>
    /// <param name="abortQuery">The executor failure initiating callback cleanup, or null for durable completion.</param>
    /// <param name="setupStatement">Optional backend-local fixture setup before writes begin.</param>
    /// <param name="preparedTransaction">Whether the irreversible completion prepares the transaction.</param>
    /// <param name="write">Whether the transaction writes and therefore commits durably.</param>
    /// <param name="failingReport">Whether a log hook raises ERROR while the terminal report is written.</param>
    private async Task AssertTerminalCleanupAsync(string preparation, int level, string? abortQuery = null,
        string? setupStatement = null, bool preparedTransaction = false, bool write = true, bool failingReport = false)
    {
        CancellationToken token = context.CancellationToken;
        using IDisposable recoverySlot = await CrashRecovery.ReserveAsync(token);
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(token);
        if (preparedTransaction)
        {
            options = new()
            {
                Installation = options.Installation,
                DataDirectoryBase = options.DataDirectoryBase,
                LogDirectory = options.LogDirectory,
                StartupTimeout = options.StartupTimeout,
                PostgreSqlConfiguration = [.. options.PostgreSqlConfiguration, "max_prepared_transactions=10"],
            };
        }

        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using NpgsqlConnection observer = await cluster.OpenConnectionAsync(token);
        await using var setup = new NpgsqlCommand("CREATE SCHEMA datatype; CREATE EXTENSION ankus_test WITH SCHEMA datatype; " +
            "CREATE TABLE callback_reporter_commit(value integer)" +
            (failingReport ? "; CREATE SCHEMA tests; " + NativeRawCallFixtureCompiler.InstallationSql : string.Empty), observer);
        await setup.ExecuteNonQueryAsync(token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        string marker = "callback-terminal-" + Guid.NewGuid().ToString("N");
        await using var command = new NpgsqlCommand($"SET application_name='{marker}'; SET log_error_verbosity=verbose" +
            (failingReport ? "; SELECT tests.log_prefix_arm('terminal cleanup café')" : string.Empty), connection);
        await command.ExecuteNonQueryAsync(token);
        int logStart = cluster.ReadServerLog().Length;
        command.CommandText = "BEGIN";
        await command.ExecuteNonQueryAsync(token);
        if (setupStatement is not null)
        {
            command.CommandText = setupStatement;
            await command.ExecuteNonQueryAsync(token);
        }

        command.CommandText = (write ? "INSERT INTO callback_reporter_commit VALUES(73); " : string.Empty) + "SELECT " + preparation;
        Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
        command.CommandText = preparedTransaction
            ? "PREPARE TRANSACTION 'cleanup_reporter_prepared'"
            : abortQuery ?? "COMMIT";
        NpgsqlException failure = await Assert.ThrowsAsync<NpgsqlException>(() => command.ExecuteNonQueryAsync(token));
        bool irreversible = abortQuery is null && write;
        string severity = level == (int)PgLogLevel.Panic || irreversible ? "PANIC" : "FATAL";
        if (failure is PostgresException error)
        {
            if (error.InvariantSeverity == "ERROR" && abortQuery is not null)
            {
                bool aggregate = setupStatement is not null;
                Assert.AreEqual(aggregate ? "P7801" : "22012", error.SqlState);
                Assert.AreEqual(aggregate ? "aggregate transition failed" : "division by zero", error.MessageText);
                Assert.AreEqual(aggregate ? "owned aggregate detail" : null, error.Detail);
                Assert.AreEqual(aggregate ? "retry valid inputs" : null, error.Hint);
            }
            else if (failingReport)
            {
                Assert.AreEqual(severity, error.InvariantSeverity);
                Assert.AreEqual("P7521", error.SqlState);
                Assert.AreEqual("native failure before managed log handler", error.MessageText);
            }
            else
            {
                Assert.AreEqual(severity, error.InvariantSeverity);
                Assert.AreEqual("P7806", error.SqlState);
                Assert.AreEqual("terminal cleanup café", error.MessageText);
                Assert.AreEqual("terminal cleanup naïve", error.Detail);
                Assert.AreEqual("restart after terminal cleanup déjà", error.Hint);
            }
        }

        Assert.AreEqual(System.Data.ConnectionState.Closed, connection.State);
        string completeLog;
        using CancellationTokenSource deadline = CrashRecovery.CreateDeadline(token);
        if (severity == "PANIC")
        {
            completeLog = await CrashRecovery.WaitAsync(cluster, deadline.Token);
        }
        else
        {
            completeLog = cluster.ReadServerLog();
            await using var observerCheck = new NpgsqlCommand("SELECT 42", observer);
            Assert.AreEqual(42, await observerCheck.ExecuteScalarAsync(deadline.Token));
        }

        string log = string.Join('\n', completeLog[logStart..].Split('\n')
            .Where(line => line.Contains($"[{marker}]:", StringComparison.Ordinal)));
        if (failingReport)
        {
            // The hook fails before PostgreSQL writes the original report, and its own error is written instead.
            Assert.Contains($"{severity}:  P7521: native failure before managed log handler", log);
            Assert.DoesNotContain("terminal cleanup café", log);
        }
        else
        {
            Assert.Contains($"{severity}:  P7806: terminal cleanup café", log);
            Assert.Contains("terminal cleanup naïve", log);
            Assert.Contains("restart after terminal cleanup déjà", log);
        }

        Assert.DoesNotContain("TRAP: failed Assert", completeLog);
        Assert.DoesNotContain("it was already committed", log);
        if (severity == "FATAL")
        {
            string abortState = abortQuery is null ? "COMMIT" : "ABORT";
            int abortWarnings = log.Split($"AbortTransaction while in {abortState} state", StringSplitOptions.None).Length - 1;
            Assert.AreEqual(1, abortWarnings);
            Assert.DoesNotContain("reinitializing", completeLog[logStart..]);
        }

        await using NpgsqlConnection recovered = await cluster.OpenConnectionAsync(deadline.Token);
        if (preparedTransaction)
        {
            await using var prepared = new NpgsqlCommand("SELECT count(*) FROM pg_prepared_xacts WHERE gid='cleanup_reporter_prepared'", recovered);
            Assert.AreEqual(1L, await prepared.ExecuteScalarAsync(deadline.Token));
            prepared.CommandText = "COMMIT PREPARED 'cleanup_reporter_prepared'";
            await prepared.ExecuteNonQueryAsync(deadline.Token);
        }

        await using var query = new NpgsqlCommand("SELECT ARRAY(SELECT value FROM callback_reporter_commit)", recovered);
        Assert.AreSequenceEqual(abortQuery is null && write ? [73] : [], Assert.IsInstanceOfType<int[]>(await query.ExecuteScalarAsync(deadline.Token)));
        query.CommandText = "SELECT datatype.log_message(9, 'cleanup reporter recovered'), 42";
        await using NpgsqlDataReader reader = await query.ExecuteReaderAsync(deadline.Token);
        Assert.IsTrue(await reader.ReadAsync(deadline.Token));
        Assert.AreEqual(42, reader.GetInt32(0));
        Assert.AreEqual(42, reader.GetInt32(1));
        Assert.IsFalse(await reader.ReadAsync(deadline.Token));
    }

    /// <summary>
    /// Creates and warms only PostgreSQL's ordinary exception-handler expression plans before native resource measurement.
    /// </summary>
    private static async Task<long[]> PrepareCaptureAsync(NpgsqlCommand command, CancellationToken token)
    {
        command.CommandText = """
            CREATE FUNCTION pg_temp.capture_cleanup_error(command_text text) RETURNS text[] LANGUAGE plpgsql AS $capture$
            DECLARE failure_state text; failure_message text; failure_detail text; failure_hint text;
            BEGIN
                EXECUTE command_text;
                RETURN ARRAY['00000', 'No error', '', ''];
            EXCEPTION WHEN OTHERS THEN
                GET STACKED DIAGNOSTICS failure_state = RETURNED_SQLSTATE, failure_message = MESSAGE_TEXT,
                    failure_detail = PG_EXCEPTION_DETAIL, failure_hint = PG_EXCEPTION_HINT;
                RETURN ARRAY[failure_state, failure_message, failure_detail, failure_hint];
            END
            $capture$
            """;
        await command.ExecuteNonQueryAsync(token);
        Assert.AreSequenceEqual(["22012", "division by zero", string.Empty, string.Empty],
            await CaptureAsync(command, "SELECT 1/0", token));
        command.CommandText = Resources;
        return Assert.IsInstanceOfType<long[]>(await command.ExecuteScalarAsync(token));
    }

    /// <summary>
    /// Reads exact diagnostics after PostgreSQL itself aborts the nested exception subtransaction.
    /// </summary>
    private static async Task<string[]> CaptureAsync(NpgsqlCommand command, string query, CancellationToken token)
    {
        command.CommandText = "SELECT pg_temp.capture_cleanup_error($1)";
        command.Parameters.AddWithValue(query);
        try
        {
            string[] diagnostic = Assert.IsInstanceOfType<string[]>(await command.ExecuteScalarAsync(token));
            Assert.HasCount(4, diagnostic);
            return diagnostic;
        }
        finally
        {
            command.Parameters.Clear();
        }
    }

    /// <summary>
    /// Proves SPI ownership, native frame balance and the actual PostgreSQL backend remain intact.
    /// </summary>
    private static async Task AssertRecoveredAsync(NpgsqlCommand command, long[] baseline, int backend, CancellationToken token)
    {
        command.CommandText = Resources;
        Assert.AreSequenceEqual(baseline, Assert.IsInstanceOfType<long[]>(await command.ExecuteScalarAsync(token)));
        command.CommandText = "SELECT set_values.set_interrupt_state()";
        Assert.AreSequenceEqual([0, 0], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token)));
        command.CommandText = "SELECT pg_backend_pid()";
        Assert.AreEqual(backend, await command.ExecuteScalarAsync(token));
        command.CommandText = "SELECT 42";
        Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
    }

    /// <summary>
    /// Proves a caught raw failure remains visible after callback unwinding without replacing the primary error.
    /// </summary>
    /// <param name="cluster">The PostgreSQL cluster whose server log receives emergency cleanup diagnostics.</param>
    /// <param name="start">The server-log length before this iteration.</param>
    /// <param name="backend">The backend executing cleanup.</param>
    /// <param name="completion">Whether cleanup runs during transaction completion.</param>
    /// <param name="message">The independent native lookup diagnostic.</param>
    private static void AssertNativeCleanupWarning(PostgresTestCluster cluster, int start, int backend, bool completion, string message)
    {
        string cleanup = CompletionLog(cluster, start, backend);
        if (completion)
        {
            Assert.Contains("WARNING: 42704: " + message, cleanup);
        }
        else
        {
            Assert.IsEmpty(cleanup);
        }
    }

    /// <summary>
    /// Reads allocation-free completion diagnostics for one backend after the supplied server-log offset.
    /// </summary>
    /// <param name="cluster">The PostgreSQL cluster.</param>
    /// <param name="start">The server-log length before cleanup.</param>
    /// <param name="backend">The backend process identifier.</param>
    /// <returns>Only Ankus completion lines for the backend.</returns>
    private static string CompletionLog(PostgresTestCluster cluster, int start, int backend)
        => string.Join('\n', cluster.ReadServerLog()[start..].Split('\n').Where(line =>
            line.Contains($"ANKUS CLEANUP [backend {backend}]", StringComparison.Ordinal)));

    /// <summary>
    /// Rejects transaction-state warnings from repeated attempts to abort the same unfinished cleanup.
    /// </summary>
    private static void AssertNoInterruptedAbort(List<PostgresNotice> notices)
        => Assert.IsEmpty(notices.Where(static notice => notice.MessageText.Contains("AbortSubTransaction while", StringComparison.Ordinal) ||
            notice.MessageText.Contains("AbortTransaction while", StringComparison.Ordinal)));
}
