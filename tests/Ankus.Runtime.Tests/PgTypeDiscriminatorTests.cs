using System.Buffers;
using System.Formats.Cbor;
using System.Text;
using System.Text.Json;

namespace Ankus.Runtime.Tests;

/// <summary>
/// Verifies polymorphic discriminator lookahead without consuming owned JSON or CBOR cursors.
/// </summary>
[TestClass]
public sealed class PgTypeDiscriminatorTests
{
    /// <summary>
    /// Finds a discriminator at any position while leaving every field available to the actual reader.
    /// </summary>
    [TestMethod]
    [DataRow("{\"$kind\":\"cat\",\"Value\":42,\"Unknown\":null}", "A365246B696E64636361746556616C7565182A67556E6B6E6F776EF6", "$kind,Value,Unknown")]
    [DataRow("{\"Value\":42,\"$kind\":\"cat\",\"Unknown\":null}", "A36556616C7565182A65246B696E646363617467556E6B6E6F776EF6", "Value,$kind,Unknown")]
    [DataRow("{\"Value\":42,\"Unknown\":null,\"$kind\":\"cat\"}", "A36556616C7565182A67556E6B6E6F776EF665246B696E6463636174", "Value,Unknown,$kind")]
    [DataRow("{\"$kind\":\"cat\",\"Value\":42,\"Unknown\":null}", "BF65246B696E64636361746556616C7565182A67556E6B6E6F776EF6FF", "$kind,Value,Unknown")]
    public void DiscriminatorPositionPreservesOriginalFieldTraversal(string json, string hex, string names)
    {
        var codec = new ProbeCodec();
        ProbeResult text = codec.Parse(json);
        ProbeResult binary = codec.Read(Convert.FromHexString(hex));
        Assert.AreEqual("cat", Assert.IsInstanceOfType<string>(text.Tag));
        Assert.AreEqual("cat", Assert.IsInstanceOfType<string>(binary.Tag));
        Assert.AreEqual(text.Tag, text.RepeatedTag);
        Assert.AreEqual(binary.Tag, binary.RepeatedTag);
        Assert.AreEqual(42L, text.Value);
        Assert.AreEqual(42L, binary.Value);
        Assert.AreEqual(names, string.Join(',', text.Names));
        Assert.AreEqual(names, string.Join(',', binary.Names));
    }

    /// <summary>
    /// Preserves exact signed integer discriminator identity at both boundaries.
    /// </summary>
    [TestMethod]
    [DataRow(0, "{\"$kind\":0}", "A165246B696E6400")]
    [DataRow(int.MinValue, "{\"$kind\":-2147483648}", "A165246B696E643A7FFFFFFF")]
    [DataRow(int.MaxValue, "{\"$kind\":2147483647}", "A165246B696E641A7FFFFFFF")]
    public void IntegerDiscriminatorsPreserveInt32Identity(int expected, string json, string hex)
    {
        var codec = new ProbeCodec();
        Assert.AreEqual(expected, Assert.IsInstanceOfType<int>(codec.Parse(json).Tag));
        Assert.AreEqual(expected, Assert.IsInstanceOfType<int>(codec.Read(Convert.FromHexString(hex)).Tag));
    }

    /// <summary>
    /// Keeps textual numbers, empty tags and Unicode distinct from integer tags.
    /// </summary>
    [TestMethod]
    [DataRow("0", "{\"$kind\":\"0\"}", "A165246B696E646130")]
    [DataRow("", "{\"$kind\":\"\"}", "A165246B696E6460")]
    [DataRow("é😀", "{\"$kind\":\"é😀\"}", "A165246B696E6466C3A9F09F9880")]
    [DataRow("cat", "{\"$kind\":\"cat\"}", "A165246B696E647F6163626174FF")]
    public void StringDiscriminatorsRetainTheirExactTypeAndText(string expected, string json, string hex)
    {
        var codec = new ProbeCodec();
        Assert.AreEqual(expected, Assert.IsInstanceOfType<string>(codec.Parse(json).Tag));
        Assert.AreEqual(expected, Assert.IsInstanceOfType<string>(codec.Read(Convert.FromHexString(hex)).Tag));
    }

    /// <summary>
    /// Treats an absent tag separately from a present invalid null and matches names ordinally.
    /// </summary>
    [TestMethod]
    [DataRow("{}", "A0", "", 0L)]
    [DataRow("{\"$Kind\":\"cat\"}", "A165244B696E6463636174", "$Kind", 0L)]
    [DataRow("{\"Value\":42}", "A16556616C7565182A", "Value", 42L)]
    public void MissingAndDifferentlyCasedDiscriminatorsRemainAbsent(string json, string hex, string names, long value)
    {
        var codec = new ProbeCodec();
        ProbeResult text = codec.Parse(json);
        ProbeResult binary = codec.Read(Convert.FromHexString(hex));
        Assert.IsNull(text.Tag);
        Assert.IsNull(binary.Tag);
        Assert.AreEqual(names, string.Join(',', text.Names));
        Assert.AreEqual(names, string.Join(',', binary.Names));
        Assert.AreEqual(value, text.Value);
        Assert.AreEqual(value, binary.Value);
    }

    /// <summary>
    /// Scans only the current nested object and preserves the following parent's sibling fields.
    /// </summary>
    [TestMethod]
    public void NestedDiscriminatorUsesCurrentOffsetAndPreservesParentSiblings()
    {
        var codec = new NestedProbeCodec();
        NestedResult text = codec.Parse("{\"$kind\":\"outer\",\"Nested\":{\"$kind\":\"inner\",\"Value\":7},\"Tail\":43}");
        NestedResult binary = codec.Read(Convert.FromHexString("A365246B696E64656F75746572664E6573746564A265246B696E6465696E6E65726556616C756507645461696C182B"));
        Assert.AreEqual("inner", text.Nested.Tag);
        Assert.AreEqual("inner", binary.Nested.Tag);
        Assert.AreEqual(7L, text.Nested.Value);
        Assert.AreEqual(7L, binary.Nested.Value);
        Assert.AreSequenceEqual<string>(["$kind", "Value"], text.Nested.Names);
        Assert.AreSequenceEqual<string>(["$kind", "Value"], binary.Nested.Names);
        Assert.AreEqual(43L, text.Tail);
        Assert.AreEqual(43L, binary.Tail);
    }

    /// <summary>
    /// Rejects duplicate tags even when unknown fields separate identical discriminator values.
    /// </summary>
    [TestMethod]
    [DataRow("{\"$kind\":\"cat\",\"$kind\":\"cat\"}", "A265246B696E646363617465246B696E6463636174")]
    [DataRow("{\"$kind\":1,\"Value\":42,\"$kind\":2}", "A365246B696E64016556616C7565182A65246B696E6402")]
    public void DuplicateDiscriminatorsAreRejected(string json, string hex)
    {
        var codec = new ProbeCodec();
        Assert.AreEqual("22P02", Assert.ThrowsExactly<PgException>(() => codec.Parse(json)).SqlState);
        Assert.AreEqual("22P03", Assert.ThrowsExactly<PgException>(() => codec.Read(Convert.FromHexString(hex))).SqlState);
    }

    /// <summary>
    /// Refuses wrong token kinds, floating-point coercion and immediately out-of-range integers.
    /// </summary>
    [TestMethod]
    [DataRow("null", "F6")]
    [DataRow("true", "F5")]
    [DataRow("false", "F4")]
    [DataRow("1.0", "F93C00")]
    [DataRow("1e0", "FA3F800000")]
    [DataRow("[]", "80")]
    [DataRow("{}", "A0")]
    [DataRow("2147483648", "1A80000000")]
    [DataRow("-2147483649", "3A80000000")]
    [DataRow("18446744073709551615", "1BFFFFFFFFFFFFFFFF")]
    public void InvalidDiscriminatorValuesHaveFormatSpecificErrors(string jsonValue, string cborValue)
    {
        var codec = new ProbeCodec();
        Assert.AreEqual("22P02", Assert.ThrowsExactly<PgException>(() => codec.Parse("{\"$kind\":" + jsonValue + "}")).SqlState);
        Assert.AreEqual("22P03", Assert.ThrowsExactly<PgException>(() => codec.Read(Convert.FromHexString("A165246B696E64" + cborValue))).SqlState);
    }

    /// <summary>
    /// Requires the probed value to be an object and validates the complete object before returning a tag.
    /// </summary>
    [TestMethod]
    [DataRow("[]", "80")]
    [DataRow("null", "F6")]
    [DataRow("1", "01")]
    [DataRow("{\"$kind\":1,\"Value\":", "A265246B696E64016556616C7565")]
    [DataRow("{\"$kind\":\"unfinished", "A165246B696E6463")]
    public void NonObjectAndTruncatedProbesAreRejected(string json, string hex)
    {
        var codec = new ProbeCodec();
        Assert.AreEqual("22P02", Assert.ThrowsExactly<PgException>(() => codec.Parse(json)).SqlState);
        Assert.AreEqual("22P03", Assert.ThrowsExactly<PgException>(() => codec.Read(Convert.FromHexString(hex))).SqlState);
    }

    /// <summary>
    /// Validates unknown strings after a matching tag instead of returning the discriminator early.
    /// </summary>
    [TestMethod]
    public void DiscriminatorLookaheadValidatesUnknownUnicode()
    {
        var codec = new ProbeCodec();
        Assert.AreEqual("22P02", Assert.ThrowsExactly<PgException>(() => codec.Parse("{\"$kind\":1,\"Unknown\":[\"\\uD800\"]}")).SqlState);
        Assert.AreEqual("22P03", Assert.ThrowsExactly<PgException>(() => codec.Read(Convert.FromHexString("A265246B696E640167556E6B6E6F776E8162C080"))).SqlState);
        Assert.AreEqual("22P02", Assert.ThrowsExactly<PgException>(() => codec.Parse("{\"$kind\":\"\\uD800\"}")).SqlState);
        Assert.AreEqual("22P03", Assert.ThrowsExactly<PgException>(() => codec.Read(Convert.FromHexString("A165246B696E6462C080"))).SqlState);
        Assert.ThrowsExactly<InvalidOperationException>(() => ProbeOnly(Encoding.UTF8.GetBytes("{\"$kind\":1,\"Unknown\":[\"\\uD800\"]}"), json: true));
        Assert.ThrowsExactly<CborContentException>(() => ProbeOnly(Convert.FromHexString("A265246B696E640167556E6B6E6F776E8162C080"), json: false));
    }

    /// <summary>
    /// Rejects an array directly during lookahead without relying on the subsequent object decoder.
    /// </summary>
    [TestMethod]
    public void DiscriminatorProbeItselfRequiresAnObject()
    {
        Assert.ThrowsExactly<FormatException>(() => ProbeOnly(Encoding.UTF8.GetBytes("[]"), json: true));
        Assert.ThrowsExactly<InvalidOperationException>(() => ProbeOnly([0x80], json: false));
    }

    /// <summary>
    /// Applies the original contract depth to the probe and its unknown nested values.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NestedLookaheadKeepsTheGlobalSixtyFourContainerLimit(bool json)
    {
        Assert.AreEqual(1, ProbeAtDepth(json, 63, nestedUnknown: false));
        Assert.AreEqual(1, ProbeAtDepth(json, 62, nestedUnknown: true));
        if (json)
        {
            Assert.Throws<JsonException>(() => ProbeAtDepth(json, 64, nestedUnknown: false));
            Assert.Throws<JsonException>(() => ProbeAtDepth(json, 63, nestedUnknown: true));
        }
        else
        {
            Assert.ThrowsExactly<FormatException>(() => ProbeAtDepth(json, 64, nestedUnknown: false));
            Assert.ThrowsExactly<FormatException>(() => ProbeAtDepth(json, 63, nestedUnknown: true));
        }
    }

    /// <summary>
    /// Counts a decimal as a nesting level, as CBOR's two-element decimal fraction is: it reads back one level below
    /// the limit, and the writer and reader both refuse it at the limit, in either format.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DecimalsCountAsANestingLevel(bool json)
    {
        var destination = new ArrayBufferWriter<byte>();
        using (var writer = new PgTypeWriter(destination, json))
        {
            WriteDecimalAtDepth(writer, 62);
            writer.Complete();
        }

        var reader = new PgTypeReader(destination.WrittenSpan, json);
        for (int position = 0; position < 62; position++)
        {
            reader.ReadStartArray();
        }

        Assert.AreEqual(1, reader.PeekDiscriminator("$kind"));
        reader.ReadStartObject();
        Assert.AreEqual("$kind", reader.ReadPropertyName());
        Assert.AreEqual(1L, reader.ReadInt64());
        Assert.AreEqual("Price", reader.ReadPropertyName());
        Assert.AreSequenceEqual<int>([123, 0, 0, 2 << 16], decimal.GetBits(reader.ReadDecimal()));
        Assert.IsNull(reader.ReadPropertyName());
        for (int position = 0; position < 62; position++)
        {
            Assert.IsTrue(reader.ReadEndArray());
        }

        reader.Complete();
        using (var writer = new PgTypeWriter(new ArrayBufferWriter<byte>(), json))
        {
            Assert.ThrowsExactly<InvalidOperationException>(() => WriteDecimalAtDepth(writer, 63));
        }

        byte[] deeper = json
            ? Encoding.UTF8.GetBytes(string.Concat(Enumerable.Repeat("[", 63)) + "{\"$kind\":1,\"Price\":1.23}" + string.Concat(Enumerable.Repeat("]", 63)))
            : [.. Enumerable.Repeat((byte)0x81, 63), .. Convert.FromHexString("A265246B696E6401655072696365C48221187B")];
        Assert.ThrowsExactly<FormatException>(() => ReadDecimalAtDepth(deeper, json, 63));
    }

    /// <summary>
    /// Skips a valid unknown decimal fraction as one value one level below the limit, without narrowing it to the .NET
    /// decimal range, and rejects it at the limit, where .NET's CBOR reader would refuse its array.
    /// </summary>
    /// <param name="hex">The decimal fraction's CBOR encoding.</param>
    [TestMethod]
    [DataRow("C48221187B")]
    [DataRow("C49F21187BFF")]
    [DataRow("C4823903E801")]
    [DataRow("C4821903E8C24D01000000000000000000000000")]
    [DataRow("C4821903E8C34D01000000000000000000000000")]
    [DataRow("C48220C25F41014102FF")]
    [DataRow("C482003BFFFFFFFFFFFFFFFF")]
    [DataRow("C4821BFFFFFFFFFFFFFFFF01")]
    [DataRow("C4823BFFFFFFFFFFFFFFFF01")]
    public void UnknownDecimalFractionsCountAsANestingLevel(string hex)
    {
        Assert.AreEqual(1, ProbeDecimalAtDepth(hex, 62));
        Assert.ThrowsExactly<FormatException>(() => ProbeDecimalAtDepth(hex, 63));
    }

    /// <summary>
    /// Rejects malformed decimal fractions without letting a semantic tag hide arbitrary nested containers.
    /// </summary>
    [TestMethod]
    [DataRow("C48100")]
    [DataRow("C483000000")]
    [DataRow("C49F000000FF")]
    [DataRow("C49F00FF")]
    [DataRow("C4828000")]
    [DataRow("C4820080")]
    [DataRow("C48200C280")]
    [DataRow("C48200C260")]
    [DataRow("C482F93C0001")]
    [DataRow("C48200F93C00")]
    [DataRow("C48200C400")]
    public void UnknownDecimalFractionsRequireExactlyTwoIntegralComponents(string hex)
    {
        var codec = new ProbeCodec();
        Assert.AreEqual("22P03", Assert.ThrowsExactly<PgException>(() => codec.Read(Convert.FromHexString("A265246B696E6401655072696365" + hex))).SqlState);
    }

    /// <summary>
    /// Keeps CBOR storage detached from its caller while allowing repeated probes and normal consumption.
    /// </summary>
    [TestMethod]
    public void BinaryProbeUsesTheReadersOwnedInput()
    {
        byte[] payload = Convert.FromHexString("A265246B696E64636361746556616C7565182A");
        var reader = new PgTypeReader(payload, json: false);
        payload.AsSpan().Clear();
        Assert.AreEqual("cat", reader.PeekDiscriminator("$kind"));
        Assert.AreEqual("cat", reader.PeekDiscriminator("$kind"));
        reader.ReadStartObject();
        Assert.AreEqual("$kind", reader.ReadPropertyName());
        Assert.AreEqual("cat", reader.ReadString());
        Assert.AreEqual("Value", reader.ReadPropertyName());
        Assert.AreEqual(42L, reader.ReadInt64());
        Assert.IsNull(reader.ReadPropertyName());
        reader.Complete();
    }

    /// <summary>
    /// Accepts an empty exact property name but rejects a null name before probing.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void DiscriminatorNameValidationDistinguishesNullAndEmpty(bool json)
    {
        byte[] payload = json ? Encoding.UTF8.GetBytes("{\"\":\"cat\"}") : Convert.FromHexString("A16063636174");
        var reader = new PgTypeReader(payload, json);
        Assert.AreEqual("cat", reader.PeekDiscriminator(""));
        Assert.AreEqual("name", Assert.ThrowsExactly<ArgumentNullException>(() => ProbeWithNullName(json)).ParamName);
    }

    /// <summary>
    /// Builds an actual nested cursor and returns only the probe's result without a later validating traversal.
    /// </summary>
    private static void WriteDecimalAtDepth(PgTypeWriter writer, int arrays)
    {
        for (int position = 0; position < arrays; position++)
        {
            writer.WriteStartArray(1);
        }

        writer.WriteStartObject(2);
        writer.WritePropertyName("$kind");
        writer.WriteInt64(1);
        writer.WritePropertyName("Price");
        writer.WriteDecimal(1.23m);
        writer.WriteEndObject();
        for (int position = 0; position < arrays; position++)
        {
            writer.WriteEndArray();
        }
    }

    private static decimal ReadDecimalAtDepth(byte[] payload, bool json, int arrays)
    {
        var reader = new PgTypeReader(payload, json);
        for (int position = 0; position < arrays; position++)
        {
            reader.ReadStartArray();
        }

        reader.ReadStartObject();
        Assert.AreEqual("$kind", reader.ReadPropertyName());
        Assert.AreEqual(1L, reader.ReadInt64());
        Assert.AreEqual("Price", reader.ReadPropertyName());
        return reader.ReadDecimal();
    }

    private static object? ProbeDecimalAtDepth(string hex, int arrays)
    {
        byte[] payload = [.. Enumerable.Repeat((byte)0x81, arrays), .. Convert.FromHexString("A265246B696E6401655072696365" + hex)];
        var reader = new PgTypeReader(payload, json: false);
        for (int position = 0; position < arrays; position++)
        {
            reader.ReadStartArray();
        }

        return reader.PeekDiscriminator("$kind");
    }

    private static object? ProbeAtDepth(bool json, int arrays, bool nestedUnknown)
    {
        string value = nestedUnknown ? "[0]" : "0";
        string document = new string('[', arrays) + "{\"$kind\":1,\"Unknown\":" + value + "}" + new string(']', arrays);
        byte[] payload = json ? Encoding.UTF8.GetBytes(document)
            : [.. Enumerable.Repeat((byte)0x81, arrays), .. Convert.FromHexString("A265246B696E640167556E6B6E6F776E"), .. nestedUnknown ? new byte[] { 0x81, 0 } : [0]];
        var reader = new PgTypeReader(payload, json);
        for (int position = 0; position < arrays; position++)
        {
            reader.ReadStartArray();
        }

        return reader.PeekDiscriminator("$kind");
    }

    /// <summary>
    /// Exposes argument validation without capturing a ref struct in an assertion delegate.
    /// </summary>
    private static object? ProbeWithNullName(bool json)
    {
        byte[] payload = json ? Encoding.UTF8.GetBytes("{}") : [0xA0];
        var reader = new PgTypeReader(payload, json);
        return reader.PeekDiscriminator(null!);
    }

    /// <summary>
    /// Observes only lookahead validation before any subsequent read or completion check can run.
    /// </summary>
    private static object? ProbeOnly(byte[] payload, bool json)
    {
        var reader = new PgTypeReader(payload, json);
        return reader.PeekDiscriminator("$kind");
    }

    /// <summary>
    /// Observes both repeated lookahead and the complete traversal of the unchanged object.
    /// </summary>
    private static ProbeResult ReadProbe(ref PgTypeReader reader)
    {
        object? tag = reader.PeekDiscriminator("$kind");
        object? repeatedTag = reader.PeekDiscriminator("$kind");
        reader.ReadStartObject();
        var names = new List<string>();
        long value = 0;
        while (reader.ReadPropertyName() is { } name)
        {
            names.Add(name);
            if (name == "Value")
            {
                value = reader.ReadInt64();
            }
            else
            {
                reader.Skip();
            }
        }

        return new(tag, repeatedTag, value, [.. names]);
    }

    /// <summary>
    /// Carries the exact observations made before and after discriminator probing.
    /// </summary>
    private sealed record ProbeResult(object? Tag, object? RepeatedTag, long Value, string[] Names);

    /// <summary>
    /// Carries one nested contract and its following parent field.
    /// </summary>
    private sealed record NestedResult(ProbeResult Nested, long Tail);

    /// <summary>
    /// Exercises input error translation and stream completion around the direct probe helper.
    /// </summary>
    private sealed class ProbeCodec : PgSerializedTypeCodec<ProbeResult>
    {
        /// <inheritdoc />
        protected override ProbeResult ReadValue(ref PgTypeReader reader) => ReadProbe(ref reader);

        /// <inheritdoc />
        protected override void WriteValue(PgTypeWriter writer, ProbeResult value) => throw new NotSupportedException();
    }

    /// <summary>
    /// Starts lookahead after consuming an enclosing object's first field and nested-property name.
    /// </summary>
    private sealed class NestedProbeCodec : PgSerializedTypeCodec<NestedResult>
    {
        /// <inheritdoc />
        protected override NestedResult ReadValue(ref PgTypeReader reader)
        {
            reader.ReadStartObject();
            ProbeResult? nested = null;
            long tail = 0;
            while (reader.ReadPropertyName() is { } name)
            {
                if (name == "Nested")
                {
                    nested = ReadProbe(ref reader);
                }
                else if (name == "Tail")
                {
                    tail = reader.ReadInt64();
                }
                else
                {
                    reader.Skip();
                }
            }

            return new(nested ?? throw new FormatException("Expected a nested contract."), tail);
        }

        /// <inheritdoc />
        protected override void WriteValue(PgTypeWriter writer, NestedResult value) => throw new NotSupportedException();
    }
}
