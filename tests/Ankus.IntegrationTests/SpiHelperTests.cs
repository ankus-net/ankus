using System.Text.Json;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies native quoting, owned tuple edits, and JSON EXPLAIN behavior against the backend's actual rules and settings.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
public sealed class SpiHelperTests(TestContext context)
{
    private static readonly string[] s_escapeSettings = ["on", "off"];

    /// <summary>
    /// Verifies identifier quoting uses server keywords, escapes, case rules, and quote_all_identifiers.
    /// </summary>
    /// <param name="input">The identifier.</param>
    /// <param name="expected">The expected default SQL spelling.</param>
    [TestMethod]
    [DataRow("simple", "simple")]
    [DataRow("select", "\"select\"")]
    [DataRow("MixedCase", "\"MixedCase\"")]
    [DataRow("has\"quote", "\"has\"\"quote\"")]
    [DataRow("schema.table", "\"schema.table\"")]
    [DataRow("", "\"\"")]
    [DataRow("café 🐘", "\"café 🐘\"")]
    public Task IdentifierQuotingUsesServerRules(string input, string expected)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(IdentifierQuotingUsesServerRules),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT datatype.sql_quote_identifier($1)", connection, transaction);
                command.Parameters.AddWithValue(input);
                Assert.AreEqual(expected, await command.ExecuteScalarAsync(token));
                await using var configure = new NpgsqlCommand("SET LOCAL quote_all_identifiers = on", connection, transaction);
                await configure.ExecuteNonQueryAsync(token);
                Assert.AreEqual('"' + input.Replace("\"", "\"\"", StringComparison.Ordinal) + '"',
                    await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Verifies qualified identifiers quote each component independently and distinguish an absent qualifier from an empty one.
    /// </summary>
    /// <param name="qualifier">The optional qualifier.</param>
    /// <param name="identifier">The identifier.</param>
    /// <param name="expected">The qualified fragment.</param>
    [TestMethod]
    [DataRow("public", "messages", "public.messages")]
    [DataRow("my schema", "my.table", "\"my schema\".\"my.table\"")]
    [DataRow("", "select", "\"\".\"select\"")]
    [DataRow(null, "table", "\"table\"")]
    public Task QualifiedIdentifiersPreserveComponentBoundaries(string? qualifier, string identifier, string expected)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(QualifiedIdentifiersPreserveComponentBoundaries),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT datatype.sql_quote_qualified($1, $2)", connection, transaction);
                command.Parameters.Add(new NpgsqlParameter<string>("", qualifier!));
                command.Parameters.AddWithValue(identifier);
                Assert.AreEqual(expected, await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Outside a SQL callback, as in a background worker, quoting goes through the memory bridge with an explicit length
    /// and no terminator. The bytes after the text are not zero, as after a large managed array, and none of them may
    /// reach the quoted result.
    /// </summary>
    /// <param name="mode">One for an identifier, two for a qualified identifier and three for a literal.</param>
    /// <param name="expected">The quoted text of exactly the bytes the request names.</param>
    [TestMethod]
    [DataRow(1, "tbl")]
    [DataRow(2, "sch.tbl")]
    [DataRow(3, "'tbl'")]
    public Task BridgeQuotingReadsOnlyTheBytesItIsGiven(int mode, string expected)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(BridgeQuotingReadsOnlyTheBytesItIsGiven),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT tests.quote_unterminated($1)", connection, transaction);
                command.Parameters.AddWithValue(mode);
                Assert.AreEqual(expected, await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Verifies exact escaping, native setting availability and recovery without allowing values to change SQL structure.
    /// </summary>
    /// <param name="value">The arbitrary literal value.</param>
    /// <param name="expected">The escaped SQL literal.</param>
    [TestMethod]
    [DataRow("", "''")]
    [DataRow("O'Reilly", "'O''Reilly'")]
    [DataRow("a\\b", "E'a\\\\b'")]
    [DataRow("café 🐘\nline", "'café 🐘\nline'")]
    [DataRow("'; SELECT 99; --", "'''; SELECT 99; --'")]
    public Task LiteralQuotingPreservesNativeEscapeSettings(string value, string expected)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(LiteralQuotingPreservesNativeEscapeSettings),
            async (connection, transaction, token) =>
            {
                int backend = connection.ProcessID;
                foreach (string setting in s_escapeSettings)
                {
                    await using var configure = new NpgsqlCommand($"SET LOCAL standard_conforming_strings = {setting}",
                        connection, transaction);
                    if (PostgresFixture.Cluster.Installation.Version.Major >= 19 && setting == "off")
                    {
                        await transaction.SaveAsync("escape_setting", token);
                        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => configure.ExecuteNonQueryAsync(token));
                        Assert.AreEqual(PostgresErrorCodes.FeatureNotSupported, error.SqlState);
                        Assert.AreEqual("non-standard string literals are not supported", error.MessageText);
                        await transaction.RollbackAsync("escape_setting", token);
                        await transaction.ReleaseAsync("escape_setting", token);
                        configure.CommandText = "SHOW standard_conforming_strings";
                        Assert.AreEqual("on", await configure.ExecuteScalarAsync(token));
                    }
                    else
                    {
                        await configure.ExecuteNonQueryAsync(token);
                        configure.CommandText = "SHOW standard_conforming_strings";
                        Assert.AreEqual(setting, await configure.ExecuteScalarAsync(token));
                    }

                    await using var command = new NpgsqlCommand("SELECT datatype.sql_quote_literal($1)", connection, transaction);
                    command.Parameters.AddWithValue(value);
                    Assert.AreEqual(expected, await command.ExecuteScalarAsync(token));
                    command.CommandText = "SELECT datatype.sql_quoted_query($2, $1)";
                    command.Parameters.AddWithValue("odd\"name; SELECT 99; --");
                    Assert.AreEqual(value, await command.ExecuteScalarAsync(token));
                }

                Assert.AreEqual(backend, connection.ProcessID);
            }, context.CancellationToken);

    /// <summary>
    /// Verifies managed input checks and backend-thread affinity before PostgreSQL receives malformed quotation input.
    /// </summary>
    /// <param name="mode">The invalid input case.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    [DataRow(7)]
    public async Task QuotingValidationErrorsPreserveBackend(int mode)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await using var command = new NpgsqlCommand("SELECT datatype.sql_quote_invalid($1)", connection);
        command.Parameters.AddWithValue(mode);
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual(PostgresErrorCodes.ExternalRoutineException, error.SqlState);
        command.Parameters.Clear();
        command.CommandText = "SELECT datatype.sql_quote_worker()";
        Assert.AreEqual("PostgreSQL APIs can only be used on the active PostgreSQL backend thread.",
            await command.ExecuteScalarAsync(token));
        command.CommandText = "SELECT datatype.sql_quote_literal('working')";
        Assert.AreEqual("'working'", await command.ExecuteScalarAsync(token));
        Assert.AreEqual(backend, connection.ProcessID);
    }

    /// <summary>
    /// Verifies actual JSON plan content reflects bound parameters and remains valid after the producing session closes.
    /// </summary>
    /// <param name="session">Whether to explain through a scoped connection.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task ExplainUsesTypedParametersAndOwnedJson(bool session)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ExplainUsesTypedParametersAndOwnedJson),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand(
                    "SELECT datatype.sql_explain('SELECT * FROM generate_series(1, $1)', 12, true, $1)::text", connection, transaction);
                command.Parameters.AddWithValue(session);
                using JsonDocument plan = JsonDocument.Parse(Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token)));
                JsonElement node = plan.RootElement[0].GetProperty("Plan");
                Assert.AreEqual("Function Scan", node.GetProperty("Node Type").GetString());
                Assert.AreEqual(12, node.GetProperty("Plan Rows").GetInt32());
                Assert.IsFalse(node.TryGetProperty("Actual Rows", out _));
                command.CommandText = "SELECT datatype.sql_explain('SELECT 1 WHERE $1 = 42', NULL, true, $1)::text";
                using JsonDocument nullPlan = JsonDocument.Parse(Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token)));
                Assert.AreEqual("NULL::boolean", nullPlan.RootElement[0].GetProperty("Plan").GetProperty("One-Time Filter").GetString());
            }, context.CancellationToken);

    /// <summary>
    /// Verifies explaining writes plans them without executing their table modifications.
    /// </summary>
    [TestMethod]
    public Task ExplainPlansWritesWithoutRunningThem()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ExplainPlansWritesWithoutRunningThem),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("CREATE TEMP TABLE explain_values (value int)", connection, transaction);
                await command.ExecuteNonQueryAsync(token);
                command.CommandText = "SELECT datatype.sql_explain('INSERT INTO explain_values VALUES (42)', NULL, false, true)::text";
                using JsonDocument plan = JsonDocument.Parse(Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token)));
                Assert.AreEqual("Insert", plan.RootElement[0].GetProperty("Plan").GetProperty("Operation").GetString());
                command.CommandText = "SELECT count(*) FROM explain_values";
                Assert.AreEqual(0L, await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Verifies multi-statement inputs are rejected before any extra command runs, and parse failures preserve the backend.
    /// </summary>
    /// <param name="query">The rejected input.</param>
    /// <param name="state">The expected SQLSTATE.</param>
    [TestMethod]
    [DataRow("SELECT 1; INSERT INTO explain_guard VALUES (99)", "42601")]
    [DataRow("SELECT 1; SELECT 2", "42601")]
    [DataRow("SELECT missing_column", "42703")]
    [DataRow(" ", "38000")]
    public async Task ExplainRejectsInvalidOrMultipleStatements(string query, string state)
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("CREATE TEMP TABLE explain_guard (value int)", connection);
        await command.ExecuteNonQueryAsync(token);
        command.CommandText = "SELECT datatype.sql_explain($1, NULL, false, false)";
        command.Parameters.AddWithValue(query);
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
        Assert.AreEqual(state, error.SqlState);
        command.Parameters.Clear();
        command.CommandText = "SELECT count(*) FROM explain_guard";
        Assert.AreEqual(0L, await command.ExecuteScalarAsync(token));
        command.CommandText = "SELECT (datatype.sql_explain('SELECT ''semicolon;literal'';', NULL, false, true)->0->'Plan'->>'Node Type')";
        Assert.AreEqual("Result", await command.ExecuteScalarAsync(token));
    }

    /// <summary>
    /// Verifies local row edits change neither original column metadata nor the source database rows.
    /// </summary>
    [TestMethod]
    public Task RowEditsRemainLocalAfterSessionEnds()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(RowEditsRemainLocalAfterSessionEnds),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT datatype.sql_edit_rows()", connection, transaction);
                Assert.AreEqual("[42]|3802|True|20|23|3", await command.ExecuteScalarAsync(token));
                command.CommandText = """
                    CREATE DOMAIN pg_temp.row_domain AS integer;
                    CREATE TEMP TABLE domain_rows (value pg_temp.row_domain);
                    INSERT INTO domain_rows VALUES (1), (2);
                    SELECT datatype.sql_edit_domain('SELECT value FROM domain_rows ORDER BY value')
                    """;
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            }, context.CancellationToken);

    /// <summary>
    /// Verifies repeated quoting and session parameters release temporary subtransaction allocations before caller transaction end.
    /// </summary>
    /// <param name="quote">Whether to exercise quotation rather than scoped parameter conversion.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task HelperAndSessionBuffersDoNotAccumulate(bool quote)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(HelperAndSessionBuffersDoNotAccumulate),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SELECT datatype.sql_helper_context_growth($1)", connection, transaction);
                command.Parameters.AddWithValue(quote);
                Assert.AreEqual(0L, await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Verifies native quoting converts through the server encoding and recovers from an unrepresentable managed input.
    /// </summary>
    [TestMethod]
    public async Task Latin1QuotationAndEncodingFailurePreserveBackend()
    {
        CancellationToken token = context.CancellationToken;
        string database = "quote_latin1_" + Guid.NewGuid().ToString("N");
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
            int backend = connection.ProcessID;
            await using var command = new NpgsqlCommand("CREATE EXTENSION ankus_test", connection);
            await command.ExecuteNonQueryAsync(token);
            command.CommandText = "SELECT sql_quoted_query('café name', $1)";
            command.Parameters.AddWithValue("l'été");
            Assert.AreEqual("l'été", await command.ExecuteScalarAsync(token));
            command.Parameters.Clear();
            command.CommandText = "SELECT sql_quote_invalid(8)";
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
            Assert.AreEqual(PostgresErrorCodes.UntranslatableCharacter, error.SqlState);
            command.CommandText = "SELECT sql_quote_qualified('schéma', 'café')";
            Assert.AreEqual("\"schéma\".\"café\"", await command.ExecuteScalarAsync(token));
            Assert.AreEqual(backend, connection.ProcessID);
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", administrator);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }
}
