using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Xml.Linq;
using Ankus.Testing;
using Npgsql;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Registered and explicit selections reach both the managed build and the real native backend, with literal filters and reports.
    /// </summary>
    /// <param name="selection">The installation selection mode.</param>
    [TestMethod]
    [DataRow("registered")]
    [DataRow("explicit")]
    [DataRow("all")]
    [DataRow("forwarded")]
    [DataRow("forwarded-relative")]
    [DataRow("custom-data")]
    [DataRow("all-custom-data")]
    [DataRow("runas")]
    public async Task TestCommandRunsSelectedBackendTests(string selection)
    {
        CancellationToken token = context.CancellationToken;
        string? account = selection == "runas" ? IntegrationEnvironment.RequireRunAsAccount() : null;
        string output = await CreateTestCommandProjectAsync(token);
        string[] selected = selection switch
        {
            "all" or "all-custom-data" => ["--home", s_home, "--all"],
            "explicit" => ["--pg", MajorText(), "--pg-config", s_installation.PgConfigPath],
            _ => ["--home", s_home, "--pg", MajorText()],
        };
        string? dataBase = selection.EndsWith("custom-data", StringComparison.Ordinal) ? CreateCustomDataBase()
            : account is null ? null : await IntegrationEnvironment.CreateAccountDirectoryAsync(account, token);
        string[] dataArguments = dataBase is null ? [] : account is null ? ["--pgdata", dataBase] : ["--pgdata", dataBase, "--runas", account];
        try
        {
            string reports = Path.Join(output, "reports with spaces");
            string host = Path.Join(output, "tests", "TestCommandProbe.Tests", "TestCommandProbe.Tests.csproj");
            string forwardedOutput = Path.Join(output, "forwarded outputs with spaces");
            string[] configuration = selection.StartsWith("forwarded", StringComparison.Ordinal) ? [] : ["-c", "Shipping"];
            string[] installationProperty = [];
            if (selection == "forwarded-relative")
            {
                installationProperty = ["-p:AnkusPgConfigPath=" +
                    Path.GetRelativePath(Path.GetDirectoryName(host)!, s_installation.PgConfigPath)];
            }

            string[] outputProperty = selection.StartsWith("forwarded", StringComparison.Ordinal)
                ? ["-p:ForwardedOutputRoot=" + forwardedOutput] : [];
            string[] properties = selection is "all" or "all-custom-data"
                ? ["-p:Configuration=Shipping", "-p:ForwardedFixtureProperty=enabled"]
                : ["-p:Configuration=Shipping", "-p:AnkusPostgresMajor=" + MajorText(),
                    "-p:ForwardedFixtureProperty=enabled", .. installationProperty, .. outputProperty];
            ProcessResult result = await PackageProcessRunner.RunAsync(s_tool,
                ["test", .. selected, .. configuration, .. dataArguments, "--results-directory", reports, "--", "--project", host,
                    "--report-trx", "--report-trx-filename", "selected.trx", "--filter",
                    "FullyQualifiedName~ConfigurationAndVersionReachBackend|FullyQualifiedName~DeclaredTestsExecuteInPostgres",
                    "--configuration", "Shipping", .. properties],
                s_environment, token, workingDirectory: output);
            Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
            Assert.Contains(s_postgresKey + ": dotnet test exited 0.", result.StandardOutput);
            string report = Path.Join(reports, s_postgresKey, "selected.trx");
            XDocument trx = XDocument.Load(report);
            XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
            Dictionary<string, string> outcomes = trx.Descendants(ns + "UnitTestResult").ToDictionary(
                item => item.Attribute("testName")!.Value, item => item.Attribute("outcome")!.Value);
            Assert.HasCount(3, outcomes);
            Assert.AreEqual("Passed", outcomes["ConfigurationAndVersionReachBackend"]);
            Assert.AreEqual("Passed", outcomes["TestCommandProbe.BackendChecks.AdditionInsidePostgres()"]);
            Assert.AreEqual("Passed", outcomes["TestCommandProbe.BackendChecks.ExpectedFailure()"]);
            string session = await File.ReadAllTextAsync(Path.Join(TestCommandHostDirectory(output, "Shipping"), "session-path.txt"), token);
            Assert.IsFalse(Directory.Exists(session), "Command-owned data, sockets and publication must be removed.");
            string actualData = await File.ReadAllTextAsync(Path.Join(TestCommandHostDirectory(output, "Shipping"), "data-path.txt"), token);
            Assert.IsFalse(Directory.Exists(actualData));
            if (dataBase is not null)
            {
                Assert.StartsWith(dataBase + Path.DirectorySeparatorChar, actualData);
                Assert.IsEmpty(Directory.GetDirectories(dataBase));
                if (account is null)
                {
                    Assert.AreEqual("preserve custom parent", await File.ReadAllTextAsync(Path.Join(dataBase, "unrelated.txt"), token));
                }
            }

            Assert.IsNotEmpty(Directory.GetFiles(Path.Join(output, "src", "TestCommandProbe", "bin", "ankus-test-logs"), "*.log"));
            if (selection.StartsWith("forwarded", StringComparison.Ordinal))
            {
                Assert.IsNotEmpty(Directory.GetFiles(Path.Join(forwardedOutput, "TestCommandProbe"), "*", SearchOption.AllDirectories));
                Assert.IsNotEmpty(Directory.GetFiles(Path.Join(forwardedOutput, "TestCommandProbe.Tests"), "*", SearchOption.AllDirectories));
            }
        }
        finally
        {
            if (account is not null)
            {
                await IntegrationEnvironment.DeleteAccountDirectoryAsync(account, dataBase!);
            }
        }
    }

    /// <summary>
    /// Invalid or conflicting forwarded configuration options fail before PostgreSQL selection or test execution.
    /// </summary>
    /// <param name="partition">The invalid argument form.</param>
    [TestMethod]
    [DataRow("conflict")]
    [DataRow("short-conflict")]
    [DataRow("equals-conflict")]
    [DataRow("colon-conflict")]
    [DataRow("short-equals-conflict")]
    [DataRow("short-colon-conflict")]
    [DataRow("duplicate-conflict")]
    [DataRow("missing")]
    [DataRow("separator")]
    [DataRow("invalid")]
    [DataRow("empty")]
    public async Task TestCommandRejectsForwardedConfiguration(string partition)
    {
        string root = CreateDirectory();
        string reports = Path.Join(root, "reports");
        string[] forwarded = partition switch
        {
            "conflict" => ["--configuration", "Release"],
            "short-conflict" => ["-c", "Release"],
            "equals-conflict" => ["--configuration=Release"],
            "colon-conflict" => ["--configuration:Release"],
            "short-equals-conflict" => ["-c=Release"],
            "short-colon-conflict" => ["-c:Release"],
            "duplicate-conflict" => ["--configuration", "Shipping", "-c", "Release"],
            "missing" => ["--configuration"],
            "separator" => ["-c", "--"],
            "invalid" => ["--configuration=../outside"],
            _ => ["-c="],
        };
        ProcessResult result = await InvokeAsync(["test", "--home", root, "-c", "Shipping", "--results-directory", reports, "--", .. forwarded], context.CancellationToken);
        Assert.AreNotEqual(0, result.ExitCode);
        string expected = partition.EndsWith("conflict", StringComparison.Ordinal) ? "Select the same configuration" :
            partition is "missing" or "separator" ? "configuration option requires a value" : "Configuration must be a nonempty directory name";
        Assert.Contains(expected, result.StandardError);
        Assert.DoesNotContain("Testing PostgreSQL", result.StandardOutput);
        Assert.IsFalse(Directory.Exists(reports));
    }

    /// <summary>
    /// Invalid selectors fail before invoking a test project or creating reports.
    /// </summary>
    /// <param name="selection">The rejected selector partition.</param>
    [TestMethod]
    [DataRow("empty-all")]
    [DataRow("missing-default")]
    [DataRow("all-major")]
    [DataRow("all-path")]
    [DataRow("low-major")]
    [DataRow("high-major")]
    [DataRow("wrong-major")]
    [DataRow("broken-registry")]
    [DataRow("configuration")]
    public async Task TestCommandRejectsInvalidSelection(string selection)
    {
        string root = CreateDirectory();
        string reports = Path.Join(root, "reports");
        string[] arguments = selection switch
        {
            "empty-all" or "broken-registry" => ["--all"],
            "missing-default" => [],
            "all-major" => ["--all", "--pg", MajorText()],
            "all-path" => ["--all", "--pg-config", s_installation.PgConfigPath],
            "low-major" => ["--pg", "12"],
            "high-major" => ["--pg", "20"],
            "wrong-major" => ["--pg", DifferentMajor().ToString(CultureInfo.InvariantCulture), "--pg-config", s_installation.PgConfigPath],
            _ => ["-c", "../outside"],
        };
        if (selection == "broken-registry")
        {
            var entries = new Dictionary<string, string>
            {
                [s_postgresKey] = s_installation.PgConfigPath,
                ["pg" + DifferentMajor().ToString(CultureInfo.InvariantCulture)] = "missing-pg-config",
            };
            await File.WriteAllTextAsync(Path.Join(root, "config.json"), JsonSerializer.Serialize(entries), context.CancellationToken);
        }

        ProcessResult result = await InvokeAsync(["test", "--home", root, "--results-directory", reports, .. arguments], context.CancellationToken);
        Assert.AreNotEqual(0, result.ExitCode);
        Assert.IsNotEmpty(result.StandardError);
        Assert.DoesNotContain("Testing PostgreSQL", result.StandardOutput);
        Assert.IsFalse(Directory.Exists(reports));
    }

    /// <summary>
    /// Fixtures reject an invalid command context or a different major before publication and retain explicit option values.
    /// </summary>
    [TestMethod]
    public async Task TestCommandRejectsConflictingFixtureInstallation()
    {
        CancellationToken token = context.CancellationToken;
        string output = await CreateTestCommandProjectAsync(token);
        await File.WriteAllTextAsync(Path.Join(output, "tests", "TestCommandProbe.Tests", "SelectionGuardTests.cs"), """
            using System.Globalization;
            using System.Runtime.CompilerServices;
            using Ankus.PgConfig;
            using Ankus.Testing;
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            namespace TestCommandProbe.Tests;
            [TestClass]
            public sealed class SelectionGuardTests(TestContext context)
            {
                [TestMethod]
                public async Task RejectsConflictingInstallationBeforePublication()
                {
                    PostgresInstallation installation = await PostgresInstallation.CreateAsync(Environment.GetEnvironmentVariable("ANKUS_TEST_PG_CONFIG")!, context.CancellationToken);
                    string project = ProjectPath();
                    var options = new PostgresExtensionTestOptions { ProjectPath = project, Installation = installation };
                    Assert.AreEqual("Debug", options.Configuration);
                    Assert.AreEqual("Custom", new PostgresExtensionTestOptions { ProjectPath = project, Configuration = "Custom" }.Configuration);
                    string? original = Environment.GetEnvironmentVariable("ANKUS_TEST_POSTGRES_MAJOR");
                    try
                    {
                        foreach (string invalid in new[] { "12", "20", " 18", "garbage" })
                        {
                            Environment.SetEnvironmentVariable("ANKUS_TEST_POSTGRES_MAJOR", invalid);
                            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PostgresExtensionTest.StartAsync(options, context.CancellationToken));
                            Assert.Contains("ANKUS_TEST_POSTGRES_MAJOR", error.Message);
                        }

                        Environment.SetEnvironmentVariable("ANKUS_TEST_POSTGRES_MAJOR", (installation.Version.Major == 13 ? 14 : 13).ToString(CultureInfo.InvariantCulture));
                        InvalidOperationException mismatch = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PostgresExtensionTest.StartAsync(options, context.CancellationToken));
                        Assert.Contains("but the fixture selected PostgreSQL", mismatch.Message);
                        InvalidOperationException clusterMismatch = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PostgresTestCluster.StartAsync(
                            new PostgresTestClusterOptions { Installation = installation }, context.CancellationToken));
                        Assert.AreEqual(mismatch.Message, clusterMismatch.Message);
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("ANKUS_TEST_POSTGRES_MAJOR", original);
                    }

                    string? session = Environment.GetEnvironmentVariable("ANKUS_TEST_SESSION_DIRECTORY");
                    try
                    {
                        Environment.SetEnvironmentVariable("ANKUS_TEST_SESSION_DIRECTORY", "relative-session");
                        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PostgresExtensionTest.StartAsync(options, context.CancellationToken));
                        Assert.Contains("existing absolute directory", error.Message);
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("ANKUS_TEST_SESSION_DIRECTORY", session);
                    }

                    string? data = Environment.GetEnvironmentVariable("ANKUS_TEST_DATA_DIRECTORY");
                    try
                    {
                        foreach (string invalid in new[] { "relative-data", Path.Join(session!, "missing-data"), project })
                        {
                            Environment.SetEnvironmentVariable("ANKUS_TEST_DATA_DIRECTORY", invalid);
                            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PostgresExtensionTest.StartAsync(options, context.CancellationToken));
                            Assert.Contains("existing absolute directory", error.Message);
                        }

                        Environment.SetEnvironmentVariable("ANKUS_TEST_DATA_DIRECTORY", data);
                        Environment.SetEnvironmentVariable("ANKUS_TEST_SESSION_DIRECTORY", null);
                        InvalidOperationException missingFixtureSession = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PostgresExtensionTest.StartAsync(options, context.CancellationToken));
                        Assert.Contains("requires an existing command session", missingFixtureSession.Message);
                        InvalidOperationException missingSession = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => PostgresTestCluster.StartAsync(
                            new PostgresTestClusterOptions { Installation = installation }, context.CancellationToken));
                        Assert.AreEqual(missingFixtureSession.Message, missingSession.Message);
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("ANKUS_TEST_DATA_DIRECTORY", data);
                        Environment.SetEnvironmentVariable("ANKUS_TEST_SESSION_DIRECTORY", session);
                    }

                    string? reuse = Environment.GetEnvironmentVariable("ANKUS_TEST_REUSE_SCHEMA");
                    try
                    {
                        foreach (string invalid in new[] { "", "1", "invalid" })
                        {
                            Environment.SetEnvironmentVariable("ANKUS_TEST_REUSE_SCHEMA", invalid);
                            InvalidOperationException error = Assert.ThrowsExactly<InvalidOperationException>(() => new PostgresExtensionTestOptions { ProjectPath = project });
                            Assert.Contains("ANKUS_TEST_REUSE_SCHEMA must be true or false", error.Message);
                        }
                    }
                    finally
                    {
                        Environment.SetEnvironmentVariable("ANKUS_TEST_REUSE_SCHEMA", reuse);
                    }

                    Assert.IsFalse(Directory.Exists(Path.Join(Path.GetDirectoryName(project)!, "bin", "ankus-test-logs")));
                }

                private static string ProjectPath([CallerFilePath] string source = "")
                    => Path.GetFullPath(Path.Join(Path.GetDirectoryName(source)!, "../../src/TestCommandProbe/TestCommandProbe.csproj"));
            }
            """, token);
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool,
            ["test", "--home", s_home, "--pg", MajorText(), "--results-directory", Path.Join(output, "guard reports"), "--",
                "--filter", "FullyQualifiedName~RejectsConflictingInstallationBeforePublication", "--report-trx", "--report-trx-filename", "guards.trx"],
            s_environment, token, workingDirectory: output);
        Assert.AreEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        XDocument report = XDocument.Load(Path.Join(output, "guard reports", s_postgresKey, "guards.trx"));
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        XElement outcome = report.Descendants(ns + "UnitTestResult").Single();
        Assert.AreEqual("RejectsConflictingInstallationBeforePublication", outcome.Attribute("testName")!.Value);
        Assert.AreEqual("Passed", outcome.Attribute("outcome")!.Value);
    }

    /// <summary>
    /// The CLI preserves an ordinary runner's failure exit and cleans up even when a host leaves a server undisposed.
    /// </summary>
    /// <param name="customData">Whether to use a custom data parent alongside another live cluster.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task TestCommandPreservesRunnerExitAndCleansAbandonedCluster(bool customData)
    {
        CancellationToken token = context.CancellationToken;
        // Both storage partitions run the same host; the second reuses the first's build.
        string host = TestCommandHostDirectory("", "Debug");
        using ReusableProjectLease lease = await AcquireReusableProjectAsync("abandoned cluster", CreateAbandonedClusterProjectAsync,
            [Path.Join(host, "abandoned-data.txt"), Path.Join(host, "abandoned-pid.txt"), Path.Join(host, "abandoned-session.txt"),
                Path.Join(host, "retained logs"), "abandoned reports", "sibling logs"], token);
        string output = lease.Directory;
        string? dataBase = customData ? CreateCustomDataBase() : null;
        string[] dataArguments = dataBase is null ? [] : ["--pgdata", dataBase];
        await using PostgresTestCluster? sibling = dataBase is null ? null : await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
        {
            Installation = s_installation,
            DataDirectoryBase = dataBase,
            LogDirectory = Path.Join(output, "sibling logs"),
        }, token);
        string hostRoot = Path.Join(output, "tests", "TestCommandProbe.Tests");
        string reports = Path.Join(output, "abandoned reports");
        ProcessResult result = await PackageProcessRunner.RunAsync(s_tool,
            ["test", "--home", s_home, "--all", .. dataArguments, "--results-directory", reports, "--", "--filter", "FullyQualifiedName~AbandonsCluster"],
            s_environment, token, workingDirectory: output);
        Assert.AreNotEqual(0, result.ExitCode, result.StandardOutput + result.StandardError);
        Assert.Contains(s_postgresKey + ": dotnet test exited " + result.ExitCode.ToString(CultureInfo.InvariantCulture) + ".", result.StandardOutput,
            result.StandardError);
        string executionDirectory = TestCommandHostDirectory(output, "Debug");
        string dataDirectory = await File.ReadAllTextAsync(Path.Join(executionDirectory, "abandoned-data.txt"), token);
        string session = await File.ReadAllTextAsync(Path.Join(executionDirectory, "abandoned-session.txt"), token);
        Assert.IsFalse(Directory.Exists(dataDirectory));
        Assert.IsFalse(Directory.Exists(session));
        int serverId = int.Parse(await File.ReadAllTextAsync(Path.Join(executionDirectory, "abandoned-pid.txt"), token), CultureInfo.InvariantCulture);
        AssertServerExited(serverId);
        Assert.IsNotEmpty(Directory.GetFiles(Path.Join(executionDirectory, "retained logs"), "*.log"));
        Assert.IsTrue(File.Exists(Path.Join(hostRoot, "AbandonedTests.cs")), "Unrelated author files must remain intact.");
        if (dataBase is not null)
        {
            Assert.StartsWith(dataBase + Path.DirectorySeparatorChar, dataDirectory);
            Assert.AreEqual("preserve custom parent", await File.ReadAllTextAsync(Path.Join(dataBase, "unrelated.txt"), token));
            Assert.AreSequenceEqual<string>([sibling!.DataDirectory], Directory.GetDirectories(dataBase));
            await using NpgsqlConnection connection = await sibling.OpenConnectionAsync(token);
            await using var command = new NpgsqlCommand("CREATE TABLE still_owned (value integer); INSERT INTO still_owned VALUES (42); SELECT value FROM still_owned", connection);
            Assert.AreEqual(42, await command.ExecuteScalarAsync(token));
        }
    }

    /// <summary>
    /// Generates the selection probe with a host test that abandons its cluster by terminating the host abruptly.
    /// </summary>
    /// <param name="parent">The class-owned directory that receives the solution.</param>
    /// <param name="token">Cancels generation and writes.</param>
    /// <returns>The generated solution directory.</returns>
    private static async Task<string> CreateAbandonedClusterProjectAsync(string parent, CancellationToken token)
    {
        string output = await CreateTestCommandProjectAsync(parent, token);
        string hostRoot = Path.Join(output, "tests", "TestCommandProbe.Tests");
        await File.WriteAllTextAsync(Path.Join(hostRoot, "AbandonedTests.cs"), """
            using Ankus.PgConfig;
            using Ankus.Testing;
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            namespace TestCommandProbe.Tests;
            [TestClass]
            public sealed class AbandonedTests(TestContext context)
            {
                [TestMethod]
                public async Task AbandonsCluster()
                {
                    Assert.AreEqual("Debug", typeof(AbandonedTests).Assembly.GetCustomAttributes(typeof(System.Reflection.AssemblyConfigurationAttribute), false)
                        .Cast<System.Reflection.AssemblyConfigurationAttribute>().Single().Configuration);
                    PostgresInstallation installation = await PostgresInstallation.CreateAsync(Environment.GetEnvironmentVariable("ANKUS_TEST_PG_CONFIG")!, context.CancellationToken);
                    PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
                    {
                        Installation = installation,
                        LogDirectory = "retained logs",
                    }, context.CancellationToken);
                    await File.WriteAllTextAsync("abandoned-data.txt", cluster.DataDirectory, context.CancellationToken);
                    await File.WriteAllTextAsync("abandoned-pid.txt", (await File.ReadAllLinesAsync(Path.Join(cluster.DataDirectory, "postmaster.pid"), context.CancellationToken))[0], context.CancellationToken);
                    await File.WriteAllTextAsync("abandoned-session.txt", Environment.GetEnvironmentVariable("ANKUS_TEST_SESSION_DIRECTORY")!, context.CancellationToken);
                    // Abrupt termination bypasses the cluster's normal ProcessExit cleanup.
                    System.Diagnostics.Process.GetCurrentProcess().Kill();
                }
            }
            """, token);
        return output;
    }

    /// <summary>
    /// Terminating the owning command stops its active host and PostgreSQL server before removing session storage.
    /// </summary>
    /// <param name="customData">Whether command-owned cluster data lives beneath a custom parent.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task TestCommandCancellationStopsCluster(bool customData)
    {
        CancellationToken token = context.CancellationToken;
        // Both storage partitions run the same host; the second reuses the first's build.
        string host = TestCommandHostDirectory("", "Debug");
        using ReusableProjectLease lease = await AcquireReusableProjectAsync("command cancellation", CreateCancellationProjectAsync,
            [Path.Join(host, "cancellation-pending.txt"), Path.Join(host, "cancellation-ready.txt"), Path.Join(host, "retained logs")],
            token);
        string output = lease.Directory;
        string? dataBase = customData ? CreateCustomDataBase() : null;
        string[] dataArguments = dataBase is null ? [] : ["--pgdata", dataBase];
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(s_tool)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = output,
            },
        };
        string[] commandArguments = ["test", "--home", s_home, "--pg", MajorText(), .. dataArguments, "--", "--filter", "FullyQualifiedName~WaitsForCommandCancellation"];
        foreach (string argument in commandArguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        foreach ((string name, string? value) in s_environment)
        {
            process.StartInfo.Environment[name] = value;
        }

        Assert.IsTrue(process.Start());
        Task<string> standardOutput = process.StandardOutput.ReadToEndAsync(token);
        Task<string> standardError = process.StandardError.ReadToEndAsync(token);
        string executionDirectory = TestCommandHostDirectory(output, "Debug");
        string ready = Path.Join(executionDirectory, "cancellation-ready.txt");
        try
        {
            using var startup = CancellationTokenSource.CreateLinkedTokenSource(token);
            startup.CancelAfter(TimeSpan.FromMinutes(3));
            while (!File.Exists(ready) && !process.HasExited)
            {
                await Task.Delay(100, startup.Token);
            }

            Assert.IsFalse(process.HasExited, process.HasExited ? await standardOutput + await standardError : "");
            ProcessResult signal = await PackageProcessRunner.RunAsync("/bin/kill", ["-TERM", process.Id.ToString(CultureInfo.InvariantCulture)],
                new Dictionary<string, string?>(), token);
            Assert.AreEqual(0, signal.ExitCode, signal.StandardError);
            using var shutdown = CancellationTokenSource.CreateLinkedTokenSource(token);
            shutdown.CancelAfter(TimeSpan.FromSeconds(45));
            await process.WaitForExitAsync(shutdown.Token);
            Assert.AreEqual(130, process.ExitCode, await standardOutput + await standardError);
            string[] evidence = await File.ReadAllLinesAsync(ready, token);
            Assert.HasCount(4, evidence);
            Assert.IsFalse(Directory.Exists(evidence[0]));
            Assert.IsFalse(Directory.Exists(evidence[2]));
            Assert.IsFalse(Directory.Exists(evidence[3]));
            if (dataBase is not null)
            {
                Assert.StartsWith(dataBase + Path.DirectorySeparatorChar, evidence[2]);
                Assert.IsEmpty(Directory.GetDirectories(dataBase));
                Assert.AreEqual("preserve custom parent", await File.ReadAllTextAsync(Path.Join(dataBase, "unrelated.txt"), token));
            }

            AssertServerExited(int.Parse(evidence[1], CultureInfo.InvariantCulture));
            Assert.IsNotEmpty(Directory.GetFiles(Path.Join(executionDirectory, "retained logs"), "*.log"));
        }
        finally
        {
            if (!process.HasExited)
            {
                await PackageProcessRunner.RunAsync("/bin/kill", ["-TERM", process.Id.ToString(CultureInfo.InvariantCulture)],
                    new Dictionary<string, string?>(), CancellationToken.None);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(45));
                try
                {
                    await process.WaitForExitAsync(cleanup.Token);
                }
                catch (OperationCanceledException) when (cleanup.IsCancellationRequested)
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
            }

            if (File.Exists(ready))
            {
                string[] evidence = await File.ReadAllLinesAsync(ready, CancellationToken.None);
                string session = evidence[0];
                if (Directory.Exists(evidence[3]))
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                    foreach (string pid in Directory.EnumerateFiles(evidence[3], "postmaster.pid", SearchOption.AllDirectories))
                    {
                        await PackageProcessRunner.RunCheckedAsync(s_installation.PgCtlPath,
                            ["stop", "-D", Path.GetDirectoryName(pid)!, "-m", "immediate", "-w", "-t", "25"],
                            new Dictionary<string, string?>(), cleanup.Token);
                    }

                    Directory.Delete(evidence[3], recursive: true);
                }

                if (Directory.Exists(session))
                {
                    Directory.Delete(session, recursive: true);
                }
            }
        }
    }

    /// <summary>
    /// Generates the selection probe with a host test that waits for its owning command to be cancelled.
    /// </summary>
    /// <param name="parent">The class-owned directory that receives the solution.</param>
    /// <param name="token">Cancels generation and writes.</param>
    /// <returns>The generated solution directory.</returns>
    private static async Task<string> CreateCancellationProjectAsync(string parent, CancellationToken token)
    {
        string output = await CreateTestCommandProjectAsync(parent, token);
        await File.WriteAllTextAsync(Path.Join(output, "tests", "TestCommandProbe.Tests", "CancellationTests.cs"), """
            using Ankus.PgConfig;
            using Ankus.Testing;
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            namespace TestCommandProbe.Tests;
            [TestClass]
            public sealed class CancellationTests(TestContext context)
            {
                [TestMethod]
                public async Task WaitsForCommandCancellation()
                {
                    PostgresInstallation installation = await PostgresInstallation.CreateAsync(Environment.GetEnvironmentVariable("ANKUS_TEST_PG_CONFIG")!, context.CancellationToken);
                    await using PostgresTestCluster cluster = await PostgresTestCluster.StartAsync(new PostgresTestClusterOptions
                    {
                        Installation = installation,
                        LogDirectory = "retained logs",
                    }, context.CancellationToken);
                    string serverId = (await File.ReadAllLinesAsync(Path.Join(cluster.DataDirectory, "postmaster.pid"), context.CancellationToken))[0];
                    await File.WriteAllLinesAsync("cancellation-pending.txt", [Environment.GetEnvironmentVariable("ANKUS_TEST_SESSION_DIRECTORY")!, serverId, cluster.DataDirectory, Environment.GetEnvironmentVariable("ANKUS_TEST_DATA_DIRECTORY")!], context.CancellationToken);
                    File.Move("cancellation-pending.txt", "cancellation-ready.txt");
                    await Task.Delay(Timeout.InfiniteTimeSpan, context.CancellationToken);
                }
            }
            """, token);
        return output;
    }

    private static void AssertServerExited(int processId)
    {
        try
        {
            using Process server = Process.GetProcessById(processId);
            Assert.IsTrue(server.HasExited, "The owned PostgreSQL server is still running.");
        }
        catch (ArgumentException)
        {
            // The operating system has already reaped the owned server.
        }
    }

    private string CreateCustomDataBase()
    {
        string root = Path.Join(CreateDirectory(), OperatingSystem.IsWindows() ? "data" : "custom data with spaces");
        if (!OperatingSystem.IsWindows())
        {
            root = Path.Join(root, new string('d', 140));
        }

        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Join(root, "unrelated.txt"), "preserve custom parent");
        return root;
    }

    private static string TestCommandHostDirectory(string project, string configuration)
        => Path.Join(project, "tests", "TestCommandProbe.Tests", "bin", configuration, "net10.0");

    private Task<string> CreateTestCommandProjectAsync(CancellationToken token)
        => CreateTestCommandProjectAsync(CreateDirectory(), token);

    /// <summary>
    /// Generates the selection probe beneath a case-owned or class-owned directory.
    /// </summary>
    /// <param name="parent">The directory that receives the generated solution.</param>
    /// <param name="token">Cancels generation and writes.</param>
    /// <returns>The generated solution directory.</returns>
    private static async Task<string> CreateTestCommandProjectAsync(string parent, CancellationToken token)
    {
        string output = Path.Join(parent, "test command project");
        (await InvokeAsync(["new", "TestCommandProbe", "-o", output], token)).EnsureSuccess(s_tool, ["new"]);
        string buildFile = Path.Join(output, "Directory.Build.props");
        XDocument build = XDocument.Load(buildFile);
        build.Root!.Add(new XElement("PropertyGroup", new XAttribute("Condition", "'$(Configuration)' == 'Shipping'"),
            new XElement("DefineConstants", "$(DefineConstants);TEST_CONFIGURATION")));
        build.Root.Add(new XElement("PropertyGroup", new XAttribute("Condition", "'$(ForwardedFixtureProperty)' == 'enabled'"),
            new XElement("DefineConstants", "$(DefineConstants);TEST_FORWARDED_FIXTURE_PROPERTY")));
        build.Root.Add(new XElement("PropertyGroup", new XAttribute("Condition", "'$(ForwardedOutputRoot)' != ''"),
            new XElement("BaseIntermediateOutputPath", "$(ForwardedOutputRoot)/$(MSBuildProjectName)/")));
        build.Root.Add(new XElement("ItemGroup", new XElement("AssemblyMetadata", new XAttribute("Include", "SelectedMajor"),
            new XAttribute("Value", "$(AnkusPostgresMajor)"))));
        build.Save(buildFile);
        await File.WriteAllTextAsync(Path.Join(output, "src", "TestCommandProbe", "ConfigurationProbe.cs"), """
            using Ankus;
            namespace TestCommandProbe;
            public static class ConfigurationProbe
            {
                [PgFunction]
                public static int TestConfiguration()
                {
            #if TEST_CONFIGURATION
            #if TEST_FORWARDED_FIXTURE_PROPERTY
                    return 42;
            #else
                    return -2;
            #endif
            #else
                    return -1;
            #endif
                }
            }
            """, token);
        await File.WriteAllTextAsync(Path.Join(output, "tests", "TestCommandProbe.Tests", "SelectionTests.cs"), """
            using System.Globalization;
            using System.Reflection;
            using Microsoft.VisualStudio.TestTools.UnitTesting;
            using Npgsql;
            namespace TestCommandProbe.Tests;
            public sealed partial class BackendTests
            {
                [TestMethod]
                public async Task ConfigurationAndVersionReachBackend()
                {
                    int expected = int.Parse(Environment.GetEnvironmentVariable("ANKUS_TEST_POSTGRES_MAJOR")!, CultureInfo.InvariantCulture);
                    string built = typeof(BackendTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(value => value.Key == "SelectedMajor").Value!;
                    Assert.AreEqual(expected.ToString(CultureInfo.InvariantCulture), built);
                    Assert.AreEqual(42, ConfigurationProbe.TestConfiguration(), "The host must use Shipping.");
                    await using NpgsqlConnection connection = await s_extension!.Cluster.OpenConnectionAsync(context.CancellationToken);
                    await using var command = new NpgsqlCommand("SELECT current_setting('server_version_num')::integer / 10000, test_configuration(), current_setting('data_directory'), current_setting('unix_socket_directories')", connection);
                    await using NpgsqlDataReader reader = await command.ExecuteReaderAsync(context.CancellationToken);
                    Assert.IsTrue(await reader.ReadAsync(context.CancellationToken));
                    Assert.AreEqual(expected, reader.GetInt32(0));
                    Assert.AreEqual(42, reader.GetInt32(1), "The fixture publication must use Shipping.");
                    Assert.AreEqual(s_extension.Cluster.DataDirectory, Path.GetFullPath(reader.GetString(2)));
                    Assert.AreEqual(s_extension.Cluster.SocketDirectory ?? "", reader.GetString(3));
                    await File.WriteAllTextAsync("data-path.txt", Path.GetFullPath(reader.GetString(2)), context.CancellationToken);
                    await File.WriteAllTextAsync("session-path.txt", Environment.GetEnvironmentVariable("ANKUS_TEST_SESSION_DIRECTORY")!, context.CancellationToken);
                }
            }
            """, token);
        return output;
    }
}
