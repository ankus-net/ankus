using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Owned scalar, range and array mappings resolve current extension identities through relocation and reinstall independently of external mappings.
    /// </summary>
    [TestMethod]
    public async Task DatumMappingPackageRelocatesAndReinstallsWithCurrentTypeIdentity()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "DatumMappings.csproj");
        File.Copy(s_project, project);
        await File.WriteAllTextAsync(Path.Join(directory, "Mappings.cs"), DatumMappingPackageSource, token);
        string output = Path.Join(directory, "published");
        await PublishSqlControlsAsync(project, output, token);
        PublishedExtension manifest = PublishedExtension.Read(output);
        string sql = await File.ReadAllTextAsync(Path.Join(output, "extension", manifest.Sql), token);
        Assert.Contains("relocatable = true", await File.ReadAllTextAsync(Path.Join(output, "extension", manifest.Control), token));
        int provider = sql.IndexOf("CREATE DOMAIN package_key", StringComparison.Ordinal);
        int consumer = sql.IndexOf("CREATE FUNCTION \"package_echo\"", StringComparison.Ordinal);
        int arrayConsumer = sql.IndexOf("CREATE FUNCTION \"package_array_echo\"", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, provider);
        Assert.IsGreaterThan(provider, consumer);
        Assert.IsGreaterThan(provider, arrayConsumer);
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        int process = connection.ProcessID;
        await ExecuteSqlPackageAsync(connection, """
            CREATE SCHEMA mapping_first; CREATE SCHEMA mapping_second; CREATE SCHEMA mapping_shadow;
            CREATE DOMAIN mapping_shadow.package_key AS bigint CHECK (VALUE < 0);
            CREATE DOMAIN mapping_shadow.int4 AS text CHECK (VALUE LIKE 'shadow%');
            CREATE TYPE mapping_shadow.package_range AS RANGE (subtype=bigint);
            CREATE FUNCTION mapping_shadow.package_echo(mapping_shadow.package_key) RETURNS mapping_shadow.package_key
                LANGUAGE SQL IMMUTABLE AS 'SELECT $1';
            SET search_path=mapping_shadow,pg_catalog;
            """);
        uint[] shadow = await DatumMappingShadowIdentities(connection);
        await ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_tool_probe WITH SCHEMA mapping_first");
        Dictionary<string, uint> original = await DatumMappingPackageMembers(connection, "mapping_first");
        uint originalType = original["type:package_key"];
        await AssertDatumMappingPackage(connection, "mapping_first", originalType);
        Assert.AreEqual(originalType, await SqlPackageScalarAsync<uint>(connection, "SELECT mapping_first.package_remember(42)"));
        Assert.AreEqual(1042, await SqlPackageScalarAsync<int>(connection, "SELECT mapping_first.package_replay()"));
        Assert.AreEqual("1:0:1042,NULL,1000", await SqlPackageScalarAsync<string>(connection, "SELECT mapping_first.package_array_replay()"));
        Assert.AreEqual("[42,44)", await SqlPackageScalarAsync<string>(connection, "SELECT mapping_first.package_range_replay()"));

        await ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_tool_probe SET SCHEMA mapping_second");
        Dictionary<string, uint> moved = await DatumMappingPackageMembers(connection, "mapping_second");
        Assert.AreSequenceEqual(original.Keys, moved.Keys);
        foreach ((string name, uint oid) in original)
        {
            Assert.AreEqual(oid, moved[name], name);
        }

        await AssertDeclarationSchemaEmpty(connection, "mapping_first");
        await AssertDatumMappingPackage(connection, "mapping_second", originalType);
        Assert.AreEqual(1042, await SqlPackageScalarAsync<int>(connection, "SELECT mapping_second.package_replay()"));
        Assert.AreEqual("1:0:1042,NULL,1000", await SqlPackageScalarAsync<string>(connection, "SELECT mapping_second.package_array_replay()"));
        Assert.AreEqual("[42,44)", await SqlPackageScalarAsync<string>(connection, "SELECT mapping_second.package_range_replay()"));
        await ExecuteSqlPackageAsync(connection, "DROP EXTENSION ankus_tool_probe");
        await AssertDeclarationSchemaEmpty(connection, "mapping_second");
        Assert.IsFalse(await SqlPackageScalarAsync<bool>(connection,
            "SELECT EXISTS(SELECT FROM pg_extension WHERE extname='ankus_tool_probe')"));
        Assert.AreSequenceEqual(shadow, await DatumMappingShadowIdentities(connection));
        Assert.AreEqual("-7|shadow value", await SqlPackageScalarAsync<string>(connection, """
            SELECT mapping_shadow.package_echo('-7')::text||'|'||'shadow value'::mapping_shadow.int4::text
            """));

        await ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_tool_probe WITH SCHEMA mapping_first");
        Dictionary<string, uint> reinstalled = await DatumMappingPackageMembers(connection, "mapping_first");
        Assert.AreSequenceEqual(original.Keys, reinstalled.Keys);
        foreach ((string name, uint oid) in original)
        {
            Assert.AreNotEqual(oid, reinstalled[name], name);
        }

        PostgresException stale = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "SELECT mapping_first.package_replay()"));
        Assert.AreEqual("38000", stale.SqlState);
        Assert.AreEqual("The mapped PostgreSQL parameter type has changed since the parameter was created.", stale.MessageText);
        PostgresException staleArray = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "SELECT mapping_first.package_array_replay()"));
        Assert.AreEqual("38000", staleArray.SqlState);
        Assert.AreEqual("The mapped PostgreSQL parameter type has changed since the parameter was created.", staleArray.MessageText);
        PostgresException staleRange = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "SELECT mapping_first.package_range_replay()"));
        Assert.AreEqual("38000", staleRange.SqlState);
        Assert.AreEqual("The mapped PostgreSQL parameter type has changed since the parameter was created.", staleRange.MessageText);
        uint newType = reinstalled["type:package_key"];
        await AssertDatumMappingPackage(connection, "mapping_first", newType);
        Assert.AreEqual(newType, await SqlPackageScalarAsync<uint>(connection, "SELECT mapping_first.package_remember(7)"));
        Assert.AreEqual(1007, await SqlPackageScalarAsync<int>(connection, "SELECT mapping_first.package_replay()"));
        Assert.AreEqual("1:0:1007,NULL,1000", await SqlPackageScalarAsync<string>(connection, "SELECT mapping_first.package_array_replay()"));
        Assert.AreEqual("[7,9)", await SqlPackageScalarAsync<string>(connection, "SELECT mapping_first.package_range_replay()"));
        Assert.AreEqual(process, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
        await ExecuteSqlPackageAsync(connection, "DROP EXTENSION ankus_tool_probe");
        await AssertDeclarationSchemaEmpty(connection, "mapping_first");
        await AssertDeclarationSchemaEmpty(connection, "mapping_second");
        Assert.AreSequenceEqual(shadow, await DatumMappingShadowIdentities(connection));
        Assert.AreEqual(23U, await SqlPackageScalarAsync<uint>(connection, "SELECT 'pg_catalog.int4'::regtype::oid"));
    }

    /// <summary>
    /// Executes independently expected converter results and domain failures after each catalog lifecycle phase.
    /// </summary>
    private async Task AssertDatumMappingPackage(NpgsqlConnection connection, string schema, uint typeOid)
    {
        int process = connection.ProcessID;
        Assert.AreEqual("1|43|1042|true|8", await SqlPackageScalarAsync<string>(connection, $"""
            SELECT concat_ws('|',{schema}.package_echo(0)::integer,{schema}.package_echo(42)::integer,
                {schema}.package_read(42),({schema}.package_echo(NULL::{schema}.package_key) IS NULL)::text,
                {schema}.package_external_echo(7))
            """));
        Assert.AreEqual("1042|42|true", await SqlPackageScalarAsync<string>(connection, $"""
            SELECT concat_ws('|',{schema}.package_generic_read(42),
                {schema}.package_generic_echo(42)::integer,
                ({schema}.package_generic_echo(NULL::{schema}.package_key) IS NULL)::text)
            """));
        Assert.AreEqual("14294967313|4294967313|true", await SqlPackageScalarAsync<string>(connection, $"""
            SELECT concat_ws('|',{schema}.package_generic_wide_read(4294967313),
                {schema}.package_generic_wide_echo(4294967313)::bigint,
                ({schema}.package_generic_wide_echo(NULL::{schema}.package_wide_key) IS NULL)::text)
            """));
        Assert.AreEqual("[2,7)", await SqlPackageScalarAsync<string>(connection,
            $"SELECT {schema}.package_range_echo('[2,7)')::text"));
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, $"""
            SELECT {schema}.package_range_echo(NULL) IS NULL
                AND (SELECT rngsubtype='{schema}.package_key'::regtype FROM pg_range WHERE rngtypid='{schema}.package_range'::regtype)
                AND (SELECT proargtypes[0]='{schema}.package_range'::regtype AND prorettype='{schema}.package_range'::regtype
                    FROM pg_proc WHERE oid='{schema}.package_range_echo({schema}.package_range)'::regprocedure)
            """));
        Assert.AreEqual(FormattableString.Invariant($"{typeOid}|1042|True|True|23|2042"),
            await SqlPackageScalarAsync<string>(connection, $"SELECT {schema}.package_probe(42)"));
        Assert.AreEqual("1042|1043|1043|2042|True|True",
            await SqlPackageScalarAsync<string>(connection, $"SELECT {schema}.package_typed()"));
        Assert.AreEqual("1:0:1042,NULL,1000|1:0:1043,NULL,1001|1:0:1043,NULL,1001|2000,2042|True|True",
            await SqlPackageScalarAsync<string>(connection, $"SELECT {schema}.package_arrays()"));
        Assert.AreEqual("[0:2]={43,NULL,1}", await SqlPackageScalarAsync<string>(connection,
            $"SELECT {schema}.package_array_echo('[0:2]={{42,NULL,0}}'::{schema}.package_key[])::text"));
        Assert.AreEqual("{}", await SqlPackageScalarAsync<string>(connection,
            $"SELECT {schema}.package_array_echo(ARRAY[]::{schema}.package_key[])::text"));
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, $"""
            SELECT (SELECT proargtypes[0]='{schema}.package_key'::regtype
                    AND prorettype='{schema}.package_key'::regtype AND NOT proisstrict
                    FROM pg_proc WHERE oid='{schema}.package_echo({schema}.package_key)'::regprocedure)
                AND (SELECT proargtypes[0]=23 AND prorettype=23 AND proisstrict
                    FROM pg_proc WHERE oid='{schema}.package_external_echo(integer)'::regprocedure)
                AND (SELECT proargtypes[0]='{schema}.package_key[]'::regtype
                    AND prorettype='{schema}.package_key[]'::regtype AND NOT proisstrict
                    FROM pg_proc WHERE oid='{schema}.package_array_echo({schema}.package_key[])'::regprocedure)
                AND (SELECT proargtypes[0]='{schema}.package_wide_key'::regtype
                    AND prorettype='{schema}.package_wide_key'::regtype AND NOT proisstrict
                    FROM pg_proc WHERE oid='{schema}.package_generic_wide_echo({schema}.package_wide_key)'::regprocedure)
                AND (SELECT typtype='d' AND typbasetype=23 AND typlen=4 AND typbyval
                    FROM pg_type WHERE oid='{schema}.package_key'::regtype)
            """));
        PostgresException rejected = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, $"SELECT {schema}.package_probe(-1)"));
        Assert.AreEqual("23514", rejected.SqlState);
        Assert.AreEqual($"value for domain {schema}.package_key violates check constraint \"package_key_check\"", rejected.MessageText);
        Assert.AreEqual(FormattableString.Invariant($"{typeOid}|1000|True|True|23|2000"),
            await SqlPackageScalarAsync<string>(connection, $"SELECT {schema}.package_probe(0)"));
        Assert.AreEqual(process, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Captures both domains, their range, the corresponding arrays and every generated callback with exact extension ownership.
    /// </summary>
    private async Task<Dictionary<string, uint>> DatumMappingPackageMembers(NpgsqlConnection connection, string schema)
    {
        string[] expected = ["array:package_key", "array:package_range", "array:package_wide_key", "function:package_array_echo", "function:package_array_replay",
            "function:package_arrays", "function:package_echo", "function:package_external_echo",
            "function:package_generic_echo", "function:package_generic_read",
            "function:package_generic_wide_echo", "function:package_generic_wide_read",
            "function:package_probe", "function:package_range_echo", "function:package_range_replay",
            "function:package_read", "function:package_remember", "function:package_replay",
            "function:package_typed", "type:package_key", "type:package_range", "type:package_wide_key"];
        await using var command = new NpgsqlCommand($"""
            WITH objects(label,classid,objid,ownerid) AS (
                SELECT 'type:'||typname,'pg_type'::regclass,oid,oid FROM pg_type
                    WHERE typnamespace='{schema}'::regnamespace AND typname IN ('package_key','package_range','package_wide_key')
                UNION ALL SELECT 'array:'||t.typname,'pg_type'::regclass,a.oid,t.oid FROM pg_type t JOIN pg_type a
                    ON a.oid=t.typarray AND a.typelem=t.oid AND a.typnamespace=t.typnamespace
                    WHERE t.typnamespace='{schema}'::regnamespace AND t.typname IN ('package_key','package_range','package_wide_key')
                UNION ALL SELECT 'function:'||proname,'pg_proc'::regclass,oid,oid FROM pg_proc
                    WHERE pronamespace='{schema}'::regnamespace
                        AND prolang=(SELECT oid FROM pg_language WHERE lanname='c'))
            SELECT label,objid,EXISTS(SELECT FROM pg_depend d JOIN pg_extension e ON e.oid=d.refobjid
                WHERE d.classid=objects.classid AND d.objid=objects.ownerid AND d.refclassid='pg_extension'::regclass
                    AND d.deptype='e' AND e.extname='ankus_tool_probe') FROM objects ORDER BY label COLLATE "C"
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
        var objects = new Dictionary<string, uint>(StringComparer.Ordinal);
        while (await reader.ReadAsync(context.CancellationToken))
        {
            string label = reader.GetString(0);
            Assert.IsTrue(reader.GetBoolean(2), label);
            objects.Add(label, reader.GetFieldValue<uint>(1));
        }

        Assert.AreSequenceEqual(expected, objects.Keys);
        return objects;
    }

    /// <summary>
    /// Retains unrelated same-name domain and function identities as a schema-resolution and drop witness.
    /// </summary>
    private Task<uint[]> DatumMappingShadowIdentities(NpgsqlConnection connection)
        => SqlPackageScalarAsync<uint[]>(connection, """
            SELECT ARRAY['mapping_shadow.package_key'::regtype::oid,'mapping_shadow.package_key[]'::regtype::oid,
                'mapping_shadow.int4'::regtype::oid,'mapping_shadow.int4[]'::regtype::oid,
                'mapping_shadow.package_range'::regtype::oid,'mapping_shadow.package_range[]'::regtype::oid,
                'mapping_shadow.package_echo(mapping_shadow.package_key)'::regprocedure::oid]
            """);

    /// <summary>
    /// Supplies a relocatable owned domain plus a fixed external wrapper with distinguishable reader results.
    /// </summary>
    private const string DatumMappingPackageSource = """
        using Ankus;
        [assembly: PgSql("a-mapping-range", "CREATE TYPE package_range AS RANGE (subtype=package_key);", Relocatable = true)]
        [assembly: PgSqlTypeProvider("a-mapping-range", typeof(PgRange<GenericOwnedKey<int>>))]
        [assembly: PgSql("mapping-type", "CREATE DOMAIN package_key AS integer CHECK (VALUE >= 0);", Relocatable = true)]
        [assembly: PgSql("mapping-wide-type", "CREATE DOMAIN package_wide_key AS bigint CHECK (VALUE >= 0);", Relocatable = true)]
        [assembly: PgSqlTypeProvider("mapping-type", typeof(OwnedKey))]
        [assembly: PgSqlTypeProvider("mapping-type", typeof(GenericOwnedKey<int>))]
        [assembly: PgSqlTypeProvider("mapping-wide-type", typeof(GenericOwnedKey<long>))]
        [PgDatumType("package_key", typeof(OwnedKeyConverter))]
        public readonly record struct OwnedKey(int Number);
        public sealed class OwnedKeyConverter : IPgDatumReader<OwnedKey>, IPgDatumWriter<OwnedKey>
        {
            public OwnedKey Read(PgDatum value) => new(value.Read<int>() + 1000);
            public PgDatum Write(OwnedKey value, uint typeOid, PgMemoryContext destination)
            {
                unsafe
                {
                    return PgDatum.DangerousCreate(unchecked((nuint)(nint)(value.Number - 1000)), typeOid, destination);
                }
            }
        }
        [PgDatumType(typeof(GenericOwnedKey<int>), "package_key", typeof(GenericOwnedKeyConverter))]
        [PgDatumType(typeof(GenericOwnedKey<long>), "package_wide_key", typeof(GenericOwnedKeyConverter))]
        [PgRangeType(typeof(GenericOwnedKey<int>), "package_range")]
        public readonly record struct GenericOwnedKey<T>(T Number);
        public sealed class GenericOwnedKeyConverter : IPgDatumReader<GenericOwnedKey<int>>, IPgDatumWriter<GenericOwnedKey<int>>,
            IPgDatumReader<GenericOwnedKey<long>>, IPgDatumWriter<GenericOwnedKey<long>>
        {
            GenericOwnedKey<int> IPgDatumReader<GenericOwnedKey<int>>.Read(PgDatum value) => new(value.Read<int>() + 1000);
            GenericOwnedKey<long> IPgDatumReader<GenericOwnedKey<long>>.Read(PgDatum value) => new(value.Read<long>() + 10000000000L);
            public PgDatum Write(GenericOwnedKey<int> value, uint typeOid, PgMemoryContext destination)
            {
                unsafe
                {
                    return PgDatum.DangerousCreate(unchecked((nuint)(nint)(value.Number - 1000)), typeOid, destination);
                }
            }

            public PgDatum Write(GenericOwnedKey<long> value, uint typeOid, PgMemoryContext destination)
            {
                unsafe
                {
                    return PgDatum.DangerousCreate(unchecked((nuint)(value.Number - 10000000000L)), typeOid, destination);
                }
            }
        }
        [PgDatumType("int4", typeof(ExternalKeyConverter), Schema = "pg_catalog", Origin = PgTypeOrigin.External)]
        public readonly record struct ExternalKey(int Number);
        public sealed class ExternalKeyConverter : IPgDatumReader<ExternalKey>, IPgDatumWriter<ExternalKey>
        {
            public ExternalKey Read(PgDatum value) => new(value.Read<int>() + 2000);
            public PgDatum Write(ExternalKey value, uint typeOid, PgMemoryContext destination)
            {
                unsafe
                {
                    return PgDatum.DangerousCreate(unchecked((nuint)(nint)(value.Number - 2000)), typeOid, destination);
                }
            }
        }
        public static class Functions
        {
            private static SpiParameter s_remembered;
            private static SpiParameter s_rememberedArray;
            private static SpiParameter s_rememberedRange;
            [PgFunction(Name = "package_range_echo")]
            public static PgRange<GenericOwnedKey<int>>? RangeEcho(PgRange<GenericOwnedKey<int>>? value) => value;
            [PgFunction(Name = "package_range_replay")]
            public static string RangeReplay() => Spi.ExecuteScalar<PgRange<GenericOwnedKey<int>>>("SELECT $1", s_rememberedRange).ToPostgresString();
            [PgFunction(Name = "package_echo")]
            public static OwnedKey? Echo(OwnedKey? value) => value is { } present ? new OwnedKey(present.Number + 1) : null;
            [PgFunction(Name = "package_read")]
            public static int ReadKey(OwnedKey value) => value.Number;
            [PgFunction(Name = "package_generic_echo")]
            public static GenericOwnedKey<int>? GenericEcho(GenericOwnedKey<int>? value) => value;
            [PgFunction(Name = "package_generic_read")]
            public static int GenericRead(GenericOwnedKey<int> value) => value.Number;
            [PgFunction(Name = "package_generic_wide_echo")]
            public static GenericOwnedKey<long>? GenericWideEcho(GenericOwnedKey<long>? value) => value;
            [PgFunction(Name = "package_generic_wide_read")]
            public static long GenericWideRead(GenericOwnedKey<long> value) => value.Number;
            [PgFunction(Name = "package_external_echo")]
            public static ExternalKey ExternalEcho(ExternalKey value) => new(value.Number + 1);
            [PgFunction(Name = "package_probe")]
            public static string Probe(int value)
            {
                using SpiRawResult result = Spi.QueryRaw("SELECT $1,$2,$3",
                    SpiParameter.Create(new OwnedKey(value + 1000)), SpiParameter.Create<OwnedKey?>(null),
                    SpiParameter.Create(new ExternalKey(value + 2000)));
                PgDatum own = result[0][0];
                PgDatum absent = result[0][1];
                PgDatum external = result[0][2];
                return FormattableString.Invariant($"{own.TypeOid}|{own.Read<OwnedKey>().Number}|{absent.IsNull}|{absent.Read<OwnedKey?>() is null}|{external.TypeOid}|{external.Read<ExternalKey>().Number}");
            }
            [PgFunction(Name = "package_remember")]
            public static uint Remember(int value)
            {
                s_remembered = SpiParameter.Create(new OwnedKey(value + 1000));
                s_rememberedArray = SpiParameter.Create(new PgArray<OwnedKey?>([new OwnedKey(value + 1000), null, new OwnedKey(1000)], [3], [0]));
                s_rememberedRange = SpiParameter.Create(new PgRange<GenericOwnedKey<int>>(new(value + 1000), new(value + 1002)));
                return s_remembered.TypeOid;
            }
            [PgFunction(Name = "package_replay")]
            public static int Replay()
                => Spi.ExecuteScalar<OwnedKey>("SELECT $1", s_remembered).Number;
            [PgFunction(Name = "package_array_replay")]
            public static string ReplayArray()
                => FormatArray(Spi.ExecuteScalar<PgArray<OwnedKey?>>("SELECT $1", s_rememberedArray));
            [PgFunction(Name = "package_array_echo")]
            public static PgArray<OwnedKey?>? EchoArray(PgArray<OwnedKey?>? values)
                => values is null ? null : new PgArray<OwnedKey?>(
                    values.Select(value => value is { } present ? (OwnedKey?)new OwnedKey(present.Number + 1) : null).ToArray(),
                    values.Lengths, values.LowerBounds);
            [PgFunction(Name = "package_arrays")]
            public static string Arrays(PgFunctionContext call)
            {
                string schema = Spi.ExecuteScalar<string>(
                    "SELECT n.nspname::text FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE p.oid=$1",
                    SpiParameter.Create(call.FunctionOid));
                string type = Spi.QuoteQualifiedIdentifier(schema, "package_key");
                string function = Spi.QuoteQualifiedIdentifier(schema, "package_array_echo");
                uint oid = Spi.ExecuteScalar<uint>("SELECT $1::regprocedure::oid", SpiParameter.Create($"{function}({type}[])"));
                PgArray<OwnedKey?> queried = Spi.ExecuteScalar<PgArray<OwnedKey?>>("SELECT '[0:2]={42,NULL,0}'::" + type + "[]");
                PgArray<OwnedKey?> named = PgFunctions.Call<PgArray<OwnedKey?>>(function, PgFunctionArgument.Create(queried));
                PgArray<OwnedKey?> identified = PgFunctions.Call<PgArray<OwnedKey?>>(oid, PgFunctionArgument.Create(queried));
                ExternalKey[] external = Spi.ExecuteScalar<ExternalKey[]>("SELECT ARRAY[0,42]");
                bool queryNull = Spi.ExecuteScalar<PgArray<OwnedKey?>?>("SELECT NULL::" + type + "[]") is null;
                bool callNull = PgFunctions.Call<PgArray<OwnedKey?>?>(function, PgFunctionArgument.Create<PgArray<OwnedKey?>?>(null)) is null;
                return $"{FormatArray(queried)}|{FormatArray(named)}|{FormatArray(identified)}|{string.Join(',', external.Select(value => value.Number))}|{queryNull}|{callNull}";
            }
            private static string FormatArray(PgArray<OwnedKey?> values)
                => $"{values.Rank}:{string.Join(',', values.LowerBounds.ToArray())}:{string.Join(',', values.Select(value => value?.Number.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "NULL"))}";
            [PgFunction(Name = "package_typed")]
            public static string Typed(PgFunctionContext call)
            {
                string schema = Spi.ExecuteScalar<string>(
                    "SELECT n.nspname::text FROM pg_proc p JOIN pg_namespace n ON n.oid=p.pronamespace WHERE p.oid=$1",
                    SpiParameter.Create(call.FunctionOid));
                string type = Spi.QuoteQualifiedIdentifier(schema, "package_key");
                string function = Spi.QuoteQualifiedIdentifier(schema, "package_echo");
                uint oid = Spi.ExecuteScalar<uint>("SELECT $1::regprocedure::oid", SpiParameter.Create($"{function}({type})"));
                OwnedKey queried = Spi.ExecuteScalar<OwnedKey>("SELECT 42::" + type);
                OwnedKey named = PgFunctions.Call<OwnedKey>(function, PgFunctionArgument.Create(new OwnedKey(1042)));
                OwnedKey identified = PgFunctions.Call<OwnedKey>(oid, PgFunctionArgument.Create(new OwnedKey(1042)));
                ExternalKey external = PgFunctions.Call<ExternalKey>("pg_catalog.abs", PgFunctionArgument.Create(-42));
                bool queryNull = Spi.ExecuteScalar<OwnedKey?>("SELECT NULL::" + type) is null;
                bool callNull = PgFunctions.Call<OwnedKey?>(function, PgFunctionArgument.Create<OwnedKey?>(null)) is null;
                return FormattableString.Invariant($"{queried.Number}|{named.Number}|{identified.Number}|{external.Number}|{queryNull}|{callNull}");
            }
        }
        """;
}
