using System.Globalization;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies startup source precedence and the original privileges retained by placeholder history.
/// </summary>
/// <param name="context">The test cancellation and diagnostic context.</param>
[TestClass]
[DoNotParallelize]
public sealed class GucSourceTests(TestContext context)
{
    /// <summary>
    /// File, database, role, database-role, and client defaults survive session and local overrides.
    /// </summary>
    [TestMethod]
    public async Task StartupSourcesPreservePriorityAndResetValues()
    {
        PostgresTestClusterOptions options = await OptionsAsync(
            "shared_preload_libraries = 'Ankus.Examples.Configuration'", "ankus_configuration.user = 61");
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        await using NpgsqlConnection administrator = await cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(administrator, "CREATE EXTENSION ankus_configuration; CREATE ROLE source_reader LOGIN");
        var builder = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Username = "source_reader", Pooling = false };
        string database = QuoteIdentifier(builder.Database!);
        await AssertNativeSourceAsync(builder, 61, "configuration file");
        await ExecuteAsync(administrator, $"ALTER DATABASE {database} SET \"ankus_configuration.user\" = '62'");
        await AssertNativeSourceAsync(builder, 62, "database");
        await ExecuteAsync(administrator, "ALTER ROLE source_reader SET \"ankus_configuration.user\" = '63'");
        await AssertNativeSourceAsync(builder, 63, "user");
        await ExecuteAsync(administrator, $"ALTER ROLE source_reader IN DATABASE {database} SET \"ankus_configuration.user\" = '64'");
        await AssertNativeSourceAsync(builder, 64, "database user");
        builder.Options = "-c ankus_configuration.user=65";
        await AssertNativeSourceAsync(builder, 65, "client");
        Assert.AreEqual("61|61|configuration file", await MetadataAsync(administrator, "ankus_configuration.user"));
    }

    /// <summary>
    /// Deferred definitions preserve startup check-hook sources for manual and session preload.
    /// </summary>
    /// <param name="sessionPreload">Whether PostgreSQL loads the managed library during connection startup.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlaceholderStartupSourcesReachTypedCheckHooks(bool sessionPreload)
    {
        PostgresTestClusterOptions options = await OptionsAsync("ankus_guc.hook_int = 31");
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, context.CancellationToken);
        await using NpgsqlConnection administrator = await cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(administrator, "CREATE EXTENSION ankus_test; CREATE ROLE source_reader LOGIN");
        var builder = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Username = "source_reader", Pooling = false };
        string database = QuoteIdentifier(builder.Database!);
        if (sessionPreload)
        {
            await ExecuteAsync(administrator, "ALTER ROLE source_reader SET session_preload_libraries = 'Ankus.TestExtension'");
        }

        await AssertHookSourceAsync(builder, 31, "File", "configuration file", sessionPreload);
        await ExecuteAsync(administrator, $"ALTER DATABASE {database} SET ankus_guc.hook_int = '32'");
        await AssertHookSourceAsync(builder, 32, "Database", "database", sessionPreload);
        await ExecuteAsync(administrator, "ALTER ROLE source_reader SET ankus_guc.hook_int = '33'");
        await AssertHookSourceAsync(builder, 33, "User", "user", sessionPreload);
        await ExecuteAsync(administrator, $"ALTER ROLE source_reader IN DATABASE {database} SET ankus_guc.hook_int = '34'");
        await AssertHookSourceAsync(builder, 34, "DatabaseUser", "database user", sessionPreload);
        builder.Options = "-c ankus_guc.hook_int=35";
        await AssertHookSourceAsync(builder, 35, "Client", "client", sessionPreload);
    }

    /// <summary>
    /// Definition replay uses the original privilege context or current parameter grant, independently of the loader.
    /// </summary>
    /// <param name="privilegedAtSet">Whether the placeholder setter initially has the required privilege.</param>
    /// <param name="privilegedAtLoad">Whether that privilege exists when the definition replaces the placeholder.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public async Task PlaceholderAdoptionRetainsOriginalSetterPrivileges(bool privilegedAtSet, bool privilegedAtLoad)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        string role = "guc_source_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, $"CREATE ROLE {role}");
        try
        {
            if (privilegedAtSet)
            {
                await SetPrivilegeAsync(connection, role, true);
            }

            await ExecuteAsync(connection, $"SET ROLE {role}; SET ankus_guc.privileged = '11'; RESET ROLE");
            Assert.AreEqual("11", await ScalarAsync(connection, "SHOW ankus_guc.privileged"));
            if (privilegedAtSet != privilegedAtLoad)
            {
                await SetPrivilegeAsync(connection, role, privilegedAtLoad);
            }

            var notices = new List<PostgresNotice>();
            connection.Notice += (_, args) => notices.Add(args.Notice);
            await ExecuteAsync(connection, "LOAD 'Ankus.TestExtension'");
            bool accepted = PostgresFixture.Cluster.Installation.Version.Major >= 15 ? privilegedAtLoad : privilegedAtSet;
            Assert.AreEqual(accepted ? "11|1|session" : "1|1|default", await MetadataAsync(connection, "ankus_guc.privileged"));
            if (accepted)
            {
                Assert.IsEmpty(notices);
            }
            else
            {
                AssertPermissionNotice(Assert.ContainsSingle(notices));
            }

            await ExecuteAsync(connection, "SET ankus_guc.privileged = '12'; RESET ankus_guc.privileged");
            Assert.AreEqual("1|1|default", await MetadataAsync(connection, "ankus_guc.privileged"));
            Assert.AreEqual(42, await ScalarAsync(connection, "SELECT 42"));
        }
        finally
        {
            await connection.DisposeAsync();
            await DropRolesAsync(role);
        }
    }

    /// <summary>
    /// SET and SET LOCAL retain independent privileges when a placeholder stack is replayed and committed.
    /// </summary>
    /// <param name="localGranted">Whether the local setter, rather than the masked session setter, has the grant.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PlaceholderMaskedAndLocalStatesRetainTheirOwnPrivileges(bool localGranted)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        string sessionRole = "guc_session_" + Guid.NewGuid().ToString("N");
        string localRole = "guc_local_" + Guid.NewGuid().ToString("N");
        await ExecuteAsync(connection, $"CREATE ROLE {sessionRole}; CREATE ROLE {localRole}");
        try
        {
            await SetPrivilegeAsync(connection, localGranted ? localRole : sessionRole, true);
            await ExecuteAsync(connection, "SET ankus_guc.privileged = '11'");
            await ExecuteAsync(connection, $"""
                BEGIN;
                SET ROLE {sessionRole}; SET ankus_guc.privileged = '22';
                RESET ROLE; SET ROLE {localRole}; SET LOCAL ankus_guc.privileged = '33'; RESET ROLE;
                """);
            Assert.AreEqual("33", await ScalarAsync(connection, "SHOW ankus_guc.privileged"));
            var notices = new List<PostgresNotice>();
            connection.Notice += (_, args) => notices.Add(args.Notice);
            await ExecuteAsync(connection, "LOAD 'Ankus.TestExtension'");
            AssertPermissionNotice(Assert.ContainsSingle(notices));
            Assert.AreEqual(localGranted ? "33" : "22", await ScalarAsync(connection, "SHOW ankus_guc.privileged"));
            await ExecuteAsync(connection, "COMMIT");
            Assert.AreEqual(localGranted ? "11" : "22", await ScalarAsync(connection, "SHOW ankus_guc.privileged"));
            await ExecuteAsync(connection, "BEGIN; SET LOCAL ankus_guc.privileged = '44'; ROLLBACK");
            Assert.AreEqual(localGranted ? "11" : "22", await ScalarAsync(connection, "SHOW ankus_guc.privileged"));
            Assert.AreEqual(42, await ScalarAsync(connection, "SELECT 42"));
        }
        finally
        {
            await connection.DisposeAsync();
            await DropRolesAsync(sessionRole, localRole);
        }
    }

    /// <summary>
    /// Sets the selected server's real privilege for a superuser-only custom setting.
    /// </summary>
    private Task SetPrivilegeAsync(NpgsqlConnection connection, string role, bool granted)
        => ExecuteAsync(connection, PostgresFixture.Cluster.Installation.Version.Major >= 15
            ? granted ? $"GRANT SET ON PARAMETER ankus_guc.privileged TO {role}"
                : $"REVOKE SET ON PARAMETER ankus_guc.privileged FROM {role}"
            : $"ALTER ROLE {role} {(granted ? "SUPERUSER" : "NOSUPERUSER")}");

    /// <summary>
    /// Checks startup/reset storage, managed reads, session overrides, and transaction restoration in a fresh backend.
    /// </summary>
    /// <param name="builder">The connection with the current startup sources.</param>
    /// <param name="value">The expected strongest startup value.</param>
    /// <param name="source">The native source name.</param>
    private async Task AssertNativeSourceAsync(NpgsqlConnectionStringBuilder builder, int value, string source)
    {
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(context.CancellationToken);
        string expected = string.Create(CultureInfo.InvariantCulture, $"{value}|{value}|{source}");
        Assert.AreEqual(expected, await MetadataAsync(connection, "ankus_configuration.user"));
        Assert.AreEqual(value.ToString(CultureInfo.InvariantCulture), await ScalarAsync(connection,
            "SELECT split_part(configuration_snapshot(), '|', 6)"));
        await ExecuteAsync(connection, "SET \"ankus_configuration.user\" = '90'");
        Assert.AreEqual($"90|{value}|session", await MetadataAsync(connection, "ankus_configuration.user"));
        await ExecuteAsync(connection, "BEGIN; SET LOCAL \"ankus_configuration.user\" = '91'");
        Assert.AreEqual($"91|{value}|session", await MetadataAsync(connection, "ankus_configuration.user"));
        await ExecuteAsync(connection, "ROLLBACK");
        Assert.AreEqual($"90|{value}|session", await MetadataAsync(connection, "ankus_configuration.user"));
        await ExecuteAsync(connection, "RESET \"ankus_configuration.user\"");
        Assert.AreEqual(expected, await MetadataAsync(connection, "ankus_configuration.user"));
    }

    /// <summary>
    /// Checks that deferred registration reconstructs the accepted default and its check source.
    /// </summary>
    /// <param name="builder">The connection with the current startup sources.</param>
    /// <param name="value">The expected accepted startup integer.</param>
    /// <param name="hookSource">The typed source supplied to check.</param>
    /// <param name="nativeSource">The source reported by PostgreSQL.</param>
    /// <param name="sessionPreload">Whether the library has already loaded during connection startup.</param>
    private async Task AssertHookSourceAsync(NpgsqlConnectionStringBuilder builder, int value, string hookSource, string nativeSource, bool sessionPreload)
    {
        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync(context.CancellationToken);
        if (!sessionPreload)
        {
            await ScalarAsync(connection, "SELECT guc_hook_values()");
        }

        Assert.AreEqual($"False|{value}|1.25|<null>|18446744073709551615", await ScalarAsync(connection, "SELECT guc_hook_values()"));
        string events = Assert.IsInstanceOfType<string>(await ScalarAsync(connection, "SELECT guc_events(true)"));
        Assert.Contains("check:10:Default", events.Split('\n'));
        Assert.Contains($"check:{value}:{hookSource}", events.Split('\n'));
        Assert.AreEqual($"{value}|{nativeSource}", await ScalarAsync(connection,
            "SELECT concat_ws('|', reset_val, source) FROM pg_settings WHERE name = 'ankus_guc.hook_int'"));
        await ExecuteAsync(connection, "SET ankus_guc.hook_int = '36'; RESET ankus_guc.hook_int");
        Assert.AreEqual($"False|{value}|1.25|<null>|18446744073709551615", await ScalarAsync(connection, "SELECT guc_hook_values()"));
    }

    /// <summary>
    /// Verifies the native warning for an unauthorized placeholder replay.
    /// </summary>
    /// <param name="notice">The single registration notice.</param>
    private static void AssertPermissionNotice(PostgresNotice notice)
    {
        Assert.AreEqual("42501", notice.SqlState);
        Assert.AreEqual("WARNING", notice.InvariantSeverity);
        Assert.AreEqual("permission denied to set parameter \"ankus_guc.privileged\"", notice.MessageText);
    }

    /// <summary>
    /// Removes isolated role grants through a fresh backend after the original session releases its transaction.
    /// </summary>
    /// <param name="roles">The generated role identifiers.</param>
    private async Task DropRolesAsync(params string[] roles)
    {
        await using NpgsqlConnection cleanup = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        string names = string.Join(", ", roles);
        await ExecuteAsync(cleanup, $"DROP OWNED BY {names}; DROP ROLE {names}");
    }

    /// <summary>
    /// Builds isolated startup options while retaining the published library search paths.
    /// </summary>
    /// <param name="settings">The native configuration entries.</param>
    /// <returns>The isolated cluster options.</returns>
    private async Task<PostgresTestClusterOptions> OptionsAsync(params string[] settings)
    {
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(context.CancellationToken);
        return new PostgresTestClusterOptions
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            PostgreSqlConfiguration = [.. defaults.PostgreSqlConfiguration, .. settings],
        };
    }

    /// <summary>
    /// Reads native current/reset values and source without parsing display commands.
    /// </summary>
    /// <param name="connection">The backend connection.</param>
    /// <param name="name">The configuration name.</param>
    /// <returns>The current/reset/source tuple.</returns>
    private async Task<object?> MetadataAsync(NpgsqlConnection connection, string name)
    {
        await using var command = new NpgsqlCommand(
            "SELECT concat_ws('|', setting, reset_val, source) FROM pg_settings WHERE name = $1", connection);
        command.Parameters.AddWithValue(name);
        return await command.ExecuteScalarAsync(context.CancellationToken);
    }

    /// <summary>
    /// Executes one scalar observation in the specified backend.
    /// </summary>
    /// <param name="connection">The backend connection.</param>
    /// <param name="sql">The SQL text.</param>
    /// <returns>The first scalar value.</returns>
    private async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(context.CancellationToken);
    }

    /// <summary>
    /// Executes configuration, privilege, or transaction changes.
    /// </summary>
    /// <param name="connection">The backend connection.</param>
    /// <param name="sql">The SQL text.</param>
    private async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }

    /// <summary>
    /// Quotes a database name for SQL text.
    /// </summary>
    private static string QuoteIdentifier(string name)
    {
        using var quoting = new NpgsqlCommandBuilder();
        return quoting.QuoteIdentifier(name);
    }
}
