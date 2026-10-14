using System.Xml.Linq;

namespace Ankus.Build.Tests;

/// <summary>
/// Verifies publication invalidation runs before compilation with either output-directory spelling.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class SdkPublishInvalidationTests(TestContext context)
{
    /// <summary>
    /// Publishing retains the old inventory and removes its active manifest, while an ordinary build preserves it.
    /// </summary>
    /// <param name="publishing">Whether the build belongs to dotnet publish.</param>
    /// <param name="trailingSeparator">Whether PublishDir already has a trailing separator.</param>
    [TestMethod]
    [DataRow(true, true)]
    [DataRow(true, false)]
    [DataRow(false, true)]
    [DataRow(false, false)]
    public async Task PrepareForBuildInvalidatesOnlyPublication(bool publishing, bool trailingSeparator)
    {
        string directory = Directory.CreateTempSubdirectory("ankus publish invalidation ").FullName;
        try
        {
            string output = Path.Join(directory, "publication");
            Directory.CreateDirectory(output);
            string manifest = Path.Join(output, "ankus.extension.json");
            string previous = Path.Join(output, "ankus.extension.previous.json");
            File.WriteAllText(manifest, "current inventory");
            File.WriteAllText(previous, "older inventory");
            string project = Path.Join(directory, "Publish.proj");
            new XDocument(new XElement("Project",
                new XElement("PropertyGroup",
                    new XElement("AnkusReuseSchema", "false"),
                    new XElement("_IsPublishing", publishing ? "true" : "false"),
                    new XElement("PublishDir", output + (trailingSeparator ? Path.DirectorySeparatorChar.ToString() : ""))),
                new XElement("Import", new XAttribute("Project", Path.Join(AppContext.BaseDirectory, "Sdk", "Ankus.Native.targets"))),
                new XElement("Target", new XAttribute("Name", "PrepareForBuild")))).Save(project);
            await NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                ["msbuild", project, "-nologo", "-nodeReuse:false", "-target:PrepareForBuild"], directory, context.CancellationToken);
            Assert.AreEqual(!publishing, File.Exists(manifest));
            Assert.AreEqual(publishing ? "current inventory" : "older inventory", File.ReadAllText(previous));
            if (!publishing)
            {
                Assert.AreEqual("current inventory", File.ReadAllText(manifest));
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
