namespace Ankus.TestExtension;

/// <summary>
/// Exercises context ownership, checked palloc access, reset invalidation, and restoration in a published extension.
/// </summary>
public static class MemoryContextFunctions
{
    private static PgMemoryContext? s_savedContext;
    private static PgAllocation? s_savedAllocation;
    private static int s_sequenceDisposals;

    /// <summary>
    /// Borrows a context by its native address, finds an allocation's owner, and rejects an address that is not a context.
    /// </summary>
    /// <returns>Whether each handle names the same context, and the rejection's exception type.</returns>
    [PgFunction]
    public static unsafe string MemoryContextNativeAddress()
    {
        using PgMemoryContext child = PgMemoryContext.Create("Ankus native address", PgMemoryContext.Current);
        nint address = child.DangerousGetPointer();
        PgMemoryContext borrowed = PgMemoryContext.DangerousBorrow(address);
        using PgAllocation allocation = borrowed.AllocateZeroed(16);
        PgMemoryContext owner = PgMemoryContext.DangerousGetOwner((nint)allocation.DangerousGetPointer());
        string rejected;
        try
        {
            _ = PgMemoryContext.DangerousBorrow((nint)allocation.DangerousGetPointer());
            rejected = "none";
        }
        catch (ArgumentException error)
        {
            rejected = error.GetType().Name;
        }

        bool same = borrowed.Id == child.Id && owner.Id == child.Id && owner.DangerousGetPointer() == address;
        child.Reset();
        return $"{same}|{owner.Name}|{rejected}|{borrowed.IsAlive}";
    }

    /// <summary>
    /// Allocates, switches, resets, and reallocates a context-owned integer in one backend callback.
    /// </summary>
    /// <param name="value">The integer written to the native allocation.</param>
    /// <returns>A compact record of the context name, copied value, reset state, and restored current context.</returns>
    [PgFunction]
    public static string MemoryContextRoundTrip(int value)
    {
        PgMemoryContext current = PgMemoryContext.Current;
        using PgMemoryContext child = PgMemoryContext.Create("Ankus memory test", current);
        using PgAllocation allocation = child.AllocateZeroed(sizeof(int));
        child.Run(() => allocation.Write(value));
        int beforeReset = allocation.Read<int>();
        string name = child.Name;
        child.Reset();
        bool stale = false;
        try
        {
            _ = allocation.Read<int>();
        }
        catch (ObjectDisposedException)
        {
            stale = true;
        }

        using PgAllocation replacement = child.Allocate(sizeof(int));
        replacement.Write(value + 1);
        int afterReset = replacement.Read<int>();
        bool restored = PgMemoryContext.Current.Id == current.Id;
        return $"{name}|{beforeReset}|{stale}|{afterReset}|{restored}";
    }

    /// <summary>
    /// Distinguishes resetting a subtree, only its root, and only its descendants.
    /// </summary>
    /// <param name="mode">Zero for reset, one for reset-only, or two for reset-children.</param>
    /// <returns>The exact surviving context and allocation state.</returns>
    [PgFunction]
    public static string MemoryResetTree(int mode)
    {
        using PgMemoryContext root = PgMemoryContext.Create("memory root café");
        using PgMemoryContext child = PgMemoryContext.Create("memory child", root);
        using PgMemoryContext leaf = PgMemoryContext.Create("memory leaf", child);
        using PgAllocation rootValue = root.Allocate(sizeof(int));
        using PgAllocation childValue = child.Allocate(sizeof(int));
        using PgAllocation leafValue = leaf.Allocate(sizeof(int));
        rootValue.Write(11);
        childValue.Write(22);
        leafValue.Write(33);
        switch (mode)
        {
            case 0:
                root.Reset();
                break;
            case 1:
                root.ResetOnly();
                break;
            case 2:
                root.ResetChildren();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        string state = $"{root.IsAlive},{child.IsAlive},{leaf.IsAlive}|{ReadOrStale(rootValue)},{ReadOrStale(childValue)},{ReadOrStale(leafValue)}";
        using PgAllocation replacement = root.AllocateZeroed(4096);
        replacement.Write(44);
        string name = root.Name;
        root.Reset();
        root.Reset();
        return $"{state}|{name}|{root.Name}|{child.IsAlive},{leaf.IsAlive}";
    }

    /// <summary>
    /// Copies, grows, shrinks, and clears allocations while checking their actual native owner.
    /// </summary>
    /// <returns>The observed bytes, native owner, empty access, and rejected bounds.</returns>
    [PgFunction]
    public static string MemoryAllocationBoundaries()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("allocation owner");
        using PgAllocation bytes = owner.AllocateZeroed(8);
        byte[] zeroes = new byte[8];
        bytes.Read(zeroes);
        string initial = Convert.ToHexString(zeroes);
        bytes.Write([1, 2, 3, 4, 5, 6, 7, 8]);
        bytes.Reallocate(16384);
        bytes.Write<ushort>(0x1020, 16382);
        ushort tail = bytes.Read<ushort>(16382);
        bytes.Reallocate(8);
        bytes.Clear(2, 3);
        byte[] result = new byte[8];
        bytes.Read(result);
        bytes.Read([], 8);
        bytes.Write([], 8);
        int rejected = 0;
        foreach (Action action in new Action[]
        {
            () => bytes.Read<byte>(8),
            () => bytes.Write<ushort>(1, 7),
            () => bytes.Clear(9),
            () => bytes.Read<byte>(nuint.MaxValue),
        })
        {
            try
            {
                action();
            }
            catch (ArgumentOutOfRangeException)
            {
                rejected++;
            }
        }

        bool nativeOwner = bytes.Context.Id == owner.Id;
        using PgAllocation empty = owner.AllocateZeroed(0);
        empty.Read([]);
        empty.Write([]);
        empty.Clear();
        using PgAllocation tried = owner.TryAllocate(1, zeroed: true) ?? throw new InvalidOperationException("A one-byte allocation failed.");
        return $"{initial}|{Convert.ToHexString(result)}|{tail}|{nativeOwner}|{rejected}|{empty.Length}|{tried.Read<byte>()}";
    }

    /// <summary>
    /// Preserves the selected context and old allocation after native allocation errors.
    /// </summary>
    /// <param name="operation">The allocation operation that receives an invalid size.</param>
    /// <returns>The SQLSTATE, preserved contents, selected context, and restored context.</returns>
    [PgFunction]
    public static string MemoryAllocationError(int operation)
    {
        PgMemoryContext previous = PgMemoryContext.Current;
        using PgMemoryContext selected = PgMemoryContext.Create("allocation error owner");
        using PgAllocation allocation = selected.Allocate(sizeof(int));
        allocation.Write(73);
        string result = selected.Run(() =>
        {
            string state = "no error";
            try
            {
                PgTransaction.RunInSubtransaction(() =>
                {
                    const nuint invalidSize = 0x40000000;
                    switch (operation)
                    {
                        case 0:
                            using (selected.Allocate(invalidSize))
                            {
                            }

                            break;
                        case 1:
                            using (selected.TryAllocate(invalidSize))
                            {
                            }

                            break;
                        case 2:
                            allocation.Reallocate(invalidSize);
                            break;
                        default:
                            throw new ArgumentOutOfRangeException(nameof(operation));
                    }
                });
            }
            catch (PgException error)
            {
                state = error.SqlState;
            }

            return $"{state}|{allocation.Read<int>()}|{allocation.Length}|{PgMemoryContext.Current.Id == selected.Id}";
        });
        return $"{result}|{PgMemoryContext.Current.Id == previous.Id}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    /// <summary>
    /// Checks nested current-context restoration and retrying deletion after a protected-current failure.
    /// </summary>
    /// <returns>The managed exception, restored identities, and deletion state.</returns>
    [PgFunction]
    public static string MemoryNestedScopes()
    {
        PgMemoryContext original = PgMemoryContext.Current;
        using PgMemoryContext first = PgMemoryContext.Create("first scope");
        using PgMemoryContext second = PgMemoryContext.Create("second scope", first);
        var expected = new InvalidOperationException("scope failure");
        bool sameError = false;
        string deletionState = string.Empty;
        bool nestedRestored = first.Run(() =>
        {
            try
            {
                second.Run(() =>
                {
                    if (PgMemoryContext.Current.Id != second.Id)
                    {
                        throw new InvalidOperationException("The inner context was not selected.");
                    }

                    try
                    {
                        first.Dispose();
                    }
                    catch (PgException error)
                    {
                        deletionState = error.SqlState;
                    }

                    throw expected;
                });
            }
            catch (InvalidOperationException error)
            {
                sameError = ReferenceEquals(expected, error);
            }

            return PgMemoryContext.Current.Id == first.Id;
        });
        bool retained = first.IsAlive && second.IsAlive;
        first.Dispose();
        first.Dispose();
        return $"{sameError}|{nestedRestored}|{PgMemoryContext.Current.Id == original.Id}|{deletionState}|{retained}|{second.IsAlive}";
    }

    /// <summary>
    /// Resolves native roots and verifies owned context appearance and disappearance in PostgreSQL statistics.
    /// </summary>
    /// <returns>The parent identities, including the current context's parent as pgrx's <c>parent</c> test checks, and native
    /// inventory counts.</returns>
    [PgFunction]
    public static string MemoryNativeInventory()
    {
        PgMemoryContext top = PgMemoryContext.Get(PgMemoryContextKind.Top) ?? throw new InvalidOperationException("No top context.");
        PgMemoryContext current = PgMemoryContext.Current;
        using PgMemoryContext owner = PgMemoryContext.Create("Ankus inventory probe", top);
        bool parent = owner.Parent?.Id == top.Id;
        bool currentSelector = PgMemoryContext.Get(PgMemoryContextKind.Current)?.Id == current.Id;
        bool transaction = PgMemoryContext.Get(PgMemoryContextKind.TopTransaction) is not null;
        owner.Parent?.Dispose();
        bool borrowed = top.IsAlive;
        long before = Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident = 'Ankus inventory probe'");
        nuint size = owner.GetAllocatedBytes();
        using PgAllocation allocation = owner.Allocate(65536);
        bool grew = owner.GetAllocatedBytes() > size;
        owner.Dispose();
        long after = Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident = 'Ankus inventory probe'");
        return $"{top.Parent is null}|{current.Parent is not null}|{parent}|{currentSelector}|{transaction}|{borrowed}|{before}|{after}|{grew}";
    }

    /// <summary>
    /// Saves native ownership under a selected long-lived root for later callbacks.
    /// </summary>
    /// <param name="value">The stored value.</param>
    /// <param name="subtransaction">Whether to use the current subtransaction rather than the top transaction.</param>
    /// <returns>The value copied from the new allocation.</returns>
    [PgFunction]
    public static int MemorySave(int value, bool subtransaction)
    {
        MemoryReleaseSaved();
        PgMemoryContext parent = PgMemoryContext.Get(subtransaction ? PgMemoryContextKind.CurTransaction : PgMemoryContextKind.TopTransaction)
            ?? throw new InvalidOperationException("No active transaction context.");
        s_savedContext = PgMemoryContext.Create("saved memory", parent);
        s_savedAllocation = s_savedContext.Allocate(sizeof(int));
        s_savedAllocation.Write(value);
        return s_savedAllocation.Read<int>();
    }

    /// <summary>
    /// Reads a checked allocation from a later callback in the same backend.
    /// </summary>
    /// <returns>The stored value.</returns>
    [PgFunction]
    public static int MemoryReadSaved() => (s_savedAllocation ?? throw new InvalidOperationException("No saved allocation.")).Read<int>();

    /// <summary>
    /// Tests whether PostgreSQL transaction cleanup invalidated the saved context.
    /// </summary>
    /// <returns>Whether the saved native owner still exists.</returns>
    [PgFunction]
    public static bool MemorySavedAlive() => s_savedContext?.IsAlive ?? false;

    /// <summary>
    /// Drops managed handles and deterministically releases any surviving native owner.
    /// </summary>
    [PgFunction]
    public static void MemoryReleaseSaved()
    {
        s_savedAllocation?.Dispose();
        s_savedContext?.Dispose();
        s_savedAllocation = null;
        s_savedContext = null;
    }

    /// <summary>
    /// Keeps a transaction-owned native value across separate iterator callbacks and releases it on disposal.
    /// </summary>
    /// <param name="count">The number of rows, or a negative number to fail after the first row.</param>
    /// <returns>The values read from retained native storage.</returns>
    [PgFunction]
    public static IEnumerable<int> MemorySequence(int count)
    {
        PgMemoryContext parent = PgMemoryContext.Get(PgMemoryContextKind.TopTransaction)
            ?? throw new InvalidOperationException("No active transaction.");
        using PgMemoryContext owner = PgMemoryContext.Create("memory sequence", parent);
        using PgAllocation value = owner.Allocate(sizeof(int));
        value.Write(80);
        try
        {
            for (int index = 0; index < Math.Abs(count); index++)
            {
                value.Write(value.Read<int>() + 1);
                yield return value.Read<int>();
                if (count < 0)
                {
                    throw new InvalidOperationException("memory sequence failure");
                }
            }
        }
        finally
        {
            s_sequenceDisposals++;
        }
    }

    /// <summary>
    /// Gets the number of iterator cleanup callbacks observed by this backend.
    /// </summary>
    /// <returns>The backend-local cleanup count.</returns>
    [PgFunction]
    public static int MemorySequenceDisposals() => s_sequenceDisposals;

    /// <summary>
    /// Compares a converted context name with PostgreSQL's native statistics and recovers from an unrepresentable identifier.
    /// </summary>
    /// <returns>The copied identifier, native match count, encoding SQLSTATE, and post-error value.</returns>
    [PgFunction]
    public static string MemoryEncodedName()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("memory café");
        long count = Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident = $1",
            SpiParameter.Create("memory café"));
        long before = Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE name = 'Ankus memory context'");
        string state = "representable";
        try
        {
            PgTransaction.RunInSubtransaction(static () =>
            {
                using PgMemoryContext other = PgMemoryContext.Create("memory 🐘");
            });
        }
        catch (PgException error)
        {
            state = error.SqlState;
        }

        owner.Reset();
        long after = Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE name = 'Ankus memory context'");
        return $"{owner.Name}|{count}|{state}|{after - before}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    /// <summary>
    /// Rejects resetting active native callback storage even while a separate user context is selected.
    /// </summary>
    /// <returns>The exact rejection states and preserved callback context.</returns>
    [PgFunction]
    public static string MemoryProtectedContexts()
    {
        PgMemoryContext original = PgMemoryContext.Current;
        PgMemoryContext top = PgMemoryContext.Get(PgMemoryContextKind.Top) ?? throw new InvalidOperationException("No top context.");
        using PgMemoryContext selected = PgMemoryContext.Create("protected context probe");
        var states = new List<string>();
        foreach (Action operation in new Action[] { original.Reset, original.ResetOnly, top.ResetChildren })
        {
            selected.Run(() =>
            {
                try
                {
                    operation();
                    states.Add("no error");
                }
                catch (PgException error)
                {
                    states.Add(error.SqlState);
                }
            });
        }

        return $"{string.Join(',', states)}|{original.IsAlive}|{PgMemoryContext.Current.Id == original.Id}|{Spi.ExecuteScalar<int>("SELECT 42")}";
    }

    private static string ReadOrStale(PgAllocation allocation)
    {
        try
        {
            return allocation.Read<int>().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ObjectDisposedException)
        {
            return "stale";
        }
    }
}
