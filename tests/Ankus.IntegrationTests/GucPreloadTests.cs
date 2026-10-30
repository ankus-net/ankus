using System.Net.Sockets;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Exercises native-only registration before backend creation, source precedence, units, and reload.
/// </summary>
/// <param name="context">The test cancellation and diagnostic context.</param>
[TestClass]
public sealed class GucPreloadTests(TestContext context)
{
    /// <summary>
    /// A GUC-only library has a usable native initializer without managed exports or SQL declarations.
    /// </summary>
    [TestMethod]
    public async Task GucOnlyLibraryLoadsAndRegisters()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(connection, "LOAD 'Ankus.GucOnlyExtension'");
        Assert.AreEqual("on", await ScalarAsync(connection, "SHOW ankus_guc_only.enabled"));
        await ExecuteAsync(connection, "SET ankus_guc_only.enabled = 'off'; LOAD 'Ankus.GucOnlyExtension'");
        Assert.AreEqual("off", await ScalarAsync(connection, "SHOW ankus_guc_only.enabled"));
    }

    /// <summary>
    /// Non-ASCII preload metadata is rejected before any database encoding has been selected.
    /// </summary>
    [TestMethod]
    public async Task SharedPreloadRejectsMetadataWithoutDatabaseEncoding()
    {
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        var options = new PostgresTestClusterOptions
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            PostgreSqlConfiguration = [.. defaults.PostgreSqlConfiguration, "shared_preload_libraries = 'Ankus.GucOnlyExtension'"],
        };
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => PostgresTestCluster.StartAsync(options, context.CancellationToken));
        Assert.Contains("ASCII", error.Message);
    }

    /// <summary>
    /// A hooks-only library can initialize its check callback during an ordinary backend LOAD.
    /// </summary>
    [TestMethod]
    public async Task HooksOnlyLibraryRunsItsCheckInBackend()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        var notices = new List<PostgresNotice>();
        connection.Notice += (_, args) => notices.Add(args.Notice);
        await ExecuteAsync(connection, "LOAD 'Ankus.GucHooksExtension'");
        Assert.AreEqual("Ankus hooks-only check entered.", Assert.ContainsSingle(notices).MessageText);
        Assert.AreEqual("on", await ScalarAsync(connection, "SHOW ankus_guc_hooks.enabled"));
        await ExecuteAsync(connection, "SET ankus_guc_hooks.enabled = 'off'");
        Assert.HasCount(2, notices);
        Assert.AreEqual("off", await ScalarAsync(connection, "SHOW ankus_guc_hooks.enabled"));
    }

    /// <summary>
    /// An assign-only library reads old native storage, observes restoration, and contains managed failures.
    /// </summary>
    [TestMethod]
    public async Task AssignOnlyLibraryPreservesOldValueAndRestoration()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(connection, "LOAD 'Ankus.GucAssignExtension'");
        Assert.AreEqual("7", await ScalarAsync(connection, "SHOW ankus_guc_assign.count"));
        await ExecuteAsync(connection, "SET ankus_guc_assign.count = '9'");
        await ExecuteAsync(connection, "BEGIN; SET LOCAL ankus_guc_assign.count = '11'; ROLLBACK");
        Assert.AreEqual("9", await ScalarAsync(connection, "SHOW ankus_guc_assign.count"));
        string session = "ankus-assign-error-" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, $"SET application_name = '{session}'; SET log_error_verbosity = verbose");
        await using NpgsqlConnection observer = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(observer, "LOAD 'Ankus.GucAssignExtension'");

        NpgsqlException failure = await Assert.ThrowsAsync<NpgsqlException>(() => ExecuteAsync(connection, "SET ankus_guc_assign.count = '666'"));
        if (failure is PostgresException error)
        {
            Assert.AreEqual("FATAL", error.InvariantSeverity);
            Assert.AreEqual("38000", error.SqlState);
            Assert.AreEqual("Assign-only callback entered.", error.MessageText);
        }
        else
        {
            // Windows can reset the socket before the client reads PostgreSQL's terminal error.
            Assert.IsTrue(OperatingSystem.IsWindows());
            IOException transport = Assert.IsInstanceOfType<IOException>(failure.InnerException);
            SocketException socket = Assert.IsInstanceOfType<SocketException>(transport.InnerException);
            Assert.AreEqual(SocketError.ConnectionReset, socket.SocketErrorCode);
        }

        Assert.Contains($"[{session}]: FATAL:  38000: Assign-only callback entered.", PostgresFixture.Cluster.ReadServerLog());
        Assert.AreEqual(System.Data.ConnectionState.Closed, connection.State);
        Assert.AreEqual(42, await ScalarAsync(observer, "SELECT 42"));
        Assert.AreEqual("7", await ScalarAsync(observer, "SHOW ankus_guc_assign.count"));
        await ExecuteAsync(observer, "SET ankus_guc_assign.count = '9'; RESET ankus_guc_assign.count");
        Assert.AreEqual("7", await ScalarAsync(observer, "SHOW ankus_guc_assign.count"));
    }

    /// <summary>
    /// A show-only library has display hooks and typed reads without any SQL execution callback.
    /// </summary>
    [TestMethod]
    public async Task ShowOnlyLibraryReadsNativeStorageWithoutRecursion()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(connection, "LOAD 'Ankus.GucShowExtension'");
        Assert.AreEqual("display=7;native=7;extra=none", await ScalarAsync(connection, "SHOW ankus_guc_show.count"));
        await ExecuteAsync(connection, "SET ankus_guc_show.count = '9'");
        await ExecuteAsync(connection, "BEGIN; SET LOCAL ankus_guc_show.count = '11'");
        Assert.AreEqual("display=11;native=11;extra=none", await ScalarAsync(connection, "SHOW ankus_guc_show.count"));
        await ExecuteAsync(connection, "ROLLBACK");
        Assert.AreEqual("display=9;native=9;extra=none", await ScalarAsync(connection, "SHOW ankus_guc_show.count"));
    }

    /// <summary>
    /// A hooks-only library runs managed checks in the postmaster and independent forked backends.
    /// </summary>
    [TestMethod]
    public async Task SharedPreloadRunsManagedHooksAcrossFork()
    {
        CancellationToken token = context.CancellationToken;
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        PostgresTestClusterOptions options = new()
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            PostgreSqlConfiguration = [.. defaults.PostgreSqlConfiguration, "shared_preload_libraries = 'Ankus.GucHooksExtension'"],
        };
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        string startupLog = cluster.ReadServerLog();
        Assert.Contains("Ankus hooks-only check entered.", startupLog);
        Assert.Contains("source=Default;sql=unavailable;pid=", startupLog);
        const string reloadMarker = "source=File;sql=unavailable;pid=";
        int reloadCount = CountOccurrences(startupLog, reloadMarker);
        await using (NpgsqlConnection administrator = await cluster.OpenConnectionAsync(token))
        {
            for (int reloadIndex = 0; reloadIndex < 3; reloadIndex++)
            {
                string value = reloadIndex % 2 == 0 ? "off" : "on";
                await ExecuteAsync(administrator, $"ALTER SYSTEM SET ankus_guc_hooks.enabled = '{value}'");
                Assert.IsTrue(Assert.IsInstanceOfType<bool>(await ScalarAsync(administrator, "SELECT pg_reload_conf()")));

                string reload = string.Empty;
                int observedCount = reloadCount;
                for (int attempt = 0; attempt < 100; attempt++)
                {
                    reload = cluster.ReadServerLog();
                    observedCount = CountOccurrences(reload, reloadMarker);
                    if (observedCount > reloadCount)
                    {
                        break;
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(50), token);
                }

                Assert.IsGreaterThan(reloadCount, observedCount);
                reloadCount = observedCount;
            }
        }

        HashSet<int> backends = [];
        for (int index = 0; index < 3; index++)
        {
            await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
            Assert.IsTrue(backends.Add(connection.ProcessID));
            List<PostgresNotice> notices = [];
            connection.Notice += (_, arguments) => notices.Add(arguments.Notice);
            await ExecuteAsync(connection, "SET ankus_guc_hooks.enabled = 'off'");
            PostgresNotice notice = Assert.ContainsSingle(notices);
            Assert.AreEqual("Ankus hooks-only check entered.", notice.MessageText);
            Assert.AreEqual($"source=Session;sql=42;pid={connection.ProcessID}", notice.Detail);
            Assert.AreEqual("off", await ScalarAsync(connection, "SHOW ankus_guc_hooks.enabled"));
        }
    }

    /// <summary>
    /// A shared-preloaded check runs while PostgreSQL applies database and role settings at connection start, under a
    /// snapshot PostgreSQL registered, and its SQL reads there without a transaction ID.
    /// </summary>
    [TestMethod]
    public async Task SharedPreloadChecksDatabaseAndRoleSettingsAtConnectionStart()
    {
        CancellationToken token = context.CancellationToken;
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(token);
        PostgresTestClusterOptions options = new()
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            PostgreSqlConfiguration = [.. defaults.PostgreSqlConfiguration, "shared_preload_libraries = 'Ankus.GucHooksExtension'",
                "log_min_messages = notice"],
        };
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using NpgsqlConnection administrator = await cluster.OpenConnectionAsync(token);
        await ExecuteAsync(administrator, "CREATE ROLE role_setting LOGIN; CREATE ROLE database_setting LOGIN");
        await ExecuteAsync(administrator, "ALTER ROLE role_setting SET ankus_guc_hooks.enabled = 'off'");
        await ExecuteAsync(administrator, $"ALTER DATABASE \"{administrator.Database}\" SET ankus_guc_hooks.enabled = 'off'");
        string[] roles = ["role_setting", "database_setting"];
        foreach (string role in roles)
        {
            var builder = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Username = role, Pooling = false };
            await using var connection = new NpgsqlConnection(builder.ConnectionString);
            await connection.OpenAsync(token);
            string source = role == "role_setting" ? "user" : "database";
            Assert.AreEqual($"off|{source}", await ScalarAsync(connection,
                "SELECT setting || '|' || source FROM pg_settings WHERE name = 'ankus_guc_hooks.enabled'"));
            string check = $"source={(role == "role_setting" ? "User" : "Database")};sql=42;pid={connection.ProcessID}";
            string log = cluster.ReadServerLog();
            for (int attempt = 0; attempt < 100 && !log.Contains(check, StringComparison.Ordinal); attempt++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), token);
                log = cluster.ReadServerLog();
            }

            Assert.Contains(check, log, role);
        }
    }

    /// <summary>
    /// Native preload leaves managed execution until each independent backend, preserving inherited settings.
    /// </summary>
    [TestMethod]
    public async Task SharedPreloadPreservesNativeStorageAndBackendRuntime()
    {
        PostgresTestClusterOptions options = await OptionsAsync("ankus_configuration.startup = 11", "ankus_configuration.text = 'preloaded'");
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        await using NpgsqlConnection setup = await cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(setup, "CREATE EXTENSION ankus_configuration");
        var processes = new HashSet<int>();
        for (int index = 0; index < 5; index++)
        {
            await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(context.CancellationToken);
            Assert.IsTrue(processes.Add(connection.ProcessID));
            string expected = $"11|20|30|40|50|60|70|preloaded|{connection.ProcessID}";
            for (int iteration = 0; iteration < 5; iteration++)
            {
                Assert.AreEqual(expected, await ScalarAsync(connection, "SELECT configuration_snapshot()"));
            }

            await ExecuteAsync(connection, "SET \"ankus_configuration.user\" = '99'");
        }

        Assert.AreEqual($"11|20|30|40|50|60|70|preloaded|{setup.ProcessID}", await ScalarAsync(setup, "SELECT configuration_snapshot()"));
    }

    /// <summary>
    /// Every native memory/time unit uses the server's conversion rules and compiled block sizes.
    /// </summary>
    [TestMethod]
    public async Task UnitsUseServerConversionsAndBlockSizes()
    {
        PostgresTestClusterOptions options = await OptionsAsync();
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(connection, "CREATE EXTENSION ankus_configuration");
        await ExecuteAsync(connection,
            "SET ankus_configuration.bytes = '1kB'; SET ankus_configuration.kilobytes = '2MB'; " +
            "SET ankus_configuration.blocks = '1MB'; SET ankus_configuration.walblocks = '1MB'; " +
            "SET ankus_configuration.megabytes = '2GB'; SET ankus_configuration.milliseconds = '1s'; " +
            "SET ankus_configuration.seconds = '2min'; SET ankus_configuration.minutes = '2h'; " +
            "SET ankus_configuration.real_seconds = '1500ms'");
        int blockSize = Assert.IsInstanceOfType<int>(await ScalarAsync(connection, "SELECT current_setting('block_size')::int"));
        int walBlockSize = Assert.IsInstanceOfType<int>(await ScalarAsync(connection, "SELECT current_setting('wal_block_size')::int"));
        Assert.AreEqual($"1024|2048|{1048576 / blockSize}|{1048576 / walBlockSize}|2048|1000|120|120|1.5", await ScalarAsync(connection, "SELECT configuration_units()"));
        PostgresException invalid = await FailureAsync(connection, "SET ankus_configuration.seconds = '1MB'");
        Assert.AreEqual("22023", invalid.SqlState);
        Assert.AreEqual("2min", await ScalarAsync(connection, "SHOW ankus_configuration.seconds"));
    }

    /// <summary>
    /// Reload updates file defaults while preserving session priority and backend-start-only values.
    /// </summary>
    [TestMethod]
    public async Task ReloadPreservesSourcePriorityAndConnectionContexts()
    {
        PostgresTestClusterOptions options = await OptionsAsync("ankus_configuration.user = 61", "ankus_configuration.connection = 31");
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(connection, "CREATE EXTENSION ankus_configuration; SET \"ankus_configuration.user\" = '90'");
        foreach (string name in new[] { "startup", "reload", "connection", "privileged_connection", "internal" })
        {
            PostgresException error = await FailureAsync(connection, $"SET ankus_configuration.{name} = '2'");
            Assert.AreEqual("55P02", error.SqlState);
        }

        await ExecuteAsync(connection, "ALTER SYSTEM SET ankus_configuration.reload = '25'");
        await ExecuteAsync(connection, "ALTER SYSTEM SET \"ankus_configuration.user\" = '65'");
        await ExecuteAsync(connection, "ALTER SYSTEM SET ankus_configuration.connection = '35'");
        await ExecuteAsync(connection, "ALTER SYSTEM SET ankus_configuration.startup = '15'");
        string? execParameters = OperatingSystem.IsWindows()
            ? Path.Join(cluster.DataDirectory, "global", "config_exec_params")
            : null;
        Assert.IsTrue(Assert.IsInstanceOfType<bool>(await ScalarAsync(connection, "SELECT pg_reload_conf()")));
        await WaitForSettingAsync(connection, "ankus_configuration.reload", "25");
        if (execParameters is not null)
        {
            await WaitForFileValueAsync(execParameters, "ankus_configuration.connection", "35");
        }

        Assert.AreEqual("90", await ScalarAsync(connection, "SHOW \"ankus_configuration.user\""));
        Assert.AreEqual("31", await ScalarAsync(connection, "SHOW ankus_configuration.connection"));
        Assert.IsTrue(Assert.IsInstanceOfType<bool>(await ScalarAsync(connection, "SELECT pending_restart FROM pg_settings WHERE name = 'ankus_configuration.startup'")));
        await ExecuteAsync(connection, "RESET \"ankus_configuration.user\"");
        Assert.AreEqual("65", await ScalarAsync(connection, "SHOW \"ankus_configuration.user\""));
        await using NpgsqlConnection next = await cluster.OpenConnectionAsync(context.CancellationToken);
        Assert.AreEqual("35", await ScalarAsync(next, "SHOW ankus_configuration.connection"));
        Assert.AreEqual("11", await ScalarAsync(next, "SHOW ankus_configuration.startup"));
        await ExecuteAsync(connection, "ALTER SYSTEM RESET \"ankus_configuration.user\"");
        await ScalarAsync(connection, "SELECT pg_reload_conf()");
        await WaitForSettingAsync(connection, "ankus_configuration.user", "60");
    }

    /// <summary>
    /// Startup client values override lower-priority file defaults, and RESET returns to the client default.
    /// </summary>
    [TestMethod]
    public async Task ClientDefaultsAndBackendSettingsRetainSources()
    {
        PostgresTestClusterOptions options = await OptionsAsync("ankus_configuration.user = 61");
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        await using NpgsqlConnection template = await cluster.OpenConnectionAsync(context.CancellationToken);
        var builder = new NpgsqlConnectionStringBuilder(template.ConnectionString)
        {
            Options = "-c ankus_configuration.user=63 -c ankus_configuration.connection=33 -c ankus_configuration.privileged_connection=43",
        };
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(context.CancellationToken);
        Assert.AreEqual("63|client|63", await ScalarAsync(connection,
            "SELECT concat_ws('|', setting, source, reset_val) FROM pg_settings WHERE name = 'ankus_configuration.user'"));
        Assert.AreEqual("33", await ScalarAsync(connection, "SHOW ankus_configuration.connection"));
        Assert.AreEqual("43", await ScalarAsync(connection, "SHOW ankus_configuration.privileged_connection"));
        await ExecuteAsync(connection, "SET \"ankus_configuration.user\" = '90'; RESET \"ankus_configuration.user\"");
        Assert.AreEqual("63", await ScalarAsync(connection, "SHOW \"ankus_configuration.user\""));
    }

    /// <summary>
    /// Postmaster-only declarations reject a late LOAD with an ordinary recoverable error.
    /// </summary>
    [TestMethod]
    public async Task LatePostmasterRegistrationRejectsWithoutTerminatingBackend()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        PostgresException error = await FailureAsync(connection, "LOAD 'Ankus.Examples.Configuration'");
        Assert.AreEqual("ERROR", error.InvariantSeverity);
        Assert.Contains("shared_preload_libraries", error.MessageText);
        Assert.AreEqual(42, await ScalarAsync(connection, "SELECT 42"));
    }

    private async Task<PostgresTestClusterOptions> OptionsAsync(params string[] settings)
    {
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        return new PostgresTestClusterOptions
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            PostgreSqlConfiguration =
            [
                .. defaults.PostgreSqlConfiguration,
                "shared_preload_libraries = 'Ankus.Examples.Configuration'",
                "ankus_configuration.startup = 11",
                .. settings,
            ],
        };
    }

    private static int CountOccurrences(string text, string value)
    {
        int count = 0;
        int offset = 0;
        while ((offset = text.IndexOf(value, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += value.Length;
        }

        return count;
    }

    private async Task WaitForSettingAsync(NpgsqlConnection connection, string name, string value)
    {
        for (int attempt = 0; attempt < 100; attempt++)
        {
            if (Equals(value, await ScalarAsync(connection, "SHOW \"" + name + "\"")))
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), context.CancellationToken);
        }

        Assert.AreEqual(value, await ScalarAsync(connection, "SHOW \"" + name + "\""));
    }

    private async Task WaitForFileValueAsync(string path, string name, string value)
    {
        byte[] expected = System.Text.Encoding.UTF8.GetBytes(name + '\0' + value + '\0');
        for (int attempt = 0; attempt < 100; attempt++)
        {
            byte[]? contents = await TryReadServerFileAsync(path);
            if (contents is not null && contents.AsSpan().IndexOf(expected) >= 0)
            {
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(50), context.CancellationToken);
        }

        byte[] actual = await TryReadServerFileAsync(path) ?? [];
        Assert.IsGreaterThanOrEqualTo(0, actual.AsSpan().IndexOf(expected),
            "The postmaster did not publish the reloaded child-process setting.");
    }

    /// <summary>
    /// Reads a file the postmaster replaces by renaming, without blocking the rename, or returns null while it is in use.
    /// </summary>
    /// <param name="path">The server-owned file.</param>
    /// <returns>The file's bytes, or null when another process holds it exclusively.</returns>
    private async Task<byte[]?> TryReadServerFileAsync(string path)
    {
        try
        {
            await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, context.CancellationToken);
            return buffer.ToArray();
        }
        catch (IOException) when (OperatingSystem.IsWindows())
        {
            return null;
        }
    }

    private async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(context.CancellationToken);
    }

    private async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }

    private async Task<PostgresException> FailureAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await Assert.ThrowsExactlyAsync<PostgresException>(() => command.ExecuteNonQueryAsync(context.CancellationToken));
    }
}
