namespace Ankus.PgConfig.Tests;

/// <summary>
/// Verifies native extension builds use the selected SDK without losing PostgreSQL's other compiler flags.
/// </summary>
/// <param name="context">The current test's cancellation context.</param>
[TestClass]
public sealed class NativeCompilerArgumentsTests(TestContext context)
{
    /// <summary>
    /// Removes every historical root while retaining definitions, include order and token boundaries.
    /// </summary>
    [TestMethod]
    public void SelectedSdkReplacesHistoricalRoots()
    {
        string[] arguments =
        [
            "-I/dependency one/include", "-isysroot", "/old sdk", "-DNAME=\"two words\"",
            "--sysroot=/second", "-isysroot/third", "--sysroot", "/fourth", "-isysroot=/fifth",
            "-I/dependency two/include", "-DROOT=--sysroot=/literal", "-arch", "x86_64",
        ];
        string[] original = [.. arguments];

        IReadOnlyList<string> result = NativeCompilerArguments.WithMacOsSdk(arguments, "/selected SDK");

        Assert.AreSequenceEqual<string>(
            ["-I/dependency one/include", "-DNAME=\"two words\"", "-I/dependency two/include",
                "-DROOT=--sysroot=/literal", "-arch", "x86_64", "-isysroot", "/selected SDK"], result);
        Assert.AreSequenceEqual(original, arguments);
    }

    /// <summary>
    /// A build without a recorded SDK still uses the active development toolchain.
    /// </summary>
    [TestMethod]
    public void SelectedSdkIsAddedWithoutHistoricalRoot()
    {
        Assert.AreSequenceEqual<string>(["-isysroot", "/selected"], NativeCompilerArguments.WithMacOsSdk([], "/selected"));
        Assert.AreSequenceEqual<string>(["-DVALUE=42", "-isysroot", "/selected"],
            NativeCompilerArguments.WithMacOsSdk(["-DVALUE=42"], "/selected"));
    }

    /// <summary>
    /// Missing root values must not consume an unrelated include or definition.
    /// </summary>
    /// <param name="option">The incomplete SDK option.</param>
    [TestMethod]
    [DataRow("-isysroot")]
    [DataRow("--sysroot")]
    [DataRow("--sysroot=")]
    [DataRow("-isysroot=")]
    public void MissingSdkPathIsRejected(string option)
    {
        Assert.ThrowsExactly<FormatException>(() => NativeCompilerArguments.WithMacOsSdk([option], "/selected"));
        Assert.ThrowsExactly<FormatException>(() => NativeCompilerArguments.WithMacOsSdk([option, "-DKEEP=1"], "/selected"));
        Assert.ThrowsExactly<FormatException>(() => NativeCompilerArguments.WithMacOsSdk([option, ""], "/selected"));
    }

    /// <summary>
    /// SDKROOT selects an existing absolute path, including paths containing spaces.
    /// </summary>
    [TestMethod]
    public async Task ExplicitSdkIsPreserved()
    {
        string directory = Directory.CreateTempSubdirectory("ankus selected SDK ").FullName;
        try
        {
            Assert.AreEqual(directory, await NativeCompilerArguments.ResolveMacOsSdkAsync(directory, context.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    /// <summary>
    /// An invalid explicit SDK is a configuration error instead of silently selecting another SDK.
    /// </summary>
    [TestMethod]
    public async Task InvalidExplicitSdkIsRejected()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-sdk-").FullName;
        try
        {
            await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() => NativeCompilerArguments.ResolveMacOsSdkAsync(
                Path.Join(directory, "missing"), context.CancellationToken));
            await Assert.ThrowsExactlyAsync<DirectoryNotFoundException>(() => NativeCompilerArguments.ResolveMacOsSdkAsync(
                "relative.sdk", context.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory);
        }
    }

    /// <summary>
    /// Cancellation stops SDK discovery before starting a native tool.
    /// </summary>
    [TestMethod]
    public async Task CanceledSdkSelectionIsRejected()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeCompilerArguments.CreateAsync([], cancellation.Token));
        await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeCompilerArguments.ResolveMacOsSdkAsync(null, cancellation.Token));
    }
}
