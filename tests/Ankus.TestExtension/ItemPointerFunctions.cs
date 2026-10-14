namespace Ankus.TestExtension;

/// <summary>
/// Exercises exact tid conversion and native item-pointer ownership through PostgreSQL callbacks.
/// </summary>
public static unsafe class ItemPointerFunctions
{
    private static PgNativeItemPointer? s_saved;

    /// <summary>
    /// Copies a nullable location, preserving SQL NULL independently of offset zero.
    /// </summary>
    [PgFunction]
    public static PgItemPointer? ItemPointerEcho(PgItemPointer? value) => value;

    /// <summary>
    /// Calls PostgreSQL's static inline <c>itemptr_encode</c> through its generated binding, as pgrx's
    /// <c>test_static_inline_fns</c> does, beside the managed index key.
    /// </summary>
    /// <param name="block">The block number.</param>
    /// <param name="offset">The line pointer offset.</param>
    /// <returns>The native key and the managed key.</returns>
    [PgFunction]
    public static long[] ItemPointerEncodeBinding(long block, int offset)
    {
        var native = new Ankus.Postgres.ItemPointerData
        {
            ip_blkid = new Ankus.Postgres.BlockIdData { bi_hi = checked((ushort)(block >> 16)), bi_lo = checked((ushort)(block & 0xFFFF)) },
            ip_posid = checked((ushort)offset),
        };
        return [Ankus.Postgres.NativeMethods.itemptr_encode(&native), new PgItemPointer(checked((uint)block), checked((ushort)offset)).ToIndexKey()];
    }

    /// <summary>
    /// Copies a required location through a strict scalar declaration.
    /// </summary>
    [PgFunction]
    public static PgItemPointer ItemPointerRequired(PgItemPointer value) => value;

    /// <summary>
    /// Copies an ordinary vector including NULL cells.
    /// </summary>
    [PgFunction]
    public static PgItemPointer?[]? ItemPointerVector(PgItemPointer?[]? values) => values;

    /// <summary>
    /// Rejects NULL cells when the managed vector requires every location.
    /// </summary>
    [PgFunction]
    public static PgItemPointer[] ItemPointerRequiredVector(PgItemPointer[] values) => values;

    /// <summary>
    /// Copies a location array without losing dimensions, bounds or NULL cells.
    /// </summary>
    [PgFunction]
    public static PgArray<PgItemPointer?>? ItemPointerArray(PgArray<PgItemPointer?>? values) => values;

    /// <summary>
    /// Produces a location, SQL NULL and a present invalid sentinel through set-returning conversion.
    /// </summary>
    [PgFunction]
    public static IEnumerable<PgItemPointer?> ItemPointerSet(PgItemPointer? value) => [value, null, PgItemPointer.Invalid];

    /// <summary>
    /// Compares raw unsigned fields through a copied scalar boundary.
    /// </summary>
    [PgFunction]
    public static int ItemPointerCompare(PgItemPointer left, PgItemPointer right) => Math.Sign(left.CompareTo(right));

    /// <summary>
    /// Copies a tid result from a native function address, with optional raw-result ownership.
    /// </summary>
    [PgFunction]
    public static PgItemPointer ItemPointerDirect(long address, PgItemPointer left, PgItemPointer right, bool raw, PgFunctionContext call)
    {
        if (call.Arguments[1].Read<PgItemPointer>() != left || call.Arguments[2].Read<PgItemPointer>() != right)
        {
            throw new InvalidOperationException("Native tid operands differ from their managed values.");
        }

        if (!raw)
        {
            return PgFunctions.DangerousCall<PgItemPointer>((nint)address, 0, call.Arguments[1], call.Arguments[2]);
        }

        using PgMemoryContext owner = PgMemoryContext.Create("direct tid copy");
        return PgFunctions.DangerousCallRaw((nint)address, 27, owner, 0, call.Arguments[1], call.Arguments[2]).Read<PgItemPointer>();
    }

    /// <summary>
    /// Transfers selected-header storage into a raw tid result whose context retains native ownership.
    /// </summary>
    [PgFunction]
    [return: PgSqlType("tid", Schema = "pg_catalog")]
    public static PgDatum ItemPointerNativeDatum(PgItemPointer value)
    {
        PgMemoryContext owner = PgMemoryContext.Current;
        using PgNativeItemPointer native = PgNativeItemPointer.Create(value, owner);
        return PgDatum.DangerousCreate((nuint)native.DangerousDetach(), 27, owner);
    }

    /// <summary>
    /// Sends and reads tid through SPI and catalog calls, including tuple field materialization.
    /// </summary>
    [PgFunction]
    public static PgHeapTuple ItemPointerRoutes(PgItemPointer value)
    {
        PgItemPointer spi = Spi.ExecuteScalar<PgItemPointer>("SELECT $1", SpiParameter.Create(value));
        PgItemPointer called = PgFunctions.Call<PgItemPointer>("datatype.item_pointer_required", PgFunctionArgument.Create(spi));
        PgHeapTuple tuple = PgHeapTuple.Create(("location", SpiParameter.Create(called)), ("missing", SpiParameter.Create<PgItemPointer?>(null)));
        if (tuple.Get<PgItemPointer>("location") != value || tuple.Get<PgItemPointer?>("missing") is not null)
        {
            throw new InvalidOperationException("Tuple conversion lost the exact item pointer or NULL cell.");
        }

        return tuple;
    }

    /// <summary>
    /// Sends a shaped nullable array through SPI and returns its independent managed copy.
    /// </summary>
    [PgFunction]
    public static PgArray<PgItemPointer?> ItemPointerSpiArray(PgArray<PgItemPointer?> values)
        => Spi.ExecuteScalar<PgArray<PgItemPointer?>>("SELECT $1", SpiParameter.Create(values));

    /// <summary>
    /// Copies raw tid or domain storage and verifies independent values after the temporary owner is reset.
    /// </summary>
    [PgFunction]
    public static string ItemPointerRaw([PgSqlType("anyelement", Schema = "pg_catalog")] PgDatum value)
    {
        using PgMemoryContext temporary = PgMemoryContext.Create("tid raw copy");
        PgDatum borrowed = value.CopyTo(temporary);
        PgItemPointer location = borrowed.Read<PgItemPointer>();
        PgDatum copy = borrowed.CopyTo(PgMemoryContext.Current);
        bool wrongType = false;
        try
        {
            _ = borrowed.Read<uint>();
        }
        catch (InvalidCastException)
        {
            wrongType = true;
        }

        temporary.Reset();
        bool stale = IsStale(borrowed.Read<PgItemPointer>);
        return $"{value.TypeOid}|{copy.TypeOid}|{location}|{copy.Read<PgItemPointer>()}|{copy.ToPostgresString()}|{wrongType}|{stale}";
    }

    /// <summary>
    /// Reads selected-header native storage and tests borrowed disposal, cloning and both ownership transfer modes.
    /// </summary>
    [PgFunction]
    public static string ItemPointerNative()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("tid owner");
        using PgMemoryContext ambient = PgMemoryContext.Create("tid ambient");
        using PgNativeItemPointer value = ambient.Run(() => PgNativeItemPointer.Create(new(0x12345678, 0xabcd), owner));
        ambient.Dispose();
        string native = NativeDescribe(value);
        using PgNativeItemPointer borrowed = value.Borrow();
        borrowed.Value = PgItemPointer.Invalid;
        PgItemPointer copy = borrowed.Value;
        using PgNativeItemPointer clone = borrowed.CloneInto(PgMemoryContext.Current);
        bool same = borrowed.DangerousDetach() == value.DangerousGetPointer();
        using PgNativeItemPointer checkedBorrow = value.Borrow();
        void* address = value.DangerousDetach();
        bool stale = IsStale(() => checkedBorrow.Value);
        using PgNativeItemPointer external = PgNativeItemPointer.DangerousBorrow(address, owner)!;
        external.Value = new(uint.MaxValue, ushort.MaxValue);
        string detached = NativeDescribe(external);
        owner.Dispose();
        bool anchorStale = IsStale(() => external.Value);
        return $"{native}|{copy}|{clone.Value}|{same}|{stale}|{detached}|{anchorStale}";
    }

    /// <summary>
    /// Borrows native stack storage, preserving its guards and rejecting access after anchor reset.
    /// </summary>
    [PgFunction]
    public static string ItemPointerBorrow(PgInternal state, int mode)
    {
        using PgMemoryContext anchor = PgMemoryContext.Create("tid stack anchor");
        using PgNativeItemPointer? value = PgNativeItemPointer.DangerousBorrow((void*)state.Datum.DangerousGetBits(), anchor);
        if (mode == 2)
        {
            return value is null ? "null" : "unexpected";
        }

        if (value is null)
        {
            throw new InvalidOperationException("The borrowed tid is SQL NULL.");
        }

        PgItemPointer initial = value.Value;
        if (mode == 1)
        {
            anchor.Reset();
            return $"{initial}|{IsStale(() => value.Value)}";
        }

        value.Value = PgItemPointer.Invalid;
        using PgNativeItemPointer borrowed = value.Borrow();
        value.Dispose();
        return $"{initial}|{borrowed.Value}";
    }

    /// <summary>
    /// Reset and deletion expire owned, checked-borrowed and externally anchored views together while copies survive.
    /// </summary>
    [PgFunction]
    public static string ItemPointerLifetime(bool delete)
    {
        using PgMemoryContext owner = PgMemoryContext.Create("tid lifetime");
        using PgNativeItemPointer value = PgNativeItemPointer.Create(new(17, 31), owner);
        using PgNativeItemPointer borrowed = value.Borrow();
        using PgNativeItemPointer external = PgNativeItemPointer.DangerousBorrow(value.DangerousGetPointer(), owner)!;
        using PgNativeItemPointer clone = value.CloneInto(PgMemoryContext.Current);
        PgItemPointer copied = value.Value;
        if (delete)
        {
            owner.Dispose();
        }
        else
        {
            owner.Reset();
        }

        return $"{IsStale(() => value.Value)}|{IsStale(() => borrowed.Value)}|{IsStale(() => external.Value)}|{copied}|{clone.Value}";
    }

    /// <summary>
    /// Retains native storage across callbacks in the selected transaction context.
    /// </summary>
    [PgFunction]
    public static string ItemPointerSave(bool subtransaction)
    {
        s_saved?.Dispose();
        s_saved = PgNativeItemPointer.Create(new(17, 31), PgMemoryContext.Get(subtransaction ? PgMemoryContextKind.CurTransaction : PgMemoryContextKind.TopTransaction));
        return "saved";
    }

    /// <summary>
    /// Reads retained storage or reports native transaction cleanup through the checked wrapper.
    /// </summary>
    [PgFunction]
    public static string ItemPointerSaved()
    {
        if (s_saved is not { } saved)
        {
            return "missing";
        }

        try
        {
            return saved.Value.ToString();
        }
        catch (ObjectDisposedException)
        {
            saved.Dispose();
            s_saved = null;
            return "stale";
        }
    }

    /// <summary>
    /// Attempts individually owned allocation in a native Slab or Bump context and preserves native ERROR/finally behavior.
    /// </summary>
    [PgFunction]
    public static int ItemPointerAllocator()
    {
        PgMemoryContext owner = PgMemoryContext.Current;
        int result = 0;
        try
        {
            PgTransaction.RunInSubtransaction(() =>
            {
                using PgNativeItemPointer value = PgNativeItemPointer.Create(default, owner);
            });
        }
        catch (PgException error) when (error.SqlState == "XX000")
        {
            result = 10;
        }
        finally
        {
            result++;
        }

        return result;
    }

    /// <summary>
    /// Lets independent native code inspect exact block halves, offset, size and allocator owner.
    /// </summary>
    private static string NativeDescribe(PgNativeItemPointer value)
        => Spi.ExecuteScalar<string>("SELECT tests.item_pointer_describe($1)", SpiParameter.Create((long)value.DangerousGetPointer()));

    /// <summary>
    /// Distinguishes checked native invalidation from other errors or an accidentally successful access.
    /// </summary>
    private static bool IsStale(Func<PgItemPointer> action)
    {
        try
        {
            _ = action();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
