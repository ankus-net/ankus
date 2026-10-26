namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Inline helpers whose bodies reach only other such helpers, memory functions, builtins or assertion aborts cannot
    /// raise an error, while an outside call, a function pointer, integer division, inline assembly or recursion can (#115).
    /// </summary>
    [TestMethod]
    public async Task HelpersThatCannotRaiseAreFound()
    {
        const string Headers = """
            #include <string.h>
            extern void may_raise(void);
            extern void ExceptionalCondition(const char *condition, const char *file, int line) __attribute__((noreturn));
            static inline int pure_add(int left, int right) { return left + right; }
            static inline int nested(int value) { return pure_add(value, 1) * 2; }
            static inline int remainder_literal(int value) { return value % 8; }
            static inline unsigned long divides_size(unsigned long value) { return value / sizeof(long); }
            static inline double divides_floating(double left, double right) { return left / right; }
            static inline void copies(void *target, const void *source) { memcpy(target, source, 4); }
            static inline int asserts(int value) { if (value == 0) ExceptionalCondition("value != 0", "header.h", 1); return value; }
            static inline int counts_bits(unsigned value) { return __builtin_popcount(value); }
            static inline int calls_outside(int value) { may_raise(); return value; }
            static inline int nested_outside(int value) { return calls_outside(value); }
            static inline int divides(int left, int right) { return left / right; }
            static inline int divides_assign(int left, int right) { left /= right; return left; }
            static inline int remainder_variable(int left, int right) { return left % right; }
            static inline int through_pointer(int (*target)(int), int value) { return target(value); }
            static inline int recursive(int value) { return value > 0 ? recursive(value - 1) : 0; }
            static inline int assembly(int value) { __asm__ volatile("" ::: "memory"); return value; }
            int defined_plain(int value) { return value; }
            int (*pure_add_reference)(int, int) = pure_add;
            int (*nested_reference)(int) = nested;
            int (*remainder_literal_reference)(int) = remainder_literal;
            unsigned long (*divides_size_reference)(unsigned long) = divides_size;
            double (*divides_floating_reference)(double, double) = divides_floating;
            void (*copies_reference)(void *, const void *) = copies;
            int (*asserts_reference)(int) = asserts;
            int (*counts_bits_reference)(unsigned) = counts_bits;
            int (*calls_outside_reference)(int) = calls_outside;
            int (*nested_outside_reference)(int) = nested_outside;
            int (*divides_reference)(int, int) = divides;
            int (*divides_assign_reference)(int, int) = divides_assign;
            int (*remainder_variable_reference)(int, int) = remainder_variable;
            int (*through_pointer_reference)(int (*)(int), int) = through_pointer;
            int (*recursive_reference)(int) = recursive;
            int (*assembly_reference)(int) = assembly;
            """;
        string[] safe = ["pure_add", "nested", "remainder_literal", "divides_size", "divides_floating", "copies", "asserts", "counts_bits"];
        string[] raising = ["may_raise", "calls_outside", "nested_outside", "divides", "divides_assign", "remainder_variable",
            "through_pointer", "recursive", "assembly", "defined_plain"];
        string directory = Path.Join(Path.GetTempPath(), $"ankus-raise-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRecords records = await CollectCallRecordsAsync(Headers, [new("pure_add", "pure_add", true)], directory);
            string file = Path.Join(directory, "bodies.c");
            string ast = Path.Join(directory, "bodies.ast");
            await File.WriteAllTextAsync(file, Headers, context.CancellationToken);
            string compiler = await HostCompilers.ClangAsync();
            string[] frontend = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Zs", "-Xclang", "-emit-pch", "-Xclang", "-o", "-Xclang", ast, file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-fsyntax-only", "-Xclang", "-emit-pch", "-Xclang", "-o", "-Xclang", ast, file];
            await NativeBindingHeaderCommand.CompileAsync(compiler, frontend, Path.Join(directory, "bodies.txt"), directory,
                context.CancellationToken, inspectBodies: true);
            string library = await NativeBindingRecordCommand.FindLibraryAsync(compiler, records.Headers.Target.ClangMajor, context.CancellationToken);
            IReadOnlySet<string> found = await NativeBindingRaiseAnalysis.InspectAsync(ast, library, [.. safe, .. raising], directory,
                context.CancellationToken);
            Assert.AreSequenceEqual<string>(safe.Order(StringComparer.Ordinal), found.Order(StringComparer.Ordinal));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// A call that cannot raise runs its body without the native guard after the same binding validation, while a call
    /// that can raise still goes through the guard, which here refuses it (#115).
    /// </summary>
    [TestMethod]
    public async Task CallsThatCannotRaiseSkipTheGuard()
    {
        const string Headers = """
            static inline int pure_add(int left, int right) { return left + right; }
            int (*pure_add_reference)(int, int) = pure_add;
            int guarded_add(int left, int right) { return left + right; }
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    scope.RejectCall = true;
                    int direct = NativeMethods.pure_add(40, 2);
                    int invocations = scope.Invocations;
                    int rejected = 0;
                    try { NativeMethods.guarded_add(1, 2); }
                    catch (PgException error) when (error.SqlState == "22023") { rejected++; }
                    scope.RejectCall = false;
                    int guarded = NativeMethods.guarded_add(20, 3);
                    return [direct, invocations, rejected, guarded, scope.Invocations, scope.Validations];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([42, 0, 1, 23, 2, 3], await ExecuteManagedCallsAsync(Headers, ["pure_add", "guarded_add"], Harness,
            link: records => records with
            {
                Headers = records.Headers with
                {
                    Symbols = records.Headers.Symbols.ToDictionary(static pair => pair.Key,
                        static pair => pair.Key == "pure_add" ? pair.Value with { CannotRaise = true } : pair.Value, StringComparer.Ordinal),
                },
            }));
    }
}
