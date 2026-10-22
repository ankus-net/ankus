using System.IO.Compression;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    private static readonly SemaphoreSlim s_consumerToolRestoreLock = new(1, 1);

    /// <summary>
    /// Shares restored packages across generated consumers without nesting native library paths beneath their solutions.
    /// </summary>
    /// <returns>The consumer's process-local environment.</returns>
    private static Dictionary<string, string?> CreateConsumerEnvironment()
        => new(s_environment, StringComparer.Ordinal);

    /// <summary>
    /// Installed templates pin every Ankus component and execute managed and real backend tests without repository policy,
    /// and their sources have no unnecessary using directives.
    /// </summary>
    /// <param name="template">The installed template short name.</param>
    /// <param name="name">The managed solution name.</param>
    /// <param name="extensionName">The independently expected SQL name.</param>
    /// <param name="managedNamespace">The C# namespace shown in the backend catalog.</param>
    /// <param name="worker">Whether the native worker must execute in another backend process.</param>
    [TestMethod]
    [DataRow("ankus", "Acme.HTTPProbe", "acme_http_probe", "Acme.HTTPProbe", false)]
    [DataRow("ankus-worker", "class.select", "class_select", "@class.select", true)]
    [DataRow("ankus", "1Ext", "_1_ext", "_1Ext", false)]
    public async Task InstalledTemplateRunsManagedAndBackendTests(string template, string name, string extensionName, string managedNamespace, bool worker)
    {
        CancellationToken token = context.CancellationToken;
        string output = Path.Join(CreateDirectory(), "template solution with spaces");
        await CreateTemplateAsync(template, name, output, token);
        using JsonDocument global = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(output, "global.json"), token));
        Assert.AreEqual(s_version, global.RootElement.GetProperty("msbuild-sdks").GetProperty("Ankus.Sdk").GetString());
        Assert.AreEqual("Microsoft.Testing.Platform", global.RootElement.GetProperty("test").GetProperty("runner").GetString());
        using JsonDocument tools = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(output, ".config", "dotnet-tools.json"), token));
        Assert.AreEqual(s_version, tools.RootElement.GetProperty("tools").GetProperty("ankus.tool").GetProperty("version").GetString());
        JsonElement commands = tools.RootElement.GetProperty("tools").GetProperty("ankus.tool").GetProperty("commands");
        Assert.AreEqual(1, commands.GetArrayLength());
        Assert.AreEqual("ankus", commands[0].GetString());
        XDocument packages = XDocument.Load(Path.Join(output, "Directory.Packages.props"));
        Assert.AreEqual(s_version, packages.Descendants("PackageVersion")
            .Single(element => (string?)element.Attribute("Include") == "Ankus.Testing").Attribute("Version")!.Value);
        string project = Path.Join(output, "src", name, name + ".csproj");
        Assert.AreEqual(extensionName, XDocument.Load(project).Descendants("AnkusExtensionName").Single().Value);
        XDocument properties = XDocument.Load(Path.Join(output, "Directory.Build.props"));
        Assert.IsEmpty(properties.Descendants("TreatWarningsAsErrors"));
        Assert.IsEmpty(properties.Descendants("EnforceCodeStyleInBuild"));
        Assert.IsFalse(File.Exists(Path.Join(output, ".editorconfig")));
        Assert.IsTrue(File.Exists(Path.Join(output, ".gitignore")));
        await AssertTemplateIgnoreAsync(output, name, token);

        Dictionary<string, string?> environment = CreateConsumerEnvironment();
        await RestoreConsumerToolAsync(output, environment, token);
        ProcessResult help = await PackageProcessRunner.RunAsync("dotnet", ["ankus", "--help"], environment, token, workingDirectory: output);
        help.EnsureSuccess("dotnet", ["ankus", "--help"]);
        Assert.Contains("PostgreSQL", help.StandardOutput);

        // A consumer that enforces code style finds no unnecessary using directives, such as MSTest's namespace, which
        // MSTest already imports when implicit usings are enabled.
        await File.WriteAllTextAsync(Path.Join(output, ".editorconfig"), "[*.cs]\ndotnet_diagnostic.IDE0005.severity = error\n", token);
        ProcessResult tests = await PackageProcessRunner.RunAsync("dotnet", ["test", "--report-trx", "-p:AnkusPostgresMajor=" + MajorText(),
            "-p:EnforceCodeStyleInBuild=true", "-p:GenerateDocumentationFile=true"], environment, token, workingDirectory: output);
        tests.EnsureSuccess("dotnet", ["test"]);
        XDocument report = XDocument.Load(Directory.GetFiles(output, "*.trx", SearchOption.AllDirectories).Single());
        XNamespace ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
        XElement counters = report.Descendants(ns + "Counters").Single();
        Assert.AreEqual(worker ? "8" : "7", counters.Attribute("total")!.Value);
        Assert.AreEqual(counters.Attribute("total")!.Value, counters.Attribute("passed")!.Value);
        Assert.AreEqual("0", counters.Attribute("failed")!.Value);
        string[] names = [.. report.Descendants(ns + "UnitTestResult").Select(element => element.Attribute("testName")!.Value)];
        Assert.Contains("FunctionsExecuteInPostgres", names);
        Assert.Contains("ManagedErrorsLeaveBackendUsable", names);
        Assert.Contains(managedNamespace + ".BackendChecks.AdditionInsidePostgres()", names);
        Assert.Contains(managedNamespace + ".BackendChecks.ExpectedFailure()", names);
        if (worker)
        {
            Assert.Contains("WorkerRunsInAnotherPostgresProcess", names);
        }

        string projectDirectory = Path.GetDirectoryName(project)!;
        Assert.IsFalse(Directory.Exists(Path.Join(projectDirectory, "bin", "ankus-test-pgdata")));
        Assert.IsEmpty(Directory.GetDirectories(Path.Join(projectDirectory, "bin", "ankus-test-publish")));
        Assert.IsNotEmpty(Directory.GetFiles(Path.Join(projectDirectory, "bin", "ankus-test-logs"), "*.log"));
    }

    /// <summary>
    /// Checks Git's actual ignore behavior for regression output and the editor state that <c>dotnet new gitignore</c>
    /// ignores, without hiding authored regression SQL or expected results.
    /// </summary>
    /// <param name="directory">The generated consumer solution.</param>
    /// <param name="name">The generated project name.</param>
    /// <param name="token">Cancels Git and fixture file creation.</param>
    private static async Task AssertTemplateIgnoreAsync(string directory, string name, CancellationToken token)
    {
        (await PackageProcessRunner.RunAsync("git", ["init", "--quiet", "--initial-branch=main"], s_environment, token,
            workingDirectory: directory)).EnsureSuccess("git", ["init"]);
        string root = "src/" + name + "/pg_regress/";
        string[] generated = [root + "results/setup.out", root + "regression.diffs", root + "regression.out",
            ".vs/" + name + "/v18/.suo", ".idea/workspace.xml", "src/" + name + "/" + name + ".csproj.user"];
        foreach (string path in generated)
        {
            string file = Path.Join(directory, path.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, "generated output", token);
        }

        ProcessResult ignored = await PackageProcessRunner.RunAsync("git", ["check-ignore", "--", .. generated],
            s_environment, token, workingDirectory: directory);
        Assert.AreEqual(0, ignored.ExitCode, ignored.StandardError);
        Assert.AreSequenceEqual(generated, ignored.StandardOutput.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries));
        Assert.IsEmpty(ignored.StandardError);
        string[] authored = [root + "sql/setup.sql", root + "expected/setup.out"];
        foreach (string path in authored)
        {
            Assert.IsTrue(File.Exists(Path.Join(directory, path.Replace('/', Path.DirectorySeparatorChar))));
        }

        ProcessResult visible = await PackageProcessRunner.RunAsync("git", ["check-ignore", "--", .. authored],
            s_environment, token, workingDirectory: directory);
        Assert.AreEqual(1, visible.ExitCode);
        Assert.IsEmpty(visible.StandardOutput);
        Assert.IsEmpty(visible.StandardError);

        // MSBuild imports a project's .user file, so none of these may remain for the build.
        foreach (string path in generated)
        {
            File.Delete(Path.Join(directory, path.Replace('/', Path.DirectorySeparatorChar)));
        }
    }

    /// <summary>
    /// Independently created workers have distinct shared-memory identities that fit older PostgreSQL index keys.
    /// </summary>
    [TestMethod]
    public async Task WorkerTemplatesHaveIndependentPortableSharedNames()
    {
        CancellationToken token = context.CancellationToken;
        var prefixes = new HashSet<string>(StringComparer.Ordinal);
        string[] names = ["FirstWorker", "SecondWorker"];
        foreach (string name in names)
        {
            string output = Path.Join(CreateDirectory(), "worker solution");
            await CreateTemplateAsync("ankus-worker", name, output, token);
            string source = await File.ReadAllTextAsync(Path.Join(output, "src", name, "Workers.cs"), token);
            MatchCollection matches = TemplateSharedNames().Matches(source);
            Assert.HasCount(2, matches);
            Assert.AreEqual(matches[0].Groups["prefix"].Value, matches[1].Groups["prefix"].Value);
            Assert.IsTrue(prefixes.Add(matches[0].Groups["prefix"].Value));
            foreach (Match match in matches)
            {
                Assert.IsLessThan(48, match.Groups["name"].Value.Length);
            }
        }
    }

    [GeneratedRegex("""new\("(?<name>(?<prefix>ankus\.[0-9A-Fa-f]{32})\.(pid|value))"\)""", RegexOptions.CultureInvariant)]
    private static partial Regex TemplateSharedNames();

    /// <summary>
    /// Template NuGet metadata identifies a content-only MIT package and includes both discovery configurations and local tool manifests.
    /// </summary>
    [TestMethod]
    public void TemplatePackageHasLicenseAndCompleteDiscoveryContent()
    {
        using ZipArchive package = ZipFile.OpenRead(Path.Join(s_root, "feed", "Ankus.Templates." + s_version + ".nupkg"));
        using Stream metadata = package.GetEntry("Ankus.Templates.nuspec")!.Open();
        XDocument document = XDocument.Load(metadata);
        XNamespace ns = document.Root!.Name.Namespace;
        Assert.AreEqual("MIT", document.Descendants(ns + "license").Single().Value);
        Assert.AreEqual("Brandon Williams", document.Descendants(ns + "authors").Single().Value);
        Assert.AreEqual("Template", document.Descendants(ns + "packageType").Single().Attribute("name")!.Value);
        Assert.AreEqual("https://github.com/ankus-net/ankus", document.Descendants(ns + "repository").Single().Attribute("url")!.Value);
        Assert.IsEmpty(package.Entries.Where(static entry => entry.FullName.StartsWith("lib/", StringComparison.Ordinal)));
        string[] templates = ["ankus", "ankus-worker"];
        foreach (string name in templates)
        {
            Assert.IsNotNull(package.GetEntry("content/" + name + "/.template.config/template.json"));
            Assert.IsNotNull(package.GetEntry("content/" + name + "/.config/dotnet-tools.json"));
            Assert.IsNotNull(package.GetEntry("content/" + name + "/.gitignore"));
        }
    }

    /// <summary>
    /// The extension template accepts an extension name, as <c>ankus new --extension-name</c> does, and turns it into
    /// a lowercase PostgreSQL identifier.
    /// </summary>
    [TestMethod]
    public async Task TemplateExtensionNameOverridesTheProjectName()
    {
        CancellationToken token = context.CancellationToken;
        string output = Path.Join(CreateDirectory(), "Named");
        await CreateTemplateAsync("ankus", "Named", output, token, "--extension-name", "Sales-Reports");
        string source = Path.Join(output, "src", "Named");
        Assert.Contains("<AnkusExtensionName>sales_reports</AnkusExtensionName>",
            await File.ReadAllTextAsync(Path.Join(source, "Named.csproj"), token));
        Assert.Contains("CREATE EXTENSION sales_reports;",
            await File.ReadAllTextAsync(Path.Join(source, "pg_regress", "sql", "setup.sql"), token));
    }

    /// <summary>
    /// The guides' template install command runs as written, with only the version replaced by the test feed's. They
    /// used <c>Ankus.Templates::version</c>, which the .NET 10 SDK still accepts but reports as deprecated in favor of
    /// <c>Ankus.Templates@version</c>.
    /// </summary>
    [TestMethod]
    public async Task DocumentedTemplateInstallRunsWithoutWarnings()
    {
        CancellationToken token = context.CancellationToken;
        string[] guides = ["README.md", Path.Join("docs", "src", "content", "docs", "getting-started", "functions.md"),
            Path.Join("docs", "src", "content", "docs", "reference", "cli.md")];
        string command = Assert.ContainsSingle(guides
            .SelectMany(guide => File.ReadLines(Path.Join(IntegrationEnvironment.RepositoryRoot, guide)))
            .Where(static line => line.StartsWith("dotnet new install ", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal));
        Match documented = DocumentedTemplateInstall().Match(command);
        Assert.IsTrue(documented.Success, command);
        string hive = Path.Join(CreateDirectory(), "isolated template hive");
        ProcessResult installed = await PackageProcessRunner.RunAsync("dotnet",
            ["new", "install", documented.Groups["package"].Value + documented.Groups["separator"].Value + s_version, "--debug:custom-hive", hive],
            s_environment, token, workingDirectory: s_root);
        installed.EnsureSuccess("dotnet", ["new", "install"]);
        Assert.Contains("Ankus PostgreSQL extension", installed.StandardOutput);
        Assert.DoesNotContain("deprecated", installed.StandardOutput + installed.StandardError);
    }

    [GeneratedRegex(@"^dotnet new install (?<package>Ankus\.Templates)(?<separator>::|@)\S+$", RegexOptions.CultureInvariant)]
    private static partial Regex DocumentedTemplateInstall();

    private async Task CreateTemplateAsync(string template, string name, string output, CancellationToken token, params string[] arguments)
    {
        string hive = Path.Join(CreateDirectory(), "isolated template hive");
        string package = Path.Join(s_root, "feed", "Ankus.Templates." + s_version + ".nupkg");
        ProcessResult installed = await PackageProcessRunner.RunAsync("dotnet", ["new", "install", package, "--debug:custom-hive", hive],
            s_environment, token, workingDirectory: s_root);
        installed.EnsureSuccess("dotnet", ["new", "install"]);
        Assert.Contains("ankus-worker", installed.StandardOutput);
        Assert.Contains("Ankus PostgreSQL extension", installed.StandardOutput);
        ProcessResult created = await PackageProcessRunner.RunAsync("dotnet", ["new", template, "--name", name, "--output", output,
            "--debug:custom-hive", hive, .. arguments], s_environment, token, workingDirectory: s_root);
        created.EnsureSuccess("dotnet", ["new", template]);
    }

    private static async Task RestoreConsumerToolAsync(
        string workingDirectory,
        IReadOnlyDictionary<string, string?> environment,
        CancellationToken token)
    {
        await s_consumerToolRestoreLock.WaitAsync(token);
        try
        {
            (await PackageProcessRunner.RunAsync("dotnet", ["tool", "restore"], environment, token, workingDirectory: workingDirectory))
                .EnsureSuccess("dotnet", ["tool", "restore"]);
        }
        finally
        {
            s_consumerToolRestoreLock.Release();
        }
    }
}
