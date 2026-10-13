using System.Diagnostics;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Runs PostgreSQL's own client programs from a backend through <c>PgChildProcess</c>, as pglogical runs
/// <c>pg_dump</c> and <c>pg_restore</c>.
/// </summary>
/// <param name="context">The current cancellation and diagnostic context.</param>
[TestClass]
public sealed class ChildProcessTests(TestContext context)
{
    private static readonly string[] s_badOption = ["--no-such-option"];

    /// <summary>
    /// <c>pg_dump</c> receives arguments with spaces, quotes and non-ASCII text exactly and writes the table's
    /// definition to the requested file.
    /// </summary>
    [TestMethod]
    public async Task PgDumpReceivesExactArguments()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        string table = "Odd \"Table\" é " + Guid.NewGuid().ToString("N")[..8];
        string quoted = "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        await ExecuteAsync(connection, $"CREATE TABLE tests.{quoted} (id integer, \"note é\" text)", token);
        try
        {
            string output = Path.Combine(PostgresFixture.Cluster.DataDirectory, "ankus child dump é.sql");
            string result = await ScalarAsync<string>(connection, "SELECT datatype.child_run_and_read(@path, @arguments, @output)", token,
                ("path", Program("pg_dump")), ("output", output),
                ("arguments", new[] { "--schema-only", "--table=tests." + quoted, "--file=" + output, "--dbname=" + ConnectionString(connection) }));
            Assert.StartsWith("0|", result);
            Assert.Contains($"CREATE TABLE tests.{quoted} (", result);
            Assert.Contains("\"note é\" text", result);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP TABLE tests.{quoted}", token);
        }
    }

    /// <summary>
    /// A program's failing exit code is returned, and a program that cannot be started raises PostgreSQL's file
    /// access error. Npgsql closes a connection after a class 58 error, so a new session checks the server afterward.
    /// </summary>
    [TestMethod]
    public async Task ExitCodesAndStartFailuresAreReported()
    {
        CancellationToken token = context.CancellationToken;
        await using (NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token))
        {
            Assert.AreEqual(1, await ScalarAsync<int>(connection, "SELECT datatype.child_run(@path, @arguments)", token,
                ("path", Program("pg_dump")), ("arguments", s_badOption)));
            PostgresException missing = await Assert.ThrowsExactlyAsync<PostgresException>(() => ScalarAsync<int>(connection,
                "SELECT datatype.child_run(@path, @arguments)", token,
                ("path", Path.Combine(PostgresFixture.Cluster.Installation.BinDirectory, "ankus_missing_program")), ("arguments", Array.Empty<string>())));
            Assert.AreEqual(PostgresErrorCodes.UndefinedFile, missing.SqlState);
            Assert.StartsWith("could not execute", missing.MessageText);
        }

        await using NpgsqlConnection next = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        Assert.AreEqual(1, await ScalarAsync<int>(next, "SELECT datatype.child_run(@path, @arguments)", token,
            ("path", Program("pg_dump")), ("arguments", s_badOption)));
    }

    /// <summary>
    /// A statement timeout stops a running program and fails with the usual error. From PostgreSQL 14 the program's
    /// own session, which notices its client is gone, disappears too.
    /// </summary>
    [TestMethod]
    public async Task CancellationStopsTheProgram()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        int version = await ScalarAsync<int>(connection, "SELECT current_setting('server_version_num')::integer", token);
        string application = "ankus_child_" + Guid.NewGuid().ToString("N")[..8];
        string sql = (version >= 140000 ? "SET client_connection_check_interval = '100ms'; " : "") + "SELECT pg_sleep(60)";
        await ExecuteAsync(connection, "SET statement_timeout = '1s'", token);
        Stopwatch watch = Stopwatch.StartNew();
        PostgresException canceled = await Assert.ThrowsExactlyAsync<PostgresException>(() => ScalarAsync<int>(connection,
            "SELECT datatype.child_run(@path, @arguments)", token, ("path", Program("psql")),
            ("arguments", new[] { "--no-psqlrc", "--command=" + sql, "--dbname=" + ConnectionString(connection) + " application_name=" + application })));
        Assert.AreEqual(PostgresErrorCodes.QueryCanceled, canceled.SqlState);
        Assert.IsLessThan(TimeSpan.FromSeconds(20), watch.Elapsed);
        await ExecuteAsync(connection, "RESET statement_timeout", token);
        if (version >= 140000)
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(20);
            while (await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name", token,
                ("name", application)) != 0)
            {
                Assert.IsLessThan(deadline, DateTime.UtcNow, "The stopped program's session stayed open.");
                await Task.Delay(100, token);
            }
        }
    }

    private static string Program(string name)
        => Path.Combine(PostgresFixture.Cluster.Installation.BinDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);

    private static string ConnectionString(NpgsqlConnection connection)
    {
        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString);
        string text = $"host={Quote(builder.Host!)} port={builder.Port} dbname={Quote(builder.Database!)} user={Quote(builder.Username!)}";
        return builder.Password is { Length: > 0 } password ? text + " password=" + Quote(password) : text;
    }

    private static string Quote(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken token, params (string Name, object Value)[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }
}
