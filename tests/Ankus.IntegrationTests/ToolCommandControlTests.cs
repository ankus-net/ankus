using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Published author settings enforce dependency, schema and privilege contracts in PostgreSQL and retain native behavior.
    /// </summary>
    [TestMethod]
    public async Task AuthoredControlSettingsPreservePostgresContracts()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = await CreateControlProjectAsync(directory, token);
        string authored = Path.Join(directory, "author settings.control");
        string output = CreateDirectory();
        string settings = """
            comment = 'it''s # exact\nsecond line'
            schema = 'Control Schema'
            requires = '"Control Dependency"'
            superuser = true
            trusted = false
            """;
        if (s_installation.Version.Major >= 16)
        {
            settings += "\nno_relocate = '\"Control Dependency\"'\n";
        }

        await File.WriteAllTextAsync(authored, settings, token);
        PublishedExtension manifest = await PublishControlProbeAsync(project, output, token);
        Assert.IsFalse(ExtensionSchema.Read(Path.Join(output, manifest.Library)).Relocatable);
        string controlPath = Path.Join(output, "extension", manifest.Control);
        string package = CreateDirectory();
        ProcessResult packaged = await InvokeAsync(["package", "--home", s_home, "--pg", MajorText(), "--from", output, "--output", package], token);
        Assert.AreEqual(0, packaged.ExitCode, packaged.StandardOutput + packaged.StandardError);
        Assert.AreSequenceEqual(await File.ReadAllBytesAsync(controlPath, token),
            await File.ReadAllBytesAsync(Path.Join(PackageSharedDirectory(package), "extension", manifest.Control), token));
        string staged = CreateDirectory();
        ProcessResult installed = await InvokeAsync(["install", "--home", s_home, "--pg", MajorText(), "--from", output, "--destdir", staged], token);
        Assert.AreEqual(0, installed.ExitCode, installed.StandardOutput + installed.StandardError);
        Assert.AreSequenceEqual(await File.ReadAllBytesAsync(controlPath, token),
            await File.ReadAllBytesAsync(Path.Join(StagedPath(staged, s_installation.SharedDirectory), "extension", manifest.Control), token));

        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token);
        string extensionDirectory = _caseInstallation is null
            ? Path.Join(output, "extension") : Path.Join(_caseInstallation.Installation.SharedDirectory, "extension");
        await File.WriteAllTextAsync(Path.Join(extensionDirectory, "Control Dependency.control"),
            "default_version='1'\nrelocatable=true\n", token);
        await File.WriteAllTextAsync(Path.Join(extensionDirectory, "Control Dependency--1.sql"),
            "CREATE TABLE control_dependency_marker (value integer);", token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        PostgresException missing = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_control_probe"));
        Assert.AreEqual("42704", missing.SqlState, missing.MessageText);
        Assert.Contains("Control Dependency", missing.MessageText);
        Assert.AreEqual(0L, await SqlPackageScalarAsync<long>(connection,
            "SELECT count(*) FROM pg_extension WHERE extname='ankus_control_probe'"));
        await ExecuteSqlPackageAsync(connection, """
            CREATE EXTENSION "Control Dependency";
            CREATE ROLE control_author;
            GRANT CREATE ON DATABASE ankus_tests TO control_author;
            SET ROLE control_author;
            """);
        PostgresException untrusted = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_control_probe"));
        Assert.AreEqual("42501", untrusted.SqlState, untrusted.MessageText);
        await ExecuteSqlPackageAsync(connection, "RESET ROLE");

        // trusted has no effect when superuser=false: ordinary C-function privileges still apply.
        await File.WriteAllTextAsync(authored, settings.Replace("trusted = false", "trusted = true", StringComparison.Ordinal)
            .Replace("superuser = true", "superuser = false", StringComparison.Ordinal), token);
        await PublishControlProbeAsync(project, output, token);
        CopyControlToCaseInstallation(output, manifest);
        await ExecuteSqlPackageAsync(connection, "SET ROLE control_author");
        PostgresException language = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "CREATE EXTENSION ankus_control_probe"));
        Assert.AreEqual("42501", language.SqlState, language.MessageText);
        Assert.Contains("language c", language.MessageText);
        await ExecuteSqlPackageAsync(connection, "RESET ROLE");

        await File.WriteAllTextAsync(authored, settings.Replace("trusted = false", "trusted = true", StringComparison.Ordinal), token);
        await PublishControlProbeAsync(project, output, token);
        CopyControlToCaseInstallation(output, manifest);
        await ExecuteSqlPackageAsync(connection, "SET ROLE control_author; CREATE EXTENSION ankus_control_probe");
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, "SELECT \"Control Schema\".control_value()"));
        Assert.AreEqual("Control Schema|control_author|false", await SqlPackageScalarAsync<string>(connection,
            "SELECT n.nspname || '|' || e.extowner::regrole::text || '|' || e.extrelocatable::text FROM pg_extension e JOIN pg_namespace n ON n.oid=e.extnamespace WHERE e.extname='ankus_control_probe'"));
        Assert.AreEqual("it's # exact\nsecond line", await SqlPackageScalarAsync<string>(connection,
            "SELECT obj_description(oid, 'pg_extension') FROM pg_extension WHERE extname='ankus_control_probe'"));
        // PostgreSQL runs a trusted extension's script as the bootstrap superuser, which then owns its objects.
        Assert.AreEqual("postgres", await SqlPackageScalarAsync<string>(connection,
            "SELECT proowner::regrole::text FROM pg_proc WHERE oid='\"Control Schema\".control_value()'::regprocedure"));
        await ExecuteSqlPackageAsync(connection, "CREATE SCHEMA relocation_target");
        PostgresException relocation = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_control_probe SET SCHEMA relocation_target"));
        Assert.AreEqual("0A000", relocation.SqlState, relocation.MessageText);
        await ExecuteSqlPackageAsync(connection, "RESET ROLE; CREATE SCHEMA relocated_dependency");
        if (s_installation.Version.Major >= 16)
        {
            PostgresException dependencyMove = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ExecuteSqlPackageAsync(connection, "ALTER EXTENSION \"Control Dependency\" SET SCHEMA relocated_dependency"));
            Assert.AreEqual("0A000", dependencyMove.SqlState, dependencyMove.MessageText);
            Assert.AreEqual("public", await SqlPackageScalarAsync<string>(connection,
                "SELECT extnamespace::regnamespace::text FROM pg_extension WHERE extname='Control Dependency'"));
        }

        PostgresException dependencyDrop = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "DROP EXTENSION \"Control Dependency\""));
        Assert.AreEqual("2BP01", dependencyDrop.SqlState, dependencyDrop.MessageText);
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, "SELECT \"Control Schema\".control_value()"));
        Assert.AreEqual(backend, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Control-only edits update published metadata; conflicting metadata invalidates publication and preserves the prior package.
    /// </summary>
    [TestMethod]
    public async Task AuthoredControlChangesInvalidateFailedPublications()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = await CreateControlProjectAsync(directory, token);
        string authored = Path.Join(directory, "author settings.control");
        string output = Path.Join(directory, "bin", "ankus", s_postgresKey,
            System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, "Release");
        await File.WriteAllTextAsync(authored, "comment='first'", token);
        PublishedExtension manifest = await PublishControlProbeAsync(project, output, token);
        Assert.IsTrue(ExtensionSchema.Read(Path.Join(output, manifest.Library)).Relocatable);
        await File.WriteAllTextAsync(authored, "comment='changed'\nrelocatable=false", token);
        await PublishControlProbeAsync(project, output, token);
        Assert.AreEqual("changed", ExtensionControlFile.Read(Path.Join(output, "extension", manifest.Control))["comment"]);
        Assert.IsFalse(ExtensionSchema.Read(Path.Join(output, manifest.Library)).Relocatable);
        string package = CreateDirectory();
        string marker = Path.Join(package, "retain.txt");
        await File.WriteAllTextAsync(marker, "previous package", token);
        await File.WriteAllTextAsync(authored, "default_version='conflicting'", token);
        ProcessResult failed = await InvokeAsync(["package", "--home", s_home, "--pg", MajorText(), "--project", project,
            "--output", package], token);
        Assert.AreNotEqual(0, failed.ExitCode);
        Assert.Contains("default_version", failed.StandardOutput + failed.StandardError);
        Assert.IsFalse(File.Exists(Path.Join(output, PublishedExtension.FileName)));
        Assert.AreEqual(marker, Assert.ContainsSingle(Directory.GetFiles(package, "*", SearchOption.AllDirectories)));
        Assert.AreEqual("previous package", await File.ReadAllTextAsync(marker, token));
        await File.WriteAllTextAsync(authored, "comment='recovered'", token);
        await PublishControlProbeAsync(project, output, token);
        Assert.AreEqual("recovered", ExtensionControlFile.Read(Path.Join(output, "extension", manifest.Control))["comment"]);
        Assert.IsTrue(ExtensionSchema.Read(Path.Join(output, manifest.Library)).Relocatable);
    }

    private static async Task<string> CreateControlProjectAsync(string directory, CancellationToken token,
        string extensionName = "ankus_control_probe")
    {
        string project = Path.Join(directory, "ControlProbe.csproj");
        XDocument definition = XDocument.Load(s_project);
        definition.Root!.Add(new XElement("PropertyGroup",
            new XElement("AnkusExtensionName", extensionName),
            new XElement("AnkusExtensionControlFile", "author settings.control")));
        definition.Save(project);
        await File.WriteAllTextAsync(Path.Join(directory, "Functions.cs"), """
            using Ankus;
            public static class Functions
            {
                [PgFunction]
                public static int ControlValue() => 42;
            }
            """, token);
        return project;
    }

    private static async Task<PublishedExtension> PublishControlProbeAsync(string project, string output, CancellationToken token)
    {
        ProcessResult result = await InvokeAsync(["publish", "--home", s_home, "--pg", MajorText(), "--project", project, "--output", output], token);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        return PublishedExtension.Read(output);
    }

    private void CopyControlToCaseInstallation(string output, PublishedExtension manifest)
    {
        if (_caseInstallation is not null)
        {
            File.Copy(Path.Join(output, "extension", manifest.Control),
                Path.Join(_caseInstallation.Installation.SharedDirectory, "extension", manifest.Control), overwrite: true);
        }
    }
}
