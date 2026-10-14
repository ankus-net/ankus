using System.Collections;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies managed list contracts independently of PostgreSQL's allocator implementation.
/// </summary>
[TestClass]
public sealed unsafe class PgListTests
{
    /// <summary>
    /// NIL needs no native capability and remains distinct from a null managed list.
    /// </summary>
    [TestMethod]
    public void DefaultNilIsBackendIndependent()
    {
        using var list = new PgList<int>();
        Assert.AreEqual(0, list.Count);
        Assert.AreEqual(0, list.Capacity);
        Assert.IsTrue(list.IsEmpty);
        Assert.IsFalse(list.IsReadOnly);
        Assert.IsNull(list.LifetimeContext);
        Assert.IsTrue(list.DangerousGetPointer() == null);
        Assert.IsTrue(list.DangerousGetCellsPointer() == null);
        Assert.IsFalse(list.TryAdd(42));
        Assert.IsFalse(list.TryReserve(0));
        Assert.IsFalse(list.TryReserve(100));
        Assert.IsFalse(list.TryGetFirst(out int value));
        Assert.AreEqual(0, value);
        Assert.IsFalse(list.TryGetLast(out _));
        Assert.IsFalse(list.TryPop(out _));
        Assert.IsFalse(list.Contains(0));
        Assert.AreEqual(-1, list.IndexOf(0));
        Assert.IsFalse(list.Remove(0));
        list.Clear();
        list.AddRange([]);
        Assert.IsEmpty(list.ToArray());
        Assert.IsEmpty(list.Drain(0, 0));
        list.CopyTo([]);
        using IEnumerator<int> iterator = list.GetEnumerator();
        Assert.IsFalse(iterator.MoveNext());
        list.Dispose();
        list.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = list.Count);
        Assert.ThrowsExactly<ObjectDisposedException>(() => list.TryAdd(1));
        Assert.ThrowsExactly<ObjectDisposedException>(list.Clear);
        Assert.ThrowsExactly<ObjectDisposedException>(() => list.DangerousDetach());
        using var detached = new PgList<int>();
        Assert.IsTrue(detached.DangerousDetach() == null);
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = detached.Count);
    }

    /// <summary>
    /// Arbitrary unmanaged values do not silently become native pointers or truncated integers.
    /// </summary>
    [TestMethod]
    public void UnsupportedCellTypesAreRejected()
    {
        Assert.ThrowsExactly<NotSupportedException>(() => new PgList<long>());
        Assert.ThrowsExactly<NotSupportedException>(() => new PgList<float>());
        Assert.ThrowsExactly<NotSupportedException>(() => new PgList<bool>());
        Assert.ThrowsExactly<NotSupportedException>(() => new PgList<Guid>());
        Assert.ThrowsExactly<NotSupportedException>(() => PgList.Create<long>());
        using var xid = new PgList<PgTransactionId>();
        Assert.IsEmpty(xid);
    }

    /// <summary>
    /// The complete boundary bit patterns survive both directions of the public list envelope.
    /// </summary>
    [TestMethod]
    public void CellBitsAreExact()
    {
        CheckCells<int>([int.MinValue, -1, 0, int.MaxValue], [2147483648, 4294967295, 0, 2147483647], 2);
        CheckCells<uint>([0, 2147483648, uint.MaxValue], [0, 2147483648, 4294967295], 3);
        CheckCells<PgTransactionId>([new(0), new(2147483648), new(uint.MaxValue)], [0, 2147483648, 4294967295], 4);
        CheckCells<nint>([0, 1, -1, nint.MinValue], [0, 1, nuint.MaxValue, (nuint)nint.MinValue], 1);
        Assert.ThrowsExactly<OverflowException>(() => NativeListValues.FromBits<int>(4294967296));
        Assert.ThrowsExactly<OverflowException>(() => NativeListValues.FromBits<uint>(4294967296));
        Assert.ThrowsExactly<OverflowException>(() => NativeListValues.FromBits<PgTransactionId>(4294967296));
    }

    /// <summary>
    /// The first allocation captures the current owner, while no-allocation attempts leave NIL unbound.
    /// </summary>
    [TestMethod]
    public void NilAndCapacityOperations()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request => Respond(fixture, request);
        using var list = new PgList<int>();
        Assert.IsFalse(list.TryAdd(7));
        Assert.IsFalse(list.TryReserve(10));
        Assert.IsEmpty(fixture.Requests);
        list.Add(7);
        Assert.AreEqual(101, list.LifetimeContext!.Id);
        NativeMemoryRequest create = fixture.Requests.Single(request => Is(request, NativeListOperation.Create));
        Assert.AreEqual(101, create._context);
        Assert.AreEqual((nuint)0, create._length);
        Assert.AreEqual((nuint)2, create._alignment);
        Assert.IsTrue(list.TryAdd(8));
        Assert.IsTrue(list.TryReserve(11));
        NativeMemoryRequest reserve = fixture.Requests[^1];
        Assert.IsTrue(Is(reserve, NativeListOperation.Reserve));
        Assert.AreEqual((nuint)11, reserve._length);
        Assert.AreEqual(701, reserve._context);
        Assert.HasCount(1, fixture.Requests.Where(request => Is(request, NativeListOperation.Create)));
    }

    /// <summary>
    /// Copies use exact ranges, return independent storage, and encode mutations without changing their order.
    /// </summary>
    [TestMethod]
    public void CopiesAndRanges()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        ulong[] input = [4294967295, 0, 42];
        var mutations = new List<(NativeListOperation Operation, int Index, ulong[] Values)>();
        fixture.Handler = request =>
        {
            if (Is(request, NativeListOperation.Read) || Is(request, NativeListOperation.Drain))
            {
                input.AsSpan((int)request._value, (int)request._length).CopyTo(new Span<ulong>((void*)request._data, (int)request._length));
            }
            else if (request._operation == NativeMemoryOperation.List && request._flags is 5 or 6 or 9)
            {
                mutations.Add(((NativeListOperation)request._flags, (int)request._value, new ReadOnlySpan<ulong>((void*)request._data, (int)request._length).ToArray()));
            }

            return Respond(fixture, request);
        };
        using PgList<int> list = PgList.Create<int>();
        int[] copy = [.. list];
        Assert.AreSequenceEqual<int>([-1, 0, 42], copy);
        copy[0] = 17;
        Assert.AreEqual(-1, list[0]);
        Assert.AreEqual("-1,0,42", string.Join(',', list.ToArray()));
        int[] slice = new int[2];
        list.CopyTo(slice.AsSpan(), 1);
        Assert.AreSequenceEqual<int>([0, 42], slice);
        int[] destination = [99, 99, 99, 99, 99];
        list.CopyTo(destination, 1);
        Assert.AreSequenceEqual<int>([99, -1, 0, 42, 99], destination);
        Assert.AreSequenceEqual<int>([0, 42], list.Drain(1, 2));
        Assert.IsTrue(list.TryGetFirst(out int first));
        Assert.AreEqual(-1, first);
        Assert.IsTrue(list.TryGetLast(out int last));
        Assert.AreEqual(42, last);
        Assert.IsTrue(list.TryPop(out int popped));
        Assert.AreEqual(42, popped);
        Assert.IsFalse(list.TryGet(-1, out _));
        Assert.IsFalse(list.TryGet(3, out _));
        Assert.AreEqual(2, list.IndexOf(42));
        Assert.IsTrue(list.Contains(0));
        Assert.IsFalse(list.Contains(99));
        list[1] = int.MinValue;
        list.Insert(3, -7);
        list.AddRange([19, -2]);
        Assert.HasCount(3, mutations);
        Assert.AreEqual(NativeListOperation.Write, mutations[0].Operation);
        Assert.AreEqual(1, mutations[0].Index);
        Assert.AreSequenceEqual<ulong>([2147483648], mutations[0].Values);
        Assert.AreEqual(NativeListOperation.Insert, mutations[1].Operation);
        Assert.AreEqual(3, mutations[1].Index);
        Assert.AreSequenceEqual<ulong>([4294967289], mutations[1].Values);
        Assert.AreEqual(NativeListOperation.Add, mutations[2].Operation);
        Assert.AreSequenceEqual<ulong>([19, 4294967294], mutations[2].Values);
        Assert.IsTrue(list.Remove(42));
        Assert.AreEqual(2, fixture.Requests[^1]._value);
        Assert.AreEqual((nuint)1, fixture.Requests[^1]._length);
        Assert.IsFalse(list.Remove(99));
    }

    /// <summary>
    /// Invalid ranges and lengths are rejected before any native mutation or implicit context binding.
    /// </summary>
    [TestMethod]
    public void InvalidRangesDoNotBindNil()
    {
        using var list = new PgList<int>();
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = list[-1]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => _ = list[0]);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list[0] = 1);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list.Insert(1, 7));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list.RemoveAt(0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list.Drain(-1, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list.Drain(0, -1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list.Drain(int.MaxValue, int.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list.TryReserve(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list.CopyTo([], 1));
        Assert.ThrowsExactly<ArgumentNullException>(() => list.CopyTo(null!, 0));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => list.CopyTo([], -1));
        Assert.ThrowsExactly<ArgumentException>(() => list.CopyTo(Array.Empty<int>(), 1));
        Assert.IsNull(list.LifetimeContext);
    }

    /// <summary>
    /// Enumeration observes start/end state, mutation, reset limitations and early owning disposal.
    /// </summary>
    [TestMethod]
    public void EnumeratorsCheckState()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        fixture.Handler = request =>
        {
            if (Is(request, NativeListOperation.Read))
            {
                *(ulong*)request._data = (ulong)(10 + request._value);
            }

            return Respond(fixture, request);
        };
        using PgList<int> list = PgList.Create<int>();
        using IEnumerator<int> iterator = list.GetEnumerator();
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = iterator.Current);
        Assert.IsTrue(iterator.MoveNext());
        Assert.AreEqual(10, iterator.Current);
        Assert.AreEqual(10, ((IEnumerator)iterator).Current);
        Assert.ThrowsExactly<NotSupportedException>(iterator.Reset);
        list[0] = 20;
        Assert.ThrowsExactly<InvalidOperationException>(() => iterator.MoveNext());
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = iterator.Current);
        iterator.Dispose();
        Assert.ThrowsExactly<ObjectDisposedException>(() => iterator.MoveNext());
        Assert.AreEqual(3, list.Count);
        using IEnumerator<int> complete = list.GetEnumerator();
        var result = new List<int>();
        while (complete.MoveNext())
        {
            result.Add(complete.Current);
        }

        Assert.AreSequenceEqual<int>([10, 11, 12], result);
        Assert.IsFalse(complete.MoveNext());
        Assert.ThrowsExactly<InvalidOperationException>(() => _ = complete.Current);
        list[0] = 21;
        Assert.ThrowsExactly<InvalidOperationException>(() => complete.MoveNext());
        using IEnumerator<int> consuming = list.GetConsumingEnumerator();
        Assert.ThrowsExactly<ObjectDisposedException>(() => _ = list.Count);
        Assert.IsTrue(consuming.MoveNext());
        Assert.AreEqual(10, consuming.Current);
        consuming.Dispose();
        consuming.Dispose();
        Assert.HasCount(1, fixture.Requests.Where(request => Is(request, NativeListOperation.Dispose)));
    }

    /// <summary>
    /// Borrowing transports NIL and raw pointers with exact type/owner, and mismatch never acquires a handle.
    /// </summary>
    [TestMethod]
    public void BorrowAndDetachPreserveNativeIdentity()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        bool reject = false;
        fixture.Handler = request => reject && Is(request, NativeListOperation.Borrow) ? default : Respond(fixture, request);
        PgMemoryContext owner = PgMemoryContext.Current;
        using PgList<uint> list = PgList.DangerousBorrow<uint>((void*)999, owner);
        NativeMemoryRequest borrow = fixture.Requests.Single(request => Is(request, NativeListOperation.Borrow));
        Assert.AreEqual(999, borrow._pointer);
        Assert.AreEqual(101, borrow._context);
        Assert.AreEqual((nuint)3, borrow._alignment);
        Assert.AreEqual(333, (nint)list.DangerousGetPointer());
        Assert.AreEqual(444, (nint)list.DangerousGetCellsPointer());
        Assert.AreEqual(333, (nint)list.DangerousDetach());
        list.Dispose();
        Assert.DoesNotContain(request => Is(request, NativeListOperation.Dispose), fixture.Requests);
        Assert.ThrowsExactly<ObjectDisposedException>(list.ToArray);
        using PgList<int> nil = PgList.DangerousBorrow<int>(null, owner);
        Assert.AreEqual(0, fixture.Requests.Last(request => Is(request, NativeListOperation.Borrow))._pointer);
        reject = true;
        Assert.IsFalse(PgList.DangerousTryBorrow<int>((void*)999, owner, out PgList<int>? mismatch));
        Assert.IsNull(mismatch);
        Assert.ThrowsExactly<ArgumentException>(() => PgList.DangerousBorrow<int>((void*)999, owner));
        Assert.ThrowsExactly<ArgumentNullException>(() => PgList.DangerousBorrow<int>(null, null!));
    }

    /// <summary>
    /// Provider changes and stale native identities fail before exposing storage, and native diagnostics are released.
    /// </summary>
    [TestMethod]
    public void LifetimeAndProviderChecks()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        bool stale = false;
        fixture.Handler = request => stale && request._operation == NativeMemoryOperation.List && !Is(request, NativeListOperation.Dispose)
            ? throw new PgException("55000", "reclaimed", "owned detail", "owned hint") : Respond(fixture, request);
        using PgList<int> list = PgList.Create<int>();
        using (MemoryContextTestFixture.Enter(99))
        {
            int requests = fixture.Requests.Count;
            Assert.ThrowsExactly<InvalidOperationException>(() => _ = list.Count);
            Assert.ThrowsExactly<InvalidOperationException>(list.Dispose);
            Assert.HasCount(requests, fixture.Requests);
        }

        stale = true;
        Assert.ThrowsExactly<ObjectDisposedException>(() => list.CopyTo([]));
        Assert.ThrowsExactly<ObjectDisposedException>(() => list.TryReserve(0));
        Assert.IsGreaterThan(0, fixture.ErrorReleases);
        list.Dispose();
        Assert.IsTrue(Is(fixture.Requests[^1], NativeListOperation.Dispose));
    }

    /// <summary>
    /// Failed mutation, disposal and detach leave ownership live until a successful retry.
    /// </summary>
    [TestMethod]
    public void NativeFailuresRetainOwnershipForRetry()
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        NativeListOperation? failure = null;
        fixture.Handler = request => failure is { } operation && Is(request, operation)
            ? throw new PgException("53200", "allocation failed", "native detail", "retry") : Respond(fixture, request);
        using PgList<int> list = PgList.Create<int>();
        foreach (NativeListOperation operation in new[] { NativeListOperation.Add, NativeListOperation.Dispose, NativeListOperation.Detach })
        {
            failure = operation;
            PgException error = Assert.ThrowsExactly<PgException>(() =>
            {
                if (operation == NativeListOperation.Add)
                {
                    list.Add(7);
                }
                else if (operation == NativeListOperation.Dispose)
                {
                    list.Dispose();
                }
                else
                {
                    list.DangerousDetach();
                }
            });
            Assert.AreEqual("53200", error.SqlState);
            Assert.AreEqual("native detail", error.Detail);
            Assert.AreEqual("retry", error.Hint);
            Assert.AreEqual(3, list.Count);
        }

        failure = null;
        list.Add(7);
        Assert.AreEqual(333, (nint)list.DangerousDetach());
        Assert.IsGreaterThan(0, fixture.ErrorReleases);
    }

    /// <summary>
    /// Runs public acquisition and reads against independently supplied exact wire bits.
    /// </summary>
    private static void CheckCells<T>(T[] values, ulong[] expected, int kind) where T : unmanaged
    {
        using var fixture = new MemoryContextTestFixture();
        using MemoryContextTestFixture.Scope scope = MemoryContextTestFixture.Enter();
        ulong[]? captured = null;
        fixture.Handler = request =>
        {
            if (Is(request, NativeListOperation.Create))
            {
                Assert.AreEqual((nuint)kind, request._alignment);
                captured = new ReadOnlySpan<ulong>((void*)request._data, (int)request._length).ToArray();
            }
            else if (Is(request, NativeListOperation.Read))
            {
                expected.AsSpan((int)request._value, (int)request._length).CopyTo(new Span<ulong>((void*)request._data, (int)request._length));
            }

            NativeMemoryResult result = Respond(fixture, request);
            result._length = (nuint)expected.Length;
            return result;
        };
        using PgList<T> list = PgList.Create<T>(values);
        Assert.AreSequenceEqual(expected, captured!);
        T[] copied = new T[values.Length];
        list.CopyTo(copied, 0);
        Assert.AreSequenceEqual(values, copied);
    }

    /// <summary>
    /// Identifies one list operation without conflating the enclosing memory operation.
    /// </summary>
    private static bool Is(NativeMemoryRequest request, NativeListOperation operation)
        => request._operation == NativeMemoryOperation.List && request._flags == (int)operation;

    /// <summary>
    /// Supplies fixed identities and metadata without modeling native mutation or allocation.
    /// </summary>
    private static NativeMemoryResult Respond(MemoryContextTestFixture fixture, NativeMemoryRequest request)
    {
        if (request._operation != NativeMemoryOperation.List)
        {
            return fixture.Respond(request);
        }

        return new NativeMemoryResult
        {
            _context = 101,
            _pointer = request._flags is 1 or 2 ? 701 : 333,
            _data = 444,
            _length = 3,
            _value = request._flags is 7 or 8 ? 1 : 8,
        };
    }
}
