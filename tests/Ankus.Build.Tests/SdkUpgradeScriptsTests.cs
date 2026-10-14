using System.Text.Json;
using System.Xml.Linq;

namespace Ankus.Build.Tests;

/// <summary>
/// Evaluates actual SDK defaults with normal MSBuild consumer item customization.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class SdkUpgradeScriptsTests(TestContext context)
{
    /// <summary>
    /// Default discovery is nonrecursive and extension-specific; consumer removal, updates and explicit additions survive evaluation.
    /// </summary>
    /// <param name="explicitName">Whether to use the extension name instead of the normalized assembly name.</param>
    /// <param name="defaults">Whether default discovery is enabled.</param>
    /// <param name="controls">Whether to discover version control files instead of SQL upgrades.</param>
    [TestMethod]
    [DataRow(false, true, false)]
    [DataRow(true, true, false)]
    [DataRow(false, false, false)]
    [DataRow(true, false, false)]
    [DataRow(false, true, true)]
    [DataRow(true, true, true)]
    [DataRow(false, false, true)]
    [DataRow(true, false, true)]
    public async Task ArtifactItemsHonorConsumerEvaluation(bool explicitName, bool defaults, bool controls)
    {
        string directory = Directory.CreateTempSubdirectory("ankus SDK items ").FullName;
        try
        {
            string itemName = controls ? "AnkusVersionControlFile" : "AnkusUpgradeScript";
            string defaultProperty = controls ? "EnableDefaultAnkusVersionControlFiles" : "EnableDefaultAnkusUpgradeScripts";
            string name = explicitName ? "custom" : "my_extension";
            Directory.CreateDirectory(Path.Join(directory, "sql", "nested"));
            string[] files = controls
                ? ["sql/" + name + "--1.control", "sql/" + name + "--2.control", "sql/" + name + ".control",
                    "sql/other--1.control", "sql/nested/" + name + "--3.control", name + "--4.control"]
                : ["sql/" + name + "--1--2.sql", "sql/" + name + "--2--3.sql",
                "sql/" + name + "--3.sql", "sql/other--1--2.sql", "sql/nested/" + name + "--3--4.sql",
                name + "--4--5.sql"];
            foreach (string file in files)
            {
                File.WriteAllText(Path.Join(directory, file), "SELECT 1;");
            }

            string project = Path.Join(directory, "Items.proj");
            new XDocument(new XElement("Project",
                new XElement("Import", new XAttribute("Project", Path.Join(AppContext.BaseDirectory, "Sdk", "Ankus.props"))),
                new XElement("PropertyGroup", new XElement("TargetName", "My.Extension"),
                    new XElement("AnkusExtensionName", explicitName ? name : ""),
                    new XElement(defaultProperty, defaults ? "true" : "false")),
                new XElement("ItemGroup",
                    new XElement(itemName, new XAttribute("Remove", files[1])),
                    new XElement(itemName, new XAttribute("Update", files[0]), new XElement("AuthorNote", "retained")),
                    new XElement(itemName, new XAttribute("Include", files[^1]))))).Save(project);
            string output = await NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                ["msbuild", project, "-nologo", "-getItem:" + itemName], directory, context.CancellationToken);
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement items = document.RootElement.GetProperty("Items").GetProperty(itemName);
            Assert.AreSequenceEqual<string>(defaults ? [files[0], files[^1]] : [files[^1]],
                items.EnumerateArray().Select(static item => item.GetProperty("Identity").GetString()!.Replace('\\', '/')));
            if (defaults)
            {
                Assert.AreEqual("retained", items[0].GetProperty("AuthorNote").GetString());
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
