using System.Globalization;
using System.Text.Json;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// The same char spelling and byte size retain different signedness when the collecting compiler's mode changes.
    /// </summary>
    /// <param name="charIsSigned">The requested compiler interpretation.</param>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task CollectedNumericModelsFollowCompilerOptions(bool charIsSigned)
    {
        const string Headers = "#include <stddef.h>\nextern char character; extern wchar_t wide; extern long double extended;";
        string directory = Path.Join(Path.GetTempPath(), "ankus-native-numeric-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            string option = charIsSigned ? "-fsigned-char" : "-funsigned-char";
            string frontendOption = OperatingSystem.IsWindows() ? "/clang:" + option : option;
            NativeRecordGraph graph = await CollectRecordsAsync(Headers,
                [new("character", "character", false), new("wide", "wide", false), new("extended", "extended", false)], directory, frontendOption);
            Assert.AreEqual(charIsSigned, graph.Target.Numeric.CharIsSigned);
            Assert.AreEqual(1L, graph.Types[graph.Roots["character"]].Size);
            Assert.AreEqual("char", graph.Types[graph.Roots["character"]].Name);
            Assert.AreEqual(graph.Types[graph.Roots["wide"]].Size, graph.Target.Numeric.WCharSize);
            Assert.AreEqual(new NativeFloatingModel(24, -125, 128), graph.Target.Numeric.Float);
            Assert.AreEqual(new NativeFloatingModel(53, -1021, 1024), graph.Target.Numeric.Double);

            const string Main = """
                #include <stdio.h>
                #include <stddef.h>
                #include <stdint.h>
                #include <float.h>
                int main(void) {
                    char value = (char)-1;
                    printf("%d %zu %d %d %d %d %d\n", (int)value, sizeof(wchar_t), WCHAR_MIN < 0,
                        FLT_RADIX, LDBL_MANT_DIG, LDBL_MIN_EXP, LDBL_MAX_EXP);
                    return 0;
                }
                """;
            string file = Path.Join(directory, "independent.c");
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "independent.exe" : "independent");
            await File.WriteAllTextAsync(file, Main, context.CancellationToken);
            string compiler = OperatingSystem.IsWindows() ? "clang-cl.exe" : "cc";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", frontendOption, "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", option, file, "-o", executable];
            await RunAsync(compiler, arguments, directory);
            int[] native = [.. (await RunAsync(executable, [], directory)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(static value => int.Parse(value, CultureInfo.InvariantCulture))];
            NativeNumericModel numeric = graph.Target.Numeric;
            Assert.AreSequenceEqual([charIsSigned ? -1 : 255, numeric.WCharSize, numeric.WCharIsSigned ? 1 : 0,
                numeric.Radix, numeric.LongDouble.Precision, numeric.LongDouble.MinExponent, numeric.LongDouble.MaxExponent], native);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// A worker compares its own compiler observations with requested numeric identity, then recovers after rejection.
    /// </summary>
    /// <param name="change">The contradictory scalar interpretation.</param>
    [TestMethod]
    [DataRow("char")]
    [DataRow("floating")]
    public async Task RecordWorkerValidatesNumericIdentity(string change)
    {
        string directory = Path.Join(Path.GetTempPath(), "ankus-numeric-worker-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph expected = await CollectRecordsAsync("extern int value;", [new("value", "value", false)], directory);
            NativeRecordRequest request = JsonSerializer.Deserialize<NativeRecordRequest>(await File.ReadAllTextAsync(
                Path.Join(directory, "native-record-request.json"), context.CancellationToken), NativeBindingRecordWorker.JsonOptions)!;
            NativeNumericModel numeric = expected.Target.Numeric;
            NativeNumericModel mismatch = change == "char" ? numeric with
            {
                CharIsSigned = !numeric.CharIsSigned
            }
                : numeric with
                {
                    LongDouble = numeric.LongDouble with
                    {
                        Precision = numeric.LongDouble.Precision + 1
                    }
                };
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() => NativeBindingRecordWorker.InspectAsync(
                request with
                {
                    Target = request.Target with
                    {
                        Numeric = mismatch
                    }
                }, directory, context.CancellationToken));
            Assert.Contains("target does not match", failure.Message);
            NativeRecordGraph recovered = await NativeBindingRecordWorker.InspectAsync(request, directory, context.CancellationToken);
            Assert.AreEqual(JsonSerializer.Serialize(expected, NativeBindingRecordWorker.JsonOptions),
                JsonSerializer.Serialize(recovered, NativeBindingRecordWorker.JsonOptions));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Final C compilation rejects a different numeric interpretation even when signatures and byte sizes are unchanged.
    /// </summary>
    /// <param name="change">The incompatible compiler model.</param>
    [TestMethod]
    [DataRow("char_signed")]
    [DataRow("wchar_size")]
    [DataRow("wchar_signed")]
    [DataRow("float_radix")]
    [DataRow("float_precision")]
    [DataRow("float_min_exp")]
    [DataRow("float_max_exp")]
    [DataRow("double_precision")]
    [DataRow("double_min_exp")]
    [DataRow("double_max_exp")]
    [DataRow("long_double_precision")]
    [DataRow("long_double_min_exp")]
    [DataRow("long_double_max_exp")]
    public async Task NativeCallBodiesRejectNumericAbiChanges(string change)
    {
        const string Headers = "#define PG_VERSION_NUM 180006\nchar native_char(char value) { return value; }\nlong double native_extended(long double value) { return value; }";
        string directory = Path.Join(Path.GetTempPath(), "ankus-numeric-calls-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers,
                [new("native_char", "native_char", true), new("native_extended", "native_extended", true)], directory);
            NativeNumericModel numeric = records.Headers.Target.Numeric;
            NativeNumericModel mismatch = change switch
            {
                "char_signed" => numeric with { CharIsSigned = !numeric.CharIsSigned },
                "wchar_size" => numeric with { WCharSize = numeric.WCharSize == 2 ? 4 : 2 },
                "wchar_signed" => numeric with { WCharIsSigned = !numeric.WCharIsSigned },
                "float_radix" => numeric with { Radix = numeric.Radix + 1 },
                "float_precision" => numeric with { Float = numeric.Float with { Precision = numeric.Float.Precision - 1 } },
                "float_min_exp" => numeric with { Float = numeric.Float with { MinExponent = numeric.Float.MinExponent - 1 } },
                "float_max_exp" => numeric with { Float = numeric.Float with { MaxExponent = numeric.Float.MaxExponent - 1 } },
                "double_precision" => numeric with { Double = numeric.Double with { Precision = numeric.Double.Precision - 1 } },
                "double_min_exp" => numeric with { Double = numeric.Double with { MinExponent = numeric.Double.MinExponent + 1 } },
                "double_max_exp" => numeric with { Double = numeric.Double with { MaxExponent = numeric.Double.MaxExponent - 1 } },
                "long_double_precision" => numeric with { LongDouble = numeric.LongDouble with { Precision = numeric.LongDouble.Precision + 1 } },
                "long_double_min_exp" => numeric with { LongDouble = numeric.LongDouble with { MinExponent = numeric.LongDouble.MinExponent - 1 } },
                "long_double_max_exp" => numeric with { LongDouble = numeric.LongDouble with { MaxExponent = numeric.LongDouble.MaxExponent + 1 } },
                _ => throw new ArgumentOutOfRangeException(nameof(change)),
            };
            NativeHeaderTarget target = records.Headers.Target with
            {
                Numeric = mismatch
            };
            NativeHeaderRecords changed = records with
            {
                Headers = records.Headers with
                {
                    Target = target
                },
                Graph = records.Graph with
                {
                    Target = target
                }
            };
            string file = Path.Join(directory, "calls.c");
            string diagnostics = Path.Join(directory, "calls.txt");
            string compiler = OperatingSystem.IsWindows() ? "clang-cl.exe" : "clang";
            string[] arguments = OperatingSystem.IsWindows() ? ["/nologo", "/std:c11", "/W4", "/WX", "/Zs", file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-fsyntax-only", file];
            await File.WriteAllTextAsync(file, NativeBindingCallSource.Generate(changed, Headers), context.CancellationToken);
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingHeaderCommand.CompileAsync(compiler, arguments, diagnostics, directory, context.CancellationToken, inspectBodies: true));
            Assert.Contains("Native numeric model changed: " + change, failure.Message);
            const string Main = """
                #include <stdio.h>
                int main(void) {
                    char value = (char)0xd3, result = 0;
                    AnkusNativeCallArgument argument = { &value, sizeof(value) };
                    if (ankus_native_call_native_char(&argument, 1, &result, sizeof(result)) != ANKUS_CALL_OK) return 1;
                    long double extended = 1.0L + LDBL_EPSILON, copied = 0;
                    argument = (AnkusNativeCallArgument){ &extended, sizeof(extended) };
                    if (ankus_native_call_native_extended(&argument, 1, &copied, sizeof(copied)) != ANKUS_CALL_OK) return 2;
                    printf("%u %d\n", (unsigned int)(unsigned char)result, copied == extended && copied != 1.0L);
                    return 0;
                }
                """;
            await File.WriteAllTextAsync(file, NativeBindingCallSource.Generate(records, Headers) + "\n" + Main, context.CancellationToken);
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "calls.exe" : "calls");
            string[] compile = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/O2", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-O2", file, "-o", executable];
            await RunAsync(compiler, compile, directory);
            Assert.AreEqual("211 1\n", (await RunAsync(executable, [], directory)).ReplaceLineEndings("\n"));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
