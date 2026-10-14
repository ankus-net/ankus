namespace Ankus.Build.Tests;

public sealed partial class NativeBindingCacheTests
{
    /// <summary>
    /// Validated readers overlap, while replacement, cancellation and retention respect every live reader.
    /// </summary>
    /// <param name="cancelWriter">Whether the first replacement attempt is cancelled while a reader remains.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CacheReadersOverlapAndProtectReplacement(bool cancelWriter)
    {
        string root = Directory.CreateTempSubdirectory("ankus-binding-cache-readers-").FullName;
        try
        {
            string input = Path.Join(root, "input");
            string cache = Path.Join(root, "cache");
            await File.WriteAllTextAsync(input, "original", context.CancellationToken);
            int produced = 0;
            async Task<IReadOnlyList<NativeBindingCacheFile>> Produce(string stage, CancellationToken token)
            {
                Interlocked.Increment(ref produced);
                string content = await File.ReadAllTextAsync(input, token);
                await File.WriteAllTextAsync(Path.Join(stage, "binding.dll"), content, token);
                return [new(input, await NativeBindingCache.HashAsync(input, token))];
            }

            await using (NativeBindingCacheLease seed = await NativeBindingCache.GetAsync(cache, s_key, Produce, context.CancellationToken))
            {
                Assert.AreEqual("original", await File.ReadAllTextAsync(Path.Join(seed.Directory, "binding.dll"), context.CancellationToken));
            }

            // Bound a regression that serializes these readers instead of releasing the first lease to let it pass.
            using var readers = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            readers.CancelAfter(TimeSpan.FromSeconds(30));
            await using NativeBindingCacheLease first = await NativeBindingCache.GetAsync(cache, s_key, Produce, readers.Token);
            await using NativeBindingCacheLease second = await NativeBindingCache.GetAsync(cache, s_key, Produce, readers.Token);
            Assert.AreEqual(first.Directory, second.Directory);
            Assert.AreEqual(1, produced);
            string artifact = Path.Join(first.Directory, "binding.dll");
            await NativeBindingCache.TrimAsync(cache, 0, context.CancellationToken);
            Assert.AreEqual("original", await File.ReadAllTextAsync(artifact, context.CancellationToken));

            await File.WriteAllTextAsync(input, "modified", context.CancellationToken);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            Task<NativeBindingCacheLease>? replacement = null;
            Task<string>? queuedReader = null;
            try
            {
                replacement = NativeBindingCache.GetAsync(cache, s_key, Produce, cancellation.Token);
                await WaitForWriterAsync(Path.Join(cache, s_key + ".admission.lock"), replacement, cancellation.Token);
                async Task<string> ReadQueuedAsync()
                {
                    await using NativeBindingCacheLease lease = await NativeBindingCache.GetAsync(cache, s_key, Produce, readers.Token);
                    return await File.ReadAllTextAsync(Path.Join(lease.Directory, "binding.dll"), readers.Token);
                }

                // A new reader must wait at admission, before attempting to open the manifest.
                // Without admission, opening this exclusively held manifest faults immediately.
                using (new FileStream(Path.Join(first.Directory, "manifest.json"), FileMode.Open,
                    FileAccess.ReadWrite, FileShare.None))
                {
                    queuedReader = ReadQueuedAsync();
                    Assert.IsFalse(queuedReader.IsCompleted, "A waiting writer must prevent new reader validation.");
                }

                await first.DisposeAsync();
                Assert.ThrowsExactly<IOException>(() =>
                {
                    using FileStream ownership = new(Path.Join(cache, s_key + ".lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
                });
                Assert.AreEqual("original", await File.ReadAllTextAsync(artifact, context.CancellationToken));
                Assert.AreEqual(1, produced);

                if (cancelWriter)
                {
                    await cancellation.CancelAsync();
                    OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(async () => await replacement);
                    Assert.AreEqual(cancellation.Token, error.CancellationToken);
                    Assert.AreEqual("original", await File.ReadAllTextAsync(artifact, context.CancellationToken));
                }

                await second.DisposeAsync();
                if (cancelWriter)
                {
                    replacement = NativeBindingCache.GetAsync(cache, s_key, Produce, readers.Token);
                }

                await using (NativeBindingCacheLease changed = await replacement)
                {
                    Assert.AreEqual(2, produced);
                    Assert.AreEqual("modified", await File.ReadAllTextAsync(Path.Join(changed.Directory, "binding.dll"), context.CancellationToken));
                }

                Assert.AreEqual("modified", await queuedReader);
            }
            finally
            {
                await cancellation.CancelAsync();
                await readers.CancelAsync();
                if (replacement is not null)
                {
                    await Task.WhenAny(replacement);
                    if (replacement.IsCompletedSuccessfully)
                    {
                        NativeBindingCacheLease completed = await replacement;
                        await completed.DisposeAsync();
                    }
                }

                if (queuedReader is not null)
                {
                    await Task.WhenAny(queuedReader);
                }
            }
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Concurrent consumers converge on one published version after either a cold miss or changed inputs.
    /// </summary>
    /// <param name="stale">Whether consumers encounter an existing entry with a changed dependency.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CacheConcurrentMissesPublishOnce(bool stale)
    {
        string root = Directory.CreateTempSubdirectory("ankus-binding-cache-burst-").FullName;
        try
        {
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            cancellation.CancelAfter(TimeSpan.FromSeconds(30));
            string input = Path.Join(root, "input");
            string cache = Path.Join(root, "cache");
            await File.WriteAllTextAsync(input, "original", cancellation.Token);
            int produced = 0;
            async Task<IReadOnlyList<NativeBindingCacheFile>> Produce(string stage, CancellationToken token)
            {
                Interlocked.Increment(ref produced);
                string content = await File.ReadAllTextAsync(input, token);
                await File.WriteAllTextAsync(Path.Join(stage, "binding.dll"), content, token);
                return [new(input, await NativeBindingCache.HashAsync(input, token))];
            }

            if (stale)
            {
                await using NativeBindingCacheLease seed = await NativeBindingCache.GetAsync(cache, s_key, Produce, cancellation.Token);
                Assert.AreEqual("original", await File.ReadAllTextAsync(Path.Join(seed.Directory, "binding.dll"), cancellation.Token));
            }

            await File.WriteAllTextAsync(input, "modified", cancellation.Token);
            var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            async Task ReadAsync()
            {
                await start.Task;
                await using NativeBindingCacheLease lease = await NativeBindingCache.GetAsync(cache, s_key, Produce, cancellation.Token);
                Assert.AreEqual("modified", await File.ReadAllTextAsync(Path.Join(lease.Directory, "binding.dll"), cancellation.Token));
            }

            Task[] readers = [.. Enumerable.Range(0, 8).Select(_ => ReadAsync())];
            start.SetResult();
            await Task.WhenAll(readers);
            Assert.AreEqual(stale ? 2 : 1, produced);
            Assert.HasCount(1, Directory.GetDirectories(cache));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Observes exclusive writer admission while existing reader leases still prevent replacement.
    /// </summary>
    private static async Task WaitForWriterAsync(string path, Task replacement, CancellationToken cancellationToken)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Assert.IsFalse(replacement.IsCompleted, "A replacement cannot finish while reader leases remain active.");
            try
            {
                using FileStream admission = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33 ||
                error.HResult == (OperatingSystem.IsMacOS() ? 35 : 11))
            {
                return;
            }

            await Task.Delay(10, cancellationToken);
        }
    }
}
