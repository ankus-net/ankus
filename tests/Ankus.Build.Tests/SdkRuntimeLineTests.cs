using System.Xml.Linq;

namespace Ankus.Build.Tests;

/// <summary>
/// Checks that each supported target framework selects its own patched runtime and matching compiler.
/// </summary>
/// <param name="context">The current test's cancellation context.</param>
[TestClass]
public sealed class SdkRuntimeLineTests(TestContext context)
{
    /// <summary>
    /// An extension's TargetFramework selects the runtime line it embeds, so net10.0 and net11.0 extensions never share a
    /// payload or compiler.
    /// </summary>
    /// <param name="targetFramework">The extension's target framework.</param>
    /// <param name="runtime">The expected patched runtime package version.</param>
    /// <param name="compiler">The expected upstream compiler version.</param>
    [TestMethod]
    [DataRow("net10.0", "10.0.13-ankus.1", "10.0.13")]
    [DataRow("net11.0", "11.0.0-rc.2.26504.105-ankus.1", "11.0.0-rc.2.26504.105")]
    public async Task TargetFrameworkSelectsItsRuntimeLine(string targetFramework, string runtime, string compiler)
    {
        string output = await EvaluateAsync(
            Path.Join(AppContext.BaseDirectory, "AnkusSdk", "targets", "Ankus.NativeAot.Lines.props"),
            ["-property:TargetFramework=" + targetFramework,
                "-getProperty:_AnkusNativeAotVersion,_AnkusNativeAotCompilerVersion,_AnkusNativeAotCompilerHostVersion"]);
        Assert.Contains($"\"_AnkusNativeAotVersion\": \"{runtime}\"", output);
        Assert.Contains($"\"_AnkusNativeAotCompilerVersion\": \"{compiler}\"", output);
        Assert.Contains($"\"_AnkusNativeAotCompilerHostVersion\": \"{runtime}\"", output);
    }

    /// <summary>
    /// A target framework without an Ankus runtime line is rejected before any compiler is selected.
    /// </summary>
    [TestMethod]
    public async Task UnsupportedTargetFrameworkIsRejected()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-sdk-line-").FullName;
        try
        {
            string project = Path.Join(directory, "Probe.proj");
            new XDocument(new XElement("Project",
                new XElement("PropertyGroup", new XElement("PublishAot", "true")),
                new XElement("Import", new XAttribute("Project",
                    Path.Join(AppContext.BaseDirectory, "AnkusSdk", "targets", "Ankus.NativeAot.targets"))))).Save(project);
            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                    ["msbuild", project, "-nologo", "-nodeReuse:false", "-property:TargetFramework=net9.0", "-target:_ValidateAnkusNativeAotTarget"],
                    directory, context.CancellationToken));
            Assert.Contains("Ankus extensions require TargetFramework=net10.0 or net11.0.", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Extension libraries keep the extension's name on every target framework. .NET 11 would otherwise add a lib prefix
    /// on Unix, which PostgreSQL extension libraries do not use; a project can still choose one.
    /// </summary>
    /// <param name="property">An explicit project setting, or null for the SDK default.</param>
    /// <param name="expected">The evaluated setting.</param>
    [TestMethod]
    [DataRow(null, "false")]
    [DataRow("true", "true")]
    public async Task ExtensionLibrariesHaveNoLibPrefix(string? property, string expected)
    {
        string[] arguments = ["-getProperty:UseNativeLibPrefix"];
        string output = await EvaluateAsync(Path.Join(AppContext.BaseDirectory, "Sdk", "Ankus.props"),
            property is null ? arguments : [.. arguments, "-property:UseNativeLibPrefix=" + property]);
        Assert.AreEqual(expected, output.Trim());
    }

    private async Task<string> EvaluateAsync(string import, string[] arguments)
    {
        string directory = Directory.CreateTempSubdirectory("ankus-sdk-line-").FullName;
        try
        {
            string project = Path.Join(directory, "Probe.proj");
            new XDocument(new XElement("Project", new XElement("Import", new XAttribute("Project", import)))).Save(project);
            return await NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                ["msbuild", project, "-nologo", "-nodeReuse:false", .. arguments], directory, context.CancellationToken);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
