using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Executes exact mapped arrays through native storage, owner transitions, and generated deferred callbacks.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class MappedArrayTests(TestContext context)
{
    /// <summary>
    /// Manual leaf identities and independent aliases survive every selected ownership path.
    /// </summary>
    /// <param name="mode">Direct, static, session, retained plan, session plan, raw query, or raw cursor.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    public async Task MappedArraysSelectDeclaredAliasesAcrossOwners(int mode)
    {
        await using NpgsqlConnection connection = await Open();
        const string input = "ARRAY['0','42',NULL,'16777215']::datum_mappings.u24[]";
        Assert.AreEqual("1|43|NULL|16777216", await Scalar<string>(connection, $"SELECT mapped_arrays.alias({input})"));
        Assert.AreSequenceEqual<int?>([0, 42, null, 16777215], await Scalar<int?[]>(connection,
            $"SELECT ARRAY(SELECT item::integer FROM unnest(mapped_arrays.u24({input},{mode})) WITH ORDINALITY t(item,n) ORDER BY n)"));
        Assert.AreSequenceEqual<int?>([0, 42, null, 16777215], await Scalar<int?[]>(connection,
            "SELECT ARRAY(SELECT item::integer FROM unnest(mapped_arrays.alias_construct()) WITH ORDINALITY t(item,n) ORDER BY n)"));
        Assert.AreEqual("[-2:-1][4:5]={{0,NULL},{42,16777215}}", await Scalar<string>(connection,
            $"SELECT mapped_arrays.u24('[-2:-1][4:5]={{{{0,NULL}},{{42,16777215}}}}'::datum_mappings.u24[],{mode})::text"));
        Assert.AreEqual("{}", await Scalar<string>(connection, $"SELECT mapped_arrays.u24('{{}}'::datum_mappings.u24[],{mode})::text"));
        Assert.IsTrue(await Scalar<bool>(connection, $"SELECT mapped_arrays.u24(NULL::datum_mappings.u24[],{mode}) IS NULL"));
        Assert.IsTrue(await Scalar<bool>(connection, """
            SELECT p.prorettype=t.typarray AND p.proargtypes[0]=t.typarray AND a.typelem=t.oid
            FROM pg_proc p JOIN pg_type t ON t.oid='datum_mappings.u24'::regtype
            JOIN pg_type a ON a.oid=t.typarray WHERE p.oid='mapped_arrays.u24(datum_mappings.u24[],integer)'::regprocedure
            """));
    }

    /// <summary>
    /// CLR enum vectors preserve unnamed values while reference covariance retains the declared writable base array.
    /// </summary>
    [TestMethod]
    public async Task MappedArraysPreserveClrEnumAndDeclaredReferenceIdentity()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreSequenceEqual<int?>([-1, 0, 42, null], await Scalar<int?[]>(connection,
            "SELECT mapped_arrays.sign(ARRAY[-1,0,42,NULL],1)"));
        Assert.AreSequenceEqual<short?>([0, 255, null], await Scalar<short?[]>(connection,
            "SELECT mapped_arrays.byte_enum(ARRAY[0,255,NULL]::smallint[],1)"));
        Assert.AreEqual("smallint[]", await Scalar<string>(connection,
            "SELECT pg_typeof(mapped_arrays.byte_enum(ARRAY[0,255]::smallint[],0))::text"));
        Assert.AreEqual("alpha|beta|replacement|alpha|True|True", await Scalar<string>(connection,
            "SELECT mapped_arrays.clr_identity(false)"));
        await Execute(connection, "CREATE TEMP SEQUENCE mapped_array_effect");
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<string>(connection,
            "SELECT mapped_arrays.clr_identity(true)"));
        Assert.AreEqual("38000", error.SqlState);
        Assert.AreEqual("Array cannot be converted to 'Ankus.TestExtension.MappedSign' elements.", error.MessageText);
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT is_called FROM mapped_array_effect"));
        Assert.AreSequenceEqual<int>([-1, 0, 42], await Scalar<int[]>(connection, "SELECT ARRAY[-1,0,42]"));
    }

    /// <summary>
    /// Manual operator and cast bundles preserve exact array argument, result and catalog identities.
    /// </summary>
    [TestMethod]
    public async Task MappedArrayManualOperatorAndCastUseExactLeafIdentity()
    {
        await using NpgsqlConnection connection = await Open();
        const string expression = "ARRAY['0',NULL]::datum_mappings.u24[] OPERATOR(mapped_arrays.##) ARRAY['42','16777215']::datum_mappings.u24[]";
        Assert.AreEqual("{0,NULL,42,16777215}", await Scalar<string>(connection, $"SELECT ({expression})::text"));
        Assert.AreEqual(16777257L, await Scalar<long>(connection, $"SELECT ({expression})::bigint"));
        Assert.IsTrue(await Scalar<bool>(connection, """
            SELECT o.oprleft=t.typarray AND o.oprright=t.typarray AND o.oprresult=t.typarray
                AND o.oprcode='mapped_arrays.concatenate(datum_mappings.u24[],datum_mappings.u24[])'::regprocedure
            FROM pg_operator o JOIN pg_type t ON t.oid='datum_mappings.u24'::regtype
            WHERE o.oid='mapped_arrays.##(datum_mappings.u24[],datum_mappings.u24[])'::regoperator
            """));
        Assert.IsTrue(await Scalar<bool>(connection, """
            SELECT c.castsource=t.typarray AND c.casttarget='bigint'::regtype AND c.castcontext='e' AND c.castmethod='f'
                AND c.castfunc='mapped_arrays.total(datum_mappings.u24[])'::regprocedure
            FROM pg_cast c JOIN pg_type t ON t.oid='datum_mappings.u24'::regtype
            WHERE c.castsource=t.typarray AND c.casttarget='bigint'::regtype
            """));
    }

    /// <summary>
    /// Dedicated probes share one lazy leaf converter and preserve zero, empty, whole NULL, and nullable cells.
    /// </summary>
    [TestMethod]
    public async Task MappedArrayNullabilityAndLazyConvertersRemainIndependent()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("0|0|0", await Counts(connection));
        Assert.AreEqual("NULL", await Raw(connection, "SELECT NULL::integer[]"));
        Assert.AreEqual("23|0|0|||", await Raw(connection, "SELECT ARRAY[]::integer[]"));
        Assert.AreEqual("23|1|2|2|1|NULL,NULL", await Raw(connection, "SELECT ARRAY[NULL,NULL]::integer[]"));
        Assert.AreEqual("0|0|0", await Counts(connection));
        Assert.AreEqual("23|1|3|3|1|0,NULL,42", await Raw(connection, "SELECT ARRAY[0,NULL,42]", vector: true));
        Assert.AreEqual("1|2|0", await Counts(connection));
        Assert.AreEqual("23|1|1|1|1|7", await Scalar<string>(connection, "SELECT mapped_arrays.inspect(ARRAY[7])"));
        Assert.AreEqual("1|3|0", await Counts(connection));
        Assert.AreEqual(19, await Scalar<int>(connection, "SELECT mapped_arrays.scalar(19)"));
        Assert.AreEqual("1|4|0", await Counts(connection));
        Assert.AreSequenceEqual<int?>([42, null, null, 7], await Scalar<int?[]>(connection, "SELECT mapped_arrays.writer(-666)"));
        Assert.AreEqual("1|4|3", await Counts(connection));
        Assert.AreEqual("4|0|3|0", await Owners(connection));
        await Reset(connection);
        PostgresException required = await Assert.ThrowsExactlyAsync<PostgresException>(() => Raw(connection, "SELECT ARRAY[7,NULL,11]", required: true));
        Assert.AreEqual("38000", required.SqlState);
        Assert.AreEqual("SQL NULL cannot be read as a non-nullable managed value.", required.MessageText);
        Assert.AreEqual("1|1|0", await Counts(connection));
        Assert.AreEqual("1|0|0|0", await Owners(connection));
    }

    /// <summary>
    /// Managed and server construction independently pin row-major order, lower bounds, and early vector rejection.
    /// </summary>
    [TestMethod]
    public async Task MappedArrayShapesUsePostgresBoundsAndRejectLossyVectorsEarly()
    {
        await using NpgsqlConnection connection = await Open();
        const string shaped = "[-2:-1][4:6]={{11,NULL,-7},{0,5,9}}";
        Assert.AreEqual(shaped, await Scalar<string>(connection, "SELECT mapped_arrays.construct()::text"));
        Assert.AreEqual(9, await Scalar<int>(connection, "SELECT (mapped_arrays.construct())[-1][6]"));
        Assert.AreEqual("23|2|6|2,3|-2,4|11,NULL,-7,0,5,9", await Scalar<string>(connection,
            $"SELECT mapped_arrays.inspect({Literal(shaped)}::integer[])"));
        Assert.AreEqual("23|6|1|1,1,1,1,1,1|-3,-2,-1,0,1,2|7", await Raw(connection,
            "SELECT array_fill(7,ARRAY[1,1,1,1,1,1],ARRAY[-3,-2,-1,0,1,2])"));
        await Reset(connection);
        foreach (string sql in new[] { "SELECT '[0:1]={7,11}'::integer[]", $"SELECT {Literal(shaped)}::integer[]" })
        {
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Raw(connection, sql, vector: true));
            Assert.AreEqual("38000", error.SqlState);
            Assert.AreEqual("Use PgArray<T> to preserve dimensions and lower bounds, or ToArray() to explicitly flatten them.", error.MessageText);
            Assert.AreEqual("1|0|0", await Counts(connection));
            Assert.AreEqual("0|0|0|0", await Owners(connection));
        }

        Assert.AreEqual("23|1|2|2|1|7,11", await Raw(connection, "SELECT ARRAY[7,11]", vector: true));
    }

    /// <summary>
    /// Every selected result slot preflights capability, while write-only arrays remain valid parameters and outputs.
    /// </summary>
    /// <param name="surface">Static, session, retained plan, or session plan.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task MappedArrayDirectionsAreCheckedBeforeSqlAndFactory(int surface)
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, "CREATE TEMP SEQUENCE mapped_array_effect");
        for (int width = 1; width <= 4; width++)
        {
            for (int kind = 0; kind < 4; kind++)
            {
                PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<int>(connection,
                    $"SELECT mapped_arrays.denied({surface},{width},{kind})"));
                Assert.AreEqual("38000", error.SqlState);
                Assert.AreEqual(width == 4 ? "The mapped PostgreSQL type has no datum writer." : "The mapped PostgreSQL type has no datum reader.", error.MessageText);
                Assert.IsFalse(await Scalar<bool>(connection, "SELECT is_called FROM mapped_array_effect"));
                Assert.AreEqual("0|0|0", await Counts(connection));
            }
        }

        Assert.AreEqual(216, await Scalar<int>(connection, "SELECT mapped_arrays.read_only(ARRAY[7,NULL,9])"));
        Assert.AreSequenceEqual<int?>([-7, null, 3], await Scalar<int?[]>(connection, $"SELECT mapped_arrays.writer_parameter({surface},0)"));
        Assert.IsEmpty(await Scalar<int?[]>(connection, $"SELECT mapped_arrays.writer_parameter({surface},1)"));
        Assert.IsTrue(await Scalar<bool>(connection, $"SELECT mapped_arrays.writer_parameter({surface},2) IS NULL"));
        Assert.AreSequenceEqual<int?>([-7, null, 3], await Scalar<int?[]>(connection, "SELECT mapped_arrays.write_only(0)"));
    }

    /// <summary>
    /// Type-only mapped-array defaults require no writer and execute exactly once through name/OID calls.
    /// </summary>
    /// <param name="byOid">Whether to call the defaulted function by catalog OID.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MappedArrayDefaultsUseReadOnlyLeafMetadata(bool byOid)
    {
        await using NpgsqlConnection connection = await Open();
        await Execute(connection, """
            CREATE TEMP SEQUENCE mapped_array_default_effect;
            CREATE FUNCTION pg_temp.mapped_array_default_value() RETURNS integer[] LANGUAGE plpgsql AS $f$
            BEGIN PERFORM nextval('mapped_array_default_effect'); RETURN ARRAY[7,NULL,9]; END $f$;
            CREATE FUNCTION pg_temp.mapped_array_default(value integer[] DEFAULT pg_temp.mapped_array_default_value())
            RETURNS integer LANGUAGE sql AS $f$ SELECT mapped_arrays.read_only(value) $f$;
            """);
        Assert.AreEqual(216, await Scalar<int>(connection, $"SELECT mapped_arrays.default_call({byOid})"));
        Assert.AreEqual(1L, await Scalar<long>(connection, "SELECT last_value FROM mapped_array_default_effect"));
        Assert.AreEqual("0|0|0", await Counts(connection));
    }

    /// <summary>
    /// Base/sibling/outer-domain arrays never substitute for the exact mapped element-array identity.
    /// </summary>
    [TestMethod]
    public async Task MappedArrayNominalIdentityIncludesNullEmptyAndAllNull()
    {
        await using NpgsqlConnection connection = await Open();
        uint element = await Scalar<uint>(connection, "SELECT 'datum_mappings.positive'::regtype::oid");
        uint expected = await Scalar<uint>(connection, "SELECT 'datum_mappings.positive[]'::regtype::oid");
        await Execute(connection, "CREATE SCHEMA mapped_array_domains; CREATE DOMAIN mapped_array_domains.outer_array AS datum_mappings.positive[]");
        try
        {
            foreach ((string expression, string result) in new[]
            {
                ("ARRAY[7,NULL,11]", $"{element}|1|3|7,NULL,11"),
                ("ARRAY[]", $"{element}|0|0|"),
                ("ARRAY[NULL,NULL]", $"{element}|1|2|NULL,NULL"),
                ("NULL", "NULL"),
            })
            {
                Assert.AreEqual(result, await Positive(connection, $"SELECT {expression}::datum_mappings.positive[]"));
                foreach (string type in new[] { "integer[]", "bigint[]", "datum_mappings.other_positive[]", "mapped_array_domains.outer_array" })
                {
                    uint actual = await Scalar<uint>(connection, $"SELECT {Literal(type)}::regtype::oid");
                    PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Positive(connection,
                        $"SELECT {expression}::{type}"));
                    Assert.AreEqual("38000", error.SqlState);
                    Assert.AreEqual($"PostgreSQL datum type OID {actual} does not match mapped array type OID {expected}.", error.MessageText);
                }
            }

            Assert.AreEqual("7|NULL|11", await Positive(connection,
                "SELECT ARRAY[7,NULL,11]::mapped_array_domains.outer_array", scalar: true));
        }
        finally
        {
            await Execute(connection, "DROP SCHEMA mapped_array_domains CASCADE");
        }
    }

    /// <summary>
    /// Actual native header identity is checked even when no present element could reveal the mismatch.
    /// </summary>
    /// <param name="kind">Present, empty, or all-NULL original int4 array.</param>
    /// <param name="original">The independently known unchanged original value.</param>
    [TestMethod]
    [DataRow(0, "{7,11}")]
    [DataRow(1, "{}")]
    [DataRow(2, "{NULL,NULL}")]
    public async Task MappedArrayHeaderIdentityIsValidatedBeforeDeconstruction(int kind, string original)
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        Assert.AreEqual($"42804|Array header element type does not match its declared type|{original}|0|True",
            await Scalar<string>(connection, $"SELECT mapped_arrays.header({kind})"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Raw extraction expires all temporary element handles while preserving the independent source array.
    /// </summary>
    [TestMethod]
    public async Task MappedArrayReadOwnershipReleasesTemporaryElementsOnly()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("23|1|3|3|1|7,NULL,11~1|2|0~2|0|0|0~{7,NULL,11}",
            await Scalar<string>(connection, "SELECT mapped_arrays.raw_owners(false)"));
        Assert.AreEqual("P8521|mapped array reader failed|later element|replace the sentinel~1|2|0~2|0|0|0~{7,-777,99}",
            await Scalar<string>(connection, "SELECT mapped_arrays.raw_owners(true)"));
        Assert.AreEqual("caller-owned|NULL|other~caller-owned~0|0|2|0~True",
            await Scalar<string>(connection, "SELECT mapped_arrays.borrowed_writer()"));
        Assert.AreEqual("23|1|1|1|1|42", await Raw(connection, "SELECT ARRAY[42]"));
    }

    /// <summary>
    /// Later typed-NULL, identity, lifetime and user errors cannot execute the target SQL or poison the backend.
    /// </summary>
    /// <param name="mode">The deliberate invalid writer mode.</param>
    /// <param name="state">The exact expected SQLSTATE.</param>
    /// <param name="message">The exact leading diagnostic.</param>
    [TestMethod]
    [DataRow(-888, "P8522", "mapped array writer failed")]
    [DataRow(-555, "38000", "PostgreSQL datum type OID 20 does not match mapped type OID 23.")]
    [DataRow(-444, "38000", "The datum's memory context has been deleted.")]
    [DataRow(-333, "38000", "The datum's memory context has been reset.")]
    public async Task MappedArrayLaterWriterErrorsPreserveCleanupAndPreExecutionTiming(int mode, string state, string message)
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        await Execute(connection, "CREATE TEMP SEQUENCE mapped_array_effect");
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection,
            $"SELECT mapped_arrays.write_before_sql({mode})"));
        Assert.AreEqual(state, error.SqlState);
        Assert.AreEqual(mode is -444 or -333 ? message + Environment.NewLine + "Object name: 'PgDatum'." : message, error.MessageText);
        Assert.IsFalse(await Scalar<bool>(connection, "SELECT is_called FROM mapped_array_effect"));
        Assert.AreEqual("1|0|2", await Counts(connection));
        Assert.AreEqual("0|0|2|0", await Owners(connection));
        Assert.AreSequenceEqual<int?>([42, null, null, 7], await Scalar<int?[]>(connection, "SELECT mapped_arrays.writer(-666)"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Both framework and writer-produced NULL elements reach native domain checks before array construction succeeds.
    /// </summary>
    [TestMethod]
    public async Task MappedArrayNullWritersCannotBypassDomainConstraints()
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        foreach (bool managedNull in new[] { false, true })
        {
            PostgresException required = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection,
                $"SELECT mapped_arrays.required_writer({managedNull})"));
            Assert.AreEqual("23502", required.SqlState);
            Assert.AreEqual("domain datum_mappings.required does not allow null values", required.MessageText);
        }

        PostgresException check = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection,
            "SELECT mapped_arrays.positive_writer(-1)"));
        Assert.AreEqual("23514", check.SqlState);
        Assert.AreEqual("value for domain datum_mappings.positive violates check constraint \"positive_check\"", check.MessageText);
        Assert.AreEqual("{7,11}", await Scalar<string>(connection, "SELECT mapped_arrays.positive_writer(11)::text"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Fixed fields and externally toasted/compressed arrays retain every copied element after native owners end.
    /// </summary>
    /// <param name="mode">The raw/query/plan ownership path.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    [DataRow(5)]
    [DataRow(6)]
    public async Task MappedArrayStoragePreservesIndependentFixedAndToastedValues(int mode)
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreSequenceEqual<string?>(["4608308318706860032|-4610560118520545280", null, "-9223372036854775808|0"],
            await Scalar<string?[]>(connection, $"""
                SELECT ARRAY(SELECT datum_mappings.complex_bits(item)
                FROM unnest(mapped_arrays.complex(ARRAY['1.25,-2.5',NULL,'-0,0']::datum_mappings.complex[],{mode}))
                WITH ORDINALITY t(item,n) ORDER BY n)
                """));
        await Execute(connection, """
            CREATE TEMP TABLE mapped_array_external(value text[]);
            ALTER TABLE mapped_array_external ALTER COLUMN value SET STORAGE EXTERNAL;
            INSERT INTO mapped_array_external VALUES(ARRAY[repeat('héllo',10000),NULL,'','NULL','a,b{c}\d']);
            CREATE TEMP TABLE mapped_array_compressed(value text[]);
            INSERT INTO mapped_array_compressed VALUES(ARRAY[repeat('compress',15000),NULL]);
            """);
        Assert.IsTrue(await Scalar<bool>(connection,
            "SELECT pg_relation_size(reltoastrelid)>0 FROM pg_class WHERE oid='mapped_array_external'::regclass"));
        Assert.IsTrue(await Scalar<bool>(connection, "SELECT pg_column_size(value)<octet_length(value[1]) FROM mapped_array_compressed"));
        Assert.AreSequenceEqual<string?>([string.Concat(Enumerable.Repeat("héllo", 10000)), null, string.Empty, "NULL", "a,b{c}\\d"],
            await Scalar<string?[]>(connection, $"SELECT mapped_arrays.text(value,{mode}) FROM mapped_array_external"));
        Assert.AreSequenceEqual<string?>([string.Concat(Enumerable.Repeat("compress", 15000)), null],
            await Scalar<string?[]>(connection, $"SELECT mapped_arrays.text(value,{mode}) FROM mapped_array_compressed"));
    }

    /// <summary>
    /// All SPI owner/width combinations select array results explicitly and retain mixed polymorphic values.
    /// </summary>
    /// <param name="surface">Static, session, retained plan, or session plan.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    public async Task MappedArraysCrossEveryTypedSpiOwnerAndSelectedPosition(int surface)
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("23|1|3|3|1|7,NULL,-9", await Query(connection, surface, 1, "SELECT ARRAY[7,NULL,-9]"));
        Assert.AreEqual("142~23|1|2|2|0|7,NULL", await Query(connection, surface, 2, "SELECT 42,'[0:1]={7,NULL}'::integer[]"));
        Assert.AreEqual("23|1|2|2|1|7,11~owned~tail", await Query(connection, surface, 3, "SELECT ARRAY[7,11],'owned'::text,'tail'::text"));
        Assert.AreEqual("73~middle~23|1|2|2|1|0,NULL", await Query(connection, surface, 4, "SELECT 73,'middle'::text,ARRAY[0,NULL]"));
        Assert.AreEqual("NULL", await Query(connection, surface, 1, "SELECT NULL::integer[]"));
        Assert.AreEqual("NULL", await Query(connection, surface, 1, "SELECT 42::bigint WHERE false"));
        Assert.AreEqual("NULL", await Query(connection, surface, 1, "CREATE TEMP TABLE mapped_array_scalar_empty(value integer)"));
        Assert.AreEqual("NULL~NULL", await Query(connection, surface, 2, "SELECT NULL::bigint WHERE false"));
        Assert.AreEqual("NULL~NULL~NULL", await Query(connection, surface, 3, "SELECT NULL::bigint WHERE false"));
        Assert.AreEqual("NULL~NULL~NULL", await Query(connection, surface, 3, "CREATE TEMP TABLE mapped_array_empty(value integer)"));
        PostgresException shortRow = await Assert.ThrowsExactlyAsync<PostgresException>(() => Query(connection, surface, 3, "SELECT ARRAY[7],42"));
        Assert.AreEqual("38000", shortRow.SqlState);
        Assert.AreEqual("The SPI result has 2 columns; column 3 was requested.", shortRow.MessageText);
        await Execute(connection, "CREATE TEMP TABLE mapped_array_writes(value integer)");
        Assert.AreEqual("23|1|1|1|1|1", await Query(connection, surface, 1,
            "INSERT INTO mapped_array_writes SELECT generate_series(1,4) RETURNING ARRAY[value],99"));
        Assert.AreEqual("1,2,3,4", await Scalar<string>(connection, "SELECT string_agg(value::text,',' ORDER BY value) FROM mapped_array_writes"));
        Assert.AreEqual("23|1|1|1|1|42", await Query(connection, surface, 1, "SELECT ARRAY[99]; SELECT ARRAY[42]"));
    }

    /// <summary>
    /// Managed element/factory/shape failures occur after completed SQL and release all captured temporary inputs.
    /// </summary>
    [TestMethod]
    public async Task MappedArrayManagedErrorsRetainCompletedSqlEffects()
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        await Execute(connection, "CREATE TEMP TABLE mapped_array_writes(value integer)");
        foreach ((int kind, string result, string diagnostic) in new[]
        {
            (0, "ARRAY[7,-777,99]", "P8521|mapped array reader failed|later element|replace the sentinel"),
            (1, "ARRAY[value]", "P8512|typed factory failed|lazy result|use another mapping"),
            (0, "'[0:1]={7,11}'::integer[]", "InvalidOperationException|Use PgArray<T> to preserve dimensions and lower bounds, or ToArray() to explicitly flatten them."),
            (3, "ARRAY[value],value::bigint", "InvalidCastException|An SPI value of type 'System.Int64' cannot be read as 'System.Int32'."),
        })
        {
            await Reset(connection);
            await Execute(connection, "TRUNCATE mapped_array_writes");
            string sql = $"INSERT INTO mapped_array_writes SELECT generate_series(1,4) RETURNING {result}";
            Assert.AreEqual(diagnostic, await Catch(connection, sql, 0, kind));
            Assert.AreEqual("1,2,3,4", await Scalar<string>(connection, "SELECT string_agg(value::text,',' ORDER BY value) FROM mapped_array_writes"));
            Assert.AreEqual(kind == 0 && result == "ARRAY[7,-777,99]" ? "2|0|0|0"
                : kind == 3 ? "1|0|0|0" : "0|0|0|0", await Owners(connection));
        }

        Assert.AreEqual("23|1|1|1|1|42", await Raw(connection, "SELECT ARRAY[42]"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Variadic, streaming/materialized arrays and TABLE rows preserve empty and NULL states with real deferred cleanup.
    /// </summary>
    /// <param name="function">The value-per-call or materialized set function.</param>
    [TestMethod]
    [DataRow("rows")]
    [DataRow("materialized")]
    public async Task MappedArrayDeferredSetsAndTablesRetainValuesAndDispose(string function)
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        Assert.AreEqual("5|2", await Scalar<string>(connection, "SELECT mapped_arrays.variadic(2,NULL,3)"));
        Assert.AreEqual("0|0", await Scalar<string>(connection, "SELECT mapped_arrays.variadic(VARIADIC ARRAY[]::integer[])"));
        Assert.AreEqual("NULL", await Scalar<string>(connection, "SELECT mapped_arrays.variadic(VARIADIC NULL::integer[])"));
        Assert.AreEqual(0L, await Scalar<long>(connection, $"SELECT count(*) FROM mapped_arrays.{function}(ARRAY['first',NULL,'last'],false,0)"));
        Assert.AreEqual(1, await Scalar<int>(connection, "SELECT mapped_arrays.disposals()"));
        Assert.AreSequenceEqual(["[0:2]={first,NULL,last}"], await Strings(connection,
            $"SELECT value::text FROM mapped_arrays.{function}('[0:2]={{first,NULL,last}}'::text[],false,1) value"));
        Assert.AreEqual(2, await Scalar<int>(connection, "SELECT mapped_arrays.disposals()"));
        Assert.AreSequenceEqual(["[0:2]={first,NULL,last}", "{}", "<NULL>", "[0:2]={first,NULL,last}"], await Strings(connection,
            $"SELECT coalesce(value::text,'<NULL>') FROM mapped_arrays.{function}('[0:2]={{first,NULL,last}}'::text[],false) value"));
        Assert.AreEqual(3, await Scalar<int>(connection, "SELECT mapped_arrays.disposals()"));
        Assert.AreSequenceEqual(["{early,NULL}"], await Strings(connection,
            $"SELECT value::text FROM mapped_arrays.{function}(ARRAY['early',NULL],false) value LIMIT 1"));
        Assert.AreEqual(4, await Scalar<int>(connection, "SELECT mapped_arrays.disposals()"));
        await Execute(connection, "CREATE TEMP TABLE mapped_array_set_results(value text[])");
        PostgresException failure = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection,
            $"INSERT INTO mapped_array_set_results SELECT value FROM mapped_arrays.{function}(ARRAY['first'],true) value"));
        Assert.AreEqual("P8525", failure.SqlState);
        Assert.AreEqual("mapped array iterator failed", failure.MessageText);
        Assert.AreEqual(0L, await Scalar<long>(connection, "SELECT count(*) FROM mapped_array_set_results"));
        Assert.AreEqual(5, await Scalar<int>(connection, "SELECT mapped_arrays.disposals()"));
        foreach (string table in new[] { "table", "table_materialized" })
        {
            Assert.AreSequenceEqual(["1|[0:2]={7,NULL,11}", "2|<NULL>", "3|{}"], await Strings(connection,
                $"SELECT position::text||'|'||coalesce(\"values\"::text,'<NULL>') FROM mapped_arrays.{table}('[0:2]={{7,NULL,11}}'::integer[]) ORDER BY position"));
        }

        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Aggregate transitions reread retained arrays while ordinary/moving helpers preserve every state element.
    /// </summary>
    [TestMethod]
    public async Task MappedArrayAggregatesRetainInputsAndMovingStates()
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual("empty", await Scalar<string>(connection,
            "SELECT mapped_arrays.retain(value) FROM (SELECT ARRAY['none'] AS value WHERE false) input"));
        Assert.AreEqual("0:first,NULL,last;NULL;:;1:later", await Scalar<string>(connection, """
            SELECT mapped_arrays.retain(value ORDER BY n) FROM (VALUES
            (1,'[0:2]={first,NULL,last}'::text[]),(2,NULL),(3,'{}'),(4,'{later}')) input(n,value)
            """));
        Assert.AreEqual("first,NULL,last;NULL;;later", await Scalar<string>(connection, """
            SELECT mapped_arrays.retain_vector(value ORDER BY n) FROM (VALUES
            (1,'{first,NULL,last}'::text[]),(2,NULL),(3,'{}'),(4,'{later}')) input(n,value)
            """));
        Assert.AreEqual("{}", await Scalar<string>(connection,
            "SELECT mapped_arrays.collect(value)::text FROM (SELECT 1 AS value WHERE false) input"));
        Assert.AreEqual("{NULL,NULL}", await Scalar<string>(connection,
            "SELECT mapped_arrays.collect(value ORDER BY n)::text FROM (VALUES(1,NULL::integer),(2,NULL)) input(n,value)"));
        Assert.AreEqual("{2,NULL,3,5}", await Scalar<string>(connection,
            "SELECT mapped_arrays.collect(value ORDER BY n)::text FROM (VALUES(1,2),(2,NULL),(3,3),(4,5)) input(n,value)"));
        Assert.AreSequenceEqual(["{2}", "{2,NULL}", "{2,NULL,3}", "{NULL,3,5}"], await Strings(connection, """
            SELECT mapped_arrays.collect(value) OVER(ORDER BY n ROWS BETWEEN 2 PRECEDING AND CURRENT ROW)::text
            FROM (VALUES(1,2),(2,NULL),(3,3),(4,5)) input(n,value) ORDER BY n
            """));
        await Execute(connection, """
            DO $block$
            DECLARE support text;
            BEGIN
                SELECT format('%I.%I',n.nspname,p.proname) INTO support
                FROM pg_aggregate a JOIN pg_proc p ON p.oid=a.aggcombinefn JOIN pg_namespace n ON n.oid=p.pronamespace
                WHERE a.aggfnoid='mapped_arrays.collect(integer)'::regprocedure;
                EXECUTE format('CREATE AGGREGATE pg_temp.mapped_array_combine(integer[]) (SFUNC=%s,STYPE=integer[],INITCOND=''{}'')',support);
            END $block$;
            """);
        Assert.AreEqual("{7,NULL,11,-9,0}", await Scalar<string>(connection, """
            SELECT pg_temp.mapped_array_combine(value ORDER BY n)::text
            FROM (VALUES (1,ARRAY[7,NULL,11]),(2,ARRAY[]::integer[]),(3,ARRAY[-9,0])) input(n,value)
            """));
    }

    /// <summary>
    /// Exact array result mismatches precede immutable callee and default-expression execution for name and OID calls.
    /// </summary>
    /// <param name="byOid">Whether calls resolve an exact catalog function OID.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MappedArrayCatalogMismatchPreventsCalleeAndDefaultEffects(bool byOid)
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        await Execute(connection, """
            CREATE TEMP SEQUENCE mapped_array_callee;
            CREATE TEMP SEQUENCE mapped_array_default;
            CREATE DOMAIN pg_temp.array_outer AS datum_mappings.positive[];
            CREATE FUNCTION pg_temp.array_default() RETURNS integer LANGUAGE plpgsql IMMUTABLE AS $f$
            BEGIN PERFORM nextval('mapped_array_default'); RETURN 7; END $f$;
            """);
        string expected = await Scalar<string>(connection, "SELECT format_type('datum_mappings.positive[]'::regtype,-1)");
        foreach (string type in new[] { "integer[]", "bigint[]", "datum_mappings.other_positive[]", "pg_temp.array_outer" })
        {
            string actual = await Scalar<string>(connection, $"SELECT format_type({Literal(type)}::regtype,-1)");
            foreach (bool absent in new[] { false, true })
            {
                await CreateTarget(connection, type, absent);
                uint oid = byOid ? await TargetOid(connection) : 0;
                foreach (bool useDefault in new[] { false, true })
                {
                    PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<string>(connection,
                        $"SELECT mapped_arrays.call('pg_temp.array_target',{oid}::oid,{useDefault})"));
                    Assert.AreEqual("42804", error.SqlState);
                    Assert.AreEqual($"Function result type {actual} does not match requested type {expected}", error.MessageText);
                    Assert.IsFalse(await Scalar<bool>(connection, "SELECT is_called FROM mapped_array_callee"));
                    Assert.IsFalse(await Scalar<bool>(connection, "SELECT is_called FROM mapped_array_default"));
                    Assert.AreEqual("0|0|0", await Counts(connection));
                    Assert.AreEqual("0|0|0|0|0|0", await Scalar<string>(connection, "SELECT mapped_results.counts()"));
                }
            }
        }

        await CreateTarget(connection, "datum_mappings.positive[]", false);
        uint valid = byOid ? await TargetOid(connection) : 0;
        Assert.AreEqual("7|NULL|11", await Scalar<string>(connection,
            $"SELECT mapped_arrays.call('pg_temp.array_target',{valid}::oid,false)"));
        Assert.AreEqual("7|NULL|11", await Scalar<string>(connection,
            $"SELECT mapped_arrays.call('pg_temp.array_target',{valid}::oid,true)"));
        Assert.AreEqual(2L, await Scalar<long>(connection, "SELECT last_value FROM mapped_array_callee"));
        Assert.AreEqual(2L, await Scalar<long>(connection, "SELECT last_value FROM mapped_array_default"));
        Assert.AreEqual("0|0|0|0|4|0", await Scalar<string>(connection, "SELECT mapped_results.counts()"));
        await CreateTarget(connection, "datum_mappings.positive[]", true);
        uint absentOid = byOid ? await TargetOid(connection) : 0;
        Assert.AreEqual("NULL", await Scalar<string>(connection,
            $"SELECT mapped_arrays.call('pg_temp.array_target',{absentOid}::oid,false)"));
        Assert.AreEqual(3L, await Scalar<long>(connection, "SELECT last_value FROM mapped_array_callee"));
        Assert.AreEqual(3L, await Scalar<long>(connection, "SELECT last_value FROM mapped_array_default"));
        Assert.AreEqual("0|0|0|0|4|0", await Scalar<string>(connection, "SELECT mapped_results.counts()"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Catching a managed catalog-array reader or factory error preserves every completed callee write.
    /// </summary>
    /// <param name="byOid">Whether calls use exact function OIDs.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MappedArrayCaughtCatalogErrorsRetainCompletedWrites(bool byOid)
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        await Execute(connection, """
            CREATE TEMP TABLE mapped_array_catalog_writes(value integer);
            CREATE FUNCTION pg_temp.array_reader_result() RETURNS integer[] LANGUAGE plpgsql VOLATILE AS $f$
            BEGIN INSERT INTO mapped_array_catalog_writes VALUES(2),(5),(9); RETURN ARRAY[7,-777,99]; END $f$;
            CREATE FUNCTION pg_temp.array_factory_result() RETURNS integer[] LANGUAGE plpgsql VOLATILE AS $f$
            BEGIN INSERT INTO mapped_array_catalog_writes VALUES(11),(17); RETURN ARRAY[42]; END $f$;
            CREATE FUNCTION pg_temp.array_valid_result() RETURNS integer[] LANGUAGE sql AS $f$ SELECT ARRAY[73] $f$;
            """);
        uint reader = byOid ? await Scalar<uint>(connection, "SELECT 'pg_temp.array_reader_result()'::regprocedure::oid") : 0;
        uint factory = byOid ? await Scalar<uint>(connection, "SELECT 'pg_temp.array_factory_result()'::regprocedure::oid") : 0;
        uint valid = byOid ? await Scalar<uint>(connection, "SELECT 'pg_temp.array_valid_result()'::regprocedure::oid") : 0;
        Assert.AreEqual("P8521|mapped array reader failed|later element|replace the sentinel",
            await Catch(connection, "pg_temp.array_reader_result", reader, 2));
        Assert.AreEqual("2,5,9", await Scalar<string>(connection,
            "SELECT string_agg(value::text,',' ORDER BY value) FROM mapped_array_catalog_writes"));
        Assert.AreEqual("1|2|0", await Counts(connection));
        Assert.AreEqual("2|0|0|0", await Owners(connection));
        for (int attempt = 0; attempt < 2; attempt++)
        {
            await Execute(connection, "TRUNCATE mapped_array_catalog_writes");
            Assert.AreEqual("P8512|typed factory failed|lazy result|use another mapping",
                await Catch(connection, "pg_temp.array_factory_result", factory, 4));
            Assert.AreEqual("11,17", await Scalar<string>(connection,
                "SELECT string_agg(value::text,',' ORDER BY value) FROM mapped_array_catalog_writes"));
            Assert.AreEqual("0|0|0|0|0|1", await Scalar<string>(connection, "SELECT mapped_results.counts()"));
        }

        Assert.AreEqual("73", await Catch(connection, "pg_temp.array_valid_result", valid, 2));
        Assert.AreEqual("1|3|0", await Counts(connection));
        Assert.AreEqual("3|0|0|0", await Owners(connection));
        Assert.AreEqual("23|1|1|1|1|73", await Raw(connection, "SELECT ARRAY[73]"));
        Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// External element and array OIDs refresh after DDL while retained present, empty and NULL parameters stay exact.
    /// </summary>
    [TestMethod]
    public async Task MappedArraysRefreshExternalIdentityWithoutRebindingSavedParameters()
    {
        await using NpgsqlConnection connection = await Open();
        int process = connection.ProcessID;
        await Execute(connection, "CREATE SCHEMA mapped_array_live; CREATE TEMP SEQUENCE mapped_array_effect");
        try
        {
            int writes = 0;
            foreach ((int kind, string values) in new[] { (0, "7,NULL,11"), (1, string.Empty), (2, "NULL") })
            {
                await Execute(connection, "CREATE DOMAIN mapped_array_live.value AS integer CHECK(VALUE>0)");
                uint firstElement = await Scalar<uint>(connection, "SELECT 'mapped_array_live.value'::regtype::oid");
                uint firstArray = await Scalar<uint>(connection, "SELECT 'mapped_array_live.value[]'::regtype::oid");
                writes += kind == 0 ? 2 : 0;
                Assert.AreEqual($"{firstArray}|{values}|{writes}", await Scalar<string>(connection,
                    $"SELECT mapped_arrays.live({kind},true)"));
                await Execute(connection, "DROP DOMAIN mapped_array_live.value");
                PostgresException missing = await Assert.ThrowsExactlyAsync<PostgresException>(() => Scalar<string>(connection,
                    $"SELECT mapped_arrays.live({kind},false)"));
                Assert.AreEqual("42704", missing.SqlState);
                Assert.AreEqual("PostgreSQL concrete defined type \"value\" does not exist in the declared schema", missing.MessageText);
                Assert.AreEqual(writes, await Scalar<int>(connection, "SELECT mapped_arrays.live_writes()"));
                await Execute(connection, "CREATE DOMAIN mapped_array_live.value AS integer CHECK(VALUE>0)");
                uint secondElement = await Scalar<uint>(connection, "SELECT 'mapped_array_live.value'::regtype::oid");
                uint secondArray = await Scalar<uint>(connection, "SELECT 'mapped_array_live.value[]'::regtype::oid");
                Assert.AreNotEqual(firstElement, secondElement);
                Assert.AreNotEqual(firstArray, secondArray);
                PostgresException stale = await Assert.ThrowsExactlyAsync<PostgresException>(() => Execute(connection, "SELECT mapped_arrays.saved()"));
                Assert.AreEqual("38000", stale.SqlState);
                Assert.AreEqual("The mapped PostgreSQL parameter type has changed since the parameter was created.", stale.MessageText);
                Assert.AreEqual(writes, await Scalar<int>(connection, "SELECT mapped_arrays.live_writes()"));
                Assert.IsFalse(await Scalar<bool>(connection, "SELECT is_called FROM mapped_array_effect"));
                writes += kind == 0 ? 2 : 0;
                Assert.AreEqual($"{secondArray}|{values}|{writes}", await Scalar<string>(connection,
                    $"SELECT mapped_arrays.live({kind},false)"));
                await Execute(connection, """
                    CREATE FUNCTION pg_temp.mapped_array_live_result() RETURNS mapped_array_live.value[] LANGUAGE sql
                    AS $f$ SELECT ARRAY[7,NULL,11]::mapped_array_live.value[] $f$;
                    """);
                uint function = await Scalar<uint>(connection, "SELECT 'pg_temp.mapped_array_live_result()'::regprocedure::oid");
                Assert.AreEqual("7,NULL,11", await Scalar<string>(connection, "SELECT mapped_arrays.live_call(0::oid)"));
                Assert.AreEqual("7,NULL,11", await Scalar<string>(connection, $"SELECT mapped_arrays.live_call({function}::oid)"));
                await Execute(connection, "DROP DOMAIN mapped_array_live.value CASCADE");
            }

            Assert.AreEqual(process, await Scalar<int>(connection, "SELECT pg_backend_pid()"));
        }
        finally
        {
            await Execute(connection, "DROP SCHEMA mapped_array_live CASCADE");
        }
    }

    /// <summary>
    /// A canonical row or tuple array reads its elements through the requested mapping, as raw arrays do.
    /// </summary>
    /// <param name="tuple">Whether to select the ordinary heap tuple path.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task MappedArraysReadCanonicalRowAndTupleCells(bool tuple)
    {
        await using NpgsqlConnection connection = await Open();
        Assert.AreEqual(1, await Scalar<int>(connection, $"SELECT mapped_arrays.ordinary_read({tuple})"));
        Assert.AreEqual("1|1|0", await Counts(connection));
        Assert.AreEqual("23|1|1|1|1|7", await Raw(connection, "SELECT ARRAY[7]"));
    }

    /// <summary>
    /// Opens a fresh backend for independent lazy factories and temporary fixture objects.
    /// </summary>
    private Task<NpgsqlConnection> Open() => PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);

    /// <summary>
    /// Reads independently tracked element conversion counters.
    /// </summary>
    private Task<string> Counts(NpgsqlConnection connection) => Scalar<string>(connection, "SELECT mapped_arrays.counts()");

    /// <summary>
    /// Reads the number of captured and still-live inputs/destinations.
    /// </summary>
    private Task<string> Owners(NpgsqlConnection connection) => Scalar<string>(connection, "SELECT mapped_arrays.owners()");

    /// <summary>
    /// Starts an isolated observation without clearing the lazy factory instance.
    /// </summary>
    private Task Reset(NpgsqlConnection connection) => Execute(connection, "SELECT mapped_arrays.reset()");

    /// <summary>
    /// Executes one raw-array target conversion.
    /// </summary>
    private Task<string> Raw(NpgsqlConnection connection, string sql, bool vector = false, bool required = false)
        => Scalar<string>(connection, $"SELECT mapped_arrays.raw({Literal(sql)},{vector},{required})");

    /// <summary>
    /// Selects an exact array-of-domain or explicitly mapped outer-domain scalar.
    /// </summary>
    private Task<string> Positive(NpgsqlConnection connection, string sql, bool scalar = false)
        => Scalar<string>(connection, $"SELECT mapped_arrays.positive({Literal(sql)},{scalar})");

    /// <summary>
    /// Executes one first-row typed-array query.
    /// </summary>
    private Task<string> Query(NpgsqlConnection connection, int surface, int width, string sql)
        => Scalar<string>(connection, $"SELECT mapped_arrays.query({surface},{width},{Literal(sql)})");

    /// <summary>
    /// Captures a managed post-execution error without rolling back its caller's successful SQL.
    /// </summary>
    private Task<string> Catch(NpgsqlConnection connection, string sql, uint oid, int kind)
        => Scalar<string>(connection, $"SELECT mapped_arrays.catch({Literal(sql)},{oid}::oid,{kind})");

    /// <summary>
    /// Replaces a catalog target without reusing previous prepared result metadata.
    /// </summary>
    private Task CreateTarget(NpgsqlConnection connection, string type, bool absent) => Execute(connection, $"""
        DROP FUNCTION IF EXISTS pg_temp.array_target(integer);
        CREATE FUNCTION pg_temp.array_target(value integer DEFAULT pg_temp.array_default()) RETURNS {type}
        LANGUAGE plpgsql IMMUTABLE AS $f$
        BEGIN PERFORM nextval('mapped_array_callee'); RETURN {(absent ? "NULL" : $"ARRAY[value,NULL,11]::{type}")}; END $f$;
        """);

    /// <summary>
    /// Resolves the exact newly defined array-returning signature.
    /// </summary>
    private Task<uint> TargetOid(NpgsqlConnection connection)
        => Scalar<uint>(connection, "SELECT 'pg_temp.array_target(integer)'::regprocedure::oid");

    /// <summary>
    /// Quotes controlled SQL text as one string literal.
    /// </summary>
    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    /// <summary>
    /// Reads one copied PostgreSQL value without changing connections.
    /// </summary>
    private async Task<T> Scalar<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
        Assert.IsTrue(await reader.ReadAsync(context.CancellationToken), "The scalar fixture must return one row.");
        return await reader.GetFieldValueAsync<T>(0, context.CancellationToken);
    }

    /// <summary>
    /// Completes all setup/cleanup results before issuing the next assertion.
    /// </summary>
    private async Task Execute(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }

    /// <summary>
    /// Reads all independent expected text rows in executor order.
    /// </summary>
    private async Task<string[]> Strings(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(context.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }
}
