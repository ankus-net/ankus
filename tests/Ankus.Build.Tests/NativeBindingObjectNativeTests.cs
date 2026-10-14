namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Independent compiler objects distinguish actual references from unused declarations, definitions, locals and common storage.
    /// </summary>
    /// <param name="target">The compiler's explicit object target.</param>
    /// <param name="format">The expected object container.</param>
    /// <param name="architecture">The independently selected processor.</param>
    /// <param name="littleEndian">The target's byte order.</param>
    [TestMethod]
    [DataRow("x86_64-unknown-linux-gnu", "elf", "x64", true)]
    [DataRow("i686-unknown-linux-gnu", "elf", "x86", true)]
    [DataRow("aarch64-unknown-linux-gnu", "elf", "arm64", true)]
    [DataRow("aarch64_be-unknown-linux-gnu", "elf", "arm64", false)]
    [DataRow("armv7-unknown-linux-gnueabihf", "elf", "arm", true)]
    [DataRow("x86_64-pc-windows-msvc", "coff", "x64", true)]
    [DataRow("i686-pc-windows-msvc", "coff", "x86", true)]
    [DataRow("aarch64-pc-windows-msvc", "coff", "arm64", true)]
    [DataRow("x86_64-apple-macosx11.0", "mach-o", "x64", true)]
    [DataRow("arm64-apple-macosx11.0", "mach-o", "arm64", true)]
    [DataRow("i386-apple-macosx10.6", "mach-o", "x86", true)]
    [DataRow("armv7-apple-ios9.0", "mach-o", "arm", true)]
    public async Task CompiledObjectsRetainOnlyNativeImports(string target, string format, string architecture, bool littleEndian)
    {
        const string Source = """
            extern int ankus_long_referenced(int value);
            extern int ankus_z(int value);
            extern int unrelated(int value);
            extern int ankus_unused(int value);
            __attribute__((weak)) extern int ankus_weak(int value);
            #if !defined(_WIN32)
            __attribute__((visibility("hidden")))
            #endif
            extern int ankus_hidden(int value);
            int ankus_common;
            int ankus_defined(int value) { return value + 3; }
            static int ankus_local(int value) { return value + 5; }
            int entry(int value)
            {
                return ankus_long_referenced(value) + ankus_z(value) + ankus_hidden(value)
                    + ankus_weak(value) + unrelated(value) + ankus_common + ankus_defined(value) + ankus_local(value);
            }
            """;
        byte[] image = await CompileNativeObjectAsync(Source, target);
        NativeObjectImports imports = NativeObjectSymbols.Read(image, "ankus_");
        Assert.AreEqual(format, imports.Format);
        Assert.AreEqual(architecture, imports.Architecture);
        Assert.AreEqual(littleEndian, imports.IsLittleEndian);
        Assert.AreSequenceEqual<string>(["ankus_hidden", "ankus_long_referenced", "ankus_weak", "ankus_z"], imports.Symbols);
        Assert.AreSequenceEqual<string>(["unrelated"], NativeObjectSymbols.Read(image, "unrelated").Symbols);
        Assert.IsEmpty(NativeObjectSymbols.Read(image, "absent_").Symbols);
    }

    /// <summary>
    /// A valid compiled object with no referenced native entries produces an empty selection.
    /// </summary>
    /// <param name="target">The independently selected native container.</param>
    [TestMethod]
    [DataRow("x86_64-unknown-linux-gnu")]
    [DataRow("x86_64-pc-windows-msvc")]
    [DataRow("arm64-apple-macosx11.0")]
    public async Task CompiledObjectsAllowEmptyImports(string target)
    {
        byte[] image = await CompileNativeObjectAsync("int ankus_defined(void) { return 42; }", target);
        Assert.IsEmpty(NativeObjectSymbols.Read(image, "ankus_").Symbols);
    }

    private async Task<byte[]> CompileNativeObjectAsync(string source, string? target = null, bool bigCoff = false)
    {
        string directory = Directory.CreateTempSubdirectory("ankus-object-").FullName;
        try
        {
            string file = Path.Join(directory, "symbols.c");
            string output = Path.Join(directory, "symbols.o");
            await File.WriteAllTextAsync(file, source, context.CancellationToken);
            string[] targetArguments = target is null ? [] : ["--target=" + target];
            // These freestanding object fixtures use no platform headers. An explicit
            // empty sysroot prevents a host macOS SDK from constraining other Apple targets.
            string[] sysrootArguments = target?.Contains("-apple-", StringComparison.Ordinal) == true
                ? ["-isysroot", directory] : [];
            string compiler = bigCoff ? "cl.exe" : OperatingSystem.IsWindows() ? "clang.exe" : "clang";
            string[] arguments = bigCoff
                ? ["/nologo", "/W4", "/WX", "/O2", "/bigobj", "/c", "/Fo" + output, file]
                : [.. targetArguments, .. sysrootArguments, "-ffreestanding", "-fcommon", "-O2", "-g", "-Wall", "-Wextra", "-Werror", "-c", file, "-o", output];
            await RunAsync(compiler, arguments, directory);
            return await File.ReadAllBytesAsync(output, context.CancellationToken);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
