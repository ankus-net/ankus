using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

/// <summary>
/// Verifies statically proved packed storage through executed generated codecs.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Preserves each integer width and exact floating-point bits in a dense native payload.
    /// </summary>
    [TestMethod]
    public void NativeLayoutMixedFieldsPreserveIndependentBytes()
    {
        string[] result = RunSerializedProbe<string[]>("""
            [Ankus.PgType(NativeLayout=true, TextCodec=typeof(TextCodec), BinaryProtocol=true)]
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack=1)]
            public struct Value
            {
                public byte Byte; public sbyte Signed; public short Short; public ushort UShort;
                public int Int; public uint UInt; public long Long; public ulong ULong; public float Single; public double Double;
            }
            public sealed class TextCodec : Ankus.PgTypeTextCodec<Value>
            {
                public override Value Parse(string text) => throw new System.InvalidOperationException("text parser reached");
                public override string Format(Value value) => throw new System.InvalidOperationException("text formatter reached");
            }
            """, """
            string hex = System.BitConverter.IsLittleEndian
                ? "01FE3412DCFE0403020198BADCFE08070605040302018899AABBCCDDEEFF00000080420000000000F87F"
                : "01FE1234FEDC01020304FEDCBA980102030405060708FFEEDDCCBBAA9988800000007FF8000000000042";
            Value decoded = codec.Read(System.Convert.FromHexString(hex));
            var bytes = new System.Buffers.ArrayBufferWriter<byte>();
            codec.Write(new Value { Byte=1, Signed=-2, Short=0x1234, UShort=0xFEDC, Int=0x01020304, UInt=0xFEDCBA98,
                Long=0x0102030405060708, ULong=0xFFEEDDCCBBAA9988,
                Single=System.BitConverter.Int32BitsToSingle(unchecked((int)0x80000000)),
                Double=System.BitConverter.Int64BitsToDouble(0x7FF8000000000042) }, bytes);
            return new[] { System.Convert.ToHexString(bytes.WrittenSpan), bytes.WrittenCount.ToString(),
                $"{decoded.Byte}:{decoded.Signed}:{decoded.Short:X4}:{decoded.UShort:X4}:{decoded.Int:X8}:{decoded.UInt:X8}:{decoded.Long:X16}:{decoded.ULong:X16}",
                System.BitConverter.SingleToInt32Bits(decoded.Single).ToString("X8"), System.BitConverter.DoubleToInt64Bits(decoded.Double).ToString("X16") };
            """);
        string expected = BitConverter.IsLittleEndian
            ? "01FE3412DCFE0403020198BADCFE08070605040302018899AABBCCDDEEFF00000080420000000000F87F"
            : "01FE1234FEDC01020304FEDCBA980102030405060708FFEEDDCCBBAA9988800000007FF8000000000042";
        Assert.AreSequenceEqual([expected, "42", "1:-2:1234:FEDC:01020304:FEDCBA98:0102030405060708:FFEEDDCCBBAA9988", "80000000", "7FF8000000000042"], result);
    }

    /// <summary>
    /// Includes private fields, nesting, unnamed enum bits and every fixed-buffer element without invoking constructors.
    /// </summary>
    [TestMethod]
    public void NativeLayoutNestedEnumsAndFixedBuffersExecute()
    {
        string[] result = RunSerializedProbe<string[]>("""
            public enum State : byte { Ready=7 }
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack=1)]
            public struct Leaf { public byte Tag; private int _number; public Leaf(byte tag,int number) { Tag=tag; _number=number; } public int Number=>_number; }
            [Ankus.PgType(NativeLayout=true, TextCodec=typeof(TextCodec))]
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack=1, Size=0, CharSet=System.Runtime.InteropServices.CharSet.None)]
            public unsafe struct Value
            {
                public Leaf Nested; public State State; public fixed short Samples[3];
                public Value() { throw new System.InvalidOperationException("constructor reached"); }
                public static Value Make() { Value result=default; result.Nested=new Leaf(0xAB,0x01020304); result.State=(State)255; result.Samples[0]=short.MinValue; result.Samples[1]=0x1234; result.Samples[2]=short.MaxValue; return result; }
                public string Describe()=> $"{Nested.Tag:X2}:{Nested.Number:X8}:{(byte)State}:{Samples[0]}:{Samples[1]}:{Samples[2]}";
            }
            public sealed class TextCodec : Ankus.PgTypeTextCodec<Value>
            {
                public override Value Parse(string text) => Value.Make();
                public override string Format(Value value) => value.Describe();
            }
            """, """
            string hex=System.BitConverter.IsLittleEndian ? "AB04030201FF00803412FF7F" : "AB01020304FF800012347FFF";
            var bytes=new System.Buffers.ArrayBufferWriter<byte>(); codec.Write(Value.Make(),bytes);
            return new[] { System.Convert.ToHexString(bytes.WrittenSpan), codec.Read(System.Convert.FromHexString(hex)).Describe(), codec.Format(codec.Parse("domain")) };
            """);
        Assert.AreSequenceEqual([BitConverter.IsLittleEndian ? "AB04030201FF00803412FF7F" : "AB01020304FF800012347FFF", "AB:01020304:255:-32768:4660:32767", "AB:01020304:255:-32768:4660:32767"], result);
    }

    /// <summary>
    /// Includes compiler backing fields while static fields and structural JSON annotations do not change storage.
    /// </summary>
    [TestMethod]
    public void NativeLayoutBackingFieldsRemainNativeStorage()
    {
        string[] result = RunSerializedProbe<string[]>("""
            [Ankus.PgType(NativeLayout=true, TextCodec=typeof(TextCodec))]
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack=1)]
            public readonly record struct Value([property:System.Text.Json.Serialization.JsonIgnore] int Number)
            { public static string Unstored="outside"; public const long UnstoredNumber=long.MaxValue; }
            public sealed class TextCodec : Ankus.PgTypeTextCodec<Value>
            {
                public override Value Parse(string text)=>new(42);
                public override string Format(Value value)=>"N"+value.Number;
            }
            """, """
            var bytes=new System.Buffers.ArrayBufferWriter<byte>(); codec.Write(new Value(42),bytes);
            Value value=codec.Read(System.Convert.FromHexString(System.BitConverter.IsLittleEndian ? "07000000" : "00000007"));
            return new[] {System.Convert.ToHexString(bytes.WrittenSpan), value.Number.ToString(), codec.Format(value)};
            """);
        Assert.AreSequenceEqual([BitConverter.IsLittleEndian ? "2A000000" : "0000002A", "7", "N7"], result);
    }

    /// <summary>
    /// Defers and shares custom text construction while native operations never call it.
    /// </summary>
    /// <param name="fail">Whether construction fails.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeLayoutTextFactoryIsDeferredAndBinaryIndependent(bool fail)
    {
        string[] result = RunSerializedProbe<string[]>($$"""
            [Ankus.PgType(NativeLayout=true, TextCodec=typeof(TextCodec))]
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack=1)]
            public struct Value { public int Number; }
            public sealed class TextCodec : Ankus.PgTypeTextCodec<Value>
            {
                public static int Created, Parsed, Formatted;
                public TextCodec() { Created++; {{(fail ? "throw new Ankus.PgException(\"P7920\",\"native text factory failed\");" : "")}} }
                public override Value Parse(string text) { Parsed++; return new Value {Number=7}; }
                public override string Format(Value value) { Formatted++; return "N"+value.Number; }
            }
            """, """
            string Invoke(System.Func<string> call) { try { return call(); } catch (Ankus.PgException error) { return error.SqlState+":"+error.Message; } }
            byte[] fixture=System.Convert.FromHexString(System.BitConverter.IsLittleEndian ? "2A000000" : "0000002A");
            Value value=codec.Read(fixture); var bytes=new System.Buffers.ArrayBufferWriter<byte>(); codec.Write(value,bytes);
            string before=$"{TextCodec.Created}:{TextCodec.Parsed}:{TextCodec.Formatted}";
            string parsed=Invoke(()=>codec.Parse("domain").Number.ToString()); string formatted=Invoke(()=>codec.Format(value));
            var after=new System.Buffers.ArrayBufferWriter<byte>(); codec.Write(codec.Read(fixture),after);
            return new[] { before, parsed, formatted, $"{TextCodec.Created}:{TextCodec.Parsed}:{TextCodec.Formatted}",
                System.Convert.ToHexString(bytes.WrittenSpan), System.Convert.ToHexString(after.WrittenSpan) };
            """);
        string hex = BitConverter.IsLittleEndian ? "2A000000" : "0000002A";
        Assert.AreSequenceEqual(fail
            ? ["0:0:0", "P7920:native text factory failed", "P7920:native text factory failed", "1:0:0", hex, hex]
            : ["0:0:0", "7", "N42", "1:1:1", hex, hex], result);
    }

    /// <summary>
    /// Generates nullable scalar, shaped array, set and tuple signatures with the declared SQL mapping.
    /// </summary>
    /// <param name="binary">Whether send and receive functions are emitted.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void NativeLayoutSignaturesCompileAcrossTypedContracts(bool binary)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            [Ankus.PgType(NativeLayout=true, TextCodec=typeof(TextCodec), BinaryProtocol={{(binary ? "true" : "false")}}, NullInputErrorMessage="native required")]
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack=1)]
            public struct Value { public int Number; }
            public sealed class TextCodec : Ankus.PgTypeTextCodec<Value>
            {
                public override Value Parse(string text)=>default;
                public override string Format(Value value)=>"native";
            }
            public static class Functions
            {
                [Ankus.PgFunction] public static Value? Echo(Value? value)=>value;
                [Ankus.PgFunction] public static Ankus.PgArray<Value?>? Array(Ankus.PgArray<Value?>? value)=>value;
                [Ankus.PgFunction] public static System.Collections.Generic.IEnumerable<Value?> Rows(Value value)=>new Value?[]{value,null};
                [Ankus.PgFunction] public static Ankus.PgHeapTuple Tuple(Ankus.PgHeapTuple value)=>value;
            }
            """);
        AssertAggregateCompilation(compilation, diagnostics);
        string sql = ManifestValue(compilation, "Ankus.Sql");
        Assert.Contains("\"value\"[]", sql);
        Assert.Contains("SETOF \"value\"", sql);
        Assert.AreEqual(binary, sql.Contains("CREATE FUNCTION \"value_recv\"", StringComparison.Ordinal));
        Assert.AreEqual(binary, sql.Contains("CREATE FUNCTION \"value_send\"", StringComparison.Ordinal));
        string input = Assert.ContainsSingle(sql.Split('\n').Where(static line => line.StartsWith("CREATE FUNCTION \"value_in\"", StringComparison.Ordinal)));
        Assert.DoesNotContain(" STRICT", input);
    }

    /// <summary>
    /// Rejects layouts that cannot prove dense, fixed-width and independently valid native bytes.
    /// </summary>
    /// <param name="declaration">The unsupported root or field graph.</param>
    /// <param name="id">The precise native-layout diagnostic.</param>
    [TestMethod]
    [DataRow("[L(K.Auto,Pack=1)] public struct Value { public int Number; }", "ANKUS438")]
    [DataRow("[L(K.Explicit,Pack=1)] public struct Value { [System.Runtime.InteropServices.FieldOffset(0)] public int A; [System.Runtime.InteropServices.FieldOffset(0)] public int B; }", "ANKUS438")]
    [DataRow("[L(K.Sequential,Pack=1,Size=8)] public struct Value { public int Number; }", "ANKUS438")]
    [DataRow("[L(K.Sequential,Pack=1,CharSet=System.Runtime.InteropServices.CharSet.Unicode)] public struct Value { public int Number; }", "ANKUS438")]
    [DataRow("[L(K.Sequential,Pack=1)] public struct Value { }", "ANKUS441")]
    [DataRow("[L(K.Sequential,Pack=1)] public sealed class Value { public int Number; }", "ANKUS436")]
    [DataRow("public enum Value : byte { A }", "ANKUS436")]
    [DataRow("[L(K.Sequential,Pack=1)] public struct Value { public bool Item; }", "ANKUS437")]
    [DataRow("[L(K.Sequential,Pack=1)] public struct Value { public char Item; }", "ANKUS437")]
    [DataRow("[L(K.Sequential,Pack=1)] public struct Value { public nint Item; }", "ANKUS437")]
    [DataRow("[L(K.Sequential,Pack=1)] public struct Value { public nuint Item; }", "ANKUS437")]
    [DataRow("[L(K.Sequential,Pack=1)] public unsafe struct Value { public int* Item; }", "ANKUS437")]
    [DataRow("[L(K.Sequential,Pack=1)] public unsafe struct Value { public delegate*<int> Item; }", "ANKUS437")]
    [DataRow("[L(K.Sequential,Pack=1)] public struct Value { public string Item; }", "ANKUS437")]
    [DataRow("[L(K.Sequential,Pack=1)] public struct Value { public int[] Item; }", "ANKUS437")]
    [DataRow("[L(K.Sequential,Pack=1)] public struct Value { public decimal Item; }", "ANKUS437")]
    [DataRow("[L(K.Sequential,Pack=1)] public struct Value { public System.Guid Item; }", "ANKUS437")]
    [DataRow("[L(K.Sequential,Pack=1)] public struct Value { public System.DateTime Item; }", "ANKUS437")]
    [DataRow("[L(K.Sequential,Pack=1)] public struct Value { public int? Item; }", "ANKUS437")]
    [DataRow("[L(K.Sequential,Pack=1)] public struct Value { public Leaf<int> Item; } [L(K.Sequential,Pack=1)] public struct Leaf<T> where T:unmanaged { public T Number; }", "ANKUS437")]
    [DataRow("[L(K.Sequential,Pack=1)] public unsafe struct Value { public fixed bool Items[2]; }", "ANKUS440")]
    [DataRow("[L(K.Sequential,Pack=1)] public unsafe struct Value { public fixed char Items[2]; }", "ANKUS440")]
    [DataRow("[L(K.Sequential,Pack=1)] public unsafe struct Value { public fixed long Items[int.MaxValue]; }", "ANKUS442")]
    [DataRow("[L(K.Sequential,Pack=1),System.Runtime.CompilerServices.InlineArray(4)] public struct Value { private byte _element; }", "ANKUS438")]
    public void InvalidNativeLayoutContractsAreDiagnosed(string declaration, string id)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            using L=System.Runtime.InteropServices.StructLayoutAttribute;
            using K=System.Runtime.InteropServices.LayoutKind;
            [Ankus.PgType(NativeLayout=true, TextCodec=typeof(TextCodec))]
            """ + declaration + """
            public sealed class TextCodec : Ankus.PgTypeTextCodec<Value>
            {
                public override Value Parse(string text)=>default!;
                public override string Format(Value value)=>"native";
            }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics.Where(value => value.Id == id));
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    /// <summary>
    /// Sequential layouts with natural alignment, an explicit Pack, nested structs and fixed buffers have the size .NET
    /// gives them, and every padding run is passed to registration and storage so writes set it to zero.
    /// </summary>
    /// <param name="declaration">The layout.</param>
    /// <param name="size">The expected size.</param>
    /// <param name="padding">The expected padding argument.</param>
    [TestMethod]
    [DataRow("public struct Value { public int A; public int B; }", 8, "[]")]
    [DataRow("public struct Value { public byte A; public int B; }", 8, "new int[] { 1, 3 }")]
    [DataRow("[L(K.Sequential,Pack=2)] public struct Value { public byte A; public int B; }", 6, "new int[] { 1, 1 }")]
    [DataRow("[L(K.Sequential)] public struct Value { public int A; public byte B; }", 8, "new int[] { 5, 3 }")]
    [DataRow("public struct Value { public byte A; public Leaf L; public byte C; } public struct Leaf { public short S; public long N; }",
        32, "new int[] { 1, 7, 10, 6, 25, 7 }")]
    [DataRow("[L(K.Sequential,Pack=4)] public struct Value { public byte A; public long B; }", 12, "new int[] { 1, 3 }")]
    [DataRow("public unsafe struct Value { public byte A; public fixed long Items[2]; }", 24, "new int[] { 1, 7 }")]
    public void NativeLayoutsFollowSequentialAlignment(string declaration, int size, string padding)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            using L=System.Runtime.InteropServices.StructLayoutAttribute;
            using K=System.Runtime.InteropServices.LayoutKind;
            [Ankus.PgType(NativeLayout=true, TextCodec=typeof(TextCodec))]
            """ + declaration + """
            public sealed class TextCodec : Ankus.PgTypeTextCodec<Value>
            {
                public override Value Parse(string text)=>default!;
                public override string Format(Value value)=>"native";
            }
            """);
        Assert.IsEmpty(diagnostics);
        string generated = string.Concat(compilation.SyntaxTrees.Select(tree => tree.GetText(context.CancellationToken).ToString()));
        string count = size.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Contains("RegisterNative<global::Value>(\"value\", null, " + count + ", " + padding + ", static", generated);
        Assert.Contains("_codec = new(" + count + ", " + padding + ", static", generated);
    }

    /// <summary>
    /// Rejects absent text conversion and conflicting full-codec storage options.
    /// </summary>
    /// <param name="options">The unsupported combination.</param>
    /// <param name="id">The precise storage-selection diagnostic.</param>
    [TestMethod]
    [DataRow("NativeLayout=true", "ANKUS422")]
    [DataRow("typeof(Codec),NativeLayout=true", "ANKUS422")]
    [DataRow("typeof(Codec),NativeLayout=true,TextCodec=typeof(Codec)", "ANKUS421")]
    public void InvalidNativeLayoutOptionsAreDiagnosed(string options, string id)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            [Ankus.PgType({{options}})]
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential, Pack=1)]
            public struct Value { public int Number; }
            public sealed class Codec : Ankus.PgTypeCodec<Value>
            {
                public override Value Parse(string text)=>default;
                public override string Format(Value value)=>"native";
                public override Value Read(System.ReadOnlySpan<byte> payload)=>default;
                public override void Write(Value value,System.Buffers.IBufferWriter<byte> destination) { }
            }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics.Where(value => value.Id == id));
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    /// <summary>
    /// Rejects generic root layouts before emitting a closed native codec.
    /// </summary>
    [TestMethod]
    public void NativeLayoutGenericRootsAreDiagnosed()
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [Ankus.PgType(NativeLayout=true,TextCodec=typeof(TextCodec))]
            [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential,Pack=1)]
            public struct Value<T> where T:unmanaged { public T Number; }
            public sealed class TextCodec : Ankus.PgTypeTextCodec<Value<int>>
            {
                public override Value<int> Parse(string text)=>default;
                public override string Format(Value<int> value)=>"native";
            }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics.Where(static value => value.Id == "ANKUS427"));
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
    }
}
