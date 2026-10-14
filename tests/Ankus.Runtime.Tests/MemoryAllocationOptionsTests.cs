using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies allocation policies, typed sizes, exact copying, resize outcomes, and raw ownership transport.
/// </summary>
[TestClass]
public sealed unsafe class MemoryAllocationOptionsTests
{
    /// <summary>
    /// Every supported option combination reaches native allocation unchanged and remains visible on its owner.
    /// </summary>
    /// <param name="options">The supported initialization and size policy combination.</param>
    [TestMethod]
    [DataRow(PgAllocationOptions.None)]
    [DataRow(PgAllocationOptions.Zeroed)]
    [DataRow(PgAllocationOptions.Huge)]
    [DataRow(PgAllocationOptions.Zeroed | PgAllocationOptions.Huge)]
    public void SupportedPoliciesRemainDistinctThroughAllocation(PgAllocationOptions options)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(17, options, 64);
        NativeMemoryRequest request = Find(fixture, NativeMemoryOperation.Allocate);
        Assert.AreEqual((int)options, request._flags);
        Assert.AreEqual((nuint)64, request._alignment);
        Assert.AreEqual((nuint)17, request._length);
        Assert.AreEqual(101, request._context);
        Assert.AreEqual(options, allocation.Options);
        Assert.AreEqual((nuint)64, allocation.Alignment);
        Assert.AreEqual((nuint)17, allocation.Length);
    }

    /// <summary>
    /// Zeroed helpers add initialization without dropping the requested huge-size policy or alignment.
    /// </summary>
    [TestMethod]
    public void ZeroedAndNoOomHelpersComposePoliciesWithoutExposingNoOomAsAnOption()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        using PgAllocation zeroed = context.AllocateZeroed(13, PgAllocationOptions.Huge, 128);
        NativeMemoryRequest first = Find(fixture, NativeMemoryOperation.Allocate);
        Assert.AreEqual(5, first._flags);
        Assert.AreEqual((nuint)128, first._alignment);
        Assert.AreEqual(PgAllocationOptions.Zeroed | PgAllocationOptions.Huge, zeroed.Options);
        fixture.Requests.Clear();
        using PgAllocation? attempted = context.TryAllocate(19, PgAllocationOptions.Huge | PgAllocationOptions.Zeroed, 256);
        Assert.IsNotNull(attempted);
        NativeMemoryRequest second = Find(fixture, NativeMemoryOperation.Allocate);
        Assert.AreEqual(7, second._flags);
        Assert.AreEqual((nuint)256, second._alignment);
        Assert.AreEqual(PgAllocationOptions.Zeroed | PgAllocationOptions.Huge, attempted.Options);
        Assert.AreEqual((nuint)256, attempted.Alignment);
    }

    /// <summary>
    /// Undefined option bits, including the private no-OOM bit, fail before native context validation.
    /// </summary>
    /// <param name="value">An unsupported option representation.</param>
    [TestMethod]
    [DataRow(-1)]
    [DataRow(2)]
    [DataRow(3)]
    [DataRow(8)]
    public void InvalidPoliciesAreRejectedBeforeNativeCalls(int value)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Requests.Clear();
        PgAllocationOptions options = (PgAllocationOptions)value;
        ArgumentOutOfRangeException error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.Allocate(1, options));
        Assert.AreEqual("options", error.ParamName);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.AllocateZeroed(1, options));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.TryAllocate(1, options));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.CopyFrom([1, 2], options));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Default alignment and each representative power-of-two partition reach the native capability unchanged.
    /// </summary>
    /// <param name="alignment">The accepted managed alignment.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(8)]
    [DataRow(4096)]
    [DataRow(67108864)]
    public void ValidAlignmentPreservesExplicitRequest(int alignment)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(0, alignment: (nuint)alignment);
        NativeMemoryRequest request = Find(fixture, NativeMemoryOperation.Allocate);
        Assert.AreEqual((nuint)alignment, request._alignment);
        Assert.AreEqual((nuint)alignment, allocation.Alignment);
        Assert.AreEqual((nuint)0, allocation.Length);
    }

    /// <summary>
    /// Nonpowers and the native exclusive upper boundary never reach native allocator assertions.
    /// </summary>
    /// <param name="alignment">The rejected managed alignment.</param>
    [TestMethod]
    [DataRow(3)]
    [DataRow(6)]
    [DataRow(134217727)]
    [DataRow(134217728)]
    [DataRow(268435456)]
    public void InvalidAlignmentIsRejectedForAllocationAndAdoption(int alignment)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Requests.Clear();
        ArgumentOutOfRangeException error = Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.Allocate(1, alignment: (nuint)alignment));
        Assert.AreEqual("alignment", error.ParamName);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.TryAllocate(1, PgAllocationOptions.None, (nuint)alignment));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.DangerousAdopt((void*)701, 1, alignment: (nuint)alignment));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// The full native-size maximum cannot wrap through the alignment power-of-two check.
    /// </summary>
    [TestMethod]
    public void NativeWidthAlignmentOverflowIsRejectedLocally()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Requests.Clear();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => context.Allocate(1, alignment: nuint.MaxValue));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Unmanaged helpers multiply element count by full value width, including zero and multiple elements.
    /// </summary>
    /// <param name="count">The requested value count.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(3)]
    public void TypedAllocationCountsUseCheckedNativeByteLengths(int count)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        using PgAllocation allocation = context.Allocate<long>((nuint)count, PgAllocationOptions.Huge, 32);
        using PgAllocation zeroed = context.AllocateZeroed<long>((nuint)count, PgAllocationOptions.Huge, 32);
        using PgAllocation? attempted = context.TryAllocate<long>((nuint)count, PgAllocationOptions.Huge, 32);
        Assert.IsNotNull(attempted);
        NativeMemoryRequest[] requests = [.. fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Allocate)];
        Assert.HasCount(3, requests);
        Assert.AreSequenceEqual([4, 5, 6], requests.Select(static request => request._flags));
        foreach (NativeMemoryRequest request in requests)
        {
            Assert.AreEqual((nuint)(count * sizeof(long)), request._length);
            Assert.AreEqual((nuint)32, request._alignment);
        }

        Assert.AreEqual((nuint)(count * sizeof(long)), allocation.Length);
        Assert.AreEqual(allocation.Length, zeroed.Length);
        Assert.AreEqual(allocation.Length, attempted.Length);
    }

    /// <summary>
    /// Omitted generic counts allocate exactly one complete value.
    /// </summary>
    [TestMethod]
    public void TypedAllocationDefaultCountIsOneValue()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        using PgAllocation value = context.Allocate<decimal>();
        using PgAllocation zeroed = context.AllocateZeroed<decimal>();
        using PgAllocation? attempted = context.TryAllocate<decimal>();
        Assert.IsNotNull(attempted);
        Assert.AreEqual((nuint)sizeof(decimal), value.Length);
        Assert.AreEqual(value.Length, zeroed.Length);
        Assert.AreEqual(value.Length, attempted.Length);
    }

    /// <summary>
    /// Counts whose byte product cannot fit native size fail before liveness and native allocation calls.
    /// </summary>
    [TestMethod]
    public void TypedAllocationOverflowNeverReachesNativeCode()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Requests.Clear();
        nuint count = (nuint.MaxValue / sizeof(long)) + 1;
        Assert.ThrowsExactly<OverflowException>(() => context.Allocate<long>(count));
        Assert.ThrowsExactly<OverflowException>(() => context.AllocateZeroed<long>(count));
        Assert.ThrowsExactly<OverflowException>(() => context.TryAllocate<long>(count));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// The largest representable typed product reaches native size validation without wrapping or inferring huge policy.
    /// </summary>
    [TestMethod]
    public void LargestRepresentableTypedProductReachesNativeSizeValidation()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Handler = request => request._operation == NativeMemoryOperation.Allocate
            ? throw new PgException("XX000", "native size limit")
            : fixture.Respond(request);
        nuint count = nuint.MaxValue / sizeof(long);
        PgException error = Assert.ThrowsExactly<PgException>(() => context.Allocate<long>(count));
        Assert.AreEqual("XX000", error.SqlState);
        Assert.AreEqual("native size limit", error.Message);
        NativeMemoryRequest request = Find(fixture, NativeMemoryOperation.Allocate);
        Assert.AreEqual(count * sizeof(long), request._length);
        Assert.AreEqual(0, request._flags);
        Assert.AreEqual(1, fixture.ErrorReleases);
    }

    /// <summary>
    /// Byte and typed copies preserve complete representations in independent storage, including an empty span.
    /// </summary>
    /// <param name="empty">Whether to copy an empty input.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CopyFromPreservesIndependentByteAndUnmanagedContents(bool empty)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] source = empty ? [] : [0, 255, 17, 128];
        byte[] expected = [.. source];
        byte[]? stored = null;
        fixture.Handler = request =>
        {
            if (request._operation == NativeMemoryOperation.Write)
            {
                stored = new ReadOnlySpan<byte>((void*)request._data, checked((int)request._length)).ToArray();
            }

            return fixture.Respond(request);
        };
        PgMemoryContext context = PgMemoryContext.Current;
        using PgAllocation copy = context.CopyFrom(source, PgAllocationOptions.Huge, 64);
        source.AsSpan().Fill(99);
        Assert.AreSequenceEqual(expected, stored);
        Assert.AreEqual((nuint)expected.Length, copy.Length);
        Assert.AreEqual(PgAllocationOptions.Huge, copy.Options);
        Assert.AreEqual((nuint)64, copy.Alignment);
        long[] values = empty ? [] : [0x0102030405060708, -19];
        byte[] expectedValues = MemoryMarshal.AsBytes(values.AsSpan()).ToArray();
        using PgAllocation typed = context.CopyFrom<long>(values);
        values.AsSpan().Fill(47);
        Assert.AreSequenceEqual(expectedValues, stored);
        Assert.AreEqual((nuint)expectedValues.Length, typed.Length);
    }

    /// <summary>
    /// Copy failures free the new native allocation and preserve both diagnostics when cleanup also fails.
    /// </summary>
    /// <param name="failCleanup">Whether freeing the failed copy also reports a native error.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedCopyAttemptsCleanupAndPreservesPrimaryAndCleanupErrors(bool failCleanup)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => request._operation switch
        {
            NativeMemoryOperation.Write => throw new PgException("22023", "copy rejected"),
            NativeMemoryOperation.Free when failCleanup => throw new PgException("55006", "free rejected"),
            _ => fixture.Respond(request),
        };
        PgMemoryContext context = PgMemoryContext.Current;
        if (failCleanup)
        {
            AggregateException error = Assert.ThrowsExactly<AggregateException>(() => context.CopyFrom([1, 2, 3]));
            Assert.HasCount(2, error.InnerExceptions);
            PgException primary = Assert.IsInstanceOfType<PgException>(error.InnerExceptions[0]);
            PgException cleanup = Assert.IsInstanceOfType<PgException>(error.InnerExceptions[1]);
            Assert.AreEqual("22023", primary.SqlState);
            Assert.AreEqual("copy rejected", primary.Message);
            Assert.AreEqual("55006", cleanup.SqlState);
            Assert.AreEqual("free rejected", cleanup.Message);
        }
        else
        {
            PgException error = Assert.ThrowsExactly<PgException>(() => context.CopyFrom([1, 2, 3]));
            Assert.AreEqual("22023", error.SqlState);
            Assert.AreEqual("copy rejected", error.Message);
        }

        NativeMemoryRequest free = Find(fixture, NativeMemoryOperation.Free);
        Assert.AreEqual(501, free._context);
        Assert.AreEqual(failCleanup ? 2 : 1, fixture.ErrorReleases);
    }

    /// <summary>
    /// UTF-8 copying preserves Unicode and exactly one terminator without consulting server encoding.
    /// </summary>
    /// <param name="value">The string whose raw UTF-8 representation is expected.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("café 🐘")]
    public void Utf8StringIncludesExactlyOneTerminator(string value)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[]? stored = null;
        fixture.Handler = request =>
        {
            if (request._operation == NativeMemoryOperation.Write)
            {
                stored = new ReadOnlySpan<byte>((void*)request._data, checked((int)request._length)).ToArray();
            }

            return fixture.Respond(request);
        };
        using PgAllocation allocation = PgMemoryContext.Current.AllocateUtf8String(value);
        byte[] expected = Encoding.UTF8.GetBytes(value + '\0');
        Assert.AreSequenceEqual(expected, stored);
        Assert.AreEqual((nuint)expected.Length, allocation.Length);
    }

    /// <summary>
    /// Invalid strings fail locally rather than replacing invalid Unicode or truncating at an embedded zero.
    /// </summary>
    [TestMethod]
    public void InvalidUtf8StringsAreRejectedBeforeNativeCalls()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Requests.Clear();
        ArgumentNullException missing = Assert.ThrowsExactly<ArgumentNullException>(() => context.AllocateUtf8String(null!));
        Assert.AreEqual("value", missing.ParamName);
        ArgumentException embedded = Assert.ThrowsExactly<ArgumentException>(() => context.AllocateUtf8String("before\0after"));
        Assert.AreEqual("value", embedded.ParamName);
        Assert.ThrowsExactly<EncoderFallbackException>(() => context.AllocateUtf8String("\ud800"));
        Assert.ThrowsExactly<EncoderFallbackException>(() => context.AllocateUtf8String("\udc00"));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Optional zero extension and no-OOM policy are separate resize flags, with original provenance retained.
    /// </summary>
    /// <param name="zeroNewMemory">Whether to request clearing the newly added tail.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ResizePreservesPolicyAndAlignmentWithExplicitZeroExtension(bool zeroNewMemory)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(8, PgAllocationOptions.Huge, 4096);
        allocation.Reallocate(19, zeroNewMemory);
        NativeMemoryRequest first = Find(fixture, NativeMemoryOperation.Reallocate);
        Assert.AreEqual(zeroNewMemory ? 1 : 0, first._flags);
        Assert.AreEqual(501, first._context);
        Assert.AreEqual((nuint)19, allocation.Length);
        fixture.Requests.Clear();
        Assert.IsTrue(allocation.TryReallocate(3, zeroNewMemory));
        NativeMemoryRequest second = Find(fixture, NativeMemoryOperation.Reallocate);
        Assert.AreEqual(zeroNewMemory ? 3 : 2, second._flags);
        Assert.AreEqual(502, second._context);
        Assert.AreEqual((nuint)3, allocation.Length);
        Assert.AreEqual(PgAllocationOptions.Huge, allocation.Options);
        Assert.AreEqual((nuint)4096, allocation.Alignment);
    }

    /// <summary>
    /// A no-OOM null reply retains the exact original identity and length until a successful retry.
    /// </summary>
    [TestMethod]
    public void TryResizeNullPreservesOldHandleAndSuccessfulRetryReplacesIt()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(8, PgAllocationOptions.Huge, 64);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Reallocate ? default : fixture.Respond(request);
        Assert.IsFalse(allocation.TryReallocate(17, true));
        Assert.AreEqual((nuint)8, allocation.Length);
        Assert.AreEqual(303, allocation.Context.Id);
        Assert.AreEqual(501, fixture.Requests[^1]._context);
        fixture.Handler = null;
        Assert.IsTrue(allocation.TryReallocate(17, true));
        Assert.AreEqual((nuint)17, allocation.Length);
        Assert.AreEqual(303, allocation.Context.Id);
        Assert.AreEqual(502, fixture.Requests[^1]._context);
        Assert.AreEqual(PgAllocationOptions.Huge, allocation.Options);
        Assert.AreEqual((nuint)64, allocation.Alignment);
    }

    /// <summary>
    /// Native resize failures remain errors rather than false no-OOM results and preserve the owned allocation.
    /// </summary>
    [TestMethod]
    public void TryResizePropagatesNativeFailureAndRetainsOldOwnership()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(8);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Reallocate
            ? throw new PgException("22023", "invalid resize")
            : fixture.Respond(request);
        PgException error = Assert.ThrowsExactly<PgException>(() => allocation.TryReallocate(19));
        Assert.AreEqual("22023", error.SqlState);
        Assert.AreEqual("invalid resize", error.Message);
        Assert.AreEqual(1, fixture.ErrorReleases);
        Assert.AreEqual((nuint)8, allocation.Length);
        Assert.AreEqual(303, allocation.Context.Id);
        Assert.AreEqual(501, fixture.Requests[^1]._context);
    }

    /// <summary>
    /// Detaching consumes the checked owner without freeing the native pointer and rejects repeated transfer.
    /// </summary>
    [TestMethod]
    public void DetachTransfersPointerWithoutNativeFreeAndConsumesCheckedOwner()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(17);
        fixture.Requests.Clear();
        void* pointer = allocation.DangerousDetach();
        Assert.AreEqual(701, (nint)pointer);
        Assert.AreEqual((nuint)0, allocation.Length);
        NativeMemoryRequest detached = Assert.ContainsSingle(fixture.Requests);
        Assert.AreEqual(NativeMemoryOperation.Detach, detached._operation);
        Assert.AreEqual(501, detached._context);
        fixture.Requests.Clear();
        allocation.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
        {
            allocation.DangerousDetach();
        });
        Assert.ThrowsExactly<ObjectDisposedException>(() => allocation.Read<int>());
        Assert.ThrowsExactly<ObjectDisposedException>(() => allocation.TryReallocate(19));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Failed raw transfer keeps the original checked owner available for another attempt.
    /// </summary>
    [TestMethod]
    public void FailedDetachRetainsOwnershipAndCanBeRetried()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using PgAllocation allocation = PgMemoryContext.Current.Allocate(17);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Detach
            ? throw new PgException("55006", "transfer unavailable")
            : fixture.Respond(request);
        PgException error = Assert.ThrowsExactly<PgException>(() =>
        {
            allocation.DangerousDetach();
        });
        Assert.AreEqual("55006", error.SqlState);
        Assert.AreEqual((nuint)17, allocation.Length);
        Assert.AreEqual(303, allocation.Context.Id);
        Assert.AreEqual(501, fixture.Requests[^1]._context);
        fixture.Handler = null;
        Assert.AreEqual(701, (nint)allocation.DangerousDetach());
        Assert.AreEqual((nuint)0, allocation.Length);
    }

    /// <summary>
    /// Adoption transports explicit provenance and creates a distinct individually disposable identity.
    /// </summary>
    /// <param name="huge">Whether the transferred allocation uses huge-size policy.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AdoptionCarriesOwnerLengthAndOriginalPolicy(bool huge)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgAllocation allocation = PgMemoryContext.Current.DangerousAdopt((void*)701, 37, huge, 4096);
        NativeMemoryRequest request = Find(fixture, NativeMemoryOperation.Adopt);
        Assert.AreEqual(101, request._context);
        Assert.AreEqual(701, request._pointer);
        Assert.AreEqual((nuint)37, request._length);
        Assert.AreEqual(huge ? 4 : 0, request._flags);
        Assert.AreEqual((nuint)4096, request._alignment);
        Assert.AreEqual((nuint)37, allocation.Length);
        Assert.AreEqual(huge ? PgAllocationOptions.Huge : PgAllocationOptions.None, allocation.Options);
        Assert.AreEqual((nuint)4096, allocation.Alignment);
        allocation.Dispose();
        Assert.AreEqual(601, Find(fixture, NativeMemoryOperation.Free)._context);
    }

    /// <summary>
    /// Null pointers cannot be adopted even when the declared length is zero.
    /// </summary>
    [TestMethod]
    public void NullAdoptionIsRejectedBeforeNativeCalls()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Requests.Clear();
        ArgumentNullException error = Assert.ThrowsExactly<ArgumentNullException>(() => context.DangerousAdopt(null, 0));
        Assert.AreEqual("address", error.ParamName);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Duplicate ownership and owner-mismatch failures preserve caller-owned raw storage without a native free.
    /// </summary>
    /// <param name="message">The native adoption rejection.</param>
    [TestMethod]
    [DataRow("pointer already tracked")]
    [DataRow("pointer belongs to a different context")]
    public void FailedAdoptionLeavesRawOwnershipWithCaller(string message)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Handler = request => request._operation == NativeMemoryOperation.Adopt
            ? throw new PgException("22023", message)
            : fixture.Respond(request);
        PgException error = Assert.ThrowsExactly<PgException>(() => context.DangerousAdopt((void*)701, 37));
        Assert.AreEqual("22023", error.SqlState);
        Assert.AreEqual(message, error.Message);
        Assert.AreEqual(1, fixture.ErrorReleases);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Free));
    }

    /// <summary>
    /// Foreign providers cannot resize, transfer, or adopt using handles from another extension registry.
    /// </summary>
    [TestMethod]
    public void ForeignProviderCannotResizeDetachOrAdopt()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        using PgAllocation allocation = context.Allocate(17);
        fixture.Requests.Clear();
        using (MemoryContextTestFixture.Enter(29))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => allocation.TryReallocate(19));
            Assert.ThrowsExactly<InvalidOperationException>(() =>
            {
                allocation.DangerousDetach();
            });
            Assert.ThrowsExactly<InvalidOperationException>(() => context.DangerousAdopt((void*)701, 17));
            Assert.IsEmpty(fixture.Requests);
        }

        Assert.AreEqual((nuint)17, allocation.Length);
    }

    /// <summary>
    /// Selects one expected native operation from the recorded transport requests.
    /// </summary>
    private static NativeMemoryRequest Find(MemoryContextTestFixture fixture, NativeMemoryOperation operation)
        => Assert.ContainsSingle(request => request._operation == operation, fixture.Requests);
}
