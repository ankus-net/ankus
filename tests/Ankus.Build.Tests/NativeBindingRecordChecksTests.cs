namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Header implementation bodies may depend on the backend without becoming executable layout checks.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task NativeRecordChecksDoNotLinkUnusedBackendImplementations(bool nativeCompiler)
    {
        const string Headers = """
            extern int backend_only(int value);
            typedef struct Entry { unsigned int value : 5; } Entry;
            extern Entry current;
            int header_body(int value) { return backend_only(value) + 1; }
            int (*header_callback)(int) = backend_only;
            int (*const header_constant)(int) = backend_only;
            """;
        NativeHeaderRequest[] requests = [new("current", "current", false), new("header_body", "header_body", true)];
        await VerifyRecordChecksAsync(Headers, Headers, requests, compile: true, execute: true, nativeCompiler: nativeCompiler);
        string changed = Headers.Replace("value : 5", "value : 4", StringComparison.Ordinal);
        string diagnostics = await VerifyRecordChecksAsync(Headers, changed, requests, compile: true, execute: false, nativeCompiler: nativeCompiler);
        Assert.Contains("Entry.value", diagnostics);
    }

    /// <summary>
    /// Discarding unused header bodies must not hide missing dependencies in executable probe code.
    /// </summary>
    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task NativeRecordChecksRejectReachableBackendDependencies(bool nativeCompiler)
    {
        const string Headers = """
            extern int backend_only(int value);
            typedef struct Entry { unsigned int value : 5; } Entry;
            extern Entry current;
            int header_body(int value) { return backend_only(value) + 1; }
            """;
        const string Reachable = "#define ankus_native_record_check() (header_body(7) ? \"backend\" : 0)\n";
        string diagnostics = await VerifyRecordChecksAsync(Headers, Headers, [new("current", "current", false)],
            compile: false, execute: false, nativeCompiler: nativeCompiler, beforeEntryPoint: Reachable);
        Assert.Contains("backend_only", diagnostics);
    }

    /// <summary>
    /// Removing backend unwind metadata preserves real exception dispatch through the standalone probe.
    /// </summary>
    [TestMethod]
    [OSCondition(OperatingSystems.Windows)]
    [DataRow(true)]
    [DataRow(false)]
    public async Task NativeRecordChecksPreserveWindowsExceptionDispatch(bool nativeCompiler)
    {
        const string Headers = """
            extern int backend_only(int value);
            typedef struct Entry { unsigned int value : 5; } Entry;
            extern Entry current;
            int header_body(int value) { return backend_only(value) + 1; }
            """;
        const string Check = """
            #include <windows.h>
            __declspec(noinline) static void raise_probe_error(void)
            {
                RaiseException(0xe0000042, 0, 0, NULL);
            }
            __declspec(noinline) static const char *check_probe_unwind(void)
            {
                int handled = 0;
                __try
                {
                    raise_probe_error();
                }
                __except (GetExceptionCode() == 0xe0000042 ? EXCEPTION_EXECUTE_HANDLER : EXCEPTION_CONTINUE_SEARCH)
                {
                    handled = 1;
                }
                return handled ? ankus_native_record_check() : "Probe exception was not handled.";
            }
            #define ankus_native_record_check check_probe_unwind

            """;
        await VerifyRecordChecksAsync(Headers, Headers, [new("current", "current", false)],
            compile: true, execute: true, nativeCompiler: nativeCompiler, beforeEntryPoint: Check);
    }

    /// <summary>
    /// Anonymous declaration anchors remove expression qualifiers while retaining the actual root and member contracts.
    /// </summary>
    [TestMethod]
    public async Task NativeRecordChecksPreserveQualifiedAnonymousDeclarations()
    {
        const string Headers = """
            typedef const struct {
                int value;
                const struct { short flag; } nested;
                enum { Absent = -7, Present = 19 } kind;
                const int *conflict;
                const char * const *names;
                volatile int *trace;
            } Entry;
            typedef const struct Opaque Opaque;
            extern volatile Entry current;
            extern Entry *entries;
            extern Opaque *opaque;
            extern void consume(Entry entry, const int count, const int * const address, Opaque *handle, Opaque value);
            """;
        NativeHeaderRequest[] requests = [new("current", "current", false), new("entries", "entries", false),
            new("opaque", "opaque", false), new("consume", "consume", true)];
        await VerifyRecordChecksAsync(Headers, Headers, requests, compile: true, execute: true, nativeCompiler: false);
        await VerifyRecordChecksAsync(Headers, Headers, requests, compile: true, execute: true,
            compilerOverride: OperatingSystem.IsWindows() ? "cl.exe" : "cc");
        string changed = Headers.Replace("extern volatile Entry", "extern Entry", StringComparison.Ordinal);
        string diagnostics = await VerifyRecordChecksAsync(Headers, changed, requests, compile: false, execute: false,
            compilerOverride: OperatingSystem.IsWindows() ? "cl.exe" : "cc");
        Assert.Contains("Native record contract changed: root type current", diagnostics);
        string changedPointee = Headers.Replace("const int *conflict", "int *conflict", StringComparison.Ordinal);
        diagnostics = await VerifyRecordChecksAsync(Headers, changedPointee, requests, compile: false, execute: false,
            compilerOverride: OperatingSystem.IsWindows() ? "cl.exe" : "cc");
        Assert.Contains("Native record contract changed: member type declaration 0.conflict", diagnostics);
    }

    /// <summary>
    /// Actual compiler objects retain packed storage, recursive pointers, qualified members, enums and bitfields.
    /// </summary>
    [TestMethod]
    public async Task NativeRecordChecksPreserveRecursiveStorage()
    {
        const string Headers = """
            struct Opaque;
            enum Choice { Negative = -7, Positive = 19 };
            typedef const int Matrix[2][3];
            #pragma pack(push, 2)
            typedef struct Entry {
                char lead[2];
                struct Entry *next;
                struct Opaque *opaque;
                Matrix matrix;
                struct { int value; } nested;
                union { int number; float fraction; } data;
                enum Choice choice;
                unsigned int flags : 3;
                signed int delta : 5;
                unsigned int : 0;
                const unsigned int fixed : 3;
                volatile unsigned int changing : 7;
                int (*callback)(const struct Entry *, int);
                char tail[];
            } Entry;
            #pragma pack(pop)
            extern Entry current;
            extern struct { int same; } first;
            extern struct { int same; } second;
            extern void array_call(Matrix values, int (*callback)(int));
            extern int variadic_call(const char *format, ...);
            extern void unspecified_call();
            """;
        NativeHeaderRequest[] requests = [new("current", "current", false), new("first", "first", false), new("second", "second", false),
            new("array_call", "array_call", true), new("variadic_call", "variadic_call", true), new("unspecified_call", "unspecified_call", true)];
        await VerifyRecordChecksAsync(Headers, Headers, requests, compile: true, execute: true);
    }

    /// <summary>
    /// Independent native member declarations reject equal-size substitutions and changed physical storage.
    /// </summary>
    /// <param name="original">The original member declaration or storage attribute.</param>
    /// <param name="changed">The incompatible declaration to compile against the saved graph.</param>
    /// <param name="reason">The compiler assertion identifying the incompatibility.</param>
    [TestMethod]
    [DataRow("int value;", "float value;", "member type Entry.value")]
    [DataRow("int value;", "unsigned int value;", "member type Entry.value")]
    [DataRow("int value;", "const int value;", "member type Entry.value")]
    [DataRow("int values[2][3];", "int values[3][2];", "member type Entry.values")]
    [DataRow("int (*callback)(int);", "int (*callback)(float);", "member type Entry.callback")]
    [DataRow("char lead[2]; int value;", "int value; char lead[2];", "offset Entry.value")]
    [DataRow("#pragma pack(push, 2)", "#pragma pack(push, 1)", "alignment Entry")]
    [DataRow("ChoiceLow = -7", "ChoiceLow = -8", "constant ChoiceLow")]
    public async Task NativeRecordChecksRejectChangedMembers(string original, string changed, string reason)
    {
        const string Headers = """
            enum Choice { ChoiceLow = -7, ChoiceHigh = 19 };
            #pragma pack(push, 2)
            typedef struct Entry {
                char lead[2]; int value;
                int values[2][3];
                int (*callback)(int);
                enum Choice choice;
            } Entry;
            #pragma pack(pop)
            extern Entry current;
            """;
        string actual = Headers.Replace(original, changed, StringComparison.Ordinal);
        Assert.AreNotEqual(Headers, actual);
        string diagnostics = await VerifyRecordChecksAsync(Headers, actual, [new("current", "current", false)], compile: false, execute: false);
        Assert.Contains("Native record contract changed: " + reason, diagnostics);
    }

    /// <summary>
    /// Real native field reads detect bit shifts, widths and signedness even when enclosing storage is unchanged.
    /// </summary>
    /// <param name="actual">An incompatible native bitfield declaration with the same object size.</param>
    [TestMethod]
    [DataRow("unsigned int before : 2; unsigned int value : 5; unsigned int after : 3;")]
    [DataRow("unsigned int before : 3; unsigned int value : 4; unsigned int after : 3;")]
    [DataRow("unsigned int before : 3; unsigned int value : 6; unsigned int after : 3;")]
    [DataRow("unsigned int before : 3; signed int value : 5; unsigned int after : 3;")]
    public async Task NativeRecordChecksRejectChangedBitfields(string actual)
    {
        const string Original = "unsigned int before : 3; unsigned int value : 5; unsigned int after : 3;";
        const string Headers = "typedef struct Entry { " + Original + " } Entry; extern Entry current;";
        string changed = Headers.Replace(Original, actual, StringComparison.Ordinal);
        await VerifyRecordChecksAsync(Headers, changed, [new("current", "current", false)], compile: true, execute: false);
    }

    /// <summary>
    /// Compatible function declarations retain C parameter adjustment on Clang and MSVC.
    /// </summary>
    [TestMethod]
    public async Task NativeRecordChecksRetainCompilerParameterAdjustment()
    {
        const string Headers = "typedef const int Matrix[2][3]; extern void apply(Matrix values, int callback(int));";
        NativeHeaderRequest[] requests = [new("apply", "apply", true)];
        await VerifyRecordChecksAsync(Headers, Headers, requests, compile: true, execute: true, nativeCompiler: false);
        await VerifyRecordChecksAsync(Headers, Headers, requests, compile: true, execute: true);
    }

    /// <summary>
    /// Wide native bitfields retain their highest bit, signed range and neighboring storage in Clang targets.
    /// </summary>
    [TestMethod]
    public async Task NativeRecordChecksPreserveWideBitfields()
    {
        const string Headers = """
            typedef struct Entry {
                unsigned __int128 wide : 127;
                signed __int128 delta : 128;
                const unsigned __int128 fixed : 65;
                _Bool enabled : 1;
            } Entry;
            extern Entry current;
            """;
        await VerifyRecordChecksAsync(Headers, Headers, [new("current", "current", false)], compile: true, execute: true, nativeCompiler: false);
    }

    /// <summary>
    /// Native values wider than the expected comparison integer cannot truncate into an apparently valid bitfield.
    /// </summary>
    /// <param name="sign">The native integer signedness retained while widening the real field.</param>
    [TestMethod]
    [DataRow("unsigned")]
    [DataRow("signed")]
    public async Task NativeRecordChecksRejectWiderNativeBitfields(string sign)
    {
        string headers = "union Entry { " + sign + " long long value : 64; unsigned __int128 alignment; }; extern union Entry current;";
        string changed = headers.Replace("long long value : 64", "__int128 value : 65", StringComparison.Ordinal);
        await VerifyRecordChecksAsync(headers, changed, [new("current", "current", false)], compile: true, execute: false, nativeCompiler: false);
    }

    /// <summary>
    /// A separately exposed anonymous return value still requires an independently addressable native type.
    /// </summary>
    [TestMethod]
    public async Task NativeRecordChecksRejectUnverifiableExposedValues()
    {
        const string Headers = "extern struct { int number; float fraction; } current(void);";
        string directory = Path.Join(Path.GetTempPath(), $"ankus-unnameable-record-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers, [new("current", "current", true)], directory);
            FormatException error = Assert.ThrowsExactly<FormatException>(() => NativeBindingRecordChecks.Generate(records, Headers));
            Assert.Contains("C type anchor for every exposed declaration", error.Message);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Two same-size anonymous declarations cannot silently collapse into a shared native typedef.
    /// </summary>
    [TestMethod]
    public async Task NativeRecordChecksRejectMergedDeclarations()
    {
        const string Headers = """
            typedef struct { int value; } First;
            typedef struct { int value; } Second;
            struct Entry { First first; Second second; };
            extern struct Entry current;
            """;
        string changed = Headers.Replace("typedef struct { int value; } Second;", "typedef First Second;", StringComparison.Ordinal);
        await VerifyRecordChecksAsync(Headers, Headers, [new("current", "current", false)], compile: true, execute: true);
        string diagnostics = await VerifyRecordChecksAsync(Headers, changed, [new("current", "current", false)], compile: false, execute: false);
        Assert.Contains("ankus_record_declaration_", diagnostics);
    }

    /// <summary>
    /// Enum extrema, aligned aliases and Clang's atomic, vector, complex and foreign callback types keep their representations.
    /// </summary>
    [TestMethod]
    public async Task NativeRecordChecksPreserveAnnotatedTypes()
    {
        const string Headers = """
            typedef enum SignedLimit : long long { Minimum = (-9223372036854775807LL - 1), Negative = -1 } SignedLimit;
            typedef enum UnsignedLimit : unsigned long long { Maximum = 18446744073709551615ULL } UnsignedLimit;
            typedef int Wide __attribute__((aligned(16)));
            typedef float Three __attribute__((ext_vector_type(3)));
            typedef void NativeExit(void) __attribute__((noreturn));
            #if defined(_M_X64)
            typedef void (__attribute__((sysv_abi)) *ForeignCallback)(int);
            #elif defined(__x86_64__)
            typedef void (__attribute__((ms_abi)) *ForeignCallback)(int);
            #else
            typedef void (*ForeignCallback)(int);
            #endif
            struct Entry {
                SignedLimit low;
                UnsignedLimit high;
                Wide aligned;
                Three vector;
                _Atomic(unsigned long) counter;
                _Complex double complex;
                NativeExit *exit;
                ForeignCallback foreign;
            };
            extern struct Entry current;
            """;
        await VerifyRecordChecksAsync(Headers, Headers, [new("current", "current", false)], compile: true, execute: true, nativeCompiler: false);
    }

    /// <summary>
    /// The actual compiler's standard variadic argument type must match the measured private Clang implementation.
    /// </summary>
    [TestMethod]
    public async Task NativeRecordChecksPreserveStandardVariadicStorage()
    {
        const string Headers = "#include <stdarg.h>\nstruct Entry { va_list arguments; }; extern struct Entry current; extern void consume(va_list arguments);";
        await VerifyRecordChecksAsync(Headers, Headers, [new("current", "current", false), new("consume", "consume", true)], compile: true, execute: true);
    }

    /// <summary>
    /// Equal enum constants cannot conceal a changed signedness or object width.
    /// </summary>
    /// <param name="underlying">The incompatible native integer representation used by the real enum.</param>
    /// <param name="reason">The compiler assertion that identifies the changed representation.</param>
    [TestMethod]
    [DataRow("int", "enum signedness Choice")]
    [DataRow("unsigned long long", "size Choice")]
    public async Task NativeRecordChecksRejectEnumRepresentationChanges(string underlying, string reason)
    {
        const string Headers = "enum Choice : unsigned int { First = 3, Last = 19 }; extern enum Choice current;";
        string actual = Headers.Replace(": unsigned int", ": " + underlying, StringComparison.Ordinal);
        string diagnostics = await VerifyRecordChecksAsync(Headers, actual, [new("current", "current", false)], compile: false, execute: false, nativeCompiler: false);
        Assert.Contains("Native record contract changed: " + reason, diagnostics);
    }

    /// <summary>
    /// Parameter-level qualification disappears from function compatibility while pointee qualification remains exact.
    /// </summary>
    [TestMethod]
    public async Task NativeRecordChecksPreserveQualifiedFunctionParameters()
    {
        const string Headers = """
            #if defined(_MSC_VER)
            #define restrict __restrict
            #endif
            extern void apply(int *restrict destination, const char *restrict source, volatile int count, const float scale);
            """;
        NativeHeaderRequest[] requests = [new("apply", "apply", true)];
        await VerifyRecordChecksAsync(Headers, Headers, requests, compile: true, execute: true);
        string changed = Headers.Replace("const char *restrict source", "char *restrict source", StringComparison.Ordinal);
        string diagnostics = await VerifyRecordChecksAsync(Headers, changed, requests, compile: false, execute: false);
        Assert.Contains("Native record contract changed: root type apply", diagnostics);
    }

    /// <summary>
    /// A PostgreSQL function-like macro fallback receives a checked signature without discarding its catalog entry.
    /// </summary>
    [TestMethod]
    public async Task NativeRecordChecksPreserveFunctionLikeMacros()
    {
        const string Measured = "extern void pg_spin_delay_impl(void);";
        const string Actual = "#define pg_spin_delay_impl() ((void)0)";
        await VerifyRecordChecksAsync(Measured, Actual, [new("pg_spin_delay_impl", "pg_spin_delay_impl", true)], compile: true, execute: true);
        await VerifyRecordChecksAsync(Measured, "#define pg_spin_delay_impl(required) ((void)(required))",
            [new("pg_spin_delay_impl", "pg_spin_delay_impl", true)], compile: false, execute: false);
    }

    /// <summary>
    /// Canonical and declared alias corruption inside a field cannot hide behind a valid top-level record signature.
    /// </summary>
    /// <param name="mutation">The invalid alias edge to introduce into otherwise compatible C declarations.</param>
    [TestMethod]
    [DataRow("canonical")]
    [DataRow("self")]
    [DataRow("mutual")]
    public async Task NativeRecordChecksRejectFieldAliasCorruption(string mutation)
    {
        const string Headers = "typedef int First; typedef int Second; struct Entry { First first; Second second; }; extern struct Entry current;";
        string directory = Path.Join(Path.GetTempPath(), $"ankus-record-alias-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers, [new("current", "current", false)], directory);
            string expected = NativeBindingRecordChecks.Generate(records, Headers);
            NativeRecordType[] types = [.. records.Graph.Types];
            int first = Array.FindIndex(types, static type => type.Kind == "alias" && type.Name == "First");
            int second = Array.FindIndex(types, static type => type.Kind == "alias" && type.Name == "Second");
            Assert.IsGreaterThanOrEqualTo(0, first);
            Assert.IsGreaterThanOrEqualTo(0, second);
            types[first] = mutation == "canonical" ? types[first] with
            {
                Canonical = first
            }
                : types[first] with
                {
                    Element = mutation == "self" ? first : second
                };
            if (mutation == "mutual")
            {
                types[second] = types[second] with
                {
                    Element = first
                };
            }

            NativeHeaderRecords changed = records with
            {
                Graph = records.Graph with
                {
                    Types = types
                }
            };
            FormatException error = Assert.ThrowsExactly<FormatException>(() => NativeBindingRecordChecks.Generate(changed, Headers));
            Assert.Contains(mutation == "canonical" ? "canonical native type" : "alias or wrapper cycle", error.Message);
            Assert.AreEqual(expected, NativeBindingRecordChecks.Generate(records, Headers));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// C unsigned conversions cannot disguise a contradictory negative or unsigned-maximum constant observation.
    /// </summary>
    /// <param name="underlying">The native enum's exact storage type.</param>
    /// <param name="value">The actual native constant expression.</param>
    /// <param name="corrupted">The incompatible observed value that shares the same low 64 bits.</param>
    [TestMethod]
    [DataRow("long long", "-1LL", "18446744073709551615")]
    [DataRow("unsigned long long", "18446744073709551615ULL", "-1")]
    public async Task NativeRecordChecksRejectWrappedEnumConstants(string underlying, string value, string corrupted)
    {
        string headers = "enum Wide : " + underlying + " { LimitValue = " + value + " }; extern enum Wide current;";
        string diagnostics = await VerifyRecordChecksAsync(headers, headers, [new("current", "current", false)],
            compile: false, execute: false, nativeCompiler: false, mutate: records =>
            {
                NativeRecordDeclaration[] declarations = [.. records.Graph.Declarations];
                int index = Array.FindIndex(declarations, static declaration => declaration.Name == "Wide");
                Assert.IsGreaterThanOrEqualTo(0, index);
                declarations[index] = declarations[index] with
                {
                    EnumValues = [new("LimitValue", corrupted)]
                };
                return records with
                {
                    Graph = records.Graph with
                    {
                        Declarations = declarations
                    }
                };
            });
        Assert.Contains("Native record contract changed: constant LimitValue", diagnostics);
    }

    /// <summary>
    /// Compiles and executes generated checks against independently supplied declarations in the production C compiler.
    /// </summary>
    private async Task<string> VerifyRecordChecksAsync(string measured, string actual, NativeHeaderRequest[] requests, bool compile, bool execute, bool nativeCompiler = true,
        Func<NativeHeaderRecords, NativeHeaderRecords>? mutate = null, string? compilerOverride = null, string beforeEntryPoint = "")
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-record-checks-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(measured, requests, directory);
            if (mutate is not null)
            {
                records = mutate(records);
            }

            string source = NativeBindingRecordChecks.Generate(records, "#define PG_VERSION_NUM 180006\n" + actual);
            string file = Path.Join(directory, "checks.c");
            await File.WriteAllTextAsync(file, source + beforeEntryPoint + NativeBindingRecordChecks.ExecutableEntryPoint, context.CancellationToken);
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "checks.exe" : "checks");
            string compiler = compilerOverride ?? (OperatingSystem.IsWindows() ? nativeCompiler ? "cl.exe" : "clang-cl.exe" : "clang");
            List<string> arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/O2", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-O2", file, "-o", executable];
            NativeBindingLayoutCommand.AddProbeLinkOptions(arguments);
            if (!compile)
            {
                InvalidOperationException error = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                    NativeBindingLayoutCommand.CompileAndLinkProbeAsync(compiler, arguments, executable, directory, context.CancellationToken));
                return error.Message;
            }

            await NativeBindingLayoutCommand.CompileAndLinkProbeAsync(compiler, arguments, executable, directory, context.CancellationToken);
            return await RunAsync(executable, [], directory, expectSuccess: execute);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
