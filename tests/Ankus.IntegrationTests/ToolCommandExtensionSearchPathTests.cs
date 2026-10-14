using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Published paths use the installation namespace, restore caller settings and remain executable after selected extraction.
    /// </summary>
    /// <param name="target">The real extension installation schema.</param>
    [TestMethod]
    [DataRow("extension_path")]
    [DataRow("Mixed café schema")]
    public async Task ExtensionSchemaSearchPathUsesInstalledNamespace(string target)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        SharedOutput shared = await GetSharedPublicationAsync(s_extensionSearchPathPublication, token);
        string output = shared.Directory;
        PublishedExtension publication = PublishedExtension.Read(output);
        string library = Path.Join(output, publication.Library);
        ExtensionSchema schema = ExtensionSchema.Read(library);
        Assert.IsFalse(schema.Relocatable);
        Assert.IsNull(schema.DefaultSchema);
        Assert.Contains("SET search_path TO \"pg_catalog\", @extschema@, \"pg_temp\"", schema.Sql);
        Assert.DoesNotContain("\"@extschema@\"", schema.Sql);
        Assert.AreEqual("false", ExtensionControlFile.Read(Path.Join(output, "extension", publication.Control))["relocatable"]);

        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(s_installation, Path.Join(directory, "server"), token);
        owner.InstallExtensionFiles(output);
        File.Copy(library, Path.Join(owner.Installation.LibraryDirectory, publication.Library));
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
        {
            Installation = owner.Installation,
            DataDirectoryBase = Path.Join(s_root, "pgdata"),
            LogDirectory = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs"),
        }, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        string quoted = "\"" + target.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        await ExecuteSqlPackageAsync(connection, $"""
            CREATE SCHEMA {quoted};
            CREATE EXTENSION ankus_extension_path WITH SCHEMA {quoted};
            CREATE TABLE {quoted}.owned_value(answer integer);
            INSERT INTO {quoted}.owned_value VALUES(42);
            CREATE TABLE public.owned_value(answer integer);
            INSERT INTO public.owned_value VALUES(-1);
            SET search_path TO public;
            """);
        string expected;
        await using (var command = new NpgsqlCommand("SELECT format('pg_catalog, %I, pg_temp', $1::text)", connection))
        {
            command.Parameters.AddWithValue(target);
            expected = Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(token));
        }

        Assert.AreEqual(expected, await SqlPackageScalarAsync<string>(connection, $"SELECT {quoted}.installed_path()"));
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, $"SELECT {quoted}.read_owned()"));
        Assert.AreEqual("public", await SqlPackageScalarAsync<string>(connection, "SELECT current_setting('search_path')"));
        ExtensionSchemaItem backendTest = Assert.ContainsSingle(schema.Graph!.Items.Where(static item => item.Names.Contains("Functions.VerifyOwned")));
        string testIdentity = Assert.ContainsSingle(backendTest.Attachments);
        Assert.StartsWith("FUNCTION ", testIdentity);
        await ExecuteSqlPackageAsync(connection, $"SELECT {quoted}.{testIdentity["FUNCTION ".Length..]}");
        Assert.AreEqual("public", await SqlPackageScalarAsync<string>(connection, "SELECT current_setting('search_path')"));
        Assert.IsFalse(await SqlPackageScalarAsync<bool>(connection, "SELECT extrelocatable FROM pg_extension WHERE extname='ankus_extension_path'"));
        PostgresException failed = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, $"SELECT {quoted}.path_failure()"));
        Assert.AreEqual("38000", failed.SqlState);
        Assert.AreEqual("extension path failure", failed.MessageText);
        Assert.AreEqual("public", await SqlPackageScalarAsync<string>(connection, "SELECT current_setting('search_path')"));
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, $"SELECT {quoted}.read_owned()"));

        ProcessResult missing = await InvokeAsync(["schema", "--from", library, "read_owned"], token);
        Assert.AreEqual(1, missing.ExitCode);
        Assert.IsEmpty(missing.StandardOutput);
        Assert.Contains("installation schema", missing.StandardError);
        string selectedPath = Path.Join(directory, "selected.sql");
        ProcessResult extracted = await InvokeAsync(["schema", "--from", library, "read_owned", "--schema", target, "-o", selectedPath], token);
        Assert.AreEqual(0, extracted.ExitCode, extracted.StandardError);
        Assert.IsEmpty(extracted.StandardOutput);
        Assert.IsEmpty(extracted.StandardError);
        string selected = await File.ReadAllTextAsync(selectedPath, token);
        Assert.AreEqual(schema.Select(["read_owned"], target).Sql, selected);
        Assert.DoesNotContain("@extschema@", selected);
        Assert.Contains(quoted + ".\"read_owned\"", selected);
        await ExecuteSqlPackageAsync(connection, $"ALTER EXTENSION ankus_extension_path DROP FUNCTION {quoted}.read_owned(); DROP FUNCTION {quoted}.read_owned();");
        await ExecuteSqlPackageAsync(connection, selected);
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, $"SELECT {quoted}.read_owned()"));
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, $"""
            SELECT EXISTS(SELECT FROM pg_depend d JOIN pg_extension e ON e.oid=d.refobjid
                WHERE d.classid='pg_proc'::regclass AND d.objid='{quoted}.read_owned()'::regprocedure
                    AND d.refclassid='pg_extension'::regclass AND d.deptype='e' AND e.extname='ankus_extension_path')
            """));
        await ExecuteSqlPackageAsync(connection, "CREATE SCHEMA other_path");
        PostgresException relocation = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_extension_path SET SCHEMA other_path"));
        Assert.AreEqual("0A000", relocation.SqlState);
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, $"SELECT {quoted}.read_owned()"));
        Assert.AreEqual(backend, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
        await ExecuteSqlPackageAsync(connection, "DROP EXTENSION ankus_extension_path");
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, $"SELECT answer FROM {quoted}.owned_value"));
        Assert.AreNotEqual(0, shared.Result.ExitCode);
        Assert.Contains("Control parameter 'relocatable' cannot be true", shared.Result.StandardOutput + shared.Result.StandardError);
    }

    /// <summary>
    /// Publishes the search-path probe once, then proves that the same project rejects a relocatable control file.
    /// </summary>
    /// <param name="directory">The class-owned project directory.</param>
    /// <param name="token">Cancels the fixture's compilation.</param>
    /// <returns>The immutable publication and the rejected relocatable publication's result.</returns>
    /// <remarks>
    /// Both installation-schema cases only read the publication. The rejected publication writes only its own
    /// output directory, and its diagnostic depends on the project rather than on either case's schema.
    /// </remarks>
    private static async Task<SharedOutput> PublishExtensionSearchPathAsync(string directory, CancellationToken token)
    {
        string project = await CreateControlProjectAsync(directory, token, "ankus_extension_path");
        XDocument definition = XDocument.Load(project);
        definition.Root!.Add(new XElement("PropertyGroup", new XElement("AnkusIncludeTests", true)));
        definition.Save(project);
        await File.WriteAllTextAsync(Path.Join(directory, "Functions.cs"), ExtensionSearchPathSource, token);
        string control = Path.Join(directory, "author settings.control");
        await File.WriteAllTextAsync(control, "comment = 'extension search path'", token);
        string output = Path.Join(directory, "published");
        await PublishControlProbeAsync(project, output, token);
        await File.WriteAllTextAsync(control, "relocatable = true", token);
        ProcessResult rejected = await RunDotnetAsync(["publish", project, "-c", "Release", "-r", System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier,
            "-o", Path.Join(directory, "rejected"), "-p:AnkusPostgresMajor=" + MajorText(), "-p:AnkusPgConfigPath=" + s_installation.PgConfigPath], token);
        return new SharedOutput(output, rejected);
    }

    /// <summary>
    /// Schema target selection cannot silently change full installation SQL, which is resolved by CREATE EXTENSION.
    /// </summary>
    [TestMethod]
    public async Task SchemaTargetRequiresSelectedDeclarations()
    {
        string library = Path.Join(s_published, PublishedExtension.Read(s_published).Library);
        ProcessResult result = await InvokeAsync(["schema", "--from", library, "--schema", "target"], context.CancellationToken);
        Assert.AreEqual(1, result.ExitCode);
        Assert.IsEmpty(result.StandardOutput);
        Assert.Contains("--schema requires at least one schema item", result.StandardError);
    }

    /// <summary>
    /// Supplies native callbacks whose unqualified lookup depends on the chosen installation schema.
    /// </summary>
    private const string ExtensionSearchPathSource = """
        using Ankus;
        public static partial class Functions
        {
            [PgFunction(SearchPath=["pg_catalog",PgSearchPath.ExtensionSchema,"pg_temp"])]
            public static string InstalledPath()=>Spi.ExecuteScalar<string>("SELECT current_setting('search_path')");
            [PgFunction(SearchPath=["pg_catalog",PgSearchPath.ExtensionSchema,"pg_temp"])]
            public static int ReadOwned()=>Spi.ExecuteScalar<int>("SELECT answer FROM owned_value");
            [PgFunction(SearchPath=["pg_catalog",PgSearchPath.ExtensionSchema,"pg_temp"])]
            public static int PathFailure()=>throw new System.InvalidOperationException("extension path failure");
            [PgTest(SearchPath=["pg_catalog",PgSearchPath.ExtensionSchema,"pg_temp"])]
            public static void VerifyOwned()
            {
                if(Spi.ExecuteScalar<int>("SELECT answer FROM owned_value")!=42)
                {
                    throw new System.InvalidOperationException("Backend test used the wrong search path.");
                }
            }
        }
        """;
}
