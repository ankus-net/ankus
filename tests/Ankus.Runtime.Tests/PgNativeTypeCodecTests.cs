using System.Buffers;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies exact native-layout storage and its independent, lazy SQL text conversion.
/// </summary>
[TestClass]
public sealed class PgNativeTypeCodecTests
{
    /// <summary>
    /// Requires the generated size to match the managed representation before a text factory can run.
    /// </summary>
    /// <param name="expectedSize">An incorrect declared size around the fifteen-byte packed representation.</param>
    [TestMethod]
    [DataRow(-1)]
    [DataRow(0)]
    [DataRow(14)]
    [DataRow(16)]
    [DataRow(int.MaxValue)]
    public void ConstructorRejectsMismatchedSize(int expectedSize)
    {
        int constructions = 0;
        ArgumentException error = Assert.ThrowsExactly<ArgumentException>(() => new PgNativeTypeCodec<PackedRecord>(expectedSize, [], () =>
        {
            constructions++;
            throw new InvalidOperationException("Text construction must remain deferred.");
        }));
        Assert.AreEqual("expectedSize", error.ParamName);
        Assert.AreEqual(0, constructions);
    }

    /// <summary>
    /// Requires a factory even when only native storage will be used.
    /// </summary>
    [TestMethod]
    public void ConstructorRejectsAbsentTextFactory()
    {
        Assert.AreEqual("createTextCodec", Assert.ThrowsExactly<ArgumentNullException>(() => new PgNativeTypeCodec<PackedRecord>(15, [], null!)).ParamName);
    }

    /// <summary>
    /// Preserves mixed-width nested fields from an unaligned span and returns a value independent of that source.
    /// </summary>
    [TestMethod]
    public void PackedNestedValuesUseIndependentNativeBytes()
    {
        PgNativeTypeCodec<PackedRecord> codec = BinaryCodec<PackedRecord>(15);
        byte[] expected = NativeBytes("A53412EFCDAB89EFCDAB8967452301", "A5123489ABCDEF0123456789ABCDEF");
        byte[] input = [0xCC, .. expected, 0xDD];
        byte[] original = [0xCC, .. expected, 0xDD];
        var value = new PackedRecord(new(0xA5, 0x1234), unchecked((int)0x89ABCDEF), 0x0123456789ABCDEF);

        PackedRecord decoded = codec.Read(input.AsSpan(1, 15));

        Assert.AreEqual(value, decoded);
        Assert.AreSequenceEqual(original, input);
        Assert.AreSequenceEqual(expected, Encode(codec, value));
        Array.Fill<byte>(input, 0);
        Assert.AreEqual(value, decoded);
        Assert.AreSequenceEqual(expected, Encode(codec, decoded));
        Assert.AreEqual(default, codec.Read(new byte[15]));
        Assert.AreSequenceEqual(new byte[15], Encode(codec, default));
    }

    /// <summary>
    /// Copies every fixed-buffer element without omitting its final value or introducing padding.
    /// </summary>
    [TestMethod]
    public unsafe void FixedBuffersKeepEveryElement()
    {
        PgNativeTypeCodec<FixedRecord> codec = BinaryCodec<FixedRecord>(7);
        byte[] expected = NativeBytes("A53412CDABFFFF", "A51234ABCDFFFF");
        var value = new FixedRecord { _tag = 0xA5 };
        value._values[0] = 0x1234;
        value._values[1] = 0xABCD;
        value._values[2] = ushort.MaxValue;

        FixedRecord decoded = codec.Read(expected);

        Assert.AreEqual((byte)0xA5, decoded._tag);
        Assert.AreEqual((ushort)0x1234, decoded._values[0]);
        Assert.AreEqual((ushort)0xABCD, decoded._values[1]);
        Assert.AreEqual(ushort.MaxValue, decoded._values[2]);
        Assert.AreSequenceEqual(expected, Encode(codec, value));
    }

    /// <summary>
    /// Retains the full signed integer domain without changing byte order or normalizing values.
    /// </summary>
    /// <param name="value">The native integer value.</param>
    /// <param name="littleEndian">Its independently specified little-endian representation.</param>
    /// <param name="bigEndian">Its independently specified big-endian representation.</param>
    [TestMethod]
    [DataRow(long.MinValue, "0000000000000080", "8000000000000000")]
    [DataRow(long.MaxValue, "FFFFFFFFFFFFFF7F", "7FFFFFFFFFFFFFFF")]
    [DataRow(-1L, "FFFFFFFFFFFFFFFF", "FFFFFFFFFFFFFFFF")]
    public void IntegerLimitsPreserveNativeRepresentation(long value, string littleEndian, string bigEndian)
    {
        PgNativeTypeCodec<long> codec = BinaryCodec<long>(8);
        byte[] expected = NativeBytes(littleEndian, bigEndian);
        Assert.AreEqual(value, codec.Read(expected));
        Assert.AreSequenceEqual(expected, Encode(codec, value));
    }

    /// <summary>
    /// Unsigned maxima and unnamed enum values remain valid bit representations.
    /// </summary>
    [TestMethod]
    public void UnsignedAndEnumBitsDoNotRequireNamedValues()
    {
        PgNativeTypeCodec<ulong> unsigned = BinaryCodec<ulong>(8);
        byte[] maximum = Convert.FromHexString("FFFFFFFFFFFFFFFF");
        Assert.AreEqual(ulong.MaxValue, unsigned.Read(maximum));
        Assert.AreSequenceEqual(maximum, Encode(unsigned, ulong.MaxValue));
        PgNativeTypeCodec<NativeCode> enumeration = BinaryCodec<NativeCode>(2);
        byte[] unnamed = NativeBytes("DCFE", "FEDC");
        Assert.AreEqual((NativeCode)0xFEDC, enumeration.Read(unnamed));
        Assert.AreSequenceEqual(unnamed, Encode(enumeration, (NativeCode)0xFEDC));
    }

    /// <summary>
    /// Preserves single-precision signed zero, NaN sign and payload, subnormals and adjacent finite values.
    /// </summary>
    /// <param name="bits">The exact IEEE 754 bits.</param>
    /// <param name="littleEndian">The independent little-endian fixture.</param>
    /// <param name="bigEndian">The independent big-endian fixture.</param>
    [TestMethod]
    [DataRow(int.MinValue, "00000080", "80000000")]
    [DataRow(0x7FC12345, "4523C17F", "7FC12345")]
    [DataRow(unchecked((int)0xFFA54321), "2143A5FF", "FFA54321")]
    [DataRow(1, "01000000", "00000001")]
    [DataRow(0x3F800001, "0100803F", "3F800001")]
    public void SinglePrecisionPreservesExactBits(int bits, string littleEndian, string bigEndian)
    {
        PgNativeTypeCodec<float> codec = BinaryCodec<float>(4);
        byte[] expected = NativeBytes(littleEndian, bigEndian);
        Assert.AreEqual(bits, BitConverter.SingleToInt32Bits(codec.Read(expected)));
        Assert.AreSequenceEqual(expected, Encode(codec, BitConverter.Int32BitsToSingle(bits)));
    }

    /// <summary>
    /// Preserves double-precision signed zero, NaN sign and payload, subnormals and adjacent finite values.
    /// </summary>
    /// <param name="bits">The exact IEEE 754 bits.</param>
    /// <param name="littleEndian">The independent little-endian fixture.</param>
    /// <param name="bigEndian">The independent big-endian fixture.</param>
    [TestMethod]
    [DataRow(long.MinValue, "0000000000000080", "8000000000000000")]
    [DataRow(0x7FF8123456789ABCL, "BC9A78563412F87F", "7FF8123456789ABC")]
    [DataRow(unchecked((long)0xFFF0123456789ABC), "BC9A78563412F0FF", "FFF0123456789ABC")]
    [DataRow(1L, "0100000000000000", "0000000000000001")]
    [DataRow(0x3FF0000000000001L, "010000000000F03F", "3FF0000000000001")]
    public void DoublePrecisionPreservesExactBits(long bits, string littleEndian, string bigEndian)
    {
        PgNativeTypeCodec<double> codec = BinaryCodec<double>(8);
        byte[] expected = NativeBytes(littleEndian, bigEndian);
        Assert.AreEqual(bits, BitConverter.DoubleToInt64Bits(codec.Read(expected)));
        Assert.AreSequenceEqual(expected, Encode(codec, BitConverter.Int64BitsToDouble(bits)));
    }

    /// <summary>
    /// Rejects empty, truncated and trailing native payloads before accepting an exact-size value afterward.
    /// </summary>
    /// <param name="length">A payload length different from the declared fifteen bytes.</param>
    [TestMethod]
    [DataRow(0)]
    [DataRow(1)]
    [DataRow(14)]
    [DataRow(16)]
    [DataRow(31)]
    public void InvalidPayloadLengthsRaiseBinaryInputErrors(int length)
    {
        PgNativeTypeCodec<PackedRecord> codec = BinaryCodec<PackedRecord>(15);
        PgException error = Assert.ThrowsExactly<PgException>(() => codec.Read(new byte[length]));
        Assert.AreEqual("22P03", error.SqlState);
        Assert.AreEqual(default, codec.Read(new byte[15]));
    }

    /// <summary>
    /// Creates one shared text codec in either call order while binary reads and writes bypass it.
    /// </summary>
    /// <param name="formatFirst">Whether formatting is the first text operation.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TextFactoryIsLazySharedAndIndependentOfStorage(bool formatFirst)
    {
        int constructions = 0;
        var text = new CountingTextCodec(
            static value => uint.Parse(value.AsSpan("value=".Length), CultureInfo.InvariantCulture),
            static value => "value=" + value.ToString(CultureInfo.InvariantCulture));
        var codec = new PgNativeTypeCodec<uint>(4, [], () =>
        {
            constructions++;
            return text;
        });
        byte[] expected = NativeBytes("2A000000", "0000002A");
        Assert.AreEqual(42U, codec.Read(expected));
        Assert.AreSequenceEqual(expected, Encode(codec, 42U));
        Assert.AreEqual(0, constructions);
        Assert.AreEqual(0, text.ParseCalls);
        Assert.AreEqual(0, text.FormatCalls);
        if (formatFirst)
        {
            Assert.AreEqual("value=42", codec.Format(42));
            Assert.AreEqual(7U, codec.Parse("value=7"));
        }
        else
        {
            Assert.AreEqual(7U, codec.Parse("value=7"));
            Assert.AreEqual("value=42", codec.Format(42));
        }

        Assert.AreEqual(42U, codec.Read(expected));
        Assert.AreSequenceEqual(expected, Encode(codec, 42U));
        Assert.AreEqual(1, constructions);
        Assert.AreEqual(1, text.ParseCalls);
        Assert.AreEqual(1, text.FormatCalls);
    }

    /// <summary>
    /// Caches factory failure while keeping native storage usable before and after either text entry point fails.
    /// </summary>
    /// <param name="formatFirst">Whether formatting first triggers the factory failure.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void FactoryFailuresAreCachedWithoutDisablingStorage(bool formatFirst)
    {
        int constructions = 0;
        var failure = new FormatException("Cannot initialize domain text.");
        var codec = new PgNativeTypeCodec<uint>(4, [], () =>
        {
            constructions++;
            throw failure;
        });
        byte[] expected = NativeBytes("2A000000", "0000002A");
        Assert.AreEqual(42U, codec.Read(expected));
        Assert.AreSequenceEqual(expected, Encode(codec, 42U));
        Assert.AreEqual(0, constructions);
        Action first = formatFirst ? () => codec.Format(42) : () => codec.Parse("42");
        Action second = formatFirst ? () => codec.Parse("42") : () => codec.Format(42);
        Assert.AreSame(failure, Assert.ThrowsExactly<FormatException>(first));
        Assert.AreSame(failure, Assert.ThrowsExactly<FormatException>(second));
        Assert.AreEqual(1, constructions);
        Assert.AreEqual(42U, codec.Read(expected));
        Assert.AreSequenceEqual(expected, Encode(codec, 42U));
        Assert.AreEqual(1, constructions);
    }

    /// <summary>
    /// A null factory result is cached as a failure instead of disabling native reads or writes.
    /// </summary>
    [TestMethod]
    public void NullFactoryResultsAreRejectedAndCached()
    {
        int constructions = 0;
        var codec = new PgNativeTypeCodec<uint>(4, [], () =>
        {
            constructions++;
            return null!;
        });
        Assert.ThrowsExactly<InvalidOperationException>(() => codec.Parse("42"));
        Assert.ThrowsExactly<InvalidOperationException>(() => codec.Format(42));
        Assert.AreEqual(1, constructions);
        byte[] expected = NativeBytes("2A000000", "0000002A");
        Assert.AreEqual(42U, codec.Read(expected));
        Assert.AreSequenceEqual(expected, Encode(codec, 42U));
        Assert.AreEqual(1, constructions);
    }

    /// <summary>
    /// Text callback errors retain the user's exception identity and PostgreSQL diagnostics.
    /// </summary>
    /// <param name="format">Whether the failure comes from formatting rather than parsing.</param>
    /// <param name="postgresError">Whether the callback supplies structured PostgreSQL diagnostics.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void TextFailuresPreserveTheirIdentity(bool format, bool postgresError)
    {
        Exception failure = postgresError
            ? new PgException("22003", "Outside the native domain.", "Value is too large.", "Use a smaller value.")
            : new FormatException("Domain syntax failed.");
        var text = new CountingTextCodec(_ => throw failure, _ => throw failure);
        var codec = new PgNativeTypeCodec<uint>(4, [], () => text);
        Action action = format ? () => codec.Format(42) : () => codec.Parse("42");
        if (postgresError)
        {
            PgException actual = Assert.ThrowsExactly<PgException>(action);
            Assert.AreSame(failure, actual);
            Assert.AreEqual("22003", actual.SqlState);
            Assert.AreEqual("Outside the native domain.", actual.Message);
            Assert.AreEqual("Value is too large.", actual.Detail);
            Assert.AreEqual("Use a smaller value.", actual.Hint);
        }
        else
        {
            Assert.AreSame(failure, Assert.ThrowsExactly<FormatException>(action));
        }

        Assert.AreEqual(format ? 0 : 1, text.ParseCalls);
        Assert.AreEqual(format ? 1 : 0, text.FormatCalls);
    }

    /// <summary>
    /// Guards missing direct arguments before constructing text code and refuses null formatted text.
    /// </summary>
    [TestMethod]
    public void NullArgumentsAndFormattedTextAreRejected()
    {
        int constructions = 0;
        var text = new CountingTextCodec(static _ => 42, static _ => null!);
        var codec = new PgNativeTypeCodec<uint>(4, [], () =>
        {
            constructions++;
            return text;
        });
        Assert.AreEqual("text", Assert.ThrowsExactly<ArgumentNullException>(() => codec.Parse(null!)).ParamName);
        Assert.AreEqual("destination", Assert.ThrowsExactly<ArgumentNullException>(() => codec.Write(42, null!)).ParamName);
        Assert.AreEqual(0, constructions);
        Assert.AreEqual(0, text.ParseCalls);
        Assert.AreEqual(0, text.FormatCalls);
        Assert.ThrowsExactly<InvalidOperationException>(() => codec.Format(42));
        Assert.AreEqual(1, constructions);
        Assert.AreEqual(0, text.ParseCalls);
        Assert.AreEqual(1, text.FormatCalls);
    }

    /// <summary>
    /// Appends exactly each payload without rewriting existing bytes or touching unused output capacity.
    /// </summary>
    [TestMethod]
    public void WritesRespectDestinationPositionAndPayloadBoundaries()
    {
        PgNativeTypeCodec<PackedRecord> codec = BinaryCodec<PackedRecord>(15);
        var writer = new GuardedWriter();
        byte[] expected = NativeBytes("A53412EFCDAB89EFCDAB8967452301", "A5123489ABCDEF0123456789ABCDEF");
        var value = new PackedRecord(new(0xA5, 0x1234), unchecked((int)0x89ABCDEF), 0x0123456789ABCDEF);

        codec.Write(value, writer);
        codec.Write(default, writer);

        byte[] expectedOutput = [0x99, 0xBB, .. expected, .. new byte[15]];
        Assert.AreEqual(32, writer.WrittenCount);
        Assert.AreSequenceEqual(expectedOutput, writer.Buffer.AsSpan(0, 32).ToArray());
        Assert.AreSequenceEqual(Convert.FromHexString("CCCCCCCCCCCCCC"), writer.Buffer.AsSpan(32).ToArray());
    }

    /// <summary>
    /// Destination failures propagate unchanged without falsely advancing or constructing text code.
    /// </summary>
    [TestMethod]
    public void DestinationFailureDoesNotAdvanceOutput()
    {
        PgNativeTypeCodec<uint> codec = BinaryCodec<uint>(4);
        var failure = new IOException("Output allocation failed.");
        var writer = new GuardedWriter(failure);
        Assert.AreSame(failure, Assert.ThrowsExactly<IOException>(() => codec.Write(42, writer)));
        Assert.AreEqual(2, writer.WrittenCount);
        byte[] expected = [0x99, 0xBB, .. Enumerable.Repeat((byte)0xCC, 37)];
        Assert.AreSequenceEqual(expected, writer.Buffer);
    }

    /// <summary>
    /// Chooses a literal fixture for the platform's specified native endianness without serializing a value.
    /// </summary>
    private static byte[] NativeBytes(string littleEndian, string bigEndian) => Convert.FromHexString(BitConverter.IsLittleEndian ? littleEndian : bigEndian);

    /// <summary>
    /// Supplies a factory that makes any unexpected text dependency fail a binary-only test.
    /// </summary>
    private static PgNativeTypeCodec<T> BinaryCodec<T>(int size) where T : unmanaged =>
        new(size, [], static () => throw new InvalidOperationException("Native storage must not construct a text codec."));

    /// <summary>
    /// Captures actual codec output through the standard buffer-writer contract.
    /// </summary>
    private static byte[] Encode<T>(PgTypeCodec<T> codec, T value)
    {
        var writer = new ArrayBufferWriter<byte>();
        codec.Write(value, writer);
        return writer.WrittenSpan.ToArray();
    }

    /// <summary>
    /// A naturally aligned layout stores zero in its padding even when the written value carries other bytes there,
    /// and reading ignores the padding; misplaced padding ranges are rejected.
    /// </summary>
    [TestMethod]
    public void PaddedLayoutsStoreZeroPadding()
    {
        var codec = new PgNativeTypeCodec<PaddedRecord>(8, [1, 3], static () => throw new InvalidOperationException("Unexpected text construction."));
        byte[] dirty = [0x2A, 0xFF, 0xFF, 0xFF, 0x07, 0x00, 0x00, 0x00];
        if (!BitConverter.IsLittleEndian)
        {
            dirty = [0x2A, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0x00, 0x07];
        }

        PaddedRecord value = MemoryMarshal.Read<PaddedRecord>(dirty);
        Assert.AreEqual(new PaddedRecord(42, 7), value);
        byte[] stored = Encode(codec, value);
        byte[] expected = [.. dirty[..1], 0, 0, 0, .. dirty[4..]];
        Assert.AreSequenceEqual(expected, stored);
        Assert.AreEqual(value, codec.Read(stored));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgNativeTypeCodec<PaddedRecord>(8, [6, 3], static () => null!));
        Assert.ThrowsExactly<ArgumentException>(() => new PgNativeTypeCodec<PaddedRecord>(8, [1], static () => null!));
    }

    /// <summary>
    /// Has three padding bytes between a byte and a naturally aligned integer.
    /// </summary>
    /// <param name="Tag">The one-byte field.</param>
    /// <param name="Count">The four-byte field, at offset four.</param>
    private readonly record struct PaddedRecord(byte Tag, int Count);

    /// <summary>
    /// Carries an unaligned two-byte member with no padding.
    /// </summary>
    /// <param name="Tag">A one-byte field preceding the wider field.</param>
    /// <param name="Code">A two-byte unsigned value.</param>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct NativePair(byte Tag, ushort Code);

    /// <summary>
    /// Places nested, signed and unsigned values at independently specified offsets zero, three and seven.
    /// </summary>
    /// <param name="Pair">The nested three-byte representation.</param>
    /// <param name="Count">An unaligned signed integer.</param>
    /// <param name="Number">An unaligned unsigned integer.</param>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private readonly record struct PackedRecord(NativePair Pair, int Count, ulong Number);

    /// <summary>
    /// Carries a fixed-width enumeration whose unnamed bit patterns are also representable.
    /// </summary>
    private enum NativeCode : ushort
    {
        /// <summary>
        /// Provides one named value without restricting other native representations.
        /// </summary>
        Zero,
    }

    /// <summary>
    /// Places three fixed-buffer elements directly after a one-byte tag.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    private unsafe struct FixedRecord
    {
        /// <summary>
        /// Prefixes the unaligned fixed buffer.
        /// </summary>
        internal byte _tag;

        /// <summary>
        /// Stores every element inside the unmanaged representation.
        /// </summary>
        internal fixed ushort _values[3];
    }

    /// <summary>
    /// Observes calls on the single text codec retained by a native storage codec.
    /// </summary>
    private sealed class CountingTextCodec(Func<string, uint> parse, Func<uint, string> format) : PgTypeTextCodec<uint>
    {
        /// <summary>
        /// Gets the number of text input callbacks.
        /// </summary>
        public int ParseCalls
        {
            get;
            private set;
        }

        /// <summary>
        /// Gets the number of text output callbacks.
        /// </summary>
        public int FormatCalls
        {
            get;
            private set;
        }

        /// <inheritdoc />
        public override uint Parse(string text)
        {
            ParseCalls++;
            return parse(text);
        }

        /// <inheritdoc />
        public override string Format(uint value)
        {
            FormatCalls++;
            return format(value);
        }
    }

    /// <summary>
    /// Exposes nonempty output with sentinel capacity and an optional allocation failure.
    /// </summary>
    private sealed class GuardedWriter(Exception? failure = null) : IBufferWriter<byte>
    {
        /// <summary>
        /// Gets the prefixed output and sentinel-filled unused capacity.
        /// </summary>
        public byte[] Buffer { get; } = [0x99, 0xBB, .. Enumerable.Repeat((byte)0xCC, 37)];

        /// <summary>
        /// Gets the logical output position, including the two-byte prefix.
        /// </summary>
        public int WrittenCount
        {
            get;
            private set;
        } = 2;

        /// <inheritdoc />
        public void Advance(int count)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(count);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(count, Buffer.Length - WrittenCount);
            WrittenCount += count;
        }

        /// <inheritdoc />
        public Memory<byte> GetMemory(int sizeHint = 0)
        {
            if (failure is not null)
            {
                throw failure;
            }

            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(sizeHint, Buffer.Length - WrittenCount);
            return Buffer.AsMemory(WrittenCount);
        }

        /// <inheritdoc />
        public Span<byte> GetSpan(int sizeHint = 0) => GetMemory(sizeHint).Span;
    }
}
