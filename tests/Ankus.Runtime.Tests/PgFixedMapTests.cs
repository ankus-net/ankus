namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies fixed maps across collisions, capacity boundaries, process attachment and probe repair.
/// </summary>
[TestClass]
public sealed class PgFixedMapTests
{
    /// <summary>
    /// Insertions append, replacements retain the original key, and removal swaps in the last entry.
    /// </summary>
    [TestMethod]
    public void FixedMapPreservesCollisionsCapacityAndOrder()
    {
        var entries = new PgFixedMapEntry<int, long>[4];
        int[] indices = new int[4];
        int count = 0;
        var comparer = new DecadeComparer();
        var map = new PgFixedMap<int, long>(entries, indices, ref count, comparer);
        Assert.AreEqual(4, map.Capacity);
        Assert.IsTrue(map.IsEmpty);
        Assert.IsFalse(map.IsFull);
        Assert.IsNull(map.Set(11, 101));
        map.Add(21, 201);
        Assert.IsTrue(map.TryAdd(31, 301));
        Assert.IsTrue(map.TrySet(41, 401, out long? absent));
        Assert.IsNull(absent);
        Assert.AreEqual(4, count);
        Assert.IsTrue(map.IsFull);
        Assert.IsFalse(map.TryAdd(19, 999));
        Assert.IsFalse(map.TryAdd(51, 501));
        Assert.IsFalse(map.TrySet(51, 501, out long? rejected));
        Assert.IsNull(rejected);
        Assert.AreEqual(201L, map.Set(29, 202));
        map[39] = 302;
        Assert.IsTrue(map.TrySet(49, 402, out long? previous));
        Assert.AreEqual(401L, previous);
        Assert.AreEqual(202L, map[21]);
        Assert.AreEqual(21, map.Entries[1].Key);
        Assert.AreEqual(202L, map.Entries[1].Value);
        Assert.AreSequenceEqual([new(11, 101), new(21, 202), new(31, 302), new(41, 402)], map.ToArray());
        var enumerated = new List<KeyValuePair<int, long>>();
        foreach (PgFixedMapEntry<int, long> entry in map)
        {
            enumerated.Add(new(entry.Key, entry.Value));
        }

        Assert.AreSequenceEqual([new(11, 101), new(21, 202), new(31, 302), new(41, 402)], enumerated);

        // An independently created view reads the persisted index without rehashing or clearing it.
        var attached = new PgFixedMap<int, long>(entries, indices, ref count, new DecadeComparer());
        Assert.IsTrue(attached.ContainsKey(49));
        Assert.IsTrue(attached.Remove(29, out long removed));
        Assert.AreEqual(202L, removed);
        Assert.AreSequenceEqual([new(11, 101), new(41, 402), new(31, 302)], map.ToArray());
        Assert.AreEqual(402L, map[41]);
        Assert.AreEqual(302L, map[31]);
        Assert.IsFalse(map.ContainsKey(21));
        Assert.IsTrue(map.Remove(31, out long last));
        Assert.AreEqual(302L, last);
        Assert.IsFalse(map.Remove(91, out long missing));
        Assert.AreEqual(0L, missing);
        Assert.IsTrue(map.TryAdd(51, 501));
        Assert.AreSequenceEqual([new(11, 101), new(41, 402), new(51, 501)], map.ToArray());
        KeyValuePair<int, long>[] owned = map.ToArray();
        map.Clear();
        Assert.HasCount(3, owned);
        Assert.IsTrue(attached.IsEmpty);
        Assert.AreSequenceEqual([0, 0, 0, 0], indices);
        Assert.AreSequenceEqual(new PgFixedMapEntry<int, long>[4], entries);
        attached.Add(71, 701);
        Assert.AreEqual(701L, map[79]);
    }

    /// <summary>
    /// Mixed additions, replacements and removals agree with an independent ordered model.
    /// </summary>
    [TestMethod]
    [DataRow(1, false)]
    [DataRow(4, false)]
    [DataRow(7, false)]
    [DataRow(4, true)]
    [DataRow(7, true)]
    public void FixedMapMatchesOrderedModel(int capacity, bool collide)
    {
        var entries = new PgFixedMapEntry<int, int>[capacity];
        int[] indices = new int[capacity];
        int count = 0;
        IEqualityComparer<int> comparer = collide ? new CollisionComparer() : PgFixedKeyComparer.Create<int>();
        var map = new PgFixedMap<int, int>(entries, indices, ref count, comparer);
        var expected = new List<KeyValuePair<int, int>>();
        var random = new Random(73);
        for (int step = 0; step < 1000; step++)
        {
            int key = random.Next(13);
            int found = expected.FindIndex(pair => pair.Key == key);
            switch (random.Next(3))
            {
                case 0:
                    bool allowed = found < 0 && expected.Count < capacity;
                    Assert.AreEqual(allowed, map.TryAdd(key, step));
                    if (allowed)
                    {
                        expected.Add(new(key, step));
                    }

                    break;
                case 1:
                    Assert.AreEqual(found >= 0 || expected.Count < capacity, map.TrySet(key, step, out int? previous));
                    Assert.AreEqual(found >= 0 ? expected[found].Value : null, previous);
                    if (found >= 0)
                    {
                        expected[found] = new(key, step);
                    }
                    else if (expected.Count < capacity)
                    {
                        expected.Add(new(key, step));
                    }

                    break;
                default:
                    Assert.AreEqual(found >= 0, map.Remove(key, out int removed));
                    Assert.AreEqual(found >= 0 ? expected[found].Value : 0, removed);
                    if (found >= 0)
                    {
                        expected[found] = expected[^1];
                        expected.RemoveAt(expected.Count - 1);
                    }

                    break;
            }

            Assert.AreEqual(expected.Count, map.Count);
            Assert.AreSequenceEqual(expected, map.ToArray(), $"Operation {step}, capacity {capacity}, collisions {collide}.");
            for (int candidate = 0; candidate < 13; candidate++)
            {
                int expectedIndex = expected.FindIndex(pair => pair.Key == candidate);
                Assert.AreEqual(expectedIndex >= 0, map.TryGetValue(candidate, out int actual));
                Assert.AreEqual(expectedIndex >= 0 ? expected[expectedIndex].Value : 0, actual);
            }
        }
    }

    /// <summary>
    /// Duplicate, full and absent-key exceptions leave the dense buffer and index intact.
    /// </summary>
    [TestMethod]
    public void FixedMapRejectsInvalidStateAndBounds()
    {
        var entries = new PgFixedMapEntry<int, int>[2];
        int[] indices = new int[2];
        int count = 0;
        var map = new PgFixedMap<int, int>(entries, indices, ref count);
        map.Add(11, 13);
        map.Add(17, 19);
        Assert.AreEqual("key", Assert.ThrowsExactly<ArgumentException>(() =>
            new PgFixedMap<int, int>(entries, indices, ref count).Add(11, 99)).ParamName);
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgFixedMap<int, int>(entries, indices, ref count).Add(23, 99));
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgFixedMap<int, int>(entries, indices, ref count).Set(23, 99));
        Assert.ThrowsExactly<KeyNotFoundException>(() => new PgFixedMap<int, int>(entries, indices, ref count)[23]);
        Assert.AreSequenceEqual([new(11, 13), new(17, 19)], map.ToArray());
        Assert.AreEqual(2, count);
        Assert.AreEqual("indices", Assert.ThrowsExactly<ArgumentException>(() =>
            _ = new PgFixedMap<int, int>(entries, [], ref count)).ParamName);
        count = -1;
        Assert.AreEqual("count", Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
            _ = new PgFixedMap<int, int>(entries, indices, ref count)).ParamName);
        count = 3;
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = new PgFixedMap<int, int>(entries, indices, ref count));
        Assert.ThrowsExactly<InvalidOperationException>(() => default(PgFixedMap<int, int>).TryAdd(1, 2));
        count = 0;
        var empty = new PgFixedMap<int, int>([], [], ref count);
        Assert.IsTrue(empty.IsEmpty);
        Assert.IsTrue(empty.IsFull);
        Assert.IsFalse(empty.TryAdd(1, 2));
        Assert.IsFalse(empty.TrySet(1, 2, out int? previous));
        Assert.IsNull(previous);
        Assert.IsFalse(empty.TryGetValue(1, out int missing));
        Assert.AreEqual(0, missing);
        Assert.IsFalse(empty.Remove(1, out int removed));
        Assert.AreEqual(0, removed);
        empty.Clear();
        Assert.IsEmpty(empty.ToArray());
    }

    /// <summary>
    /// Corrupted indices are rejected before an insertion or removal can change other storage.
    /// </summary>
    [TestMethod]
    public void FixedMapRejectsInvalidIndicesBeforeMutation()
    {
        var entries = new PgFixedMapEntry<int, int>[4];
        int[] indices = new int[4];
        int count = 0;
        var comparer = new CollisionComparer();
        var map = new PgFixedMap<int, int>(entries, indices, ref count, comparer);
        map.Add(11, 13);
        map.Add(17, 19);
        indices[1] = 99;
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgFixedMap<int, int>(entries, indices, ref count, comparer).TryAdd(23, 29));
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgFixedMap<int, int>(entries, indices, ref count, comparer).Remove(11, out _));
        Assert.AreSequenceEqual([new(11, 13), new(17, 19)], map.ToArray());
        Assert.AreSequenceEqual([1, 99, 0, 0], indices);
        Assert.AreEqual(2, count);
        indices[1] = 2;
        indices[2] = -1;
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgFixedMap<int, int>(entries, indices, ref count, comparer).Remove(11, out _));
        Assert.AreSequenceEqual([new(11, 13), new(17, 19)], map.ToArray());
        Assert.AreEqual(2, count);
        indices[2] = 0;
        Assert.IsTrue(map.Remove(11, out int value));
        Assert.AreEqual(13, value);
        Assert.AreEqual(19, map[17]);
    }

    /// <summary>
    /// Exceptions from caller equality or hashing occur before a mutation and preserve a usable map.
    /// </summary>
    [TestMethod]
    public void FixedMapComparerFailuresPreserveStorage()
    {
        var entries = new PgFixedMapEntry<int, int>[2];
        int[] indices = new int[2];
        int count = 0;
        var comparer = new FailingComparer();
        var map = new PgFixedMap<int, int>(entries, indices, ref count, comparer);
        map.Add(1, 11);
        comparer.ThrowHash = true;
        Assert.AreSame(comparer.Failure, Assert.ThrowsExactly<InvalidOperationException>(() =>
            new PgFixedMap<int, int>(entries, indices, ref count, comparer).TrySet(2, 22, out _)));
        comparer.ThrowHash = false;
        comparer.ThrowEquality = true;
        Assert.AreSame(comparer.Failure, Assert.ThrowsExactly<InvalidOperationException>(() =>
            new PgFixedMap<int, int>(entries, indices, ref count, comparer).Remove(1, out _)));
        comparer.ThrowEquality = false;
        Assert.AreEqual(1, count);
        Assert.AreEqual(11, map[1]);
        Assert.IsFalse(map.TryGetValue(2, out _));
        map.Add(2, 22);
        Assert.IsTrue(map.Remove(1, out int value));
        Assert.AreEqual(11, value);
        Assert.AreEqual(22, map[2]);
    }

    /// <summary>
    /// Repeated default view construction and scalar updates allocate no managed storage after warmup.
    /// </summary>
    [TestMethod]
    public void FixedMapAttachmentAndUpdatesDoNotAllocate()
    {
        var entries = new PgFixedMapEntry<int, int>[2];
        int[] indices = new int[2];
        int count = 0;
        Assert.AreEqual(499500, AttachAndUpdate(entries, indices, ref count));
        long before = GC.GetAllocatedBytesForCurrentThread();
        int actual = AttachAndUpdate(entries, indices, ref count);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.AreEqual(499500, actual);
        Assert.AreEqual(0L, allocated);
        Assert.AreEqual(1, count);
        Assert.AreEqual(999, entries[0].Value);
    }

    /// <summary>
    /// Attaches and updates through the default comparer path without allocating test assertions in the measured region.
    /// </summary>
    private static int AttachAndUpdate(PgFixedMapEntry<int, int>[] entries, int[] indices, ref int count)
    {
        int total = 0;
        for (int value = 0; value < 1000; value++)
        {
            var map = new PgFixedMap<int, int>(entries, indices, ref count);
            map.Set(1, value);
            total += map[1];
        }

        return total;
    }

    /// <summary>
    /// Uses deliberate collisions with exact integer equality, including wrapped full tables.
    /// </summary>
    private sealed class CollisionComparer : EqualityComparer<int>
    {
        /// <inheritdoc/>
        public override bool Equals(int x, int y) => x == y;

        /// <inheritdoc/>
        public override int GetHashCode(int obj) => 0;
    }

    /// <summary>
    /// Treats a decade as one key while colliding every key at the last bucket.
    /// </summary>
    private sealed class DecadeComparer : EqualityComparer<int>
    {
        /// <inheritdoc/>
        public override bool Equals(int x, int y) => x / 10 == y / 10;

        /// <inheritdoc/>
        public override int GetHashCode(int obj) => 3;
    }

    /// <summary>
    /// Injects explicit callback failures; stable operation uses a constant hash and exact equality.
    /// </summary>
    private sealed class FailingComparer : EqualityComparer<int>
    {
        /// <summary>
        /// Gets the exact exception propagated by a rejected operation.
        /// </summary>
        internal InvalidOperationException Failure { get; } = new("Comparer failure.");

        /// <summary>
        /// Gets or sets whether hashing fails.
        /// </summary>
        internal bool ThrowHash
        {
            get;
            set;
        }

        /// <summary>
        /// Gets or sets whether equality fails.
        /// </summary>
        internal bool ThrowEquality
        {
            get;
            set;
        }

        /// <inheritdoc/>
        public override bool Equals(int x, int y) => ThrowEquality ? throw Failure : x == y;

        /// <inheritdoc/>
        public override int GetHashCode(int obj) => ThrowHash ? throw Failure : 0;
    }
}
