namespace Ankus.Build.Tests;

/// <summary>
/// Verifies command-level selection validation before toolchain discovery or output mutation.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class NativeBindingSignatureCommandTests(TestContext context)
{
    /// <summary>
    /// Invalid command arities fail before attempting to read any input file.
    /// </summary>
    /// <param name="count">An unsupported number of arguments.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(9)]
    public async Task InvalidArgumentCountsFailExplicitly(int count)
        => await Assert.ThrowsExactlyAsync<ArgumentException>(() => NativeBindingSignatureCommand.RunAsync(
            [.. Enumerable.Repeat(string.Empty, count)], context.CancellationToken));

    /// <summary>
    /// Unknown and duplicate names cannot reach compiler discovery or overwrite existing artifacts.
    /// </summary>
    /// <param name="names">The invalid requested function set.</param>
    [TestMethod]
    [DataRow("unknown_native_function\n")]
    [DataRow("ExecutorRun\nExecutorRun\n")]
    public async Task InvalidFunctionListsPreserveExistingOutput(string names)
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-signature-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string list = Path.Join(directory, "functions.txt");
        string artifact = Path.Join(directory, "native-signatures.json");
        try
        {
            await File.WriteAllTextAsync(list, names, context.CancellationToken);
            await File.WriteAllTextAsync(artifact, "unchanged", context.CancellationToken);
            await Assert.ThrowsExactlyAsync<FormatException>(() => NativeBindingSignatureCommand.RunAsync(
                [list, "18", "missing-pg-config", directory], context.CancellationToken));
            Assert.AreEqual("unchanged", await File.ReadAllTextAsync(artifact, context.CancellationToken));
            Assert.AreSequenceEqual<string>(["functions.txt", "native-signatures.json"],
                Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
