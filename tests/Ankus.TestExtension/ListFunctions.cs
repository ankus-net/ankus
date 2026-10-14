namespace Ankus.TestExtension;

/// <summary>
/// Exercises native list values, mutations and PostgreSQL ownership through published callbacks.
/// </summary>
public static unsafe class ListFunctions
{
    private static PgList<int>? s_saved;

    /// <summary>
    /// Returns independently readable native cells for each supported PostgreSQL list kind.
    /// </summary>
    /// <param name="kind">The pointer, integer, OID or transaction-ID list discriminator.</param>
    [PgFunction]
    public static string ListCells(int kind)
    {
        return kind switch
        {
            1 => Describe<nint>([0, -1, nint.MinValue, nint.MaxValue]),
            2 => Describe<int>([int.MinValue, -1, 0, int.MaxValue]),
            3 => Describe<uint>([0, 2147483648, uint.MaxValue]),
            4 => Describe<PgTransactionId>([new(0), new(2147483648), new(uint.MaxValue)]),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    /// <summary>
    /// Grows across inline and separate cell storage while another context is current.
    /// </summary>
    /// <param name="count">The number of consecutive values to append.</param>
    [PgFunction]
    public static string ListGrowth(int count)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("list owner");
        using PgMemoryContext ambient = PgMemoryContext.Create("list ambient");
        using PgList<int> list = PgList.Create<int>([0], owner);
        long original = (long)list.DangerousGetPointer();
        ambient.Run(() =>
        {
            for (int index = 1; index < count; index++)
            {
                list.Add(index);
            }
        });
        ambient.Dispose();
        int[] actual = [.. list];
        bool exact = actual.Length == count;
        for (int index = 0; index < actual.Length; index++)
        {
            exact &= actual[index] == index;
        }

        return $"{exact}|{list.Count}|{list.Capacity >= count}|{original == (long)list.DangerousGetPointer()}|" +
            $"{list.LifetimeContext!.Id == owner.Id}|{NativeDescribe(list)}";
    }

    /// <summary>
    /// Tests reserve's additional-count contract and no-allocation append behavior at exact capacity.
    /// </summary>
    [PgFunction]
    public static string ListReserve()
    {
        using PgList<int> list = PgList.Create<int>();
        bool nil = list.IsEmpty && list.Capacity == 0 && list.DangerousGetPointer() == null;
        bool nilResults = !list.TryAdd(99) && !list.TryReserve(0) && !list.TryReserve(100) && !list.TryReserve(int.MaxValue);
        list.Add(-7);
        int initial = list.Capacity;
        while (list.Count < initial)
        {
            if (!list.TryAdd(list.Count))
            {
                throw new InvalidOperationException("spare capacity rejected");
            }
        }

        long fullCells = (long)list.DangerousGetCellsPointer();
        bool full = !list.TryAdd(777) && list.Count == initial && fullCells == (long)list.DangerousGetCellsPointer();
        bool reserved = list.TryReserve(100) && list.Capacity >= list.Count + 100;
        list.Clear();
        bool cleared = list.Count == 0 && list.Capacity == 0 && list.DangerousGetPointer() == null;
        list.Add(42);
        return $"{nil}|{nilResults}|{full}|{reserved}|{cleared}|{NativeDescribe(list)}";
    }

    /// <summary>
    /// Combines insertion, replacement, removal, independent cloning, copying and final pop.
    /// </summary>
    [PgFunction]
    public static string ListMutations()
    {
        using PgList<int> list = PgList.Create<int>([10, 30]);
        list.Insert(1, 20);
        list.Insert(0, -1);
        list.Insert(list.Count, 40);
        list[0] = 0;
        list.AddRange([50, 60]);
        using PgList<int> clone = list.Clone();
        list.RemoveAt(2);
        bool removed = list.Remove(50) && !list.Remove(999);
        int[] copy = new int[list.Count + 2];
        list.CopyTo(copy, 1);
        copy[1] = -999;
        bool independent = list[0] == 0;
        bool ends = list.TryGetFirst(out int first) && first == 0 && list.TryGetLast(out int last) && last == 60;
        bool popped = list.TryPop(out int poppedValue) && poppedValue == 60;
        return $"{NativeDescribe(list)}|{NativeDescribe(clone)}|{removed}|{independent}|{ends}|{popped}|{list.IndexOf(30)}|{list.Contains(20)}";
    }

    /// <summary>
    /// Drains exact head, interior, tail, whole, singleton and empty ranges before observing the retained sequence.
    /// </summary>
    /// <param name="index">The first removed cell.</param>
    /// <param name="count">The number of removed cells.</param>
    [PgFunction]
    public static string ListDrain(int index, int count)
    {
        using PgList<int> list = PgList.Create<int>([0, 1, 2, 3, 4, 5, 6, 7, 8, 9]);
        int[] removed = list.Drain(index, count);
        string retained = NativeDescribe(list);
        bool head = removed.Length == 0 || removed.Take(1).Single() == index;
        return $"{string.Join(',', removed)}|{retained}|{head}|{list.IsEmpty == (list.DangerousGetPointer() == null)}";
    }

    /// <summary>
    /// Proves borrowed iteration, mutation invalidation and immediate consuming transfer.
    /// </summary>
    [PgFunction]
    public static string ListIteration()
    {
        using PgList<int> list = PgList.Create<int>([2, 3, 5]);
        var values = new List<int>();
        foreach (int value in list)
        {
            values.Add(value);
        }

        using IEnumerator<int> invalidated = list.GetEnumerator();
        _ = invalidated.MoveNext();
        list[1] = 7;
        bool rejected = false;
        try
        {
            invalidated.MoveNext();
        }
        catch (InvalidOperationException)
        {
            rejected = true;
        }

        using IEnumerator<int> consuming = list.GetConsumingEnumerator();
        bool consumed = false;
        try
        {
            _ = list.Count;
        }
        catch (ObjectDisposedException)
        {
            consumed = true;
        }

        bool first = consuming.MoveNext() && consuming.Current == 2;
        consuming.Dispose();
        using PgList<int> all = PgList.Create<int>([11, 13]);
        using IEnumerator<int> complete = all.GetConsumingEnumerator();
        var consumedValues = new List<int>();
        while (complete.MoveNext())
        {
            consumedValues.Add(complete.Current);
        }

        return $"{string.Join(',', values)}|{rejected}|{consumed}|{first}|{string.Join(',', consumedValues)}|{complete.MoveNext()}";
    }

    /// <summary>
    /// Rejects stale owned and borrowed identities after reset or deletion, including empty operations.
    /// </summary>
    /// <param name="delete">Whether to delete the owner instead of resetting it.</param>
    [PgFunction]
    public static string ListLifetime(bool delete)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("list lifetime");
        using PgList<int> list = PgList.Create<int>([1, 2], owner);
        using PgList<int> empty = PgList.Create<int>(owner);
        using PgList<int> owned = PgList.Create<int>([99], owner);
        void* raw = list.DangerousDetach();
        using PgList<int> borrowed = PgList.DangerousBorrow<int>(raw, owner);
        if (delete)
        {
            owner.Dispose();
        }
        else
        {
            owner.Reset();
        }

        int rejected = 0;
        try
        {
            _ = borrowed.Count;
        }
        catch (ObjectDisposedException)
        {
            rejected++;
        }

        try
        {
            empty.CopyTo([]);
        }
        catch (ObjectDisposedException)
        {
            rejected++;
        }

        try
        {
            owned.TryAdd(1);
        }
        catch (ObjectDisposedException)
        {
            rejected++;
        }

        borrowed.Dispose();
        empty.Dispose();
        string fresh = "deleted";
        if (!delete)
        {
            using PgList<int> replacement = PgList.Create<int>([42], owner);
            fresh = NativeDescribe(replacement);
        }

        return $"{rejected}|{fresh}";
    }

    /// <summary>
    /// Catches a guarded native limit error and proves managed finally, original values and backend recovery.
    /// </summary>
    [PgFunction]
    public static string ListErrors()
    {
        using PgList<int> list = PgList.Create<int>([17, 23]);
        string state = "none";
        int finalizers = 0;
        try
        {
            PgTransaction.RunInSubtransaction(() => list.TryReserve(int.MaxValue));
        }
        catch (PgException exception)
        {
            state = exception.SqlState;
        }
        finally
        {
            finalizers++;
        }

        list.Add(31);
        return $"{state}|{finalizers}|{NativeDescribe(list)}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    /// <summary>
    /// Disposes several pointer containers while retaining independently owned pointee storage.
    /// </summary>
    [PgFunction]
    public static string ListPointers()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("list pointees");
        using PgAllocation allocation = owner.Allocate(4);
        allocation.Write([17, 34, 0, 255]);
        nint pointer = (nint)allocation.DangerousGetPointer();
        using PgList<nint> list = PgList.Create<nint>([0, pointer], owner);
        bool nullCell = list.TryGetFirst(out nint first) && first == 0;
        using PgList<nint> clone = list.Clone(owner);
        list.Clear();
        clone.Dispose();
        byte[] bytes = new byte[4];
        allocation.Read(bytes);
        return $"{nullCell}|{Convert.ToHexString(bytes)}|{list.DangerousGetPointer() == null}";
    }

    /// <summary>
    /// Tests transitions between singleton storage and NIL, including empty consuming iteration.
    /// </summary>
    [PgFunction]
    public static string ListEmptyTransitions()
    {
        using PgList<int> list = PgList.Create<int>();
        list.Insert(0, 0);
        bool popped = list.TryPop(out int zero) && zero == 0 && list.IsEmpty && list.DangerousGetPointer() == null;
        bool absent = !list.TryPop(out _) && !list.TryGetFirst(out _) && !list.TryGetLast(out _);
        list.Add(42);
        list.RemoveAt(0);
        bool removed = list.IsEmpty && list.Capacity == 0 && list.DangerousGetPointer() == null;
        list.Add(17);
        int[] drained = list.Drain(0, 1);
        bool empty = list.IsEmpty && list.Capacity == 0 && list.DangerousGetPointer() == null;
        using IEnumerator<int> iterator = list.GetConsumingEnumerator();
        return $"{popped}|{absent}|{removed}|{string.Join(',', drained)}|{empty}|{iterator.MoveNext()}|{iterator.MoveNext()}";
    }

    /// <summary>
    /// Rejects borrowing under a different allocator owner without invalidating the real owner.
    /// </summary>
    [PgFunction]
    public static string ListBorrowOwner()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("actual list owner");
        using PgMemoryContext wrong = PgMemoryContext.Create("wrong list owner");
        using PgList<int> list = PgList.Create<int>([17, 31], owner);
        string state = "none";
        try
        {
            PgTransaction.RunInSubtransaction(() =>
            {
                using PgList<int> rejected = PgList.DangerousBorrow<int>(list.DangerousGetPointer(), wrong);
            });
        }
        catch (PgException error)
        {
            state = error.SqlState;
        }

        return $"{state}|{NativeDescribe(list)}";
    }

    /// <summary>
    /// Borrows the C fixture's list, rejects a wrong tag, and returns the new pointer after structural changes.
    /// </summary>
    /// <param name="state">The native pointer, including NIL.</param>
    /// <param name="mode">Native borrow scenario.</param>
    [PgFunction]
    public static long ListBorrow(PgInternal state, int mode)
    {
        void* pointer = (void*)state.Datum.DangerousGetBits();
        PgMemoryContext owner = PgMemoryContext.Current;
        if (mode == 2)
        {
            bool rejected = !PgList.DangerousTryBorrow<uint>(pointer, owner, out PgList<uint>? wrong) && wrong is null;
            if (!rejected)
            {
                throw new InvalidOperationException("wrong tag accepted");
            }
        }

        using PgList<int> list = PgList.DangerousBorrow<int>(pointer, owner);
        if (mode == 3)
        {
            list.Clear();
        }
        else
        {
            list.Add(42);
            list.Insert(0, -7);
            if (mode == 0)
            {
                list[1] = 11;
            }
        }

        long result = (long)list.DangerousGetPointer();
        list.Dispose();
        return result;
    }

    /// <summary>
    /// Saves a list across callbacks in the selected transaction lifetime.
    /// </summary>
    /// <param name="subtransaction">Whether to use the innermost transaction owner.</param>
    [PgFunction]
    public static string ListSave(bool subtransaction)
    {
        s_saved?.Dispose();
        s_saved = PgList.Create<int>([17], PgMemoryContext.Get(subtransaction ? PgMemoryContextKind.CurTransaction : PgMemoryContextKind.TopTransaction)!);
        return "saved";
    }

    /// <summary>
    /// Reads a saved list or reports checked expiration after native transaction cleanup.
    /// </summary>
    [PgFunction]
    public static string ListSaved()
    {
        if (s_saved is not { } saved)
        {
            return "missing";
        }

        try
        {
            return string.Join(',', saved.ToArray());
        }
        catch (ObjectDisposedException)
        {
            saved.Dispose();
            s_saved = null;
            return "stale";
        }
    }

    /// <summary>
    /// Creates typed cells and compares safe managed copies with their exact original values.
    /// </summary>
    private static string Describe<T>(T[] values) where T : unmanaged
    {
        using PgList<T> list = PgList.Create<T>(values);
        T[] copy = new T[values.Length];
        list.CopyTo(copy, 0);
        return $"{copy.AsSpan().SequenceEqual(values)}|{NativeDescribe(list)}";
    }

    /// <summary>
    /// Lets independent C code interpret the actual native tag and each cell.
    /// </summary>
    private static string NativeDescribe<T>(PgList<T> list) where T : unmanaged
        => Spi.ExecuteScalar<string>("SELECT tests.list_describe($1)", SpiParameter.Create((long)list.DangerousGetPointer()));
}
