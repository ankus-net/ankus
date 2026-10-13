using System.Collections.Immutable;
using System.Text.Json;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies statically generated tagged variants and inherited serialization contracts.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Provides type-sensitive integer and string discriminator contracts.
    /// </summary>
    private const string TaggedRecordSource = """
        [Ankus.PgType]
        [System.Text.Json.Serialization.JsonDerivedType(typeof(NumberValue), 7)]
        [System.Text.Json.Serialization.JsonDerivedType(typeof(TextValue), "7")]
        public abstract record Value;
        public sealed record NumberValue(int Number) : Value;
        public sealed record TextValue(string Text) : Value;
        """;

    /// <summary>
    /// Distinguishes string and integer tags and executes independently specified CBOR storage.
    /// </summary>
    [TestMethod]
    public void PolymorphicStringAndIntegerTagsPreserveExactVariants()
    {
        string[] actual = RunSerializedProbe<string[]>(TaggedRecordSource, """
            Value number = codec.Parse("{\"Number\":42,\"unused\":[{\"nested\":null}],\"$type\":7}");
            Value text = codec.Parse("{\"$type\":\"7\",\"Text\":\"héllo 😀\"}");
            var bytes = new System.Buffers.ArrayBufferWriter<byte>();
            codec.Write(number, bytes);
            Value decoded = codec.Read(System.Convert.FromHexString("A265247479706507664E756D626572182A"));
            return new[] { number.GetType().Name, ((NumberValue)number).Number.ToString(), text.GetType().Name,
                ((TextValue)text).Text, System.Convert.ToHexString(bytes.WrittenSpan), codec.Format(decoded) };
            """);
        Assert.AreSequenceEqual(["NumberValue", "42", "TextValue", "héllo 😀", "A265247479706507664E756D626572182A", "{\"$type\":7,\"Number\":42}"], actual);
    }

    /// <summary>
    /// Preserves exact concrete base values with and without explicit self-registration.
    /// </summary>
    /// <param name="registerSelf">Whether the base has its own explicit discriminator.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PolymorphicConcreteBaseAndSelfRegistrationExecute(bool registerSelf)
    {
        string[] actual = RunSerializedProbe<string[]>($$"""
            [Ankus.PgType]
            [System.Text.Json.Serialization.JsonDerivedType(typeof(Derived), "derived")]
            {{(registerSelf ? "[System.Text.Json.Serialization.JsonDerivedType(typeof(Value), \"base\")]" : "")}}
            public record Value(int Number);
            public sealed record Derived(int Number, string Text) : Value(Number);
            public sealed record Unregistered(int Number, string Secret) : Value(Number);
            """, """
            Value value = codec.Parse("{\"Number\":3}");
            Value derived = codec.Parse("{\"Text\":\"x\",\"Number\":4,\"$type\":\"derived\"}");
            string self;
            try { Value tagged = codec.Parse("{\"$type\":\"base\",\"Number\":5}"); self = tagged.GetType().Name + ":" + tagged.Number.ToString(); }
            catch (Ankus.PgException error) { self = error.SqlState; }
            string unregistered;
            try { codec.Write(new Unregistered(6, "kept"), new System.Buffers.ArrayBufferWriter<byte>()); unregistered = "accepted"; }
            catch (System.InvalidOperationException) { unregistered = "rejected"; }
            return new[] { value.GetType().Name, codec.Format(value), derived.GetType().Name,
                derived.Number.ToString(), ((Derived)derived).Text, self, unregistered };
            """);
        Assert.AreSequenceEqual(["Value", registerSelf ? "{\"$type\":\"base\",\"Number\":3}" : "{\"Number\":3}", "Derived", "4", "x", registerSelf ? "Value:5" : "22P02", "rejected"], actual);
    }

    /// <summary>
    /// Binds inherited constructor parameters and virtual overrides without overwriting normalization.
    /// </summary>
    [TestMethod]
    public void PolymorphicInheritedConstructorsAndOverridesPreserveValues()
    {
        string[] actual = RunSerializedProbe<string[]>("""
            [Ankus.PgType]
            [System.Text.Json.Serialization.JsonPolymorphic(TypeDiscriminatorPropertyName="kind")]
            [System.Text.Json.Serialization.JsonDerivedType(typeof(Derived), "item")]
            public abstract class Value(string name)
            {
                [System.Text.Json.Serialization.JsonPropertyName("name")]
                public virtual string Name { get; } = name.Trim();
            }
            public sealed class Derived(string name, int number) : Value(name)
            {
                public override string Name => base.Name.ToUpperInvariant();
                public int Number { get; } = number;
                public string? Extra { get; init; }
            }
            """, """
            Value value = codec.Parse("{\"Extra\":\"kept\",\"Number\":9,\"name\":\" x \",\"kind\":\"item\"}");
            var bytes = new System.Buffers.ArrayBufferWriter<byte>();
            codec.Write(value, bytes);
            Value decoded = codec.Read(bytes.WrittenSpan);
            return new[] { decoded.GetType().Name, decoded.Name, ((Derived)decoded).Number.ToString(), ((Derived)decoded).Extra! };
            """);
        Assert.AreSequenceEqual(["Derived", "X", "9", "kept"], actual);
    }

    /// <summary>
    /// Closes generic derived contracts statically and preserves their instantiated member types.
    /// </summary>
    [TestMethod]
    public void PolymorphicClosedGenericVariantsExecute()
    {
        string[] actual = RunSerializedProbe<string[]>("""
            [Ankus.PgType, System.Text.Json.Serialization.JsonDerivedType(typeof(Generic<int>), "integer")]
            public abstract class Value;
            public sealed class Generic<T> : Value { public T Item { get; set; } = default!; }
            """, """
            Value value = codec.Parse("{\"Item\":42,\"$type\":\"integer\"}");
            var bytes = new System.Buffers.ArrayBufferWriter<byte>();
            codec.Write(value, bytes);
            Value decoded = codec.Read(bytes.WrittenSpan);
            return new[] { ((Generic<int>)decoded).Item.ToString(), codec.Format(decoded) };
            """);
        Assert.AreSequenceEqual(["42", "{\"$type\":\"integer\",\"Item\":42}"], actual);
    }

    /// <summary>
    /// Executes ordinary inherited fields and overrides even when no polymorphism metadata is present.
    /// </summary>
    [TestMethod]
    public void OrdinaryInheritedMembersExecute()
    {
        string[] actual = RunSerializedProbe<string[]>("""
            public class Base
            {
                public int Number;
                public virtual string Text { get; set; } = "";
            }
            [Ankus.PgType] public sealed class Value : Base
            {
                public override string Text { get; set; } = "";
                public int Extra { get; init; }
            }
            """, """
            Value value = codec.Parse("{\"Extra\":7,\"Number\":3,\"Text\":\"kept\"}");
            var bytes = new System.Buffers.ArrayBufferWriter<byte>();
            codec.Write(value, bytes);
            Value decoded = codec.Read(bytes.WrittenSpan);
            return new[] { decoded.Number.ToString(), decoded.Text, decoded.Extra.ToString() };
            """);
        Assert.AreSequenceEqual(["3", "kept", "7"], actual);
    }

    /// <summary>
    /// Inherits required and ignored metadata while allowing an override to rename or exclude base state.
    /// </summary>
    [TestMethod]
    public void OrdinaryOverridesPreserveEffectiveJsonMetadata()
    {
        string[] actual = RunSerializedProbe<string[]>("""
            public class Base
            {
                [System.Text.Json.Serialization.JsonPropertyName("baseName")] public virtual string Name { get; set; } = "";
                [System.Text.Json.Serialization.JsonRequired] public virtual string? Optional { get; set; }
                [System.Text.Json.Serialization.JsonIgnore] public virtual System.Uri Ignored { get; } = new("https://example.com/");
                [System.Text.Json.Serialization.JsonNumberHandling(System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString)] public virtual int Excluded { get; set; }
            }
            [Ankus.PgType] public sealed class Value : Base
            {
                [System.Text.Json.Serialization.JsonPropertyName("name")] public override string Name { get; set; } = "";
                public override string? Optional { get; set; }
                public override System.Uri Ignored { get; } = new("https://example.com/derived");
                [System.Text.Json.Serialization.JsonIgnore] public override int Excluded { get; set; }
            }
            """, """
            Value value = codec.Parse("{\"name\":\"x\",\"Optional\":null,\"Ignored\":{},\"Excluded\":\"ignored\"}");
            string missing;
            try { codec.Parse("{\"name\":\"x\"}"); missing = "accepted"; }
            catch (Ankus.PgException error) { missing = error.SqlState; }
            return new[] { value.Name, codec.Format(value), missing };
            """);
        Assert.AreSequenceEqual(["x", "{\"name\":\"x\",\"Optional\":null}", "22P02"], actual);
    }

    /// <summary>
    /// Preserves nullable polymorphic members in arrays, lists and ordinal dictionaries.
    /// </summary>
    [TestMethod]
    public void PolymorphicNestedNullableGraphsExecute()
    {
        string[] actual = RunSerializedProbe<string[]>("""
            [System.Text.Json.Serialization.JsonDerivedType(typeof(Item), "item")]
            public abstract record Node;
            public sealed record Item(int Number) : Node;
            [Ankus.PgType] public sealed record Value(Node?[]? Items,
                System.Collections.Generic.List<Node?> List, System.Collections.Generic.Dictionary<string,Node?> Map);
            """, """
            Value value = codec.Parse("{\"Items\":[null,{\"Number\":1,\"$type\":\"item\"}],\"List\":[{\"$type\":\"item\",\"Number\":2},null],\"Map\":{\"A\":null,\"a\":{\"Number\":3,\"$type\":\"item\"}}}");
            var bytes = new System.Buffers.ArrayBufferWriter<byte>();
            codec.Write(value, bytes);
            Value decoded = codec.Read(bytes.WrittenSpan);
            return new[] { codec.Format(decoded), decoded.Items![1]!.GetType().Name, ((Item)decoded.Map["a"]!).Number.ToString(),
                codec.Format(codec.Parse("{\"List\":[],\"Map\":{}}")) };
            """);
        using JsonDocument expected = JsonDocument.Parse("""{"Items":[null,{"$type":"item","Number":1}],"List":[{"$type":"item","Number":2},null],"Map":{"A":null,"a":{"$type":"item","Number":3}}}""");
        using JsonDocument encoded = JsonDocument.Parse(actual[0]);
        Assert.IsTrue(JsonElement.DeepEquals(expected.RootElement, encoded.RootElement), actual[0]);
        Assert.AreEqual("Item", actual[1]);
        Assert.AreEqual("3", actual[2]);
        Assert.AreEqual("{\"Items\":null,\"List\":[],\"Map\":{}}", actual[3]);
    }

    /// <summary>
    /// Bounds recursive tagged objects with exact decimal leaves and rejects self cycles. A decimal counts as a level,
    /// because binary storage writes it as an array and .NET's CBOR reader reads at most 64 levels.
    /// </summary>
    /// <param name="amount">The decimal value whose exact scale must survive lookahead.</param>
    [TestMethod]
    [DataRow("19.125")]
    [DataRow("0.000")]
    public void PolymorphicRecursiveGraphsRespectDepthAndCycles(string amount)
    {
        string[] actual = RunSerializedProbe<string[]>("""
            [Ankus.PgType, System.Text.Json.Serialization.JsonDerivedType(typeof(Link), "link"), System.Text.Json.Serialization.JsonDerivedType(typeof(Leaf), "leaf")]
            public abstract class Value;
            public sealed class Link : Value { public Value? Next { get; set; } }
            public sealed class Leaf : Value { public decimal Amount { get; set; } }
            """, $$"""
            Value head = new Leaf { Amount = decimal.Parse("{{amount}}", System.Globalization.CultureInfo.InvariantCulture) };
            for (int i = 0; i < 62; i++) head = new Link { Next = head };
            var bytes = new System.Buffers.ArrayBufferWriter<byte>();
            codec.Write(head, bytes);
            string Describe(Value value)
            {
                int count = 1;
                while (value is Link link) { count++; value = link.Next!; }
                return count.ToString() + ":" + ((Leaf)value).Amount.ToString(System.Globalization.CultureInfo.InvariantCulture);
            }
            string Fail(System.Action action)
            {
                try { action(); return "accepted"; }
                catch (System.InvalidOperationException) { return "bounded"; }
            }
            var cycle = new Link();
            cycle.Next = cycle;
            string Reject(System.Action action)
            {
                try { action(); return "accepted"; }
                catch (Ankus.PgException error) when (error.SqlState == "22P02") { return "rejected"; }
            }
            string deeper = "{\"$type\":\"link\",\"Next\":" + codec.Format(head) + "}";
            return new[] { Describe(codec.Read(bytes.WrittenSpan)), Describe(codec.Parse(codec.Format(head))),
                Fail(() => codec.Write(new Link { Next = head }, new System.Buffers.ArrayBufferWriter<byte>())),
                Fail(() => codec.Format(new Link { Next = head })), Reject(() => codec.Parse(deeper)),
                Fail(() => codec.Format(cycle)), Fail(() => codec.Write(cycle, new System.Buffers.ArrayBufferWriter<byte>())) };
            """);
        Assert.AreSequenceEqual(["63:" + amount, "63:" + amount, "bounded", "bounded", "rejected", "bounded", "bounded"], actual);
    }

    /// <summary>
    /// Rejects invalid discriminator states without consuming them as ordinary unknown members.
    /// </summary>
    /// <param name="input">The invalid JSON document.</param>
    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"Number\":1}")]
    [DataRow("{\"$type\":null,\"Number\":1}")]
    [DataRow("{\"$type\":true,\"Number\":1}")]
    [DataRow("{\"$type\":7.5,\"Number\":1}")]
    [DataRow("{\"$type\":2147483648,\"Number\":1}")]
    [DataRow("{\"$type\":[],\"Number\":1}")]
    [DataRow("{\"$type\":\"unknown\",\"Number\":1}")]
    [DataRow("{\"$type\":7,\"Number\":1,\"$type\":7}")]
    [DataRow("{\"$type\":7,\"Number\":1,\"$type\":\"7\"}")]
    public void PolymorphicDiscriminatorsRejectInvalidInput(string input)
    {
        string[] actual = RunSerializedProbe<string[]>(TaggedRecordSource, $$"""
            string state;
            try { codec.Parse({{Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(input, true)}}); state = "accepted"; }
            catch (Ankus.PgException error) { state = error.SqlState; }
            return new[] { state, codec.Parse("{\"$type\":7,\"Number\":42}").GetType().Name };
            """);
        Assert.AreSequenceEqual(["22P02", "NumberValue"], actual);
    }

    /// <summary>
    /// Refuses unregistered direct and further-derived runtime types.
    /// </summary>
    [TestMethod]
    public void PolymorphicUnknownRuntimeTypesAreRejected()
    {
        string[] actual = RunSerializedProbe<string[]>("""
            [Ankus.PgType, System.Text.Json.Serialization.JsonDerivedType(typeof(Known), "known")]
            public abstract class Value;
            public class Known : Value { public int Number { get; set; } }
            public sealed class Further : Known { public int Extra { get; set; } }
            public sealed class Unknown : Value { public int Other { get; set; } }
            """, """
            string Fail(Value value)
            {
                try { codec.Write(value, new System.Buffers.ArrayBufferWriter<byte>()); return "accepted"; }
                catch (System.InvalidOperationException) { return "rejected"; }
            }
            return new[] { Fail(new Unknown()), Fail(new Further()), codec.Format(new Known { Number = 7 }) };
            """);
        Assert.AreSequenceEqual(["rejected", "rejected", "{\"$type\":\"known\",\"Number\":7}"], actual);
    }

    /// <summary>
    /// Diagnoses ambiguous, unsupported or lossy tagged contracts before native compilation.
    /// </summary>
    /// <param name="declaration">The invalid contract graph.</param>
    /// <param name="id">The precise tagged-contract diagnostic.</param>
    [TestMethod]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonPolymorphic] public class Value { }", "ANKUS466")]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonDerivedType(typeof(Variant), \"x\")] public struct Value; public sealed class Variant { }", "ANKUS460")]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonDerivedType(typeof(Derived))] public abstract class Value; public sealed class Derived : Value;", "ANKUS462")]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonDerivedType(typeof(Derived), null)] public abstract class Value; public sealed class Derived : Value;", "ANKUS463")]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonDerivedType(typeof(string), \"x\")] public class Value;", "ANKUS464")]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonDerivedType(typeof(Derived), \"x\")] public abstract class Value; public abstract class Derived : Value;", "ANKUS464")]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonDerivedType(typeof(Derived<>), \"x\")] public abstract class Value; public sealed class Derived<T> : Value;", "ANKUS464")]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonDerivedType(typeof(A), \"x\"), System.Text.Json.Serialization.JsonDerivedType(typeof(B), \"x\")] public abstract class Value; public sealed class A : Value; public sealed class B : Value;", "ANKUS465")]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonDerivedType(typeof(A), \"a\"), System.Text.Json.Serialization.JsonDerivedType(typeof(A), \"b\")] public abstract class Value; public sealed class A : Value;", "ANKUS465")]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonPolymorphic(IgnoreUnrecognizedTypeDiscriminators=true), System.Text.Json.Serialization.JsonDerivedType(typeof(Derived), \"x\")] public class Value; public sealed class Derived : Value;", "ANKUS461")]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonPolymorphic(UnknownDerivedTypeHandling=System.Text.Json.Serialization.JsonUnknownDerivedTypeHandling.FallBackToBaseType), System.Text.Json.Serialization.JsonDerivedType(typeof(Derived), \"x\")] public class Value; public sealed class Derived : Value;", "ANKUS461")]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonPolymorphic(UnknownDerivedTypeHandling=System.Text.Json.Serialization.JsonUnknownDerivedTypeHandling.FallBackToNearestAncestor), System.Text.Json.Serialization.JsonDerivedType(typeof(Derived), \"x\")] public class Value; public sealed class Derived : Value;", "ANKUS461")]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonDerivedType(typeof(Derived), \"x\")] public abstract class Value; public sealed class Derived : Value { [System.Text.Json.Serialization.JsonPropertyName(\"$type\")] public int Number { get; set; } }", "ANKUS443")]
    [DataRow("public class Base { public int Number { get; set; } } [Ankus.PgType] public sealed class Value : Base { public new int Number { get; set; } }", "ANKUS467")]
    [DataRow("[Ankus.PgType, System.Text.Json.Serialization.JsonDerivedType(typeof(Derived), \"x\")] public abstract class Value { private sealed class Derived : Value; }", "ANKUS464")]
    public void InvalidPolymorphicContractsAreDiagnosed(string declaration, string id)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate(declaration);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics.Where(item => item.Id == id));
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
    }
}
