using System.Text.Json;
using System.Xml.Linq;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Invalid framework choices fail before a template or tool creates a consumer directory.
    /// </summary>
    /// <param name="template">Whether to invoke dotnet new rather than ankus new.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task UnknownTestFrameworkCreatesNoOutput(bool template)
    {
        CancellationToken token = context.CancellationToken;
        string output = Path.Combine(CreateDirectory(), "invalid framework output");
        ProcessResult result;
        if (template)
        {
            string hive = Path.Combine(CreateDirectory(), "isolated invalid template hive");
            string package = Path.Combine(s_root, "feed", "Ankus.Templates." + s_version + ".nupkg");
            (await PackageProcessRunner.RunAsync("dotnet", ["new", "install", package, "--debug:custom-hive", hive],
                s_environment, token, workingDirectory: s_root)).EnsureSuccess("dotnet", ["new", "install"]);
            result = await PackageProcessRunner.RunAsync("dotnet", ["new", "ankus", "--name", "UnknownFramework", "--output", output,
                "--test-framework", "unknown", "--debug:custom-hive", hive], s_environment, token, workingDirectory: s_root);
        }
        else
        {
            result = await InvokeAsync(["new", "UnknownFramework", "--output", output, "--test-framework", "unknown"], token);
        }

        Assert.AreNotEqual(0, result.ExitCode);
        Assert.Contains("unknown", result.StandardOutput + result.StandardError);
        Assert.IsFalse(Directory.Exists(output));
    }

    /// <summary>
    /// Each optional framework discovers managed and named native cases, skips explicitly ignored cases and disposes its real backend fixture.
    /// </summary>
    /// <param name="framework">The selected consumer test framework.</param>
    /// <param name="template">Whether to use the installed dotnet template rather than the tool.</param>
    /// <param name="worker">Whether the native worker must run in a separate process.</param>
    [TestMethod]
    [DataRow("xunit", false, false)]
    [DataRow("nunit", false, false)]
    [DataRow("xunit", true, false)]
    [DataRow("nunit", true, false)]
    [DataRow("xunit", false, true)]
    [DataRow("nunit", false, true)]
    [DataRow("xunit", true, true)]
    [DataRow("nunit", true, true)]
    public async Task FrameworkTemplatesRunManagedAndBackendTests(string framework, bool template, bool worker)
    {
        CancellationToken token = context.CancellationToken;
        const string Name = "Acme.Probe";
        string output = Path.Combine(CreateDirectory(), "framework with spaces");
        if (template)
        {
            string hive = Path.Combine(CreateDirectory(), "isolated template hive");
            string package = Path.Combine(s_root, "feed", "Ankus.Templates." + s_version + ".nupkg");
            (await PackageProcessRunner.RunAsync("dotnet", ["new", "install", package, "--debug:custom-hive", hive],
                s_environment, token, workingDirectory: s_root)).EnsureSuccess("dotnet", ["new", "install"]);
            (await PackageProcessRunner.RunAsync("dotnet", ["new", worker ? "ankus-worker" : "ankus", "--name", Name,
                "--output", output, "--test-framework", framework, "--debug:custom-hive", hive],
                s_environment, token, workingDirectory: s_root)).EnsureSuccess("dotnet", ["new"]);
        }
        else
        {
            string[] options = worker ? ["--background-worker"] : [];
            (await InvokeAsync(["new", Name, "--output", output, "--test-framework", framework, .. options], token))
                .EnsureSuccess(s_tool, ["new"]);
        }

        using JsonDocument global = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(output, "global.json"), token));
        Assert.AreEqual("Microsoft.Testing.Platform", global.RootElement.GetProperty("test").GetProperty("runner").GetString());
        Assert.IsFalse(global.RootElement.GetProperty("msbuild-sdks").TryGetProperty("MSTest.Sdk", out _));
        XDocument project = XDocument.Load(Path.Combine(output, "tests", Name + ".Tests", Name + ".Tests.csproj"));
        Assert.AreEqual("Microsoft.NET.Sdk", (string?)project.Root!.Attribute("Sdk"));
        Assert.AreEqual("Exe", project.Descendants("OutputType").Single().Value);
        string[] selected = [.. project.Descendants("PackageReference").Select(static reference => (string)reference.Attribute("Include")!)];
        Assert.Contains(framework == "xunit" ? "xunit.v3.mtp-v2" : "NUnit", selected);
        Assert.Contains("Microsoft.Testing.Extensions.TrxReport", selected);
        Assert.DoesNotContain(framework == "xunit" ? "NUnit" : "xunit.v3.mtp-v2", selected);
        Assert.IsFalse(Directory.Exists(Path.Combine(output, "Frameworks")));
        string[] references = [.. project.Descendants("ProjectReference").Select(static reference => (string)reference.Attribute("Include")!)];
        Assert.AreSequenceEqual(["../../src/" + Name + "/" + Name + ".csproj"], references);
        (await PackageProcessRunner.RunAsync("dotnet", ["sln", Name + ".slnx", "list"], s_environment, token,
            workingDirectory: output)).EnsureSuccess("dotnet", ["sln", "list"]);
        string extensionDirectory = Path.Combine(output, "src", Name);
        Dictionary<string, string?> environment = CreateConsumerEnvironment();
        await File.AppendAllTextAsync(Path.Combine(extensionDirectory, "BackendChecks.cs"), """

            public static partial class BackendChecks
            {
                [PgTest(IgnoreReason = "Template discovery skip")]
                public static void IgnoredNativeCase()
                    => throw new InvalidOperationException("The ignored backend case must never execute.");
            }
            """, token);
        await RestoreConsumerToolAsync(output, environment, token);
        // The tool and installed template generate byte-identical non-worker solutions; worker
        // templates differ in their shared-memory identities. Each distinct tree runs once.
        string[] test = ["test", "--report-trx", "-p:AnkusPostgresMajor=" + MajorText()];
        SharedOutput run = await GetSharedOutputAsync(ContentKey(output, ["dotnet", .. test]),
            shared => TestSharedSolutionAsync(CopySharedSnapshot(output), Name, test, environment, shared), token);
        string solution = run.Directory;
        run.Listing!.EnsureSuccess("dotnet", ["test", "--list-tests"]);
        Assert.IsFalse(run.PublishedDuringListing, "Listing tests must not publish the extension or start PostgreSQL.");
        Assert.Contains("AdditionInsidePostgres", run.Listing.StandardOutput);
        Assert.Contains("IgnoredNativeCase", run.Listing.StandardOutput);
        run.Result.EnsureSuccess("dotnet", ["test"]);
        XDocument report = XDocument.Load(Directory.GetFiles(solution, "*.trx", SearchOption.AllDirectories).Single());
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        XElement counters = report.Descendants(ns + "Counters").Single();
        Assert.AreEqual(worker ? "9" : "8", counters.Attribute("total")!.Value);
        Assert.AreEqual(worker ? "8" : "7", counters.Attribute("passed")!.Value);
        Assert.AreEqual("0", counters.Attribute("failed")!.Value);
        XElement[] cases = [.. report.Descendants(ns + "UnitTestResult")];
        string[] names = [.. cases.Select(static element => element.Attribute("testName")!.Value)];
        Assert.HasCount(names.Length, names.Distinct(StringComparer.Ordinal));
        Assert.AreEqual(2, names.Count(static name => name.Contains("AddReturnsSum", StringComparison.Ordinal)));
        Assert.ContainsSingle(names.Where(static name => name.Contains("AddRejectsOverflow", StringComparison.Ordinal)));
        Assert.ContainsSingle(names.Where(static name => name.Contains("FunctionsExecuteInPostgres", StringComparison.Ordinal)));
        Assert.ContainsSingle(names.Where(static name => name.Contains("ManagedErrorsLeaveBackendUsable", StringComparison.Ordinal)));
        Assert.Contains(Name + ".BackendChecks.AdditionInsidePostgres()", names.Select(name => NativeReportIdentity(name, framework)));
        Assert.Contains(Name + ".BackendChecks.ExpectedFailure()", names.Select(name => NativeReportIdentity(name, framework)));
        XElement ignored = cases.Single(element => NativeReportIdentity(element.Attribute("testName")!.Value, framework) == Name + ".BackendChecks.IgnoredNativeCase()");
        Assert.AreEqual("NotExecuted", ignored.Attribute("outcome")!.Value);
        Assert.Contains("Template discovery skip", ignored.Value);
        if (worker)
        {
            Assert.ContainsSingle(names.Where(static name => name.Contains("WorkerRunsInAnotherPostgresProcess", StringComparison.Ordinal)));
        }

        string testedExtension = Path.Combine(solution, "src", Name);
        Assert.IsFalse(Directory.Exists(Path.Combine(testedExtension, "bin", "ankus-test-pgdata")));
        Assert.IsEmpty(Directory.GetDirectories(Path.Combine(testedExtension, "bin", "ankus-test-publish")));
        Assert.IsNotEmpty(Directory.GetFiles(Path.Combine(testedExtension, "bin", "ankus-test-logs"), "*.log"));
    }

    /// <summary>
    /// Lists, then runs, a generated solution's tests in class-owned storage so every case with the same files reads one
    /// result. The listing discovers backend cases from the compiled catalog, before anything is published.
    /// </summary>
    /// <param name="solution">The class-owned copy of the generated solution.</param>
    /// <param name="name">The generated extension's name.</param>
    /// <param name="arguments">The dotnet test arguments that form part of the content key.</param>
    /// <param name="environment">The consumer's process environment.</param>
    /// <param name="token">Cancels the class-owned run.</param>
    /// <returns>The tested solution, its runner result and its listing.</returns>
    private static async Task<SharedOutput> TestSharedSolutionAsync(string solution, string name, string[] arguments,
        IReadOnlyDictionary<string, string?> environment, CancellationToken token)
    {
        ProcessResult listing = await PackageProcessRunner.RunAsync("dotnet",
            ["test", "--list-tests", .. arguments.Where(static argument => argument.StartsWith("-p:", StringComparison.Ordinal))],
            environment, token, workingDirectory: solution);
        string publish = Path.Combine(solution, "src", name, "bin", "ankus-test-publish");
        bool published = Directory.Exists(publish) && Directory.EnumerateFileSystemEntries(publish).Any();
        ProcessResult result = await PackageProcessRunner.RunAsync("dotnet", arguments, environment, token, workingDirectory: solution);
        return new SharedOutput(solution, result, listing, published);
    }

    /// <summary>
    /// Separates xUnit's serialized theory argument suffix from the exact managed backend identity.
    /// </summary>
    /// <param name="name">The actual test name from the framework's report.</param>
    /// <param name="framework">The selected consumer framework.</param>
    /// <returns>The unchanged catalog identity, or the complete ordinary test name.</returns>
    private static string NativeReportIdentity(string name, string framework)
    {
        int suffix = framework == "xunit" ? name.IndexOf("(name: ", StringComparison.Ordinal) : -1;
        return suffix < 0 ? name : name[..suffix];
    }
}
