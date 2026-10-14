namespace Ankus.Build.Tests;

/// <summary>
/// Invalid source requests fail before native discovery or mutation of an existing companion contract.
/// </summary>
/// <param name="context">The test cancellation context.</param>
[TestClass]
public sealed class NativeBindingSourceCommandTests(TestContext context)
{
    /// <summary>
    /// Invalid command boundaries cannot reach installation or compiler discovery.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(2)]
    [DataRow(11)]
    public async Task InvalidSourceCommandAritiesFailExplicitly(int count)
    {
        ArgumentException error = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            NativeBindingSourceCommand.RunAsync([.. Enumerable.Repeat("", count)], context.CancellationToken));
        Assert.AreEqual("arguments", error.ParamName);
    }

    /// <summary>
    /// A cancelled request leaves the prior source intact without starting discovery against an invalid installation.
    /// </summary>
    [TestMethod]
    public async Task CancelledSourceCommandPreservesExistingCompanion()
    {
        string directory = Path.Join(Path.GetTempPath(), "ankus-node-source-cancelled-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Join(directory, "native-binding.g.cs");
            await File.WriteAllTextAsync(file, "existing-source", context.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeBindingSourceCommand.RunAsync(
                ["18", "missing-pg-config", directory], cancellation.Token));
            Assert.AreEqual("existing-source", await File.ReadAllTextAsync(file, context.CancellationToken));
            Assert.AreSequenceEqual<string>([file], Directory.GetFiles(directory));
            Assert.IsEmpty(Directory.GetDirectories(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
