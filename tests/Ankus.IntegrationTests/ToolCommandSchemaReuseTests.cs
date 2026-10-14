using System.Globalization;
using System.Runtime.InteropServices;
using System.Xml.Linq;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Installed commands and direct fixture options reuse actual installed SQL while executing newly compiled native bodies.
    /// </summary>
    /// <param name="direct">Whether ordinary dotnet test selects reuse through the public fixture option.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TestCommandSchemaReusePreservesSqlAndRebuildsBodies(bool direct)
    {
        CancellationToken token = context.CancellationToken;
        string output = await CreateSchemaReuseProjectAsync(direct, token);
        await RunSchemaReuseProbeAsync(output, direct, reuse: false, expectedSql: 11, expectedNative: 42, "first", token);
        string extension = Path.Join(output, "src", "TestCommandProbe");
        string snapshot = Assert.ContainsSingle(Directory.GetFiles(Path.Join(extension, "obj"), "schema-pg" + MajorText() + "-tests.json", SearchOption.AllDirectories));
        byte[] original = await File.ReadAllBytesAsync(snapshot, token);
        File.SetLastWriteTimeUtc(snapshot, new DateTime(2011, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        DateTime savedTime = File.GetLastWriteTimeUtc(snapshot);
        string functions = Path.Join(extension, "Functions.cs");
        string source = await File.ReadAllTextAsync(functions, token);
        await File.WriteAllTextAsync(functions, source.Replace("checked(left + right)", "checked(left + right + 100)", StringComparison.Ordinal), token);
        await WriteSchemaReuseSqlAsync(output, 22, token);
        await RunSchemaReuseProbeAsync(output, direct, reuse: true, expectedSql: 11, expectedNative: 142, "reused", token);
        Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(snapshot, token));
        Assert.AreEqual(savedTime, File.GetLastWriteTimeUtc(snapshot));

        if (!direct)
        {
            string changedContract = Path.Join(extension, "ChangedContract.cs");
            await File.WriteAllTextAsync(changedContract,
                "using Ankus; namespace TestCommandProbe; public static class ChangedContract { [PgFunction] public static int NewEntry() => 7; }", token);
            ProcessResult rejected = await InvokeSchemaReuseProbeAsync(output, direct: false, reuse: true, 11, 142, "incompatible", token);
            Assert.AreNotEqual(0, rejected.ExitCode);
            Assert.Contains("Native declarations changed", rejected.StandardOutput + rejected.StandardError);
            Assert.AreSequenceEqual(original, await File.ReadAllBytesAsync(snapshot, token));
            Assert.AreEqual(savedTime, File.GetLastWriteTimeUtc(snapshot));
            File.Delete(changedContract);
        }

        await RunSchemaReuseProbeAsync(output, direct, reuse: false, expectedSql: 22, expectedNative: 142, "regenerated", token);
        Assert.AreNotEqual(savedTime, File.GetLastWriteTimeUtc(snapshot));
        Assert.AreNotEqual(Convert.ToBase64String(original), Convert.ToBase64String(await File.ReadAllBytesAsync(snapshot, token)));

        string sentinel = Path.Join(Path.GetDirectoryName(snapshot)!, "preserve-user-file.txt");
        await File.WriteAllTextAsync(sentinel, "unrelated content", token);
        ProcessResult cleaned = await PackageProcessRunner.RunAsync("dotnet",
            ["clean", Path.Join(extension, "TestCommandProbe.csproj"), "-c", "Debug",
                "-r", RuntimeInformation.RuntimeIdentifier,
                "--artifacts-path", Path.Join(extension, "obj", "ankus-test-build"),
                "-bl:" + Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs", "schema-reuse", "clean-{}.binlog")],
            s_environment, token, workingDirectory: output);
        Assert.AreEqual(0, cleaned.ExitCode, cleaned.StandardOutput + cleaned.StandardError);
        Assert.IsFalse(File.Exists(snapshot));
        Assert.AreEqual("unrelated content", await File.ReadAllTextAsync(sentinel, token));
    }

    /// <summary>
    /// Schema reuse requires a successful publication for the selected test mode, even if a normal publication exists.
    /// </summary>
    /// <param name="ordinaryPublication">Whether to create a non-test snapshot first.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TestCommandSchemaReuseRequiresMatchingPublication(bool ordinaryPublication)
    {
        CancellationToken token = context.CancellationToken;
        string output = await CreateSchemaReuseProjectAsync(direct: false, token);
        string extension = Path.Join(output, "src", "TestCommandProbe");
        string? normal = null;
        byte[]? normalBytes = null;
        string project = Path.Join(extension, "TestCommandProbe.csproj");
        if (ordinaryPublication)
        {
            string publication = Path.Join(output, "ordinary publication");
            ProcessResult published = await PackageProcessRunner.RunAsync("dotnet",
                ["publish", project, "-c", "Debug", "-o", publication, "-p:AnkusIncludeTests=false",
                    "-p:AnkusPostgresMajor=" + MajorText(), "-p:AnkusPgConfigPath=" + s_installation.PgConfigPath],
                s_environment, token, workingDirectory: output);
            Assert.AreEqual(0, published.ExitCode, published.StandardOutput + published.StandardError);
            normal = Assert.ContainsSingle(Directory.GetFiles(Path.Join(extension, "obj"), "schema-pg" + MajorText() + "-normal.json", SearchOption.AllDirectories));
            normalBytes = await File.ReadAllBytesAsync(normal, token);
        }
        else
        {
            ProcessResult invalid = await PackageProcessRunner.RunAsync("dotnet", ["build", project, "-p:AnkusReuseSchema=invalid"],
                s_environment, token, workingDirectory: output);
            Assert.AreNotEqual(0, invalid.ExitCode);
            Assert.Contains("AnkusReuseSchema must be true or false", invalid.StandardOutput + invalid.StandardError);
        }

        ProcessResult result = await InvokeSchemaReuseProbeAsync(output, direct: false, reuse: true, 11, 42, "missing", token);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("No saved schema exists for this build", result.StandardOutput + result.StandardError);
        Assert.IsEmpty(Directory.GetFiles(Path.Join(extension, "obj"), "schema-pg" + MajorText() + "-tests.json", SearchOption.AllDirectories));
        Assert.IsEmpty(Directory.GetFiles(Path.Join(extension, "bin", "ankus-test-logs"), "*.log"));
        if (normal is not null)
        {
            Assert.AreSequenceEqual(normalBytes!, await File.ReadAllBytesAsync(normal, token));
        }
    }

    private async Task<string> CreateSchemaReuseProjectAsync(bool direct, CancellationToken token)
    {
        string output = await CreateTestCommandProjectAsync(token);
        string hostRoot = Path.Join(output, "tests", "TestCommandProbe.Tests");
        string file = Path.Join(hostRoot, "BackendTests.cs");
        string source = await File.ReadAllTextAsync(file, token);
        source = source.Replace("PostgresExtensionTest.StartAsync(", "StartSchemaFixtureAsync(", StringComparison.Ordinal);
        if (direct)
        {
            source = source.Replace("IncludeTests = true,", "IncludeTests = true,\n            Configuration = \"Debug\",\n            ReuseSchema = bool.Parse(Environment.GetEnvironmentVariable(\"TEST_REUSE_SCHEMA\")!),", StringComparison.Ordinal);
        }

        await File.WriteAllTextAsync(file, source, token);
        await WriteSchemaReuseSqlAsync(output, 11, token);
        await File.WriteAllTextAsync(Path.Join(hostRoot, "SchemaReuseTests.cs"), """
            using System.Globalization;
            using Ankus.PgConfig;
            using Ankus.Testing;
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            using Npgsql;
            namespace TestCommandProbe.Tests;
            public sealed partial class BackendTests
            {
                private static async Task<PostgresExtensionTest> StartSchemaFixtureAsync(PostgresExtensionTestOptions options, CancellationToken token)
                {
                    string intermediate = Path.Join(Path.GetDirectoryName(options.ProjectPath)!, "obj", "Debug");
                    Dictionary<string, byte[]> originals = Directory.GetFiles(intermediate, "*", SearchOption.AllDirectories)
                        .Where(path => Path.GetExtension(path) is ".dll" or ".pdb" || path.EndsWith(".FileListAbsolute.txt", StringComparison.Ordinal))
                        .ToDictionary(path => path, File.ReadAllBytes);
                    Assert.IsNotEmpty(originals);
                    PostgresExtensionTest fixture = await PostgresExtensionTest.StartAsync(options, token);
                    try
                    {
                        foreach (KeyValuePair<string, byte[]> entry in originals)
                        {
                            Assert.IsTrue(File.Exists(entry.Key), entry.Key);
                            Assert.AreSequenceEqual(entry.Value, await File.ReadAllBytesAsync(entry.Key, token), entry.Key);
                        }

                        return fixture;
                    }
                    catch
                    {
                        await fixture.DisposeAsync();
                        throw;
                    }
                }

                [TestMethod]
                public async Task RetainedSchemaMatchesBackend()
                {
                    int expectedSql = int.Parse(Environment.GetEnvironmentVariable("TEST_EXPECTED_SQL")!, CultureInfo.InvariantCulture);
                    int expectedNative = int.Parse(Environment.GetEnvironmentVariable("TEST_EXPECTED_NATIVE")!, CultureInfo.InvariantCulture);
                    await using NpgsqlConnection connection = await s_extension!.Cluster.OpenConnectionAsync(context.CancellationToken);
                    await using var command = new NpgsqlCommand("SELECT public.schema_value(), public.add(19, 23), current_setting('dynamic_library_path')", connection);
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
                    Assert.IsTrue(await reader.ReadAsync(context.CancellationToken));
                    Assert.AreEqual(expectedSql, reader.GetInt32(0));
                    Assert.AreEqual(expectedNative, reader.GetInt32(1));
                    string publication = reader.GetString(2).Split(OperatingSystem.IsWindows() ? ';' : ':')[0];
                    PublishedExtension artifacts = PublishedExtension.Read(publication);
                    ExtensionSchema schema = ExtensionSchema.Read(Path.Join(publication, artifacts.Library));
                    Assert.AreEqual(await File.ReadAllTextAsync(Path.Join(publication, "extension", artifacts.Sql), context.CancellationToken), schema.Sql);
                    Assert.IsNotNull(schema.Graph);
                    Assert.AreEqual(schema.Sql, schema.Graph.Sql);
                    string expectedFragment = "SELECT " + expectedSql.ToString(CultureInfo.InvariantCulture);
                    ExtensionSchemaItem item = Assert.ContainsSingle(schema.Graph.Items.Where(item => item.Names.Contains("schema-value")));
                    Assert.Contains(expectedFragment, item.Sql);
                    Assert.Contains(expectedFragment, schema.Select(["schema-value"], alterExtension: false).Sql);
                    await File.WriteAllLinesAsync("schema-storage.txt", [s_extension.Cluster.DataDirectory, Environment.GetEnvironmentVariable("ANKUS_TEST_SESSION_DIRECTORY") ?? ""], context.CancellationToken);
                }
            }
            """, token);
        return output;
    }

    private static Task WriteSchemaReuseSqlAsync(string output, int value, CancellationToken token)
        => File.WriteAllTextAsync(Path.Join(output, "src", "TestCommandProbe", "SchemaValue.cs"),
            "using Ankus;\n[assembly: PgSql(\"schema-value\", \"CREATE FUNCTION public.schema_value() RETURNS integer LANGUAGE sql AS 'SELECT " +
            value.ToString(CultureInfo.InvariantCulture) + "';\")]\n", token);

    private static async Task RunSchemaReuseProbeAsync(string output, bool direct, bool reuse, int expectedSql,
        int expectedNative, string label, CancellationToken token)
    {
        ProcessResult result = await InvokeSchemaReuseProbeAsync(output, direct, reuse, expectedSql, expectedNative, label, token);
        if (result.ExitCode != 0)
        {
            string diagnostics = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs", "schema-reuse");
            Directory.CreateDirectory(diagnostics);
            System.IO.Compression.ZipFile.CreateFromDirectory(Path.Join(output, "src", "TestCommandProbe"),
                Path.Join(diagnostics, label + "-" + Guid.NewGuid().ToString("N") + ".zip"));
        }

        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        string reports = Path.Join(output, "reports " + label);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        string report = Assert.ContainsSingle(Directory.GetFiles(reports, "schema.trx", SearchOption.AllDirectories));
        XElement outcome = Assert.ContainsSingle(XDocument.Load(report).Descendants(ns + "UnitTestResult"));
        Assert.AreEqual("RetainedSchemaMatchesBackend", outcome.Attribute("testName")!.Value);
        Assert.AreEqual("Passed", outcome.Attribute("outcome")!.Value);
        string[] storage = await File.ReadAllLinesAsync(Path.Join(TestCommandHostDirectory(output, "Debug"), "schema-storage.txt"), token);
        Assert.IsFalse(Directory.Exists(storage[0]));
        if (storage[1].Length != 0)
        {
            Assert.IsFalse(Directory.Exists(storage[1]));
        }
    }

    private static Task<ProcessResult> InvokeSchemaReuseProbeAsync(string output, bool direct, bool reuse, int expectedSql,
        int expectedNative, string label, CancellationToken token)
    {
        var environment = new Dictionary<string, string?>(s_environment)
        {
            ["TEST_EXPECTED_SQL"] = expectedSql.ToString(CultureInfo.InvariantCulture),
            ["TEST_EXPECTED_NATIVE"] = expectedNative.ToString(CultureInfo.InvariantCulture),
            ["TEST_REUSE_SCHEMA"] = reuse ? "true" : "false",
            // Explicit fixture options override ambient defaults; commands replace inherited context for their own child.
            ["ANKUS_TEST_REUSE_SCHEMA"] = "true",
            ["ANKUS_TEST_PG_CONFIG"] = s_installation.PgConfigPath,
        };
        string reports = Path.Join(output, "reports " + label);
        string project = Path.Join(output, "tests", "TestCommandProbe.Tests", "TestCommandProbe.Tests.csproj");
        string diagnostics = Path.Join(IntegrationEnvironment.RepositoryRoot, "artifacts", "test-logs", "schema-reuse");
        Directory.CreateDirectory(diagnostics);
        string[] runner = ["--project", project, "--filter", "FullyQualifiedName~RetainedSchemaMatchesBackend", "--report-trx", "--report-trx-filename", "schema.trx",
            "-bl:" + Path.Join(diagnostics, label + "-{}.binlog")];
        string[] arguments = direct
            ? ["test", .. runner, "--results-directory", reports, "-p:AnkusPostgresMajor=" + MajorText(), "-p:AnkusPgConfigPath=" + s_installation.PgConfigPath]
            : ["test", "--home", s_home, .. reuse ? new[] { "--all", "--no-schema" } : ["--pg", MajorText()], "--results-directory", reports, "--", .. runner];
        return PackageProcessRunner.RunAsync(direct ? "dotnet" : s_tool, arguments, environment, token, workingDirectory: output);
    }
}
