namespace Ankus.Build.Tests;

/// <summary>
/// Invalid requests stop before discovery, process creation or mutation of a prior final record artifact.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class NativeBindingRecordCommandTests(TestContext context)
{
    /// <summary>
    /// Invalid arities never access missing inputs or toolchains.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(10)]
    public async Task InvalidRecordCommandAritiesFailExplicitly(int count)
    {
        ArgumentException failure = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            NativeBindingRecordCommand.RunAsync([.. Enumerable.Repeat("", count)], context.CancellationToken));
        Assert.AreEqual("arguments", failure.ParamName);
    }

    /// <summary>
    /// Invalid symbol identities and duplicate requests preserve an existing contract byte-for-byte.
    /// </summary>
    [TestMethod]
    [DataRow("missing_native_symbol\n")]
    [DataRow("ExecutorRun\nExecutorRun\n")]
    [DataRow("ExecutorRun;\n")]
    public async Task InvalidRecordSelectionsPreserveOutput(string names)
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-record-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string list = Path.Join(directory, "symbols.txt");
            string contract = Path.Join(directory, "native-records.json");
            await File.WriteAllTextAsync(list, names, context.CancellationToken);
            await File.WriteAllTextAsync(contract, "existing-contract", context.CancellationToken);
            await Assert.ThrowsExactlyAsync<FormatException>(() => NativeBindingRecordCommand.RunAsync(
                [list, "18", "missing-pg-config", directory], context.CancellationToken));
            Assert.AreEqual("existing-contract", await File.ReadAllTextAsync(contract, context.CancellationToken));
            Assert.AreSequenceEqual<string>(["native-records.json", "symbols.txt"], Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeBindingRecordCommand.RunAsync(
                [list, "18", "missing-pg-config", directory], cancellation.Token));
            Assert.AreEqual("existing-contract", await File.ReadAllTextAsync(contract, context.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
