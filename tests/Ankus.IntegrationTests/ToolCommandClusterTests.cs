using System.Globalization;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Installed lifecycle commands preserve rows, exact settings, and the existing backend across redundant starts.
    /// </summary>
    [RetryPortCollisionTestMethod]
    public async Task ClusterCommandsPreserveDataAcrossRestarts()
    {
        CancellationToken token = context.CancellationToken;
        string home = CreateDirectory();
        var cluster = new PostgresDevelopmentCluster(s_installation, home);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        string portText = port.ToString(CultureInfo.InvariantCulture);
        const string Value = " café='\\value # ; $(echo test) ";
        string[] start = ["start", "--home", home, "--pg", MajorText(), "--pg-config", s_installation.PgConfigPath,
            "--port", portText, "--postgresql-conf", "probe.text=" + Value];
        try
        {
            ProcessResult missing = await InvokeAsync(
                ["status", "--home", home, "--pg", MajorText(), "--pg-config", s_installation.PgConfigPath], token);
            Assert.AreEqual(0, missing.ExitCode, missing.StandardError);
            Assert.Contains(s_postgresKey + ": stopped", missing.StandardOutput);
            Assert.IsEmpty(Directory.GetFileSystemEntries(home));
            reservation.Dispose();
            ProcessResult started = await InvokeAsync(start, token).WaitAsync(TimeSpan.FromMinutes(2), token);
            Assert.AreEqual(0, started.ExitCode, started.StandardError);
            Assert.Contains(s_postgresKey + ": started", started.StandardOutput);
            Assert.Contains(cluster.DataDirectory, started.StandardOutput);
            Assert.IsTrue(await cluster.IsRunningAsync(token));
            await using (NpgsqlConnection connection = await OpenDevelopmentConnectionAsync(port, token))
            {
                await using var setup = new NpgsqlCommand("CREATE TABLE retained(value integer); INSERT INTO retained VALUES (42); SELECT current_setting('probe.text')", connection);
                Assert.AreEqual(Value, await setup.ExecuteScalarAsync(token));
                await using var pid = new NpgsqlCommand("SELECT pg_backend_pid()", connection);
                object? originalPid = await pid.ExecuteScalarAsync(token);
                ProcessResult duplicate = await InvokeAsync(start, token);
                Assert.AreEqual(0, duplicate.ExitCode, duplicate.StandardError);
                Assert.Contains(": already running", duplicate.StandardOutput);
                Assert.AreEqual(originalPid, await pid.ExecuteScalarAsync(token));
            }

            ProcessResult running = await InvokeAsync(
                ["status", "--home", home, "--pg", MajorText(), "--pg-config", s_installation.PgConfigPath], token);
            Assert.AreEqual(0, running.ExitCode, running.StandardError);
            Assert.Contains(s_postgresKey + ": running", running.StandardOutput);
            foreach (string expected in new[] { ": stopped", ": already stopped" })
            {
                ProcessResult stopped = await InvokeAsync(
                    ["stop", "--home", home, "--pg", MajorText(), "--pg-config", s_installation.PgConfigPath], token);
                Assert.AreEqual(0, stopped.ExitCode, stopped.StandardError);
                Assert.Contains(expected, stopped.StandardOutput);
                Assert.IsFalse(await cluster.IsRunningAsync(token));
                Assert.IsTrue(File.Exists(Path.Join(cluster.DataDirectory, "PG_VERSION")));
            }

            ProcessResult restarted = await InvokeAsync(start, token).WaitAsync(TimeSpan.FromMinutes(2), token);
            Assert.AreEqual(0, restarted.ExitCode, restarted.StandardError);
            await using NpgsqlConnection restored = await OpenDevelopmentConnectionAsync(port, token);
            await using var query = new NpgsqlCommand("SELECT value FROM retained", restored);
            Assert.AreEqual(42, await query.ExecuteScalarAsync(token));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// A real occupied TCP port fails without touching the listener or discarding initialized data; retry succeeds.
    /// </summary>
    [TestMethod]
    public async Task DevelopmentClusterRecoversAfterStartupFailure()
    {
        CancellationToken token = context.CancellationToken;
        string home = CreateDirectory();
        var cluster = new PostgresDevelopmentCluster(s_installation, home);
        using PortReservation reservation = PortReservation.Create();
        var options = new PostgresDevelopmentOptions { Port = reservation.Port, TimeoutSeconds = 5 };
        try
        {
            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cluster.StartAsync(options, token));
            Assert.Contains(cluster.LogFilePath, error.Message);
            Assert.IsFalse(await cluster.IsRunningAsync(token));
            Assert.IsTrue(File.Exists(Path.Join(cluster.DataDirectory, "PG_VERSION")));
            Assert.IsEmpty(Directory.GetDirectories(Path.GetDirectoryName(cluster.DataDirectory)!, ".init-*"));
            reservation.Dispose();
            // The deliberate failure ends at once; the recovered start is an ordinary startup with the suite's budget.
            var recovered = new PostgresDevelopmentOptions { Port = options.Port, TimeoutSeconds = (int)IntegrationEnvironment.StartupTimeout.TotalSeconds };
            Assert.IsTrue(await cluster.StartAsync(recovered, token));
            await using NpgsqlConnection connection = await OpenDevelopmentConnectionAsync(options.Port!.Value, token);
            await using var command = new NpgsqlCommand("SELECT 19 + 23", connection);
            Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Absent clusters, invalid options, and already canceled calls make no changes; an operation lock excludes a second start.
    /// </summary>
    [TestMethod]
    public async Task DevelopmentClusterValidatesBeforeChangesAndHonorsOperationLock()
    {
        CancellationToken token = context.CancellationToken;
        string home = CreateDirectory();
        var cluster = new PostgresDevelopmentCluster(s_installation, home);
        Assert.IsFalse(await cluster.IsRunningAsync(token));
        Assert.IsFalse(await cluster.StopAsync(token));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => cluster.StartAsync(new PostgresDevelopmentOptions { Port = 0 }, token));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => cluster.StartAsync(cancellationToken: canceled.Token));
        Assert.IsEmpty(Directory.GetFileSystemEntries(home));
        string root = Path.Join(home, "clusters");
        Directory.CreateDirectory(root);
        string lockPath = Path.Join(root, s_installation.Label + ".lock");
        using (new FileStream(lockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None))
        {
            await Assert.ThrowsExactlyAsync<IOException>(() => cluster.StartAsync(cancellationToken: token));
            Assert.IsFalse(Directory.Exists(cluster.DataDirectory));
            Assert.AreSequenceEqual([lockPath], Directory.GetFileSystemEntries(root));
        }
    }

    /// <summary>
    /// All lifecycle operations reject foreign or incompatible data without modifying a file.
    /// </summary>
    /// <param name="owned">Whether a marker exists with the wrong PostgreSQL major.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task DevelopmentClusterPreservesUnownedAndIncompatibleData(bool owned)
    {
        string home = CreateDirectory();
        var cluster = new PostgresDevelopmentCluster(s_installation, home);
        Directory.CreateDirectory(cluster.DataDirectory);
        string sentinel = Path.Join(cluster.DataDirectory, "keep.txt");
        await File.WriteAllTextAsync(sentinel, "user data", context.CancellationToken);
        if (owned)
        {
            await File.WriteAllTextAsync(Path.Join(cluster.DataDirectory, ".ankus-cluster"), s_installation.Label, context.CancellationToken);
            await File.WriteAllTextAsync(Path.Join(cluster.DataDirectory, "PG_VERSION"), DifferentMajor().ToString(CultureInfo.InvariantCulture), context.CancellationToken);
        }

        string[] entries = Directory.GetFileSystemEntries(cluster.DataDirectory);
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cluster.StartAsync(cancellationToken: context.CancellationToken));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cluster.StopAsync(context.CancellationToken));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cluster.IsRunningAsync(context.CancellationToken));
        Assert.AreEqual("user data", await File.ReadAllTextAsync(sentinel, context.CancellationToken));
        Assert.AreEquivalent(entries, Directory.GetFileSystemEntries(cluster.DataDirectory));
    }

    /// <summary>
    /// The installed command enumerates registered versions and rejects contradictory selections and malformed settings.
    /// </summary>
    [TestMethod]
    public async Task ClusterCommandsValidateSelectionsAndSettings()
    {
        string home = CreateDirectory();
        CancellationToken token = context.CancellationToken;
        ProcessResult empty = await InvokeAsync(["status", "--all", "--home", home], token);
        Assert.AreEqual(1, empty.ExitCode);
        Assert.Contains("No PostgreSQL versions are registered", empty.StandardError);
        (await InvokeAsync(["init", "--home", home, s_postgresOption, s_installation.PgConfigPath], token)).EnsureSuccess(s_tool, ["init"]);
        ProcessResult all = await InvokeAsync(["status", "--all", "--home", home], token);
        Assert.AreEqual(0, all.ExitCode, all.StandardError);
        Assert.AreEqual(s_postgresKey + ": stopped", all.StandardOutput.Trim());
        ProcessResult stopped = await InvokeAsync(["stop", "--all", "--home", home], token);
        Assert.AreEqual(0, stopped.ExitCode, stopped.StandardError);
        Assert.AreEqual(s_postgresKey + ": already stopped", stopped.StandardOutput.Trim());
        ProcessResult conflict = await InvokeAsync(["start", "--all", "--pg", MajorText(), "--home", home], token);
        Assert.AreEqual(1, conflict.ExitCode);
        Assert.Contains("Use --all without", conflict.StandardError);
        ProcessResult malformed = await InvokeAsync(["start", "--pg", MajorText(), "--home", home, "--postgresql-conf", "work_mem"], token);
        Assert.AreEqual(1, malformed.ExitCode);
        Assert.Contains("name=value", malformed.StandardError);
        Assert.IsFalse(Directory.Exists(Path.Join(home, "clusters")));
    }

    private static async Task<NpgsqlConnection> OpenDevelopmentConnectionAsync(int port, CancellationToken token)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = port, Username = "postgres", Database = "postgres", Pooling = false,
        }.ConnectionString);
        try
        {
            await connection.OpenAsync(token);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }
}
