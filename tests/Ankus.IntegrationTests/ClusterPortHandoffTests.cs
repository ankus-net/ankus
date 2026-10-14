using System.Net;
using System.Net.Sockets;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Retains real TCP listeners during handoff and checks PostgreSQL startup, cleanup and failure boundaries.
/// </summary>
/// <param name="context">The per-test context.</param>
[TestClass]
public sealed class ClusterPortHandoffTests(TestContext context)
{
    /// <summary>
    /// A requested port is visible in PostgreSQL, accepts real queries, and is released with the owned cluster.
    /// </summary>
    [TestMethod]
    public async Task RequestedTestPortRunsQueriesAndIsReleased()
    {
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        using PortReservation reserved = TestPortReservations.Create();
        PostgresTestClusterOptions options = AtPort(defaults, reserved.Port);
        reserved.Dispose();
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        Assert.AreEqual(reserved.Port, cluster.Port);
        await using (NpgsqlConnection connection = await cluster.OpenConnectionAsync(context.CancellationToken))
        {
            await using var command = new NpgsqlCommand("SELECT current_setting('port')::integer", connection);
            Assert.AreEqual(reserved.Port, await command.ExecuteScalarAsync(context.CancellationToken));
            command.CommandText = "SELECT 19 + 23";
            Assert.AreEqual(42, await command.ExecuteScalarAsync(context.CancellationToken));
        }

        await cluster.DisposeAsync();
        Assert.IsFalse(Directory.Exists(cluster.DataDirectory));
        Assert.IsFalse(Directory.Exists(cluster.SocketDirectory));
        using PortReservation released = PortReservation.Create(reserved.Port);
        Assert.AreEqual(reserved.Port, released.Port);
    }

    /// <summary>
    /// An already occupied explicit port fails native startup, cleans owned data, and leaves its owner connected.
    /// </summary>
    [TestMethod]
    public async Task OccupiedRequestedPortCleansUpWithoutSwitchingPorts()
    {
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        using PortReservation reservation = TestPortReservations.Create();
        PostgresTestClusterOptions options = AtPort(defaults, reservation.Port);
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            PostgresTestCluster.StartAsync(options, context.CancellationToken));
        Assert.Contains("could not create any TCP/IP sockets", error.Message);
        Assert.IsEmpty(Directory.GetFileSystemEntries(options.DataDirectoryBase));
        TcpListener competitor = reservation.TakeListener();
        try
        {
            await AssertListenerAliveAsync(competitor);
        }
        finally
        {
            competitor.Stop();
        }
    }

    /// <summary>
    /// Losing an explicit port during handoff cleans the failed cluster without selecting another port.
    /// </summary>
    [TestMethod]
    public async Task RequestedPortCollisionDoesNotRetryAndRecoversAfterRelease()
    {
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        using PortReservation selected = TestPortReservations.Create();
        PostgresTestClusterOptions options = AtPort(defaults, selected.Port);
        selected.Dispose();
        var attempts = new List<PostgresTestCluster>();
        var competitors = new List<TcpListener>();
        try
        {
            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                PostgresTestCluster.StartAsync(options, (attempt, _) =>
                {
                    attempts.Add(attempt);
                    var competitor = new TcpListener(IPAddress.Loopback, attempt.Port);
                    competitors.Add(competitor);
                    competitor.Start();
                }, context.CancellationToken));
            PostgresTestCluster failed = Assert.ContainsSingle(attempts);
            Assert.AreEqual(selected.Port, failed.Port);
            Assert.Contains("could not create any TCP/IP sockets", error.Message);
            Assert.IsFalse(Directory.Exists(failed.DataDirectory));
            Assert.IsFalse(Directory.Exists(failed.SocketDirectory));
            Assert.ContainsSingle(competitors);
            Assert.ThrowsExactly<SocketException>(() =>
            {
                using PortReservation unexpected = PortReservation.Create(selected.Port);
            });
        }
        finally
        {
            foreach (TcpListener competitor in competitors)
            {
                competitor.Stop();
            }
        }

        await using PostgresTestCluster recovered = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        Assert.AreEqual(selected.Port, recovered.Port);
        await using NpgsqlConnection connection = await recovered.OpenConnectionAsync(context.CancellationToken);
        await using var command = new NpgsqlCommand("SELECT 42", connection);
        Assert.AreEqual(42, await command.ExecuteScalarAsync(context.CancellationToken));
    }

    /// <summary>
    /// Invalid requested ports fail before initialization or native publication.
    /// </summary>
    /// <param name="port">An invalid explicit port.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(-1)]
    [DataRow(65536)]
    public async Task InvalidRequestedPortsFailBeforeCreatingFiles(int port)
    {
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        PostgresTestClusterOptions options = AtPort(defaults, port);
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => PostgresTestCluster.StartAsync(options, context.CancellationToken));
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => PostgresExtensionTest.StartAsync(
            Path.Join(options.DataDirectoryBase, "missing.csproj"), sharedPreload: false, port, cancellationToken: context.CancellationToken));
        Assert.IsFalse(Directory.Exists(options.DataDirectoryBase));
    }

    /// <summary>
    /// A stolen port produces a fresh isolated cluster while preserving the competing listener and failed startup log.
    /// </summary>
    [TestMethod]
    public async Task PortCollisionRetriesAndPreservesCompetingListener()
    {
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        var attempts = new List<PostgresTestCluster>();
        TcpListener? competitor = null;
        try
        {
            await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, (attempt, reservation) =>
            {
                Assert.IsNotNull(reservation);
                attempts.Add(attempt);
                if (attempts.Count == 1)
                {
                    competitor = reservation.TakeListener();
                }
            }, context.CancellationToken);

            Assert.HasCount(2, attempts);
            Assert.AreSame(cluster, attempts[1]);
            Assert.AreNotEqual(attempts[0].Port, cluster.Port);
            Assert.IsFalse(Directory.Exists(attempts[0].DataDirectory));
            Assert.IsFalse(Directory.Exists(attempts[0].SocketDirectory));
            Assert.Contains("could not create any TCP/IP sockets", attempts[0].ReadServerLog());
            Assert.IsNotNull(competitor);
            await AssertListenerAliveAsync(competitor);
            await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(context.CancellationToken);
            await using var command = new NpgsqlCommand("SELECT current_setting('port')::integer", connection);
            Assert.AreEqual(cluster.Port, await command.ExecuteScalarAsync(context.CancellationToken));
            command.CommandText = "SELECT current_setting('data_directory')";
            string dataDirectory = Assert.IsInstanceOfType<string>(await command.ExecuteScalarAsync(context.CancellationToken));
            Assert.AreEqual(cluster.DataDirectory, Path.GetFullPath(dataDirectory));
            command.CommandText = "SELECT 42";
            Assert.AreEqual(42, await command.ExecuteScalarAsync(context.CancellationToken));
        }
        finally
        {
            competitor?.Stop();
        }
    }

    /// <summary>
    /// Persistent collisions stop after three attempts, clean every owned cluster and retain the final native error.
    /// </summary>
    [TestMethod]
    public async Task RepeatedPortCollisionsAreBoundedAndCleanEveryAttempt()
    {
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        var attempts = new List<PostgresTestCluster>();
        var competitors = new List<TcpListener>();
        try
        {
            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                PostgresTestCluster.StartAsync(options, (attempt, reservation) =>
                {
                    Assert.IsNotNull(reservation);
                    attempts.Add(attempt);
                    competitors.Add(reservation.TakeListener());
                }, context.CancellationToken));

            Assert.HasCount(3, attempts, error.ToString());
            Assert.HasCount(3, attempts.Select(static attempt => attempt.Port).Distinct());
            Assert.Contains("could not create any TCP/IP sockets", error.Message);
            Assert.Contains(attempts[^1].LogFilePath, error.Message);
            Assert.IsInstanceOfType<InvalidOperationException>(error.InnerException);
            foreach (PostgresTestCluster attempt in attempts)
            {
                Assert.IsFalse(Directory.Exists(attempt.DataDirectory));
                Assert.IsFalse(Directory.Exists(attempt.SocketDirectory));
                Assert.Contains($"Is another postmaster already running on port {attempt.Port}?", attempt.ReadServerLog());
            }

            foreach (TcpListener competitor in competitors)
            {
                await AssertListenerAliveAsync(competitor);
            }
        }
        finally
        {
            foreach (TcpListener competitor in competitors)
            {
                competitor.Stop();
            }
        }
    }

    /// <summary>
    /// An unrelated failure after a collision is not retried using stale diagnostics from the prior attempt.
    /// </summary>
    [TestMethod]
    public async Task ConfigurationFailureAfterCollisionStopsImmediately()
    {
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        var attempts = new List<PostgresTestCluster>();
        TcpListener? competitor = null;
        try
        {
            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                PostgresTestCluster.StartAsync(options, (attempt, reservation) =>
                {
                    Assert.IsNotNull(reservation);
                    attempts.Add(attempt);
                    if (attempts.Count == 1)
                    {
                        competitor = reservation.TakeListener();
                    }
                    else
                    {
                        File.AppendAllText(Path.Join(attempt.DataDirectory, "postgresql.auto.conf"), "ankus_invalid_configuration = 'invalid'\n");
                    }
                }, context.CancellationToken));

            Assert.HasCount(2, attempts, error.ToString());
            Assert.Contains("could not create any TCP/IP sockets", attempts[0].ReadServerLog());
            Assert.Contains("ankus_invalid_configuration", error.Message);
            Assert.DoesNotContain("could not create any TCP/IP sockets", error.Message);
            foreach (PostgresTestCluster attempt in attempts)
            {
                Assert.IsFalse(Directory.Exists(attempt.DataDirectory));
                Assert.IsFalse(Directory.Exists(attempt.SocketDirectory));
            }

            Assert.IsNotNull(competitor);
            await AssertListenerAliveAsync(competitor);
        }
        finally
        {
            competitor?.Stop();
        }
    }

    /// <summary>
    /// Cancellation during the handoff cleans initialized storage without starting a postmaster or retrying.
    /// </summary>
    [TestMethod]
    public async Task CancellationAtHandoffDoesNotStartOrRetryPostgres()
    {
        PostgresTestClusterOptions options = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
        var attempts = new List<PostgresTestCluster>();
        await Assert.ThrowsAsync<OperationCanceledException>(() => PostgresTestCluster.StartAsync(options, (attempt, _) =>
        {
            attempts.Add(attempt);
            cancellation.Cancel();
        }, cancellation.Token));

        PostgresTestCluster failed = Assert.ContainsSingle(attempts);
        Assert.IsFalse(Directory.Exists(failed.DataDirectory));
        Assert.IsFalse(Directory.Exists(failed.SocketDirectory));
        Assert.IsFalse(File.Exists(failed.LogFilePath));
    }

    private static PostgresTestClusterOptions AtPort(PostgresTestClusterOptions defaults, int port)
        => new()
        {
            Installation = defaults.Installation,
            Port = port,
            SharedDirectory = defaults.SharedDirectory,
            DataDirectoryBase = Path.Join(defaults.DataDirectoryBase, "fixed-" + Guid.NewGuid().ToString("N")),
            LogDirectory = defaults.LogDirectory,
            DatabaseName = defaults.DatabaseName,
            UserName = defaults.UserName,
            PostgreSqlConfiguration = defaults.PostgreSqlConfiguration,
            ProcessEnvironment = defaults.ProcessEnvironment,
            StartupTimeout = defaults.StartupTimeout,
            ShutdownTimeout = defaults.ShutdownTimeout,
        };

    private async Task AssertListenerAliveAsync(TcpListener listener)
    {
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port, context.CancellationToken);
        using TcpClient accepted = await listener.AcceptTcpClientAsync(context.CancellationToken);
        Assert.IsTrue(client.Connected);
        Assert.IsTrue(accepted.Connected);
    }
}
