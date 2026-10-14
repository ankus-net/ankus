using System.Globalization;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies exact tuple locations, distinct native encodings, and detached scalar and array conversion.
/// </summary>
[TestClass]
public sealed class PgItemPointerTests
{
    /// <summary>
    /// Preserves all raw fields while only a zero offset makes a location invalid.
    /// </summary>
    [TestMethod]
    public void RawValuesPreserveValidityAndIdentity()
    {
        PgItemPointer zero = default;
        Assert.AreEqual(0U, zero.BlockNumber);
        Assert.AreEqual((ushort)0, zero.OffsetNumber);
        Assert.IsFalse(zero.IsValid);
        Assert.AreNotEqual(zero, PgItemPointer.Invalid);
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, 0), PgItemPointer.Invalid);
        Assert.ThrowsExactly<InvalidOperationException>(() => zero.GetBlockNumber());
        Assert.ThrowsExactly<InvalidOperationException>(() => PgItemPointer.Invalid.GetOffsetNumber());
        var special = new PgItemPointer(uint.MaxValue, ushort.MaxValue);
        Assert.IsTrue(special.IsValid);
        Assert.AreEqual(uint.MaxValue, special.GetBlockNumber());
        Assert.AreEqual(ushort.MaxValue, special.GetOffsetNumber());
        (uint block, ushort offset) = special;
        Assert.AreEqual(uint.MaxValue, block);
        Assert.AreEqual(ushort.MaxValue, offset);
        Assert.AreEqual(new PgItemPointer(17, 31), special with
        {
            BlockNumber = 17,
            OffsetNumber = 31
        });
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, 0xfffd), PgItemPointer.MovedPartitions);
        Assert.IsTrue(PgItemPointer.MovedPartitions.IndicatesMovedPartitions);
        Assert.IsFalse(new PgItemPointer(17, 0xfffd).IndicatesMovedPartitions);
        Assert.IsFalse(new PgItemPointer(uint.MaxValue, 0xfffe).IndicatesMovedPartitions);
    }

    /// <summary>
    /// Pins sparse pgrx bits independently of the dense native index witness.
    /// </summary>
    [TestMethod]
    public void PackedRepresentationsRemainDistinct()
    {
        var value = new PgItemPointer(0x006f00de, 333);
        Assert.AreEqual(0x006f00de0000014dUL, value.ToUInt64());
        Assert.AreEqual(476755919181L, value.ToIndexKey());
        Assert.AreEqual(value, PgItemPointer.FromUInt64(0x006f00de0000014dUL));
        Assert.AreEqual(value, PgItemPointer.FromIndexKey(476755919181L));
        Assert.AreEqual(0xffffffff0000ffffUL, new PgItemPointer(uint.MaxValue, ushort.MaxValue).ToUInt64());
        Assert.AreEqual(281474976710655L, new PgItemPointer(uint.MaxValue, ushort.MaxValue).ToIndexKey());
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, ushort.MaxValue), PgItemPointer.FromUInt64(0xffffffff0000ffffUL));
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, ushort.MaxValue), PgItemPointer.FromIndexKey(281474976710655L));
        Assert.AreEqual(PgItemPointer.Invalid, PgItemPointer.FromUInt64(0xffffffff00000000UL));
        Assert.AreEqual(PgItemPointer.Invalid, PgItemPointer.FromIndexKey(281474976645120L));
        Assert.AreEqual(default, PgItemPointer.FromUInt64(0));
        Assert.AreEqual(default, PgItemPointer.FromIndexKey(0));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgItemPointer.Invalid.ToIndexKey());
    }

    /// <summary>
    /// Rejects every unused region at its first and last bit rather than silently losing data.
    /// </summary>
    [TestMethod]
    public void PackedInputsRejectLoss()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgItemPointer.FromUInt64(0x10000UL));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgItemPointer.FromUInt64(0x80000000UL));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgItemPointer.FromUInt64(ulong.MaxValue));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgItemPointer.FromIndexKey(-1));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgItemPointer.FromIndexKey(281474976710656L));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgItemPointer.FromIndexKey(long.MinValue));
    }

    /// <summary>
    /// Explicit truncating helpers retain the raw pgrx and PostgreSQL decode contracts.
    /// </summary>
    [TestMethod]
    public void TruncatingCodecsAreExplicit()
    {
        Assert.AreEqual(new PgItemPointer(0x12345678, 0xdef0), PgItemPointer.FromUInt64Truncating(0x123456789abcdef0));
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, ushort.MaxValue), PgItemPointer.FromUInt64Truncating(ulong.MaxValue));
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, ushort.MaxValue), PgItemPointer.FromIndexKeyTruncating(-1));
        Assert.AreEqual(default, PgItemPointer.FromIndexKeyTruncating(long.MinValue));
        Assert.AreEqual(new PgItemPointer(0x56789abc, 0xdef0), PgItemPointer.FromIndexKeyTruncating(0x123456789abcdef0));
    }

    /// <summary>
    /// Compares blocks as unsigned values and offsets only when blocks match, including invalid locations.
    /// </summary>
    [TestMethod]
    public void OrderingMatchesTupleFields()
    {
        PgItemPointer[] sorted = [default, new(0, 1), new(0, ushort.MaxValue), new(1, 0),
            new(65535, 65535), new(65536, 0), new(0x7fffffff, 65535), new(0x80000000, 0),
            new(uint.MaxValue, 0), new(uint.MaxValue, ushort.MaxValue)];
        for (int index = 0; index < sorted.Length; index++)
        {
            PgItemPointer value = sorted[index];
            PgItemPointer equal = new(value.BlockNumber, value.OffsetNumber);
            Assert.AreEqual(0, value.CompareTo(equal));
            Assert.IsTrue(value == equal);
            Assert.IsFalse(value != equal);
            Assert.IsTrue(value <= equal);
            Assert.IsTrue(value >= equal);
            Assert.AreEqual(value.GetHashCode(), equal.GetHashCode());
            if (index != 0)
            {
                Assert.IsTrue(sorted[index - 1] < value);
                Assert.IsTrue(value > sorted[index - 1]);
                Assert.IsFalse(value <= sorted[index - 1]);
                Assert.IsFalse(sorted[index - 1] >= value);
            }
        }
    }

    /// <summary>
    /// Exercises range saturation and both carries independently of page-size tuple limits.
    /// </summary>
    [TestMethod]
    public void IncrementAndDecrementRespectBoundaries()
    {
        Assert.AreEqual(new PgItemPointer(0, 1), default(PgItemPointer).Increment());
        Assert.AreEqual(default, default(PgItemPointer).Decrement());
        Assert.AreEqual(new PgItemPointer(7, 65535), new PgItemPointer(7, 65534).Increment());
        Assert.AreEqual(new PgItemPointer(8, 0), new PgItemPointer(7, 65535).Increment());
        Assert.AreEqual(new PgItemPointer(7, 65535), new PgItemPointer(8, 0).Decrement());
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, 0), new PgItemPointer(uint.MaxValue - 1, 65535).Increment());
        Assert.AreEqual(new PgItemPointer(0, 65535), new PgItemPointer(1, 0).Decrement());
        Assert.AreEqual(new PgItemPointer(8, 0), new PgItemPointer(8, 1).Decrement());
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, 65535), new PgItemPointer(uint.MaxValue, 65535).Increment());
        Assert.AreEqual(new PgItemPointer(uint.MaxValue, 65534), new PgItemPointer(uint.MaxValue, 65535).Decrement());
    }

    /// <summary>
    /// Canonical output does not depend on the managed caller's culture.
    /// </summary>
    [TestMethod]
    public void FormattingIsCultureIndependent()
    {
        CultureInfo original = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("ar-SA");
            Assert.AreEqual("(4294967295,65535)", new PgItemPointer(uint.MaxValue, ushort.MaxValue).ToString());
            Assert.AreEqual("(0,0)", default(PgItemPointer).ToString());
        }
        finally
        {
            CultureInfo.CurrentCulture = original;
        }
    }

    /// <summary>
    /// Zero offsets preserve a present tid while nullable values alone produce SQL NULL.
    /// </summary>
    [TestMethod]
    public void TransportPreservesInvalidAndNullValues()
    {
        PgItemPointer[] values = [default, PgItemPointer.Invalid, new(0x12345678, 0xabcd), new(uint.MaxValue, ushort.MaxValue)];
        foreach (PgItemPointer value in values)
        {
            NativeValue transport = SpiType.ToNative(value);
            Assert.AreEqual(0, transport.IsNull);
            Assert.AreEqual(value.BlockNumber, transport.Integral);
            Assert.AreEqual(value.OffsetNumber, Auxiliary1(ref transport));
            Assert.AreEqual(value, transport.ReadItemPointer());
            Assert.AreEqual(value, SpiType.FromNative(transport, 27));
            Assert.AreEqual(27U, SpiParameter.Create(value).TypeOid);
        }

        Assert.AreEqual(27U, SpiParameter.Create<PgItemPointer?>(null).TypeOid);
        NativeValue empty = SpiType.ToNative(null);
        Assert.AreEqual(1, empty.IsNull);
        Assert.IsNull(SpiType.FromNative(empty, 27));
    }

    /// <summary>
    /// Both native field ranges reject negative and immediately overflowing values.
    /// </summary>
    [TestMethod]
    public void CorruptTransportIsRejected()
    {
        NativeValue value = new()
        {
            Integral = -1
        };
        Assert.ThrowsExactly<OverflowException>(() => value.ReadItemPointer());
        value.Integral = 4294967296L;
        Assert.ThrowsExactly<OverflowException>(() => value.ReadItemPointer());
        value.Integral = 0;
        Auxiliary1(ref value) = -1;
        Assert.ThrowsExactly<OverflowException>(() => value.ReadItemPointer());
        Auxiliary1(ref value) = 65536;
        Assert.ThrowsExactly<OverflowException>(() => value.ReadItemPointer());
    }

    /// <summary>
    /// Every statically closed array path preserves tid identity, dimensions and NULL cells.
    /// </summary>
    [TestMethod]
    public void ArraysPreserveIdentityShapeAndNulls()
    {
        PgItemPointer?[] values = [default(PgItemPointer), null, PgItemPointer.Invalid, new(uint.MaxValue, ushort.MaxValue)];
        var shaped = new PgArray<PgItemPointer?>(values, [2, 2], [-3, 7]);
        Assert.AreEqual(1010U, SpiParameter.Create(shaped).TypeOid);
        Assert.AreEqual(1010U, SpiParameter.Create(values).TypeOid);
        Assert.AreEqual(1010U, SpiParameter.Create<PgItemPointer[]>([]).TypeOid);
        Assert.AreEqual(1010U, SpiParameter.Create<PgArray<PgItemPointer?>?>(null).TypeOid);
        NativeValue transport = NativeValue.FromArray(shaped);
        try
        {
            PgArray<PgItemPointer?> copy = transport.ReadArray<PgItemPointer?>();
            Assert.AreSequenceEqual(values, copy);
            Assert.AreSequenceEqual([2, 2], copy.Lengths.ToArray());
            Assert.AreSequenceEqual([-3, 7], copy.LowerBounds.ToArray());
            Assert.AreEqual(27U, copy.ElementTypeOid);
            IPgArray untyped = transport.ReadArray();
            Assert.AreSequenceEqual(values, (PgArray<PgItemPointer?>)untyped);
            Assert.ThrowsExactly<InvalidOperationException>(transport.ReadArray<PgItemPointer>);
            Assert.ThrowsExactly<InvalidCastException>(transport.ReadArray<uint?>);
            var row = new SpiRow([untyped], [new("locations", 1010)]);
            Assert.AreSequenceEqual(values, row.Get<PgArray<PgItemPointer?>>(0));
            Assert.ThrowsExactly<InvalidOperationException>(() => row.Get<PgItemPointer?[]>(0));
            row.Set(0, new PgItemPointer[] { new(7, 3) });
            Assert.AreEqual(1010U, row.GetTypeOid(0));
            Assert.AreSequenceEqual([new PgItemPointer(7, 3)], row.Get<PgItemPointer[]>(0));
            row.Set(0, values);
            Assert.AreSequenceEqual(values, row.Get<PgItemPointer?[]>(0));
            Assert.ThrowsExactly<InvalidOperationException>(() => row.Get<PgItemPointer[]>(0));
        }
        finally
        {
            transport.Release();
        }

        NativeValue empty = NativeValue.FromArray(new PgArray<PgItemPointer>([]));
        try
        {
            Assert.IsEmpty(empty.ReadArray<PgItemPointer>());
            Assert.AreEqual(0, empty.ReadArray<PgItemPointer>().Rank);
        }
        finally
        {
            empty.Release();
        }
    }

    /// <summary>
    /// Exposes the internal transport field for independent wire assertions and corruption witnesses.
    /// </summary>
    private static ref int Auxiliary1(ref NativeValue value) => ref value._auxiliary1;
}
