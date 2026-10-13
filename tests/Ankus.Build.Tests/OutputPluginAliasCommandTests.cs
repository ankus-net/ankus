namespace Ankus.Build.Tests;

/// <summary>
/// Verifies the generated output plugin alias modules and the module list the publication manifest reads.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class OutputPluginAliasCommandTests(TestContext context)
{
    private readonly string _root = Directory.CreateTempSubdirectory("ankus plugin aliases ").FullName;

    /// <summary>
    /// Removes this test's artifacts.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// The module declares its own magic block and forwards the initializer to the library by name, so PostgreSQL's
    /// dynamic loader finds the library the same way it found the module.
    /// </summary>
    [TestMethod]
    public void ModuleForwardsTheInitializerToTheLibrary()
    {
        string source = OutputPluginAliasCommand.Source("Probe.Decoder-1.0");
        Assert.Contains("PG_MODULE_MAGIC;", source);
        Assert.Contains("extern PGDLLEXPORT void _PG_output_plugin_init(OutputPluginCallbacks *cb);", source);
        Assert.Contains("load_external_function(\"Probe.Decoder-1.0\", \"_PG_output_plugin_init\", true, NULL);", source);
        Assert.Contains("init(cb);", source);
    }

    /// <summary>
    /// Without aliases the command only rewrites the module list, so a later publish drops modules an earlier one
    /// listed, and it needs neither PostgreSQL nor a compiler.
    /// </summary>
    [TestMethod]
    public async Task NoAliasesClearsTheModuleList()
    {
        string[] arguments = Prepare([]);
        string list = Path.Combine(arguments[0], "plugin-libraries.txt");
        File.WriteAllText(list, "stale.so\n");
        await OutputPluginAliasCommand.RunAsync(arguments, context.CancellationToken);
        Assert.AreEqual("", File.ReadAllText(list));
        Assert.IsEmpty(Directory.GetFiles(arguments[1]));
    }

    /// <summary>
    /// Names that are not plain filenames, or that are the library's own name, fail before anything is written.
    /// </summary>
    /// <param name="alias">The invalid alias.</param>
    [TestMethod]
    [DataRow("../escape")]
    [DataRow("nested/name")]
    [DataRow("two words")]
    [DataRow(".hidden")]
    [DataRow("quote\"name")]
    [DataRow("Probe")]
    [DataRow("PROBE")]
    public async Task InvalidAliasesFailBeforeWriting(string alias)
    {
        string[] arguments = Prepare([alias]);
        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            OutputPluginAliasCommand.RunAsync(arguments, context.CancellationToken));
        Assert.AreEqual("arguments", error.ParamName);
        Assert.IsFalse(File.Exists(Path.Combine(arguments[0], "plugin-libraries.txt")));
    }

    /// <summary>
    /// A library whose name cannot appear in the generated C, and a wrong argument count, are rejected.
    /// </summary>
    [TestMethod]
    public async Task InvalidLibraryAndArgumentsAreRejected()
    {
        string[] arguments = Prepare(["probe_output"]);
        arguments[2] = "bad\"name.so";
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => OutputPluginAliasCommand.RunAsync(arguments, context.CancellationToken));
        Assert.IsFalse(File.Exists(Path.Combine(arguments[0], "plugin-libraries.txt")));
        await Assert.ThrowsExactlyAsync<ArgumentException>(() => OutputPluginAliasCommand.RunAsync(arguments[..8], context.CancellationToken));
    }

    private string[] Prepare(string[] aliases)
    {
        string artifacts = Path.Combine(_root, "artifacts");
        string output = Path.Combine(_root, "publication");
        Directory.CreateDirectory(artifacts);
        Directory.CreateDirectory(output);
        string list = Path.Combine(artifacts, "plugin-aliases.txt");
        File.WriteAllLines(list, aliases);
        return [artifacts, output, "Probe.so", "18", Path.Combine(_root, "missing pg_config"), Path.Combine(_root, "missing compiler"), "", "", list];
    }
}
