namespace Ankus.Build.Tests;

/// <summary>
/// Invalid call requests fail before tool discovery or replacement of a prior generated body.
/// </summary>
/// <param name="context">The current test's cancellation context.</param>
[TestClass]
public sealed class NativeBindingCallCommandTests(TestContext context)
{
    /// <summary>
    /// The public command rejects malformed arities before touching its paths.
    /// </summary>
    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(11)]
    public async Task InvalidCallCommandAritiesFailExplicitly(int count)
    {
        ArgumentException failure = await Assert.ThrowsExactlyAsync<ArgumentException>(() =>
            NativeBindingCallCommand.RunAsync([.. Enumerable.Repeat("", count)], context.CancellationToken));
        Assert.AreEqual("arguments", failure.ParamName);
    }

    /// <summary>
    /// Invalid selections and precancellation preserve output bytes and create no publication temporary file.
    /// </summary>
    [TestMethod]
    [DataRow("missing_native_symbol\n")]
    [DataRow("ExecutorRun\nExecutorRun\n")]
    public async Task InvalidCallSelectionsPreserveOutput(string names)
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-call-command-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string list = Path.Join(directory, "symbols.txt");
            string source = Path.Join(directory, "native-calls.c");
            await File.WriteAllTextAsync(list, names, context.CancellationToken);
            await File.WriteAllTextAsync(source, "existing-call-body", context.CancellationToken);
            await Assert.ThrowsExactlyAsync<FormatException>(() => NativeBindingCallCommand.RunAsync(
                [list, "18", "missing-pg-config", directory], context.CancellationToken));
            Assert.AreEqual("existing-call-body", await File.ReadAllTextAsync(source, context.CancellationToken));
            Assert.AreSequenceEqual<string>(["native-calls.c", "symbols.txt"], Directory.GetFiles(directory).Select(Path.GetFileName).Order(StringComparer.Ordinal)!);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeBindingCallCommand.RunAsync(
                [list, "18", "missing-pg-config", directory], cancellation.Token));
            Assert.AreEqual("existing-call-body", await File.ReadAllTextAsync(source, context.CancellationToken));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Compiler failure and cancellation after validation retain the old source; a successful compile publishes exact bytes.
    /// </summary>
    [TestMethod]
    public async Task CallSourcePublicationRequiresSuccessfulUncancelledCompilation()
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-call-publication-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string final = Path.Join(directory, "native-calls.c");
            await File.WriteAllTextAsync(final, "prior", context.CancellationToken);
            int inspections = 0;
            var expected = new InvalidOperationException("native body rejected");
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingCallCommand.PublishAsync(directory, "candidate", async (path, token) =>
                {
                    Assert.AreEqual("candidate", await File.ReadAllTextAsync(path, token));
                    Assert.AreEqual("prior", await File.ReadAllTextAsync(final, token));
                    inspections++;
                    throw expected;
                }, context.CancellationToken));
            Assert.AreSame(expected, failure);
            Assert.AreEqual(1, inspections);
            Assert.AreEqual("prior", await File.ReadAllTextAsync(final, context.CancellationToken));
            Assert.AreSequenceEqual([final], Directory.GetFiles(directory));
            using var cancellation = new CancellationTokenSource();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeBindingCallCommand.PublishAsync(directory, "cancelled",
                async (path, token) =>
                {
                    Assert.AreEqual("cancelled", await File.ReadAllTextAsync(path, token));
                    inspections++;
                    await cancellation.CancelAsync();
                }, cancellation.Token));
            Assert.AreEqual(2, inspections);
            Assert.AreEqual("prior", await File.ReadAllTextAsync(final, context.CancellationToken));
            Assert.AreSequenceEqual([final], Directory.GetFiles(directory));
            await NativeBindingCallCommand.PublishAsync(directory, "compiled", async (path, token) =>
            {
                Assert.AreEqual("compiled", await File.ReadAllTextAsync(path, token));
                Assert.AreEqual("prior", await File.ReadAllTextAsync(final, token));
                inspections++;
            }, context.CancellationToken);
            Assert.AreEqual(3, inspections);
            Assert.AreEqual("compiled", await File.ReadAllTextAsync(final, context.CancellationToken));
            Assert.AreSequenceEqual([final], Directory.GetFiles(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
