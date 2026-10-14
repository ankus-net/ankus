using System.Text.Json;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies set-returning values, executor modes and enumerator lifetime in a real PostgreSQL backend.
/// </summary>
/// <param name="context">The current test context.</param>
[TestClass]
[DoNotParallelize]
public sealed class SetReturningTests(TestContext context)
{
    /// <summary>
    /// Scalar sets distinguish empty sequences, NULL cells and strict NULL input suppression.
    /// </summary>
    [TestMethod]
    public Task ScalarSetsPreserveEmptyAndNullSemantics()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ScalarSetsPreserveEmptyAndNullSemantics), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT (SELECT array_agg(v) FROM set_values.set_series(4,3) v),
                    (SELECT count(*) FROM set_values.set_series(4,0)),
                    (SELECT count(*) FROM set_values.set_nullable(true)),
                    (SELECT array_agg(v) FROM set_values.set_nullable(false) v),
                    (SELECT count(*) FROM set_values.set_probe(NULL,0,false))
                """, connection, transaction);
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
            {
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreSequenceEqual([4, 5, 6], reader.GetFieldValue<int[]>(0));
                Assert.AreEqual(0L, reader.GetInt64(1));
                Assert.AreEqual(0L, reader.GetInt64(2));
                Assert.AreSequenceEqual([null, 7, null, 19], reader.GetFieldValue<int?[]>(3));
                Assert.AreEqual(0L, reader.GetInt64(4));
            }

            int[] counters = await ReadStatusAsync(command, token);
            Assert.AreEqual(0, counters[0], "STRICT NULL input must not invoke the sequence factory.");
            Assert.AreEqual(0, counters[10]);
        }, context.CancellationToken);

    /// <summary>
    /// PostgreSQL table metadata retains declared names and tuple elements beyond ValueTuple's seventh field.
    /// </summary>
    [TestMethod]
    public Task TableColumnsPreserveNamesTypesAndValues()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(TableColumnsPreserveNamesTypesAndValues), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT * FROM set_values.set_named()", connection, transaction);
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
            {
                Assert.AreEqual(2, reader.FieldCount);
                Assert.AreEqual("custom_id", reader.GetName(0));
                Assert.AreEqual("custom_text", reader.GetName(1));
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(1, reader.GetInt32(0));
                Assert.AreEqual("café", reader.GetString(1));
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(2, reader.GetInt32(0));
                Assert.IsTrue(reader.IsDBNull(1));
                Assert.IsFalse(await reader.ReadAsync(token));
            }

            command.CommandText = "SELECT * FROM set_values.set_one_column()";
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
            {
                Assert.AreEqual(1, reader.FieldCount);
                Assert.AreEqual("custom_value", reader.GetName(0));
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(7, reader.GetInt32(0));
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.IsTrue(reader.IsDBNull(0));
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(19, reader.GetInt32(0));
                Assert.IsFalse(await reader.ReadAsync(token));
            }

            command.CommandText = """
                SELECT first_number, second_text, third_number, identifier, mood::text, numbers::text,
                    exact_value::text, flag, ninth_text, pg_typeof(mood)::text, pg_typeof(numbers)::text
                FROM set_values.set_wide()
                """;
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
            {
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(1, reader.GetInt32(0));
                Assert.AreEqual("café", reader.GetString(1));
                Assert.AreEqual(5000000000L, reader.GetInt64(2));
                Assert.AreEqual(Guid.Parse("c7c3e551-bd58-4dc6-b1cd-065b72302136"), reader.GetGuid(3));
                Assert.AreEqual("café", reader.GetString(4));
                Assert.AreEqual("[-1:0][5:6]={{4,NULL},{6,7}}", reader.GetString(5));
                Assert.AreEqual("1234.500", reader.GetString(6));
                Assert.IsTrue(reader.GetBoolean(7));
                Assert.AreEqual("ninth", reader.GetString(8));
                Assert.AreEqual("datatype.enum_mood", reader.GetString(9));
                Assert.AreEqual("integer[]", reader.GetString(10));
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(2, reader.GetInt32(0));
                Assert.IsTrue(reader.IsDBNull(1));
                Assert.AreEqual(-5000000000L, reader.GetInt64(2));
                Assert.AreEqual(Guid.Empty, reader.GetGuid(3));
                Assert.IsTrue(reader.IsDBNull(4));
                Assert.IsTrue(reader.IsDBNull(5));
                Assert.AreEqual("0", reader.GetString(6));
                Assert.IsFalse(reader.GetBoolean(7));
                Assert.AreEqual("last", reader.GetString(8));
                Assert.IsFalse(await reader.ReadAsync(token));
            }
        }, context.CancellationToken);

    /// <summary>
    /// Catalog metadata includes set status, default and explicit row estimates, function options and TABLE argument modes.
    /// </summary>
    [TestMethod]
    public Task SetCatalogRetainsRowsAndTableContracts()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(SetCatalogRetainsRowsAndTableContracts), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT probe.proretset, probe.prorettype = 'integer'::regtype, probe.prorows, probe.procost,
                    series.prorows, series.provolatile::text, series.proparallel::text,
                    named.prorows, named.prorettype = 'record'::regtype, named.proargnames, named.proargmodes::text[],
                    named.proallargtypes = ARRAY['integer'::regtype::oid,'text'::regtype::oid],
                    wide.proargnames, one.proargnames, one.proargmodes::text[]
                FROM pg_proc probe CROSS JOIN pg_proc series CROSS JOIN pg_proc named CROSS JOIN pg_proc wide CROSS JOIN pg_proc one
                WHERE probe.oid = 'set_values.set_probe(integer,integer,boolean)'::regprocedure
                    AND series.oid = 'set_values.set_pure_series(integer,integer)'::regprocedure
                    AND named.oid = 'set_values.set_named()'::regprocedure
                    AND wide.oid = 'set_values.set_wide()'::regprocedure
                    AND one.oid = 'set_values.set_one_column()'::regprocedure
                """, connection, transaction);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.IsTrue(await reader.ReadAsync(token));
            Assert.IsTrue(reader.GetBoolean(0));
            Assert.IsTrue(reader.GetBoolean(1));
            Assert.AreEqual(17f, reader.GetFloat(2));
            Assert.AreEqual(2.5f, reader.GetFloat(3));
            Assert.AreEqual(23.5f, reader.GetFloat(4));
            Assert.AreEqual("i", reader.GetString(5));
            Assert.AreEqual("s", reader.GetString(6));
            Assert.AreEqual(1000f, reader.GetFloat(7));
            Assert.IsTrue(reader.GetBoolean(8));
            Assert.AreSequenceEqual(["custom_id", "custom_text"], reader.GetFieldValue<string[]>(9));
            Assert.AreSequenceEqual(["t", "t"], reader.GetFieldValue<string[]>(10));
            Assert.IsTrue(reader.GetBoolean(11));
            Assert.AreSequenceEqual(["first_number", "second_text", "third_number", "identifier", "mood", "numbers", "exact_value", "flag", "ninth_text"], reader.GetFieldValue<string[]>(12));
            Assert.AreSequenceEqual(["custom_value"], reader.GetFieldValue<string[]>(13));
            Assert.AreSequenceEqual(["t"], reader.GetFieldValue<string[]>(14));
            Assert.IsFalse(await reader.ReadAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// ProjectSet LIMIT terminates iteration early, while FunctionScan fully consumes a set before applying its LIMIT.
    /// </summary>
    [TestMethod]
    [DataRow("SELECT set_values.set_probe(5,0,true) LIMIT 2", 2, 2)]
    [DataRow("SELECT set_values.set_probe_streaming(5,0,true) LIMIT 2", 2, 2)]
    [DataRow("SELECT * FROM set_values.set_probe(5,0,true) LIMIT 2", 6, 5)]
    [DataRow("SELECT * FROM set_values.set_probe_streaming(5,0,true) LIMIT 2", 6, 5)]
    [DataRow("SELECT * FROM set_values.set_probe_materialized(5,0,true) LIMIT 2", 6, 5)]
    [DataRow("SELECT set_values.set_probe_materialized(5,0,true) LIMIT 2", 6, 5)]
    public Task ExecutorModesDisposeExactlyOnce(string sql, int expectedMoves, int expectedCurrent)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ExecutorModesDisposeExactlyOnce), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("CREATE TEMP TABLE set_cleanup(value int); SELECT set_values.set_reset()", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = sql;
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
            {
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(1, reader.GetInt32(0));
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(2, reader.GetInt32(0));
                Assert.IsFalse(await reader.ReadAsync(token));
            }

            Assert.AreSequenceEqual([1, 1, 1, 1, expectedMoves, expectedCurrent, 1, 1, 1, 0, 0, 0, 0], await ReadStatusAsync(command, token));
            command.CommandText = "SELECT count(*) FROM set_cleanup";
            Assert.AreEqual(1L, await command.ExecuteScalarAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// A nullable empty result, a zero-row enumerator and an untouched LIMIT zero retain separate lifecycle contracts.
    /// </summary>
    [TestMethod]
    [DataRow("SELECT count(*) FROM set_values.set_probe(0,0,false)", 1)]
    [DataRow("SELECT set_values.set_probe(3,0,false) LIMIT 0", 0)]
    public Task EmptyExecutionDoesNotLeakEnumerators(string sql, int enumerators)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EmptyExecutionDoesNotLeakEnumerators), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT set_values.set_reset()", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = sql;
            await command.ExecuteNonQueryAsync(token);
            Assert.AreSequenceEqual([enumerators, enumerators, enumerators, enumerators, enumerators, 0, enumerators, enumerators, 0, 0, 0, 0, 0], await ReadStatusAsync(command, token));
        }, context.CancellationToken);

    /// <summary>
    /// Independent factory and iterator failures preserve the primary SQLSTATE and dispose only acquired enumerators.
    /// </summary>
    [TestMethod]
    [DataRow("set_probe_streaming", 1)]
    [DataRow("set_probe_streaming", 2)]
    [DataRow("set_probe_streaming", 3)]
    [DataRow("set_probe_streaming", 4)]
    [DataRow("set_probe_streaming", 5)]
    [DataRow("set_probe_streaming", 6)]
    [DataRow("set_probe_streaming", 7)]
    [DataRow("set_probe_streaming", 8)]
    [DataRow("set_probe_materialized", 1)]
    [DataRow("set_probe_materialized", 2)]
    [DataRow("set_probe_materialized", 3)]
    [DataRow("set_probe_materialized", 4)]
    [DataRow("set_probe_materialized", 5)]
    [DataRow("set_probe_materialized", 6)]
    [DataRow("set_probe_materialized", 7)]
    [DataRow("set_probe_materialized", 8)]
    public Task IteratorFailuresPreserveOwnershipAndRecover(string function, int failure)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(IteratorFailuresPreserveOwnershipAndRecover), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT set_values.set_reset()", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            await transaction.SaveAsync("set_failure", token);
            command.CommandText = $"SELECT array_agg(v) FROM set_values.{function}(3,{failure},false) v";
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
            Assert.AreEqual($"P710{(failure == 8 ? 5 : failure)}", error.SqlState);
            await transaction.RollbackAsync("set_failure", token);
            int[] expected = failure switch
            {
                1 => [1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                2 => [1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                3 => [1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                4 => [1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0],
                5 or 8 => [1, 1, 1, 1, 2, 1, 1, 1, 0, 0, 0, 0, 0],
                6 => [1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0],
                7 => [1, 1, 1, 1, 4, 3, 1, 1, 0, 0, 0, 0, 0],
                _ => throw new ArgumentOutOfRangeException(nameof(failure)),
            };
            Assert.AreSequenceEqual(expected, await ReadStatusAsync(command, token));
            command.CommandText = "SELECT array_agg(v) FROM set_values.set_series(40,3) v";
            Assert.AreSequenceEqual([40, 41, 42], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token)));
        }, context.CancellationToken);

    /// <summary>
    /// Native cleanup failures cannot become successful results, and rollback permits another iterator in the same backend.
    /// </summary>
    /// <param name="mode">Normal disposal, executor abort, or disposal after a managed row failure.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public Task CaughtNativeCleanupErrorsRequireRollback(int mode)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CaughtNativeCleanupErrorsRequireRollback), async (connection, transaction, token) =>
        {
            int backend = connection.ProcessID;
            var notices = new List<PostgresNotice>();
            connection.Notice += (_, args) => notices.Add(args.Notice);
            await using var command = new NpgsqlCommand("SELECT set_values.set_reset()", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            await transaction.SaveAsync("set_native_cleanup", token);
            int logStart = PostgresFixture.Cluster.ReadServerLog().Length;
            command.CommandText = mode switch
            {
                0 => "SELECT array_agg(v) FROM set_values.set_probe_streaming(3,9,false) AS v",
                1 => "SELECT 1/(v-1) FROM (SELECT set_values.set_probe_streaming(3,9,false) AS v) s",
                _ => "SELECT array_agg(v) FROM set_values.set_probe_streaming(3,10,false) AS v",
            };
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
            Assert.AreEqual(mode switch
            {
                0 => "42704",
                1 => "22012",
                _ => "P7105",
            }, error.SqlState);
            Assert.AreEqual(mode switch
            {
                0 => "ExtensibleNodeMethods \"ankus_missing_iterator_cleanup\" was not registered",
                1 => "division by zero",
                _ => "set MoveNext failure",
            }, error.MessageText);
            Assert.IsNull(error.Detail);
            Assert.IsNull(error.Hint);
            await transaction.RollbackAsync("set_native_cleanup", token);
            Assert.IsEmpty(notices.Where(static notice => notice.SqlState == "42704"));
            string cleanup = string.Join('\n', PostgresFixture.Cluster.ReadServerLog()[logStart..].Split('\n').Where(line =>
                line.Contains($"ANKUS CLEANUP [backend {backend}]", StringComparison.Ordinal)));
            if (mode == 1)
            {
                Assert.Contains("WARNING: 42704: ExtensibleNodeMethods \"ankus_missing_iterator_cleanup\" was not registered", cleanup);
            }
            else
            {
                Assert.IsEmpty(cleanup);
            }

            int[] status = await ReadStatusAsync(command, token);
            Assert.AreEqual(1, status[6]);
            Assert.AreEqual(1, status[7]);
            Assert.AreEqual(0, status[10]);
            Assert.AreEqual(0, status[11]);
            command.CommandText = "SELECT set_values.set_native_cleanup_caught()";
            Assert.AreEqual(1, await command.ExecuteScalarAsync(token));
            command.CommandText = "SELECT array_agg(v) FROM set_values.set_probe_streaming(3,0,false) AS v";
            Assert.AreSequenceEqual([1, 2, 3], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token)));
            Assert.AreEqual(backend, connection.ProcessID);
        }, context.CancellationToken);

    /// <summary>
    /// A native row error blocks ordinary disposer SQL until its enclosing operation has rolled back.
    /// </summary>
    [TestMethod]
    public Task NativeRowFailureBlocksCleanupSql()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(NativeRowFailureBlocksCleanupSql), async (connection, transaction, token) =>
        {
            int backend = connection.ProcessID;
            await using var command = new NpgsqlCommand("CREATE TEMP TABLE set_cleanup(value int); SELECT set_values.set_reset()", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            await transaction.SaveAsync("set_native_row", token);
            command.CommandText = "SELECT set_values.set_interrupt_state()";
            Assert.AreSequenceEqual([0, 0], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token)));
            command.CommandText = "SELECT array_agg(v) FROM set_values.set_probe_streaming(3,11,true) AS v";
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
            Assert.AreEqual("42704", error.SqlState);
            Assert.AreEqual("ExtensibleNodeMethods \"ankus_missing_iterator_cleanup\" was not registered", error.MessageText);
            await transaction.RollbackAsync("set_native_row", token);
            int[] status = await ReadStatusAsync(command, token);
            Assert.AreEqual(1, status[6]);
            Assert.AreEqual(1, status[7]);
            Assert.AreEqual(0, status[8]);
            Assert.AreEqual(1, status[9]);
            Assert.AreEqual(0, status[10]);
            Assert.AreEqual(0, status[11]);
            command.CommandText = "SELECT set_values.set_interrupt_state()";
            Assert.AreSequenceEqual([0, 0], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token)));
            command.CommandText = "SELECT 42";
            Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
            Assert.AreEqual(backend, connection.ProcessID);
        }, context.CancellationToken);

    /// <summary>
    /// A native executor failure aborts enumeration, denies backend access during reset cleanup and retains its original error.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(7)]
    public Task ExecutorErrorsAbortEnumeratorsWithoutReplacingTheError(int failure)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ExecutorErrorsAbortEnumeratorsWithoutReplacingTheError), async (connection, transaction, token) =>
        {
            int backend = connection.ProcessID;
            var notices = new List<PostgresNotice>();
            connection.Notice += (_, args) => notices.Add(args.Notice);
            await using var command = new NpgsqlCommand("CREATE TEMP TABLE set_cleanup(value int); SELECT set_values.set_reset()", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            await transaction.SaveAsync("set_executor_failure", token);
            int logStart = PostgresFixture.Cluster.ReadServerLog().Length;
            command.CommandText = $"SELECT 1/(v-1) FROM (SELECT set_values.set_probe_streaming(3,{failure},true) AS v) s";
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
            Assert.AreEqual(PostgresErrorCodes.DivisionByZero, error.SqlState);
            await transaction.RollbackAsync("set_executor_failure", token);
            Assert.AreSequenceEqual([1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0], await ReadStatusAsync(command, token));
            Assert.IsEmpty(notices.Where(static notice => notice.SqlState == "P7107"));
            string cleanup = string.Join('\n', PostgresFixture.Cluster.ReadServerLog()[logStart..].Split('\n').Where(line =>
                line.Contains($"ANKUS CLEANUP [backend {backend}]", StringComparison.Ordinal)));
            if (failure == 7)
            {
                Assert.Contains("WARNING: P7107: set Dispose failure", cleanup);
            }
            else
            {
                Assert.IsEmpty(cleanup);
            }

            command.CommandText = "SELECT count(*) FROM set_cleanup";
            Assert.AreEqual(0L, await command.ExecuteScalarAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// Portal close disposes a partially consumed sequence with backend access still available.
    /// </summary>
    [TestMethod]
    public Task ClosingPortalDisposesAbandonedSequence()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ClosingPortalDisposesAbandonedSequence), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("""
                CREATE TEMP TABLE set_cleanup(value int);
                SELECT set_values.set_reset();
                DECLARE set_cursor NO SCROLL CURSOR FOR SELECT set_values.set_probe_streaming(100,0,true)
                """, connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = "FETCH 1 FROM set_cursor";
            Assert.AreEqual(1, await command.ExecuteScalarAsync(token));
            Assert.AreSequenceEqual([1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 1, 0, 0], await ReadStatusAsync(command, token));
            command.CommandText = "CLOSE set_cursor";
            await command.ExecuteNonQueryAsync(token);
            Assert.AreSequenceEqual([1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0], await ReadStatusAsync(command, token));
            command.CommandText = "SELECT count(*) FROM set_cleanup";
            Assert.AreEqual(1L, await command.ExecuteScalarAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// PostgreSQL query cancellation disposes the suspended iterator without leaving a live managed handle.
    /// </summary>
    [TestMethod]
    public async Task CancellationDisposesSuspendedSequence()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await using var command = new NpgsqlCommand("CREATE TEMP TABLE set_cleanup(value int); SELECT set_values.set_reset(); SET statement_timeout = '100ms'", connection);
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT v, pg_sleep(5) FROM (SELECT set_values.set_probe_streaming(100,0,true) AS v) s";
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual(PostgresErrorCodes.QueryCanceled, error.SqlState);
        command.CommandText = "SET statement_timeout = 0";
        await command.ExecuteNonQueryAsync(token);
        Assert.AreSequenceEqual([1, 1, 1, 1, 1, 1, 1, 1, 0, 1, 0, 0, 0], await ReadStatusAsync(command, token));
        command.CommandText = "SELECT array_agg(v) FROM set_values.set_series(40,3) v";
        Assert.AreSequenceEqual([40, 41, 42], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token)));
        Assert.AreEqual(backend, connection.ProcessID);
    }

    /// <summary>
    /// Dispose failures during normal early shutdown are observable errors and cannot cause duplicate disposal on abort.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task EarlyDisposalFailureIsReportedExactlyOnce(bool portal)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EarlyDisposalFailureIsReportedExactlyOnce), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT set_values.set_reset()", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            await transaction.SaveAsync("set_disposal_failure", token);
            if (portal)
            {
                command.CommandText = "DECLARE failing_set NO SCROLL CURSOR FOR SELECT set_values.set_probe_streaming(3,7,false)";
                await command.ExecuteNonQueryAsync(token);
                command.CommandText = "FETCH 1 FROM failing_set";
                Assert.AreEqual(1, await command.ExecuteScalarAsync(token));
            }

            if (portal)
            {
                command.CommandText = "CLOSE failing_set";
            }
            else
            {
                command.CommandText = "SELECT set_values.set_probe_streaming(3,7,false) LIMIT 1";
                Assert.AreEqual(1, await command.ExecuteScalarAsync(token));
                // Binding the next command closes the suspended unnamed portal.
                command.CommandText = "SELECT 1";
            }

            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
            Assert.AreEqual("P7107", error.SqlState);
            await transaction.RollbackAsync("set_disposal_failure", token);
            Assert.AreSequenceEqual([1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0], await ReadStatusAsync(command, token));
        }, context.CancellationToken);

    /// <summary>
    /// Guarded SPI contains nested set errors, rolls back prior writes and retains the outer managed scope and plans.
    /// </summary>
    [TestMethod]
    public Task NestedSetFailuresRecoverInsideSpi()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(NestedSetFailuresRecoverInsideSpi), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT set_values.set_recovery()", connection, transaction);
            Assert.AreEqual("20:20:0:2:0", await command.ExecuteScalarAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// Native output failure releases retained SPI plans and cursor ownership during restricted abort cleanup.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task AbortCleanupReleasesOwnedPlansAndCursors(bool openCursor)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(AbortCleanupReleasesOwnedPlansAndCursors), async (connection, transaction, token) =>
        {
            var notices = new List<PostgresNotice>();
            connection.Notice += (_, args) => notices.Add(args.Notice);
            await using var command = new NpgsqlCommand("SELECT set_values.set_reset()", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            const string resourceCounts = """
                SELECT ARRAY[
                    (SELECT count(*) FROM ankus_test_memory.contexts WHERE name = 'SPI Plan'),
                    (SELECT count(*) FROM pg_cursors)]
                """;
            command.CommandText = resourceCounts;
            long[] baseline = Assert.IsInstanceOfType<long[]>(await command.ExecuteScalarAsync(token));
            for (int index = 0; index < 20; index++)
            {
                await transaction.SaveAsync("set_resource_failure", token);
                command.CommandText = "ALTER TYPE datatype.enum_mood RENAME VALUE 'Low' TO 'renamed'";
                await command.ExecuteNonQueryAsync(token);
                command.CommandText = $"SELECT value::text FROM set_values.set_owned_resources({openCursor}) value";
                PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
                Assert.AreEqual("22P02", error.SqlState);
                await transaction.RollbackAsync("set_resource_failure", token);
                command.CommandText = resourceCounts;
                Assert.AreSequenceEqual(baseline, Assert.IsInstanceOfType<long[]>(await command.ExecuteScalarAsync(token)), $"Resource counts after abort {index}.");
            }

            int[] counters = await ReadStatusAsync(command, token);
            Assert.AreEqual(20, counters[12]);
            Assert.IsEmpty(notices.Where(static notice => notice.InvariantSeverity == "WARNING" && notice.MessageText.Contains("iterator disposal", StringComparison.Ordinal)));
            command.CommandText = $"SELECT value::text FROM set_values.set_owned_resources({openCursor}) value";
            Assert.AreEqual("Low", await command.ExecuteScalarAsync(token));
            command.CommandText = resourceCounts;
            Assert.AreSequenceEqual(baseline, Assert.IsInstanceOfType<long[]>(await command.ExecuteScalarAsync(token)));
        }, context.CancellationToken);

    /// <summary>
    /// A caught raw cleanup error cannot leak retained SPI resources or recovery frames during implicit transaction cleanup.
    /// </summary>
    /// <param name="openCursor">Whether the iterator owns a portal as well as its plan.</param>
    /// <param name="executorFailure">Whether an executor error precedes the cleanup error.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task NativeIteratorCleanupErrorsReleaseOwnedResourcesAcrossImplicitAborts(bool openCursor, bool executorFailure)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        const string resources = """
            SELECT ARRAY[
                (SELECT count(*) FROM ankus_test_memory.contexts WHERE name = 'SPI Plan'),
                (SELECT count(*) FROM pg_cursors)]
            """;
        await using var command = new NpgsqlCommand("SELECT set_values.set_reset()", connection);
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = resources;
        long[] baseline = Assert.IsInstanceOfType<long[]>(await command.ExecuteScalarAsync(token));
        string source = $"set_values.set_owned_resources_with_cleanup_error({openCursor})";
        for (int index = 0; index < 10; index++)
        {
            command.CommandText = executorFailure
                ? $"SELECT 1/(CASE WHEN value='Low'::datatype.enum_mood THEN 0 ELSE 1 END) FROM (SELECT {source} AS value) input"
                : $"SELECT value::text FROM {source} value";
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
            Assert.AreEqual(executorFailure ? "22012" : "42704", error.SqlState);
            Assert.AreEqual(executorFailure ? "division by zero" :
                "ExtensibleNodeMethods \"ankus_missing_iterator_cleanup\" was not registered", error.MessageText);
            command.CommandText = "SELECT set_values.set_status()";
            int[] status = Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token));
            Assert.AreEqual(index + 1, status[12], "Each acquired iterator must execute its finally block once.");
            Assert.AreEqual(executorFailure ? 0 : index + 1, status[8], "Ordinary cleanup still permits SPI queries.");
            Assert.AreEqual(executorFailure ? index + 1 : 0, status[9], "Executor abort must use restricted cleanup.");
            Assert.AreEqual(0, status[10]);
            Assert.AreEqual(0, status[11]);
            command.CommandText = "SELECT set_values.set_native_cleanup_caught()";
            Assert.AreEqual(index + 1, await command.ExecuteScalarAsync(token));
            command.CommandText = resources;
            Assert.AreSequenceEqual(baseline, Assert.IsInstanceOfType<long[]>(await command.ExecuteScalarAsync(token)),
                $"Retained SPI resources after failure {index}.");
            command.CommandText = "SELECT set_values.set_interrupt_state()";
            Assert.AreSequenceEqual([0, 0], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token)));
            command.CommandText = "SELECT 42";
            Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
            Assert.AreEqual(backend, connection.ProcessID);
        }

        command.CommandText = $"SELECT value::text FROM set_values.set_owned_resources({openCursor}) value";
        Assert.AreEqual("Low", await command.ExecuteScalarAsync(token));
        command.CommandText = "SELECT set_values.set_status()";
        Assert.AreEqual(11, Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token))[12]);
        command.CommandText = "SELECT set_values.set_native_cleanup_caught()";
        Assert.AreEqual(10, await command.ExecuteScalarAsync(token));
        command.CommandText = resources;
        Assert.AreSequenceEqual(baseline, Assert.IsInstanceOfType<long[]>(await command.ExecuteScalarAsync(token)));
        Assert.AreEqual(backend, connection.ProcessID);
    }

    /// <summary>
    /// Abort cleanup releases an adopted parent-transaction cursor that PostgreSQL's subtransaction cleanup would preserve.
    /// </summary>
    [TestMethod]
    public Task AbortCleanupReleasesAdoptedParentCursor()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(AbortCleanupReleasesAdoptedParentCursor), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT set_values.set_reset(); DECLARE set_parent_cursor CURSOR FOR SELECT 1", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_cursors WHERE name = 'set_parent_cursor')";
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            await transaction.SaveAsync("set_parent_failure", token);
            command.CommandText = "ALTER TYPE datatype.enum_mood RENAME VALUE 'Low' TO 'renamed'";
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = "SELECT value::text FROM set_values.set_existing_cursor('set_parent_cursor') value";
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
            Assert.AreEqual("22P02", error.SqlState);
            await transaction.RollbackAsync("set_parent_failure", token);
            command.CommandText = "SELECT EXISTS (SELECT 1 FROM pg_cursors WHERE name = 'set_parent_cursor')";
            Assert.IsFalse(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            int[] counters = await ReadStatusAsync(command, token);
            Assert.AreEqual(1, counters[12]);
        }, context.CancellationToken);

    /// <summary>
    /// Lateral arguments, ordinality and repeated executor scans keep independent iteration positions.
    /// </summary>
    [TestMethod]
    public Task LateralOrdinalityAndRescansPreserveRows()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(LateralOrdinalityAndRescansPreserveRows), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT array_agg(i * 100 + v ORDER BY i, v)
                FROM generate_series(1,3) i CROSS JOIN LATERAL set_values.set_series(i,2) v
                """, connection, transaction);
            Assert.AreSequenceEqual([101, 102, 202, 203, 303, 304], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token)));
            command.CommandText = "SELECT array_agg(v*10+ordinality ORDER BY ordinality) FROM set_values.set_series(4,3) WITH ORDINALITY s(v,ordinality)";
            Assert.AreSequenceEqual([41L, 52L, 63L], Assert.IsInstanceOfType<long[]>(await command.ExecuteScalarAsync(token)));
            command.CommandText = "SELECT array_agg(a.v*10+b.v ORDER BY a.v,b.v) FROM set_values.set_series(1,2) a(v), set_values.set_series(1,2) b(v)";
            Assert.AreSequenceEqual([11, 12, 21, 22], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token)));
        }, context.CancellationToken);

    /// <summary>
    /// Multiple suspended iterators retain independent handles until each portal closes.
    /// </summary>
    [TestMethod]
    public Task InterleavedPortalsResumeIndependentEnumerators()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(InterleavedPortalsResumeIndependentEnumerators), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT set_values.set_reset();
                DECLARE first_set NO SCROLL CURSOR FOR SELECT set_values.set_probe_streaming(3,0,false);
                DECLARE second_set NO SCROLL CURSOR FOR SELECT set_values.set_probe_streaming(5,0,false)
                """, connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = "FETCH 1 FROM first_set";
            Assert.AreEqual(1, await command.ExecuteScalarAsync(token));
            command.CommandText = "FETCH 1 FROM second_set";
            Assert.AreEqual(1, await command.ExecuteScalarAsync(token));
            command.CommandText = "FETCH 1 FROM first_set";
            Assert.AreEqual(2, await command.ExecuteScalarAsync(token));
            Assert.AreSequenceEqual([2, 2, 2, 2, 3, 3, 0, 0, 0, 0, 2, 0, 0], await ReadStatusAsync(command, token));
            command.CommandText = "CLOSE first_set; CLOSE second_set";
            await command.ExecuteNonQueryAsync(token);
            Assert.AreSequenceEqual([2, 2, 2, 2, 3, 3, 2, 2, 0, 0, 0, 0, 0], await ReadStatusAsync(command, token));
        }, context.CancellationToken);

    /// <summary>
    /// Text, enum and shaped-array arguments remain owned after the original callback and intervening SPI buffers end.
    /// </summary>
    [TestMethod]
    public Task SuspendedArgumentsAndSpiPlansRetainValues()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(SuspendedArgumentsAndSpiPlansRetainValues), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT row_index, payload = repeat('café',10000), mood::text,
                    array_send("values") = array_send('[0:1][-3:-2]={{Low,NULL},{High,café}}'::datatype.enum_mood[])
                FROM set_values.set_retained(repeat('café',10000),'Medium','[0:1][-3:-2]={{Low,NULL},{High,café}}',3)
                """, connection, transaction);
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
            {
                for (int index = 0; index < 3; index++)
                {
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.AreEqual(index, reader.GetInt32(0));
                    Assert.IsTrue(reader.GetBoolean(1));
                    Assert.AreEqual("Medium", reader.GetString(2));
                    Assert.IsTrue(reader.GetBoolean(3));
                }

                Assert.IsFalse(await reader.ReadAsync(token));
            }

            command.CommandText = "SELECT row_index, value::text FROM set_values.set_spi(4)";
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
            {
                for (int index = 0; index < 4; index++)
                {
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.AreEqual(index, reader.GetInt32(0));
                    Assert.AreEqual(index % 2 == 0 ? "café" : "Medium", reader.GetString(1));
                }

                Assert.IsFalse(await reader.ReadAsync(token));
            }

            command.CommandText = "SELECT set_values.set_reset()";
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = "SELECT set_values.set_series(1,100) LIMIT 1";
            Assert.AreEqual(1, await command.ExecuteScalarAsync(token));
            int[] counters = await ReadStatusAsync(command, token);
            Assert.AreEqual(1, counters[12], "Compiler-generated iterator finally must run on LIMIT.");
        }, context.CancellationToken);

    /// <summary>
    /// Forced materialization spills its tuple store under work_mem and returns every exact value.
    /// </summary>
    [TestMethod]
    public Task MaterializedTuplestoreSpillsAndPreservesEveryRow()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(MaterializedTuplestoreSpillsAndPreservesEveryRow), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SET LOCAL work_mem = '64kB'", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            const string query = "SELECT count(*), sum(row_id), sum(octet_length(payload)), bool_and(payload = repeat('x',1024)) FROM set_values.set_materialized(2000,1024)";
            command.CommandText = query;
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
            {
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(2000L, reader.GetInt64(0));
                Assert.AreEqual(2001000L, reader.GetInt64(1));
                Assert.AreEqual(2048000L, reader.GetInt64(2));
                Assert.IsTrue(reader.GetBoolean(3));
            }

            command.CommandText = $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON, TIMING OFF) {query}";
            string plan = Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token));
            using JsonDocument document = JsonDocument.Parse(plan);
            JsonElement scan = document.RootElement[0].GetProperty("Plan").GetProperty("Plans")[0];
            Assert.AreEqual("Function Scan", scan.GetProperty("Node Type").GetString());
            Assert.IsGreaterThan(0, scan.GetProperty("Temp Written Blocks").GetInt64(), plan);
            Assert.AreEqual(2000d, scan.GetProperty("Actual Rows").GetDouble());
        }, context.CancellationToken);

    /// <summary>
    /// Native interrupt checks cancel a materialized iterator whose MoveNext never calls PostgreSQL itself.
    /// </summary>
    [TestMethod]
    public async Task PureManagedMaterializationObservesCancellation()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await using var command = new NpgsqlCommand("SELECT set_values.set_reset(); SET work_mem = '64kB'; SET statement_timeout = '100ms'", connection);
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT count(*) FROM set_values.set_managed_flood()";
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual(PostgresErrorCodes.QueryCanceled, error.SqlState);
        command.CommandText = "SET statement_timeout = 0";
        await command.ExecuteNonQueryAsync(token);
        int[] counters = await ReadStatusAsync(command, token);
        Assert.AreEqual(1, counters[12]);
        command.CommandText = "SELECT set_values.set_flood_rows()";
        int rows = Assert.IsInstanceOfType<int>(await command.ExecuteScalarAsync(token));
        Assert.IsGreaterThan(0, rows);
        Assert.IsLessThan(int.MaxValue, rows);
        command.CommandText = "SELECT array_agg(value) FROM set_values.set_series(40,3) value";
        Assert.AreSequenceEqual([40, 41, 42], Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(token)));
        Assert.AreEqual(backend, connection.ProcessID);
    }

    /// <summary>
    /// Numeric precision rounds each produced value, preserves NULL rows and rejects overflow after earlier rows.
    /// </summary>
    [TestMethod]
    public Task SetNumericPrecisionRoundsNullsAndRecoversFromOverflow()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(SetNumericPrecisionRoundsNullsAndRecoversFromOverflow), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT array_agg(value::text), ARRAY[(12.345::numeric(4,2))::text,NULL,(-99.994::numeric(4,2))::text]
                FROM set_values.set_precision(12.345,-99.994) value
                """, connection, transaction);
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
            {
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreSequenceEqual(["12.35", null, "-99.99"], reader.GetFieldValue<string?[]>(0));
                Assert.AreSequenceEqual(reader.GetFieldValue<string?[]>(1), reader.GetFieldValue<string?[]>(0));
            }

            command.CommandText = "SELECT set_values.set_reset()";
            await command.ExecuteNonQueryAsync(token);
            await transaction.SaveAsync("set_numeric_failure", token);
            command.CommandText = "SELECT array_agg(value) FROM set_values.set_precision(1.234,99.995) value";
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
            Assert.AreEqual(PostgresErrorCodes.NumericValueOutOfRange, error.SqlState);
            await transaction.RollbackAsync("set_numeric_failure", token);
            int[] counters = await ReadStatusAsync(command, token);
            Assert.AreEqual(1, counters[12]);
            command.CommandText = "SELECT array_agg(value::text) FROM set_values.set_precision(NULL,1.235) value";
            Assert.AreSequenceEqual([null, null, "1.24"], Assert.IsInstanceOfType<string?[]>(await command.ExecuteScalarAsync(token)));
        }, context.CancellationToken);

    /// <summary>
    /// Per-row native allocation stays bounded while distinct large rows spill into a PostgreSQL tuple store.
    /// </summary>
    [TestMethod]
    public Task MaterializedRowContextsRemainBoundedAcrossSpill()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(MaterializedRowContextsRemainBoundedAcrossSpill), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT set_values.set_reset(); SET LOCAL work_mem = '64kB'", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            const string query = "SELECT count(*),sum(row_id),sum(octet_length(payload)),bool_and(left(payload,length(row_id::text))=row_id::text) FROM set_values.set_memory_bounded(512,32768)";
            command.CommandText = query;
            await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
            {
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(512L, reader.GetInt64(0));
                Assert.AreEqual(131328L, reader.GetInt64(1));
                Assert.AreEqual(16777216L, reader.GetInt64(2));
                Assert.IsTrue(reader.GetBoolean(3));
            }

            command.CommandText = $"EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON, TIMING OFF) {query}";
            string plan = Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token));
            using JsonDocument document = JsonDocument.Parse(plan);
            JsonElement scan = document.RootElement[0].GetProperty("Plan").GetProperty("Plans")[0];
            Assert.AreEqual("Function Scan", scan.GetProperty("Node Type").GetString());
            Assert.IsGreaterThan(0, scan.GetProperty("Temp Written Blocks").GetInt64(), plan);
            command.CommandText = "SELECT set_values.set_maximum_row_bytes()";
            long maximum = Assert.IsInstanceOfType<long>(await command.ExecuteScalarAsync(token));
            Assert.IsGreaterThan(0L, maximum);
            Assert.IsLessThanOrEqualTo(262144L, maximum, "A row context must not retain the prior16MiB of materialized values.");
        }, context.CancellationToken);

    /// <summary>
    /// Failures during managed or native row conversion dispose the suspended iterator at the appropriate boundary.
    /// </summary>
    [TestMethod]
    [DataRow("set_enum_streaming", false)]
    [DataRow("set_enum_streaming", true)]
    [DataRow("set_enum_materialized", false)]
    [DataRow("set_enum_materialized", true)]
    public Task RowConversionFailuresReleaseEnumerator(string function, bool nativeFailure)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(RowConversionFailuresReleaseEnumerator), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("CREATE TEMP TABLE set_cleanup(value int); SELECT set_values.set_reset()", connection, transaction);
            await command.ExecuteNonQueryAsync(token);
            await transaction.SaveAsync("set_row_failure", token);
            if (nativeFailure)
            {
                command.CommandText = "ALTER TYPE datatype.enum_mood RENAME VALUE 'Low' TO 'renamed'";
                await command.ExecuteNonQueryAsync(token);
            }

            command.CommandText = $"SELECT array_agg(v::text) FROM set_values.{function}({!nativeFailure}) v";
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
            Assert.AreEqual(nativeFailure ? "22P02" : "38000", error.SqlState);
            await transaction.RollbackAsync("set_row_failure", token);
            int rows = nativeFailure ? 1 : 2;
            Assert.AreSequenceEqual([1, 1, 1, 1, rows, rows, 1, 1, nativeFailure ? 0 : 1, nativeFailure ? 1 : 0, 0, 0, 0], await ReadStatusAsync(command, token));
            command.CommandText = "SELECT count(*) FROM set_values.set_enum_streaming(false)";
            Assert.AreEqual(3L, await command.ExecuteScalarAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// Set-row conversion preserves backend binary representations across all distinct native datum families.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    public Task TypedSetColumnsPreserveExactNativeValues(int scenario)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(TypedSetColumnsPreserveExactNativeValues), async (connection, transaction, token) =>
        {
            string values = scenario switch
            {
                0 => "'-infinity'::date, '294000-01-01 12:34:56.123456+00'::timestamptz, '13 mons -3 days 00:00:00.000001'::interval, '[1,42)'::int4range, '(-0,NaN)'::point, '-0'::float8, decode('00ff007f','hex'), '2001:db8::1234/48'::inet, '{\"z\":null,\"a\":[1,true]}'::jsonb",
                1 => "'0001-01-01 BC'::date, '-infinity'::timestamptz, '-00:00:00.000001'::interval, 'empty'::int4range, '(-1.5,2.25)'::point, 'NaN'::float8, ''::bytea, '192.0.2.42/24'::inet, '[null,1.2300]'::jsonb",
                2 => "NULL::date, NULL::timestamptz, NULL::interval, NULL::int4range, NULL::point, NULL::float8, NULL::bytea, NULL::inet, NULL::jsonb",
                _ => throw new ArgumentOutOfRangeException(nameof(scenario)),
            };
            await using var command = new NpgsqlCommand($"""
                WITH source(d,t,i,r,p,f,b,a,j) AS (SELECT {values})
                SELECT count(*),
                    bool_and(date_send(result.date_value) IS NOT DISTINCT FROM date_send(source.d)),
                    bool_and(timestamptz_send(result.instant) IS NOT DISTINCT FROM timestamptz_send(source.t)),
                    bool_and(interval_send(result.interval_value) IS NOT DISTINCT FROM interval_send(source.i)),
                    bool_and(range_send(result.range_value) IS NOT DISTINCT FROM range_send(source.r)),
                    bool_and(point_send(result.point_value) IS NOT DISTINCT FROM point_send(source.p)),
                    bool_and(float8send(result.floating_value) IS NOT DISTINCT FROM float8send(source.f)),
                    bool_and(result.bytes IS NOT DISTINCT FROM source.b),
                    bool_and(inet_send(result.address) IS NOT DISTINCT FROM inet_send(source.a)),
                    bool_and(jsonb_send(result.json) IS NOT DISTINCT FROM jsonb_send(source.j))
                FROM source CROSS JOIN LATERAL set_values.set_datums(d,t,i,r,p,f,b,a,j) result
                """, connection, transaction);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.IsTrue(await reader.ReadAsync(token));
            Assert.AreEqual(2L, reader.GetInt64(0));
            for (int column = 1; column <= 9; column++)
            {
                Assert.IsTrue(reader.GetBoolean(column), $"Native datum family {column} in scenario {scenario}.");
            }
        }, context.CancellationToken);

    /// <summary>
    /// Copies backend-local counters after the preceding executor and its cleanup have completed.
    /// </summary>
    private static async Task<int[]> ReadStatusAsync(NpgsqlCommand command, CancellationToken cancellationToken)
    {
        command.CommandText = "SELECT set_values.set_status()";
        return Assert.IsInstanceOfType<int[]>(await command.ExecuteScalarAsync(cancellationToken));
    }
}
