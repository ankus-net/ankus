using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises native typed ownership and raw pointer lifetime contracts in PostgreSQL.
/// </summary>
public static unsafe class NativeBoxFunctions
{
#if ANKUS_PG13 || ANKUS_PG14 || ANKUS_PG15
    private const nuint BoxAlignment = 8;
    private const nuint ViewAlignment = 8;
#else
    private const nuint BoxAlignment = 64;
    private const nuint ViewAlignment = 4096;
#endif

    private static int s_pointeeDisposals;
    private static PgMemoryContext? s_savedOwner;
    private static PgNativeBox<int>? s_savedBox;
    private static PgContextValue<int>? s_savedValue;
    private static PgNativeReference<int>? s_savedKnown;
    private static PgNativeReference<int>? s_savedRaw;

    /// <summary>
    /// Checks the pgrx five-value, zeroed, raw-owned, stack-borrowed, and null-pointer construction witnesses.
    /// </summary>
    /// <returns>The exact values, native owners, and null results from an already disposed context.</returns>
    [PgFunction]
    public static string NativeBoxBasics()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("native box basics");
        using PgNativeBox<int> initialized = owner.CreateBox(5);
        using PgNativeBox<int> zeroed = owner.AllocateZeroedBox<int>();
        using PgNativeBox<int> uninitialized = owner.DangerousAllocateUninitializedBox<int>();
        uninitialized.Value = 5;
        using PgNativeBox<int> tried = owner.TryCreateBox(5) ?? throw new InvalidOperationException("A four-byte box allocation failed.");
        PgContextValue<int> contextValue = owner.CreateContextValue(5);
        PgContextValue<int> triedContext = owner.TryCreateContextValue(5) ?? throw new InvalidOperationException("A four-byte context allocation failed.");
        using PgAllocation raw = owner.Allocate<int>();
        raw.Write(5);
        using PgNativeBox<int> adopted = owner.DangerousAdoptBox<int>(raw.DangerousDetach())
            ?? throw new InvalidOperationException("A non-null native pointer became null.");
        int stackValue = 5;
        PgNativeReference<int> borrowed = owner.DangerousBorrow<int>(&stackValue)
            ?? throw new InvalidOperationException("A non-null stack pointer became null.");
        bool owners = initialized.Context.Id == owner.Id && contextValue.Context.Id == owner.Id &&
            tried.Context.Id == owner.Id && triedContext.Context.Id == owner.Id && adopted.Context.Id == owner.Id;
        string values = $"{initialized.Value},{zeroed.Value},{uninitialized.Value},{tried.Value},{contextValue.Value},{triedContext.Value},{adopted.Value},{borrowed.Value}";
        owner.Dispose();
        bool borrowedNull = owner.DangerousBorrow<int>(null) is null;
        bool adoptedNull = owner.DangerousAdoptBox<int>(null) is null;
        return $"{values}|{owners}|{borrowedNull},{adoptedNull}|{ReadOrStale(() => initialized.Value)}";
    }

    /// <summary>
    /// Transfers individual ownership into its context while retaining existing checked aliases.
    /// </summary>
    /// <param name="deleteParent">Whether final cleanup deletes the parent rather than resetting the value's context.</param>
    /// <returns>The shared values, consumed owner behavior, and final checked lifetimes.</returns>
    [PgFunction]
    public static string NativeBoxReleaseToContext(bool deleteParent)
    {
        using PgMemoryContext parent = PgMemoryContext.Create("native box release parent");
        using PgMemoryContext owner = PgMemoryContext.Create("native box release owner", parent);
        using PgNativeBox<int> box = owner.CreateBox(5, PgAllocationOptions.Huge, BoxAlignment);
        PgNativeReference<int> originalBorrow = box.Borrow();
        nint pointer = (nint)box.DangerousGetPointer();
        PgContextValue<int> value = box.ReleaseToContext();
        PgNativeReference<int> nextBorrow = value.Borrow();
        box.Dispose();
        box.Dispose();
        originalBorrow.Value = 73;
        bool samePointer = (nint)value.DangerousGetPointer() == pointer && (nint)nextBorrow.DangerousGetPointer() == pointer;
        string before = $"{value.Value},{originalBorrow.Value},{nextBorrow.Value}";
        int consumedOperations = new Action[]
        {
            () =>
            {
                _ = box.Value;
            },
            () => box.Value = 99,
            () =>
            {
                _ = box.Borrow();
            },
            () =>
            {
                _ = box.ReleaseToContext();
            },
            () =>
            {
                _ = box.DangerousGetPointer();
            },
        }.Count(IsDisposed);

        bool policy = value.Options == PgAllocationOptions.Huge && value.Alignment == BoxAlignment;
        if (deleteParent)
        {
            parent.Dispose();
        }
        else
        {
            owner.Reset();
        }

        return $"{before}|{samePointer}|{consumedOperations}|{policy}|{ReadOrStale(() => value.Value)}," +
            $"{ReadOrStale(() => originalBorrow.Value)},{ReadOrStale(() => nextBorrow.Value)}|{owner.IsAlive}";
    }

    /// <summary>
    /// Raw detachment invalidates all shared checked views and allows one fresh owner of the same pointer.
    /// </summary>
    /// <param name="fromContext">Whether raw transfer starts from a context-owned value.</param>
    /// <returns>The pointer continuity, old-view invalidation, adopted mutations, and final lifetime.</returns>
    [PgFunction]
    public static string NativeBoxDetachAndAdopt(bool fromContext)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("native box raw transfer");
        using PgNativeBox<int> box = owner.CreateBox(5, PgAllocationOptions.Huge, BoxAlignment);
        PgNativeReference<int> borrowed = box.Borrow();
        PgContextValue<int>? contextValue = fromContext ? box.ReleaseToContext() : null;
        nint original = (nint)borrowed.DangerousGetPointer();
        void* pointer = contextValue is null ? box.DangerousDetach() : contextValue.DangerousDetach();
        string oldValue = contextValue is null ? ReadOrStale(() => box.Value) : ReadOrStale(() => contextValue.Value);
        string oldBorrow = ReadOrStale(() => borrowed.Value);
        box.Dispose();
        using PgNativeBox<int> adopted = owner.DangerousAdoptBox<int>(pointer, huge: true, alignment: BoxAlignment)
            ?? throw new InvalidOperationException("Detached storage unexpectedly became null.");
        int copied = adopted.Value;
        adopted.Value = 91;
        PgNativeReference<int> nextBorrow = adopted.Borrow();
        bool nativeOwner = adopted.Context.Id == owner.Id;
        bool policy = adopted.Options == PgAllocationOptions.Huge && adopted.Alignment == BoxAlignment;
        int changed = nextBorrow.Value;
        string oldAfterAdoption = ReadOrStale(() => borrowed.Value);
        adopted.Dispose();
        adopted.Dispose();
        return $"{(nint)pointer == original}|{oldValue},{oldBorrow}|{copied}|{changed}|{nativeOwner}|{policy}|" +
            $"{oldAfterAdoption}|{ReadOrStale(() => nextBorrow.Value)}|{owner.IsAlive}";
    }

    /// <summary>
    /// Clones exact padding and pointer bytes into explicit and current contexts without recursively copying the pointee.
    /// </summary>
    /// <param name="sourceKind">Owned box, context value, checked borrow, or raw borrow.</param>
    /// <returns>The raw byte equality, distinct storage, shallow pointers, target policies, and independent values.</returns>
    [PgFunction]
    public static string NativeBoxClone(int sourceKind)
    {
        using PgMemoryContext source = PgMemoryContext.Create("native box clone source");
        using PgMemoryContext target = PgMemoryContext.Create("native box clone target");
        using PgMemoryContext selected = PgMemoryContext.Create("native box clone selected");
        using PgMemoryContext pointeeOwner = PgMemoryContext.Create("native box clone pointee");
        using PgAllocation pointee = pointeeOwner.Allocate<int>();
        pointee.Write(901);
        using PgAllocation storage = source.Allocate((nuint)sizeof(PaddedValue), PgAllocationOptions.Huge, BoxAlignment);
        PaddedValue* sourcePointer = (PaddedValue*)storage.DangerousGetPointer();
        new Span<byte>(sourcePointer, sizeof(PaddedValue)).Fill(0xa7);
        sourcePointer->Tag = 7;
        sourcePointer->Pointer = (nint)pointee.DangerousGetPointer();
        sourcePointer->Number = 123;
        byte[] expectedBytes = new ReadOnlySpan<byte>(sourcePointer, sizeof(PaddedValue)).ToArray();
        using PgNativeBox<PaddedValue> box = source.DangerousAdoptBox<PaddedValue>(storage.DangerousDetach(), huge: true, alignment: BoxAlignment)
            ?? throw new InvalidOperationException("Padded native storage became null.");
        Func<PgMemoryContext?, PgContextValue<PaddedValue>> clone;
        Func<PgMemoryContext?, PgNativeBox<PaddedValue>> cloneOwned;
        switch (sourceKind)
        {
            case 0:
                clone = box.CloneInto;
                cloneOwned = box.CloneOwnedInto;
                break;
            case 1:
                PgContextValue<PaddedValue> value = box.ReleaseToContext();
                clone = value.CloneInto;
                cloneOwned = value.CloneOwnedInto;
                break;
            case 2:
                PgNativeReference<PaddedValue> known = box.Borrow();
                clone = known.CloneInto;
                cloneOwned = known.CloneOwnedInto;
                break;
            case 3:
                PgNativeReference<PaddedValue> raw = source.DangerousBorrow<PaddedValue>(sourcePointer)
                    ?? throw new InvalidOperationException("Padded native storage became null.");
                clone = raw.CloneInto;
                cloneOwned = raw.CloneOwnedInto;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(sourceKind));
        }

        PgContextValue<PaddedValue> explicitCopy = clone(target);
        using PgNativeBox<PaddedValue> explicitOwner = cloneOwned(target);
        PgContextValue<PaddedValue> currentCopy = selected.Run(() => clone(null));
        using PgNativeBox<PaddedValue> currentOwner = selected.Run(() => cloneOwned(null));
        nint[] pointers = [(nint)explicitCopy.DangerousGetPointer(), (nint)explicitOwner.DangerousGetPointer(),
            (nint)currentCopy.DangerousGetPointer(), (nint)currentOwner.DangerousGetPointer()];
        bool exactBytes = pointers.All(pointer => new ReadOnlySpan<byte>((void*)pointer, sizeof(PaddedValue)).SequenceEqual(expectedBytes));
        bool distinct = pointers.Append((nint)sourcePointer).Distinct().Count() == 5;
        bool contexts = explicitCopy.Context.Id == target.Id && explicitOwner.Context.Id == target.Id &&
            currentCopy.Context.Id == selected.Id && currentOwner.Context.Id == selected.Id;
        bool defaults = explicitCopy.Options == PgAllocationOptions.None && explicitOwner.Options == PgAllocationOptions.None &&
            currentCopy.Options == PgAllocationOptions.None && currentOwner.Options == PgAllocationOptions.None &&
            explicitCopy.Alignment == 0 && explicitOwner.Alignment == 0 && currentCopy.Alignment == 0 && currentOwner.Alignment == 0;
        bool shallow = pointers.All(pointer => ((PaddedValue*)pointer)->Pointer == (nint)pointee.DangerousGetPointer());
        PaddedValue changed = explicitOwner.Value;
        changed.Number = 999;
        explicitOwner.Value = changed;
        int originalNumber = sourcePointer->Number;
        pointee.Write(902);
        source.Reset();
        int[] pointees = [.. pointers.Select(static pointer => *(int*)((PaddedValue*)pointer)->Pointer)];
        return $"{exactBytes}|{distinct}|{contexts}|{defaults}|{shallow}|{originalNumber}|{explicitOwner.Value.Number}|" +
            $"{string.Join(',', pointees)}|{explicitCopy.Value.Number},{currentCopy.Value.Number},{currentOwner.Value.Number}";
    }

    /// <summary>
    /// Frees and resets unmanaged values without invoking their optional managed IDisposable implementation.
    /// </summary>
    /// <returns>The destructor count after native cleanup and after a separate explicit managed disposal control.</returns>
    [PgFunction]
    public static string NativeBoxDoesNotDisposePointee()
    {
        s_pointeeDisposals = 0;
        using PgMemoryContext owner = PgMemoryContext.Create("native box destructor probe");
        using PgNativeBox<DisposableValue> box = owner.CreateBox(new DisposableValue(5));
        PgContextValue<DisposableValue> contextValue = owner.CreateContextValue(new DisposableValue(7));
        using PgNativeBox<DisposableValue> copy = contextValue.CloneOwnedInto(owner);
        int sum = box.Value.Number + contextValue.Value.Number + copy.Value.Number;
        box.Dispose();
        copy.Dispose();
        owner.Reset();
        int afterNative = s_pointeeDisposals;
        var control = new DisposableValue(9);
        control.Dispose();
        return $"{sum}|{afterNative}|{s_pointeeDisposals}|{control.Number}";
    }

    /// <summary>
    /// Individually frees a separately accounted native chunk before its owning context is reset or deleted.
    /// </summary>
    /// <returns>The exact payload, immediate allocator reclamation, live context, and harmless repeated disposal.</returns>
    [PgFunction]
    public static string NativeBoxIndividualFree()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("native box individual free");
        nuint baseline = owner.GetAllocatedBytes();
        using PgNativeBox<LargeValue> box = owner.CreateBox(new LargeValue { First = 5, Last = 73 });
        PgNativeReference<LargeValue> borrowed = box.Borrow();
        nuint allocated = owner.GetAllocatedBytes();
        LargeValue contents = borrowed.Value;
        box.Dispose();
        nuint after = owner.GetAllocatedBytes();
        long nativeAfter = Spi.ExecuteScalar<long>("SELECT total_bytes FROM ankus_test_memory.contexts WHERE ident = 'native box individual free'");
        bool expired = IsDisposed(() =>
        {
            _ = borrowed.Value;
        });
        box.Dispose();
        return $"{allocated > baseline}|{contents.First},{contents.Last}|{after == baseline}|{nativeAfter == (long)baseline}|" +
            $"{owner.IsAlive}|{expired}|{owner.GetAllocatedBytes() == after}";
    }

    /// <summary>
    /// Collects typed wrappers and references while PostgreSQL remains the native storage lifetime owner.
    /// </summary>
    /// <param name="owned">Whether the discarded wrapper has individual disposal rights.</param>
    /// <returns>Managed collection, unchanged native accounting, live raw bytes, and reset reclamation.</returns>
    [PgFunction]
    public static string NativeBoxCollection(bool owned)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("native box collection probe");
        nuint baseline = owner.GetAllocatedBytes();
        (nint pointer, WeakReference wrapper, WeakReference reference) = AllocateCollectible(owner, owned);
        nuint allocated = owner.GetAllocatedBytes();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            GC.WaitForPendingFinalizers();
        }

        bool collected = !wrapper.IsAlive && !reference.IsAlive;
        bool retained = owner.GetAllocatedBytes() == allocated;
        PgNativeReference<LargeValue> borrowed = owner.DangerousBorrow<LargeValue>((void*)pointer)
            ?? throw new InvalidOperationException("Collected native wrapper lost its non-null pointer.");
        LargeValue contents = borrowed.Value;
        owner.Reset();
        bool expired = IsDisposed(() =>
        {
            _ = borrowed.Value;
        });
        return $"{collected}|{allocated > baseline}|{retained}|{contents.First},{contents.Last}|{owner.GetAllocatedBytes() == baseline}|{expired}";
    }

    /// <summary>
    /// Uses stack and interior pointers with an independent lifetime anchor and checks generation expiry after reset.
    /// </summary>
    /// <param name="cleanup">Reset-only, reset, reset-children, or parent deletion.</param>
    /// <returns>The shared bytes, declared lifetime anchor, stale old generations, and current-generation usability.</returns>
    [PgFunction]
    public static string NativeBoxRawAliases(int cleanup)
    {
        using PgMemoryContext parent = PgMemoryContext.Create("native raw reference parent");
        using PgMemoryContext anchor = PgMemoryContext.Create("native raw reference anchor", parent);
        using PgMemoryContext actualOwner = PgMemoryContext.Create("native raw reference actual owner");
        using PgAllocation allocation = actualOwner.Allocate<int>(3);
        allocation.Write(31, sizeof(int));
        int stackValue = 5;
        PgNativeReference<int> first = anchor.DangerousBorrow<int>(&stackValue) ?? throw new InvalidOperationException("Missing stack borrow.");
        PgNativeReference<int> second = anchor.DangerousBorrow<int>(&stackValue) ?? throw new InvalidOperationException("Missing stack alias.");
        void* interior = (byte*)allocation.DangerousGetPointer() + sizeof(int);
        PgNativeReference<int> inside = anchor.DangerousBorrow<int>(interior) ?? throw new InvalidOperationException("Missing interior borrow.");
        first.Value = 7;
        int shared = second.Value;
        second.Value = 9;
        inside.Value = 73;
        bool anchored = first.LifetimeContext.Id == anchor.Id && inside.LifetimeContext.Id == anchor.Id &&
            inside.LifetimeContext.Id != allocation.Context.Id;
        nuint before = anchor.GetAllocatedBytes();
        int aliases = 0;
        for (int index = 0; index < 500; index++)
        {
            PgNativeReference<int> alias = anchor.DangerousBorrow<int>(interior)!;
            if (alias.Value == 73)
            {
                aliases++;
            }
        }

        bool unchanged = anchor.GetAllocatedBytes() == before;
        nint identity = anchor.Id;
        switch (cleanup)
        {
            case 0:
                anchor.ResetOnly();
                break;
            case 1:
                anchor.Reset();
                break;
            case 2:
                parent.ResetChildren();
                break;
            case 3:
                parent.Dispose();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cleanup));
        }

        string stale = $"{ReadOrStale(() => first.Value)},{ReadOrStale(() => second.Value)},{ReadOrStale(() => inside.Value)}";
        bool writeRejected = IsDisposed(() => first.Value = 99);
        bool pointerRejected = IsDisposed(() =>
        {
            _ = inside.DangerousGetPointer();
        });
        string fresh = "deleted";
        if (anchor.IsAlive)
        {
            PgNativeReference<int> current = anchor.DangerousBorrow<int>(interior)!;
            fresh = current.Value.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        return $"{shared}|{stackValue}|{allocation.Read<int>(sizeof(int))}|{anchored}|{aliases}|{unchanged}|{anchor.Id == identity}|" +
            $"{stale}|{writeRejected}|{pointerRejected}|{fresh}|{ReadOrStale(() => inside.Value)}";
    }

    /// <summary>
    /// Separates parent and descendant generations and expires a fresh capture on a second explicit reset.
    /// </summary>
    /// <param name="operation">Reset-only, reset-children, or reset of the root context.</param>
    /// <returns>The exact old lifetimes, surviving context inventory, and two successive generation boundaries.</returns>
    [PgFunction]
    public static string NativeBoxGenerationTree(int operation)
    {
        using PgMemoryContext parent = PgMemoryContext.Create("native generation tree parent");
        using PgMemoryContext child = PgMemoryContext.Create("native generation tree child", parent);
        using PgMemoryContext grandchild = PgMemoryContext.Create("native generation tree grandchild", child);
        using PgMemoryContext actualOwner = PgMemoryContext.Create("native generation external owner");
        using PgAllocation external = actualOwner.CopyFrom<int>([11, 22, 33]);
        using PgNativeBox<int> parentBox = parent.CreateBox(111);
        using PgNativeBox<int> childBox = child.CreateBox(222);
        using PgNativeBox<int> grandchildBox = grandchild.CreateBox(333);
        nint[] identities = [parent.Id, child.Id, grandchild.Id];
        int* pointer = (int*)external.DangerousGetPointer();
        PgNativeReference<int> parentRaw = parent.DangerousBorrow<int>(pointer) ?? throw new InvalidOperationException("Missing parent borrow.");
        PgNativeReference<int> childRaw = child.DangerousBorrow<int>(pointer + 1) ?? throw new InvalidOperationException("Missing child borrow.");
        PgNativeReference<int> grandchildRaw = grandchild.DangerousBorrow<int>(pointer + 2) ?? throw new InvalidOperationException("Missing grandchild borrow.");
        Action reset = operation switch
        {
            0 => parent.ResetOnly,
            1 => parent.ResetChildren,
            2 => parent.Reset,
            _ => throw new ArgumentOutOfRangeException(nameof(operation)),
        };
        reset();
        string first = $"{ReadOrStale(() => parentRaw.Value)},{ReadOrStale(() => childRaw.Value)},{ReadOrStale(() => grandchildRaw.Value)}|" +
            $"{ReadOrStale(() => parentBox.Value)},{ReadOrStale(() => childBox.Value)},{ReadOrStale(() => grandchildBox.Value)}|" +
            $"{parent.IsAlive},{child.IsAlive},{grandchild.IsAlive}";
        long inventory = Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident LIKE 'native generation tree %'");
        bool sameIdentities = identities.SequenceEqual([parent.Id, child.Id, grandchild.Id]);
        PgMemoryContext selected = operation == 1 ? child : parent;
        int offset = operation == 1 ? 1 : 0;
        PgNativeReference<int> current = selected.DangerousBorrow<int>(pointer + offset)
            ?? throw new InvalidOperationException("Missing intermediate generation borrow.");
        int middle = current.Value;
        reset();
        string expired = ReadOrStale(() => current.Value);
        string oldest = operation == 1 ? ReadOrStale(() => childRaw.Value) : ReadOrStale(() => parentRaw.Value);
        PgNativeReference<int> latest = selected.DangerousBorrow<int>(pointer + offset)
            ?? throw new InvalidOperationException("Missing final generation borrow.");
        latest.Value = 44;
        return $"{first}|{inventory}|{sameIdentities}|{middle}|{expired}|{oldest}|{latest.Value}|{external.Read<int>((nuint)(offset * sizeof(int)))}";
    }

    /// <summary>
    /// Follows a resized tracked allocation and rejects typed views that no longer fit after shrinking.
    /// </summary>
    /// <returns>The shared offset values, moved pointer, range rejections, cloned value, and free invalidation.</returns>
    [PgFunction]
    public static string NativeBoxOffsetViews()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("native allocation view owner");
        using PgMemoryContext target = PgMemoryContext.Create("native allocation view clone");
        using PgAllocation allocation = owner.AllocateZeroed(20, alignment: ViewAlignment);
        allocation.Write(11);
        allocation.Write(22, 4);
        PgNativeReference<int> reference = allocation.Borrow<int>(4);
        PgNativeReference<long> tail = allocation.Borrow<long>(12);
        reference.Value = 73;
        int shared = allocation.Read<int>(4);
        nint before = (nint)allocation.DangerousGetPointer();
        if (!allocation.TryReallocate(128, zeroNewMemory: true))
        {
            throw new InvalidOperationException("A bounded view resize failed.");
        }

        bool moved = (nint)allocation.DangerousGetPointer() != before;
        bool follows = (nint)reference.DangerousGetPointer() == (nint)((byte*)allocation.DangerousGetPointer() + 4);
        int afterGrowth = reference.Value;
        allocation.Reallocate(4);
        int rejected = 0;
        foreach (Action action in new Action[]
        {
            () =>
            {
                _ = reference.Value;
            },
            () => reference.Value = 99,
            () =>
            {
                _ = reference.DangerousGetPointer();
            },
            () =>
            {
                _ = tail.Value;
            },
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

        allocation.Reallocate(12, zeroNewMemory: true);
        int afterRegrowth = reference.Value;
        reference.Value = 91;
        using PgNativeBox<int> copy = reference.CloneOwnedInto(target);
        int first = allocation.Read<int>();
        allocation.Dispose();
        return $"{shared}|{moved}|{follows}|{afterGrowth}|{rejected}|{afterRegrowth}|{first}|{copy.Value}|" +
            $"{copy.Context.Id == target.Id}|{ReadOrStale(() => reference.Value)}|{IsDisposed(() =>
            {
                _ = tail.Value;
            })}";
    }

    /// <summary>
    /// Leaves raw generations live after a cleanup error rolls back and invalidates them when the remaining reset succeeds.
    /// </summary>
    /// <returns>The exact cleanup diagnostic, live partial value, pending callback order, and final stale references.</returns>
    [PgFunction]
    public static string NativeBoxFailedResetGeneration()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("raw generation failed reset");
        using PgNativeBox<int> box = owner.CreateBox(5);
        PgNativeReference<int> known = box.Borrow();
        PgNativeReference<int> raw = owner.DangerousBorrow<int>(box.DangerousGetPointer())
            ?? throw new InvalidOperationException("Missing raw reset witness.");
        var events = new List<string>();
        using PgMemoryCallback older = owner.RegisterResetCallback(() => events.Add($"A{raw.Value}"));
        using PgMemoryCallback newer = owner.RegisterResetCallback(() =>
        {
            events.Add($"B{raw.Value}");
            throw new PgException("22023", "raw generation reset failure", "raw detail", "raw hint");
        });
        string diagnostic = "no error";
        try
        {
            PgTransaction.RunInSubtransaction(owner.Reset);
        }
        catch (PgException error)
        {
            diagnostic = $"{error.SqlState}|{error.Message}|{error.Detail}|{error.Hint}";
        }

        int retained = raw.Value;
        raw.Value = 9;
        string partial = $"{owner.IsAlive}|{retained}|{known.Value}|{older.IsPending},{newer.IsPending}|{string.Join(',', events)}";
        owner.Reset();
        owner.Reset();
        return $"{diagnostic}|{partial}|{string.Join(',', events)}|{ReadOrStale(() => raw.Value)}," +
            $"{ReadOrStale(() => known.Value)}|{older.IsPending},{newer.IsPending}|{owner.IsAlive}";
    }

    /// <summary>
    /// Saves individual, transferred, or raw-adopted typed ownership under the selected transaction context.
    /// </summary>
    /// <param name="ownership">Individual ownership, release to context, or raw adoption into context ownership.</param>
    /// <param name="subtransaction">Whether the selected parent is the current subtransaction.</param>
    /// <returns>The shared typed and raw views observed before returning to SQL.</returns>
    [PgFunction]
    public static string NativeBoxSave(int ownership, bool subtransaction)
    {
        s_savedBox?.Dispose();
        s_savedOwner?.Dispose();
        s_savedValue = null;
        PgMemoryContext parent = PgMemoryContext.Get(subtransaction ? PgMemoryContextKind.CurTransaction : PgMemoryContextKind.TopTransaction)
            ?? throw new InvalidOperationException("No transaction for native box lifetime.");
        PgMemoryContext owner = PgMemoryContext.Create("saved native typed value", parent);
        s_savedOwner = owner;
        PgNativeBox<int> box = owner.CreateBox(5, PgAllocationOptions.Huge, BoxAlignment);
        s_savedBox = box;
        switch (ownership)
        {
            case 0:
                s_savedKnown = box.Borrow();
                break;
            case 1:
                s_savedKnown = box.Borrow();
                s_savedValue = box.ReleaseToContext();
                break;
            case 2:
                s_savedValue = owner.DangerousAdoptContextValue<int>(box.DangerousDetach(), huge: true, alignment: BoxAlignment);
                s_savedKnown = s_savedValue.Borrow();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(ownership));
        }

        s_savedRaw = owner.DangerousBorrow<int>(s_savedKnown.DangerousGetPointer())
            ?? throw new InvalidOperationException("Missing raw saved value.");
        s_savedRaw.Value = 73;
        return NativeBoxSavedState();
    }

    /// <summary>
    /// Reads saved native ownership and both alias forms from a later SQL callback.
    /// </summary>
    /// <returns>The source value, checked borrow, raw borrow, and native owner's current lifetime.</returns>
    [PgFunction]
    public static string NativeBoxSavedState()
    {
        PgNativeReference<int> known = s_savedKnown ?? throw new InvalidOperationException("No saved checked reference.");
        PgNativeReference<int> raw = s_savedRaw ?? throw new InvalidOperationException("No saved raw reference.");
        return $"{ReadOrStale(ReadSavedValue)}|{ReadOrStale(() => known.Value)}|{ReadOrStale(() => raw.Value)}|{s_savedOwner?.IsAlive}";
    }

    private static int ReadSavedValue()
        => s_savedValue is { } value ? value.Value : (s_savedBox ?? throw new InvalidOperationException("No saved native value.")).Value;

    private static string ReadOrStale(Func<int> read)
    {
        try
        {
            return read().ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        catch (ObjectDisposedException)
        {
            return "stale";
        }
    }

    private static bool IsDisposed(Action action)
    {
        try
        {
            action();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (nint Pointer, WeakReference Wrapper, WeakReference Reference) AllocateCollectible(PgMemoryContext owner, bool owned)
    {
        var contents = new LargeValue { First = 5, Last = 73 };
        if (owned)
        {
            PgNativeBox<LargeValue> box = owner.CreateBox(contents);
            PgNativeReference<LargeValue> reference = box.Borrow();
            return ((nint)box.DangerousGetPointer(), new WeakReference(box), new WeakReference(reference));
        }

        PgContextValue<LargeValue> value = owner.CreateContextValue(contents);
        PgNativeReference<LargeValue> borrowed = value.Borrow();
        return ((nint)value.DangerousGetPointer(), new WeakReference(value), new WeakReference(borrowed));
    }

    [StructLayout(LayoutKind.Explicit, Size = 32)]
    private struct PaddedValue
    {
        /// <summary>
        /// Identifies the value before its deliberately initialized padding.
        /// </summary>
        [FieldOffset(0)]
        public byte Tag;

        /// <summary>
        /// Refers to an independently owned native integer without acquiring ownership of it.
        /// </summary>
        [FieldOffset(8)]
        public nint Pointer;

        /// <summary>
        /// Carries an independently mutable scalar between padding regions.
        /// </summary>
        [FieldOffset(24)]
        public int Number;
    }

    [StructLayout(LayoutKind.Explicit, Size = 65536)]
    private struct LargeValue
    {
        /// <summary>
        /// Marks the beginning of the separately accounted native block.
        /// </summary>
        [FieldOffset(0)]
        public int First;

        /// <summary>
        /// Marks the final four bytes of the separately accounted native block.
        /// </summary>
        [FieldOffset(65532)]
        public int Last;
    }

    private struct DisposableValue(int number) : IDisposable
    {
        /// <summary>
        /// Carries the initialized value without any native destructor semantics.
        /// </summary>
        public int Number
        {
            get;
            private set;
        } = number;

        /// <summary>
        /// Records a managed disposal when explicitly invoked by the control witness.
        /// </summary>
        public void Dispose()
        {
            s_pointeeDisposals++;
            Number = -1;
        }
    }
}
