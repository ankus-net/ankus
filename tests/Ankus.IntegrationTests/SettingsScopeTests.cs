using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Verifies that settings scopes save and restore values through PostgreSQL's configuration nest levels.
/// </summary>
/// <param name="context">The current test context.</param>
[TestClass]
public sealed class SettingsScopeTests(TestContext context)
{
    /// <summary>
    /// The scoped value is visible inside the callback, survives a statement error caught there, and is restored however
    /// the callback ends, including when an atomic scope rolls back around it.
    /// </summary>
    /// <param name="outcome">How the callback ends.</param>
    /// <param name="expected">The value inside, the outcome, and the value afterwards.</param>
    [TestMethod]
    [DataRow("return", "250ms|ok|5s")]
    [DataRow("throw", "250ms|scoped failure|5s")]
    [DataRow("error", "250ms|22012|5s")]
    [DataRow("caught", "250ms:22012:250ms|ok|5s")]
    [DataRow("atomic", "250ms|22012|5s")]
    public async Task ScopesRestoreValuesHoweverTheCallbackEnds(string outcome, string expected)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(connection, "SET lock_timeout = '5s'");
        Assert.AreEqual(expected, await ScalarAsync(connection, $"SELECT datatype.settings_scope_lock_timeout('250ms', '{outcome}')"));
        Assert.AreEqual("5s", await ScalarAsync(connection, "SHOW lock_timeout"));
        Assert.AreEqual(42, await ScalarAsync(connection, "SELECT 42"));
    }

    /// <summary>
    /// An inner scope restores the outer scope's value, and the outer scope restores every setting it changed.
    /// </summary>
    [TestMethod]
    public async Task NestedScopesRestoreInOrder()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(connection, "SET lock_timeout = '5s'; SET statement_timeout = '10min'");
        Assert.AreEqual("1s,2min|3s,2min|1s,2min|5s,10min", await ScalarAsync(connection, "SELECT datatype.settings_scope_nested()"));
    }

    /// <summary>
    /// A plain <c>SET</c> inside a scope outlasts it, as it does inside a function with a <c>SET</c> clause.
    /// </summary>
    [TestMethod]
    public async Task PlainSetInsideAScopeOutlastsIt()
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(connection, "SET work_mem = '4MB'");
        Assert.AreEqual("7MB", await ScalarAsync(connection, "SELECT datatype.settings_scope_plain_set()"));
        Assert.AreEqual("7MB", await ScalarAsync(connection, "SHOW work_mem"));
    }

    /// <summary>
    /// PostgreSQL rejects an unknown setting, an invalid value, or a value the current user may not set before the callback
    /// runs, and the transaction remains usable.
    /// </summary>
    /// <param name="name">The setting's name.</param>
    /// <param name="value">The value.</param>
    /// <param name="superuser">Whether the call runs as the superuser.</param>
    /// <param name="expected">Whether the callback ran, the SQLSTATE, and the value of a later query.</param>
    [TestMethod]
    [DataRow("no_such_setting", "1", true, "skipped|42704|5s")]
    [DataRow("lock_timeout", "soon", true, "skipped|22023|5s")]
    [DataRow("log_min_duration_statement", "1s", false, "skipped|42501|5s")]
    [DataRow("log_min_duration_statement", "1s", true, "ran|none|5s")]
    public async Task RejectedSettingsSkipTheCallback(string name, string value, bool superuser, string expected)
    {
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(context.CancellationToken);
        await ExecuteAsync(connection, "SET lock_timeout = '5s'; BEGIN");
        if (!superuser)
        {
            await ExecuteAsync(connection, "CREATE ROLE ankus_settings_scope_user; GRANT USAGE ON SCHEMA datatype TO ankus_settings_scope_user; " +
                "SET LOCAL ROLE ankus_settings_scope_user");
        }

        Assert.AreEqual(expected, await ScalarAsync(connection, $"SELECT datatype.settings_scope_rejected('{name}', '{value}')"));
        Assert.AreEqual(42, await ScalarAsync(connection, "SELECT 42"));
        await ExecuteAsync(connection, "ROLLBACK");
    }

    private async Task ExecuteAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }

    private async Task<object?> ScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(context.CancellationToken);
    }
}
