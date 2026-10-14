using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// File providers order packaged consumers, rebuild their type behavior and retain exact ownership through failure and relocation.
    /// </summary>
    [TestMethod]
    public async Task DeclaredProviderPackageRelocatesReinstallsAndRollsBack()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "DeclaredProviders.csproj");
        XDocument projectFile = XDocument.Load(s_project);
        projectFile.Root!.Add(new XElement("ItemGroup", new XElement("AdditionalFiles", new XAttribute("Include", "type setup/types.sql"))));
        projectFile.Save(project);
        string source = Path.Join(directory, "Functions.cs");
        await File.WriteAllTextAsync(source, DeclaredProviderPackageSource, token);
        string sqlDirectory = Path.Join(directory, "type setup");
        Directory.CreateDirectory(sqlDirectory);
        string sqlFile = Path.Join(sqlDirectory, "types.sql");
        string output = Path.Join(directory, "published");
        await File.WriteAllTextAsync(sqlFile, DeclaredProviderPackageSql, token);
        await PublishSqlControlsAsync(project, output, token);
        PublishedExtension initialManifest = PublishedExtension.Read(output);
        string initialSql = await File.ReadAllTextAsync(Path.Join(output, "extension", initialManifest.Sql), token);
        string[] exports = await ReadSqlControlExportsAsync(directory, token);
        string guard = DeclarationExport(exports, "_package_guard");
        AssertDeclaredProviderOrder(initialSql);
        await using (PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token))
        await using (NpgsqlConnection connection = await cluster.OpenConnectionAsync(token))
        {
            await ExecuteSqlPackageAsync(connection, "CREATE SCHEMA provider_initial; CREATE EXTENSION ankus_tool_probe WITH SCHEMA provider_initial");
            await DeclaredProviderMembers(connection, "provider_initial");
            await AssertDeclaredProviderPackage(connection, "provider_initial", "before", 1);
            await ExecuteSqlPackageAsync(connection, "DROP EXTENSION ankus_tool_probe");
            await AssertDeclarationSchemaEmpty(connection, "provider_initial");
        }

        string correctedFile = DeclaredProviderPackageSql.Replace("'before'", "'after'", StringComparison.Ordinal)
            .Replace("VALUES(1)", "VALUES(2)", StringComparison.Ordinal);
        string falseClaim = correctedFile.Replace("CREATE TYPE package_code", "CREATE TYPE claimed_elsewhere", StringComparison.Ordinal);
        await File.WriteAllTextAsync(sqlFile, falseClaim, token);
        await PublishSqlControlsAsync(project, output, token);
        PublishedExtension failedManifest = PublishedExtension.Read(output);
        string failedSql = await File.ReadAllTextAsync(Path.Join(output, "extension", failedManifest.Sql), token);
        Assert.Contains("CREATE TYPE claimed_elsewhere", failedSql);
        Assert.DoesNotContain("CREATE TYPE package_code", failedSql);
        Assert.AreSequenceEqual(exports, await ReadSqlControlExportsAsync(directory, token));
        await using (PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token))
        await using (NpgsqlConnection connection = await cluster.OpenConnectionAsync(token))
        {
            int process = connection.ProcessID;
            await ExecuteSqlPackageAsync(connection, "CREATE SCHEMA provider_failed");
            PostgresException rejected = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_tool_probe WITH SCHEMA provider_failed"));
            Assert.AreEqual("42704", rejected.SqlState);
            Assert.AreEqual("type package_code does not exist", rejected.MessageText);
            await AssertDeclarationSchemaEmpty(connection, "provider_failed");
            Assert.IsFalse(await SqlPackageScalarAsync<bool>(connection,
                "SELECT EXISTS(SELECT FROM pg_extension WHERE extname='ankus_tool_probe')"));
            await ExecuteSqlPackageAsync(connection, $"""
                CREATE FUNCTION public.provider_recovery(integer) RETURNS integer
                    AS '{failedManifest.Library}','{guard}' LANGUAGE c STRICT;
                """);
            Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, "SELECT public.provider_recovery(42)"));
            PostgresException nativeError = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ExecuteSqlPackageAsync(connection, "SELECT public.provider_recovery(-1)"));
            Assert.AreEqual("P8408", nativeError.SqlState);
            Assert.AreEqual("Declared provider callback rejected value.", nativeError.MessageText);
            Assert.AreEqual(43, await SqlPackageScalarAsync<int>(connection, "SELECT public.provider_recovery(43)"));
            Assert.AreEqual(process, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
        }

        await File.WriteAllTextAsync(sqlFile, correctedFile, token);
        await PublishSqlControlsAsync(project, output, token);
        PublishedExtension manifest = PublishedExtension.Read(output);
        string finalSql = await File.ReadAllTextAsync(Path.Join(output, "extension", manifest.Sql), token);
        Assert.AreEqual(DeclaredProviderPackageSource, await File.ReadAllTextAsync(source, token));
        Assert.AreNotEqual(initialSql, finalSql);
        Assert.Contains("'after'", finalSql);
        Assert.DoesNotContain("'before'", finalSql);
        Assert.DoesNotContain("claimed_elsewhere", finalSql);
        Assert.AreSequenceEqual(exports, await ReadSqlControlExportsAsync(directory, token));
        AssertDeclaredProviderOrder(finalSql);
        Assert.Contains("relocatable = true", await File.ReadAllTextAsync(Path.Join(output, "extension", manifest.Control), token));
        await using PostgresTestCluster finalCluster = await StartPublishedClusterAsync(output, token);
        await using NpgsqlConnection final = await finalCluster.OpenConnectionAsync(token);
        await ExecuteSqlPackageAsync(final, """
            CREATE SCHEMA provider_first; CREATE SCHEMA provider_second; CREATE SCHEMA provider_shadow;
            CREATE TYPE provider_shadow.package_code AS ENUM('shadow');
            CREATE TYPE provider_shadow.package_pair AS (number bigint,label text);
            CREATE TABLE provider_shadow.package_marker(revision integer);
            INSERT INTO provider_shadow.package_marker VALUES(99);
            CREATE FUNCTION provider_shadow.package_echo(provider_shadow.package_code) RETURNS provider_shadow.package_code
                LANGUAGE SQL IMMUTABLE AS 'SELECT $1';
            SET search_path=provider_shadow,pg_catalog;
            """);
        uint[] shadow = await DeclaredProviderShadowIdentities(final);
        await ExecuteSqlPackageAsync(final, "CREATE EXTENSION ankus_tool_probe WITH SCHEMA provider_first");
        Dictionary<string, uint> original = await DeclaredProviderMembers(final, "provider_first");
        await AssertDeclaredProviderPackage(final, "provider_first", "after", 2);
        await ExecuteSqlPackageAsync(final, "ALTER EXTENSION ankus_tool_probe SET SCHEMA provider_second");
        Dictionary<string, uint> moved = await DeclaredProviderMembers(final, "provider_second");
        Assert.AreSequenceEqual(original.Keys, moved.Keys);
        foreach ((string name, uint oid) in original)
        {
            Assert.AreEqual(oid, moved[name], name);
        }

        await AssertDeclarationSchemaEmpty(final, "provider_first");
        await AssertDeclaredProviderPackage(final, "provider_second", "after", 2);
        await ExecuteSqlPackageAsync(final, "DROP EXTENSION ankus_tool_probe");
        await AssertDeclarationSchemaEmpty(final, "provider_second");
        Assert.IsFalse(await SqlPackageScalarAsync<bool>(final,
            "SELECT EXISTS(SELECT FROM pg_extension WHERE extname='ankus_tool_probe')"));
        Assert.AreSequenceEqual(shadow, await DeclaredProviderShadowIdentities(final));
        Assert.AreEqual("shadow|99|2147483648", await SqlPackageScalarAsync<string>(final, """
            SELECT concat_ws('|',provider_shadow.package_echo('shadow')::text,
                (SELECT revision FROM provider_shadow.package_marker),
                (ROW(2147483648,'kept')::provider_shadow.package_pair).number)
            """));
        await ExecuteSqlPackageAsync(final, "CREATE EXTENSION ankus_tool_probe WITH SCHEMA provider_first");
        Dictionary<string, uint> reinstalled = await DeclaredProviderMembers(final, "provider_first");
        Assert.AreSequenceEqual(original.Keys, reinstalled.Keys);
        foreach ((string name, uint oid) in original)
        {
            Assert.AreNotEqual(oid, reinstalled[name], name);
        }

        await AssertDeclaredProviderPackage(final, "provider_first", "after", 2);
        await ExecuteSqlPackageAsync(final, "DROP EXTENSION ankus_tool_probe");
        await AssertDeclarationSchemaEmpty(final, "provider_first");
        Assert.AreSequenceEqual(shadow, await DeclaredProviderShadowIdentities(final));
    }

    /// <summary>
    /// Pins automatic provider ordering independently of actual backend installation.
    /// </summary>
    private static void AssertDeclaredProviderOrder(string sql)
    {
        int provider = sql.IndexOf("CREATE TYPE package_code", StringComparison.Ordinal);
        int raw = sql.IndexOf("CREATE FUNCTION \"package_echo\"", StringComparison.Ordinal);
        int composite = sql.IndexOf("CREATE FUNCTION \"package_pair_echo\"", StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, provider);
        Assert.IsGreaterThan(provider, raw);
        Assert.IsGreaterThan(provider, composite);
    }

    /// <summary>
    /// Executes exact enum/composite values, SQL NULL, native errors and same-session recovery after each lifecycle phase.
    /// </summary>
    private async Task AssertDeclaredProviderPackage(NpgsqlConnection connection, string schema, string label, int revision)
    {
        int process = connection.ProcessID;
        Assert.AreSequenceEqual(["café", "", label], await SqlPackageScalarAsync<string[]>(connection,
            $"SELECT ARRAY(SELECT value::text FROM unnest(enum_range(NULL::{schema}.package_code)) value)"));
        Assert.AreEqual(label, await SqlPackageScalarAsync<string>(connection, $"SELECT {schema}.package_echo('{label}')::text"));
        Assert.AreEqual("café||true|42|café|0|true|true", await SqlPackageScalarAsync<string>(connection, $"""
            SELECT concat_ws('|',{schema}.package_echo('café')::text,{schema}.package_echo('')::text,
                ({schema}.package_echo(NULL::{schema}.package_code) IS NULL)::text,
                ({schema}.package_pair_echo(ROW(42,'café')::{schema}.package_pair)).number,
                ({schema}.package_pair_echo(ROW(42,'café')::{schema}.package_pair)).label,
                ({schema}.package_pair_echo(ROW(0,NULL)::{schema}.package_pair)).number,
                (({schema}.package_pair_echo(ROW(0,NULL)::{schema}.package_pair)).label IS NULL)::text,
                ({schema}.package_pair_echo(NULL::{schema}.package_pair) IS NULL)::text)
            """));
        Assert.AreEqual(revision, await SqlPackageScalarAsync<int>(connection, $"SELECT revision FROM {schema}.package_marker"));
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, $"""
            SELECT (SELECT proargtypes[0]='{schema}.package_code'::regtype AND prorettype='{schema}.package_code'::regtype
                    AND NOT proisstrict FROM pg_proc WHERE oid='{schema}.package_echo({schema}.package_code)'::regprocedure)
                AND (SELECT proargtypes[0]='{schema}.package_pair'::regtype AND prorettype='{schema}.package_pair'::regtype
                    AND NOT proisstrict FROM pg_proc WHERE oid='{schema}.package_pair_echo({schema}.package_pair)'::regprocedure)
                AND pg_typeof({schema}.package_echo('café'))='{schema}.package_code'::regtype
                AND pg_typeof({schema}.package_pair_echo(ROW(42,'café')::{schema}.package_pair))='{schema}.package_pair'::regtype
            """));
        PostgresException rejected = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, $"SELECT {schema}.package_guard(-1)"));
        Assert.AreEqual("P8408", rejected.SqlState);
        Assert.AreEqual("Declared provider callback rejected value.", rejected.MessageText);
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, $"SELECT {schema}.package_guard(42)"));
        Assert.AreEqual("café", await SqlPackageScalarAsync<string>(connection, $"SELECT {schema}.package_echo('café')::text"));
        Assert.AreEqual(process, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Captures every explicit provider object and both generated array identities, with extension ownership on their base types.
    /// </summary>
    private async Task<Dictionary<string, uint>> DeclaredProviderMembers(NpgsqlConnection connection, string schema)
    {
        string[] expected = ["array:package_code", "array:package_pair", "function:package_echo", "function:package_guard",
            "function:package_pair_echo", "table:package_marker", "type:package_code", "type:package_pair"];
        await using var command = new NpgsqlCommand($"""
            WITH objects(label,classid,objid,ownerid) AS (
                SELECT 'type:'||typname,'pg_type'::regclass,oid,oid FROM pg_type
                    WHERE typnamespace='{schema}'::regnamespace AND typname IN('package_code','package_pair')
                UNION ALL SELECT 'array:'||t.typname,'pg_type'::regclass,a.oid,t.oid FROM pg_type t JOIN pg_type a
                    ON a.oid=t.typarray AND a.typelem=t.oid AND a.typnamespace=t.typnamespace
                    WHERE t.typnamespace='{schema}'::regnamespace AND t.typname IN('package_code','package_pair')
                UNION ALL SELECT 'function:'||proname,'pg_proc'::regclass,oid,oid FROM pg_proc WHERE pronamespace='{schema}'::regnamespace
                UNION ALL SELECT 'table:'||relname,'pg_class'::regclass,oid,oid FROM pg_class
                    WHERE relnamespace='{schema}'::regnamespace AND relkind='r')
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
    /// Retains unrelated same-name objects as an independent drop and schema-resolution witness.
    /// </summary>
    private Task<uint[]> DeclaredProviderShadowIdentities(NpgsqlConnection connection)
        => SqlPackageScalarAsync<uint[]>(connection, """
            SELECT ARRAY['provider_shadow.package_code'::regtype::oid,'provider_shadow.package_pair'::regtype::oid,
                'provider_shadow.package_marker'::regclass::oid,
                'provider_shadow.package_echo(provider_shadow.package_code)'::regprocedure::oid]
            """);

    /// <summary>
    /// Declares both file providers while deliberately leaving their managed consumers without explicit dependencies.
    /// </summary>
    private const string DeclaredProviderPackageSource = """
        using Ankus;
        [assembly: PgSqlFile("package-types", "type setup/types.sql", Relocatable = true)]
        [assembly: PgSqlTypeProvider("package-types", "package_code")]
        [assembly: PgSqlTypeProvider("package-types", "package_pair")]
        public static class Functions
        {
            [PgFunction(Name = "package_echo")]
            [return: PgSqlType("package_code")]
            public static PgDatum? RawCode([PgSqlType("package_code")] PgDatum? value) => value;

            [PgFunction(Name = "package_pair_echo")]
            [return: PgCompositeType("package_pair")]
            public static PgHeapTuple? TuplePair([PgCompositeType("package_pair")] PgHeapTuple? value) => value;

            [PgFunction(Name = "package_guard")]
            public static int ProviderGuard(int value)
            {
                if (value < 0)
                {
                    throw new PgException("P8408", "Declared provider callback rejected value.");
                }

                return value;
            }
        }
        """;

    /// <summary>
    /// Supplies literal type and marker behavior that later publications change without modifying managed source.
    /// </summary>
    private const string DeclaredProviderPackageSql = """
        CREATE TYPE package_code AS ENUM('café','','before');
        CREATE TYPE package_pair AS (number integer,label text);
        CREATE TABLE package_marker(revision integer);
        INSERT INTO package_marker VALUES(1);
        """;
}
