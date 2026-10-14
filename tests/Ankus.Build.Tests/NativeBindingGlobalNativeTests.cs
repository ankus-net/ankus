namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Executed C global bodies preserve native values, padding, qualifiers, pointer identity and failed-write state.
    /// </summary>
    [TestMethod]
    public async Task NativeGlobalBodiesPreserveValuesAndRejectInvalidFrames()
    {
        const string Headers = """
            #include <stdint.h>
            typedef struct { unsigned long long bits; int count; unsigned char marker; } State;
            typedef struct { volatile unsigned long long version; int count; } ObservedState;
            typedef union { unsigned long long bits; double number; } Representation;
            typedef struct { const int identity; int count; } ImmutableState;
            struct Opaque;
            extern int native_value;
            extern const int native_constant;
            extern volatile unsigned long long native_observed;
            extern const int *native_address;
            extern int * const native_fixed_address;
            extern State native_state;
            extern ObservedState native_observed_state;
            extern Representation native_union;
            extern ImmutableState native_immutable;
            extern int native_array[2][3];
            extern volatile int native_observed_array[2][3];
            extern int native_unbounded[];
            extern struct Opaque native_opaque;
            extern int (*native_callback)(int);
            """;
        string[] names = ["native_constant", "native_observed", "native_address", "native_fixed_address", "native_state",
            "native_observed_state", "native_union", "native_immutable", "native_array", "native_observed_array",
            "native_unbounded", "native_opaque", "native_callback"];
        NativeHeaderRequest[] requests = [new("public_value", "native_value", false),
            .. names.Select(static name => new NativeHeaderRequest(name, name, false))];
        string directory = Path.Join(Path.GetTempPath(), $"ankus-global-native-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers, requests, directory);
            IReadOnlyList<NativeBindingGlobalContract> globals = NativeBindingGlobalModel.Select(records, [.. requests.Select(static request => request.Name)]);
            var accesses = new List<NativeBindingGlobalAccess>();
            foreach (NativeBindingGlobalContract global in globals)
            {
                accesses.Add(new(global.Name, NativeBindingGlobalOperation.Address));
                if (global.IsComplete)
                {
                    accesses.Add(new(global.Name, NativeBindingGlobalOperation.Read));
                }

                if (global.CanWrite)
                {
                    accesses.Add(new(global.Name, NativeBindingGlobalOperation.Write));
                }
            }

            string source = NativeBindingGlobalSource.Generate(records, "#define PG_VERSION_NUM 180006\n" + Headers, accesses);
            Assert.AreEqual(source, NativeBindingGlobalSource.Generate(records, "#define PG_VERSION_NUM 180006\n" + Headers, [.. Enumerable.Reverse(accesses)]));
            const string Main = """
                #include <stdio.h>
                #define REQUIRE(expression) do { if (!(expression)) { fprintf(stderr, "line %d: %s\n", __LINE__, #expression); return 1; } } while (0)
                int native_value = -41;
                const int native_constant = 73;
                volatile unsigned long long native_observed = 0xfedcba9876543210ULL;
                const int *native_address = &native_constant;
                int * const native_fixed_address = &native_value;
                State native_state;
                ObservedState native_observed_state = { 0x8123456789abcdefULL, -7 };
                Representation native_union = { 0xfff800000000002aULL };
                ImmutableState native_immutable = { 19, -3 };
                int native_array[2][3] = {{1, -2, 3}, {-4, 5, -6}};
                volatile int native_observed_array[2][3] = {{7, -8, 9}, {-10, 11, -12}};
                int native_unbounded[3] = {13, 14, 15};
                struct Opaque { int value; };
                struct Opaque native_opaque = {29};
                static int first_callback(int value) { return value + 7; }
                static int second_callback(int value) { return value * 3; }
                int (*native_callback)(int) = first_callback;
                int main(void)
                {
                    int value = 0;
                    REQUIRE(ankus_native_global_read_public_value(NULL, 0, &value, sizeof(value)) == ANKUS_CALL_OK && value == -41);
                    value = 123;
                    AnkusNativeCallArgument argument = { &value, sizeof(value) };
                    REQUIRE(ankus_native_global_write_public_value(&argument, 1, NULL, 0) == ANKUS_CALL_OK && native_value == 123);
                    REQUIRE(ankus_native_global_read_native_constant(NULL, 0, &value, sizeof(value)) == ANKUS_CALL_OK && value == 73);
                    uintptr_t address = 0;
                    REQUIRE(ankus_native_global_address_public_value(NULL, 0, &address, sizeof(address)) == ANKUS_CALL_OK);
                    REQUIRE(address == (uintptr_t)&native_value);
                    REQUIRE(ankus_native_global_address_native_unbounded(NULL, 0, &address, sizeof(address)) == ANKUS_CALL_OK);
                    REQUIRE(address == (uintptr_t)native_unbounded);
                    REQUIRE(ankus_native_global_address_native_opaque(NULL, 0, &address, sizeof(address)) == ANKUS_CALL_OK);
                    REQUIRE(address == (uintptr_t)&native_opaque);
                    const int *target = NULL;
                    REQUIRE(ankus_native_global_read_native_address(NULL, 0, &target, sizeof(target)) == ANKUS_CALL_OK && target == &native_constant);
                    target = &native_value;
                    argument = (AnkusNativeCallArgument){ &target, sizeof(target) };
                    REQUIRE(ankus_native_global_write_native_address(&argument, 1, NULL, 0) == ANKUS_CALL_OK && native_address == &native_value);
                    REQUIRE(ankus_native_global_read_native_fixed_address(NULL, 0, &target, sizeof(target)) == ANKUS_CALL_OK && target == &native_value);
                    unsigned long long bits = 0;
                    REQUIRE(ankus_native_global_read_native_observed(NULL, 0, &bits, sizeof(bits)) == ANKUS_CALL_OK && bits == 0xfedcba9876543210ULL);
                    bits = 0x8000000000000001ULL;
                    argument = (AnkusNativeCallArgument){ &bits, sizeof(bits) };
                    REQUIRE(ankus_native_global_write_native_observed(&argument, 1, NULL, 0) == ANKUS_CALL_OK && native_observed == bits);
                    native_observed = 19;
                    REQUIRE(ankus_native_global_read_native_observed(NULL, 0, &bits, sizeof(bits)) == ANKUS_CALL_OK && bits == 19);
                    memset(&native_state, 0xa5, sizeof(native_state));
                    native_state.bits = 0x8123456789abcdefULL;
                    native_state.count = -57;
                    native_state.marker = 199;
                    State state;
                    REQUIRE(ankus_native_global_read_native_state(NULL, 0, &state, sizeof(state)) == ANKUS_CALL_OK);
                    REQUIRE(memcmp(&state, &native_state, sizeof(state)) == 0);
                    state.bits = 0xfedcba9876543210ULL;
                    state.count = 81;
                    argument = (AnkusNativeCallArgument){ &state, sizeof(state) };
                    REQUIRE(ankus_native_global_write_native_state(&argument, 1, NULL, 0) == ANKUS_CALL_OK);
                    REQUIRE(memcmp(&state, &native_state, sizeof(state)) == 0);
                    REQUIRE(ankus_native_global_write_native_state(&argument, 0, NULL, 0) == ANKUS_CALL_COUNT);
                    REQUIRE(ankus_native_global_write_native_state(NULL, 1, NULL, 0) == ANKUS_CALL_ARGUMENTS);
                    REQUIRE(ankus_native_global_write_native_state(&argument, 1, &value, sizeof(value)) == ANKUS_CALL_RESULT);
                    argument.size--;
                    REQUIRE(ankus_native_global_write_native_state(&argument, 1, NULL, 0) == ANKUS_CALL_STORAGE);
                    argument = (AnkusNativeCallArgument){ NULL, sizeof(state) };
                    REQUIRE(ankus_native_global_write_native_state(&argument, 1, NULL, 0) == ANKUS_CALL_STORAGE);
                    unsigned char misaligned[sizeof(State) + _Alignof(State)];
                    uintptr_t aligned = ((uintptr_t)misaligned + _Alignof(State) - 1) & ~(uintptr_t)(_Alignof(State) - 1);
                    argument = (AnkusNativeCallArgument){ (void *)(aligned + 1), sizeof(state) };
                    REQUIRE(ankus_native_global_write_native_state(&argument, 1, NULL, 0) == ANKUS_CALL_ALIGNMENT);
                    REQUIRE(memcmp(&state, &native_state, sizeof(state)) == 0);
                    REQUIRE(ankus_native_global_read_native_state(NULL, 1, &state, sizeof(state)) == ANKUS_CALL_COUNT);
                    REQUIRE(ankus_native_global_read_native_state(NULL, 0, NULL, sizeof(state)) == ANKUS_CALL_RESULT);
                    REQUIRE(ankus_native_global_read_native_state(NULL, 0, &state, sizeof(state) - 1) == ANKUS_CALL_RESULT);
                    REQUIRE(memcmp(&state, &native_state, sizeof(state)) == 0);
                    ObservedState observed = {0};
                    REQUIRE(ankus_native_global_read_native_observed_state(NULL, 0, &observed, sizeof(observed)) == ANKUS_CALL_OK);
                    REQUIRE(observed.version == 0x8123456789abcdefULL && observed.count == -7);
                    observed.version = 0xfedcba9876543210ULL;
                    observed.count = 38;
                    argument = (AnkusNativeCallArgument){ &observed, sizeof(observed) };
                    REQUIRE(ankus_native_global_write_native_observed_state(&argument, 1, NULL, 0) == ANKUS_CALL_OK);
                    REQUIRE(native_observed_state.version == observed.version && native_observed_state.count == 38);
                    Representation representation;
                    REQUIRE(ankus_native_global_read_native_union(NULL, 0, &representation, sizeof(representation)) == ANKUS_CALL_OK);
                    REQUIRE(representation.bits == 0xfff800000000002aULL);
                    representation.bits = 0x8000000000000000ULL;
                    argument = (AnkusNativeCallArgument){ &representation, sizeof(representation) };
                    REQUIRE(ankus_native_global_write_native_union(&argument, 1, NULL, 0) == ANKUS_CALL_OK && native_union.bits == representation.bits);
                    unsigned char immutable[sizeof(ImmutableState)];
                    REQUIRE(ankus_native_global_read_native_immutable(NULL, 0, immutable, sizeof(immutable)) == ANKUS_CALL_OK);
                    REQUIRE(memcmp(immutable, &native_immutable, sizeof(immutable)) == 0);
                    int array[2][3] = {{0}};
                    REQUIRE(ankus_native_global_read_native_array(NULL, 0, array, sizeof(array)) == ANKUS_CALL_OK);
                    REQUIRE(array[0][0] == 1 && array[0][1] == -2 && array[0][2] == 3 && array[1][0] == -4 && array[1][1] == 5 && array[1][2] == -6);
                    array[0][0] = -31;
                    array[1][2] = 47;
                    argument = (AnkusNativeCallArgument){ array, sizeof(array) };
                    REQUIRE(ankus_native_global_write_native_array(&argument, 1, NULL, 0) == ANKUS_CALL_OK && memcmp(array, native_array, sizeof(array)) == 0);
                    REQUIRE(ankus_native_global_read_native_observed_array(NULL, 0, array, sizeof(array)) == ANKUS_CALL_OK);
                    REQUIRE(array[0][0] == 7 && array[0][1] == -8 && array[0][2] == 9 && array[1][0] == -10 && array[1][1] == 11 && array[1][2] == -12);
                    array[0][0] = -51;
                    array[1][2] = 67;
                    REQUIRE(ankus_native_global_write_native_observed_array(&argument, 1, NULL, 0) == ANKUS_CALL_OK);
                    REQUIRE(native_observed_array[0][0] == -51 && native_observed_array[0][1] == -8 && native_observed_array[0][2] == 9);
                    REQUIRE(native_observed_array[1][0] == -10 && native_observed_array[1][1] == 11 && native_observed_array[1][2] == 67);
                    int (*callback)(int) = NULL;
                    REQUIRE(ankus_native_global_read_native_callback(NULL, 0, &callback, sizeof(callback)) == ANKUS_CALL_OK && callback(5) == 12);
                    callback = second_callback;
                    argument = (AnkusNativeCallArgument){ &callback, sizeof(callback) };
                    REQUIRE(ankus_native_global_write_native_callback(&argument, 1, NULL, 0) == ANKUS_CALL_OK && native_callback(5) == 15);
                    puts("native global values, identity, qualifiers and failed-write preservation");
                    return 0;
                }
                """;
            string file = Path.Join(directory, "globals.c");
            await File.WriteAllTextAsync(file, source + Main, context.CancellationToken);
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "globals.exe" : "globals");
            string compiler = OperatingSystem.IsWindows() ? "cl.exe" : "clang";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/O2", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-O2", file, "-o", executable];
            await RunAsync(compiler, arguments, directory);
            Assert.AreEqual("native global values, identity, qualifiers and failed-write preservation\n",
                (await RunAsync(executable, [], directory)).ReplaceLineEndings("\n"));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
