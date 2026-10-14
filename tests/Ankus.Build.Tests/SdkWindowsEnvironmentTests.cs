using System.Text.Json;
using System.Xml.Linq;

namespace Ankus.Build.Tests;

/// <summary>
/// Checks that environment-selected native tools and Ankus consume the same library roots.
/// </summary>
/// <param name="context">The current test's cancellation context.</param>
[TestClass]
public sealed class SdkWindowsEnvironmentTests(TestContext context)
{
    /// <summary>
    /// Returns the configured Windows libraries to binding discovery without changing other toolchain modes.
    /// </summary>
    /// <param name="targetOs">The native target operating system.</param>
    /// <param name="environmentalTools">Whether Native AOT uses the developer environment.</param>
    /// <param name="expected">The library roots that binding verification must receive.</param>
    [TestMethod]
    [DataRow("win", "true", "consumer;compiler;sdk")]
    [DataRow("win", "false", "consumer")]
    [DataRow("win", "", "consumer")]
    [DataRow("linux", "true", "consumer")]
    public async Task BindingDiscoveryPreservesSelectedLibraryRoots(string targetOs, string environmentalTools, string expected)
    {
        string directory = Directory.CreateTempSubdirectory("ankus-sdk-environment-").FullName;
        try
        {
            string sdk = Path.Join(AppContext.BaseDirectory, "Sdk");
            string project = Path.Join(directory, "Toolchain.proj");
            new XDocument(new XElement("Project",
                new XElement("PropertyGroup",
                    new XElement("_targetOS", targetOs),
                    new XElement("IlcUseEnvironmentalTools", environmentalTools),
                    new XElement("LIB", "compiler;sdk"),
                    new XElement("CppLinker", "link.exe")),
                new XElement("ItemGroup", new XElement("AdditionalNativeLibraryDirectories", new XAttribute("Include", "consumer"))),
                new XElement("Import", new XAttribute("Project", Path.Join(sdk, "Ankus.Bindings.targets"))),
                new XElement("Import", new XAttribute("Project", Path.Join(sdk, "Ankus.Native.targets"))),
                new XElement("Target", new XAttribute("Name", "ResolveFrameworkReferences")),
                new XElement("Target", new XAttribute("Name", "SetupOSSpecificProps")))).Save(project);
            string output = await NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                ["msbuild", project, "-nologo", "-nodeReuse:false", "-target:_GetAnkusBindingToolchain;SetupOSSpecificProps",
                    "-getItem:_AnkusDiscoveredBindingToolchain,AdditionalNativeLibraryDirectories"], directory, context.CancellationToken);
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement items = document.RootElement.GetProperty("Items");
            JsonElement toolchain = items.GetProperty("_AnkusDiscoveredBindingToolchain")[0];
            Assert.AreEqual(expected, toolchain.GetProperty("LibraryDirectories").GetString());
            string[] libraries = [.. items.GetProperty("AdditionalNativeLibraryDirectories").EnumerateArray()
                .Select(static item => item.GetProperty("Identity").GetString()!)];
            Assert.AreSequenceEqual(expected.Split(';'), libraries);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
