namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    private const string ImportHeaders = MixedCallHeaders + """

        int native_call_count;
        int native_tracked(int value) { ++native_call_count; return value * 3; }
        """;

    private static NativeHeaderRequest[] CreateImportRequests() => [.. s_mixedCallRequests, new("native_tracked", "native_tracked", true)];

    /// <summary>
    /// Actual object imports select linkable bodies from the complete graph and pure getters retain native execution and values.
    /// </summary>
    [TestMethod]
    public async Task NativeCallImportsSelectReferencedBodies()
    {
        const string Consumer = """
            typedef __SIZE_TYPE__ Size;
            typedef struct { const void *data; Size size; } Argument;
            typedef int (*Body)(const Argument *, Size, void *, Size);
            extern Body ankus_native_body_native_tracked(void);
            extern Body ankus_native_body_native_first(void);
            Body ankus_native_body_absent_definition(void) { return (Body)0; }
            int dispatch(int value, int *result)
            {
                Argument argument = { &value, sizeof(value) };
                return ankus_native_body_native_tracked()(&argument, 1, result, sizeof(*result));
            }
            """;
        const string Main = """
            #include <stdio.h>
            extern int dispatch(int value, int *result);
            int main(void)
            {
                if (native_call_count != 0) return 1;
                AnkusNativeCallBody address = ankus_native_body_native_tracked();
                if (address == NULL || native_call_count != 0) return 2;
                int result = -1;
                if (dispatch(14, &result) != 0 || result != 42 || native_call_count != 1) return 3;
                if (dispatch(-11, &result) != 0 || result != -33 || native_call_count != 2) return 4;
                puts("referenced native getter executes exact body");
                return 0;
            }
            """;
        string directory = Directory.CreateTempSubdirectory("ankus-import-calls-").FullName;
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(ImportHeaders, CreateImportRequests(), directory);
            NativeBindingSource companion = NativeBindingRecordCSharp.Generate(records.Graph);
            byte[] image = await CompileNativeObjectAsync(Consumer);
            string source = NativeBindingCallImports.Generate(records, "#define PG_VERSION_NUM 180006\n" + ImportHeaders, image);
            Assert.AreEqual("referenced native getter executes exact body\n", await RunImportedBodiesAsync(source, Main, image, directory));
            byte[] empty = await CompileNativeObjectAsync("int no_imports(void) { return 42; }");
            string none = NativeBindingCallImports.Generate(records, "#define PG_VERSION_NUM 180006\n" + ImportHeaders, empty);
            Assert.AreEqual("", await RunImportedBodiesAsync(none, "int main(void) { return native_call_count; }", empty, directory));
            Assert.AreEqual(companion, NativeBindingRecordCSharp.Generate(records.Graph));
            Assert.HasCount(6, records.Graph.Roots);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Missing and unsupported imports, foreign targets and invalid unselected roots fail without changing the complete companion.
    /// </summary>
    [TestMethod]
    public async Task NativeCallImportsRejectForeignContracts()
    {
        string directory = Directory.CreateTempSubdirectory("ankus-import-contract-").FullName;
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(ImportHeaders, CreateImportRequests(), directory);
            NativeBindingSource companion = NativeBindingRecordCSharp.Generate(records.Graph);
            byte[] selected = await CompileNativeObjectAsync(Import("native_tracked"));
            string expected = NativeBindingCallImports.Generate(records, ImportHeaders, selected);
            foreach (string name in new[] { "missing", "native_global", "native_variadic", "native_unprototyped" })
            {
                byte[] invalid = await CompileNativeObjectAsync(Import(name));
                Assert.ThrowsExactly<FormatException>(() => NativeBindingCallImports.Generate(records, ImportHeaders, invalid), name);
            }

            NativeHeaderTarget target = records.Headers.Target;
            int separator = target.RuntimeIdentifier.LastIndexOf('-');
            string other = target.RuntimeIdentifier[..(separator + 1)] + (target.RuntimeIdentifier.EndsWith("-x64", StringComparison.Ordinal) ? "arm64" : "x64");
            NativeHeaderTarget foreign = target with
            {
                RuntimeIdentifier = other
            };
            NativeHeaderRecords incompatible = records with
            {
                Headers = records.Headers with
                {
                    Target = foreign
                },
                Graph = records.Graph with
                {
                    Target = foreign
                }
            };
            Assert.ThrowsExactly<FormatException>(() => NativeBindingCallImports.Generate(incompatible, ImportHeaders, selected));
            string processor = target.RuntimeIdentifier.EndsWith("-x64", StringComparison.Ordinal) ? "x86_64" : "aarch64";
            string foreignFormat = processor + (OperatingSystem.IsWindows() ? "-unknown-linux-gnu" : "-pc-windows-msvc");
            byte[] foreignImage = await CompileNativeObjectAsync(Import("native_tracked"), foreignFormat);
            Assert.ThrowsExactly<FormatException>(() => NativeBindingCallImports.Generate(records, ImportHeaders, foreignImage));
            NativeHeaderTarget reversed = target with
            {
                IsLittleEndian = !target.IsLittleEndian
            };
            NativeHeaderRecords wrongByteOrder = records with
            {
                Headers = records.Headers with
                {
                    Target = reversed
                },
                Graph = records.Graph with
                {
                    Target = reversed
                }
            };
            Assert.ThrowsExactly<FormatException>(() => NativeBindingCallImports.Generate(wrongByteOrder, ImportHeaders, selected));
            Dictionary<string, int> roots = records.Graph.Roots.ToDictionary();
            roots["native_global"] = records.Graph.Types.Count;
            NativeHeaderRecords invalidGraph = records with
            {
                Graph = records.Graph with
                {
                    Roots = roots
                }
            };
            Assert.ThrowsExactly<FormatException>(() => NativeBindingCallImports.Generate(invalidGraph, ImportHeaders, selected));
            byte[] empty = await CompileNativeObjectAsync("int no_imports(void) { return 42; }");
            Assert.ThrowsExactly<FormatException>(() => NativeBindingCallImports.Generate(invalidGraph, ImportHeaders, empty));
            Assert.AreEqual(expected, NativeBindingCallImports.Generate(records, ImportHeaders, selected));
            Assert.AreEqual(companion, NativeBindingRecordCSharp.Generate(records.Graph));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }

        static string Import(string name) => $"extern void *ankus_native_body_{name}(void); void *entry(void) {{ return ankus_native_body_{name}(); }}";
    }

    /// <summary>
    /// MSVC's forced large-object output supplies the same exact import through its wider symbol records.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    public async Task CompiledBigCoffObjectsRetainNativeImports()
    {
        byte[] image = await CompileNativeObjectAsync("extern int ankus_imported(int value); int entry(int value) { return ankus_imported(value); }", bigCoff: true);
        Assert.AreEqual((byte)0, image[0]);
        Assert.AreEqual((byte)0xff, image[2]);
        NativeObjectImports imports = NativeObjectSymbols.Read(image, "ankus_");
        Assert.AreEqual("coff", imports.Format);
        Assert.AreSequenceEqual<string>(["ankus_imported"], imports.Symbols);
    }

    private async Task<string> RunImportedBodiesAsync(string source, string main, byte[] image, string directory, bool nativeCompiler = true)
    {
        string stem = Path.Join(directory, "selected-" + Guid.NewGuid().ToString("N"));
        string file = stem + ".c";
        string consumer = stem + ".consumer.obj";
        string executable = stem + (OperatingSystem.IsWindows() ? ".exe" : string.Empty);
        await File.WriteAllTextAsync(file, source + "\n" + main, context.CancellationToken);
        await File.WriteAllBytesAsync(consumer, image, context.CancellationToken);
        string compiler = OperatingSystem.IsWindows() && nativeCompiler ? "cl.exe" : await HostCompilers.ClangAsync();
        string[] arguments = OperatingSystem.IsWindows()
            ? ["/nologo", "/std:c11", "/W4", "/WX", "/O2", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file, "/link", consumer]
            : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-O2", file, consumer, "-o", executable];
        await RunAsync(compiler, arguments, directory);
        return (await RunAsync(executable, [], directory)).ReplaceLineEndings("\n");
    }
}
