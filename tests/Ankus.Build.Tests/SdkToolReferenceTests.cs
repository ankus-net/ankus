using System.Reflection;
using System.Text.Json;
using System.Xml.Linq;

namespace Ankus.Build.Tests;

/// <summary>
/// Builds the repository SDK's tool references in a solution that also builds the tools directly, as Ankus.slnx does.
/// </summary>
/// <param name="context">The current test's cancellation context.</param>
[TestClass]
public sealed class SdkToolReferenceTests(TestContext context)
{
    /// <summary>
    /// A solution built with PublishAot on the command line builds each tool once. An extension's reference to a tool
    /// that removed the property would build a second configuration beside the solution's own, and both would compile
    /// into the same intermediate directory at once.
    /// </summary>
    [TestMethod]
    public async Task CommandLinePublishAotBuildsEachToolOnce()
    {
        CancellationToken token = context.CancellationToken;
        string directory = Directory.CreateTempSubdirectory("ankus-sdk-tools-").FullName;
        try
        {
            string? version = Assert.ContainsSingle(typeof(SdkToolReferenceTests).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>()
                .Where(static metadata => metadata.Key == "AnkusTestSdkVersion")).Value;
            Assert.IsNotNull(version);
            await File.WriteAllTextAsync(Path.Join(directory, "global.json"),
                JsonSerializer.Serialize(new { sdk = new { version, rollForward = "disable", allowPrerelease = false } }), token);

            // The repository targets reference the tools beside their own directory.
            string source = Path.Join(AppContext.BaseDirectory, "AnkusSdk");
            string sdk = Path.Join(directory, "Ankus.Sdk");
            foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                string target = Path.Join(sdk, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }

            string configurations = Path.Join(directory, "configurations");
            string[] tools = ["Ankus.Runtime", "Ankus.Build", "Ankus.Generators", "Ankus.CodeFixes"];
            foreach (string name in tools)
            {
                string project = Directory.CreateDirectory(Path.Join(directory, name)).FullName;
                new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                    new XElement("PropertyGroup",
                        new XElement("TargetFramework", name is "Ankus.Generators" or "Ankus.CodeFixes" ? "netstandard2.0" : "net10.0")),
                    // Each configuration resolves its references once, and records that in a file of its own because
                    // MSBuild runs the configurations in parallel.
                    new XElement("Target", new XAttribute("Name", "RecordConfiguration"), new XAttribute("BeforeTargets", "ResolveProjectReferences"),
                        new XElement("WriteLinesToFile",
                            new XAttribute("File", Path.Join(configurations, name + ".$([System.Guid]::NewGuid()).log")),
                            new XAttribute("Lines", "$(PublishAot)"))))).Save(Path.Join(project, name + ".csproj"));
            }

            string extension = Directory.CreateDirectory(Path.Join(directory, "Extension")).FullName;
            new XDocument(new XElement("Project", new XAttribute("Sdk", "Microsoft.NET.Sdk"),
                new XElement("PropertyGroup", new XElement("TargetFramework", "net10.0")),
                new XElement("Import", new XAttribute("Project", Path.Join(sdk, "Ankus.Sdk.targets"))))).Save(Path.Join(extension, "Extension.csproj"));
            string solution = Path.Join(directory, "Tools.slnx");
            new XDocument(new XElement("Solution",
                new XElement("Project", new XAttribute("Path", "Extension/Extension.csproj")),
                tools.Select(static name => new XElement("Project", new XAttribute("Path", name + "/" + name + ".csproj"))))).Save(solution);

            _ = await NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                ["msbuild", solution, "-restore", "-nologo", "-nodeReuse:false", "-property:PublishAot=false",
                    "-target:ResolveProjectReferences", "-bl:" + Path.Join(directory, "tools-{}.binlog")], directory, token);
            foreach (string name in tools)
            {
                string[] built = Directory.GetFiles(configurations, name + ".*.log");
                Assert.HasCount(1, built, name + " must build in one configuration.");
                Assert.AreEqual("false", (await File.ReadAllTextAsync(built[0], token)).Trim());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
