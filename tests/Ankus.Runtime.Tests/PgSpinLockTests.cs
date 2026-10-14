using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies stable spinlock storage, exact updates, ownership and guarded native protocol failures.
/// </summary>
[TestClass]
public sealed class PgSpinLockTests
{
    /// <summary>
    /// Initialization preserves opaque native bytes and all bits of the user's value.
    /// </summary>
    [TestMethod]
    public void SpinValuesInitializeExactStorage()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var original = new Pair(ulong.MaxValue, long.MinValue);
        var value = new PgSpinLockValue<Pair>(original);
        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<PgSpinLockValue<Pair>>(in value));
        Assert.AreEqual(0x17395B7D12345678L, MemoryMarshal.Read<long>(bytes));
        Assert.AreEqual(NativeSpinLockLease.Format | 1, MemoryMarshal.Read<long>(bytes[8..]));
        Assert.AreEqual(original, MemoryMarshal.Read<Pair>(bytes[16..]));
        Assert.HasCount(1, fixture.Memory.Requests);
        Assert.AreEqual(0, fixture.Memory.Requests[0]._flags);
        Assert.IsEmpty(fixture.Held);
    }

    /// <summary>
    /// Ten guarded updates match pgrx's spinlock witness and value access makes no backend requests.
    /// </summary>
    [TestMethod]
    public void SpinGuardsPreserveUpdatesAndOwnership()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var storage = new PgSpinLock<long>(0);
        for (long index = 0; index < 10; index++)
        {
            using PgSpinLockGuard<long> guard = storage.Lock();
            int requests = fixture.Memory.Requests.Count;
            Assert.AreEqual(index, guard.Value);
            guard.Value = index + 1;
            Assert.AreEqual(index + 1, guard.Value);
            Assert.HasCount(requests, fixture.Memory.Requests);
        }

        using (PgSpinLockGuard<long> final = storage.Lock())
        {
            Assert.AreEqual(10L, final.Value);
        }

        Assert.HasCount(11, fixture.Released);
        Assert.HasCount(23, fixture.Memory.Requests);
        Assert.IsEmpty(fixture.Held);
        Assert.IsNull(fixture.ReleaseFailure);
    }

    /// <summary>
    /// Local guards retain pinned storage across a compacting collection and the loss of the owner reference.
    /// </summary>
    [TestMethod]
    public void LocalSpinLocksRetainStableOwnedStorage()
    {
        using var fixture = new NativeSpinLockTestFixture();
        using PgSpinLockGuard<Pair> guard = AcquireLocal();
        nint address = Assert.ContainsSingle(fixture.Held);
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.AreEqual(new Pair(ulong.MaxValue, long.MinValue), guard.Value);
        var changed = new Pair(73, -79);
        guard.Value = changed;
        GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
        Assert.AreEqual(changed, guard.Value);
        Assert.AreEqual(address, Assert.ContainsSingle(fixture.Held));
        guard.Dispose();
        Assert.AreSequenceEqual([address], fixture.Released);
        Assert.IsNull(fixture.ReleaseFailure);

        [MethodImpl(MethodImplOptions.NoInlining)]
        static PgSpinLockGuard<Pair> AcquireLocal() => new PgSpinLock<Pair>(new Pair(ulong.MaxValue, long.MinValue)).Lock();
    }

    /// <summary>
    /// Aliases release exactly once; stale reads, writes and disposal cannot affect a newer guard.
    /// </summary>
    [TestMethod]
    public void SpinGuardsReleaseOnceAndRejectStaleAccess()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var storage = new PgSpinLock<int>(11);
        PgSpinLockGuard<int> first = storage.Lock();
        PgSpinLockGuard<int> alias = first;
        first.Value = 17;
        alias.Dispose();
        using PgSpinLockGuard<int> next = storage.Lock();
        first.Dispose();
        alias.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => first.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(() => alias.Value = 99);
        Assert.IsTrue(storage.IsLocked);
        Assert.AreEqual(17, next.Value);
        next.Value = 19;
        Assert.HasCount(1, fixture.Released);
        next.Dispose();
        Assert.IsFalse(storage.IsLocked);
        Assert.HasCount(2, fixture.Released);
        Assert.IsNull(fixture.ReleaseFailure);
    }

    /// <summary>
    /// Callback exit releases forgotten acquisitions in reverse order and preserves the enclosing guard.
    /// </summary>
    [TestMethod]
    public void SpinScopesReleaseForgottenGuards()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var first = new PgSpinLock<int>(11);
        var second = new PgSpinLock<int>(17);
        var third = new PgSpinLock<int>(23);
        using PgSpinLockGuard<int> outer = first.Lock();
        nint outerAddress = Assert.ContainsSingle(fixture.Held);
        PgSpinLockGuard<int> expired;
        nint secondAddress;
        nint thirdAddress;
        using (MemoryContextTestFixture.Enter())
        {
            expired = second.Lock();
            secondAddress = fixture.Memory.Requests[^1]._pointer;
            _ = third.Lock();
            thirdAddress = fixture.Memory.Requests[^1]._pointer;
            expired.Value = 29;
        }

        Assert.AreSequenceEqual([thirdAddress, secondAddress], fixture.Released);
        Assert.AreEqual(outerAddress, Assert.ContainsSingle(fixture.Held));
        Assert.AreEqual(11, outer.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(() => expired.Value);
        // codeql[cs/dispose-not-called-on-throw] The test disposes this at the step it checks; the fake native layer holds nothing an earlier failure could leak.
        expired.Dispose();
        outer.Dispose();
        Assert.AreSequenceEqual([thirdAddress, secondAddress, outerAddress], fixture.Released);
        using PgSpinLockGuard<int> recovered = second.Lock();
        Assert.AreEqual(29, recovered.Value);
        Assert.IsNull(fixture.ReleaseFailure);
    }

    /// <summary>
    /// Foreign capabilities, threads and recursive acquisition fail before further native calls.
    /// </summary>
    [TestMethod]
    public void SpinGuardsRejectForeignAndRecursiveAccess()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var storage = new PgSpinLock<int>(73);
        using PgSpinLockGuard<int> guard = storage.Lock();
        int requests = fixture.Memory.Requests.Count;
        Assert.ThrowsExactly<InvalidOperationException>(storage.Lock);
        foreach (nint provider in new nint[] { 0, 29 })
        {
            using MemoryContextTestFixture.Scope foreign = MemoryContextTestFixture.Enter(provider);
            Assert.ThrowsExactly<InvalidOperationException>(storage.Lock);
            Assert.ThrowsExactly<InvalidOperationException>(() => storage.IsLocked);
            Assert.ThrowsExactly<InvalidOperationException>(() => guard.Value);
            Assert.ThrowsExactly<InvalidOperationException>(() => guard.Value = 99);
            Assert.ThrowsExactly<InvalidOperationException>(guard.Dispose);
        }

        DedicatedThread.Run(() =>
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => storage.Lock());
            Assert.ThrowsExactly<InvalidOperationException>(() => guard.Value);
            Assert.ThrowsExactly<InvalidOperationException>(guard.Dispose);
        });

        Assert.HasCount(requests, fixture.Memory.Requests);
        Assert.AreEqual(73, guard.Value);
        Assert.IsEmpty(fixture.Released);
    }

    /// <summary>
    /// Backend, memory, logging and configuration calls cannot run inside the critical section.
    /// </summary>
    [TestMethod]
    public void SpinGuardsRejectBackendCalls()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var storage = new PgSpinLock<int>(11);
        using (PgSpinLockGuard<int> guard = storage.Lock())
        {
            int requests = fixture.Memory.Requests.Count;
            Assert.Contains("spinlock", Assert.ThrowsExactly<InvalidOperationException>(() => PgMemoryContext.Current).Message);
            Assert.Contains("spinlock", Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 1")).Message);
            Assert.Contains("spinlock", Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.Write(PgLogLevel.Info, "blocked")).Message);
            Assert.Contains("spinlock", Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadInt32("test.count")).Message);
            Assert.Contains("spinlock", Assert.ThrowsExactly<InvalidOperationException>(() => new PgSpinLock<int>(0)).Message);
            Assert.HasCount(requests, fixture.Memory.Requests);
            guard.Value = 17;
        }

        Assert.IsNotNull(PgMemoryContext.Current);
        using PgSpinLockGuard<int> recovered = storage.Lock();
        Assert.AreEqual(17, recovered.Value);
        Assert.IsNull(fixture.ReleaseFailure);
    }

    /// <summary>
    /// Managed PostgreSQL diagnostics preserve their identity while using unwinds the guard before reporting.
    /// </summary>
    [TestMethod]
    public void SpinManagedDiagnosticsUnwindAndRecover()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var storage = new PgSpinLock<int>(31);
        nint previous = NativeLog.Enter(1);
        try
        {
            PgException failure = Assert.ThrowsExactly<PgException>(() => EmitGuardedDiagnostic(storage));
            Assert.AreEqual("P7832", failure.SqlState);
            Assert.AreEqual("guarded failure", failure.Message);
            Assert.AreEqual("owned detail", failure.Detail);
        }
        finally
        {
            NativeLog.Exit(previous);
        }

        Assert.IsEmpty(fixture.Held);
        Assert.HasCount(1, fixture.Released);
        using PgSpinLockGuard<int> recovered = storage.Lock();
        Assert.AreEqual(37, recovered.Value);

        static void EmitGuardedDiagnostic(PgSpinLock<int> target)
        {
            using PgSpinLockGuard<int> guard = target.Lock();
            guard.Value = 37;
            PgLog.Write(PgLogLevel.Error, new PgDiagnostic("guarded failure") { SqlState = "P7832", Detail = "owned detail" });
        }
    }

    /// <summary>
    /// Range checks include the entire inline cell and its provider, with an exact-boundary positive control.
    /// </summary>
    [TestMethod]
    [DataRow(0, -1, true)]
    [DataRow(1, 0, true)]
    [DataRow(-1, 0, true)]
    [DataRow(0, 0, false)]
    public unsafe void SpinReadAdmissionRejectsIncompleteRanges(int offset, int sizeDelta, bool sameProvider)
    {
        using var fixture = new NativeSpinLockTestFixture();
        var value = new PgSpinLockValue<int>(73);
        nint address = (nint)(&value);
        NativeSharedReadScope frame = default;
        NativeSharedReadScope.Push(&frame, sameProvider ? 17 : 29, address + offset, (nuint)(sizeof(PgSpinLockValue<int>) + sizeDelta));
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => ((PgSpinLockValue<int>*)address)->Lock());
            Assert.HasCount(1, fixture.Memory.Requests);
            Assert.IsEmpty(fixture.Held);
        }
        finally
        {
            NativeSharedReadScope.Pop(&frame);
        }

        NativeSharedReadScope.Push(&frame, 17, address, (nuint)sizeof(PgSpinLockValue<int>));
        try
        {
            using PgSpinLockGuard<int> guard = value.Lock();
            Assert.AreEqual(73, guard.Value);
            guard.Value = 79;
            Assert.AreEqual(79, guard.Value);
        }
        finally
        {
            NativeSharedReadScope.Pop(&frame);
        }

        Assert.AreSequenceEqual([address], fixture.Released);
    }

    /// <summary>
    /// State queries reflect selected-header availability without preventing ordinary guarded access.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void SpinStateQueriesHonorSelectedMajor(bool supported)
    {
        using var fixture = new NativeSpinLockTestFixture { SupportsQuery = supported };
        var storage = new PgSpinLock<int>(73);
        if (supported)
        {
            Assert.IsFalse(storage.IsLocked);
        }
        else
        {
            Assert.ThrowsExactly<NotSupportedException>(() => storage.IsLocked);
        }

        using (PgSpinLockGuard<int> guard = storage.Lock())
        {
            Assert.AreEqual(73, guard.Value);
            if (supported)
            {
                Assert.IsTrue(storage.IsLocked);
            }
            else
            {
                Assert.ThrowsExactly<NotSupportedException>(() => storage.IsLocked);
            }
        }

        Assert.AreEqual(supported ? 2 : 0, fixture.Memory.Requests.Count(static request => request._flags == 2));
        Assert.HasCount(1, fixture.Released);
    }

    /// <summary>
    /// Failed preparation or acquisition retains exact initial data and permits a valid retry.
    /// </summary>
    [TestMethod]
    public void SpinAcquisitionFailurePreservesStorageAndScope()
    {
        using var fixture = new NativeSpinLockTestFixture();
        var storage = new PgSpinLock<long>(long.MinValue);
        fixture.MissingRelease = true;
        Assert.ThrowsExactly<InvalidOperationException>(storage.Lock);
        Assert.DoesNotContain(1, fixture.Memory.Requests.Select(static request => request._flags));
        fixture.MissingRelease = false;
        fixture.AcquireFailure = new PgException("55006", "Acquisition rejected.", "Owned detail.", "Retry safely.");
        PgException failure = Assert.ThrowsExactly<PgException>(storage.Lock);
        Assert.AreEqual("55006", failure.SqlState);
        Assert.AreEqual("Acquisition rejected.", failure.Message);
        Assert.AreEqual("Owned detail.", failure.Detail);
        Assert.AreEqual("Retry safely.", failure.Hint);
        Assert.IsGreaterThan(0, fixture.Memory.ErrorReleases);
        Assert.IsEmpty(fixture.Held);
        Assert.IsEmpty(fixture.Released);
        fixture.AcquireFailure = null;
        using PgSpinLockGuard<long> recovered = storage.Lock();
        Assert.AreEqual(long.MinValue, recovered.Value);
        recovered.Value = long.MaxValue;
        Assert.AreEqual(long.MaxValue, recovered.Value);
    }

    /// <summary>
    /// Default and detached inline values never acquire a lock outside an admitted native shared range.
    /// </summary>
    [TestMethod]
    public void SpinValuesRequireAdmittedStorage()
    {
        using var fixture = new NativeSpinLockTestFixture();
        Assert.ThrowsExactly<InvalidOperationException>(() => default(PgSpinLockValue<int>).Lock());
        Assert.ThrowsExactly<InvalidOperationException>(() => default(PgSpinLockValue<int>).IsLocked);
        Assert.IsEmpty(fixture.Memory.Requests);
        var value = new PgSpinLockValue<int>(73);
        Assert.ThrowsExactly<InvalidOperationException>(value.Lock);
        Assert.ThrowsExactly<InvalidOperationException>(() => value.IsLocked);
        Assert.HasCount(1, fixture.Memory.Requests);
        Assert.IsEmpty(fixture.Held);
    }

    /// <summary>
    /// Carries exact wide values independently of the spinlock metadata.
    /// </summary>
    private readonly record struct Pair(ulong First, long Second);
}
