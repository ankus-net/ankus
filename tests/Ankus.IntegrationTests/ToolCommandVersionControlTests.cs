using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Per-version dependencies and privileges govern installation and transactional updates, with same-session recovery.
    /// </summary>
    [TestMethod]
    public async Task VersionControlsPreservePostgresInstallAndUpdateContracts()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = await CreateControlProjectAsync(directory, token, "ankus_version_control_probe");
        await File.WriteAllTextAsync(Path.Join(directory, "author settings.control"),
            "requires=missing_primary_dependency\ntrusted=false\ncomment=primary", token);
        string scripts = Path.Join(directory, "sql");
        Directory.CreateDirectory(scripts);
        await File.WriteAllTextAsync(Path.Join(scripts, "ankus_version_control_probe--0.1.0.control"),
            "requires=''\ntrusted=true\nschema='Version Schema'\ncomment='base comment'", token);
        await File.WriteAllTextAsync(Path.Join(scripts, "ankus_version_control_probe--release.control"),
            "requires=version_dependency\ntrusted=false\nrelocatable=true", token);
        await File.WriteAllTextAsync(Path.Join(scripts, "ankus_version_control_probe--final.control"),
            "requires=''\ntrusted=true\nrelocatable=false", token);
        const string ReleaseUpgrade = "ankus_version_control_probe--0.1.0--release.sql";
        await File.WriteAllTextAsync(Path.Join(scripts, ReleaseUpgrade),
            "ALTER TABLE version_rows ADD COLUMN marker text; UPDATE version_rows SET marker='release'; SELECT 1/0;\n", token);
        await File.WriteAllTextAsync(Path.Join(scripts, "ankus_version_control_probe--release--final.sql"),
            "UPDATE version_rows SET marker='final';", token);
        string output = CreateDirectory();
        PublishedExtension manifest = await PublishControlProbeAsync(project, output, token);
        Assert.AreSequenceEqual<string>(["ankus_version_control_probe--0.1.0.control", "ankus_version_control_probe--final.control",
            "ankus_version_control_probe--release.control"], manifest.VersionControlFiles);
        Assert.IsFalse(ExtensionSchema.Read(Path.Join(output, manifest.Library)).Relocatable);
        Assert.AreEqual("missing_primary_dependency", ExtensionControlFile.Read(Path.Join(output, "extension", manifest.Control))["requires"]);
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token);
        string extensionDirectory = _caseInstallation is null
            ? Path.Join(output, "extension") : Path.Join(_caseInstallation.Installation.SharedDirectory, "extension");
        await File.WriteAllTextAsync(Path.Join(extensionDirectory, "version_dependency.control"),
            "default_version='1'\nrelocatable=true\n", token);
        await File.WriteAllTextAsync(Path.Join(extensionDirectory, "version_dependency--1.sql"),
            "CREATE TABLE dependency_marker (value integer);", token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        int backend = connection.ProcessID;
        await ExecuteSqlPackageAsync(connection, """
            CREATE ROLE version_author;
            GRANT CREATE ON DATABASE ankus_tests TO version_author;
            SET ROLE version_author;
            CREATE EXTENSION ankus_version_control_probe;
            """);
        Assert.AreEqual("0.1.0|false|Version Schema|version_author", await SqlPackageScalarAsync<string>(connection,
            "SELECT extversion || '|' || extrelocatable::text || '|' || n.nspname || '|' || extowner::regrole::text FROM pg_extension e JOIN pg_namespace n ON n.oid=e.extnamespace WHERE extname='ankus_version_control_probe'"));
        Assert.AreEqual("base comment", await SqlPackageScalarAsync<string>(connection,
            "SELECT obj_description(oid, 'pg_extension') FROM pg_extension WHERE extname='ankus_version_control_probe'"));
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, "SELECT \"Version Schema\".control_value()"));
        await ExecuteSqlPackageAsync(connection, """
            RESET ROLE;
            CREATE TABLE "Version Schema".version_rows (id integer PRIMARY KEY, payload text);
            ALTER EXTENSION ankus_version_control_probe ADD TABLE "Version Schema".version_rows;
            INSERT INTO "Version Schema".version_rows VALUES (7, 'café 🐘');
            """);
        PostgresException missing = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_version_control_probe UPDATE TO 'release'"));
        Assert.AreEqual("42704", missing.SqlState, missing.MessageText);
        Assert.Contains("version_dependency", missing.MessageText);
        await ExecuteSqlPackageAsync(connection, "CREATE EXTENSION version_dependency; SET ROLE version_author");
        PostgresException denied = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_version_control_probe UPDATE TO 'release'"));
        Assert.AreEqual("42501", denied.SqlState, denied.MessageText);
        await ExecuteSqlPackageAsync(connection, "RESET ROLE");
        PostgresException failed = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_version_control_probe UPDATE TO 'release'"));
        Assert.AreEqual("22012", failed.SqlState, failed.MessageText);
        Assert.AreEqual("0.1.0|false", await SqlPackageScalarAsync<string>(connection,
            "SELECT extversion || '|' || extrelocatable::text FROM pg_extension WHERE extname='ankus_version_control_probe'"));
        Assert.AreEqual("7|café 🐘", await SqlPackageScalarAsync<string>(connection,
            "SELECT id::text || '|' || payload FROM \"Version Schema\".version_rows"));
        Assert.IsFalse(await SqlPackageScalarAsync<bool>(connection,
            "SELECT EXISTS(SELECT FROM pg_attribute WHERE attrelid='\"Version Schema\".version_rows'::regclass AND attname='marker' AND NOT attisdropped)"));
        Assert.AreEqual(0L, await VersionDependencyCountAsync(connection));
        string upgrade = Path.Join(extensionDirectory, ReleaseUpgrade);
        string repaired = (await File.ReadAllTextAsync(upgrade, token)).Replace("SELECT 1/0;", "", StringComparison.Ordinal);
        await File.WriteAllTextAsync(upgrade, repaired, token);
        await ExecuteSqlPackageAsync(connection, "ALTER EXTENSION ankus_version_control_probe UPDATE TO 'release'");
        Assert.AreEqual("release|true", await SqlPackageScalarAsync<string>(connection,
            "SELECT extversion || '|' || extrelocatable::text FROM pg_extension WHERE extname='ankus_version_control_probe'"));
        Assert.AreEqual("7|café 🐘|release", await SqlPackageScalarAsync<string>(connection,
            "SELECT id::text || '|' || payload || '|' || marker FROM \"Version Schema\".version_rows"));
        Assert.AreEqual(1L, await VersionDependencyCountAsync(connection));
        PostgresException dependency = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteSqlPackageAsync(connection, "DROP EXTENSION version_dependency"));
        Assert.AreEqual("2BP01", dependency.SqlState, dependency.MessageText);
        await ExecuteSqlPackageAsync(connection, "SET ROLE version_author; ALTER EXTENSION ankus_version_control_probe UPDATE TO 'final'; RESET ROLE");
        Assert.AreEqual("final|false", await SqlPackageScalarAsync<string>(connection,
            "SELECT extversion || '|' || extrelocatable::text FROM pg_extension WHERE extname='ankus_version_control_probe'"));
        Assert.AreEqual(0L, await VersionDependencyCountAsync(connection));
        await ExecuteSqlPackageAsync(connection, "DROP EXTENSION version_dependency");
        Assert.AreEqual("7|café 🐘|final", await SqlPackageScalarAsync<string>(connection,
            "SELECT id::text || '|' || payload || '|' || marker FROM \"Version Schema\".version_rows"));
        Assert.AreEqual(42, await SqlPackageScalarAsync<int>(connection, "SELECT \"Version Schema\".control_value()"));
        Assert.AreEqual(backend, await SqlPackageScalarAsync<int>(connection, "SELECT pg_backend_pid()"));
    }

    /// <summary>
    /// Secondary-only edits reach native metadata; invalid settings invalidate publication, and removal cleans owned controls.
    /// </summary>
    [TestMethod]
    public async Task VersionControlChangesUpdateNativeMetadataAndCleanRemovedFiles()
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string project = await CreateControlProjectAsync(directory, token, "ankus_version_control_probe");
        await File.WriteAllTextAsync(Path.Join(directory, "author settings.control"), "relocatable=false", token);
        string scripts = Path.Join(directory, "sql");
        Directory.CreateDirectory(scripts);
        const string Name = "ankus_version_control_probe--0.1.0.control";
        string authored = Path.Join(scripts, Name);
        await File.WriteAllTextAsync(authored, "relocatable=true", token);
        string output = CreateDirectory();
        PublishedExtension manifest = await PublishControlProbeAsync(project, output, token);
        Assert.IsTrue(ExtensionSchema.Read(Path.Join(output, manifest.Library)).Relocatable);
        Assert.AreEqual("false", ExtensionControlFile.Read(Path.Join(output, "extension", manifest.Control))["relocatable"]);
        await File.WriteAllTextAsync(authored, "schema='version schema'", token);
        await PublishControlProbeAsync(project, output, token);
        Assert.IsFalse(ExtensionSchema.Read(Path.Join(output, manifest.Library)).Relocatable);
        Assert.AreEqual("false", ExtensionControlFile.Read(Path.Join(output, "extension", Name))["relocatable"]);
        await File.WriteAllTextAsync(authored, "default_version='0.1.0'", token);
        ProcessResult failed = await InvokeAsync(["publish", "--home", s_home, "--pg", MajorText(), "--project", project, "--output", output], token);
        Assert.AreNotEqual(0, failed.ExitCode);
        Assert.Contains("secondary extension control file", failed.StandardOutput + failed.StandardError);
        Assert.IsFalse(File.Exists(Path.Join(output, PublishedExtension.FileName)));
        File.Delete(authored);
        string unlisted = Path.Join(output, "extension", "unlisted.control");
        await File.WriteAllTextAsync(unlisted, "preserved", token);
        PublishedExtension repaired = await PublishControlProbeAsync(project, output, token);
        Assert.IsEmpty(repaired.VersionControlFiles);
        Assert.IsFalse(File.Exists(Path.Join(output, "extension", Name)));
        Assert.AreEqual("preserved", await File.ReadAllTextAsync(unlisted, token));
        Assert.IsFalse(ExtensionSchema.Read(Path.Join(output, repaired.Library)).Relocatable);
    }

    private Task<long> VersionDependencyCountAsync(NpgsqlConnection connection)
        => SqlPackageScalarAsync<long>(connection,
            "SELECT count(*) FROM pg_depend WHERE classid='pg_extension'::regclass AND objid=(SELECT oid FROM pg_extension WHERE extname='ankus_version_control_probe') AND refclassid='pg_extension'::regclass AND refobjid=(SELECT oid FROM pg_extension WHERE extname='version_dependency')");
}
