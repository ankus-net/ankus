using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// An output plugin alias loads the extension's plugin under a second name, as pglogical's <c>pglogical_output</c>
    /// module loads <c>pglogical</c>. Both names decode the same changes through one loaded library, PostgreSQL trusts
    /// each name separately, packaging installs the module beside the library, and publishing without the alias removes
    /// it.
    /// </summary>
    [TestMethod]
    public async Task OutputPluginAliasesLoadTheExtensionPlugin()
    {
        CancellationToken token = context.CancellationToken;
        const string Alias = "alias_decoder_output";
        string directory = CreateDirectory();
        string output = await PublishPackageConsumerInDirectoryAsync(directory, "AliasDecoder", "ankus_alias_decoder", AliasDecoderSource,
            token, outputPluginAliases: [Alias]);
        PublishedExtension manifest = PublishedExtension.Read(output);
        string plugin = Path.GetFileNameWithoutExtension(manifest.Library);
        string module = Alias + Path.GetExtension(manifest.Library);
        Assert.AreSequenceEqual<string>([module], manifest.PluginLibraries);
        Assert.IsTrue(File.Exists(Path.Combine(output, module)));

        await using (PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token,
            additionalConfiguration: ["wal_level = logical", "max_replication_slots = 4"]))
        {
            await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
            await ExecutePackageGucAsync(connection, "CREATE TABLE items (id int)");
            bool restricted = (bool)(await PackageGucScalarAsync(connection,
                "SELECT EXISTS (SELECT FROM pg_settings WHERE name = 'output_plugin_libraries')"))!;
            if (restricted)
            {
                // PostgreSQL checks the name a slot uses, so trusting the library does not trust its alias.
                await ExecutePackageGucAsync(connection, $"SET output_plugin_libraries = 'pgoutput', '{plugin}'");
                PostgresException untrusted = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                    ExecutePackageGucAsync(connection, $"SELECT pg_create_logical_replication_slot('aliased', '{Alias}')"));
                Assert.AreEqual("42501", untrusted.SqlState);
                Assert.AreEqual($"library \"{Alias}\" may not be used as an output plugin", untrusted.MessageText);
                await ExecutePackageGucAsync(connection, $"SET output_plugin_libraries = 'pgoutput', '{plugin}', '{Alias}'");
            }

            await ExecutePackageGucAsync(connection, $"SELECT pg_create_logical_replication_slot('direct', '{plugin}')");
            await ExecutePackageGucAsync(connection, $"SELECT pg_create_logical_replication_slot('aliased', '{Alias}')");
            Assert.AreEqual($"aliased:{Alias},direct:{plugin}", await PackageGucScalarAsync(connection,
                "SELECT string_agg(slot_name || ':' || plugin, ',' ORDER BY slot_name) FROM pg_replication_slots"));
            await ExecutePackageGucAsync(connection, "INSERT INTO items VALUES (1), (2)");

            // Each decoding context initializes the plugin again, including the two that created the slots. The count
            // continues across both names, so the alias reached the same loaded library and runtime.
            Assert.AreEqual("BEGIN 3|change items|change items|COMMIT", await PackageGucScalarAsync(connection,
                "SELECT string_agg(data, '|' ORDER BY ordinality) FROM pg_logical_slot_get_changes('direct', NULL, NULL) WITH ORDINALITY"));
            Assert.AreEqual("BEGIN 4|change items|change items|COMMIT", await PackageGucScalarAsync(connection,
                "SELECT string_agg(data, '|' ORDER BY ordinality) FROM pg_logical_slot_get_changes('aliased', NULL, NULL) WITH ORDINALITY"));
        }

        string package = CreateDirectory();
        ProcessResult packaged = await InvokeAsync(["package", "--home", s_home, "--pg", MajorText(), "--from", output, "--output", package], token);
        Assert.AreEqual(0, packaged.ExitCode, packaged.StandardOutput + packaged.StandardError);
        foreach (string file in new[] { manifest.Library, module })
        {
            Assert.AreSequenceEqual(await File.ReadAllBytesAsync(Path.Combine(output, file), token),
                await File.ReadAllBytesAsync(Path.Combine(PackageLibraryDirectory(package), file), token));
        }

        string republished = await PublishPackageConsumerInDirectoryAsync(directory, "AliasDecoder", "ankus_alias_decoder", AliasDecoderSource,
            token);
        Assert.IsEmpty(PublishedExtension.Read(republished).PluginLibraries);
        Assert.IsFalse(File.Exists(Path.Combine(republished, module)));
        Assert.IsTrue(File.Exists(Path.Combine(republished, manifest.Library)));
    }

    private const string AliasDecoderSource = """
        using System.Text;
        using Ankus;
        using Ankus.Postgres;

        public static unsafe partial class AliasDecoder
        {
            private static int s_initialized;

            [PgNativeCallback(nameof(Startup))]
            private static partial OutputPluginCallbacks_startup_cbCallback StartupCallback { get; }

            [PgNativeCallback(nameof(Begin))]
            private static partial OutputPluginCallbacks_begin_cbCallback BeginCallback { get; }

            [PgNativeCallback(nameof(Change))]
            private static partial OutputPluginCallbacks_change_cbCallback ChangeCallback { get; }

            [PgNativeCallback(nameof(Commit))]
            private static partial OutputPluginCallbacks_commit_cbCallback CommitCallback { get; }

            [PgOutputPlugin]
            public static void Initialize(OutputPluginCallbacks* callbacks)
            {
                s_initialized++;
                callbacks->startup_cb = StartupCallback;
                callbacks->begin_cb = BeginCallback;
                callbacks->change_cb = ChangeCallback;
                callbacks->commit_cb = CommitCallback;
            }

            private static void Startup(LogicalDecodingContext* context, OutputPluginOptions* options, bool isInit)
                => options->output_type = OutputPluginOutputType.OUTPUT_PLUGIN_TEXTUAL_OUTPUT;

            private static void Begin(LogicalDecodingContext* context, ReorderBufferTXN* transaction)
                => Write(context, "BEGIN " + s_initialized);

            private static void Change(LogicalDecodingContext* context, ReorderBufferTXN* transaction, RelationData* relation,
                ReorderBufferChange* change)
            {
                using PgRelation borrowed = PgRelation.DangerousBorrow(relation)!;
                Write(context, "change " + borrowed.Name);
            }

            private static void Commit(LogicalDecodingContext* context, ReorderBufferTXN* transaction, ulong commitLsn)
                => Write(context, "COMMIT");

            private static void Write(LogicalDecodingContext* context, string text)
            {
                byte[] bytes = Encoding.ASCII.GetBytes(text);
                NativeMethods.OutputPluginPrepareWrite(context, true);
                fixed (byte* data = bytes)
                {
                    NativeMethods.appendBinaryStringInfo(context->@out, (sbyte*)data, bytes.Length);
                }

                NativeMethods.OutputPluginWrite(context, true);
            }
        }
        """;
}
