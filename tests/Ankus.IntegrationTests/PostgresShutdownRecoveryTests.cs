using Ankus.Testing;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies a canceled native shutdown preserves recoverable data and allows subsequent owned cleanup.
/// </summary>
/// <param name="context">The current native test context.</param>
[TestClass]
public sealed class PostgresShutdownRecoveryTests(TestContext context)
{
    /// <summary>
    /// A timed-out shutdown retries after the owned postmaster resumes and completes its requested stop.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task CanceledShutdownCanReclaimStoppedClusterOnRetry()
    {
        PostgresTestClusterOptions template = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        var options = new PostgresTestClusterOptions
        {
            Installation = template.Installation,
            DataDirectoryBase = template.DataDirectoryBase,
            LogDirectory = template.LogDirectory,
            StartupTimeout = template.StartupTimeout,
            PostgreSqlConfiguration = template.PostgreSqlConfiguration,
            ShutdownTimeout = TimeSpan.FromMilliseconds(500),
        };
        PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        string pid = (await File.ReadAllLinesAsync(Path.Join(cluster.DataDirectory, "postmaster.pid"), context.CancellationToken))[0];
        try
        {
            (await ProcessRunner.RunAsync("kill", ["-STOP", pid], new Dictionary<string, string?>(), context.CancellationToken))
                .EnsureSuccess("kill", ["-STOP", pid]);
            Task shutdown = cluster.DisposeAsync().AsTask();
            await Assert.ThrowsAsync<OperationCanceledException>(() => shutdown);
            Assert.IsTrue(shutdown.IsCanceled);
            Assert.IsTrue(Directory.Exists(cluster.DataDirectory));
            Assert.IsNotNull(cluster.SocketDirectory);
            Assert.IsTrue(Directory.Exists(cluster.SocketDirectory));
            (await ProcessRunner.RunAsync("kill", ["-CONT", pid], new Dictionary<string, string?>(), context.CancellationToken))
                .EnsureSuccess("kill", ["-CONT", pid]);
            await StopOwnedServerAsync(cluster);
            await cluster.DisposeAsync();
            await cluster.DisposeAsync();
            Assert.IsFalse(Directory.Exists(cluster.DataDirectory));
            Assert.IsFalse(Directory.Exists(cluster.SocketDirectory));
            Assert.IsTrue(File.Exists(cluster.LogFilePath));
            Assert.Contains("database system is shut down", cluster.ReadServerLog());
        }
        finally
        {
            if (Directory.Exists(cluster.DataDirectory))
            {
                _ = await ProcessRunner.RunAsync("kill", ["-CONT", pid], new Dictionary<string, string?>(), CancellationToken.None);
                await StopOwnedServerAsync(cluster);
                Directory.Delete(cluster.DataDirectory, recursive: true);
            }

            if (cluster.SocketDirectory is { } socket && Directory.Exists(socket))
            {
                Directory.Delete(socket, recursive: true);
            }
        }
    }

    /// <summary>
    /// Joins the owned postmaster's requested termination before checking stopped-directory recovery.
    /// </summary>
    /// <param name="cluster">The single owned server whose timeout is under test.</param>
    private static async Task StopOwnedServerAsync(PostgresTestCluster cluster)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        _ = await ProcessRunner.RunAsync(cluster.Installation.PgCtlPath,
            ["stop", "-D", cluster.DataDirectory, "-m", "immediate", "-w", "-t", "5"], new Dictionary<string, string?>(), timeout.Token);
        ProcessResult status = await ProcessRunner.RunAsync(cluster.Installation.PgCtlPath,
            ["status", "-D", cluster.DataDirectory], new Dictionary<string, string?>(), timeout.Token);
        Assert.AreEqual(3, status.ExitCode, "Only stopped, owned PostgreSQL directories may be removed.");
    }
}
