using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks detached array ownership, shape, exact element conversions, and malformed native transports.
/// </summary>
[TestClass]
public sealed class PgArrayTests
{
    /// <summary>
    /// Copies constructor inputs and indexes row-major values with independent PostgreSQL lower bounds.
    /// </summary>
    [TestMethod]
    public void ShapeOwnsInputsAndUsesPostgresSubscripts()
    {
        int?[] input = [11, null, -7, 0, int.MaxValue, int.MinValue];
        int[] lengths = [2, 3];
        int[] bounds = [-2, 4];
        var array = new PgArray<int?>(input, lengths, bounds);
        input[0] = 99;
        lengths[0] = 99;
        bounds[0] = 99;
        Assert.AreEqual(6, array.Count);
        Assert.AreEqual(2, array.Rank);
        Assert.AreSequenceEqual([2, 3], array.Lengths.ToArray());
        Assert.AreSequenceEqual([-2, 4], array.LowerBounds.ToArray());
        Assert.AreEqual(11, array.GetValue(-2, 4));
        Assert.IsNull(array.GetValue(-2, 5));
        Assert.AreEqual(-7, array[2]);
        Assert.AreEqual(int.MinValue, array.GetValue(-1, 6));
        Assert.AreSequenceEqual([11, null, -7, 0, int.MaxValue, int.MinValue], array);
        Assert.ThrowsExactly<InvalidOperationException>(array.ToVector);
        Assert.ThrowsExactly<ArgumentException>(() => array.GetValue(-2));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => array.GetValue(-3, 4));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => array.GetValue(-1, 7));
        array.ToArray()[0] = 42;
        Assert.AreEqual(11, array[0]);
    }

    /// <summary>
    /// Vectors use lower bound one while empty shapes normalize to PostgreSQL's rank-zero representation.
    /// </summary>
    [TestMethod]
    public void VectorAndEmptySemanticsAreExplicit()
    {
        var vector = new PgArray<int>([1, 2, 3]);
        Assert.AreEqual(1, vector.Rank);
        Assert.AreSequenceEqual([1], vector.LowerBounds.ToArray());
        Assert.AreSequenceEqual([1, 2, 3], vector.ToVector());
        var empty = new PgArray<int>([], [2, 0], [-4, 7]);
        Assert.AreEqual(0, empty.Rank);
        Assert.AreEqual(0, empty.Count);
        Assert.IsEmpty(empty.Lengths.ToArray());
        Assert.IsEmpty(empty.LowerBounds.ToArray());
        Assert.IsEmpty(empty.ToVector());
        Assert.ThrowsExactly<ArgumentException>(() => empty.GetValue());
        Assert.ThrowsExactly<InvalidOperationException>(() => new PgArray<int>([7], [1], [0]).ToVector());
    }

    /// <summary>
    /// Six dimensions and exclusive upper bounds match PostgreSQL's dimension limits.
    /// </summary>
    [TestMethod]
    public void ShapeValidationRejectsOnlyInvalidBoundsAndCounts()
    {
        var six = new PgArray<int>([7, 8], [1, 1, 1, 1, 1, 2], [0, 1, 2, 3, 4, 5]);
        Assert.AreEqual(8, six.GetValue(0, 1, 2, 3, 4, 6));
        Assert.AreEqual(7, new PgArray<int>([7], [1], [int.MinValue]).GetValue(int.MinValue));
        Assert.AreEqual(7, new PgArray<int>([7], [1], [int.MaxValue - 1]).GetValue(int.MaxValue - 1));
        Assert.ThrowsExactly<ArgumentException>(() => new PgArray<int>([7], [1, 1, 1, 1, 1, 1, 1]));
        Assert.ThrowsExactly<ArgumentException>(() => new PgArray<int>([7], [2]));
        Assert.ThrowsExactly<ArgumentException>(() => new PgArray<int>([7], [1], [0, 1]));
        Assert.ThrowsExactly<ArgumentException>(() => new PgArray<int>([7], []));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgArray<int>([], [-1]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgArray<int>([7], [1], [int.MaxValue]));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => new PgArray<int>([], [int.MaxValue, 2]));
        Assert.ThrowsExactly<NotSupportedException>(() => new PgArray<byte>([1]));
        Assert.ThrowsExactly<NotSupportedException>(() => new PgArray<int[]>([[1]]));
        Assert.ThrowsExactly<ArgumentNullException>(() => new PgArray<int>(null!));
    }

    /// <summary>
    /// Row conversions distinguish SQL NULL elements from defaults and preserve exact decimal and temporal adapters.
    /// </summary>
    [TestMethod]
    public void TypedRowsRejectNullAndPrecisionLoss()
    {
        var row = new SpiRow([new PgArray<int?>([1, null, 3])], [new("value", 1007)]);
        Assert.AreSequenceEqual([1, null, 3], row.Get<int?[]>(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => row.Get<int[]>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => row.Get<long?[]>(0));
        row.Set(0, new DateOnly?[] { new(2000, 1, 1), null });
        Assert.AreEqual(1182U, row.GetTypeOid(0));
        Assert.AreEqual(new PgDate(0), row.Get<PgArray<PgDate?>>(0)[0]);
        Assert.IsNull(row.Get<PgArray<PgDate?>>(0)[1]);
        row.Set(0, new PgArray<PgNumeric?>([PgNumeric.FromDecimal(1.2300m), null]));
        Assert.AreSequenceEqual([1.2300m, null], row.Get<decimal?[]>(0));
        Assert.AreEqual(1231U, row.GetTypeOid(0));
        row.Set<int[]?>(0, null);
        Assert.IsNull(row.Get<PgArray<int>>(0));
        Assert.AreEqual(1007U, row.GetTypeOid(0));
        Assert.AreEqual(17U, SpiParameter.Create(new byte[] { 1 }).TypeOid);
        Assert.AreEqual(1001U, SpiParameter.Create(new byte[][] { [1] }).TypeOid);
    }

    /// <summary>
    /// Native payloads have independent big-endian headers, dimensions, signed values, and NULL flags.
    /// </summary>
    [TestMethod]
    public void NativeTransportHasExpectedIndependentLayout()
    {
        NativeValue value = NativeValue.FromArray(new PgArray<int?>([42, null, -7], [3], [-2]));
        try
        {
            byte[] bytes = value.ReadBytes();
            Assert.HasCount(104, bytes);
            Assert.AreEqual(1, BinaryPrimitives.ReadInt32BigEndian(bytes));
            Assert.AreEqual(3, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(4)));
            Assert.AreEqual(23, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(8)));
            Assert.AreEqual(3, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(12)));
            Assert.AreEqual(-2, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(16)));
            Assert.AreEqual(42L, BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(20)));
            Assert.AreEqual(1, BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(68)));
            Assert.AreEqual(-7L, BinaryPrimitives.ReadInt64BigEndian(bytes.AsSpan(76)));
            Assert.ThrowsExactly<InvalidCastException>(value.ReadArray<long?>);
            Assert.ThrowsExactly<InvalidOperationException>(value.ReadArray<int>);
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Owned strings and binary elements remain valid after native storage is released.
    /// </summary>
    [TestMethod]
    public void BufferedElementsOutliveNativeTransport()
    {
        NativeValue text = NativeValue.FromArray(new PgArray<string?>(["héllo 😀", null, ""]));
        NativeValue binary = NativeValue.FromArray(new PgArray<byte[]?>([[0, 255, 16], null, []]));
        PgArray<string?> strings;
        PgArray<byte[]?> bytes;
        try
        {
            strings = text.ReadArray<string?>();
            bytes = binary.ReadArray<byte[]?>();
        }
        finally
        {
            text.Release();
            binary.Release();
        }

        Assert.AreSequenceEqual(["héllo 😀", null, ""], strings);
        Assert.AreSequenceEqual(new byte[] { 0, 255, 16 }, bytes[0]!);
        Assert.IsNull(bytes[1]);
        Assert.IsEmpty(bytes[2]!);
    }

    /// <summary>
    /// Decodes a hand-authored transport without relying on the writer's dimension, integer, or NULL encoding.
    /// </summary>
    [TestMethod]
    public void NativeReaderAcceptsIndependentWireValues()
    {
        byte[] bytes = new byte[104];
        BinaryPrimitives.WriteInt32BigEndian(bytes, 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), 3);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 23);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), 3);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), -2);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(20), 42);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(68), 1);
        BinaryPrimitives.WriteInt64BigEndian(bytes.AsSpan(76), -7);
        NativeValue value = NativeValue.FromBytes(bytes);
        ArrayFlag(ref value) = -1;
        PgArray<int?> array;
        try
        {
            array = value.ReadArray<int?>();
        }
        finally
        {
            value.Release();
        }

        Assert.AreEqual(1, array.Rank);
        Assert.AreSequenceEqual([-2], array.LowerBounds.ToArray());
        Assert.AreSequenceEqual([3], array.Lengths.ToArray());
        Assert.AreSequenceEqual([42, null, -7], array);
    }

    /// <summary>
    /// Rejects missing bytes, wrong outer flags, extra bytes, and unsupported element identities even for empty arrays.
    /// </summary>
    /// <param name="kind">The malformed envelope.</param>
    [TestMethod]
    [DataRow("short")]
    [DataRow("unmarked")]
    [DataRow("null")]
    [DataRow("trailing")]
    [DataRow("unknown")]
    [DataRow("shaped-empty")]
    public void MalformedArrayEnvelopesAreRejected(string kind)
    {
        byte[] bytes = new byte[kind switch
        {
            "short" => 11,
            "trailing" => 13,
            "shaped-empty" => 20,
            _ => 12
        }];
        if (bytes.Length >= 12)
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), kind == "unknown" ? 999999 : 23);
        }

        if (kind == "shaped-empty")
        {
            BinaryPrimitives.WriteInt32BigEndian(bytes, 1);
        }

        NativeValue value = NativeValue.FromBytes(bytes);
        ArrayFlag(ref value) = kind == "unmarked" ? 0 : -1;
        value.IsNull = kind == "null" ? (byte)1 : (byte)0;
        try
        {
            if (kind == "unknown")
            {
                Assert.ThrowsExactly<NotSupportedException>(() => value.ReadArray<int>());
            }
            else
            {
                Assert.ThrowsExactly<InvalidOperationException>(() => value.ReadArray<int>());
            }
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Malformed counts, headers, null flags and payload lengths fail before accessing invalid native memory.
    /// </summary>
    /// <param name="offset">The header field to corrupt.</param>
    /// <param name="replacement">The invalid field value.</param>
    [TestMethod]
    [DataRow(0, -1)]
    [DataRow(0, 7)]
    [DataRow(4, int.MaxValue)]
    [DataRow(40, 2)]
    [DataRow(44, -1)]
    [DataRow(44, 1)]
    public void MalformedArrayTransportIsRejected(int offset, int replacement)
    {
        // A NULL element keeps the per-element layout these offsets address.
        NativeValue source = NativeValue.FromArray(new PgArray<int?>([42, null]));
        byte[] bytes;
        try
        {
            bytes = source.ReadBytes();
        }
        finally
        {
            source.Release();
        }

        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(offset), replacement);
        NativeValue malformed = NativeValue.FromBytes(bytes);
        ArrayFlag(ref malformed) = -1;
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => malformed.ReadArray<int?>());
        }
        finally
        {
            malformed.Release();
        }
    }

    /// <summary>
    /// Elements PostgreSQL stores as a packed native array arrive as one block with the transport's shape header and
    /// read as plain or nullable managed elements.
    /// </summary>
    [TestMethod]
    public void PackedPrimitiveElementsKeepShapeAndValues()
    {
        byte[] bytes = new byte[20 + 3 * 8];
        BinaryPrimitives.WriteInt32BigEndian(bytes, 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), 3);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(8), 20);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), 3);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), 0);
        new long[] { long.MinValue, 0, long.MaxValue }.CopyTo(MemoryMarshal.Cast<byte, long>(bytes.AsSpan(20)));
        NativeValue value = NativeValue.FromBytes(bytes);
        ArrayFlag(ref value) = -1;
        ElementKind(ref value) = 4;
        try
        {
            PgArray<long> plain = value.ReadArray<long>();
            PgArray<long?> nullable = value.ReadArray<long?>();
            Assert.AreSequenceEqual([0], plain.LowerBounds.ToArray());
            Assert.AreSequenceEqual([long.MinValue, 0, long.MaxValue], plain);
            Assert.AreSequenceEqual([long.MinValue, 0, long.MaxValue], nullable);
            Assert.AreSequenceEqual([long.MinValue, 0, long.MaxValue], ((PgArray<long?>)value.ReadArray()).Select(static item => item!.Value));
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// A packed block must match its count exactly, hold a packed element type and, for Booleans, only zero or one.
    /// </summary>
    /// <param name="oid">The transported element type.</param>
    /// <param name="payload">The packed element bytes after a one-dimensional, two-element header.</param>
    [TestMethod]
    [DataRow(23u, new byte[] { 1, 0, 0, 0 })]
    [DataRow(23u, new byte[] { 1, 0, 0, 0, 2, 0, 0, 0, 3 })]
    [DataRow(25u, new byte[] { 1, 0, 0, 0, 2, 0, 0, 0 })]
    [DataRow(16u, new byte[] { 1, 2 })]
    public void MalformedPackedArraysAreRejected(uint oid, byte[] payload)
    {
        byte[] bytes = new byte[20 + payload.Length];
        BinaryPrimitives.WriteInt32BigEndian(bytes, 1);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(4), 2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(8), oid);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(12), 2);
        BinaryPrimitives.WriteInt32BigEndian(bytes.AsSpan(16), 1);
        payload.CopyTo(bytes, 20);
        NativeValue value = NativeValue.FromBytes(bytes);
        ArrayFlag(ref value) = -1;
        ElementKind(ref value) = 4;
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => value.ReadArray());
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Primitive arrays without NULLs are written packed and read back with their shape; a NULL keeps the per-element
    /// form.
    /// </summary>
    [TestMethod]
    public void PrimitiveArraysRoundTripPacked()
    {
        AssertRoundTrip(new PgArray<double>([1.5, double.NaN, -0.0], [3], [5]), packed: true);
        AssertRoundTrip(new PgArray<bool?>([true, false]), packed: true);
        AssertRoundTrip(new PgArray<short?>([1, null]), packed: false);
        AssertRoundTrip(new PgArray<int>([]), packed: true);
        AssertRoundTrip(new PgArray<string?>(["text"]), packed: false);

        static void AssertRoundTrip<T>(PgArray<T> array, bool packed)
        {
            NativeValue value = NativeValue.FromArray(array);
            try
            {
                Assert.AreEqual(packed ? 4 : 0, ElementKind(ref value));
                PgArray<T> copy = value.ReadArray<T>();
                Assert.AreSequenceEqual(array.Lengths.ToArray(), copy.Lengths.ToArray());
                Assert.AreSequenceEqual(array.LowerBounds.ToArray(), copy.LowerBounds.ToArray());
                Assert.AreSequenceEqual(array, copy);
            }
            finally
            {
                value.Release();
            }
        }
    }

    /// <summary>
    /// A primitive vector result is written as the same packed block a <see cref="PgArray{T}"/> produces, and an empty
    /// vector becomes PostgreSQL's zero-dimensional array.
    /// </summary>
    [TestMethod]
    public void PrimitiveVectorsWriteThePackedBlock()
    {
        AssertVector<int>([int.MinValue, 0, int.MaxValue]);
        AssertVector<bool>([true, false, true]);
        AssertVector<double>([double.Epsilon, -1.5]);
        AssertVector<long>([]);

        static void AssertVector<T>(T[] values) where T : unmanaged
        {
            NativeValue vector = NativeValue.FromVector(values);
            NativeValue array = NativeValue.FromArray(new PgArray<T>(values));
            try
            {
                Assert.AreEqual(4, ElementKind(ref vector));
                Assert.AreSequenceEqual(array.ReadBytes(), vector.ReadBytes());
                PgArray<T> copy = vector.ReadArray<T>();
                Assert.AreEqual(values.Length == 0 ? 0 : 1, copy.Rank);
                Assert.AreSequenceEqual(values, copy);
            }
            finally
            {
                vector.Release();
                array.Release();
            }
        }
    }

    /// <summary>
    /// UTF-8 text arrives as a native length, -1 for SQL NULL, and the bytes; lengths must stay inside the block.
    /// </summary>
    [TestMethod]
    public void PackedTextKeepsNullsAndRejectsOverruns()
    {
        byte[] text = Encoding.UTF8.GetBytes("héllo 😀");
        using var stream = new MemoryStream();
        Span<byte> header = stackalloc byte[20];
        BinaryPrimitives.WriteInt32BigEndian(header, 1);
        BinaryPrimitives.WriteInt32BigEndian(header[4..], 3);
        BinaryPrimitives.WriteInt32BigEndian(header[8..], 25);
        BinaryPrimitives.WriteInt32BigEndian(header[12..], 3);
        BinaryPrimitives.WriteInt32BigEndian(header[16..], 1);
        stream.Write(header);
        stream.Write(BitConverter.GetBytes(text.Length));
        stream.Write(text);
        stream.Write(BitConverter.GetBytes(-1));
        stream.Write(BitConverter.GetBytes(0));
        byte[] bytes = stream.ToArray();
        NativeValue value = NativeValue.FromBytes(bytes);
        ArrayFlag(ref value) = -1;
        ElementKind(ref value) = 5;
        try
        {
            Assert.AreSequenceEqual(["héllo 😀", null, ""], value.ReadArray<string?>());
        }
        finally
        {
            value.Release();
        }

        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(20), text.Length + 9);
        NativeValue overrun = NativeValue.FromBytes(bytes);
        ArrayFlag(ref overrun) = -1;
        ElementKind(ref overrun) = 5;
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => overrun.ReadArray<string?>());
        }
        finally
        {
            overrun.Release();
        }
    }

    /// <summary>
    /// A vector read returns the elements directly and rejects other lower bounds and dimensions, as ToVector does.
    /// </summary>
    [TestMethod]
    public void VectorReadsRejectShapeLoss()
    {
        NativeValue vector = NativeValue.FromArray(new PgArray<int>([1, 2, 3]));
        NativeValue offset = NativeValue.FromArray(new PgArray<int>([1, 2], [2], [0]));
        NativeValue matrix = NativeValue.FromArray(new PgArray<string?>(["a", null, "c", "d"], [2, 2], [1, 1]));
        try
        {
            Assert.AreSequenceEqual([1, 2, 3], vector.ReadVector<int>());
            InvalidOperationException shifted = Assert.ThrowsExactly<InvalidOperationException>(() => offset.ReadVector<int>());
            Assert.Contains("Use PgArray<T>", shifted.Message);
            Assert.ThrowsExactly<InvalidOperationException>(() => matrix.ReadVector<string?>());
        }
        finally
        {
            vector.Release();
            offset.Release();
            matrix.Release();
        }
    }

    private static ref int ArrayFlag(ref NativeValue value) => ref value._auxiliary1;

    private static ref int ElementKind(ref NativeValue value) => ref value._auxiliary2;
}
