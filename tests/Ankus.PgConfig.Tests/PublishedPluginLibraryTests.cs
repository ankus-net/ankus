using System.Text.Json;

namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies the output plugin modules a publication owns beside its library.
/// </summary>
[TestClass]
public sealed class PublishedPluginLibraryTests
{
    private readonly string _directory = Directory.CreateTempSubdirectory("ankus-plugin-libraries-").FullName;

    /// <summary>
    /// Removes this test's publication files.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_directory, recursive: true);

    /// <summary>
    /// Modules are ordered, immutable and round-trip through version five with or without a SQL directory.
    /// </summary>
    /// <param name="customDirectory">Whether the control names its own SQL directory.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PluginManifestOwnsOrderedModules(bool customDirectory)
    {
        string? scriptDirectory = customDirectory ? "scripts" : null;
        string[] modules = ["probe_output.so", "probe_decoder.so"];
        PublishedExtension manifest = Create(modules, scriptDirectory);
        modules[0] = "changed";
        Assert.AreSequenceEqual<string>(["probe_decoder.so", "probe_output.so"], manifest.PluginLibraries);
        IList<string> list = Assert.IsInstanceOfType<IList<string>>(manifest.PluginLibraries);
        Assert.ThrowsExactly<NotSupportedException>(() => list[0] = "changed");
        manifest.Write(_directory);
        using (JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Join(_directory, PublishedExtension.FileName))))
        {
            Assert.AreEqual(5, json.RootElement.GetProperty("formatVersion").GetInt32());
            Assert.AreEqual(0, json.RootElement.GetProperty("upgradeScripts").GetArrayLength());
            Assert.AreEqual(0, json.RootElement.GetProperty("versionControlFiles").GetArrayLength());
            Assert.AreEqual(scriptDirectory is not null, json.RootElement.TryGetProperty("scriptDirectory", out _));
        }

        PublishedExtension actual = PublishedExtension.Read(_directory);
        Assert.AreSequenceEqual<string>(["probe_decoder.so", "probe_output.so"], actual.PluginLibraries);
        Assert.AreEqual(scriptDirectory, actual.ScriptDirectory);
        Assert.AreEqual("Probe.so", actual.Library);
        Assert.IsEmpty(new PublishedExtension(18, "linux-x64", "Probe.so", "probe.control", "probe--2.sql", [], [], null).PluginLibraries);
    }

    /// <summary>
    /// A module must be a plain filename with the library's suffix that names neither the library nor another module.
    /// </summary>
    /// <param name="name">The invalid module name partition.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("probe_output")]
    [DataRow("probe_output.dll")]
    [DataRow("../probe_output.so")]
    [DataRow("lib/probe_output.so")]
    [DataRow("probe output.so")]
    [DataRow("Probe.so")]
    public void RejectsInvalidModuleNames(string name)
        => Assert.ThrowsExactly<ArgumentException>(() => Create([name]));

    /// <summary>
    /// Null lists, null entries and duplicate modules fail before publication. Case-insensitive file systems also
    /// reject names that differ only in case.
    /// </summary>
    [TestMethod]
    public void RejectsNullAndDuplicateModules()
    {
        Assert.ThrowsExactly<ArgumentNullException>(() => Create(null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => Create([null!]));
        Assert.ThrowsExactly<ArgumentException>(() => Create(["probe_output.so", "probe_output.so"]));
        if (OperatingSystem.IsWindows() || OperatingSystem.IsMacOS())
        {
            Assert.ThrowsExactly<ArgumentException>(() => Create(["probe.so"]));
            Assert.ThrowsExactly<ArgumentException>(() => Create(["probe_output.so", "PROBE_output.so"]));
        }
        else
        {
            Assert.HasCount(1, Create(["probe.so"]).PluginLibraries);
            Assert.HasCount(2, Create(["probe_output.so", "PROBE_output.so"]).PluginLibraries);
        }
    }

    /// <summary>
    /// Older formats cannot list modules, and version five requires a complete module list of valid names.
    /// </summary>
    /// <param name="fragment">The invalid format and inventory fields.</param>
    [TestMethod]
    [DataRow("\"formatVersion\":4,\"upgradeScripts\":[],\"versionControlFiles\":[],\"scriptDirectory\":\"scripts\",\"pluginLibraries\":[]")]
    [DataRow("\"formatVersion\":3,\"upgradeScripts\":[],\"versionControlFiles\":[],\"pluginLibraries\":[\"probe_output.so\"]")]
    [DataRow("\"formatVersion\":6,\"upgradeScripts\":[],\"versionControlFiles\":[],\"pluginLibraries\":[]")]
    [DataRow("\"formatVersion\":5,\"upgradeScripts\":[],\"versionControlFiles\":[]")]
    [DataRow("\"formatVersion\":5,\"upgradeScripts\":[],\"pluginLibraries\":[]")]
    [DataRow("\"formatVersion\":5,\"upgradeScripts\":[],\"versionControlFiles\":[],\"pluginLibraries\":null")]
    [DataRow("\"formatVersion\":5,\"upgradeScripts\":[],\"versionControlFiles\":[],\"pluginLibraries\":[null]")]
    [DataRow("\"formatVersion\":5,\"upgradeScripts\":[],\"versionControlFiles\":[],\"pluginLibraries\":[1]")]
    [DataRow("\"formatVersion\":5,\"upgradeScripts\":[],\"versionControlFiles\":[],\"pluginLibraries\":[\"../probe_output.so\"]")]
    [DataRow("\"formatVersion\":5,\"upgradeScripts\":[],\"versionControlFiles\":[],\"pluginLibraries\":[\"Probe.so\"]")]
    [DataRow("\"formatVersion\":5,\"upgradeScripts\":[],\"versionControlFiles\":[],\"pluginLibraries\":[],\"pluginLibraries\":[]")]
    [DataRow("\"formatVersion\":5,\"upgradeScripts\":[],\"versionControlFiles\":[],\"pluginLibraries\":[],\"scriptDirectory\":null")]
    public void RejectsMalformedPluginManifests(string fragment)
    {
        File.WriteAllText(Path.Join(_directory, PublishedExtension.FileName),
            "{" + fragment + ",\"postgresMajor\":18,\"runtimeIdentifier\":\"linux-x64\",\"library\":\"Probe.so\",\"control\":\"probe.control\",\"sql\":\"probe--2.sql\"}");
        Assert.ThrowsExactly<FormatException>(() => PublishedExtension.Read(_directory));
    }

    /// <summary>
    /// A missing module keeps the previous publication's files; a repaired publication deletes only the modules it no
    /// longer lists, and keeps one that became the library's own name.
    /// </summary>
    [TestMethod]
    public void MissingModulePreservesPreviousFilesUntilRepair()
    {
        PublishedExtension previous = Create(["probe_output.so", "probe_decoder.so"]);
        string extension = Path.Join(_directory, "extension");
        Directory.CreateDirectory(extension);
        File.WriteAllText(Path.Join(extension, previous.Control), "control");
        File.WriteAllText(Path.Join(extension, previous.Sql), "sql");
        foreach (string file in new[] { previous.Library, "probe_output.so", "probe_decoder.so", "unlisted.so" })
        {
            File.WriteAllText(Path.Join(_directory, file), "retained");
        }

        previous.CompletePublish(_directory);
        PublishedExtension next = Create(["probe_output.so", "probe_logical.so"]);
        FileNotFoundException error = Assert.ThrowsExactly<FileNotFoundException>(() => next.CompletePublish(_directory));
        Assert.AreEqual(Path.Join(_directory, "probe_logical.so"), error.FileName);
        Assert.IsFalse(File.Exists(Path.Join(_directory, PublishedExtension.FileName)));
        Assert.AreEqual("retained", File.ReadAllText(Path.Join(_directory, "probe_decoder.so")));
        File.WriteAllText(Path.Join(_directory, "probe_logical.so"), "next");
        next.CompletePublish(_directory);
        Assert.AreSequenceEqual<string>(["probe_logical.so", "probe_output.so"], PublishedExtension.Read(_directory).PluginLibraries);
        Assert.IsFalse(File.Exists(Path.Join(_directory, "probe_decoder.so")));
        Assert.AreEqual("retained", File.ReadAllText(Path.Join(_directory, "probe_output.so")));
        Assert.AreEqual("retained", File.ReadAllText(Path.Join(_directory, "unlisted.so")));

        // The library takes a former module's name, so that file is now the library and stays.
        File.WriteAllText(Path.Join(extension, "probe--3.sql"), "sql");
        new PublishedExtension(18, "linux-x64", "probe_output.so", "probe.control", "probe--3.sql", [], [], null, []).CompletePublish(_directory);
        Assert.IsEmpty(PublishedExtension.Read(_directory).PluginLibraries);
        Assert.AreEqual("retained", File.ReadAllText(Path.Join(_directory, "probe_output.so")));
        Assert.IsFalse(File.Exists(Path.Join(_directory, "probe_logical.so")));
        Assert.AreEqual("retained", File.ReadAllText(Path.Join(_directory, "Probe.so")));
    }

    private static PublishedExtension Create(IReadOnlyList<string> modules, string? scriptDirectory = null)
        => new(18, "linux-x64", "Probe.so", "probe.control", "probe--2.sql", [], [], scriptDirectory, modules);
}
