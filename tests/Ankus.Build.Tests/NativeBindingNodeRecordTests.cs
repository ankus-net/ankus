namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    private const string NodeRecordDeclarations = """
        pub enum NodeTag { T_Invalid = 0, T_Leaf = 7, }
        pub struct Node { pub type_: NodeTag, }
        pub mod Mode {
            pub type Type = ::core::ffi::c_int;
            pub const MODE_LOW: Type = -3;
            pub const MODE_HIGH: Type = 7;
        }
        pub struct Payload {
            pub bits: i32,
            pub native_width: ::core::ffi::c_long,
            pub mode: Mode::Type,
        }
        pub struct Leaf {
            pub type_: NodeTag,
            pub payload: Payload,
            pub tail: __IncompleteArrayField<u16>,
        }
        """;

    private const string NodeRecordHeaders = """
        #include <stdint.h>
        #include <stdio.h>
        #define PG_VERSION_NUM 180006
        typedef enum NodeTag { T_Invalid = 0, T_Leaf = 7 } NodeTag;
        typedef struct Node { NodeTag type; } Node;
        typedef enum ActualMode { MODE_LOW = -3, MODE_HIGH = 7, MODE_ADDED = 91 } Mode;
        typedef struct Leaf {
            NodeTag type;
            struct { int32_t bits; int32_t added; long native_width; Mode mode; } payload;
            unsigned int flags : 3;
            uint16_t tail[];
        } Leaf;
        """;

    /// <summary>
    /// Complete measured records retain old embedded names and node APIs while exposing fields absent from the pinned catalog.
    /// </summary>
    [TestMethod]
    public async Task CompiledNodeRecordRootsRetainNativeIdentity()
    {
        const string Main = """
            #include <stddef.h>
            #include <stdlib.h>
            int main(void) {
                Leaf *value = calloc(1, sizeof(*value) + 4);
                if (value == NULL) return 2;
                value->type = T_Leaf; value->payload.bits = -44; value->payload.added = 91;
                value->payload.native_width = -23; value->payload.mode = MODE_LOW; value->flags = 7; value->tail[1] = 65000;
                printf("%zu %zu %zu %zu %zu %d %d %ld %u %u %d %d\n", sizeof(Leaf), sizeof(value->payload),
                    offsetof(Leaf, payload), offsetof(Leaf, tail), _Alignof(Leaf), value->payload.bits,
                    value->payload.added, value->payload.native_width, value->flags, (unsigned int)value->tail[1],
                    (int)value->payload.mode, MODE_ADDED);
                free(value);
                return 0;
            }
            """;
        const string Harness = """
            using System;
            using Ankus;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    byte* bytes = stackalloc byte[sizeof(Leaf) + 4];
                    new Span<byte>(bytes, sizeof(Leaf) + 4).Clear();
                    Leaf* value = (Leaf*)bytes;
                    value->type = NodeTag.T_Leaf; value->payload.bits = -44; value->payload.added = 91;
                    value->payload.native_width = -23; value->payload.mode = Mode.MODE_LOW; value->flags = 7;
                    Span<ushort> tail = Leaf.Dangerous_tail(value, 2);
                    tail[1] = 65000;
                    if (!Accepts<Leaf>(7) || Accepts<Leaf>(0) || !Accepts<Node>(uint.MaxValue))
                        throw new InvalidOperationException("Measured declarations lost their node cast contract.");
                    if (typeof(NodeTag).GetEnumUnderlyingType() != typeof(uint))
                        throw new InvalidOperationException("The node discriminator changed its existing managed representation.");
                    if (NativeBinding.NodeLayouts != $"7:{sizeof(Leaf)}:{Alignment<Leaf>()}")
                        throw new InvalidOperationException("Concrete node allocation metadata changed.");
                    fixed (ushort* start = tail)
                        return [sizeof(Leaf), sizeof(Payload), (byte*)&value->payload - bytes, (byte*)start - bytes,
                            Alignment<Leaf>(), value->payload.bits, value->payload.added,
                            value->payload.native_width, value->flags, tail[1], (int)value->payload.mode, (int)Mode.MODE_ADDED];
                }
                private static bool Accepts<T>(uint tag) where T : unmanaged, IPgNativeNode => T.AcceptsTag(tag);
                private static int Alignment<T>() where T : unmanaged, IPgNativeType => T.NativeAlignment;
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-node-record-values-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            (NativeBindingCatalog catalog, NativeBindingLayout layout, NativeRecordGraph graph) = await CollectNodeFixtureAsync(directory);
            Assert.AreSequenceEqual<string>(["ankus_node_record_Leaf", "ankus_node_record_Node", "ankus_node_tag_contract"], graph.Roots.Keys);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(graph, catalog, layout);
            long[] expected = await RunRecordWitnessAsync(NodeRecordHeaders + "\n" + Main, directory);
            Assert.AreSequenceEqual(expected, GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
            Assert.AreEqual(binding, NativeBindingRecordCSharp.Generate(graph, catalog, layout));
            NativeBindingSource raw = NativeBindingRecordCSharp.Generate(graph);
            Assert.AreNotEqual(raw.AbiIdentity, binding.AbiIdentity);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// A mismatch between native graph, pinned node identities and independent C observations rejects emission and permits recovery.
    /// </summary>
    [TestMethod]
    [DataRow("version")]
    [DataRow("major")]
    [DataRow("runtime")]
    [DataRow("address-width")]
    [DataRow("byte-order")]
    [DataRow("char-sign")]
    [DataRow("long-width")]
    [DataRow("node-size")]
    [DataRow("node-alignment")]
    [DataRow("field-offset")]
    [DataRow("field-size")]
    [DataRow("field-alignment")]
    [DataRow("element-stride")]
    [DataRow("missing-field")]
    [DataRow("missing-value")]
    [DataRow("extra-value")]
    [DataRow("tag-value")]
    [DataRow("cast-tag")]
    [DataRow("node-kind")]
    [DataRow("extra-field")]
    [DataRow("missing-tag-root")]
    [DataRow("wrong-tag-root")]
    [DataRow("missing-record-root")]
    [DataRow("duplicate-record-root")]
    [DataRow("extra-record-root")]
    [DataRow("enum-size")]
    [DataRow("enum-sign")]
    [DataRow("enum-value")]
    [DataRow("missing-enum")]
    [DataRow("extra-enum")]
    public async Task NodeRecordContractsRejectContradictions(string mutation)
    {
        string directory = Path.Join(Path.GetTempPath(), "ankus-node-record-invalid-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            (NativeBindingCatalog catalog, NativeBindingLayout layout, NativeRecordGraph graph) = await CollectNodeFixtureAsync(directory);
            NativeBindingSource expected = NativeBindingRecordCSharp.Generate(graph, catalog, layout);
            var values = layout.Types.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            var fields = values["Leaf"].Fields.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
            values["Leaf"] = values["Leaf"] with
            {
                Fields = fields
            };
            NativeBindingLayout changed = layout with
            {
                Types = values
            };
            NativeBindingCatalog declarations = catalog;
            NativeRecordGraph records = graph;
            switch (mutation)
            {
                case "version":
                    changed = changed with
                    {
                        PostgresVersion = 180005
                    };
                    break;
                case "major":
                    declarations = catalog with
                    {
                        PostgresMajor = 17
                    };
                    break;
                case "runtime":
                    changed = changed with
                    {
                        RuntimeIdentifier = "other"
                    };
                    break;
                case "address-width":
                    changed = changed with
                    {
                        PointerSize = layout.PointerSize * 2
                    };
                    break;
                case "byte-order":
                    changed = changed with
                    {
                        IsLittleEndian = !layout.IsLittleEndian
                    };
                    break;
                case "char-sign":
                    changed = changed with
                    {
                        CharIsSigned = !layout.CharIsSigned
                    };
                    break;
                case "long-width":
                    changed = changed with
                    {
                        LongSize = layout.LongSize * 2
                    };
                    break;
                case "node-size":
                    values["Leaf"] = values["Leaf"] with
                    {
                        Size = values["Leaf"].Size + 4
                    };
                    break;
                case "node-alignment":
                    values["Leaf"] = values["Leaf"] with
                    {
                        Alignment = values["Leaf"].Alignment * 2
                    };
                    break;
                case "field-offset":
                    fields["payload"] = fields["payload"] with
                    {
                        Offset = fields["payload"].Offset + 1
                    };
                    break;
                case "field-size":
                    fields["payload"] = fields["payload"] with
                    {
                        Size = fields["payload"].Size + 1
                    };
                    break;
                case "field-alignment":
                    fields["payload"] = fields["payload"] with
                    {
                        Alignment = fields["payload"].Alignment * 2
                    };
                    break;
                case "element-stride":
                    fields["tail"] = fields["tail"] with
                    {
                        ElementSize = fields["tail"].ElementSize + 1
                    };
                    break;
                case "missing-field":
                    fields.Remove("payload");
                    break;
                case "extra-field":
                    fields.Add("other", fields["payload"]);
                    break;
                case "missing-value":
                    values.Remove("Payload");
                    break;
                case "extra-value":
                    values.Add("Other", values["Payload"]);
                    break;
                case "tag-value":
                    var tags = catalog.Tags.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
                    tags["T_Leaf"] = 8;
                    declarations = catalog with
                    {
                        Tags = tags
                    };
                    break;
                case "cast-tag":
                case "node-kind":
                    var types = catalog.Types.ToDictionary(static pair => pair.Key, static pair => pair.Value, StringComparer.Ordinal);
                    types["Leaf"] = mutation == "cast-tag" ? types["Leaf"] with
                    {
                        CastTags = ["T_Missing"]
                    } : types["Leaf"] with
                    {
                        IsUnion = true
                    };
                    declarations = catalog with
                    {
                        Types = types
                    };
                    break;
                case "missing-tag-root":
                case "wrong-tag-root":
                case "missing-record-root":
                case "duplicate-record-root":
                case "extra-record-root":
                    var roots = graph.Roots.ToDictionary(StringComparer.Ordinal);
                    switch (mutation)
                    {
                        case "missing-tag-root":
                            roots.Remove("ankus_node_tag_contract");
                            break;
                        case "wrong-tag-root":
                            roots["ankus_node_tag_contract"] = roots["ankus_node_record_Node"];
                            break;
                        case "missing-record-root":
                            roots.Remove("ankus_node_record_Leaf");
                            break;
                        case "duplicate-record-root":
                            roots["ankus_node_record_Leaf"] = roots["ankus_node_record_Node"];
                            break;
                        case "extra-record-root":
                            roots.Add("ankus_node_record_Unknown", roots["ankus_node_record_Leaf"]);
                            break;
                    }

                    records = graph with
                    {
                        Roots = roots
                    };
                    break;
                case "enum-size":
                case "enum-sign":
                case "missing-enum":
                case "extra-enum":
                    var enums = layout.Enums.ToDictionary(StringComparer.Ordinal);
                    switch (mutation)
                    {
                        case "enum-size":
                            enums["Mode"] = enums["Mode"] with
                            {
                                Size = 8
                            };
                            break;
                        case "enum-sign":
                            enums["Mode"] = enums["Mode"] with
                            {
                                IsSigned = false
                            };
                            break;
                        case "missing-enum":
                            enums.Remove("Mode");
                            break;
                        case "extra-enum":
                            enums.Add("Other", enums["Mode"]);
                            break;
                    }

                    changed = changed with
                    {
                        Enums = enums
                    };
                    break;
                case "enum-value":
                    var named = catalog.Enums.ToDictionary(StringComparer.Ordinal);
                    named["Mode"] = named["Mode"] with
                    {
                        Values = new Dictionary<string, string> { ["MODE_LOW"] = "-4", ["MODE_HIGH"] = "7" }
                    };
                    declarations = catalog with
                    {
                        Enums = named
                    };
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(mutation));
            }

            Assert.ThrowsExactly<FormatException>(() => NativeBindingRecordCSharp.Generate(records, declarations, changed));
            Assert.AreEqual(expected, NativeBindingRecordCSharp.Generate(graph, catalog, layout));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// A packed embedding changes its offset and enclosing alignment without changing the embedded native type's alignment.
    /// </summary>
    [TestMethod]
    public async Task CompiledNodeRecordsPreservePackedEmbedding()
    {
        const string Declarations = """
            pub enum NodeTag { T_Invalid = 0, T_Leaf = 7, }
            pub struct Node { pub type_: NodeTag, }
            pub struct Pair {
                pub high: u16,
                pub low: u16,
            }
            pub struct Packed {
                pub prefix: u8,
                pub pair: Pair,
                pub suffix: u16,
            }
            pub struct Leaf {
                pub type_: NodeTag,
                pub packed: Packed,
            }
            """;
        const string Headers = """
            #include <stdint.h>
            #include <stdio.h>
            #define PG_VERSION_NUM 180006
            typedef enum NodeTag { T_Invalid = 0, T_Leaf = 7 } NodeTag;
            typedef struct Node { NodeTag type; } Node;
            typedef struct Pair { uint16_t high; uint16_t low; } Pair;
            #pragma pack(push, 1)
            typedef struct Packed { uint8_t prefix; Pair pair; uint16_t suffix; } Packed;
            #pragma pack(pop)
            typedef struct Leaf { NodeTag type; Packed packed; } Leaf;
            """;
        const string Main = """
            #include <stddef.h>
            typedef struct AlignedPair { uint8_t prefix; Pair value; } AlignedPair;
            int main(void) {
                Leaf value = { T_Leaf, { 91, { 65535, 257 }, 65000 } };
                printf("%zu %zu %zu %zu %zu %zu %zu %zu %u %u %u %u\n",
                    sizeof(Pair), _Alignof(Pair), sizeof(Packed), _Alignof(Packed),
                    sizeof(Leaf), offsetof(Leaf, packed), offsetof(Packed, pair), offsetof(AlignedPair, value),
                    (unsigned int)value.packed.prefix, (unsigned int)value.packed.pair.high,
                    (unsigned int)value.packed.pair.low, (unsigned int)value.packed.suffix);
                return 0;
            }
            """;
        const string Harness = """
            using Ankus;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    Leaf value = new() { type = NodeTag.T_Leaf, packed = new() { prefix = 91, pair = new() { high = 65535, low = 257 }, suffix = 65000 } };
                    AlignedPair aligned = default;
                    return [sizeof(Pair), Alignment<Pair>(), sizeof(Packed), Alignment<Packed>(),
                        sizeof(Leaf), (byte*)&value.packed - (byte*)&value, (byte*)&value.packed.pair - (byte*)&value.packed,
                        (byte*)&aligned.Value - (byte*)&aligned, value.packed.prefix,
                        value.packed.pair.high, value.packed.pair.low, value.packed.suffix];
                }
                private static int Alignment<T>() where T : unmanaged, IPgNativeType => T.NativeAlignment;
                private struct AlignedPair { public byte Prefix; public Pair Value; }
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-node-record-packed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            (NativeBindingCatalog catalog, NativeBindingLayout layout, NativeRecordGraph graph) = await CollectNodeFixtureAsync(directory, Declarations, Headers);
            Assert.AreEqual(2, layout.Types["Pair"].Alignment);
            Assert.AreEqual(new NativeBindingFieldLayout(1, 4, 2, 4), layout.Types["Packed"].Fields["pair"]);
            long[] expected = await RunRecordWitnessAsync(Headers + "\n" + Main, directory);
            Assert.AreSequenceEqual(expected, GeneratedBindingCompilation.Run(
                NativeBindingRecordCSharp.Generate(graph, catalog, layout), Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// A discriminator-only catalog still emits a loadable single contract with no invented node allocations.
    /// </summary>
    [TestMethod]
    public async Task CompiledNodeRecordsAllowEmptyValueSelection()
    {
        const string Headers = """
            #include <stdio.h>
            #define PG_VERSION_NUM 180006
            typedef enum NodeTag { T_Invalid = 0 } NodeTag;
            """;
        const string Harness = """
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static long[] Run() => [(uint)NodeTag.T_Invalid, NativeBinding.NodeLayouts.Length,
                    NativeBinding.Identity.Length, typeof(NodeTag).GetEnumUnderlyingType() == typeof(uint) ? 1 : 0];
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-node-record-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            (NativeBindingCatalog catalog, NativeBindingLayout layout, NativeRecordGraph graph) = await CollectNodeFixtureAsync(
                directory, "pub enum NodeTag { T_Invalid = 0, }", Headers);
            Assert.IsEmpty(layout.Types);
            Assert.AreSequenceEqual<long>([0, 0, 64, 1], GeneratedBindingCompilation.Run(
                NativeBindingRecordCSharp.Generate(graph, catalog, layout), Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Supplemental records retain compiler-measured storage even when no node field or function signature references them.
    /// </summary>
    [TestMethod]
    public async Task SupplementalRecordRootsPreserveNativeStorage()
    {
        const string Headers = NodeRecordHeaders + "\ntypedef struct Supplemental { unsigned short flags; long position; } Supplemental;\n";
        const string Main = """
            #include <stddef.h>
            int main(void) {
                Supplemental value = { 65000, -23 };
                printf("%zu %zu %zu %u %ld\n", sizeof(value), _Alignof(Supplemental),
                    offsetof(Supplemental, position), (unsigned int)value.flags, value.position);
                return 0;
            }
            """;
        const string Harness = """
            using Ankus;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    Supplemental value = new() { flags = 65000, position = -23 };
                    return [sizeof(Supplemental), Alignment<Supplemental>(),
                        (byte*)&value.position - (byte*)&value, value.flags, value.position];
                }
                private static int Alignment<T>() where T : unmanaged, IPgNativeType => T.NativeAlignment;
            }
            """;
        string directory = Path.Join(Path.GetTempPath(), "ankus-supplemental-record-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            (NativeBindingCatalog catalog, NativeBindingLayout layout, NativeRecordGraph graph) = await CollectNodeFixtureAsync(
                directory, headers: Headers, additionalTypes: ["Supplemental"]);
            Assert.AreSequenceEqual<string>(["ankus_header_record_Supplemental", "ankus_node_record_Leaf", "ankus_node_record_Node",
                "ankus_node_tag_contract"], graph.Roots.Keys);
            long[] expected = await RunRecordWitnessAsync(Headers + Main, directory);
            Assert.AreSequenceEqual(expected, GeneratedBindingCompilation.Run(
                NativeBindingRecordCSharp.Generate(graph, catalog, layout), Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Supplemental roots reject duplicate names and text that cannot name one native object type.
    /// </summary>
    /// <param name="name">A duplicate or invalid additional type name.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("bad-name")]
    [DataRow("Supplemental *")]
    [DataRow("Supplemental")]
    public void SupplementalRecordRootsRejectInvalidNames(string name)
    {
        NativeBindingCatalog catalog = NativeBindingParser.Parse(NodeRecordDeclarations, 18);
        Assert.ThrowsExactly<FormatException>(() => NativeBindingNodeRecords.CreateRoots(catalog, NodeRecordHeaders, ["Supplemental", name]));
        NativeBindingNodeRoots recovered = NativeBindingNodeRecords.CreateRoots(catalog, NodeRecordHeaders, ["Supplemental"]);
        Assert.AreEqual(new NativeHeaderRequest("ankus_header_record_Supplemental", "ankus_header_record_Supplemental", false), recovered.Requests[^1]);
    }

    private async Task<(NativeBindingCatalog Catalog, NativeBindingLayout Layout, NativeRecordGraph Graph)> CollectNodeFixtureAsync(
        string directory, string declarations = NodeRecordDeclarations, string headers = NodeRecordHeaders, IReadOnlyList<string>? additionalTypes = null)
    {
        NativeBindingCatalog catalog = NativeBindingParser.Parse(declarations, 18);
        string source = Path.Join(directory, "probe.c");
        string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "probe.exe" : "probe");
        await File.WriteAllTextAsync(source, NativeBindingProbe.GenerateSource(catalog, headers), context.CancellationToken);
        string compiler = OperatingSystem.IsWindows() ? "clang-cl.exe" : "cc";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["/nologo", "/std:c11", "/W4", "/WX", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), source]
            : ["-std=c11", "-Wall", "-Wextra", "-Werror", source, "-o", executable];
        await RunAsync(compiler, arguments, directory);
        NativeBindingLayout layout = NativeBindingProbe.Read(catalog, await RunAsync(executable, [], directory));
        NativeBindingNodeRoots roots = NativeBindingNodeRecords.CreateRoots(catalog, headers, additionalTypes);
        NativeRecordGraph graph = await CollectMeasuredRecordsAsync(roots.Source, roots.Requests, directory, 18);
        return (catalog, layout, graph);
    }
}
