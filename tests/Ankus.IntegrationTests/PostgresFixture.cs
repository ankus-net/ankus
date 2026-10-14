using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Publishes the extension and owns one local PostgreSQL cluster for the assembly's parallel test sessions.
/// </summary>
internal static class PostgresFixture
{
    private static PostgresTestCluster? s_cluster;

    /// <summary>
    /// Gets the initialized assembly-scoped test cluster.
    /// </summary>
    internal static PostgresTestCluster Cluster => s_cluster ?? throw new InvalidOperationException("Cluster is not initialized.");

    /// <summary>
    /// Builds the extension, starts PostgreSQL, and installs the published package with CREATE EXTENSION.
    /// </summary>
    /// <param name="context">The assembly test context.</param>
    internal static async Task InitializeAsync(TestContext context)
    {
        await IntegrationEnvironment.PublishSampleAsync(context.CancellationToken);
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        await AllocatorFixtureCompiler.BuildAsync(options.Installation, context.CancellationToken);
        await AllocatorFaultFixtureCompiler.CompileAsync(options.Installation, context.CancellationToken);
        await NativeRawCallFixtureCompiler.CompileAsync(options.Installation, context.CancellationToken);
        await NativeValueOwnershipFixtureCompiler.CompileAsync(options.Installation, context.CancellationToken);
        s_cluster = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        try
        {
            await using NpgsqlConnection connection = await Cluster.OpenConnectionAsync(context.CancellationToken);
            string sql = """
                CREATE EXTENSION ankus_hello;
                CREATE SCHEMA datatype;
                CREATE EXTENSION ankus_test WITH SCHEMA datatype;
                CREATE SCHEMA customscan;
                CREATE EXTENSION ankus_trace_scan WITH SCHEMA customscan;
                CREATE SCHEMA tests;
                CREATE TABLE tests.rollback_probe (value integer NOT NULL);
                CREATE FUNCTION tests.insert_probe() RETURNS void LANGUAGE sql AS
                    'INSERT INTO tests.rollback_probe VALUES (42)';
                CREATE FUNCTION tests.fail_probe() RETURNS integer LANGUAGE sql AS 'SELECT 1 / 0';
                """;
            await using var command = new NpgsqlCommand(sql + AllocatorFixtureCompiler.InstallationSql + AllocatorFaultFixtureCompiler.InstallationSql +
                NativeRawCallFixtureCompiler.InstallationSql + NativeValueOwnershipFixtureCompiler.InstallationSql, connection);
            await command.ExecuteNonQueryAsync(context.CancellationToken);
        }
        catch
        {
            if (s_cluster is not null)
            {
                await s_cluster.DisposeAsync();
                s_cluster = null;
            }

            await IntegrationEnvironment.CleanupAsync();
            throw;
        }
    }

    /// <summary>
    /// Stops PostgreSQL even if tests failed or their cancellation token was canceled, then writes the timing summary.
    /// </summary>
    internal static async Task CleanupAsync()
    {
        try
        {
            if (s_cluster is not null)
            {
                await s_cluster.DisposeAsync();
                s_cluster = null;
            }
        }
        finally
        {
            try
            {
                await IntegrationEnvironment.CleanupAsync();
            }
            finally
            {
                ProcessTimings.Write(Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-results", "process-timings.log"));
            }
        }
    }
}
