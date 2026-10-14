using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

public sealed partial class PgSharedMemoryTests
{
    /// <summary>
    /// Releasing a lease removes the callback's managed root before the callback itself exits.
    /// </summary>
    [TestMethod]
    public void ReleasedSharedLeasesAreCollectibleInsideTheOwningCallback()
    {
        using var fixture = new SharedFixture();
        NativeBorrowScope scope = NativeMemoryContext.BorrowScope;
        WeakReference released = ReleaseLease(scope);
        GarbageCollection.Collect();
        Assert.IsFalse(released.IsAlive, "A long-running callback must not retain every disposed lock acquisition.");
        scope.Validate();
        Assert.AreSequenceEqual<nint>([20], fixture.Memory.Requests.Select(static request => request._other));

        [MethodImpl(MethodImplOptions.NoInlining)]
        static WeakReference ReleaseLease(NativeBorrowScope owner)
        {
            using var lease = new NativeSharedMemoryLease(owner, 71);
            owner.Register(lease);
            lease.Acquired(20);
            var weak = new WeakReference(lease);
            lease.Dispose();
            return weak;
        }
    }

    /// <summary>
    /// Shared and exclusive guards transport distinct modes, exact aggregate copies and one release per acquisition.
    /// </summary>
    [TestMethod]
    public void SharedLockGuardsCopyExactValuesAndReleaseOnce()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<SharedPair>("record");
        PgSharedMemory.Initialize(storage);
        SharedPair original = new(ulong.MaxValue, long.MinValue);
        fixture.Bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<SharedPair>(in original)).ToArray();
        using (PgLwLockShareGuard<SharedPair> reader = storage.Share())
        {
            Assert.AreEqual(original, reader.Value);
            reader.Dispose();
            reader.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => reader.Value);
        }

        SharedPair changed = new(17, -912345678901234);
        using (PgLwLockExclusiveGuard<SharedPair> writer = storage.Exclusive())
        {
            Assert.AreEqual(original, writer.Value);
            writer.Value = changed;
            Assert.AreEqual(changed, writer.Value);
            writer.Dispose();
            Assert.ThrowsExactly<ObjectDisposedException>(() => writer.Value = original);
        }

        Assert.AreEqual(changed, MemoryMarshal.Read<SharedPair>(fixture.Bytes));
        Assert.AreSequenceEqual([0, 1, 3, 5, 2, 3, 4, 3, 5], fixture.Memory.Requests.Select(static request => request._flags));
        Assert.AreSequenceEqual<nint>([20, 21], fixture.Memory.Requests.Where(static request => request._flags == 5).Select(static request => request._other));
        foreach (NativeMemoryRequest request in fixture.Memory.Requests.Where(static request => request._flags is 3 or 4))
        {
            Assert.AreEqual((nuint)16, request._length);
            Assert.AreEqual(71, request._context);
        }
    }

    /// <summary>
    /// Callback exit releases forgotten guards in reverse order and nested scopes do not consume an outer guard.
    /// </summary>
    [TestMethod]
    public void SharedLockGuardsExpireWithTheirOwningCallback()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<int>("scope");
        PgSharedMemory.Initialize(storage);
        fixture.Bytes = BitConverter.GetBytes(731);
        using PgLwLockShareGuard<int> outer = storage.Share();
        using PgLwLockExclusiveGuard<int> expired = AcquireInCallback();
        Assert.AreSequenceEqual<nint>([22, 21], fixture.Memory.Requests.Where(static request => request._flags == 5).Select(static request => request._other));
        Assert.AreEqual(731, outer.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(() => expired.Value);
        expired.Dispose();
        outer.Dispose();
        Assert.AreSequenceEqual<nint>([22, 21, 20], fixture.Memory.Requests.Where(static request => request._flags == 5).Select(static request => request._other));

        PgLwLockExclusiveGuard<int> AcquireInCallback()
        {
            using (MemoryContextTestFixture.Enter())
            {
                PgLwLockExclusiveGuard<int> guard = storage.Exclusive();
                _ = storage.Share();
                Assert.AreEqual(731, outer.Value);
                Assert.AreEqual(731, guard.Value);
                return guard;
            }
        }
    }

    /// <summary>
    /// Foreign providers, masked callbacks and other managed threads cannot access or release a live guard.
    /// </summary>
    [TestMethod]
    public void SharedLockGuardsRejectForeignCapabilitiesBeforeNativeCalls()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<int>("owned");
        PgSharedMemory.Initialize(storage);
        fixture.Bytes = BitConverter.GetBytes(37);
        using PgLwLockExclusiveGuard<int> guard = storage.Exclusive();
        foreach (nint provider in new nint[] { 0, 29 })
        {
            using MemoryContextTestFixture.Scope foreign = MemoryContextTestFixture.Enter(provider);
            Assert.ThrowsExactly<InvalidOperationException>(() => guard.Value);
            Assert.ThrowsExactly<InvalidOperationException>(() => guard.Value = 19);
            Assert.ThrowsExactly<InvalidOperationException>(guard.Dispose);
            Assert.ThrowsExactly<InvalidOperationException>(storage.Share);
            Assert.ThrowsExactly<InvalidOperationException>(() => PgSharedMemory.Initialize(storage));
        }

        DedicatedThread.Run(() =>
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => guard.Value);
            Assert.ThrowsExactly<InvalidOperationException>(guard.Dispose);
        });

        Assert.HasCount(2, fixture.Memory.Requests);
        Assert.AreEqual(37, guard.Value);
        guard.Value = 42;
        Assert.AreEqual(42, guard.Value);
    }

    /// <summary>
    /// A native failure before acquisition does not publish a guard, while failed reads transport diagnostics without changing data.
    /// </summary>
    [TestMethod]
    public void SharedLockNativeFailuresRetainOwnershipAndRecover()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<int>("errors");
        PgSharedMemory.Initialize(storage);
        fixture.Bytes = BitConverter.GetBytes(-731);
        Func<NativeMemoryRequest, NativeMemoryResult> respond = fixture.Memory.Handler!;
        int rejected = 0;
        fixture.Memory.Handler = request =>
        {
            if (request._flags == 2 && rejected++ == 0)
            {
                throw new PgException("55006", "Already held.");
            }

            return respond(request);
        };
        PgException acquisition = Assert.ThrowsExactly<PgException>(storage.Exclusive);
        Assert.AreEqual("55006", acquisition.SqlState);
        Assert.AreEqual("Already held.", acquisition.Message);
        using PgLwLockExclusiveGuard<int> guard = storage.Exclusive();
        int reads = 0;
        fixture.Memory.Handler = request =>
        {
            if (request._flags == 3 && reads++ == 0)
            {
                throw new PgException("P7805", "Read rejected.", "Owned read detail.", "Retry the read.");
            }

            return respond(request);
        };
        PgException reading = Assert.ThrowsExactly<PgException>(() => guard.Value);
        Assert.AreEqual("P7805", reading.SqlState);
        Assert.AreEqual("Read rejected.", reading.Message);
        Assert.AreEqual("Owned read detail.", reading.Detail);
        Assert.AreEqual("Retry the read.", reading.Hint);
        Assert.AreEqual(-731, guard.Value);
        guard.Value = 42;
        Assert.AreEqual(42, guard.Value);
        guard.Dispose();
        Assert.AreSequenceEqual<nint>([20], fixture.Memory.Requests.Where(static request => request._flags == 5).Select(static request => request._other));
        Assert.IsGreaterThan(0, fixture.Memory.ErrorReleases);
    }
}
