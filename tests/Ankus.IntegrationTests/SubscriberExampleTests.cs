using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Runs the subscriber sample between two databases of one cluster: a provider with a publication and a subscriber
/// whose background worker copies, streams and applies the provider's changes with the logical replication toolkit.
/// </summary>
/// <param name="context">The current cancellation and diagnostic context.</param>
[TestClass]
public sealed class SubscriberExampleTests(TestContext context)
{
    private static readonly TimeSpan s_deadline = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The worker copies the published tables, then streams inserts, an update that leaves a TOAST value unchanged, a
    /// delete and a truncate. Local columns keep their defaults, applied commits carry the subscription's origin and the
    /// provider's commit times, and the origin's progress follows the stream. An apply error rolls back, the worker
    /// restarts and resumes once the error is fixed, and unsubscribing removes the slot and the origin.
    /// </summary>
    [TestMethod]
    public async Task SubscriberCopiesAndStreamsChanges()
    {
        CancellationToken token = context.CancellationToken;
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(token);
        PostgresTestClusterOptions options = new()
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            PostgreSqlConfiguration = [.. defaults.PostgreSqlConfiguration, "wal_level = logical", "max_replication_slots = 8",
                "max_wal_senders = 8", "max_worker_processes = 16", "track_commit_timestamp = on"],
        };
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using NpgsqlConnection subscriber = await cluster.OpenConnectionAsync(token);
        await ExecuteAsync(subscriber, "CREATE DATABASE provider", token);
        var builder = new NpgsqlConnectionStringBuilder(subscriber.ConnectionString) { Database = "provider" };
        await using NpgsqlConnection provider = new(builder.ConnectionString);
        await provider.OpenAsync(token);

        await ExecuteAsync(provider, """
            CREATE TABLE items (id integer PRIMARY KEY, name text, body text);
            CREATE TABLE notes (id integer PRIMARY KEY, note text);
            INSERT INTO items VALUES (1, 'one', repeat('x', 100000)), (2, 'two', 'short');
            INSERT INTO notes VALUES (1, 'café 😀');
            CREATE PUBLICATION shop FOR TABLE items, notes;
            """, token);
        await ExecuteAsync(subscriber, """
            CREATE EXTENSION ankus_subscriber;
            CREATE TABLE items (id integer PRIMARY KEY, name text, body text, local text DEFAULT 'here');
            CREATE TABLE notes (id integer PRIMARY KEY, note text);
            """, token);
        string providerString = $"host={Quote(builder.Host!)} port={builder.Port} dbname=provider user={Quote(builder.Username!)}" +
            (builder.Password is { Length: > 0 } password ? " password=" + Quote(password) : "");
        await using (NpgsqlCommand subscribe = new("SELECT subscribe('shop', @provider, 'shop')", subscriber))
        {
            subscribe.Parameters.AddWithValue("provider", providerString);
            await subscribe.ExecuteNonQueryAsync(token);
        }

        const string Items = "SELECT string_agg(id || ':' || name || ':' || length(body) || ':' || local, ';' ORDER BY id) FROM items";
        await WaitForAsync(subscriber, Items, "1:one:100000:here;2:two:5:here", token);
        Assert.AreEqual("1:café 😀", await ScalarAsync<string>(subscriber, "SELECT string_agg(id || ':' || note, ';') FROM notes", token));

        await ExecuteAsync(provider, "INSERT INTO items VALUES (3, 'three', 'body')", token);
        await ExecuteAsync(provider, "UPDATE items SET name = 'uno' WHERE id = 1", token);
        await ExecuteAsync(provider, "DELETE FROM items WHERE id = 2", token);
        await WaitForAsync(subscriber, Items, "1:uno:100000:here;3:three:4:here", token);
        Assert.AreEqual(
            await ScalarAsync<string>(provider, "SELECT pg_xact_commit_timestamp(xmin)::text FROM items WHERE id = 3", token),
            await ScalarAsync<string>(subscriber, "SELECT pg_xact_commit_timestamp(xmin)::text FROM items WHERE id = 3", token));
        int origin = await ScalarAsync<int>(subscriber,
            "SELECT roident::integer FROM pg_replication_origin WHERE roname = 'ankus_subscriber_shop'", token);
        if (await ScalarAsync<int>(subscriber, "SELECT current_setting('server_version_num')::integer", token) >= 140000)
        {
            Assert.AreEqual(origin, await ScalarAsync<int>(subscriber,
                "SELECT roident::integer FROM pg_xact_commit_timestamp_origin((SELECT xmin FROM items WHERE id = 3))", token));
        }

        Assert.IsTrue(await ScalarAsync<bool>(subscriber,
            $"SELECT remote_lsn <= '{await ScalarAsync<string>(provider, "SELECT pg_current_wal_lsn()::text", token)}'::pg_lsn AND remote_lsn > '0/0' " +
            "FROM pg_replication_origin_status WHERE external_id = 'ankus_subscriber_shop'", token));

        // A row the subscriber rejects rolls back its transaction and stops the worker, which PostgreSQL restarts and
        // which records its new process ID. After the fix, the restarted worker applies the transaction.
        int worker = await ScalarAsync<int>(subscriber, "SELECT pid FROM subscription", token);
        await ExecuteAsync(subscriber, "ALTER TABLE items ADD CONSTRAINT short_names CHECK (length(name) < 6)", token);
        await ExecuteAsync(provider, "INSERT INTO items VALUES (4, 'too long', 'body'); TRUNCATE notes", token);
        await WaitForAsync(subscriber, $"SELECT pid <> {worker} FROM subscription", true, token);
        Assert.AreEqual(0L, await ScalarAsync<long>(subscriber, "SELECT count(*) FROM items WHERE id = 4", token));
        Assert.AreEqual(1L, await ScalarAsync<long>(subscriber, "SELECT count(*) FROM notes", token));
        await ExecuteAsync(subscriber, "ALTER TABLE items DROP CONSTRAINT short_names", token);
        await WaitForAsync(subscriber, Items, "1:uno:100000:here;3:three:4:here;4:too long:4:here", token);
        await WaitForAsync(subscriber, "SELECT count(*) FROM notes", 0L, token);

        await ExecuteAsync(subscriber, "SELECT unsubscribe('shop')", token);
        Assert.AreEqual(0L, await ScalarAsync<long>(provider, "SELECT count(*) FROM pg_replication_slots", token));
        Assert.AreEqual(0L, await ScalarAsync<long>(subscriber, "SELECT count(*) FROM pg_replication_origin", token));
        Assert.AreEqual(0L, await ScalarAsync<long>(subscriber, "SELECT count(*) FROM subscription", token));
        await WaitForAsync(subscriber, "SELECT count(*) FROM pg_stat_activity WHERE backend_type = 'ankus subscriber'", 0L, token);
    }

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

            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail($"Waited {s_deadline} for {expected} from \"{sql}\"; the last result was {value}.");
            }

            await Task.Delay(100, token);
        }
    }

    private static string Quote(string value) => "'" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("'", "\\'", StringComparison.Ordinal) + "'";

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
