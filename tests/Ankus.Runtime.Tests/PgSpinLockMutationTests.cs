using System.Runtime.CompilerServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies exclusive original-storage updates, conflicting borrows and pinned lock ownership.
/// </summary>
[TestClass]
public sealed class PgSpinLockMutationTests
{
    /// <summary>
    /// Mutations affect original fields and atomics without modifying detached snapshots or moving pinned storage.
    /// </summary>
    [TestMethod]
    public void SpinMutationsPreserveOriginalStorage()
    {
        using var fixture = new NativeSpinLockTestFixture();
        using PgSpinLockGuard<MutableLockValue> guard = new PgSpinLock<MutableLockValue>(new MutableLockValue(long.MinValue)).Lock();
        MutableLockValue copy = guard.Value;
        nint held = Assert.ContainsSingle(fixture.Held);
        int requests = fixture.Memory.Requests.Count;
        Assert.AreEqual((long.MaxValue, 11L), guard.Mutate((ref value) =>
        {
            value._marker = long.MaxValue;
            value._bits = ulong.MaxValue;
            Assert.AreEqual(3L, value._atomic.Exchange(11));
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            Assert.AreEqual(long.MaxValue, guard.Value._marker);
            GuardMutationAssertions.ChildAccessIsRejected(in value._child);
            return (value._marker, value._atomic.Value);
        }));
        Assert.AreEqual(long.MinValue, copy._marker);
        Assert.AreEqual(3L, copy._atomic.Value);
        Assert.AreEqual(ulong.MaxValue, guard.Value._bits);
        Assert.AreEqual(7L, guard.Read(static (in value) =>
        {
            using PgSpinLockGuard<long> child = value._child.Lock();
            return child.Value;
        }));
        Assert.AreEqual(held, Assert.ContainsSingle(fixture.Held));
        Assert.HasCount(requests + 2, fixture.Memory.Requests);
        guard.Dispose();
        Assert.AreEqual(held, fixture.Released[^1]);
        Assert.IsEmpty(fixture.Held);
    }

    /// <summary>
    /// Aliases cannot create conflicting borrows, replace storage or release the parent during a mutation.
    /// </summary>
    [TestMethod]
    public void SpinMutationsProtectParentOwnership()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var owner = new PgSpinLock<int>(17);
        using PgSpinLockGuard<int> guard = owner.Lock();
        PgSpinLockGuard<int> alias = guard;
        Assert.AreEqual(19, guard.Mutate((ref value) =>
        {
            value = 19;
            Assert.AreEqual(19, alias.Value);
            Assert.ThrowsExactly<InvalidOperationException>(alias.Dispose);
            Assert.ThrowsExactly<InvalidOperationException>(() => alias.Value = 99);
            Assert.ThrowsExactly<InvalidOperationException>(() => alias.Read(static (in nested) => nested));
            Assert.ThrowsExactly<InvalidOperationException>(() => alias.Mutate(static (ref nested) => ++nested));
            return value;
        }));
        Assert.AreEqual(19, guard.Read((in value) =>
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => alias.Mutate(static (ref nested) => ++nested));
            return value;
        }));
        Assert.AreEqual(23, alias.Mutate(static (ref value) => value = 23));
        alias.Value = 29;
        Assert.AreEqual(29, guard.Value);
        Assert.IsEmpty(fixture.Released);
        alias.Dispose();
        Assert.HasCount(1, fixture.Released);
    }

    /// <summary>
    /// A throwing mutator preserves its exact exception, immediate writes and still-held parent.
    /// </summary>
    [TestMethod]
    public void SpinMutationsUnwindAndRecover()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var owner = new PgSpinLock<MutableLockValue>(new MutableLockValue(31));
        using PgSpinLockGuard<MutableLockValue> guard = owner.Lock();
        var expected = new FormatException("mutation failed");
        FormatException error = Assert.ThrowsExactly<FormatException>(() => guard.Mutate<int>((ref value) =>
        {
            value._marker = 37;
            value._atomic.Exchange(41);
            throw expected;
        }));
        Assert.AreSame(expected, error);
        Assert.AreEqual((37L, 41L), (guard.Value._marker, guard.Value._atomic.Value));
        Assert.HasCount(1, fixture.Held);
        Assert.IsEmpty(fixture.Released);
        Assert.AreEqual(43L, guard.Read(static (in value) =>
        {
            using PgSpinLockGuard<long> child = value._child.Lock();
            child.Value = 43;
            return child.Value;
        }));
        Assert.AreEqual(47L, guard.Mutate(static (ref value) => value._marker = 47));
        guard.Dispose();
        using PgSpinLockGuard<MutableLockValue> recovered = owner.Lock();
        Assert.AreEqual(47L, recovered.Value._marker);
    }

    /// <summary>
    /// Invalid callback ownership fails before mutation, without disturbing the original guard.
    /// </summary>
    [TestMethod]
    public void SpinMutationsValidateOwnership()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var owner = new PgSpinLock<long>(53);
        using PgSpinLockGuard<long> guard = owner.Lock();
        Assert.AreEqual("mutator", Assert.ThrowsExactly<ArgumentNullException>(() => guard.Mutate<long>(null!)).ParamName);
        bool invoked = false;
        long mutator(ref long value)
        {
            invoked = true;
            return ++value;
        }

        foreach (nint provider in new nint[] { 0, 29 })
        {
            using MemoryContextTestFixture.Scope foreign = MemoryContextTestFixture.Enter(provider);
            Assert.ThrowsExactly<InvalidOperationException>(() => guard.Mutate(mutator));
        }

        GuardMutationAssertions.ForeignThreadIsRejected(() => guard.Mutate(mutator));
        Assert.AreEqual(53L, guard.Value);
        guard.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => guard.Mutate(mutator));
        Assert.IsFalse(invoked);
        Assert.HasCount(1, fixture.Released);
    }

    /// <summary>
    /// Terminal diagnostics preserve mutations while forbidden backend calls never enter native code.
    /// </summary>
    [TestMethod]
    public void SpinMutationsPreserveBackendBoundaries()
    {
        using var fixture = new NativeSpinLockTestFixture();
        using PgSpinLockGuard<int> guard = new PgSpinLock<int>(59).Lock();
        int requests = fixture.Memory.Requests.Count;
        nint previous = NativeLog.Enter(1);
        try
        {
            PgException error = Assert.ThrowsExactly<PgException>(() => guard.Mutate<int>(static (ref value) =>
            {
                value = 61;
                Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 1/0"));
                Assert.ThrowsExactly<InvalidOperationException>(() => PgMemoryContext.Current);
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadInt32("example.count"));
                Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.Write(PgLogLevel.Notice, "blocked"));
                PgLog.Write(PgLogLevel.Error, new PgDiagnostic("spin mutation error")
                {
                    SqlState = "P7843",
                    Detail = "owned detail",
                    Hint = "after unwind",
                });
                return 0;
            }));
            Assert.AreEqual("P7843", error.SqlState);
            Assert.AreEqual("spin mutation error", error.Message);
            Assert.AreEqual("owned detail", error.Detail);
            Assert.AreEqual("after unwind", error.Hint);
        }
        finally
        {
            NativeLog.Exit(previous);
        }

        Assert.AreEqual(61, guard.Value);
        Assert.HasCount(requests, fixture.Memory.Requests);
        guard.Dispose();
        NativeBorrowScope.CheckBackendAccess();
    }

    /// <summary>
    /// A mutation blocks every overlapping cell even when a newer readonly frame also admits it.
    /// </summary>
    /// <param name="offset">The candidate offset in the surrounding readonly range.</param>
    /// <param name="length">The complete candidate length.</param>
    /// <param name="blocked">Whether the candidate overlaps the mutable region.</param>
    [TestMethod]
    [DataRow(31, 1, false)]
    [DataRow(31, 2, true)]
    [DataRow(32, 1, true)]
    [DataRow(47, 1, true)]
    [DataRow(48, 1, false)]
    [DataRow(16, 64, true)]
    [DataRow(0, 32, false)]
    [DataRow(48, 80, false)]
    public unsafe void MutationsExcludeOverlappingSpinlockAdmissions(int offset, int length, bool blocked)
    {
        using var fixture = new NativeSpinLockTestFixture();
        byte* bytes = stackalloc byte[128];
        nint address = (nint)bytes + offset;
        NativeSharedReadScope outer = default;
        NativeSharedReadScope mutation = default;
        NativeSharedReadScope inner = default;
        NativeSharedReadScope.Push(&outer, NativeMemoryContext.Provider, (nint)bytes, 128);
        try
        {
            NativeSharedReadScope.PushMutation(&mutation, NativeMemoryContext.Provider, (nint)(bytes + 32), 16);
            try
            {
                NativeSharedReadScope.Push(&inner, NativeMemoryContext.Provider, (nint)bytes, 128);
                try
                {
                    if (blocked)
                    {
                        Assert.ThrowsExactly<InvalidOperationException>(() => NativeSharedReadScope.Find(address, (nuint)length));
                    }
                    else
                    {
                        Assert.AreEqual((nint)(&inner), NativeSharedReadScope.Find(address, (nuint)length));
                    }
                }
                finally
                {
                    NativeSharedReadScope.Pop(&inner);
                }
            }
            finally
            {
                NativeSharedReadScope.Pop(&mutation);
            }

            Assert.AreEqual((nint)(&outer), NativeSharedReadScope.Find(address, (nuint)length));
        }
        finally
        {
            NativeSharedReadScope.Pop(&outer);
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => NativeSharedReadScope.Find(address, (nuint)length));
    }
}

/// <summary>
/// Combines mutable ordinary fields with original interior synchronization.
/// </summary>
internal struct MutableLockValue(long marker)
{
    /// <summary>
    /// Stores an ordinary signed field.
    /// </summary>
    internal long _marker = marker;

    /// <summary>
    /// Stores an independent unsigned field.
    /// </summary>
    internal ulong _bits;

    /// <summary>
    /// Stores an independently mutable atomic scalar.
    /// </summary>
    internal readonly PgAtomicValue<long> _atomic = new(3);

    /// <summary>
    /// Stores an independently acquired child lock.
    /// </summary>
    internal readonly PgSpinLockValue<long> _child = new(7);
}

/// <summary>
/// Asserts original-cell and thread boundaries without copying a synchronization-bearing struct.
/// </summary>
internal static class GuardMutationAssertions
{
    /// <summary>
    /// Tests the original cell's lock and query operations while its owner keeps its address stable.
    /// </summary>
    internal static unsafe void ChildAccessIsRejected(scoped in PgSpinLockValue<long> cell)
    {
        nint address = (nint)Unsafe.AsPointer(ref Unsafe.AsRef(in cell));
        Assert.ThrowsExactly<InvalidOperationException>(() => Unsafe.AsRef<PgSpinLockValue<long>>((void*)address).Lock());
        Assert.ThrowsExactly<InvalidOperationException>(() => Unsafe.AsRef<PgSpinLockValue<long>>((void*)address).IsLocked);
    }

    /// <summary>
    /// Observes thread-affinity rejection and rethrows any assertion failure on the test thread.
    /// </summary>
    internal static void ForeignThreadIsRejected(Action action)
    {
        DedicatedThread.Run(() => Assert.ThrowsExactly<InvalidOperationException>(action));
    }
}
