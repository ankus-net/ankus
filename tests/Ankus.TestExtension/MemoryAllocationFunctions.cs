using System.Text;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises typed, aligned, transferred, and transient allocations in PostgreSQL's real allocators.
/// </summary>
public static unsafe class MemoryAllocationFunctions
{
    private static PgMemoryContext? s_transferredOwner;
    private static PgAllocation? s_transferredValue;

    /// <summary>
    /// Copies distinct typed and byte values and verifies zero-sized and overflowing count boundaries.
    /// </summary>
    /// <returns>The native values, independent copies, exact byte lengths, and rejected count operations.</returns>
    [PgFunction]
    public static string MemoryTypedAllocations()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("typed allocation probe");
        using PgAllocation integers = owner.Allocate<long>(3);
        integers.Write(long.MinValue);
        integers.Write(731L, sizeof(long));
        integers.Write(long.MaxValue, sizeof(long) * 2);
        using PgAllocation zeroes = owner.AllocateZeroed<int>(4);
        using PgAllocation tried = owner.TryAllocate<short>(3, PgAllocationOptions.Zeroed)
            ?? throw new InvalidOperationException("A six-byte allocation unexpectedly failed.");
        byte[] source = [0, 1, 127, 128, 255];
        using PgAllocation copied = owner.CopyFrom(source);
        source.AsSpan().Fill(42);
        long[] typedSource = [-73, 0, 9123];
        using PgAllocation typedCopy = owner.CopyFrom<long>(typedSource);
        typedSource.AsSpan().Fill(99);
        using PgAllocation empty = owner.Allocate<long>(0);
        using PgAllocation emptyCopy = owner.CopyFrom([]);
        using PgAllocation emptyTypedCopy = owner.CopyFrom<long>([]);
        empty.Read([]);
        emptyCopy.Write([]);
        bool overflow = false;
        try
        {
            using PgAllocation invalid = owner.Allocate<long>(nuint.MaxValue / sizeof(long) + 1);
        }
        catch (OverflowException)
        {
            overflow = true;
        }

        string nativeLimit = CaptureState(() =>
        {
            using PgAllocation invalid = owner.Allocate<byte>(0x40000000);
        });
        return $"{integers.Length}|{integers.Read<long>()},{integers.Read<long>(sizeof(long))},{integers.Read<long>(sizeof(long) * 2)}|" +
            $"{Convert.ToHexString(ReadBytes(zeroes))}|{Convert.ToHexString(ReadBytes(tried))}|{Convert.ToHexString(ReadBytes(copied))}|" +
            $"{typedCopy.Length}|{typedCopy.Read<long>()},{typedCopy.Read<long>(sizeof(long))},{typedCopy.Read<long>(sizeof(long) * 2)}|" +
            $"{empty.Length},{emptyCopy.Length},{emptyTypedCopy.Length}|{empty.DangerousGetPointer() != null}|{overflow}|{nativeLimit}|{integers.Context.Id == owner.Id}";
    }

    /// <summary>
    /// Copies UTF-8 bytes independently of server encoding and rejects invalid C-string inputs.
    /// </summary>
    /// <returns>The exact terminated byte sequences, lengths, and invalid-input outcomes.</returns>
    [PgFunction]
    public static string MemoryUtf8Allocations()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("UTF8 allocation probe");
        using PgAllocation empty = owner.AllocateUtf8String(string.Empty);
        using PgAllocation text = owner.AllocateUtf8String("café 🐘e\u0301");
        int rejected = 0;
        try
        {
            using PgAllocation invalid = owner.AllocateUtf8String("before\0after");
        }
        catch (ArgumentException)
        {
            rejected++;
        }

        try
        {
            using PgAllocation invalid = owner.AllocateUtf8String("\ud800");
        }
        catch (EncoderFallbackException)
        {
            rejected++;
        }

        return $"{Convert.ToHexString(ReadBytes(empty))}|{empty.Length}|{Convert.ToHexString(ReadBytes(text))}|{text.Length}|{rejected}|{text.Context.Id == owner.Id}";
    }

    /// <summary>
    /// Retains thirty-two pgrx-sized blocks through aligned growth, shrinkage, and exact zero-extension.
    /// </summary>
    /// <param name="alignment">The requested native alignment.</param>
    /// <param name="huge">Whether to preserve the huge-allocation policy on these bounded allocations.</param>
    /// <param name="tryResize">Whether to use no-OOM allocation and resizing.</param>
    /// <returns>The number of witnesses satisfying each independent byte, alignment, and owner boundary.</returns>
    [PgFunction]
    public static string MemoryAlignedAllocations(int alignment, bool huge, bool tryResize)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("aligned allocation probe");
        PgAllocationOptions options = PgAllocationOptions.Zeroed | (huge ? PgAllocationOptions.Huge : PgAllocationOptions.None);
        var allocations = new List<PgAllocation>();
        int initiallyZero = 0;
        int alignmentMatches = 0;
        int grownPrefix = 0;
        int grownTail = 0;
        int shrunkPrefix = 0;
        int regrownTail = 0;
        int ownerMatches = 0;
        int policyMatches = 0;
        try
        {
            for (int index = 0; index < 32; index++)
            {
                PgAllocation value = tryResize
                    ? owner.TryAllocate(3319, options, (nuint)alignment) ?? throw new InvalidOperationException("A bounded aligned allocation failed.")
                    : owner.Allocate(3319, options, (nuint)alignment);
                allocations.Add(value);
                if (ReadBytes(value).AsSpan().IndexOfAnyExcept((byte)0) < 0)
                {
                    initiallyZero++;
                }

                byte[] pattern = Pattern(3319, index);
                value.Write(pattern);
                bool aligned = IsAligned(value, (nuint)alignment);
                Resize(value, 10003, tryResize);
                aligned &= IsAligned(value, (nuint)alignment);
                byte[] grown = ReadBytes(value);
                if (grown.AsSpan(0, pattern.Length).SequenceEqual(pattern))
                {
                    grownPrefix++;
                }

                if (grown.AsSpan(pattern.Length).IndexOfAnyExcept((byte)0) < 0)
                {
                    grownTail++;
                }

                Resize(value, 17, tryResize);
                aligned &= IsAligned(value, (nuint)alignment);
                if (ReadBytes(value).AsSpan().SequenceEqual(pattern.AsSpan(0, 17)))
                {
                    shrunkPrefix++;
                }

                Resize(value, 4111, tryResize);
                aligned &= IsAligned(value, (nuint)alignment);
                byte[] regrown = ReadBytes(value);
                if (regrown.AsSpan(0, 17).SequenceEqual(pattern.AsSpan(0, 17)) &&
                    regrown.AsSpan(17).IndexOfAnyExcept((byte)0) < 0)
                {
                    regrownTail++;
                }

                if (aligned)
                {
                    alignmentMatches++;
                }

                if (value.Context.Id == owner.Id)
                {
                    ownerMatches++;
                }

                if (value.Options == options && value.Alignment == (nuint)alignment && value.Length == 4111)
                {
                    policyMatches++;
                }
            }
        }
        finally
        {
            Disposal.DisposeAll(allocations);
        }

        owner.Reset();
        return $"{initiallyZero}|{alignmentMatches}|{grownPrefix}|{grownTail}|{shrunkPrefix}|{regrownTail}|{ownerMatches}|{policyMatches}|{owner.IsAlive}";
    }

    /// <summary>
    /// Rejects invalid native size and aligned-padding bounds without attempting an actual large allocation.
    /// </summary>
    /// <param name="huge">Whether the allocation uses the huge size bound.</param>
    /// <param name="aligned">Whether to test the aligned allocator's extra-byte bound.</param>
    /// <param name="operation">Allocate, try-allocate, resize, or try-resize.</param>
    /// <returns>The native error and unchanged original pointer, length, data, owner, and current context.</returns>
    [PgFunction]
    public static string MemoryAllocationLimits(bool huge, bool aligned, int operation)
    {
        PgMemoryContext original = PgMemoryContext.Current;
        using PgMemoryContext owner = PgMemoryContext.Create("allocation limit probe");
        PgAllocationOptions options = huge ? PgAllocationOptions.Huge : PgAllocationOptions.None;
        nuint alignment = aligned ? 4096u : 0u;
        using PgAllocation value = owner.Allocate(64, options, alignment);
        byte[] pattern = Pattern(64, 7);
        value.Write(pattern);
        nint originalPointer = (nint)value.DangerousGetPointer();
        nuint invalidSize = huge ? nuint.MaxValue / 2 : 0x3fffffff;
        if (!aligned)
        {
            invalidSize++;
        }

        string error = owner.Run(() => CaptureState(() =>
        {
            switch (operation)
            {
                case 0:
                    using (owner.Allocate(invalidSize, options, alignment))
                    {
                    }

                    break;
                case 1:
                    using (owner.TryAllocate(invalidSize, options, alignment))
                    {
                    }

                    break;
                case 2:
                    value.Reallocate(invalidSize, zeroNewMemory: true);
                    break;
                case 3:
                    value.TryReallocate(invalidSize, zeroNewMemory: true);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(operation));
            }
        }));
        return $"{error}|{(nint)value.DangerousGetPointer() == originalPointer}|{value.Length}|{ReadBytes(value).AsSpan().SequenceEqual(pattern)}|" +
            $"{value.Context.Id == owner.Id}|{value.Options == options}|{value.Alignment == alignment}|{PgMemoryContext.Current.Id == original.Id}";
    }

    /// <summary>
    /// Uses native AllocSet sizing presets and non-power-of-two blocks with a larger retained minimum.
    /// </summary>
    /// <param name="preset">Default, small, start-small, or custom sizing.</param>
    /// <returns>Independent native inventory and managed statistics before growth, after reset, and after deletion.</returns>
    [PgFunction]
    public static string MemoryContextSizing(int preset)
    {
        PgMemoryContextOptions options = preset switch
        {
            0 => PgMemoryContextOptions.Default,
            1 => PgMemoryContextOptions.Small,
            2 => PgMemoryContextOptions.StartSmall,
            3 => new PgMemoryContextOptions(6144, 3072, 24576),
            _ => throw new ArgumentOutOfRangeException(nameof(preset)),
        };
        PgMemoryContext original = PgMemoryContext.Current;
        using PgMemoryContext owner = PgMemoryContext.Create("allocation sizing probe", options: options);
        nuint initial = owner.GetAllocatedBytes();
        long nativeInitial = Spi.ExecuteScalar<long>("SELECT total_bytes FROM ankus_test_memory.contexts WHERE ident = 'allocation sizing probe'");
        using PgAllocation value = owner.AllocateZeroed(65536);
        value.Write(731);
        bool grew = owner.GetAllocatedBytes() > initial;
        int copied = value.Read<int>();
        owner.Reset();
        nuint afterReset = owner.GetAllocatedBytes();
        long nativeAfterReset = Spi.ExecuteScalar<long>("SELECT total_bytes FROM ankus_test_memory.contexts WHERE ident = 'allocation sizing probe'");
        owner.Dispose();
        long remaining = Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident = 'allocation sizing probe'");
        return $"{initial}|{nativeInitial}|{grew}|{copied}|{afterReset}|{nativeAfterReset}|{remaining}|{PgMemoryContext.Current.Id == original.Id}";
    }

    /// <summary>
    /// Forces ordinary AllocSet block growth to its custom maximum while retaining every earlier chunk.
    /// </summary>
    /// <returns>The native growth sequence, capped blocks, independent statistics, and retained payload contents.</returns>
    [PgFunction]
    public static string MemoryContextBlockGrowth()
    {
        PgMemoryContext original = PgMemoryContext.Current;
        using PgMemoryContext owner = PgMemoryContext.Create("allocation block growth probe",
            options: new PgMemoryContextOptions(6144, 3072, 24576));
        var values = new List<PgAllocation>();
        var growth = new List<nuint>();
        nuint previousBytes = owner.GetAllocatedBytes();
        (long initialBytes, long initialBlocks) = ReadBlockGrowthStatistics();
        bool nativeStatisticsMatch = initialBytes == (long)previousBytes && initialBlocks == 1;
        for (int index = 0; index < 200; index++)
        {
            PgAllocation value = owner.Allocate(1024);
            values.Add(value);
            value.Write(Pattern(1024, index));
            nuint bytes = owner.GetAllocatedBytes();
            if (bytes != previousBytes)
            {
                growth.Add(bytes - previousBytes);
                (long nativeBytes, long nativeBlocks) = ReadBlockGrowthStatistics();
                nativeStatisticsMatch &= nativeBytes == (long)bytes && nativeBlocks == initialBlocks + growth.Count;
                previousBytes = bytes;
            }
        }

        int retainedValues = 0;
        for (int index = 0; index < values.Count; index++)
        {
            if (ReadBytes(values[index]).AsSpan().SequenceEqual(Pattern(1024, index)))
            {
                retainedValues++;
            }
        }

        nuint maximumGrowth = growth.Count == 0 ? 0 : growth.Max();
        int cappedBlocks = growth.Count(static bytes => bytes == 24576);
        bool cappedRemainder = growth.Count > 3 && growth.Skip(3).All(static bytes => bytes == 24576);
        string firstGrowth = string.Join(',', growth.Take(3));
        owner.Reset();
        (long resetBytes, long resetBlocks) = ReadBlockGrowthStatistics();
        string stale = ReadOrStale(values[0]);
        owner.Dispose();
        long remaining = Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident = 'allocation block growth probe'");
        return $"{initialBytes}|{firstGrowth}|{maximumGrowth}|{cappedBlocks >= 3}|{cappedRemainder}|{nativeStatisticsMatch}|{retainedValues}|" +
            $"{resetBytes}|{resetBlocks}|{stale}|{remaining}|{PgMemoryContext.Current.Id == original.Id}";
    }

    /// <summary>
    /// Rejects target-native block alignment and offset limits before reaching PostgreSQL's allocator assertions.
    /// </summary>
    /// <param name="invalidPart">Minimum alignment, initial alignment, maximum alignment, or maximum block offset.</param>
    /// <returns>The native error, unchanged context inventory, and current-context restoration.</returns>
    [PgFunction]
    public static string MemoryContextNativeSizingError(int invalidPart)
    {
        PgMemoryContextOptions options = invalidPart switch
        {
            0 => new PgMemoryContextOptions(1025, 8192, 8192),
            1 => new PgMemoryContextOptions(0, 1025, 8192),
            2 => new PgMemoryContextOptions(0, 1024, 8193),
            3 => new PgMemoryContextOptions(0, 8192, 0x40000000),
            _ => throw new ArgumentOutOfRangeException(nameof(invalidPart)),
        };
        PgMemoryContext original = PgMemoryContext.Current;
        string state = CaptureState(() =>
        {
            using PgMemoryContext invalid = PgMemoryContext.Create("invalid allocation sizing", options: options);
        });
        long remaining = Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident = 'invalid allocation sizing'");
        return $"{state}|{remaining}|{PgMemoryContext.Current.Id == original.Id}";
    }

    /// <summary>
    /// Preserves transient action and cleanup errors, restores the caller, and invalidates escaped checked handles.
    /// </summary>
    /// <param name="mode">Success, managed failure, native failure, cleanup failure, both failures, or action-overload success.</param>
    /// <returns>The exact error evidence, partial cleanup state, retry order, and final native lifetime.</returns>
    [PgFunction]
    public static string MemoryTransientLifetime(int mode)
    {
        PgMemoryContext original = PgMemoryContext.Current;
        using PgMemoryContext parent = PgMemoryContext.Create("transient explicit parent");
        PgMemoryContext? escapedOwner = null;
        PgAllocation? escapedValue = null;
        PgMemoryCallback? older = null;
        PgMemoryCallback? newer = null;
        var events = new List<string>();
        var expected = new InvalidOperationException("transient action failure");
        bool selected = false;
        bool parentMatches = false;
        int result = 0;
        int Work(PgMemoryContext transient)
        {
            escapedOwner = transient;
            selected = PgMemoryContext.Current.Id == transient.Id;
            parentMatches = transient.Parent?.Id == parent.Id;
            PgAllocation value = transient.Allocate<int>();
            escapedValue = value;
            value.Write(91);
            older = transient.RegisterResetCallback(() => events.Add($"A{value.Read<int>()}"));
            newer = transient.RegisterResetCallback(() =>
            {
                events.Add($"B{value.Read<int>()}");
                if (mode is 3 or 4)
                {
                    throw new PgException("22023", "transient cleanup café", "transient detail", "transient hint");
                }
            });
            if (mode is 1 or 4)
            {
                throw expected;
            }

            if (mode == 2)
            {
                PgTransaction.RunInSubtransaction(() =>
                {
                    using PgAllocation invalid = transient.Allocate(0x40000000);
                });
            }

            return value.Read<int>() + 1;
        }

        string outcome;
        try
        {
            PgTransaction.RunInSubtransaction(() =>
            {
                if (mode == 5)
                {
                    PgMemoryContext.RunTransient("allocation transient", transient =>
                    {
                        result = Work(transient);
                    }, parent, PgMemoryContextOptions.Small);
                }
                else
                {
                    result = PgMemoryContext.RunTransient("allocation transient", Work, parent, PgMemoryContextOptions.Small);
                }
            });

            outcome = $"result:{result}";
        }
        catch (AggregateException error)
        {
            PgException cleanup = error.InnerExceptions.Count == 2 && error.InnerExceptions[1] is PgException native
                ? native : throw new InvalidOperationException("Transient cleanup did not preserve both errors.", error);
            outcome = $"combined:{ReferenceEquals(expected, error.InnerExceptions[0])}:{cleanup.SqlState}:{cleanup.Message}:{cleanup.Detail}:{cleanup.Hint}";
        }
        catch (PgException error)
        {
            outcome = mode == 2 ? $"native:{error.SqlState}" : $"cleanup:{error.SqlState}:{error.Message}:{error.Detail}:{error.Hint}";
        }
        catch (InvalidOperationException error)
        {
            outcome = $"managed:{ReferenceEquals(expected, error)}";
        }

        PgMemoryContext owner = escapedOwner ?? throw new InvalidOperationException("Transient work did not run.");
        PgAllocation allocation = escapedValue ?? throw new InvalidOperationException("Transient work did not allocate.");
        string partial = $"{owner.IsAlive}|{ReadOrStale(allocation)}|{older?.IsPending},{newer?.IsPending}|{string.Join(',', events)}";
        owner.Dispose();
        owner.Dispose();
        long remaining = Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident = 'allocation transient'");
        return $"{outcome}|{selected}|{parentMatches}|{partial}|{string.Join(',', events)}|{owner.IsAlive}|{ReadOrStale(allocation)}|" +
            $"{older?.IsPending},{newer?.IsPending}|{remaining}|{parent.IsAlive}|{PgMemoryContext.Current.Id == original.Id}";
    }

    /// <summary>
    /// Creates identically named nested transient contexts with independent identities and exact restoration.
    /// </summary>
    /// <returns>The selected identities, native inventory sequence, cleanup order, and stale escaped contexts.</returns>
    [PgFunction]
    public static string MemoryNestedTransients()
    {
        PgMemoryContext original = PgMemoryContext.Current;
        PgMemoryContext? outerHandle = null;
        PgMemoryContext? innerHandle = null;
        var events = new List<string>();
        var counts = new List<long>();
        bool innerParent = false;
        bool innerSelected = false;
        bool outerRestored = false;
        int result = PgMemoryContext.RunTransient("nested allocation transient", outer =>
        {
            outerHandle = outer;
            outer.RegisterResetCallback(() => events.Add("outer"));
            counts.Add(CountNestedTransients());
            int innerResult = PgMemoryContext.RunTransient("nested allocation transient", inner =>
            {
                innerHandle = inner;
                innerSelected = PgMemoryContext.Current.Id == inner.Id;
                innerParent = inner.Parent?.Id == outer.Id;
                inner.RegisterResetCallback(() => events.Add("inner"));
                counts.Add(CountNestedTransients());
                using PgAllocation value = inner.Allocate<int>();
                value.Write(41);
                return value.Read<int>();
            });
            outerRestored = PgMemoryContext.Current.Id == outer.Id;
            counts.Add(CountNestedTransients());
            return innerResult + 1;
        });
        counts.Add(CountNestedTransients());
        return $"{result}|{innerSelected}|{innerParent}|{outerRestored}|{outerHandle?.Id != innerHandle?.Id}|{string.Join(',', counts)}|" +
            $"{string.Join(',', events)}|{outerHandle?.IsAlive},{innerHandle?.IsAlive}|{PgMemoryContext.Current.Id == original.Id}";
    }

    /// <summary>
    /// Transfers a live pointer, rejects duplicate and wrong-owner adoption, and checks its new exclusive ownership.
    /// </summary>
    /// <param name="aligned">Whether the chunk is over-aligned.</param>
    /// <param name="huge">Whether the chunk retains the huge-allocation policy.</param>
    /// <param name="cleanup">Individual disposal, context reset, or parent deletion.</param>
    /// <returns>The exact transfer, adoption, resizing, and terminal lifetime observations.</returns>
    [PgFunction]
    public static string MemoryAllocationTransfer(bool aligned, bool huge, int cleanup)
    {
        using PgMemoryContext parent = PgMemoryContext.Create("transfer allocation parent");
        using PgMemoryContext owner = PgMemoryContext.Create("transfer allocation owner", parent);
        using PgMemoryContext wrongOwner = PgMemoryContext.Create("transfer wrong owner");
        PgAllocationOptions options = huge ? PgAllocationOptions.Huge : PgAllocationOptions.None;
        nuint alignment = aligned ? 4096u : 0u;
        using PgAllocation original = owner.Allocate(128, options, alignment);
        byte[] pattern = Pattern(128, 3);
        original.Write(pattern);
        nint pointer = (nint)original.DangerousGetPointer();
        string duplicate = CaptureState(() =>
        {
            using PgAllocation invalid = owner.DangerousAdopt((void*)pointer, 128, huge, alignment);
        });
        void* detached = original.DangerousDetach();
        bool samePointer = detached == (void*)pointer;
        string detachedState = ReadOrStale(original);
        original.Dispose();
        string wrong = CaptureState(() =>
        {
            using PgAllocation invalid = wrongOwner.DangerousAdopt((void*)pointer, 128, huge, alignment);
        });
        using PgAllocation adopted = owner.DangerousAdopt(detached, 128, huge, alignment);
        bool preserved = ReadBytes(adopted).AsSpan().SequenceEqual(pattern);
        bool actualOwner = adopted.Context.Id == owner.Id;
        original.Dispose();
        string oldAfterAdoption = ReadOrStale(original);
        adopted.Reallocate(256, zeroNewMemory: true);
        byte[] grown = ReadBytes(adopted);
        bool resized = grown.AsSpan(0, pattern.Length).SequenceEqual(pattern) && grown.AsSpan(pattern.Length).IndexOfAnyExcept((byte)0) < 0;
        bool policy = adopted.Options == options && adopted.Alignment == alignment && IsAligned(adopted, alignment);
        switch (cleanup)
        {
            case 0:
                adopted.Dispose();
                break;
            case 1:
                owner.Reset();
                break;
            case 2:
                parent.Dispose();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(cleanup));
        }

        adopted.Dispose();
        return $"{duplicate}|{samePointer}|{detachedState}|{original.Length}|{wrong}|{preserved}|{actualOwner}|{oldAfterAdoption}|" +
            $"{resized}|{policy}|{ReadOrStale(adopted)}|{owner.IsAlive}|{parent.IsAlive}";
    }

    /// <summary>
    /// Leaves detached bytes owned by their native context and verifies that reset reclaims their dedicated block.
    /// </summary>
    /// <returns>The detached byte contents, unchanged native ownership, and reclaimed block statistics.</returns>
    [PgFunction]
    public static string MemoryDetachedContextLifetime()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("detached native owner");
        nuint baseline = owner.GetAllocatedBytes();
        using PgAllocation allocation = owner.Allocate(65536);
        allocation.Write<byte>(17);
        allocation.Write<byte>(231, 65535);
        nuint allocated = owner.GetAllocatedBytes();
        byte* pointer = (byte*)allocation.DangerousDetach();
        allocation.Dispose();
        string values = $"{pointer[0]},{pointer[65535]}";
        bool retained = owner.GetAllocatedBytes() == allocated;
        owner.Reset();
        return $"{values}|{allocated > baseline}|{retained}|{owner.GetAllocatedBytes() == baseline}|{ReadOrStale(allocation)}|{owner.IsAlive}";
    }

    /// <summary>
    /// Retains an adopted aligned huge-policy chunk across SQL callbacks under a transaction-owned context.
    /// </summary>
    /// <param name="subtransaction">Whether the native owner belongs to the current subtransaction.</param>
    /// <param name="alignment">The alignment supported by the selected server.</param>
    /// <returns>The exact adopted value and native policy metadata.</returns>
    [PgFunction]
    public static string MemoryTransferredSave(bool subtransaction, int alignment)
    {
        s_transferredValue?.Dispose();
        s_transferredOwner?.Dispose();
        PgMemoryContext parent = PgMemoryContext.Get(subtransaction ? PgMemoryContextKind.CurTransaction : PgMemoryContextKind.TopTransaction)
            ?? throw new InvalidOperationException("No transaction context for the transferred allocation.");
        PgMemoryContext owner = PgMemoryContext.Create("saved transferred allocation", parent);
        s_transferredOwner = owner;
        using PgAllocation original = owner.AllocateZeroed<long>(4, PgAllocationOptions.Huge, (nuint)alignment);
        original.Write(731);
        void* pointer = original.DangerousDetach();
        PgAllocation adopted = owner.DangerousAdopt(pointer, sizeof(long) * 4, huge: true, alignment: (nuint)alignment);
        s_transferredValue = adopted;
        adopted.Reallocate(64, zeroNewMemory: true);
        return $"{adopted.Read<int>()}|{adopted.Context.Id == owner.Id}|{adopted.Length}|{(int)adopted.Options}|{adopted.Alignment}";
    }

    /// <summary>
    /// Inspects a saved adopted handle after PostgreSQL transaction or subtransaction cleanup.
    /// </summary>
    /// <returns>The owner and payload lifetime together with the retained allocation metadata.</returns>
    [PgFunction]
    public static string MemoryTransferredState()
    {
        PgMemoryContext owner = s_transferredOwner ?? throw new InvalidOperationException("No saved transferred owner.");
        PgAllocation value = s_transferredValue ?? throw new InvalidOperationException("No saved transferred allocation.");
        return $"{owner.IsAlive}|{ReadOrStale(value)}|{value.Length}|{(int)value.Options}|{value.Alignment}";
    }

    private static long CountNestedTransients()
        => Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident = 'nested allocation transient'");

    private static (long Bytes, long Blocks) ReadBlockGrowthStatistics()
    {
        SpiResult result = Spi.Query("SELECT total_bytes, total_nblocks FROM ankus_test_memory.contexts WHERE ident = 'allocation block growth probe'");
        if (result.Count != 1)
        {
            throw new InvalidOperationException("The block-growth context must have exactly one native statistics row.");
        }

        return (result[0].Get<long>(0), result[0].Get<long>(1));
    }

    private static byte[] ReadBytes(PgAllocation allocation)
    {
        byte[] bytes = new byte[checked((int)allocation.Length)];
        allocation.Read(bytes);
        return bytes;
    }

    private static byte[] Pattern(int length, int seed)
    {
        byte[] bytes = new byte[length];
        for (int index = 0; index < length; index++)
        {
            bytes[index] = (byte)((seed * 37 + index * 13) % 251);
        }

        return bytes;
    }

    private static bool IsAligned(PgAllocation allocation, nuint alignment)
        => alignment == 0 || (nuint)allocation.DangerousGetPointer() % alignment == 0;

    private static void Resize(PgAllocation allocation, nuint length, bool tryResize)
    {
        if (tryResize)
        {
            if (!allocation.TryReallocate(length, zeroNewMemory: true))
            {
                throw new InvalidOperationException("A bounded aligned resize unexpectedly failed.");
            }
        }
        else
        {
            allocation.Reallocate(length, zeroNewMemory: true);
        }
    }

    private static string CaptureState(Action action)
    {
        try
        {
            PgTransaction.RunInSubtransaction(action);
            return "no error";
        }
        catch (PgException error)
        {
            return error.SqlState;
        }
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
