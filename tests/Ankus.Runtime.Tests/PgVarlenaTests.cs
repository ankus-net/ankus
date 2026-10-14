using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies copied native access, callback borrowing, copy-on-write and atomic ownership transitions.
/// </summary>
[TestClass]
public sealed unsafe class PgVarlenaTests
{
    [ThreadStatic]
    private static bool s_failOid;

    /// <summary>
    /// Sets whether the static native callback fails the next type OID lookup.
    /// </summary>
    private static void FailOidLookup(bool fail) => s_failOid = fail;

    /// <summary>
    /// Registers isolated layouts whose codec factories must never participate in ownership operations.
    /// </summary>
    static PgVarlenaTests()
    {
        PgTypeRegistry.RegisterNative<Value>("ownership_value", null, 4, [], static () => throw new InvalidOperationException("Unexpected codec construction."));
        PgTypeRegistry.RegisterNative<ConstructedValue>("ownership_constructed", null, 4, [], static () => throw new InvalidOperationException("Unexpected codec construction."));
        PgTypeRegistry.RegisterNative<Payload125>("ownership_125", null, 125, [], static () => throw new InvalidOperationException("Unexpected codec construction."));
        PgTypeRegistry.RegisterNative<Payload126>("ownership_126", null, 126, [], static () => throw new InvalidOperationException("Unexpected codec construction."));
        PgTypeRegistry.RegisterNative<Payload127>("ownership_127", null, 127, [], static () => throw new InvalidOperationException("Unexpected codec construction."));
        PgTypeRegistry.RegisterNative<WrongSize>("ownership_wrong_size", null, 3, [], static () => throw new InvalidOperationException("Unexpected codec construction."));
        PgTypeRegistry.RegisterValue<NonNative>("ownership_non_native", null, static () => throw new InvalidOperationException("Unexpected codec construction."));
        PgTypeRegistry.RegisterNative<ArrayValue>("ownership_array_value", null, 4, [],
            static () => new PgNativeTypeCodec<ArrayValue>(4, [], static () => throw new InvalidOperationException("Unexpected text construction.")));
    }

    /// <summary>
    /// Honors native-reported short/full offsets, zeroes only the payload and retains the full header capacity.
    /// </summary>
    /// <param name="size">The payload size adjacent to the short-header threshold.</param>
    /// <param name="offset">The independently supplied native header size.</param>
    [TestMethod]
    [DataRow(125, 1)]
    [DataRow(126, 1)]
    [DataRow(127, 4)]
    public void CreationUsesExactHeaderOffsetAndZeroedPayload(int size, int offset)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var script = new StorageScript(fixture, (size, offset));
        using IDisposable value = size switch
        {
            125 => new PgVarlena<Payload125>(),
            126 => new PgVarlena<Payload126>(),
            _ => new PgVarlena<Payload127>(),
        };
        NativeMemoryRequest allocation = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.AllocateVarlena, fixture.Requests);
        Assert.AreEqual((nuint)size, allocation._length);
        Assert.AreEqual(101, allocation._context);
        NativeMemoryRequest write = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Write, fixture.Requests);
        Assert.AreEqual(offset, write._value);
        Assert.AreEqual((nuint)size, write._length);
        Assert.AreSequenceEqual(new byte[size], script.Blocks[0].Bytes.Slice(offset, size).ToArray());
        Assert.AreSequenceEqual(Enumerable.Repeat((byte)0xCC, offset), script.Blocks[0].Bytes[..offset].ToArray());
        Assert.AreSequenceEqual(Enumerable.Repeat((byte)0xCC, 4 - offset), script.Blocks[0].Bytes[(offset + size)..].ToArray());
    }

    /// <summary>
    /// Default initialization does not call a user-defined unmanaged parameterless constructor.
    /// </summary>
    [TestMethod]
    public void DefaultValueBypassesUserConstructor()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var script = new StorageScript(fixture, (4, 1));
        using var value = new PgVarlena<ConstructedValue>();
        Assert.AreEqual(99, new ConstructedValue().Number);
        Assert.AreEqual(0, value.Value.Number);
        Assert.IsFalse(value.IsBorrowed);
        Assert.AreEqual(script.Blocks[0].Pointer, (nint)value.DangerousGetPointer());
    }

    /// <summary>
    /// Rejects absent, non-native and mismatched native registrations before any backend request.
    /// </summary>
    [TestMethod]
    public void UnsupportedMappingsFailBeforeAllocation()
    {
        using var fixture = new MemoryContextTestFixture();
        Assert.ThrowsExactly<NotSupportedException>(() => new PgVarlena<Unregistered>());
        Assert.ThrowsExactly<NotSupportedException>(() => new PgVarlena<NonNative>());
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgVarlena<WrongSize>());
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Constructors and clones reject foreign or disposed destinations before requesting a native allocation.
    /// </summary>
    /// <param name="foreign">Whether the destination belongs to another provider rather than being disposed.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InvalidDestinationsFailBeforeAllocation(bool foreign)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var script = new StorageScript(fixture, (4, 1));
        using var source = new PgVarlena<Value>(new Value(42));
        PgMemoryContext destination = foreign ? PgMemoryContext.FromId(29, 101) : PgMemoryContext.Create("disposed destination");
        if (!foreign)
        {
            destination.Dispose();
        }

        fixture.Requests.Clear();
        if (foreign)
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => new PgVarlena<Value>(new(7), destination));
            Assert.ThrowsExactly<InvalidOperationException>(() => source.Clone(destination));
        }
        else
        {
            Assert.ThrowsExactly<ObjectDisposedException>(() => new PgVarlena<Value>(new(7), destination));
            Assert.ThrowsExactly<ObjectDisposedException>(() => source.Clone(destination));
        }

        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.AllocateVarlena));
        Assert.AreEqual(42, source.Value.Number);
    }

    /// <summary>
    /// Borrowed getters preserve pointer identity and expose an independent copied value without allocating payloads.
    /// </summary>
    [TestMethod]
    public void BorrowedReadsPreserveInputWithoutAllocation()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var input = new StorageBlock(4, 1);
        input.SetNumber(42);
        using PgVarlena<Value> value = Borrow(input);
        fixture.Requests.Clear();
        Assert.IsTrue(value.IsBorrowed);
        Assert.AreEqual(input.Pointer, (nint)value.DangerousGetPointer());
        Value copied = value.Value;
        Assert.AreEqual(42, copied.Number);
        copied.Number = 7;
        Assert.AreEqual(7, copied.Number);
        Assert.AreEqual(42, value.Value.Number);
        Assert.AreEqual(42, input.Number);
        PgMemoryContext firstContext = value.Context;
        PgMemoryContext secondContext = value.Context;
        Assert.AreNotSame(firstContext, secondContext);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation is NativeMemoryOperation.AllocateVarlena or NativeMemoryOperation.Write));
        fixture.Requests.Clear();
        value.Dispose();
        value.Dispose();
        AssertDisposed(value);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// The first write creates one private owner in the source context and leaves another view and the input unchanged.
    /// </summary>
    [TestMethod]
    public void FirstMutationCopiesOnceAndPreservesSourceAliases()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var script = new StorageScript(fixture, (4, 1));
        using var input = new StorageBlock(4, 4);
        input.SetNumber(42);
        using PgVarlena<Value> first = Borrow(input);
        using PgVarlena<Value> second = Borrow(input);
        fixture.Current = 909;
        first.Value = new(7);
        Assert.IsFalse(first.IsBorrowed);
        Assert.IsTrue(second.IsBorrowed);
        Assert.AreEqual(7, first.Value.Number);
        Assert.AreEqual(42, second.Value.Number);
        Assert.AreEqual(42, input.Number);
        Assert.AreEqual(script.Blocks[0].Pointer, (nint)first.DangerousGetPointer());
        Assert.AreEqual(input.Pointer, (nint)second.DangerousGetPointer());
        first.Value = new(-9);
        Assert.AreEqual(-9, first.Value.Number);
        Assert.AreEqual(script.Blocks[0].Pointer, (nint)first.DangerousGetPointer());
        NativeMemoryRequest allocation = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.AllocateVarlena, fixture.Requests);
        Assert.AreEqual(101, allocation._context);
        Assert.AreEqual(101, first.Context.Id);
        Assert.AreEqual(42, input.Number);
    }

    /// <summary>
    /// Writable native temporaries update in place but never acquire individual native cleanup rights.
    /// </summary>
    [TestMethod]
    public void WritableInputMutatesInPlaceWithoutOwningCleanup()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var input = new StorageBlock(4, 4);
        input.SetNumber(42);
        using PgVarlena<Value> value = Borrow(input, writable: true);
        Assert.IsFalse(value.IsBorrowed);
        value.Value = new(7);
        Assert.AreEqual(input.Pointer, (nint)value.DangerousGetPointer());
        Assert.AreEqual(7, input.Number);
        Assert.AreEqual(7, value.Value.Number);
        value.Dispose();
        value.Dispose();
        AssertDisposed(value);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation is NativeMemoryOperation.AllocateVarlena or NativeMemoryOperation.Free or NativeMemoryOperation.Detach));
    }

    /// <summary>
    /// Both input states expire on callback exit before native access, including in a later callback at the same depth.
    /// </summary>
    /// <param name="writable">Whether native detoasting supplied a writable temporary.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExpiredInputCannotReviveOrReachNativeMemory(bool writable)
    {
        using var fixture = new MemoryContextTestFixture();
        using var input = new StorageBlock(4, 1);
        input.SetNumber(42);
        using PgVarlena<Value> value = BorrowInCallback();
        fixture.Requests.Clear();
        AssertDisposed(value);
        using (MemoryContextTestFixture.Enter())
        {
            AssertDisposed(value);
        }

        value.Dispose();
        value.Dispose();
        Assert.AreEqual(42, input.Number);
        Assert.IsEmpty(fixture.Requests);

        PgVarlena<Value> BorrowInCallback()
        {
            using (MemoryContextTestFixture.Enter())
            {
                return Borrow(input, writable);
            }
        }
    }

    /// <summary>
    /// Successful copy-on-write ends the input lease and leaves independent storage valid in a later callback.
    /// </summary>
    [TestMethod]
    public void PromotedBorrowSurvivesCallbackButNotSourceContextReset()
    {
        using var fixture = new MemoryContextTestFixture();
        using var script = new StorageScript(fixture, (4, 1));
        using var input = new StorageBlock(4, 4);
        input.SetNumber(42);
        PgVarlena<Value> value;
        using (MemoryContextTestFixture.Enter())
        {
            value = Borrow(input);
            value.Value = new(7);
        }

        using MemoryContextTestFixture.Scope next = MemoryContextTestFixture.Enter();
        Assert.AreEqual(7, value.Value.Number);
        Assert.IsFalse(value.IsBorrowed);
        Assert.AreEqual(42, input.Number);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Read
            ? throw new PgException("55000", "Source context reset.") : script.Respond(request);
        AssertDisposed(value);
        // codeql[cs/dispose-not-called-on-throw] The test disposes this at the step it checks; the fake native layer holds nothing an earlier failure could leak.
        value.Dispose();
    }

    /// <summary>
    /// Clones use the source context by default or an explicit destination, preserve all bytes and own separate lifetimes.
    /// </summary>
    [TestMethod]
    public void ClonesUseIndependentStorageAndSelectedContext()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var script = new StorageScript(fixture, (4, 1), (4, 4), (4, 1));
        using var original = new PgVarlena<Value>(new Value(42));
        fixture.Current = 909;
        using PgVarlena<Value> sameContext = original.Clone();
        using PgVarlena<Value> explicitContext = original.Clone(PgMemoryContext.FromId(17, 808));
        Assert.AreEqual(101, sameContext.Context.Id);
        Assert.AreEqual(808, explicitContext.Context.Id);
        Assert.AreNotEqual((nint)original.DangerousGetPointer(), (nint)sameContext.DangerousGetPointer());
        Assert.AreNotEqual((nint)sameContext.DangerousGetPointer(), (nint)explicitContext.DangerousGetPointer());
        original.Value = new(7);
        Assert.AreEqual(42, sameContext.Value.Number);
        Assert.AreEqual(42, explicitContext.Value.Number);
        original.Dispose();
        Assert.AreEqual(42, sameContext.Value.Number);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Read && request._context == script.Blocks[1].Pointer
            ? throw new PgException("55000", "Source context reset.") : script.Respond(request);
        AssertDisposed(sameContext);
        Assert.AreEqual(42, explicitContext.Value.Number);
        explicitContext.Value = new(-1);
        Assert.AreEqual(-1, explicitContext.Value.Number);
    }

    /// <summary>
    /// Explicit transfer consumes one owner without copying it and returns the header pointer under its context lifetime.
    /// </summary>
    [TestMethod]
    public void OwnedTransferConsumesAliasesWithoutFreeingStorage()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new OidScope();
        using var script = new StorageScript(fixture, (4, 1));
        using var value = new PgVarlena<Value>(new Value(42));
        PgVarlena<Value> alias = value;
        fixture.Requests.Clear();
        PgDatum result = value.IntoDatum();
        Assert.AreEqual(54321U, result.TypeOid);
        Assert.IsFalse(result.IsNull);
        Assert.AreEqual((nuint)script.Blocks[0].Pointer, result.DangerousGetBits());
        Assert.AreEqual(101, result.Lifetime.ContextId);
        Assert.AreEqual(42, script.Blocks[0].Number);
        AssertDisposed(alias);
        value.Dispose();
        Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Detach, fixture.Requests);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation is NativeMemoryOperation.AllocateVarlena or NativeMemoryOperation.Free));
        fixture.Handler = request => request._operation == NativeMemoryOperation.CaptureGeneration
            ? new NativeMemoryResult { _value = 902 } : script.Respond(request);
        Assert.ThrowsExactly<ObjectDisposedException>(() => result.DangerousGetBits());
    }

    /// <summary>
    /// Original inputs and writable detoast temporaries both copy before transfer, leaving native input cleanup untouched.
    /// </summary>
    /// <param name="writable">Whether the callback owns a writable temporary.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void InputTransferCopiesBeforeConsumingWrapper(bool writable)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new OidScope();
        using var script = new StorageScript(fixture, (4, 1));
        using var input = new StorageBlock(4, 4);
        input.SetNumber(42);
        using PgVarlena<Value> value = Borrow(input, writable);
        using PgVarlena<Value> other = Borrow(input, writable);
        PgDatum result = value.IntoDatum();
        Assert.AreEqual((nuint)script.Blocks[0].Pointer, result.DangerousGetBits());
        Assert.AreNotEqual((nuint)input.Pointer, result.DangerousGetBits());
        Assert.AreEqual(42, script.Blocks[0].Number);
        Assert.AreEqual(42, other.Value.Number);
        AssertDisposed(value);
        Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.AllocateVarlena, fixture.Requests);
        Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Detach, fixture.Requests);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Free));
    }

    /// <summary>
    /// Failed copy-on-write preserves the original lease and releases only a successfully allocated provisional owner.
    /// </summary>
    /// <param name="allocationFailure">Whether failure precedes allocating the destination.</param>
    /// <param name="cleanupFailure">Whether failed initialization also fails its cleanup.</param>
    [TestMethod]
    [DataRow(true, false)]
    [DataRow(false, false)]
    [DataRow(false, true)]
    public void FailedCopyOnWritePreservesBorrowAndBothErrors(bool allocationFailure, bool cleanupFailure)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var script = new StorageScript(fixture, (4, 1), (4, 1));
        using var input = new StorageBlock(4, 4);
        input.SetNumber(42);
        using PgVarlena<Value> value = Borrow(input);
        fixture.Handler = request => request._operation switch
        {
            NativeMemoryOperation.AllocateVarlena when allocationFailure => throw new PgException("53200", "Allocation failed."),
            NativeMemoryOperation.Write => throw new PgException("22023", "Initialization failed."),
            NativeMemoryOperation.Free when cleanupFailure => throw new PgException("55006", "Cleanup failed."),
            _ => script.Respond(request),
        };
        if (cleanupFailure)
        {
            AggregateException error = Assert.ThrowsExactly<AggregateException>(() => value.Value = new(7));
            Assert.HasCount(2, error.InnerExceptions);
            Assert.AreEqual("22023", Assert.IsInstanceOfType<PgException>(error.InnerExceptions[0]).SqlState);
            Assert.AreEqual("55006", Assert.IsInstanceOfType<PgException>(error.InnerExceptions[1]).SqlState);
        }
        else
        {
            Assert.AreEqual(allocationFailure ? "53200" : "22023", Assert.ThrowsExactly<PgException>(() => value.Value = new(7)).SqlState);
        }

        Assert.IsTrue(value.IsBorrowed);
        Assert.AreEqual(input.Pointer, (nint)value.DangerousGetPointer());
        Assert.AreEqual(42, value.Value.Number);
        Assert.AreEqual(42, input.Number);
        Assert.HasCount(allocationFailure ? 0 : 1, fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Free));
        fixture.Handler = script.Respond;
        value.Value = new(9);
        Assert.IsFalse(value.IsBorrowed);
        Assert.AreEqual(9, value.Value.Number);
        Assert.AreEqual(42, input.Number);
    }

    /// <summary>
    /// OID, lifetime and detach failures preserve an owned wrapper for retry without freeing or consuming it.
    /// </summary>
    /// <param name="phase">The native transition that fails.</param>
    [TestMethod]
    [DataRow("oid")]
    [DataRow("lifetime")]
    [DataRow("detach")]
    public void FailedOwnedTransferRetainsRetryRights(string phase)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new OidScope();
        using var script = new StorageScript(fixture, (4, 1));
        using var value = new PgVarlena<Value>(new Value(42));
        fixture.Requests.Clear();
        FailOidLookup(phase == "oid");
        fixture.Handler = request => (phase == "lifetime" && request._operation == NativeMemoryOperation.CaptureGeneration) ||
            (phase == "detach" && request._operation == NativeMemoryOperation.Detach)
            ? throw new PgException("55006", "Transfer failed.") : script.Respond(request);
        Assert.AreEqual("55006", Assert.ThrowsExactly<PgException>(value.IntoDatum).SqlState);
        Assert.AreEqual(42, value.Value.Number);
        Assert.AreEqual(script.Blocks[0].Pointer, (nint)value.DangerousGetPointer());
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Free));
        Assert.HasCount(phase == "detach" ? 1 : 0, fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Detach));
        FailOidLookup(false);
        fixture.Handler = script.Respond;
        PgDatum datum = value.IntoDatum();
        Assert.AreEqual((nuint)script.Blocks[0].Pointer, datum.DangerousGetBits());
        AssertDisposed(value);
    }

    /// <summary>
    /// A failed input detach frees the provisional copy and leaves the original callback view available for retry.
    /// </summary>
    /// <param name="writable">Whether the original input is writable.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FailedInputTransferReleasesOnlyProvisionalCopy(bool writable)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new OidScope();
        using var script = new StorageScript(fixture, (4, 1), (4, 1));
        using var input = new StorageBlock(4, 4);
        input.SetNumber(42);
        using PgVarlena<Value> value = Borrow(input, writable);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Detach
            ? throw new PgException("55006", "Detach failed.") : script.Respond(request);
        Assert.AreEqual("55006", Assert.ThrowsExactly<PgException>(value.IntoDatum).SqlState);
        NativeMemoryRequest free = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Free, fixture.Requests);
        Assert.AreEqual(script.Blocks[0].Pointer, free._context);
        Assert.AreEqual(input.Pointer, (nint)value.DangerousGetPointer());
        Assert.AreEqual(42, value.Value.Number);
        Assert.AreEqual(!writable, value.IsBorrowed);
        fixture.Handler = script.Respond;
        PgDatum datum = value.IntoDatum();
        Assert.AreEqual((nuint)script.Blocks[1].Pointer, datum.DangerousGetBits());
        Assert.AreEqual(42, input.Number);
    }

    /// <summary>
    /// A failed release preserves the live owner and retry succeeds exactly once, followed by idempotent disposal.
    /// </summary>
    [TestMethod]
    public void FailedDisposePreservesOwnerForRetry()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var script = new StorageScript(fixture, (4, 1));
        using var value = new PgVarlena<Value>(new Value(42));
        fixture.Handler = request => request._operation == NativeMemoryOperation.Free
            ? throw new PgException("55006", "Release failed.") : script.Respond(request);
        Assert.AreEqual("55006", Assert.ThrowsExactly<PgException>(value.Dispose).SqlState);
        Assert.AreEqual(42, value.Value.Number);
        value.Value = new(7);
        Assert.AreEqual(7, value.Value.Number);
        fixture.Handler = script.Respond;
        value.Dispose();
        fixture.Requests.Clear();
        value.Dispose();
        AssertDisposed(value);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Reset generations and deleted contexts reject every input operation before touching the still-live test buffer.
    /// </summary>
    /// <param name="deleted">Whether the native anchor was deleted rather than reset.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ExpiredContextRejectsBorrowBeforeDereference(bool deleted)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var input = new StorageBlock(4, 1);
        input.SetNumber(42);
        using PgVarlena<Value> value = Borrow(input);
        fixture.Requests.Clear();
        fixture.Handler = request => request._operation == NativeMemoryOperation.CaptureGeneration
            ? deleted ? throw new PgException("55000", "Deleted anchor.") : new NativeMemoryResult { _value = 902 }
            : fixture.Respond(request);
        AssertDisposed(value);
        Assert.AreEqual(42, input.Number);
        Assert.IsTrue(fixture.Requests.All(static request => request._operation == NativeMemoryOperation.CaptureGeneration));
    }

    /// <summary>
    /// Active handles reject foreign providers, missing capability and a matching provider on another thread before requests.
    /// </summary>
    /// <param name="owned">Whether the handle owns an allocation rather than borrowing an input.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ProviderAndOriginatingThreadAreRequired(bool owned)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var script = new StorageScript(fixture, (4, 1));
        using var input = new StorageBlock(4, 4);
        input.SetNumber(42);
        using PgVarlena<Value> value = owned ? new(new Value(42)) : Borrow(input);
        fixture.Requests.Clear();
        using (MemoryContextTestFixture.Enter(29))
        {
            AssertUnavailable(value);
        }

        nint previous = NativeMemoryContext.Enter(0);
        try
        {
            AssertUnavailable(value);
        }
        finally
        {
            NativeMemoryContext.Exit(previous);
        }

        DedicatedThread.Run(() =>
        {
            using var otherFixture = new MemoryContextTestFixture();
            using MemoryContextTestFixture.Scope otherScope = MemoryContextTestFixture.Enter();
            AssertUnavailable(value);
            Assert.ThrowsExactly<InvalidOperationException>(value.Dispose);
            Assert.IsEmpty(otherFixture.Requests);
        });

        Assert.IsEmpty(fixture.Requests);
        Assert.AreEqual(42, value.Value.Number);
    }

    /// <summary>
    /// Ordinary output copies the payload without consuming any owner or retaining later mutations.
    /// </summary>
    /// <param name="state">Owned, original-borrowed or writable-input storage.</param>
    [TestMethod]
    [DataRow("owned")]
    [DataRow("borrowed")]
    [DataRow("temporary")]
    public void OrdinaryOutputCopiesWithoutConsumingAliases(string state)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var script = new StorageScript(fixture, (4, 1));
        using var input = new StorageBlock(4, 4);
        input.SetNumber(42);
        using PgVarlena<Value> value = state == "owned" ? new(new Value(42)) : Borrow(input, state == "temporary");
        PgVarlena<Value> alias = value;
        NativeValue output = value.ToNative(54321);
        try
        {
            value.Value = new(7);
            Assert.AreEqual(7, alias.Value.Number);
            byte[] expected = Convert.FromHexString(BitConverter.IsLittleEndian ? "2A000000" : "0000002A");
            Assert.AreSequenceEqual(expected, output.ReadCustomPayload(54321).ToArray());
            Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Detach));
        }
        finally
        {
            output.Release();
        }
    }

    /// <summary>
    /// The native transport entry point preserves the original payload pointer and its writability without allocating.
    /// </summary>
    /// <param name="writable">Whether native detoasting owns a writable temporary.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeInputEnvelopeBorrowsExactPayload(bool writable)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new OidScope();
        using var input = new StorageBlock(4, 1);
        input.SetNumber(42);
        NativeValue envelope = InputEnvelope(input);
        Writability(ref envelope) = writable ? 1 : 0;
        using PgVarlena<Value> value = envelope.ReadVarlena<Value>();
        Assert.AreEqual(!writable, value.IsBorrowed);
        Assert.AreEqual(input.Pointer, (nint)value.DangerousGetPointer());
        Assert.AreEqual(42, value.Value.Number);
        input.SetNumber(7);
        Assert.AreEqual(7, value.Value.Number);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.AllocateVarlena));
    }

    /// <summary>
    /// Native wrapper input accepts only an exact complete payload, reporting malformed binary lengths independently.
    /// </summary>
    /// <param name="length">An invalid length adjacent to the four-byte payload.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(3)]
    [DataRow(5)]
    public void NativeInputEnvelopeRejectsNonexactLength(int length)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new OidScope();
        using var input = new StorageBlock(4, 1);
        NativeValue envelope = InputEnvelope(input);
        PayloadLength(ref envelope) = length;
        Assert.AreEqual("22P03", Assert.ThrowsExactly<PgException>(() => envelope.ReadVarlena<Value>()).SqlState);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Invalid identity, nullability, pointers and writability are rejected before acquiring any memory capability.
    /// </summary>
    /// <param name="invalidField">The independently invalid envelope field.</param>
    [TestMethod]
    [DataRow("header")]
    [DataRow("writable")]
    [DataRow("negative writable")]
    [DataRow("oid")]
    [DataRow("marker")]
    [DataRow("null")]
    [DataRow("data")]
    [DataRow("negative length")]
    public void NativeInputEnvelopeRejectsInvalidProvenance(string invalidField)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new OidScope();
        using var input = new StorageBlock(4, 1);
        input.SetNumber(42);
        NativeValue envelope = InputEnvelope(input);
        switch (invalidField)
        {
            case "header":
                envelope.Integral = 0;
                break;
            case "writable":
                Writability(ref envelope) = 2;
                break;
            case "negative writable":
                Writability(ref envelope) = -1;
                break;
            case "oid":
                TypeOid(ref envelope) = 54322;
                break;
            case "marker":
                Marker(ref envelope) = 0;
                break;
            case "null":
                envelope.IsNull = 1;
                break;
            case "data":
                Payload(ref envelope) = null;
                break;
            case "negative length":
                PayloadLength(ref envelope) = -1;
                break;
        }

        Assert.ThrowsExactly<InvalidOperationException>(() => envelope.ReadVarlena<Value>());
        Assert.AreEqual(42, input.Number);
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Vector conversion rejects shape loss before creating native elements or disposing existing wrapper aliases.
    /// </summary>
    /// <param name="wrappers">Whether the input already contains owned wrappers.</param>
    /// <param name="nonstandardBound">Whether only the lower bound differs rather than the rank.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void WrapperVectorShapeRejectionAllocatesNothing(bool wrappers, bool nonstandardBound)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var script = new StorageScript(fixture, (4, 1));
        using PgVarlena<Value>? existing = wrappers ? new(new Value(42)) : null;
        int[] lengths = nonstandardBound ? [1] : [1, 1];
        int[] bounds = nonstandardBound ? [0] : [1, 1];
        IPgArray source = wrappers ? new PgArray<PgVarlena<Value>>([existing!], (lengths, bounds)) :
            new PgArray<Value>([new(42)], (lengths, bounds));
        fixture.Requests.Clear();
        Assert.ThrowsExactly<InvalidOperationException>(() => PgTypeRegistry.RequireVarlena<Value>().Convert(source, typeof(PgVarlena<Value>[])));
        Assert.IsEmpty(fixture.Requests);
        Assert.AreSequenceEqual(lengths, source.Lengths.ToArray());
        Assert.AreSequenceEqual(bounds, source.LowerBounds.ToArray());
        if (wrappers)
        {
            Assert.IsNotNull(existing);
            Assert.AreSame(existing, source.GetElement(0));
            Assert.AreEqual(42, existing.Value.Number);
            Assert.AreEqual(script.Blocks[0].Pointer, (nint)existing.DangerousGetPointer());
        }
        else
        {
            Assert.AreEqual(new Value(42), source.GetElement(0));
        }
    }

    /// <summary>
    /// A later element allocation failure frees all earlier provisional wrappers, even when an earlier free also fails.
    /// </summary>
    /// <param name="cleanupFailure">Whether releasing the first provisional element fails.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PartialWrapperArrayFailureReleasesEveryNewOwner(bool cleanupFailure)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var script = new StorageScript(fixture, (4, 1), (4, 4));
        int allocationCount = 0;
        fixture.Handler = request => request._operation switch
        {
            NativeMemoryOperation.AllocateVarlena when ++allocationCount == 3 => throw new PgException("53200", "Third element allocation failed."),
            NativeMemoryOperation.Free when cleanupFailure && request._context == script.Blocks[0].Pointer => throw new PgException("55006", "First element release failed."),
            _ => script.Respond(request),
        };
        var source = new PgArray<Value>([new(42), new(7), new(-9)]);
        void convert() => PgTypeRegistry.RequireVarlena<Value>().Convert(source, typeof(PgVarlena<Value>[]));
        if (cleanupFailure)
        {
            AggregateException failure = Assert.ThrowsExactly<AggregateException>(convert);
            Assert.HasCount(2, failure.InnerExceptions);
            Assert.AreEqual("53200", Assert.IsInstanceOfType<PgException>(failure.InnerExceptions[0]).SqlState);
            Assert.AreEqual("55006", Assert.IsInstanceOfType<PgException>(failure.InnerExceptions[1]).SqlState);
        }
        else
        {
            Assert.AreEqual("53200", Assert.ThrowsExactly<PgException>(convert).SqlState);
        }

        Assert.AreEqual(3, allocationCount);
        Assert.AreEqual(42, script.Blocks[0].Number);
        Assert.AreEqual(7, script.Blocks[1].Number);
        Assert.AreSequenceEqual([new Value(42), new Value(7), new Value(-9)], source);
        nint[] freed = [.. fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Free).Select(static request => request._context)];
        Assert.AreSequenceEqual([script.Blocks[0].Pointer, script.Blocks[1].Pointer], freed);
    }

    /// <summary>
    /// Malformed later elements and trailing bytes release all decoded wrapper owners while preserving both failure causes.
    /// </summary>
    /// <param name="malformation">The independently modified array transport boundary.</param>
    /// <param name="cleanupFailure">Whether releasing the first decoded element also fails.</param>
    [TestMethod]
    [DataRow("truncated", false)]
    [DataRow("wrong size", false)]
    [DataRow("trailing", false)]
    [DataRow("trailing", true)]
    public void MalformedNativeArrayReleasesDecodedWrappers(string malformation, bool cleanupFailure)
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        using var backend = new OidScope();
        using var script = new StorageScript(fixture, (4, 1), (4, 4));
        byte[] first = Convert.FromHexString(BitConverter.IsLittleEndian ? "2A000000" : "0000002A");
        byte[] second = Convert.FromHexString(BitConverter.IsLittleEndian ? "07000000" : "00000007");
        byte[] elementHeader = Convert.FromHexString("0000000000000000FFFFFFF90000D432000000000000000000000004");
        byte[] payload = [.. Convert.FromHexString("00000001000000020000D4320000000200000001"), .. elementHeader, .. first, .. elementHeader, .. second];
        if (malformation == "trailing")
        {
            payload = [.. payload, 0xCC];
        }
        else
        {
            payload = payload[..^1];
            if (malformation == "wrong size")
            {
                payload[79] = 3;
            }
        }

        fixture.Handler = request => request._operation == NativeMemoryOperation.Free && cleanupFailure && request._context == script.Blocks[0].Pointer
            ? throw new PgException("55006", "First decoded wrapper release failed.") : script.Respond(request);
        NativeValue envelope = NativeValue.FromBytes(payload);
        Marker(ref envelope) = -1;
        TypeOid(ref envelope) = 3;
        try
        {
            void decode() => envelope.ReadArrayData<PgVarlena<ArrayValue>>(54322);
            if (cleanupFailure)
            {
                AggregateException error = Assert.ThrowsExactly<AggregateException>(decode);
                Assert.HasCount(2, error.InnerExceptions);
                Assert.IsInstanceOfType<InvalidOperationException>(error.InnerExceptions[0]);
                Assert.AreEqual("55006", Assert.IsInstanceOfType<PgException>(error.InnerExceptions[1]).SqlState);
            }
            else if (malformation == "wrong size")
            {
                Assert.AreEqual("22P03", Assert.ThrowsExactly<PgException>(decode).SqlState);
            }
            else
            {
                Assert.ThrowsExactly<InvalidOperationException>(decode);
            }

            Assert.AreEqual(42, script.Blocks[0].Number);
            nint[] freed = [.. fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Free).Select(static request => request._context)];
            if (malformation == "trailing")
            {
                Assert.AreEqual(7, script.Blocks[1].Number);
                Assert.AreSequenceEqual([script.Blocks[0].Pointer, script.Blocks[1].Pointer], freed);
            }
            else
            {
                Assert.AreSequenceEqual([script.Blocks[0].Pointer], freed);
            }
        }
        finally
        {
            envelope.Release();
        }
    }

    /// <summary>
    /// Builds the native callback envelope over independent live test storage without using a production encoder.
    /// </summary>
    private static NativeValue InputEnvelope(StorageBlock input)
    {
        var result = new NativeValue { Integral = input.Pointer };
        Marker(ref result) = -7;
        TypeOid(ref result) = 54321;
        Payload(ref result) = (byte*)input.Pointer + input.Offset;
        PayloadLength(ref result) = 4;
        return result;
    }

    /// <summary>
    /// Accesses the native-only custom type marker for controlled envelope fixtures.
    /// </summary>
    private static ref int Marker(ref NativeValue value) => ref value._auxiliary1;

    /// <summary>
    /// Accesses the exact input type OID for controlled envelope fixtures.
    /// </summary>
    private static ref int TypeOid(ref NativeValue value) => ref value._auxiliary2;

    /// <summary>
    /// Accesses the native detoast writability discriminator.
    /// </summary>
    private static ref int Writability(ref NativeValue value) => ref value._temporalInfinity;

    /// <summary>
    /// Accesses the native input payload pointer while its backing test buffer remains live.
    /// </summary>
    private static ref byte* Payload(ref NativeValue value) => ref value._data;

    /// <summary>
    /// Accesses the native input payload length for framing checks.
    /// </summary>
    private static ref int PayloadLength(ref NativeValue value) => ref value._length;

    /// <summary>
    /// Constructs a callback input over a live independent test buffer.
    /// </summary>
    private static PgVarlena<Value> Borrow(StorageBlock input, bool writable = false) =>
        new(input.Pointer, input.Pointer + input.Offset, writable, PgMemoryContext.Current, NativeMemoryContext.BorrowScope);

    /// <summary>
    /// Checks every pointer-bearing public operation after disposal or lifetime expiry.
    /// </summary>
    private static void AssertDisposed(PgVarlena<Value> value)
    {
        Assert.ThrowsExactly<ObjectDisposedException>(() => value.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(() => value.Value = new(7));
        Assert.ThrowsExactly<ObjectDisposedException>(() => value.IsBorrowed);
        Assert.ThrowsExactly<ObjectDisposedException>(() => value.Context);
        Assert.ThrowsExactly<ObjectDisposedException>(() => value.Clone());
        Assert.ThrowsExactly<ObjectDisposedException>(value.IntoDatum);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
        {
            _ = value.DangerousGetPointer();
        });
    }

    /// <summary>
    /// Checks capability and thread denial before any operation can touch native storage.
    /// </summary>
    private static void AssertUnavailable(PgVarlena<Value> value)
    {
        Assert.ThrowsExactly<InvalidOperationException>(() => value.Value);
        Assert.ThrowsExactly<InvalidOperationException>(() => value.Value = new(7));
        Assert.ThrowsExactly<InvalidOperationException>(() => value.IsBorrowed);
        Assert.ThrowsExactly<InvalidOperationException>(() => value.Context);
        Assert.ThrowsExactly<InvalidOperationException>(() => value.Clone());
        Assert.ThrowsExactly<InvalidOperationException>(value.IntoDatum);
        Assert.ThrowsExactly<InvalidOperationException>(() =>
        {
            _ = value.DangerousGetPointer();
        });
    }

    /// <summary>
    /// Supplies only the guarded type lookup used by explicit ownership transfer.
    /// </summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int ResolveOid(NativeSpiRequest* request, NativeSpiResult* result, NativeCallError* error)
    {
        if (s_failOid || request->_operation != SpiOperation.CustomType)
        {
            NativeError.Write(new PgException("55006", "Type lookup failed."), error);
            return 1;
        }

        result->_text = new NativeValue { Integral = request->_parameters[0]._value.ReadString() == "ownership_array_value" ? 54322 : 54321 };
        return 0;
    }

    /// <summary>
    /// Restores the backend callback and failure script after a transfer test.
    /// </summary>
    private sealed class OidScope : IDisposable
    {
        private readonly nint _previous = NativeBackend.Enter((nint)(delegate* unmanaged[Cdecl]<NativeSpiRequest*, NativeSpiResult*, NativeCallError*, int>)&ResolveOid);
        private readonly bool _previousFailure = s_failOid;

        /// <inheritdoc />
        public void Dispose()
        {
            FailOidLookup(_previousFailure);
            NativeBackend.Exit(_previous);
        }
    }

    /// <summary>
    /// Supplies prepared buffers and copy responses without simulating PostgreSQL allocator lifetime.
    /// </summary>
    private sealed class StorageScript : IDisposable
    {
        private readonly MemoryContextTestFixture _fixture;
        private readonly Dictionary<nint, nint> _owners = [];
        private int _next;

        /// <summary>
        /// Prepares all live buffers independently of native allocation requests.
        /// </summary>
        internal StorageScript(MemoryContextTestFixture fixture, params (int Size, int Offset)[] layouts)
        {
            _fixture = fixture;
            Blocks = [.. layouts.Select(static layout => new StorageBlock(layout.Size, layout.Offset))];
            fixture.Handler = Respond;
        }

        /// <summary>
        /// Gets the prepared response buffers, whose physical lifetime lasts through the test.
        /// </summary>
        internal StorageBlock[] Blocks { get; }

        /// <summary>
        /// Responds to each operation over the independently prepared storage.
        /// </summary>
        internal NativeMemoryResult Respond(NativeMemoryRequest request)
        {
            if (request._operation == NativeMemoryOperation.AllocateVarlena)
            {
                StorageBlock block = Blocks[_next++];
                _owners.Add(block.Pointer, request._context);
                return new NativeMemoryResult { _pointer = block.Pointer, _length = (nuint)block.Bytes.Length, _value = block.Offset };
            }

            if (request._operation == NativeMemoryOperation.Owner)
            {
                return new NativeMemoryResult { _context = _owners[request._context] };
            }

            if (request._operation is NativeMemoryOperation.Read or NativeMemoryOperation.Write or NativeMemoryOperation.Detach)
            {
                StorageBlock block = Blocks.Single(block => block.Pointer == request._context);
                int length = checked((int)request._length);
                if (length != 0)
                {
                    Span<byte> bytes = block.Bytes.Slice(checked((int)request._value), length);
                    Span<byte> transfer = new((void*)request._data, length);
                    if (request._operation == NativeMemoryOperation.Read)
                    {
                        bytes.CopyTo(transfer);
                    }
                    else
                    {
                        transfer.CopyTo(bytes);
                    }
                }

                return new NativeMemoryResult { _pointer = block.Pointer };
            }

            return _fixture.Respond(request);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            foreach (StorageBlock block in Blocks)
            {
                block.Dispose();
            }
        }
    }

    /// <summary>
    /// Keeps deliberately expired test addresses physically valid until all assertions finish.
    /// </summary>
    private sealed class StorageBlock(int size, int offset) : IDisposable
    {
        private readonly nint _pointer = Allocate(size);

        /// <summary>
        /// Gets the live header address.
        /// </summary>
        internal nint Pointer => _pointer;

        /// <summary>
        /// Gets the independently selected header width.
        /// </summary>
        internal int Offset { get; } = offset;

        /// <summary>
        /// Gets the complete test buffer, including spare header capacity.
        /// </summary>
        internal Span<byte> Bytes => new((void*)_pointer, size + 4);

        /// <summary>
        /// Gets the independent four-byte test payload.
        /// </summary>
        internal int Number => BitConverter.ToInt32(Bytes.Slice(Offset, 4));

        /// <summary>
        /// Initializes the input fixture without using the production wrapper or codec.
        /// </summary>
        internal void SetNumber(int value) => BitConverter.GetBytes(value).CopyTo(Bytes[Offset..]);

        /// <inheritdoc />
        public void Dispose() => NativeMemory.Free((void*)_pointer);

        /// <summary>
        /// Initializes header and unused capacity with a sentinel distinct from a default payload.
        /// </summary>
        private static nint Allocate(int length)
        {
            void* pointer = NativeMemory.Alloc((nuint)(length + 4));
            new Span<byte>(pointer, length + 4).Fill(0xCC);
            return (nint)pointer;
        }
    }

    /// <summary>
    /// Carries a mutable copied value for alias-isolation assertions.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private record struct Value(int Number);

    /// <summary>
    /// Makes accidental invocation of a value constructor observable.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct ConstructedValue(int Number)
    {
        /// <summary>
        /// Produces a nondefault value only when explicitly invoked.
        /// </summary>
        public ConstructedValue() : this(99)
        {
        }
    }

    /// <summary>
    /// Supplies a payload immediately below the short-header limit.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Payload125
    {
        /// <summary>
        /// Occupies the complete packed representation.
        /// </summary>
        internal fixed byte _data[125];
    }

    /// <summary>
    /// Supplies a payload exactly at the short-header limit.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Payload126
    {
        /// <summary>
        /// Occupies the complete packed representation.
        /// </summary>
        internal fixed byte _data[126];
    }

    /// <summary>
    /// Supplies a payload immediately above the short-header limit.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private struct Payload127
    {
        /// <summary>
        /// Occupies the complete packed representation.
        /// </summary>
        internal fixed byte _data[127];
    }

    /// <summary>
    /// Supplies a registered native mapping with an intentionally incorrect generated size.
    /// </summary>
    private readonly record struct WrongSize(int Number);

    /// <summary>
    /// Supplies a separate exact native codec for decoding independent array transport fixtures.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct ArrayValue(int Number);

    /// <summary>
    /// Supplies a custom mapping whose storage is not native layout.
    /// </summary>
    private readonly record struct NonNative(int Number);

    /// <summary>
    /// Supplies an unmanaged type with no PostgreSQL mapping.
    /// </summary>
    private readonly record struct Unregistered(int Number);
}
