using System.Globalization;
using System.Text.Json;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Actual native calls retain scalar widths and compiler-classified aggregate, pointer and callback values.
    /// </summary>
    [TestMethod]
    public async Task NativeCallBodiesPreserveCompilerAbi()
    {
        const string Headers = """
            #include <stdint.h>
            #include <limits.h>
            #include <stdbool.h>
            #include <float.h>
            typedef struct { double fraction; uint32_t bits; } Mixed;
            typedef struct { uint64_t values[5]; } Large;
            typedef union { uint64_t bits; double fraction; } Bits;
            typedef struct { const int value; } Immutable;
            typedef struct {} Empty;
            typedef int Aligned __attribute__((aligned(32)));
            typedef void NativeVoid;
            typedef int (*Callback)(int);
            int offset;
            int callback(int value) { return value + offset; }
            unsigned long native_width(unsigned long value) { return ~value; }
            Mixed native_mixed(Mixed value, double factor) { value.fraction *= factor; value.bits ^= UINT32_MAX; return value; }
            Large native_large(Large value) { for (int i = 0; i < 5; ++i) value.values[i] += (uint64_t)i; return value; }
            Bits native_union(uint64_t value) { Bits result; result.bits = value; return result; }
            Callback native_factory(int value) { offset = value; return callback; }
            int native_callback(Callback function, int value) { return function(value); }
            long double native_extended(long double value) { return value * 2; }
            unsigned __int128 native_huge(unsigned __int128 value) { return value ^ ((unsigned __int128)1 << 111); }
            Aligned native_aligned(Aligned value) { return value + 7; }
            int native_array(const int values[static 3]) { return values[0] + values[1] + values[2]; }
            Immutable native_const(Immutable value) { Immutable result = { value.value + 1 }; return result; }
            void *native_address(void *address) { return address; }
            bool native_bool(bool value) { return !value; }
            int native_zero(void) { return 52; }
            NativeVoid native_void(int value) { offset = value; }
            Empty native_empty_record(Empty value) { ++offset; return value; }
            """;
        string[] names = ["native_width", "native_mixed", "native_large", "native_union", "native_factory", "native_callback", "native_extended", "native_huge", "native_array",
            "native_const", "native_address", "native_bool", "native_zero", "native_void", "native_empty_record", "native_aligned"];
        const string Main = """
            #include <stdio.h>
            #define REQUIRE(expression) do { if (!(expression)) { fprintf(stderr, "line %d: %s\n", __LINE__, #expression); return 1; } } while (0)
            int main(void)
            {
                unsigned long input = 17, width = 0;
                AnkusNativeCallArgument one[] = {{ &input, sizeof(input) }};
                REQUIRE(ankus_native_call_native_width(one, 1, &width, sizeof(width)) == ANKUS_CALL_OK);
                REQUIRE(width == ULONG_MAX - 17);
                Mixed mixed = { 1.25, UINT32_C(0x81234567) };
                double factor = -4.0;
                AnkusNativeCallArgument pair[] = {{ &mixed, sizeof(mixed) }, { &factor, sizeof(factor) }};
                REQUIRE(ankus_native_call_native_mixed(pair, 2, &mixed, sizeof(mixed)) == ANKUS_CALL_OK);
                REQUIRE(mixed.fraction == -5.0 && mixed.bits == UINT32_C(0x7edcba98));
                Large large = {{ UINT64_MAX, UINT64_C(0x100000000), 17, 23, 29 }}, output = {0};
                one[0] = (AnkusNativeCallArgument){ &large, sizeof(large) };
                REQUIRE(ankus_native_call_native_large(one, 1, &output, sizeof(output)) == ANKUS_CALL_OK);
                REQUIRE(output.values[0] == UINT64_MAX && output.values[1] == UINT64_C(0x100000001));
                REQUIRE(output.values[2] == 19 && output.values[3] == 26 && output.values[4] == 33);
                REQUIRE(large.values[2] == 17 && large.values[4] == 29);
                uint64_t bits = UINT64_C(0xfedcba9876543210);
                Bits union_result = {0};
                one[0] = (AnkusNativeCallArgument){ &bits, sizeof(bits) };
                unsigned char unaligned[sizeof(Bits) + 1];
                REQUIRE(ankus_native_call_native_union(one, 1, unaligned + 1, sizeof(Bits)) == ANKUS_CALL_OK);
                memcpy(&union_result, unaligned + 1, sizeof(union_result));
                REQUIRE(union_result.bits == bits);
                int number = 41, scalar = 0;
                Callback generated = NULL;
                one[0] = (AnkusNativeCallArgument){ &number, sizeof(number) };
                REQUIRE(ankus_native_call_native_factory(one, 1, &generated, sizeof(generated)) == ANKUS_CALL_OK);
                REQUIRE(generated == callback && generated(1) == 42);
                int increment = 3;
                pair[0] = (AnkusNativeCallArgument){ &generated, sizeof(generated) };
                pair[1] = (AnkusNativeCallArgument){ &increment, sizeof(increment) };
                REQUIRE(ankus_native_call_native_callback(pair, 2, &scalar, sizeof(scalar)) == ANKUS_CALL_OK);
                REQUIRE(scalar == 44);
                long double extended = 1.0L + LDBL_EPSILON, extended_result = 0;
                one[0] = (AnkusNativeCallArgument){ &extended, sizeof(extended) };
                REQUIRE(ankus_native_call_native_extended(one, 1, &extended_result, sizeof(extended_result)) == ANKUS_CALL_OK);
                REQUIRE(extended_result == 2.0L + 2 * LDBL_EPSILON);
                unsigned __int128 huge = ((unsigned __int128)1 << 100) + 12345, huge_result = 0;
                one[0] = (AnkusNativeCallArgument){ &huge, sizeof(huge) };
                REQUIRE(ankus_native_call_native_huge(one, 1, &huge_result, sizeof(huge_result)) == ANKUS_CALL_OK);
                REQUIRE(huge_result == (((unsigned __int128)1 << 111) | huge));
                Aligned aligned = 91, aligned_result = 0;
                one[0] = (AnkusNativeCallArgument){ &aligned, sizeof(aligned) };
                REQUIRE(ankus_native_call_native_aligned(one, 1, &aligned_result, sizeof(aligned_result)) == ANKUS_CALL_OK);
                REQUIRE(aligned_result == 98);
                int values[] = { 3, 5, 11 };
                const int *address = values;
                one[0] = (AnkusNativeCallArgument){ &address, sizeof(address) };
                REQUIRE(ankus_native_call_native_array(one, 1, &scalar, sizeof(scalar)) == ANKUS_CALL_OK);
                REQUIRE(scalar == 19);
                Immutable immutable = { 72 };
                one[0] = (AnkusNativeCallArgument){ &immutable, sizeof(immutable) };
                unsigned char immutable_result[sizeof(Immutable)];
                REQUIRE(ankus_native_call_native_const(one, 1, immutable_result, sizeof(Immutable)) == ANKUS_CALL_OK);
                int immutable_value;
                memcpy(&immutable_value, immutable_result, sizeof(immutable_value));
                REQUIRE(immutable_value == 73 && immutable.value == 72);
                void *borrowed = values, *returned = NULL;
                one[0] = (AnkusNativeCallArgument){ &borrowed, sizeof(borrowed) };
                REQUIRE(ankus_native_call_native_address(one, 1, &returned, sizeof(returned)) == ANKUS_CALL_OK);
                REQUIRE(returned == values);
                borrowed = NULL;
                REQUIRE(ankus_native_call_native_address(one, 1, &returned, sizeof(returned)) == ANKUS_CALL_OK);
                REQUIRE(returned == NULL);
                bool condition = true, opposite = true;
                one[0] = (AnkusNativeCallArgument){ &condition, sizeof(condition) };
                REQUIRE(ankus_native_call_native_bool(one, 1, &opposite, sizeof(opposite)) == ANKUS_CALL_OK);
                REQUIRE(!opposite);
                REQUIRE(ankus_native_call_native_zero(NULL, 0, &scalar, sizeof(scalar)) == ANKUS_CALL_OK);
                REQUIRE(scalar == 52);
                number = 83;
                one[0] = (AnkusNativeCallArgument){ &number, sizeof(number) };
                REQUIRE(ankus_native_call_native_void(one, 1, NULL, 0) == ANKUS_CALL_OK);
                REQUIRE(offset == 83);
                Empty empty, empty_result;
                memset(&empty, 0, sizeof(empty));
                one[0] = (AnkusNativeCallArgument){ &empty, sizeof(empty) };
                REQUIRE(ankus_native_call_native_empty_record(one, 1, &empty_result, sizeof(empty_result)) == ANKUS_CALL_OK);
                REQUIRE(offset == 84);
                puts("native ABI values retained");
                return 0;
            }
            """;
        Assert.AreEqual("native ABI values retained\n", await ExecuteNativeCallsAsync(Headers, names, Main));
    }

    /// <summary>
    /// Every envelope check precedes native side effects, and invalid results preserve their original bytes.
    /// </summary>
    [TestMethod]
    public async Task NativeCallBodiesRejectInvalidStorageBeforeInvocation()
    {
        const string Headers = """
            int calls;
            int native_checked(int first, int second) { ++calls; return first + second; }
            void native_empty(void) { ++calls; }
            """;
        const string Main = """
            #include <stdio.h>
            #define REQUIRE(expression) do { if (!(expression)) { fprintf(stderr, "line %d: %s\n", __LINE__, #expression); return 1; } } while (0)
            int main(void)
            {
                int first = 11, second = 31, result = 12345;
                AnkusNativeCallArgument arguments[] = {{ &first, sizeof(first) }, { &second, sizeof(second) }};
                REQUIRE(ankus_native_call_native_checked(arguments, 1, &result, sizeof(result)) == ANKUS_CALL_COUNT);
                REQUIRE(ankus_native_call_native_checked(arguments, 3, &result, sizeof(result)) == ANKUS_CALL_COUNT);
                REQUIRE(ankus_native_call_native_checked(NULL, 2, &result, sizeof(result)) == ANKUS_CALL_ARGUMENTS);
                REQUIRE(ankus_native_call_native_checked(arguments, 2, NULL, sizeof(result)) == ANKUS_CALL_RESULT);
                REQUIRE(ankus_native_call_native_checked(arguments, 2, &result, sizeof(result) - 1) == ANKUS_CALL_RESULT);
                REQUIRE(ankus_native_call_native_checked(arguments, 2, &result, sizeof(result) + 1) == ANKUS_CALL_RESULT);
                arguments[1].data = NULL;
                REQUIRE(ankus_native_call_native_checked(arguments, 2, &result, sizeof(result)) == ANKUS_CALL_STORAGE);
                arguments[1].data = &second;
                arguments[1].size--;
                REQUIRE(ankus_native_call_native_checked(arguments, 2, &result, sizeof(result)) == ANKUS_CALL_STORAGE);
                arguments[1].size += 2;
                REQUIRE(ankus_native_call_native_checked(arguments, 2, &result, sizeof(result)) == ANKUS_CALL_STORAGE);
                _Alignas(int) unsigned char misaligned[sizeof(int) + 1];
                arguments[1] = (AnkusNativeCallArgument){ misaligned + 1, sizeof(int) };
                REQUIRE(ankus_native_call_native_checked(arguments, 2, &result, sizeof(result)) == ANKUS_CALL_ALIGNMENT);
                REQUIRE(ankus_native_call_native_empty(NULL, 0, &result, 0) == ANKUS_CALL_RESULT);
                REQUIRE(ankus_native_call_native_empty(NULL, 0, NULL, 1) == ANKUS_CALL_RESULT);
                REQUIRE(calls == 0 && result == 12345);
                arguments[1] = (AnkusNativeCallArgument){ &second, sizeof(second) };
                REQUIRE(ankus_native_call_native_checked(arguments, 2, &result, sizeof(result)) == ANKUS_CALL_OK);
                REQUIRE(calls == 1 && result == 42);
                REQUIRE(ankus_native_call_native_empty(NULL, 0, NULL, 0) == ANKUS_CALL_OK);
                REQUIRE(calls == 2);
                puts("invalid storage rejected before effects");
                return 0;
            }
            """;
        Assert.AreEqual("invalid storage rejected before effects\n",
            await ExecuteNativeCallsAsync(Headers, ["native_checked", "native_empty"], Main));
    }

    /// <summary>
    /// An entirely native non-local error bypasses result publication and permits a subsequent successful native call.
    /// </summary>
    [TestMethod]
    public async Task NativeCallBodiesRetainResultUntilNativeReturn()
    {
        const string Headers = """
            #include <setjmp.h>
            jmp_buf recovery;
            int calls;
            int native_fail(int value) { ++calls; if (value == 0) longjmp(recovery, 1); return value + 17; }
            _Noreturn void native_stop(void) { longjmp(recovery, 2); }
            """;
        const string Main = """
            #include <stdio.h>
            #define REQUIRE(expression) do { if (!(expression)) { fprintf(stderr, "line %d: %s\n", __LINE__, #expression); return 1; } } while (0)
            int main(void)
            {
                int value = 0;
                static int result = 12345;
                AnkusNativeCallArgument arguments[] = {{ &value, sizeof(value) }};
                int error = setjmp(recovery);
                if (error == 0) { ankus_native_call_native_fail(arguments, 1, &result, sizeof(result)); return 2; }
                REQUIRE(error == 1 && calls == 1 && result == 12345);
                value = 25;
                REQUIRE(ankus_native_call_native_fail(arguments, 1, &result, sizeof(result)) == ANKUS_CALL_OK);
                REQUIRE(calls == 2 && result == 42);
                error = setjmp(recovery);
                if (error == 0) { ankus_native_call_native_stop(NULL, 0, NULL, 0); return 3; }
                REQUIRE(error == 2 && result == 42);
                puts("native unwind preserves result and recovery");
                return 0;
            }
            """;
        Assert.AreEqual("native unwind preserves result and recovery\n",
            await ExecuteNativeCallsAsync(Headers, ["native_fail", "native_stop"], Main));
    }

    private async Task<string> ExecuteNativeCallsAsync(string headers, string[] names, string main)
        => await ExecuteNativeCallsAsync(headers, [.. names.Select(static name => new NativeHeaderRequest(name, name, true))], null, main);

    private async Task<string> ExecuteNativeCallsAsync(string headers, NativeHeaderRequest[] requests, string[]? names, string main, bool nativeCompiler = false,
        string? nativeHeaders = null, int major = 18)
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-native-calls-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(headers, requests, directory, major);
            string file = Path.Join(directory, "calls.c");
            string compilerHeaders = "#define PG_VERSION_NUM " + (major * 10000 + 6).ToString(CultureInfo.InvariantCulture) + "\n" + (nativeHeaders ?? headers);
            string source = names is null ? NativeBindingCallSource.Generate(records, compilerHeaders)
                : NativeBindingCallSource.Generate(records, compilerHeaders, names);
            await File.WriteAllTextAsync(file, source + "\n" + main, context.CancellationToken);
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "calls.exe" : "calls");
            string compiler = OperatingSystem.IsWindows() && nativeCompiler ? "cl.exe" : await HostCompilers.ClangAsync();
            string[] compile = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/O2", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-O2", file, "-o", executable];
            await RunAsync(compiler, compile, directory);
            return (await RunAsync(executable, [], directory)).ReplaceLineEndings("\n");
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    private async Task<NativeHeaderRecords> CollectCallRecordsAsync(string headers, NativeHeaderRequest[] requests, string directory, int major = 18)
    {
        string compilerHeaders = "#define PG_VERSION_NUM " + (major * 10000 + 6).ToString(CultureInfo.InvariantCulture) + "\n" + headers;
        NativeRecordGraph graph = await CollectMeasuredRecordsAsync(compilerHeaders, requests, directory, major);
        using JsonDocument ast = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Join(directory, "records.ast.json"), context.CancellationToken));
        var catalog = new NativeHeaderCatalog(graph.Target, NativeBindingHeaderParser.Read(ast.RootElement, requests));
        return new(catalog, graph);
    }
}
