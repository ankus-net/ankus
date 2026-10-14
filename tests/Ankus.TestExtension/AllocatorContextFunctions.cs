using System.Globalization;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises borrowed native Slab, Generation, and Bump contexts supplied by the integration fixture.
/// </summary>
public static unsafe class AllocatorContextFunctions
{
    private static PgMemoryContext? s_owner;
    private static PgAllocation? s_saved;
    private static PgNativeReference<int>? s_known;
    private static PgNativeReference<int>? s_raw;
    private static PgMemoryCallback? s_first;
    private static PgMemoryCallback? s_second;
    private static readonly List<string> s_events = [];
    private static string s_name = "";

    /// <summary>
    /// Captures a special current context without returning a by-reference PostgreSQL datum.
    /// </summary>
    /// <param name="owner">The injected native allocator context.</param>
    /// <returns>A fixed by-value witness after reading the allocator's encoded name.</returns>
    [PgFunction]
    public static int AllocatorCapture(PgMemoryContext owner)
    {
        s_owner = owner;
        s_saved = null;
        s_known = null;
        s_raw = null;
        s_first = null;
        s_second = null;
        s_events.Clear();
        s_name = owner.Name;
        return owner.Id == PgMemoryContext.Current.Id ? 42 : -1;
    }

    /// <summary>
    /// Replays a managed error while the native fixture's special allocator remains current.
    /// </summary>
    /// <param name="owner">The borrowed context selected by the native fixture.</param>
    /// <returns>No value because the managed error is terminal.</returns>
    [PgFunction]
    public static int AllocatorCaptureError(PgMemoryContext owner)
    {
        AllocatorCapture(owner);
        throw new PgException("22023", "allocator café", "detail naïve", "hint déjà")
        {
            File = "allocator-fixture.cs",
            Line = 73,
            Routine = "AllocatorCaptureError",
        };
    }

    /// <summary>
    /// Reports a structured notice while a special native allocator remains current.
    /// </summary>
    /// <param name="owner">The borrowed context selected by the native fixture.</param>
    /// <returns>A by-value result after native notice reporting returns.</returns>
    [PgFunction]
    public static int AllocatorCaptureNotice(PgMemoryContext owner)
    {
        int result = AllocatorCapture(owner);
        PgLog.Write(PgLogLevel.Notice, new PgDiagnostic("allocator café")
        {
            SqlState = "01000",
            Detail = "detail naïve",
            Hint = "hint déjà",
            File = "allocator-fixture.cs",
            Line = 73,
            Routine = "AllocatorCaptureNotice",
        });
        return result;
    }

    /// <summary>
    /// Reads the name captured while the special allocator was current.
    /// </summary>
    /// <returns>The exact converted native context name.</returns>
    [PgFunction]
    public static string AllocatorCapturedName() => s_name;

    /// <summary>
    /// Allocates bounded native buffers using ordinary, zeroed, huge, and no-OOM paths.
    /// </summary>
    /// <param name="slab">Whether the native allocator requires fixed 64-byte chunks.</param>
    /// <param name="alignment">The requested native alignment.</param>
    /// <returns>Exact payload, zeroing, ownership, and accounting witnesses.</returns>
    [PgFunction]
    public static string AllocatorBasics(bool slab, int alignment)
    {
        PgMemoryContext owner = Owner();
        nuint before = owner.GetAllocatedBytes();
        long catalogBefore = CatalogBytes();
        bool empty = owner.IsEmpty;
        PgAllocation ordinary = owner.Allocate(64, alignment: (nuint)alignment);
        PgAllocation zero = owner.AllocateZeroed(64, alignment: (nuint)alignment);
        PgAllocation tried = owner.TryAllocate(64, PgAllocationOptions.Huge, (nuint)alignment)
            ?? throw new InvalidOperationException("A bounded allocation unexpectedly failed.");
        if (!slab)
        {
            _ = owner.Allocate(65536);
        }

        byte[] pattern = Pattern(64);
        ordinary.Write(pattern);
        tried.Write(pattern);
        PgNativeReference<int> known = ordinary.Borrow<int>();
        PgNativeReference<int> raw = owner.DangerousBorrow<int>(ordinary.DangerousGetPointer())
            ?? throw new InvalidOperationException("The allocated pointer became null.");
        raw.Value = 731;
        bool copied = ordinary.Read<int>() == 731 && known.Value == 731;
        ordinary.Write(pattern);
        bool owners = ordinary.Context.Id == owner.Id && zero.Context.Id == owner.Id && tried.Context.Id == owner.Id && raw.LifetimeContext.Id == owner.Id;
        bool aligned = (nuint)ordinary.DangerousGetPointer() % (nuint)alignment == 0 &&
            (nuint)zero.DangerousGetPointer() % (nuint)alignment == 0 && (nuint)tried.DangerousGetPointer() % (nuint)alignment == 0;
        long directDelta = checked((long)(owner.GetAllocatedBytes() - before));
        long catalogDelta = CatalogBytes() - catalogBefore;
        bool bytes = ReadBytes(ordinary).SequenceEqual(pattern) && ReadBytes(tried).SequenceEqual(pattern);
        bool zeros = ReadBytes(zero).All(static value => value == 0);
        return $"{empty}|{bytes}|{zeros}|{copied}|{owners}|{aligned}|{directDelta > 0}|{directDelta == catalogDelta}|{!owner.IsEmpty}";
    }

    /// <summary>
    /// Verifies individual chunk release and exact reuse without resetting the native owner.
    /// </summary>
    /// <returns>The remaining chunk, stale alias, and replacement contents.</returns>
    [PgFunction]
    public static string AllocatorIndividualFree()
    {
        PgMemoryContext owner = Owner();
        using PgAllocation first = owner.Allocate(64);
        PgAllocation control = owner.Allocate(64);
        first.Write(731);
        control.Write(9123);
        PgNativeReference<int> old = first.Borrow<int>();
        first.Dispose();
        first.Dispose();
        PgAllocation replacement = owner.AllocateZeroed(64);
        bool zeros = ReadBytes(replacement).All(static value => value == 0);
        replacement.Write(-73);
        return $"{ReadOrStale(() => old.Value)}|{control.Read<int>()}|{zeros}|{replacement.Read<int>()}|{replacement.Context.Id == owner.Id}|{owner.IsAlive}";
    }

    /// <summary>
    /// Exercises exact-size Slab resize or variable-size Generation resizing with preserved values.
    /// </summary>
    /// <param name="slab">Whether the native allocator requires exactly 64 bytes.</param>
    /// <param name="tryResize">Whether to use the no-OOM replacement path.</param>
    /// <returns>Prefix bytes, growth zeroing, checked aliases, and actual owners.</returns>
    [PgFunction]
    public static string AllocatorResize(bool slab, bool tryResize)
    {
        PgMemoryContext owner = Owner();
        int size = slab ? 64 : 65536;
        PgAllocation allocation = owner.Allocate((nuint)size);
        byte[] pattern = Pattern(size);
        allocation.Write(pattern);
        PgNativeReference<int> known = allocation.Borrow<int>();
        nint pointer = (nint)allocation.DangerousGetPointer();
        nuint grown = slab ? 64u : 131072u;
        bool succeeded = Resize(allocation, grown, tryResize);
        byte[] growth = ReadBytes(allocation);
        bool prefix = growth.AsSpan(0, size).SequenceEqual(pattern);
        bool zeroTail = growth.AsSpan(size).ToArray().All(static value => value == 0);
        bool stableSlab = !slab || tryResize || (nint)allocation.DangerousGetPointer() == pointer;
        bool resized = Resize(allocation, slab ? 64u : 32u, tryResize);
        bool suffix = ReadBytes(allocation).SequenceEqual(pattern.Take(slab ? 64 : 32));
        bool view = known.Value == BitConverter.ToInt32(pattern);
        return $"{succeeded},{resized}|{prefix}|{zeroTail}|{stableSlab}|{suffix}|{view}|{allocation.Context.Id == owner.Id}|{allocation.Length}";
    }

    /// <summary>
    /// Reclaims replaced external Generation blocks before resetting their owner.
    /// </summary>
    /// <returns>Exact payload, ownership, native and catalog byte-release, and final reset observations.</returns>
    [PgFunction]
    public static string AllocatorReplacementReclaims()
    {
        const int initialSize = 1024 * 1024;
        const int grownSize = 2 * initialSize;
        PgMemoryContext owner = Owner();
        nuint baseline = owner.GetAllocatedBytes();
        long catalogBaseline = CatalogBytes();
        // Keep a regular block available before measuring external-block release.
        // Older Generation allocators create that block only on the first small allocation.
        PgAllocation control = owner.Allocate(32);
        byte[] controlBytes = new byte[32];
        Array.Fill(controlBytes, (byte)0xa7);
        control.Write(controlBytes);
        nint controlAddress = (nint)control.DangerousGetPointer();
        PgAllocation allocation = owner.Allocate(initialSize);
        byte[] pattern = Pattern(initialSize);
        allocation.Write(pattern);
        PgNativeReference<int> known = allocation.Borrow<int>();
        bool grew = allocation.TryReallocate(grownSize, zeroNewMemory: true);
        byte[] growth = ReadBytes(allocation);
        bool prefix = growth.AsSpan(0, initialSize).SequenceEqual(pattern);
        bool zeroTail = growth.AsSpan(initialSize).IndexOfAnyExcept((byte)0) < 0;
        nuint grownBytes = owner.GetAllocatedBytes();
        long grownCatalog = CatalogBytes();
        bool shrank = allocation.TryReallocate(32, zeroNewMemory: true);
        bool suffix = ReadBytes(allocation).AsSpan().SequenceEqual(pattern.AsSpan(0, 32));
        bool view = known.Value == BitConverter.ToInt32(pattern);
        bool sameOwner = allocation.Context.Id == owner.Id;
        nuint length = allocation.Length;
        // Generation can retain the first freed block for reuse. The second
        // external block must be released when that cached slot is occupied.
        long releasedBytes = checked((long)(grownBytes - owner.GetAllocatedBytes()));
        long releasedCatalog = grownCatalog - CatalogBytes();
        bool controlUnchanged = (nint)control.DangerousGetPointer() == controlAddress &&
            ReadBytes(control).AsSpan().SequenceEqual(controlBytes);
        owner.Reset();
        return $"{grew}|{shrank}|{prefix}|{zeroTail}|{suffix}|{view}|{sameOwner}|{length}|" +
            $"{releasedBytes}|{releasedCatalog}|{owner.GetAllocatedBytes() == baseline}|{CatalogBytes() == catalogBaseline}|" +
            $"{ReadOrStale(() => known.Value)}|{controlUnchanged}";
    }

    /// <summary>
    /// Preserves a live control allocation after rolling back native size, resize, or unsupported free failures.
    /// </summary>
    /// <param name="operation">Allocate, resize, or individual free.</param>
    /// <param name="size">The attempted byte count.</param>
    /// <param name="tryOperation">Whether to use the no-OOM path.</param>
    /// <param name="alignment">The alignment of the original allocation.</param>
    /// <returns>The exact native diagnostics and unchanged handle, pointer, length, and payload.</returns>
    [PgFunction]
    public static string AllocatorOperationError(int operation, int size, bool tryOperation, int alignment)
    {
        PgMemoryContext owner = Owner();
        PgAllocation control = owner.Allocate(64, alignment: (nuint)alignment);
        byte[] pattern = Pattern(64);
        control.Write(pattern);
        nint pointer = (nint)control.DangerousGetPointer();
        PgNativeReference<int> known = control.Borrow<int>();
        string diagnostic = "returned";
        try
        {
            PgTransaction.RunInSubtransaction(() =>
            {
                if (operation == 0)
                {
                    _ = tryOperation ? owner.TryAllocate((nuint)size) : owner.Allocate((nuint)size);
                }
                else if (operation == 1)
                {
                    _ = Resize(control, (nuint)size, tryOperation);
                }
                else if (operation == 2)
                {
                    control.Dispose();
                }
                else
                {
                    _ = owner.Allocate(64, alignment: 4096);
                }
            });
        }
        catch (PgException error)
        {
            diagnostic = error.SqlState + ":" + error.Message;
        }

        return $"{diagnostic}|{control.Length}|{(nint)control.DangerousGetPointer() == pointer}|{ReadBytes(control).SequenceEqual(pattern)}|" +
            $"{known.Value == BitConverter.ToInt32(pattern)}|{control.Context.Id == owner.Id}|{owner.IsAlive}";
    }

    /// <summary>
    /// Exercises raw ownership transfer and rejection without dispatching through a Bump chunk header.
    /// </summary>
    /// <param name="bump">Whether raw adoption must be rejected by the native allocator.</param>
    /// <returns>Transfer invalidation, exact diagnostics, and remaining raw bytes before reset.</returns>
    [PgFunction]
    public static string AllocatorTransfer(bool bump)
    {
        PgMemoryContext owner = Owner();
        PgAllocation allocation = owner.Allocate(64);
        allocation.Write(731);
        PgNativeReference<int> old = allocation.Borrow<int>();
        void* pointer = allocation.DangerousDetach();
        PgNativeReference<int> raw = owner.DangerousBorrow<int>(pointer)
            ?? throw new InvalidOperationException("The detached pointer became null.");
        string diagnostic = "adopted";
        bool newOwner = false;
        try
        {
            PgAllocation adopted = PgTransaction.RunInSubtransaction(() => owner.DangerousAdopt(pointer, 64));
            newOwner = adopted.Context.Id == owner.Id && adopted.Read<int>() == 731;
            adopted.Write(9123);
        }
        catch (PgException error)
        {
            diagnostic = error.SqlState + ":" + error.Message;
        }

        string previous = ReadOrStale(() => old.Value);
        int rawValue = raw.Value;
        owner.Reset();
        return $"{diagnostic}|{newOwner == !bump}|{previous}|{rawValue}|{ReadOrStale(() => raw.Value)}|{owner.IsAlive}";
    }

    /// <summary>
    /// Creates typed context values and proves failed individual Bump disposal retains ownership for release.
    /// </summary>
    /// <param name="bump">Whether owned disposal must fail before release to the native context.</param>
    /// <returns>The context-owned values, copied writes, exact disposal result, and reset expiration.</returns>
    [PgFunction]
    public static string AllocatorTypedValues(bool bump)
    {
        PgMemoryContext owner = Owner();
        PgAllocation empty = owner.Allocate(0);
        empty.Read([]);
        PgContextValue<byte> tried = owner.TryCreateContextValue((byte)231)
            ?? throw new InvalidOperationException("A one-byte context value unexpectedly failed.");
        byte initialTried = tried.Value;
        tried.Value = 17;
        PgContextValue<int> value = owner.CreateContextValue(5);
        PgContextValue<int> zero = owner.AllocateZeroedBox<int>().ReleaseToContext();
        PgNativeBox<int> box = owner.CreateBox(17);
        PgNativeReference<int> borrow = box.Borrow();
        string diagnostic = "released";
        if (bump)
        {
            try
            {
                PgTransaction.RunInSubtransaction(box.Dispose);
            }
            catch (PgException error)
            {
                diagnostic = error.SqlState + ":" + error.Message;
            }
        }

        PgContextValue<int> released = box.ReleaseToContext();
        borrow.Value = 731;
        string values = $"{empty.Length}|{value.Value},{zero.Value},{released.Value},{borrow.Value},{initialTried},{tried.Value}";
        bool owners = value.Context.Id == owner.Id && zero.Context.Id == owner.Id && released.Context.Id == owner.Id && tried.Context.Id == owner.Id;
        owner.Reset();
        return $"{diagnostic}|{values}|{owners}|{ReadOrStale(() => value.Value)},{ReadOrStale(() => released.Value)}," +
            $"{ReadOrStale(() => borrow.Value)},{ReadOrStale(() => tried.Value)}";
    }

    /// <summary>
    /// Retains checked and raw aliases for reset, deletion, and transaction cleanup probes.
    /// </summary>
    /// <returns>The initial payload and pending callback state.</returns>
    [PgFunction]
    public static string AllocatorSave()
    {
        PgMemoryContext owner = Owner();
        s_saved = owner.Allocate(64);
        s_saved.Write(731);
        s_known = s_saved.Borrow<int>();
        s_raw = owner.DangerousBorrow<int>(s_saved.DangerousGetPointer())
            ?? throw new InvalidOperationException("The saved pointer became null.");
        s_first = owner.RegisterResetCallback(() => s_events.Add($"A{Known().Value}:{s_first?.IsPending}"));
        s_second = owner.RegisterResetCallback(() => s_events.Add($"B{Raw().Value}:{s_second?.IsPending}"));
        return AllocatorSavedState();
    }

    /// <summary>
    /// Resets twice with a fresh captured generation between, observing native empty state before reallocation.
    /// </summary>
    /// <returns>Exact callback order, old and fresh generation expiration, and surviving context identity.</returns>
    [PgFunction]
    public static string AllocatorResetTwice()
    {
        PgMemoryContext owner = Owner();
        nint identity = owner.Id;
        owner.Reset();
        string first = AllocatorSavedState();
        bool empty = owner.IsEmpty;
        PgAllocation fresh = owner.Allocate(64);
        fresh.Write(9123);
        PgNativeReference<int> raw = owner.DangerousBorrow<int>(fresh.DangerousGetPointer())
            ?? throw new InvalidOperationException("The fresh pointer became null.");
        int value = raw.Value;
        owner.Reset();
        return $"{first}|{empty}|{value}|{ReadOrStale(() => raw.Value)}|{owner.Id == identity}|{string.Join(',', s_events)}|{owner.IsEmpty}";
    }

    /// <summary>
    /// Reads retained managed handles after explicit or implicit native cleanup.
    /// </summary>
    /// <returns>The exact live state, values, one-shot callback state, and callback order.</returns>
    [PgFunction]
    public static string AllocatorSavedState()
        => $"{Owner().IsAlive}|{ReadOrStale(() => Saved().Read<int>())}|{ReadOrStale(() => Known().Value)}|" +
            $"{ReadOrStale(() => Raw().Value)}|{s_first?.IsPending},{s_second?.IsPending}|{string.Join(',', s_events)}";

    private static PgMemoryContext Owner() => s_owner ?? throw new InvalidOperationException("No allocator was captured.");

    private static PgAllocation Saved() => s_saved ?? throw new InvalidOperationException("No allocation was saved.");

    private static PgNativeReference<int> Known() => s_known ?? throw new InvalidOperationException("No checked reference was saved.");

    private static PgNativeReference<int> Raw() => s_raw ?? throw new InvalidOperationException("No raw reference was saved.");

    private static bool Resize(PgAllocation allocation, nuint size, bool tryResize)
    {
        if (tryResize)
        {
            return allocation.TryReallocate(size, zeroNewMemory: true);
        }

        allocation.Reallocate(size, zeroNewMemory: true);
        return true;
    }

    private static byte[] Pattern(int count)
        => [.. Enumerable.Range(0, count).Select(static index => (byte)(index % 251))];

    private static byte[] ReadBytes(PgAllocation allocation)
    {
        byte[] bytes = new byte[checked((int)allocation.Length)];
        allocation.Read(bytes);
        return bytes;
    }

    private static long CatalogBytes()
        => Spi.ExecuteScalar<long>("SELECT total_bytes FROM ankus_test_memory.contexts WHERE name = 'Ankus fixture allocator'");

    private static string ReadOrStale(Func<int> read)
    {
        try
        {
            return read().ToString(CultureInfo.InvariantCulture);
        }
        catch (ObjectDisposedException)
        {
            return "stale";
        }
    }
}
