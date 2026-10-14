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
    /// <c>pg_dump</c> receives arguments with spaces, quotes and, except on Windows, non-ASCII text exactly and writes
    /// the table's definition to the requested file. Windows gives a program with a narrow <c>main</c>, such as
    /// <c>pg_dump</c>, its arguments in the system's ANSI code page, which need not contain every character.
    /// </summary>
    [TestMethod]
    public async Task PgDumpReceivesExactArguments()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        string text = OperatingSystem.IsWindows() ? "" : " é";
        string table = "Odd \"Table\"" + text + " " + Guid.NewGuid().ToString("N")[..8];
        string quoted = "\"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        // pg_dump reads every object in the database it dumps, and the shared test database holds same-named types in
        // several schemas, whose casts PostgreSQL 19's assertion-enabled pg_dump cannot order. A database of its own
        // holds only the table.
        string database = "ankus_child_dump_" + Guid.NewGuid().ToString("N")[..8];
        await ExecuteAsync(connection, $"CREATE DATABASE {database}", token);
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString) { Database = database, Pooling = false };
            await using var dumped = new NpgsqlConnection(builder.ConnectionString);
            await dumped.OpenAsync(token);
            await ExecuteAsync(dumped, $"CREATE TABLE public.{quoted} (id integer, \"note é\" text)", token);
            string output = Path.Join(PostgresFixture.Cluster.DataDirectory, "ankus child dump" + text + ".sql");
            string result = await ScalarAsync<string>(connection, "SELECT datatype.child_run_and_read(@path, @arguments, @output)", token,
                ("path", Program("pg_dump")), ("output", output),
                ("arguments", new[] { "--schema-only", "--table=public." + quoted, "--file=" + output, "--dbname=" + ConnectionString(dumped) }));
            Assert.StartsWith("0|", result);
            Assert.Contains($"CREATE TABLE public.{quoted} (", result);
            Assert.Contains("\"note é\" text", result);
        }
        finally
        {
            await ExecuteAsync(connection, $"DROP DATABASE {database} WITH (FORCE)", token);
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
                ("path", Path.Join(PostgresFixture.Cluster.Installation.BinDirectory, "ankus_missing_program")), ("arguments", Array.Empty<string>())));
            Assert.AreEqual(PostgresErrorCodes.UndefinedFile, missing.SqlState);
            Assert.StartsWith("could not execute", missing.MessageText);
        }

        await using NpgsqlConnection next = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        Assert.AreEqual(1, await ScalarAsync<int>(next, "SELECT datatype.child_run(@path, @arguments)", token,
            ("path", Program("pg_dump")), ("arguments", s_badOption)));
    }

    /// <summary>
    /// A statement timeout stops a running program and fails with the usual error. Where PostgreSQL can check for a
    /// closed client connection, the program's own session disappears too.
    /// </summary>
    [TestMethod]
    public async Task CancellationStopsTheProgram()
    {
        CancellationToken token = context.CancellationToken;
        await using NpgsqlConnection connection = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        bool checksClient = await ChecksClientConnectionAsync(connection, token);
        string application = "ankus_child_" + Guid.NewGuid().ToString("N")[..8];

        // Without the check, the orphaned session runs until its sleep ends.
        string sql = checksClient ? "SET client_connection_check_interval = '100ms'; SELECT pg_sleep(60)" : "SELECT pg_sleep(10)";
        await ExecuteAsync(connection, "SET statement_timeout = '1s'", token);
        Stopwatch watch = Stopwatch.StartNew();
        PostgresException canceled = await Assert.ThrowsExactlyAsync<PostgresException>(() => ScalarAsync<int>(connection,
            "SELECT datatype.child_run(@path, @arguments)", token, ("path", Program("psql")),
            ("arguments", new[] { "--no-psqlrc", "--command=" + sql, "--dbname=" + ConnectionString(connection) + " application_name=" + application })));
        Assert.AreEqual(PostgresErrorCodes.QueryCanceled, canceled.SqlState);
        Assert.IsLessThan(TimeSpan.FromSeconds(20), watch.Elapsed);
        await ExecuteAsync(connection, "RESET statement_timeout", token);
        if (checksClient)
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

    /// <summary>
    /// Gets whether PostgreSQL can notice a closed client connection during a query: from PostgreSQL 14 on Linux, and on
    /// more platforms in later versions, but not on Windows.
    /// </summary>
    private static async Task<bool> ChecksClientConnectionAsync(NpgsqlConnection connection, CancellationToken token)
    {
        if (await ScalarAsync<int>(connection, "SELECT current_setting('server_version_num')::integer", token) < 140000)
        {
            return false;
        }

        try
        {
            await ExecuteAsync(connection, "SET client_connection_check_interval = '100ms'", token);
        }
        catch (PostgresException error) when (error.SqlState == PostgresErrorCodes.InvalidParameterValue)
        {
            return false;
        }

        await ExecuteAsync(connection, "RESET client_connection_check_interval", token);
        return true;
    }

    private static string Program(string name)
        => Path.Join(PostgresFixture.Cluster.Installation.BinDirectory, OperatingSystem.IsWindows() ? name + ".exe" : name);

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
