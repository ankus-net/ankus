using System.Text.Json;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies SQL directory identity, path resolution and publication compatibility.
/// </summary>
[TestClass]
public sealed class PublishedScriptDirectoryTests
{
    private readonly string _root = Directory.CreateTempSubdirectory("ankus-script-directory-").FullName;

    /// <summary>
    /// Removes this test's publication and owned target paths.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// Missing, empty, nested, parent-relative and absolute settings retain distinct PostgreSQL destinations.
    /// </summary>
    [TestMethod]
    public void ResolvesLiteralScriptDirectoriesWithoutCreatingTargets()
    {
        string shared = Path.Join(_root, "share");
        string absolute = Path.Join(_root, "absolute scripts");
        (string? Directory, string Expected)[] cases =
        [
            (null, Path.Join(shared, "extension")),
            ("", shared),
            (".", Path.Join(shared, ".")),
            ("nested/SQL files", Path.Join(shared, "nested", "SQL files")),
            ("../sibling", Path.Join(shared, "../sibling")),
            ("missing/../scripts", Path.Join(shared, "missing/../scripts")),
            (absolute, absolute),
        ];
        foreach ((string? directory, string expected) in cases)
        {
            PublishedExtension manifest = Prepare(directory);
            string expectedPath = OperatingSystem.IsWindows() ? Path.GetFullPath(expected) : expected;
            Assert.AreEqual(expectedPath, manifest.GetScriptDirectory(_root, shared));
            Assert.IsFalse(Directory.Exists(expectedPath));
            manifest.Write(_root);
            PublishedExtension actual = PublishedExtension.Read(_root);
            Assert.AreEqual(directory, actual.ScriptDirectory);
            Assert.AreEqual(expectedPath, actual.GetScriptDirectory(_root, shared));
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Join(_root, PublishedExtension.FileName)));
            Assert.AreEqual(directory is null ? 1 : 4, json.RootElement.GetProperty("formatVersion").GetInt32());
            if (directory is not null)
            {
                Assert.AreEqual(directory, json.RootElement.GetProperty("scriptDirectory").GetString());
                Assert.AreEqual(0, json.RootElement.GetProperty("upgradeScripts").GetArrayLength());
                Assert.AreEqual(0, json.RootElement.GetProperty("versionControlFiles").GetArrayLength());
            }
        }
    }

    /// <summary>
    /// Version four retains upgrade and secondary inventories while requiring its own directory field.
    /// </summary>
    [TestMethod]
    public void ScriptDirectoryManifestRetainsEveryInventory()
    {
        var manifest = new PublishedExtension(18, "linux-x64", "Probe.so", "probe.control", "probe--2.sql",
            ["probe--1--2.sql"], ["probe--1.control", "probe--2.control"], "scripts");
        manifest.Write(_root);
        PublishedExtension actual = PublishedExtension.Read(_root);
        Assert.AreEqual("scripts", actual.ScriptDirectory);
        Assert.AreSequenceEqual<string>(["probe--1--2.sql"], actual.UpgradeScripts);
        Assert.AreSequenceEqual<string>(["probe--1.control", "probe--2.control"], actual.VersionControlFiles);
    }

    /// <summary>
    /// A control edit cannot silently redirect installation after its publication was recorded.
    /// </summary>
    /// <param name="declared">The manifest setting.</param>
    /// <param name="actual">The changed primary setting.</param>
    [TestMethod]
    [DataRow(null, "scripts")]
    [DataRow("scripts", null)]
    [DataRow("scripts", "different")]
    [DataRow("", null)]
    public void RejectsControlAndManifestDirectoryDisagreement(string? declared, string? actual)
    {
        PublishedExtension manifest = Prepare(declared);
        WriteControl(actual);
        FormatException error = Assert.ThrowsExactly<FormatException>(() => manifest.GetScriptDirectory(_root, _root));
        Assert.Contains("does not match", error.Message);
    }

    /// <summary>
    /// Unrepresentable PostgreSQL control values fail at the manifest boundary.
    /// </summary>
    /// <param name="directory">The invalid setting.</param>
    [TestMethod]
    [DataRow("café")]
    [DataRow("bad\0path")]
    public void RejectsInvalidScriptDirectoryValues(string directory)
        => Assert.ThrowsExactly<ArgumentException>(() => Create(directory));

    /// <summary>
    /// Missing, null, wrongly typed and old-format directory fields fail before a layout is returned.
    /// </summary>
    /// <param name="fields">The invalid format-specific fields.</param>
    [TestMethod]
    [DataRow("\"formatVersion\":1,\"scriptDirectory\":\"scripts\"")]
    [DataRow("\"formatVersion\":2,\"scriptDirectory\":\"scripts\",\"upgradeScripts\":[]")]
    [DataRow("\"formatVersion\":3,\"scriptDirectory\":\"scripts\",\"upgradeScripts\":[],\"versionControlFiles\":[]")]
    [DataRow("\"formatVersion\":4,\"upgradeScripts\":[],\"versionControlFiles\":[]")]
    [DataRow("\"formatVersion\":4,\"upgradeScripts\":[],\"versionControlFiles\":[],\"scriptDirectory\":null")]
    [DataRow("\"formatVersion\":4,\"upgradeScripts\":[],\"versionControlFiles\":[],\"scriptDirectory\":false")]
    [DataRow("\"formatVersion\":4,\"upgradeScripts\":[],\"versionControlFiles\":[],\"scriptDirectory\":[]")]
    [DataRow("\"formatVersion\":4,\"upgradeScripts\":[],\"versionControlFiles\":[],\"scriptDirectory\":1")]
    [DataRow("\"formatVersion\":4,\"upgradeScripts\":[],\"versionControlFiles\":[],\"scriptDirectory\":\"one\",\"scriptDirectory\":\"two\"")]
    [DataRow("\"formatVersion\":4,\"scriptDirectory\":\"scripts\",\"upgradeScripts\":[]")]
    [DataRow("\"formatVersion\":4,\"scriptDirectory\":\"scripts\",\"versionControlFiles\":[]")]
    public void RejectsMalformedScriptDirectoryManifests(string fields)
    {
        File.WriteAllText(Path.Join(_root, PublishedExtension.FileName),
            "{" + fields + ",\"postgresMajor\":18,\"runtimeIdentifier\":\"linux-x64\",\"library\":\"Probe.so\",\"control\":\"probe.control\",\"sql\":\"probe--2.sql\"}");
        Assert.ThrowsExactly<FormatException>(() => PublishedExtension.Read(_root));
    }

    /// <summary>
    /// Custom-directory completion verifies control identity while keeping every payload in the publication.
    /// </summary>
    [TestMethod]
    public void CompletePublishDoesNotWriteTheAuthoredDirectory()
    {
        string target = Path.Join(_root, "outside publication");
        PublishedExtension manifest = Prepare(target);
        File.WriteAllText(Path.Join(_root, manifest.Library), "native payload");
        File.WriteAllText(Path.Join(_root, "extension", manifest.Sql), "SELECT 42;");
        manifest.Write(_root);
        WriteControl("wrong");
        Assert.ThrowsExactly<FormatException>(() => manifest.CompletePublish(_root));
        Assert.IsFalse(File.Exists(Path.Join(_root, PublishedExtension.FileName)));
        Assert.IsTrue(File.Exists(Path.Join(_root, "ankus.extension.previous.json")));
        WriteControl(target);
        manifest.CompletePublish(_root);
        Assert.AreEqual(target, PublishedExtension.Read(_root).ScriptDirectory);
        Assert.IsFalse(Directory.Exists(target));
        Assert.AreEqual("SELECT 42;", File.ReadAllText(Path.Join(_root, "extension", manifest.Sql)));
    }

    /// <summary>
    /// Missing arguments and drive-relative paths cannot depend on a caller's working drive.
    /// </summary>
    [TestMethod]
    public void ScriptDirectoryResolutionRejectsMissingOrAmbiguousInputs()
    {
        PublishedExtension manifest = Prepare(null);
        Assert.ThrowsExactly<ArgumentNullException>(() => manifest.GetScriptDirectory(null!, _root));
        Assert.ThrowsExactly<ArgumentException>(() => manifest.GetScriptDirectory("", _root));
        Assert.ThrowsExactly<ArgumentNullException>(() => manifest.GetScriptDirectory(_root, null!));
        Assert.ThrowsExactly<ArgumentException>(() => manifest.GetScriptDirectory(_root, ""));
        if (OperatingSystem.IsWindows())
        {
            string[] ambiguousDirectories = ["C:relative", "\\relative", "/relative"];
            foreach (PublishedExtension ambiguous in ambiguousDirectories.Select(Prepare))
            {
                Assert.ThrowsExactly<FormatException>(() => ambiguous.GetScriptDirectory(_root, _root));
            }
        }
    }

    private PublishedExtension Prepare(string? directory)
    {
        WriteControl(directory);
        return Create(directory);
    }

    private void WriteControl(string? directory)
    {
        Directory.CreateDirectory(Path.Join(_root, "extension"));
        var settings = new Dictionary<string, string> { ["default_version"] = "2" };
        if (directory is not null)
        {
            settings.Add("directory", directory);
        }

        File.WriteAllText(Path.Join(_root, "extension", "probe.control"), ExtensionControlFile.Format(settings));
    }

    private static PublishedExtension Create(string? directory)
        => new(18, "linux-x64", "Probe.so", "probe.control", "probe--2.sql", [], [], directory);
}
