using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies native Windows event diagnostics remain isolated, readable and retained by each test cluster.
/// </summary>
/// <param name="context">The current test context.</param>
[TestClass]
[OSCondition(OperatingSystems.Windows)]
public sealed class PostgresEventLogTests(TestContext context)
{
    /// <summary>
    /// Event-only routing preserves structured Unicode messages without mixing clusters or duplicating reads.
    /// </summary>
    [TestMethod]
    public async Task EventDiagnosticsRemainIsolatedAndSurviveShutdown()
    {
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        await using PostgresTestCluster first = await PostgresTestCluster.StartAsync(options,
            static (cluster, _) => RouteToEvents(cluster), context.CancellationToken);
        await using PostgresTestCluster second = await PostgresTestCluster.StartAsync(options,
            static (cluster, _) => RouteToEvents(cluster), context.CancellationToken);
        string firstMessage = "first café 🐘 " + Guid.NewGuid().ToString("N");
        string secondMessage = "second café 🐘 " + Guid.NewGuid().ToString("N");
        await ReportAsync(first, firstMessage);
        await ReportAsync(second, secondMessage);
        string firstLog = first.ReadServerLog();
        Assert.Contains("WARNING:  " + firstMessage, firstLog);
        Assert.Contains("DETAIL:  native detail café", firstLog);
        Assert.Contains("HINT:  native hint 🐘", firstLog);
        Assert.DoesNotContain(secondMessage, firstLog);
        string secondLog = second.ReadServerLog();
        Assert.Contains("WARNING:  " + secondMessage, secondLog);
        Assert.DoesNotContain(firstMessage, secondLog);
        string[] repeated = await Task.WhenAll(Enumerable.Range(0, 4)
            .Select(_ => Task.Run(first.ReadServerLog, context.CancellationToken)));
        foreach (string log in repeated)
        {
            Assert.AreEqual(1, log.Split(firstMessage, StringSplitOptions.None).Length - 1);
            Assert.DoesNotContain(secondMessage, log);
        }

        string laterMessage = "later café 🐘 " + Guid.NewGuid().ToString("N");
        await ReportAsync(first, laterMessage);
        string updated = first.ReadServerLog();
        Assert.AreEqual(1, updated.Split(firstMessage, StringSplitOptions.None).Length - 1);
        Assert.AreEqual(1, updated.Split(laterMessage, StringSplitOptions.None).Length - 1);
        Assert.DoesNotContain(secondMessage, updated);

        await first.DisposeAsync();
        string retained = await File.ReadAllTextAsync(first.LogFilePath, context.CancellationToken);
        Assert.Contains(firstMessage, retained);
        Assert.Contains(laterMessage, retained);
        Assert.Contains("database system is shut down", retained);
        Assert.DoesNotContain(secondMessage, retained);
        Assert.IsFalse(Directory.Exists(first.DataDirectory));
        await using NpgsqlConnection connection = await second.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT 42", connection);
        Assert.AreEqual(42, await command.ExecuteScalarAsync(context.CancellationToken));
    }

    /// <summary>
    /// A failure before accepting connections retains its native reason and removes only its owned data.
    /// </summary>
    [TestMethod]
    public async Task EventDiagnosticsPreserveStartupFailureAndCleanup()
    {
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        PostgresTestCluster? failed = null;
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            PostgresTestCluster.StartAsync(options, (cluster, _) =>
            {
                failed = cluster;
                RouteToEvents(cluster);
                File.AppendAllText(Path.Join(cluster.DataDirectory, "postgresql.auto.conf"),
                    "shared_preload_libraries = 'ankus_eventlog_missing_library'\n");
            }, context.CancellationToken));
        Assert.Contains("ankus_eventlog_missing_library", error.Message);
        Assert.Contains("FATAL:", error.Message);
        Assert.IsNotNull(failed);
        Assert.IsFalse(Directory.Exists(failed.DataDirectory));
        Assert.Contains("ankus_eventlog_missing_library",
            await File.ReadAllTextAsync(failed.LogFilePath, context.CancellationToken));
    }

    /// <summary>
    /// Selects PostgreSQL's real event destination without requiring the test host to run as a service.
    /// </summary>
    private static void RouteToEvents(PostgresTestCluster cluster)
        => File.AppendAllText(Path.Join(cluster.DataDirectory, "postgresql.auto.conf"),
            "log_destination = 'eventlog'\nlog_error_verbosity = default\n");

    /// <summary>
    /// Emits one native message whose exact fields must remain in the diagnostic evidence.
    /// </summary>
    private async Task ReportAsync(PostgresTestCluster cluster, string message)
    {
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand(
            $"DO $test$ BEGIN RAISE WARNING '{message}' USING DETAIL = 'native detail café', HINT = 'native hint 🐘'; END $test$;", connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }
}
