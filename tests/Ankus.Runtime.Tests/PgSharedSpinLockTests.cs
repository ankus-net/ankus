using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

public sealed unsafe partial class PgSharedTests
{
    /// <summary>
    /// Nested original-storage readers release children and parent before shared admission retires.
    /// </summary>
    [TestMethod]
    public void SpinGuardReadsReleaseBeforeSharedAdmission()
    {
        using var spins = new NativeSpinLockTestFixture();
        using var fixture = new SharedFixture();
        Func<NativeMemoryRequest, NativeMemoryResult> shared = fixture.Memory.Handler!;
        fixture.Memory.Handler = request => request._operation == NativeMemoryOperation.SpinLock ? spins.Respond(request) : shared(request);
        PgShared<PgSpinLockValue<SpinState>> storage = fixture.Start(new PgSpinLockValue<SpinState>(new SpinState(73)));
        var admissions = new List<int>();
        spins.BeforeRelease = () => admissions.Add(fixture.Access->_readers);
        PgSpinLockGuard<int>? staleChild = null;
        PgSpinLockGuard<SpinState> staleParent = storage.Read((in value) =>
        {
            PgSpinLockGuard<SpinState> parent = value.Lock();
            Assert.AreEqual(79, parent.Read((in state) =>
            {
                staleChild = state._counter.Lock();
                Assert.AreEqual(73, staleChild.Value);
                staleChild.Value = 79;
                return staleChild.Value;
            }));
            Assert.IsNotNull(staleChild);
            Assert.ThrowsExactly<ObjectDisposedException>(() => staleChild.Value);
            Assert.HasCount(1, spins.Held);
            Assert.AreEqual(1, fixture.Access->_readers);
            return parent;
        });
        Assert.AreSequenceEqual([1, 1], admissions);
        Assert.AreEqual(0, fixture.Access->_readers);
        Assert.IsEmpty(spins.Held);
        Assert.ThrowsExactly<ObjectDisposedException>(() => staleParent.Read(static (in value) => value._other.IsLocked));
        Assert.AreEqual(79, storage.Read(static (in value) =>
        {
            using PgSpinLockGuard<SpinState> parent = value.Lock();
            return parent.Read(static (in state) =>
            {
                using PgSpinLockGuard<int> child = state._counter.Lock();
                return child.Value;
            });
        }));
        fixture.UseReplacement(new PgSpinLockValue<SpinState>(new SpinState(101)));
        // codeql[cs/dispose-not-called-on-throw] The test disposes this at the step it checks; the fake native layer holds nothing an earlier failure could leak.
        staleParent.Dispose();
        Assert.IsNotNull(staleChild);
        staleChild.Dispose();
        Assert.AreEqual(101, storage.Read(static (in value) =>
        {
            using PgSpinLockGuard<SpinState> parent = value.Lock();
            return parent.Read(static (in state) =>
            {
                using PgSpinLockGuard<int> child = state._counter.Lock();
                return child.Value;
            });
        }));
        Assert.AreEqual(0, fixture.Access->_readers);
        Assert.IsEmpty(spins.Held);
        fixture.AssertGuards(sizeof(PgSpinLockValue<SpinState>));
        Assert.IsNull(spins.ReleaseFailure);
    }

    /// <summary>
    /// Native admission does not make zeroed spinlock fields initialized or detached copies safe to acquire.
    /// </summary>
    [TestMethod]
    public void SpinSharedValuesRequireInitializationAndOriginalStorage()
    {
        using var spins = new NativeSpinLockTestFixture();
        using var fixture = new SharedFixture();
        Func<NativeMemoryRequest, NativeMemoryResult> shared = fixture.Memory.Handler!;
        fixture.Memory.Handler = request => request._operation == NativeMemoryOperation.SpinLock ? spins.Respond(request) : shared(request);
        PgShared<PgSpinLockValue<int>> storage = fixture.Start(default(PgSpinLockValue<int>));
        Assert.ThrowsExactly<InvalidOperationException>(() => storage.Read(static (in value) => value.Lock()));
        Assert.ThrowsExactly<InvalidOperationException>(() => storage.Read(static (in value) => value.IsLocked));
        Assert.HasCount(1, fixture.Memory.Requests);
        fixture.UseReplacement(new PgSpinLockValue<int>(73));
        Assert.AreEqual(73, storage.Read(static (in value) =>
        {
            PgSpinLockValue<int> copy = value;
            Assert.ThrowsExactly<InvalidOperationException>(() => copy.Lock());
            Assert.ThrowsExactly<InvalidOperationException>(() => copy.IsLocked);
            using PgSpinLockGuard<int> guard = value.Lock();
            return guard.Value;
        }));
        Assert.AreEqual(0, fixture.Access->_readers);
        Assert.IsEmpty(spins.Held);
        Assert.HasCount(1, spins.Released);
    }

    /// <summary>
    /// Nested and forgotten inline guards release before their exact shared read admissions end.
    /// </summary>
    [TestMethod]
    public void SpinSharedReadsReleaseBeforeAdmission()
    {
        using var spins = new NativeSpinLockTestFixture();
        using var fixture = new SharedFixture();
        Func<NativeMemoryRequest, NativeMemoryResult> shared = fixture.Memory.Handler!;
        fixture.Memory.Handler = request => request._operation == NativeMemoryOperation.SpinLock ? spins.Respond(request) : shared(request);
        PgShared<SpinState> storage = fixture.Start(new SpinState(11));
        var admissions = new List<int>();
        spins.BeforeRelease = () => admissions.Add(fixture.Access->_readers);
        using (PgSpinLockGuard<int> expired = storage.Read((in state) =>
        {
            PgSpinLockGuard<int> outer = state._counter.Lock();
            outer.Value = 17;
            PgSpinLockGuard<int> inner = storage.Read(static (in nested) =>
            {
                PgSpinLockGuard<int> guard = nested._other.Lock();
                guard.Value = 119;
                return guard;
            });
            Assert.ThrowsExactly<ObjectDisposedException>(() => inner.Value);
            Assert.AreEqual(1, fixture.Access->_readers);
            Assert.AreEqual(17, outer.Value);
            return outer;
        }))
        {
            Assert.AreSequenceEqual([2, 1], admissions);
            Assert.AreEqual(0, fixture.Access->_readers);
            Assert.IsEmpty(spins.Held);
            Assert.ThrowsExactly<ObjectDisposedException>(() => expired.Value);
        }

        Assert.HasCount(2, spins.Released);
        Assert.AreEqual((17, 119), storage.Read(static (in state) =>
        {
            using PgSpinLockGuard<int> first = state._counter.Lock();
            using PgSpinLockGuard<int> second = state._other.Lock();
            return (first.Value, second.Value);
        }));
        Assert.AreEqual(0, fixture.Access->_readers);
        fixture.AssertGuards(sizeof(SpinState));
        Assert.IsNull(spins.ReleaseFailure);
    }

    /// <summary>
    /// Throwing reads release guards before admission, and replacement storage cannot be changed by old guards.
    /// </summary>
    [TestMethod]
    public void SpinSharedReadsRecoverFromErrorsAndReplacement()
    {
        using var spins = new NativeSpinLockTestFixture();
        using var fixture = new SharedFixture();
        Func<NativeMemoryRequest, NativeMemoryResult> shared = fixture.Memory.Handler!;
        fixture.Memory.Handler = request => request._operation == NativeMemoryOperation.SpinLock ? spins.Respond(request) : shared(request);
        PgShared<SpinState> storage = fixture.Start(new SpinState(23));
        var failure = new InvalidOperationException("reader failed");
        PgSpinLockGuard<int>? stale = null;
        Assert.AreSame(failure, Assert.ThrowsExactly<InvalidOperationException>(() => storage.Read<int>((in state) =>
        {
            stale = state._counter.Lock();
            stale.Value = 29;
            throw failure;
        })));
        Assert.IsNotNull(stale);
        Assert.AreEqual(0, fixture.Access->_readers);
        Assert.IsEmpty(spins.Held);
        Assert.AreEqual(29, storage.Read(static (in state) =>
        {
            using PgSpinLockGuard<int> guard = state._counter.Lock();
            return guard.Value;
        }));
        fixture.UseReplacement(new SpinState(43));
        Assert.AreEqual(43, storage.Read((in state) =>
        {
            using PgSpinLockGuard<int> current = state._counter.Lock();
            stale.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => stale.Value = 99);
            Assert.IsTrue(state._counter.IsLocked);
            return current.Value;
        }));
        Assert.AreEqual(0, fixture.Access->_readers);
        Assert.IsEmpty(spins.Held);
        Assert.IsNull(spins.ReleaseFailure);
    }

    /// <summary>
    /// Inline values copied away from their admitted storage and unaligned packed fields cannot acquire.
    /// </summary>
    [TestMethod]
    public void SpinValuesRejectInvalidStorage()
    {
        using var spins = new NativeSpinLockTestFixture();
        using var fixture = new SharedFixture();
        Func<NativeMemoryRequest, NativeMemoryResult> shared = fixture.Memory.Handler!;
        fixture.Memory.Handler = request => request._operation == NativeMemoryOperation.SpinLock ? spins.Respond(request) : shared(request);
        PgShared<PackedSpinState> storage = fixture.Start(new PackedSpinState(new PgSpinLockValue<int>(73)));
        int requests = fixture.Memory.Requests.Count;
        Assert.ThrowsExactly<InvalidOperationException>(() => storage.Read(static (in state) => state._counter.Lock()));
        Assert.ThrowsExactly<InvalidOperationException>(() => storage.Read(static (in state) => state._counter.IsLocked));
        Assert.HasCount(requests, fixture.Memory.Requests);
        Assert.AreEqual((byte)0x5A, storage.Read(static (in state) => state._tag));
        Assert.AreEqual(0, fixture.Access->_readers);
        Assert.IsEmpty(spins.Held);
        fixture.AssertGuards(sizeof(PackedSpinState));
    }

    /// <summary>
    /// Embeds two independent spinlock cells without managed references.
    /// </summary>
    private readonly struct SpinState(int value)
    {
        public readonly PgSpinLockValue<int> _counter = new(value);
        public readonly PgSpinLockValue<int> _other = new(value + 100);
    }

    /// <summary>
    /// Deliberately misaligns an initialized cell through an ordinary unmanaged copy.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly struct PackedSpinState(PgSpinLockValue<int> value)
    {
        public readonly byte _tag = 0x5A;
        public readonly PgSpinLockValue<int> _counter = value;
    }
}
