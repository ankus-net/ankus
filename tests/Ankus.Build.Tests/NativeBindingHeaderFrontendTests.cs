using System.Text.Json;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Header inspection retains inline signatures without compiling implementation bodies, and still rejects invalid declarations.
    /// </summary>
    [TestMethod]
    public async Task HeaderFrontendChecksDeclarationsWithoutCompilingBodies()
    {
        const string Headers = """
            static inline unsigned long native_inline(unsigned long value)
            {
                return server_compiler_intrinsic(value);
            }
            unsigned long (*native_inline_reference)(unsigned long) = native_inline;
            static inline void native_unannotated_exit(void)
            {
                __builtin_unreachable();
            }
            void (*native_exit_reference)(void) = native_unannotated_exit;
            extern _Noreturn void native_declared_exit(void);
            """;
        NativeHeaderRequest[] requests =
        [
            new("native_inline", "native_inline", true), new("native_unannotated_exit", "native_unannotated_exit", true),
            new("native_declared_exit", "native_declared_exit", true),
        ];
        string directory = Path.Join(Path.GetTempPath(), $"ankus-header-frontend-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Join(directory, "types.c");
            string ast = Path.Join(directory, "types.ast.json");
            string compiler = await HostCompilers.ClangAsync();
            string[] frontend = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Zs", "-Xclang", "-ast-dump=json", file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-fsyntax-only", "-Xclang", "-ast-dump=json", file];
            await File.WriteAllTextAsync(file, NativeBindingHeaderParser.GenerateSource(Headers, requests), context.CancellationToken);
            await NativeBindingHeaderCommand.CompileAsync(compiler, frontend, ast, directory, context.CancellationToken);
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(ast, context.CancellationToken));
            IReadOnlyDictionary<string, NativeHeaderSymbol> symbols = NativeBindingHeaderParser.Read(document.RootElement, requests);
            NativeHeaderSymbol symbol = symbols["native_inline"];
            Assert.AreEqual("unsigned long invoke(unsigned long ankus_arg0)", symbol.Type.Declare("invoke"));
            Assert.AreEqual("static", symbol.StorageClass);
            Assert.AreSequenceEqual<string>(["value"], symbol.ParameterNames);
            NativeHeaderSymbol unannotated = symbols["native_unannotated_exit"];
            Assert.IsFalse(unannotated.DoesNotReturn);
            Assert.IsFalse(Assert.IsInstanceOfType<NativeHeaderFunction>(unannotated.Type).DoesNotReturn);
            Assert.IsEmpty(unannotated.Attributes);
            Assert.IsTrue(symbols["native_declared_exit"].DoesNotReturn);
            Assert.Contains("C11NoReturnAttr", symbols["native_declared_exit"].Attributes);
            string checks = NativeBindingHeaderParser.GenerateChecks(Headers, symbols);
            await File.WriteAllTextAsync(file, checks, context.CancellationToken);
            await NativeBindingHeaderCommand.CompileAsync(compiler, frontend, ast, directory, context.CancellationToken);

            string changed = Headers.Replace("unsigned long value", "unsigned int value", StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, NativeBindingHeaderParser.GenerateChecks(changed, symbols), context.CancellationToken);
            InvalidOperationException mismatch = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingHeaderCommand.CompileAsync(compiler, frontend, ast, directory, context.CancellationToken));
            Assert.Contains("incompatible reconstructed native type: native_inline", mismatch.Message);

            await File.WriteAllTextAsync(file, checks + "\nint *invalid_pointer = (unsigned int *)0;\n", context.CancellationToken);
            InvalidOperationException diagnostic = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingHeaderCommand.CompileAsync(compiler, frontend, ast, directory, context.CancellationToken));
            Assert.Contains("-Werror", diagnostic.Message);
            Assert.Contains("invalid_pointer", diagnostic.Message);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
