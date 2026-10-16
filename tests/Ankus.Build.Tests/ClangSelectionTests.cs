using System.Runtime.Versioning;

namespace Ankus.Build.Tests;

/// <summary>
/// Verifies how the build chooses the Clang that reads PostgreSQL's headers when none is configured.
/// </summary>
/// <param name="context">The current test's cancellation context.</param>
[TestClass]
public sealed class ClangSelectionTests(TestContext context)
{
    private readonly string _root = Directory.CreateTempSubdirectory("ankus clang selection ").FullName;

    /// <summary>
    /// Removes the test's scripts.
    /// </summary>
    [TestCleanup]
    public void Cleanup() => Directory.Delete(_root, recursive: true);

    /// <summary>
    /// An older Clang first on <c>PATH</c> is passed over for a newer one in a standard location. On macOS, Xcode's Apple
    /// clang 17 is on <c>PATH</c> while Homebrew keeps its LLVM off it, and the build used to stop at Apple's.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task SkipsAnOlderClangForANewerOne()
    {
        string apple = await WriteClangAsync("xcode", "Apple clang version 17.0.0 (clang-1700.6.4.2)");
        string llvm = await WriteClangAsync("homebrew", "Homebrew clang version 23.1.2");
        Assert.AreEqual(llvm, await ClangSelection.FindAsync([Path.Join(_root, "missing", "clang"), apple, llvm], context.CancellationToken));
    }

    /// <summary>
    /// On macOS the search covers Homebrew on Apple silicon and Intel, then MacPorts, which keeps each LLVM release under
    /// <c>/opt/local/libexec/llvm-N</c>; versioned installations are tried newest first.
    /// </summary>
    [TestMethod]
    public void ListsHomebrewThenMacPortsInstallations()
    {
        foreach (string directory in new[] { "opt/homebrew/opt/llvm@20", "opt/local/libexec/llvm-19", "opt/local/libexec/llvm-21", "opt/local/libexec/llvm-config" })
        {
            Directory.CreateDirectory(Path.Join(_root, directory));
        }

        Assert.AreSequenceEqual(
        [
            Path.Join(_root, "opt", "homebrew", "opt", "llvm", "bin", "clang"),
            Path.Join(_root, "opt", "homebrew", "opt", "llvm@20", "bin", "clang"),
            Path.Join(_root, "usr", "local", "opt", "llvm", "bin", "clang"),
            Path.Join(_root, "opt", "local", "libexec", "llvm-21", "bin", "clang"),
            Path.Join(_root, "opt", "local", "libexec", "llvm-19", "bin", "clang"),
        ], ClangSelection.GetMacInstallations(_root));
    }

    /// <summary>
    /// With Xcode's Apple clang first on <c>PATH</c> and LLVM only from MacPorts, as on an Intel Mac that uses MacPorts,
    /// the MacPorts Clang is selected.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task FindsMacPortsLlvm()
    {
        string apple = await WriteClangAsync("xcode", "Apple clang version 17.0.0 (clang-1700.0.13.5)");
        string macports = await WriteClangAsync(Path.Join("opt", "local", "libexec", "llvm-20", "bin"), "clang version 20.1.8");
        Assert.AreEqual(macports, await ClangSelection.FindAsync([apple, .. ClangSelection.GetMacInstallations(_root)], context.CancellationToken));
    }

    /// <summary>
    /// With no Clang new enough, the error names each one found and its version instead of a later preprocessing failure.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task NamesEveryClangThatIsTooOld()
    {
        string apple = await WriteClangAsync("xcode", "Apple clang version 17.0.0 (clang-1700.6.4.2)");
        string old = await WriteClangAsync("distribution", "Ubuntu clang version 18.1.3 (1ubuntu1)");
        FileNotFoundException error = await Assert.ThrowsExactlyAsync<FileNotFoundException>(() =>
            ClangSelection.FindAsync([apple, old], context.CancellationToken));
        Assert.StartsWith($"Ankus needs LLVM Clang 20 or later to read PostgreSQL's headers, and '{apple}' is Apple clang 17, " +
            $"'{old}' is Clang 18. ", error.Message);
        Assert.EndsWith("set AnkusClangPath to its clang.", error.Message);
    }

    /// <summary>
    /// A Clang chosen explicitly that is too old fails with its version and the setting to change, not as an unsupported
    /// identity.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [SupportedOSPlatform("linux")]
    [SupportedOSPlatform("macos")]
    public async Task AnExplicitOlderClangNamesItsVersion()
    {
        string apple = await WriteClangAsync("xcode", "#define __clang_major__ 17");
        InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
            NativeBindingPreprocessor.ObserveAsync(apple, [], "int value;\n", _root, context.CancellationToken));
        Assert.AreEqual($"'{apple}' is Clang 17, and Ankus needs LLVM Clang 20 or later to read PostgreSQL's headers. " +
            "Set AnkusClangPath to a newer clang, or leave it unset so Ankus finds one.", error.Message);
    }

    /// <summary>
    /// The host's own Clang, which the build tests already require, is read as version 20 or later and selected.
    /// </summary>
    [TestMethod]
    public async Task SelectsTheHostClang()
    {
        string compiler = NativeBindingRecordCommand.FindCompiler(OperatingSystem.IsWindows() ? "clang-cl.exe" : "clang");
        int major = (await ClangSelection.GetVersionAsync(compiler, context.CancellationToken))?.Major ?? 0;
        Assert.IsGreaterThanOrEqualTo(ClangSelection.MinimumMajor, major);
        Assert.AreEqual(compiler, await ClangSelection.FindAsync([compiler], context.CancellationToken));
    }

    [UnsupportedOSPlatform("windows")]
    private async Task<string> WriteClangAsync(string directory, string output)
    {
        string path = Path.Join(Directory.CreateDirectory(Path.Join(_root, directory)).FullName, "clang");
        await File.WriteAllTextAsync(path, $"#!/bin/sh\necho '{output}'\n", context.CancellationToken);
        File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        return path;
    }
}
