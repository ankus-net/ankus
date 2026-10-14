using System.Text;
using System.Text.Json;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies JSON text ownership, syntax validation, numeric precision, and default-value semantics.
/// </summary>
[TestClass]
public sealed class PgJsonTests
{
    /// <summary>
    /// Verifies both JSON wrappers preserve exact text while providing independently disposable documents.
    /// </summary>
    /// <param name="text">The valid JSON input.</param>
    [TestMethod]
    [DataRow("null")]
    [DataRow("false")]
    [DataRow("[]")]
    [DataRow("\"café 🐘\"")]
    [DataRow(" { \"z\":1, \"z\": 2, \"a\": 123456789012345678901234567890.000 } ")]
    [DataRow("1e1000000")]
    [DataRow("\"\\u0000\"")]
    public void ValidTextPreservesSpellingAndOwnership(string text)
    {
        var json = new PgJson(text);
        var jsonb = new PgJsonb(text);
        json.Parse().Dispose();
        jsonb.Parse().Dispose();
        Assert.AreEqual(text, json.Text);
        Assert.AreEqual(text, jsonb.Text);
        using JsonDocument jsonCopy = json.Parse();
        using JsonDocument jsonbCopy = jsonb.Parse();
        Assert.AreEqual(text.Trim(), jsonCopy.RootElement.GetRawText());
        Assert.AreEqual(text.Trim(), jsonbCopy.RootElement.GetRawText());
    }

    /// <summary>
    /// Verifies invalid JSON cannot be stored or later written through an unchecked JSON converter.
    /// </summary>
    /// <param name="text">The invalid JSON text.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow(" ")]
    [DataRow("{} []")]
    [DataRow("[1,]")]
    [DataRow("/* comment */ null")]
    [DataRow("NaN")]
    [DataRow("\"literal\nnewline\"")]
    public void InvalidSyntaxIsRejected(string text)
    {
        Assert.Throws<JsonException>(() => new PgJson(text));
        Assert.Throws<JsonException>(() => new PgJsonb(text));
    }

    /// <summary>
    /// Verifies null references and invalid UTF-16 do not silently become replacement characters.
    /// </summary>
    [TestMethod]
    public void NullAndInvalidUtf16AreRejected()
    {
        Assert.AreEqual("text", Assert.ThrowsExactly<ArgumentNullException>(() => new PgJson(null!)).ParamName);
        Assert.AreEqual("text", Assert.ThrowsExactly<ArgumentNullException>(() => new PgJsonb(null!)).ParamName);
        Assert.ThrowsExactly<EncoderFallbackException>(() => new PgJson("\"\uD800\""));
        Assert.ThrowsExactly<EncoderFallbackException>(() => new PgJsonb("\"\uDC00\""));
    }

    /// <summary>
    /// Verifies default wrappers are JSON null, nullable wrappers distinguish SQL NULL, and equality uses exact text.
    /// </summary>
    [TestMethod]
    public void DefaultNullAndTextEqualityHaveConsistentHashes()
    {
        Assert.AreEqual(new PgJson("null"), default(PgJson));
        Assert.AreEqual(new PgJsonb("null"), default(PgJsonb));
        Assert.AreEqual(new PgJson("null").GetHashCode(), default(PgJson).GetHashCode());
        Assert.AreEqual(new PgJsonb("null").GetHashCode(), default(PgJsonb).GetHashCode());
        Assert.AreNotEqual(new PgJson("1"), new PgJson("1.0"));
        Assert.AreNotEqual(new PgJsonb("{}"), new PgJsonb("{ }"));
        Assert.AreEqual("null", default(PgJson).ToString());
        Assert.AreEqual("null", default(PgJsonb).ToString());
    }

    /// <summary>
    /// Verifies server JSON is not constrained by System.Text.Json's default nesting limit.
    /// </summary>
    [TestMethod]
    public void DeepJsonRetainsAllLevels()
    {
        string text = new string('[', 128) + "42" + new string(']', 128);
        using JsonDocument document = new PgJson(text).Parse();
        JsonElement element = document.RootElement;
        for (int index = 0; index < 128; index++)
        {
            element = element[0];
        }

        Assert.AreEqual(42, element.GetInt32());
        Assert.AreEqual(text, new PgJsonb(text).Text);
    }
}
