using System.Diagnostics;
using System.Globalization;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Runs one workload through PostgreSQL's built-in logical replication and through the subscriber sample on the same
/// cluster, and reports how fast each applies it.
/// </summary>
/// <param name="context">The current cancellation and diagnostic context.</param>
/// <remarks>
/// By default the workload is small, so the suite checks that both subscribers converge to the same rows. Set
/// <c>ANKUS_REPLICATION_BENCHMARK_ROWS</c> and <c>ANKUS_REPLICATION_BENCHMARK_BATCH</c> to measure; the timings go to
/// the test output.
/// </remarks>
[TestClass]
public sealed class ReplicationBenchmarkTests(TestContext context)
{
    private static readonly TimeSpan s_deadline = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Inserts rows in batches on the provider, then updates every row, and times how long each subscriber takes to
    /// apply the backlog.
    /// </summary>
    [TestMethod]
    public async Task SubscriberSampleMatchesBuiltInReplication()
    {
        CancellationToken token = context.CancellationToken;
        int rows = Setting("ANKUS_REPLICATION_BENCHMARK_ROWS", 2_000);
        int batch = Setting("ANKUS_REPLICATION_BENCHMARK_BATCH", 100);
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(token);
        PostgresTestClusterOptions options = new()
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            PostgreSqlConfiguration = [.. defaults.PostgreSqlConfiguration, "wal_level = logical", "max_replication_slots = 10",
                "max_wal_senders = 10", "max_worker_processes = 16"],
        };
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using NpgsqlConnection subscriber = await cluster.OpenConnectionAsync(token);
        await ExecuteAsync(subscriber, "CREATE DATABASE provider", token);
        var builder = new NpgsqlConnectionStringBuilder(subscriber.ConnectionString) { Database = "provider" };
        await using NpgsqlConnection provider = new(builder.ConnectionString);
        await provider.OpenAsync(token);
        string connection = $"host={Quote(builder.Host!)} port={builder.Port} dbname=provider user={Quote(builder.Username!)}" +
            (builder.Password is { Length: > 0 } password ? " password=" + Quote(password) : "");

        const string Table = "(id integer PRIMARY KEY, name text, amount numeric)";
        await ExecuteAsync(provider, $"""
            CREATE TABLE builtin_items {Table};
            CREATE TABLE ankus_items {Table};
            CREATE PUBLICATION builtin_pub FOR TABLE builtin_items;
            CREATE PUBLICATION ankus_pub FOR TABLE ankus_items;
            """, token);
        await ExecuteAsync(provider, "SELECT pg_create_logical_replication_slot('builtin_sub', 'pgoutput')", token);
        await ExecuteAsync(subscriber, $"""
            CREATE TABLE builtin_items {Table};
            CREATE TABLE ankus_items {Table};
            CREATE EXTENSION ankus_subscriber;
            """, token);
        // A subscription to the same cluster cannot create its slot while it runs, so the slot already exists.
        await ExecuteAsync(subscriber, $"CREATE SUBSCRIPTION builtin_sub CONNECTION {Literal(connection)} PUBLICATION builtin_pub " +
            "WITH (create_slot = false, slot_name = 'builtin_sub', copy_data = false)", token);
        await ExecuteAsync(subscriber, $"SELECT subscribe('ankus', {Literal(connection)}, 'ankus_pub')", token);
        await WaitForAsync(provider, "SELECT count(*) FROM pg_replication_slots WHERE active", 2L, token);

        await using NpgsqlConnection holder = await cluster.OpenConnectionAsync(token);
        TimeSpan builtIn = await RunAsync(provider, subscriber, holder, "builtin_items", rows, batch, token);
        TimeSpan sample = await RunAsync(provider, subscriber, holder, "ankus_items", rows, batch, token);
        context.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{rows} rows in transactions of {batch}, inserted then updated, applied from a backlog: built-in " +
            $"{builtIn.TotalSeconds:F2} s ({2d * rows / builtIn.TotalSeconds:F0} changes/s), Ankus subscriber sample " +
            $"{sample.TotalSeconds:F2} s ({2d * rows / sample.TotalSeconds:F0} changes/s)."));
        Assert.AreEqual(await ScalarAsync<string>(subscriber, "SELECT md5(string_agg(id || name || amount, ',' ORDER BY id)) FROM builtin_items", token),
            await ScalarAsync<string>(subscriber, "SELECT md5(string_agg(id || name || amount, ',' ORDER BY id)) FROM ankus_items", token));
        await ExecuteAsync(subscriber, "SELECT unsubscribe('ankus')", token);
        await ExecuteAsync(subscriber, "DROP SUBSCRIPTION builtin_sub", token);
    }

    /// <summary>
    /// Holds the subscriber's table locked while the provider inserts and then updates the rows in batches, so the
    /// changes queue up, and returns the time from releasing the lock until the subscriber's copy matches.
    /// </summary>
    private static async Task<TimeSpan> RunAsync(NpgsqlConnection provider, NpgsqlConnection subscriber, NpgsqlConnection holder,
        string table, int rows, int batch, CancellationToken token)
    {
        await using NpgsqlTransaction hold = await holder.BeginTransactionAsync(token);
        await ExecuteAsync(holder, $"LOCK TABLE {table} IN ACCESS EXCLUSIVE MODE", token);
        for (int start = 1; start <= rows; start += batch)
        {
            int end = Math.Min(rows, start + batch - 1);
            await ExecuteAsync(provider, $"INSERT INTO {table} SELECT g, 'row ' || g, g * 1.5 FROM generate_series({start}, {end}) g", token);
        }

        for (int start = 1; start <= rows; start += batch)
        {
            int end = Math.Min(rows, start + batch - 1);
            await ExecuteAsync(provider, $"UPDATE {table} SET amount = amount + 1 WHERE id BETWEEN {start} AND {end}", token);
        }

        string expected = await ScalarAsync<string>(provider, $"SELECT count(*) || ':' || sum(amount) FROM {table}", token);
        var watch = Stopwatch.StartNew();
        await hold.CommitAsync(token);
        await WaitForAsync(subscriber, $"SELECT count(*) || ':' || coalesce(sum(amount), 0) FROM {table}", expected, token);
        return watch.Elapsed;
    }

    private static int Setting(string name, int fallback)
        => int.TryParse(Environment.GetEnvironmentVariable(name), NumberStyles.None, CultureInfo.InvariantCulture, out int value) && value > 0
            ? value : fallback;

    private static async Task WaitForAsync<T>(NpgsqlConnection connection, string sql, T expected, CancellationToken token)
    {
        DateTime deadline = DateTime.UtcNow + s_deadline;
        while (true)
        {
            await using var command = new NpgsqlCommand(sql, connection);
            object? value = await command.ExecuteScalarAsync(token);
            if (value is T actual && EqualityComparer<T>.Default.Equals(actual, expected))
            {
                return;
            }

            Assert.IsLessThan(deadline, DateTime.UtcNow, $"Waited for {expected} from \"{sql}\"; the last result was {value}.");
            await Task.Delay(20, token);
        }
    }

    private static string Quote(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

    private static string Literal(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(token);
    }
}
