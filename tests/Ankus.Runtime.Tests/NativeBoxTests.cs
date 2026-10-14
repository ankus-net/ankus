using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies individually owned native values, initialization, transfer, cleanup, and exact cloning.
/// </summary>
[TestClass]
public sealed unsafe class NativeBoxTests
{
    /// <summary>
    /// Initialized values preserve exact bytes and update through copied property access with native provenance intact.
    /// </summary>
    [TestMethod]
    public void InitializedOwnerPreservesValuePoliciesAndNativeContext()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] storage = new byte[sizeof(long)];
        fixture.Handler = request => fixture.RespondWithStorage(request, storage);
        using PgNativeBox<long> box = PgMemoryContext.Current.CreateBox(0x1020304050607080L, PgAllocationOptions.Huge, 64);
        Assert.AreEqual(0x1020304050607080L, box.Value);
        Assert.AreSequenceEqual(BitConverter.GetBytes(0x1020304050607080L), storage);
        box.Value = -7;
        Assert.AreEqual(-7L, box.Value);
        Assert.AreSequenceEqual(BitConverter.GetBytes(-7L), storage);
        Assert.AreEqual(303, box.Context.Id);
        Assert.AreEqual(PgAllocationOptions.Huge, box.Options);
        Assert.AreEqual((nuint)64, box.Alignment);
        Assert.AreEqual(701, (nint)box.DangerousGetPointer());
        NativeMemoryRequest allocated = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Allocate, fixture.Requests);
        Assert.AreEqual((nuint)sizeof(long), allocated._length);
        Assert.AreEqual(4, allocated._flags);
        Assert.AreEqual((nuint)64, allocated._alignment);
    }

    /// <summary>
    /// Zeroed and uninitialized factories retain distinct native allocation policies and do not perform value initialization writes.
    /// </summary>
    [TestMethod]
    public void ZeroedAndUninitializedFactoriesPreserveTheirInitializationContracts()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] storage = new byte[sizeof(int)];
        fixture.Handler = request => fixture.RespondWithStorage(request, storage);
        PgMemoryContext context = PgMemoryContext.Current;
        using (PgNativeBox<int> zeroed = context.AllocateZeroedBox<int>(PgAllocationOptions.Huge, 32))
        {
            Assert.AreEqual(0, zeroed.Value);
            Assert.AreEqual(PgAllocationOptions.Zeroed | PgAllocationOptions.Huge, zeroed.Options);
            NativeMemoryRequest allocated = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Allocate, fixture.Requests);
            Assert.AreEqual(5, allocated._flags);
            Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Write));
        }

        fixture.Requests.Clear();
        using PgNativeBox<int> uninitialized = context.DangerousAllocateUninitializedBox<int>();
        Assert.AreEqual(0, Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Allocate, fixture.Requests)._flags);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Write));
        uninitialized.Value = 5;
        Assert.AreEqual(5, uninitialized.Value);
    }

    /// <summary>
    /// No-OOM NULL produces no owner or write, while successful retry initializes the exact provided value.
    /// </summary>
    [TestMethod]
    public void TryCreateDistinguishesNativeNullFromInitializedSuccessAndErrors()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        byte[] storage = new byte[sizeof(int)];
        fixture.Handler = request => request._operation == NativeMemoryOperation.Allocate ? default : fixture.Respond(request);
        Assert.IsNull(context.TryCreateBox(5, PgAllocationOptions.Huge, 64));
        Assert.AreEqual(6, Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Allocate, fixture.Requests)._flags);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation is NativeMemoryOperation.Write or NativeMemoryOperation.Free));
        fixture.Handler = request => fixture.RespondWithStorage(request, storage);
        using PgNativeBox<int>? box = context.TryCreateBox(5, PgAllocationOptions.Huge, 64);
        Assert.IsNotNull(box);
        Assert.AreEqual(5, box.Value);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Allocate
            ? throw new PgException("22023", "native allocation rejection")
            : fixture.RespondWithStorage(request, storage);
        PgException error = Assert.ThrowsExactly<PgException>(() => context.TryCreateBox(7));
        Assert.AreEqual("22023", error.SqlState);
        Assert.AreEqual("native allocation rejection", error.Message);
    }

    /// <summary>
    /// Initializing a new owner attempts native release and retains both errors when release also fails.
    /// </summary>
    /// <param name="failCleanup">Whether native freeing also fails.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedInitializationFreesNewAllocationAndPreservesCleanupError(bool failCleanup)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Handler = request => request._operation switch
        {
            NativeMemoryOperation.Write => throw new PgException("22023", "initialization failed"),
            NativeMemoryOperation.Free when failCleanup => throw new PgException("55006", "cleanup failed"),
            _ => fixture.Respond(request),
        };
        if (failCleanup)
        {
            AggregateException error = Assert.ThrowsExactly<AggregateException>(() => context.CreateBox(5));
            Assert.HasCount(2, error.InnerExceptions);
            PgException primary = Assert.IsInstanceOfType<PgException>(error.InnerExceptions[0]);
            PgException cleanup = Assert.IsInstanceOfType<PgException>(error.InnerExceptions[1]);
            Assert.AreEqual("22023", primary.SqlState);
            Assert.AreEqual("initialization failed", primary.Message);
            Assert.AreEqual("55006", cleanup.SqlState);
            Assert.AreEqual("cleanup failed", cleanup.Message);
        }
        else
        {
            PgException error = Assert.ThrowsExactly<PgException>(() => context.CreateBox(5));
            Assert.AreEqual("22023", error.SqlState);
            Assert.AreEqual("initialization failed", error.Message);
        }

        Assert.AreEqual(501, Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Free, fixture.Requests)._context);
        Assert.AreEqual(failCleanup ? 2 : 1, fixture.ErrorReleases);
    }

    /// <summary>
    /// Failed native free preserves the owner's value and borrowed view until successful retry invalidates both.
    /// </summary>
    [TestMethod]
    public void FailedFreeRetainsOwnerAndSuccessfulRetryInvalidatesBorrow()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] storage = new byte[sizeof(int)];
        fixture.Handler = request => fixture.RespondWithStorage(request, storage);
        using PgNativeBox<int> box = PgMemoryContext.Current.CreateBox(5);
        PgNativeReference<int> borrow = box.Borrow();
        fixture.Handler = request => request._operation == NativeMemoryOperation.Free
            ? throw new PgException("55006", "free failed")
            : fixture.RespondWithStorage(request, storage);
        PgException error = Assert.ThrowsExactly<PgException>(box.Dispose);
        Assert.AreEqual("55006", error.SqlState);
        Assert.AreEqual(5, box.Value);
        Assert.AreEqual(5, borrow.Value);
        fixture.Handler = request => fixture.RespondWithStorage(request, storage);
        box.Dispose();
        fixture.Requests.Clear();
        box.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => box.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(() => borrow.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(box.Borrow);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Context transfer consumes only the old release capability and preserves existing checked views without native detachment.
    /// </summary>
    [TestMethod]
    public void ReleaseToContextRetainsStorageAndExistingBorrowWithoutFreeing()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] storage = new byte[sizeof(int)];
        fixture.Handler = request => fixture.RespondWithStorage(request, storage);
        using PgNativeBox<int> box = PgMemoryContext.Current.CreateBox(5, PgAllocationOptions.Huge, 32);
        PgNativeReference<int> before = box.Borrow();
        fixture.Requests.Clear();
        PgContextValue<int> value = box.ReleaseToContext();
        box.Dispose();
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation is NativeMemoryOperation.Free or NativeMemoryOperation.Detach));
        Assert.ThrowsExactly<ObjectDisposedException>(() => box.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(box.ReleaseToContext);
        Assert.AreEqual(5, value.Value);
        Assert.AreEqual(PgAllocationOptions.Huge, value.Options);
        Assert.AreEqual((nuint)32, value.Alignment);
        before.Value = 17;
        Assert.AreEqual(17, value.Value);
        Assert.AreEqual(303, before.LifetimeContext.Id);
        Assert.AreEqual(701, (nint)value.DangerousDetach());
        Assert.ThrowsExactly<ObjectDisposedException>(() => value.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(() => before.Value);
    }

    /// <summary>
    /// Failed transfer validation preserves copied access, new borrows, and either disposal or a later ownership transfer.
    /// </summary>
    /// <param name="disposeAfterFailure">Whether to exercise retained disposal rights instead of retrying transfer.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedContextTransferRetainsOwnerUntilExplicitDisposalOrRetry(bool disposeAfterFailure)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] storage = new byte[sizeof(int)];
        fixture.Handler = request => fixture.RespondWithStorage(request, storage);
        using PgNativeBox<int> box = PgMemoryContext.Current.CreateBox(5);
        PgNativeReference<int> before = box.Borrow();
        fixture.Handler = request => request._operation == NativeMemoryOperation.Read && request._length == 0
            ? throw new PgException("55006", "transfer check failed", "native detail", "native hint")
            : fixture.RespondWithStorage(request, storage);
        PgException error = Assert.ThrowsExactly<PgException>(box.ReleaseToContext);
        Assert.AreEqual("55006", error.SqlState);
        Assert.AreEqual("transfer check failed", error.Message);
        Assert.AreEqual("native detail", error.Detail);
        Assert.AreEqual("native hint", error.Hint);
        Assert.AreEqual(3, fixture.ErrorReleases);
        Assert.AreEqual(5, box.Value);
        Assert.AreEqual(5, before.Value);
        fixture.Handler = request => fixture.RespondWithStorage(request, storage);
        PgNativeReference<int> after = box.Borrow();
        box.Value = 17;
        Assert.AreEqual(17, before.Value);
        Assert.AreEqual(17, after.Value);
        if (disposeAfterFailure)
        {
            box.Dispose();
            Assert.AreEqual(501, Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Free, fixture.Requests)._context);
            Assert.ThrowsExactly<ObjectDisposedException>(() => box.Value);
            Assert.ThrowsExactly<ObjectDisposedException>(() => before.Value);
            Assert.ThrowsExactly<ObjectDisposedException>(() => after.Value);
        }
        else
        {
            PgContextValue<int> transferred = box.ReleaseToContext();
            box.Dispose();
            Assert.AreEqual(17, transferred.Value);
            Assert.AreEqual(17, before.Value);
            Assert.AreEqual(17, after.Value);
            Assert.ThrowsExactly<ObjectDisposedException>(() => box.Value);
            Assert.IsEmpty(fixture.Requests.Where(static request => request._operation is NativeMemoryOperation.Free or NativeMemoryOperation.Detach));
        }
    }

    /// <summary>
    /// Failed detach leaves all checked views live, and successful detach consumes their shared allocation identity.
    /// </summary>
    [TestMethod]
    public void FailedDetachPreservesOwnerAndViewsUntilSuccessfulTransfer()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] storage = new byte[sizeof(int)];
        fixture.Handler = request => fixture.RespondWithStorage(request, storage);
        using PgNativeBox<int> box = PgMemoryContext.Current.CreateBox(5);
        PgNativeReference<int> reference = box.Borrow();
        fixture.Handler = request => request._operation == NativeMemoryOperation.Detach
            ? throw new PgException("55006", "transfer failed")
            : fixture.RespondWithStorage(request, storage);
        PgException error = Assert.ThrowsExactly<PgException>(() =>
        {
            box.DangerousDetach();
        });
        Assert.AreEqual("55006", error.SqlState);
        Assert.AreEqual(5, box.Value);
        Assert.AreEqual(5, reference.Value);
        fixture.Handler = request => fixture.RespondWithStorage(request, storage);
        Assert.AreEqual(701, (nint)box.DangerousDetach());
        fixture.Requests.Clear();
        box.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => box.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(() => reference.Value);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Raw owned adoption maps null before capability checks and otherwise transfers the exact native value width and provenance.
    /// </summary>
    [TestMethod]
    public void RawAdoptionMapsNullWithoutBackendAndPreservesNativeOwnership()
    {
        using var fixture = new MemoryContextTestFixture();
        PgMemoryContext context;
        using (MemoryContextTestFixture.Enter())
        {
            context = PgMemoryContext.Current;
        }

        fixture.Requests.Clear();
        Assert.IsNull(context.DangerousAdoptBox<int>(null));
        Assert.IsEmpty(fixture.Requests);
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgNativeBox<int>? box = context.DangerousAdoptBox<int>((void*)701, huge: true, alignment: 64);
        Assert.IsNotNull(box);
        NativeMemoryRequest request = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Adopt, fixture.Requests);
        Assert.AreEqual(701, request._pointer);
        Assert.AreEqual((nuint)sizeof(int), request._length);
        Assert.AreEqual(4, request._flags);
        Assert.AreEqual((nuint)64, request._alignment);
        Assert.AreEqual(PgAllocationOptions.Huge, box.Options);
        box.Dispose();
        Assert.AreEqual(601, Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Free, fixture.Requests)._context);
    }

    /// <summary>
    /// Owned cloning copies every padding byte and pointer field into default-policy storage in the explicit destination.
    /// </summary>
    [TestMethod]
    public void OwnedClonePreservesPaddingAndShallowPointerBytesWithIndependentOwnership()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] source = [.. Enumerable.Range(1, sizeof(PaddedValue)).Select(static value => (byte)value)];
        byte[] destination = new byte[sizeof(PaddedValue)];
        int allocations = 0;
        fixture.Handler = request =>
        {
            if (request._operation == NativeMemoryOperation.Allocate)
            {
                return new NativeMemoryResult { _pointer = ++allocations == 1 ? 501 : 502, _length = request._length };
            }

            return fixture.RespondWithStorage(request, request._context == 502 ? destination : source);
        };
        PgMemoryContext context = PgMemoryContext.Current;
        using PgNativeBox<PaddedValue> box = context.DangerousAllocateUninitializedBox<PaddedValue>(PgAllocationOptions.Huge, 64);
        using PgMemoryContext target = PgMemoryContext.Create("clone target");
        using PgNativeBox<PaddedValue> clone = box.CloneOwnedInto(target);
        Assert.AreSequenceEqual(source, destination);
        Assert.AreEqual(PgAllocationOptions.None, clone.Options);
        Assert.AreEqual((nuint)0, clone.Alignment);
        NativeMemoryRequest allocated = fixture.Requests.Last(static request => request._operation == NativeMemoryOperation.Allocate);
        Assert.AreEqual(202, allocated._context);
        Assert.AreEqual((nuint)sizeof(PaddedValue), allocated._length);
        Assert.AreEqual(0, allocated._flags);
        source.AsSpan().Fill(99);
        Assert.AreEqual((byte)1, destination[0]);
        box.Dispose();
        PaddedValue copied = clone.Value;
        Assert.AreEqual((byte)1, copied._tag);
        Assert.AreEqual(BitConverter.ToInt64(destination, 8), copied._payload);
        Assert.AreEqual((nint)BitConverter.ToInt64(destination, 16), copied._address);
        Assert.AreEqual(502, fixture.Requests[^1]._context);
    }

    /// <summary>
    /// Native storage disposal does not invoke IDisposable implemented by an unmanaged pointee.
    /// </summary>
    [TestMethod]
    public void OwningBoxNeverInvokesPointeeDestructor()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int disposed = 0;
        var value = new DisposableValue { _counter = &disposed };
        using (PgNativeBox<DisposableValue> box = PgMemoryContext.Current.CreateBox(value))
        {
            Assert.AreEqual(303, box.Context.Id);
        }

        Assert.AreEqual(0, disposed);
        Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Free, fixture.Requests);
    }

    /// <summary>
    /// Provides a representation with explicit padding whose contents cannot be reconstructed through ordinary field copies.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PaddedValue
    {
        /// <summary>
        /// Occupies only the first byte of the representation.
        /// </summary>
        [FieldOffset(0)]
        internal byte _tag;

        /// <summary>
        /// Holds a shallow unmanaged payload after an explicit padding gap.
        /// </summary>
        [FieldOffset(8)]
        internal long _payload;

        /// <summary>
        /// Holds a shallow pointer whose target must not be recursively copied.
        /// </summary>
        [FieldOffset(16)]
        internal nint _address;
    }

    /// <summary>
    /// Supplies an unmanaged destructor witness whose counter must never be touched by native ownership cleanup.
    /// </summary>
    private struct DisposableValue : IDisposable
    {
        /// <summary>
        /// Points to the owning test's live destructor counter.
        /// </summary>
        internal int* _counter;

        /// <summary>
        /// Records a destructor call if a wrapper incorrectly invokes it.
        /// </summary>
        public readonly void Dispose() => (*_counter)++;
    }
}
