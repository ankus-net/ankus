using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies the defaults a test cluster starts with.
/// </summary>
/// <param name="context">The current test context.</param>
[TestClass]
public sealed class PostgresTestClusterDefaultsTests(TestContext context)
{
    /// <summary>
    /// A default test cluster's superuser is <c>postgres</c>, as in clusters from <c>ankus start</c>, so one connection
    /// string works against either. Test clusters used to create <c>ankus</c>.
    /// </summary>
    [TestMethod]
    public async Task DefaultSuperuserIsPostgres()
    {
        CancellationToken token = context.CancellationToken;
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(token);
        Assert.AreEqual("postgres", options.UserName);
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await using var command = new NpgsqlCommand(
            "SELECT current_user || '|' || rolsuper::text FROM pg_roles WHERE rolname = current_user", connection);
        Assert.AreEqual("postgres|true", await command.ExecuteScalarAsync(token));
    }
}
