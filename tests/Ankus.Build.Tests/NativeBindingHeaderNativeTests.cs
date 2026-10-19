using System.Text;
using System.Text.Json;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Native functions and dispatch-pointer globals retain their actual types when the reference platform differs.
    /// </summary>
    /// <param name="dispatch">Whether the selected headers expose a dispatch-pointer global.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task HeaderAvailabilityPreservesPlatformDeclarationKinds(bool dispatch)
    {
        const string Inventory = """
            extern "C" {
                #[link_name = "native_count"]
                pub fn count(word: u32) -> i32;
                #[link_name = "native_dispatch"]
                pub static mut dispatch: Option<unsafe extern "C" fn(word: u32) -> i32>;
            }
            """;
        string headers = "extern int " + (dispatch ? "(*native_count)" : "native_count") + "(unsigned int word);\n" +
            "extern int " + (dispatch ? "(*native_dispatch)" : "native_dispatch") + "(unsigned int word);\n";
        NativeBindingRawCatalog inventory = NativeBindingRawParser.Parse(Inventory, 18);
        NativeHeaderRequest[] reference = [.. inventory.Functions.Keys.Concat(inventory.Globals.Keys)
            .Select(name => NativeBindingHeaderAvailability.Request(inventory, name))];
        string directory = Directory.CreateTempSubdirectory("ankus-native-kind-").FullName;
        try
        {
            string file = Path.Join(directory, "types.c");
            string ast = Path.Join(directory, "types.json");
            string compiler = await HostCompilers.ClangAsync();
            string[] frontend = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Zs", "-Xclang", "-ast-dump=json", file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-fsyntax-only", "-Xclang", "-ast-dump=json", file];
            await File.WriteAllTextAsync(file, NativeBindingHeaderParser.GenerateSource(headers, reference), context.CancellationToken);
            await NativeBindingHeaderCommand.CompileAsync(compiler, frontend, ast, directory, context.CancellationToken);
            using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(ast, context.CancellationToken));
            NativeBindingAvailability availability = NativeBindingHeaderAvailability.Read(document.RootElement, inventory);
            Assert.IsEmpty(availability.Absent);
            Assert.AreSequenceEqual<NativeHeaderRequest>([new("count", "native_count", !dispatch), new("dispatch", "native_dispatch", !dispatch)], availability.Available);
            IReadOnlyDictionary<string, NativeHeaderSymbol> symbols = NativeBindingHeaderParser.Read(document.RootElement, availability.Available);
            foreach (NativeHeaderSymbol symbol in symbols.Values)
            {
                Assert.AreEqual(!dispatch, symbol.IsFunction);
                NativeHeaderType callable = dispatch ? Assert.IsInstanceOfType<NativeHeaderPointer>(symbol.Type).Element : symbol.Type;
                NativeHeaderFunction function = Assert.IsInstanceOfType<NativeHeaderFunction>(callable);
                Assert.AreEqual(new NativeHeaderScalar("int"), function.Result);
                Assert.AreSequenceEqual<NativeHeaderType>([new NativeHeaderScalar("unsigned int")], function.Parameters);
            }

            Assert.ThrowsExactly<FormatException>(() => NativeBindingHeaderParser.Read(document.RootElement, reference));
            string definitions = "static int implementation(unsigned int word) { return word == 0xffffffffU ? 32 : 0; }\n" +
                (dispatch
                    ? "int (*native_count)(unsigned int) = implementation;\nint (*native_dispatch)(unsigned int) = implementation;\n"
                    : "int native_count(unsigned int word) { return implementation(word); }\nint native_dispatch(unsigned int word) { return implementation(word); }\n");
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "types.exe" : "types");
            string source = NativeBindingHeaderParser.GenerateChecks(headers, symbols) + definitions +
                "int main(void) { return native_count(0xffffffffU) == 32 && native_dispatch(0U) == 0 ? 0 : 1; }\n";
            await File.WriteAllTextAsync(file, source, context.CancellationToken);
            string[] compile = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", file, "-o", executable];
            await RunAsync(compiler, compile, directory);
            Assert.AreEqual("", await RunAsync(executable, [], directory));
            string incompatible = headers.Replace("unsigned int", "unsigned long long", StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, NativeBindingHeaderParser.GenerateChecks(incompatible, symbols), context.CancellationToken);
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingHeaderCommand.CompileAsync(compiler, frontend, ast, directory, context.CancellationToken));
            Assert.Contains("incompatible reconstructed native type", failure.Message);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Structured header types preserve native distinctions and reconstruct compiler-compatible declarations.
    /// </summary>
    [TestMethod]
    public async Task HeaderTypesRetainNativeSemantics()
    {
        const string Headers = """
            #include <stdarg.h>
            typedef unsigned long long NativeWidth;
            typedef struct { NativeWidth value; } Anonymous;
            union NativeUnion { int number; NativeWidth bits; };
            typedef enum { First = 1, Second = 2 } Mode;
            typedef int (*Callback)(volatile Anonymous *, const char *const *);
            typedef long NativeFunction(int value);
            extern NativeFunction alias_call;
            extern Callback factory(NativeWidth start);
            extern void arrays(const int values[static 3], int (*row)[4]);
            extern void qualified_arrays(int values[static const restrict 3]);
            extern union NativeUnion pick(Mode mode);
            extern void variadic(const char *format, ...);
            extern void restricted(int *__restrict values);
            extern int native_va(const char *format, va_list arguments);
            extern _Noreturn void stop(int code);
            extern void unspecified();
            extern volatile Anonymous current;
            extern Callback hook;
            extern const NativeWidth fixed_value;
            extern int open_array[];
            extern _Thread_local int thread_value;
            """;
        NativeHeaderRequest[] requests =
        [
            new("factory", "factory", true), new("arrays", "arrays", true), new("pick", "pick", true), new("alias_call", "alias_call", true),
            new("variadic", "variadic", true), new("restricted", "restricted", true), new("native_va", "native_va", true),
            new("qualified_arrays", "qualified_arrays", true),
            new("stop", "stop", true), new("unspecified", "unspecified", true),
            new("current", "current", false), new("hook", "hook", false), new("fixed_value", "fixed_value", false),
            new("open_array", "open_array", false), new("thread_value", "thread_value", false),
        ];
        string directory = Path.Join(Path.GetTempPath(), $"ankus-header-types-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            string file = Path.Join(directory, "types.c");
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "types.exe" : "types");
            string compiler = await HostCompilers.ClangAsync();
            string[] frontend = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Zs", "-Xclang", "-ast-dump=json", file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-fsyntax-only", "-Xclang", "-ast-dump=json", file];
            string targetHeaders = NativeBindingHeaderTarget.GenerateSource("#define PG_VERSION_NUM 180006\n" + Headers, 18);
            await File.WriteAllTextAsync(file, NativeBindingHeaderParser.GenerateSource(targetHeaders, requests), context.CancellationToken);
            string ast = Path.Join(directory, "types.ast.json");
            await NativeBindingHeaderCommand.CompileAsync(compiler, frontend, ast, directory, context.CancellationToken);
            string json = await File.ReadAllTextAsync(ast, context.CancellationToken);
            using JsonDocument document = JsonDocument.Parse(json);
            IReadOnlyDictionary<string, NativeHeaderSymbol> symbols = NativeBindingHeaderParser.Read(document.RootElement, requests);
            NativeHeaderTarget target = NativeBindingHeaderTarget.Read(document.RootElement, 18);
            Assert.AreEqual(180006, target.PostgresVersion);
            Assert.AreEqual(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, target.RuntimeIdentifier);
            Assert.AreEqual(IntPtr.Size, target.PointerSize);
            Assert.AreEqual(BitConverter.IsLittleEndian, target.IsLittleEndian);
            Assert.IsGreaterThan(0, target.ClangMajor);
            Assert.AreSequenceEqual(requests.Select(static request => request.Name).Order(StringComparer.Ordinal), symbols.Keys);
            NativeHeaderFunction factory = Assert.IsInstanceOfType<NativeHeaderFunction>(symbols["factory"].Type);
            NativeHeaderAlias functionAlias = Assert.IsInstanceOfType<NativeHeaderAlias>(symbols["alias_call"].Type);
            Assert.AreEqual("NativeFunction", functionAlias.Name);
            Assert.AreEqual("long invoke(int ankus_arg0)", functionAlias.Underlying.Declare("invoke"));
            Assert.AreSequenceEqual<string>([""], symbols["alias_call"].ParameterNames);
            Assert.AreSequenceEqual<string>(["start"], symbols["factory"].ParameterNames);
            NativeHeaderAlias width = Assert.IsInstanceOfType<NativeHeaderAlias>(Assert.ContainsSingle(factory.Parameters));
            Assert.AreEqual("NativeWidth", width.Name);
            Assert.AreEqual(new NativeHeaderScalar("unsigned long long"), width.Underlying);
            NativeHeaderAlias callback = Assert.IsInstanceOfType<NativeHeaderAlias>(factory.Result);
            Assert.AreEqual("Callback", callback.Name);
            NativeHeaderFunction callbackType = Assert.IsInstanceOfType<NativeHeaderFunction>(
                Assert.IsInstanceOfType<NativeHeaderPointer>(callback.Underlying).Element);
            Assert.AreEqual("int callback(volatile Anonymous *ankus_arg0, const char *const *ankus_arg1)", callbackType.Declare("callback"));
            NativeHeaderQualified current = Assert.IsInstanceOfType<NativeHeaderQualified>(symbols["current"].Type);
            Assert.AreEqual(NativeHeaderQualifiers.Volatile, current.Modifiers);
            NativeHeaderAlias anonymous = Assert.IsInstanceOfType<NativeHeaderAlias>(current.Underlying);
            Assert.AreEqual(new NativeHeaderRecord("", false, true), anonymous.Underlying);
            Assert.AreEqual("volatile Anonymous state", current.Declare("state"));
            Assert.AreEqual("extern", symbols["current"].StorageClass);
            Assert.IsFalse(symbols["current"].IsThreadLocal);
            Assert.IsTrue(symbols["thread_value"].IsThreadLocal);
            Assert.IsTrue(symbols["stop"].DoesNotReturn);
            Assert.Contains("C11NoReturnAttr", symbols["stop"].Attributes);
            Assert.IsFalse(symbols["factory"].DoesNotReturn);
            NativeHeaderFunction arrays = Assert.IsInstanceOfType<NativeHeaderFunction>(symbols["arrays"].Type);
            NativeHeaderAdjusted adjusted = Assert.IsInstanceOfType<NativeHeaderAdjusted>(arrays.Parameters[0]);
            Assert.IsInstanceOfType<NativeHeaderPointer>(adjusted.Adjusted);
            NativeHeaderArray written = Assert.IsInstanceOfType<NativeHeaderArray>(adjusted.Written);
            Assert.AreEqual(3UL, written.Count);
            Assert.IsTrue(written.HasMinimumExtent);
            Assert.AreEqual("void arrays(const int ankus_arg0[static 3], int (*ankus_arg1)[4])", arrays.Declare("arrays"));
            NativeHeaderFunction qualifiedArrays = Assert.IsInstanceOfType<NativeHeaderFunction>(symbols["qualified_arrays"].Type);
            NativeHeaderAdjusted qualifiedAdjusted = Assert.IsInstanceOfType<NativeHeaderAdjusted>(Assert.ContainsSingle(qualifiedArrays.Parameters));
            NativeHeaderArray qualifiedWritten = Assert.IsInstanceOfType<NativeHeaderArray>(qualifiedAdjusted.Written);
            NativeHeaderQualifiers bracketQualifiers = NativeHeaderQualifiers.Const | NativeHeaderQualifiers.Restrict;
            Assert.AreEqual(bracketQualifiers, qualifiedWritten.IndexQualifiers);
            Assert.AreEqual(bracketQualifiers, Assert.IsInstanceOfType<NativeHeaderQualified>(qualifiedAdjusted.Adjusted).Modifiers);
            Assert.AreEqual("void qualifiedArrays(int ankus_arg0[static const restrict 3])", qualifiedArrays.Declare("qualifiedArrays"));
            Assert.AreEqual("int values[]", symbols["open_array"].Type.Declare("values"));
            Assert.AreEqual("const NativeWidth fixedValue", symbols["fixed_value"].Type.Declare("fixedValue"));
            Assert.IsTrue(Assert.IsInstanceOfType<NativeHeaderFunction>(symbols["variadic"].Type).IsVariadic);
            Assert.AreEqual("void restricted(int *restrict ankus_arg0)", symbols["restricted"].Type.Declare("restricted"));
            Assert.AreEqual("int native_va(const char *ankus_arg0, va_list ankus_arg1)", symbols["native_va"].Type.Declare("native_va"));
            Assert.IsFalse(Assert.IsInstanceOfType<NativeHeaderFunction>(symbols["unspecified"].Type).HasPrototype);
            NativeHeaderFunction pick = Assert.IsInstanceOfType<NativeHeaderFunction>(symbols["pick"].Type);
            Assert.AreEqual(new NativeHeaderRecord("NativeUnion", true, true), pick.Result);
            Assert.AreEqual(new NativeHeaderEnum("", true), Assert.IsInstanceOfType<NativeHeaderAlias>(pick.Parameters[0]).Underlying);

            var source = new StringBuilder(NativeBindingHeaderParser.GenerateChecks(Headers, symbols));
            source.Append("typedef ").Append(adjusted.Declare("AdjustedValues")).AppendLine(";");
            source.AppendLine("_Static_assert(_Generic((AdjustedValues)0, const int*: 1, default: 0), \"parameter storage must be a pointer\");");
            source.Append("typedef ").Append(qualifiedAdjusted.Declare("QualifiedAdjustedValues")).AppendLine(";");
            source.AppendLine("QualifiedAdjustedValues qualified_values = 0;");
            source.AppendLine("_Static_assert(_Generic(&qualified_values, int *const restrict *: 1, default: 0), \"adjusted storage must retain pointer qualifiers\");");
            source.AppendLine("int main(void) { return 0; }");
            await File.WriteAllTextAsync(file, source.ToString(), context.CancellationToken);
            string[] compile = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", file, "-o", executable];
            await RunAsync(compiler, compile, directory);
            Assert.AreEqual("", await RunAsync(executable, [], directory));
            string changedHeaders = Headers.Replace("extern union NativeUnion pick(Mode mode)", "extern int pick(Mode mode)", StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, NativeBindingHeaderParser.GenerateChecks(changedHeaders, symbols), context.CancellationToken);
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingHeaderCommand.CompileAsync(compiler, frontend, ast, directory, context.CancellationToken));
            Assert.Contains("pick", failure.Message);
            string serialized = JsonSerializer.Serialize(symbols);
            Assert.Contains("\"Kind\":\"alias\"", serialized);
            Assert.DoesNotContain("\"id\"", serialized);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
