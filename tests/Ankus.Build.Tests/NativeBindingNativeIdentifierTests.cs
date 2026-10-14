namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Native probes and generated values use actual C field names after decoding bindgen's escaped identifiers.
    /// </summary>
    [TestMethod]
    public async Task CompiledProbePreservesBindgenEscapedFieldNames()
    {
        const string Declarations = """
            pub enum NodeTag { T_Invalid = 0, T_Leaf = 7, }
            pub struct Node {
                pub type_: NodeTag,
            }
            pub struct Leaf {
                pub type_: NodeTag,
                pub proc_: i32,
                pub pure_: i32,
                pub u64_: i32,
                pub ordinary_: i32,
                pub proc__: i32,
            }
            """;
        const string Headers = """
            #include <stdint.h>
            #include <stdio.h>
            #define PG_VERSION_NUM 190006
            typedef enum NodeTag { T_Invalid = 0, T_Leaf = 7 } NodeTag;
            typedef struct Node { NodeTag type; } Node;
            typedef struct Leaf {
                NodeTag type;
                int32_t proc;
                int32_t pure;
                int32_t u64;
                int32_t ordinary_;
                int32_t proc__;
            } Leaf;
            """;
        string directory = Directory.CreateTempSubdirectory("ankus-native-identifiers-").FullName;
        try
        {
            NativeBindingCatalog catalog = NativeBindingParser.Parse(Declarations, 19);
            string source = Path.Join(directory, "probe.c");
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "probe.exe" : "probe");
            await File.WriteAllTextAsync(source, NativeBindingProbe.GenerateSource(catalog, Headers), context.CancellationToken);
            string compiler = OperatingSystem.IsWindows() ? "clang-cl.exe" : "cc";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), source]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", source, "-o", executable];
            await RunAsync(compiler, arguments, directory);
            NativeBindingLayout layout = NativeBindingProbe.Read(catalog, await RunAsync(executable, [], directory));
            Assert.AreEqual(24, layout.Types["Leaf"].Size);
            Assert.AreEqual(4, layout.Types["Leaf"].Alignment);
            Assert.HasCount(6, layout.Types["Leaf"].Fields);
            Assert.AreEqual(new NativeBindingFieldLayout(4, 4, 4, 4), layout.Types["Leaf"].Fields["proc_"]);
            Assert.AreEqual(new NativeBindingFieldLayout(8, 4, 4, 4), layout.Types["Leaf"].Fields["pure_"]);
            Assert.AreEqual(new NativeBindingFieldLayout(12, 4, 4, 4), layout.Types["Leaf"].Fields["u64_"]);
            Assert.AreEqual(new NativeBindingFieldLayout(16, 4, 4, 4), layout.Types["Leaf"].Fields["ordinary_"]);
            Assert.AreEqual(new NativeBindingFieldLayout(20, 4, 4, 4), layout.Types["Leaf"].Fields["proc__"]);
            NativeBindingNodeRoots roots = NativeBindingNodeRecords.CreateRoots(catalog, Headers);
            NativeHeaderRecords records = await CollectCallRecordsAsync(roots.Source, [.. roots.Requests], directory, major: 19);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(records, catalog, layout, []);
            const string Harness = """
                public static class BindingAssertions
                {
                    public static long[] Run()
                    {
                        Ankus.Postgres.Leaf value = new()
                        {
                            type = Ankus.Postgres.NodeTag.T_Leaf,
                            proc = -731,
                            pure = 19,
                            u64 = 83,
                            ordinary_ = -7,
                            proc__ = 101,
                        };
                        return [(long)value.type, value.proc, value.pure, value.u64, value.ordinary_, value.proc__];
                    }
                }
                """;
            Assert.AreSequenceEqual<long>([7, -731, 19, 83, -7, 101], GeneratedBindingCompilation.Run(binding, Harness, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
