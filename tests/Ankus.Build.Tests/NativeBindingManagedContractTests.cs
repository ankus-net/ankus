using System.Runtime.InteropServices;

namespace Ankus.Build.Tests;

/// <summary>
/// Verifies generated declaration identity and rejects unrepresentable native value graphs.
/// </summary>
[TestClass]
public sealed class NativeBindingManagedContractTests(TestContext context)
{
    /// <summary>
    /// Invalid storage, cycles and incomplete compiler identities fail explicitly before emission, then valid emission recovers.
    /// </summary>
    [TestMethod]
    [DataRow("value-cycle")]
    [DataRow("canonical-wrapper")]
    [DataRow("enum-cycle")]
    [DataRow("enum-size")]
    [DataRow("enum-range")]
    [DataRow("boolean-size")]
    [DataRow("integer-size")]
    [DataRow("field-name")]
    [DataRow("field-bounds")]
    [DataRow("zero-value")]
    [DataRow("huge-value")]
    [DataRow("missing-target")]
    [DataRow("missing-roots")]
    [DataRow("old-major")]
    [DataRow("new-major")]
    [DataRow("compiler")]
    public void ManagedRecordContractsRejectInvalidGraphs(string mutation)
    {
        NativeRecordGraph graph = CreateGraph();
        NativeBindingSource valid = NativeBindingRecordCSharp.Generate(graph);
        NativeRecordType[] types = [.. graph.Types];
        NativeRecordDeclaration declaration = graph.Declarations[0];
        NativeRecordGraph changed = graph with
        {
            Types = types
        };
        switch (mutation)
        {
            case "value-cycle":
                declaration = declaration with
                {
                    Fields = [declaration.Fields[0] with { Type = 0 }]
                };
                break;
            case "canonical-wrapper":
                types[1] = types[1] with
                {
                    Kind = "alias",
                    Element = 1,
                    SourceDeclaration = "typedef unsigned int count;"
                };
                break;
            case "enum-cycle":
            case "enum-size":
            case "enum-range":
                types[0] = types[0] with
                {
                    Kind = "enum"
                };
                declaration = declaration with
                {
                    Kind = "enum",
                    Fields = [],
                    EnumUnderlying = mutation == "enum-cycle" ? 0 : 1,
                    EnumValues = [new("Limit", mutation == "enum-range" ? "4294967296" : "1")]
                };
                if (mutation == "enum-cycle")
                {
                    changed = changed with
                    {
                        Types = [types[0]]
                    };
                }

                if (mutation == "enum-size")
                {
                    types[1] = types[1] with
                    {
                        Size = 8,
                        Alignment = 8
                    };
                }

                break;
            case "boolean-size":
                types[1] = types[1] with
                {
                    Name = "_Bool"
                };
                break;
            case "integer-size":
                types[1] = types[1] with
                {
                    Size = 3,
                    Alignment = 1
                };
                break;
            case "field-name":
                declaration = declaration with
                {
                    Fields = [declaration.Fields[0] with { Name = "a.b" }]
                };
                break;
            case "field-bounds":
                declaration = declaration with
                {
                    Fields = [declaration.Fields[0] with { OffsetBits = 8 }]
                };
                break;
            case "zero-value":
                types[1] = types[1] with
                {
                    Size = 0,
                    Alignment = 1
                };
                break;
            case "huge-value":
                types[0] = types[0] with
                {
                    Size = (long)int.MaxValue + 1
                };
                declaration = declaration with
                {
                    Size = (long)int.MaxValue + 1
                };
                break;
            case "missing-target":
                changed = changed with
                {
                    Target = null!
                };
                break;
            case "missing-roots":
                changed = changed with
                {
                    Roots = null!
                };
                break;
            case "old-major":
                changed = changed with
                {
                    Target = graph.Target with
                    {
                        PostgresVersion = 120000
                    }
                };
                break;
            case "new-major":
                changed = changed with
                {
                    Target = graph.Target with
                    {
                        PostgresVersion = 200000
                    }
                };
                break;
            case "compiler":
                changed = changed with
                {
                    Target = graph.Target with
                    {
                        ClangMajor = 0
                    }
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mutation));
        }

        changed = changed with
        {
            Declarations = [declaration]
        };
        Assert.ThrowsExactly<FormatException>(() => NativeBindingRecordCSharp.Generate(changed));
        Assert.AreEqual(valid, NativeBindingRecordCSharp.Generate(graph));
    }

    /// <summary>
    /// Empty selections produce loadable metadata and host mismatches fail before any generated value is used.
    /// </summary>
    [TestMethod]
    public void ManagedRecordContractsValidateHostWithEmptySelection()
    {
        NativeRecordGraph graph = CreateGraph() with
        {
            Roots = new Dictionary<string, int>(),
            Types = [],
            Declarations = []
        };
        const string Harness = "public static class BindingAssertions { public static long[] Run() => [Ankus.Postgres.NativeBinding.Identity.Length]; }";
        Assert.AreSequenceEqual([64L], GeneratedBindingCompilation.Run(NativeBindingRecordCSharp.Generate(graph), Harness, context.CancellationToken));
        string otherHost = OperatingSystem.IsWindows() ? "linux-x64" : "win-x64";
        NativeRecordGraph changed = graph with
        {
            Target = graph.Target with
            {
                RuntimeIdentifier = otherHost,
                PointerSize = 8,
                IsLittleEndian = true
            }
        };
        TypeInitializationException error = Assert.ThrowsExactly<TypeInitializationException>(() =>
            GeneratedBindingCompilation.Run(NativeBindingRecordCSharp.Generate(changed), Harness, context.CancellationToken));
        Assert.IsInstanceOfType<PlatformNotSupportedException>(error.InnerException);
        Assert.AreSequenceEqual([64L], GeneratedBindingCompilation.Run(NativeBindingRecordCSharp.Generate(graph), Harness, context.CancellationToken));
    }

    /// <summary>
    /// Root enumeration order is immaterial while native numeric, signature and storage facts change assembly identity.
    /// </summary>
    [TestMethod]
    [DataRow("version")]
    [DataRow("compiler")]
    [DataRow("numeric")]
    [DataRow("field")]
    [DataRow("qualifier")]
    [DataRow("root")]
    public void ManagedRecordContractsRetainCompleteIdentity(string mutation)
    {
        NativeRecordGraph graph = CreateGraph() with
        {
            Roots = new Dictionary<string, int> { ["z"] = 0, ["a"] = 0 }
        };
        NativeBindingSource expected = NativeBindingRecordCSharp.Generate(graph);
        Assert.AreEqual(expected, NativeBindingRecordCSharp.Generate(graph with
        {
            Roots = new Dictionary<string, int> { ["a"] = 0, ["z"] = 0 }
        }));
        NativeRecordGraph changed = mutation switch
        {
            "version" => graph with { Target = graph.Target with { PostgresVersion = 180005 } },
            "compiler" => graph with { Target = graph.Target with { ClangMajor = 20 } },
            "numeric" => graph with { Target = graph.Target with { Numeric = graph.Target.Numeric with { CharIsSigned = !graph.Target.Numeric.CharIsSigned } } },
            "field" => graph with { Declarations = [graph.Declarations[0] with { Fields = [graph.Declarations[0].Fields[0] with { Name = "renamed" }] }] },
            "qualifier" => graph with { Types = [graph.Types[0], graph.Types[1] with { Qualifiers = NativeHeaderQualifiers.Const }] },
            "root" => graph with { Roots = new Dictionary<string, int> { ["other"] = 0 } },
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        NativeBindingSource actual = NativeBindingRecordCSharp.Generate(changed);
        Assert.AreNotEqual(expected.AbiIdentity, actual.AbiIdentity);
        Assert.AreNotEqual(expected.AssemblyName, actual.AssemblyName);
        Assert.AreEqual(expected, NativeBindingRecordCSharp.Generate(graph));
    }

    /// <summary>
    /// Plain char is unsigned on Linux ARM64 and signed elsewhere; bindings give it sbyte on every target, so the same
    /// source compiles on each, as Rust's c_char does. A plain char bitfield's value would depend on the target, so an
    /// unsigned target rejects one instead of reading it with the other sign.
    /// </summary>
    /// <param name="charIsSigned">Whether the target's plain char is signed.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public void PlainCharMapsToSbyteOnEveryTarget(bool charIsSigned)
    {
        NativeRecordGraph graph = CreateGraph();
        graph = graph with
        {
            Target = graph.Target with { Numeric = graph.Target.Numeric with { CharIsSigned = charIsSigned } },
            Types = [graph.Types[0] with { Size = 1, Alignment = 1 }, new("scalar", 1, "char", 0, 1, 1, "char", null, null, null, null, null)],
            Declarations = [new("struct", "Root", true, 1, 1, [new("tag", 1, 0, null, false, "char tag;")], null, [])],
        };
        Assert.Contains("public sbyte @tag;", NativeBindingRecordCSharp.Generate(graph).Source);

        NativeRecordGraph bitfield = graph with
        {
            Declarations = [new("struct", "Root", true, 1, 1, [new("tag", 1, 0, 3, false, "char tag : 3;")], null, [])],
        };
        if (charIsSigned)
        {
            Assert.Contains("public sbyte @tag", NativeBindingRecordCSharp.Generate(bitfield).Source);
        }
        else
        {
            FormatException error = Assert.ThrowsExactly<FormatException>(() => NativeBindingRecordCSharp.Generate(bitfield));
            Assert.Contains("plain char bitfield", error.Message);
        }
    }

    private static NativeRecordGraph CreateGraph()
    {
        var target = new NativeHeaderTarget(180006, RuntimeInformation.RuntimeIdentifier, IntPtr.Size, BitConverter.IsLittleEndian, 21, NativeNumericModelFixture.Binary80);
        NativeRecordType[] types =
        [
            new("record", 0, "struct Root", 0, 4, 4, "", null, 0, null, null, null),
            new("scalar", 1, "unsigned int", 0, 4, 4, "unsigned int", null, null, null, null, null),
        ];
        var declaration = new NativeRecordDeclaration("struct", "Root", true, 4, 4, [new("count", 1, 0, null, false, "unsigned int count;")], null, []);
        return new(target, new Dictionary<string, int> { ["current"] = 0 }, types, [declaration]);
    }

    /// <summary>
    /// Complete empty native records retain their zero size without acquiring a fictitious one-byte CLR value.
    /// </summary>
    [TestMethod]
    public void ManagedRecordContractsDistinguishEmptyStorage()
    {
        NativeRecordGraph graph = CreateGraph();
        NativeRecordGraph empty = graph with
        {
            Types = [graph.Types[0] with { Size = 0, Alignment = 1 }],
            Declarations = [graph.Declarations[0] with { Size = 0, Alignment = 1, Fields = [] }],
        };
        const string Harness = """
            using System;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static long[] Run() => [Root.IsComplete ? 1 : 0, Root.NativeSize,
                    typeof(Root).IsValueType && !typeof(Ankus.IPgNativeType).IsAssignableFrom(typeof(Root)) ? 1 : 0];
            }
            """;
        Assert.AreSequenceEqual([1L, 0L, 1L], GeneratedBindingCompilation.Run(NativeBindingRecordCSharp.Generate(empty), Harness, context.CancellationToken));
    }
}
