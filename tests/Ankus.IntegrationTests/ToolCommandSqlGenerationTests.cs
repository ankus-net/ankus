using System.Runtime.InteropServices;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// A disabled-only package retains a callable native export without installing its function, operator or cast.
    /// </summary>
    [TestMethod]
    public async Task DisabledOnlySqlPackageRetainsCallableNativeExports()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "DisabledSql.csproj");
        File.Copy(s_project, project);
        await File.WriteAllTextAsync(Path.Join(directory, "Functions.cs"), SqlPackageSource("GenerateSql = false"), token);
        string output = Path.Join(directory, "published");
        await PublishSqlControlsAsync(project, output, token);
        PublishedExtension manifest = PublishedExtension.Read(output);
        string[] exports = await ReadSqlControlExportsAsync(directory, token);
        string native = Assert.ContainsSingle(exports.Where(static value => value.StartsWith("ankus_fn_", StringComparison.Ordinal)));
        Assert.Contains("pg_finfo_" + native, exports);
        string sql = await File.ReadAllTextAsync(Path.Join(output, "extension", manifest.Sql), token);
        Assert.DoesNotContain("CREATE FUNCTION", sql);
        Assert.DoesNotContain("CREATE OPERATOR", sql);
        Assert.DoesNotContain("CREATE CAST", sql);
        Assert.Contains("relocatable = true", await File.ReadAllTextAsync(Path.Join(output, "extension", manifest.Control), token));

        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        int process = connection.ProcessID;
        await ExecuteSqlPackageAsync(connection, $"LOAD '{manifest.Library}'; CREATE EXTENSION ankus_tool_probe");
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, """
            SELECT to_regprocedure('public.default_value(double precision)') IS NULL
                AND NOT EXISTS(SELECT FROM pg_operator WHERE oprnamespace='public'::regnamespace AND oprname='^#')
                AND NOT EXISTS(SELECT FROM pg_cast WHERE castsource='double precision'::regtype AND casttarget='bytea'::regtype)
                AND (SELECT extrelocatable FROM pg_extension WHERE extname='ankus_tool_probe')
                AND NOT EXISTS(SELECT FROM pg_depend d JOIN pg_extension e ON e.oid=d.refobjid
                    WHERE d.refclassid='pg_extension'::regclass AND e.extname='ankus_tool_probe' AND d.deptype='e')
            """));
        PostgresException missing = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "SELECT default_value(1)"));
        Assert.AreEqual("42883", missing.SqlState);

        await ExecuteSqlPackageAsync(connection, $"""
            CREATE FUNCTION public.manual_value(double precision) RETURNS bytea
            AS '{manifest.Library}', '{native}' LANGUAGE c IMMUTABLE CALLED ON NULL INPUT;
            """);
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, $"""
            SELECT prosrc='{native}' AND probin='{manifest.Library}' AND NOT proisstrict
                AND proargtypes='701'::oidvector AND prorettype='bytea'::regtype
            FROM pg_proc WHERE oid='public.manual_value(double precision)'::regprocedure
            """));
        Assert.AreEqual("4045000000000000|bff8000000000000|3ff0000000000001|cafe|true", await SqlPackageScalarAsync<string>(connection, """
            SELECT concat_ws('|',encode(manual_value(42),'hex'),encode(manual_value(-1.5),'hex'),
                encode(manual_value(1.0000000000000002),'hex'),encode(manual_value(NULL),'hex'),(manual_value(0) IS NULL)::text)
            """));
        PostgresException rejected = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "SELECT manual_value(-17)"));
        Assert.AreEqual("P8211", rejected.SqlState);
        Assert.AreEqual("SQL package callback rejected value.", rejected.MessageText);
        Assert.AreEqual("4045800000000000", await SqlPackageScalarAsync<string>(connection, "SELECT encode(manual_value(43),'hex')"));
        Assert.AreEqual(process, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
        await ExecuteSqlPackageAsync(connection, "DROP EXTENSION ankus_tool_probe");
        Assert.AreEqual("4046000000000000", await SqlPackageScalarAsync<string>(connection, "SELECT encode(manual_value(44),'hex')"));
        Assert.IsFalse(await SqlPackageScalarAsync<bool>(connection, "SELECT EXISTS(SELECT FROM pg_extension WHERE extname='ankus_tool_probe')"));
        await ExecuteSqlPackageAsync(connection, "DROP FUNCTION manual_value(double precision); CREATE EXTENSION ankus_tool_probe; DROP EXTENSION ankus_tool_probe");
    }

    /// <summary>
    /// Attribute-only replacement edits rebuild packaged SQL, roll back failed installation and preserve owned identities through relocation.
    /// </summary>
    [TestMethod]
    public async Task ReplacementSqlPackageRebuildsRelocatesAndRollsBackInstallation()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "ReplacementSql.csproj");
        File.Copy(s_project, project);
        string source = Path.Join(directory, "Functions.cs");
        string output = Path.Join(directory, "published");
        string failing = ReplacementPackageSql.Replace("VALUES (2)", "VALUES (1)", StringComparison.Ordinal) +
            "\nSELECT replacement_value(-17);\n";
        await File.WriteAllTextAsync(source, SqlPackageSource("SqlRelocatable = true, Sql = " +
            System.Text.Json.JsonSerializer.Serialize(failing)), token);
        await PublishSqlControlsAsync(project, output, token);
        PublishedExtension failedManifest = PublishedExtension.Read(output);
        string failedSql = await File.ReadAllTextAsync(Path.Join(output, "extension", failedManifest.Sql), token);
        Assert.Contains("VALUES (1)", failedSql);
        Assert.Contains("SELECT replacement_value(-17);", failedSql);
        string[] originalExports = await ReadSqlControlExportsAsync(directory, token);

        await using (PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token))
        await using (NpgsqlConnection connection = await cluster.OpenConnectionAsync(token))
        {
            int process = connection.ProcessID;
            await ExecuteSqlPackageAsync(connection, "CREATE SCHEMA sql_failed");
            PostgresException rejected = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_tool_probe WITH SCHEMA sql_failed"));
            Assert.AreEqual("P8211", rejected.SqlState);
            Assert.AreEqual("SQL package callback rejected value.", rejected.MessageText);
            Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, """
                SELECT to_regclass('sql_failed.replacement_marker') IS NULL
                    AND to_regclass('sql_failed.replacement_view') IS NULL
                    AND to_regprocedure('sql_failed.replacement_value(double precision)') IS NULL
                    AND NOT EXISTS(SELECT FROM pg_operator WHERE oprnamespace='sql_failed'::regnamespace)
                    AND NOT EXISTS(SELECT FROM pg_cast WHERE castsource='double precision'::regtype AND casttarget='bytea'::regtype)
                    AND NOT EXISTS(SELECT FROM pg_extension WHERE extname='ankus_tool_probe')
                """));
            Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, "SELECT 19+23"));
            Assert.AreEqual(process, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
        }

        await File.WriteAllTextAsync(source, SqlPackageSource("SqlRelocatable = true, Sql = " +
            System.Text.Json.JsonSerializer.Serialize(ReplacementPackageSql)), token);
        await PublishSqlControlsAsync(project, output, token);
        PublishedExtension manifest = PublishedExtension.Read(output);
        string correctedSql = await File.ReadAllTextAsync(Path.Join(output, "extension", manifest.Sql), token);
        Assert.AreNotEqual(failedSql, correctedSql);
        Assert.Contains("VALUES (2)", correctedSql);
        Assert.DoesNotContain("VALUES (1)", correctedSql);
        Assert.DoesNotContain("SELECT replacement_value(-17);", correctedSql);
        Assert.DoesNotContain("@FUNCTION_NAME@", correctedSql);
        Assert.DoesNotContain("@MODULE_PATHNAME@", correctedSql);
        Assert.AreSequenceEqual(originalExports, await ReadSqlControlExportsAsync(directory, token));
        string native = Assert.ContainsSingle(originalExports.Where(static value => value.StartsWith("ankus_fn_", StringComparison.Ordinal)));
        Assert.Contains("AS 'MODULE_PATHNAME', '" + native + "'", correctedSql);
        Assert.Contains("relocatable = true", await File.ReadAllTextAsync(Path.Join(output, "extension", manifest.Control), token));

        await using PostgresTestCluster correctedCluster = await StartPublishedClusterAsync(output, token);
        await using NpgsqlConnection corrected = await correctedCluster.OpenConnectionAsync(token);
        await ExecuteSqlPackageAsync(corrected, """
            CREATE SCHEMA sql_first; CREATE SCHEMA sql_second; CREATE SCHEMA sql_shadow;
            CREATE FUNCTION sql_shadow.replacement_value(double precision) RETURNS bytea
                LANGUAGE SQL IMMUTABLE AS 'SELECT decode(''ff'',''hex'')';
            CREATE OPERATOR sql_shadow.^# (RIGHTARG=double precision, FUNCTION=sql_shadow.replacement_value);
            SET search_path=sql_shadow,pg_catalog;
            CREATE EXTENSION ankus_tool_probe WITH SCHEMA sql_first;
            """);
        uint shadowFunction = await SqlPackageScalarAsync<uint>(corrected, "SELECT 'sql_shadow.replacement_value(double precision)'::regprocedure::oid");
        uint shadowOperator = await SqlPackageScalarAsync<uint>(corrected, "SELECT 'sql_shadow.^#(NONE,double precision)'::regoperator::oid");
        uint[] original = await SqlPackageMembersAsync(corrected, "sql_first");
        await AssertReplacementPackageAsync(corrected, "sql_first", native, manifest.Library);
        await ExecuteSqlPackageAsync(corrected, "ALTER EXTENSION ankus_tool_probe SET SCHEMA sql_second");
        Assert.AreSequenceEqual(original, await SqlPackageMembersAsync(corrected, "sql_second"));
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(corrected, "SELECT to_regprocedure('sql_first.replacement_value(double precision)') IS NULL"));
        await AssertReplacementPackageAsync(corrected, "sql_second", native, manifest.Library);
        await ExecuteSqlPackageAsync(corrected, "DROP EXTENSION ankus_tool_probe");
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(corrected, """
            SELECT to_regclass('sql_second.replacement_marker') IS NULL AND to_regclass('sql_second.replacement_view') IS NULL
                AND to_regprocedure('sql_second.replacement_value(double precision)') IS NULL
                AND NOT EXISTS(SELECT FROM pg_operator WHERE oprnamespace='sql_second'::regnamespace)
                AND NOT EXISTS(SELECT FROM pg_cast WHERE castsource='double precision'::regtype AND casttarget='bytea'::regtype)
                AND NOT EXISTS(SELECT FROM pg_extension WHERE extname='ankus_tool_probe')
            """));
        Assert.AreEqual(shadowFunction, await SqlPackageScalarAsync<uint>(corrected, "SELECT 'sql_shadow.replacement_value(double precision)'::regprocedure::oid"));
        Assert.AreEqual(shadowOperator, await SqlPackageScalarAsync<uint>(corrected, "SELECT 'sql_shadow.^#(NONE,double precision)'::regoperator::oid"));
        Assert.AreEqual("ff", await SqlPackageScalarAsync<string>(corrected, "SELECT encode(OPERATOR(sql_shadow.^#) 7,'hex')"));
        await ExecuteSqlPackageAsync(corrected, "CREATE EXTENSION ankus_tool_probe WITH SCHEMA sql_first");
        uint[] reinstalled = await SqlPackageMembersAsync(corrected, "sql_first");
        for (int index = 0; index < original.Length; index++)
        {
            Assert.AreNotEqual(original[index], reinstalled[index]);
        }

        await AssertReplacementPackageAsync(corrected, "sql_first", native, manifest.Library);
        await ExecuteSqlPackageAsync(corrected, "DROP EXTENSION ankus_tool_probe");
    }

    /// <summary>
    /// Publishes the isolated consumer without changing its source, restore cache or package references.
    /// </summary>
    private static async Task PublishSqlControlsAsync(string project, string output, CancellationToken token)
    {
        ProcessResult result = await RunDotnetAsync(["publish", project, "-c", "Release", "-r", RuntimeInformation.RuntimeIdentifier,
            "-o", output, "-p:AnkusPostgresMajor=" + MajorText(), "-p:AnkusPgConfigPath=" + s_installation.PgConfigPath,
            "-bl:" + Path.Join(Path.GetDirectoryName(project)!, "sql-controls-{}.binlog")], token);
        result.EnsureSuccess("dotnet", ["publish"]);
        Assert.DoesNotContain("LNK4197", result.StandardOutput + result.StandardError);
    }

    /// <summary>
    /// Reads the linker's actual export manifest rather than reproducing the generator's symbol hash.
    /// </summary>
    private static async Task<string[]> ReadSqlControlExportsAsync(string directory, CancellationToken token)
    {
        string path = Assert.ContainsSingle(Directory.GetFiles(Path.Join(directory, "obj"), "exports.txt", SearchOption.AllDirectories));
        string[] exports = [.. (await File.ReadAllLinesAsync(path, token)).Where(static line => !string.IsNullOrWhiteSpace(line))];
        Assert.AreEqual(OperatingSystem.IsWindows() ? "ankus_schema_manifest,DATA" : "ankus_schema_manifest",
            Assert.ContainsSingle(exports.Where(static line => line.StartsWith("ankus_schema_manifest", StringComparison.Ordinal))));
        return exports;
    }

    /// <summary>
    /// Checks replacement results, non-strict NULL semantics and exact linkage independently of schema lookup.
    /// </summary>
    private async Task AssertReplacementPackageAsync(NpgsqlConnection connection, string schema, string native, string library)
    {
        Assert.AreEqual("4044800000000000|4045000000000000|4045800000000000|cafe|true|2", await SqlPackageScalarAsync<string>(connection, $"""
            SELECT concat_ws('|',encode((SELECT value FROM {schema}.replacement_view),'hex'),
                encode(OPERATOR({schema}.^#) 42,'hex'),encode(CAST(43::double precision AS bytea),'hex'),
                encode({schema}.replacement_value(NULL),'hex'),({schema}.replacement_value(0) IS NULL)::text,
                (SELECT revision::text FROM {schema}.replacement_marker))
            """));
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, $"""
            SELECT prosrc='{native}' AND probin='{library}' AND NOT proisstrict AND provolatile='i'
                AND proargtypes='701'::oidvector AND prorettype='bytea'::regtype
                AND to_regprocedure('{schema}.default_value(double precision)') IS NULL
            FROM pg_proc WHERE oid='{schema}.replacement_value(double precision)'::regprocedure
            """));
        int process = connection.ProcessID;
        PostgresException rejected = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, $"SELECT {schema}.replacement_value(-17)"));
        Assert.AreEqual("P8211", rejected.SqlState);
        Assert.AreEqual("SQL package callback rejected value.", rejected.MessageText);
        Assert.AreEqual("4046000000000000", await SqlPackageScalarAsync<string>(connection, $"SELECT encode({schema}.replacement_value(44),'hex')"));
        Assert.AreEqual(process, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Captures all five explicit replacement objects and proves each belongs to the extension.
    /// </summary>
    private async Task<uint[]> SqlPackageMembersAsync(NpgsqlConnection connection, string schema)
    {
        Assert.AreEqual(5L, await SqlPackageScalarAsync<long>(connection, $"""
            WITH members(classid,objid) AS (
                VALUES ('pg_proc'::regclass,'{schema}.replacement_value(double precision)'::regprocedure::oid),
                    ('pg_operator'::regclass,'{schema}.^#(NONE,double precision)'::regoperator::oid),
                    ('pg_class'::regclass,'{schema}.replacement_marker'::regclass::oid),
                    ('pg_class'::regclass,'{schema}.replacement_view'::regclass::oid)
                UNION ALL SELECT 'pg_cast'::regclass,oid FROM pg_cast
                    WHERE castsource='double precision'::regtype AND casttarget='bytea'::regtype)
            SELECT count(*) FROM members m JOIN pg_depend d ON d.classid=m.classid AND d.objid=m.objid
                JOIN pg_extension e ON e.oid=d.refobjid
            WHERE d.refclassid='pg_extension'::regclass AND d.deptype='e' AND e.extname='ankus_tool_probe'
            """));
        return await SqlPackageScalarAsync<uint[]>(connection, $"""
            SELECT ARRAY['{schema}.replacement_value(double precision)'::regprocedure::oid,
                '{schema}.^#(NONE,double precision)'::regoperator::oid,'{schema}.replacement_marker'::regclass::oid,
                '{schema}.replacement_view'::regclass::oid,
                (SELECT oid FROM pg_cast WHERE castsource='double precision'::regtype AND casttarget='bytea'::regtype)]
            """);
    }

    /// <summary>
    /// Executes package statements while retaining the test cancellation token.
    /// </summary>
    private async Task ExecuteSqlPackageAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }

    /// <summary>
    /// Requires an exact managed SQL scalar type before checking its value.
    /// </summary>
    private async Task<T> SqlPackageScalarAsync<T>(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(context.CancellationToken));
    }

    /// <summary>
    /// Keeps the compiled callback identical while changing only its SQL-generation attribute options.
    /// </summary>
    private static string SqlPackageSource(string options) => $$"""
        using Ankus;
        using System.Buffers.Binary;
        public static class Functions
        {
            [PgFunction(Name = "default_value", {{options}})]
            [PgOperator("^#")]
            [PgCast]
            public static byte[]? NativeValue(double? value)
            {
                if (value is null) return new byte[] { 0xca, 0xfe };
                if (value == 0) return null;
                if (value == -17) throw new PgException("P8211", "SQL package callback rejected value.");
                byte[] bytes = new byte[8];
                BinaryPrimitives.WriteInt64BigEndian(bytes, System.BitConverter.DoubleToInt64Bits(value.Value));
                return bytes;
            }
        }
        """;

    /// <summary>
    /// Owns the complete function/operator/cast bundle plus dependent objects in its installation schema.
    /// </summary>
    private const string ReplacementPackageSql = """
        CREATE FUNCTION replacement_value(double precision) RETURNS bytea
            AS '@MODULE_PATHNAME@', '@FUNCTION_NAME@' LANGUAGE c IMMUTABLE CALLED ON NULL INPUT;
        CREATE OPERATOR ^# (RIGHTARG=double precision, FUNCTION=replacement_value);
        CREATE CAST (double precision AS bytea) WITH FUNCTION replacement_value(double precision);
        CREATE TABLE replacement_marker(revision integer);
        INSERT INTO replacement_marker VALUES (2);
        CREATE VIEW replacement_view AS SELECT replacement_value(41) AS value;
        """;
}
