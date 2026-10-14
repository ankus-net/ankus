using System.Text.Json;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies publication identity, upgrade ownership and failed-publication recovery.
/// </summary>
[TestClass]
public sealed class PublishedExtensionTests
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ankus-manifest-").FullName;

    /// <summary>
    /// Removes this test's artifact directory.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// Existing publications retain their original readable version-one format.
    /// </summary>
    [TestMethod]
    public void LegacyManifestRoundTripsWithoutUpgradeFields()
    {
        var manifest = new PublishedExtension(13, "linux-x64", "Probe.so", "probe.control", "probe--1.sql");
        manifest.Write(_directory);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Join(_directory, PublishedExtension.FileName)));
        Assert.AreEqual(1, json.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.IsFalse(json.RootElement.TryGetProperty("upgradeScripts", out _));
        PublishedExtension actual = PublishedExtension.Read(_directory);
        Assert.AreEqual(13, actual.PostgresMajor);
        Assert.AreEqual("linux-x64", actual.RuntimeIdentifier);
        Assert.AreEqual("Probe.so", actual.Library);
        Assert.AreEqual("probe.control", actual.Control);
        Assert.AreEqual("probe--1.sql", actual.Sql);
        Assert.IsEmpty(actual.UpgradeScripts);
        Assert.IsEmpty(actual.VersionControlFiles);
    }

    /// <summary>
    /// Upgrade lists own their input, sort deterministically, and retain literal PostgreSQL versions.
    /// </summary>
    [TestMethod]
    public void UpgradeManifestOwnsOrderedLiteralVersionNames()
    {
        string[] scripts = ["probe--preview--stable+one.sql", "probe--base--preview.sql"];
        var manifest = new PublishedExtension(19, "osx-arm64", "Probe.dylib", "probe.control", "probe--stable+one.sql", scripts);
        scripts[0] = "corrupted";
        Assert.AreSequenceEqual<string>(["probe--base--preview.sql", "probe--preview--stable+one.sql"], manifest.UpgradeScripts);
        IList<string> list = Assert.IsInstanceOfType<IList<string>>(manifest.UpgradeScripts);
        Assert.ThrowsExactly<NotSupportedException>(() => list[0] = "changed");
        manifest.Write(_directory);
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Join(_directory, PublishedExtension.FileName)));
        Assert.AreEqual(2, json.RootElement.GetProperty("formatVersion").GetInt32());
        Assert.AreSequenceEqual<string>(["probe--base--preview.sql", "probe--preview--stable+one.sql"],
            PublishedExtension.Read(_directory).UpgradeScripts);
    }

    /// <summary>
    /// Invalid upgrade names cannot escape the publication or impersonate installation SQL.
    /// </summary>
    /// <param name="script">The invalid file partition.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("../probe--1--2.sql")]
    [DataRow("other--1--2.sql")]
    [DataRow("probe--1.sql")]
    [DataRow("probe--1--2.txt")]
    [DataRow("probe----2.sql")]
    [DataRow("probe--1--.sql")]
    [DataRow("probe---1--2.sql")]
    [DataRow("probe--1---2.sql")]
    [DataRow("probe--1--2-.sql")]
    [DataRow("probe--1--2--3.sql")]
    [DataRow("probe--1--two words.sql")]
    public void RejectsInvalidUpgradeNames(string script)
        => Assert.ThrowsExactly<ArgumentException>(() => new PublishedExtension(18, "linux-x64", "Probe.so",
            "probe.control", "probe--2.sql", [script]));

    /// <summary>
    /// Null collections, null entries, duplicate outputs and installation SQL collisions fail explicitly.
    /// </summary>
    [TestMethod]
    public void RejectsNullAndConflictingUpgradeInputs()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => new PublishedExtension(18, "linux-x64", "Probe.so",
            "probe.control", "probe--2.sql", null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => new PublishedExtension(18, "linux-x64", "Probe.so",
            "probe.control", "probe--2.sql", [null!]));
        Assert.ThrowsExactly<ArgumentException>(() => new PublishedExtension(18, "linux-x64", "Probe.so",
            "probe.control", "probe--2.sql", ["probe--1--2.sql", "probe--1--2.sql"]));
        Assert.ThrowsExactly<ArgumentException>(() => new PublishedExtension(18, "linux-x64", "Probe.so",
            "probe.control", "probe--1--2.sql", ["probe--1--2.sql"]));
    }

    /// <summary>
    /// Invalid manifests fail before consumers receive a partial artifact contract.
    /// </summary>
    /// <param name="fragment">The invalid format and upgrade fields.</param>
    [TestMethod]
    [DataRow("\"formatVersion\":5")]
    [DataRow("\"formatVersion\":1,\"upgradeScripts\":[]")]
    [DataRow("\"formatVersion\":2")]
    [DataRow("\"formatVersion\":2,\"upgradeScripts\":null")]
    [DataRow("\"formatVersion\":2,\"upgradeScripts\":[null]")]
    [DataRow("\"formatVersion\":2,\"upgradeScripts\":[1]")]
    [DataRow("\"formatVersion\":2,\"upgradeScripts\":[\"other--1--2.sql\"]")]
    [DataRow("\"formatVersion\":1,\"formatVersion\":1")]
    public void RejectsMalformedManifestContracts(string fragment)
    {
        File.WriteAllText(Path.Join(_directory, PublishedExtension.FileName),
            "{" + fragment + ",\"postgresMajor\":18,\"runtimeIdentifier\":\"linux-x64\",\"library\":\"Probe.so\",\"control\":\"probe.control\",\"sql\":\"probe--2.sql\"}");
        Assert.ThrowsExactly<FormatException>(() => PublishedExtension.Read(_directory));
    }

    /// <summary>
    /// Failed rebuilds retain ownership for recovery; only removed owned SQL is deleted after all payloads exist.
    /// </summary>
    [TestMethod]
    public void FailedPublishPreservesInventoryUntilCompleteReplacement()
    {
        var previous = new PublishedExtension(18, "linux-x64", "Old.so", "probe.control", "probe--2.sql", ["probe--1--2.sql"]);
        previous.Write(_directory);
        string extension = Path.Join(_directory, "extension");
        Directory.CreateDirectory(extension);
        foreach (string file in new[] { previous.Control, previous.Sql, "probe--1--2.sql", "unrelated.sql" })
        {
            File.WriteAllText(Path.Join(extension, file), "keep until replaced");
        }

        File.WriteAllText(Path.Join(_directory, "Old.so"), "loaded old library");
        PublishedExtension.Invalidate(_directory);
        PublishedExtension.Invalidate(_directory);
        Assert.IsFalse(File.Exists(Path.Join(_directory, PublishedExtension.FileName)));
        var replacement = new PublishedExtension(18, "linux-x64", "New.so", "probe.control", "probe--3.sql", ["probe--2--3.sql"]);
        File.WriteAllText(Path.Join(_directory, "New.so"), "new library");
        File.WriteAllText(Path.Join(extension, "probe--3.sql"), "new installation");
        FileNotFoundException failure = Assert.ThrowsExactly<FileNotFoundException>(() => replacement.CompletePublish(_directory));
        Assert.AreEqual(Path.Join(extension, "probe--2--3.sql"), failure.FileName);
        Assert.AreEqual("keep until replaced", File.ReadAllText(Path.Join(extension, "probe--1--2.sql")));
        Assert.IsFalse(File.Exists(Path.Join(_directory, PublishedExtension.FileName)));
        File.WriteAllText(Path.Join(extension, "probe--2--3.sql"), "new upgrade");
        replacement.CompletePublish(_directory);
        Assert.AreSequenceEqual<string>(["probe--2--3.sql", "probe--3.sql", "probe.control", "unrelated.sql"],
            Directory.GetFiles(extension).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
        Assert.AreEqual("keep until replaced", File.ReadAllText(Path.Join(extension, "unrelated.sql")));
        Assert.AreEqual("loaded old library", File.ReadAllText(Path.Join(_directory, "Old.so")));
        Assert.AreEqual("New.so", PublishedExtension.Read(_directory).Library);
        Assert.IsFalse(File.Exists(Path.Join(_directory, "ankus.extension.previous.json")));
    }
}
