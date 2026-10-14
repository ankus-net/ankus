using Ankus.PgConfig;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies diagnostic failures cannot bypass owned cleanup after a real server stops or fails startup.
/// </summary>
/// <param name="context">The current native test context.</param>
[TestClass]
public sealed class PostgresDiagnosticCleanupTests(TestContext context)
{
    /// <summary>
    /// Denying access to an owned native log reports the read error after deleting stopped data and sockets.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task NativeLogReadFailureStillDeletesStoppedCluster()
    {
        if (OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("This case requires Unix file permissions.");
        }

        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        UnixFileMode original = File.GetUnixFileMode(cluster.LogFilePath);
        string unrelated = Path.Join(options.DataDirectoryBase, "unrelated-" + Guid.NewGuid().ToString("N"));
        try
        {
            await File.WriteAllTextAsync(unrelated, "preserved", context.CancellationToken);
            Assert.IsTrue(Directory.Exists(cluster.DataDirectory));
            Assert.IsNotNull(cluster.SocketDirectory);
            Assert.IsTrue(Directory.Exists(cluster.SocketDirectory));
            File.SetUnixFileMode(cluster.LogFilePath, UnixFileMode.None);
            Assert.ThrowsExactly<UnauthorizedAccessException>(() => cluster.ReadServerLog());
            UnauthorizedAccessException first = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => cluster.DisposeAsync().AsTask());
            UnauthorizedAccessException repeated = await Assert.ThrowsExactlyAsync<UnauthorizedAccessException>(() => cluster.DisposeAsync().AsTask());
            Assert.AreEqual(first.Message, repeated.Message);
            Assert.IsFalse(Directory.Exists(cluster.DataDirectory));
            Assert.IsFalse(Directory.Exists(cluster.SocketDirectory));
            Assert.IsTrue(File.Exists(cluster.LogFilePath));
            Assert.AreEqual("preserved", await File.ReadAllTextAsync(unrelated, context.CancellationToken));
            File.SetUnixFileMode(cluster.LogFilePath, original);
            await cluster.DisposeAsync();
            await cluster.DisposeAsync();
            Assert.Contains("database system is shut down", cluster.ReadServerLog());
        }
        finally
        {
            File.SetUnixFileMode(cluster.LogFilePath, original);
            if (Directory.Exists(cluster.DataDirectory))
            {
                await RemoveStoppedFixtureDirectoriesAsync(cluster);
            }

            File.Delete(unrelated);
        }
    }

    /// <summary>
    /// Invalid owned cursor metadata preserves its error and the retained log while reclaiming stopped data.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task CursorReadFailureStillDeletesStoppedCluster()
    {
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        string? state = null;
        string? original = null;
        try
        {
            _ = cluster.ReadServerLog();
            state = Assert.ContainsSingle(Directory.GetFiles(Path.GetDirectoryName(cluster.LogFilePath)!,
                Path.GetFileName(cluster.LogFilePath) + ".events.*.json"));
            original = await File.ReadAllTextAsync(state, context.CancellationToken);
            Assert.IsTrue(Directory.Exists(cluster.DataDirectory));
            await File.WriteAllTextAsync(state, "{\"source\":\"another-server\"}", context.CancellationToken);
            Assert.ThrowsExactly<InvalidDataException>(() => cluster.ReadServerLog());
            InvalidDataException error = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => cluster.DisposeAsync().AsTask());
            Assert.Contains("different server identity", error.Message);
            Assert.IsFalse(Directory.Exists(cluster.DataDirectory));
            Assert.IsTrue(File.Exists(state));
            Assert.IsTrue(File.Exists(cluster.LogFilePath));
            Assert.AreEqual("{\"source\":\"another-server\"}", await File.ReadAllTextAsync(state, context.CancellationToken));
            InvalidDataException repeated = await Assert.ThrowsExactlyAsync<InvalidDataException>(() => cluster.DisposeAsync().AsTask());
            Assert.AreEqual(error.Message, repeated.Message);
            await File.WriteAllTextAsync(state, original, context.CancellationToken);
            await cluster.DisposeAsync();
            await cluster.DisposeAsync();
            Assert.IsNotEmpty(cluster.ReadServerLog());
        }
        finally
        {
            if (state is not null && original is not null)
            {
                await File.WriteAllTextAsync(state, original, CancellationToken.None);
            }

            if (Directory.Exists(cluster.DataDirectory))
            {
                await RemoveStoppedFixtureDirectoriesAsync(cluster);
            }
        }
    }

    /// <summary>
    /// Simultaneous native startup and collection failures retain both causes without leaving an owned data directory.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task StartupAndLogFailuresStillDeleteOwnedData()
    {
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        PostgresTestCluster? failed = null;
        try
        {
            AggregateException error = await Assert.ThrowsExactlyAsync<AggregateException>(() =>
                PostgresTestCluster.StartAsync(options, (cluster, _) =>
                {
                    failed = cluster;
                    File.AppendAllText(Path.Join(cluster.DataDirectory, "postgresql.auto.conf"),
                        "shared_preload_libraries = 'ankus_diagnostic_cleanup_missing_library'\n");
                    Directory.CreateDirectory(cluster.LogFilePath + ".events.lock");
                }, context.CancellationToken));
            Assert.IsNotNull(failed);
            Assert.IsFalse(Directory.Exists(failed.DataDirectory));
            Exception[] causes = [.. error.Flatten().InnerExceptions];
            InvalidOperationException startup = Assert.ContainsSingle(causes.OfType<InvalidOperationException>());
            Assert.Contains("pg_ctl", startup.Message);
            Assert.IsNotEmpty(causes.OfType<UnauthorizedAccessException>());
            Assert.Contains(failed.LogFilePath, error.Message);
        }
        finally
        {
            if (failed is not null)
            {
                string occupied = failed.LogFilePath + ".events.lock";
                if (Directory.Exists(occupied))
                {
                    Directory.Delete(occupied);
                }

                if (Directory.Exists(failed.DataDirectory))
                {
                    await RemoveStoppedFixtureDirectoriesAsync(failed);
                }
            }
        }
    }

    /// <summary>
    /// Reclaims only this failed assertion's fixture after independently confirming PostgreSQL has stopped.
    /// </summary>
    /// <param name="cluster">The invocation whose original implementation may have skipped deletion.</param>
    private static async Task RemoveStoppedFixtureDirectoriesAsync(PostgresTestCluster cluster)
    {
        try
        {
            await cluster.DisposeAsync();
        }
        catch (IOException)
        {
            // Disposal retains its first failure; the deliberately invalid log has already been restored.
        }
        catch (UnauthorizedAccessException)
        {
            // A cached permission failure must not leave artifacts from the before-fix regression run.
        }

        if (!Directory.Exists(cluster.DataDirectory))
        {
            return;
        }

        ProcessResult status = await ProcessRunner.RunAsync(cluster.Installation.PgCtlPath,
            ["status", "-D", cluster.DataDirectory], new Dictionary<string, string?>(), CancellationToken.None);
        Assert.AreEqual(3, status.ExitCode, "A live PostgreSQL directory must never be removed by regression-test cleanup.");
        PostgresServerStorage.Delete(cluster.DataDirectory);
        if (cluster.SocketDirectory is not null)
        {
            PostgresServerStorage.Delete(cluster.SocketDirectory);
        }
    }
}
