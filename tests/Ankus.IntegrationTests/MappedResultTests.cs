using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Proves target-directed scalar conversion, exact catalog identity, and temporary ownership in PostgreSQL.
/// </summary>
/// <param name="context">The cancellation and execution context.</param>
[TestClass]
public sealed class MappedResultTests(TestContext context)
{
    /// <summary>
    /// Every scalar, pair, and triple entry point selects reader-only managed identities independently.
    /// </summary>
    /// <param name="surface">Static, session, retained plan, or session plan.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task TypedMappedSpiResultsUseEverySurfaceAndDeclaredAlias(int surface)
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("0|0|0|0|0|0", await Counts(connection));
        Assert.AreEqual("0", await Values(connection, surface, 1, "SELECT 0, 'ignored'::text"));
        Assert.AreEqual("-7|107", await Values(connection, surface, 2, "SELECT -7, 7, 'ignored'::text"));
        Assert.AreEqual("42|142|héllo", await Values(connection, surface, 3, "SELECT 42, 42, 'héllo'::text, 99"));
        Assert.AreEqual("1|3|1|1|0|0", await Counts(connection));
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT mapped_results.captured_alive(false)"));
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT mapped_results.captured_alive(true)"));
        Assert.AreEqual(int.MinValue, await Probe<int>(connection, "required", surface, "SELECT '-2147483648'::integer"));
        Assert.AreEqual(int.MaxValue, await Probe<int>(connection, "required", surface, "SELECT 2147483647"));
    }

    /// <summary>
    /// Empty rows, utility results, and present NULL remain distinct from zero and never invoke a reader.
    /// </summary>
    /// <param name="surface">The selected SPI owner.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task TypedMappedAbsenceDoesNotInventAValueOrInvokeAConverter(int surface)
    {
        await using NpgsqlConnection connection = await Open();
        for (int width = 1; width <= 3; width++)
        {
            string expected = string.Join('|', Enumerable.Repeat("<NULL>", width));
            Assert.AreEqual(expected, await Values(connection, surface, width, "SELECT NULL::integer, NULL::integer, NULL::text"));
            Assert.AreEqual(expected, await Values(connection, surface, width, "SELECT NULL::bigint WHERE false"));
            Assert.AreEqual(expected, await Values(connection, surface, width, $"CREATE TEMP TABLE mapped_empty_{width}(value integer)"));
        }

        Assert.AreEqual("0|0|0|0|0|0", await Counts(connection));
        Assert.AreEqual(DBNull.Value, await Probe<object>(connection, "factory_optional", surface, "SELECT NULL::integer"));
        Assert.AreEqual(DBNull.Value, await Probe<object>(connection, "factory_optional", surface, "SELECT 42 WHERE false"));
        Assert.AreEqual("0|0|0|0|0|0", await Counts(connection));
        foreach (string sql in new[] { "SELECT NULL::integer", "SELECT 1 WHERE false", "CREATE TEMP TABLE mapped_empty_required(value integer)" })
        {
            PostgresException absent = await Assert.ThrowsExactlyAsync<PostgresException>(() => Probe<int>(connection, "required", surface, sql));
            Assert.AreEqual("38000", absent.SqlState);
            Assert.AreEqual(sql == "SELECT NULL::integer"
                ? "SQL NULL cannot be read as a non-nullable managed value."
                : "The query returned no rows; read a nullable type to treat an empty result as SQL NULL.", absent.MessageText);
        }

        Assert.AreEqual("0|0|0|0|0|0", await Counts(connection));
        Assert.AreEqual(0, await Probe<int>(connection, "required", surface, "SELECT 0"));
        Assert.AreEqual("1|1|0|0|0|0", await Counts(connection));
    }

    /// <summary>
    /// A returned row must supply every requested column; empty rows retain absence semantics instead.
    /// </summary>
    /// <param name="surface">The SPI entry point.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task TypedMappedShortRowsFailAfterReleasingTheirTemporaryOwner(int surface)
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        foreach ((int width, string sql, string message) in new[]
        {
            (2, "SELECT 1", "The SPI result has 1 columns; column 2 was requested."),
            (3, "SELECT 1, 2", "The SPI result has 2 columns; column 3 was requested."),
        })
        {
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Values(connection, surface, width, sql));
            Assert.AreEqual("38000", error.SqlState);
            Assert.AreEqual(message, error.MessageText);
            Assert.IsFalse(await Scalar<bool>(connection, "SELECT mapped_results.captured_alive(false)"));
        }

        Assert.AreEqual("7|109|ok", await Values(connection, surface, 3, "SELECT 7, 9, 'ok'::text"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Every selected result slot is preflighted even when an earlier slot already selects raw transport.
    /// </summary>
    /// <param name="surface">The SPI entry point.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task TypedMappedLaterSlotCapabilitiesFailBeforeAnySqlEffect(int surface)
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, "CREATE TEMP SEQUENCE mapped_result_effect");
        for (int width = 1; width <= 4; width++)
        {
            foreach (string sql in new[]
            {
                "SELECT nextval('mapped_result_effect')::integer, NULL::integer, NULL::integer",
                "SELECT nextval('mapped_result_effect')::integer, NULL::integer, NULL::integer WHERE false",
            })
            {
                PostgresException denied = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<int>(connection,
                    $"SELECT mapped_results.denied({surface},{width},{Literal(sql)})"));
                Assert.AreEqual("38000", denied.SqlState);
                Assert.AreEqual("The mapped PostgreSQL type has no datum reader.", denied.MessageText);
                Assert.IsFalse(await Scalar<bool>(connection, "SELECT is_called FROM mapped_result_effect"));
                Assert.AreEqual("0|0|0|0|0|0", await Counts(connection));
            }
        }

        Assert.AreEqual("1", await Values(connection, surface, 1, "SELECT nextval('mapped_result_effect')::integer"));
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT is_called FROM mapped_result_effect"));
    }

    /// <summary>
    /// Present domain NULLs retain nominal identity while absence has no datum identity to validate.
    /// </summary>
    /// <param name="surface">The SPI entry point.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task TypedMappedSpiDomainsValidatePresentAndNullIdentities(int surface)
    {
        await using NpgsqlConnection connection = await Open();
        uint expected = await Scalar<uint>(connection, "SELECT 'datum_mappings.positive'::regtype::oid");
        Assert.AreEqual(DBNull.Value, await Probe<object>(connection, "positive", surface, "SELECT NULL::datum_mappings.positive"));
        Assert.AreEqual(DBNull.Value, await Probe<object>(connection, "positive", surface, "SELECT NULL::integer WHERE false"));
        Assert.AreEqual("0|0|0|0|0|0", await Counts(connection));
        foreach (string type in new[] { "integer", "bigint", "datum_mappings.other_positive" })
        {
            uint actual = await Scalar<uint>(connection, $"SELECT {Literal(type)}::regtype::oid");
            foreach (string value in new[] { "42", "NULL" })
            {
                PostgresException mismatch = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                    Probe<int>(connection, "positive", surface, $"SELECT {value}::{type}"));
                Assert.AreEqual("38000", mismatch.SqlState);
                Assert.AreEqual($"PostgreSQL datum type OID {actual} does not match mapped type OID {expected}.", mismatch.MessageText);
            }
        }

        Assert.AreEqual("0|0|0|0|0|0", await Counts(connection));
        Assert.AreEqual(42, await Probe<int>(connection, "positive", surface, "SELECT 42::datum_mappings.positive"));
        Assert.AreEqual("0|0|0|0|1|0", await Counts(connection));
    }

    /// <summary>
    /// Fixed-size, toasted, and mixed polymorphic results survive every temporary/session/plan teardown.
    /// </summary>
    /// <param name="surface">The selected owner path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task TypedMappedDetachedAndMixedResultsOutliveTheirOwners(int surface)
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, """
            CREATE TEMP TABLE mapped_result_toast(value text);
            ALTER TABLE mapped_result_toast ALTER COLUMN value SET STORAGE EXTERNAL;
            INSERT INTO mapped_result_toast VALUES(repeat('héllo',10000));
            """);
        Assert.IsTrue(await Scalar<bool>(connection,
            "SELECT pg_relation_size(reltoastrelid)>0 FROM pg_class WHERE oid='mapped_result_toast'::regclass"));
        string expected = string.Concat(Enumerable.Repeat("héllo", 10000));
        Assert.AreEqual(expected, await Probe<string>(connection, "text", surface, "SELECT value FROM mapped_result_toast"));
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT mapped_results.captured_alive(true)"));
        await Execute(connection, "DROP TABLE mapped_result_toast");
        Assert.AreEqual(expected, await Probe<string>(connection, "text", surface, "SELECT repeat('héllo',10000)"));
        Assert.AreEqual("4608308318706860032|-4610560118520545280", await Probe<string>(connection, "complex", surface,
            "SELECT '1.25,-2.5'::datum_mappings.complex"));
        Assert.AreEqual("-9223372036854775808|0", await Probe<string>(connection, "complex", surface,
            "SELECT '-0,0'::datum_mappings.complex"));
        Assert.AreEqual("73|42|25|kept", await Scalar<string>(connection, $"SELECT mapped_results.mixed({surface},false)"));
        Assert.AreEqual("owned|1007|23|1|3|-2:3|7;NULL;-9|73", await Scalar<string>(connection, $"SELECT mapped_results.mixed({surface},true)"));
    }

    /// <summary>
    /// First-row selection preserves complete writes, ignores later/extra cells, and returns the final statement.
    /// </summary>
    /// <param name="surface">The SPI entry point.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task TypedMappedFirstRowsDoNotLimitWritesOrUseEarlierStatements(int surface)
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, "CREATE TEMP TABLE mapped_result_writes(value integer)");
        Assert.AreEqual("1", await Values(connection, surface, 1,
            "INSERT INTO mapped_result_writes SELECT generate_series(1,7) RETURNING value, 'ignored'::text"));
        Assert.AreEqual("1,2,3,4,5,6,7", await Scalar<string>(connection,
            "SELECT string_agg(value::text,',' ORDER BY value) FROM mapped_result_writes"));
        Assert.AreEqual("42|109|final", await Values(connection, surface, 3,
            "SELECT 99,99,'earlier'::text; SELECT 42,9,'final'::text, 123"));
        Assert.AreEqual("11|121", await Values(connection, surface, 2,
            "INSERT INTO mapped_result_writes VALUES(11),(12),(13) RETURNING value,value+10"));
        Assert.AreEqual("1,2,3,4,5,6,7,11,12,13", await Scalar<string>(connection,
            "SELECT string_agg(value::text,',' ORDER BY value) FROM mapped_result_writes"));
    }

    /// <summary>
    /// Caught managed conversions occur after successful SQL, preserve diagnostics, and release raw owners.
    /// </summary>
    /// <param name="surface">The SPI owner path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task TypedMappedManagedFailuresRetainCompletedWritesAndRecover(int surface)
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        await Execute(connection, "CREATE TEMP TABLE mapped_result_writes(value integer)");
        foreach ((int kind, string expression, string diagnostic) in new[]
        {
            (0, "'reader-error'::text", "P8511|typed reader failed|detached result|choose another result"),
            (0, "'ordinary-error'::text", "FormatException|ordinary typed reader failed"),
            (0, "'null-result'::text", "InvalidOperationException|A datum reader returned null for a present PostgreSQL value."),
            (1, "value", "P8512|typed factory failed|lazy result|use another mapping"),
            (2, "value", "FormatException|ordinary typed factory failed"),
            (4, "value,value::bigint", "InvalidCastException|An SPI value of type 'System.Int64' cannot be read as 'System.Int32'."),
        })
        {
            await Execute(connection, "TRUNCATE mapped_result_writes");
            string sql = $"INSERT INTO mapped_result_writes SELECT generate_series(1,4) RETURNING {expression}";
            Assert.AreEqual(diagnostic, await Scalar<string>(connection,
                $"SELECT mapped_results.catch_query({surface},{kind},{Literal(sql)})"));
            Assert.AreEqual("1,2,3,4", await Scalar<string>(connection,
                "SELECT string_agg(value::text,',' ORDER BY value) FROM mapped_result_writes"));
            if (kind == 0)
            {
                Assert.IsFalse(await Scalar<bool>(connection, "SELECT mapped_results.captured_alive(true)"));
            }
        }

        uint domain = await Scalar<uint>(connection, "SELECT 'datum_mappings.positive'::regtype::oid");
        const string identitySql = "INSERT INTO mapped_result_writes VALUES(5) RETURNING value";
        Assert.AreEqual($"InvalidCastException|PostgreSQL datum type OID 23 does not match mapped type OID {domain}.",
            await Scalar<string>(connection, $"SELECT mapped_results.catch_query({surface},3,{Literal(identitySql)})"));
        Assert.AreEqual("1,2,3,4,5", await Scalar<string>(connection,
            "SELECT string_agg(value::text,',' ORDER BY value) FROM mapped_result_writes"));
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT mapped_results.captured_alive(false)"));
        Assert.AreEqual("recovered", await Probe<string>(connection, "text", surface, "SELECT 'recovered'::text"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Exact catalog mismatches prevent callee and immutable default evaluation before executor preparation.
    /// </summary>
    /// <param name="byOid">Whether lookup uses the OID instead of the function name.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TypedMappedCatalogMismatchPreventsCalleeAndDefaultEffects(bool byOid)
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        await CreateCallEffects(connection);
        foreach (string type in new[] { "integer", "bigint", "datum_mappings.other_positive" })
        {
            foreach (bool absent in new[] { false, true })
            {
                await CreateTarget(connection, type, absent);
                uint oid = byOid ? await TargetOid(connection) : 0;
                for (int argument = 0; argument <= 2; argument++)
                {
                    PostgresException mismatch = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<int>(connection,
                        $"SELECT mapped_results.call_positive('pg_temp.result_target',{oid}::oid,{argument})"));
                    Assert.AreEqual("42804", mismatch.SqlState);
                    Assert.AreEqual($"Function result type {type} does not match requested type datum_mappings.positive", mismatch.MessageText);
                    Assert.IsFalse(await Scalar<bool>(connection, "SELECT is_called FROM mapped_result_callee"));
                    Assert.IsFalse(await Scalar<bool>(connection, "SELECT is_called FROM mapped_result_default"));
                    Assert.AreEqual("0|0|0|0|0|0", await Counts(connection));
                }
            }
        }

        await CreateTarget(connection, "datum_mappings.positive", false);
        uint valid = byOid ? await TargetOid(connection) : 0;
        Assert.AreEqual(7, await Scalar<int>(connection, $"SELECT mapped_results.call_positive('pg_temp.result_target',{valid}::oid,0)"));
        Assert.AreEqual(1L, await Scalar<long>(connection, "SELECT last_value FROM mapped_result_callee"));
        Assert.AreEqual(1L, await Scalar<long>(connection, "SELECT last_value FROM mapped_result_default"));
        Assert.AreEqual(7, await Scalar<int>(connection, $"SELECT mapped_results.call_positive('pg_temp.result_target',{valid}::oid,1)"));
        Assert.AreEqual(11, await Scalar<int>(connection, $"SELECT mapped_results.call_positive('pg_temp.result_target',{valid}::oid,2)"));
        Assert.AreEqual(3L, await Scalar<long>(connection, "SELECT last_value FROM mapped_result_callee"));
        Assert.AreEqual(2L, await Scalar<long>(connection, "SELECT last_value FROM mapped_result_default"));
        Assert.AreEqual("0|0|0|0|3|0", await Counts(connection));
        Assert.AreEqual(7, await Scalar<int>(connection, $"SELECT mapped_results.call_ordinary('pg_temp.result_target',{valid}::oid)"));
        Assert.AreEqual(4L, await Scalar<long>(connection, "SELECT last_value FROM mapped_result_callee"));
        Assert.AreEqual(3L, await Scalar<long>(connection, "SELECT last_value FROM mapped_result_default"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Catalog result capability is checked before looking up or invoking the side-effecting target.
    /// </summary>
    /// <param name="byOid">Whether lookup supplies an explicit OID.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TypedMappedCatalogWriterOnlyTargetsFailBeforeInvocation(bool byOid)
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, """
            CREATE TEMP SEQUENCE mapped_result_effect;
            CREATE FUNCTION pg_temp.denied_result() RETURNS integer LANGUAGE sql
            AS $f$ SELECT nextval('mapped_result_effect')::integer $f$;
            """);
        uint oid = byOid ? await Scalar<uint>(connection, "SELECT 'pg_temp.denied_result()'::regprocedure::oid") : 0;
        PostgresException denied = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<int>(connection,
            $"SELECT mapped_results.call_denied('pg_temp.denied_result',{oid}::oid)"));
        Assert.AreEqual("38000", denied.SqlState);
        Assert.AreEqual("The mapped PostgreSQL type has no datum reader.", denied.MessageText);
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT is_called FROM mapped_result_effect"));
        Assert.AreEqual("0|0|0|0|0|0", await Counts(connection));
        Assert.AreEqual(1, await Scalar<int>(connection, $"SELECT mapped_results.call_integer('pg_temp.denied_result',{oid}::oid)"));
    }

    /// <summary>
    /// Native execution failures clean provisional result owners before a successful same-backend retry.
    /// </summary>
    /// <param name="surface">The SPI owner path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task TypedMappedNativeQueryErrorsRecoverBeforeAnyReaderRuns(int surface)
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            Probe<int>(connection, "required", surface, "SELECT 1/0"));
        Assert.AreEqual("22012", error.SqlState);
        Assert.AreEqual("division by zero", error.MessageText);
        Assert.AreEqual("0|0|0|0|0|0", await Counts(connection));
        Assert.AreEqual(42, await Probe<int>(connection, "required", surface, "SELECT 42"));
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT mapped_results.captured_alive(false)"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Mapped catalog NULLs validate the declared identity but bypass factory and present-reader code.
    /// </summary>
    /// <param name="byOid">Whether catalog lookup uses an OID.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TypedMappedCatalogNullAndAliasesUseRequestedReadOnlyContracts(bool byOid)
    {
        await using NpgsqlConnection connection = await Open();
        await CreateCallEffects(connection);
        await CreateTarget(connection, "datum_mappings.positive", true);
        uint domain = byOid ? await TargetOid(connection) : 0;
        Assert.AreEqual(DBNull.Value, await Scalar<object>(connection,
            $"SELECT mapped_results.call_positive('pg_temp.result_target',{domain}::oid,0)"));
        Assert.AreEqual("0|0|0|0|0|0", await Counts(connection));
        await Execute(connection, "CREATE FUNCTION pg_temp.integer_result() RETURNS integer LANGUAGE sql AS 'SELECT NULL::integer'");
        uint oid = byOid ? await Scalar<uint>(connection, "SELECT 'pg_temp.integer_result()'::regprocedure::oid") : 0;
        Assert.AreEqual(DBNull.Value, await Scalar<object>(connection, $"SELECT mapped_results.call_integer('pg_temp.integer_result',{oid}::oid)"));
        Assert.AreEqual("0|0|0|0|0|0", await Counts(connection));
        await Execute(connection, "CREATE OR REPLACE FUNCTION pg_temp.integer_result() RETURNS integer LANGUAGE sql AS 'SELECT 42'");
        Assert.AreEqual(42, await Scalar<int>(connection, $"SELECT mapped_results.call_integer('pg_temp.integer_result',{oid}::oid)"));
        Assert.AreEqual(142, await Scalar<int>(connection, $"SELECT mapped_results.call_alias('pg_temp.integer_result',{oid}::oid)"));
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT mapped_results.captured_alive(false)"));
        Assert.AreEqual("1|1|0|0|0|0", await Counts(connection));
    }

    /// <summary>
    /// Catalog temporary owners release by-reference inputs after detached conversion on success and failure.
    /// </summary>
    /// <param name="byOid">Whether lookup uses the target OID.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TypedMappedCatalogStorageAndErrorsPreserveOwnership(bool byOid)
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        await Execute(connection, """
            CREATE FUNCTION pg_temp.text_result() RETURNS text LANGUAGE sql AS $f$ SELECT repeat('héllo',10000) $f$;
            CREATE FUNCTION pg_temp.complex_result() RETURNS datum_mappings.complex LANGUAGE sql AS $f$ SELECT '1.25,-2.5'::datum_mappings.complex $f$;
            CREATE FUNCTION pg_temp.integer_result() RETURNS integer LANGUAGE sql AS $f$ SELECT 42 $f$;
            """);
        uint textOid = byOid ? await Scalar<uint>(connection, "SELECT 'pg_temp.text_result()'::regprocedure::oid") : 0;
        uint complexOid = byOid ? await Scalar<uint>(connection, "SELECT 'pg_temp.complex_result()'::regprocedure::oid") : 0;
        uint integerOid = byOid ? await Scalar<uint>(connection, "SELECT 'pg_temp.integer_result()'::regprocedure::oid") : 0;
        string expected = string.Concat(Enumerable.Repeat("héllo", 10000));
        Assert.AreEqual(expected, await Scalar<string>(connection, $"SELECT mapped_results.call_text('pg_temp.text_result',{textOid}::oid)"));
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT mapped_results.captured_alive(true)"));
        Assert.AreEqual("4608308318706860032|-4610560118520545280", await Scalar<string>(connection,
            $"SELECT mapped_results.call_complex('pg_temp.complex_result',{complexOid}::oid)"));
        await Execute(connection, "CREATE OR REPLACE FUNCTION pg_temp.text_result() RETURNS text LANGUAGE sql AS $f$ SELECT 'reader-error'::text $f$");
        PostgresException reader = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<string>(connection,
            $"SELECT mapped_results.call_text('pg_temp.text_result',{textOid}::oid)"));
        Assert.AreEqual("P8511", reader.SqlState);
        Assert.AreEqual("typed reader failed", reader.MessageText);
        Assert.AreEqual("detached result", reader.Detail);
        Assert.AreEqual("choose another result", reader.Hint);
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT mapped_results.captured_alive(true)"));
        for (int attempt = 0; attempt < 2; attempt++)
        {
            PostgresException factory = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<int>(connection,
                $"SELECT mapped_results.call_factory('pg_temp.integer_result',{integerOid}::oid,false)"));
            Assert.AreEqual("P8512", factory.SqlState);
            Assert.AreEqual("typed factory failed", factory.MessageText);
            Assert.AreEqual("lazy result", factory.Detail);
            Assert.AreEqual("use another mapping", factory.Hint);
        }

        PostgresException ordinary = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<int>(connection,
            $"SELECT mapped_results.call_factory('pg_temp.integer_result',{integerOid}::oid,true)"));
        Assert.AreEqual("38000", ordinary.SqlState);
        Assert.AreEqual("ordinary typed factory failed", ordinary.MessageText);
        Assert.AreEqual("0|0|1|2|0|1", await Counts(connection));
        Assert.AreEqual(42, await Scalar<int>(connection, $"SELECT mapped_results.call_integer('pg_temp.integer_result',{integerOid}::oid)"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Caught catalog reader and factory failures do not undo already completed callee writes.
    /// </summary>
    /// <param name="byOid">Whether calls use exact catalog OIDs.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TypedMappedCaughtCatalogConversionErrorsRetainCompletedWrites(bool byOid)
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        await Execute(connection, """
            CREATE TEMP TABLE mapped_catalog_writes(value integer);
            CREATE FUNCTION pg_temp.writing_text_result() RETURNS text LANGUAGE plpgsql VOLATILE AS $f$
            BEGIN INSERT INTO mapped_catalog_writes VALUES(2),(5),(9); RETURN 'reader-error'; END $f$;
            CREATE FUNCTION pg_temp.writing_integer_result() RETURNS integer LANGUAGE plpgsql VOLATILE AS $f$
            BEGIN INSERT INTO mapped_catalog_writes VALUES(11),(17); RETURN 42; END $f$;
            CREATE FUNCTION pg_temp.valid_integer_result() RETURNS integer LANGUAGE sql AS $f$ SELECT 73 $f$;
            """);
        uint readerOid = byOid ? await Scalar<uint>(connection, "SELECT 'pg_temp.writing_text_result()'::regprocedure::oid") : 0;
        uint factoryOid = byOid ? await Scalar<uint>(connection, "SELECT 'pg_temp.writing_integer_result()'::regprocedure::oid") : 0;
        uint validOid = byOid ? await Scalar<uint>(connection, "SELECT 'pg_temp.valid_integer_result()'::regprocedure::oid") : 0;
        Assert.AreEqual("P8511|typed reader failed|detached result|choose another result", await Scalar<string>(connection,
            $"SELECT mapped_results.catch_call('pg_temp.writing_text_result',{readerOid}::oid,false)"));
        Assert.AreEqual("2,5,9", await Scalar<string>(connection,
            "SELECT string_agg(value::text,',' ORDER BY value) FROM mapped_catalog_writes"));
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT mapped_results.captured_alive(true)"));
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Execute(connection, "TRUNCATE mapped_catalog_writes");
            Assert.AreEqual("P8512|typed factory failed|lazy result|use another mapping", await Scalar<string>(connection,
                $"SELECT mapped_results.catch_call('pg_temp.writing_integer_result',{factoryOid}::oid,true)"));
            Assert.AreEqual("11,17", await Scalar<string>(connection,
                "SELECT string_agg(value::text,',' ORDER BY value) FROM mapped_catalog_writes"));
            Assert.AreEqual("0|0|1|1|0|1", await Counts(connection));
        }

        Assert.AreEqual(73, await Scalar<int>(connection, $"SELECT mapped_results.call_integer('pg_temp.valid_integer_result',{validOid}::oid)"));
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT mapped_results.captured_alive(false)"));
        Assert.AreEqual("11,17", await Scalar<string>(connection,
            "SELECT string_agg(value::text,',' ORDER BY value) FROM mapped_catalog_writes"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Result mappings resolve a new external OID after drop/recreate instead of caching converter metadata.
    /// </summary>
    [TestMethod]
    public async Task TypedMappedResultsResolveExternalIdentityAfterCatalogChanges()
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        await Execute(connection, "CREATE SCHEMA mapped_result_live; CREATE DOMAIN mapped_result_live.value AS integer");
        try
        {
            await CreateLiveTarget(connection);
            uint first = await Scalar<uint>(connection, "SELECT 'mapped_result_live.value'::regtype::oid");
            Assert.AreEqual(42, await Scalar<int>(connection, "SELECT mapped_results.live(false)"));
            Assert.AreEqual(7, await Scalar<int>(connection, "SELECT mapped_results.live(true)"));
            await Execute(connection, "DROP DOMAIN mapped_result_live.value CASCADE");
            PostgresException missing = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<int>(connection, "SELECT mapped_results.live(true)"));
            Assert.AreEqual("42704", missing.SqlState);
            Assert.AreEqual("PostgreSQL concrete defined type \"value\" does not exist in the declared schema", missing.MessageText);
            await Execute(connection, "CREATE DOMAIN mapped_result_live.value AS integer CHECK(VALUE > 0)");
            await CreateLiveTarget(connection);
            uint second = await Scalar<uint>(connection, "SELECT 'mapped_result_live.value'::regtype::oid");
            Assert.AreNotEqual(first, second);
            Assert.AreEqual(42, await Scalar<int>(connection, "SELECT mapped_results.live(false)"));
            Assert.AreEqual(7, await Scalar<int>(connection, "SELECT mapped_results.live(true)"));
            Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
        }
        finally
        {
            await Execute(connection, "DROP SCHEMA mapped_result_live CASCADE");
        }
    }

    /// <summary>
    /// A canonical row or tuple cell selects the requested mapping only when read, and the reader's input expires with
    /// its temporary owner.
    /// </summary>
    /// <param name="tuple">Whether to use a heap tuple.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TypedMappedReadsOfCanonicalCellsUseTemporaryOwners(bool tuple)
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual(42, await Scalar<int>(connection, $"SELECT mapped_results.ordinary_read({tuple})"));
        Assert.AreEqual("1|1|0|0|0|0", await Counts(connection));
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT mapped_results.captured_alive(false)"));
        Assert.AreEqual(42, await Probe<int>(connection, "required", 0, "SELECT 42"));
    }

    /// <summary>
    /// Creates independent nontransactional witnesses for executor and default-expression evaluation.
    /// </summary>
    private Task CreateCallEffects(NpgsqlConnection connection) => Execute(connection, """
        CREATE TEMP SEQUENCE mapped_result_callee;
        CREATE TEMP SEQUENCE mapped_result_default;
        CREATE FUNCTION pg_temp.result_default() RETURNS integer LANGUAGE plpgsql IMMUTABLE AS $f$
        BEGIN PERFORM nextval('mapped_result_default'); RETURN 7; END $f$;
        """);

    /// <summary>
    /// Replaces only the test target so each result identity is independent of cached plans or old OIDs.
    /// </summary>
    private Task CreateTarget(NpgsqlConnection connection, string type, bool absent) => Execute(connection, $"""
        DROP FUNCTION IF EXISTS pg_temp.result_target(integer);
        CREATE FUNCTION pg_temp.result_target(value integer DEFAULT pg_temp.result_default()) RETURNS {type}
        LANGUAGE plpgsql IMMUTABLE AS $f$
        BEGIN PERFORM nextval('mapped_result_callee'); RETURN {(absent ? "NULL" : "value")}; END $f$;
        """);

    /// <summary>
    /// Resolves the newly created exact target signature.
    /// </summary>
    private Task<uint> TargetOid(NpgsqlConnection connection)
        => Scalar<uint>(connection, "SELECT 'pg_temp.result_target(integer)'::regprocedure::oid");

    /// <summary>
    /// Binds a new function to the current external domain identity.
    /// </summary>
    private Task CreateLiveTarget(NpgsqlConnection connection) => Execute(connection, """
        CREATE FUNCTION pg_temp.live_result() RETURNS mapped_result_live.value LANGUAGE sql
        AS $f$ SELECT 7::mapped_result_live.value $f$;
        """);

    /// <summary>
    /// Opens a fresh backend for isolated lazy converter state and temporary objects.
    /// </summary>
    private Task<NpgsqlConnection> Open() => PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);

    /// <summary>
    /// Reads the fixture's exact factory and reader counters.
    /// </summary>
    private Task<string> Counts(NpgsqlConnection connection) => Scalar<string>(connection, "SELECT mapped_results.counts()");

    /// <summary>
    /// Invokes a scalar, pair, or triple probe without quoting test-controlled SQL as an identifier.
    /// </summary>
    private Task<string> Values(NpgsqlConnection connection, int surface, int width, string sql)
        => Scalar<string>(connection, $"SELECT mapped_results.values({surface},{width},{Literal(sql)})");

    /// <summary>
    /// Invokes one scalar result probe.
    /// </summary>
    private Task<T> Probe<T>(NpgsqlConnection connection, string name, int surface, string sql)
        => Scalar<T>(connection, $"SELECT mapped_results.{name}({surface},{Literal(sql)})");

    /// <summary>
    /// Quotes fixture-controlled SQL command text as one PostgreSQL string literal.
    /// </summary>
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Copies one scalar assertion value without changing the backend connection.
    /// </summary>
    private async Task<T> Scalar<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        object? value = await command.ExecuteScalarAsync(context.CancellationToken);
        return (T)value!;
    }

    /// <summary>
    /// Completes setup, cleanup, and side-effect statements before the next assertion.
    /// </summary>
    private async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }
}
