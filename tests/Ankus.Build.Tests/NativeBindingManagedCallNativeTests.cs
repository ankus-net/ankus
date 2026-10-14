using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Generated methods transport exact scalar, aggregate and extended floating values through actual compiled native bodies.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallsPreserveNativeValues()
    {
        const string Headers = """
            #include <stdint.h>
            #include <float.h>
            typedef enum Choice { Low = -7, High = 31 } Choice;
            typedef struct Record { long long count; unsigned long long wide; void *address; Choice choice; _Bool enabled; } Record;
            Record process(Record input, unsigned long long maximum, _Bool enabled, void *address)
            {
                input.count -= 11;
                input.wide = maximum;
                input.address = address;
                input.choice = High;
                input.enabled = enabled;
                return input;
            }
            long double make_extended(void) { return 1.0L + LDBL_EPSILON; }
            _Bool exact_extended(long double value) { return value == 1.0L + LDBL_EPSILON; }
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static unsafe long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    Record original = default;
                    original.count = -9223372036854775797L;
                    original.choice = Choice.Low;
                    Record result = NativeMethods.process(original, ulong.MaxValue, true, (void*)(-17));
                    bool extended = NativeMethods.exact_extended(NativeMethods.make_extended());
                    return [result.count, unchecked((long)result.wide), (nint)result.address, (long)result.choice,
                        result.enabled ? 1 : 0, original.count, (long)original.choice, extended ? 1 : 0,
                        scope.Validations, scope.Invocations, NativeCallTestBridge.Accessors, NativeCallTestBridge.Allocator.Live];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([long.MinValue, -1, -17, 31, 1, -9223372036854775797L, -7, 1, 3, 3, 3, 0],
            await ExecuteManagedCallsAsync(Headers, ["process", "make_extended", "exact_extended"], Harness));
    }

    /// <summary>
    /// Real native bodies receive over-aligned records even though managed values have packed object representations.
    /// </summary>
    /// <param name="owned">Whether the aligned record requires a heap frame.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public async Task ManagedCallsAlignNativeFrames(bool owned)
    {
        const string Headers = """
            #if defined(_MSC_VER) && !defined(__clang__)
            typedef struct __declspec(align(64)) Aligned { int value; unsigned char padding[__PADDING__]; } Aligned;
            #else
            typedef struct __attribute__((aligned(64))) Aligned { int value; unsigned char padding[__PADDING__]; } Aligned;
            #endif
            int aligned_read(Aligned value, int extra) { return value.value + extra; }
            """;
        const string Harness = """
            public static class BindingAssertions
            {
                public static long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    Aligned value = default;
                    value.value = 731;
                    int result = NativeMethods.aligned_read(value, 19);
                    return [result, value.value, NativeCallTestBridge.Allocator.Allocations, NativeCallTestBridge.Allocator.Releases,
                        NativeCallTestBridge.Allocator.Live, scope.Invocations];
                }
            }
            """;
        string headers = Headers.Replace("__PADDING__", owned ? "8188" : "60", StringComparison.Ordinal);
        Assert.AreSequenceEqual<long>([750, 731, owned ? 1 : 0, owned ? 1 : 0, 0, 1], await ExecuteManagedCallsAsync(headers, ["aligned_read"], Harness));
    }

    /// <summary>
    /// Frames immediately below, at and above the stack budget preserve all bytes and release any heap allocation.
    /// </summary>
    /// <param name="difference">The number of bytes relative to the 4096-byte stack budget.</param>
    /// <param name="allocations">The expected owned native allocations.</param>
    [TestMethod]
    [DataRow(-1, 0)]
    [DataRow(0, 0)]
    [DataRow(1, 1)]
    [DataRow(4096, 1)]
    public async Task ManagedCallsBoundStackStorage(int difference, int allocations)
    {
        // One native descriptor contains two addresses; pointer alignment needs at most one address minus one byte.
        int length = 4096 - 2 * IntPtr.Size - (IntPtr.Size - 1) + difference;
        string harness = """
            public static class BindingAssertions
            {
                public static long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    Payload value = default;
                    value.bytes[0] = 7;
                    value.bytes[__LAST__] = 201;
                    NativeMethods.consume(value);
                    int result = NativeMethods.checksum();
                    return [result, NativeCallTestBridge.Allocator.Allocations, NativeCallTestBridge.Allocator.Releases,
                        NativeCallTestBridge.Allocator.Live, scope.Validations, scope.Invocations, NativeCallTestBridge.Accessors];
                }
            }
            """;
        harness = harness.Replace("__LAST__", (length - 1).ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        Assert.AreSequenceEqual<long>([222, allocations, allocations, 0, 2, 2, 2],
            await ExecuteManagedCallsAsync(PayloadHeaders(length), ["consume", "checksum"], harness));
    }

    /// <summary>
    /// A transported native error releases a large argument frame, preserves diagnostics and permits same-scope recovery.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallsRecoverFromNativeErrors()
    {
        const string Harness = """
            public static class BindingAssertions
            {
                public static long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    Payload value = default;
                    value.bytes[0] = 7;
                    value.bytes[8191] = 201;
                    scope.RejectCall = true;
                    PgException? failure = null;
                    try { NativeMethods.consume(value); }
                    catch (PgException error) { failure = error; }
                    int liveAfterFailure = NativeCallTestBridge.Allocator.Live;
                    scope.RejectCall = false;
                    int before = NativeMethods.checksum();
                    NativeMethods.consume(value);
                    int after = NativeMethods.checksum();
                    return [failure?.SqlState == "22023" ? 1 : 0, failure?.Message == "native call café" ? 1 : 0,
                        failure?.Detail == "detail naïve" ? 1 : 0, failure?.Hint == "hint déjà" ? 1 : 0,
                        liveAfterFailure, before, after, NativeCallTestBridge.Allocator.Allocations,
                        NativeCallTestBridge.Allocator.Releases, NativeCallTestBridge.Allocator.Live,
                        scope.Validations, scope.Invocations, NativeCallTestBridge.Accessors];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([1, 1, 1, 1, 0, 0, 222, 2, 2, 0, 4, 4, 4],
            await ExecuteManagedCallsAsync(PayloadHeaders(8192), ["consume", "checksum"], Harness));
    }

    /// <summary>
    /// A failed owned allocation cannot obtain or enter a native body and does not prevent a later successful call.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallsRecoverFromAllocationFailure()
    {
        const string Harness = """
            public static class BindingAssertions
            {
                public static long[] Run()
                {
                    using NativeCallTestBridge.Scope scope = new();
                    NativeCallTestBridge.Allocator.Reject = true;
                    int rejected = 0;
                    try { NativeMethods.consume(default); }
                    catch (OutOfMemoryException) { rejected++; }
                    int entered = NativeCallTestBridge.Accessors + scope.Invocations;
                    NativeCallTestBridge.Allocator.Reject = false;
                    NativeMethods.consume(default);
                    return [rejected, entered, scope.Validations, scope.Invocations, NativeCallTestBridge.Accessors,
                        NativeCallTestBridge.Allocator.Allocations, NativeCallTestBridge.Allocator.Releases,
                        NativeCallTestBridge.Allocator.Live];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([1, 0, 2, 1, 1, 1, 1, 0],
            await ExecuteManagedCallsAsync(PayloadHeaders(8192), ["consume"], Harness));
    }

    /// <summary>
    /// Each call revalidates its active native provider before address lookup, including after a nested mismatched scope.
    /// </summary>
    [TestMethod]
    public async Task ManagedCallsRevalidateNestedBinding()
    {
        const string Headers = "int calls; int increment(void) { return ++calls; }";
        const string Harness = """
            public static class BindingAssertions
            {
                public static long[] Run()
                {
                    using NativeCallTestBridge.Scope outer = new();
                    int first = NativeMethods.increment();
                    int rejected = 0;
                    int nestedValidations;
                    int nestedInvocations;
                    using (NativeCallTestBridge.Scope inner = new())
                    {
                        inner.Identity = "different";
                        try { NativeMethods.increment(); }
                        catch (PgException error) when (error.SqlState == "0A000") { rejected++; }
                        nestedValidations = inner.Validations;
                        nestedInvocations = inner.Invocations;
                    }
                    int second = NativeMethods.increment();
                    return [first, second, rejected, nestedValidations, nestedInvocations,
                        outer.Validations, outer.Invocations, NativeCallTestBridge.Accessors];
                }
            }
            """;
        Assert.AreSequenceEqual<long>([1, 2, 1, 1, 0, 2, 2, 2], await ExecuteManagedCallsAsync(Headers, ["increment"], Harness));
    }

    private static string PayloadHeaders(int length) =>
        "typedef struct Payload { unsigned char bytes[" + length.ToString(CultureInfo.InvariantCulture) + "]; } Payload;\n" +
        "int observed; void consume(Payload value) { observed = value.bytes[0] * 3 + value.bytes[" +
        (length - 1).ToString(CultureInfo.InvariantCulture) + "]; } int checksum(void) { return observed; }";

    /// <summary>
    /// Compiles actual C bodies, substitutes only counting allocation and pure lookup collaborators, and executes emitted C#.
    /// </summary>
    private async Task<long[]> ExecuteManagedCallsAsync(string headers, string[] names, string harness, bool nativeCompiler = true,
        string[]? globalNames = null, bool checkOverflow = false)
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-managed-native-call-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeHeaderRequest[] requests = [.. names.Select(static name => new NativeHeaderRequest(name, name, true)),
                .. (globalNames ?? []).Select(static name => new NativeHeaderRequest(name, name, false))];
            NativeHeaderRecords records = await CollectCallRecordsAsync(headers, requests, directory);
            NativeBindingSource binding = NativeBindingRecordCSharp.Generate(records, names, globalNames);
            var native = new StringBuilder(NativeBindingCallSource.Generate(records, "#define PG_VERSION_NUM 180006\n" + headers, names));
            var globalAccesses = new List<NativeBindingGlobalAccess>();
            foreach (NativeBindingGlobalContract global in NativeBindingGlobalModel.Select(records, globalNames ?? []))
            {
                globalAccesses.Add(new(global.Name, NativeBindingGlobalOperation.Address));
                if (global.IsComplete)
                {
                    globalAccesses.Add(new(global.Name, NativeBindingGlobalOperation.Read));
                }

                if (global.CanWrite)
                {
                    globalAccesses.Add(new(global.Name, NativeBindingGlobalOperation.Write));
                }
            }

            native.Append(NativeBindingGlobalSource.Bodies(records, globalAccesses));
            int[] indirect = [.. NativeBindingIndirectModel.Describe(records.Graph).Where(static call => call.CanInvoke)
                .Select(static call => call.FunctionType)];
            native.Append(NativeBindingIndirectSource.Bodies(records, indirect));
            native.AppendLine("typedef int (*AnkusBody)(const AnkusNativeCallArgument *, size_t, void *, size_t);");
            foreach (string name in names)
            {
                native.AppendLine("#if defined(_WIN32)\n__declspec(dllexport)\n#else\n__attribute__((visibility(\"default\")))\n#endif");
                native.Append("AnkusBody ").Append(NativeBindingCallImports.Prefix).Append(name).Append("(void) { return ankus_native_call_")
                    .Append(name).AppendLine("; }");
            }

            foreach (NativeBindingGlobalAccess access in globalAccesses)
            {
                native.AppendLine("#if defined(_WIN32)\n__declspec(dllexport)\n#else\n__attribute__((visibility(\"default\")))\n#endif");
                native.Append("AnkusBody ").Append(NativeBindingGlobalImports.Prefix).Append(NativeBindingGlobalSource.OperationName(access.Operation))
                    .Append('_').Append(access.Name).Append("(void) { return ").Append(NativeBindingGlobalSource.BodyName(access)).AppendLine("; }");
            }

            foreach (int signature in indirect)
            {
                native.AppendLine("#if defined(_WIN32)\n__declspec(dllexport)\n#else\n__attribute__((visibility(\"default\")))\n#endif");
                native.Append("AnkusBody ").Append(NativeBindingIndirectImports.Prefix).Append(signature.ToString(CultureInfo.InvariantCulture))
                    .Append("(void) { return ").Append(NativeBindingIndirectSource.BodyName(signature)).AppendLine("; }");
            }

            string file = Path.Join(directory, "calls.c");
            await File.WriteAllTextAsync(file, native.ToString(), context.CancellationToken);
            string library = Path.Join(directory, OperatingSystem.IsWindows() ? "calls.dll" : OperatingSystem.IsMacOS() ? "calls.dylib" : "calls.so");
            string compiler = OperatingSystem.IsWindows() ? nativeCompiler ? "cl.exe" : "clang-cl.exe" : "clang";
            string[] arguments = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/O2", "/LD", "/Fe" + library, "/Fo" + Path.ChangeExtension(library, ".obj"), file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-O2", "-fPIC", OperatingSystem.IsMacOS() ? "-dynamiclib" : "-shared", file, "-o", library];
            await RunAsync(compiler, arguments, directory, expectSuccess: true);
            nint module = NativeLibrary.Load(library);
            try
            {
                var managed = new StringBuilder(NativeBindingManagedCallHarness.Source.Replace("__EXPECTED_IDENTITY__", binding.AbiIdentity, StringComparison.Ordinal));
                foreach (MethodDeclarationSyntax method in CSharpSyntaxTree.ParseText(binding.Source, cancellationToken: context.CancellationToken)
                    .GetRoot(context.CancellationToken).DescendantNodes().OfType<MethodDeclarationSyntax>())
                {
                    AttributeSyntax? import = method.AttributeLists.SelectMany(static list => list.Attributes)
                        .FirstOrDefault(static attribute => attribute.Name.ToString().EndsWith("LibraryImport", StringComparison.Ordinal));
                    if (import is null)
                    {
                        continue;
                    }

                    AttributeArgumentSyntax argument = import.ArgumentList!.Arguments.Single(static argument => argument.NameEquals?.Name.Identifier.ValueText == "EntryPoint");
                    string entry = ((LiteralExpressionSyntax)argument.Expression).Token.ValueText;
                    nint address = NativeLibrary.GetExport(module, entry);
                    var owner = (TypeDeclarationSyntax)method.Parent!;
                    managed.Append("\nnamespace Ankus.Postgres { public ")
                        .Append(owner is StructDeclarationSyntax ? "readonly partial struct " : "static partial class ")
                        .Append(owner.Identifier.Text).AppendLine(" {");
                    managed.Append("private static partial nint ").Append(method.Identifier.Text)
                        .Append("() { unsafe { global::NativeCallTestBridge.Accessors++; return ((delegate* unmanaged[Cdecl]<nint>)unchecked((nint)")
                        .Append(unchecked((ulong)address).ToString(CultureInfo.InvariantCulture)).AppendLine("UL))(); } }");
                    managed.AppendLine("} }");
                }

                managed.AppendLine(harness);
                // The real allocator still owns real bytes. Counting this collaborator makes omitted/wrong finally frees observable.
                NativeBindingSource observed = binding with
                {
                    Source = binding.Source.Replace("global::System.Runtime.InteropServices.NativeMemory",
                    "global::NativeCallTestBridge.Allocator", StringComparison.Ordinal)
                };
                return GeneratedBindingCompilation.Run(observed, managed.ToString(), context.CancellationToken, checkOverflow);
            }
            finally
            {
                NativeLibrary.Free(module);
            }
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }
}
