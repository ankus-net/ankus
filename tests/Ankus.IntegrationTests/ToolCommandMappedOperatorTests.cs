using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// A module containing only mapped derives retains exact extension ownership and usable indexes through relocation and reinstall.
    /// </summary>
    [TestMethod]
    public async Task MappedOperatorPackageRelocatesAndReinstalls()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "MappedFamilies.csproj");
        File.Copy(s_project, project);
        string source = "using Ankus;\n[assembly: PgSql(\"manual-type\", " +
            System.Text.Json.JsonSerializer.Serialize(MappedPackageTypeSql("package_key")) +
            ", Relocatable=true)]\n[assembly: PgSqlTypeProvider(\"manual-type\", typeof(OwnedKey))]\n" +
            MappedPackageContract("OwnedKey", "package_key", string.Empty, "[PgOrdering][PgHashing]");
        Assert.DoesNotContain("[PgFunction", source);
        await File.WriteAllTextAsync(Path.Join(directory, "Families.cs"), source, token);
        string output = Path.Join(directory, "published");
        await PublishSqlControlsAsync(project, output, token);
        PublishedExtension manifest = PublishedExtension.Read(output);
        Assert.Contains("relocatable = true", await File.ReadAllTextAsync(Path.Join(output, "extension", manifest.Control), token));
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await ExecuteSqlPackageAsync(connection, """
            CREATE SCHEMA mapped_first; CREATE SCHEMA mapped_second; CREATE SCHEMA mapped_shadow;
            CREATE DOMAIN mapped_shadow.package_key AS integer;
            CREATE FUNCTION mapped_shadow.package_key_hash(mapped_shadow.package_key) RETURNS integer LANGUAGE SQL IMMUTABLE AS 'SELECT 999';
            CREATE OPERATOR FAMILY mapped_shadow.package_key_btree_ops USING btree;
            CREATE OPERATOR FAMILY mapped_shadow.package_key_hash_ops USING hash;
            CREATE TABLE mapped_shadow.kept(value integer); INSERT INTO mapped_shadow.kept VALUES(73);
            CREATE INDEX kept_index ON mapped_shadow.kept(value);
            SET search_path=mapped_shadow,pg_catalog;
            """);
        string[] shadow = await MappedPackageSnapshot(connection, "mapped_shadow");
        await ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_tool_probe WITH SCHEMA mapped_first");
        Dictionary<string, uint> original = await MappedPackageMembers(connection, "mapped_first");
        await CreateMappedPackageIndexes(connection, "mapped_first");
        await AssertMappedPackageIndexes(connection, "mapped_first");
        await ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_tool_probe SET SCHEMA mapped_second");
        Dictionary<string, uint> moved = await MappedPackageMembers(connection, "mapped_second");
        Assert.AreSequenceEqual(original.Keys, moved.Keys);
        foreach ((string name, uint oid) in original)
        {
            Assert.AreEqual(oid, moved[name], name);
        }

        await AssertDeclarationSchemaEmpty(connection, "mapped_first");
        await AssertMappedPackageIndexes(connection, "mapped_second");
        await ExecuteSqlPackageAsync(connection, "REINDEX INDEX public.mapped_package_btree; REINDEX INDEX public.mapped_package_hash");
        await AssertMappedPackageIndexes(connection, "mapped_second");
        await ExecuteSqlPackageAsync(connection, "DROP TABLE public.mapped_package_btree_rows,public.mapped_package_hash_rows; DROP EXTENSION ankus_tool_probe");
        await AssertDeclarationSchemaEmpty(connection, "mapped_second");
        Assert.AreSequenceEqual(shadow, await MappedPackageSnapshot(connection, "mapped_shadow"));
        Assert.AreEqual(73, await SqlPackageScalarAsync<int>(connection, "SELECT value FROM mapped_shadow.kept"));
        Assert.AreEqual(999, await SqlPackageScalarAsync<int>(connection, "SELECT mapped_shadow.package_key_hash(17)"));
        await ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_tool_probe WITH SCHEMA mapped_first");
        Dictionary<string, uint> reinstalled = await MappedPackageMembers(connection, "mapped_first");
        Assert.AreSequenceEqual(original.Keys, reinstalled.Keys);
        foreach ((string name, uint oid) in original)
        {
            Assert.AreNotEqual(oid, reinstalled[name], name);
        }

        await CreateMappedPackageIndexes(connection, "mapped_first");
        await AssertMappedPackageIndexes(connection, "mapped_first");
        Assert.AreEqual(connection.ProcessID, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
        await ExecuteSqlPackageAsync(connection, "DROP TABLE public.mapped_package_btree_rows,public.mapped_package_hash_rows; DROP EXTENSION ankus_tool_probe");
        await AssertDeclarationSchemaEmpty(connection, "mapped_first");
        Assert.AreSequenceEqual(shadow, await MappedPackageSnapshot(connection, "mapped_shadow"));
    }

    /// <summary>
    /// Helper-only external mappings honor SQL controls, ordinary collisions and external type ownership.
    /// </summary>
    [TestMethod]
    public async Task MappedExternalOperatorPackageRetainsSchemaAndSqlControls()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "ExternalFamilies.csproj");
        File.Copy(s_project, project);
        const string options = ", Schema=\"mapped_external\", Origin=PgTypeOrigin.External";
        const string hashSql = """
            CREATE OPERATOR FAMILY mapped_external.controlled_hash USING hash;
            CREATE OPERATOR CLASS mapped_external.controlled_hash FOR TYPE mapped_external.controlled_key USING hash
                FAMILY mapped_external.controlled_hash AS
                OPERATOR 1 mapped_external.= (mapped_external.controlled_key,mapped_external.controlled_key),
                FUNCTION 1 @HASH_FUNCTION_SQL@(mapped_external.controlled_key);
            """;
        string source = "using Ankus;\n" + MappedPackageContract("ExternalKey", "external_key", options, "[PgOrdering][PgHashing]") +
            MappedPackageContract("ControlledKey", "controlled_key", options,
                "[PgOrdering(GenerateSql=false)][PgHashing(Sql=" + System.Text.Json.JsonSerializer.Serialize(hashSql) + ", SqlRelocatable=true)]");
        Assert.DoesNotContain("[PgFunction", source);
        Assert.DoesNotContain("PgSqlTypeProvider", source);
        await File.WriteAllTextAsync(Path.Join(directory, "Families.cs"), source, token);
        string output = Path.Join(directory, "published");
        await PublishSqlControlsAsync(project, output, token);
        PublishedExtension manifest = PublishedExtension.Read(output);
        Assert.Contains("relocatable = false", await File.ReadAllTextAsync(Path.Join(output, "extension", manifest.Control), token));
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await ExecuteSqlPackageAsync(connection, "CREATE SCHEMA mapped_external; CREATE SCHEMA mapped_existing; CREATE SCHEMA mapped_move;" +
            MappedPackageTypeSql("mapped_external.external_key") + MappedPackageTypeSql("mapped_external.controlled_key") + """
            CREATE TABLE public.external_kept(id integer,value mapped_external.external_key);
            INSERT INTO public.external_kept VALUES(1,'17'),(2,'19'),(3,'31'),(4,'22'),(5,NULL);
            CREATE FUNCTION mapped_existing.equal(mapped_external.external_key,mapped_external.external_key) RETURNS boolean
                LANGUAGE SQL IMMUTABLE STRICT AS 'SELECT $1::text::integer=$2::text::integer';
            CREATE FUNCTION mapped_existing.hash(mapped_external.external_key) RETURNS integer
                LANGUAGE SQL IMMUTABLE STRICT AS 'SELECT $1::text::integer';
            CREATE OPERATOR mapped_existing.= (LEFTARG=mapped_external.external_key,RIGHTARG=mapped_external.external_key,FUNCTION=mapped_existing.equal);
            CREATE OPERATOR FAMILY mapped_existing.existing_hash USING hash;
            CREATE OPERATOR CLASS mapped_existing.existing_hash DEFAULT FOR TYPE mapped_external.external_key USING hash
                FAMILY mapped_existing.existing_hash AS
                OPERATOR 1 mapped_existing.= (mapped_external.external_key,mapped_external.external_key),
                FUNCTION 1 mapped_existing.hash(mapped_external.external_key);
            CREATE OPERATOR mapped_external.= (LEFTARG=mapped_external.external_key,RIGHTARG=mapped_external.external_key,FUNCTION=mapped_existing.equal);
            """);
        string[] externalBefore = await MappedPackageSnapshot(connection, "mapped_external");
        string[] existing = await MappedPackageSnapshot(connection, "mapped_existing");
        PostgresException duplicate = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_tool_probe"));
        Assert.AreEqual("42723", duplicate.SqlState);
        Assert.AreEqual("operator = already exists", duplicate.MessageText);
        Assert.AreSequenceEqual(externalBefore, await MappedPackageSnapshot(connection, "mapped_external"));
        Assert.AreSequenceEqual(existing, await MappedPackageSnapshot(connection, "mapped_existing"));
        Assert.IsFalse(await SqlPackageScalarAsync<bool>(connection, "SELECT EXISTS(SELECT FROM pg_extension WHERE extname='ankus_tool_probe')"));
        await ExecuteSqlPackageAsync(connection, "DROP OPERATOR mapped_external.= (mapped_external.external_key,mapped_external.external_key)");
        externalBefore = await MappedPackageSnapshot(connection, "mapped_external");
        PostgresException defaultClass = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_tool_probe"));
        Assert.AreEqual("42710", defaultClass.SqlState);
        Assert.AreEqual("could not make operator class \"external_key_hash_ops\" be default for type mapped_external.external_key", defaultClass.MessageText);
        Assert.AreEqual("Operator class \"existing_hash\" already is the default.", defaultClass.Detail);
        Assert.AreSequenceEqual(externalBefore, await MappedPackageSnapshot(connection, "mapped_external"));
        Assert.AreSequenceEqual(existing, await MappedPackageSnapshot(connection, "mapped_existing"));
        Assert.AreEqual("1:17,2:19,3:31,4:22,5:NULL", await MappedExternalRows(connection));
        await ExecuteSqlPackageAsync(connection, "DROP OPERATOR CLASS mapped_existing.existing_hash USING hash; CREATE EXTENSION ankus_tool_probe");
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, """
            SELECT NOT EXISTS(SELECT FROM pg_depend d JOIN pg_extension e ON e.oid=d.refobjid
                WHERE e.extname='ankus_tool_probe' AND d.classid='pg_type'::regclass AND d.deptype='e'
                    AND d.objid IN('mapped_external.external_key'::regtype,'mapped_external.controlled_key'::regtype))
            """));
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, """
            SELECT NOT EXISTS(SELECT FROM pg_opclass WHERE opcintype='mapped_external.controlled_key'::regtype AND opcdefault)
                AND NOT EXISTS(SELECT FROM pg_opfamily WHERE opfnamespace='mapped_external'::regnamespace AND opfname='controlled_key_btree_ops')
                AND to_regprocedure('mapped_external.controlled_key_cmp(mapped_external.controlled_key,mapped_external.controlled_key)') IS NOT NULL
            """));
        Assert.AreEqual(int.MaxValue, await SqlPackageScalarAsync<int>(connection, "SELECT mapped_external.controlled_key_cmp('17','22')"));
        await ExecuteSqlPackageAsync(connection, """
            CREATE INDEX external_key_index ON public.external_kept USING hash(value);
            CREATE TEMP TABLE controlled_rows(id integer,value mapped_external.controlled_key);
            INSERT INTO controlled_rows VALUES(1,'17'),(2,'19'),(3,'31');
            CREATE INDEX controlled_index ON controlled_rows USING hash(value mapped_external.controlled_hash);
            SET enable_seqscan=off; SET enable_bitmapscan=off;
            """);
        const string ordinaryQuery = "SELECT id FROM public.external_kept WHERE value OPERATOR(mapped_external.=) '10'::mapped_external.external_key ORDER BY id";
        const string controlledQuery = "SELECT id FROM controlled_rows WHERE value OPERATOR(mapped_external.=) '10'::mapped_external.controlled_key ORDER BY id";
        await AssertDeclarationIndex(connection, ordinaryQuery, "external_key_index");
        await AssertDeclarationIndex(connection, controlledQuery, "controlled_index");
        Assert.AreSequenceEqual([1, 2], await SqlPackageScalarAsync<int[]>(connection, "SELECT ARRAY(" + ordinaryQuery + ")"));
        Assert.AreSequenceEqual([1, 2], await SqlPackageScalarAsync<int[]>(connection, "SELECT ARRAY(" + controlledQuery + ")"));
        PostgresException fixedSchema = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteSqlPackageAsync(connection,
            "ALTER EXTENSION ankus_tool_probe SET SCHEMA mapped_move"));
        Assert.AreEqual("0A000", fixedSchema.SqlState);
        Assert.AreEqual("extension \"ankus_tool_probe\" does not support SET SCHEMA", fixedSchema.MessageText);
        await ExecuteSqlPackageAsync(connection, "DROP INDEX public.external_key_index; DROP TABLE controlled_rows; DROP EXTENSION ankus_tool_probe");
        Assert.AreSequenceEqual(externalBefore, await MappedPackageSnapshot(connection, "mapped_external"));
        Assert.AreEqual("1:17,2:19,3:31,4:22,5:NULL", await MappedExternalRows(connection));
        Assert.AreEqual(connection.ProcessID, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Creates independent heap data and the two default index access paths before relocation.
    /// </summary>
    private async Task CreateMappedPackageIndexes(NpgsqlConnection connection, string schema)
        => await ExecuteSqlPackageAsync(connection, $"""
            CREATE TABLE public.mapped_package_btree_rows(id integer,value {schema}.package_key);
            INSERT INTO public.mapped_package_btree_rows VALUES(1,'17'),(2,'19'),(3,'31'),(4,'22'),(5,NULL);
            CREATE TABLE public.mapped_package_hash_rows AS TABLE public.mapped_package_btree_rows;
            CREATE INDEX mapped_package_btree ON public.mapped_package_btree_rows USING btree(value);
            CREATE INDEX mapped_package_hash ON public.mapped_package_hash_rows USING hash(value);
            SET enable_seqscan=off; SET enable_bitmapscan=off;
            """);

    /// <summary>
    /// Reads exact values through live mapped readers and both independently named indexes.
    /// </summary>
    private async Task AssertMappedPackageIndexes(NpgsqlConnection connection, string schema)
    {
        string btree = $"SELECT id FROM public.mapped_package_btree_rows WHERE value OPERATOR({schema}.<) '20'::{schema}.package_key ORDER BY id";
        string hash = $"SELECT id FROM public.mapped_package_hash_rows WHERE value OPERATOR({schema}.=) '10'::{schema}.package_key ORDER BY id";
        await AssertDeclarationIndex(connection, btree, "mapped_package_btree");
        await AssertDeclarationIndex(connection, hash, "mapped_package_hash");
        Assert.AreSequenceEqual([3], await SqlPackageScalarAsync<int[]>(connection, "SELECT ARRAY(" + btree + ")"));
        Assert.AreSequenceEqual([1, 2], await SqlPackageScalarAsync<int[]>(connection, "SELECT ARRAY(" + hash + ")"));
        Assert.AreEqual("12382|12382|12382|12345|true|true", await SqlPackageScalarAsync<string>(connection, $"""
            SELECT concat_ws('|',{schema}.package_key_hash('17'),{schema}.package_key_hash('19'),
                {schema}.package_key_hash('31'),{schema}.package_key_hash('22'),
                ({schema}.package_key_eq(NULL,'17') IS NULL)::text,({schema}.package_key_hash(NULL) IS NULL)::text)
            """));
        Assert.AreEqual("1:17,2:19,3:31,4:22,5:NULL", await SqlPackageScalarAsync<string>(connection, """
            SELECT string_agg(id::text||':'||coalesce(value::text,'NULL'),',' ORDER BY id) FROM public.mapped_package_btree_rows
            """));
    }

    /// <summary>
    /// Checks every owned type, helper, operator, class and family identity without a count-only oracle.
    /// </summary>
    private async Task<Dictionary<string, uint>> MappedPackageMembers(NpgsqlConnection connection, string schema)
    {
        string[] expected = ["array:package_key", "class:package_key_btree_ops", "class:package_key_hash_ops",
            "family:package_key_btree_ops", "family:package_key_hash_ops", "function:package_key_cmp", "function:package_key_eq",
            "function:package_key_ge", "function:package_key_gt", "function:package_key_hash", "function:package_key_in",
            "function:package_key_le", "function:package_key_lt", "function:package_key_ne", "function:package_key_out",
            "operator:<", "operator:<=", "operator:<>", "operator:=", "operator:>", "operator:>=", "type:package_key"];
        await using var command = new NpgsqlCommand($"""
            WITH objects(label,classid,objid,ownerid) AS (
                SELECT 'type:'||typname,'pg_type'::regclass,oid,oid FROM pg_type WHERE typnamespace='{schema}'::regnamespace AND typname='package_key'
                UNION ALL SELECT 'array:'||t.typname,'pg_type'::regclass,t.typarray,t.oid FROM pg_type t
                    WHERE typnamespace='{schema}'::regnamespace AND typname='package_key'
                UNION ALL SELECT 'function:'||proname,'pg_proc'::regclass,oid,oid FROM pg_proc WHERE pronamespace='{schema}'::regnamespace
                UNION ALL SELECT 'operator:'||oprname,'pg_operator'::regclass,oid,oid FROM pg_operator WHERE oprnamespace='{schema}'::regnamespace
                UNION ALL SELECT 'family:'||opfname,'pg_opfamily'::regclass,oid,oid FROM pg_opfamily WHERE opfnamespace='{schema}'::regnamespace
                UNION ALL SELECT 'class:'||opcname,'pg_opclass'::regclass,oid,oid FROM pg_opclass WHERE opcnamespace='{schema}'::regnamespace)
            SELECT label,objid,EXISTS(SELECT FROM pg_depend d JOIN pg_extension e ON e.oid=d.refobjid
                WHERE d.classid=objects.classid AND d.objid=objects.ownerid AND d.refclassid='pg_extension'::regclass
                    AND d.deptype='e' AND e.extname='ankus_tool_probe') FROM objects ORDER BY label COLLATE "C"
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
        var members = new Dictionary<string, uint>(StringComparer.Ordinal);
        while (await reader.ReadAsync(context.CancellationToken))
        {
            string name = reader.GetString(0);
            Assert.IsTrue(reader.GetBoolean(2), name);
            members.Add(name, reader.GetFieldValue<uint>(1));
        }

        Assert.AreSequenceEqual(expected, members.Keys);
        return members;
    }

    /// <summary>
    /// Captures complete namespace object identities before installation, rollback and removal.
    /// </summary>
    private async Task<string[]> MappedPackageSnapshot(NpgsqlConnection connection, string schema)
    {
        await using var command = new NpgsqlCommand($"""
            SELECT label||':'||oid::text FROM(
                SELECT 'type:'||typname AS label,oid FROM pg_type WHERE typnamespace='{schema}'::regnamespace
                UNION ALL SELECT 'function:'||proname,oid FROM pg_proc WHERE pronamespace='{schema}'::regnamespace
                UNION ALL SELECT 'operator:'||oprname,oid FROM pg_operator WHERE oprnamespace='{schema}'::regnamespace
                UNION ALL SELECT 'family:'||opfname,oid FROM pg_opfamily WHERE opfnamespace='{schema}'::regnamespace
                UNION ALL SELECT 'class:'||opcname,oid FROM pg_opclass WHERE opcnamespace='{schema}'::regnamespace
                UNION ALL SELECT 'relation:'||relname,oid FROM pg_class WHERE relnamespace='{schema}'::regnamespace
            ) objects ORDER BY label COLLATE "C",oid
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
        var values = new List<string>();
        while (await reader.ReadAsync(context.CancellationToken))
        {
            values.Add(reader.GetString(0));
        }

        return [.. values];
    }

    /// <summary>
    /// Preserves all preexisting external values, including NULL, without invoking a generated reader.
    /// </summary>
    private Task<string> MappedExternalRows(NpgsqlConnection connection)
        => SqlPackageScalarAsync<string>(connection,
            "SELECT string_agg(id::text||':'||coalesce(value::text,'NULL'),',' ORDER BY id) FROM public.external_kept");

    /// <summary>
    /// Supplies genuine PostgreSQL by-value I/O independently of any generated callback.
    /// </summary>
    private static string MappedPackageTypeSql(string type) => $"""
        CREATE TYPE {type};
        CREATE FUNCTION {type}_in(cstring) RETURNS {type} LANGUAGE internal IMMUTABLE STRICT AS 'int4in';
        CREATE FUNCTION {type}_out({type}) RETURNS cstring LANGUAGE internal IMMUTABLE STRICT AS 'int4out';
        CREATE TYPE {type}(INPUT={type}_in,OUTPUT={type}_out,LIKE=int4);
        """;

    /// <summary>
    /// Emits a read-only mapped root and derives without ordinary functions masking native fragment requirements.
    /// </summary>
    private static string MappedPackageContract(string managed, string sql, string options, string families) => $$"""
        [PgDatumType("{{sql}}", typeof({{managed}}Reader){{options}})][PgEquality]{{families}}
        public readonly record struct {{managed}}(int Word) : System.IComparable<{{managed}}>, IPgHashable
        {
            public bool Equals({{managed}} other) => Word/10 == other.Word/10;
            public int CompareTo({{managed}} other) => Word/10 > other.Word/10 ? int.MinValue : Word/10 < other.Word/10 ? int.MaxValue : 0;
            public int GetPostgresHashCode() => 12345 + ((Word/10)&1)*37;
            public override int GetHashCode() => GetPostgresHashCode();
            public static bool operator <({{managed}} left, {{managed}} right) => left.CompareTo(right)<0;
            public static bool operator <=({{managed}} left, {{managed}} right) => left.CompareTo(right)<=0;
            public static bool operator >({{managed}} left, {{managed}} right) => left.CompareTo(right)>0;
            public static bool operator >=({{managed}} left, {{managed}} right) => left.CompareTo(right)>=0;
        }
        public sealed class {{managed}}Reader : IPgDatumReader<{{managed}}>
        {
            public {{managed}} Read(PgDatum value) => new(unchecked((int)value.DangerousGetBits()));
        }
        """;
}
