using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies the test fixture installs an extension's required extensions, as pgrx's framework does.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
public sealed class ExtensionDependencyTests(TestContext context)
{
    /// <summary>
    /// An extension whose control file requires another extension starts in the fixture, which creates it with
    /// <c>CASCADE</c> as pgrx's <c>CREATE EXTENSION ... CASCADE</c> does.
    /// </summary>
    [TestMethod]
    public async Task FixtureInstallsRequiredExtensionsWithCascade()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(token);
        string root = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "cascade-" + Guid.NewGuid().ToString("N")[..8]);
        await using PostgresTestInstallation staged = await PostgresTestInstallation.StageAsync(installation, root, token);
        string extensions = Path.Join(staged.Installation.SharedDirectory, "extension");
        await File.WriteAllTextAsync(Path.Join(extensions, "ankus_cascade_dependency.control"),
            "default_version = '1.0'\nrelocatable = true\n", token);
        await File.WriteAllTextAsync(Path.Join(extensions, "ankus_cascade_dependency--1.0.sql"),
            "CREATE FUNCTION ankus_cascade_dependency() RETURNS integer LANGUAGE sql AS 'SELECT 41';\n", token);
        await using PostgresExtensionTest fixture = await PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions
        {
            ProjectPath = Path.Join(IntegrationEnvironment.RepositoryRoot, "tests", "Ankus.CascadeExtension", "Ankus.CascadeExtension.csproj"),
            Installation = staged.Installation,
            DataDirectoryBase = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-pgdata"),
        }, token);
        await using NpgsqlConnection connection = await fixture.Cluster.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand("""
            SELECT string_agg(extname, ',' ORDER BY extname) FILTER (WHERE extname LIKE 'ankus_cascade%'),
                   cascade_value()
              FROM pg_extension
            """, connection);
        await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(token);
        Assert.IsTrue(await reader.ReadAsync(token));
        Assert.AreEqual("ankus_cascade,ankus_cascade_dependency", reader.GetString(0));
        Assert.AreEqual(42, reader.GetInt32(1));
    }
}
