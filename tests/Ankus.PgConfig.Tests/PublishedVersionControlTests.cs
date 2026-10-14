using System.Text.Json;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies version-specific control ownership, manifest compatibility and publication recovery.
/// </summary>
[TestClass]
public sealed class PublishedVersionControlTests
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ankus-version-controls-").FullName;

    /// <summary>
    /// Removes this test's publication files.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// Secondary inventories are immutable, ordered and round-trip with or without upgrades.
    /// </summary>
    /// <param name="upgrades">Whether the manifest also owns upgrade SQL.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void VersionControlManifestOwnsOrderedFiles(bool upgrades)
    {
        string[] files = ["probe--stable+one.control", "probe--base.control"];
        string[] scripts = upgrades ? ["probe--base--stable+one.sql"] : [];
        var manifest = new PublishedExtension(18, "linux-x64", "Probe.so", "probe.control", "probe--stable+one.sql", scripts, files);
        files[0] = "changed";
        Assert.AreSequenceEqual<string>(["probe--base.control", "probe--stable+one.control"], manifest.VersionControlFiles);
        IList<string> list = Assert.IsInstanceOfType<IList<string>>(manifest.VersionControlFiles);
        Assert.ThrowsExactly<NotSupportedException>(() => list[0] = "changed");
        manifest.Write(_directory);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Join(_directory, PublishedExtension.FileName)));
        Assert.AreEqual(3, json.RootElement.GetProperty("formatVersion").GetInt32());
        PublishedExtension actual = PublishedExtension.Read(_directory);
        Assert.AreSequenceEqual<string>(["probe--base.control", "probe--stable+one.control"], actual.VersionControlFiles);
        Assert.AreSequenceEqual(scripts, actual.UpgradeScripts);
        Assert.AreEqual("probe--stable+one.sql", actual.Sql);
    }

    /// <summary>
    /// Invalid version names cannot escape the package or masquerade as primary controls or upgrades.
    /// </summary>
    /// <param name="name">The invalid name partition.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("../probe--1.control")]
    [DataRow("other--1.control")]
    [DataRow("probe.control")]
    [DataRow("probe--1.sql")]
    [DataRow("probe--.control")]
    [DataRow("probe---1.control")]
    [DataRow("probe--1-.control")]
    [DataRow("probe--1--2.control")]
    [DataRow("probe--two words.control")]
    public void RejectsInvalidVersionControlNames(string name)
        => Assert.ThrowsExactly<ArgumentException>(() => Create([name]));

    /// <summary>
    /// Null lists, null entries and duplicate destinations fail before publication.
    /// </summary>
    [TestMethod]
    public void RejectsNullAndDuplicateVersionControls()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => Create(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => Create([null!]));
        Assert.ThrowsExactly<ArgumentException>(() => Create(["probe--1.control", "probe--1.control"]));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.ThrowsExactly<ArgumentException>(() => Create(["probe--base.control", "probe--BASE.control"]));
        }
        else
        {
            Assert.HasCount(2, Create(["probe--base.control", "probe--BASE.control"]).VersionControlFiles);
        }
    }

    /// <summary>
    /// Old formats cannot silently lose secondary files; version three requires both complete arrays.
    /// </summary>
    /// <param name="fragment">The invalid format and inventory fields.</param>
    [TestMethod]
    [DataRow("\"formatVersion\":1,\"versionControlFiles\":[]")]
    [DataRow("\"formatVersion\":2,\"upgradeScripts\":[],\"versionControlFiles\":[]")]
    [DataRow("\"formatVersion\":3,\"upgradeScripts\":[]")]
    [DataRow("\"formatVersion\":3,\"versionControlFiles\":[]")]
    [DataRow("\"formatVersion\":3,\"upgradeScripts\":[],\"versionControlFiles\":null")]
    [DataRow("\"formatVersion\":3,\"upgradeScripts\":[],\"versionControlFiles\":[null]")]
    [DataRow("\"formatVersion\":3,\"upgradeScripts\":[],\"versionControlFiles\":[1]")]
    [DataRow("\"formatVersion\":3,\"upgradeScripts\":[],\"versionControlFiles\":[\"other--1.control\"]")]
    [DataRow("\"formatVersion\":3,\"upgradeScripts\":[],\"versionControlFiles\":[],\"versionControlFiles\":[]")]
    public void RejectsMalformedVersionControlManifests(string fragment)
    {
        File.WriteAllText(Path.Join(_directory, PublishedExtension.FileName),
            "{" + fragment + ",\"postgresMajor\":18,\"runtimeIdentifier\":\"linux-x64\",\"library\":\"Probe.so\",\"control\":\"probe.control\",\"sql\":\"probe--2.sql\"}");
        Assert.ThrowsExactly<FormatException>(() => PublishedExtension.Read(_directory));
    }

    /// <summary>
    /// Missing controls retain previous ownership; a repaired publication deletes only obsolete owned files.
    /// </summary>
    [TestMethod]
    public void MissingVersionControlPreservesPreviousFilesUntilRepair()
    {
        PublishedExtension previous = Create(["probe--1.control"]);
        previous.Write(_directory);
        string extension = Path.Join(_directory, "extension");
        Directory.CreateDirectory(extension);
        File.WriteAllText(Path.Join(_directory, previous.Library), "native");
        foreach (string file in new[] { previous.Control, previous.Sql, "probe--1.control", "unlisted.control" })
        {
            File.WriteAllText(Path.Join(extension, file), "retained");
        }

        PublishedExtension next = Create(["probe--2.control"]);
        FileNotFoundException error = Assert.ThrowsExactly<FileNotFoundException>(() => next.CompletePublish(_directory));
        Assert.AreEqual(Path.Join(extension, "probe--2.control"), error.FileName);
        Assert.IsFalse(File.Exists(Path.Join(_directory, PublishedExtension.FileName)));
        Assert.AreEqual("retained", File.ReadAllText(Path.Join(extension, "probe--1.control")));
        File.WriteAllText(Path.Join(extension, "probe--2.control"), "next");
        next.CompletePublish(_directory);
        Assert.AreSequenceEqual<string>(["probe--2.control"], PublishedExtension.Read(_directory).VersionControlFiles);
        Assert.IsFalse(File.Exists(Path.Join(extension, "probe--1.control")));
        Assert.AreEqual("retained", File.ReadAllText(Path.Join(extension, "unlisted.control")));
        Create([]).CompletePublish(_directory);
        Assert.IsEmpty(PublishedExtension.Read(_directory).VersionControlFiles);
        Assert.IsFalse(File.Exists(Path.Join(extension, "probe--2.control")));
        Assert.AreEqual("retained", File.ReadAllText(Path.Join(extension, "unlisted.control")));
    }

    private static PublishedExtension Create(IReadOnlyList<string> controls)
        => new(18, "linux-x64", "Probe.so", "probe.control", "probe--2.sql", [], controls);
}
