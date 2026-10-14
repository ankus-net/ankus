using Ankus.Testing;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies that test fixtures expire only the logs they named, so repeated runs do not fill a log directory.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
public sealed class TestLogRetentionTests(TestContext context)
{
    private const string Id = "0123456789abcdef0123456789abcdef";

    /// <summary>
    /// Server logs, their Windows event sidecars and publish binary logs are the fixtures' own; nothing else is.
    /// </summary>
    /// <param name="name">The file name.</param>
    /// <param name="owned">Whether a fixture created a file of that name.</param>
    [TestMethod]
    [DataRow("18-4242-" + Id + ".log", true)]
    [DataRow("13-1-" + Id + ".log.events.lock", true)]
    [DataRow("19-77-" + Id + ".log.events." + Id + ".json", true)]
    [DataRow(Id + ".binlog", true)]
    [DataRow("18-4242-" + Id + ".log.bak", false)]
    [DataRow("18-4242-" + Id + ".log.events." + Id + ".txt", false)]
    [DataRow("18-" + Id + ".log", false)]
    [DataRow("x-4242-" + Id + ".log", false)]
    [DataRow("18-4242-0123.log", false)]
    [DataRow("server.log", false)]
    [DataRow("build-" + Id + ".binlog", false)]
    [DataRow("0123456789abcdef0123456789abcdeg.binlog", false)]
    [DataRow("notes.txt", false)]
    public void RecognizesOnlyFixtureLogNames(string name, bool owned) => Assert.AreEqual(owned, TestLogRetention.IsOwned(name));

    /// <summary>
    /// Pruning removes the fixtures' logs older than the cutoff and leaves recent logs and every other file.
    /// </summary>
    [TestMethod]
    public void PruneRemovesOnlyExpiredFixtureLogs()
    {
        string directory = Path.Join(Path.GetTempPath(), "ankus-log-retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            DateTime now = DateTime.UtcNow;
            string expiredLog = Write(directory, "18-1-" + Id + ".log", now.AddDays(-8));
            string expiredSidecar = Write(directory, "18-1-" + Id + ".log.events.lock", now.AddDays(-8));
            string expiredBuild = Write(directory, Id + ".binlog", now.AddDays(-8));
            string recentLog = Write(directory, "18-2-" + Id + ".log", now.AddDays(-1));
            string foreign = Write(directory, "notes.log", now.AddDays(-30));

            Assert.AreEqual(3, TestLogRetention.Prune(directory, now - TestLogRetention.Retention));
            Assert.IsFalse(File.Exists(expiredLog));
            Assert.IsFalse(File.Exists(expiredSidecar));
            Assert.IsFalse(File.Exists(expiredBuild));
            Assert.IsTrue(File.Exists(recentLog));
            Assert.IsTrue(File.Exists(foreign));
            Assert.AreEqual(0, TestLogRetention.Prune(Path.Join(directory, "missing"), now));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A cluster starting in a log directory expires the earlier runs' logs there and keeps its own.
    /// </summary>
    [TestMethod]
    public async Task ClusterStartExpiresEarlierLogs()
    {
        CancellationToken token = context.CancellationToken;
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(token);
        string directory = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs", "retention-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string expired = Write(directory, "18-1-" + Id + ".log", DateTime.UtcNow.AddDays(-8));
            string foreign = Write(directory, "notes.log", DateTime.UtcNow.AddDays(-8));
            var options = new PostgresTestClusterOptions
            {
                Installation = defaults.Installation,
                DataDirectoryBase = defaults.DataDirectoryBase,
                LogDirectory = directory,
                StartupTimeout = defaults.StartupTimeout,
                PostgreSqlConfiguration = defaults.PostgreSqlConfiguration,
            };
            await using (PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token))
            {
                Assert.IsTrue(File.Exists(cluster.LogFilePath));
            }

            Assert.IsFalse(File.Exists(expired));
            Assert.IsTrue(File.Exists(foreign));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string Write(string directory, string name, DateTime lastWriteUtc)
    {
        string path = Path.Join(directory, name);
        File.WriteAllText(path, "log");
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
