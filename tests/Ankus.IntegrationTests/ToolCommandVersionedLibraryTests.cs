using System.Runtime.InteropServices;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Versioned libraries from two publications install and package side by side, each version's SQL names its own
    /// library, and ALTER EXTENSION UPDATE moves one backend between the versions in both directions.
    /// </summary>
    [TestMethod]
    public async Task VersionedLibrariesInstallSideBySideAndUpdateBetweenVersions()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = Path.Join(directory, "VersionedProbe.csproj");
        string source = Path.Join(directory, "Functions.cs");
        XDocument document = XDocument.Load(s_project);
        // An empty AnkusExtensionVersion follows the project's ordinary .NET Version.
        document.Root!.Add(new XElement("PropertyGroup",
            new XElement("AssemblyName", "Ankus.Versioned.Probe"),
            new XElement("AnkusExtensionName", "ankus_versioned_probe"),
            new XElement("AnkusExtensionVersion", ""),
            new XElement("Version", "0.1.0"),
            new XElement("AnkusVersionedLibrary", "true")));
        document.Save(project);
        await File.WriteAllTextAsync(source, VersionedSource(1), token);
        string publication = Path.Join(directory, "bin", "ankus", s_installation.Label, RuntimeInformation.RuntimeIdentifier, "Release");
        string package = CreateDirectory();
        string stage = CreateDirectory();
        string[] packageArguments = ["package", "--home", s_home, "--pg", MajorText(), "--project", project, "--output", package];
        string[] installArguments = ["install", "--home", s_home, "--pg", MajorText(), "--from", publication, "--destdir", stage];
        (await InvokeAsync(packageArguments, token)).EnsureSuccess(s_tool, packageArguments);
        (await InvokeAsync(installArguments, token)).EnsureSuccess(s_tool, installArguments);
        PublishedExtension first = PublishedExtension.Read(publication);
        string suffix = CustomLibraryNameExampleTests.LibrarySuffix(s_installation.Version.Major);
        Assert.AreEqual("Ankus.Versioned.Probe-0.1.0" + suffix, first.Library);
        Assert.IsFalse(ExtensionControlFile.Read(Path.Join(publication, "extension", first.Control)).ContainsKey("module_pathname"));
        ProcessResult property = await InvokeAsync(["get", "module_pathname", "--from", publication], token);
        Assert.AreEqual(0, property.ExitCode, property.StandardOutput + property.StandardError);
        Assert.AreEqual("", property.StandardOutput);
        string firstSql = await File.ReadAllTextAsync(Path.Join(publication, "extension", first.Sql), token);
        Assert.Contains("AS 'Ankus.Versioned.Probe-0.1.0', ", firstSql);
        Assert.DoesNotContain("MODULE_PATHNAME", firstSql);

        document.Root.Elements("PropertyGroup").Last().Element("Version")!.Value = "0.2.0";
        document.Save(project);
        await File.WriteAllTextAsync(source, VersionedSource(2), token);
        string[] publishArguments = ["publish", "--home", s_home, "--pg", MajorText(), "--project", project, "--output", publication];
        (await InvokeAsync(publishArguments, token)).EnsureSuccess(s_tool, publishArguments);
        string secondSql = await File.ReadAllTextAsync(Path.Join(publication, "extension", PublishedExtension.Read(publication).Sql), token);

        // Upgrade scripts can use MODULE_PATHNAME; each resolves to the library of the version it updates to.
        string scripts = Path.Join(directory, "sql");
        Directory.CreateDirectory(scripts);
        const string Up = "ankus_versioned_probe--0.1.0--0.2.0.sql";
        const string Down = "ankus_versioned_probe--0.2.0--0.1.0.sql";
        await File.WriteAllTextAsync(Path.Join(scripts, Up), Replaceable(secondSql, "0.2.0"), token);
        await File.WriteAllTextAsync(Path.Join(scripts, Down), Replaceable(firstSql, "0.1.0"), token);
        (await InvokeAsync(packageArguments, token)).EnsureSuccess(s_tool, packageArguments);
        (await InvokeAsync(installArguments, token)).EnsureSuccess(s_tool, installArguments);
        PublishedExtension second = PublishedExtension.Read(publication);
        Assert.AreEqual("Ankus.Versioned.Probe-0.2.0" + suffix, second.Library);
        Assert.AreSequenceEqual<string>([Up, Down], second.UpgradeScripts);
        string upgraded = await File.ReadAllTextAsync(Path.Join(publication, "extension", Up), token);
        Assert.Contains("AS 'Ankus.Versioned.Probe-0.2.0', ", upgraded);
        Assert.Contains("AS 'Ankus.Versioned.Probe-0.1.0', ", await File.ReadAllTextAsync(Path.Join(publication, "extension", Down), token));
        Assert.DoesNotContain("MODULE_PATHNAME", upgraded);
        foreach (string libraryDirectory in new[] { PackageLibraryDirectory(package), StagedPath(stage, s_installation.LibraryDirectory) })
        {
            Assert.IsTrue(File.Exists(Path.Join(libraryDirectory, first.Library)), libraryDirectory);
            Assert.IsTrue(File.Exists(Path.Join(libraryDirectory, second.Library)), libraryDirectory);
        }

        foreach (string extensionDirectory in new[] { Path.Join(PackageSharedDirectory(package), "extension"),
            Path.Join(StagedPath(stage, s_installation.SharedDirectory), "extension") })
        {
            Assert.IsTrue(File.Exists(Path.Join(extensionDirectory, first.Sql)), extensionDirectory);
            Assert.IsTrue(File.Exists(Path.Join(extensionDirectory, second.Sql)), extensionDirectory);
            IReadOnlyDictionary<string, string> control = ExtensionControlFile.Read(Path.Join(extensionDirectory, second.Control));
            Assert.AreEqual("0.2.0", control["default_version"]);
            Assert.IsFalse(control.ContainsKey("module_pathname"));
        }

        PostgresInstallation installation = await ReserveCaseInstallationAsync(token);
        CopyUpgradePackageToCaseInstallation(package, first);
        CopyUpgradePackageToCaseInstallation(package, second);
        List<string> configuration = ["dynamic_library_path = '" + EscapeSetting(PackageLibraryDirectory(package)) + "'"];
        if (installation.Version.Major >= 18)
        {
            configuration.Add("extension_control_path = '" + EscapeSetting(PackageSharedDirectory(package)) + "'");
        }

        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
        {
            Installation = installation,
            DataDirectoryBase = Path.Join(s_root, "versioned-pgdata"),
            LogDirectory = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs"),
            StartupTimeout = IntegrationEnvironment.StartupTimeout,
            PostgreSqlConfiguration = configuration,
        }, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_versioned_probe VERSION '0.1.0'");
        Assert.AreEqual("1|Ankus.Versioned.Probe-0.1.0|0.1.0", await VersionedStateAsync(connection));

        // The default version installs beside the older one in another database of the same server.
        await ExecuteSqlPackageAsync(connection, "CREATE DATABASE versioned_current");
        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString) { Database = "versioned_current", Pooling = false };
        await using (var current = new NpgsqlConnection(builder.ConnectionString))
        {
            await current.OpenAsync(token);
            await ExecuteSqlPackageAsync(current, "CREATE EXTENSION ankus_versioned_probe");
            Assert.AreEqual("2|Ankus.Versioned.Probe-0.2.0|0.2.0", await VersionedStateAsync(current));
            Assert.AreEqual("1|Ankus.Versioned.Probe-0.1.0|0.1.0", await VersionedStateAsync(connection));
        }

        Assert.AreEqual("0.1.0>0.2.0,0.2.0>0.1.0", await SqlPackageScalarAsync<string>(connection,
            "SELECT string_agg(source || '>' || target, ',' ORDER BY source) FROM pg_extension_update_paths('ankus_versioned_probe') WHERE path IS NOT NULL"));
        await ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_versioned_probe UPDATE TO '0.2.0'");
        Assert.AreEqual("2|Ankus.Versioned.Probe-0.2.0|0.2.0", await VersionedStateAsync(connection));
        await ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_versioned_probe UPDATE TO '0.1.0'");
        Assert.AreEqual("1|Ankus.Versioned.Probe-0.1.0|0.1.0", await VersionedStateAsync(connection));
        await ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_versioned_probe UPDATE");
        Assert.AreEqual("2|Ankus.Versioned.Probe-0.2.0|0.2.0", await VersionedStateAsync(connection));
        if (installation.Version.Major >= 18)
        {
            // Both versions' libraries are loaded in this one backend.
            Assert.AreEqual(first.Library + "|" + second.Library, await SqlPackageScalarAsync<string>(connection,
                "SELECT string_agg(file_name, '|' ORDER BY file_name) FROM pg_get_loaded_modules() WHERE file_name LIKE 'Ankus.Versioned.Probe-%'"));
        }

        Assert.AreEqual(backend, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
        await ExecuteSqlPackageAsync(connection, "DROP DATABASE versioned_current WITH (FORCE)");

        static string Replaceable(string installation, string version)
            => installation.Replace("CREATE FUNCTION", "CREATE OR REPLACE FUNCTION", StringComparison.Ordinal)
                .Replace("'Ankus.Versioned.Probe-" + version + "'", "'MODULE_PATHNAME'", StringComparison.Ordinal);
    }

    private Task<string> VersionedStateAsync(NpgsqlConnection connection)
        => SqlPackageScalarAsync<string>(connection, """
            SELECT versioned_value() || '|' || p.probin || '|' || e.extversion
            FROM pg_proc p, pg_extension e
            WHERE p.oid = 'versioned_value()'::regprocedure AND e.extname = 'ankus_versioned_probe'
            """);

    private static string VersionedSource(int value) => $$"""
        using Ankus;
        public static class VersionedFunctions
        {
            [PgFunction(Name = "versioned_value")]
            public static int Value() => {{value}};
        }
        """;
}
