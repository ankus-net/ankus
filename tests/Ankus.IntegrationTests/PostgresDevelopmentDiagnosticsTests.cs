using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Exercises persistent native diagnostics without publishing an extension or installing packages.
/// </summary>
/// <param name="context">The current test context.</param>
[TestClass]
public sealed class PostgresDevelopmentDiagnosticsTests(TestContext context)
{
    /// <summary>
    /// Separate development-cluster instances retain the same isolated event diagnostics across reads, shutdown and restart.
    /// </summary>
    [RetryPortCollisionTestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task DevelopmentDiagnosticsPersistAcrossCollectorsAndRestarts()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(token);
        string home = Directory.CreateTempSubdirectory("ankus-cluster-diagnostics-").FullName;
        var first = new PostgresDevelopmentCluster(installation, home);
        var next = new PostgresDevelopmentCluster(installation, home);
        using PortReservation reservation = PortReservation.Create();
        var options = new PostgresDevelopmentOptions { Port = reservation.Port };
        reservation.Dispose();
        try
        {
            Assert.IsTrue(await first.StartAsync(options, token));
            string message = "retained café 🐘 " + Guid.NewGuid().ToString("N");
            await using (NpgsqlConnection connection = await OpenDevelopmentConnectionAsync(reservation.Port, token))
            {
                await using var setup = new NpgsqlCommand("CREATE TABLE retained_diagnostics(value integer); INSERT INTO retained_diagnostics VALUES (42)", connection);
                await setup.ExecuteNonQueryAsync(token);
                await using var destination = new NpgsqlCommand("ALTER SYSTEM SET log_destination = 'eventlog'", connection);
                await destination.ExecuteNonQueryAsync(token);
                await using var reload = new NpgsqlCommand("SELECT pg_reload_conf()", connection);
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await reload.ExecuteScalarAsync(token)));
                await WaitForEventDestinationAsync(connection, token);
                await using var warning = new NpgsqlCommand(
                    $"DO $test$ BEGIN RAISE WARNING '{message}' USING DETAIL = 'native detail café', HINT = 'native hint 🐘'; END $test$;", connection);
                await warning.ExecuteNonQueryAsync(token);
            }

            string initial = next.ReadServerLog(token);
            Assert.Contains("WARNING:  " + message, initial);
            Assert.Contains("DETAIL:  native detail café", initial);
            Assert.Contains("HINT:  native hint 🐘", initial);
            string[] concurrent = await Task.WhenAll(Enumerable.Range(0, 4).Select(index =>
                Task.Run(() => (index % 2 == 0 ? first : next).ReadServerLog(token), token)));
            foreach (string text in concurrent)
            {
                Assert.AreEqual(1, text.Split(message, StringSplitOptions.None).Length - 1);
            }

            Assert.IsTrue(await next.StopAsync(token));
            var stopped = new PostgresDevelopmentCluster(installation, home);
            string retained = stopped.ReadServerLog(token);
            Assert.AreEqual(1, retained.Split(message, StringSplitOptions.None).Length - 1);
            Assert.Contains("database system is shut down", retained);
            Assert.AreEqual(retained, await File.ReadAllTextAsync(first.LogFilePath, token));
            Assert.IsTrue(await stopped.StartAsync(options, token));
            await using NpgsqlConnection restored = await OpenDevelopmentConnectionAsync(reservation.Port, token);
            await using var query = new NpgsqlCommand("SELECT value FROM retained_diagnostics", restored);
            Assert.AreEqual(42, await query.ExecuteScalarAsync(token));
            Assert.AreEqual(1, stopped.ReadServerLog(token).Split(message, StringSplitOptions.None).Length - 1);
        }
        finally
        {
            await first.StopAsync(CancellationToken.None);
            PostgresServerStorage.Delete(home);
        }
    }

    /// <summary>
    /// A native startup failure retains its reason and existing database, and a corrected restart recovers.
    /// </summary>
    [RetryPortCollisionTestMethod]
    public async Task DevelopmentDiagnosticsRetainNativeFailureAndRecover()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(token);
        string home = Directory.CreateTempSubdirectory("ankus-cluster-diagnostics-").FullName;
        var cluster = new PostgresDevelopmentCluster(installation, home);
        using PortReservation reservation = PortReservation.Create();
        // Successful starts get the suite's startup budget; the deliberate FATAL ends pg_ctl's wait at once.
        var options = new PostgresDevelopmentOptions { Port = reservation.Port, TimeoutSeconds = (int)IntegrationEnvironment.StartupTimeout.TotalSeconds };
        reservation.Dispose();
        try
        {
            Assert.IsTrue(await cluster.StartAsync(options, token));
            await using (NpgsqlConnection connection = await OpenDevelopmentConnectionAsync(reservation.Port, token))
            {
                await using var setup = new NpgsqlCommand("CREATE TABLE retained_failure(value integer); INSERT INTO retained_failure VALUES (42)", connection);
                await setup.ExecuteNonQueryAsync(token);
            }

            Assert.IsTrue(await cluster.StopAsync(token));
            string auto = Path.Join(cluster.DataDirectory, "postgresql.auto.conf");
            string original = await File.ReadAllTextAsync(auto, token);
            string missing = "ankus_missing_diagnostic_library_" + Guid.NewGuid().ToString("N");
            string destination = OperatingSystem.IsWindows() ? "log_destination = 'eventlog'\n" : string.Empty;
            await File.AppendAllTextAsync(auto, destination + $"shared_preload_libraries = '{missing}'\n", token);
            var next = new PostgresDevelopmentCluster(installation, home);
            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => next.StartAsync(options, token));
            Assert.Contains("FATAL:", error.Message);
            Assert.Contains(missing, error.Message);
            Assert.Contains(missing, next.ReadServerLog(token));
            Assert.IsFalse(await next.IsRunningAsync(token));
            Assert.IsTrue(File.Exists(Path.Join(cluster.DataDirectory, "PG_VERSION")));
            await File.WriteAllTextAsync(auto, original, token);
            Assert.IsTrue(await next.StartAsync(options, token));
            await using NpgsqlConnection recovered = await OpenDevelopmentConnectionAsync(reservation.Port, token);
            await using var query = new NpgsqlCommand("SELECT value FROM retained_failure", recovered);
            Assert.AreEqual(42, await query.ExecuteScalarAsync(token));
            Assert.Contains(missing, new PostgresDevelopmentCluster(installation, home).ReadServerLog(token));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
            PostgresServerStorage.Delete(home);
        }
    }

    /// <summary>
    /// Each start records the file the server writes in the data directory's <c>.ankus-log</c>, so a tool such as a log
    /// tailer can follow it without reproducing Ankus's naming. The server's own output reaches that file, the
    /// <c>.ankus-cluster</c> marker keeps the label every Ankus version checks, and a start after the Ankus home moves
    /// records the new path. The home is copied to its new path: on a busy Windows machine, the system can keep a new
    /// cluster's files referenced for minutes after every process has closed them, and until it lets go, Windows
    /// refuses to rename any directory above them.
    /// </summary>
    [RetryPortCollisionTestMethod]
    public async Task StartRecordsTheServerLogPath()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(token);
        string home = Directory.CreateTempSubdirectory("ankus-cluster-log-").FullName;
        string moved = home + "-moved";
        var cluster = new PostgresDevelopmentCluster(installation, home);
        var relocated = new PostgresDevelopmentCluster(installation, moved);
        using PortReservation reservation = PortReservation.Create();
        var options = new PostgresDevelopmentOptions { Port = reservation.Port, TimeoutSeconds = (int)IntegrationEnvironment.StartupTimeout.TotalSeconds };
        reservation.Dispose();
        try
        {
            Assert.IsTrue(await cluster.StartAsync(options, token));
            string recorded = await ReadRecordedLogAsync(cluster, token);
            Assert.AreEqual(cluster.LogFilePath + (OperatingSystem.IsWindows() ? ".stderr.log" : string.Empty), recorded);
            string message = "recorded log " + Guid.NewGuid().ToString("N");
            await using (NpgsqlConnection connection = await OpenDevelopmentConnectionAsync(reservation.Port, token))
            {
                await using var warning = new NpgsqlCommand($"DO $test$ BEGIN RAISE WARNING '{message}'; END $test$;", connection);
                await warning.ExecuteNonQueryAsync(token);
            }

            Assert.IsTrue(await cluster.StopAsync(token));
            Assert.Contains("WARNING:  " + message, await File.ReadAllTextAsync(recorded, token));
            Assert.AreEqual(installation.Label, await File.ReadAllTextAsync(Path.Join(cluster.DataDirectory, ".ankus-cluster"), token));

            CopyTree(home, moved);
            Assert.IsTrue(await relocated.StartAsync(options, token));
            Assert.AreEqual(relocated.LogFilePath + (OperatingSystem.IsWindows() ? ".stderr.log" : string.Empty),
                await ReadRecordedLogAsync(relocated, token));
        }
        finally
        {
            await relocated.StopAsync(CancellationToken.None);
            await cluster.StopAsync(CancellationToken.None);
            PostgresServerStorage.Delete(moved);
            PostgresServerStorage.Delete(home);
        }

        // Keeps each directory's mode, as a rename would: PostgreSQL refuses a data directory others can read.
        static void CopyTree(string source, string destination)
        {
            CopyDirectory(source, destination);
            foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                CopyDirectory(directory, Path.Join(destination, Path.GetRelativePath(source, directory)));
            }

            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                File.Copy(file, Path.Join(destination, Path.GetRelativePath(source, file)));
            }
        }

        static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(destination, File.GetUnixFileMode(source));
            }
        }

        static async Task<string> ReadRecordedLogAsync(PostgresDevelopmentCluster cluster, CancellationToken token)
        {
            string text = await File.ReadAllTextAsync(Path.Join(cluster.DataDirectory, ".ankus-log"), token);
            Assert.EndsWith("\n", text);
            return text[..^1];
        }
    }

    /// <summary>
    /// A damaged cluster identity cannot attach to a different provider or overwrite retained diagnostics.
    /// </summary>
    /// <param name="identity">The damaged identity marker.</param>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow("")]
    [DataRow("not-a-guid")]
    [DataRow("00000000000000000000000000000000")]
    [DataRow("af312eb0-0a4c-44b4-af76-27c73f44db19")]
    public async Task DevelopmentDiagnosticsRejectDamagedIdentityWithoutChangingFiles(string identity)
    {
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(context.CancellationToken);
        string home = Directory.CreateTempSubdirectory("ankus-cluster-diagnostics-").FullName;
        try
        {
            var cluster = new PostgresDevelopmentCluster(installation, home);
            Directory.CreateDirectory(cluster.DataDirectory);
            string ownership = Path.Join(cluster.DataDirectory, ".ankus-cluster");
            string version = Path.Join(cluster.DataDirectory, "PG_VERSION");
            string major = installation.Version.Major.ToString(System.Globalization.CultureInfo.InvariantCulture);
            await File.WriteAllTextAsync(ownership, installation.Label, context.CancellationToken);
            await File.WriteAllTextAsync(version, major, context.CancellationToken);
            string marker = Path.Join(cluster.DataDirectory, ".ankus-log-identity");
            await File.WriteAllTextAsync(marker, identity, context.CancellationToken);
            const string Retained = "previous retained diagnostics café 🐘\n";
            await File.WriteAllTextAsync(cluster.LogFilePath, Retained, context.CancellationToken);
            InvalidDataException error = Assert.ThrowsExactly<InvalidDataException>(() => cluster.ReadServerLog(context.CancellationToken));
            Assert.Contains("identity is invalid", error.Message);
            Assert.AreEqual(identity, await File.ReadAllTextAsync(marker, context.CancellationToken));
            Assert.AreEqual(installation.Label, await File.ReadAllTextAsync(ownership, context.CancellationToken));
            Assert.AreEqual(major, await File.ReadAllTextAsync(version, context.CancellationToken));
            Assert.AreEqual(Retained, await File.ReadAllTextAsync(cluster.LogFilePath, context.CancellationToken));
            Assert.IsTrue(Directory.Exists(cluster.DataDirectory));
            Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(cluster.LogFilePath)!, "*.json"));
            Assert.IsEmpty(Directory.GetFiles(Path.GetDirectoryName(cluster.LogFilePath)!, "*.tmp"));
        }
        finally
        {
            PostgresServerStorage.Delete(home);
        }
    }

    /// <summary>
    /// Waits for PostgreSQL's asynchronous configuration reload before emitting an event-only witness.
    /// </summary>
    /// <param name="connection">The live backend whose configuration will reload.</param>
    /// <param name="token">The current test cancellation token.</param>
    private static async Task WaitForEventDestinationAsync(NpgsqlConnection connection, CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        await using var setting = new NpgsqlCommand("SHOW log_destination", connection);
        while (await setting.ExecuteScalarAsync(token) is not "eventlog")
        {
            if (DateTime.UtcNow >= deadline)
            {
                Assert.Fail("PostgreSQL did not apply the requested event-only destination.");
            }

            await Task.Delay(25, token);
        }
    }

    /// <summary>
    /// Connects only to this test's loopback development server without retaining pooled backend connections.
    /// </summary>
    /// <param name="port">The test's reserved native port.</param>
    /// <param name="token">The current test cancellation token.</param>
    /// <returns>The open native server connection.</returns>
    private static async Task<NpgsqlConnection> OpenDevelopmentConnectionAsync(int port, CancellationToken token)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1",
            Port = port,
            Username = "postgres",
            Database = "postgres",
            Pooling = false,
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
