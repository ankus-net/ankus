using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace Ankus.Build.Tests;

/// <summary>
/// Verifies versioned native library names, module paths and the SDK properties that select them.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class VersionedLibraryTests(TestContext context)
{
    /// <summary>
    /// The base name precedes the exact version and platform suffix, including versions with build metadata.
    /// </summary>
    /// <param name="library">The published library filename.</param>
    /// <param name="version">The extension version.</param>
    /// <param name="expected">The library base name.</param>
    [TestMethod]
    [DataRow("versioned_so-0.1.0.so", "0.1.0", "versioned_so")]
    [DataRow("Ankus.Examples.VersionedLibrary-0.1.0.dylib", "0.1.0", "Ankus.Examples.VersionedLibrary")]
    [DataRow("other-name-1.0.0+build.7.dll", "1.0.0+build.7", "other-name")]
    [DataRow("probe-release-release.so", "release", "probe-release")]
    public void BaseNamePrecedesTheVersion(string library, string version, string expected)
    {
        Assert.AreEqual(expected, VersionedLibrary.GetBaseName(library, version));
        Assert.AreEqual(Path.GetFileNameWithoutExtension(library), VersionedLibrary.GetModulePath(expected, version));
    }

    /// <summary>
    /// A filename that does not carry the version, or carries only the version, cannot be a versioned library.
    /// </summary>
    /// <param name="library">The invalid library filename.</param>
    [TestMethod]
    [DataRow("probe.so")]
    [DataRow("probe-0.1.so")]
    [DataRow("-0.1.0.so")]
    [DataRow("probe-0.1.0")]
    public void LibraryWithoutVersionIsRejected(string library)
        => Assert.ThrowsExactly<ArgumentException>(() => VersionedLibrary.GetBaseName(library, "0.1.0"));

    /// <summary>
    /// The SDK argument uses MSBuild's exact Boolean spelling and treats an omitted value as unversioned.
    /// </summary>
    [TestMethod]
    public void ModeUsesExactBooleanText()
    {
        Assert.IsTrue(VersionedLibrary.ParseMode("true"));
        Assert.IsFalse(VersionedLibrary.ParseMode("false"));
        Assert.IsFalse(VersionedLibrary.ParseMode(""));
        Assert.ThrowsExactly<ArgumentException>(() => VersionedLibrary.ParseMode("True"));
        Assert.ThrowsExactly<ArgumentException>(() => VersionedLibrary.ParseMode("1"));
    }

    /// <summary>
    /// Substitution replaces every marker as PostgreSQL does, and a malformed graph is rejected rather than copied.
    /// </summary>
    [TestMethod]
    public void SubstitutionMatchesPostgresAndRejectsMalformedGraphs()
    {
        Assert.AreEqual("AS 'probe-1', 'f' -- probe-1", VersionedLibrary.Substitute("AS 'MODULE_PATHNAME', 'f' -- MODULE_PATHNAME", "probe-1"));
        Assert.AreEqual("module_pathname", VersionedLibrary.Substitute("module_pathname", "probe-1"));
        byte[] truncated = [.. "ANKUSG2\0"u8, 4, 0, 0, 0];
        Assert.ThrowsExactly<FormatException>(() => VersionedLibrary.SubstituteGraph(Convert.ToBase64String(truncated), "probe-1"));
        byte[] trailing = [.. "ANKUSG2\0"u8, 0, 0, 0, 0, 0, 0, 0, 0, 7];
        Assert.ThrowsExactly<FormatException>(() => VersionedLibrary.SubstituteGraph(Convert.ToBase64String(trailing), "probe-1"));
        byte[] header = Encoding.ASCII.GetBytes("ANKUSG9\0\0\0\0\0");
        Assert.ThrowsExactly<FormatException>(() => VersionedLibrary.SubstituteGraph(Convert.ToBase64String(header), "probe-1"));
    }

    /// <summary>
    /// The SDK renames the native binary, including macOS's install name, only for a custom library name or a
    /// versioned library, and keeps the ordinary target-name library otherwise.
    /// </summary>
    /// <param name="versioned">The project's value; MSBuild also treats <c>On</c> as true.</param>
    /// <param name="libraryName">The project's AnkusLibraryName, or empty text.</param>
    /// <param name="operatingSystem">The Native AOT target operating system.</param>
    /// <param name="library">The expected library filename.</param>
    /// <param name="installName">The expected macOS install name, or empty text.</param>
    [TestMethod]
    [DataRow("true", "", "linux", "My.Extension-1.2.3+build.so", "")]
    [DataRow("true", "", "osx", "My.Extension-1.2.3+build.so", "@rpath/My.Extension-1.2.3+build.so")]
    [DataRow("On", "", "linux", "My.Extension-1.2.3+build.so", "")]
    [DataRow("false", "", "linux", "My.Extension.so", "")]
    [DataRow("false", "", "osx", "My.Extension.so", "")]
    [DataRow("false", "other_name", "osx", "other_name.so", "@rpath/other_name.so")]
    [DataRow("true", "other_name", "linux", "other_name-1.2.3+build.so", "")]
    public async Task SdkNamesNativeBinary(string versioned, string libraryName, string operatingSystem, string library, string installName)
    {
        string directory = Directory.CreateTempSubdirectory("ankus versioned library ").FullName;
        try
        {
            string project = WriteProject(directory, versioned, operatingSystem, libraryName);
            string output = await NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                ["msbuild", project, "-nologo", "-nodeReuse:false", "-target:SetupOSSpecificProps",
                    "-getProperty:NativeBinary,_AnkusNativeLibrary,SharedLibraryInstallName"],
                directory, context.CancellationToken);
            using JsonDocument document = JsonDocument.Parse(output);
            JsonElement properties = document.RootElement.GetProperty("Properties");
            Assert.AreEqual(library, properties.GetProperty("_AnkusNativeLibrary").GetString());
            Assert.AreEqual(versioned == "false" && libraryName.Length == 0 ? "native/original.so" : "native/" + library,
                properties.GetProperty("NativeBinary").GetString());
            Assert.AreEqual(installName, properties.GetProperty("SharedLibraryInstallName").GetString());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Without an extension version, the versioned name uses the project's Version, as the extension identity does,
    /// and the native targets do not require the control targets.
    /// </summary>
    [TestMethod]
    public async Task SdkVersionedNameFollowsProjectVersion()
    {
        string directory = Directory.CreateTempSubdirectory("ankus versioned library ").FullName;
        try
        {
            string project = WriteProject(directory, "true", "linux", extensionVersion: "", controlTargets: false);
            string output = await NativeBindingLayoutCommand.RunProcessAsync("dotnet",
                ["msbuild", project, "-nologo", "-nodeReuse:false", "-target:SetupOSSpecificProps", "-property:Version=4.5.6",
                    "-getProperty:_AnkusNativeLibrary"], directory, context.CancellationToken);
            Assert.AreEqual("My.Extension-4.5.6.so", output.Trim());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// A mode other than true or false, or a library name with a path or unsupported character, fails before the build starts.
    /// </summary>
    /// <param name="versioned">The project's versioned-library value.</param>
    /// <param name="libraryName">The project's library name.</param>
    /// <param name="message">The expected error text.</param>
    [TestMethod]
    [DataRow("maybe", "", "AnkusVersionedLibrary must be true or false.")]
    [DataRow("false", "lib/other", "AnkusLibraryName must be a filename")]
    [DataRow("false", ".hidden", "AnkusLibraryName must be a filename")]
    [DataRow("true", "other name", "AnkusLibraryName must be a filename")]
    public async Task SdkRejectsInvalidLibrarySettings(string versioned, string libraryName, string message)
    {
        string directory = Directory.CreateTempSubdirectory("ankus versioned library ").FullName;
        try
        {
            string project = WriteProject(directory, versioned, "linux", libraryName);
            InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingLayoutCommand.RunProcessAsync("dotnet", ["msbuild", project, "-nologo", "-nodeReuse:false", "-target:PrepareForBuild"],
                    directory, context.CancellationToken));
            Assert.Contains(message, error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string WriteProject(string directory, string versioned, string operatingSystem, string libraryName = "",
        string extensionVersion = "1.2.3+build", bool controlTargets = true)
    {
        string sdk = Path.Join(AppContext.BaseDirectory, "AnkusSdk");
        string project = Path.Join(directory, "Versioned.proj");
        new XDocument(new XElement("Project",
            new XElement("PropertyGroup",
                new XElement("TargetName", "My.Extension"),
                new XElement("AnkusExtensionVersion", extensionVersion),
                new XElement("AnkusVersionedLibrary", versioned),
                new XElement("AnkusLibraryName", libraryName),
                new XElement("AnkusPostgresMajor", "18"),
                new XElement("NativeBinaryExt", ".so"),
                new XElement("NativeOutputPath", "native/"),
                new XElement("NativeBinary", "native/original.so"),
                new XElement("_targetOS", operatingSystem)),
            new XElement("Import", new XAttribute("Project", Path.Join(sdk, "Ankus.props"))),
            controlTargets ? new XElement("Import", new XAttribute("Project", Path.Join(sdk, "targets", "Ankus.Control.targets"))) : null,
            new XElement("Import", new XAttribute("Project", Path.Join(sdk, "targets", "Ankus.Native.targets"))),
            // Microsoft.NET.Sdk supplies these targets; this bare project needs only their names.
            new XElement("Target", new XAttribute("Name", "ImportRuntimeIlcPackageTarget")),
            new XElement("Target", new XAttribute("Name", "_ResolveAnkusPostgresSelection")),
            new XElement("Target", new XAttribute("Name", "SetupOSSpecificProps")),
            new XElement("Target", new XAttribute("Name", "PrepareForBuild")))).Save(project);
        return project;
    }
}
