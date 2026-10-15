using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies function options, argument defaults, schema ownership, and execution privileges in PostgreSQL.
/// </summary>
/// <param name="context">The current test context.</param>
[TestClass]
[DoNotParallelize]
public sealed class FunctionDeclarationTests(TestContext context)
{
    /// <summary>
    /// Checks the server's function catalog rather than inferring options from generated SQL text.
    /// </summary>
    [TestMethod]
    public Task CatalogRetainsPlannerAndArgumentContracts()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(CatalogRetainsPlannerAndArgumentContracts),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT provolatile::text, proparallel::text, proisstrict, prosecdef, proleakproof, procost, proargnames,
                           (SELECT proconfig FROM pg_proc WHERE oid = 'ankus_contract.declaration_path()'::regprocedure),
                           (SELECT prosupport::regproc::text FROM pg_proc WHERE oid = 'ankus_contract.declaration_like(text,text)'::regprocedure)
                    FROM pg_proc WHERE oid = 'ankus_contract.declaration_identity(int)'::regprocedure
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual("i", reader.GetString(0));
                Assert.AreEqual("s", reader.GetString(1));
                Assert.IsTrue(reader.GetBoolean(2));
                Assert.IsFalse(reader.GetBoolean(3));
                Assert.IsTrue(reader.GetBoolean(4));
                Assert.AreEqual(2.5f, reader.GetFloat(5));
                Assert.AreSequenceEqual(["input_value"], reader.GetFieldValue<string[]>(6));
                Assert.AreSequenceEqual(["search_path=pg_catalog, ankus_contract, pg_temp"], reader.GetFieldValue<string[]>(7));
                Assert.AreEqual("textlike_support", reader.GetString(8));
            }, context.CancellationToken);

    /// <summary>
    /// Checks named calls, C# constants, server expressions, NULL dispatch, empty variadics and value-type defaults.
    /// </summary>
    /// <param name="expression">The function call.</param>
    /// <param name="expected">Its expected text result or SQL NULL.</param>
    [TestMethod]
    [DataRow("ankus_contract.declaration_defaults()", "-12:quote' slash\\ café:1.2300")]
    [DataRow("ankus_contract.declaration_defaults(amount => 2.50, first_count => 7)", "7:quote' slash\\ café:2.50")]
    [DataRow("ankus_contract.declaration_defaults(text => 'changed')", "-12:changed:1.2300")]
    [DataRow("ankus_contract.declaration_identity(input_value => 9)", "9")]
    [DataRow("ankus_contract.declaration_strict(NULL)", null)]
    [DataRow("ankus_contract.declaration_called(NULL)", "42")]
    [DataRow("ankus_contract.declaration_default_variadic()", "0")]
    [DataRow("ankus_contract.declaration_default_variadic(3, 4)", "7")]
    [DataRow("ankus_contract.declaration_struct_defaults()", "00000000-0000-0000-0000-000000000000:0:0001-01-01:0:0:0:0:0:null:0,0,0")]
    [DataRow("ankus_contract.declaration_like('café', 'caf%')", "true")]
    [DataRow("ankus_contract.declaration_nested()", "123")]
    [DataRow("public.declaration_existing()", "52")]
    [DataRow("ankus_contract.declaration_na_n()", "NaN")]
    [DataRow("encode(float4send(ankus_contract.declaration_negative_zero()), 'hex')", "80000000")]
    [DataRow("ankus_contract.declaration_nullable()", "99")]
    [DataRow("ankus_contract.declaration_nullable(0)", "0")]
    [DataRow("ankus_contract.declaration_optional_text()", "got default of null")]
    [DataRow("ankus_contract.declaration_optional_text('test')", "test")]
    [DataRow("ankus_contract.declaration_optional_text(NULL)", "got default of null")]
    [DataRow("ankus_contract.same_name('test')", "test")]
    [DataRow("ankus_contract.declaration_empty_text_array_default()", "0")]
    [DataRow("ankus_contract.declaration_empty_text_array_default(ARRAY['a', NULL])", "2")]
    [DataRow("ankus_contract.same_name(same_name => 'named')", "named")]
    [DataRow("replace_values.create_or_replace_method()", "true")]
    [DataRow("replace_values.create_or_replace_method_other()", "42")]
    [DataRow("(SELECT lanname FROM pg_proc JOIN pg_language ON pg_language.oid = prolang WHERE pg_proc.oid = 'replace_values.create_or_replace_method()'::regprocedure)", "c")]
    [DataRow("get_byte(charsend(ankus_contract.declaration_char()), 0)", "128")]
    [DataRow("ankus_contract.declaration_oid()", "4294967295")]
    [DataRow("ankus_contract.declaration_minimums()", "-32768:-2147483648:-9223372036854775808")]
    [DataRow("\"ankus café \"\"schema\".declaration_override(27)", "27")]
    public Task SqlDispatchHonorsDeclarations(string expression, string? expected)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(SqlDispatchHonorsDeclarations),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand($"SELECT ({expression})::text", connection, transaction);
                Assert.AreEqual((object?)expected ?? DBNull.Value, await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Defaults are evaluated by PostgreSQL at call time and pinned search paths restore the caller's setting on return.
    /// </summary>
    [TestMethod]
    public Task ServerDefaultsAndScopedSettingsUseTheCallingSession()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(ServerDefaultsAndScopedSettingsUseTheCallingSession),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SET LOCAL search_path TO public;
                    SET LOCAL timezone TO 'Pacific/Kiritimati';
                    SELECT ankus_contract.declaration_default_date() = current_date,
                           ankus_contract.declaration_default_date("when" => date '2001-02-03') = date '2001-02-03',
                           ankus_contract.declaration_path(), current_setting('search_path'),
                           (SELECT provolatile::text || proparallel::text FROM pg_proc WHERE oid = 'ankus_contract.declaration_path()'::regprocedure)
                    """, connection, transaction);
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.IsTrue(reader.GetBoolean(0));
                Assert.IsTrue(reader.GetBoolean(1));
                Assert.AreEqual("pg_catalog, ankus_contract, pg_temp", reader.GetString(2));
                Assert.AreEqual("public", reader.GetString(3));
                Assert.AreEqual("sr", reader.GetString(4));
            }, context.CancellationToken);

    /// <summary>
    /// An explicitly empty path has PostgreSQL's native representation and restores the caller's path after the call.
    /// </summary>
    [TestMethod]
    public Task EmptySearchPathUsesNativeSettingSemantics()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EmptySearchPathUsesNativeSettingSemantics),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("SET LOCAL search_path TO ''; SELECT current_setting('search_path')", connection, transaction);
                string expected = Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token));
                command.CommandText = "SET LOCAL search_path TO public; SELECT ankus_contract.declaration_empty_path(), current_setting('search_path')";
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(expected, reader.GetString(0));
                Assert.AreEqual("public", reader.GetString(1));
            }, context.CancellationToken);

    /// <summary>
    /// Reapplying generated CREATE OR REPLACE retains the PostgreSQL function OID and dependent views.
    /// </summary>
    [TestMethod]
    public Task GeneratedReplacementPreservesDependentObjects()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(GeneratedReplacementPreservesDependentObjects),
            async (connection, transaction, token) =>
            {
                const string function = "\"ankus café \"\"schema\".declaration_override";
                await using var command = new NpgsqlCommand($"CREATE TEMP VIEW declaration_dependent AS SELECT {function}(17) AS value; SELECT '{function}(int)'::regprocedure::oid", connection, transaction);
                uint original = Assert.IsInstanceOfType<uint>(await command.ExecuteScalarAsync(token));
                string sql = await File.ReadAllTextAsync(IntegrationEnvironment.ExtensionScript("ankus_test"), token);
                int start = sql.IndexOf("CREATE OR REPLACE FUNCTION", StringComparison.Ordinal);
                Assert.IsGreaterThanOrEqualTo(0, start);
                int end = sql.IndexOf(';', start);
                command.CommandText = "SELECT probin FROM pg_proc WHERE oid = $1";
                command.Parameters.AddWithValue(NpgsqlTypes.NpgsqlDbType.Oid, original);
                string library = Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token));
                command.Parameters.Clear();
                command.CommandText = sql[start..(end + 1)].Replace("MODULE_PATHNAME", library, StringComparison.Ordinal);
                await command.ExecuteNonQueryAsync(token);
                command.CommandText = $"SELECT value, '{function}(int)'::regprocedure::oid FROM declaration_dependent";
                await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(17, reader.GetInt32(0));
                Assert.AreEqual(original, reader.GetFieldValue<uint>(1));
            }, context.CancellationToken);

    /// <summary>
    /// Definer execution can read owner-only data while invoker execution fails and restores the caller's identity.
    /// </summary>
    [TestMethod]
    public Task SecurityModeControlsPrivilegesAndRestoresContext()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(SecurityModeControlsPrivilegesAndRestoresContext),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    CREATE ROLE ankus_declaration_reader;
                    CREATE TABLE ankus_contract.declaration_secret(value text);
                    INSERT INTO ankus_contract.declaration_secret VALUES ('owner only');
                    GRANT USAGE ON SCHEMA ankus_contract TO ankus_declaration_reader;
                    SET LOCAL ROLE ankus_declaration_reader;
                    SELECT ankus_contract.declaration_secret(), current_user::text
                    """, connection, transaction);
                await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
                {
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.AreEqual("owner only", reader.GetString(0));
                    Assert.AreEqual("ankus_declaration_reader", reader.GetString(1));
                }

                await transaction.SaveAsync("invoker", token);
                command.CommandText = "SELECT ankus_contract.declaration_invoker()";
                PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteScalarAsync(token));
                Assert.AreEqual(PostgresErrorCodes.InsufficientPrivilege, error.SqlState);
                await transaction.RollbackAsync("invoker", token);
                command.CommandText = "SELECT current_user::text";
                Assert.AreEqual("ankus_declaration_reader", await command.ExecuteScalarAsync(token));
            }, context.CancellationToken);

    /// <summary>
    /// Fixed schemas are extension-owned, prevent relocation, and are recreated after uninstall.
    /// </summary>
    [TestMethod]
    public Task FixedSchemasParticipateInExtensionLifecycle()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(FixedSchemasParticipateInExtensionLifecycle),
            async (connection, transaction, token) =>
            {
                await using var command = new NpgsqlCommand("""
                    SELECT extrelocatable, EXISTS (
                        SELECT FROM pg_depend WHERE refclassid = 'pg_extension'::regclass AND refobjid = e.oid
                          AND classid = 'pg_namespace'::regclass AND objid = 'ankus_empty_contract'::regnamespace AND deptype = 'e')
                    FROM pg_extension e WHERE extname = 'ankus_test'
                    """, connection, transaction);
                await using (NpgsqlDataReader reader = await command.ExecuteReaderAsync(token))
                {
                    Assert.IsTrue(await reader.ReadAsync(token));
                    Assert.IsFalse(reader.GetBoolean(0));
                    Assert.IsTrue(reader.GetBoolean(1));
                }

                await transaction.SaveAsync("relocation", token);
                command.CommandText = "ALTER EXTENSION ankus_test SET SCHEMA public";
                PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteNonQueryAsync(token));
                Assert.AreEqual(PostgresErrorCodes.FeatureNotSupported, error.SqlState);
                await transaction.RollbackAsync("relocation", token);
                command.CommandText = """
                    DROP EXTENSION ankus_test;
                    SELECT to_regnamespace('ankus_contract') IS NULL AND to_regnamespace('ankus_empty_contract') IS NULL
                    """;
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
                command.CommandText = "CREATE SCHEMA ankus_contract";
                await command.ExecuteNonQueryAsync(token);
                await transaction.SaveAsync("existing", token);
                command.CommandText = "CREATE EXTENSION ankus_test WITH SCHEMA datatype";
                PostgresException conflict = await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteNonQueryAsync(token));
                Assert.AreEqual(PostgresErrorCodes.ObjectNotInPrerequisiteState, conflict.SqlState);
                await transaction.RollbackAsync("existing", token);
                string escapeSetting = PostgresFixture.Cluster.Installation.Version.Major >= 19 ? "on" : "off";
                command.CommandText = $"SET LOCAL standard_conforming_strings TO {escapeSetting}; SHOW standard_conforming_strings";
                Assert.AreEqual(escapeSetting, await command.ExecuteScalarAsync(token));
                command.CommandText = """
                    DROP SCHEMA ankus_contract;
                    CREATE EXTENSION ankus_test WITH SCHEMA datatype;
                    SELECT ankus_contract.declaration_defaults()
                    """;
                Assert.AreEqual("-12:quote' slash\\ café:1.2300", await command.ExecuteScalarAsync(token));
                command.CommandText = "DROP EXTENSION ankus_test; SELECT to_regnamespace('public') IS NOT NULL AND to_regprocedure('public.declaration_existing()') IS NULL";
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            }, context.CancellationToken);
}
