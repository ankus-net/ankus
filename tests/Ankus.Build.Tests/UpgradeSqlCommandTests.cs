using System.Diagnostics;
using System.Text;
using Ankus.PgConfig;

namespace Ankus.Build.Tests;

/// <summary>
/// Verifies SQL publication and token expansion through real files and Git, without a native compiler.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class UpgradeSqlCommandTests(TestContext context)
{
    private readonly string _root = Directory.CreateTempSubdirectory("ankus upgrade SQL ").FullName;

    /// <summary>
    /// Removes this test's owned project and publication.
    /// </summary>
    [TestCleanup]
    public void Cleanup()
    {
        // Git marks its object files read-only on Windows.
        foreach (string file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
        {
            FileAttributes attributes = File.GetAttributes(file);
            if ((attributes & FileAttributes.ReadOnly) != 0)
            {
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
        }

        Directory.Delete(_root, recursive: true);
    }

    /// <summary>
    /// Projects without Git preserve UTF-8 bytes, line endings, PostgreSQL markers and the configured extension version.
    /// </summary>
    /// <param name="bom">Whether the script starts with a UTF-8 byte-order mark.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PublishesExactUtf8AndVersionWithoutGit(bool bom)
    {
        string[] arguments = Prepare();
        string script = Path.Combine(_root, "probe--base--release.sql");
        string prefix = bom ? "\uFEFF" : "";
        File.WriteAllText(script, prefix + "SELECT 'café 🐘', '@EXTENSION_VERSION@', 'MODULE_PATHNAME';\r\n");
        File.WriteAllLines(arguments[2], [script]);
        await UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken);
        string actual = Path.Combine(arguments[1], "extension", "probe--base--release.sql");
        Assert.AreSequenceEqual(Encoding.UTF8.GetBytes(prefix + "SELECT 'café 🐘', 'release', 'MODULE_PATHNAME';\r\n"),
            File.ReadAllBytes(actual));
        Assert.AreEqual(prefix + "SELECT 'café 🐘', '@EXTENSION_VERSION@', 'MODULE_PATHNAME';\r\n", Encoding.UTF8.GetString(File.ReadAllBytes(script)));
        Assert.AreSequenceEqual<string>(["probe--base--release.sql"], PublishedExtension.Read(arguments[1]).UpgradeScripts);
        Assert.AreEqual("installation SQL", File.ReadAllText(Path.Combine(arguments[1], "extension", "probe--release.sql")));
        Assert.AreEqual("control content", File.ReadAllText(Path.Combine(arguments[1], "extension", "probe.control")));
    }

    /// <summary>
    /// An empty script selection publishes the legacy contract and removes previously owned upgrades.
    /// </summary>
    [TestMethod]
    public async Task EmptySelectionRemovesOnlyPreviouslyPublishedUpgrade()
    {
        string[] arguments = Prepare();
        string script = Path.Combine(_root, "probe--base--release.sql");
        File.WriteAllText(script, "SELECT 1;");
        File.WriteAllLines(arguments[2], [script]);
        await UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken);
        string unrelated = Path.Combine(arguments[1], "extension", "unrelated.sql");
        File.WriteAllText(unrelated, "preserved");
        File.WriteAllText(arguments[2], "");
        await UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken);
        Assert.IsEmpty(PublishedExtension.Read(arguments[1]).UpgradeScripts);
        Assert.IsFalse(File.Exists(Path.Combine(arguments[1], "extension", "probe--base--release.sql")));
        Assert.AreEqual("preserved", File.ReadAllText(unrelated));
    }

    /// <summary>
    /// The project directory selects the Git repository, including when the invocation directory is elsewhere.
    /// </summary>
    [TestMethod]
    public async Task GitTokenUsesCommittedProjectIdentity()
    {
        string[] arguments = Prepare();
        await GitAsync("init", "--quiet", _root);
        await GitAsync("-C", _root, "-c", "user.name=Ankus Tests", "-c", "user.email=tests@example.invalid",
            "-c", "commit.gpgsign=false", "commit", "--allow-empty", "--quiet", "-m", "test identity");
        string expectedHash = (await GitAsync("-C", _root, "rev-parse", "HEAD")).Trim();
        string script = Path.Combine(_root, "probe--base--release.sql");
        File.WriteAllText(script, "SELECT '@GIT_HASH@', '@EXTENSION_VERSION@', '@GIT_HASH@';");
        File.WriteAllLines(arguments[2], [script]);
        await UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken);
        Assert.AreEqual($"SELECT '{expectedHash}', 'release', '{expectedHash}';",
            File.ReadAllText(Path.Combine(arguments[1], "extension", "probe--base--release.sql")));
    }

    /// <summary>
    /// Every invalid input is detected before changing SQL and invalidates the install manifest.
    /// </summary>
    /// <param name="failure">The invalid script partition.</param>
    [TestMethod]
    [DataRow("missing")]
    [DataRow("utf8")]
    [DataRow("git")]
    [DataRow("duplicate")]
    [DataRow("name")]
    public async Task InvalidScriptLeavesSqlIntactAndPublicationUninstallable(string failure)
    {
        string[] arguments = Prepare();
        string extension = Path.Combine(arguments[1], "extension");
        Directory.CreateDirectory(extension);
        string original = Path.Combine(extension, "probe--release.sql");
        File.WriteAllText(original, "previous SQL");
        PublishedExtension.Read(arguments[0]).Write(arguments[1]);
        string valid = Path.Combine(_root, "probe--base--middle.sql");
        File.WriteAllText(valid, "SELECT 1;");
        string invalid = Path.Combine(_root, failure == "name" ? "wrong.sql" : "probe--middle--release.sql");
        File.WriteAllText(invalid, failure == "git" ? "SELECT '@GIT_HASH@';" : "SELECT 2;");
        if (failure == "missing")
        {
            File.Delete(invalid);
        }
        else if (failure == "utf8")
        {
            File.WriteAllBytes(invalid, [255]);
        }

        File.WriteAllLines(arguments[2], [valid, invalid, .. failure == "duplicate" ? new[] { valid } : []]);
        switch (failure)
        {
            case "missing":
                await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken));
                break;
            case "utf8":
                await Assert.ThrowsExactlyAsync<DecoderFallbackException>(() => UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken));
                break;
            case "git":
                InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken));
                Assert.Contains("@GIT_HASH@", error.Message);
                break;
            default:
                await Assert.ThrowsExactlyAsync<ArgumentException>(() => UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken));
                break;
        }

        Assert.AreEqual("previous SQL", File.ReadAllText(original));
        Assert.AreEqual(original, Assert.ContainsSingle(Directory.GetFiles(extension)));
        Assert.IsFalse(File.Exists(Path.Combine(arguments[1], PublishedExtension.FileName)));
    }

    /// <summary>
    /// Argument validation and pre-cancellation leave the existing publication untouched.
    /// </summary>
    [TestMethod]
    public async Task InvalidArgumentsAndCancellationDoNotMutatePublication()
    {
        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(() => UpgradeSqlCommand.RunAsync([], context.CancellationToken));
        Assert.AreEqual("arguments", error.ParamName);
        string[] arguments = Prepare();
        PublishedExtension.Read(arguments[0]).Write(arguments[1]);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => UpgradeSqlCommand.RunAsync(arguments, cancelled.Token));
        Assert.AreEqual("probe.control", PublishedExtension.Read(arguments[1]).Control);
    }

    /// <summary>
    /// Only a successful publication replaces the saved schema, and reuse can leave the existing snapshot unchanged.
    /// </summary>
    [TestMethod]
    public async Task SuccessfulPublicationCommitsSchemaAndFailuresPreserveIt()
    {
        string[] original = Prepare();
        string saved = Path.Combine(original[0], "schema-pg18-tests.json");
        string prepared = Path.Combine(original[0], "schema.generated.json");
        string[] arguments = [.. original, saved];
        File.WriteAllText(prepared, "first schema");
        await UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken);
        Assert.AreEqual("first schema", File.ReadAllText(saved));
        Assert.AreEqual("probe.control", PublishedExtension.Read(original[1]).Control);

        File.WriteAllText(prepared, "second schema");
        string missing = Path.Combine(_root, "probe--base--release.sql");
        File.WriteAllLines(original[2], [missing]);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken));
        Assert.AreEqual("first schema", File.ReadAllText(saved));
        Assert.IsFalse(File.Exists(Path.Combine(original[1], PublishedExtension.FileName)));
        File.WriteAllText(original[2], "");
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => UpgradeSqlCommand.RunAsync(arguments, cancelled.Token));
        Assert.AreEqual("first schema", File.ReadAllText(saved));

        await UpgradeSqlCommand.RunAsync([.. original, ""], context.CancellationToken);
        Assert.AreEqual("first schema", File.ReadAllText(saved));
        Assert.AreEqual("probe.control", PublishedExtension.Read(original[1]).Control);
        await UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken);
        Assert.AreEqual("second schema", File.ReadAllText(saved));
        Assert.IsEmpty(Directory.GetFiles(original[0], "*.tmp"));
    }

    /// <summary>
    /// Failure to commit a schema cannot leave an apparently successful installable publication.
    /// </summary>
    [TestMethod]
    public async Task SchemaCommitFailureInvalidatesPublication()
    {
        string[] original = Prepare();
        string saved = Path.Combine(original[0], "schema-pg18-tests.json");
        File.WriteAllText(saved, "preserved schema");
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            UpgradeSqlCommand.RunAsync([.. original, saved], context.CancellationToken));
        Assert.AreEqual("preserved schema", File.ReadAllText(saved));
        Assert.IsFalse(File.Exists(Path.Combine(original[1], PublishedExtension.FileName)));
        Assert.IsEmpty(Directory.GetFiles(original[0], "*.tmp"));
    }

    /// <summary>
    /// Version controls are read from the native build snapshot and prevalidated before replacing published payloads.
    /// </summary>
    [TestMethod]
    public async Task PublishesVersionControlSnapshotAndRejectsMissingPayload()
    {
        string[] arguments = Prepare();
        var manifest = new PublishedExtension(18, "linux-x64", "Probe.so", "probe.control", "probe--release.sql", [],
            ["probe--base.control", "probe--release.control"]);
        manifest.Write(arguments[0]);
        string extension = Path.Combine(arguments[0], "extension");
        string first = Path.Combine(extension, "probe--base.control");
        string second = Path.Combine(extension, "probe--release.control");
        File.WriteAllText(first, "module_pathname='Old.so'\r\n");
        File.WriteAllText(second, "requires='helper'\n");
        await UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken);
        Assert.AreSequenceEqual(manifest.VersionControlFiles, PublishedExtension.Read(arguments[1]).VersionControlFiles);
        Assert.AreSequenceEqual(File.ReadAllBytes(first), File.ReadAllBytes(Path.Combine(arguments[1], "extension", "probe--base.control")));
        Assert.AreSequenceEqual(File.ReadAllBytes(second), File.ReadAllBytes(Path.Combine(arguments[1], "extension", "probe--release.control")));
        File.WriteAllText(first, "changed");
        File.Delete(second);
        FileNotFoundException error = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken));
        Assert.AreEqual(second, error.FileName);
        Assert.AreEqual("module_pathname='Old.so'\r\n", File.ReadAllText(Path.Combine(arguments[1], "extension", "probe--base.control")));
        Assert.AreEqual("requires='helper'\n", File.ReadAllText(Path.Combine(arguments[1], "extension", "probe--release.control")));
        Assert.IsFalse(File.Exists(Path.Combine(arguments[1], PublishedExtension.FileName)));
    }

    /// <summary>
    /// Versioned libraries resolve MODULE_PATHNAME to each script's target version unless that version's control names a library.
    /// </summary>
    [TestMethod]
    public async Task VersionedLibrariesResolveEachScriptTargetLibrary()
    {
        string[] arguments = Prepare();
        var manifest = new PublishedExtension(18, "linux-x64", "Probe-release.so", "probe.control", "probe--release.sql", [],
            ["probe--legacy.control"]);
        manifest.Write(arguments[0]);
        File.WriteAllText(Path.Combine(arguments[0], "extension", "probe--legacy.control"), "module_pathname='Legacy.so'\n");
        File.WriteAllText(Path.Combine(arguments[1], "Probe-release.so"), "native payload");
        const string Script = "CREATE OR REPLACE FUNCTION f() RETURNS int AS 'MODULE_PATHNAME', 'f' LANGUAGE c; -- MODULE_PATHNAME @EXTENSION_VERSION@\n";
        string[] names = ["probe--base--release.sql", "probe--release--base.sql", "probe--older--legacy.sql"];
        foreach (string name in names)
        {
            File.WriteAllText(Path.Combine(_root, name), Script);
        }

        File.WriteAllLines(arguments[2], names.Select(name => Path.Combine(_root, name)));
        await UpgradeSqlCommand.RunAsync([.. arguments, "", "true"], context.CancellationToken);
        string extension = Path.Combine(arguments[1], "extension");
        Assert.AreEqual(Script.Replace("MODULE_PATHNAME", "Probe-release", StringComparison.Ordinal).Replace("@EXTENSION_VERSION@", "release",
            StringComparison.Ordinal), File.ReadAllText(Path.Combine(extension, names[0])));
        Assert.AreEqual(Script.Replace("MODULE_PATHNAME", "Probe-base", StringComparison.Ordinal).Replace("@EXTENSION_VERSION@", "release",
            StringComparison.Ordinal), File.ReadAllText(Path.Combine(extension, names[1])));
        Assert.AreEqual(Script.Replace("@EXTENSION_VERSION@", "release", StringComparison.Ordinal), File.ReadAllText(Path.Combine(extension, names[2])));
        Assert.AreEqual("module_pathname='Legacy.so'\n", File.ReadAllText(Path.Combine(extension, "probe--legacy.control")));

        await UpgradeSqlCommand.RunAsync([.. arguments, "", "false"], context.CancellationToken);
        Assert.AreEqual(Script.Replace("@EXTENSION_VERSION@", "release", StringComparison.Ordinal), File.ReadAllText(Path.Combine(extension, names[0])));

        new PublishedExtension(18, "linux-x64", "Probe.so", "probe.control", "probe--release.sql").Write(arguments[0]);
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => UpgradeSqlCommand.RunAsync([.. arguments, "", "true"], context.CancellationToken));
        Assert.IsFalse(File.Exists(Path.Combine(arguments[1], PublishedExtension.FileName)));
    }

    /// <summary>
    /// SQL publication preserves the authored directory in metadata without writing outside its artifact directory.
    /// </summary>
    /// <param name="empty">Whether the author explicitly selected the PostgreSQL shared directory.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task PublishesAuthoredScriptDirectoryWithoutInstallingIt(bool empty)
    {
        string[] arguments = Prepare();
        string directory = empty ? "" : Path.Combine(_root, "external SQL");
        var manifest = new PublishedExtension(18, "linux-x64", "Probe.so", "probe.control", "probe--release.sql", [], [], directory);
        manifest.Write(arguments[0]);
        string control = ExtensionControlFile.Format(new Dictionary<string, string> { ["directory"] = directory });
        File.WriteAllText(Path.Combine(arguments[0], "extension", manifest.Control), control);
        await UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken);
        Assert.AreEqual(directory, PublishedExtension.Read(arguments[1]).ScriptDirectory);
        Assert.AreEqual(control, File.ReadAllText(Path.Combine(arguments[1], "extension", manifest.Control)));
        Assert.AreEqual("installation SQL", File.ReadAllText(Path.Combine(arguments[1], "extension", manifest.Sql)));
        Assert.IsFalse(Directory.Exists(Path.Combine(_root, "external SQL")));
    }

    /// <summary>
    /// The plugin module list adds the output plugin modules built beside the library to the manifest. A missing list
    /// means none, a missing module leaves the publication uninstallable, and a later publish without a module deletes
    /// it.
    /// </summary>
    [TestMethod]
    public async Task PublishesOutputPluginModulesBesideTheLibrary()
    {
        string[] arguments = [.. Prepare(), "", "false", Path.Combine(_root, "plugin-libraries.txt")];
        await UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken);
        Assert.IsEmpty(PublishedExtension.Read(arguments[1]).PluginLibraries);

        File.WriteAllLines(arguments[7], ["probe_output.so", ""]);
        await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken));
        Assert.IsFalse(File.Exists(Path.Combine(arguments[1], PublishedExtension.FileName)));
        string module = Path.Combine(arguments[1], "probe_output.so");
        File.WriteAllText(module, "module payload");
        await UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken);
        Assert.AreSequenceEqual<string>(["probe_output.so"], PublishedExtension.Read(arguments[1]).PluginLibraries);

        File.WriteAllText(arguments[7], "");
        await UpgradeSqlCommand.RunAsync(arguments, context.CancellationToken);
        Assert.IsEmpty(PublishedExtension.Read(arguments[1]).PluginLibraries);
        Assert.IsFalse(File.Exists(module));
        Assert.AreEqual("native payload", File.ReadAllText(Path.Combine(arguments[1], "Probe.so")));
    }

    private string[] Prepare()
    {
        string artifacts = Path.Combine(_root, "artifacts");
        string output = Path.Combine(_root, "publication");
        string extension = Path.Combine(artifacts, "extension");
        Directory.CreateDirectory(extension);
        Directory.CreateDirectory(output);
        new PublishedExtension(18, "linux-x64", "Probe.so", "probe.control", "probe--release.sql").Write(artifacts);
        File.WriteAllText(Path.Combine(extension, "probe.control"), "control content");
        File.WriteAllText(Path.Combine(extension, "probe--release.sql"), "installation SQL");
        File.WriteAllText(Path.Combine(output, "Probe.so"), "native payload");
        string list = Path.Combine(_root, "scripts.txt");
        File.WriteAllText(list, "");
        return [artifacts, output, list, _root, "release"];
    }

    private async Task<string> GitAsync(params string[] arguments)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("git") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true },
        };
        foreach (string argument in arguments)
        {
            process.StartInfo.ArgumentList.Add(argument);
        }

        process.Start();
        Task<string> output = process.StandardOutput.ReadToEndAsync(context.CancellationToken);
        Task<string> error = process.StandardError.ReadToEndAsync(context.CancellationToken);
        await process.WaitForExitAsync(context.CancellationToken);
        Assert.AreEqual(0, process.ExitCode, await error);
        return await output;
    }
}
