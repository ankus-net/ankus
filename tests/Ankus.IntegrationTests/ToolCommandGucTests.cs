using System.Runtime.InteropServices;
using System.Text.Json;
using System.Xml.Linq;
using Ankus.PgConfig;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// A cold package consumer preserves typed settings, hook extras and diagnostics across transactions and SQL reinstall.
    /// </summary>
    [TestMethod]
    public async Task PackedGucConsumerPreservesHooksAndNativeStateAcrossReinstall()
    {
        CancellationToken token = context.CancellationToken;
        string output = await PublishPackageConsumerAsync("PackedGuc", "ankus_packed_guc", ManagedGucConsumer, token, coldPackages: true);
        await using PostgresTestCluster cluster = await StartPublishedClusterAsync(output, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        await ExecutePackageGucAsync(connection, "CREATE EXTENSION ankus_packed_guc");
        const string defaults = "True|10|1.25|<null>|18446744073709551615|10:0A00FF";
        Assert.AreEqual(defaults, await PackageGucScalarAsync(connection, "SELECT package_guc_snapshot()"));
        Assert.AreEqual("10:0A00FF", await PackageGucScalarAsync(connection, "SHOW packed_guc.batch"));
        Assert.AreEqual("integer|10|10|0|100|user|default|Packed batch size|Retained package metadata.",
            await PackageGucScalarAsync(connection, """
                SELECT concat_ws('|', vartype, boot_val, reset_val, min_val, max_val, context, source, short_desc, extra_desc)
                FROM pg_settings WHERE name = 'packed_guc.batch'
                """));
        Assert.AreSequenceEqual(["idle", "rest", "fast"], Assert.IsInstanceOfType<string[]>(await PackageGucScalarAsync(connection,
            "SELECT enumvals FROM pg_settings WHERE name = 'packed_guc.mode'")));
        Assert.AreEqual("3.2.1|true|2", await PackageGucScalarAsync(connection, """
            SELECT e.extversion || '|' || e.extrelocatable::text || '|' || count(p.oid)::text
            FROM pg_extension e
            JOIN pg_depend d ON d.refobjid = e.oid AND d.refclassid = 'pg_extension'::regclass AND d.deptype = 'e'
            JOIN pg_proc p ON p.oid = d.objid AND d.classid = 'pg_proc'::regclass
            WHERE e.extname = 'ankus_packed_guc' GROUP BY e.extversion, e.extrelocatable
            """));

        await ExecutePackageGucAsync(connection, """
            SET packed_guc.enabled = off;
            SET packed_guc.batch = 11;
            SET packed_guc.ratio = -2.5;
            SET packed_guc.mode = 'FaSt';
            """);
        await using (var text = new NpgsqlCommand("SELECT set_config('packed_guc.label', $1, false)", connection))
        {
            text.Parameters.AddWithValue("café 🐘");
            Assert.AreEqual("café 🐘", await text.ExecuteScalarAsync(token));
        }

        const string changed = "False|12|-2.5|café 🐘|9223372036854775808|12:0C00FF";
        Assert.AreEqual(changed, await PackageGucScalarAsync(connection, "SELECT package_guc_snapshot()"));
        Assert.AreEqual("12:0C00FF", await PackageGucScalarAsync(connection, "SHOW packed_guc.batch"));
        PostgresException rejected = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
            ExecutePackageGucAsync(connection, "SET packed_guc.batch = 13"));
        Assert.AreEqual("P7513", rejected.SqlState);
        Assert.AreEqual("Packed count rejected.", rejected.MessageText);
        Assert.AreEqual("Thirteen is unavailable.", rejected.Detail);
        Assert.AreEqual("Choose another count.", rejected.Hint);
        Assert.AreEqual(changed, await PackageGucScalarAsync(connection, "SELECT package_guc_snapshot()"));
        Assert.AreEqual(42, await PackageGucScalarAsync(connection, "SELECT 42"));

        await ExecutePackageGucAsync(connection, "BEGIN; SET LOCAL packed_guc.batch = 21; SET LOCAL packed_guc.label = 'temporary'");
        Assert.AreEqual("False|22|-2.5|temporary|9223372036854775808|22:1600FF",
            await PackageGucScalarAsync(connection, "SELECT package_guc_snapshot()"));
        Assert.AreEqual("22:1600FF", await PackageGucScalarAsync(connection, "SHOW packed_guc.batch"));
        await ExecutePackageGucAsync(connection, "ROLLBACK");
        Assert.AreEqual(changed, await PackageGucScalarAsync(connection, "SELECT package_guc_snapshot()"));
        Assert.AreEqual("12:0C00FF", await PackageGucScalarAsync(connection, "SHOW packed_guc.batch"));
        await ExecutePackageGucAsync(connection, "SET packed_guc.mode = 'rest'");
        Assert.AreEqual("idle", await PackageGucScalarAsync(connection, "SHOW packed_guc.mode"));
        await ExecutePackageGucAsync(connection, "RESET ALL");
        Assert.AreEqual(defaults, await PackageGucScalarAsync(connection, "SELECT package_guc_snapshot()"));

        await ExecutePackageGucAsync(connection, "SET packed_guc.batch = 17");
        int checks = Assert.IsInstanceOfType<int>(await PackageGucScalarAsync(connection, "SELECT package_guc_check_count()"));
        await ExecutePackageGucAsync(connection, "DROP EXTENSION ankus_packed_guc");
        Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection, """
            SELECT to_regprocedure('public.package_guc_snapshot()') IS NULL
                AND to_regprocedure('public.package_guc_check_count()') IS NULL
                AND NOT EXISTS (SELECT FROM pg_extension WHERE extname = 'ankus_packed_guc')
            """)));
        Assert.AreEqual("18:1200FF", await PackageGucScalarAsync(connection, "SHOW packed_guc.batch"));
        Assert.AreEqual("session|10|10", await PackageGucScalarAsync(connection,
            "SELECT concat_ws('|', source, boot_val, reset_val) FROM pg_settings WHERE name = 'packed_guc.batch'"));
        if (s_installation.Version.Major >= 15)
        {
            PostgresException reserved = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ExecutePackageGucAsync(connection, "SET packed_guc.unknown = 1"));
            Assert.AreEqual("42602", reserved.SqlState);
            Assert.AreEqual("\"packed_guc\" is a reserved prefix.", reserved.Detail);
        }
        else
        {
            await ExecutePackageGucAsync(connection, "SET packed_guc.unknown = 1");
            Assert.AreEqual("1", await PackageGucScalarAsync(connection, "SHOW packed_guc.unknown"));
        }

        await ExecutePackageGucAsync(connection, "CREATE SCHEMA restored; CREATE EXTENSION ankus_packed_guc WITH SCHEMA restored");
        Assert.AreEqual("True|18|1.25|<null>|18446744073709551615|18:1200FF",
            await PackageGucScalarAsync(connection, "SELECT restored.package_guc_snapshot()"));
        Assert.AreEqual(checks, await PackageGucScalarAsync(connection, "SELECT restored.package_guc_check_count()"));
        Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection,
            "SELECT to_regprocedure('public.package_guc_snapshot()') IS NULL")));
        await ExecutePackageGucAsync(connection, "RESET packed_guc.batch");
        Assert.AreEqual(defaults, await PackageGucScalarAsync(connection, "SELECT restored.package_guc_snapshot()"));
    }

    /// <summary>
    /// A package consumer containing only native settings and a prefix can preload without SQL functions or managed entry.
    /// </summary>
    [TestMethod]
    public async Task PackedNativeGucConsumerPreloadsWithoutSqlExports()
    {
        CancellationToken token = context.CancellationToken;
        string output = await PublishPackageConsumerAsync("PackedNativeGuc", "ankus_packed_native", """
            using Ankus;
            [assembly: PgGucPrefix("packed_native")]
            public static partial class Settings
            {
                [PgGucInt("packed_native.slots", 7, "Packed native slots", Minimum = 0, Maximum = 20)]
                public static partial int Slots { get; }
            }
            """, token, coldPackages: true);
        PublishedExtension manifest = PublishedExtension.Read(output);
        Assert.AreEqual("-- Generated by Ankus. Do not edit.\n\n-- No installable objects declared.\n", (await File.ReadAllTextAsync(
            Path.Combine(output, "extension", manifest.Sql), token)).ReplaceLineEndings("\n"));
        PostgresInstallation installation = await PrepareCaseInstallationAsync(output, token);
        List<string> configuration =
        [
            "dynamic_library_path = '" + EscapeSetting(output) + "'",
            "shared_preload_libraries = '" + manifest.Library + "'",
            "packed_native.slots = 9",
        ];
        if (s_installation.Version.Major >= 18)
        {
            configuration.Insert(0, "extension_control_path = '" + EscapeSetting(output) + "'");
        }

        await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
        {
            Installation = installation,
            DataDirectoryBase = Path.Combine(s_root, "pgdata"),
            LogDirectory = Path.Combine(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs"),
            PostgreSqlConfiguration = configuration,
        }, token);
        await using NpgsqlConnection connection = await cluster.OpenConnectionAsync(token);
        Assert.AreEqual("9", await PackageGucScalarAsync(connection, "SHOW packed_native.slots"));
        Assert.AreEqual("9|7|9|configuration file|integer|user", await PackageGucScalarAsync(connection, """
            SELECT concat_ws('|', setting, boot_val, reset_val, source, vartype, context)
            FROM pg_settings WHERE name = 'packed_native.slots'
            """));
        await ExecutePackageGucAsync(connection, "CREATE EXTENSION ankus_packed_native; SET packed_native.slots = 12; DROP EXTENSION ankus_packed_native");
        Assert.AreEqual("12", await PackageGucScalarAsync(connection, "SHOW packed_native.slots"));
        Assert.IsTrue(Assert.IsInstanceOfType<bool>(await PackageGucScalarAsync(connection,
            "SELECT NOT EXISTS (SELECT FROM pg_extension WHERE extname = 'ankus_packed_native')")));
        if (s_installation.Version.Major >= 15)
        {
            PostgresException reserved = await Assert.ThrowsExactlyAsync<PostgresException>(() =>
                ExecutePackageGucAsync(connection, "SET packed_native.unknown = 1"));
            Assert.AreEqual("42602", reserved.SqlState);
            Assert.AreEqual("\"packed_native\" is a reserved prefix.", reserved.Detail);
        }
        else
        {
            await ExecutePackageGucAsync(connection, "SET packed_native.unknown = 1");
            Assert.AreEqual("1", await PackageGucScalarAsync(connection, "SHOW packed_native.unknown"));
        }

        await ExecutePackageGucAsync(connection, "CREATE EXTENSION ankus_packed_native; RESET packed_native.slots");
        Assert.AreEqual("9", await PackageGucScalarAsync(connection, "SHOW packed_native.slots"));
        Assert.AreEqual(42, await PackageGucScalarAsync(connection, "SELECT 42"));
    }

    /// <summary>
    /// Publishes with test-owned packages and proves the consumer has no repository references or style imports.
    /// </summary>
    private Task<string> PublishPackageConsumerAsync(string name, string extension, string source, CancellationToken token,
        bool coldPackages = false)
        => PublishPackageConsumerInDirectoryAsync(CreateDirectory(), name, extension, source, token, coldPackages);

    /// <summary>
    /// Publishes a packaged consumer into the caller's test-owned or fixture-owned directory, with any output plugin
    /// aliases as <c>AnkusOutputPluginAlias</c> items.
    /// </summary>
    private static async Task<string> PublishPackageConsumerInDirectoryAsync(string directory, string name, string extension,
        string source, CancellationToken token, bool coldPackages = false, string[]? outputPluginAliases = null)
    {
        string project = Path.Combine(directory, name + ".csproj");
        new XDocument(new XElement("Project", new XAttribute("Sdk", "Ankus.Sdk/" + s_version),
            new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0"),
                new XElement("Nullable", "enable"), new XElement("ImplicitUsings", "enable"),
                new XElement("TreatWarningsAsErrors", "true"), new XElement("AnkusExtensionName", extension),
                new XElement("AnkusExtensionVersion", "3.2.1")),
            new XElement("ItemGroup", (outputPluginAliases ?? []).Select(static alias =>
                new XElement("AnkusOutputPluginAlias", new XAttribute("Include", alias)))))).Save(project);
        await File.WriteAllTextAsync(Path.Combine(directory, "Settings.cs"), source, token);
        var environment = new Dictionary<string, string?>(s_environment);
        if (coldPackages)
        {
            environment["NUGET_PACKAGES"] = Path.Combine(directory, "cold packages");
            Assert.IsFalse(Directory.Exists(environment["NUGET_PACKAGES"]!));
        }

        // Only cold-restore contract tests need a new package root. Other consumers
        // reuse this run's packages so binding caches can recognize the same tools.
        Assert.StartsWith(s_root + Path.DirectorySeparatorChar, environment["NUGET_PACKAGES"]!);
        Assert.IsFalse(project.StartsWith(IntegrationEnvironment.RepositoryRoot, StringComparison.Ordinal));
        string output = Path.Combine(directory, "published");
        ProcessResult published = await PackageProcessRunner.RunAsync("dotnet",
            ["publish", project, "-c", "Release", "-r", RuntimeInformation.RuntimeIdentifier, "-o", output,
                "-p:AnkusPostgresMajor=" + MajorText(), "-p:AnkusPgConfigPath=" + s_installation.PgConfigPath,
                "-getProperty:MSBuildAllProjects,EnforceCodeStyleInBuild,GenerateDocumentationFile,_AnkusBuildTool",
                "-getItem:ProjectReference,Analyzer",
                "-bl:" + Path.Combine(directory, "guc-publish-{}.binlog")],
            environment, token, workingDirectory: directory);
        published.EnsureSuccess("dotnet", ["publish"]);
        using JsonDocument evaluation = JsonDocument.Parse(published.StandardOutput);
        JsonElement properties = evaluation.RootElement.GetProperty("Properties");
        Assert.DoesNotContain(IntegrationEnvironment.RepositoryRoot, properties.GetProperty("MSBuildAllProjects").GetString()!);
        Assert.AreNotEqual("true", properties.GetProperty("EnforceCodeStyleInBuild").GetString());
        Assert.AreNotEqual("true", properties.GetProperty("GenerateDocumentationFile").GetString());
        Assert.StartsWith(environment["NUGET_PACKAGES"]!, Path.GetFullPath(properties.GetProperty("_AnkusBuildTool").GetString()!));
        Assert.AreEqual(0, evaluation.RootElement.GetProperty("Items").GetProperty("ProjectReference").GetArrayLength());
        JsonElement generator = evaluation.RootElement.GetProperty("Items").GetProperty("Analyzer").EnumerateArray()
            .Single(static item => Path.GetFileName(item.GetProperty("Identity").GetString()) == "Ankus.Generators.dll");
        Assert.StartsWith(environment["NUGET_PACKAGES"]!, generator.GetProperty("FullPath").GetString());
        using JsonDocument assets = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "obj", "project.assets.json"), token));
        JsonElement libraries = assets.RootElement.GetProperty("libraries");
        Assert.AreEqual("package", libraries.GetProperty("Ankus.Runtime/" + s_version).GetProperty("type").GetString());
        Assert.AreEqual("package", libraries.GetProperty("Ankus.Generators/" + s_version).GetProperty("type").GetString());
        foreach (JsonProperty library in libraries.EnumerateObject())
        {
            Assert.AreEqual("package", library.Value.GetProperty("type").GetString(), library.Name);
        }

        PublishedExtension manifest = PublishedExtension.Read(output);
        Assert.AreEqual(extension + ".control", manifest.Control);
        Assert.AreEqual(extension + "--3.2.1.sql", manifest.Sql);
        Assert.IsTrue(File.Exists(Path.Combine(output, manifest.Library)));
        Assert.IsEmpty(Directory.GetFiles(output, "Ankus.*.dll")
            .Where(path => !string.Equals(Path.GetFileName(path), manifest.Library, StringComparison.Ordinal)));
        return output;
    }

    /// <summary>
    /// Executes a single package-consumer scalar while owning its command.
    /// </summary>
    private async Task<object?> PackageGucScalarAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        return await command.ExecuteScalarAsync(context.CancellationToken);
    }

    /// <summary>
    /// Executes package-consumer lifecycle statements while owning their command.
    /// </summary>
    private async Task ExecutePackageGucAsync(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await command.ExecuteNonQueryAsync(context.CancellationToken);
    }

    /// <summary>
    /// Ordinary author code exercises packaged attributes and generated callbacks without repository style imports.
    /// </summary>
    private const string ManagedGucConsumer = """
        using System.Globalization;
        using Ankus;

        [assembly: PgGucPrefix("packed_guc")]

        public enum Mode : ulong
        {
            [PgGucLabel("idle")] Idle = ulong.MaxValue,
            [PgGucLabel("rest")] Rest = Idle,
            [PgGucLabel("fast")] Fast = 1UL << 63,
        }

        public static partial class Settings
        {
            private static string assigned = "not assigned";
            private static int checks;

            [PgGucBool("packed_guc.enabled", true, "Packed Boolean")]
            public static partial bool Enabled { get; }

            [PgGucInt("packed_guc.batch", 10, "Packed batch size", Minimum = 0, Maximum = 100,
                LongDescription = "Retained package metadata.", Check = nameof(Check), Assign = nameof(Assign), Show = nameof(Show))]
            public static partial int Batch { get; }

            [PgGucReal("packed_guc.ratio", 1.25, "Packed ratio")]
            public static partial double Ratio { get; }

            [PgGucString("packed_guc.label", null, "Packed optional label")]
            public static partial string? Label { get; }

            [PgGucEnum("packed_guc.mode", Mode.Idle, "Packed mode")]
            public static partial Mode CurrentMode { get; }

            public static PgGucCheckResult<int> Check(int proposed, PgGucSource source)
            {
                checks++;
                if (proposed == 13)
                    return new(new PgGucCheckError("Packed count rejected.", "Thirteen is unavailable.", "Choose another count.", "P7513"));
                var normalized = proposed + proposed % 2;
                return new(normalized, new PgGucExtra(new byte[] { (byte)normalized, 0, 255 }));
            }

            public static void Assign(int accepted, PgGucExtra? extra)
                => assigned = Describe(accepted, extra);

            public static string Show(int current, PgGucExtra? extra) => Describe(current, extra);

            private static string Describe(int current, PgGucExtra? extra)
                => current.ToString(CultureInfo.InvariantCulture) + ":" + (extra is null ? "missing" : Convert.ToHexString(extra.AsSpan()));

            [PgFunction]
            public static string PackageGucSnapshot()
                => string.Create(CultureInfo.InvariantCulture, $"{Enabled}|{Batch}|{Ratio}|{Label ?? "<null>"}|{(ulong)CurrentMode}|{assigned}");

            [PgFunction]
            public static int PackageGucCheckCount() => checks;
        }
        """;
}
