using System.Runtime.InteropServices;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Actual package updates preserve rows, replace native behavior, and roll back a failed multi-step upgrade in one session.
    /// </summary>
    [TestMethod]
    public async Task PackagedUpgradePreservesDataAndRecoversFromFailedUpdate()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Combine(directory, "UpgradeProbe.csproj");
        string source = Path.Combine(directory, "Functions.cs");
        XDocument document = XDocument.Load(s_project);
        document.Root!.Add(new XElement("PropertyGroup",
            new XElement("AssemblyName", "Ankus.Upgrade.Base"),
            new XElement("AnkusExtensionName", "ankus_upgrade_probe"),
            new XElement("AnkusExtensionVersion", "base")));
        document.Save(project);
        await File.WriteAllTextAsync(source, UpgradeSource(1), token);
        string publication = Path.Combine(directory, "bin", "ankus", s_installation.Label,
            RuntimeInformation.RuntimeIdentifier, "Release");
        string package = CreateDirectory();
        string[] arguments = ["package", "--home", s_home, "--pg", MajorText(), "--project", project, "--output", package];
        (await InvokeAsync(arguments, token)).EnsureSuccess(s_tool, arguments);
        PublishedExtension original = PublishedExtension.Read(publication);
        PostgresInstallation installation = await ReserveCaseInstallationAsync(token);
        CopyUpgradePackageToCaseInstallation(package, original);
        List<string> configuration = ["dynamic_library_path = '" + EscapeSetting(PackageLibraryDirectory(package)) + "'"];
        if (installation.Version.Major >= 18)
        {
            configuration.Add("extension_control_path = '" + EscapeSetting(PackageSharedDirectory(package)) + "'");
        }

        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
        {
            Installation = installation,
            DataDirectoryBase = Path.Combine(s_root, "upgrade-pgdata"),
            LogDirectory = Path.Combine(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs"),
            PostgreSqlConfiguration = configuration,
        }, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await ExecuteSqlPackageAsync(connection, """
            CREATE EXTENSION ankus_upgrade_probe;
            CREATE TABLE upgrade_rows (id integer PRIMARY KEY, payload text);
            ALTER EXTENSION ankus_upgrade_probe ADD TABLE upgrade_rows;
            INSERT INTO upgrade_rows VALUES (7, 'café 🐘');
            """);
        Assert.AreEqual(1, await SqlPackageScalarAsync<int>(connection, "SELECT upgrade_value()"));

        document.Root.Elements("PropertyGroup").Last().Element("AssemblyName")!.Value = "Ankus.Upgrade.Release";
        document.Root.Elements("PropertyGroup").Last().Element("AnkusExtensionVersion")!.Value = "release";
        document.Save(project);
        await File.WriteAllTextAsync(source, UpgradeSource(2), token);
        string[] publishArguments = ["publish", "--home", s_home, "--pg", MajorText(), "--project", project, "--output", publication];
        (await InvokeAsync(publishArguments, token)).EnsureSuccess(s_tool, publishArguments);
        PublishedExtension replacement = PublishedExtension.Read(publication);
        string upgradeSql = (await File.ReadAllTextAsync(Path.Combine(publication, "extension", replacement.Sql), token))
            .Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal);
        string scripts = Path.Combine(directory, "sql");
        Directory.CreateDirectory(scripts);
        const string First = "ankus_upgrade_probe--base--middle.sql";
        const string Second = "ankus_upgrade_probe--middle--release.sql";
        await File.WriteAllTextAsync(Path.Combine(scripts, First), "ALTER TABLE upgrade_rows ADD COLUMN marker text;", token);
        string secondSql = upgradeSql + "\nSELECT upgrade_value();\nUPDATE upgrade_rows SET marker = '@EXTENSION_VERSION@';\n";
        await File.WriteAllTextAsync(Path.Combine(scripts, Second), secondSql + "SELECT 1/0;\n", token);
        (await InvokeAsync(arguments, token)).EnsureSuccess(s_tool, arguments);
        PublishedExtension updated = PublishedExtension.Read(publication);
        Assert.AreSequenceEqual<string>([First, Second], updated.UpgradeScripts);
        Assert.AreNotEqual(original.Library, updated.Library);
        CopyUpgradePackageToCaseInstallation(package, updated);
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_upgrade_probe UPDATE TO 'release'"));
        Assert.AreEqual("22012", error.SqlState, error.MessageText);
        Assert.AreEqual("base", await SqlPackageScalarAsync<string>(connection,
            "SELECT extversion FROM pg_extension WHERE extname='ankus_upgrade_probe'"));
        Assert.AreEqual("7|café 🐘", await SqlPackageScalarAsync<string>(connection,
            "SELECT id::text || '|' || payload FROM upgrade_rows"));
        Assert.IsFalse(await SqlPackageScalarAsync<bool>(connection,
            "SELECT EXISTS(SELECT FROM pg_attribute WHERE attrelid='upgrade_rows'::regclass AND attname='marker' AND NOT attisdropped)"));
        Assert.AreEqual(1, await SqlPackageScalarAsync<int>(connection, "SELECT upgrade_value()"));

        // Repair only the failing SQL in the installed package; loaded native libraries remain versioned and intact.
        string packagedUpgrade = Path.Combine(PackageSharedDirectory(package), "extension", Second);
        string corrected = (await File.ReadAllTextAsync(packagedUpgrade, token)).Replace("SELECT 1/0;\n", "", StringComparison.Ordinal);
        await File.WriteAllTextAsync(packagedUpgrade, corrected, token);
        CopyUpgradePackageToCaseInstallation(package, updated);
        await ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_upgrade_probe UPDATE TO 'release'");
        Assert.AreEqual("release", await SqlPackageScalarAsync<string>(connection,
            "SELECT extversion FROM pg_extension WHERE extname='ankus_upgrade_probe'"));
        Assert.AreEqual("7|café 🐘|release", await SqlPackageScalarAsync<string>(connection,
            "SELECT id::text || '|' || payload || '|' || marker FROM upgrade_rows"));
        Assert.AreEqual(2, await SqlPackageScalarAsync<int>(connection, "SELECT upgrade_value()"));
        Assert.AreEqual(backend, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
        await ExecuteSqlPackageAsync(connection, "DROP EXTENSION ankus_upgrade_probe");
        Assert.IsTrue(await SqlPackageScalarAsync<bool>(connection, "SELECT to_regclass('upgrade_rows') IS NULL"));
    }

    /// <summary>
    /// Existing publications install all declared upgrade and secondary control bytes and exclude unlisted files.
    /// </summary>
    /// <param name="operation">The installation or packaging command.</param>
    [TestMethod]
    [DataRow("install")]
    [DataRow("package")]
    public async Task InstallAndPackageCopyOnlyDeclaredExtensionFiles(string operation)
    {
        CancellationToken token = context.CancellationToken;
        PublishedExtension original = PublishedExtension.Read(s_published);
        string source = CreateDirectory();
        string extension = Path.Combine(source, "extension");
        Directory.CreateDirectory(extension);
        string[] scripts = ["ankus_tool_probe--base--middle.sql", "ankus_tool_probe--middle--0.1.0.sql"];
        var manifest = new PublishedExtension(original.PostgresMajor, original.RuntimeIdentifier, original.Library,
            original.Control, original.Sql, scripts, ["ankus_tool_probe--base.control"]);
        manifest.Write(source);
        File.Copy(Path.Combine(s_published, original.Library), Path.Combine(source, original.Library));
        foreach (string file in new[] { original.Control, original.Sql })
        {
            File.Copy(Path.Combine(s_published, "extension", file), Path.Combine(extension, file));
        }

        foreach (string script in scripts.Concat(manifest.VersionControlFiles))
        {
            string content = script.EndsWith(".control", StringComparison.Ordinal)
                ? "comment='version # control'\r\nrequires=''\r\n" : "-- " + script + "\r\nSELECT 'café 🐘';\r\n";
            await File.WriteAllTextAsync(Path.Combine(extension, script), content, token);
        }

        await File.WriteAllTextAsync(Path.Combine(extension, "unlisted.sql"), "do not distribute", token);
        await File.WriteAllTextAsync(Path.Combine(extension, "unlisted.control"), "do not distribute", token);
        string stage = CreateDirectory();
        ProcessResult result = await InvokeAsync([operation, "--home", s_home, "--pg", MajorText(), "--from", source,
            operation == "package" ? "--output" : "--destdir", stage], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string shared = operation == "package" ? PackageSharedDirectory(stage) : StagedPath(stage, s_installation.SharedDirectory);
        foreach (string script in scripts.Concat(manifest.VersionControlFiles))
        {
            Assert.AreSequenceEqual(await File.ReadAllBytesAsync(Path.Combine(extension, script), token),
                await File.ReadAllBytesAsync(Path.Combine(shared, "extension", script), token));
        }

        Assert.HasCount(6, Directory.GetFiles(stage, "*", SearchOption.AllDirectories));
        Assert.IsFalse(File.Exists(Path.Combine(shared, "extension", "unlisted.control")));
        Assert.IsFalse(File.Exists(Path.Combine(shared, "extension", "unlisted.sql")));
    }

    /// <summary>
    /// A release that changes the assembly version and replaces the library in place keeps the functions its upgrade
    /// script does not recreate: their catalog entries still name symbols the new library exports.
    /// </summary>
    [TestMethod]
    public async Task InPlaceUpgradeKeepsUnchangedFunctionsAcrossAssemblyVersions()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Combine(directory, "VersionProbe.csproj");
        XDocument document = XDocument.Load(s_project);
        document.Root!.Add(new XElement("PropertyGroup",
            new XElement("AssemblyName", "Ankus.Version.Probe"),
            new XElement("AnkusExtensionName", "ankus_version_probe"),
            new XElement("Version", "1.0.0"),
            new XElement("AnkusExtensionVersion", "1.0.0")));
        document.Save(project);
        await File.WriteAllTextAsync(Path.Combine(directory, "Functions.cs"), """
            using Ankus;
            public static class VersionFunctions
            {
                [PgFunction(Name = "stable_value")]
                public static int Stable() => 42;
            }
            """, token);
        string publication = Path.Combine(directory, "bin", "ankus", s_installation.Label,
            RuntimeInformation.RuntimeIdentifier, "Release");
        string package = CreateDirectory();
        string[] arguments = ["package", "--home", s_home, "--pg", MajorText(), "--project", project, "--output", package];
        (await InvokeAsync(arguments, token)).EnsureSuccess(s_tool, arguments);
        PublishedExtension original = PublishedExtension.Read(publication);
        PostgresInstallation installation = await ReserveCaseInstallationAsync(token);
        CopyUpgradePackageToCaseInstallation(package, original);
        List<string> configuration = ["dynamic_library_path = '" + EscapeSetting(PackageLibraryDirectory(package)) + "'"];
        if (installation.Version.Major >= 18)
        {
            configuration.Add("extension_control_path = '" + EscapeSetting(PackageSharedDirectory(package)) + "'");
        }

        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
        {
            Installation = installation,
            DataDirectoryBase = Path.Combine(s_root, "version-upgrade-pgdata"),
            LogDirectory = Path.Combine(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs"),
            PostgreSqlConfiguration = configuration,
        }, token);
        await using (NpgsqlConnection connection = await cluster.OpenConnectionAsync(token))
        {
            await ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_version_probe");
            Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, "SELECT stable_value()"));
        }

        // The next release has a new assembly version, the same library file, and an upgrade script that leaves
        // stable_value in place.
        XElement properties = document.Root.Elements("PropertyGroup").Last();
        properties.Element("Version")!.Value = "2.0.0";
        properties.Element("AnkusExtensionVersion")!.Value = "2.0.0";
        document.Save(project);
        string scripts = Path.Combine(directory, "sql");
        Directory.CreateDirectory(scripts);
        await File.WriteAllTextAsync(Path.Combine(scripts, "ankus_version_probe--1.0.0--2.0.0.sql"), "SELECT 1;\n", token);
        (await InvokeAsync(arguments, token)).EnsureSuccess(s_tool, arguments);
        PublishedExtension updated = PublishedExtension.Read(publication);
        Assert.AreEqual(original.Library, updated.Library);
        CopyUpgradePackageToCaseInstallation(package, updated);
        await using NpgsqlConnection upgraded = await cluster.OpenConnectionAsync(token);
        await ExecuteSqlPackageAsync(upgraded, "ALTER EXTENSION ankus_version_probe UPDATE TO '2.0.0'");
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(upgraded, "SELECT stable_value()"));
        await ExecuteSqlPackageAsync(upgraded, "DROP EXTENSION ankus_version_probe");
    }

    private void CopyUpgradePackageToCaseInstallation(string package, PublishedExtension manifest)
    {
        if (_caseInstallation is null)
        {
            return;
        }

        string[] files = [manifest.Sql, .. manifest.UpgradeScripts, .. manifest.VersionControlFiles, manifest.Control];
        foreach (string file in files)
        {
            File.Copy(Path.Combine(PackageSharedDirectory(package), "extension", file),
                Path.Combine(_caseInstallation.Installation.SharedDirectory, "extension", file), overwrite: true);
        }
    }

    /// <summary>
    /// Direct dotnet publication invalidates prior manifests even when compilation fails before native helpers run.
    /// </summary>
    [TestMethod]
    public async Task DirectPublishFailureInvalidatesPreviousManifest()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Combine(directory, "Broken.csproj");
        File.Copy(s_project, project);
        await File.WriteAllTextAsync(Path.Combine(directory, "Broken.cs"), "#error broken direct publication", token);
        string output = CreateDirectory();
        File.Copy(Path.Combine(s_published, PublishedExtension.FileName), Path.Combine(output, PublishedExtension.FileName));
        ProcessResult result = await PackageProcessRunner.RunAsync("dotnet",
            ["publish", project, "--configuration", "Release", "--output", output,
                "-p:AnkusPostgresMajor=" + MajorText(),
                "-p:AnkusPgConfigPath=" + s_installation.PgConfigPath], s_environment, token, workingDirectory: directory);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("broken direct publication", result.StandardOutput + result.StandardError);
        Assert.IsFalse(File.Exists(Path.Combine(output, PublishedExtension.FileName)));
        Assert.IsTrue(File.Exists(Path.Combine(output, "ankus.extension.previous.json")));
    }

    private static string UpgradeSource(int value) => $$"""
        using Ankus;
        public static class UpgradeFunctions
        {
            [PgFunction(Name = "upgrade_value")]
            public static int Value() => {{value}};
        }
        """;
}
