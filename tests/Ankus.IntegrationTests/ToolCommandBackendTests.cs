using System.Xml.Linq;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Packaged test catalogs discover backend-only methods, report individual outcomes and preserve transaction isolation.
    /// </summary>
    /// <param name="sourceGeneration">Whether the host uses MSTest's generated discovery.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task GeneratedBackendTestsDiscoverExecuteAndRollback(bool sourceGeneration)
    {
        CancellationToken token = context.CancellationToken;
        string output = Path.Join(CreateDirectory(), "backend discovery");
        (await InvokeAsync(["new", "BackendProbe", "-o", output], token)).EnsureSuccess(s_tool, ["new"]);
        string extensionRoot = Path.Join(output, "src", "BackendProbe");
        string hostRoot = Path.Join(output, "tests", "BackendProbe.Tests");
        string project = Path.Join(extensionRoot, "BackendProbe.csproj");
        XDocument extension = XDocument.Load(project);
        extension.Root!.Add(new XElement("PropertyGroup", new XElement("AnkusExtensionControlFile", "probe.control")));
        extension.Save(project);
        await File.WriteAllTextAsync(Path.Join(extensionRoot, "probe.control"), "schema = 'Case Schema'\nrelocatable = false\n", token);
        XDocument host = XDocument.Load(Path.Join(hostRoot, "BackendProbe.Tests.csproj"));
        host.Root!.Add(new XElement("PropertyGroup", new XElement("EnableMSTestSourceGeneration", sourceGeneration)));
        host.Save(Path.Join(hostRoot, "BackendProbe.Tests.csproj"));
        await File.WriteAllTextAsync(Path.Join(extensionRoot, "BackendChecks.cs"), """
            using Ankus;
            namespace BackendProbe;
            public static partial class BackendChecks
            {
                // Discovery must not execute this constructor outside PostgreSQL.
                static BackendChecks()
                {
                    if (Spi.ExecuteScalar<int>("SELECT 42") != 42)
                    {
                        throw new InvalidOperationException("Backend initialization failed.");
                    }
                }

                [PgTest]
                public static void WritesRows()
                {
                    Spi.Execute("CREATE TABLE backend_rollback_probe(value integer); INSERT INTO backend_rollback_probe VALUES(42)");
                    if (Spi.ExecuteScalar<int>("SELECT value FROM backend_rollback_probe") != 42)
                    {
                        throw new InvalidOperationException("Backend value changed.");
                    }
                }

                [PgTest(ExpectedError = "expected café")]
                public static void ExpectedFailure()
                {
                    WritesRows();
                    throw new InvalidOperationException("expected café");
                }

                [PgTest(ExpectedError = "foo \"bar\"")]
                public static void QuotedExpectedFailure() => PgLog.Error("foo \"bar\"");

                [PgTest(ExpectedError = "different error")]
                public static void WrongError() => ExpectedFailure();

                [PgTest(ExpectedError = "missing error")]
                public static void MissingError() => WritesRows();

                [PgTest(IgnoreReason = "Explicit discovery probe")]
                public static void Ignored() => throw new InvalidOperationException("Ignored test was executed.");

                [PgTest]
                public static void AssertsInBackend()
                {
                    PgAssert.AreEqual(42, Spi.ExecuteScalar<int>("SELECT 42"));
                    Spi.Execute("CREATE TABLE backend_assert_probe(value integer)");
                    PgException error = PgAssert.ThrowsSqlState(PgSqlStates.DivisionByZero, static () =>
                    {
                        Spi.Execute("INSERT INTO backend_assert_probe VALUES (1)");
                        _ = Spi.ExecuteScalar<int>("SELECT 1 / 0");
                    });
                    PgAssert.AreEqual("division by zero", error.Message);
                    PgAssert.AreEqual(0L, Spi.ExecuteScalar<long>("SELECT count(*) FROM backend_assert_probe"));
                    PgAssert.ThrowsExactly<InvalidOperationException>(static () => throw new InvalidOperationException("managed"));
                }

                [PgTest(ExpectedError = "PgAssert.AreEqual failed. Expected: 42. Actual: 41.")]
                public static void AssertionFailureNamesValues() => PgAssert.AreEqual(42, Spi.ExecuteScalar<int>("SELECT 41"));

                [PgTest]
                public static void CallsTestOnlyFunction()
                {
                    if (Spi.ExecuteScalar<int>("SELECT \"Case Schema\".test_only_answer()") != 42)
                    {
                        throw new InvalidOperationException("The test-only function was not published.");
                    }
                }
            }

            #if ANKUS_TESTS
            public static class TestOnlyFunctions
            {
                [PgFunction]
                public static int TestOnlyAnswer() => 42;
            }
            #endif

            #if ANKUS_NONEXISTENT
            public static class MissingFunctions
            {
                [PgFunction]
                public static int Missing(NonexistentType value) => 0;
            }
            #endif

            [PgSchema(" ")]
            public static partial class SchemaChecks
            {
                [PgTest]
                public static void ReadsBackend()
                {
                    if (Spi.ExecuteScalar<int>("SELECT 42") != 42)
                    {
                        throw new InvalidOperationException("Explicit schema test failed.");
                    }
                }
            }
            """, token);
        await File.WriteAllTextAsync(Path.Join(hostRoot, "BackendTests.cs"), """
            using System.Runtime.CompilerServices;
            using Ankus;
            using Ankus.Testing;
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            using Npgsql;
            namespace BackendProbe.Tests;

            [TestClass]
            public sealed partial class BackendTests(TestContext context)
            {
                private static PostgresExtensionTest s_extension = null!;

                public static IEnumerable<TestDataRow<PgTestCase>> Cases
                    => BackendChecks.PostgresTests.Cases.Concat(SchemaChecks.PostgresTests.Cases).Select(test => new TestDataRow<PgTestCase>(test)
                    {
                        DisplayName = test.Name,
                        IgnoreMessage = test.IgnoreReason,
                    });

                [ClassInitialize]
                public static async Task InitializeAsync(TestContext context)
                {
                    s_extension = await PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions
                    {
                        ProjectPath = ProjectPath(),
                        IncludeTests = true,
                        Configuration = "Release",
                        PostgreSqlConfiguration = ["ankus_test.setting = 'configured'"],
                    }, context.CancellationToken);
                }

                [ClassCleanup]
                public static async Task CleanupAsync()
                {
                    if (s_extension is not null)
                    {
                        await s_extension.DisposeAsync();
                    }
                }

                [TestMethod]
                [DynamicData(nameof(Cases))]
                public Task NativeCase(PgTestCase test) => s_extension.RunTestAsync(test, context.CancellationToken);

                [TestMethod]
                public async Task FixtureContracts()
                {
                    CancellationToken token = context.CancellationToken;
                    PgTestCase success = Find("WritesRows");
                    await s_extension.RunTestAsync(success, token);
                    await CheckRollback();
                    await s_extension.RunTestAsync(Find("ExpectedFailure"), token);
                    await CheckRollback();
                    await s_extension.RunTestAsync(Find("QuotedExpectedFailure"), token);
                    await CheckRollback();
                    PostgresTestException wrong = await Assert.ThrowsExactlyAsync<PostgresTestException>(
                        () => s_extension.RunTestAsync(Find("WrongError"), token));
                    Assert.AreEqual("BackendProbe.BackendChecks.WrongError()", wrong.TestName);
                    PostgresException server = Assert.IsInstanceOfType<PostgresException>(wrong.InnerException);
                    Assert.AreEqual("expected café", server.MessageText);
                    Assert.AreEqual("38000", server.SqlState);
                    Assert.Contains("expected café", wrong.ServerLog);
                    await CheckRollback();
                    PostgresTestException missing = await Assert.ThrowsExactlyAsync<PostgresTestException>(
                        () => s_extension.RunTestAsync(Find("MissingError"), token));
                    Assert.IsInstanceOfType<InvalidOperationException>(missing.InnerException);
                    Assert.Contains("missing error", missing.Message);
                    await CheckRollback();
                    InvalidOperationException ignored = Assert.ThrowsExactly<InvalidOperationException>(
                        () => s_extension.RunTestAsync(Find("Ignored"), token));
                    Assert.Contains("Explicit discovery probe", ignored.Message);
                    InvalidOperationException stale = Assert.ThrowsExactly<InvalidOperationException>(
                        () => s_extension.RunTestAsync(new PgTestCase("Stale.Test()", null, success.FunctionName), token));
                    Assert.Contains("Rebuild", stale.Message);
                    InvalidOperationException wrongSchema = Assert.ThrowsExactly<InvalidOperationException>(
                        () => s_extension.RunTestAsync(new PgTestCase(success.Name, "Wrong Schema", success.FunctionName), token));
                    Assert.Contains("Rebuild", wrongSchema.Message);
                    Assert.ThrowsExactly<ArgumentNullException>(() => s_extension.RunTestAsync(null!, token));
                    using var canceled = new CancellationTokenSource();
                    await canceled.CancelAsync();
                    await Assert.ThrowsAsync<OperationCanceledException>(() => s_extension.RunTestAsync(success, canceled.Token));
                    await s_extension.RunTestAsync(success, token);
                    await CheckRollback();
                }

                private static PgTestCase Find(string method)
                    => BackendChecks.PostgresTests.Cases.Single(test => test.Name == "BackendProbe.BackendChecks." + method + "()");

                private async Task CheckRollback()
                {
                    await using NpgsqlConnection connection = await s_extension.Cluster.OpenConnectionAsync(context.CancellationToken);
                    await using var command = new NpgsqlCommand("SELECT to_regclass('backend_rollback_probe') IS NULL, current_setting('ankus_test.setting')", connection);
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
                    Assert.IsTrue(await reader.ReadAsync(context.CancellationToken));
                    Assert.IsTrue(reader.GetBoolean(0));
                    Assert.AreEqual("configured", reader.GetString(1));
                }

                private static string ProjectPath([CallerFilePath] string source = "")
                    => Path.GetFullPath(Path.Join(Path.GetDirectoryName(source)!, "../../src/BackendProbe/BackendProbe.csproj"));
            }
            """, token);
        ProcessResult listing = await PackageProcessRunner.RunAsync("dotnet", ["test", "--list-tests"], s_environment, token, workingDirectory: output);
        Assert.AreEqual(0, listing.ExitCode, listing.StandardOutput + listing.StandardError);
        Assert.Contains("BackendProbe.BackendChecks.WritesRows()", listing.StandardOutput);
        Assert.Contains("BackendProbe.BackendChecks.QuotedExpectedFailure()", listing.StandardOutput);
        Assert.IsFalse(Directory.Exists(Path.Join(extensionRoot, "bin", "ankus-test-publish")));
        ProcessResult result = await PackageProcessRunner.RunAsync("dotnet",
            ["test", "--report-trx", "-p:AnkusPostgresMajor=" + MajorText()], s_environment, token, workingDirectory: output);
        Assert.AreNotEqual(0, result.ExitCode, "Incorrect expected errors must fail the host test run.");
        XDocument report = XDocument.Load(Directory.GetFiles(output, "*.trx", SearchOption.AllDirectories).Single());
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        Dictionary<string, string> outcomes = report.Descendants(ns + "UnitTestResult")
            .ToDictionary(element => element.Attribute("testName")!.Value, element => element.Attribute("outcome")!.Value, StringComparer.Ordinal);
        Assert.AreEqual("Passed", outcomes["FixtureContracts"], result.StandardOutput + result.StandardError);
        Assert.AreEqual("Passed", outcomes["BackendProbe.BackendChecks.WritesRows()"]);
        Assert.AreEqual("Passed", outcomes["BackendProbe.BackendChecks.ExpectedFailure()"]);
        Assert.AreEqual("Passed", outcomes["BackendProbe.BackendChecks.QuotedExpectedFailure()"]);
        Assert.AreEqual("Passed", outcomes["BackendProbe.SchemaChecks.ReadsBackend()"]);
        Assert.AreEqual("Passed", outcomes["BackendProbe.BackendChecks.CallsTestOnlyFunction()"]);
        Assert.AreEqual("Passed", outcomes["BackendProbe.BackendChecks.AssertsInBackend()"]);
        Assert.AreEqual("Passed", outcomes["BackendProbe.BackendChecks.AssertionFailureNamesValues()"]);
        Assert.AreEqual("NotExecuted", outcomes["BackendProbe.BackendChecks.Ignored()"]);
        Assert.AreEqual("Failed", outcomes["BackendProbe.BackendChecks.WrongError()"]);
        Assert.AreEqual("Failed", outcomes["BackendProbe.BackendChecks.MissingError()"]);
        XElement counters = report.Descendants(ns + "Counters").Single();
        Assert.AreEqual("14", counters.Attribute("total")!.Value);
        Assert.AreEqual("11", counters.Attribute("passed")!.Value);
        Assert.AreEqual("2", counters.Attribute("failed")!.Value);
        Assert.IsEmpty(Directory.GetDirectories(Path.Join(extensionRoot, "bin", "ankus-test-publish")));
    }

    /// <summary>
    /// Invalid fixture options fail before discovery, publication or filesystem mutation.
    /// </summary>
    [TestMethod]
    public void BackendTestFixtureRejectsInvalidOptions()
    {
        string project = Path.Join(CreateDirectory(), "absent.csproj");
        Assert.ThrowsExactly<ArgumentNullException>(() => PostgresExtensionTest.StartAsync(null!, context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions { ProjectPath = " " }, context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions { ProjectPath = project, Configuration = " " }, context.CancellationToken));
        Assert.ThrowsExactly<ArgumentNullException>(() => PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions { ProjectPath = project, BuildProperties = null! }, context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions { ProjectPath = project, BuildProperties = new Dictionary<string, string> { ["invalid name"] = "value" } }, context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions { ProjectPath = project, Configuration = "Release", BuildProperties = new Dictionary<string, string> { ["Configuration"] = "Debug" } }, context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions { ProjectPath = project, BuildProperties = new Dictionary<string, string> { ["RuntimeIdentifier"] = "unavailable-target" } }, context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions { ProjectPath = project, BuildProperties = new Dictionary<string, string> { ["SelfContained"] = "false" } }, context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions { ProjectPath = project, BuildProperties = new Dictionary<string, string> { ["AnkusIncludeTests"] = "true" } }, context.CancellationToken));
        Assert.ThrowsExactly<ArgumentException>(() => PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions { ProjectPath = project, BuildProperties = new Dictionary<string, string> { ["AnkusReuseSchema"] = "true" } }, context.CancellationToken));
        Assert.ThrowsExactly<ArgumentNullException>(() => PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions { ProjectPath = project, PostgreSqlConfiguration = null! }, context.CancellationToken));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions { ProjectPath = project, Port = 0 }, context.CancellationToken));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PostgresExtensionTest.StartAsync(new PostgresExtensionTestOptions { ProjectPath = project, Port = 65536 }, context.CancellationToken));
        Assert.IsEmpty(Directory.GetFileSystemEntries(Path.GetDirectoryName(project)!));
    }
}
