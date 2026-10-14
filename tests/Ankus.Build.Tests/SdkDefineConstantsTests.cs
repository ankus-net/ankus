using System.Text.Json;
using System.Xml.Linq;

namespace Ankus.Build.Tests;

/// <summary>
/// Executes the shared SDK targets to verify selected-header compilation symbols after consumer evaluation.
/// </summary>
/// <param name="context">The current test's cancellation context.</param>
[TestClass]
public sealed class SdkDefineConstantsTests(TestContext context)
{
    /// <summary>
    /// The default and every supported major retain consumer symbols and select exactly one PostgreSQL symbol.
    /// </summary>
    /// <param name="major">The consumer's selected major, or empty for the SDK default.</param>
    /// <param name="symbol">The exact compiler symbol required for that selection.</param>
    [TestMethod]
    [DataRow("", "ANKUS_PG18")]
    [DataRow("13", "ANKUS_PG13")]
    [DataRow("14", "ANKUS_PG14")]
    [DataRow("15", "ANKUS_PG15")]
    [DataRow("16", "ANKUS_PG16")]
    [DataRow("17", "ANKUS_PG17")]
    [DataRow("18", "ANKUS_PG18")]
    [DataRow("19", "ANKUS_PG19")]
    public async Task SelectedMajorRetainsConsumerConstants(string major, string symbol)
    {
        (string[] constants, string selected, bool explicitlySelected) = await EvaluateAsync(
            major, null, "PrepareForBuild;BeforeCompile;CoreCompile");
        Assert.AreSequenceEqual<string>(["CONSUMER", "SECOND", symbol], constants);
        Assert.AreEqual(major.Length == 0 ? "18" : major, selected);
        Assert.AreEqual(major.Length != 0, explicitlySelected);
    }

    /// <summary>
    /// A command-line major wins over the project while direct compile entry points receive the same symbol.
    /// </summary>
    /// <param name="target">The compiler entry point without a preceding build preparation.</param>
    [TestMethod]
    [DataRow("BeforeCompile")]
    [DataRow("CoreCompile")]
    public async Task GlobalMajorAppliesToDirectCompilation(string target)
    {
        (string[] constants, string selected, bool explicitlySelected) = await EvaluateAsync("15", "14", target);
        Assert.AreSequenceEqual<string>(["CONSUMER", "SECOND", "ANKUS_PG14"], constants);
        Assert.AreEqual("14", selected);
        Assert.IsTrue(explicitlySelected);
    }

    /// <summary>
    /// Imports the actual SDK defaults and shared targets, with a later consumer override and real MSBuild scheduling.
    /// The selection defaults follow the shared targets, matching their position after Microsoft.NET.Sdk in the SDK.
    /// </summary>
    private async Task<(string[] Constants, string Major, bool Explicit)> EvaluateAsync(string major, string? globalMajor,
        string targets)
    {
        string directory = Directory.CreateTempSubdirectory("ankus-sdk-symbols-").FullName;
        try
        {
            string sdk = Path.Join(AppContext.BaseDirectory, "Sdk");
            string project = Path.Join(directory, "Symbols.proj");
            // Isolate the SDK default from CI's environment selection; command-line properties still win.
            new XDocument(new XElement("Project",
                new XElement("PropertyGroup",
                    new XElement("AnkusPostgresMajor", string.Empty),
                    new XElement("AnkusPgConfigPath", string.Empty)),
                new XElement("Import", new XAttribute("Project", Path.Join(sdk, "Ankus.props"))),
                new XElement("PropertyGroup",
                    new XElement("DefineConstants", "CONSUMER;SECOND"),
                    new XElement("AnkusPostgresMajor", new XAttribute("Condition", "'" + major + "' != ''"), major)),
                new XElement("Import", new XAttribute("Project", Path.Join(sdk, "Ankus.Bindings.targets"))),
                new XElement("Import", new XAttribute("Project", Path.Join(sdk, "Ankus.PostgresSelection.targets"))),
                new XElement("Target", new XAttribute("Name", "PrepareForBuild")),
                new XElement("Target", new XAttribute("Name", "BeforeCompile")),
                new XElement("Target", new XAttribute("Name", "CoreCompile")))).Save(project);
            List<string> arguments = ["msbuild", project, "-nologo", "-nodeReuse:false", "-target:" + targets,
                "-getProperty:DefineConstants,AnkusPostgresMajor,_AnkusPostgresMajorWasSpecified"];
            if (globalMajor is not null)
            {
                arguments.Add("-property:AnkusPostgresMajor=" + globalMajor);
            }

            string output = await NativeBindingLayoutCommand.RunProcessAsync("dotnet", arguments, directory, context.CancellationToken);
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement properties = document.RootElement.GetProperty("Properties");
            return (properties.GetProperty("DefineConstants").GetString()!.Split(';', StringSplitOptions.RemoveEmptyEntries),
                properties.GetProperty("AnkusPostgresMajor").GetString()!,
                bool.Parse(properties.GetProperty("_AnkusPostgresMajorWasSpecified").GetString()!));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
