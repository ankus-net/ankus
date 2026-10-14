using System.Text.Json.Nodes;
using System.Xml.Linq;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Builds and test hosts retain the caller's relative registry through their different working directories.
    /// </summary>
    /// <param name="homeName">The relative registry directory, including environment-list separators.</param>
    [TestMethod]
    [DataRow("registry home")]
    [DataRow("registry; home")]
    public async Task RelativeHomeRemainsSelectedAcrossBuildAndTestHosts(string homeName)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string home = Path.Join(directory, homeName);
        Directory.CreateDirectory(home);
        string configuration = Path.Join(home, "config.json");
        const string Missing = "missing/bin/pg_config";
        await WriteRegistrationAsync(Missing);
        string output = Path.Join(directory, "nested", "consumer");
        (await InvokeAsync(["new", "HomeProbe", "--output", output], token)).EnsureSuccess(s_tool, ["new"]);
        string project = Path.Join(output, "src", "HomeProbe", "HomeProbe.csproj");
        var environment = new Dictionary<string, string?>(s_environment, StringComparer.Ordinal)
        {
            ["ANKUS_HOME"] = homeName,
            ["ANKUS_TEST_PG_CONFIG"] = null,
            ["AnkusPgConfigPath"] = null,
            ["NUGET_PACKAGES"] = Path.Join(directory, "packages"),
            ["ANKUS_TEST_EXPECTED_HOME"] = home,
        };

        ProcessResult rejected = await PackageProcessRunner.RunAsync("dotnet",
            ["build", project, "-p:AnkusPostgresMajor=" + MajorText()], environment, token, workingDirectory: directory);
        Assert.AreNotEqual(0, rejected.ExitCode, "An unavailable registered server must not fall back to another installation.");
        Assert.Contains(Path.GetFullPath(Missing, home), rejected.StandardOutput + rejected.StandardError);
        Assert.AreEqual(Missing, JsonNode.Parse(await File.ReadAllTextAsync(configuration, token))![s_postgresKey]!.GetValue<string>());

        await WriteRegistrationAsync(s_installation.PgConfigPath);
        await File.WriteAllTextAsync(Path.Join(output, "tests", "HomeProbe.Tests", "HomeSelectionTests.cs"), """
            using Microsoft.VisualStudio.TestTools.UnitTesting;

            namespace HomeProbe.Tests;

            [TestClass]
            public sealed class HomeSelectionTests
            {
                [TestMethod]
                public void HostRetainsAbsoluteCallerHome()
                {
                    string expected = Environment.GetEnvironmentVariable("ANKUS_TEST_EXPECTED_HOME")!;
                    string? actual = AppContext.GetData("Ankus.Testing.HomeDirectory") as string;
                    Assert.AreEqual(expected, actual);
                    Assert.IsTrue(Path.IsPathFullyQualified(actual!));
                }
            }
            """, token);
        string reports = Path.Join(directory, "reports");
        ProcessResult tested = await PackageProcessRunner.RunAsync("dotnet",
            ["test", "--solution", Path.Join(output, "HomeProbe.slnx"), "--report-trx", "--results-directory", reports,
                "-p:AnkusPostgresMajor=" + MajorText()],
            environment, token, workingDirectory: directory);
        tested.EnsureSuccess("dotnet", ["test"]);
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        XDocument report = XDocument.Load(Directory.GetFiles(reports, "*.trx", SearchOption.AllDirectories).Single());
        XElement counters = report.Descendants(ns + "Counters").Single();
        Assert.AreEqual("8", counters.Attribute("total")!.Value);
        Assert.AreEqual("8", counters.Attribute("passed")!.Value);
        Assert.AreEqual("0", counters.Attribute("failed")!.Value);
        Assert.AreEqual("0", counters.Attribute("notExecuted")!.Value);
        string[] names = [.. report.Descendants(ns + "UnitTestResult").Select(result => result.Attribute("testName")!.Value)];
        Assert.Contains("HostRetainsAbsoluteCallerHome", names);
        Assert.Contains("FunctionsExecuteInPostgres", names);
        Assert.Contains("ManagedErrorsLeaveBackendUsable", names);
        Assert.Contains("HomeProbe.BackendChecks.AdditionInsidePostgres()", names);
        Assert.Contains("HomeProbe.BackendChecks.ExpectedFailure()", names);
        string bin = Path.Join(Path.GetDirectoryName(project)!, "bin");
        Assert.IsFalse(Directory.Exists(Path.Join(bin, "ankus-test-pgdata")));
        Assert.IsEmpty(Directory.GetDirectories(Path.Join(bin, "ankus-test-publish")));

        // A built host must keep the selected home even when launched from another directory.
        await WriteRegistrationAsync(Missing);
        ProcessResult rejectedHost = await PackageProcessRunner.RunAsync("dotnet",
            ["test", "--solution", Path.Join(output, "HomeProbe.slnx"), "--no-build", "--report-trx",
                "--results-directory", Path.Join(directory, "rejected-host-reports"),
                "-p:AnkusPostgresMajor=" + MajorText()], environment, token, workingDirectory: output);
        Assert.AreNotEqual(0, rejectedHost.ExitCode, "The test host must retain the build's registration instead of discovering another server.");
        Assert.Contains(Path.GetFullPath(Missing, home), rejectedHost.StandardOutput + rejectedHost.StandardError);
        Assert.IsEmpty(Directory.GetDirectories(Path.Join(bin, "ankus-test-publish")));

        async Task WriteRegistrationAsync(string path)
            => await File.WriteAllTextAsync(configuration, new JsonObject { [s_postgresKey] = path }.ToJsonString(), token);
    }
}
