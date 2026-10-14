using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies enum catalog identity, Native AOT transport and recovery against PostgreSQL itself.
/// Catalog mutation cases run serially with other tests using the same declared types.
/// </summary>
/// <param name="context">The current test context.</param>
[TestClass]
[DoNotParallelize]
public sealed class EnumDatumTests(TestContext context)
{
    /// <summary>
    /// Nullable scalars, vectors and shaped arrays preserve exact labels and SQL identity over every SPI lifetime path.
    /// </summary>
    [TestMethod]
    [DataRow("scalar", "datatype.enum_mood", "enum_send", "'Low'")]
    [DataRow("scalar", "datatype.enum_mood", "enum_send", "'Medium'")]
    [DataRow("scalar", "datatype.enum_mood", "enum_send", "'High'")]
    [DataRow("scalar", "datatype.enum_mood", "enum_send", "'café'")]
    [DataRow("scalar", "datatype.enum_mood", "enum_send", "''")]
    [DataRow("scalar", "datatype.enum_mood", "enum_send", "E'a''b\\\\c'")]
    [DataRow("scalar", "datatype.enum_mood", "enum_send", "NULL")]
    [DataRow("array", "datatype.enum_mood[]", "array_send", "ARRAY['Low'::datatype.enum_mood,NULL,'café','']")]
    [DataRow("array", "datatype.enum_mood[]", "array_send", "'[0:1][-3:-2]={{Low,NULL},{High,Medium}}'")]
    [DataRow("array", "datatype.enum_mood[]", "array_send", "'{}'")]
    [DataRow("array", "datatype.enum_mood[]", "array_send", "NULL")]
    [DataRow("vector", "datatype.enum_mood[]", "array_send", "ARRAY['High'::datatype.enum_mood,NULL,'Low']")]
    [DataRow("vector", "datatype.enum_mood[]", "array_send", "'{}'")]
    [DataRow("vector", "datatype.enum_mood[]", "array_send", "NULL")]
    [DataRow("required", "datatype.enum_mood[]", "array_send", "ARRAY['Medium'::datatype.enum_mood,'High','Low']")]
    [DataRow("required", "datatype.enum_mood[]", "array_send", "'{}'")]
    [DataRow("bytes", "enum_values.other_mood[]", "array_send", "ARRAY['Low'::enum_values.other_mood,'High']")]
    [DataRow("bytes", "enum_values.other_mood[]", "array_send", "'{}'")]
    public Task EnumOwnershipPathsPreserveIdentity(string function, string type, string send, string literal)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumOwnershipPathsPreserveIdentity), async (connection, transaction, token) =>
        {
            await using (var path = new NpgsqlCommand("SET LOCAL search_path = pg_catalog", connection, transaction))
            {
                await path.ExecuteNonQueryAsync(token);
            }

            for (int mode = 0; mode <= 7; mode++)
            {
                await using var command = new NpgsqlCommand($"SELECT {send}(enum_values.enum_{function}(({literal})::{type},{mode})) IS NOT DISTINCT FROM {send}(({literal})::{type})", connection, transaction);
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)), $"{function}, mode {mode}");
            }
        }, context.CancellationToken);

    /// <summary>
    /// Enum ordering follows source declaration order while managed numeric values remain unchanged.
    /// </summary>
    [TestMethod]
    public Task EnumDeclarationOrderAndValuesAreIndependent()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumDeclarationOrderAndValuesAreIndependent), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("""
                SELECT enum_range(NULL::datatype.enum_mood)::text[],
                    enum_values.enum_number('Low'), enum_values.enum_number('Medium'), enum_values.enum_number('High'),
                    enum_values.enum_default()::text,
                    enum_values.enum_variadic('Low',NULL,'Medium')::text[],
                    enum_values.enum_oid() = 'datatype.enum_mood'::regtype::oid,
                    (SELECT count(*) FROM pg_depend WHERE classid = 'pg_type'::regclass
                     AND objid = 'datatype.enum_mood'::regtype AND deptype = 'e')
                """, connection, transaction);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.IsTrue(await reader.ReadAsync(token));
            Assert.AreSequenceEqual(["Low", "Medium", "High", "café", "", "a'b\\c"], reader.GetFieldValue<string[]>(0));
            Assert.AreEqual(99L, reader.GetInt64(1));
            Assert.AreEqual(-7L, reader.GetInt64(2));
            Assert.AreEqual(long.MaxValue, reader.GetInt64(3));
            Assert.AreEqual("Medium", reader.GetString(4));
            Assert.AreSequenceEqual(["Low", null, "Medium"], reader.GetFieldValue<string?[]>(5));
            Assert.IsTrue(reader.GetBoolean(6));
            Assert.AreEqual(1L, reader.GetInt64(7));
        }, context.CancellationToken);

    /// <summary>
    /// Domain scalars, domains over arrays and arrays of domains retain owned enum values across later SPI calls.
    /// </summary>
    [TestMethod]
    public Task EnumDomainsRetainBaseTypeAndShape()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumDomainsRetainBaseTypeAndShape), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("""
                CREATE DOMAIN pg_temp.mood_domain AS datatype.enum_mood;
                CREATE DOMAIN pg_temp.mood_array_domain AS datatype.enum_mood[];
                SELECT enum_values.enum_query('SELECT ''café''::pg_temp.mood_domain')::text,
                    enum_values.enum_array_query('SELECT ARRAY[''Low''::pg_temp.mood_domain,NULL,''High'']')::text[],
                    enum_values.enum_array_query('SELECT ''[0:1]={Low,High}''::pg_temp.mood_array_domain')::text
                """, connection, transaction);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.IsTrue(await reader.ReadAsync(token));
            Assert.AreEqual("café", reader.GetString(0));
            Assert.AreSequenceEqual(["Low", null, "High"], reader.GetFieldValue<string?[]>(1));
            Assert.AreEqual("[0:1]={Low,High}", reader.GetString(2));
        }, context.CancellationToken);

    /// <summary>
    /// Compressed and external arrays are detoasted and retain every element across native and SPI conversions.
    /// </summary>
    [TestMethod]
    [DataRow("EXTENDED")]
    [DataRow("EXTERNAL")]
    public Task EnumToastedArraysRemainOwned(string storage)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumToastedArraysRemainOwned), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                CREATE TEMP TABLE enum_toast(value datatype.enum_mood[]);
                ALTER TABLE enum_toast ALTER COLUMN value SET STORAGE {storage};
                INSERT INTO enum_toast SELECT array_agg(CASE WHEN i % 7 = 0 THEN NULL
                    WHEN i % 2 = 0 THEN 'Low'::datatype.enum_mood ELSE 'café'::datatype.enum_mood END)
                    FROM generate_series(1,10000) i;
                SELECT array_send(enum_values.enum_array(value,1)) = array_send(value),
                    array_send(enum_values.enum_array_query('SELECT value FROM enum_toast')) = array_send(value),
                    cardinality(value)
                    FROM enum_toast
                """, connection, transaction);
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.IsTrue(await reader.ReadAsync(token));
            Assert.IsTrue(reader.GetBoolean(0));
            Assert.IsTrue(reader.GetBoolean(1));
            Assert.AreEqual(10000, reader.GetInt32(2));
        }, context.CancellationToken);

    /// <summary>
    /// Undefined values, unknown labels, foreign enum identity and shape/NULL narrowing fail and leave the backend usable.
    /// </summary>
    [TestMethod]
    [DataRow("SELECT enum_values.enum_undefined()", "38000")]
    [DataRow("SELECT enum_values.enum_wrong_type()", "38000")]
    [DataRow("SELECT enum_values.enum_scalar('low',0)", "22P02")]
    [DataRow("SELECT enum_values.enum_required(ARRAY['Low'::datatype.enum_mood,NULL],0)", "38000")]
    [DataRow("SELECT enum_values.enum_vector('[0:1]={Low,High}',0)", "38000")]
    public Task EnumFailuresRecoverOnTheSameBackend(string sql, string state)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumFailuresRecoverOnTheSameBackend), async (connection, transaction, token) =>
        {
            await transaction.SaveAsync("enum_failure", token);
            await using var failing = new NpgsqlCommand(sql, connection, transaction);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => failing.ExecuteScalarAsync(token));
            Assert.AreEqual(state, error.SqlState);
            await transaction.RollbackAsync("enum_failure", token);
            await using var recovery = new NpgsqlCommand("SELECT enum_values.enum_scalar('Medium',1)::text", connection, transaction);
            Assert.AreEqual("Medium", await recovery.ExecuteScalarAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// Renamed server labels are rejected by the generated managed map and can recover without a stale label cache.
    /// </summary>
    [TestMethod]
    public Task EnumRenamedLabelsAreValidatedAgainstTheManagedContract()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumRenamedLabelsAreValidatedAgainstTheManagedContract), async (connection, transaction, token) =>
        {
            await using (var rename = new NpgsqlCommand("ALTER TYPE datatype.enum_mood RENAME VALUE 'Low' TO 'Renamed'", connection, transaction))
            {
                await rename.ExecuteNonQueryAsync(token);
            }

            await transaction.SaveAsync("enum_renamed", token);
            await using (var failing = new NpgsqlCommand("SELECT enum_values.enum_scalar('Renamed',0)", connection, transaction))
            {
                PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => failing.ExecuteScalarAsync(token));
                Assert.AreEqual("38000", error.SqlState);
                Assert.Contains("Renamed", error.MessageText);
            }

            await transaction.RollbackAsync("enum_renamed", token);
            await using var undo = new NpgsqlCommand("ALTER TYPE datatype.enum_mood RENAME VALUE 'Renamed' TO 'Low'; SELECT enum_values.enum_scalar('Low',1)::text", connection, transaction);
            Assert.AreEqual("Low", await undo.ExecuteScalarAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// Recreating a type refreshes both scalar and array OIDs inside the same backend without process-global caching.
    /// </summary>
    [TestMethod]
    public Task EnumTypeRecreationUsesFreshCatalogIdentities()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumTypeRecreationUsesFreshCatalogIdentities), async (connection, transaction, token) =>
        {
            await using var first = new NpgsqlCommand("SELECT enum_values.enum_ephemeral_oid()", connection, transaction);
            uint oldOid = Assert.IsInstanceOfType<uint>(await first.ExecuteScalarAsync(token));
            await using var recreate = new NpgsqlCommand("""
                ALTER EXTENSION ankus_test DROP TYPE enum_values.ephemeral_mood;
                DROP TYPE enum_values.ephemeral_mood;
                CREATE TYPE enum_values.ephemeral_mood AS ENUM ('Value');
                SELECT enum_values.enum_ephemeral_oid()
                """, connection, transaction);
            uint newOid = Assert.IsInstanceOfType<uint>(await recreate.ExecuteScalarAsync(token));
            Assert.AreNotEqual(oldOid, newOid);
            await using var current = new NpgsqlCommand("SELECT 'enum_values.ephemeral_mood'::regtype::oid", connection, transaction);
            Assert.AreEqual(newOid, await current.ExecuteScalarAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// Managed finally blocks, earlier writes, prepared plans and native contexts survive repeated enum errors.
    /// </summary>
    [TestMethod]
    public Task EnumGuardedRecoveryPreservesStateAndCleansContexts()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumGuardedRecoveryPreservesStateAndCleansContexts), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("SELECT enum_values.enum_recovery()", connection, transaction);
            Assert.AreEqual("40:20:2:0", await command.ExecuteScalarAsync(token));
        }, context.CancellationToken);
    /// <summary>
    /// A relocatable enum extension follows installation schemas, ignores shadow types and refreshes OIDs after reinstallation.
    /// </summary>
    [TestMethod]
    public Task EnumExtensionRelocationAndReinstallationFollowCatalogIdentity()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumExtensionRelocationAndReinstallationFollowCatalogIdentity), async (connection, transaction, token) =>
        {
            await using (var install = new NpgsqlCommand("""
                CREATE SCHEMA enum_first;
                CREATE SCHEMA enum_second;
                CREATE SCHEMA enum_shadow;
                CREATE TYPE enum_shadow.delivery_status AS ENUM ('pending','in transit','delivered');
                CREATE EXTENSION ankus_enums WITH SCHEMA enum_first;
                SET LOCAL search_path = enum_shadow, pg_catalog;
                """, connection, transaction))
            {
                await install.ExecuteNonQueryAsync(token);
            }

            await using (var first = new NpgsqlCommand("SELECT enum_first.delivery_status_oid(), 'enum_first.delivery_status'::regtype::oid, enum_first.advance_delivery()::text", connection, transaction))
            await using (NpgsqlDataReader reader = await first.ExecuteReaderAsync(token))
            {
                Assert.IsTrue(await reader.ReadAsync(token));
                Assert.AreEqual(reader.GetFieldValue<uint>(1), reader.GetFieldValue<uint>(0));
                Assert.AreEqual("in transit", reader.GetString(2));
            }

            await using (var detached = new NpgsqlCommand("""
                ALTER EXTENSION ankus_enums DROP FUNCTION enum_first.delivery_status_oid();
                SELECT enum_first.delivery_status_oid() = 'enum_first.delivery_status'::regtype::oid
                """, connection, transaction))
            {
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await detached.ExecuteScalarAsync(token)));
                detached.CommandText = "ALTER EXTENSION ankus_enums ADD FUNCTION enum_first.delivery_status_oid()";
                await detached.ExecuteNonQueryAsync(token);
            }

            await using (var move = new NpgsqlCommand("ALTER EXTENSION ankus_enums SET SCHEMA enum_second", connection, transaction))
            {
                await move.ExecuteNonQueryAsync(token);
            }

            uint movedOid;
            await using (var moved = new NpgsqlCommand("""
                SELECT enum_second.delivery_status_oid(), 'enum_second.delivery_status'::regtype::oid,
                    enum_second.advance_delivery('in transit')::text,
                    enum_second.delivery_statuses(ARRAY['pending'::enum_second.delivery_status,NULL,'delivered'])::text[]
                """, connection, transaction))
            await using (NpgsqlDataReader reader = await moved.ExecuteReaderAsync(token))
            {
                Assert.IsTrue(await reader.ReadAsync(token));
                movedOid = reader.GetFieldValue<uint>(0);
                Assert.AreEqual(reader.GetFieldValue<uint>(1), movedOid);
                Assert.AreEqual("delivered", reader.GetString(2));
                Assert.AreSequenceEqual(["pending", null, "delivered"], reader.GetFieldValue<string?[]>(3));
            }

            await using (var drop = new NpgsqlCommand("DROP EXTENSION ankus_enums; SELECT to_regtype('enum_second.delivery_status') IS NULL AND to_regprocedure('enum_second.delivery_status_oid()') IS NULL", connection, transaction))
            {
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await drop.ExecuteScalarAsync(token)));
            }

            await using var reinstall = new NpgsqlCommand("CREATE EXTENSION ankus_enums WITH SCHEMA enum_first; SELECT enum_first.delivery_status_oid()", connection, transaction);
            uint newOid = Assert.IsInstanceOfType<uint>(await reinstall.ExecuteScalarAsync(token));
            Assert.AreNotEqual(movedOid, newOid);
            await using var verify = new NpgsqlCommand("SELECT enum_first.delivery_status_oid() = 'enum_first.delivery_status'::regtype::oid AND enum_first.advance_delivery('pending') = 'in transit'", connection, transaction);
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await verify.ExecuteScalarAsync(token)));
        }, context.CancellationToken);
    /// <summary>
    /// Catalog lookup returns server labels, type OIDs, datum OIDs and fractional ordering without requiring a managed mapping.
    /// </summary>
    [TestMethod]
    public Task EnumCatalogHelpersMatchPostgresEntries()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumCatalogHelpersMatchPostgresEntries), async (connection, transaction, token) =>
        {
            await using var command = new NpgsqlCommand("""
                CREATE TYPE pg_temp.catalog_enum AS ENUM ('first','last');
                ALTER TYPE pg_temp.catalog_enum ADD VALUE 'middle' BEFORE 'last';
                SELECT bool_and(enum_values.enum_catalog(oid) = enumlabel || ':' || enumtypid || ':' || oid || ':' || enumsortorder)
                    FROM pg_enum WHERE enumtypid IN ('datatype.enum_mood'::regtype, 'pg_temp.catalog_enum'::regtype)
                """, connection, transaction);
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            command.CommandText = """
                SELECT bool_and(enum_values.enum_value_oid(enumlabel::datatype.enum_mood) = oid)
                    FROM pg_enum WHERE enumtypid = 'datatype.enum_mood'::regtype
                """;
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
            command.CommandText = "SELECT enum_values.enum_multiple_columns() AND enum_values.enum_recursive('High')";
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await command.ExecuteScalarAsync(token)));
        }, context.CancellationToken);

    /// <summary>
    /// Missing and wrong-kind catalog types fail through the native guard and recover after the same savepoint is rolled back.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public Task EnumCatalogLookupRejectsMissingAndNonEnumTypes(bool wrongKind)
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumCatalogLookupRejectsMissingAndNonEnumTypes), async (connection, transaction, token) =>
        {
            await transaction.SaveAsync("enum_catalog", token);
            await using var drop = new NpgsqlCommand("ALTER EXTENSION ankus_test DROP TYPE enum_values.ephemeral_mood; DROP TYPE enum_values.ephemeral_mood;" +
                (wrongKind ? "CREATE TYPE enum_values.ephemeral_mood AS (value int)" : string.Empty), connection, transaction);
            await drop.ExecuteNonQueryAsync(token);
            await using (var unrelated = new NpgsqlCommand("SELECT enum_values.enum_scalar('Low',1)::text", connection, transaction))
            {
                Assert.AreEqual("Low", await unrelated.ExecuteScalarAsync(token));
            }

            await using var lookup = new NpgsqlCommand("SELECT enum_values.enum_ephemeral_oid()", connection, transaction);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => lookup.ExecuteScalarAsync(token));
            Assert.AreEqual("42704", error.SqlState);
            await transaction.RollbackAsync("enum_catalog", token);
            Assert.IsGreaterThan(0u, Assert.IsInstanceOfType<uint>(await lookup.ExecuteScalarAsync(token)));
        }, context.CancellationToken);

    /// <summary>
    /// Native output conversion and direct catalog helpers recover from missing labels and invalid datum OIDs.
    /// </summary>
    [TestMethod]
    public Task EnumNativeOutputAndCatalogErrorsRecover()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumNativeOutputAndCatalogErrorsRecover), async (connection, transaction, token) =>
        {
            await using var rename = new NpgsqlCommand("ALTER TYPE datatype.enum_mood RENAME VALUE 'Low' TO 'Renamed'", connection, transaction);
            await rename.ExecuteNonQueryAsync(token);
            await transaction.SaveAsync("enum_output", token);
            await using var output = new NpgsqlCommand("SELECT enum_values.enum_from_label('Low')", connection, transaction);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => output.ExecuteScalarAsync(token));
            Assert.AreEqual("22P02", error.SqlState);
            await transaction.RollbackAsync("enum_output", token);
            await using var recovery = new NpgsqlCommand("SELECT enum_values.enum_catalog_recovery()", connection, transaction);
            Assert.AreEqual("40:Medium", await recovery.ExecuteScalarAsync(token));
            rename.CommandText = "ALTER TYPE datatype.enum_mood RENAME VALUE 'Renamed' TO 'Low'";
            await rename.ExecuteNonQueryAsync(token);
            output.CommandText = "SELECT enum_values.enum_from_label('Low')::text";
            Assert.AreEqual("Low", await output.ExecuteScalarAsync(token));
        }, context.CancellationToken);

    /// <summary>
    /// Unicode labels use server encoding in both directions for scalar, array and catalog lookup paths.
    /// </summary>
    [TestMethod]
    public async Task EnumLatin1LabelsUseServerEncoding()
    {
        CancellationToken token = context.CancellationToken;
        string control = await File.ReadAllTextAsync(Path.Join(IntegrationEnvironment.NativeOutputDirectory, "extension", "ankus_test.control"), token);
        Assert.Contains("encoding = 'UTF8'", control);
        string database = "enum_latin1_" + Guid.NewGuid().ToString("N");
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
            command.CommandText = """
                SELECT enum_values.enum_scalar('café',1)::text,
                    enum_values.enum_array(ARRAY['café'::enum_mood,NULL,''],4)::text[],
                    enum_values.enum_catalog(enum_values.enum_value_oid('café')) LIKE 'café:%',
                    "schéma".unicode_enum_oid() = '"schéma"."état"'::regtype::oid,
                    "schéma".unicode_enum('café')::text
                """;
            await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
            Assert.IsTrue(await reader.ReadAsync(token));
            Assert.AreEqual("café", reader.GetString(0));
            Assert.AreSequenceEqual(["café", null, ""], reader.GetFieldValue<string?[]>(1));
            Assert.IsTrue(reader.GetBoolean(2));
            Assert.IsTrue(reader.GetBoolean(3));
            Assert.AreEqual("café", reader.GetString(4));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE {database} WITH (FORCE)", administrator);
            await drop.ExecuteNonQueryAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A missing fixed schema is reported directly while unrelated registered enums still decode through optional catalog scans.
    /// </summary>
    [TestMethod]
    public Task EnumMissingFixedSchemaDoesNotPoisonOtherMappings()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumMissingFixedSchemaDoesNotPoisonOtherMappings), async (connection, transaction, token) =>
        {
            await transaction.SaveAsync("enum_schema", token);
            await using var rename = new NpgsqlCommand("ALTER SCHEMA \"schéma\" RENAME TO enum_renamed_schema", connection, transaction);
            await rename.ExecuteNonQueryAsync(token);
            await using var unrelated = new NpgsqlCommand("SELECT enum_values.enum_scalar('Low',1)::text", connection, transaction);
            Assert.AreEqual("Low", await unrelated.ExecuteScalarAsync(token));
            await using var missing = new NpgsqlCommand("SELECT enum_renamed_schema.unicode_enum_oid()", connection, transaction);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => missing.ExecuteScalarAsync(token));
            Assert.AreEqual("3F000", error.SqlState);
            await transaction.RollbackAsync("enum_schema", token);
            missing.CommandText = "SELECT \"schéma\".unicode_enum_oid() = '\"schéma\".\"état\"'::regtype::oid";
            Assert.IsTrue(Assert.IsInstanceOfType<bool>(await missing.ExecuteScalarAsync(token)));
        }, context.CancellationToken);
    /// <summary>
    /// Newly added matching labels still obey PostgreSQL's transaction-visibility checks on enum input.
    /// </summary>
    [TestMethod]
    public Task EnumUncommittedLabelsRetainPostgresSafetyChecks()
        => PostgresFixture.Cluster.RunInTransactionAsync(nameof(EnumUncommittedLabelsRetainPostgresSafetyChecks), async (connection, transaction, token) =>
        {
            await transaction.SaveAsync("enum_visibility", token);
            await using var change = new NpgsqlCommand("""
                ALTER TYPE enum_values.ephemeral_mood RENAME VALUE 'Value' TO 'Old';
                ALTER TYPE enum_values.ephemeral_mood ADD VALUE 'Value'
                """, connection, transaction);
            await change.ExecuteNonQueryAsync(token);
            await using var query = new NpgsqlCommand("SELECT enum_values.enum_ephemeral_oid()", connection, transaction);
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => query.ExecuteScalarAsync(token));
            Assert.AreEqual("55P04", error.SqlState);
            await transaction.RollbackAsync("enum_visibility", token);
            Assert.IsGreaterThan(0u, Assert.IsInstanceOfType<uint>(await query.ExecuteScalarAsync(token)));
        }, context.CancellationToken);
}
