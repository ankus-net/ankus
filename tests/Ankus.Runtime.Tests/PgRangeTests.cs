using System.Buffers.Binary;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies detached range states, structural equality, exact subtype conversions and pointer-free framing.
/// </summary>
[TestClass]
public sealed class PgRangeTests
{
    /// <summary>
    /// Empty, unbounded, half-bounded and finite singleton representations remain distinct without backend access.
    /// </summary>
    [TestMethod]
    public void RangeStatesRetainRequestedBounds()
    {
        PgRange<int> empty = PgRange.Empty<int>();
        PgRange<int> unbounded = PgRange.Unbounded<int>();
        Assert.IsTrue(empty.IsEmpty);
        Assert.IsFalse(empty.IsUnbounded);
        Assert.IsNull(empty.Lower);
        Assert.IsNull(empty.Upper);
        Assert.IsFalse(unbounded.IsEmpty);
        Assert.IsTrue(unbounded.IsUnbounded);
        Assert.IsFalse(unbounded.LowerInclusive);
        Assert.IsFalse(unbounded.UpperInclusive);
        Assert.AreNotEqual(empty, unbounded);
        Assert.AreEqual("empty", empty.ToString());
        Assert.AreEqual("(,)", unbounded.ToString());
        var left = new PgRange<int>(null, 4, true, true);
        var right = new PgRange<int>(-3, null, false, true);
        Assert.IsNull(left.Lower);
        Assert.AreEqual(4, left.Upper);
        Assert.IsFalse(left.LowerInclusive);
        Assert.IsTrue(left.UpperInclusive);
        Assert.AreEqual(-3, right.Lower);
        Assert.IsNull(right.Upper);
        Assert.IsFalse(right.LowerInclusive);
        Assert.IsFalse(right.UpperInclusive);
        Assert.IsFalse(PgRange.Create(3, 3).IsEmpty);
        Assert.IsFalse(PgRange.Create(3, 3, true, true).IsEmpty);
        Assert.AreEqual(9, PgRange.Create(9, 2).Lower);
        Assert.ThrowsExactly<NotSupportedException>(PgRange.Empty<double>);
        Assert.ThrowsExactly<NotSupportedException>(() => new PgRange<PgTime>(null, null));
    }

    /// <summary>
    /// Structural equality distinguishes flags and bounds without canonicalization and produces consistent hashes.
    /// </summary>
    [TestMethod]
    public void RangeEqualityIsDetachedAndStructural()
    {
        PgRange<int> range = PgRange.Create(1, 3);
        Assert.AreEqual(range, new PgRange<int>(1, 3));
        Assert.AreEqual(range.GetHashCode(), new PgRange<int>(1, 3).GetHashCode());
        Assert.AreNotEqual(range, new PgRange<int>(1, 3, false));
        Assert.AreNotEqual(range, new PgRange<int>(1, 3, true, true));
        Assert.AreNotEqual(range, new PgRange<int>(2, 3));
        Assert.AreNotEqual(range, new PgRange<int>(1, 4));
        PgRange<int>? missing = null;
        object other = PgRange.Create(1L, 3L);
        Assert.IsFalse(PgRange.Create(1, 3).Equals(missing));
        Assert.IsFalse(PgRange.Create(1, 3).Equals((object?)missing));
        Assert.IsFalse(range.Equals(other));
        Assert.AreEqual(PgRange.Empty<int>(), new PgRange<int>());
        Assert.AreEqual(PgRange.Create(1.00m, 2.0m), PgRange.Create(1m, 2m));
    }

    /// <summary>
    /// .NET index ranges resolve from-end bounds against length and reject invalid or negative lengths.
    /// </summary>
    [TestMethod]
    public void IndexRangeConversionResolvesLength()
    {
        Assert.AreEqual(PgRange.Create(2, 8), PgRange.FromRange(2..^2, 10));
        Assert.AreEqual(PgRange.Create(0, 10), PgRange.FromRange(.., 10));
        Assert.IsTrue(PgRange.FromRange(^0.., 10).IsEmpty);
        Assert.IsTrue(PgRange.FromRange(.., 0).IsEmpty);
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgRange.FromRange(2..1, 10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgRange.FromRange(..11, 10));
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(() => PgRange.FromRange(0..0, -1));
    }

    /// <summary>
    /// Detached data and checked .NET aliases survive disposal of the native buffer, including numeric display scale.
    /// </summary>
    [TestMethod]
    public void RangeTransportOwnsBoundsAndConvertsAliases()
    {
        RoundTrip(PgRange.Create(int.MinValue, int.MaxValue));
        RoundTrip(PgRange.Create(long.MinValue, long.MaxValue, false, true));
        RoundTrip(PgRange.Empty<PgNumeric>());
        RoundTrip(PgRange.Unbounded<DateOnly>());
        RoundTrip(new PgRange<PgDate>(PgDate.NegativeInfinity, PgDate.PositiveInfinity, true, true));
        RoundTrip(PgRange.Create(new PgTimestamp(-123456), new PgTimestamp(567890)));
        RoundTrip(PgRange.Create(PgTimestampTz.NegativeInfinity, PgTimestampTz.PositiveInfinity));
        NativeValue value = NativeValue.FromRange(PgRange.Create(1.2300m, 2.450m));
        PgRange<PgNumeric> numeric;
        try
        {
            numeric = value.ReadRange<PgNumeric>();
            Assert.AreEqual(PgRange.Create(1.2300m, 2.450m), value.ReadRange<decimal>());
            Assert.ThrowsExactly<InvalidCastException>(value.ReadRange<int>);
        }
        finally
        {
            value.Release();
        }

        Assert.AreEqual("1.2300", numeric.Lower!.Value.Text);
        Assert.AreEqual("2.450", numeric.Upper!.Value.Text);
        var row = new SpiRow([new PgRange<PgDate>(new(0), new(1))], [new("range", 3912)]);
        Assert.AreEqual(PgRange.Create(new DateOnly(2000, 1, 1), new DateOnly(2000, 1, 2)), row.Get<PgRange<DateOnly>>(0));
        Assert.ThrowsExactly<InvalidCastException>(() => row.Get<PgRange<int>>(0));
        var infinity = new SpiRow([new PgRange<PgDate>(null, PgDate.PositiveInfinity)], [new("range", 3912)]);
        Assert.ThrowsExactly<InvalidOperationException>(() => infinity.Get<PgRange<DateOnly>>(0));
    }

    /// <summary>
    /// Range arrays retain nullable elements, empty ranges, unbounded ends, shape and lower bounds.
    /// </summary>
    [TestMethod]
    public void RangeArrayTransportPreservesStates()
    {
        var input = new PgArray<PgRange<int>?>([null, PgRange.Empty<int>(), PgRange.Unbounded<int>(), new(1, null)], [2, 2], [-2, 4]);
        NativeValue value = NativeValue.FromArray(input);
        PgArray<PgRange<int>?> copy;
        try
        {
            copy = value.ReadArray<PgRange<int>?>();
        }
        finally
        {
            value.Release();
        }

        Assert.AreSequenceEqual(input, copy);
        Assert.AreSequenceEqual([2, 2], copy.Lengths.ToArray());
        Assert.AreSequenceEqual([-2, 4], copy.LowerBounds.ToArray());
        Assert.IsNull(copy[0]);
        Assert.IsTrue(copy[1]!.IsEmpty);
        Assert.IsTrue(copy[2]!.IsUnbounded);
        Assert.AreEqual(1, copy[3]!.Lower);
    }

    /// <summary>
    /// A hand-authored range transport proves flag and scalar decoding independently of the writer.
    /// </summary>
    [TestMethod]
    public void RangeReaderAcceptsIndependentFrame()
    {
        byte[] data = new byte[36];
        BinaryPrimitives.WriteInt32BigEndian(data, 3904);
        BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), 12);
        BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(8), -7);
        NativeValue value = NativeValue.FromBytes(data);
        RangeFlag(ref value) = -2;
        try
        {
            PgRange<int> range = value.ReadRange<int>();
            Assert.IsFalse(range.IsEmpty);
            Assert.IsNull(range.Lower);
            Assert.IsFalse(range.LowerInclusive);
            Assert.AreEqual(-7, range.Upper);
            Assert.IsTrue(range.UpperInclusive);
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Invalid flags, framing, integer overflow and NULL bound markers fail before reading invalid storage.
    /// </summary>
    [TestMethod]
    [DataRow("short")]
    [DataRow("unmarked")]
    [DataRow("null")]
    [DataRow("flags")]
    [DataRow("empty-bounds")]
    [DataRow("inclusive-infinite")]
    [DataRow("null-bound")]
    [DataRow("length")]
    [DataRow("trailing")]
    [DataRow("overflow")]
    public void MalformedRangeTransportIsRejected(string kind)
    {
        byte[] data = new byte[kind == "short" ? 7 : kind == "trailing" ? 37 : 36];
        BinaryPrimitives.WriteInt32BigEndian(data, 3904);
        if (data.Length >= 8)
        {
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(4), kind switch
            {
                "flags" => 32,
                "empty-bounds" => 3,
                "inclusive-infinite" => 10,
                _ => 12
            });
            BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(8), kind == "overflow" ? (long)int.MaxValue + 1 : 4);
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(28), kind == "null-bound" ? 1 : 0);
            BinaryPrimitives.WriteInt32BigEndian(data.AsSpan(32), kind == "length" ? int.MaxValue : 0);
        }

        NativeValue value = NativeValue.FromBytes(data);
        RangeFlag(ref value) = kind == "unmarked" ? 0 : -2;
        value.IsNull = kind == "null" ? (byte)1 : (byte)0;
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => value.ReadRange<int>());
        }
        finally
        {
            value.Release();
        }
    }

    /// <summary>
    /// Missing input returns false; parsing without a backend throws an operational error, which is distinct from an
    /// invalid range.
    /// </summary>
    [TestMethod]
    public void RangeParsingRequiresBackend()
    {
        Assert.IsFalse(PgRange.TryParse<int>(null, out PgRange<int>? value));
        Assert.IsNull(value);
        Assert.IsFalse(PgRange.TryParse<int>("[1,2)\0", out _));
        Assert.ThrowsExactly<InvalidOperationException>(() => PgRange.TryParse<int>("[1,2)", out _));
        Assert.ThrowsExactly<ArgumentNullException>(() => PgRange.Create(1, 2).Union(null!));
    }

    /// <summary>
    /// Range bounds retain scalar checked-conversion contracts, including upper-bound failures after a valid lower bound.
    /// </summary>
    [TestMethod]
    public void RangeAliasesRejectLossyBounds()
    {
        var start = new DateTime(2024, 1, 1, 0, 0, 0, DateTimeKind.Unspecified);
        Assert.ThrowsExactly<ArgumentException>(() => NativeValue.FromRange(PgRange.Create(start, start.AddTicks(1))));
        Assert.ThrowsExactly<ArgumentException>(() => NativeValue.FromRange(PgRange.Create(DateTime.SpecifyKind(start, DateTimeKind.Utc), start)));
        var instant = new DateTimeOffset(start, TimeSpan.Zero);
        Assert.ThrowsExactly<ArgumentException>(() => NativeValue.FromRange(PgRange.Create(instant, instant.AddTicks(1))));
        NativeValue value = NativeValue.FromRange(PgRange.Create(PgNumeric.FromDecimal(1m), PgNumeric.NaN));
        try
        {
            Assert.ThrowsExactly<OverflowException>(value.ReadRange<decimal>);
        }
        finally
        {
            value.Release();
        }
    }

    private static void RoundTrip<T>(PgRange<T> expected) where T : struct
    {
        NativeValue value = NativeValue.FromRange(expected);
        try
        {
            Assert.AreEqual(expected, value.ReadRange<T>());
        }
        finally
        {
            value.Release();
        }
    }

    private static ref int RangeFlag(ref NativeValue value) => ref value._auxiliary1;
}
