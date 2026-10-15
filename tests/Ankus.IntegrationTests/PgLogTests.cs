using System.Net.Sockets;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies PostgreSQL severity, routing, structured diagnostics, recovery, and terminal reporting in real backends.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
public sealed partial class PgLogTests(TestContext context)
{
    /// <summary>
    /// Verifies every nonterminal level's actual wire severity and default SQLSTATE, including server-only suppression.
    /// </summary>
    /// <param name="level">The managed level.</param>
    /// <param name="severity">The expected PostgreSQL severity.</param>
    /// <param name="state">The default SQLSTATE.</param>
    [TestMethod]
    [DataRow(0, "DEBUG", "00000")]
    [DataRow(1, "DEBUG", "00000")]
    [DataRow(2, "DEBUG", "00000")]
    [DataRow(3, "DEBUG", "00000")]
    [DataRow(4, "DEBUG", "00000")]
    [DataRow(5, "LOG", "00000")]
    [DataRow(6, "LOG", "00000")]
    [DataRow(7, "INFO", "00000")]
    [DataRow(8, "NOTICE", "00000")]
    [DataRow(9, "WARNING", "01000")]
    public Task NonterminalLevelsUsePostgresRouting(int level, string severity, string state)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(NonterminalLevelsUsePostgresRouting),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SET LOCAL client_min_messages = debug5; SET LOCAL log_min_messages = debug5",
                    connection, transaction);
                await command.ExecuteNonQueryAsync(token);
                var notices = new List<PostgresNotice>();
                connection.Notice += (_, args) => notices.Add(args.Notice);
                string marker = "literal %s %n café 🐘 " + Guid.NewGuid().ToString("N");
                command.CommandText = "SELECT datatype.log_message($1, $2)";
                command.Parameters.AddWithValue(level);
                command.Parameters.AddWithValue(marker);
                Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
                PostgresNotice[] matching = [.. notices.Where(notice => notice.MessageText == marker)];
                Assert.HasCount(level == 6 ? 0 : 1, matching);
                if (level != 6)
                {
                    Assert.AreEqual(severity, matching[0].InvariantSeverity);
                    Assert.AreEqual(state, matching[0].SqlState);
                }

                Assert.Contains(severity + ":  " + marker, PostgresFixture.Cluster.ReadServerLog());
            }, context.CancellationToken);

    /// <summary>
    /// An error raised with a message domain reaches the client, and PostgreSQL records the domain in the ErrorData a
    /// caller catches, as pgrx's ereport_domain tests require; backend errors carry PostgreSQL's own domain.
    /// </summary>
    [TestMethod]
    public Task ErrorsCarryTheirMessageDomain()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ErrorsCarryTheirMessageDomain),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT datatype.catch_domain('SELECT datatype.raise_domain()')", connection, transaction);
                Assert.AreEqual("XX000|ereport error|test_extension_domain", await command.ExecuteScalarAsync(token));
                command.CommandText = "SELECT datatype.catch_domain('SELECT 1 / 0')";
                Assert.AreEqual($"22012|division by zero|postgres-{PostgresFixture.Cluster.Installation.Version.Major}",
                    await command.ExecuteScalarAsync(token));
                command.CommandText = "SELECT datatype.raise_domain()";
                PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteNonQueryAsync(token));
                Assert.AreEqual(PostgresErrorCodes.InternalError, error.SqlState);
                Assert.AreEqual("ereport error", error.MessageText);
            }, context.CancellationToken);

    /// <summary>
    /// An ERROR from an extension function reaches the calling C handler with ErrorContext current, as PostgreSQL's own
    /// errors do. Handlers that restore the context they caught an error in before rethrowing, as logical decoding's
    /// does after deleting other contexts, depend on it.
    /// </summary>
    [TestMethod]
    public Task ErrorsReachHandlersInErrorContext()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ErrorsReachHandlersInErrorContext),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand(
                    "CREATE FUNCTION pg_temp.native_failure() RETURNS integer LANGUAGE sql AS $$ SELECT 1 / 0 $$", connection, transaction);
                await command.ExecuteNonQueryAsync(token);
                command.CommandText = "SELECT tests.error_arrival('pg_temp.native_failure()'::regprocedure)";
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
                command.CommandText = "SELECT tests.error_arrival('datatype.raise_domain()'::regprocedure)";
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
                command.CommandText = "SELECT 42";
                Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Interpolated messages evaluate their expressions only for enabled levels: DEBUG levels are disabled at
    /// PostgreSQL's default client threshold, WARNING is enabled, and enabling DEBUG1 evaluates and delivers it.
    /// </summary>
    [TestMethod]
    public Task InterpolatedMessagesSkipDisabledLevels()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(InterpolatedMessagesSkipDisabledLevels),
            async (connection, transaction, token) =>
            {
                var notices = new List<PostgresNotice>();
                connection.Notice += (_, args) => notices.Add(args.Notice);
                await using var command = new NpgsqlCommand(
                    "SELECT array_agg(datatype.log_enabled(level) ORDER BY level) FROM generate_series(0, 4) AS level", connection, transaction);
                Assert.AreSequenceEqual<bool>([false, false, false, false, false], Assert.IsInstanceOfType<bool[]>(await command.ExecuteScalarAsync(token)));
                command.CommandText = "SELECT datatype.log_enabled(9)";
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
                command.CommandText = "SELECT datatype.log_lazy(4)";
                Assert.AreEqual(0, await command.ExecuteScalarAsync(token));
                Assert.IsEmpty(notices);
                command.CommandText = "SELECT datatype.log_lazy(9)";
                Assert.AreEqual(1, await command.ExecuteScalarAsync(token));
                Assert.AreEqual("WARNING|lazy 1", $"{Assert.ContainsSingle(notices).Severity}|{notices[0].MessageText}");
                notices.Clear();
                command.CommandText = "SET LOCAL client_min_messages = debug1";
                await command.ExecuteNonQueryAsync(token);
                command.CommandText = "SELECT datatype.log_lazy(4)";
                Assert.AreEqual(1, await command.ExecuteScalarAsync(token));
                Assert.AreEqual("DEBUG|lazy 1", $"{Assert.ContainsSingle(notices).Severity}|{notices[0].MessageText}");
            }, context.CancellationToken);

    /// <summary>
    /// Verifies filtering treats INFO and LOG specially rather than relying on a single numeric threshold.
    /// </summary>
    [TestMethod]
    public Task FilteringMatchesClientAndServerRules()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(FilteringMatchesClientAndServerRules),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SET LOCAL client_min_messages = error; SET LOCAL log_min_messages = log",
                    connection, transaction);
                await command.ExecuteNonQueryAsync(token);
                var notices = new List<PostgresNotice>();
                connection.Notice += (_, args) => notices.Add(args.Notice);
                command.CommandText = "SELECT array_agg(datatype.log_enabled(level) ORDER BY level) FROM generate_series(0, 12) AS level";
                Assert.AreSequenceEqual<bool>([false, false, false, false, false, true, true, true, false, false, true, true, true],
                    Assert.IsInstanceOfType<bool[]>(await command.ExecuteScalarAsync(token)));
                command.CommandText = "SELECT datatype.log_message(7, 'always info'), datatype.log_message(9, 'filtered warning')";
                await command.ExecuteNonQueryAsync(token);
                Assert.HasCount(1, notices);
                Assert.AreEqual("always info", notices[0].MessageText);
                command.CommandText = "SET LOCAL log_min_messages = panic; SET LOCAL client_min_messages = debug5";
                await command.ExecuteNonQueryAsync(token);
                command.CommandText = "SELECT datatype.log_enabled(6)";
                Assert.IsFalse(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            }, context.CancellationToken);

    /// <summary>
    /// Verifies complete structured diagnostics and server-only detail survive without truncation or session damage.
    /// </summary>
    /// <param name="helper">Whether to use the structured warning helper.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task StructuredNoticePreservesFieldsAndLongUnicode(bool helper)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(StructuredNoticePreservesFieldsAndLongUnicode),
            async (connection, transaction, token) =>
            {
                string text = string.Concat(Enumerable.Repeat("é🐘%s", 2000));
                var notices = new List<PostgresNotice>();
                connection.Notice += (_, args) => notices.Add(args.Notice);
                await using var command = new NpgsqlCommand("SELECT datatype.log_diagnostic($1, $2)", connection, transaction);
                command.Parameters.AddWithValue(text);
                command.Parameters.AddWithValue(helper);
                Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
                Assert.HasCount(1, notices);
                PostgresNotice notice = notices[0];
                Assert.AreEqual("WARNING", notice.InvariantSeverity);
                Assert.AreEqual("01P01", notice.SqlState);
                Assert.AreEqual(text, notice.MessageText);
                Assert.AreEqual("client détail", notice.Detail);
                Assert.AreEqual("retry 🐘", notice.Hint);
                Assert.AreEqual("managed context", notice.Where);
                Assert.AreEqual("schéma", notice.SchemaName);
                Assert.AreEqual("t", notice.TableName);
                Assert.AreEqual("c", notice.ColumnName);
                Assert.AreEqual("custom_type", notice.DataTypeName);
                Assert.AreEqual("constraint_name", notice.ConstraintName);
                Assert.AreEqual(3, notice.Position);
                Assert.AreEqual(2, notice.InternalPosition);
                Assert.AreEqual("SELECT 1", notice.InternalQuery);
                Assert.AreEqual("logging.cs", notice.File);
                Assert.AreEqual("42", notice.Line);
                Assert.AreEqual("LogDiagnostic", notice.Routine);
                Assert.Contains("DETAIL:  server-only détail", PostgresFixture.Cluster.ReadServerLog());
            }, context.CancellationToken);

    /// <summary>
    /// Verifies managed ERROR handling can recover, while an unhandled error unwinds finally and rolls back writes.
    /// </summary>
    [TestMethod]
    public async Task ErrorUnwindsAndRemainsCatchable()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await using var command = new NpgsqlCommand("SELECT datatype.log_catch_error()", connection);
        Assert.AreEqual("22023|caught error|detail|hint|42", await command.ExecuteScalarAsync(token));
        command.CommandText = "CREATE TEMP TABLE log_rollback(value int)";
        await command.ExecuteNonQueryAsync(token);
        var notices = new List<PostgresNotice>();
        connection.Notice += (_, args) => notices.Add(args.Notice);
        command.CommandText = "SELECT datatype.log_terminal(10, 'error marker')";
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual("P0001", error.SqlState);
        Assert.AreEqual("ERROR", error.InvariantSeverity);
        Assert.AreEqual("terminal detail", error.Detail);
        Assert.AreEqual("error marker", error.MessageText);
        Assert.HasCount(1, notices);
        Assert.AreEqual("finally error marker", notices[0].MessageText);
        command.CommandText = "SELECT count(*) FROM log_rollback";
        Assert.AreEqual(0L, await command.ExecuteScalarAsync(token));
        Assert.AreEqual(backend, connection.ProcessID);
    }

    /// <summary>
    /// Verifies argument failures preserve the native backend and diagnostic buffers are released on partial encoding failures.
    /// </summary>
    /// <param name="mode">The invalid input case.</param>
    /// <param name="expected">The managed rejection type.</param>
    [TestMethod]
    [DataRow(0, "ArgumentOutOfRangeException")]
    [DataRow(1, "ArgumentOutOfRangeException")]
    [DataRow(2, "ArgumentException")]
    [DataRow(3, "ArgumentException")]
    [DataRow(4, "ArgumentException")]
    [DataRow(5, "EncoderFallbackException")]
    [DataRow(6, "EncoderFallbackException")]
    [DataRow(7, "ArgumentNullException")]
    public Task InvalidReportsPreserveBackend(int mode, string expected)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(InvalidReportsPreserveBackend),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT datatype.log_invalid($1)", connection, transaction);
                command.Parameters.AddWithValue(mode);
                Assert.AreEqual(expected + "|42", await command.ExecuteScalarAsync(token));
                command.Parameters.Clear();
                command.CommandText = "SELECT datatype.log_worker()";
                Assert.AreEqual("PostgreSQL APIs can only be used on the active PostgreSQL backend thread.",
                    await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Verifies repeated reports reclaim native temporary memory before the outer transaction finishes.
    /// </summary>
    [TestMethod]
    public Task ReportingReclaimsOperationContexts()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ReportingReclaimsOperationContexts),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT datatype.log_context_growth()", connection, transaction);
                Assert.AreEqual(0L, await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Verifies native report encoding errors are caught inside the guard and the surrounding SPI session remains usable.
    /// </summary>
    [TestMethod]
    public async Task Latin1ReportingRecoversFromUnrepresentableText()
    {
        CancellationToken token = context.CancellationToken;
        string database = "log_latin1_" + Guid.NewGuid().ToString("N");
        await using NpgsqlConnection administrator = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await using (var create = new NpgsqlCommand(
            $"CREATE DATABASE {database} TEMPLATE template0 ENCODING 'LATIN1' LC_COLLATE 'C' LC_CTYPE 'C'", administrator))
        {
            await create.ExecuteNonQueryAsync(token);
        }

        try
        {
            var builder = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Database = database };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(token);
            await using var command = new NpgsqlCommand("CREATE EXTENSION ankus_test", connection);
            await command.ExecuteNonQueryAsync(token);
            var notices = new List<PostgresNotice>();
            connection.Notice += (_, args) => notices.Add(args.Notice);
            command.CommandText = "SELECT log_invalid(8)";
            Assert.AreEqual("22P05|42", await command.ExecuteScalarAsync(token));
            Assert.IsEmpty(notices);
            command.CommandText = "SELECT log_message(9, 'café')";
            Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
            Assert.HasCount(1, notices);
            Assert.AreEqual("café", notices[0].MessageText);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", administrator);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Verifies FATAL closes just its backend, PANIC initiates crash recovery, and both execute managed finally blocks first.
    /// </summary>
    /// <param name="level">The terminal severity.</param>
    /// <param name="severity">The expected wire severity.</param>
    /// <param name="mode">The managed propagation or swallowing path.</param>
    /// <param name="helper">Whether to use the structured terminal helper.</param>
    [TestMethod]
    [DataRow(11, "FATAL", 0, false)]
    [DataRow(12, "PANIC", 0, false)]
    [DataRow(11, "FATAL", 1, false)]
    [DataRow(12, "PANIC", 1, false)]
    [DataRow(11, "FATAL", 2, false)]
    [DataRow(12, "PANIC", 2, false)]
    [DataRow(11, "FATAL", 0, true)]
    [DataRow(12, "PANIC", 0, true)]
    [DataRow(11, "FATAL", 1, true)]
    [DataRow(12, "PANIC", 1, true)]
    [DataRow(11, "FATAL", 2, true)]
    [DataRow(12, "PANIC", 2, true)]
    public async Task TerminalLevelsUnwindBeforeNativeTermination(int level, string severity, int mode, bool helper)
    {
        CancellationToken token = context.CancellationToken;
        using IDisposable recoverySlot = await CrashRecovery.ReserveAsync(token);
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(token);
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using NpgsqlConnection observer = await cluster.OpenConnectionAsync(token);
        await using (var setup = new NpgsqlCommand("CREATE EXTENSION ankus_test; CREATE TABLE log_rollback(value int)", observer))
        {
            await setup.ExecuteNonQueryAsync(token);
        }

        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        var notices = new List<PostgresNotice>();
        connection.Notice += (_, args) => notices.Add(args.Notice);
        string marker = "terminal-" + Guid.NewGuid().ToString("N");
        await using (var identify = new NpgsqlCommand($"SET application_name = '{marker}'; SET log_error_verbosity = verbose", connection))
        {
            await identify.ExecuteNonQueryAsync(token);
        }

        await using var command = new NpgsqlCommand("SELECT log_terminal($1, $2, $3, $4)", connection);
        command.Parameters.AddWithValue(level);
        command.Parameters.AddWithValue(marker);
        command.Parameters.AddWithValue(mode);
        command.Parameters.AddWithValue(helper);
        NpgsqlException terminalFailure = await Assert.ThrowsAsync<NpgsqlException>(() => command.ExecuteScalarAsync(token));
        if (terminalFailure is PostgresException error)
        {
            Assert.AreEqual(severity, error.InvariantSeverity);
            Assert.AreEqual("P0001", error.SqlState);
            Assert.AreEqual(marker, error.MessageText);
            Assert.AreEqual("finally " + marker, Assert.ContainsSingle(notices).MessageText);
        }
        else
        {
            Assert.IsTrue(OperatingSystem.IsWindows());
            IOException transport = Assert.IsInstanceOfType<IOException>(terminalFailure.InnerException);
            SocketException socket = Assert.IsInstanceOfType<SocketException>(transport.InnerException);
            Assert.AreEqual(SocketError.ConnectionReset, socket.SocketErrorCode);
            if (notices.Count != 0)
            {
                Assert.AreEqual("finally " + marker, Assert.ContainsSingle(notices).MessageText);
            }
        }

        string log = string.Join('\n', cluster.ReadServerLog().Split('\n')
            .Where(line => line.Contains($"[{marker}]:", StringComparison.Ordinal)));
        string terminal = severity + ":  P0001: " + marker;
        Assert.Contains(terminal, log);
        Assert.Contains("finally " + marker, log);
        Assert.IsLessThan(log.IndexOf(terminal, StringComparison.Ordinal), log.IndexOf("finally " + marker, StringComparison.Ordinal));
        Assert.AreEqual(System.Data.ConnectionState.Closed, connection.State);

        if (level == 11)
        {
            await using var query = new NpgsqlCommand("SELECT count(*) FROM log_rollback", observer);
            Assert.AreEqual(0L, await query.ExecuteScalarAsync(token));
        }
        else
        {
            using CancellationTokenSource deadline = CrashRecovery.CreateDeadline(token);
            string recoveredLog = await CrashRecovery.WaitAsync(cluster, deadline.Token);
            Assert.DoesNotContain("the database system is in recovery mode", recoveredLog);
            Assert.DoesNotContain("the database system is not yet accepting connections", recoveredLog);
            await using NpgsqlConnection recovered = await cluster.OpenConnectionAsync(deadline.Token);
            await using var query = new NpgsqlCommand("SELECT count(*) FROM log_rollback", recovered);
            Assert.AreEqual(0L, await query.ExecuteScalarAsync(deadline.Token));
            Assert.Contains("reinitializing", recoveredLog);
        }
    }
}
