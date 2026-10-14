namespace Ankus.Build.Tests;

/// <summary>
/// Verifies compiler inventories retain exact content identity, validation and cancellation.
/// </summary>
/// <param name="context">The test's cooperative cancellation context.</param>
[TestClass]
public sealed class NativeBindingCompilerInputsTests(TestContext context)
{
    /// <summary>
    /// Duplicate and unordered paths retain every distinct selected file and independent SHA-256 identities.
    /// </summary>
    [TestMethod]
    public async Task InventoryPreservesDistinctCompilerInputsAndExactHashes()
    {
        string root = Directory.CreateTempSubdirectory("ankus-compiler-inventory-").FullName;
        try
        {
            string first = Path.Join(root, "alpha input.dll");
            string last = Path.Join(root, "zeta café.dll");
            string inventory = Path.Join(root, "compiler-inputs.txt");
            await File.WriteAllBytesAsync(first, "abc"u8.ToArray(), context.CancellationToken);
            await File.WriteAllBytesAsync(last, [], context.CancellationToken);
            await File.WriteAllLinesAsync(inventory, [last, first, last, first], context.CancellationToken);

            NativeBindingCacheFile[] actual = await NativeBindingCompilerInputs.ReadAsync(inventory, context.CancellationToken);

            Assert.AreSequenceEqual<NativeBindingCacheFile>(
                [new(first, "BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD"),
                    new(last, "E3B0C44298FC1C149AFBF4C8996FB92427AE41E4649B934CA495991B7852B855")], actual);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Same-length input changes are observed even when the original timestamp is restored.
    /// </summary>
    [TestMethod]
    public async Task InventoryObservesSameTimestampContentChanges()
    {
        string root = Directory.CreateTempSubdirectory("ankus-compiler-content-").FullName;
        try
        {
            string input = Path.Join(root, "selected.dll");
            string inventory = Path.Join(root, "compiler-inputs.txt");
            await File.WriteAllBytesAsync(input, "abc"u8.ToArray(), context.CancellationToken);
            await File.WriteAllLinesAsync(inventory, [input], context.CancellationToken);
            DateTime timestamp = File.GetLastWriteTimeUtc(input);
            NativeBindingCacheFile initial = Assert.ContainsSingle(await NativeBindingCompilerInputs.ReadAsync(inventory, context.CancellationToken));
            Assert.AreEqual("BA7816BF8F01CFEA414140DE5DAE2223B00361A396177A9CB410FF61F20015AD", initial.Hash);

            await File.WriteAllBytesAsync(input, "xyz"u8.ToArray(), context.CancellationToken);
            File.SetLastWriteTimeUtc(input, timestamp);
            NativeBindingCacheFile changed = Assert.ContainsSingle(await NativeBindingCompilerInputs.ReadAsync(inventory, context.CancellationToken));

            Assert.AreEqual(input, changed.Path);
            Assert.AreEqual("3608BCA1E44EA6C4D268EB6DB02260269892C0B42B86BBF1E77A6FA16C3C9282", changed.Hash);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Empty or nonabsolute inventories fail instead of producing a partial compiler contract.
    /// </summary>
    /// <param name="invalid">The invalid inventory contents.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("relative.dll\n")]
    [DataRow("\n")]
    public async Task InventoryRejectsInvalidPaths(string invalid)
    {
        string root = Directory.CreateTempSubdirectory("ankus-compiler-invalid-").FullName;
        try
        {
            string inventory = Path.Join(root, "compiler-inputs.txt");
            await File.WriteAllTextAsync(inventory, invalid, context.CancellationToken);

            FormatException error = await Assert.ThrowsExactlyAsync<FormatException>(() =>
                NativeBindingCompilerInputs.ReadAsync(inventory, context.CancellationToken));

            Assert.AreEqual("The binding compiler input snapshot is invalid.", error.Message);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Missing selected files fail and a canceled read cannot return a partial inventory.
    /// </summary>
    [TestMethod]
    public async Task InventoryRejectsMissingInputsAndCancellation()
    {
        string root = Directory.CreateTempSubdirectory("ankus-compiler-missing-").FullName;
        try
        {
            string input = Path.Join(root, "missing.dll");
            string inventory = Path.Join(root, "compiler-inputs.txt");
            await File.WriteAllLinesAsync(inventory, [input], context.CancellationToken);

            await Assert.ThrowsExactlyAsync<FileNotFoundException>(() => NativeBindingCompilerInputs.ReadAsync(inventory, context.CancellationToken));
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsAsync<OperationCanceledException>(() => NativeBindingCompilerInputs.ReadAsync(inventory, cancellation.Token));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
