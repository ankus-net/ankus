using System.Globalization;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Adjusted array and function parameters retain exact pointer values and can execute native callbacks.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallsPreserveAdjustedArguments()
    {
        const string Headers = """
            typedef const int Number;
            typedef int Operation(int);
            int triple(int value) { return value * 3; }
            Operation *operation(void) { return triple; }
            int adjusted(Number values[3], Operation callback) { return callback(values[0]) + values[1] - values[2]; }
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    int* values = stackalloc int[] { 17, -9, 11 };
                    int result = NativeMethods.adjusted(values, NativeMethods.operation());
                    return [result, values[0], values[1], values[2], scope.Invocations];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([31, 17, -9, 11, 2], await ExecuteManagedCallsAsync(Headers, ["operation", "adjusted"], Harness));
    }

    /// <summary>
    /// C long and unsigned long arguments accept 64-bit values on every target. Where C long has 32 bits, as on
    /// Windows, a value it cannot hold throws instead of being truncated.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallsAcceptLongArgumentsForCLong()
    {
        const string Headers = """
            long echo_long(long value) { return value; }
            unsigned long echo_unsigned(unsigned long value) { return value; }
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    long small = -7;
                    ulong positive = 9;
                    long large = 5_000_000_000;
                    long outcome;
                    try
                    {
                        outcome = NativeMethods.echo_long(large) == large ? 1 : 0;
                    }
                    catch (System.ArgumentOutOfRangeException error) when (error.ParamName == "value")
                    {
                        outcome = 2;
                    }

                    return [NativeMethods.echo_long(small), (long)NativeMethods.echo_unsigned(positive), outcome];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([-7, 9, OperatingSystem.IsWindows() ? 2 : 1],
            await ExecuteManagedCallsAsync(Headers, ["echo_long", "echo_unsigned"], Harness));
    }

    /// <summary>
    /// True void requires no frame while zero-size records retain distinct logical values and nonnull native addresses.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallsPreserveEmptyValues()
    {
        const string Headers = """
            typedef struct {} First;
            typedef struct {} Second;
            int observed;
            void reset(void) { observed = 0; }
            First first(void) { First value; ++observed; return value; }
            Second second(First value) { Second result; (void)value; observed *= 7; return result; }
            int finish(Second value) { (void)value; return observed; }
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    NativeMethods.reset();
                    int result = NativeMethods.finish(NativeMethods.second(NativeMethods.first()));
                    return [result, typeof(NativeMethods).GetMethod("first")!.ReturnType != typeof(NativeMethods).GetMethod("second")!.ReturnType ? 1 : 0,
                        scope.Validations, scope.Invocations, NativeCallTestBridge.Allocator.Allocations];
                }
            }
            """;
        // Clang retains zero native bytes on Unix and the MSVC ABI's nonzero empty-record storage on Windows.
        Assert.AreSequenceEqual<long>([7, 1, 4, 4, 0], await ExecuteManagedCallsAsync(Headers, ["reset", "first", "second", "finish"], Harness, nativeCompiler: false));
    }

    /// <summary>
    /// Descriptor storage participates in the stack limit even when individual native argument objects are small.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallsBoundManyArguments()
    {
        const int Count = 300;
        string[] names = [.. Enumerable.Range(0, Count).Select(static index => "v" + index.ToString(CultureInfo.InvariantCulture))];
        string headers = "int total(" + string.Join(", ", names.Select(static name => "int " + name)) + ") { return " + string.Join(" + ", names) + "; }";
        string harness = """
            public static class BindingAssertions
            {
                public static long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    int result = NativeMethods.total(__VALUES__);
                    return [result, scope.Invocations, NativeCallTestBridge.Allocator.Allocations,
                        NativeCallTestBridge.Allocator.Releases, NativeCallTestBridge.Allocator.Live];
                }
            }
            """;
        harness = harness.Replace("__VALUES__", string.Join(", ", Enumerable.Range(1, Count)), StringComparison.Ordinal);
        Assert.AreSequenceEqual<long>([45150, 1, 1, 1, 0], await ExecuteManagedCallsAsync(headers, ["total"], harness));
    }

    /// <summary>
    /// Native functions with inherited CLR member names remain callable without unnecessary hiding diagnostics.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallsPreserveMemberNames()
    {
        const string Headers = """
            int Equals(int value) { return value + 1; }
            int GetHashCode(void) { return 41; }
            int GetType(int value) { return value + 2; }
            void Finalize(void) { }
            int GetNativeBody_Equals(void) { return 71; }
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    NativeMethods.Native_Finalize();
                    return [NativeMethods.Equals(13), NativeMethods.GetHashCode(), NativeMethods.GetType(19), NativeMethods.GetNativeBody_Equals()];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([14, 41, 21, 71], await ExecuteManagedCallsAsync(Headers, ["Equals", "GetHashCode", "GetType", "Finalize", "GetNativeBody_Equals"], Harness));
    }
}
