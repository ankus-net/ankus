namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies exact copies of row and composite cells whose PostgreSQL types have no managed conversion.
/// </summary>
[TestClass]
public sealed class DetachedDatumTests
{
    private const uint Money = 790;
    private const uint MacAddress = 829;
    private const uint TextSearchVector = 3614;
    private const uint Unknown = 705;

    /// <summary>
    /// Each storage form survives the native transport unchanged.
    /// </summary>
    [TestMethod]
    public void TransportKeepsEveryStorageForm()
    {
        AssertRoundTrip(new DetachedDatum(Money, -1234));
        AssertRoundTrip(new DetachedDatum(Money, long.MinValue));
        AssertRoundTrip(new DetachedDatum(MacAddress, 6, [8, 0, 0x2B, 1, 2, 3]));
        AssertRoundTrip(new DetachedDatum(TextSearchVector, -1, [1, 2, 3, 0, 255]));
        AssertRoundTrip(new DetachedDatum(TextSearchVector, -1, []));
        AssertRoundTrip(new DetachedDatum(Unknown, -2, "text"u8));
    }

    /// <summary>
    /// A transport whose identity or shape does not match is rejected before a copy is made.
    /// </summary>
    [TestMethod]
    public void TransportRejectsMismatchedIdentityAndShape()
    {
        NativeValue byValue = NativeValue.FromDetachedDatum(new DetachedDatum(Money, 5));
        NativeValue fixedLength = NativeValue.FromDetachedDatum(new DetachedDatum(MacAddress, 6, [1, 2, 3, 4, 5, 6]));
        NativeValue wrongLength = NativeValue.FromDetachedDatum(new DetachedDatum(MacAddress, 8, [1, 2, 3, 4, 5, 6]));
        NativeValue zeroLength = NativeValue.FromDetachedDatum(new DetachedDatum(MacAddress, 0, []));
        NativeValue ordinary = NativeValue.FromBytes([1, 2]);
        try
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => byValue.ReadDetachedDatum(MacAddress));
            Assert.ThrowsExactly<InvalidOperationException>(() => byValue.ReadDetachedDatum(0));
            Assert.ThrowsExactly<InvalidOperationException>(() => wrongLength.ReadDetachedDatum(MacAddress));
            Assert.ThrowsExactly<InvalidOperationException>(() => zeroLength.ReadDetachedDatum(MacAddress));
            Assert.ThrowsExactly<InvalidOperationException>(() => ordinary.ReadDetachedDatum(MacAddress));
            Assert.AreEqual(5, byValue.ReadDetachedDatum(Money).Bits);
            Assert.AreSequenceEqual(new byte[] { 1, 2, 3, 4, 5, 6 }, fixedLength.ReadDetachedDatum(MacAddress).Payload.ToArray());
        }
        finally
        {
            byValue.Release();
            fixedLength.Release();
            wrongLength.Release();
            zeroLength.Release();
            ordinary.Release();
        }
    }

    /// <summary>
    /// An SPI row reads its converted cells while rejecting ordinary reads of a detached cell.
    /// </summary>
    [TestMethod]
    public void RowRejectsOrdinaryReadsOfDetachedCells()
    {
        var row = new SpiRow([new DetachedDatum(Money, 100), "label"], [new SpiColumn("amount", Money), new SpiColumn("label", 25)]);
        NotSupportedException error = Assert.ThrowsExactly<NotSupportedException>(() => row["amount"]);
        Assert.Contains("type OID 790", error.Message);
        Assert.ThrowsExactly<NotSupportedException>(() => row.Get<object>(0));
        Assert.ThrowsExactly<NotSupportedException>(() => row.Get<long>(0));
        Assert.AreEqual(Money, row.GetTypeOid("amount"));
        Assert.AreEqual("label", row["label"]);
        Assert.AreEqual("label", row.Get<string>(1));
    }

    /// <summary>
    /// A composite rejects ordinary reads of a detached cell and serializes it exactly with the rest of the row.
    /// </summary>
    [TestMethod]
    public void TupleSerializesDetachedCellsExactly()
    {
        PgTupleAttributeInfo[] attributes =
        [
            new("words", TextSearchVector, TextSearchVector, -1, 0),
            new("address", MacAddress, MacAddress, -1, 0),
            new("amount", Money, Money, -1, 0),
            new("id", 23, 23, -1, 0),
        ];
        var tuple = new PgHeapTuple(new PgTupleDescriptor(2249, -1, attributes),
            [new DetachedDatum(TextSearchVector, -1, [9, 8, 7]), new DetachedDatum(MacAddress, 6, [1, 2, 3, 4, 5, 6]),
                new DetachedDatum(Money, -7), 42]);
        Assert.ThrowsExactly<NotSupportedException>(() => tuple["words"]);
        Assert.ThrowsExactly<NotSupportedException>(() => tuple.Get<object>(1));
        Assert.AreEqual(42, tuple.Get<int>("id"));

        NativeValue native = NativeValue.FromTuple(tuple);
        try
        {
            PgHeapTuple copy = native.ReadTuple();
            AssertSame(new DetachedDatum(TextSearchVector, -1, [9, 8, 7]), (DetachedDatum)copy.GetStoredCell(0)!);
            AssertSame(new DetachedDatum(MacAddress, 6, [1, 2, 3, 4, 5, 6]), (DetachedDatum)copy.GetStoredCell(1)!);
            AssertSame(new DetachedDatum(Money, -7), (DetachedDatum)copy.GetStoredCell(2)!);
            Assert.AreEqual(42, copy.Get<int>(3));
        }
        finally
        {
            native.Release();
        }
    }

    private static void AssertRoundTrip(DetachedDatum value)
    {
        NativeValue native = NativeValue.FromDetachedDatum(value);
        try
        {
            AssertSame(value, native.ReadDetachedDatum(value.TypeOid));
        }
        finally
        {
            native.Release();
        }
    }

    private static void AssertSame(DetachedDatum expected, DetachedDatum actual)
    {
        Assert.AreEqual(expected.TypeOid, actual.TypeOid);
        Assert.AreEqual(expected.IsByValue, actual.IsByValue);
        Assert.AreEqual(expected.Bits, actual.Bits);
        Assert.AreSequenceEqual(expected.Payload.ToArray(), actual.Payload.ToArray());
    }
}
