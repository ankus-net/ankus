namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies native item-pointer ownership and boundary requests without modeling PostgreSQL's allocator.
/// </summary>
[TestClass]
public sealed unsafe class NativeItemPointerTests
{
    /// <summary>
    /// Creates exact fields and shares an allocation identity with borrows until the owner releases it.
    /// </summary>
    [TestMethod]
    public void OwnedValuesAndBorrowsShareIdentity()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => Respond(fixture, request);
        using PgNativeItemPointer owner = PgNativeItemPointer.Create(new(0x12345678, 0xabcd));
        NativeMemoryRequest create = fixture.Requests.Single(request => Is(request, 1));
        Assert.AreEqual(101, create._context);
        Assert.AreEqual(0x12345678, create._value);
        Assert.AreEqual((nuint)0xabcd, create._length);
        using PgNativeItemPointer borrowed = owner.Borrow();
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, ushort.MaxValue), borrowed.Value);
        Assert.AreEqual(101, borrowed.LifetimeContext.Id);
        Assert.AreEqual(801, (nint)owner.DangerousGetPointer());
        borrowed.Value = PgItemPointer.Invalid;
        NativeMemoryRequest write = fixture.Requests[^1];
        Assert.IsTrue(Is(write, 3));
        Assert.AreEqual(701, write._context);
        Assert.AreEqual(4294967295L, write._value);
        Assert.AreEqual((nuint)0, write._length);
        Assert.AreEqual(0, write._pointer);
        using PgNativeItemPointer second = borrowed.Borrow();
        borrowed.Dispose();
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, ushort.MaxValue), second.Value);
        Assert.IsEmpty(fixture.Requests.Where(request => request._operation == NativeMemoryOperation.Free));
        owner.Dispose();
        owner.Dispose();
        Assert.HasCount(1, fixture.Requests.Where(request => request._operation == NativeMemoryOperation.Free));
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = owner.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(() => second.Value = default);
        Assert.ThrowsExactly<ObjectDisposedException>(second.Borrow);
        Assert.ThrowsExactly<ObjectDisposedException>(() => second.DangerousGetPointer());
    }

    /// <summary>
    /// Clone copies fields into the destination; borrowed transfer never consumes the owner's release rights.
    /// </summary>
    [TestMethod]
    public void CloneAndTransferPreserveOwnership()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int creates = 0;
        fixture.Handler = request => Is(request, 1)
            ? new NativeMemoryResult { _pointer = 701 + creates++, _length = 6 }
            : Respond(fixture, request);
        using PgNativeItemPointer owner = PgNativeItemPointer.Create(new(7, 3));
        using PgMemoryContext destination = PgMemoryContext.Create("copy");
        using PgNativeItemPointer clone = owner.CloneInto(destination);
        NativeMemoryRequest copy = fixture.Requests.Last(request => Is(request, 1));
        Assert.AreEqual(202, copy._context);
        Assert.AreEqual(4294967295L, copy._value);
        Assert.AreEqual((nuint)65535, copy._length);
        using PgNativeItemPointer borrowed = owner.Borrow();
        Assert.AreEqual(801, (nint)borrowed.DangerousDetach());
        Assert.IsEmpty(fixture.Requests.Where(request => request._operation == NativeMemoryOperation.Detach));
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = borrowed.Value);
        using PgNativeItemPointer retained = owner.Borrow();
        fixture.Handler = request => request._operation == NativeMemoryOperation.Detach
            ? throw new PgException("53200", "transfer refused") : Respond(fixture, request);
        PgException error = Assert.ThrowsExactly<PgException>(() => owner.DangerousDetach());
        Assert.AreEqual("53200", error.SqlState);
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, ushort.MaxValue), retained.Value);
        fixture.Handler = request => Respond(fixture, request);
        Assert.AreEqual(801, (nint)owner.DangerousDetach());
        Assert.AreEqual(NativeMemoryOperation.Detach, fixture.Requests[^1]._operation);
        Assert.AreEqual(701, fixture.Requests[^1]._context);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = retained.Value);
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, ushort.MaxValue), clone.Value);
        owner.Dispose();
        Assert.IsEmpty(fixture.Requests.Where(request => request._operation == NativeMemoryOperation.Free));
        clone.Dispose();
        Assert.AreEqual(702, fixture.Requests[^1]._context);
        Assert.AreEqual(NativeMemoryOperation.Free, fixture.Requests[^1]._operation);
    }

    /// <summary>
    /// External addresses carry a context generation, without native ownership or chunk-header assumptions.
    /// </summary>
    [TestMethod]
    public void ExternalBorrowPreservesGeneration()
    {
        Assert.IsNull(PgNativeItemPointer.DangerousBorrow(null, null!));
        Assert.ThrowsExactly<ArgumentNullException>(() => PgNativeItemPointer.DangerousBorrow((void*)801, null!));
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => Respond(fixture, request);
        using PgNativeItemPointer? borrowed = PgNativeItemPointer.DangerousBorrow((void*)801, PgMemoryContext.Current);
        Assert.IsNotNull(borrowed);
        Assert.AreEqual(NativeMemoryOperation.CaptureGeneration, fixture.Requests[^1]._operation);
        borrowed.Value = new(123, 0);
        NativeMemoryRequest write = fixture.Requests[^1];
        Assert.AreEqual(101, write._context);
        Assert.AreEqual(901, write._other);
        Assert.AreEqual(801, write._pointer);
        Assert.AreEqual(123, write._value);
        Assert.AreEqual((nuint)0, write._length);
        using PgNativeItemPointer another = borrowed.Borrow();
        borrowed.Dispose();
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, ushort.MaxValue), another.Value);
        Assert.AreEqual(801, (nint)another.DangerousDetach());
        Assert.DoesNotContain(request => request._operation is NativeMemoryOperation.Free or NativeMemoryOperation.Detach, fixture.Requests);
    }

    /// <summary>
    /// Detached and foreign providers reject access before dispatch; native expiry becomes a managed stale-view error.
    /// </summary>
    [TestMethod]
    public void LifetimesAndFailuresAreChecked()
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => PgNativeItemPointer.Create(default));
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => Respond(fixture, request);
        using PgNativeItemPointer owner = PgNativeItemPointer.Create(default);
        int before = fixture.Requests.Count;
        using (MemoryContextTestFixture.Enter(18))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = owner.Value);
            Assert.ThrowsExactly<InvalidOperationException>(owner.Dispose);
            Assert.HasCount(before, fixture.Requests);
        }

        fixture.Handler = request => Is(request, 2)
            ? throw new PgException("55000", "stale native item pointer") : Respond(fixture, request);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = owner.Value);
        Assert.AreEqual(1, fixture.ErrorReleases);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Free
            ? throw new PgException("53200", "retry the native free") : Respond(fixture, request);
        PgException error = Assert.ThrowsExactly<PgException>(owner.Dispose);
        Assert.AreEqual("53200", error.SqlState);
        Assert.AreEqual("retry the native free", error.Message);
        Assert.AreEqual(2, fixture.ErrorReleases);
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, ushort.MaxValue), owner.Value);
        fixture.Handler = request => Respond(fixture, request);
        owner.Dispose();
        Assert.HasCount(2, fixture.Requests.Where(request => request._operation == NativeMemoryOperation.Free));
    }

    /// <summary>
    /// Native allocation errors release diagnostics, retain exact error identity, and allow a later successful creation.
    /// </summary>
    [TestMethod]
    public void CreationFailureAndCorruptReadsDoNotLoseData()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => Is(request, 1)
            ? throw new PgException("53200", "allocation refused", "no storage acquired", "retry later") : Respond(fixture, request);
        PgException error = Assert.ThrowsExactly<PgException>(() => PgNativeItemPointer.Create(PgItemPointer.Invalid));
        Assert.AreEqual("53200", error.SqlState);
        Assert.AreEqual("allocation refused", error.Message);
        Assert.AreEqual("no storage acquired", error.Detail);
        Assert.AreEqual("retry later", error.Hint);
        Assert.AreEqual(3, fixture.ErrorReleases);
        fixture.Handler = request => Respond(fixture, request);
        using PgNativeItemPointer owner = PgNativeItemPointer.Create(default);
        fixture.Handler = request => Is(request, 2)
            ? new NativeMemoryResult { _value = -1, _length = 3 } : Respond(fixture, request);
        Assert.ThrowsExactly<OverflowException>(() => _ = owner.Value);
        fixture.Handler = request => Is(request, 2)
            ? new NativeMemoryResult { _value = 1, _length = 65536 } : Respond(fixture, request);
        Assert.ThrowsExactly<OverflowException>(() => _ = owner.Value);
        fixture.Handler = request => Respond(fixture, request);
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, ushort.MaxValue), owner.Value);
    }

    private static bool Is(NativeMemoryRequest request, int operation) => request._operation == NativeMemoryOperation.ItemPointer && request._flags == operation;

    private static NativeMemoryResult Respond(MemoryContextTestFixture fixture, NativeMemoryRequest request) => request._operation != NativeMemoryOperation.ItemPointer
        ? fixture.Respond(request)
        : request._flags == 1 ? new NativeMemoryResult { _pointer = 701, _length = 6, _context = 101 }
        : new NativeMemoryResult { _pointer = 801, _value = unchecked((nint)4294967295L), _length = 65535, _context = 101 };
}
