namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    private const string AvailableNodeDeclarations = """
        pub enum NodeTag { T_Invalid = 0, T_Leaf = 7, }
        pub struct Node {
            pub type_: NodeTag,
        }
        pub struct Obsolete {
            pub old: i32,
        }
        pub struct Nested {
            pub active: i32,
            pub removed: i32,
        }
        pub struct Leaf {
            pub type_: NodeTag,
            pub obsolete: i32,
            pub old_child: Obsolete,
            pub nested: Nested,
            pub items: [Nested; 2usize],
            pub tail: __IncompleteArrayField<u16>,
        }
        """;

    private const string AvailableNodeHeaders = """
        #include <stdint.h>
        #include <stdio.h>
        #define PG_VERSION_NUM 180006
        typedef enum NodeTag { T_Invalid = 0, T_Leaf = 7 } NodeTag;
        typedef struct Node { NodeTag type; } Node;
        typedef struct Obsolete { int32_t old; } Obsolete;
        typedef struct Nested {
            int32_t active;
        #if KEEP_REFERENCE_FIELDS
            int32_t removed;
        #endif
            int32_t added;
        } Nested;
        typedef struct Leaf {
            NodeTag type;
        #if KEEP_REFERENCE_FIELDS
            int32_t obsolete;
            Obsolete old_child;
        #endif
            Nested nested;
            Nested items[2];
            unsigned int flags : 3;
            uint16_t tail[];
        } Leaf;
        """;

    /// <summary>
    /// Removed scalar, embedded and nested fields are explicit while new fields and native bitfields remain usable.
    /// </summary>
    [TestMethod]
    public Task SelectedNodeFieldsFollowHeadersAndPreserveAddedValues()
        => VerifySelectedNodeFieldsAsync(keepReferenceFields: false);

    /// <summary>
    /// The same selection retains all reference fields when they are still present in the selected headers.
    /// </summary>
    [TestMethod]
    public Task SelectedNodeFieldsKeepEveryPresentReferenceField()
        => VerifySelectedNodeFieldsAsync(keepReferenceFields: true);

    /// <summary>
    /// Anonymous embedded records reached through multiple array dimensions retain their original native identities.
    /// </summary>
    [TestMethod]
    public async Task SelectedNodeFieldsResolveNestedArrayDependencies()
    {
        const string Declarations = """
            pub enum NodeTag { T_Invalid = 0, T_Leaf = 7, }
            pub struct Node { pub type_: NodeTag, }
            pub struct Cell {
                pub value: i32,
                pub removed: i32,
            }
            pub struct Outer {
                pub cells: [[Cell; 3usize]; 2usize],
            }
            pub struct Leaf {
                pub type_: NodeTag,
                pub outer: Outer,
            }
            """;
        const string Headers = """
            #include <stdint.h>
            #include <stdio.h>
            #define PG_VERSION_NUM 180006
            typedef enum NodeTag { T_Invalid = 0, T_Leaf = 7 } NodeTag;
            typedef struct Node { NodeTag type; } Node;
            typedef struct Leaf {
                NodeTag type;
                struct { struct { int32_t value; int32_t added; } cells[2][3]; } outer;
            } Leaf;
            """;
        string directory = Directory.CreateTempSubdirectory("ankus-selected-node-arrays-").FullName;
        try
        {
            (NativeBindingSelectedNodes selected, NativeHeaderRecords records, NativeBindingLayout layout) =
                await CollectSelectedNodeFixtureAsync(directory, Declarations, Headers);
            Assert.AreSequenceEqual<NativeBindingAbsentNodeField>([new("Cell", "removed")], selected.AbsentFields);
            Assert.AreEqual(8, layout.Types["Cell"].Size);
            Assert.AreEqual(48, layout.Types["Outer"].Size);
            Assert.AreEqual(52, layout.Types["Leaf"].Size);
            Assert.AreEqual(new NativeBindingFieldLayout(0, 48, 4, 24), layout.Types["Outer"].Fields["cells"]);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(records, selected.Catalog, layout, []);
            const string Harness = """
                public static class BindingAssertions
                {
                    public static unsafe long[] Run()
                    {
                        Ankus.Postgres.Leaf value = default;
                        value.outer.cells[0][0].value = -731;
                        value.outer.cells[1][2].value = 83;
                        value.outer.cells[1][2].added = 19;
                        return [sizeof(Ankus.Postgres.Cell), sizeof(Ankus.Postgres.Outer), sizeof(Ankus.Postgres.Leaf),
                            value.outer.cells[0][0].value, value.outer.cells[1][2].value,
                            value.outer.cells[1][2].added, value.outer.cells[1][1].added];
                    }
                }
                """;
            Assert.AreSequenceEqual<long>([8, 48, 52, -731, 83, 19, 0], GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Removing, displacing or changing a node discriminator cannot preserve an unsafe inherited cast contract.
    /// </summary>
    /// <param name="prefix">The incompatible native prefix declaration.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("int32_t before; NodeTag type;")]
    [DataRow("uint32_t type;")]
    [DataRow("Node type;")]
    public async Task SelectedNodeFieldsRejectChangedNodePrefix(string prefix)
    {
        const string Declarations = """
            pub enum NodeTag { T_Invalid = 0, T_Leaf = 7, }
            pub struct Node { pub type_: NodeTag, }
            pub struct Leaf {
                pub type_: NodeTag,
                pub value: i32,
            }
            """;
        const string Headers = """
            #include <stdint.h>
            typedef enum NodeTag { T_Invalid = 0, T_Leaf = 7 } NodeTag;
            typedef struct Node { NodeTag type; } Node;
            typedef struct Leaf { __PREFIX__ int32_t value; } Leaf;
            """;
        string directory = Directory.CreateTempSubdirectory("ankus-selected-node-prefix-").FullName;
        try
        {
            NativeBindingCatalog catalog = NativeBindingParser.Parse(Declarations, 18);
            NativeBindingNodeRoots roots = NativeBindingNodeRecords.CreateRoots(catalog, Headers.Replace("__PREFIX__", prefix, StringComparison.Ordinal));
            NativeHeaderRecords invalid = await CollectCallRecordsAsync(roots.Source, [.. roots.Requests], directory);
            Assert.ThrowsExactly<FormatException>(() => NativeBindingNodeAvailability.Read(catalog, invalid.Graph));
            NativeBindingNodeRoots valid = NativeBindingNodeRecords.CreateRoots(catalog, Headers.Replace("__PREFIX__", "NodeTag type;", StringComparison.Ordinal));
            NativeHeaderRecords recovered = await CollectCallRecordsAsync(valid.Source, [.. valid.Requests], directory);
            NativeBindingSelectedNodes selected = NativeBindingNodeAvailability.Read(catalog, recovered.Graph);
            Assert.IsEmpty(selected.AbsentFields);
            Assert.AreSequenceEqual(catalog.Types["Leaf"].Fields, selected.Catalog.Types["Leaf"].Fields);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Inherited and union casts keep their actual parent identities and reject a missing or displaced cast member.
    /// </summary>
    /// <param name="mutation">The incompatible parent or union member to reject before verifying recovery.</param>
    [TestMethod]
    [DataRow("parent-type")]
    [DataRow("parent-offset")]
    [DataRow("union-member")]
    public async Task SelectedNodeFieldsPreserveInheritedAndUnionCasts(string mutation)
    {
        const string Declarations = """
            pub enum NodeTag { T_Invalid = 0, T_Leaf = 7, }
            pub struct Node { pub type_: NodeTag, }
            pub struct Expr { pub type_: NodeTag, }
            pub struct Leaf {
                pub xpr: Expr,
                pub value: i32,
            }
            pub union ValUnion {
                pub node: Node,
                pub leaf: Leaf,
            }
            """;
        const string Headers = """
            #include <stdint.h>
            #include <stdio.h>
            #define PG_VERSION_NUM 180006
            typedef enum NodeTag { T_Invalid = 0, T_Leaf = 7 } NodeTag;
            typedef struct Node { NodeTag type; } Node;
            typedef struct Expr { NodeTag type; } Expr;
            typedef struct Leaf { Expr xpr; int32_t value; } Leaf;
            union ValUnion { Node node; Leaf leaf; };
            """;
        string changed = mutation switch
        {
            "parent-type" => Headers.Replace("Expr xpr;", "Node xpr;", StringComparison.Ordinal),
            "parent-offset" => Headers.Replace("Expr xpr;", "int32_t before; Expr xpr;", StringComparison.Ordinal),
            "union-member" => Headers.Replace("Leaf leaf;", "", StringComparison.Ordinal),
            _ => throw new ArgumentOutOfRangeException(nameof(mutation)),
        };
        string directory = Directory.CreateTempSubdirectory("ankus-selected-node-casts-").FullName;
        try
        {
            NativeBindingCatalog catalog = NativeBindingParser.Parse(Declarations, 18);
            NativeBindingNodeRoots roots = NativeBindingNodeRecords.CreateRoots(catalog, changed);
            NativeHeaderRecords invalid = await CollectCallRecordsAsync(roots.Source, [.. roots.Requests], directory);
            Assert.ThrowsExactly<FormatException>(() => NativeBindingNodeAvailability.Read(catalog, invalid.Graph));
            (NativeBindingSelectedNodes selected, NativeHeaderRecords records, NativeBindingLayout layout) =
                await CollectSelectedNodeFixtureAsync(directory, Declarations, Headers);
            Assert.IsEmpty(selected.AbsentFields);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(records, selected.Catalog, layout, []);
            const string Harness = """
                using Ankus;
                using Ankus.Postgres;
                public static class BindingAssertions
                {
                    public static unsafe long[] Run()
                    {
                        ValUnion value = default;
                        value.leaf.xpr.type = NodeTag.T_Leaf;
                        value.leaf.value = -731;
                        return [(uint)value.node.type, value.leaf.value, sizeof(ValUnion),
                            Accepts<Expr>(7) ? 1 : 0, Accepts<Expr>(8) ? 1 : 0,
                            Accepts<Leaf>(7) ? 1 : 0, Accepts<Leaf>(8) ? 1 : 0];
                    }
                    private static bool Accepts<T>(uint tag) where T : unmanaged, IPgNativeNode => T.AcceptsTag(tag);
                }
                """;
            Assert.AreSequenceEqual<long>([7, -731, 8, 1, 0, 1, 0], GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// A shared reference value cannot merge two distinct anonymous native record identities.
    /// </summary>
    [TestMethod]
    public async Task SelectedNodeFieldsRejectAmbiguousEmbeddedIdentities()
    {
        const string Declarations = """
            pub enum NodeTag { T_Invalid = 0, T_Leaf = 7, }
            pub struct Node { pub type_: NodeTag, }
            pub struct Cell { pub value: i32, }
            pub struct Leaf {
                pub type_: NodeTag,
                pub first: Cell,
                pub second: Cell,
            }
            """;
        const string Headers = """
            #include <stdint.h>
            typedef enum NodeTag { T_Invalid = 0, T_Leaf = 7 } NodeTag;
            typedef struct Node { NodeTag type; } Node;
            typedef struct Leaf {
                NodeTag type;
                struct { int32_t value; } first;
                struct { int32_t value; } second;
            } Leaf;
            """;
        string directory = Directory.CreateTempSubdirectory("ankus-selected-node-identity-").FullName;
        try
        {
            NativeBindingCatalog catalog = NativeBindingParser.Parse(Declarations, 18);
            NativeBindingNodeRoots roots = NativeBindingNodeRecords.CreateRoots(catalog, Headers);
            NativeHeaderRecords records = await CollectCallRecordsAsync(roots.Source, [.. roots.Requests], directory);
            FormatException error = Assert.ThrowsExactly<FormatException>(() => NativeBindingNodeAvailability.Read(catalog, records.Graph));
            Assert.AreEqual("One reference value names distinct native declaration identities.", error.Message);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Independent native verification still rejects a changed field that was never present in the reference inventory.
    /// </summary>
    [TestMethod]
    public async Task SelectedNodeChecksRejectChangesToAddedFields()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-selected-node-added-check-").FullName;
        try
        {
            string headers = "#define KEEP_REFERENCE_FIELDS 0\n" + AvailableNodeHeaders;
            (NativeBindingSelectedNodes selected, NativeHeaderRecords records, _) =
                await CollectSelectedNodeFixtureAsync(directory, AvailableNodeDeclarations, headers);
            Assert.DoesNotContain("added", selected.Catalog.Types["Nested"].Fields.Select(static field => field.NativeName));
            NativeBindingNodeRoots changed = NativeBindingNodeRecords.CreateRoots(selected.Catalog,
                headers.Replace("int32_t added;", "float added;", StringComparison.Ordinal));
            string source = Path.Join(directory, "changed.c");
            await File.WriteAllTextAsync(source, NativeBindingRecordChecks.Generate(records, changed.Source) +
                NativeBindingRecordChecks.ExecutableEntryPoint, context.CancellationToken);
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "changed.exe" : "changed");
            string errors = await CompileSelectedNodeFixtureAsync(source, executable, directory, expectSuccess: false);
            Assert.Contains("added", errors);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Compares available reference fields and new native storage with an independent C witness.
    /// </summary>
    private async Task VerifySelectedNodeFieldsAsync(bool keepReferenceFields)
    {
        string directory = Directory.CreateTempSubdirectory("ankus-selected-node-values-").FullName;
        try
        {
            string headers = "#define KEEP_REFERENCE_FIELDS " + (keepReferenceFields ? "1" : "0") + "\n" + AvailableNodeHeaders;
            (NativeBindingSelectedNodes selected, NativeHeaderRecords records, NativeBindingLayout layout) =
                await CollectSelectedNodeFixtureAsync(directory, AvailableNodeDeclarations, headers);
            Assert.AreSequenceEqual<NativeBindingAbsentNodeField>(keepReferenceFields ? [] :
                [new("Leaf", "obsolete"), new("Leaf", "old_child"), new("Nested", "removed")], selected.AbsentFields);
            Assert.HasCount(keepReferenceFields ? 6 : 4, selected.Catalog.Types["Leaf"].Fields);
            Assert.HasCount(keepReferenceFields ? 2 : 1, selected.Catalog.Types["Nested"].Fields);
            Assert.AreEqual(keepReferenceFields, layout.Types.ContainsKey("Obsolete"));
            const string Witness = """
                #include <stddef.h>
                #include <stdlib.h>
                int main(void)
                {
                    Leaf *value = calloc(1, sizeof(Leaf) + 4);
                    if (value == NULL)
                    {
                        return 2;
                    }

                    value->nested.active = -731;
                    value->nested.added = 19;
                    value->items[1].active = 83;
                    value->items[1].added = -7;
                    value->flags = 5;
                    value->tail[1] = 55000;
                    printf("%zu %zu %zu %zu %d %d %d %d %u %u\n", sizeof(Leaf), sizeof(Nested),
                        offsetof(Leaf, nested), offsetof(Leaf, tail), value->nested.active, value->nested.added,
                        value->items[1].active, value->items[1].added, value->flags, (unsigned int)value->tail[1]);
                    free(value);
                    return 0;
                }
                """;
            long[] expected = await RunRecordWitnessAsync(headers + "\n" + Witness, directory);
            Assert.AreSequenceEqual<long>([-731, 19, 83, -7, 5, 55000], expected.Skip(4));
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(records, selected.Catalog, layout, []);
            const string Harness = """
                using System;
                using Ankus.Postgres;
                public static class BindingAssertions
                {
                    public static unsafe long[] Run()
                    {
                        byte* bytes = stackalloc byte[sizeof(Leaf) + 4];
                        new Span<byte>(bytes, sizeof(Leaf) + 4).Clear();
                        Leaf* value = (Leaf*)bytes;
                        value->nested.active = -731;
                        value->nested.added = 19;
                        value->items[1].active = 83;
                        value->items[1].added = -7;
                        value->flags = 5;
                        Span<ushort> tail = Leaf.Dangerous_tail(value, 2);
                        tail[1] = 55000;
                        fixed (ushort* start = tail)
                        {
                            return [sizeof(Leaf), sizeof(Nested), (byte*)&value->nested - bytes, (byte*)start - bytes,
                                value->nested.active, value->nested.added, value->items[1].active, value->items[1].added,
                                value->flags, tail[1]];
                        }
                    }
                }
                """;
            Assert.AreSequenceEqual(expected, GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Discovers native fields before measuring selected node layouts and checking the entire native graph.
    /// </summary>
    private async Task<(NativeBindingSelectedNodes Selected, NativeHeaderRecords Records, NativeBindingLayout Layout)> CollectSelectedNodeFixtureAsync(
        string directory, string declarations, string headers)
    {
        NativeBindingCatalog catalog = NativeBindingParser.Parse(declarations, 18);
        NativeHeaderRecords tags = await CollectCallRecordsAsync(headers + "\nextern NodeTag ankus_node_tag_contract;",
            [new("ankus_node_tag_contract", "ankus_node_tag_contract", false)], directory);
        NativeBindingNodeRoots roots = NativeBindingNodeRecords.CreateRoots(NativeBindingNodeTags.Select(catalog, tags.Graph), headers);
        NativeHeaderRecords records = await CollectCallRecordsAsync(roots.Source, [.. roots.Requests], directory);
        NativeBindingSelectedNodes selected = NativeBindingNodeAvailability.Read(catalog, records.Graph);
        string source = Path.Join(directory, "selected-probe.c");
        string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "selected-probe.exe" : "selected-probe");
        await File.WriteAllTextAsync(source, NativeBindingProbe.GenerateSource(selected.Catalog, headers), context.CancellationToken);
        await CompileSelectedNodeFixtureAsync(source, executable, directory);
        NativeBindingLayout layout = NativeBindingProbe.Read(selected.Catalog, await RunAsync(executable, [], directory));
        string checks = Path.Join(directory, "selected-checks.c");
        string checker = Path.Join(directory, OperatingSystem.IsWindows() ? "selected-checks.exe" : "selected-checks");
        await File.WriteAllTextAsync(checks, NativeBindingRecordChecks.Generate(records, roots.Source) +
            NativeBindingRecordChecks.ExecutableEntryPoint, context.CancellationToken);
        await CompileSelectedNodeFixtureAsync(checks, checker, directory);
        Assert.AreEqual("", await RunAsync(checker, [], directory));
        return (selected, records, layout);
    }

    /// <summary>
    /// Compiles selected-header fixtures with the platform's independent C toolchain and warnings as errors.
    /// </summary>
    private Task<string> CompileSelectedNodeFixtureAsync(string source, string executable, string directory, bool expectSuccess = true)
    {
        string compiler = OperatingSystem.IsWindows() ? "clang-cl.exe" : "cc";
        string[] arguments = OperatingSystem.IsWindows()
            ? ["/nologo", "/std:c11", "/W4", "/WX", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), source]
            : ["-std=c11", "-Wall", "-Wextra", "-Werror", source, "-o", executable];
        return RunAsync(compiler, arguments, directory, expectSuccess);
    }
}
