using System.Globalization;
using System.Runtime.InteropServices;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises copied, packed native storage across PostgreSQL datum boundaries.
/// </summary>
[PgSchema("native_layout")]
public static class NativeLayoutTypeFunctions
{
    /// <summary>
    /// Provides a named enum while permitting every native byte pattern.
    /// </summary>
    public enum State : byte
    {
        /// <summary>
        /// The single named state.
        /// </summary>
        Ready = 7,
    }

    /// <summary>
    /// Places an integer at an unaligned offset inside a nested packed structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Leaf
    {
        /// <summary>
        /// The leading byte.
        /// </summary>
        public byte Tag;

        /// <summary>
        /// The unaligned integer.
        /// </summary>
        public int Number;
    }

    /// <summary>
    /// Stores all fields as exactly twenty-four native bytes, including arbitrary enum and floating bits.
    /// </summary>
    [PgType(NativeLayout = true, TextCodec = typeof(PacketText), BinaryProtocol = true, Id = "native_layout.packet")]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct Packet
    {
        /// <summary>
        /// The nested packed value.
        /// </summary>
        public Leaf Leaf;

        /// <summary>
        /// The arbitrary enum bits.
        /// </summary>
        public State State;

        /// <summary>
        /// Three densely stored sample values.
        /// </summary>
        public fixed short Samples[3];

        /// <summary>
        /// The exact single-precision bits.
        /// </summary>
        public float Sample;

        /// <summary>
        /// The exact double-precision bits.
        /// </summary>
        public double Precise;
    }

    /// <summary>
    /// Supplies domain text and observable lazy construction for the packed structure.
    /// </summary>
    public sealed class PacketText : PgTypeTextCodec<Packet>
    {
        /// <summary>
        /// Counts construction in the current backend.
        /// </summary>
        internal static int s_created;

        /// <summary>
        /// Counts text parses.
        /// </summary>
        internal static int s_parsed;

        /// <summary>
        /// Counts text formats.
        /// </summary>
        internal static int s_formatted;

        /// <summary>
        /// Records the first text operation.
        /// </summary>
        public PacketText() => s_created++;

        /// <inheritdoc />
        public override Packet Parse(string text)
        {
            s_parsed++;
            return text switch
            {
                "!pg" => throw new PgException("P7921", "native parser failed"),
                "!managed" => throw new InvalidOperationException("native managed parser failed"),
                _ => int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number)
                    ? NativeMake(number) : throw new PgException("22P02", "Expected a native packet integer."),
            };
        }

        /// <inheritdoc />
        public override string Format(Packet value)
        {
            s_formatted++;
            return value.Leaf.Number switch
            {
                -999 => null!,
                -998 => throw new PgException("P7922", "native formatter failed"),
                _ => value.Leaf.Number.ToString(CultureInfo.InvariantCulture),
            };
        }
    }

    /// <summary>
    /// Shares a byte width with an unrelated SQL type and has an intentionally unusable text adapter.
    /// </summary>
    [PgType(NativeLayout = true, TextCodec = typeof(FaultText), BinaryProtocol = true, NullInputErrorMessage = "native fault needs input")]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct Fault
    {
        /// <summary>
        /// The stored integer.
        /// </summary>
        public int Number;
    }

    /// <summary>
    /// Throws during the first text operation while leaving native storage usable.
    /// </summary>
    public sealed class FaultText : PgTypeTextCodec<Fault>
    {
        /// <summary>
        /// Counts attempted constructions.
        /// </summary>
        internal static int s_attempts;

        /// <summary>
        /// Reports an ordinary managed factory failure.
        /// </summary>
        public FaultText()
        {
            s_attempts++;
            throw new InvalidOperationException("native text factory failed");
        }

        /// <inheritdoc />
        public override Fault Parse(string text) => throw new InvalidOperationException("Fault parser reached.");

        /// <inheritdoc />
        public override string Format(Fault value) => throw new InvalidOperationException("Fault formatter reached.");
    }

    /// <summary>
    /// Forces compressed and external TOAST paths with a fixed-size numeric buffer.
    /// </summary>
    [PgType(NativeLayout = true, TextCodec = typeof(BlockText), BinaryProtocol = true)]
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public unsafe struct Block
    {
        /// <summary>
        /// The complete stored block.
        /// </summary>
        public fixed byte Bytes[8192];
    }

    /// <summary>
    /// Keeps the large block's SQL text independent of its raw binary representation.
    /// </summary>
    public sealed class BlockText : PgTypeTextCodec<Block>
    {
        /// <inheritdoc />
        public override Block Parse(string text) => NativeBlock(int.Parse(text, CultureInfo.InvariantCulture));

        /// <inheritdoc />
        public override string Format(Block value) => "native block";
    }

    /// <summary>
    /// Has three padding bytes between its fields, as a naturally aligned C struct does.
    /// </summary>
    [PgType(NativeLayout = true, TextCodec = typeof(AlignedText), BinaryProtocol = true)]
    public struct Aligned
    {
        /// <summary>
        /// Gets or sets the leading byte.
        /// </summary>
        public byte Tag
        {
            readonly get;
            set;
        }

        /// <summary>
        /// Gets or sets the integer, aligned at offset four.
        /// </summary>
        public int Number
        {
            readonly get;
            set;
        }
    }

    /// <summary>
    /// Formats the aligned value as its tag and number.
    /// </summary>
    public sealed class AlignedText : PgTypeTextCodec<Aligned>
    {
        /// <inheritdoc />
        public override Aligned Parse(string text)
        {
            string[] parts = text.Split(':');
            return new Aligned { Tag = byte.Parse(parts[0], CultureInfo.InvariantCulture), Number = int.Parse(parts[1], CultureInfo.InvariantCulture) };
        }

        /// <inheritdoc />
        public override string Format(Aligned value) => string.Create(CultureInfo.InvariantCulture, $"{value.Tag}:{value.Number}");
    }

    /// <summary>
    /// Builds an aligned value whose padding bytes are 0xFF, as a value copied from uninitialized memory can be.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <param name="number">The number.</param>
    /// <returns>The value, stored with zero padding.</returns>
    [PgFunction]
    public static Aligned NativeAlignedDirty(int tag, int number) => Dirty(checked((byte)tag), number);

    /// <summary>
    /// Builds the same dirty value as a native varlena.
    /// </summary>
    /// <param name="tag">The tag.</param>
    /// <param name="number">The number.</param>
    /// <returns>The native varlena, written with zero padding.</returns>
    [PgFunction]
    public static PgVarlena<Aligned> NativeAlignedVarlena(int tag, int number) => new(Dirty(checked((byte)tag), number));

    /// <summary>
    /// Replaces the number in place in a copied native varlena.
    /// </summary>
    /// <param name="value">The varlena.</param>
    /// <param name="number">The new number.</param>
    /// <returns>The updated varlena.</returns>
    [PgFunction]
    public static PgVarlena<Aligned> NativeAlignedSetNumber(PgVarlena<Aligned> value, int number)
    {
        value.Value = Dirty(value.Value.Tag, number);
        return value;
    }

    /// <summary>
    /// Reads a value from bytes whose padding is 0xFF.
    /// </summary>
    private static Aligned Dirty(byte tag, int number)
    {
        Span<byte> bytes = stackalloc byte[8];
        bytes.Fill(0xFF);
        bytes[0] = tag;
        MemoryMarshal.Write(bytes[4..], in number);
        return MemoryMarshal.Read<Aligned>(bytes);
    }

    /// <summary>
    /// Builds exact nested, enum, buffer and floating-point fields without text conversion.
    /// </summary>
    [PgFunction]
    public static unsafe Packet NativeMake(int number)
    {
        Packet value = default;
        value.Leaf = new Leaf { Tag = 0xAB, Number = number };
        value.State = (State)255;
        value.Samples[0] = short.MinValue;
        value.Samples[1] = 0x1234;
        value.Samples[2] = short.MaxValue;
        value.Sample = BitConverter.Int32BitsToSingle(unchecked((int)0x80000000));
        value.Precise = BitConverter.Int64BitsToDouble(0x7FF8000000000042);
        return value;
    }

    /// <summary>
    /// Observes every stored field independently of the text adapter.
    /// </summary>
    [PgFunction]
    public static unsafe string NativeDescribe(Packet value) =>
        $"{value.Leaf.Tag:X2}:{value.Leaf.Number}:{(byte)value.State}:{value.Samples[0]}:{value.Samples[1]}:{value.Samples[2]}:" +
        $"{BitConverter.SingleToInt32Bits(value.Sample):X8}:{BitConverter.DoubleToInt64Bits(value.Precise):X16}";

    /// <summary>
    /// Distinguishes an all-zero native value from SQL NULL.
    /// </summary>
    [PgFunction]
    public static Packet NativeZero() => default;

    /// <summary>
    /// Exchanges nullable packed values through native and SPI ownership paths.
    /// </summary>
    [PgFunction]
    public static Packet? NativeEcho(Packet? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Preserves array shape, lower bounds and nullable packed cells.
    /// </summary>
    [PgFunction]
    public static PgArray<Packet?>? NativeArray(PgArray<Packet?>? value, int mode) => ArrayFunctions.Exchange(value, mode);

    /// <summary>
    /// Returns packed values and SQL NULL through set-returning conversion.
    /// </summary>
    [PgFunction]
    public static IEnumerable<Packet?> NativeRows(Packet? value) => [value, null, NativeMake(9)];

    /// <summary>
    /// Reassigns packed scalar and shaped array tuple cells before SPI conversion.
    /// </summary>
    [PgFunction]
    public static PgHeapTuple NativeTuple(PgHeapTuple value, int mode)
    {
        PgHeapTuple copy = value.Clone();
        copy.Set(0, copy.Get<Packet?>(0));
        copy.Set(1, copy.Get<PgArray<Packet?>?>(1));
        return ArrayFunctions.Exchange(copy, mode);
    }

    /// <summary>
    /// Copies an exactly typed raw datum through a temporary owner.
    /// </summary>
    [PgFunction(Requires = ["native_layout.packet"])]
    [return: PgSqlType("packet", Schema = "native_layout")]
    public static PgDatum? NativeRaw([PgSqlType("packet", Schema = "native_layout")] PgDatum? value)
    {
        using PgMemoryContext temporary = PgMemoryContext.Create("native packet copy");
        return value?.CopyTo(temporary).CopyTo(PgMemoryContext.Current);
    }

    /// <summary>
    /// Requires raw SQL identity before reading a managed packed value.
    /// </summary>
    [PgFunction]
    public static int NativeRawRead([PgSqlType("anyelement", Schema = "pg_catalog")] PgDatum value) => value.Read<Packet>().Leaf.Number;

    /// <summary>
    /// Reads a copied raw value after releasing its original SPI result.
    /// </summary>
    [PgFunction]
    public static string NativeRawLifetime()
    {
        using PgMemoryContext owner = PgMemoryContext.Create("native packet retained copy");
        using SpiRawResult result = Spi.QueryRaw("SELECT native_layout.native_make(42)");
        PgDatum source = result[0][0];
        PgDatum copy = source.CopyTo(owner);
        result.Dispose();
        int rejected = 0;
        try
        {
            _ = source.Read<Packet>();
        }
        catch (InvalidOperationException)
        {
            rejected++;
        }

        string description = NativeDescribe(copy.Read<Packet>());
        owner.Reset();
        try
        {
            _ = copy.Read<Packet>();
        }
        catch (InvalidOperationException)
        {
            rejected++;
        }

        return description + "|" + rejected.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Returns text callback counts without invoking an adapter.
    /// </summary>
    [PgFunction]
    public static string NativeCounters() => $"{PacketText.s_created}:{PacketText.s_parsed}:{PacketText.s_formatted}:{FaultText.s_attempts}";

    /// <summary>
    /// Creates a value despite its failing text factory.
    /// </summary>
    [PgFunction]
    public static Fault NativeFault(int number) => new() { Number = number };

    /// <summary>
    /// Reads binary storage without invoking the failing text factory.
    /// </summary>
    [PgFunction]
    public static int NativeFaultNumber(Fault value) => value.Number;

    /// <summary>
    /// Supplies an already-typed NULL without invoking custom input.
    /// </summary>
    [PgFunction]
    public static Fault? NativeFaultNull() => null;

    /// <summary>
    /// Preserves already-typed NULL through nullable function dispatch.
    /// </summary>
    [PgFunction]
    public static Fault? NativeFaultEcho(Fault? value) => value;

    /// <summary>
    /// Builds a fixed block with either a compressible or varying byte pattern.
    /// </summary>
    [PgFunction]
    public static unsafe Block NativeBlock(int mode)
    {
        Block value = default;
        for (int i = 0; i < 8192; i++)
        {
            value.Bytes[i] = mode == 0 ? (byte)7 : (byte)((i * 17 + (i >> 4)) & 255);
        }

        return value;
    }

    /// <summary>
    /// Exchanges large fixed blocks through typed SPI after detoasting.
    /// </summary>
    [PgFunction]
    public static Block NativeBlockEcho(Block value, int mode) => ArrayFunctions.Exchange(value, mode);
}
