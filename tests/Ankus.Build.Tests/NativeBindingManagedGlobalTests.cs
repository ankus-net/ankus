namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// A large plain global is copied in place with no frame allocation, retains exact values, and stays unchanged when the
    /// guard refuses the access that would have looked up its address.
    /// </summary>
    [TestMethod]
    public async Task ManagedGlobalsCopyLargeValuesInPlace()
    {
        const string Headers = """
            typedef struct Payload { unsigned char bytes[8192]; } Payload;
            Payload payload;
            int checksum(void) { return payload.bytes[0] * 3 + payload.bytes[8191]; }
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    NativeCallTestBridge.Allocator.Reject = true;
                    int allocationFailures = 0;
                    try { _ = NativeGlobals.payload; }
                    catch (OutOfMemoryException) { allocationFailures++; }
                    try { NativeGlobals.payload = default; }
                    catch (OutOfMemoryException) { allocationFailures++; }
                    int early = NativeCallTestBridge.Accessors + scope.Invocations;
                    NativeCallTestBridge.Allocator.Reject = false;
                    Payload value = NativeGlobals.payload;
                    int before = value.bytes[0] + value.bytes[8191];
                    value.bytes[0] = 7;
                    value.bytes[8191] = 201;
                    scope.RejectCall = true;
                    int rejected = 0;
                    try { NativeGlobals.payload = value; }
                    catch (PgException error) when (error.SqlState == "22023" && error.Message == "native call café"
                        && error.Detail == "detail naïve" && error.Hint == "hint déjà") { rejected++; }
                    int live = NativeCallTestBridge.Allocator.Live;
                    scope.RejectCall = false;
                    int unchanged = NativeMethods.checksum();
                    NativeGlobals.payload = value;
                    Payload observed = NativeGlobals.payload;
                    return [allocationFailures, early, before, rejected, live, unchanged, observed.bytes[0], observed.bytes[8191],
                        NativeMethods.checksum(), NativeCallTestBridge.Allocator.Allocations, NativeCallTestBridge.Allocator.Releases,
                        NativeCallTestBridge.Allocator.Live];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([0, 4, 0, 1, 0, 0, 7, 201, 222, 0, 0, 0],
            await ExecuteManagedCallsAsync(Headers, ["checksum"], Harness, globalNames: ["payload"]));
    }

    /// <summary>
    /// Global and generated member names cannot capture accessors, CLR members or their containing class.
    /// </summary>
    [TestMethod]
    public async Task ManagedGlobalsPreserveMemberNames()
    {
        const string Headers = """
            struct NativeGlobals { int value; };
            struct NativeGlobals shaped = {31};
            int NativeGlobals = 11;
            int Read_NativeGlobals = 23;
            int GetNativeRead_Equals = 37;
            int Equals = 41;
            int Finalize = 43;
            int GetHashCode = 47;
            int event = 53;
            int DangerousAddressOf_Equals = 59;
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    NativeGlobals.@event = 67;
                    return [NativeGlobals.Native_NativeGlobals, NativeGlobals.Read_NativeGlobals, NativeGlobals.GetNativeRead_Equals,
                        NativeGlobals.Equals, NativeGlobals.Finalize, NativeGlobals.GetHashCode, NativeGlobals.@event, NativeGlobals.DangerousAddressOf_Equals,
                        NativeGlobals.shaped.value];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([11, 23, 37, 41, 43, 47, 67, 59, 31], await ExecuteManagedCallsAsync(Headers, [], Harness,
            globalNames: ["NativeGlobals", "Read_NativeGlobals", "GetNativeRead_Equals", "Equals", "Finalize", "GetHashCode", "event", "DangerousAddressOf_Equals", "shaped"]));
    }

    /// <summary>
    /// Zero-size native arrays retain distinct logical types and valid native addresses without transporting CLR bytes.
    /// </summary>
    [TestMethod]
    public async Task ManagedGlobalsPreserveEmptyArrays()
    {
        const string Headers = """
            int empty[0];
            const int constant_empty[0];
            unsigned char bytes[0];
            volatile unsigned char observed_bytes[0];
            typedef struct {} Empty;
            Empty writable;
            const Empty readonly_value;
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    NativeGlobals.empty = NativeGlobals.empty;
                    NativeGlobals.bytes = NativeGlobals.bytes;
                    NativeGlobals.empty = NativeGlobals.constant_empty;
                    NativeGlobals.bytes = NativeGlobals.observed_bytes;
                    NativeGlobals.writable = NativeGlobals.readonly_value;
                    return [NativeGlobals.DangerousAddressOf_empty() != null ? 1 : 0, NativeGlobals.DangerousAddressOf_bytes() != null ? 1 : 0,
                        typeof(NativeGlobals).GetProperty("empty")!.PropertyType != typeof(NativeGlobals).GetProperty("bytes")!.PropertyType ? 1 : 0,
                        scope.Invocations];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([1, 1, 1, 12], await ExecuteManagedCallsAsync(Headers, [], Harness,
            nativeCompiler: false, globalNames: ["empty", "constant_empty", "bytes", "observed_bytes", "writable", "readonly_value"]));
    }

    /// <summary>
    /// Thread-local bodies obtain the active native thread's storage on every invocation without caching its address.
    /// </summary>
    [TestMethod]
    public async Task ManagedGlobalsResolveThreadLocalStoragePerAccess()
    {
        const string Harness = """
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    int before = NativeGlobals.current;
                    NativeGlobals.current = 41;
                    int* outerAddress = NativeGlobals.DangerousAddressOf_current();
                    int childBefore = 0;
                    int childAfter = 0;
                    int* childAddress = null;
                    Exception? failure = null;
                    System.Threading.Thread thread = new(() =>
                    {
                        try
                        {
                            using NativeCallTestBridge.Scope child = new();
                            childBefore = NativeGlobals.current;
                            NativeGlobals.current = -73;
                            childAfter = NativeGlobals.current;
                            childAddress = NativeGlobals.DangerousAddressOf_current();
                        }
                        catch (Exception error) { failure = error; }
                    });
                    thread.Start();
                    thread.Join();
                    if (failure is not null) { throw failure; }
                    return [before, childBefore, childAfter, NativeGlobals.current, childAddress != null && childAddress != outerAddress ? 1 : 0,
                        NativeGlobals.DangerousAddressOf_current() == outerAddress ? 1 : 0];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([17, 17, -73, 41, 1, 1], await ExecuteManagedCallsAsync("_Thread_local int current = 17;", [], Harness, globalNames: ["current"]));
    }

    /// <summary>
    /// Generated global properties read current C values, mutate native storage and preserve const and pointer contracts.
    /// </summary>
    [TestMethod]
    public async Task ManagedGlobalsPreserveNativeValuesAndIdentity()
    {
        const string Headers = """
            typedef struct State { unsigned long long bits; int count; } State;
            int current = -41;
            const int constant = 73;
            const int *address = &constant;
            int * const fixed_address = &current;
            volatile unsigned long long observed = 0xfedcba9876543210ULL;
            State state = { 0x8123456789abcdefULL, -7 };
            int matrix[2][3] = {{1, -2, 3}, {-4, 5, -6}};
            void change(void) { current = 127; observed = 0x8000000000000001ULL; }
            void *current_address(void) { return &current; }
            int inspect(void) { return *address + state.count + matrix[0][0] + matrix[1][2]; }
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    int before = NativeGlobals.current;
                    ulong firstObserved = NativeGlobals.observed;
                    NativeMethods.change();
                    int changed = NativeGlobals.current;
                    ulong secondObserved = NativeGlobals.observed;
                    NativeGlobals.current = -19;
                    int* originalAddress = (int*)NativeMethods.current_address();
                    bool identity = NativeGlobals.DangerousAddressOf_current() == originalAddress && NativeGlobals.fixed_address == originalAddress;
                    NativeGlobals.address = originalAddress;
                    State state = NativeGlobals.state;
                    ulong originalBits = state.bits;
                    state.bits = ulong.MaxValue;
                    state.count = 38;
                    NativeGlobals.state = state;
                    var matrix = NativeGlobals.matrix;
                    int originalFirst = matrix[0][0];
                    int originalLast = matrix[1][2];
                    matrix[0][0] = -31;
                    matrix[1][2] = 47;
                    NativeGlobals.matrix = matrix;
                    var observedMatrix = NativeGlobals.matrix;
                    bool array = observedMatrix[0][0] == -31 && observedMatrix[0][1] == -2 && observedMatrix[0][2] == 3
                        && observedMatrix[1][0] == -4 && observedMatrix[1][1] == 5 && observedMatrix[1][2] == 47;
                    bool setters = typeof(NativeGlobals).GetProperty("constant")!.SetMethod is null
                        && typeof(NativeGlobals).GetProperty("fixed_address")!.SetMethod is null
                        && typeof(NativeGlobals).GetProperty("address")!.SetMethod is not null;
                    return [before, changed, NativeGlobals.current, unchecked((long)firstObserved), unchecked((long)secondObserved),
                        identity ? 1 : 0, unchecked((long)originalBits), unchecked((long)NativeGlobals.state.bits),
                        NativeMethods.inspect(), originalFirst, originalLast, array ? 1 : 0, setters ? 1 : 0, NativeGlobals.constant];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([-41, 127, -19, unchecked((long)0xfedcba9876543210UL), unchecked((long)0x8000000000000001UL),
            1, unchecked((long)0x8123456789abcdefUL), -1, 35, 1, -6, 1, 1, 73],
            await ExecuteManagedCallsAsync(Headers, ["change", "current_address", "inspect"], Harness,
                globalNames: ["current", "constant", "address", "fixed_address", "observed", "state", "matrix"]));
    }

    /// <summary>
    /// Each global operation rejects absent and mismatched callbacks before native lookup and revalidates after nesting.
    /// </summary>
    [TestMethod]
    public async Task ManagedGlobalsValidateEveryAccessAndRecover()
    {
        const string Harness = """
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    int absent = 0;
                    try { _ = NativeGlobals.current; }
                    catch (InvalidOperationException error) when (error.Message.Contains("active backend callback", StringComparison.Ordinal)) { absent++; }
                    try { NativeGlobals.current = -1; }
                    catch (InvalidOperationException error) when (error.Message.Contains("active backend callback", StringComparison.Ordinal)) { absent++; }
                    try { _ = NativeGlobals.DangerousAddressOf_current(); }
                    catch (InvalidOperationException error) when (error.Message.Contains("active backend callback", StringComparison.Ordinal)) { absent++; }
                    int earlyAccessors = NativeCallTestBridge.Accessors;
                    using NativeCallTestBridge.Scope outer = new();
                    int before = NativeGlobals.current;
                    int mismatch = 0;
                    int nestedInvocations;
                    int nestedValidations;
                    using (NativeCallTestBridge.Scope inner = new() { Identity = "wrong" })
                    {
                        try { _ = NativeGlobals.current; }
                        catch (PgException error) when (error.SqlState == "0A000") { mismatch++; }
                        try { NativeGlobals.current = 99; }
                        catch (PgException error) when (error.SqlState == "0A000") { mismatch++; }
                        try { _ = NativeGlobals.DangerousAddressOf_current(); }
                        catch (PgException error) when (error.SqlState == "0A000") { mismatch++; }
                        nestedInvocations = inner.Invocations;
                        nestedValidations = inner.Validations;
                    }
                    int unchanged = NativeGlobals.current;
                    NativeGlobals.current = 47;
                    int after = NativeGlobals.current;
                    return [absent, earlyAccessors, before, mismatch, nestedInvocations, nestedValidations, unchanged, after,
                        outer.Validations, outer.Invocations, NativeCallTestBridge.Accessors];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([3, 0, 19, 3, 0, 3, 19, 47, 4, 4, 4],
            await ExecuteManagedCallsAsync("int current = 19;", [], Harness, globalNames: ["current"]));
    }
}
