using System.Diagnostics;
using System.Text;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies installed regression tools and exact database resets against PostgreSQL.
/// </summary>
/// <param name="context">The current test cancellation context.</param>
[TestClass]
public sealed class PostgresRegressionPrerequisiteTests(TestContext context)
{
    private readonly string _root = Directory.CreateTempSubdirectory("ankus regression prerequisites ").FullName;

    /// <summary>
    /// Removes only the temporary data owned by this test after its server has stopped, waiting as PostgreSQL does for
    /// Windows to release the stopped server's file handles.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => PostgresServerStorage.Delete(_root);

    /// <summary>
    /// The regression executable follows authoritative PGXS relocation and is optional for ordinary installation discovery.
    /// </summary>
    [TestMethod]
    public async Task RegressionDriverFollowsPgxsAndReportsMissingTools()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation source = await IntegrationEnvironment.GetInstallationAsync(token);
        string original = await source.GetRegressionDriverPathAsync(token);
        ProcessResult pgxs = await ProcessRunner.RunAsync(source.PgConfigPath, ["--pgxs"], new Dictionary<string, string?>(), token);
        pgxs.EnsureSuccess("pg_config", ["--pgxs"]);
        string pgxsDriver = Path.GetFullPath(Path.Join(Path.GetDirectoryName(pgxs.StandardOutput.Trim())!, "..", "test", "regress",
            OperatingSystem.IsWindows() ? "pg_regress.exe" : "pg_regress"));
        Assert.AreEqual(OperatingSystem.IsWindows() && !File.Exists(pgxsDriver)
            ? Path.Join(source.BinDirectory, "pg_regress.exe")
            : pgxsDriver, original);
        ProcessResult version = await ProcessRunner.RunAsync(original, ["--version"],
            new Dictionary<string, string?> { ["PATH"] = source.BinDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") }, token);
        Assert.AreEqual(0, version.ExitCode, version.StandardError);
        ProcessResult configurationVersion = await ProcessRunner.RunAsync(source.PgConfigPath, ["--version"], new Dictionary<string, string?>(), token);
        configurationVersion.EnsureSuccess("pg_config", ["--version"]);
        Assert.AreEqual(configurationVersion.StandardOutput.Replace("PostgreSQL ", "pg_regress (PostgreSQL) ", StringComparison.Ordinal), version.StandardOutput);

        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(source, Path.Join(_root, "installation"), token);
        string relocated = await owner.Installation.GetRegressionDriverPathAsync(token);
        string staged = Directory.GetFiles(owner.RootDirectory, Path.GetFileName(original), SearchOption.AllDirectories).Single();
        Assert.AreNotEqual(original, relocated);
        Assert.IsTrue(File.Exists(staged));
        Assert.IsTrue(File.Exists(relocated));
        ProcessResult relocatedVersion = await ProcessRunner.RunAsync(relocated, ["--version"],
            new Dictionary<string, string?> { ["PATH"] = owner.Installation.BinDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") }, token);
        Assert.AreEqual(0, relocatedVersion.ExitCode, relocatedVersion.StandardError);
        Assert.AreEqual(version.StandardOutput, relocatedVersion.StandardOutput);
        await DeleteExecutable(relocated, token);
        Assert.IsFalse(File.Exists(staged));
        PostgresInstallation stillUsable = await PostgresInstallation.CreateAsync(owner.Installation.PgConfigPath, token);
        Assert.AreEqual(source.Version, stillUsable.Version);
        FileNotFoundException error = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => stillUsable.GetRegressionDriverPathAsync(token));
        ProcessResult stagedPgxs = await ProcessRunner.RunAsync(stillUsable.PgConfigPath, ["--pgxs"], new Dictionary<string, string?>(), token);
        stagedPgxs.EnsureSuccess("pg_config", ["--pgxs"]);
        Assert.AreEqual(Path.GetFullPath(Path.Join(Path.GetDirectoryName(stagedPgxs.StandardOutput.Trim())!, "..", "test", "regress",
            OperatingSystem.IsWindows() ? "pg_regress.exe" : "pg_regress")), error.FileName);
        Assert.Contains("regression tools", error.Message);
        Assert.IsTrue(File.Exists(original));
        using var canceled = new CancellationTokenSource();
        await canceled.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => source.GetRegressionDriverPathAsync(canceled.Token));
    }

    /// <summary>
    /// Windows resolves both native installer layouts inside the selected installation, preferring its PGXS driver.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task WindowsRegressionDriverSupportsBothInstallerLayouts()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation source = await IntegrationEnvironment.GetInstallationAsync(token);
        string original = await source.GetRegressionDriverPathAsync(token);
        await using PostgresTestInstallation owner = await PostgresTestInstallation.StageAsync(source, Path.Join(_root, "layouts"), token);
        ProcessResult pgxs = await ProcessRunner.RunAsync(owner.Installation.PgConfigPath, ["--pgxs"], new Dictionary<string, string?>(), token);
        pgxs.EnsureSuccess("pg_config", ["--pgxs"]);
        string pgxsDriver = Path.GetFullPath(Path.Join(Path.GetDirectoryName(pgxs.StandardOutput.Trim())!, "..", "test", "regress", "pg_regress.exe"));
        string binaryDriver = Path.Join(owner.Installation.BinDirectory, "pg_regress.exe");
        Assert.StartsWith(owner.RootDirectory + Path.DirectorySeparatorChar, pgxsDriver);
        Assert.StartsWith(owner.RootDirectory + Path.DirectorySeparatorChar, binaryDriver);
        Directory.CreateDirectory(Path.GetDirectoryName(pgxsDriver)!);
        File.Copy(original, pgxsDriver, overwrite: true);
        File.Copy(original, binaryDriver, overwrite: true);
        Assert.AreEqual(pgxsDriver, await owner.Installation.GetRegressionDriverPathAsync(token));

        File.Delete(pgxsDriver);
        Assert.AreEqual(binaryDriver, await owner.Installation.GetRegressionDriverPathAsync(token));
        ProcessResult version = await ProcessRunner.RunAsync(binaryDriver, ["--version"],
            new Dictionary<string, string?> { ["PATH"] = owner.Installation.BinDirectory + Path.PathSeparator + Environment.GetEnvironmentVariable("PATH") }, token);
        version.EnsureSuccess(binaryDriver, ["--version"]);
        ProcessResult configuration = await ProcessRunner.RunAsync(owner.Installation.PgConfigPath, ["--version"], new Dictionary<string, string?>(), token);
        configuration.EnsureSuccess("pg_config", ["--version"]);
        Assert.AreEqual(configuration.StandardOutput.Replace("PostgreSQL ", "pg_regress (PostgreSQL) ", StringComparison.Ordinal), version.StandardOutput);

        File.Delete(binaryDriver);
        FileNotFoundException error = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => owner.Installation.GetRegressionDriverPathAsync(token));
        Assert.AreEqual(pgxsDriver, error.FileName);
        Assert.IsTrue(File.Exists(original));
    }

    /// <summary>
    /// Removal preserves exact names, enforces the byte limit, leaves other data intact and can recreate an empty database.
    /// </summary>
    [RetryPortCollisionTestMethod]
    public async Task DatabaseRemovalPreservesNamesOtherDataAndResetIdentity()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(token);
        var cluster = new PostgresDevelopmentCluster(installation, _root);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        try
        {
            Assert.IsTrue(await cluster.StartAsync(new PostgresDevelopmentOptions { Port = port }, token));
            await using NpgsqlConnection administration = await OpenAsync(port, "postgres", token);
            await using var stringMode = new NpgsqlCommand("ALTER DATABASE postgres SET standard_conforming_strings = off", administration);
            if (installation.Version.Major < 19)
            {
                await stringMode.ExecuteNonQueryAsync(token);
            }
            else
            {
                PostgresException removed = await Assert.ThrowsExactlyAsync<PostgresException>(() => stringMode.ExecuteNonQueryAsync(token));
                Assert.AreEqual("0A000", removed.SqlState);
                Assert.AreEqual("non-standard string literals are not supported", removed.MessageText);
            }

            await using (NpgsqlConnection freshSettings = await OpenAsync(port, "postgres", token))
            {
                await using var configured = new NpgsqlCommand("SHOW standard_conforming_strings", freshSettings);
                Assert.AreEqual(installation.Version.Major < 19 ? "off" : "on", await configured.ExecuteScalarAsync(token));
            }

            await using var retained = new NpgsqlCommand(
                "CREATE TABLE retained(value integer); INSERT INTO retained VALUES (42); SELECT pg_backend_pid()", administration);
            object? backend = await retained.ExecuteScalarAsync(token);
            List<string> names = ["ordinary", " café'\\\"; # ", "--help", "host=elsewhere dbname=other", "postgresql://elsewhere/db", " ",
                "control\u001aend", "supplementary\U0001F986", new string('a', 63), new string('é', 31) + "a"];
            foreach (string name in new[] { "line\n\\! echo forbidden", "line\r\\! echo forbidden", "line\r\n\\! echo forbidden" })
            {
                if (installation.Version.Major < 19)
                {
                    names.Add(name);
                }
                else
                {
                    InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cluster.CreateDatabaseAsync(name, token));
                    Assert.Contains("contains a newline or carriage return character", error.Message);
                    Assert.IsFalse(await cluster.DropDatabaseAsync(name, cancellationToken: token));
                }
            }

            foreach (string name in names)
            {
                Assert.IsFalse(await cluster.DropDatabaseAsync(name, cancellationToken: token));
                Assert.IsTrue(await cluster.CreateDatabaseAsync(name, token));
            }

            foreach (string oversized in new[] { new string('a', 64), new string('é', 31) + "ab", new string('é', 32) })
            {
                ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(() => cluster.DropDatabaseAsync(oversized, cancellationToken: token));
                Assert.Contains("63-byte", error.Message);
            }

            await using var count = new NpgsqlCommand("SELECT count(*) FROM pg_database WHERE NOT datistemplate AND datname <> 'postgres'", administration);
            Assert.AreEqual((long)names.Count, await count.ExecuteScalarAsync(token));
            await using (NpgsqlConnection old = await OpenAsync(port, "ordinary", token))
            {
                await using var setup = new NpgsqlCommand("CREATE TABLE discarded(value integer); INSERT INTO discarded VALUES (99)", old);
                await setup.ExecuteNonQueryAsync(token);
            }

            for (int index = 0; index < names.Count; index++)
            {
                Assert.IsTrue(await cluster.DropDatabaseAsync(names[index], cancellationToken: token));
                Assert.IsFalse(await cluster.DropDatabaseAsync(names[index], cancellationToken: token));
                Assert.AreEqual((long)(names.Count - index - 1), await count.ExecuteScalarAsync(token));
            }

            Assert.IsTrue(await cluster.CreateDatabaseAsync("ordinary", token));
            await using NpgsqlConnection recreated = await OpenAsync(port, "ordinary", token);
            await using var empty = new NpgsqlCommand("SELECT to_regclass('public.discarded') IS NULL", recreated);
            Assert.IsTrue((bool)(await empty.ExecuteScalarAsync(token))!);
            await using var unchanged = new NpgsqlCommand("SELECT value FROM retained WHERE pg_backend_pid() = @backend", administration);
            unchanged.Parameters.AddWithValue("backend", backend!);
            Assert.AreEqual(42, await unchanged.ExecuteScalarAsync(token));
            Assert.IsTrue(await cluster.IsRunningAsync(token));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Active sessions require explicit force, and errors, cancellation and operation locks preserve the database.
    /// </summary>
    [RetryPortCollisionTestMethod]
    public async Task DatabaseRemovalHonorsForceLocksCancellationAndRecovery()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(token);
        var cluster = new PostgresDevelopmentCluster(installation, _root);
        using PortReservation reservation = PortReservation.Create();
        int port = reservation.Port;
        reservation.Dispose();
        try
        {
            await cluster.StartAsync(new PostgresDevelopmentOptions { Port = port }, token);
            using var statusWriter = new FileStream(Path.Join(cluster.DataDirectory, "postmaster.pid"), FileMode.Open,
                FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            Assert.IsTrue(await cluster.CreateDatabaseAsync("busy", token));
            await using NpgsqlConnection busy = await OpenAsync(port, "busy", token);
            await using var witness = new NpgsqlCommand("SELECT 42", busy);
            InvalidOperationException active = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cluster.DropDatabaseAsync("busy", cancellationToken: token));
            Assert.Contains("being accessed", active.Message);
            Assert.AreEqual(42, await witness.ExecuteScalarAsync(token));
            using var canceled = new CancellationTokenSource();
            await canceled.CancelAsync();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => cluster.DropDatabaseAsync("busy", true, canceled.Token));
            string operationPath = Path.Join(_root, "clusters", installation.Label + ".lock");
            using (new FileStream(operationPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                await Assert.ThrowsExactlyAsync<IOException>(() => cluster.DropDatabaseAsync("busy", true, token));
            }

            Assert.AreEqual(42, await witness.ExecuteScalarAsync(token));
            Assert.IsTrue(await cluster.DropDatabaseAsync("busy", true, token));
            Assert.IsFalse(await cluster.DropDatabaseAsync("busy", true, token));
            await using NpgsqlConnection administration = await OpenAsync(port, "postgres", token);
            await using var state = new NpgsqlCommand("SELECT NOT EXISTS (SELECT FROM pg_database WHERE datname='busy') AND NOT EXISTS (SELECT FROM pg_stat_activity WHERE datname='busy')", administration);
            Assert.IsTrue((bool)(await state.ExecuteScalarAsync(token))!);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cluster.DropDatabaseAsync("postgres", true, token));
            Assert.IsTrue((bool)(await state.ExecuteScalarAsync(token))!);
            Assert.IsTrue(await cluster.CreateDatabaseAsync("recovered", token));
            Assert.IsTrue(await cluster.DropDatabaseAsync("recovered", cancellationToken: token));
        }
        finally
        {
            await cluster.StopAsync(CancellationToken.None);
        }
    }

    /// <summary>
    /// Invalid input and a stopped server cannot create or modify a development directory.
    /// </summary>
    [TestMethod]
    public async Task DatabaseRemovalRejectsInvalidAndStoppedRequestsWithoutWrites()
    {
        CancellationToken token = context.CancellationToken;
        PostgresInstallation installation = await IntegrationEnvironment.GetInstallationAsync(token);
        var cluster = new PostgresDevelopmentCluster(installation, _root);
        await Assert.ThrowsExactlyAsync<ArgumentNullException>(() => cluster.DropDatabaseAsync(null!, cancellationToken: token));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => cluster.DropDatabaseAsync("", cancellationToken: token));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => cluster.DropDatabaseAsync("bad\0name", cancellationToken: token));
        await Assert.ThrowsExactlyAsync<EncoderFallbackException>(() => cluster.DropDatabaseAsync("bad\ud800", cancellationToken: token));
        await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => cluster.DropDatabaseAsync("missing", true, token));
        Assert.IsEmpty(Directory.GetFileSystemEntries(_root));
    }

    private static async Task<NpgsqlConnection> OpenAsync(int port, string database, CancellationToken token)
    {
        var connection = new NpgsqlConnection(new NpgsqlConnectionStringBuilder
        {
            Host = "127.0.0.1", Port = port, Username = "postgres", Database = database, Pooling = false,
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

    /// <summary>
    /// Deletes an executable that has just exited. Windows can deny the deletion while the exited process's image is
    /// still being released, so access and sharing failures are retried for up to ten seconds, as PostgreSQL's own
    /// Windows unlink does.
    /// </summary>
    private static async Task DeleteExecutable(string path, CancellationToken token)
    {
        long started = Stopwatch.GetTimestamp();
        while (true)
        {
            try
            {
                File.Delete(path);
                return;
            }
            catch (Exception error) when (OperatingSystem.IsWindows() && error is IOException or UnauthorizedAccessException &&
                Stopwatch.GetElapsedTime(started) < TimeSpan.FromSeconds(10))
            {
                await Task.Delay(100, token);
            }
        }
    }
}
