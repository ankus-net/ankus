namespace Ankus.Build.Tests;

/// <summary>
/// Verifies binding cache content, ownership, failure recovery and cancellation against real files.
/// </summary>
/// <param name="context">The test's cooperative cancellation context.</param>
[TestClass]
public sealed partial class NativeBindingCacheTests(TestContext context)
{
    /// <summary>
    /// A request key in the cache's 64-digit hexadecimal form.
    /// </summary>
    private static readonly string s_key = string.Concat(Enumerable.Repeat("0123456789ABCDEF", 4));

    /// <summary>
    /// Linked SDK inputs reuse unchanged bytes but invalidate when their target or content changes.
    /// </summary>
    /// <param name="retarget">Whether to replace the link instead of modifying its target.</param>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CacheTracksLinkedDependencyContent(bool retarget)
    {
        string root = Directory.CreateTempSubdirectory("ankus-binding-cache-link-").FullName;
        try
        {
            string target = Path.Join(root, "target");
            string input = Path.Join(root, "input");
            string cache = Path.Join(root, "cache");
            await File.WriteAllTextAsync(target, "original", context.CancellationToken);
            File.CreateSymbolicLink(input, target);
            int produced = 0;
            async Task<IReadOnlyList<NativeBindingCacheFile>> Produce(string stage, CancellationToken token)
            {
                produced++;
                string hash = await NativeBindingCache.HashAsync(input, token);
                await File.WriteAllTextAsync(Path.Join(stage, "binding.dll"), await File.ReadAllTextAsync(input, token), token);
                return [new(input, hash)];
            }

            for (int attempt = 0; attempt < 2; attempt++)
            {
                await using NativeBindingCacheLease lease = await NativeBindingCache.GetAsync(cache, s_key, Produce, context.CancellationToken);
                Assert.AreEqual("original", await File.ReadAllTextAsync(Path.Join(lease.Directory, "binding.dll"), context.CancellationToken));
                Assert.AreEqual(1, produced);
            }

            DateTime timestamp = File.GetLastWriteTimeUtc(target);
            if (retarget)
            {
                target = Path.Join(root, "replacement");
                await File.WriteAllTextAsync(target, "modified", context.CancellationToken);
                File.Delete(input);
                File.CreateSymbolicLink(input, target);
            }
            else
            {
                await File.WriteAllTextAsync(target, "modified", context.CancellationToken);
            }

            File.SetLastWriteTimeUtc(target, timestamp);
            await using NativeBindingCacheLease changed = await NativeBindingCache.GetAsync(cache, s_key, Produce, context.CancellationToken);
            Assert.AreEqual(2, produced);
            Assert.AreEqual("modified", await File.ReadAllTextAsync(Path.Join(changed.Directory, "binding.dll"), context.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A linked output cannot be published as an owned immutable artifact even when its bytes match.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Linux | OperatingSystems.OSX)]
    public async Task CacheRejectsLinkedArtifactsAndRecovers()
    {
        string root = Directory.CreateTempSubdirectory("ankus-binding-cache-artifact-link-").FullName;
        try
        {
            string target = Path.Join(root, "target");
            string cache = Path.Join(root, "cache");
            await File.WriteAllTextAsync(target, "verified", context.CancellationToken);
            IOException error = await Assert.ThrowsExactlyAsync<IOException>(() => NativeBindingCache.GetAsync(cache, s_key, (stage, _) =>
            {
                File.CreateSymbolicLink(Path.Join(stage, "binding.dll"), target);
                return Task.FromResult<IReadOnlyList<NativeBindingCacheFile>>([]);
            }, context.CancellationToken));
            Assert.Contains("artifacts changed before publication", error.Message);
            Assert.IsEmpty(Directory.GetDirectories(cache));
            Assert.AreEqual("verified", await File.ReadAllTextAsync(target, context.CancellationToken));
            await using NativeBindingCacheLease recovered = await NativeBindingCache.GetAsync(cache, s_key, async (stage, token) =>
            {
                await File.WriteAllTextAsync(Path.Join(stage, "binding.dll"), "verified", token);
                return [];
            }, context.CancellationToken);
            string artifact = Path.Join(recovered.Directory, "binding.dll");
            Assert.IsFalse(File.GetAttributes(artifact).HasFlag(FileAttributes.ReparsePoint));
            Assert.AreEqual("verified", await File.ReadAllTextAsync(artifact, context.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Repeated consumers reuse verified bytes while same-timestamp input and artifact changes force production.
    /// </summary>
    /// <param name="change">The independent cache invalidation boundary.</param>
    [TestMethod]
    [DataRow("artifact")]
    [DataRow("input")]
    [DataRow("missing-artifact")]
    [DataRow("manifest")]
    [DataRow("extra-file")]
    [DataRow("added-input")]
    [DataRow("removed-input")]
    [DataRow("added-directory")]
    public async Task CacheRebuildsChangedContent(string change)
    {
        string root = Directory.CreateTempSubdirectory("ankus-binding-cache-").FullName;
        try
        {
            string cache = Path.Join(root, "cache");
            string inputs = Directory.CreateDirectory(Path.Join(root, "inputs")).FullName;
            string input = Path.Join(inputs, "input");
            await File.WriteAllTextAsync(input, "original", context.CancellationToken);
            string extraInput = Path.Join(inputs, "extra");
            string candidate = Path.Join(root, "preferred-package");
            if (change == "removed-input")
            {
                await File.WriteAllTextAsync(extraInput, "extra", context.CancellationToken);
            }

            int produced = 0;
            async Task<IReadOnlyList<NativeBindingCacheFile>> Produce(string stage, CancellationToken token)
            {
                produced++;
                string hash = await NativeBindingCache.HashAsync(input, token);
                await File.WriteAllTextAsync(Path.Join(stage, "binding.dll"), await File.ReadAllTextAsync(input, token), token);
                return [new(input, hash), NativeBindingCache.SnapshotDirectory(inputs), NativeBindingCache.SnapshotDirectory(candidate)];
            }

            string entry;
            await using (NativeBindingCacheLease lease = await NativeBindingCache.GetAsync(cache, s_key, Produce, context.CancellationToken))
            {
                entry = lease.Directory;
                Assert.AreEqual("original", await File.ReadAllTextAsync(Path.Join(entry, "binding.dll"), context.CancellationToken));
            }

            await using (NativeBindingCacheLease lease = await NativeBindingCache.GetAsync(cache, s_key, Produce, context.CancellationToken))
            {
                Assert.AreEqual(entry, lease.Directory);
                Assert.AreEqual(1, produced);
            }

            string artifact = Path.Join(entry, "binding.dll");
            if (change is "artifact" or "input")
            {
                string file = change == "input" ? input : artifact;
                DateTime timestamp = File.GetLastWriteTimeUtc(file);
                await File.WriteAllTextAsync(file, "modified", context.CancellationToken);
                File.SetLastWriteTimeUtc(file, timestamp);
            }
            else if (change == "missing-artifact")
            {
                File.Delete(artifact);
            }
            else if (change == "manifest")
            {
                await File.WriteAllTextAsync(Path.Join(entry, "manifest.json"), "not json", context.CancellationToken);
            }
            else if (change == "added-input")
            {
                await File.WriteAllTextAsync(extraInput, "extra", context.CancellationToken);
            }
            else if (change == "removed-input")
            {
                File.Delete(extraInput);
            }
            else if (change == "added-directory")
            {
                Directory.CreateDirectory(candidate);
            }
            else
            {
                await File.WriteAllTextAsync(Path.Join(entry, "unexpected"), "changed", context.CancellationToken);
            }

            await using (NativeBindingCacheLease lease = await NativeBindingCache.GetAsync(cache, s_key, Produce, context.CancellationToken))
            {
                Assert.AreEqual(2, produced);
                Assert.AreEqual(change == "input" ? "modified" : "original",
                    await File.ReadAllTextAsync(Path.Join(lease.Directory, "binding.dll"), context.CancellationToken));
                Assert.AreSequenceEqual<string>(["binding.dll", "manifest.json"], Directory.GetFiles(lease.Directory).Select(static file => Path.GetFileName(file)).Order(StringComparer.Ordinal));
            }

            Assert.AreSequenceEqual<string>([entry], Directory.GetDirectories(cache));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A consumer's lease prevents replacement until its reads finish, and a cancelled waiter cannot enter production.
    /// </summary>
    /// <param name="cancelBeforeWaiting">Whether cancellation precedes lock acquisition.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CacheOwnershipProtectsReadersAndCancelsWaiters(bool cancelBeforeWaiting)
    {
        string root = Directory.CreateTempSubdirectory("ankus-binding-cache-lock-").FullName;
        try
        {
            int produced = 0;
            async Task<IReadOnlyList<NativeBindingCacheFile>> Produce(string stage, CancellationToken token)
            {
                produced++;
                await File.WriteAllTextAsync(Path.Join(stage, "binding.dll"), "verified", token);
                return [];
            }

            await using (NativeBindingCacheLease first = await NativeBindingCache.GetAsync(root, s_key, Produce, context.CancellationToken))
            {
                using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
                if (cancelBeforeWaiting)
                {
                    await cancellation.CancelAsync();
                }

                Task<NativeBindingCacheLease> waiting = NativeBindingCache.GetAsync(root, s_key, Produce, cancellation.Token);
                if (!cancelBeforeWaiting)
                {
                    Assert.IsFalse(waiting.IsCompleted);
                    await cancellation.CancelAsync();
                }

                OperationCanceledException error = await Assert.ThrowsAsync<OperationCanceledException>(async () => await waiting);
                Assert.AreEqual(cancellation.Token, error.CancellationToken);
                Assert.IsTrue(waiting.IsCanceled);
                Assert.AreEqual(1, produced);
                Assert.AreEqual("verified", await File.ReadAllTextAsync(Path.Join(first.Directory, "binding.dll"), context.CancellationToken));
            }

            await using NativeBindingCacheLease recovered = await NativeBindingCache.GetAsync(root, s_key, Produce, context.CancellationToken);
            Assert.AreEqual(1, produced);
            Assert.AreEqual("verified", await File.ReadAllTextAsync(Path.Join(recovered.Directory, "binding.dll"), context.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// Failed or cancelled production releases ownership and removes only its own staging before recovery.
    /// </summary>
    /// <param name="cancel">Whether the producer is cancelled after writing a partial artifact.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task CacheFailureCleansStagingAndRecovers(bool cancel)
    {
        string root = Directory.CreateTempSubdirectory("ankus-binding-cache-failure-").FullName;
        try
        {
            string sentinel = Path.Join(root, "unrelated");
            await File.WriteAllTextAsync(sentinel, "preserve", context.CancellationToken);
            using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(context.CancellationToken);
            async Task<IReadOnlyList<NativeBindingCacheFile>> Fail(string stage, CancellationToken token)
            {
                await File.WriteAllTextAsync(Path.Join(stage, "binding.dll"), "partial", token);
                if (cancel)
                {
                    await cancellation.CancelAsync();
                    return [];
                }

                throw new InvalidOperationException("producer failed");
            }

            if (cancel)
            {
                await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeBindingCache.GetAsync(root, s_key, Fail, cancellation.Token));
            }
            else
            {
                InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => NativeBindingCache.GetAsync(root, s_key, Fail, cancellation.Token));
                Assert.AreEqual("producer failed", error.Message);
            }

            Assert.IsEmpty(Directory.GetDirectories(root));
            Assert.AreEqual("preserve", await File.ReadAllTextAsync(sentinel, context.CancellationToken));
            await using NativeBindingCacheLease recovered = await NativeBindingCache.GetAsync(root, s_key, async (stage, token) =>
            {
                await File.WriteAllTextAsync(Path.Join(stage, "binding.dll"), "complete", token);
                return [];
            }, context.CancellationToken);
            Assert.AreEqual("complete", await File.ReadAllTextAsync(Path.Join(recovered.Directory, "binding.dll"), context.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A dependency changed after the compiler's snapshot cannot certify stale output with a newer input hash.
    /// </summary>
    [TestMethod]
    public async Task CacheRejectsInputsChangedDuringProduction()
    {
        string root = Directory.CreateTempSubdirectory("ankus-binding-cache-input-").FullName;
        try
        {
            string input = Path.Join(root, "input");
            string cache = Path.Join(root, "cache");
            await File.WriteAllTextAsync(input, "before", context.CancellationToken);
            IOException error = await Assert.ThrowsExactlyAsync<IOException>(() => NativeBindingCache.GetAsync(cache, s_key, async (stage, token) =>
            {
                string hash = await NativeBindingCache.HashAsync(input, token);
                await File.WriteAllTextAsync(Path.Join(stage, "binding.dll"), "stale", token);
                await File.WriteAllTextAsync(input, "after", token);
                return [new(input, hash)];
            }, context.CancellationToken));
            Assert.Contains("changed during production", error.Message);
            Assert.IsEmpty(Directory.GetDirectories(cache));
            Assert.AreEqual("after", await File.ReadAllTextAsync(input, context.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    /// <summary>
    /// A failed replacement retains the previous immutable artifact until a complete replacement succeeds.
    /// </summary>
    [TestMethod]
    public async Task CacheFailurePreservesPreviousEntry()
    {
        string root = Directory.CreateTempSubdirectory("ankus-binding-cache-replace-").FullName;
        try
        {
            string input = Path.Join(root, "input");
            string cache = Path.Join(root, "cache");
            await File.WriteAllTextAsync(input, "previous", context.CancellationToken);
            async Task<IReadOnlyList<NativeBindingCacheFile>> Produce(string stage, CancellationToken token)
            {
                string hash = await NativeBindingCache.HashAsync(input, token);
                File.Copy(input, Path.Join(stage, "binding.dll"));
                return [new(input, hash)];
            }

            string entry;
            await using (NativeBindingCacheLease lease = await NativeBindingCache.GetAsync(cache, s_key, Produce, context.CancellationToken))
            {
                entry = lease.Directory;
                Assert.AreEqual("previous", await File.ReadAllTextAsync(Path.Join(entry, "binding.dll"), context.CancellationToken));
            }

            await File.WriteAllTextAsync(input, "replacement", context.CancellationToken);
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => NativeBindingCache.GetAsync(cache, s_key, async (stage, token) =>
            {
                await File.WriteAllTextAsync(Path.Join(stage, "binding.dll"), "partial", token);
                throw new InvalidOperationException("replacement failed");
            }, context.CancellationToken));
            Assert.AreEqual("previous", await File.ReadAllTextAsync(Path.Join(entry, "binding.dll"), context.CancellationToken));
            Assert.AreSequenceEqual<string>([entry], Directory.GetDirectories(cache));
            await using NativeBindingCacheLease recovered = await NativeBindingCache.GetAsync(cache, s_key, Produce, context.CancellationToken);
            Assert.AreEqual("replacement", await File.ReadAllTextAsync(Path.Join(recovered.Directory, "binding.dll"), context.CancellationToken));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
