using System.Text;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Compiler-resolved typeof wrappers retain qualifiers, typedefs, arrays and prototypes without evaluating expressions.
    /// </summary>
    [TestMethod]
    public async Task HeaderTypesPreserveTypeofSemantics()
    {
        const string Headers = """
            typedef unsigned long long Width;
            typedef struct Record { Width value; } Record;
            extern const Record original;
            extern __typeof__(original) copy;
            extern __typeof_unqual__(original) mutable_copy;
            typedef __typeof__(const Width) Constant;
            typedef __typeof_unqual__(const Width) Mutable;
            extern Constant constant_value;
            extern Mutable mutable_value;
            extern int rows[3];
            extern __typeof__(rows) copied_rows;
            extern const volatile Width *const restrict address;
            extern __typeof_unqual__(address) mutable_address;
            extern const volatile __typeof_unqual__(address) qualified_address;
            extern int evaluate(const Record *input, Width extra);
            extern __typeof__(evaluate) copied_function;
            """;
        NativeHeaderRequest[] requests =
        [
            new("original", "original", false), new("copy", "copy", false), new("mutable_copy", "mutable_copy", false),
            new("constant_value", "constant_value", false), new("mutable_value", "mutable_value", false),
            new("rows", "rows", false), new("copied_rows", "copied_rows", false),
            new("mutable_address", "mutable_address", false), new("qualified_address", "qualified_address", false),
            new("evaluate", "evaluate", true), new("copied_function", "copied_function", true),
        ];
        string directory = Directory.CreateTempSubdirectory("ankus-typeof-").FullName;
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers, requests, directory);
            NativeBindingSignatureValidation.Validate(records);
            IReadOnlyDictionary<string, NativeHeaderSymbol> symbols = records.Headers.Symbols;
            Assert.AreEqual(symbols["original"].Type, symbols["copy"].Type);
            NativeHeaderQualified original = Assert.IsInstanceOfType<NativeHeaderQualified>(symbols["copy"].Type);
            Assert.AreEqual(NativeHeaderQualifiers.Const, original.Modifiers);
            Assert.AreEqual(original.Underlying, symbols["mutable_copy"].Type);
            NativeHeaderAlias constant = Assert.IsInstanceOfType<NativeHeaderAlias>(symbols["constant_value"].Type);
            NativeHeaderQualified qualified = Assert.IsInstanceOfType<NativeHeaderQualified>(constant.Underlying);
            Assert.AreEqual(NativeHeaderQualifiers.Const, qualified.Modifiers);
            Assert.AreEqual(qualified.Underlying, Assert.IsInstanceOfType<NativeHeaderAlias>(symbols["mutable_value"].Type).Underlying);
            Assert.AreEqual(symbols["rows"].Type, symbols["copied_rows"].Type);
            Assert.AreEqual("const volatile Width *value", symbols["mutable_address"].Type.Declare("value"));
            Assert.AreEqual("const volatile Width *const volatile value", symbols["qualified_address"].Type.Declare("value"));
            Assert.AreEqual("int call(const Record *ankus_arg0, Width ankus_arg1)", symbols["copied_function"].Type.Declare("call"));
            Assert.AreSequenceEqual<string>(["", ""], symbols["copied_function"].ParameterNames);
            string checks = NativeBindingRecordChecks.Generate(records, "#define PG_VERSION_NUM 180006\n" + Headers);
            var source = new StringBuilder(NativeBindingHeaderParser.GenerateChecks(Headers, symbols));
            source.AppendLine("int main(void) { return 0; }");
            string file = Path.Join(directory, "checks.c");
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "checks.exe" : "checks");
            string compiler = OperatingSystem.IsWindows() ? "cl.exe" : await HostCompilers.ClangAsync();
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", file, "-o", executable];
            await File.WriteAllTextAsync(file, source.ToString(), context.CancellationToken);
            await RunAsync(compiler, arguments, directory);
            Assert.AreEqual("", await RunAsync(executable, [], directory));
            await File.WriteAllTextAsync(file, checks + NativeBindingRecordChecks.ExecutableEntryPoint, context.CancellationToken);
            await RunAsync(compiler, arguments, directory);
            Assert.AreEqual("", await RunAsync(executable, [], directory));
            string changed = Headers.Replace("extern const Record original;", "extern Record original;", StringComparison.Ordinal);
            await File.WriteAllTextAsync(file, NativeBindingHeaderParser.GenerateChecks(changed, symbols), context.CancellationToken);
            string diagnostic = await RunAsync(compiler, arguments, directory, expectSuccess: false);
            Assert.Contains("incompatible reconstructed native type: copy", diagnostic);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Resolving a typeof wrapper cannot accept incompatible qualifications, storage identities or prototypes.
    /// </summary>
    /// <param name="declaration">The declaration establishing the compiler's actual type.</param>
    /// <param name="replacement">The independent header signature's incompatible type.</param>
    /// <param name="expected">The specific rejected part of the native contract.</param>
    [TestMethod]
    [DataRow("const Width value", "unsigned long long value", "type qualifiers")]
    [DataRow("const Width *value", "unsigned long long *value", "type qualifiers")]
    [DataRow("Width value", "long long value", "scalar identity")]
    [DataRow("Width value[3]", "unsigned long long value[4]", "array extent")]
    [DataRow("struct First value", "struct Second value", "tag identity")]
    [DataRow("Width (*value)(const Width *)", "unsigned long long (*value)(unsigned long long *)", "type qualifiers")]
    [DataRow("Width (*value)(int)", "unsigned long long (*value)(int, int)", "parameter count")]
    [DataRow("Width (*value)(int)", "long long (*value)(int)", "scalar identity")]
    [DataRow("Width (*value)(int)", "unsigned long long (*value)(int, ...)", "function prototype")]
    public async Task TypeofSignaturesRejectChangedContracts(string declaration, string replacement, string expected)
    {
        const string Common = "typedef unsigned long long Width; struct First { Width member; }; struct Second { Width member; };\n";
        string directory = Directory.CreateTempSubdirectory("ankus-typeof-contract-").FullName;
        try
        {
            NativeHeaderRequest[] requests = [new("copy", "copy", false)];
            NativeHeaderRecords records = await CollectCallRecordsAsync(Common + "extern " + declaration + "; extern __typeof__(value) copy;", requests, directory);
            NativeBindingSignatureValidation.Validate(records);
            NativeHeaderRecords changed = await CollectCallRecordsAsync(Common + "extern " + replacement + "; extern __typeof__(value) copy;", requests, directory);
            FormatException failure = Assert.ThrowsExactly<FormatException>(() =>
                NativeBindingSignatureValidation.Validate(records with
                {
                    Headers = changed.Headers
                }));
            Assert.Contains("copy", failure.Message);
            Assert.Contains(expected, failure.Message);
            NativeBindingSignatureValidation.Validate(records);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Canonical prototype adjustment cannot hide a lost qualifier in a written typeof parameter.
    /// </summary>
    [TestMethod]
    public async Task TypeofParametersRetainWrittenQualification()
    {
        const string Headers = "typedef unsigned long long Width; extern const Width original; extern int invoke(__typeof__(original) input);";
        string directory = Directory.CreateTempSubdirectory("ankus-typeof-parameter-").FullName;
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers, [new("invoke", "invoke", true)], directory);
            NativeBindingSignatureValidation.Validate(records);
            NativeHeaderSymbol symbol = records.Headers.Symbols["invoke"];
            NativeHeaderFunction function = Assert.IsInstanceOfType<NativeHeaderFunction>(symbol.Type);
            NativeHeaderQualified parameter = Assert.IsInstanceOfType<NativeHeaderQualified>(Assert.ContainsSingle(function.Parameters));
            Assert.AreEqual(NativeHeaderQualifiers.Const, parameter.Modifiers);
            Dictionary<string, NativeHeaderSymbol> changed = records.Headers.Symbols.ToDictionary();
            changed["invoke"] = symbol with
            {
                Type = function with
                {
                    Parameters = [parameter.Underlying]
                }
            };
            FormatException failure = Assert.ThrowsExactly<FormatException>(() =>
                NativeBindingSignatureValidation.Validate(records with
                {
                    Headers = records.Headers with
                    {
                        Symbols = changed
                    }
                }));
            Assert.Contains("invoke.argument0", failure.Message);
            Assert.Contains("type qualifiers", failure.Message);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Nested typeof parameters and results preserve exact aggregate bytes through generated managed and native calls.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallsPreserveTypeofArguments()
    {
        const string Headers = """
            typedef unsigned long long Width;
            typedef struct Record { Width value; } Record;
            extern const Record original;
            __typeof_unqual__(original) transform(__typeof__(original) input, __typeof__(input.value) extra)
            {
                Record result = { input.value + extra };
                return result;
            }
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    Record input = default;
                    input.value = 9223372036854775808UL;
                    Record result = NativeMethods.transform(input, 19);
                    return [unchecked((long)result.value), unchecked((long)input.value), scope.Validations, scope.Invocations];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([long.MinValue + 19, long.MinValue, 1, 1], await ExecuteManagedCallsAsync(Headers, ["transform"], Harness));
    }
}
