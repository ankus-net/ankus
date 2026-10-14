using System.Globalization;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Compiled native declarations preserve pgrx's root and inherited views, alias tags and shared payload bytes.
    /// </summary>
    [TestMethod]
    public async Task CompiledNodesPreserveCastInheritanceAndAliases()
    {
        const string Declarations = """
            pub enum NodeTag {
                T_Invalid = 0, T_RangeTblRef = 7, T_AlternativeSubPlan = 11,
                T_Var = 19, T_RangeAlias = 29,
            }
            pub struct Node { pub type_: NodeTag, }
            pub struct Expr { pub type_: NodeTag, }
            pub struct RangeTblRef {
                pub type_: NodeTag,
                pub rtindex: i32,
            }
            pub type RangeAlias = RangeTblRef;
            pub struct AlternativeSubPlan {
                pub xpr: Expr,
                pub subplans: *mut Node,
            }
            pub struct Var {
                pub xpr: Expr,
                pub varno: i32,
            }
            """;
        const string Headers = """
            #include <stdint.h>
            #include <stdio.h>
            #define PG_VERSION_NUM 180006
            typedef enum NodeTag {
                T_Invalid = 0, T_RangeTblRef = 7, T_AlternativeSubPlan = 11,
                T_Var = 19, T_RangeAlias = 29
            } NodeTag;
            typedef struct Node { NodeTag type; } Node;
            typedef struct Expr { NodeTag type; } Expr;
            typedef struct RangeTblRef { NodeTag type; int32_t rtindex; } RangeTblRef;
            typedef RangeTblRef RangeAlias;
            typedef struct AlternativeSubPlan { Expr xpr; Node *subplans; } AlternativeSubPlan;
            typedef struct Var { Expr xpr; int32_t varno; } Var;
            """;
        NativeBindingSource binding = await CompileCastBindingAsync(Declarations, Headers, 18);
        const string Harness = """
            using Ankus;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    RangeTblRef range = new() { type = NodeTag.T_RangeTblRef, rtindex = 9 };
                    Node* root = (Node*)&range;
                    RangeTblRef* roundtrip = (RangeTblRef*)root;
                    int original = roundtrip->rtindex;
                    roundtrip->rtindex = 21;
                    long[] rangeValues = [Accepts<Node>(range.type), Accepts<RangeTblRef>(root->type),
                        (uint)root->type, original, range.rtindex, (nint)roundtrip == (nint)(&range) ? 1 : 0,
                        Accepts<Expr>(root->type), Accepts<Var>(root->type)];

                    AlternativeSubPlan plan = new() { xpr = new() { type = NodeTag.T_AlternativeSubPlan }, subplans = root };
                    Node* planRoot = (Node*)&plan;
                    Expr* parent = (Expr*)planRoot;
                    AlternativeSubPlan* child = (AlternativeSubPlan*)parent;
                    ((RangeTblRef*)child->subplans)->rtindex = 42;
                    long[] inherited = [Accepts<Node>(planRoot->type), Accepts<Expr>(planRoot->type),
                        Accepts<AlternativeSubPlan>(parent->type), Accepts<Var>(planRoot->type),
                        Accepts<RangeTblRef>(parent->type), (uint)parent->type,
                        child->subplans == root ? 1 : 0, range.rtindex,
                        (nint)parent == (nint)(&plan) ? 1 : 0, (nint)child == (nint)(&plan) ? 1 : 0];

                    range.type = NodeTag.T_RangeAlias;
                    long[] aliases = [Accepts<Node>(root->type), Accepts<RangeTblRef>(root->type),
                        Accepts<Expr>(root->type), Accepts<Var>(root->type),
                        (uint)roundtrip->type, roundtrip->rtindex,
                        Accepts<RangeTblRef>(NodeTag.T_Invalid), Accepts<AlternativeSubPlan>((NodeTag)uint.MaxValue)];
                    return [.. rangeValues, .. inherited, .. aliases];
                }

                private static int Accepts<T>(NodeTag tag) where T : unmanaged, IPgNativeNode => T.AcceptsTag((uint)tag) ? 1 : 0;
            }
            """;
        long[] observed = GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken);
        Assert.AreSequenceEqual<long>(
            [1, 1, 7, 9, 21, 1, 0, 0, 1, 1, 1, 0, 0, 11, 1, 42, 1, 1, 1, 1, 0, 0, 29, 42, 0, 0], observed);
    }

    /// <summary>
    /// Legacy Value accepts its five constructor tags while later tagged union members preserve one-way casts and pointer identity.
    /// </summary>
    /// <param name="major">The PostgreSQL declaration generation whose Value rules apply.</param>
    [TestMethod]
    [DataRow(13)]
    [DataRow(14)]
    [DataRow(15)]
    [DataRow(16)]
    [DataRow(17)]
    [DataRow(18)]
    [DataRow(19)]
    public async Task CompiledValueTagsPreserveVersionedUnionRules(int major)
    {
        const string Tags = """
            pub enum NodeTag { T_Invalid = 0, T_Integer = 1, T_Float = 2, T_String = 3, T_BitString = 4, T_Null = 5, }
            pub struct Node { pub type_: NodeTag, }
            """;
        const string LegacyDeclarations = """
            pub struct Value {
                pub type_: NodeTag,
                pub val: ValUnion,
            }
            pub union ValUnion {
                pub ival: i32,
                pub str_: *mut ::core::ffi::c_char,
            }
            """;
        const string ModernDeclarations = """
            pub struct Integer {
                pub type_: NodeTag,
                pub ival: i32,
            }
            pub struct String {
                pub type_: NodeTag,
                pub sval: *mut ::core::ffi::c_char,
            }
            pub union ValUnion {
                pub node: Node,
                pub ival: Integer,
                pub sval: String,
            }
            """;
        const string CommonHeaders = """
            #include <stdint.h>
            #include <stdio.h>
            typedef enum NodeTag { T_Invalid = 0, T_Integer = 1, T_Float = 2, T_String = 3, T_BitString = 4, T_Null = 5 } NodeTag;
            typedef struct Node { NodeTag type; } Node;
            """;
        const string LegacyHeaders = """
            typedef struct Value { NodeTag type; union { int32_t ival; char *str; } val; } Value;
            """;
        const string ModernHeaders = """
            typedef struct Integer { NodeTag type; int32_t ival; } Integer;
            typedef struct String { NodeTag type; char *sval; } String;
            union ValUnion { Node node; Integer ival; String sval; };
            """;
        bool legacy = major <= 14;
        string headers = "#define PG_VERSION_NUM " + (major * 10000).ToString(CultureInfo.InvariantCulture) + "\n" +
            CommonHeaders + "\n" + (legacy ? LegacyHeaders : ModernHeaders);
        NativeBindingSource binding = await CompileCastBindingAsync(Tags + "\n" +
            (legacy ? LegacyDeclarations : ModernDeclarations), headers, major);
        const string LegacyHarness = """
            using Ankus;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    Value value = new() { type = NodeTag.T_Integer, val = new() { ival = 42 } };
                    Node* node = (Node*)&value;
                    Value* roundtrip = (Value*)node;
                    int original = roundtrip->val.ival;
                    roundtrip->val.ival = -91;
                    long[] integers = [Accepts<Node>(node->type), Accepts<Value>(node->type),
                        (uint)node->type, original, value.val.ival];
                    byte* text = stackalloc byte[] { 115, 111, 109, 101, 116, 104, 105, 110, 103, 0 };
                    NodeTag[] tags = [NodeTag.T_Float, NodeTag.T_String, NodeTag.T_BitString, NodeTag.T_Null];
                    long[] values = new long[tags.Length * 4];
                    for (int i = 0; i < tags.Length; i++)
                    {
                        value.type = tags[i];
                        value.val.@str = (sbyte*)text;
                        values[i * 4] = Accepts<Node>(node->type);
                        values[i * 4 + 1] = Accepts<Value>(node->type);
                        values[i * 4 + 2] = (uint)roundtrip->type;
                        values[i * 4 + 3] = roundtrip->val.@str == (sbyte*)text ? 1 : 0;
                    }
                    return [.. integers, .. values, Accepts<Value>(NodeTag.T_Invalid), Accepts<Value>((NodeTag)uint.MaxValue)];
                }

                private static int Accepts<T>(NodeTag tag) where T : unmanaged, IPgNativeNode => T.AcceptsTag((uint)tag) ? 1 : 0;
            }
            """;
        const string ModernHarness = """
            using Ankus;
            using Ankus.Postgres;
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    byte* text = stackalloc byte[] { 115, 111, 109, 101, 116, 104, 105, 110, 103, 0 };
                    ValUnion value = new() { sval = new() { type = NodeTag.T_String, sval = (sbyte*)text } };
                    Node* node = (Node*)&value;
                    Ankus.Postgres.String* member = (Ankus.Postgres.String*)node;
                    long[] strings = [Accepts<Node>(node->type), Accepts<Ankus.Postgres.String>(node->type),
                        Accepts<Integer>(node->type), Accepts<ValUnion>(node->type), (uint)member->type,
                        member->sval == (sbyte*)text ? 1 : 0, ((byte*)member->sval)[8]];
                    member->sval = (sbyte*)(text + 1);
                    long mutated = value.sval.sval == (sbyte*)(text + 1) ? 1 : 0;
                    value.ival = new() { type = NodeTag.T_Integer, ival = 42 };
                    Integer* integer = (Integer*)node;
                    int original = integer->ival;
                    integer->ival = -91;
                    return [.. strings, mutated, Accepts<Integer>(node->type), Accepts<Ankus.Postgres.String>(node->type),
                        Accepts<ValUnion>(node->type), (uint)integer->type, original, value.ival.ival,
                        Accepts<ValUnion>(NodeTag.T_Invalid), Accepts<ValUnion>((NodeTag)uint.MaxValue)];
                }

                private static int Accepts<T>(NodeTag tag) where T : unmanaged, IPgNativeNode => T.AcceptsTag((uint)tag) ? 1 : 0;
            }
            """;
        long[] observed = GeneratedBindingCompilation.Run(binding, legacy ? LegacyHarness : ModernHarness, context.CancellationToken);
        Assert.AreSequenceEqual<long>(legacy
            ? [1, 1, 1, 42, -91, 1, 1, 2, 1, 1, 1, 3, 1, 1, 1, 4, 1, 1, 1, 5, 1, 0, 0]
            : [1, 1, 0, 0, 3, 1, 103, 1, 1, 0, 0, 1, 42, -91, 0, 0], observed);
    }

    private async Task<NativeBindingSource> CompileCastBindingAsync(string declarations, string headers, int major)
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-native-casts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeBindingCatalog catalog = NativeBindingParser.Parse(declarations, major);
            string source = Path.Join(directory, "probe.c");
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "probe.exe" : "probe");
            await File.WriteAllTextAsync(source, NativeBindingProbe.GenerateSource(catalog, headers), context.CancellationToken);
            string compiler = OperatingSystem.IsWindows() ? "clang-cl.exe" : "cc";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), source]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", source, "-o", executable];
            await RunAsync(compiler, arguments, directory);
            string observations = await RunAsync(executable, [], directory);
            NativeBindingLayout layout = NativeBindingProbe.Read(catalog, observations);
            NativeBindingNodeRoots roots = NativeBindingNodeRecords.CreateRoots(catalog, headers);
            NativeRecordGraph graph = await CollectMeasuredRecordsAsync(roots.Source, roots.Requests, directory, major);
            return NativeBindingRecordCSharp.Generate(graph, catalog, layout);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
