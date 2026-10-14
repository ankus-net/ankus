namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies allocation ownership queries, managed bounds, resizing, and retryable disposal.
/// </summary>
[TestClass]
public sealed class PgAllocationTests
{
    /// <summary>
    /// Allocation ownership is resolved through the native registry and cannot survive a stale allocation identity.
    /// </summary>
    [TestMethod]
    public void ContextUsesNativeOwnerAndRejectsStaleIdentity()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(8);
        fixture.Requests.Clear();
        Assert.AreEqual(303, allocation.Context.Id);
        NativeMemoryRequest request = Assert.ContainsSingle(fixture.Requests);
        Assert.AreEqual(NativeMemoryOperation.Owner, request._operation);
        Assert.AreEqual(501, request._context);
        fixture.Handler = nativeRequest => nativeRequest._operation == NativeMemoryOperation.Owner
            ? throw new PgException("55000", "allocation expired")
            : fixture.Respond(nativeRequest);
        ObjectDisposedException exception = Assert.ThrowsExactly<ObjectDisposedException>(() => allocation.Context);
        Assert.AreEqual(nameof(PgAllocation), exception.ObjectName);
        Assert.AreEqual(1, fixture.ErrorReleases);
    }

    /// <summary>
    /// Ranges immediately beyond the end or extending past it fail before entering native code.
    /// </summary>
    /// <param name="offset">The requested byte offset.</param>
    /// <param name="length">The requested byte count.</param>
    [TestMethod]
    [DataRow(5, 0)]
    [DataRow(4, 1)]
    [DataRow(3, 2)]
    [DataRow(0, 5)]
    public void InvalidRangesAreRejectedBeforeNativeCalls(int offset, int length)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(4);
        byte[] buffer = new byte[length];
        fixture.Requests.Clear();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Read(buffer, (nuint)offset));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Write(buffer, (nuint)offset));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Clear((nuint)offset, (nuint)length));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Native-size overflow candidates cannot wrap managed range validation into a permitted access.
    /// </summary>
    [TestMethod]
    public void NativeSizeOverflowRangesAreRejectedBeforeNativeCalls()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(4);
        fixture.Requests.Clear();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Read([], nuint.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Write([], nuint.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Clear(nuint.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Clear(1, nuint.MaxValue));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Empty access at the allocation end remains valid, including for a zero-length allocation.
    /// </summary>
    /// <param name="length">The allocation's byte length.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(4)]
    public void EmptyRangesAtAllocationEndRemainValid(int length)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate((nuint)length);
        fixture.Requests.Clear();
        allocation.Read([], (nuint)length);
        allocation.Write([], (nuint)length);
        allocation.Clear((nuint)length);
        Assert.AreSequenceEqual([NativeMemoryOperation.Read, NativeMemoryOperation.Write, NativeMemoryOperation.Clear],
            fixture.Requests.Select(static request => request._operation));
        foreach (NativeMemoryRequest request in fixture.Requests)
        {
            Assert.AreEqual(length, request._value);
            Assert.AreEqual((nuint)0, request._length);
        }
    }

    /// <summary>
    /// Managed buffers and unmanaged values preserve the requested offsets and copied bytes.
    /// </summary>
    [TestMethod]
    public unsafe void ReadWriteAndClearPreserveBytesAndOffsets()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(8);
        byte[] storage = [10, 20, 30, 40, 50, 60, 70, 80];
        fixture.Handler = request =>
        {
            Span<byte> range = storage.AsSpan((int)request._value, (int)request._length);
            switch (request._operation)
            {
                case NativeMemoryOperation.Read:
                    range.CopyTo(new Span<byte>((void*)request._data, (int)request._length));
                    break;
                case NativeMemoryOperation.Write:
                    new ReadOnlySpan<byte>((void*)request._data, (int)request._length).CopyTo(range);
                    break;
                case NativeMemoryOperation.Clear:
                    range.Clear();
                    break;
            }

            return fixture.Respond(request);
        };
        byte[] destination = new byte[3];
        allocation.Read(destination, 2);
        Assert.AreSequenceEqual<byte>([30, 40, 50], destination);
        allocation.Write([91, 92], 6);
        Assert.AreSequenceEqual<byte>([10, 20, 30, 40, 50, 60, 91, 92], storage);
        allocation.Write(0x10203040, 1);
        Assert.AreSequenceEqual(BitConverter.GetBytes(0x10203040), storage[1..5]);
        Assert.AreEqual(0x10203040, allocation.Read<int>(1));
        Assert.AreEqual((byte)10, storage[0]);
        Assert.AreEqual((byte)60, storage[5]);
        allocation.Clear(2, 3);
        Assert.AreSequenceEqual<byte>([0, 0, 0], storage[2..5]);
        Assert.AreEqual((byte)60, storage[5]);
        allocation.Clear(5);
        Assert.AreSequenceEqual<byte>([0, 0, 0], storage[5..8]);
        fixture.Handler = null;
    }

    /// <summary>
    /// Typed reads and writes honor the full unmanaged value width at the end boundary.
    /// </summary>
    [TestMethod]
    public void TypedAccessRejectsPartiallyAvailableValues()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(4);
        fixture.Requests.Clear();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Read<int>(1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Write(17, 1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Read<long>());
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Write(17L));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Failed resize preserves the old identity and length; a successful response replaces both.
    /// </summary>
    [TestMethod]
    public void ReallocateFailurePreservesIdentityAndLengthUntilSuccess()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(8);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Reallocate
            ? throw new PgException("53200", "resize failed")
            : fixture.Respond(request);
        PgException exception = Assert.ThrowsExactly<PgException>(() => allocation.Reallocate(16));
        Assert.AreEqual("53200", exception.SqlState);
        Assert.AreEqual((nuint)8, allocation.Length);
        Assert.AreEqual(303, allocation.Context.Id);
        Assert.AreEqual(501, fixture.Requests[^1]._context);
        fixture.Handler = null;
        allocation.Reallocate(16);
        Assert.AreEqual((nuint)16, allocation.Length);
        Assert.AreEqual(303, allocation.Context.Id);
        Assert.AreEqual(502, fixture.Requests[^1]._context);
    }

    /// <summary>
    /// Failed native free preserves the handle so disposal can be retried safely.
    /// </summary>
    [TestMethod]
    public void DisposeFailureRetainsAllocationForRetry()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(8);
        int frees = 0;
        fixture.Handler = request =>
        {
            if (request._operation == NativeMemoryOperation.Free && ++frees == 1)
            {
                throw new PgException("XX000", "free failed");
            }

            return fixture.Respond(request);
        };
        PgException exception = Assert.ThrowsExactly<PgException>(allocation.Dispose);
        Assert.AreEqual("XX000", exception.SqlState);
        Assert.AreEqual((nuint)8, allocation.Length);
        Assert.AreEqual(303, allocation.Context.Id);
        allocation.Dispose();
        allocation.Dispose();
        Assert.AreEqual(2, frees);
        Assert.AreEqual((nuint)0, allocation.Length);
        Assert.ThrowsExactly<ObjectDisposedException>(() => allocation.Context);
    }

    /// <summary>
    /// Explicitly disposed allocations reject every access without issuing native requests.
    /// </summary>
    [TestMethod]
    public unsafe void DisposedAllocationRejectsAccessAndRepeatedDisposeIsLocal()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(8);
        allocation.Dispose();
        fixture.Requests.Clear();
        allocation.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => allocation.Context);
        Assert.ThrowsExactly<ObjectDisposedException>(() => allocation.Read([]));
        Assert.ThrowsExactly<ObjectDisposedException>(() => allocation.Write([]));
        Assert.ThrowsExactly<ObjectDisposedException>(() => allocation.Clear());
        Assert.ThrowsExactly<ObjectDisposedException>(() => allocation.Reallocate(4));
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
        {
            allocation.DangerousGetPointer();
        });
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Stale allocations are distinguished from unrelated native read failures.
    /// </summary>
    [TestMethod]
    public void ReadDistinguishesStaleIdentityFromOperationalFailure()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(8);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Read
            ? throw new PgException("55000", "expired")
            : fixture.Respond(request);
        ObjectDisposedException stale = Assert.ThrowsExactly<ObjectDisposedException>(() => allocation.Read<int>());
        Assert.AreEqual(nameof(PgAllocation), stale.ObjectName);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Read
            ? throw new PgException("22023", "invalid native request")
            : fixture.Respond(request);
        PgException operational = Assert.ThrowsExactly<PgException>(() => allocation.Read<int>());
        Assert.AreEqual("22023", operational.SqlState);
        Assert.AreEqual("invalid native request", operational.Message);
        Assert.AreEqual(2, fixture.ErrorReleases);
    }
}
