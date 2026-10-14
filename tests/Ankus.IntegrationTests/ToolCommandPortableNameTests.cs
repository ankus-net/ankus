using System.Xml.Linq;
using Ankus.Testing;

namespace Ankus.IntegrationTests;

public sealed partial class ToolCommandTests
{
    /// <summary>
    /// Both installed creation paths preserve portable project names and produce the same valid namespace and SQL identity.
    /// </summary>
    /// <param name="name">The portable project name.</param>
    /// <param name="expectedNamespace">The independently expected C# namespace.</param>
    /// <param name="expectedExtension">The independently expected SQL extension name.</param>
    [TestMethod]
    [DataRow("1Ext", "_1Ext", "_1_ext")]
    [DataRow("Hello-World", "Hello_World", "hello_world")]
    [DataRow("Hello World", "Hello_World", "hello_world")]
    [DataRow("Café.Δelta", "Café.Δelta", "caf___elta")]
    [DataRow("class.event", "@class.@event", "class_event")]
    [DataRow("A1B2", "A1B2", "a1_b2")]
    [DataRow("A\u0301BC", "A\u0301BC", "a_bc")]
    [DataRow("ⅫValue", "ⅫValue", "_value")]
    [DataRow("١Ext", "_١Ext", "_ext")]
    public async Task CreationPathsPreservePortableNames(string name, string expectedNamespace, string expectedExtension)
    {
        CancellationToken token = context.CancellationToken;
        string directory = CreateDirectory();
        string cli = Path.Join(directory, "cli");
        string template = Path.Join(directory, "template");
        ProcessResult created = await InvokeAsync(["new", name, "--output", cli], token);
        Assert.AreEqual(0, created.ExitCode, created.StandardError);

        string hive = Path.Join(directory, "template hive");
        string package = Path.Join(s_root, "feed", "Ankus.Templates." + s_version + ".nupkg");
        ProcessResult installed = await PackageProcessRunner.RunAsync("dotnet", ["new", "install", package, "--debug:custom-hive", hive],
            s_environment, token, workingDirectory: s_root);
        Assert.AreEqual(0, installed.ExitCode, installed.StandardError);
        ProcessResult templated = await PackageProcessRunner.RunAsync("dotnet",
            ["new", "ankus", "--name", name, "--output", template, "--debug:custom-hive", hive],
            s_environment, token, workingDirectory: s_root);
        Assert.AreEqual(0, templated.ExitCode, templated.StandardError);

        foreach (string output in new[] { cli, template })
        {
            string extension = Path.Join(output, "src", name);
            string source = await File.ReadAllTextAsync(Path.Join(extension, "Functions.cs"), token);
            Assert.Contains("namespace " + expectedNamespace + ";", source);
            string checks = await File.ReadAllTextAsync(Path.Join(extension, "BackendChecks.cs"), token);
            Assert.Contains("namespace " + expectedNamespace + ";", checks);
            XDocument project = XDocument.Load(Path.Join(extension, name + ".csproj"));
            Assert.AreEqual(expectedExtension, project.Descendants("AnkusExtensionName").Single().Value);
            Assert.AreEqual(expectedExtension, project.Descendants("AssemblyName").Single().Value);
            XDocument solution = XDocument.Load(Path.Join(output, name + ".slnx"));
            Assert.AreSequenceEqual<string>(["src/" + name + "/" + name + ".csproj", "tests/" + name + ".Tests/" + name + ".Tests.csproj"],
                solution.Descendants("Project").Select(static element => element.Attribute("Path")!.Value.Replace('\\', '/')));
            string testDirectory = Path.Join(output, "tests", name + ".Tests");
            string managed = await File.ReadAllTextAsync(Path.Join(testDirectory, "ManagedTests.cs"), token);
            Assert.Contains("namespace " + expectedNamespace + ".Tests;", managed);
            XDocument host = XDocument.Load(Path.Join(testDirectory, name + ".Tests.csproj"));
            Assert.AreEqual("../../src/" + name + "/" + name + ".csproj",
                host.Descendants("ProjectReference").Single().Attribute("Include")!.Value.Replace('\\', '/'));
            string regression = Path.Join(extension, "pg_regress");
            string setup = await File.ReadAllTextAsync(Path.Join(regression, "sql", "setup.sql"), token);
            Assert.Contains("CREATE EXTENSION " + expectedExtension + ";", setup);
            Assert.AreEqual(setup, await File.ReadAllTextAsync(Path.Join(regression, "expected", "setup.out"), token));
        }

        Assert.IsEmpty(Directory.GetDirectories(directory, ".ankus-new-*"));
    }
}
