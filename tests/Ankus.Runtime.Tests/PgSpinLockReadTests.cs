using System.Runtime.CompilerServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies scoped original-storage access, nested spinlock ownership and parent lifetime protection.
/// </summary>
[TestClass]
public sealed class PgSpinLockReadTests
{
    /// <summary>
    /// A guard reader admits precisely its protected value and withdraws that address before returning.
    /// </summary>
    [TestMethod]
    public unsafe void SpinReadsBoundTheirOriginalAddressAdmission()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var owner = new PgSpinLock<Nested>(new Nested(5));
        using PgSpinLockGuard<Nested> parent = owner.Lock();
        nint saved = 0;
        Assert.AreEqual(5L, parent.Read((in value) =>
        {
            nint start = (nint)Unsafe.AsPointer(ref Unsafe.AsRef(in value));
            saved = start;
            nint frame = NativeSharedReadScope.Find(start, (nuint)sizeof(Nested));
            Assert.AreNotEqual(0, frame);
            Assert.AreEqual(frame, NativeSharedReadScope.Find(start + sizeof(Nested) - 1, 1));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeSharedReadScope.Find(start - 1, (nuint)sizeof(Nested)));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeSharedReadScope.Find(start + 1, (nuint)sizeof(Nested)));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeSharedReadScope.Find(start, (nuint)sizeof(Nested) + 1));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeSharedReadScope.Find(start + sizeof(Nested), 1));
            return value._marker;
        }));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeSharedReadScope.Find(saved, 1));
        Assert.HasCount(1, fixture.Held);
        Assert.IsEmpty(fixture.Released);
    }

    /// <summary>
    /// Interior atomic updates target original storage while a detached aggregate remains independent.
    /// </summary>
    [TestMethod]
    public void SpinReadsPreserveOriginalStorage()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var owner = new PgSpinLock<Nested>(new Nested(long.MinValue));
        using PgSpinLockGuard<Nested> guard = owner.Lock();
        int requests = fixture.Memory.Requests.Count;
        Assert.AreEqual((long.MinValue, 11L, long.MaxValue), guard.Read(static (in value) =>
        {
            Nested copy = value;
            Assert.AreEqual(11L, copy._atomic.Exchange(99));
            Assert.AreEqual(11L, value._atomic.Value);
            Assert.ThrowsExactly<InvalidOperationException>(() => copy._inner.Lock());
            long original = value._atomic.Exchange(long.MaxValue);
            return (value._marker, original, value._atomic.Value);
        }));
        Assert.AreEqual(long.MaxValue, guard.Value._atomic.Value);
        Assert.HasCount(requests, fixture.Memory.Requests);
        Assert.HasCount(1, fixture.Held);
    }

    /// <summary>
    /// Child acquisitions use original storage and forgotten guards expire before a reader returns.
    /// </summary>
    [TestMethod]
    public void SpinReadsComposeNestedLocks()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var owner = new PgSpinLock<Nested>(new Nested(7));
        using PgSpinLockGuard<Nested> parent = owner.Lock();
        nint parentAddress = Assert.ContainsSingle(fixture.Held);
        PgSpinLockGuard<long> expired = parent.Read(static (in value) =>
        {
            PgSpinLockGuard<long> child = value._inner.Lock();
            Assert.AreEqual(31L, child.Value);
            child.Value = 47;
            return child;
        });
        Assert.ThrowsExactly<ObjectDisposedException>(() => expired.Value);
        expired.Dispose();
        Assert.HasCount(1, fixture.Released);
        Assert.AreEqual(parentAddress, Assert.ContainsSingle(fixture.Held));
        Assert.AreEqual(47L, parent.Read(static (in value) =>
        {
            using PgSpinLockGuard<long> child = value._inner.Lock();
            return child.Value;
        }));
        parent.Dispose();
        Assert.AreEqual(parentAddress, fixture.Released[^1]);
        Assert.HasCount(3, fixture.Released);
        Assert.IsEmpty(fixture.Held);
    }

    /// <summary>
    /// Aliases cannot replace or release a value until the outermost reader returns.
    /// </summary>
    [TestMethod]
    public void SpinReadsPreventReplacementAndRelease()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var owner = new PgSpinLock<int>(17);
        using PgSpinLockGuard<int> parent = owner.Lock();
        PgSpinLockGuard<int> alias = parent;
        Assert.AreEqual(17, parent.Read((in value) =>
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => alias.Value = 99);
            Assert.ThrowsExactly<InvalidOperationException>(alias.Dispose);
            Assert.AreEqual(17, parent.Read((in nested) =>
            {
                Assert.ThrowsExactly<InvalidOperationException>(alias.Dispose);
                Assert.ThrowsExactly<InvalidOperationException>(() => alias.Value = 101);
                return nested;
            }));
            Assert.ThrowsExactly<InvalidOperationException>(alias.Dispose);
            Assert.ThrowsExactly<InvalidOperationException>(() => alias.Value = 103);
            return value;
        }));
        Assert.HasCount(1, fixture.Held);
        Assert.IsEmpty(fixture.Released);
        alias.Value = 23;
        Assert.AreEqual(23, parent.Read(static (in value) => value));
        alias.Dispose();
        Assert.HasCount(1, fixture.Released);
        Assert.IsEmpty(fixture.Held);
    }

    /// <summary>
    /// Exceptional readers release their children, retain their parent and permit a later acquisition.
    /// </summary>
    [TestMethod]
    public void SpinReadsUnwindAndRecover()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var owner = new PgSpinLock<Nested>(new Nested(13));
        using PgSpinLockGuard<Nested> parent = owner.Lock();
        nint parentAddress = Assert.ContainsSingle(fixture.Held);
        var expected = new InvalidOperationException("reader failed");
        PgSpinLockGuard<long>? escaped = null;
        InvalidOperationException failure = Assert.ThrowsExactly<InvalidOperationException>(() => parent.Read<int>((in value) =>
        {
            escaped = value._inner.Lock();
            escaped.Value = 59;
            throw expected;
        }));
        Assert.AreSame(expected, failure);
        Assert.IsNotNull(escaped);
        Assert.ThrowsExactly<ObjectDisposedException>(() => escaped.Value);
        Assert.AreEqual(parentAddress, Assert.ContainsSingle(fixture.Held));
        Assert.AreEqual(59L, parent.Read(static (in value) =>
        {
            using PgSpinLockGuard<long> child = value._inner.Lock();
            return child.Value;
        }));
        parent.Dispose();
        Assert.AreEqual(parentAddress, fixture.Released[^1]);
        Assert.HasCount(3, fixture.Released);
        Assert.IsEmpty(fixture.Held);
    }

    /// <summary>
    /// A scoped reader preserves the parent's backend-call prohibition and managed diagnostic cleanup.
    /// </summary>
    [TestMethod]
    public void SpinReadsPreserveCriticalSectionBoundaries()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var owner = new PgSpinLock<Nested>(new Nested(19));
        using PgSpinLockGuard<Nested> parent = owner.Lock();
        nint previous = NativeLog.Enter(1);
        try
        {
            PgException error = Assert.ThrowsExactly<PgException>(() => parent.Read<int>(static (in value) =>
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 42"));
                _ = value._inner.Lock();
                PgLog.Write(PgLogLevel.Error, new PgDiagnostic("spin reader diagnostic")
                {
                    SqlState = "P7841",
                    Detail = "owned detail",
                    Hint = "release safely",
                });
                return 0;
            }));
            Assert.AreEqual("P7841", error.SqlState);
            Assert.AreEqual("spin reader diagnostic", error.Message);
            Assert.AreEqual("owned detail", error.Detail);
            Assert.AreEqual("release safely", error.Hint);
            Assert.HasCount(1, fixture.Held);
            Assert.HasCount(1, fixture.Released);
            Assert.AreEqual(19L, parent.Read(static (in value) => value._marker));
        }
        finally
        {
            NativeLog.Exit(previous);
        }

        parent.Dispose();
        Assert.IsEmpty(fixture.Held);
        using PgSpinLockGuard<Nested> recovered = owner.Lock();
        Assert.AreEqual(31L, recovered.Read(static (in value) =>
        {
            using PgSpinLockGuard<long> child = value._inner.Lock();
            return child.Value;
        }));
    }

    /// <summary>
    /// Both the parent and a nested child retain their original address through compacting collections.
    /// </summary>
    [TestMethod]
    public void SpinReadsRetainPinnedOwners()
    {
        using var fixture = new NativeSpinLockTestFixture();
        using PgSpinLockGuard<Nested> parent = new PgSpinLock<Nested>(new Nested(long.MaxValue)).Lock();
        nint parentAddress = Assert.ContainsSingle(fixture.Held);
        Assert.AreEqual((long.MaxValue, 67L), parent.Read(static (in value) =>
        {
            using PgSpinLockGuard<long> child = value._inner.Lock();
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Assert.AreEqual(31L, child.Value);
            child.Value = 67;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            return (value._marker, child.Value);
        }));
        Assert.AreEqual(parentAddress, Assert.ContainsSingle(fixture.Held));
        Assert.AreEqual(67L, parent.Read(static (in value) =>
        {
            using PgSpinLockGuard<long> child = value._inner.Lock();
            return child.Value;
        }));
    }

    /// <summary>
    /// Null, expired and foreign-provider reads fail before a supplied callback runs.
    /// </summary>
    [TestMethod]
    public void SpinReadsValidateCallbacksAndOwnership()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var owner = new PgSpinLock<int>(71);
        using PgSpinLockGuard<int> parent = owner.Lock();
        ArgumentNullException missing = Assert.ThrowsExactly<ArgumentNullException>(() => parent.Read<int>(null!));
        Assert.AreEqual("reader", missing.ParamName);
        bool invoked = false;
        int reader(in int value)
        {
            invoked = true;
            return value;
        }

        foreach (nint provider in new nint[] { 0, 29 })
        {
            using MemoryContextTestFixture.Scope foreign = MemoryContextTestFixture.Enter(provider);
            Assert.ThrowsExactly<InvalidOperationException>(() => parent.Read(reader));
        }

        DedicatedThread.Run(() => Assert.ThrowsExactly<InvalidOperationException>(() => parent.Read(reader)));

        parent.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => parent.Read(reader));
        Assert.IsFalse(invoked);
        Assert.HasCount(1, fixture.Released);
        Assert.IsEmpty(fixture.Held);
    }

    /// <summary>
    /// Combines an ordinary field with two independently observable interior-mutable fields.
    /// </summary>
    private readonly struct Nested(long marker)
    {
        /// <summary>
        /// Stores an exact ordinary value.
        /// </summary>
        internal readonly long _marker = marker;

        /// <summary>
        /// Stores the original inline atomic scalar.
        /// </summary>
        internal readonly PgAtomicValue<long> _atomic = new(11);

        /// <summary>
        /// Stores a separately acquired inline spinlock and value.
        /// </summary>
        internal readonly PgSpinLockValue<long> _inner = new(31);
    }
}
