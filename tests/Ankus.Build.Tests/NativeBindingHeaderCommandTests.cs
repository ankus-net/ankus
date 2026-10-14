namespace Ankus.Build.Tests;

/// <summary>
/// Verifies header command validation, bounded compiler output and cancellation before process creation.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class NativeBindingHeaderCommandTests(TestContext context)
{
    /// <summary>
    /// Invalid command shapes fail before input or toolchain access.
    /// </summary>
    /// <param name="count">The unsupported argument count.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(9)]
    public async Task InvalidHeaderCommandAritiesFailExplicitly(int count)
    {
        ArgumentException failure = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            NativeBindingHeaderCommand.RunAsync([.. Enumerable.Repeat("", count)], context.CancellationToken));
        Assert.AreEqual("arguments", failure.ParamName);
    }

    /// <summary>
    /// Bad selections cannot start discovery or replace a previously validated contract.
    /// </summary>
    /// <param name="names">The invalid requested symbol list.</param>
    [TestMethod]
    [DataRow("missing_native_symbol\n")]
    [DataRow("ExecutorRun\nExecutorRun\n")]
    [DataRow("ExecutorRun;\n")]
    public async Task InvalidHeaderSelectionsPreserveOutput(string names)
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-header-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string list = Path.Join(directory, "symbols.txt");
            string contract = Path.Join(directory, "native-header-types.json");
            await File.WriteAllTextAsync(list, names, context.CancellationToken);
            await File.WriteAllTextAsync(contract, "existing-contract", context.CancellationToken);
            await Assert.ThrowsExactlyAsync<FormatException>(() => NativeBindingHeaderCommand.RunAsync(
                [list, "18", "missing-pg-config", directory], context.CancellationToken));
            Assert.AreEqual("existing-contract", await File.ReadAllTextAsync(contract, context.CancellationToken));
            Assert.AreSequenceEqual<string>(["native-header-types.json", "symbols.txt"], Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// The byte bound preserves every accepted byte across empty, interior and buffer-boundary inputs.
    /// </summary>
    /// <param name="count">The exact accepted input extent.</param>
    /// <param name="cancelable">Whether the stream needs asynchronous cancellation support.</param>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(65535, false)]
    [DataRow(65535, true)]
    [DataRow(65536, false)]
    [DataRow(65536, true)]
    [DataRow(65537, false)]
    [DataRow(65537, true)]
    public async Task BoundedCompilerOutputPreservesExactBytes(int count, bool cancelable)
    {
        byte[] bytes = [.. Enumerable.Range(0, count).Select(static value => unchecked((byte)(value * 31 + 7)))];
        using var source = new MemoryStream(bytes);
        using var destination = new MemoryStream();
        await NativeBindingHeaderCommand.CopyAsync(source, destination, count, cancelable ? context.CancellationToken : CancellationToken.None);
        Assert.AreSequenceEqual(bytes, destination.ToArray());
    }

    /// <summary>
    /// An overrun is an explicit failure and cannot write beyond the allowed output extent.
    /// </summary>
    /// <param name="limit">The maximum accepted extent.</param>
    /// <param name="cancelable">Whether the stream needs asynchronous cancellation support.</param>
    [TestMethod]
    [DataRow(0, false)]
    [DataRow(0, true)]
    [DataRow(1, false)]
    [DataRow(1, true)]
    [DataRow(65536, false)]
    [DataRow(65536, true)]
    public async Task CompilerOutputOverrunsFailExplicitly(int limit, bool cancelable)
    {
        byte[] bytes = [.. Enumerable.Range(0, limit + 1).Select(static value => unchecked((byte)(value * 17)))];
        using var source = new MemoryStream(bytes);
        using var destination = new MemoryStream();
        await Assert.ThrowsExactlyAsync<InvalidDataException>(() => NativeBindingHeaderCommand.CopyAsync(
            source, destination, limit, cancelable ? context.CancellationToken : CancellationToken.None));
        Assert.IsLessThanOrEqualTo(limit, destination.Length);
        Assert.AreSequenceEqual(bytes.Take((int)destination.Length), destination.ToArray());
    }

    /// <summary>
    /// Invalid bounds and cancellation fail without starting a compiler or mutating existing artifacts.
    /// </summary>
    [TestMethod]
    public async Task InvalidBoundsAndPreCancelledCompilationPreserveState()
    {
        using var source = new MemoryStream([1]);
        using var destination = new MemoryStream();
        await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(() => NativeBindingHeaderCommand.CopyAsync(source, destination, -1, context.CancellationToken));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAsync<OperationCanceledException>(() => NativeBindingHeaderCommand.CopyAsync(source, destination, 1, cancellation.Token));
        Assert.AreEqual(0L, destination.Length);
        string file = Path.GetTempFileName();
        try
        {
            await File.WriteAllTextAsync(file, "unchanged", context.CancellationToken);
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeBindingHeaderCommand.CompileAsync(
                "missing-compiler", [], file, Path.GetDirectoryName(file)!, cancellation.Token));
            Assert.AreEqual("unchanged", await File.ReadAllTextAsync(file, context.CancellationToken));
        }
        finally
        {
            File.Delete(file);
        }
    }
}
