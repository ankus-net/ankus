using System.Runtime.InteropServices;
using System.Text.Json;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Real Clang types retain native widths, over-alignment, array decay and enum signedness without backend exports.
    /// </summary>
    [TestMethod]
    public async Task CollectedHeaderStorageMatchesIndependentNativeTypes()
    {
        const string Headers = """
            #define PG_VERSION_NUM 180006
            typedef unsigned long long NativeWidth;
            typedef int AlignedInt __attribute__((aligned(16)));
            typedef int AlignedArray[3] __attribute__((aligned(16)));
            typedef enum __attribute__((packed)) { TinyNegative = -1, TinyPositive = 2 } Tiny;
            typedef struct { int first; int second; } Pair;
            struct Hidden;
            struct Later;
            extern struct Later *later;
            struct Later { int value; };
            typedef int (*Callback)(long value);
            extern NativeWidth call(int number, _Bool flag, double real, AlignedInt aligned, Callback callback,
                const int items[static 3], Tiny mode, Pair pair);
            extern void done(void);
            extern void report(const char *format, ...);
            extern Callback hook;
            extern AlignedArray aligned_rows;
            extern int open_rows[];
            extern int empty_rows[0];
            extern struct Hidden hidden;
            extern struct Hidden unknown_result(void);
            """;
        NativeHeaderRequest[] requests =
        [
            new("call", "call", true), new("done", "done", true), new("report", "report", true),
            new("hook", "hook", false), new("aligned_rows", "aligned_rows", false),
            new("open_rows", "open_rows", false), new("empty_rows", "empty_rows", false),
            new("hidden", "hidden", false),
            new("unknown_result", "unknown_result", true), new("later", "later", false),
        ];
        string directory = Path.Join(Path.GetTempPath(), $"ankus-storage-native-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Join(directory, "storage.c");
            string compiler = OperatingSystem.IsWindows() ? "clang-cl.exe" : "clang";
            string[] frontend = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Zs", "-Xclang", "-ast-dump=json", file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-fsyntax-only", "-Xclang", "-ast-dump=json", file];
            await File.WriteAllTextAsync(file, NativeBindingHeaderParser.GenerateSource(
                NativeBindingHeaderTarget.GenerateSource(Headers, 18), requests), context.CancellationToken);
            string ast = Path.Join(directory, "storage.ast.json");
            await NativeBindingHeaderCommand.CompileAsync(compiler, frontend, ast, directory, context.CancellationToken);
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(ast, context.CancellationToken));
            NativeHeaderTarget target = NativeBindingHeaderTarget.Read(document.RootElement, 18);
            var catalog = new NativeHeaderCatalog(target, NativeBindingHeaderParser.Read(document.RootElement, requests));
            Assert.AreEqual(new NativeHeaderRecord("Hidden", false, false), catalog.Symbols["hidden"].Type);
            Assert.AreEqual(new NativeHeaderRecord("Later", false, true),
                Assert.IsInstanceOfType<NativeHeaderPointer>(catalog.Symbols["later"].Type).Element);
            string code = NativeBindingStorageProbe.GenerateSource(catalog, Headers);
            await File.WriteAllTextAsync(file, code, context.CancellationToken);
            await NativeBindingHeaderCommand.CompileAsync(compiler, frontend, ast, directory, context.CancellationToken);
            using JsonDocument measured = JsonDocument.Parse(await File.ReadAllTextAsync(ast, context.CancellationToken));
            string output = NativeBindingStorageProbe.ReadObservations(catalog, measured.RootElement);
            NativeHeaderStorage storage = NativeBindingStorageProbe.Read(catalog, output);
            Assert.AreEqual(RuntimeInformation.RuntimeIdentifier, storage.Headers.Target.RuntimeIdentifier);
            // The Microsoft ABI keeps this enum int-sized even with the packed attribute.
            ulong enumSize = OperatingSystem.IsWindows() ? 4UL : 1UL;
            Assert.AreSequenceEqual<NativeHeaderValueStorage>(
            [
                new(4, 4, null, true), new(1, 1, null, false), new(8, 8, null, null),
                new(4, 16, null, true), new((ulong)IntPtr.Size, (ulong)IntPtr.Size, null, null),
                new((ulong)IntPtr.Size, (ulong)IntPtr.Size, null, null), new(enumSize, enumSize, null, true),
                new(8, 4, null, null),
            ], storage.Symbols["call"].Parameters);
            Assert.AreEqual(new NativeHeaderValueStorage(8, 8, null, false), storage.Symbols["call"].Result);
            Assert.IsEmpty(storage.Symbols["done"].Parameters);
            Assert.IsNull(storage.Symbols["done"].Result);
            Assert.AreEqual(new NativeHeaderValueStorage((ulong)IntPtr.Size, (ulong)IntPtr.Size, null, null),
                Assert.ContainsSingle(storage.Symbols["report"].Parameters));
            Assert.AreEqual(new NativeHeaderValueStorage((ulong)IntPtr.Size, (ulong)IntPtr.Size, null, null), storage.Symbols["hook"].Global);
            Assert.AreEqual(new NativeHeaderValueStorage(12, 16, 4, null), storage.Symbols["aligned_rows"].Global);
            Assert.AreEqual(new NativeHeaderValueStorage(null, 4, 4, null), storage.Symbols["open_rows"].Global);
            Assert.AreEqual(new NativeHeaderValueStorage(0, 4, 4, null), storage.Symbols["empty_rows"].Global);
            Assert.AreEqual(new NativeHeaderValueStorage(null, null, null, null), storage.Symbols["hidden"].Global);
            Assert.AreEqual(new NativeHeaderValueStorage(null, null, null, null), storage.Symbols["unknown_result"].Result);
            Assert.IsEmpty(storage.Symbols["unknown_result"].Parameters);
            Assert.AreEqual(new NativeHeaderValueStorage((ulong)IntPtr.Size, (ulong)IntPtr.Size, null, null), storage.Symbols["later"].Global);
            Assert.IsNull(storage.Symbols["call"].Global);
            string changed = code.Replace("extern NativeWidth call(int number", "extern NativeWidth call(short number", StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, changed, context.CancellationToken);
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingHeaderCommand.CompileAsync(compiler, frontend, ast, directory, context.CancellationToken));
            Assert.Contains("incompatible reconstructed native type: call", failure.Message);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
