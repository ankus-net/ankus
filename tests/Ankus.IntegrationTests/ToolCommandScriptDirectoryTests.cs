using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Install and package preserve the declared target layout and exact bytes without writing outside the staging root.
    /// </summary>
    /// <param name="operation">The installation or packaging command.</param>
    /// <param name="kind">The SQL directory partition.</param>
    [TestMethod]
    [DataRow("install", "nested")]
    [DataRow("install", "empty")]
    [DataRow("install", "parent")]
    [DataRow("install", "absolute")]
    [DataRow("package", "nested")]
    [DataRow("package", "empty")]
    [DataRow("package", "parent")]
    [DataRow("package", "absolute")]
    [DataRow("install", "nested-parent")]
    [DataRow("package", "nested-parent")]
    public async Task ScriptDirectoryStagingPreservesDeclaredLayout(string operation, string kind)
    {
        CancellationToken token = context.CancellationToken;
        string absolute = Path.Join(CreateDirectory(), "SQL 'files");
        string directory = kind switch
        {
            "nested" => "nested/SQL files",
            "empty" => "",
            "parent" => "../sibling SQL",
            "nested-parent" => "traversal parent/../after traversal",
            _ => absolute,
        };
        string source = CreateDirectory();
        PublishedExtension manifest = await CopyScriptDirectoryPublicationAsync(source, directory, token);
        string stage = CreateDirectory();
        ProcessResult result = await InvokeAsync([operation, "--home", s_home, "--pg", MajorText(), "--from", source,
            operation == "package" ? "--output" : "--destdir", stage], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string shared = operation == "package" ? PackageSharedDirectory(stage) : StagedPath(stage, s_installation.SharedDirectory);
        string scripts = kind switch
        {
            "nested" => Path.Join(shared, "nested", "SQL files"),
            "empty" => shared,
            "parent" => Path.Join(Directory.GetParent(shared)!.FullName, "sibling SQL"),
            "nested-parent" => Path.Join(shared, "after traversal"),
            _ => StagedPath(stage, absolute),
        };
        string[] files = [manifest.Sql, .. manifest.UpgradeScripts, .. manifest.VersionControlFiles];
        foreach (string file in files)
        {
            Assert.AreSequenceEqual(await File.ReadAllBytesAsync(Path.Join(source, "extension", file), token),
                await File.ReadAllBytesAsync(Path.Join(scripts, file), token));
        }

        Assert.AreSequenceEqual(await File.ReadAllBytesAsync(Path.Join(source, "extension", manifest.Control), token),
            await File.ReadAllBytesAsync(Path.Join(shared, "extension", manifest.Control), token));
        Assert.AreEqual(directory, ExtensionControlFile.Read(Path.Join(shared, "extension", manifest.Control))["directory"]);
        Assert.HasCount(5, Directory.GetFiles(stage, "*", SearchOption.AllDirectories));
        Assert.IsFalse(Directory.Exists(absolute));
        if (kind == "nested-parent" && !OperatingSystem.IsWindows())
        {
            Assert.IsTrue(Directory.Exists(Path.Join(shared, "traversal parent")));
        }
    }

    /// <summary>
    /// A portable Windows package cannot write a parent-relative directory outside its output root.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task ScriptDirectoryCannotEscapePortableWindowsPackage()
    {
        string source = CreateDirectory();
        await CopyScriptDirectoryPublicationAsync(source, "../../escaped SQL", context.CancellationToken);
        string stage = CreateDirectory();
        ProcessResult result = await InvokeAsync(["package", "--home", s_home, "--pg", MajorText(), "--from", source, "--output", stage],
            context.CancellationToken);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("escapes the portable Windows package root", result.StandardError);
        Assert.IsEmpty(Directory.GetFiles(stage, "*", SearchOption.AllDirectories));
    }

    /// <summary>
    /// Real PostgreSQL finds installation SQL, version controls and transactional upgrades in the authored directory.
    /// </summary>
    /// <param name="kind">The directory partition executed by PostgreSQL.</param>
    [TestMethod]
    [DataRow("nested")]
    [DataRow("empty")]
    [DataRow("absolute")]
    [DataRow("nested-parent")]
    public async Task AuthoredScriptDirectoriesInstallAndUpgradeInPostgres(string kind)
        => await VerifyAuthoredScriptDirectoryAsync(kind);

    /// <summary>
    /// Unix parent traversal starts from a symbolic link's target, matching PostgreSQL's filesystem lookup.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task AuthoredScriptDirectoryFollowsUnixLinks()
        => await VerifyAuthoredScriptDirectoryAsync("linked-parent");

    private async Task VerifyAuthoredScriptDirectoryAsync(string kind)
    {
        CancellationToken token = context.CancellationToken;
        string directory = kind switch
        {
            "nested" => "extension scripts/v1",
            "empty" => "",
            "nested-parent" => "traversal parent/../after traversal",
            "linked-parent" => "traversal link/../after link",
            _ => Path.Join(CreateDirectory(), "SQL 'files"),
        };
        string projectRoot = CreateDirectory();
        string project = await CreateControlProjectAsync(projectRoot, token, "ankus_directory_probe");
        await File.WriteAllTextAsync(Path.Join(projectRoot, "author settings.control"), ExtensionControlFile.Format(
            new Dictionary<string, string> { ["directory"] = directory, ["comment"] = "primary" }), token);
        string sql = Path.Join(projectRoot, "sql");
        Directory.CreateDirectory(sql);
        const string Upgrade = "ankus_directory_probe--0.1.0--next.sql";
        await File.WriteAllTextAsync(Path.Join(sql, "ankus_directory_probe--0.1.0.control"), "comment='directory current'", token);
        await File.WriteAllTextAsync(Path.Join(sql, "ankus_directory_probe--next.control"), "relocatable=false", token);
        await File.WriteAllTextAsync(Path.Join(sql, Upgrade),
            "CREATE TABLE directory_state (value text); INSERT INTO directory_state VALUES ('café 🐘'); SELECT 1/0;", token);
        string output = CreateDirectory();
        PublishedExtension manifest = await PublishControlProbeAsync(project, output, token);
        Assert.AreEqual(directory, manifest.ScriptDirectory);
        Assert.IsTrue(File.Exists(Path.Join(output, "extension", manifest.Sql)));
        await using PostgresTestInstallation? owned = _caseInstallation is null
            ? await PostgresTestInstallation.StageAsync(s_installation, CreateDirectory(), token) : null;
        PostgresTestInstallation installationOwner = _caseInstallation ?? owned!;
        PostgresInstallation installation = installationOwner.Installation;
        string ownedRoot = IntegrationEnvironment.PhysicalDirectory(new DirectoryInfo(installationOwner.RootDirectory)) + Path.DirectorySeparatorChar;
        Assert.StartsWith(ownedRoot, IntegrationEnvironment.PhysicalDirectory(new DirectoryInfo(installation.LibraryDirectory)));
        Assert.StartsWith(ownedRoot, IntegrationEnvironment.PhysicalDirectory(new DirectoryInfo(installation.SharedDirectory)));
        string linkedDestination = Path.Join(installationOwner.RootDirectory, "directory link target", "after link");
        if (kind == "linked-parent")
        {
            string target = Path.Join(installationOwner.RootDirectory, "directory link target", "leaf");
            Directory.CreateDirectory(target);
            Directory.CreateSymbolicLink(Path.Join(installation.SharedDirectory, "traversal link"), target);
        }

        string home = CreateDirectory();
        ProcessResult registered = await InvokeAsync(["init", "--home", home, s_postgresOption, installation.PgConfigPath], token);
        Assert.AreEqual(0, registered.ExitCode, registered.StandardOutput + registered.StandardError);
        ProcessResult installed = await InvokeAsync(["install", "--home", home, "--pg", MajorText(), "--from", output], token);
        Assert.AreEqual(0, installed.ExitCode, installed.StandardOutput + installed.StandardError);
        string installedScripts = kind == "linked-parent" ? linkedDestination
            : manifest.GetScriptDirectory(output, installation.SharedDirectory);
        string[] files = [manifest.Sql, .. manifest.UpgradeScripts, .. manifest.VersionControlFiles];
        foreach (string file in files)
        {
            Assert.AreSequenceEqual(await File.ReadAllBytesAsync(Path.Join(output, "extension", file), token),
                await File.ReadAllBytesAsync(Path.Join(installedScripts, file), token));
        }

        Assert.AreEqual(directory, ExtensionControlFile.Read(Path.Join(installation.SharedDirectory, "extension", manifest.Control))["directory"]);
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
        {
            Installation = installation,
            DataDirectoryBase = Path.Join(s_root, "script-directory-pgdata"),
            LogDirectory = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs"),
            PostgreSqlConfiguration = ["dynamic_library_path = '" + EscapeSetting(installation.LibraryDirectory) + "'"],
        }, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_directory_probe");
        Assert.AreEqual("directory current", await SqlPackageScalarAsync<string>(connection,
            "SELECT obj_description(oid, 'pg_extension') FROM pg_extension WHERE extname='ankus_directory_probe'"));
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, "SELECT control_value()"));
        PostgresException failed = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_directory_probe UPDATE TO 'next'"));
        Assert.AreEqual("22012", failed.SqlState, failed.MessageText);
        Assert.AreEqual("0.1.0", await SqlPackageScalarAsync<string>(connection,
            "SELECT extversion FROM pg_extension WHERE extname='ankus_directory_probe'"));
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, "SELECT to_regclass('directory_state') IS NULL"));
        string upgrade = Path.Join(installedScripts, Upgrade);
        string corrected = (await File.ReadAllTextAsync(upgrade, token)).Replace("SELECT 1/0;", "", StringComparison.Ordinal);
        await File.WriteAllTextAsync(upgrade, corrected, token);
        await ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_directory_probe UPDATE TO 'next'");
        Assert.AreEqual("next|false", await SqlPackageScalarAsync<string>(connection,
            "SELECT extversion || '|' || extrelocatable::text FROM pg_extension WHERE extname='ankus_directory_probe'"));
        Assert.AreEqual("café 🐘", await SqlPackageScalarAsync<string>(connection, "SELECT value FROM directory_state"));
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, "SELECT control_value()"));
        Assert.AreEqual(backend, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// The public testing package runs native tests with an absolute author directory while leaving that directory untouched.
    /// </summary>
    [TestMethod]
    public async Task GeneratedTestsIsolateAuthoredScriptDirectory()
    {
        CancellationToken token = context.CancellationToken;
        string output = Path.Join(CreateDirectory(), "directory fixture");
        ProcessResult created = await InvokeAsync(["new", "DirectoryFixture", "-o", output], token);
        Assert.AreEqual(0, created.ExitCode, created.StandardOutput + created.StandardError);
        string project = Path.Join(output, "src", "DirectoryFixture", "DirectoryFixture.csproj");
        XDocument definition = XDocument.Load(project);
        definition.Root!.Add(new XElement("PropertyGroup",
            new XElement("AnkusExtensionControlFile", "author.control")));
        definition.Save(project);
        string authorDirectory = CreateDirectory();
        string marker = Path.Join(authorDirectory, "retain.txt");
        await File.WriteAllTextAsync(marker, "untouched", token);
        string control = ExtensionControlFile.Format(new Dictionary<string, string> { ["directory"] = authorDirectory });
        string projectDirectory = Path.GetDirectoryName(project)!;
        string controlFile = Path.Join(projectDirectory, "author.control");
        await File.WriteAllTextAsync(controlFile, control, token);
        ProcessResult tests = await PackageProcessRunner.RunAsync("dotnet", ["test", "--report-trx", "-p:AnkusPostgresMajor=" + MajorText()],
            s_environment, token, workingDirectory: output);
        tests.EnsureSuccess("dotnet", ["test"]);
        string trx = Assert.ContainsSingle(Directory.GetFiles(output, "*.trx", SearchOption.AllDirectories));
        XDocument report = XDocument.Load(trx);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        XElement counters = report.Descendants(ns + "Counters").Single();
        Assert.AreEqual("7", counters.Attribute("passed")!.Value);
        Assert.AreEqual("0", counters.Attribute("failed")!.Value);
        Assert.Contains("FunctionsExecuteInPostgres", report.Descendants(ns + "UnitTestResult").Select(element => element.Attribute("testName")!.Value));
        Assert.Contains("ManagedErrorsLeaveBackendUsable", report.Descendants(ns + "UnitTestResult").Select(element => element.Attribute("testName")!.Value));
        Assert.Contains("DirectoryFixture.BackendChecks.AdditionInsidePostgres()", report.Descendants(ns + "UnitTestResult").Select(element => element.Attribute("testName")!.Value));
        Assert.Contains("DirectoryFixture.BackendChecks.ExpectedFailure()", report.Descendants(ns + "UnitTestResult").Select(element => element.Attribute("testName")!.Value));
        Assert.AreEqual(control, await File.ReadAllTextAsync(controlFile, token));
        Assert.AreEqual(marker, Assert.ContainsSingle(Directory.GetFiles(authorDirectory, "*", SearchOption.AllDirectories)));
        Assert.AreEqual("untouched", await File.ReadAllTextAsync(marker, token));
        Assert.IsEmpty(Directory.GetDirectories(Path.Join(projectDirectory, "bin", "ankus-test-publish")));
    }

    private static async Task<PublishedExtension> CopyScriptDirectoryPublicationAsync(string source, string directory, CancellationToken token)
    {
        PublishedExtension original = PublishedExtension.Read(s_published);
        var manifest = new PublishedExtension(original.PostgresMajor, original.RuntimeIdentifier, original.Library, original.Control,
            original.Sql, ["ankus_tool_probe--0.1.0--next.sql"], ["ankus_tool_probe--next.control"], directory);
        string extension = Path.Join(source, "extension");
        Directory.CreateDirectory(extension);
        File.Copy(Path.Join(s_published, original.Library), Path.Join(source, original.Library));
        File.Copy(Path.Join(s_published, "extension", original.Sql), Path.Join(extension, original.Sql));
        var settings = new Dictionary<string, string>(ExtensionControlFile.Read(Path.Join(s_published, "extension", original.Control)), StringComparer.Ordinal)
        {
            ["directory"] = directory,
        };
        await File.WriteAllTextAsync(Path.Join(extension, original.Control), ExtensionControlFile.Format(settings), token);
        await File.WriteAllTextAsync(Path.Join(extension, manifest.UpgradeScripts[0]), "SELECT 'café 🐘';\r\n", token);
        await File.WriteAllTextAsync(Path.Join(extension, manifest.VersionControlFiles[0]), "relocatable=false\r\n", token);
        await File.WriteAllTextAsync(Path.Join(extension, "unlisted.sql"), "unlisted", token);
        manifest.Write(source);
        return manifest;
    }
}
