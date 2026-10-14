using System.Text;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Header-only prototype checks require no server exports and reject incompatible headers before any call exists.
    /// </summary>
    [TestMethod]
    public async Task CompiledSignatureProbeChecksHeadersAndMeasuresValues()
    {
        NativeBindingCatalog catalog = NativeBindingParser.Parse("""
            pub enum NodeTag { T_Invalid = 0, }
            pub type NativeWidth = ::core::ffi::c_ulong;
            pub struct Pair { pub first: u64, pub second: u32, }
            """, 18);
        NativeBindingRawCatalog raw = NativeBindingRawParser.Parse("""
            extern "C" {
                #[link_name = "measure__pgrx_cshim"]
                pub fn measure(value: NativeWidth, handler: ::core::option::Option<unsafe extern "C" fn(value: u32)>) -> Pair;
                pub fn done();
            }
            """, 18);
        const string Headers = """
            #include <stdint.h>
            #define PG_VERSION_NUM 180006
            typedef uint64_t NativeWidth;
            struct Pair { uint64_t first; uint32_t second; };
            extern struct Pair measure(NativeWidth value, void (*handler)(uint32_t));
            extern void done(void);
            """;
        string directory = Path.Join(Path.GetTempPath(), $"ankus-signature-probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Join(directory, "probe.c");
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "probe.exe" : "probe");
            string compiler = OperatingSystem.IsWindows() ? "clang-cl.exe" : "cc";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/O2", "/W4", "/WX", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
                : ["-std=c11", "-O2", "-Wall", "-Wextra", "-Werror", file, "-o", executable];
            string source = NativeBindingSignatureProbe.GenerateSource(catalog, raw, ["measure", "done"], Headers);
            await File.WriteAllTextAsync(file, source, context.CancellationToken);
            await RunAsync(compiler, arguments, directory);
            NativeBindingSignatures measured = NativeBindingSignatureProbe.Read(catalog, raw, ["measure", "done"],
                await RunAsync(executable, [], directory));
            Assert.AreEqual(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, measured.RuntimeIdentifier);
            Assert.AreEqual(IntPtr.Size, measured.PointerSize);
            Assert.AreSequenceEqual<NativeBindingSignatureValue>([new(8, 8), new(IntPtr.Size, IntPtr.Size)], measured.Functions["measure"].Parameters);
            Assert.AreEqual(new NativeBindingSignatureValue(16, 8), measured.Functions["measure"].Result);
            Assert.IsEmpty(measured.Functions["done"].Parameters);
            Assert.IsNull(measured.Functions["done"].Result);

            string incompatible = Headers.Replace("extern struct Pair measure(NativeWidth value", "extern struct Pair measure(uint32_t value", StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, NativeBindingSignatureProbe.GenerateSource(catalog, raw, ["measure"], incompatible), context.CancellationToken);
            Assert.Contains("measure", await RunAsync(compiler, arguments, directory, expectSuccess: false));
            string wrongResult = Headers.Replace("extern void done(void)", "extern int done(void)", StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, NativeBindingSignatureProbe.GenerateSource(catalog, raw, ["done"], wrongResult), context.CancellationToken);
            Assert.Contains("done", await RunAsync(compiler, arguments, directory, expectSuccess: false));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// A native compiler independently checks nested prototypes, then executes them with exact values and callbacks.
    /// </summary>
    [TestMethod]
    public async Task CompiledDeclarationsPreserveNativeCallSemantics()
    {
        NativeBindingCatalog catalog = NativeBindingParser.Parse("""
            pub enum NodeTag { T_Invalid = 0, }
            pub struct Node { pub type_: NodeTag, }
            pub type NativeWidth = ::core::ffi::c_ulong;
            pub type NativeCallback = ::core::option::Option<unsafe extern "C" fn(value: NativeWidth)>;
            pub mod Direction { pub type Type = ::core::ffi::c_int; pub const Backward: Type = -1; }
            """, 18);
        NativeBindingRawCatalog raw = NativeBindingRawParser.Parse("""
            extern "C" {
                pub fn combine(value: NativeWidth, offset: *const NativeWidth, direction: Direction::Type, callback: NativeCallback) -> NativeWidth;
                pub fn row(values: *const [u32; 3usize]) -> u32;
                pub fn resolve() -> ::core::option::Option<unsafe extern "C" fn(value: NativeWidth)>;
            }
            """, 18);
        var source = new StringBuilder("""
            #include <stdint.h>
            #include <stdio.h>
            typedef uint64_t NativeWidth;
            typedef void (*NativeCallback)(NativeWidth value);
            typedef enum Direction { Backward = -1, Forward = 1 } Direction;
            static NativeWidth observed;
            static void callback(NativeWidth value) { observed = value; }
            static NativeWidth combine(NativeWidth value, const NativeWidth *offset, Direction direction, NativeCallback notify)
            {
                NativeWidth result = direction == Backward ? value - *offset : value + *offset;
                notify(result);
                return result;
            }
            static uint32_t row(const uint32_t (*values)[3]) { return (*values)[0] + (*values)[2]; }
            static NativeCallback resolve(void) { return callback; }
            """);
        source.AppendLine();
        foreach ((string name, NativeBindingFunction function) in raw.Functions)
        {
            source.Append("static ").Append(NativeBindingCDeclaration.FunctionPointer(catalog, function, "invoke_" + name))
                .Append(" = ").Append(name).AppendLine(";");
        }

        source.Append("extern ").Append(NativeBindingCDeclaration.Global(catalog, "[u8; 0usize]", "native_values")).AppendLine(";");
        source.AppendLine("""
            uint8_t native_values[3] = { 11, 22, 33 };
            int main(void)
            {
                const uint32_t values[3] = { 17, 19, 23 };
                NativeWidth offset = 7;
                NativeWidth result = invoke_combine(UINT64_C(0xF123456789ABCDEF), &offset, Backward, callback);
                if (result != UINT64_C(0xF123456789ABCDE8) || observed != result) return 1;
                invoke_resolve()(UINT64_C(0xFEDCBA9876543210));
                if (observed != UINT64_C(0xFEDCBA9876543210)) return 2;
                printf("%zu,%u,%u", sizeof(NativeWidth), (unsigned)invoke_row(&values), (unsigned)native_values[2]);
                return 0;
            }
            """);
        string directory = Path.Join(Path.GetTempPath(), $"ankus-native-signatures-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Join(directory, "signatures.c");
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "signatures.exe" : "signatures");
            await File.WriteAllTextAsync(file, source.ToString(), context.CancellationToken);
            string compiler = OperatingSystem.IsWindows() ? "clang-cl.exe" : "cc";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", file, "-o", executable];
            await RunAsync(compiler, arguments, directory);
            Assert.AreEqual("8,40,33", await RunAsync(executable, [], directory));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
