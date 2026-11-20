using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Checks startup and connection recovery when a PostgreSQL process reads a <c>pg_control</c> that fails its checksum,
/// as one started on Windows while another process rewrites the file can.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
public sealed class ControlFileReadRetryTests(TestContext context)
{
    private const string TornRead = "FATAL:  incorrect checksum in control file";

    /// <summary>
    /// A startup that ends on the checksum failure begins again with a fresh cluster that serves queries.
    /// </summary>
    [TestMethod]
    public async Task StartupAfterTornControlFileReadUsesFreshCluster()
    {
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        var attempts = new List<PostgresTestCluster>();
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, (attempt, _) =>
        {
            attempts.Add(attempt);
            if (attempts.Count == 1)
            {
                CorruptControlFile(attempt);
            }
        }, context.CancellationToken);

        Assert.HasCount(2, attempts);
        Assert.AreSame(cluster, attempts[1]);
        Assert.IsFalse(Directory.Exists(attempts[0].DataDirectory));
        Assert.Contains(TornRead, attempts[0].ReadServerLog());
        Assert.DoesNotContain(TornRead, cluster.ReadServerLog());
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT 19 + 23", connection);
        Assert.AreEqual(42, await command.ExecuteScalarAsync(context.CancellationToken));
    }

    /// <summary>
    /// Repeated checksum failures stop after three fresh clusters, each removed, with the server's message reported.
    /// </summary>
    [TestMethod]
    public async Task RepeatedTornControlFileReadsAreBoundedAndCleanEveryAttempt()
    {
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        var attempts = new List<PostgresTestCluster>();
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            PostgresTestCluster.StartAsync(options, (attempt, _) =>
            {
                attempts.Add(attempt);
                CorruptControlFile(attempt);
            }, context.CancellationToken));

        Assert.HasCount(3, attempts, error.ToString());
        Assert.Contains("incorrect checksum in control file", error.Message);
        Assert.Contains(attempts[^1].LogFilePath, error.Message);
        foreach (PostgresTestCluster attempt in attempts)
        {
            Assert.IsFalse(Directory.Exists(attempt.DataDirectory));
            Assert.Contains(TornRead, attempt.ReadServerLog());
        }
    }

    /// <summary>
    /// On Windows each new backend reads <c>pg_control</c> again; one that fails the checksum is replaced and the
    /// connection opens without restarting the server.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task BackendThatReadsTornControlFileIsReplaced()
    {
        await using PostgresTestCluster cluster = await StartQuietClusterAsync();
        string path = Path.Join(cluster.DataDirectory, "global", "pg_control");
        byte[] original = await File.ReadAllBytesAsync(path, context.CancellationToken);
        int retries = 0;
        cluster.BeforeConnectionRetry = () =>
        {
            retries++;
            File.WriteAllBytes(path, original);
        };

        await File.WriteAllBytesAsync(path, Corrupt(original), context.CancellationToken);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT 19 + 23", connection);
        Assert.AreEqual(42, await command.ExecuteScalarAsync(context.CancellationToken));
        Assert.AreEqual(1, retries);
        string log = cluster.ReadServerLog();
        Assert.AreEqual(1, CountOccurrences(log, TornRead), log);
        Assert.DoesNotContain("reinitializing", log);
    }

    /// <summary>
    /// On Windows a backend that keeps failing the checksum ends the connection after three attempts, and the server
    /// accepts connections again once the file is repaired.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task PersistentTornControlFileReadsStopAfterThreeConnectionAttempts()
    {
        await using PostgresTestCluster cluster = await StartQuietClusterAsync();
        string path = Path.Join(cluster.DataDirectory, "global", "pg_control");
        byte[] original = await File.ReadAllBytesAsync(path, context.CancellationToken);
        int retries = 0;
        cluster.BeforeConnectionRetry = () => retries++;
        await File.WriteAllBytesAsync(path, Corrupt(original), context.CancellationToken);
        try
        {
            await Assert.ThrowsAsync<NpgsqlException>(() => cluster.OpenConnectionAsync(context.CancellationToken));
        }
        finally
        {
            await File.WriteAllBytesAsync(path, original, context.CancellationToken);
        }

        Assert.AreEqual(2, retries);
        string log = cluster.ReadServerLog();
        Assert.AreEqual(3, CountOccurrences(log, TornRead), log);
        Assert.DoesNotContain("reinitializing", log);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT 19 + 23", connection);
        Assert.AreEqual(42, await command.ExecuteScalarAsync(context.CancellationToken));
    }

    /// <summary>
    /// Starts a cluster without autovacuum workers or timed checkpoints, so only the test's own backends read or
    /// write <c>pg_control</c> while it is corrupted.
    /// </summary>
    private async Task<PostgresTestCluster> StartQuietClusterAsync()
    {
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        var options = new PostgresTestClusterOptions
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            StartupTimeout = defaults.StartupTimeout,
            PostgreSqlConfiguration = [.. defaults.PostgreSqlConfiguration, "autovacuum = off", "checkpoint_timeout = '1d'"],
        };
        return await PostgresTestCluster.StartAsync(options, context.CancellationToken);
    }

    private static void CorruptControlFile(PostgresTestCluster cluster)
    {
        string path = Path.Join(cluster.DataDirectory, "global", "pg_control");
        File.WriteAllBytes(path, Corrupt(File.ReadAllBytes(path)));
    }

    // The system identifier comes first in pg_control, so changing it keeps the version check passing and fails only the
    // checksum.
    private static byte[] Corrupt(byte[] controlFile)
    {
        byte[] corrupted = [.. controlFile];
        corrupted[0] ^= 0xFF;
        return corrupted;
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int index = text.IndexOf(value, StringComparison.Ordinal);
        while (index >= 0)
        {
            count++;
            index = text.IndexOf(value, index + value.Length, StringComparison.Ordinal);
        }

        return count;
    }
}
