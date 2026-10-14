using System.Globalization;
using System.Runtime.InteropServices;
using Block = Ankus.TestExtension.NativeLayoutTypeFunctions.Block;
using Packet = Ankus.TestExtension.NativeLayoutTypeFunctions.Packet;

namespace Ankus.TestExtension;

/// <summary>
/// Observes native varlena ownership through real callbacks without manufacturing native addresses.
/// </summary>
[PgSchema("varlena_ownership")]
public static class VarlenaOwnershipFunctions
{
    private static PgVarlena<Packet>? s_saved;
    private static PgMemoryContext? s_owner;
    private static PgVarlena<Packet>? s_outer;
    private static PgVarlena<Packet>? s_inner;
    private static PgVarlena<Packet>? s_setSaved;
    private static PgVarlena<Packet>? s_aggregateSaved;
    private static int s_setDisposals;

    /// <summary>
    /// Compares live input identity and both writes while preserving the original datum.
    /// </summary>
    [PgFunction(Requires = ["native_layout.packet"])]
    public static unsafe string VarlenaProbe(PgVarlena<Packet> value,
        [PgSqlType("packet", Schema = "native_layout")] PgDatum original)
    {
        bool borrowed = value.IsBorrowed;
        nuint before = (nuint)value.DangerousGetPointer();
        bool same = before == original.DangerousGetBits();
        Packet copy = value.Value;
        int number = copy.Leaf.Number;
        copy.Leaf.Number = number + 100;
        bool readUnchanged = value.Value.Leaf.Number == number && (nuint)value.DangerousGetPointer() == before;
        copy.Leaf.Number = number + 1;
        value.Value = copy;
        nuint written = (nuint)value.DangerousGetPointer();
        bool changed = written != before;
        copy.Leaf.Number = number + 2;
        value.Value = copy;
        return $"{borrowed}|{same}|{readUnchanged}|{changed}|{value.IsBorrowed}|{written == (nuint)value.DangerousGetPointer()}|" +
            $"{original.Read<Packet>().Leaf.Number}|{value.Value.Leaf.Number}";
    }

    /// <summary>
    /// Mutates a detoasted block and separately reads the untouched PostgreSQL source.
    /// </summary>
    [PgFunction]
    public static unsafe string VarlenaBlockProbe(PgVarlena<Block> value, Block original)
    {
        bool borrowed = value.IsBorrowed;
        nuint before = (nuint)value.DangerousGetPointer();
        Block copy = value.Value;
        byte first = copy.Bytes[0];
        byte last = copy.Bytes[8191];
        copy.Bytes[0] = 99;
        value.Value = copy;
        Block after = value.Value;
        return $"{borrowed}|{before != (nuint)value.DangerousGetPointer()}|{first}|{last}|{after.Bytes[0]}|{original.Bytes[0]}";
    }

    /// <summary>
    /// Exchanges wrapper arguments through native and SPI paths without changing their SQL mapping.
    /// </summary>
    [PgFunction]
    public static PgVarlena<Packet>? VarlenaEcho(PgVarlena<Packet>? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Exchanges shaped wrapper arrays whose elements own independent native storage.
    /// </summary>
    [PgFunction]
    public static PgArray<PgVarlena<Packet>?>? VarlenaArray(PgArray<PgVarlena<Packet>?>? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Exchanges wrapper vectors while preserving their declared element type.
    /// </summary>
    [PgFunction]
    public static PgVarlena<Packet>?[]? VarlenaVector(PgVarlena<Packet>?[]? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Verifies canonical managed SPI cells and reversible typed wrapper conversion without consuming an alias.
    /// </summary>
    [PgFunction]
    public static unsafe string VarlenaConversions(PgVarlena<Packet> value)
    {
        nuint pointer = (nuint)value.DangerousGetPointer();
        SpiParameter parameter = SpiParameter.Create(value);
        SpiRow row = Spi.Query("SELECT $1", parameter)[0];
        bool canonical = row[0] is Packet;
        using PgVarlena<Packet> first = row.Get<PgVarlena<Packet>>(0);
        using PgVarlena<Packet> second = row.Get<PgVarlena<Packet>>(0);
        bool independent = first.DangerousGetPointer() != second.DangerousGetPointer();
        Packet changed = first.Value;
        changed.Leaf.Number = 91;
        first.Value = changed;
        int bare = row.Get<Packet>(0).Leaf.Number;
        row.Set(0, first);
        int inverse = row.Get<Packet>(0).Leaf.Number;
        Packet echoed = Spi.ExecuteScalar<Packet>("SELECT $1", parameter);
        return $"{canonical}|{independent}|{first.IsBorrowed}|{second.Value.Leaf.Number}|{bare}|{inverse}|{echoed.Leaf.Number}|" +
            $"{value.Value.Leaf.Number}|{pointer == (nuint)value.DangerousGetPointer()}|{parameter.TypeOid == row.GetTypeOid(0)}";
    }

    /// <summary>
    /// Converts canonical shaped arrays to wrappers and back while preserving bounds and NULL elements.
    /// </summary>
    [PgFunction]
    public static string VarlenaArrayConversions(PgArray<Packet?> value)
    {
        SpiRow row = Spi.Query("SELECT $1", SpiParameter.Create(value))[0];
        bool canonical = row[0] is PgArray<Packet?>;
        PgArray<PgVarlena<Packet>?> wrappers = row.Get<PgArray<PgVarlena<Packet>?>>(0);
        bool owned = wrappers.Where(static item => item is not null).All(static item => !item!.IsBorrowed);
        row.Set(0, wrappers);
        PgArray<Packet?> bare = row.Get<PgArray<Packet?>>(0);
        string result = $"{canonical}|{owned}|{string.Join(',', bare.Lengths.ToArray())}|{string.Join(',', bare.LowerBounds.ToArray())}|" +
            string.Join(',', bare.Select(static item => item?.Leaf.Number.ToString(CultureInfo.InvariantCulture) ?? "NULL"));
        foreach (PgVarlena<Packet>? wrapper in wrappers)
        {
            wrapper?.Dispose();
        }

        return result;
    }

    /// <summary>
    /// Reassigns tuple cells using alternate wrapper representations of the same SQL type.
    /// </summary>
    [PgFunction]
    public static PgHeapTuple VarlenaTuple(PgHeapTuple value, int mode)
    {
        PgHeapTuple copy = value.Clone();
        copy.Set(0, copy.Get<PgVarlena<Packet>?>(0));
        copy.Set(1, copy.Get<PgArray<PgVarlena<Packet>?>?>(1));
        return ArrayFunctions.Exchange(copy, mode);
    }

    /// <summary>
    /// Consumes only explicit transfer while the transferred datum remains independently readable.
    /// </summary>
    [PgFunction]
    public static unsafe string VarlenaTransfer(int number)
    {
        using var value = new PgVarlena<Packet>(NativeLayoutTypeFunctions.NativeMake(number));
        PgVarlena<Packet> alias = value;
        nuint pointer = (nuint)value.DangerousGetPointer();
        PgDatum datum = value.IntoDatum();
        bool same = pointer == datum.DangerousGetBits();
        int rejected = Reject(alias);
        value.Dispose();
        Packet read = datum.Read<Packet>();
        Packet rebound = Spi.ExecuteScalar<Packet>("SELECT $1", SpiParameter.Create(datum));
        return $"{same}|{rejected}|{read.Leaf.Number}|{rebound.Leaf.Number}|{datum.TypeOid == SpiParameter.Create(read).TypeOid}";
    }

    /// <summary>
    /// Copies an input before transfer so native input cleanup cannot invalidate the returned datum.
    /// </summary>
    [PgFunction(Requires = ["native_layout.packet"])]
    public static unsafe string VarlenaInputTransfer(PgVarlena<Packet> value,
        [PgSqlType("packet", Schema = "native_layout")] PgDatum original)
    {
        nuint pointer = (nuint)value.DangerousGetPointer();
        PgDatum datum = value.IntoDatum();
        return $"{pointer != datum.DangerousGetBits()}|{Reject(value)}|{datum.Read<Packet>().Leaf.Number}|{original.Read<Packet>().Leaf.Number}";
    }

    /// <summary>
    /// Captures scoped, promoted or explicitly cloned values for later callback validation.
    /// </summary>
    [PgFunction]
    public static int VarlenaCapture(PgVarlena<Packet> value, int mode)
    {
        VarlenaClear();
        switch (mode)
        {
            case 0:
                s_saved = value;
                break;
            case 1:
                Packet changed = value.Value;
                changed.Leaf.Number++;
                value.Value = changed;
                s_saved = value;
                break;
            case 2:
                s_saved = value.Clone();
                break;
            case 3:
                s_owner = PgMemoryContext.Create("retained varlena ownership", PgMemoryContext.Get(PgMemoryContextKind.TopTransaction));
                s_saved = value.Clone(s_owner);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return mode;
    }

    /// <summary>
    /// Reads after an argument-producing callback has exited in the same executor expression.
    /// </summary>
    [PgFunction]
    public static string VarlenaSavedAfter(int token) => token >= 0 ? Saved(s_saved) : throw new ArgumentOutOfRangeException(nameof(token));

    /// <summary>
    /// Returns an owned value while retaining the same C# alias in an explicit transaction context.
    /// </summary>
    [PgFunction]
    public static PgVarlena<Packet> VarlenaRetainedReturn(int number)
    {
        VarlenaClear();
        s_owner = PgMemoryContext.Create("retained returned varlena", PgMemoryContext.Get(PgMemoryContextKind.TopTransaction));
        s_saved = new PgVarlena<Packet>(NativeLayoutTypeFunctions.NativeMake(number), s_owner);
        return s_saved;
    }

    /// <summary>
    /// Lets the managed error boundary report stale input rather than converting it to a status string.
    /// </summary>
    [PgFunction]
    public static int VarlenaExpiredRead() => s_saved!.Value.Leaf.Number;

    /// <summary>
    /// Reads a raw datum through the alternate wrapper mapping with exact SQL identity validation.
    /// </summary>
    [PgFunction]
    public static int VarlenaRawRead([PgSqlType("anyelement", Schema = "pg_catalog")] PgDatum value)
    {
        using PgVarlena<Packet> wrapper = value.Read<PgVarlena<Packet>>();
        return wrapper.Value.Leaf.Number;
    }

    /// <summary>
    /// Expires the explicit clone destination without touching already freed input memory.
    /// </summary>
    [PgFunction]
    public static string VarlenaResetOwner()
    {
        s_owner!.Reset();
        return Saved(s_saved);
    }

    /// <summary>
    /// Releases deliberate retained state after a lifetime probe.
    /// </summary>
    [PgFunction]
    public static void VarlenaClear()
    {
        s_saved?.Dispose();
        s_saved = null;
        s_owner?.Dispose();
        s_owner = null;
    }

    /// <summary>
    /// Keeps an outer scalar lease valid while a nested SPI callback creates and expires another lease.
    /// </summary>
    [PgFunction]
    public static string VarlenaNested(PgVarlena<Packet> value)
    {
        s_outer = value;
        try
        {
            string inner = Spi.ExecuteScalar<string>("SELECT varlena_ownership.varlena_inner(native_layout.native_make(7))");
            return inner + "|" + value.Value.Leaf.Number.ToString(CultureInfo.InvariantCulture) + "|" + Saved(s_inner);
        }
        finally
        {
            s_outer = null;
            Disposal.Release(ref s_inner);
        }
    }

    /// <summary>
    /// Reads the ancestor lease and deliberately retains this shorter input lease.
    /// </summary>
    [PgFunction]
    public static string VarlenaInner(PgVarlena<Packet> value)
    {
        s_inner = value;
        return s_outer!.Value.Leaf.Number.ToString(CultureInfo.InvariantCulture) + ":" + value.Value.Leaf.Number.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Reads promoted input only when the deferred iterator is actually advanced.
    /// </summary>
    [PgFunction(SetMode = PgSetMode.ValuePerCall)]
    public static IEnumerable<PgVarlena<Packet>?> VarlenaRows(PgVarlena<Packet>? value)
    {
        s_setSaved = value;
        try
        {
            if (value is not null)
            {
                _ = value.Value;
                if (value.IsBorrowed)
                {
                    throw new InvalidOperationException("Retained set input was borrowed.");
                }
            }

            yield return value;
            yield return null;
            if (value is not null)
            {
                Packet next = value.Value;
                next.Leaf.Number++;
                using var created = new PgVarlena<Packet>(next);
                yield return created;
            }
        }
        finally
        {
            s_setDisposals++;
        }
    }

    /// <summary>
    /// Exercises the same deferred body through materialized set-returning execution.
    /// </summary>
    [PgFunction(SetMode = PgSetMode.Materialize)]
    public static IEnumerable<PgVarlena<Packet>?> VarlenaMaterialized(PgVarlena<Packet>? value) => VarlenaRows(value);

    /// <summary>
    /// Accesses each promoted array element only after iterator initialization has returned.
    /// </summary>
    [PgFunction(SetMode = PgSetMode.ValuePerCall)]
    public static IEnumerable<PgVarlena<Packet>?> VarlenaArrayRows(PgArray<PgVarlena<Packet>?> values)
    {
        s_setSaved = null;
        try
        {
            foreach (PgVarlena<Packet>? value in values)
            {
                if (value is not null)
                {
                    _ = value.Value;
                    if (value.IsBorrowed)
                    {
                        throw new InvalidOperationException("Retained set array element was borrowed.");
                    }

                    s_setSaved = value;
                }

                yield return value;
            }
        }
        finally
        {
            s_setDisposals++;
        }
    }

    /// <summary>
    /// Reports deferred iterator disposal and stale retained input after SQL execution completes.
    /// </summary>
    [PgFunction]
    public static string VarlenaSetState() => s_setDisposals.ToString(CultureInfo.InvariantCulture) + "|" + Saved(s_setSaved);

    /// <summary>
    /// Retains wrapper arguments across aggregate transitions and finalization.
    /// </summary>
    [PgAggregate(Name = "varlena_collect")]
    public sealed class Collect : IPgAggregate<PgAggregateState<List<PgVarlena<Packet>?>>?, PgVarlena<Packet>?>,
        IPgFinalizingAggregate<PgAggregateState<List<PgVarlena<Packet>?>>?, ValueTuple, string>
    {
        /// <summary>
        /// Rechecks every previously retained argument before retaining the current one.
        /// </summary>
        public static PgAggregateState<List<PgVarlena<Packet>?>> Transition(
            PgAggregateContext context, PgAggregateState<List<PgVarlena<Packet>?>>? state, PgVarlena<Packet>? value)
        {
            state ??= new([]);
            foreach (PgVarlena<Packet> previous in state.Value.OfType<PgVarlena<Packet>>())
            {
                _ = previous.Value;
            }

            if (value?.IsBorrowed == true)
            {
                throw new InvalidOperationException("Retained aggregate input was borrowed.");
            }

            s_aggregateSaved = value ?? s_aggregateSaved;
            state.Value.Add(value);
            return state;
        }

        /// <summary>
        /// Reads actual wrapper payloads only after all transition callbacks have returned.
        /// </summary>
        public static string Final(PgAggregateContext context, PgAggregateState<List<PgVarlena<Packet>?>>? state, ValueTuple arguments) => state is null ? "empty" :
            string.Join(',', state.Value.Select(static value => value?.Value.Leaf.Number.ToString(CultureInfo.InvariantCulture) ?? "NULL"));
    }

    /// <summary>
    /// Checks an aggregate-retained native value after the aggregate memory owner is gone.
    /// </summary>
    [PgFunction]
    public static string VarlenaAggregateState() => Saved(s_aggregateSaved);

    /// <summary>
    /// Retains all wrapper elements and dimensions across aggregate callbacks.
    /// </summary>
    [PgAggregate(Name = "varlena_collect_arrays")]
    public sealed class CollectArrays : IPgAggregate<PgAggregateState<List<PgArray<PgVarlena<Packet>?>?>>?, PgArray<PgVarlena<Packet>?>?>,
        IPgFinalizingAggregate<PgAggregateState<List<PgArray<PgVarlena<Packet>?>?>>?, ValueTuple, string>
    {
        /// <summary>
        /// Reads preceding arrays after their transition callback and temporary memory have ended.
        /// </summary>
        public static PgAggregateState<List<PgArray<PgVarlena<Packet>?>?>> Transition(
            PgAggregateContext context, PgAggregateState<List<PgArray<PgVarlena<Packet>?>?>>? state, PgArray<PgVarlena<Packet>?>? value)
        {
            state ??= new([]);
            foreach (PgArray<PgVarlena<Packet>?>? previous in state.Value)
            {
                _ = DescribeArray(previous);
            }

            state.Value.Add(value);
            return state;
        }

        /// <summary>
        /// Observes retained values, NULL elements and nondefault lower bounds.
        /// </summary>
        public static string Final(PgAggregateContext context, PgAggregateState<List<PgArray<PgVarlena<Packet>?>?>>? state, ValueTuple arguments) => state is null ? "empty" :
            string.Join(';', state.Value.Select(DescribeArray));
    }

    /// <summary>
    /// Retains wrapper vectors across aggregate callbacks independently of shaped arrays.
    /// </summary>
    [PgAggregate(Name = "varlena_collect_vectors")]
    public sealed class CollectVectors : IPgAggregate<PgAggregateState<List<PgVarlena<Packet>?[]?>>?, PgVarlena<Packet>?[]?>,
        IPgFinalizingAggregate<PgAggregateState<List<PgVarlena<Packet>?[]?>>?, ValueTuple, string>
    {
        /// <summary>
        /// Rechecks prior vector elements before accepting another transition input.
        /// </summary>
        public static PgAggregateState<List<PgVarlena<Packet>?[]?>> Transition(
            PgAggregateContext context, PgAggregateState<List<PgVarlena<Packet>?[]?>>? state, PgVarlena<Packet>?[]? value)
        {
            state ??= new([]);
            foreach (PgVarlena<Packet>?[] previous in state.Value.OfType<PgVarlena<Packet>?[]>())
            {
                foreach (PgVarlena<Packet> item in previous.OfType<PgVarlena<Packet>>())
                {
                    _ = item.Value;
                }
            }

            state.Value.Add(value);
            return state;
        }

        /// <summary>
        /// Reads every retained vector element during finalization.
        /// </summary>
        public static string Final(PgAggregateContext context, PgAggregateState<List<PgVarlena<Packet>?[]?>>? state, ValueTuple arguments) => state is null ? "empty" :
            string.Join(';', state.Value.Select(static value => value is null ? "NULL" : string.Join(',', value.Select(Saved))));
    }

    /// <summary>
    /// Describes a retained shaped array through live payload access.
    /// </summary>
    private static string DescribeArray(PgArray<PgVarlena<Packet>?>? value) => value is null ? "NULL" :
        string.Join(',', value.LowerBounds.ToArray()) + ":" + string.Join(',', value.Select(static item => item is null ? "NULL" :
            item.Value.Leaf.Number.ToString(CultureInfo.InvariantCulture)));

    /// <summary>
    /// Defines the last payload comfortably within the short-header limit.
    /// </summary>
    [PgType(NativeLayout = true, TextCodec = typeof(BufferText<Bytes125>))]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct Bytes125
    {
        /// <summary>
        /// The fixed payload.
        /// </summary>
        public fixed byte Bytes[125];
    }

    /// <summary>
    /// Defines the maximum payload with a short header.
    /// </summary>
    [PgType(NativeLayout = true, TextCodec = typeof(BufferText<Bytes126>))]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct Bytes126
    {
        /// <summary>
        /// The fixed payload.
        /// </summary>
        public fixed byte Bytes[126];
    }

    /// <summary>
    /// Defines the first payload requiring a regular header.
    /// </summary>
    [PgType(NativeLayout = true, TextCodec = typeof(BufferText<Bytes127>), BinaryProtocol = true, Id = "varlena_ownership.bytes127")]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct Bytes127
    {
        /// <summary>
        /// The fixed payload.
        /// </summary>
        public fixed byte Bytes[127];
    }

    /// <summary>
    /// Creates a non-short payload with independently recognizable first and last bytes.
    /// </summary>
    [PgFunction]
    public static unsafe Bytes127 VarlenaFullMake()
    {
        Bytes127 value = default;
        value.Bytes[0] = 42;
        value.Bytes[126] = 211;
        return value;
    }

    /// <summary>
    /// Observes shared borrowed full-header identity and both writes without changing the second view.
    /// </summary>
    [PgFunction(Requires = ["varlena_ownership.bytes127"])]
    public static unsafe string VarlenaFullProbe(PgVarlena<Bytes127> value, PgVarlena<Bytes127> second,
        [PgSqlType("bytes127", Schema = "varlena_ownership")] PgDatum original)
    {
        bool borrowed = value.IsBorrowed;
        nuint before = (nuint)value.DangerousGetPointer();
        bool same = second.IsBorrowed && before == (nuint)second.DangerousGetPointer();
        Bytes127 copy = value.Value;
        copy.Bytes[0] = 142;
        Bytes127 read = value.Value;
        bool readUnchanged = read.Bytes[0] == 42 && (nuint)value.DangerousGetPointer() == before;
        copy.Bytes[0] = 43;
        value.Value = copy;
        nuint written = (nuint)value.DangerousGetPointer();
        copy.Bytes[0] = 44;
        value.Value = copy;
        Bytes127 source = original.Read<Bytes127>();
        Bytes127 after = value.Value;
        Bytes127 untouched = second.Value;
        bool aliasPreserved = second.IsBorrowed && before == (nuint)second.DangerousGetPointer() &&
            untouched.Bytes[0] == 42 && untouched.Bytes[126] == 211;
        return $"{borrowed}|{same}|{readUnchanged}|{written != before}|{value.IsBorrowed}|" +
            $"{written == (nuint)value.DangerousGetPointer()}|{source.Bytes[0]}|{after.Bytes[0]}|{source.Bytes[126]}|{after.Bytes[126]}|{aliasPreserved}";
    }

    /// <summary>
    /// Copies an original borrowed input before consuming the wrapper's explicit transfer right.
    /// </summary>
    [PgFunction(Requires = ["varlena_ownership.bytes127"])]
    public static unsafe string VarlenaFullInputTransfer(PgVarlena<Bytes127> value,
        [PgSqlType("bytes127", Schema = "varlena_ownership")] PgDatum original)
    {
        bool borrowed = value.IsBorrowed;
        nuint pointer = (nuint)value.DangerousGetPointer();
        PgDatum datum = value.IntoDatum();
        Bytes127 copied = datum.Read<Bytes127>();
        Bytes127 source = original.Read<Bytes127>();
        return $"{borrowed}|{pointer != datum.DangerousGetBits()}|{Reject(value)}|{copied.Bytes[0]}|{source.Bytes[0]}|" +
            $"{copied.Bytes[126]}|{source.Bytes[126]}";
    }

    /// <summary>
    /// Supplies intentionally unused text conversion for fixed header-boundary layouts.
    /// </summary>
    /// <typeparam name="T">The exact packed payload.</typeparam>
    public sealed class BufferText<T> : PgTypeTextCodec<T> where T : unmanaged
    {
        /// <inheritdoc />
        public override T Parse(string text) => default;

        /// <inheritdoc />
        public override string Format(T value) => "buffer";
    }

    /// <summary>
    /// Observes independently specified PostgreSQL header boundaries and zero-filled payloads.
    /// </summary>
    [PgFunction]
    public static unsafe string VarlenaHeaders()
    {
        using var first = new PgVarlena<Bytes125>();
        using var second = new PgVarlena<Bytes126>();
        using var third = new PgVarlena<Bytes127>();
        bool Short(void* pointer) => (*(byte*)pointer & (BitConverter.IsLittleEndian ? 1 : 128)) != 0;
        Bytes125 a = first.Value;
        Bytes126 b = second.Value;
        Bytes127 c = third.Value;
        bool zero = new ReadOnlySpan<byte>(a.Bytes, 125).IndexOfAnyExcept((byte)0) == -1 &&
            new ReadOnlySpan<byte>(b.Bytes, 126).IndexOfAnyExcept((byte)0) == -1 && new ReadOnlySpan<byte>(c.Bytes, 127).IndexOfAnyExcept((byte)0) == -1;
        return $"{Short(first.DangerousGetPointer())}|{Short(second.DangerousGetPointer())}|{Short(third.DangerousGetPointer())}|{zero}|" +
            $"{first.IsBorrowed}|{second.IsBorrowed}|{third.IsBorrowed}";
    }

    /// <summary>
    /// Produces a stable observation of a saved wrapper without accessing expired storage.
    /// </summary>
    private static string Saved(PgVarlena<Packet>? value)
    {
        if (value is null)
        {
            return "NULL";
        }

        try
        {
            return value.Value.Leaf.Number.ToString(CultureInfo.InvariantCulture);
        }
        catch (ObjectDisposedException)
        {
            return "expired";
        }
    }

    /// <summary>
    /// Counts independently rejected operations on a consumed wrapper alias.
    /// </summary>
    private static unsafe int Reject<T>(PgVarlena<T> value) where T : unmanaged
    {
        int count = 0;
        try
        {
            _ = value.Value;
        }
        catch (ObjectDisposedException)
        {
            count++;
        }

        try
        {
            _ = value.IsBorrowed;
        }
        catch (ObjectDisposedException)
        {
            count++;
        }

        try
        {
            _ = value.Context;
        }
        catch (ObjectDisposedException)
        {
            count++;
        }

        try
        {
            _ = value.DangerousGetPointer();
        }
        catch (ObjectDisposedException)
        {
            count++;
        }

        try
        {
            _ = value.IntoDatum();
        }
        catch (ObjectDisposedException)
        {
            count++;
        }

        return count;
    }
}
