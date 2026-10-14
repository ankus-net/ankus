using System.Runtime.CompilerServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies context-owned unmanaged values, checked aliases, cloning, and collection without native release.
/// </summary>
[TestClass]
public sealed unsafe class ContextValueTests
{
    /// <summary>
    /// Context-owned values and their aliases exchange copied values without granting individual free rights.
    /// </summary>
    [TestMethod]
    public void ContextOwnedValueSharesCheckedAccessAndRetainsNativePolicy()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] storage = new byte[sizeof(int)];
        fixture.Handler = request => fixture.RespondWithStorage(request, storage);
        PgContextValue<int> value = PgMemoryContext.Current.CreateContextValue(5, PgAllocationOptions.Huge, 64);
        PgNativeReference<int> reference = value.Borrow();
        Assert.AreEqual(5, value.Value);
        reference.Value = 17;
        Assert.AreEqual(17, value.Value);
        value.Value = -9;
        Assert.AreEqual(-9, reference.Value);
        Assert.AreEqual(303, value.Context.Id);
        Assert.AreEqual(PgAllocationOptions.Huge, value.Options);
        Assert.AreEqual((nuint)64, value.Alignment);
        Assert.AreEqual(701, (nint)value.DangerousGetPointer());
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Free));
    }

    /// <summary>
    /// Context-value no-OOM factories distinguish NULL from initialized success and preserve other native errors.
    /// </summary>
    [TestMethod]
    public void ContextValueTryFactoryPreservesNullSuccessAndErrorPartitions()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        byte[] storage = new byte[sizeof(int)];
        fixture.Handler = request => request._operation == NativeMemoryOperation.Allocate ? default : fixture.Respond(request);
        Assert.IsNull(context.TryCreateContextValue(5));
        Assert.AreEqual(2, Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Allocate, fixture.Requests)._flags);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation is NativeMemoryOperation.Write or NativeMemoryOperation.Free));
        fixture.Handler = request => fixture.RespondWithStorage(request, storage);
        PgContextValue<int>? value = context.TryCreateContextValue(5, PgAllocationOptions.Huge, 32);
        Assert.IsNotNull(value);
        Assert.AreEqual(5, value.Value);
        Assert.AreEqual(PgAllocationOptions.Huge, value.Options);
        Assert.AreEqual((nuint)32, value.Alignment);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Allocate
            ? throw new PgException("22023", "allocation rejected")
            : fixture.Respond(request);
        PgException error = Assert.ThrowsExactly<PgException>(() => context.TryCreateContextValue(7));
        Assert.AreEqual("22023", error.SqlState);
        Assert.AreEqual("allocation rejected", error.Message);
    }

    /// <summary>
    /// Initialization errors for context-owned factories still release the temporary individually owned allocation.
    /// </summary>
    [TestMethod]
    public void FailedContextValueInitializationDoesNotAbandonTemporaryAllocation()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Handler = request => request._operation == NativeMemoryOperation.Write
            ? throw new PgException("22023", "context initialization failed")
            : fixture.Respond(request);
        PgException error = Assert.ThrowsExactly<PgException>(() => context.CreateContextValue(5));
        Assert.AreEqual("22023", error.SqlState);
        Assert.AreEqual("context initialization failed", error.Message);
        Assert.AreEqual(501, Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Free, fixture.Requests)._context);
        Assert.AreEqual(1, fixture.ErrorReleases);
    }

    /// <summary>
    /// Context adoption rejects null locally and registers the exact non-null native representation without freeing it.
    /// </summary>
    [TestMethod]
    public void ContextValueAdoptionRequiresNonNullAndRetainsNativeContextOwnership()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgMemoryContext context = PgMemoryContext.Current;
        fixture.Requests.Clear();
        ArgumentNullException error = Assert.ThrowsExactly<ArgumentNullException>(() => context.DangerousAdoptContextValue<int>(null));
        Assert.AreEqual("address", error.ParamName);
        Assert.IsEmpty(fixture.Requests);
        PgContextValue<int> adopted = context.DangerousAdoptContextValue<int>((void*)701, huge: true, alignment: 64);
        Assert.AreEqual(PgAllocationOptions.Huge, adopted.Options);
        Assert.AreEqual((nuint)64, adopted.Alignment);
        NativeMemoryRequest request = Assert.ContainsSingle(static request => request._operation == NativeMemoryOperation.Adopt, fixture.Requests);
        Assert.AreEqual(701, request._pointer);
        Assert.AreEqual((nuint)sizeof(int), request._length);
        Assert.AreEqual(4, request._flags);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Free));
    }

    /// <summary>
    /// Raw detachment consumes context-owned checked tracking and invalidates views without issuing an individual free.
    /// </summary>
    [TestMethod]
    public void ContextValueDetachInvalidatesAllViewsWithoutFreeingStorage()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgContextValue<int> value = PgMemoryContext.Current.CreateContextValue(5);
        PgNativeReference<int> borrow = value.Borrow();
        fixture.Requests.Clear();
        Assert.AreEqual(701, (nint)value.DangerousDetach());
        Assert.AreEqual(NativeMemoryOperation.Detach, Assert.ContainsSingle(fixture.Requests)._operation);
        fixture.Requests.Clear();
        Assert.ThrowsExactly<ObjectDisposedException>(() => value.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(() => borrow.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(value.Borrow);
        Assert.ThrowsExactly<ObjectDisposedException>(() =>
        {
            value.DangerousGetPointer();
        });
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Native reset invalidates context-owned values and aliases while unrelated native errors retain their diagnostic class.
    /// </summary>
    [TestMethod]
    public void ContextValueAccessDistinguishesStaleStorageFromOperationalErrors()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        PgContextValue<int> value = PgMemoryContext.Current.CreateContextValue(5);
        PgNativeReference<int> borrow = value.Borrow();
        fixture.Handler = request => request._operation == NativeMemoryOperation.Read
            ? throw new PgException("55000", "native reset")
            : fixture.Respond(request);
        Assert.ThrowsExactly<ObjectDisposedException>(() => value.Value);
        Assert.ThrowsExactly<ObjectDisposedException>(() => borrow.Value);
        fixture.Handler = request => request._operation == NativeMemoryOperation.Read
            ? throw new PgException("55006", "cleanup owner is protected")
            : fixture.Respond(request);
        PgException error = Assert.ThrowsExactly<PgException>(() => value.Value);
        Assert.AreEqual("55006", error.SqlState);
        Assert.AreEqual("cleanup owner is protected", error.Message);
        Assert.AreEqual(3, fixture.ErrorReleases);
    }

    /// <summary>
    /// Context clones default to the current target rather than the source owner and remain independent after source transfer.
    /// </summary>
    [TestMethod]
    public void ContextValueClonesUseCurrentContextAndIndependentDefaultPolicyStorage()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        byte[] source = BitConverter.GetBytes(5);
        byte[] cloneStorage = new byte[sizeof(int)];
        int allocations = 0;
        fixture.Handler = request =>
        {
            if (request._operation == NativeMemoryOperation.Allocate)
            {
                return new NativeMemoryResult { _pointer = ++allocations == 1 ? 501 : 502, _length = request._length };
            }

            return fixture.RespondWithStorage(request, request._context == 502 ? cloneStorage : source);
        };
        PgContextValue<int> value = PgMemoryContext.Current.CreateContextValue(5, PgAllocationOptions.Huge, 64);
        fixture.Current = 909;
        PgContextValue<int> clone = value.CloneInto();
        Assert.AreEqual(5, clone.Value);
        Assert.AreEqual(PgAllocationOptions.None, clone.Options);
        Assert.AreEqual((nuint)0, clone.Alignment);
        Assert.AreEqual(909, fixture.Requests.Last(static request => request._operation == NativeMemoryOperation.Allocate)._context);
        clone.Value = 17;
        Assert.AreEqual(5, value.Value);
        Assert.AreEqual(17, clone.Value);
        value.DangerousDetach();
        Assert.AreEqual(17, clone.Value);
        Assert.IsEmpty(fixture.Requests.Where(static request => request._operation == NativeMemoryOperation.Free));
    }

    /// <summary>
    /// Collecting context-owned wrappers and their borrowed views never sends native release requests.
    /// </summary>
    [TestMethod]
    public void CollectingContextValueAndBorrowDoesNotFreeNativeBytes()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        (WeakReference<PgContextValue<int>> value, WeakReference<PgNativeReference<int>> borrow) = CreateUnreferenced(PgMemoryContext.Current);
        fixture.Requests.Clear();
        GarbageCollection.Collect();
        Assert.IsFalse(IsAlive(value));
        Assert.IsFalse(IsAlive(borrow));
        Assert.IsEmpty(fixture.Requests);
    }

    /// <summary>
    /// Leaves native ownership intact while moving all managed strong references outside the collecting caller's stack.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (WeakReference<PgContextValue<int>> Value, WeakReference<PgNativeReference<int>> Borrow) CreateUnreferenced(PgMemoryContext context)
    {
        PgContextValue<int> value = context.CreateContextValue(5);
        PgNativeReference<int> borrow = value.Borrow();
        return (new WeakReference<PgContextValue<int>>(value), new WeakReference<PgNativeReference<int>>(borrow));
    }

    /// <summary>
    /// Observes reachability without retaining a strong reference in the collecting test frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static bool IsAlive<T>(WeakReference<T> reference) where T : class => reference.TryGetTarget(out _);
}
