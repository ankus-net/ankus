namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies how attribute names travel between the native transport and managed strings.
/// </summary>
[TestClass]
public sealed class PgAttributeNameTests
{
    /// <summary>
    /// Valid UTF-8 names, including supplementary characters whose low surrogates fall in the escape range, read and
    /// write back unchanged.
    /// </summary>
    [TestMethod]
    [DataRow("id")]
    [DataRow("café")]
    [DataRow("\U00010080")]
    [DataRow("a\U000100FFb")]
    public void ValidNamesRoundTrip(string name)
    {
        byte[] bytes = PgAttributeName.Encode(name);
        Assert.AreSequenceEqual(System.Text.Encoding.UTF8.GetBytes(name), bytes);
        Assert.AreEqual(name, PgAttributeName.Decode(bytes));
    }

    /// <summary>
    /// Each escaped byte of a SQL_ASCII name reads as its unpaired low surrogate and writes back as the same three bytes.
    /// </summary>
    [TestMethod]
    public void EscapedBytesReadAsSurrogatesAndWriteBack()
    {
        // "caf" followed by the escapes of 0xE9, 0xED, 0xB2 and 0x80, then "x".
        byte[] transported = [0x63, 0x61, 0x66, 0xED, 0xB3, 0xA9, 0xED, 0xB3, 0xAD, 0xED, 0xB2, 0xB2, 0xED, 0xB2, 0x80, 0x78];
        string name = PgAttributeName.Decode(transported);
        Assert.AreEqual("caf\uDCE9\uDCED\uDCB2\uDC80x", name);
        Assert.AreSequenceEqual(transported, PgAttributeName.Encode(name));
    }

    /// <summary>
    /// Text that is neither valid UTF-8 nor an escape is still rejected in both directions.
    /// </summary>
    [TestMethod]
    public void OtherInvalidTextIsRejected()
    {
        _ = Assert.ThrowsExactly<System.Text.DecoderFallbackException>(() => PgAttributeName.Decode([0x63, 0xE9]));
        _ = Assert.ThrowsExactly<System.Text.DecoderFallbackException>(() => PgAttributeName.Decode([0xED, 0xA0, 0x80]));
        _ = Assert.ThrowsExactly<System.Text.EncoderFallbackException>(() => PgAttributeName.Encode("a\uD800"));
        _ = Assert.ThrowsExactly<System.Text.EncoderFallbackException>(() => PgAttributeName.Encode("a\uDC10"));
    }
}
