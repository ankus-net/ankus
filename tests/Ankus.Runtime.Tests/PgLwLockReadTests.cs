using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

public sealed unsafe partial class PgSharedMemoryTests
{
    /// <summary>
    /// Both guard modes expose original atomic and spinlock fields while copies remain independent.
    /// </summary>
    /// <param name="exclusive">Whether to acquire the exclusive guard.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void LwLockReadsPreserveOriginalStorage(bool exclusive)
    {
        using var spins = new NativeSpinLockTestFixture();
        using var fixture = new SharedFixture();
        Func<NativeMemoryRequest, NativeMemoryResult> shared = fixture.Memory.Handler!;
        fixture.Memory.Handler = request => request._operation == NativeMemoryOperation.SpinLock ? spins.Respond(request) : shared(request);
        var storage = new PgLwLock<ReadState>("nested");
        PgSharedMemory.Initialize(storage);
        ReadState original = new(long.MinValue);
        fixture.Bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<ReadState>(in original)).ToArray();
        using PgLwLockShareGuard<ReadState>? reader = exclusive ? null : storage.Share();
        using PgLwLockExclusiveGuard<ReadState>? writer = exclusive ? storage.Exclusive() : null;
        PgSpinLockGuard<long>? expired = null;
        (long, long, long) callback(in ReadState value)
        {
            ReadState copy = value;
            Assert.AreEqual(3L, copy._atomic.Exchange(101));
            Assert.AreEqual(3L, value._atomic.Exchange(long.MaxValue));
            Assert.ThrowsExactly<InvalidOperationException>(copy._child.Lock);
            expired = value._child.Lock();
            Assert.AreEqual(7L, expired.Value);
            expired.Value = 29;
            GC.Collect(2, GCCollectionMode.Forced, blocking: true, compacting: true);
            return (value._marker, value._atomic.Value, expired.Value);
        }

        Assert.AreEqual((long.MinValue, long.MaxValue, 29L), exclusive ? writer!.Read(callback) : reader!.Read(callback));
        Assert.IsNotNull(expired);
        Assert.ThrowsExactly<ObjectDisposedException>(() => expired.Value);
        Assert.HasCount(1, spins.Released);
        Assert.IsEmpty(spins.Held);
        long verify(in ReadState value)
        {
            using PgSpinLockGuard<long> child = value._child.Lock();
            return child.Value;
        }

        Assert.AreEqual(29L, exclusive ? writer!.Read(verify) : reader!.Read(verify));
        Assert.AreEqual(long.MaxValue, MemoryMarshal.Read<ReadState>(fixture.Bytes)._atomic.Value);
        NativeMemoryRequest[] admissions = [.. fixture.Memory.Requests.Where(static request => request._flags == 6)];
        Assert.HasCount(2, admissions);
        Assert.IsTrue(admissions.All(static request => request._context == 71 && request._other == 20 && request._length == (nuint)sizeof(ReadState)));
    }

    /// <summary>
    /// Parent aliases cannot replace or release original bytes while any nested reader is active.
    /// </summary>
    [TestMethod]
    public void LwLockReadsProtectParentOwnership()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<long>("parent");
        PgSharedMemory.Initialize(storage);
        fixture.Bytes = BitConverter.GetBytes(31L);
        using PgLwLockExclusiveGuard<long> guard = storage.Exclusive();
        PgLwLockExclusiveGuard<long> alias = guard;
        nint saved = 0;
        Assert.AreEqual(31L, guard.Read((in value) =>
        {
            nint address = (nint)Unsafe.AsPointer(ref Unsafe.AsRef(in value));
            saved = address;
            nint frame = NativeSharedReadScope.Find(address, sizeof(long));
            Assert.AreEqual(frame, NativeSharedReadScope.Find(address + 7, 1));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeSharedReadScope.Find(address - 1, 8));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeSharedReadScope.Find(address + 1, 8));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeSharedReadScope.Find(address, 9));
            Assert.ThrowsExactly<InvalidOperationException>(() => NativeSharedReadScope.Find(address + 8, 1));
            Assert.AreEqual(31L, alias.Value);
            Assert.ThrowsExactly<InvalidOperationException>(alias.Dispose);
            Assert.ThrowsExactly<InvalidOperationException>(() => alias.Value = 99);
            Assert.AreEqual(31L, alias.Read((in nested) =>
            {
                Assert.ThrowsExactly<InvalidOperationException>(guard.Dispose);
                Assert.ThrowsExactly<InvalidOperationException>(() => guard.Value = 101);
                return nested;
            }));
            Assert.ThrowsExactly<InvalidOperationException>(alias.Dispose);
            Assert.ThrowsExactly<InvalidOperationException>(() => alias.Value = 103);
            return value;
        }));
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeSharedReadScope.Find(saved, 1));
        Assert.AreSequenceEqual([0, 2, 6], fixture.Memory.Requests.Select(static request => request._flags));
        guard.Value = 37;
        Assert.AreEqual(37L, guard.Value);
        guard.Dispose();
        Assert.HasCount(1, fixture.Memory.Requests.Where(static request => request._flags == 5));
        Assert.ThrowsExactly<ObjectDisposedException>(() => alias.Read(static (in value) => value));
    }

    /// <summary>
    /// Already-held guards validate independently and a rejected inner admission preserves an outer reader.
    /// </summary>
    [TestMethod]
    public void LwLockReadsComposeAlreadyHeldGuards()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<long>("first");
        PgSharedMemory.Initialize(storage);
        fixture.Bytes = BitConverter.GetBytes(41L);
        using PgLwLockShareGuard<long> first = storage.Share();
        var lease = new NativeSharedMemoryLease(NativeMemoryContext.BorrowScope, 73);
        NativeMemoryContext.BorrowScope.Register(lease);
        lease.Acquired(27);
        using var second = new PgLwLockShareGuard<long>(lease);
        long[] other = GC.AllocateArray<long>(1, pinned: true);
        other[0] = 43;
        bool valid = true;
        Func<NativeMemoryRequest, NativeMemoryResult> respond = fixture.Memory.Handler!;
        fixture.Memory.Handler = request =>
        {
            if (request._flags == 6 && request._context == 73)
            {
                Assert.AreEqual(27, request._other);
                if (!valid)
                {
                    throw new PgException("55000", "guard expired");
                }

                return new NativeMemoryResult { _data = (nint)Unsafe.AsPointer(ref other[0]), _length = sizeof(long) };
            }

            return respond(request);
        };
        Assert.AreEqual((41L, 43L), first.Read((in value) =>
        {
            Assert.ThrowsExactly<InvalidOperationException>(first.Dispose);
            Assert.AreEqual(43L, second.Read((in nested) =>
            {
                Assert.ThrowsExactly<InvalidOperationException>(second.Dispose);
                return nested;
            }));
            valid = false;
            PgException error = Assert.ThrowsExactly<PgException>(() => second.Read(static (in nested) => nested));
            Assert.AreEqual("55000", error.SqlState);
            Assert.AreEqual("guard expired", error.Message);
            Assert.ThrowsExactly<InvalidOperationException>(NativeBorrowScope.CheckBackendAccess);
            valid = true;
            return (value, second.Read(static (in nested) => nested));
        }));
        NativeBorrowScope.CheckBackendAccess();
        Assert.AreEqual(41L, first.Value);
        GC.KeepAlive(other);
    }

    /// <summary>
    /// Exceptions preserve identity and updates, release child locks and withdraw the original admission.
    /// </summary>
    [TestMethod]
    public void LwLockReadsUnwindAndRecover()
    {
        using var spins = new NativeSpinLockTestFixture();
        using var fixture = new SharedFixture();
        Func<NativeMemoryRequest, NativeMemoryResult> shared = fixture.Memory.Handler!;
        fixture.Memory.Handler = request => request._operation == NativeMemoryOperation.SpinLock ? spins.Respond(request) : shared(request);
        var storage = new PgLwLock<ReadState>("exception");
        PgSharedMemory.Initialize(storage);
        ReadState original = new(47);
        fixture.Bytes = MemoryMarshal.AsBytes(new ReadOnlySpan<ReadState>(in original)).ToArray();
        using PgLwLockExclusiveGuard<ReadState> guard = storage.Exclusive();
        PgSpinLockGuard<long>? expired = null;
        nint saved = 0;
        var expected = new FormatException("reader failed");
        FormatException failure = Assert.ThrowsExactly<FormatException>(() => guard.Read<int>((in value) =>
        {
            saved = (nint)Unsafe.AsPointer(ref Unsafe.AsRef(in value));
            expired = value._child.Lock();
            expired.Value = 53;
            throw expected;
        }));
        Assert.AreSame(expected, failure);
        Assert.IsNotNull(expired);
        Assert.ThrowsExactly<ObjectDisposedException>(() => expired.Value);
        Assert.ThrowsExactly<InvalidOperationException>(() => NativeSharedReadScope.Find(saved, 1));
        Assert.IsEmpty(spins.Held);
        Assert.IsEmpty(fixture.Memory.Requests.Where(static request => request._operation == NativeMemoryOperation.SharedMemory && request._flags == 5));
        Assert.AreEqual(53L, guard.Read(static (in value) =>
        {
            using PgSpinLockGuard<long> child = value._child.Lock();
            return child.Value;
        }));
        NativeBorrowScope.CheckBackendAccess();
        guard.Value = original;
        Assert.AreEqual(47L, guard.Value._marker);
        guard.Dispose();
        Assert.HasCount(1, fixture.Memory.Requests.Where(static request => request._operation == NativeMemoryOperation.SharedMemory && request._flags == 5));
    }

    /// <summary>
    /// Scoped readers block backend calls, preserve terminal diagnostics and restore backend access on exit.
    /// </summary>
    [TestMethod]
    public void LwLockReadsPreserveBackendBoundaries()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<long>("boundaries");
        PgSharedMemory.Initialize(storage);
        fixture.Bytes = BitConverter.GetBytes(59L);
        using PgLwLockShareGuard<long> guard = storage.Share();
        nint previous = NativeLog.Enter(1);
        try
        {
            PgException error = Assert.ThrowsExactly<PgException>(() => guard.Read<int>(static (in value) =>
            {
                Assert.AreEqual(59L, value);
                Assert.ThrowsExactly<InvalidOperationException>(() => Spi.Execute("SELECT 42"));
                Assert.ThrowsExactly<InvalidOperationException>(() => PgMemoryContext.Current);
                Assert.ThrowsExactly<InvalidOperationException>(() => NativeGuc.ReadInt32("example.count"));
                Assert.ThrowsExactly<InvalidOperationException>(() => PgLog.Write(PgLogLevel.Notice, "blocked"));
                PgLog.Write(PgLogLevel.Error, new PgDiagnostic("scoped lock error")
                {
                    SqlState = "P7842",
                    Detail = "owned detail",
                    Hint = "retry after unwinding",
                });
                return 0;
            }));
            Assert.AreEqual("P7842", error.SqlState);
            Assert.AreEqual("scoped lock error", error.Message);
            Assert.AreEqual("owned detail", error.Detail);
            Assert.AreEqual("retry after unwinding", error.Hint);
        }
        finally
        {
            NativeLog.Exit(previous);
        }

        Assert.AreSequenceEqual([0, 1, 6], fixture.Memory.Requests.Select(static request => request._flags));
        Assert.AreEqual(59L, guard.Value);
        NativeBorrowScope.CheckBackendAccess();
        NativeMemoryRequest request = new() { _operation = NativeMemoryOperation.Current };
        fixture.Memory.Handler = fixture.Memory.Respond;
        NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
        Assert.AreEqual(101, result._context);
    }

    /// <summary>
    /// Invalid native address contracts are rejected before invoking a reader or publishing a backend restriction.
    /// </summary>
    /// <param name="scenario">The missing, misaligned, undersized, oversized or failed native response.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(4)]
    public void LwLockReadsValidateAdmission(int scenario)
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<long>("admission");
        PgSharedMemory.Initialize(storage);
        fixture.Bytes = BitConverter.GetBytes(61L);
        using PgLwLockShareGuard<long> guard = storage.Share();
        Func<NativeMemoryRequest, NativeMemoryResult> respond = fixture.Memory.Handler!;
        fixture.Memory.Handler = request =>
        {
            NativeMemoryResult result = respond(request);
            if (request._flags == 6)
            {
                switch (scenario)
                {
                    case 0:
                        result._data = 0;
                        break;
                    case 1:
                        result._data++;
                        break;
                    case 2:
                        result._length--;
                        break;
                    case 3:
                        result._length++;
                        break;
                    default:
                        throw new PgException("55000", "no longer held");
                }
            }

            return result;
        };
        bool invoked = false;
        long callback(in long value)
        {
            invoked = true;
            return value;
        }

        if (scenario == 4)
        {
            PgException error = Assert.ThrowsExactly<PgException>(() => guard.Read(callback));
            Assert.AreEqual("55000", error.SqlState);
            Assert.AreEqual("no longer held", error.Message);
        }
        else
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => guard.Read(callback));
        }

        Assert.IsFalse(invoked);
        NativeBorrowScope.CheckBackendAccess();
        fixture.Memory.Handler = respond;
        Assert.AreEqual(61L, guard.Read(callback));
        Assert.IsTrue(invoked);
    }

    /// <summary>
    /// Invalid ownership and null callbacks fail before native admission or callback execution.
    /// </summary>
    [TestMethod]
    public void LwLockReadsValidateOwnership()
    {
        using var fixture = new SharedFixture();
        var storage = new PgLwLock<long>("ownership");
        PgSharedMemory.Initialize(storage);
        fixture.Bytes = BitConverter.GetBytes(67L);
        using PgLwLockShareGuard<long> guard = storage.Share();
        Assert.AreEqual("reader", Assert.ThrowsExactly<ArgumentNullException>(() => guard.Read<long>(null!)).ParamName);
        bool invoked = false;
        long callback(in long value)
        {
            invoked = true;
            return value;
        }

        foreach (nint provider in new nint[] { 0, 29 })
        {
            using MemoryContextTestFixture.Scope foreign = MemoryContextTestFixture.Enter(provider);
            Assert.ThrowsExactly<InvalidOperationException>(() => guard.Read(callback));
        }

        DedicatedThread.Run(() => Assert.ThrowsExactly<InvalidOperationException>(() => guard.Read(callback)));

        guard.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => guard.Read(callback));
        Assert.IsFalse(invoked);
        Assert.AreSequenceEqual([0, 1, 5], fixture.Memory.Requests.Select(static request => request._flags));
    }

    /// <summary>
    /// Combines ordinary storage with independent interior-mutable fields.
    /// </summary>
    private readonly struct ReadState(long marker)
    {
        /// <summary>
        /// Stores an exact ordinary value.
        /// </summary>
        internal readonly long _marker = marker;

        /// <summary>
        /// Stores an inline atomic value.
        /// </summary>
        internal readonly PgAtomicValue<long> _atomic = new(3);

        /// <summary>
        /// Stores an inline child spinlock.
        /// </summary>
        internal readonly PgSpinLockValue<long> _child = new(7);
    }
}
