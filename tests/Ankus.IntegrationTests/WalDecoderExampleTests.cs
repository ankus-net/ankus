using System.Text;
using System.Text.Json;
using Ankus.Examples.WalDecoder;
using Ankus.Testing;
using Npgsql;
using Npgsql.Replication;
using Npgsql.Replication.Internal;
using NpgsqlTypes;

namespace Ankus.IntegrationTests;

/// <summary>
/// Decodes committed changes through the ported pgrx wal_decoder output plugin in a cluster with logical WAL.
/// </summary>
/// <param name="context">The current cancellation and diagnostic context.</param>
/// <remarks>
/// PostgreSQL loads the published Native AOT library by name for each decoding context, in ordinary backends for the SQL
/// slot functions and in a WAL sender for the replication protocol. Commit timestamps come from
/// <c>track_commit_timestamp</c>, an independent record of the time the plugin reports.
/// </remarks>
[TestClass]
public sealed class WalDecoderExampleTests(TestContext context)
{
    /// <summary>
    /// The output plugin's name: the published library's file name without its suffix.
    /// </summary>
    private const string Plugin = "Ankus.Examples.WalDecoder";

    /// <summary>
    /// Owns the test publication that installs the sample's ported pgrx backend tests.
    /// </summary>
    private static readonly SampleBackendFixture s_backend = new("Ankus.Examples.WalDecoder");

    /// <summary>
    /// Gets the sample's generated backend test cases.
    /// </summary>
    public static IEnumerable<TestDataRow<PgTestCase>> BackendCases
        => WalDecoderTests.PostgresTests.Cases.Select(static test => new TestDataRow<PgTestCase>(test)
        {
            DisplayName = test.Name,
            IgnoreMessage = test.IgnoreReason,
        });

    /// <summary>
    /// Runs pgrx's action serialization test and the remaining JSON shapes inside PostgreSQL.
    /// </summary>
    /// <param name="test">The generated backend test case.</param>
    [TestMethod]
    [DynamicData(nameof(BackendCases))]
    public async Task WalDecoderSampleBackendTestsPass(PgTestCase test)
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
    /// Reproduces the README: inserts, an update with REPLICA IDENTITY FULL and a delete decode to pgrx's JSON, with
    /// the transaction's ID, commit time and change count.
    /// </summary>
    [TestMethod]
    public Task WalDecoderSampleReproducesReadme() => RunAsync(async (connection, token) =>
    {
        await ExecuteAsync(connection, """
            CREATE EXTENSION ankus_wal_decoder;
            CREATE TABLE person (name TEXT, age INT);
            ALTER TABLE person REPLICA IDENTITY FULL;
            CREATE PUBLICATION gotham_pub FOR TABLE person;
            """, token);
        Assert.AreEqual(0L, await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_depend WHERE refobjid = (SELECT oid FROM pg_extension WHERE extname = 'ankus_wal_decoder') AND deptype = 'e'", token));
        if (await ScalarAsync<bool>(connection, "SELECT EXISTS (SELECT FROM pg_settings WHERE name = 'output_plugin_libraries')", token))
        {
            // Without the DBA's trust, PostgreSQL refuses to load the library, even for a superuser.
            await ExecuteAsync(connection, "SET output_plugin_libraries = 'pgoutput, test_decoding'", token);
            PostgresException untrusted = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ExecuteAsync(connection, $"SELECT pg_create_logical_replication_slot('untrusted', '{Plugin}')", token));
            Assert.AreEqual("42501", untrusted.SqlState);
            Assert.AreEqual($"library \"{Plugin}\" may not be used as an output plugin", untrusted.MessageText);
            await ExecuteAsync(connection, "RESET output_plugin_libraries", token);
            Assert.AreEqual(0L, await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_replication_slots", token));
        }

        await ExecuteAsync(connection, $"SELECT pg_create_logical_replication_slot('gotham_slot', '{Plugin}')", token);

        long insertXid = await CommitAsync(connection, "INSERT INTO person VALUES ('Bruce Wayne', 42), ('Clark Kent', 33)", token);
        string insertCommit = await CommitTimeAsync(connection, insertXid, token);
        Assert.AreSequenceEqual(
        [
            $"{insertXid}|{{\"typ\":\"BEGIN\"}}",
            $"{insertXid}|{{\"typ\":\"INSERT\",\"rel\":\"public.person\",\"new\":{{\"name\":\"Bruce Wayne\",\"age\":42}}}}",
            $"{insertXid}|{{\"typ\":\"INSERT\",\"rel\":\"public.person\",\"new\":{{\"name\":\"Clark Kent\",\"age\":33}}}}",
            $"{insertXid}|{{\"typ\":\"COMMIT\",\"committed\":{insertCommit},\"change_count\":2}}",
        ], await ChangesAsync(connection, "gotham_slot", token));

        long updateXid = await CommitAsync(connection, "UPDATE person SET name = 'Batman' WHERE name = 'Bruce Wayne'", token);
        long deleteXid = await CommitAsync(connection, "DELETE FROM person WHERE age = 33", token);
        Assert.AreSequenceEqual(
        [
            $"{updateXid}|{{\"typ\":\"BEGIN\"}}",
            $"{updateXid}|{{\"typ\":\"UPDATE\",\"rel\":\"public.person\",\"old\":{{\"name\":\"Bruce Wayne\",\"age\":42}},\"new\":{{\"name\":\"Batman\",\"age\":42}}}}",
            $"{updateXid}|{{\"typ\":\"COMMIT\",\"committed\":{await CommitTimeAsync(connection, updateXid, token)},\"change_count\":1}}",
            $"{deleteXid}|{{\"typ\":\"BEGIN\"}}",
            $"{deleteXid}|{{\"typ\":\"DELETE\",\"rel\":\"public.person\",\"old\":{{\"name\":\"Clark Kent\",\"age\":33}}}}",
            $"{deleteXid}|{{\"typ\":\"COMMIT\",\"committed\":{await CommitTimeAsync(connection, deleteXid, token)},\"change_count\":1}}",
        ], await ChangesAsync(connection, "gotham_slot", token));

        // The README's jsonb_pretty query parses every document.
        await CommitAsync(connection, "INSERT INTO person VALUES ('Diana Prince', 30)", token);
        Assert.AreEqual(3L, await ScalarAsync<long>(connection,
            "SELECT count(jsonb_pretty(data::jsonb)) FROM pg_logical_slot_get_changes('gotham_slot', NULL, NULL)", token));
        Assert.AreEqual(0L, await ScalarAsync<long>(connection, "SELECT count(*) FROM pg_logical_slot_get_changes('gotham_slot', NULL, NULL)", token));
    });

    /// <summary>
    /// Replica identities, NULLs, dropped columns, quoted names, Unicode, TOAST and rollbacks follow pgrx's rules.
    /// </summary>
    [TestMethod]
    public Task WalDecoderSampleDecodesRowImages() => RunAsync(async (connection, token) =>
    {
        await ExecuteAsync(connection, """"
            CREATE TABLE keyed (id int PRIMARY KEY, note text, extra int);
            CREATE TABLE unkeyed (note text);
            CREATE SCHEMA "My Schema";
            CREATE TABLE "My Schema"."Odd ""Table""" ("Value" text, "select" int);
            """", token);
        await ExecuteAsync(connection, $"SELECT pg_create_logical_replication_slot('rows', '{Plugin}')", token);

        // REPLICA IDENTITY DEFAULT logs no old tuple for a non-key update and only key columns for a delete.
        await CommitAsync(connection, "INSERT INTO keyed VALUES (1, NULL, 7)", token);
        await CommitAsync(connection, "UPDATE keyed SET note = 'café 🐘' WHERE id = 1", token);
        await CommitAsync(connection, "DELETE FROM keyed WHERE id = 1", token);
        await CommitAsync(connection, "INSERT INTO unkeyed VALUES ('a'); DELETE FROM unkeyed", token);
        Assert.AreSequenceEqual(
        [
            """{"typ":"INSERT","rel":"public.keyed","new":{"id":1,"extra":7}}""",
            """{"typ":"UPDATE","rel":"public.keyed","old":{},"new":{"id":1,"note":"café 🐘","extra":7}}""",
            """{"typ":"DELETE","rel":"public.keyed","old":{"id":1}}""",
            """{"typ":"INSERT","rel":"public.unkeyed","new":{"note":"a"}}""",
            """{"typ":"DELETE","rel":"public.unkeyed","old":{}}""",
        ], Rows(await ChangesAsync(connection, "rows", token)));

        // Quoted identifiers keep PostgreSQL's quoting, and JSON escapes only syntax and control characters.
        await CommitAsync(connection, """"
            INSERT INTO "My Schema"."Odd ""Table""" VALUES (E'"quoted"\\path\nnext\u0001<tag>', 5)
            """", token);
        Assert.AreSequenceEqual(
        [
            """{"typ":"INSERT","rel":"\"My Schema\".\"Odd \"\"Table\"\"\"","new":{"\"Value\"":"\"quoted\"\\path\nnext\u0001<tag>","\"select\"":5}}""",
        ], Rows(await ChangesAsync(connection, "rows", token)));

        // Dropped columns are skipped, rolled-back work is never decoded, and TOASTed text is reassembled.
        await ExecuteAsync(connection, "ALTER TABLE keyed DROP COLUMN extra", token);
        await ExecuteAsync(connection, "BEGIN; INSERT INTO keyed VALUES (2, 'rolled back'); ROLLBACK", token);
        await CommitAsync(connection, "INSERT INTO keyed SELECT 3, string_agg(md5(g::text), '') FROM generate_series(1, 1000) g", token);
        string[] toasted = Rows(await ChangesAsync(connection, "rows", token));
        string expected = await ScalarAsync<string>(connection, "SELECT note FROM keyed WHERE id = 3", token);
        Assert.AreEqual(32000, expected.Length);
        Assert.AreSequenceEqual([$$$"""{"typ":"INSERT","rel":"public.keyed","new":{"id":3,"note":"{{{expected}}}"}}"""], toasted);
        // 32000 hexadecimal digits still need about 16 kB compressed, so PostgreSQL stores the value out of line.
        Assert.IsGreaterThan(8000, await ScalarAsync<int>(connection, "SELECT pg_column_size(note) FROM keyed WHERE id = 3", token));

        // A key change logs the old key; the unchanged TOAST value is not logged again but is read while decoding.
        await CommitAsync(connection, "UPDATE keyed SET id = 4 WHERE id = 3", token);
        Assert.AreSequenceEqual([$$$"""{"typ":"UPDATE","rel":"public.keyed","old":{"id":3},"new":{"id":4,"note":"{{{expected}}}"}}"""],
            Rows(await ChangesAsync(connection, "rows", token)));

        // A transaction changing two tables counts both changes.
        long xid = await CommitAsync(connection, "INSERT INTO unkeyed VALUES ('b'); UPDATE keyed SET note = 'short' WHERE id = 4", token);
        string[] mixed = await ChangesAsync(connection, "rows", token);
        Assert.HasCount(4, mixed);
        Assert.EndsWith($",\"change_count\":2}}", mixed[^1]);
        Assert.IsTrue(mixed.All(row => row.StartsWith(xid + "|", StringComparison.Ordinal)));
    });

    /// <summary>
    /// An unsupported column type fails decoding with SQLSTATE 0A000 without consuming the change; the session, the
    /// slot and later changes remain usable after the slot advances past it.
    /// </summary>
    [TestMethod]
    public Task WalDecoderSampleRejectsUnsupportedTypesAndRecovers() => RunAsync(async (connection, token) =>
    {
        await ExecuteAsync(connection, "CREATE TABLE flags (id int, flag boolean); CREATE TABLE notes (note text)", token);
        await ExecuteAsync(connection, $"SELECT pg_create_logical_replication_slot('typed', '{Plugin}')", token);
        await CommitAsync(connection, "INSERT INTO flags VALUES (1, NULL)", token);
        await CommitAsync(connection, "INSERT INTO flags VALUES (2, true)", token);
        int backend = connection.ProcessID;
        for (int attempt = 0; attempt < 2; attempt++)
        {
            PostgresException error = await Assert.ThrowsExactlyAsync<PostgresException>(() => ChangesAsync(connection, "typed", token));
            Assert.AreEqual("0A000", error.SqlState);
            Assert.AreEqual("wal_decoder serializes only integer and text columns; public.flags.flag has type OID 16", error.MessageText);
            Assert.AreEqual(backend, await ScalarAsync<int>(connection, "SELECT pg_backend_pid()", token));
        }

        // A NULL value of the unsupported type is skipped, so the first transaction decodes before the failure.
        Assert.AreSequenceEqual(["""{"typ":"INSERT","rel":"public.flags","new":{"id":1}}"""], Rows(await ScalarAsync<string[]>(connection,
            "SELECT array_agg(xid::text || '|' || data ORDER BY ordinality) FROM pg_logical_slot_peek_changes('typed', NULL, 3) WITH ORDINALITY",
            token)));
        await ExecuteAsync(connection, "SELECT pg_replication_slot_advance('typed', pg_current_wal_lsn())", token);
        await CommitAsync(connection, "INSERT INTO notes VALUES ('after')", token);
        Assert.AreSequenceEqual(["""{"typ":"INSERT","rel":"public.notes","new":{"note":"after"}}"""],
            Rows(await ChangesAsync(connection, "typed", token)));
    });

    /// <summary>
    /// A LATIN1 database receives JSON text in its own encoding, which the server converts for a UTF-8 client.
    /// </summary>
    [TestMethod]
    public Task WalDecoderSampleWritesTheDatabaseEncoding() => RunAsync(async (connection, token) =>
    {
        await ExecuteAsync(connection, "CREATE DATABASE latin1 TEMPLATE template0 ENCODING 'LATIN1' LC_COLLATE 'C' LC_CTYPE 'C'", token);
        var builder = new NpgsqlConnectionStringBuilder(connection.ConnectionString) { Database = "latin1", Pooling = false };
        await using var latin1 = new NpgsqlConnection(builder.ConnectionString);
        await latin1.OpenAsync(token);
        await ExecuteAsync(latin1, "CREATE TABLE words (word text)", token);
        await ExecuteAsync(latin1, $"SELECT pg_create_logical_replication_slot('latin1_slot', '{Plugin}')", token);
        await CommitAsync(latin1, "INSERT INTO words VALUES ('café naïve')", token);
        Assert.AreSequenceEqual(["""{"typ":"INSERT","rel":"public.words","new":{"word":"café naïve"}}"""],
            Rows(await ChangesAsync(latin1, "latin1_slot", token, peek: true)));
        byte[] raw = await ScalarAsync<byte[]>(latin1,
            "SELECT data FROM pg_logical_slot_get_binary_changes('latin1_slot', NULL, NULL) OFFSET 1 LIMIT 1", token);
        Assert.AreSequenceEqual(Encoding.Latin1.GetBytes("""{"typ":"INSERT","rel":"public.words","new":{"word":"café naïve"}}"""), raw);
        await ExecuteAsync(latin1, "SELECT pg_drop_replication_slot('latin1_slot')", token);
    });

    /// <summary>
    /// A WAL sender loads the plugin for the streaming replication protocol and sends the same JSON documents.
    /// </summary>
    [TestMethod]
    public Task WalDecoderSampleStreamsThroughAWalSender() => RunAsync(async (connection, token) =>
    {
        await ExecuteAsync(connection, "CREATE TABLE person (name TEXT, age INT)", token);
        await ExecuteAsync(connection, $"SELECT pg_create_logical_replication_slot('stream_slot', '{Plugin}')", token);
        long xid = await CommitAsync(connection, "INSERT INTO person VALUES ('Bruce Wayne', 42), ('Clark Kent', 33)", token);
        string committed = await CommitTimeAsync(connection, xid, token);

        await using var replication = new LogicalReplicationConnection(connection.ConnectionString);
        await replication.Open(token);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(60));
        var received = new List<string>();
        await foreach (XLogDataMessage message in replication.StartLogicalReplication(
            new WalDecoderSlot(new ReplicationSlotOptions("stream_slot", default(NpgsqlLogSequenceNumber))), timeout.Token))
        {
            using var buffer = new MemoryStream();
            await message.Data.CopyToAsync(buffer, timeout.Token);
            received.Add(Encoding.UTF8.GetString(buffer.ToArray()));
            replication.SetReplicationStatus(message.WalEnd);
            if (received.Count == 4)
            {
                break;
            }
        }

        Assert.AreSequenceEqual(
        [
            """{"typ":"BEGIN"}""",
            """{"typ":"INSERT","rel":"public.person","new":{"name":"Bruce Wayne","age":42}}""",
            """{"typ":"INSERT","rel":"public.person","new":{"name":"Clark Kent","age":33}}""",
            $$"""{"typ":"COMMIT","committed":{{committed}},"change_count":2}""",
        ], received);
        Assert.IsTrue(received.All(static document => JsonElement.Parse(document).ValueKind == JsonValueKind.Object));
    });

    /// <summary>
    /// Starts a cluster with logical WAL and commit timestamps, and runs the test in its default database.
    /// </summary>
    private async Task RunAsync(Func<NpgsqlConnection, CancellationToken, Task> test)
    {
        CancellationToken token = context.CancellationToken;
        PostgresTestClusterOptions defaults = await IntegrationEnvironment.CreateOptionsAsync(token);
        PostgresTestClusterOptions options = new()
        {
            Installation = defaults.Installation,
            DataDirectoryBase = defaults.DataDirectoryBase,
            LogDirectory = defaults.LogDirectory,
            PostgreSqlConfiguration = [.. defaults.PostgreSqlConfiguration, "wal_level = logical", "max_replication_slots = 8",
                "max_wal_senders = 8", "track_commit_timestamp = on"],
        };
        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(options, token);
        await using (NpgsqlConnection administrator = await cluster.OpenConnectionAsync(token))
        {
            if (await ScalarAsync<bool>(administrator, "SELECT EXISTS (SELECT FROM pg_settings WHERE name = 'output_plugin_libraries')", token))
            {
                // Current minor releases load only output plugins that the DBA trusts, for every role.
                await ExecuteAsync(administrator,
                    $"ALTER SYSTEM SET output_plugin_libraries = 'pgoutput', 'test_decoding', '{Plugin}'; SELECT pg_reload_conf()", token);
            }
        }

        NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        try
        {
            DateTime deadline = DateTime.UtcNow.AddSeconds(30);
            while (await ScalarAsync<bool>(connection,
                "SELECT EXISTS (SELECT FROM pg_settings WHERE name = 'output_plugin_libraries' AND setting NOT LIKE '%WalDecoder%')", token))
            {
                // A backend forked before the postmaster processed the reload keeps the previous setting.
                await connection.DisposeAsync();
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail("The reloaded output_plugin_libraries setting did not reach new backends.");
                }

                await Task.Delay(100, token);
                connection = await cluster.OpenConnectionAsync(token);
            }

            await test(connection, token);
        }
        finally
        {
            await connection.DisposeAsync();
        }
    }

    /// <summary>
    /// Runs statements in one committed transaction and returns its transaction ID.
    /// </summary>
    private static async Task<long> CommitAsync(NpgsqlConnection connection, string sql, CancellationToken token)
    {
        await using NpgsqlTransaction transaction = await connection.BeginTransactionAsync(token);
        await using (var command = new NpgsqlCommand(sql, connection, transaction))
        {
            await command.ExecuteNonQueryAsync(token);
        }

        await using var identity = new NpgsqlCommand("SELECT pg_current_xact_id()::text::bigint", connection, transaction);
        long xid = Assert.IsInstanceOfType<long>(await identity.ExecuteScalarAsync(token));
        await transaction.CommitAsync(token);
        return xid;
    }

    /// <summary>
    /// Formats a committed transaction's recorded commit time as PostgreSQL microseconds since 2000-01-01 UTC.
    /// </summary>
    private static async Task<string> CommitTimeAsync(NpgsqlConnection connection, long xid, CancellationToken token)
    {
        DateTime committed = await ScalarAsync<DateTime>(connection, $"SELECT pg_xact_commit_timestamp('{xid}'::xid)", token);
        long microseconds = (committed - new DateTime(2000, 1, 1, 0, 0, 0, DateTimeKind.Utc)).Ticks / TimeSpan.TicksPerMicrosecond;
        return microseconds.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads and consumes, or only peeks at, a slot's pending changes as transaction ID and document pairs.
    /// </summary>
    private static async Task<string[]> ChangesAsync(NpgsqlConnection connection, string slot, CancellationToken token, bool peek = false)
    {
        await using var command = new NpgsqlCommand($$"""
            SELECT coalesce(array_agg(xid::text || '|' || data ORDER BY ordinality), '{}')
            FROM pg_logical_slot_{{(peek ? "peek" : "get")}}_changes($1, NULL, NULL) WITH ORDINALITY
            """, connection);
        command.Parameters.AddWithValue(slot);
        return Assert.IsInstanceOfType<string[]>(await command.ExecuteScalarAsync(token));
    }

    /// <summary>
    /// Keeps only row-change documents, without their transaction IDs.
    /// </summary>
    private static string[] Rows(string[] changes) => [.. changes.Select(static change => change[(change.IndexOf('|', StringComparison.Ordinal) + 1)..])
        .Where(static document => !document.StartsWith("{\"typ\":\"BEGIN\"", StringComparison.Ordinal) &&
            !document.StartsWith("{\"typ\":\"COMMIT\"", StringComparison.Ordinal))];

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

    /// <summary>
    /// Identifies a slot created through SQL for Npgsql's streaming replication protocol.
    /// </summary>
    /// <param name="options">The slot name and starting position.</param>
    private sealed class WalDecoderSlot(ReplicationSlotOptions options) : LogicalReplicationSlot(Plugin, options);
}
