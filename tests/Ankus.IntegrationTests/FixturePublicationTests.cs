using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies that fixtures in one test run share an extension's publication while each owns its cluster and installed copy.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
public sealed class FixturePublicationTests(TestContext context)
{
    /// <summary>
    /// Fixtures that select the same project, configuration, installation and build properties publish once, as a test
    /// that starts several nodes needs. Other build properties publish again, and so does a fixture whose cached
    /// publication another process replaced.
    /// </summary>
    [TestMethod]
    public async Task FixturesWithTheSameSelectionPublishOnce()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(token);
        string root = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "publication-" + Guid.NewGuid().ToString("N")[..8]);
        await using PostgresTestInstallation staged = await PostgresTestInstallation.StageAsync(installation, root, token);
        string extensions = Path.Join(staged.Installation.SharedDirectory, "extension");
        await File.WriteAllTextAsync(Path.Join(extensions, "ankus_cascade_dependency.control"),
            "default_version = '1.0'\nrelocatable = true\n", token);
        await File.WriteAllTextAsync(Path.Join(extensions, "ankus_cascade_dependency--1.0.sql"),
            "CREATE FUNCTION ankus_cascade_dependency() RETURNS integer LANGUAGE sql AS 'SELECT 41';\n", token);
        // Another test publishes the cascade extension in parallel, so this one counts the build logs of its own copy. The
        // copy sits at the same depth, where the project's relative SDK import still resolves.
        string projectDirectory = root + "-extension";
        Directory.CreateDirectory(projectDirectory);
        foreach (string file in Directory.GetFiles(Path.Join(IntegrationEnvironment.RepositoryRoot, "tests", "Ankus.CascadeExtension")))
        {
            File.Copy(file, Path.Join(projectDirectory, Path.GetFileName(file)));
        }

        try
        {
            await StartFixturesAsync(Path.Join(projectDirectory, "Ankus.CascadeExtension.csproj"), staged.Installation, token);
        }
        finally
        {
            Directory.Delete(projectDirectory, recursive: true);
        }
    }

    private static async Task StartFixturesAsync(string project, PostgresInstallation installation, CancellationToken token)
    {
        string projectDirectory = Path.GetDirectoryName(project)!;
        int Publications() => Directory.GetFiles(Path.Join(projectDirectory, "bin", "ankus-test-logs"), "*.binlog").Length;
        string data = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-pgdata");
        var options = new PostgresExtensionTestOptions { ProjectPath = project, Installation = installation, DataDirectoryBase = data };

        await using PostgresExtensionTest first = await PostgresExtensionTest.StartAsync(options, token);
        await using PostgresExtensionTest second = await PostgresExtensionTest.StartAsync(options, token);
        Assert.AreEqual(1, Publications());
        Assert.AreNotEqual(await LibraryPathAsync(first, token), await LibraryPathAsync(second, token));
        await first.DisposeAsync();
        Assert.AreEqual(42, await CascadeValueAsync(second, token));

        await using PostgresExtensionTest other = await PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions
        {
            ProjectPath = project,
            Installation = installation,
            DataDirectoryBase = data,
            BuildProperties = new Dictionary<string, string> { ["AnkusFixturePublicationProbe"] = "other" },
        }, token);
        Assert.AreEqual(2, Publications());
        Assert.AreEqual(42, await CascadeValueAsync(other, token));

        foreach (string stamp in Directory.GetFiles(Path.Join(projectDirectory, "obj", "ankus-test-build", "publish-cache"), "*.stamp"))
        {
            await File.WriteAllTextAsync(stamp, "another process", token);
        }

        await using PostgresExtensionTest replaced = await PostgresExtensionTest.StartAsync(options, token);
        Assert.AreEqual(3, Publications());
        Assert.AreEqual(42, await CascadeValueAsync(replaced, token));
        Assert.AreEqual(42, await CascadeValueAsync(second, token));
    }

    private static async Task<string> LibraryPathAsync(PostgresExtensionTest fixture, CancellationToken token)
    {
        await using NpgsqlConnection connection = await fixture.Cluster.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("SHOW dynamic_library_path", connection);
        return (string)(await command.ExecuteScalarAsync(token))!;
    }

    private static async Task<int> CascadeValueAsync(PostgresExtensionTest fixture, CancellationToken token)
    {
        await using NpgsqlConnection connection = await fixture.Cluster.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("SELECT cascade_value()", connection);
        return (int)(await command.ExecuteScalarAsync(token))!;
    }
}
