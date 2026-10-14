namespace Ankus.Build.Tests;

/// <summary>
/// Verifies command validation before native discovery or replacement of measured storage.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class NativeBindingStorageCommandTests(TestContext context)
{
    /// <summary>
    /// Invalid argument counts fail before accessing files or a compiler.
    /// </summary>
    /// <param name="count">The invalid argument count.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(9)]
    public async Task InvalidStorageCommandAritiesFailExplicitly(int count)
    {
        ArgumentException failure = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            NativeBindingStorageCommand.RunAsync([.. Enumerable.Repeat("", count)], context.CancellationToken));
        Assert.AreEqual("arguments", failure.ParamName);
    }

    /// <summary>
    /// Invalid selected symbols preserve previously measured bytes before installation discovery.
    /// </summary>
    /// <param name="selection">The invalid function/global selection.</param>
    [TestMethod]
    [DataRow("missing_symbol\n")]
    [DataRow("ExecutorRun\nExecutorRun\n")]
    [DataRow("ExecutorRun;\n")]
    public async Task InvalidStorageSelectionsPreserveContract(string selection)
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-storage-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string list = Path.Join(directory, "symbols.txt");
            string contract = Path.Join(directory, "native-storage.json");
            await File.WriteAllTextAsync(list, selection, context.CancellationToken);
            await File.WriteAllTextAsync(contract, "existing storage", context.CancellationToken);
            await Assert.ThrowsExactlyAsync<FormatException>(() => NativeBindingStorageCommand.RunAsync(
                [list, "18", "missing-pg-config", directory], context.CancellationToken));
            Assert.AreEqual("existing storage", await File.ReadAllTextAsync(contract, context.CancellationToken));
            Assert.AreSequenceEqual<string>(["native-storage.json", "symbols.txt"],
                Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
