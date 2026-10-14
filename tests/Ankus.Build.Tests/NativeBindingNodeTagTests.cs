using System.Text.Json;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Selected headers remove unavailable nodes and replace numeric tags throughout emitted enum, casts and allocation metadata.
    /// </summary>
    [TestMethod]
    public async Task SelectedNodeTagsFollowNativeDeclarations()
    {
        const string Declarations = """
            pub enum NodeTag { T_Invalid = 0, T_Parent = 4, T_Leaf = 7, T_Removed = 20, }
            pub struct Node { pub type_: NodeTag, }
            pub struct Parent {
                pub type_: NodeTag,
                pub data: i32,
            }
            pub struct Leaf {
                pub parent: Parent,
                pub value: i32,
            }
            pub struct Removed { pub type_: NodeTag, }
            """;
        const string Headers = """
            #include <stdint.h>
            #include <stdio.h>
            #define PG_VERSION_NUM 180006
            typedef enum NodeTag { T_Invalid = 0, T_Parent = 41, T_Leaf = 99, T_Added = 123 } NodeTag;
            typedef struct Node { NodeTag type; } Node;
            typedef struct Parent { NodeTag type; int32_t data; } Parent;
            typedef struct Leaf { Parent parent; int32_t value; } Leaf;
            """;
        string directory = Directory.CreateTempSubdirectory("ankus-selected-node-tags-").FullName;
        try
        {
            (NativeBindingSelectedNodes selected, NativeHeaderRecords records, NativeBindingLayout layout) =
                await CollectSelectedNodeFixtureAsync(directory, Declarations, Headers);
            Assert.AreSequenceEqual<string>(["Removed"], selected.Declarations.AbsentTypes);
            Assert.AreSequenceEqual<string>(["T_Removed"], selected.Declarations.AbsentTags);
            Assert.AreSequenceEqual<string>(["T_Added"], selected.Declarations.AdditionalTags);
            Assert.AreSequenceEqual<NativeBindingChangedNodeTag>([new("T_Leaf", 7, 99), new("T_Parent", 4, 41)], selected.Declarations.ChangedTags);
            Assert.IsEmpty(selected.AbsentFields);
            Assert.IsFalse(selected.Catalog.Types.ContainsKey("Removed"));
            using JsonDocument ast = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(directory, "records.ast.json"), context.CancellationToken));
            IReadOnlyDictionary<string, uint> observed = NativeBindingNodeTags.Read(ast.RootElement);
            Assert.AreSequenceEqual(selected.Catalog.Tags.OrderBy(static pair => pair.Key, StringComparer.Ordinal),
                observed.OrderBy(static pair => pair.Key, StringComparer.Ordinal));
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(records, selected.Catalog, layout, []);
            const string Harness = """
                using System;
                using Ankus;
                using Ankus.Postgres;
                public static class BindingAssertions
                {
                    public static unsafe long[] Run()
                    {
                        Leaf value = new() { parent = new() { type = NodeTag.T_Leaf, data = -31 }, value = 81 };
                        if (!Accepts<Parent>(41) || !Accepts<Parent>(99) || Accepts<Parent>(4) || Accepts<Parent>(7) ||
                            !Accepts<Leaf>(99) || Accepts<Leaf>(41) || Accepts<Leaf>(7) || Accepts<Leaf>(123))
                        {
                            throw new InvalidOperationException("Node casts used reference discriminator values.");
                        }

                        if (NativeBinding.NodeLayouts != "41:8:4;99:12:4")
                        {
                            throw new InvalidOperationException("Node allocations used reference discriminator values: " + NativeBinding.NodeLayouts);
                        }

                        return [sizeof(Node), sizeof(Parent), sizeof(Leaf), (uint)value.parent.type, value.parent.data, value.value, (uint)NodeTag.T_Added];
                    }

                    private static bool Accepts<T>(uint tag) where T : unmanaged, IPgNativeNode => T.AcceptsTag(tag);
                }
                """;
            Assert.AreSequenceEqual<long>([4, 8, 12, 99, -31, 81, 123], GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Compiler-evaluated explicit values, implicit increments and the unsigned boundary remain exact.
    /// </summary>
    [TestMethod]
    public void NativeNodeTagReaderPreservesEvaluatedAndImplicitValues()
    {
        using JsonDocument document = JsonDocument.Parse("""
            {"kind":"TranslationUnitDecl","inner":[{"kind":"EnumDecl","name":"NodeTag","inner":[
                {"kind":"EnumConstantDecl","name":"T_Invalid"},
                {"kind":"EnumConstantDecl","name":"T_First"},
                {"kind":"EnumConstantDecl","name":"T_Expression","inner":[{"kind":"ConstantExpr","value":"41"}]},
                {"kind":"EnumConstantDecl","name":"T_Next"},
                {"kind":"EnumConstantDecl","name":"T_Max","inner":[{"kind":"ImplicitCastExpr","inner":[{"kind":"ConstantExpr","value":"4294967295"}]}]}
            ]}]}
            """);
        IReadOnlyDictionary<string, uint> tags = NativeBindingNodeTags.Read(document.RootElement);
        Assert.HasCount(5, tags);
        Assert.AreEqual(0U, tags["T_Invalid"]);
        Assert.AreEqual(1U, tags["T_First"]);
        Assert.AreEqual(41U, tags["T_Expression"]);
        Assert.AreEqual(42U, tags["T_Next"]);
        Assert.AreEqual(uint.MaxValue, tags["T_Max"]);
    }

    /// <summary>
    /// Node fields use exact native enum members even when prerelease headers remove, add or renumber them.
    /// </summary>
    [TestMethod]
    public async Task SelectedNodeEnumsFollowNativeDeclarations()
    {
        const string Declarations = """
            pub enum NodeTag { T_Invalid = 0, T_Leaf = 7, }
            pub mod Mode {
                pub type Type = ::core::ffi::c_uint;
                pub const MODE_ACTIVE: Type = 4;
                pub const MODE_REMOVED: Type = 7;
            }
            pub struct Node { pub type_: NodeTag, }
            pub struct Leaf {
                pub type_: NodeTag,
                pub mode: Mode::Type,
            }
            """;
        const string Headers = """
            #include <stdio.h>
            #define PG_VERSION_NUM 180006
            typedef enum NodeTag { T_Invalid = 0, T_Leaf = 7 } NodeTag;
            typedef enum Mode { MODE_ACTIVE = -3, MODE_ADDED = 91 } Mode;
            typedef struct Node { NodeTag type; } Node;
            typedef struct Leaf { NodeTag type; Mode mode; } Leaf;
            """;
        string directory = Directory.CreateTempSubdirectory("ankus-selected-node-enums-").FullName;
        try
        {
            (NativeBindingSelectedNodes selected, NativeHeaderRecords records, NativeBindingLayout layout) =
                await CollectSelectedNodeFixtureAsync(directory, Declarations, Headers);
            NativeBindingEnumChanges changed = Assert.ContainsSingle(selected.Declarations.Enums);
            Assert.AreEqual("Mode", changed.Name);
            Assert.AreSequenceEqual<string>(["MODE_REMOVED"], changed.AbsentValues);
            Assert.AreSequenceEqual<string>(["MODE_ADDED"], changed.AdditionalValues);
            Assert.AreSequenceEqual<NativeBindingChangedEnumValue>([new("MODE_ACTIVE", "4", "-3")], changed.ChangedValues);
            Assert.AreEqual(new NativeBindingEnumLayout(4, true), layout.Enums["Mode"]);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(records, selected.Catalog, layout, []);
            const string Harness = """
                using System;
                using Ankus.Postgres;
                public static class BindingAssertions
                {
                    public static unsafe long[] Run()
                    {
                        if (Enum.IsDefined(typeof(Mode), "MODE_REMOVED") || typeof(Mode).GetEnumUnderlyingType() != typeof(int))
                        {
                            throw new InvalidOperationException("Enum retained stale reference members or unsigned storage.");
                        }

                        Leaf value = new() { type = NodeTag.T_Leaf, mode = Mode.MODE_ACTIVE };
                        return [sizeof(Leaf), (int)value.mode, (int)Mode.MODE_ADDED];
                    }
                }
                """;
            Assert.AreSequenceEqual<long>([8, -3, 91], GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Incomplete, ambiguous or lossy discriminator observations fail instead of producing unsafe node casts.
    /// </summary>
    /// <param name="members">Invalid compiler enum members.</param>
    [TestMethod]
    [DataRow("[]")]
    [DataRow("[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Other\"}]")]
    [DataRow("[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\",\"inner\":[{\"kind\":\"ConstantExpr\",\"value\":\"1\"}]}]")]
    [DataRow("[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\"},{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\"}]")]
    [DataRow("[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\"},{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Same\",\"inner\":[{\"kind\":\"ConstantExpr\",\"value\":\"0\"}]}]")]
    [DataRow("[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\"},{\"kind\":\"EnumConstantDecl\",\"name\":\"Other\"}]")]
    [DataRow("[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\"},{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Negative\",\"inner\":[{\"kind\":\"ConstantExpr\",\"value\":\"-1\"}]}]")]
    [DataRow("[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\"},{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Large\",\"inner\":[{\"kind\":\"ConstantExpr\",\"value\":\"4294967296\"}]}]")]
    [DataRow("[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\"},{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Max\",\"inner\":[{\"kind\":\"ConstantExpr\",\"value\":\"4294967295\"}]},{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Overflow\"}]")]
    [DataRow("[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\",\"inner\":[{\"kind\":\"ConstantExpr\"}]}]")]
    [DataRow("[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\",\"inner\":[{\"kind\":\"ConstantExpr\",\"value\":\"garbage\"}]}]")]
    [DataRow("[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\",\"inner\":[{\"kind\":\"IntegerLiteral\",\"value\":\"0\"}]}]")]
    [DataRow("[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\",\"inner\":{}}]")]
    public void NativeNodeTagReaderRejectsInvalidMembers(string members)
    {
        using JsonDocument document = JsonDocument.Parse("{\"kind\":\"TranslationUnitDecl\",\"inner\":[{\"kind\":\"EnumDecl\",\"name\":\"NodeTag\",\"inner\":" + members + "}]}");
        Assert.ThrowsExactly<FormatException>(() => NativeBindingNodeTags.Read(document.RootElement));
    }

    /// <summary>
    /// Missing translation units and duplicate complete discriminator enums cannot serve as compiler evidence.
    /// </summary>
    /// <param name="source">Invalid compiler translation-unit JSON.</param>
    [TestMethod]
    [DataRow("{}")]
    [DataRow("{\"kind\":\"TranslationUnitDecl\",\"inner\":{}}")]
    [DataRow("{\"kind\":\"TranslationUnitDecl\",\"inner\":[]}")]
    [DataRow("{\"kind\":\"TranslationUnitDecl\",\"inner\":[{\"kind\":\"EnumDecl\",\"name\":\"NodeTag\",\"inner\":[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\"}]},{\"kind\":\"EnumDecl\",\"name\":\"NodeTag\",\"inner\":[{\"kind\":\"EnumConstantDecl\",\"name\":\"T_Invalid\"}]}]}")]
    public void NativeNodeTagReaderRejectsIncompleteTranslationUnits(string source)
    {
        using JsonDocument document = JsonDocument.Parse(source);
        Assert.ThrowsExactly<FormatException>(() => NativeBindingNodeTags.Read(document.RootElement));
    }
}
