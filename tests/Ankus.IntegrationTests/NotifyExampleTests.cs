using Ankus.Examples.Notify;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

/// <summary>
/// Executes the ported pgrx notify sample through its published Native AOT extension with separate listening sessions.
/// </summary>
/// <param name="context">The current cancellation and diagnostic context.</param>
/// <remarks>
/// Each test owns a database because notifications are scoped to one database. A listener proves that nothing else is
/// pending by waiting for a sentinel committed after the operation under test: PostgreSQL delivers notifications from
/// different transactions in commit order.
/// </remarks>
[TestClass]
public sealed class NotifyExampleTests(TestContext context)
{
    /// <summary>
    /// The channel used only to mark the end of the notifications under test.
    /// </summary>
    private const string Sentinel = "sentinel";

    /// <summary>
    /// Bounds each wait for a notification that PostgreSQL must deliver.
    /// </summary>
    private static readonly TimeSpan s_deliveryTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Owns the test publication that installs the sample's ported pgrx backend tests.
    /// </summary>
    private static readonly SampleBackendFixture s_backend = new("Ankus.Examples.Notify");

    /// <summary>
    /// Gets the sample's generated backend test cases.
    /// </summary>
    public static IEnumerable<TestDataRow<PgTestCase>> BackendCases
        => NotifyTests.PostgresTests.Cases.Select(static test => new TestDataRow<PgTestCase>(test)
        {
            DisplayName = test.Name,
            IgnoreMessage = test.IgnoreReason,
        });

    /// <summary>
    /// Runs pgrx's NUL-rejection and payload-limit tests, and the transaction-state guard, inside PostgreSQL.
    /// </summary>
    /// <param name="test">The generated backend test case.</param>
    [TestMethod]
    [DynamicData(nameof(BackendCases))]
    public async Task NotifySampleBackendTestsPass(PgTestCase test)
    {
        PostgresExtensionTest extension = await s_backend.GetAsync(context.CancellationToken);
        await extension.RunTestAsync(test, context.CancellationToken);
    }

    /// <summary>
    /// Stops the backend-test cluster after the class finishes.
    /// </summary>
    /// <returns>A task that completes after shutdown.</returns>
    [ClassCleanup]
    public static async Task CleanupAsync() => await s_backend.DisposeAsync();

    /// <summary>
    /// Mirrors pgrx's delivery test and README: each committed product change notifies its ID from the writer's backend.
    /// </summary>
    [TestMethod]
    public Task NotifySampleDeliversProductInvalidationsAtCommit() => RunInstalledAsync(async (database, token) =>
    {
        await using NpgsqlConnection listenerConnection = await database.OpenAsync(token);
        Listener listener = await Listener.StartAsync(listenerConnection, ["cache_invalidation", Sentinel], token);
        await using NpgsqlConnection writer = await database.OpenAsync(token);
        await using NpgsqlConnection other = await database.OpenAsync(token);

        long id = await ScalarAsync<long>(writer, "INSERT INTO products (name) VALUES ('widget') RETURNING id", token);
        await ExecuteAsync(writer, "UPDATE products SET name = 'gadget' WHERE id = $1", token, id);
        await ExecuteAsync(writer, "DELETE FROM products WHERE id = $1", token, id);
        string expected = $"cache_invalidation:{id}";
        Assert.AreSequenceEqual([expected, expected, expected], await listener.DrainAsync(other, token));
        Assert.IsTrue(listener.Senders("cache_invalidation").All(pid => pid == writer.ProcessID));

        // One transaction notifies at commit only, and PostgreSQL collapses its duplicate notifications.
        await ExecuteAsync(writer, "BEGIN", token);
        long first = await ScalarAsync<long>(writer, "INSERT INTO products (name) VALUES ('first') RETURNING id", token);
        long second = await ScalarAsync<long>(writer, "INSERT INTO products (name) VALUES ('second') RETURNING id", token);
        await ExecuteAsync(writer, "UPDATE products SET name = upper(name)", token);
        Assert.IsEmpty(await listener.DrainAsync(other, token));
        await ExecuteAsync(writer, "COMMIT", token);
        Assert.AreSequenceEqual([$"cache_invalidation:{first}", $"cache_invalidation:{second}"], await listener.DrainAsync(other, token));

        // A rolled-back change, including one that failed, notifies nobody.
        await ExecuteAsync(writer, "BEGIN; INSERT INTO products (name) VALUES ('discarded')", token);
        await ExecuteAsync(writer, "ROLLBACK", token);
        await ExecuteAsync(writer, "BEGIN; INSERT INTO products (name) VALUES ('failed')", token);
        await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(writer, "SELECT 1 / 0", token));
        await ExecuteAsync(writer, "ROLLBACK", token);
        Assert.IsEmpty(await listener.DrainAsync(other, token));

        long recovered = await ScalarAsync<long>(writer, "INSERT INTO products (name) VALUES ('recovered') RETURNING id", token);
        Assert.AreSequenceEqual([$"cache_invalidation:{recovered}"], await listener.DrainAsync(other, token));
    });

    /// <summary>
    /// Mirrors pgrx's coalescing test and README: one notification per changed category, sent at commit.
    /// </summary>
    [TestMethod]
    public Task NotifySampleCoalescesCategoryInvalidations() => RunInstalledAsync(async (database, token) =>
    {
        await using NpgsqlConnection listenerConnection = await database.OpenAsync(token);
        Listener listener = await Listener.StartAsync(listenerConnection, ["category_invalidation", Sentinel], token);
        await using NpgsqlConnection writer = await database.OpenAsync(token);
        await using NpgsqlConnection other = await database.OpenAsync(token);

        // 500 rows across two categories in one autocommitted statement.
        await ExecuteAsync(writer, "INSERT INTO inventory (sku, category) SELECT 'sku' || g, g % 2 FROM generate_series(1, 500) g", token);
        Assert.AreSequenceEqual(["category_invalidation:0", "category_invalidation:1"], await listener.DrainAsync(other, token));
        Assert.IsTrue(listener.Senders("category_invalidation").All(pid => pid == writer.ProcessID));

        // Moving rows dirties both categories; deleting dirties the old one.
        await ExecuteAsync(writer, "UPDATE inventory SET category = 5 WHERE category = 1", token);
        Assert.AreSequenceEqual(["category_invalidation:1", "category_invalidation:5"], await listener.DrainAsync(other, token));
        await ExecuteAsync(writer, "DELETE FROM inventory WHERE category = 0", token);
        Assert.AreSequenceEqual(["category_invalidation:0"], await listener.DrainAsync(other, token));

        // Several statements in one transaction notify once per category, and only after commit.
        await ExecuteAsync(writer, "BEGIN", token);
        await ExecuteAsync(writer, "INSERT INTO inventory (sku, category) VALUES ('a', 10), ('b', 9)", token);
        await ExecuteAsync(writer, "INSERT INTO inventory (sku, category) VALUES ('c', 9)", token);
        await ExecuteAsync(writer, "UPDATE inventory SET sku = sku || '!' WHERE category = 10", token);
        Assert.IsEmpty(await listener.DrainAsync(other, token));
        await ExecuteAsync(writer, "COMMIT", token);
        Assert.AreSequenceEqual(["category_invalidation:9", "category_invalidation:10"], await listener.DrainAsync(other, token));

        // Rolled-back and failed transactions discard their categories, and the next transaction starts clean.
        await ExecuteAsync(writer, "BEGIN; INSERT INTO inventory (sku, category) VALUES ('rolled back', 7)", token);
        await ExecuteAsync(writer, "ROLLBACK", token);
        await ExecuteAsync(writer, "BEGIN; INSERT INTO inventory (sku, category) VALUES ('failed', 11)", token);
        await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(writer, "SELECT 1 / 0", token));
        await ExecuteAsync(writer, "ROLLBACK", token);
        await ExecuteAsync(writer, "INSERT INTO inventory (sku, category) VALUES ('kept', 8)", token);
        Assert.AreSequenceEqual(["category_invalidation:8"], await listener.DrainAsync(other, token));

        // Like pgrx, a category recorded in a rolled-back savepoint is still notified when the transaction commits.
        await ExecuteAsync(writer, "BEGIN; SAVEPOINT s; INSERT INTO inventory (sku, category) VALUES ('savepoint', 13)", token);
        await ExecuteAsync(writer, "ROLLBACK TO SAVEPOINT s; INSERT INTO inventory (sku, category) VALUES ('kept', 14)", token);
        await ExecuteAsync(writer, "COMMIT", token);
        Assert.AreSequenceEqual(["category_invalidation:13", "category_invalidation:14"], await listener.DrainAsync(other, token));
        // 250 moved rows remain after deleting category 0, plus five committed single-row inserts.
        Assert.AreEqual(255L, await ScalarAsync<long>(writer, "SELECT count(*) FROM inventory", token));
    });

    /// <summary>
    /// The SQL wrappers change this session's subscriptions at commit, use exact channel names and can be rolled back.
    /// </summary>
    [TestMethod]
    public Task NotifySampleListensAndUnlistensThroughWrappers() => RunInstalledAsync(async (database, token) =>
    {
        await using NpgsqlConnection listenerConnection = await database.OpenAsync(token);
        Listener listener = await Listener.StartAsync(listenerConnection, [Sentinel], token);
        await using NpgsqlConnection writer = await database.OpenAsync(token);

        await ExecuteAsync(listenerConnection, "SELECT ankus_listen('alpha'), ankus_listen('MixedCase')", token);
        Assert.AreEqual("MixedCase,alpha,sentinel", await ChannelsAsync(listenerConnection, token));
        await ExecuteAsync(writer, "SELECT ankus_notify('alpha', 'one'), ankus_notify('MixedCase', 'two')", token);
        await ExecuteAsync(writer, "NOTIFY MixedCase, 'folded'", token);
        Assert.AreSequenceEqual(["alpha:one", "MixedCase:two"], await listener.DrainAsync(writer, token));

        await ExecuteAsync(listenerConnection, "SELECT ankus_unlisten('alpha')", token);
        Assert.AreEqual("MixedCase,sentinel", await ChannelsAsync(listenerConnection, token));
        await ExecuteAsync(writer, "SELECT ankus_notify('alpha', 'ignored')", token);
        Assert.IsEmpty(await listener.DrainAsync(writer, token));

        // Subscriptions belong to the transaction: a rollback discards them, and they appear only after commit.
        await ExecuteAsync(listenerConnection, "BEGIN; SELECT ankus_listen('gamma')", token);
        Assert.AreEqual("MixedCase,sentinel", await ChannelsAsync(listenerConnection, token));
        await ExecuteAsync(listenerConnection, "ROLLBACK", token);
        await ExecuteAsync(listenerConnection, "BEGIN; SELECT ankus_listen('delta'); SELECT ankus_unlisten('MixedCase')", token);
        await ExecuteAsync(listenerConnection, "COMMIT", token);
        Assert.AreEqual("delta,sentinel", await ChannelsAsync(listenerConnection, token));

        await ExecuteAsync(listenerConnection, "SELECT ankus_unlisten_all()", token);
        Assert.AreEqual("", await ChannelsAsync(listenerConnection, token));
        await ExecuteAsync(writer, "SELECT ankus_notify('delta', 'ignored')", token);
        await ExecuteAsync(listenerConnection, "SELECT ankus_listen('sentinel')", token);
        Assert.IsEmpty(await listener.DrainAsync(writer, token));
        Assert.AreEqual(1, await ScalarAsync<int>(listenerConnection, "SELECT 1", token));
    });

    /// <summary>
    /// PostgreSQL's channel and payload limits surface as SQLSTATE 22023 at the exact byte boundaries, and a failed
    /// call leaves earlier notifications of a recovered transaction intact.
    /// </summary>
    [TestMethod]
    public Task NotifySampleRejectsInvalidArgumentsAndRecovers() => RunInstalledAsync(async (database, token) =>
    {
        await using NpgsqlConnection listenerConnection = await database.OpenAsync(token);
        Listener listener = await Listener.StartAsync(listenerConnection, ["limits", new string('c', 63), Sentinel], token);
        await using NpgsqlConnection writer = await database.OpenAsync(token);

        foreach ((string call, string message) in new[]
        {
            ("ankus_notify('', 'x')", "channel name cannot be empty"),
            ("ankus_notify(repeat('c', 64), 'x')", "channel name too long"),
            ("ankus_notify('limits', repeat('x', 8000))", "payload string too long"),
            ("ankus_notify('limits', repeat('é', 4000))", "payload string too long"),
            ("ankus_notify('limits', repeat('x', 9000))", "payload string too long"),
        })
        {
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(writer, "SELECT " + call, token));
            Assert.AreEqual("22023", error.SqlState);
            Assert.AreEqual(message, error.MessageText);
        }

        string multibyte = new string('é', 3999) + "x";
        await ExecuteAsync(writer, "SELECT ankus_notify(repeat('c', 63), 'boundary'), ankus_notify('limits', repeat('x', 7999))", token);
        await ExecuteAsync(writer, "SELECT ankus_notify('limits', $1)", token, multibyte);
        Assert.AreSequenceEqual([$"{new string('c', 63)}:boundary", "limits:" + new string('x', 7999), "limits:" + multibyte],
            await listener.DrainAsync(writer, token));

        // The failure rolls back only its savepoint; notifications before and after it are delivered at commit.
        await ExecuteAsync(writer, "BEGIN; SELECT ankus_notify('limits', 'before'); SAVEPOINT s", token);
        await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(writer, "SELECT ankus_notify('limits', repeat('x', 8000))", token));
        await ExecuteAsync(writer, "ROLLBACK TO SAVEPOINT s; SELECT ankus_notify('limits', 'after')", token);
        await ExecuteAsync(writer, "COMMIT", token);
        Assert.AreSequenceEqual(["limits:before", "limits:after"], await listener.DrainAsync(writer, token));

        // A failed transaction discards its earlier notification; the same session then notifies normally.
        await ExecuteAsync(writer, "BEGIN; SELECT ankus_notify('limits', 'discarded')", token);
        await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(writer, "SELECT ankus_notify('', 'x')", token));
        await ExecuteAsync(writer, "ROLLBACK", token);
        await ExecuteAsync(writer, "SELECT ankus_notify('limits', 'recovered')", token);
        Assert.AreSequenceEqual(["limits:recovered"], await listener.DrainAsync(writer, token));
        Assert.IsTrue(await ScalarAsync<bool>(writer, "SELECT ankus_notify(NULL, 'x') IS NULL AND ankus_listen(NULL) IS NULL", token));
    });

    /// <summary>
    /// Channel names and payloads are converted to a LATIN1 database's encoding, where byte limits count one byte per
    /// accented letter, and are delivered intact to a UTF-8 client.
    /// </summary>
    [TestMethod]
    public Task NotifySampleConvertsTextToTheDatabaseEncoding() => RunInstalledAsync(async (database, token) =>
    {
        await using NpgsqlConnection listenerConnection = await database.OpenAsync(token);
        Listener listener = await Listener.StartAsync(listenerConnection, [Sentinel], token);
        await using NpgsqlConnection writer = await database.OpenAsync(token);
        Assert.AreEqual("LATIN1", await ScalarAsync<string>(writer, "SELECT pg_encoding_to_char(encoding) FROM pg_database WHERE datname = current_database()", token));

        await ExecuteAsync(listenerConnection, "SELECT ankus_listen('café')", token);
        Assert.AreEqual("café,sentinel", await ChannelsAsync(listenerConnection, token));
        string payload = new('é', 7999);
        await ExecuteAsync(writer, "SELECT ankus_notify('café', 'naïve'), ankus_notify('café', $1)", token, payload);
        PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecuteAsync(writer, "SELECT ankus_notify('café', repeat('é', 8000))", token));
        Assert.AreEqual("payload string too long", error.MessageText);
        Assert.AreSequenceEqual(["café:naïve", "café:" + payload], await listener.DrainAsync(writer, token));
    }, "LATIN1");

    /// <summary>
    /// The coalescing trigger refuses PREPARE TRANSACTION, as PostgreSQL does after NOTIFY, so no category is orphaned.
    /// </summary>
    [TestMethod]
    public async Task NotifySampleRejectsPreparedInvalidations()
    {
        CancellationToken token = context.CancellationToken;
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(token);
        PostgresTestClusterOptions options = new()
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            PostgreSqlConfiguration = [.. defaults.PostgreSqlConfiguration, "max_prepared_transactions = 2"],
        };
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using NpgsqlConnection writer = await cluster.OpenConnectionAsync(token);
        await ExecuteAsync(writer, "CREATE EXTENSION ankus_notify", token);
        await using NpgsqlConnection listenerConnection = await cluster.OpenConnectionAsync(token);
        Listener listener = await Listener.StartAsync(listenerConnection, ["category_invalidation", "cache_invalidation", Sentinel], token);

        await ExecuteAsync(writer, "BEGIN; INSERT INTO inventory (sku, category) VALUES ('prepared', 3)", token);
        PostgresException rejected = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(writer, "PREPARE TRANSACTION 'inventory'", token));
        Assert.AreEqual("0A000", rejected.SqlState);
        Assert.AreEqual("cannot PREPARE a transaction that has pending category invalidations", rejected.MessageText);

        // PostgreSQL applies its own rule to the per-row trigger's direct NOTIFY.
        await ExecuteAsync(writer, "BEGIN; INSERT INTO products (name) VALUES ('prepared')", token);
        rejected = await Assert.ThrowsExactlyAsync<PostgresException>(() => ExecuteAsync(writer, "PREPARE TRANSACTION 'products'", token));
        Assert.AreEqual("0A000", rejected.SqlState);
        Assert.AreEqual("cannot PREPARE a transaction that has executed LISTEN, UNLISTEN, or NOTIFY", rejected.MessageText);

        // The rejected categories were discarded; later transactions in the same backend notify normally.
        await ExecuteAsync(writer, "INSERT INTO inventory (sku, category) VALUES ('committed', 4)", token);
        Assert.AreSequenceEqual(["category_invalidation:4"], await listener.DrainAsync(writer, token));
        await ExecuteAsync(writer, "BEGIN; INSERT INTO inventory (sku, category) VALUES ('untouched', 4); ROLLBACK", token);
        await ExecuteAsync(writer, "BEGIN; CREATE TABLE prepared_probe (value integer)", token);
        await ExecuteAsync(writer, "PREPARE TRANSACTION 'unrelated'", token);
        await ExecuteAsync(writer, "ROLLBACK PREPARED 'unrelated'", token);
        Assert.AreEqual(1L, await ScalarAsync<long>(writer, "SELECT count(*) FROM inventory", token));
        Assert.IsEmpty(await listener.DrainAsync(writer, token));
    }

    /// <summary>
    /// Runs one test in a new database with the sample installed, then removes the database.
    /// </summary>
    private async Task RunInstalledAsync(Func<NotifyDatabase, CancellationToken, Task> test, string? encoding = null)
    {
        CancellationToken token = context.CancellationToken;
        string database = "notify_" + Guid.NewGuid().ToString("N");
        await using NpgsqlConnection administrator = await PostgresFixture.Cluster.OpenConnectionAsync(token);
        string options = encoding is null ? "" : $" ENCODING '{encoding}' LC_COLLATE 'C' LC_CTYPE 'C'";
        await ExecuteAsync(administrator, $"CREATE DATABASE {database} TEMPLATE template0{options}", token);
        try
        {
            var builder = new NpgsqlConnectionStringBuilder(administrator.ConnectionString) { Database = database, Pooling = false };
            var notifyDatabase = new NotifyDatabase(builder.ConnectionString);
            await using (NpgsqlConnection installer = await notifyDatabase.OpenAsync(token))
            {
                await ExecuteAsync(installer, "CREATE EXTENSION ankus_notify", token);
            }

            await test(notifyDatabase, token);
        }
        finally
        {
            await ExecuteAsync(administrator, $"DROP DATABASE {database} WITH (FORCE)", CancellationToken.None);
        }
    }

    /// <summary>
    /// Formats this session's committed subscriptions in channel order.
    /// </summary>
    private static Task<string> ChannelsAsync(NpgsqlConnection connection, CancellationToken token) => ScalarAsync<string>(connection,
        "SELECT coalesce(string_agg(channel, ',' ORDER BY channel COLLATE \"C\"), '') FROM pg_listening_channels() AS channel", token);

    private static async Task<T> ScalarAsync<T>(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return Assert.IsInstanceOfType<T>(await command.ExecuteScalarAsync(token));
    }

    private static async Task ExecuteAsync(NpgsqlConnection connection, string sql, CancellationToken token, params object[] parameters)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        foreach (object parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter);
        }

        await command.ExecuteNonQueryAsync(token);
    }

    /// <summary>
    /// Opens unpooled sessions on one test database.
    /// </summary>
    /// <param name="connectionString">The database's connection string.</param>
    private sealed class NotifyDatabase(string connectionString)
    {
        /// <summary>
        /// Opens a new backend on the test database.
        /// </summary>
        internal async Task<NpgsqlConnection> OpenAsync(CancellationToken token)
        {
            var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(token);
            return connection;
        }
    }

    /// <summary>
    /// Records the notifications delivered to one listening session.
    /// </summary>
    /// <param name="connection">The listening session.</param>
    private sealed class Listener(NpgsqlConnection connection)
    {
        private readonly List<NpgsqlNotificationEventArgs> _received = [];
        private readonly List<NpgsqlNotificationEventArgs> _drained = [];
        private int _sentinels;

        /// <summary>
        /// Subscribes the session with SQL <c>LISTEN</c> and starts recording its notifications.
        /// </summary>
        internal static async Task<Listener> StartAsync(NpgsqlConnection connection, string[] channels, CancellationToken token)
        {
            var listener = new Listener(connection);
            connection.Notification += (_, notification) => listener._received.Add(notification);
            foreach (string channel in channels)
            {
                await using var command = new NpgsqlCommand($"LISTEN \"{channel.Replace("\"", "\"\"", StringComparison.Ordinal)}\"", connection);
                await command.ExecuteNonQueryAsync(token);
            }

            return listener;
        }

        /// <summary>
        /// Commits a sentinel from <paramref name="sender"/> and returns every earlier notification in delivery order.
        /// </summary>
        internal async Task<string[]> DrainAsync(NpgsqlConnection sender, CancellationToken token)
        {
            string marker = (++_sentinels).ToString(System.Globalization.CultureInfo.InvariantCulture);
            await using (var command = new NpgsqlCommand("SELECT pg_notify($1, $2)", sender))
            {
                command.Parameters.AddWithValue(Sentinel);
                command.Parameters.AddWithValue(marker);
                await command.ExecuteNonQueryAsync(token);
            }

            DateTime deadline = DateTime.UtcNow + s_deliveryTimeout;
            while (!_received.Any(notification => notification.Channel == Sentinel && notification.Payload == marker))
            {
                TimeSpan remaining = deadline - DateTime.UtcNow;
                if (remaining <= TimeSpan.Zero)
                {
                    throw new TimeoutException($"Sentinel {marker} was not delivered within {s_deliveryTimeout}.");
                }

                _ = await connection.WaitAsync(remaining, token);
            }

            int end = _received.FindIndex(notification => notification.Channel == Sentinel && notification.Payload == marker);
            NpgsqlNotificationEventArgs[] delivered = [.. _received.Take(end)];
            _received.RemoveRange(0, end + 1);
            _drained.AddRange(delivered);
            return [.. delivered.Select(static notification => notification.Channel + ":" + notification.Payload)];
        }

        /// <summary>
        /// Gets the sending backends of every drained notification on <paramref name="channel"/>.
        /// </summary>
        internal IEnumerable<int> Senders(string channel)
            => _drained.Where(notification => notification.Channel == channel).Select(static notification => notification.PID);
    }
}
