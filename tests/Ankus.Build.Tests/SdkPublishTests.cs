using System.Xml.Linq;

namespace Ankus.Build.Tests;

/// <summary>
/// Checks the publish settings the SDK chooses for every extension.
/// </summary>
/// <param name="context">The current test's cancellation context.</param>
[TestClass]
public sealed class SdkPublishTests(TestContext context)
{
    /// <summary>
    /// Extensions publish through the full build. SDK 11's optimized Native AOT publish skips it but still copies the
    /// documentation file from the empty output directory; a project can still choose the optimized path.
    /// </summary>
    /// <param name="property">An explicit project setting, or null for the SDK default.</param>
    /// <param name="expected">The evaluated setting.</param>
    [TestMethod]
    [DataRow(null, "false")]
    [DataRow("true", "true")]
    [DataRow("false", "false")]
    public async Task ExtensionsPublishThroughTheFullBuild(string? property, string expected)
    {
        string directory = Directory.CreateTempSubdirectory("ankus-sdk-publish-").FullName;
        try
        {
            string project = Path.Join(directory, "Probe.proj");
            new XDocument(new XElement("Project",
                new XElement("Import", new XAttribute("Project", Path.Join(AppContext.BaseDirectory, "Sdk", "Ankus.props"))))).Save(project);
            string[] arguments = ["msbuild", project, "-nologo", "-nodeReuse:false", "-getProperty:UseOptimizedPublish"];
            string output = await NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                property is null ? arguments : [.. arguments, "-property:UseOptimizedPublish=" + property], directory, context.CancellationToken);
            Assert.AreEqual(expected, output.Trim());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
