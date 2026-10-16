namespace Ankus.Build.Tests;

/// <summary>
/// Verifies which native tools the Windows layout probes run.
/// </summary>
/// <param name="context">The current test's cancellation context.</param>
[TestClass]
public sealed class NativeProbeToolTests(TestContext context)
{
    private readonly string _root = Directory.CreateTempSubdirectory("ankus probe tools ").FullName;

    /// <summary>
    /// Removes the test's files.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// The probe links with the <c>link.exe</c> beside the selected <c>cl.exe</c>. A bare <c>link.exe</c> ran Git for
    /// Windows' coreutils <c>link</c>, first on <c>PATH</c> outside a Developer Prompt, which failed with
    /// "link: extra operand '/OUT:...'".
    /// </summary>
    [TestMethod]
    public async Task LinksWithTheLinkerBesideTheCompiler()
    {
        string tools = Directory.CreateDirectory(Path.Join(_root, "VC", "Tools", "MSVC", "bin", "Hostx64", "x64")).FullName;
        string compiler = Path.Join(tools, "cl.exe");
        await File.WriteAllTextAsync(compiler, "", context.CancellationToken);
        Assert.AreEqual("link.exe", NativeBindingLayoutCommand.FindLinker(compiler));
        await File.WriteAllTextAsync(Path.Join(tools, "link.exe"), "", context.CancellationToken);
        Assert.AreEqual(Path.Join(tools, "link.exe"), NativeBindingLayoutCommand.FindLinker(compiler));
    }

    /// <summary>
    /// A compiler named without a directory, as in a Developer Prompt, keeps the linker that prompt puts on <c>PATH</c>.
    /// </summary>
    [TestMethod]
    public void CompilerFromPathKeepsTheLinkerFromPath()
        => Assert.AreEqual("link.exe", NativeBindingLayoutCommand.FindLinker("cl.exe"));

    /// <summary>
    /// An LLVM tool that is neither on <c>PATH</c> nor installed is run by name, so the failure names it.
    /// </summary>
    [TestMethod]
    public void MissingLlvmToolKeepsItsName()
    {
        const string Tool = "ankus-missing-llvm-tool.exe";
        Assert.AreEqual(Tool, NativeBindingLayoutCommand.FindLlvmTool(Tool));
    }
}
