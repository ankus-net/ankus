using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies non-owning typed views, checked offsets, raw lifetime anchors, and exact shallow clones.
/// </summary>
[TestClass]
public sealed unsafe partial class NativeReferenceTests
{
    /// <summary>
    /// A checked view reads and writes the complete value at its requested byte offset without adopting storage.
    /// </summary>
    [TestMethod]
    public void AllocationBorrowChecksWholeValueAndPreservesExactOffset()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] storage = [10, 20, 30, 40, .. BitConverter.GetBytes(5)];
        fixture.Handler = request => fixture.RespondWithStorage(request, storage);
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(8);
        fixture.Requests.Clear();
        PgNativeReference<int> reference = allocation.Borrow<int>(4);
        NativeMemoryRequest validation = Assert.ContainsSingle(fixture.Requests);
        Assert.AreEqual(NativeMemoryOperation.Read, validation._operation);
        Assert.AreEqual((nuint)0, validation._length);
        Assert.AreEqual(4, validation._value);
        Assert.AreEqual(5, reference.Value);
        reference.Value = 17;
        Assert.AreSequenceEqual<byte>([10, 20, 30, 40], storage[..4]);
        Assert.AreSequenceEqual(BitConverter.GetBytes(17), storage[4..]);
        Assert.AreEqual(705, (nint)reference.DangerousGetPointer());
        Assert.AreEqual(303, reference.LifetimeContext.Id);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation is NativeMemoryOperation.Adopt or NativeMemoryOperation.CaptureGeneration));
    }

    /// <summary>
    /// Incomplete values and overflowing offsets are rejected before a native view-validation call.
    /// </summary>
    [TestMethod]
    public void BorrowRejectsPartialAndNativeOverflowRangesBeforeNativeAccess()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(8);
        fixture.Requests.Clear();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Borrow<int>(5));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Borrow<long>(1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => allocation.Borrow<int>(nuint.MaxValue));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Creating a borrow checks native identity even though validation does not copy any bytes.
    /// </summary>
    [TestMethod]
    public void BorrowRejectsAlreadyReclaimedNativeIdentity()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(8);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Read
            ? throw new PgException("55000", "expired allocation")
            : fixture.Respond(request);
        Assert.ThrowsExactly<ObjectDisposedException>(() => allocation.Borrow<int>());
        Assert.AreEqual(1, fixture.ErrorReleases);
    }

    /// <summary>
    /// Existing views follow shared resize identity and current address, then reject a shrink that removes their full range.
    /// </summary>
    [TestMethod]
    public void BorrowTracksResizeAndRejectsShrunkenRangeForEveryAccess()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] storage = [0, 0, 0, 0, .. BitConverter.GetBytes(5), 0, 0, 0, 0];
        fixture.Handler = request =>
        {
            NativeMemoryResult result = fixture.RespondWithStorage(request, storage);
            if (request._operation == NativeMemoryOperation.Read)
            {
                result._pointer = request._context == 501 ? 701 : 801;
            }

            return result;
        };
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(8);
        PgNativeReference<int> reference = allocation.Borrow<int>(4);
        Assert.AreEqual(705, (nint)reference.DangerousGetPointer());
        allocation.Reallocate(12);
        Assert.AreEqual(5, reference.Value);
        Assert.AreEqual(502, fixture.Requests[^1]._context);
        Assert.AreEqual(805, (nint)reference.DangerousGetPointer());
        reference.Value = 17;
        Assert.AreEqual(17, reference.Value);
        allocation.Reallocate(7);
        fixture.Requests.Clear();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => reference.Value);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => reference.Value = 9);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => reference.LifetimeContext);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() =>
        {
            reference.DangerousGetPointer();
        });
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Null raw addresses map to nullable references before backend or even consumed-context validation.
    /// </summary>
    [TestMethod]
    public void NullRawViewsAndOwnersNeedNoCapabilityOrLiveContext()
    {
        using var fixture = new MemoryContextTestFixture();
        PgMemoryContext context;
        using (MemoryContextTestFixture.Enter())
        {
            context = PgMemoryContext.Current;
        }

        context.Dispose();
        fixture.Requests.Clear();
        Assert.IsNull(context.DangerousBorrow<int>(null));
        Assert.IsNull(context.DangerousAdoptBox<int>(null));
        Assert.IsEmpty(fixture.Requests);
        ArgumentNullException error = Assert.ThrowsExactly<ArgumentNullException>(() => context.DangerousAdoptContextValue<int>(null));
        Assert.AreEqual("address", error.ParamName);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Raw aliases copy stack-backed initialized values without acquiring ownership or consulting chunk headers.
    /// </summary>
    [TestMethod]
    public void RawStackAliasesShareValuesUsingExplicitLifetimeAnchor()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int value = 5;
        nint address = (nint)(&value);
        fixture.Handler = request => RespondRaw(fixture, request);
        PgMemoryContext anchor = PgMemoryContext.Current;
        PgNativeReference<int>? first = anchor.DangerousBorrow<int>((void*)address);
        PgNativeReference<int>? second = anchor.DangerousBorrow<int>((void*)address);
        Assert.IsNotNull(first);
        Assert.IsNotNull(second);
        Assert.AreEqual(5, first.Value);
        second.Value = 17;
        Assert.AreEqual(17, value);
        Assert.AreEqual(17, first.Value);
        Assert.AreEqual(address, (nint)first.DangerousGetPointer());
        Assert.AreEqual(101, first.LifetimeContext.Id);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation is
            NativeMemoryOperation.Allocate or NativeMemoryOperation.Adopt or NativeMemoryOperation.Owner or NativeMemoryOperation.Free));
        NativeMemoryRequest[] captures = [.. fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.CaptureGeneration)];
        Assert.HasCount(2, captures);
        Assert.AreEqual(101, captures[0]._context);
        Assert.AreEqual(101, captures[1]._context);
    }

    /// <summary>
    /// Raw aliases preserve an interior address and repeated access never creates additional native tracking records.
    /// </summary>
    [TestMethod]
    public void RawInteriorAliasesPreserveExactAddressWithoutPerAccessRegistration()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int* values = stackalloc int[3] { 11, 5, 22 };
        fixture.Handler = request => RespondRaw(fixture, request);
        PgNativeReference<int>? reference = PgMemoryContext.Current.DangerousBorrow<int>(values + 1);
        Assert.IsNotNull(reference);
        fixture.Requests.Clear();
        for (int index = 0; index < 5; index++)
        {
            reference.Value = index;
            Assert.AreEqual(index, reference.Value);
        }

        Assert.AreEqual(11, values[0]);
        Assert.AreEqual(4, values[1]);
        Assert.AreEqual(22, values[2]);
        Assert.HasCount(10, fixture.Requests);
        Assert.IsTrue(fixture.Requests.All(static request => request._operation is NativeMemoryOperation.ReadReference or NativeMemoryOperation.WriteReference));
        Assert.IsTrue(fixture.Requests.All(request => request._pointer == (nint)(values + 1)));
    }

    /// <summary>
    /// The generation token preserves all native-width bits rather than treating its sign bit as invalid or truncating it.
    /// </summary>
    [TestMethod]
    public void RawReferencePreservesNativeWidthGenerationBitPattern()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int value = 5;
        fixture.Handler = request => request._operation == NativeMemoryOperation.CaptureGeneration
            ? new NativeMemoryResult { _value = nint.MinValue + 37 }
            : RespondRaw(fixture, request);
        PgNativeReference<int>? reference = PgMemoryContext.Current.DangerousBorrow<int>(&value);
        Assert.IsNotNull(reference);
        Assert.AreEqual(5, reference.Value);
        NativeMemoryRequest read = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.ReadReference, fixture.Requests);
        Assert.AreEqual(nint.MinValue + 37, read._other);
        Assert.AreEqual(101, read._context);
        Assert.AreEqual((nuint)sizeof(int), read._length);
    }

    /// <summary>
    /// A changed native generation invalidates every raw access while unrelated native errors retain their exact diagnostics.
    /// </summary>
    [TestMethod]
    public void RawGenerationInvalidationAndOperationalErrorsRemainDistinct()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int value = 5;
        PgNativeReference<int>? reference = PgMemoryContext.Current.DangerousBorrow<int>(&value);
        Assert.IsNotNull(reference);
        fixture.Handler = request => request._operation is NativeMemoryOperation.ReadReference or NativeMemoryOperation.WriteReference
            ? throw new PgException("55000", "generation changed")
            : fixture.Respond(request);
        Assert.ThrowsExactly<ObjectDisposedException>(() => reference.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(() => reference.Value = 7);
        Assert.ThrowsExactly<ObjectDisposedException>(() => reference.LifetimeContext);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
        {
            reference.DangerousGetPointer();
        });
        fixture.Handler = request => request._operation == NativeMemoryOperation.ReadReference
            ? throw new PgException("55006", "owner protected", "native detail", "native hint")
            : fixture.Respond(request);
        PgException error = Assert.ThrowsExactly<PgException>(() => reference.Value);
        Assert.AreEqual("55006", error.SqlState);
        Assert.AreEqual("owner protected", error.Message);
        Assert.AreEqual("native detail", error.Detail);
        Assert.AreEqual("native hint", error.Hint);
        Assert.AreEqual(7, fixture.ErrorReleases);
        Assert.AreEqual(5, value);
    }

    /// <summary>
    /// Failed generation capture exposes its native error without adopting or freeing the caller's raw storage.
    /// </summary>
    [TestMethod]
    public void FailedGenerationCaptureNeverAcquiresRawStorageOwnership()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int value = 5;
        nint address = (nint)(&value);
        fixture.Handler = request => request._operation == NativeMemoryOperation.CaptureGeneration
            ? throw new PgException("54000", "generation retired")
            : fixture.Respond(request);
        PgException error = Assert.ThrowsExactly<PgException>(() => PgMemoryContext.Current.DangerousBorrow<int>((void*)address));
        Assert.AreEqual("54000", error.SqlState);
        Assert.AreEqual("generation retired", error.Message);
        Assert.AreEqual(1, fixture.ErrorReleases);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation is NativeMemoryOperation.Adopt or NativeMemoryOperation.Free));
        Assert.AreEqual(5, value);
    }

    /// <summary>
    /// Raw clone copies exact padding and shallow pointer bytes into current-context storage with default policies.
    /// </summary>
    [TestMethod]
    public void RawReferenceCloneCopiesExactRepresentationIntoCurrentContext()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte* source = stackalloc byte[sizeof(PaddedValue)];
        for (int index = 0; index < sizeof(PaddedValue); index++)
        {
            source[index] = (byte)(index + 1);
        }

        byte[] expected = new ReadOnlySpan<byte>(source, sizeof(PaddedValue)).ToArray();
        byte[] copied = new byte[sizeof(PaddedValue)];
        fixture.Handler = request => request._operation == NativeMemoryOperation.ReadReference
            ? RespondRaw(fixture, request)
            : fixture.RespondWithStorage(request, copied);
        PgNativeReference<PaddedValue>? reference = PgMemoryContext.Current.DangerousBorrow<PaddedValue>(source);
        Assert.IsNotNull(reference);
        fixture.Current = 909;
        PgContextValue<PaddedValue> clone = reference.CloneInto();
        Assert.AreSequenceEqual(expected, copied);
        Assert.AreEqual(PgAllocationOptions.None, clone.Options);
        Assert.AreEqual((nuint)0, clone.Alignment);
        NativeMemoryRequest allocated = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Allocate, fixture.Requests);
        Assert.AreEqual(909, allocated._context);
        Assert.AreEqual((nuint)sizeof(PaddedValue), allocated._length);
        Assert.AreEqual(0, allocated._flags);
        new Span<byte>(source, sizeof(PaddedValue)).Fill(99);
        Assert.AreEqual((byte)1, clone.Value._tag);
        Assert.AreEqual((nint)BitConverter.ToInt64(expected, 8), clone.Value._address);
        Assert.AreSequenceEqual(expected, copied);
    }

    /// <summary>
    /// Cloning failure after reading raw bytes frees the new allocation without taking ownership of the raw source.
    /// </summary>
    [TestMethod]
    public void RawCloneInitializationFailureCleansDestinationWithoutFreeingSource()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        int value = 5;
        fixture.Handler = request => request._operation == NativeMemoryOperation.Write
            ? throw new PgException("22023", "clone write failed")
            : RespondRaw(fixture, request);
        PgNativeReference<int>? reference = PgMemoryContext.Current.DangerousBorrow<int>(&value);
        Assert.IsNotNull(reference);
        PgException error = Assert.ThrowsExactly<PgException>(() => reference.CloneOwnedInto());
        Assert.AreEqual("22023", error.SqlState);
        Assert.AreEqual("clone write failed", error.Message);
        Assert.AreEqual(501, Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Free, fixture.Requests)._context);
        Assert.AreEqual(5, value);
        Assert.AreEqual(5, reference.Value);
    }

    /// <summary>
    /// Raw views reject detached, foreign-provider and worker-thread calls before any raw memory transport.
    /// </summary>
    [TestMethod]
    public void RawReferencesRemainBoundToBackendCapabilityAndProvider()
    {
        using var fixture = new MemoryContextTestFixture();
        int value = 5;
        PgNativeReference<int>? reference;
        fixture.Handler = request => RespondRaw(fixture, request);
        using (MemoryContextTestFixture.Enter())
        {
            reference = PgMemoryContext.Current.DangerousBorrow<int>(&value);
        }

        Assert.IsNotNull(reference);
        fixture.Requests.Clear();
        Assert.ThrowsExactly<InvalidOperationException>(() => reference.Value);
        using (MemoryContextTestFixture.Enter(29))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => reference.Value);
            Assert.ThrowsExactly<InvalidOperationException>(() => reference.Value = 7);
            Assert.ThrowsExactly<InvalidOperationException>(() => reference.LifetimeContext);
            Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                reference.DangerousGetPointer();
            });
        }

        DedicatedThread.Run(() => Assert.ThrowsExactly<InvalidOperationException>(() => reference.Value));
        Assert.IsEmpty(fixture.Requests);
        using (MemoryContextTestFixture.Enter())
        {
            Assert.AreEqual(5, reference.Value);
        }
    }

    /// <summary>
    /// Performs only the requested memory copy over known live test-owned addresses, without emulating allocator or generation behavior.
    /// </summary>
    private static NativeMemoryResult RespondRaw(MemoryContextTestFixture fixture, NativeMemoryRequest request)
    {
        if (request._operation is not (NativeMemoryOperation.ReadReference or NativeMemoryOperation.WriteReference))
        {
            return fixture.Respond(request);
        }

        int length = checked((int)request._length);
        if (length != 0)
        {
            if (request._operation == NativeMemoryOperation.ReadReference)
            {
                new ReadOnlySpan<byte>((void*)request._pointer, length).CopyTo(new Span<byte>((void*)request._data, length));
            }
            else
            {
                new ReadOnlySpan<byte>((void*)request._data, length).CopyTo(new Span<byte>((void*)request._pointer, length));
            }
        }

        return new NativeMemoryResult { _context = request._context, _pointer = request._pointer, _length = request._length };
    }

    /// <summary>
    /// Supplies meaningful pointer bytes and explicit padding for exact representation copying.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PaddedValue
    {
        /// <summary>
        /// Occupies the first representation byte.
        /// </summary>
        [FieldOffset(0)]
        internal byte _tag;

        /// <summary>
        /// Contains a shallow pointer value that is copied without dereferencing its target.
        /// </summary>
        [FieldOffset(8)]
        internal nint _address;
    }
}
