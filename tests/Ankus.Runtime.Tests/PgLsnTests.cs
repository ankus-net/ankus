namespace Ankus.Runtime.Tests;

/// <summary>
/// Checks write-ahead log positions against PostgreSQL's <c>pg_lsn</c> text form and ordering.
/// </summary>
[TestClass]
public sealed class PgLsnTests
{
    /// <summary>
    /// The text form is the high and low 32 bits in upper-case hexadecimal without leading zeros, and parsing it
    /// returns the same position.
    /// </summary>
    /// <param name="text">PostgreSQL's text form.</param>
    /// <param name="value">The position.</param>
    [TestMethod]
    [DataRow("0/0", 0UL)]
    [DataRow("16/B374D848", 0x16B374D848UL)]
    [DataRow("FFFFFFFF/FFFFFFFF", ulong.MaxValue)]
    public void TextRoundTrips(string text, ulong value)
    {
        Assert.AreEqual(new PgLsn(value), PgLsn.Parse(text));
        Assert.AreEqual(text, new PgLsn(value).ToString());
        Assert.AreEqual(new PgLsn(value), PgLsn.Parse(text.ToLowerInvariant()));
        Span<char> buffer = stackalloc char[17];
        Assert.IsTrue(new PgLsn(value).TryFormat(buffer, out int written, default, null));
        Assert.AreEqual(text, buffer[..written].ToString());
    }

    /// <summary>
    /// PostgreSQL 19 pads the low half to eight digits; that form parses to the same position.
    /// </summary>
    /// <param name="text">The padded text form.</param>
    /// <param name="value">The position.</param>
    [TestMethod]
    [DataRow("0/00001000", 0x1000UL)]
    [DataRow("16/0374D848", 0x160374D848UL)]
    public void PaddedTextParses(string text, ulong value) => Assert.AreEqual(new PgLsn(value), PgLsn.Parse(text));

    /// <summary>
    /// Text without exactly one slash between two hexadecimal numbers of up to eight digits is rejected.
    /// </summary>
    /// <param name="text">The rejected text.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("16")]
    [DataRow("/1")]
    [DataRow("1/")]
    [DataRow("1/2/3")]
    [DataRow("G/0")]
    [DataRow("100000000/0")]
    [DataRow(" 1/0")]
    public void MalformedTextIsRejected(string text)
    {
        Assert.IsFalse(PgLsn.TryParse(text, out _));
        Assert.ThrowsExactly<FormatException>(() => PgLsn.Parse(text));
    }

    /// <summary>
    /// Positions order by their value, and the invalid position comes first.
    /// </summary>
    [TestMethod]
    public void PositionsOrderByValue()
    {
        PgLsn early = PgLsn.Parse("1/FFFFFFFF");
        PgLsn late = PgLsn.Parse("2/0");
        Assert.IsTrue(early < late && late > early && early <= late && late >= early);
        Assert.IsLessThan(0, early.CompareTo(late));
        Assert.IsFalse(PgLsn.Invalid.IsValid);
        Assert.IsTrue(early.IsValid);
        Assert.IsGreaterThan(0, early.CompareTo(null));
    }
}
