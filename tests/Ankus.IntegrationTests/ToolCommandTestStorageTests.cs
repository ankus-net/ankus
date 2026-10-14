using System.Globalization;
using System.Xml.Linq;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// An invalid custom parent fails before invoking tests and preserves an existing file at that location.
    /// </summary>
    /// <param name="partition">The rejected path form.</param>
    [TestMethod]
    [DataRow("empty")]
    [DataRow("file")]
    public async Task TestCommandRejectsInvalidDataDirectory(string partition)
    {
        string root = CreateDirectory();
        string file = Path.Join(root, "data-file");
        await File.WriteAllTextAsync(file, "preserve file", context.CancellationToken);
        string reports = Path.Join(root, "reports");
        ProcessResult result = await InvokeAsync(["test", "--home", s_home, "--pg", MajorText(),
            "--pgdata", partition == "file" ? file : "", "--results-directory", reports], context.CancellationToken);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains(partition == "file" ? "data-file" : "path", result.StandardError);
        Assert.DoesNotContain("Testing PostgreSQL", result.StandardOutput);
        Assert.IsFalse(Directory.Exists(reports));
        Assert.AreEqual("preserve file", await File.ReadAllTextAsync(file, context.CancellationToken));
    }

    /// <summary>
    /// Ordinary dotnet test can select fixture storage through the public options while preserving the parent and logs.
    /// </summary>
    /// <param name="relative">Whether the fixture option is relative to the ordinary test host's working directory.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PackagedExtensionFixtureUsesCustomDataDirectory(bool relative)
    {
        CancellationToken token = context.CancellationToken;
        // Both path forms run the same host and extension; the second reuses the first's build and publication.
        string host = TestCommandHostDirectory("", "Debug");
        using ReusableProjectLease lease = await AcquireReusableProjectAsync("custom fixture storage", CreateStorageProjectAsync,
            [Path.Join(host, "direct-storage.txt"), "direct reports", Path.Join("src", "TestCommandProbe", "bin", "ankus-test-logs")],
            token);
        string output = lease.Directory;
        string dataBase = CreateCustomDataBase();
        string hostRoot = Path.Join(output, "tests", "TestCommandProbe.Tests");
        string execution = TestCommandHostDirectory(output, "Debug");
        var environment = new Dictionary<string, string?>(s_environment)
        {
            ["TEST_DATA_PARENT"] = relative ? Path.GetRelativePath(execution, dataBase) : dataBase,
            ["ANKUS_TEST_PG_CONFIG"] = s_installation.PgConfigPath,
            ["ANKUS_TEST_POSTGRES_MAJOR"] = null,
            ["ANKUS_TEST_SESSION_DIRECTORY"] = null,
            ["ANKUS_TEST_DATA_DIRECTORY"] = null,
        };
        string reports = Path.Join(output, "direct reports");
        ProcessResult result = await PackageProcessRunner.RunAsync("dotnet",
            ["test", "--project", Path.Join(hostRoot, "TestCommandProbe.Tests.csproj"),
                "-p:AnkusPostgresMajor=" + MajorText(), "-p:AnkusPgConfigPath=" + s_installation.PgConfigPath,
                "--filter", "FullyQualifiedName~DirectFixtureUsesSelectedStorage", "--report-trx", "--report-trx-filename", "storage.trx", "--results-directory", reports],
            environment, token, workingDirectory: output);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        XElement outcome = Assert.ContainsSingle(XDocument.Load(Path.Join(reports, "storage.trx")).Descendants(ns + "UnitTestResult"));
        Assert.AreEqual("DirectFixtureUsesSelectedStorage", outcome.Attribute("testName")!.Value);
        Assert.AreEqual("Passed", outcome.Attribute("outcome")!.Value);
        string[] evidence = await File.ReadAllLinesAsync(Path.Join(execution, "direct-storage.txt"), token);
        Assert.IsFalse(Directory.Exists(evidence[0]));
        AssertServerExited(int.Parse(evidence[1], CultureInfo.InvariantCulture));
        Assert.IsEmpty(Directory.GetDirectories(dataBase));
        Assert.AreEqual("preserve custom parent", await File.ReadAllTextAsync(Path.Join(dataBase, "unrelated.txt"), token));
        Assert.IsNotEmpty(Directory.GetFiles(Path.Join(output, "src", "TestCommandProbe", "bin", "ankus-test-logs"), "*.log"));
    }

    /// <summary>
    /// Generates the selection probe with a fixture whose data parent comes from the host's environment.
    /// </summary>
    /// <param name="parent">The class-owned directory that receives the solution.</param>
    /// <param name="token">Cancels generation and writes.</param>
    /// <returns>The generated solution directory.</returns>
    private static async Task<string> CreateStorageProjectAsync(string parent, CancellationToken token)
    {
        string output = await CreateTestCommandProjectAsync(parent, token);
        string hostRoot = Path.Join(output, "tests", "TestCommandProbe.Tests");
        string backendFile = Path.Join(hostRoot, "BackendTests.cs");
        string backend = await File.ReadAllTextAsync(backendFile, token);
        await File.WriteAllTextAsync(backendFile, backend.Replace("IncludeTests = true,",
            "IncludeTests = true,\n            DataDirectoryBase = Environment.GetEnvironmentVariable(\"TEST_DATA_PARENT\"),", StringComparison.Ordinal), token);
        await File.WriteAllTextAsync(Path.Join(hostRoot, "StorageTests.cs"), """
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            using Npgsql;
            namespace TestCommandProbe.Tests;
            public sealed partial class BackendTests
            {
                [TestMethod]
                public async Task DirectFixtureUsesSelectedStorage()
                {
                    string expected = Path.GetFullPath(Environment.GetEnvironmentVariable("TEST_DATA_PARENT")!);
                    await using NpgsqlConnection connection = await s_extension!.Cluster.OpenConnectionAsync(context.CancellationToken);
                    await using var command = new NpgsqlCommand("SELECT current_setting('data_directory'), current_setting('unix_socket_directories'), public.add(19, 23)", connection);
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
                    Assert.IsTrue(await reader.ReadAsync(context.CancellationToken));
                    string actual = Path.GetFullPath(reader.GetString(0));
                    Assert.StartsWith(expected + Path.DirectorySeparatorChar, actual);
                    Assert.AreEqual(s_extension.Cluster.DataDirectory, actual);
                    Assert.AreEqual(s_extension.Cluster.SocketDirectory ?? "", reader.GetString(1));
                    Assert.DoesNotContain(expected, reader.GetString(1));
                    Assert.AreEqual(42, reader.GetInt32(2));
                    string[] pid = await File.ReadAllLinesAsync(Path.Join(actual, "postmaster.pid"), context.CancellationToken);
                    await File.WriteAllLinesAsync("direct-storage.txt", [actual, pid[0]], context.CancellationToken);
                }
            }
            """, token);
        return output;
    }
}
