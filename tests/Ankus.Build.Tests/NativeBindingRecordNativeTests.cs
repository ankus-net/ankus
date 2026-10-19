using System.Globalization;
using System.Text.Json;

namespace Ankus.Build.Tests;

public sealed partial class NativeBindingNativeTests
{
    /// <summary>
    /// Native execution independently verifies record storage, while cursor identities retain cycles and anonymous containers.
    /// </summary>
    [TestMethod]
    public async Task CollectedRecordsPreservePhysicalFieldsAndIdentity()
    {
        const string Headers = """
            typedef unsigned long NativeWidth;
            struct Forward;
            typedef struct RecordRoot {
                struct RecordRoot *next;
                const NativeWidth value;
                unsigned int flags : 3;
                unsigned int : 0;
                struct { int inner; } nested;
                union { int number; float fraction; };
                struct Forward *opaque;
                int (*callback)(const struct RecordRoot *);
                char tail[];
            } RecordRoot;
            extern RecordRoot current;
            extern struct { int same; } first;
            extern struct { int same; } second;
            extern void array_call(const int values[static 3]);
            extern int variadic_call(const char *format, ...);
            extern void unspecified_call();
            """;
        NativeHeaderRequest[] requests = [new("current", "current", false), new("first", "first", false), new("second", "second", false),
            new("array_call", "array_call", true), new("variadic_call", "variadic_call", true), new("unspecified_call", "unspecified_call", true)];
        string directory = Path.Join(Path.GetTempPath(), $"ankus-native-records-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph graph = await CollectRecordsAsync(Headers, requests, directory);
            NativeRecordType alias = DeclaredType(graph, graph.Roots["current"]);
            Assert.AreEqual("RecordRoot", alias.Name);
            NativeRecordType rootType = graph.Types[alias.Canonical];
            NativeRecordDeclaration root = graph.Declarations[rootType.Declaration!.Value];
            Assert.AreEqual("struct", root.Kind);
            Assert.AreEqual("RecordRoot", root.Name);
            Assert.IsTrue(root.IsComplete);
            Assert.AreSequenceEqual<string>(["next", "value", "flags", "", "nested", "", "opaque", "callback", "tail"], root.Fields.Select(static field => field.Name));
            NativeRecordType next = graph.Types[root.Fields[0].Type];
            Assert.AreEqual("pointer", next.Kind);
            Assert.AreEqual(alias.Canonical, graph.Types[next.Element!.Value].Canonical);
            Assert.AreEqual(NativeHeaderQualifiers.Const, graph.Types[graph.Types[root.Fields[1].Type].Canonical].Qualifiers);
            Assert.AreEqual("NativeWidth", DeclaredType(graph, root.Fields[1].Type).Name);
            Assert.AreEqual(3, root.Fields[2].BitWidth);
            Assert.AreEqual(0, root.Fields[3].BitWidth);
            Assert.IsFalse(root.Fields[3].IsAnonymous);
            Assert.IsFalse(root.Fields[4].IsAnonymous);
            Assert.IsTrue(root.Fields[5].IsAnonymous);
            NativeRecordDeclaration nested = Record(root.Fields[4].Type);
            NativeRecordDeclaration union = Record(root.Fields[5].Type);
            Assert.AreEqual("", nested.Name);
            Assert.AreEqual("inner", Assert.ContainsSingle(nested.Fields).Name);
            Assert.AreEqual("union", union.Kind);
            Assert.AreSequenceEqual<string>(["number", "fraction"], union.Fields.Select(static field => field.Name));
            Assert.AreSequenceEqual<long>([0, 0], union.Fields.Select(static field => field.OffsetBits));
            NativeRecordDeclaration opaque = Record(graph.Types[root.Fields[6].Type].Element!.Value);
            Assert.AreEqual("Forward", opaque.Name);
            Assert.IsFalse(opaque.IsComplete);
            Assert.IsNull(opaque.Size);
            Assert.IsNull(opaque.Alignment);
            Assert.IsEmpty(opaque.Fields);
            NativeRecordFunction? callback = graph.Types[graph.Types[root.Fields[7].Type].Element!.Value].Function;
            Assert.IsNotNull(callback);
            NativeRecordType parameter = graph.Types[Assert.ContainsSingle(callback.Parameters)];
            Assert.AreEqual("pointer", parameter.Kind);
            NativeRecordType referenced = graph.Types[parameter.Element!.Value];
            Assert.AreEqual(NativeHeaderQualifiers.Const, referenced.Qualifiers);
            Assert.AreEqual(rootType.Declaration, graph.Types[referenced.Canonical].Declaration);
            Assert.AreEqual("int", graph.Types[callback.Result].Name);
            Assert.IsTrue(callback.HasPrototype);
            Assert.IsFalse(callback.IsVariadic);
            Assert.AreEqual(1, callback.CallingConvention);
            NativeRecordType tail = graph.Types[root.Fields[8].Type];
            Assert.AreEqual("array", tail.Kind);
            Assert.IsNull(tail.Count);
            Assert.IsNull(tail.Size);
            Assert.AreEqual(1L, tail.Alignment);
            Assert.AreEqual("char", graph.Types[tail.Element!.Value].Name);
            Assert.AreNotEqual(graph.Types[graph.Types[graph.Roots["first"]].Canonical].Declaration,
                graph.Types[graph.Types[graph.Roots["second"]].Canonical].Declaration);
            string[] anonymousRoots = ["first", "second"];
            foreach (NativeRecordDeclaration record in anonymousRoots.Select(anonymous => Record(graph.Roots[anonymous])))
            {
                Assert.AreEqual("", record.Name);
                Assert.AreEqual("same", Assert.ContainsSingle(record.Fields).Name);
                Assert.AreEqual(4L, record.Size);
            }

            NativeRecordFunction? arrays = graph.Types[graph.Roots["array_call"]].Function;
            Assert.IsNotNull(arrays);
            NativeRecordType writtenArray = graph.Types[Assert.ContainsSingle(arrays.Parameters)];
            Assert.AreEqual("array", writtenArray.Kind);
            Assert.AreEqual(3L, writtenArray.Count);
            NativeRecordFunction? adjusted = graph.Types[graph.Types[graph.Roots["array_call"]].Canonical].Function;
            Assert.IsNotNull(adjusted);
            NativeRecordType arrayParameter = graph.Types[Assert.ContainsSingle(adjusted.Parameters)];
            Assert.AreEqual("pointer", arrayParameter.Kind);
            Assert.AreEqual(NativeHeaderQualifiers.Const, graph.Types[arrayParameter.Element!.Value].Qualifiers);
            Assert.IsTrue(graph.Types[graph.Roots["variadic_call"]].Function!.IsVariadic);
            NativeRecordFunction? unspecified = graph.Types[graph.Roots["unspecified_call"]].Function;
            Assert.IsNotNull(unspecified);
            Assert.IsFalse(unspecified.HasPrototype);
            Assert.IsEmpty(unspecified.Parameters);

            string file = Path.Join(directory, "independent.c");
            string executable = Path.Join(directory, OperatingSystem.IsWindows() ? "independent.exe" : "independent");
            const string Main = """
                #include <stddef.h>
                #include <stdio.h>
                int main(void) {
                    RecordRoot value = {0};
                    value.flags = 7;
                    const unsigned char *bytes = (const unsigned char *)&value;
                    size_t occupied = 0;
                    while (occupied < sizeof(value) && bytes[occupied] == 0) { ++occupied; }
                    printf("%zu %zu %zu %zu %zu %zu %zu %u %zu %u\n", sizeof(RecordRoot), _Alignof(RecordRoot),
                        offsetof(RecordRoot, value), offsetof(RecordRoot, nested), offsetof(RecordRoot, number),
                        offsetof(RecordRoot, callback), offsetof(RecordRoot, tail), value.flags,
                        occupied, occupied < sizeof(value) ? (unsigned int)bytes[occupied] : 0);
                    return 0;
                }
                """;
            await File.WriteAllTextAsync(file, Headers + "\n" + Main, context.CancellationToken);
            string compiler = OperatingSystem.IsWindows() ? "clang-cl.exe" : "cc";
            string[] compile = OperatingSystem.IsWindows()
                ? ["/nologo", "/std:c11", "/W4", "/WX", "/Fe" + executable, "/Fo" + Path.ChangeExtension(executable, ".obj"), file]
                : ["-std=c11", "-Wall", "-Wextra", "-Werror", file, "-o", executable];
            await RunAsync(compiler, compile, directory);
            long[] observed = [.. (await RunAsync(executable, [], directory)).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .Select(static value => long.Parse(value, CultureInfo.InvariantCulture))];
            Assert.AreSequenceEqual<long>([root.Size!.Value, root.Alignment!.Value, root.Fields[1].OffsetBits / 8,
                root.Fields[4].OffsetBits / 8, root.Fields[5].OffsetBits / 8, root.Fields[7].OffsetBits / 8, root.Fields[8].OffsetBits / 8,
                7, root.Fields[2].OffsetBits / 8, 7L << (int)(root.Fields[2].OffsetBits % 8)], observed);

            string serialized = JsonSerializer.Serialize(graph, NativeBindingRecordWorker.JsonOptions);
            Assert.DoesNotContain(directory, serialized);
            string otherDirectory = Path.Join(directory, "different source location");
            Directory.CreateDirectory(otherDirectory);
            NativeRecordGraph repeated = await CollectRecordsAsync(Headers, [.. requests.Reverse()], otherDirectory);
            Assert.AreEqual(serialized, JsonSerializer.Serialize(repeated, NativeBindingRecordWorker.JsonOptions));

            NativeRecordDeclaration Record(int type) => graph.Declarations[graph.Types[graph.Types[type].Canonical].Declaration!.Value];
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Exact enum limits, over-aligned aliases, atomic and vector fields and callback annotations survive AST loading.
    /// </summary>
    [TestMethod]
    public async Task CollectedRecordsRetainEnumsAndAnnotatedTypes()
    {
        const string Headers = """
            typedef enum SignedLimit : long long { Minimum = (-9223372036854775807LL - 1), Negative = -1 } SignedLimit;
            typedef enum UnsignedLimit : unsigned long long { Maximum = 18446744073709551615ULL } UnsignedLimit;
            typedef enum { SmallFirst = 1, SmallLast = 3 } Small;
            typedef struct { int value; } Untagged;
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
            typedef struct AttributeRoot {
                SignedLimit low;
                UnsignedLimit high;
                Wide wide;
                Three vector;
                _Atomic(unsigned long) counter;
                _Complex double complex;
                NativeExit *exit;
                Small small;
                Untagged untagged;
                ForeignCallback foreign;
            } AttributeRoot;
            extern AttributeRoot attributes;
            """;
        string directory = Path.Join(Path.GetTempPath(), $"ankus-native-attributes-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph graph = await CollectRecordsAsync(Headers, [new("attributes", "attributes", false)], directory);
            NativeRecordDeclaration low = graph.Declarations.Single(static value => value.Name == "SignedLimit");
            NativeRecordDeclaration high = graph.Declarations.Single(static value => value.Name == "UnsignedLimit");
            Assert.AreSequenceEqual<NativeRecordConstant>([new("Minimum", "-9223372036854775808"), new("Negative", "-1")], low.EnumValues);
            Assert.AreEqual(new NativeRecordConstant("Maximum", "18446744073709551615"), Assert.ContainsSingle(high.EnumValues));
            Assert.AreEqual(8L, graph.Types[low.EnumUnderlying!.Value].Size);
            Assert.AreEqual(8L, graph.Types[high.EnumUnderlying!.Value].Size);
            NativeRecordDeclaration root = graph.Declarations.Single(static value => value.Name == "AttributeRoot");
            NativeRecordType wide = DeclaredType(graph, root.Fields[2].Type);
            Assert.AreEqual("Wide", wide.Name);
            Assert.AreEqual(4L, wide.Size);
            Assert.AreEqual(16L, wide.Alignment);
            Assert.IsNotNull(wide.SourceDeclaration);
            Assert.Contains("aligned(16)", wide.SourceDeclaration);
            NativeRecordType vector = graph.Types[graph.Types[root.Fields[3].Type].Canonical];
            Assert.AreEqual("vector", vector.Kind);
            Assert.AreEqual(3L, vector.Count);
            Assert.AreEqual(16L, vector.Size);
            Assert.AreEqual(4L, graph.Types[vector.Element!.Value].Size);
            NativeRecordType atomic = graph.Types[root.Fields[4].Type];
            Assert.AreEqual("atomic", atomic.Kind);
            Assert.AreEqual("unsigned long", graph.Types[atomic.Element!.Value].Name);
            NativeRecordType complex = graph.Types[root.Fields[5].Type];
            Assert.AreEqual("complex", complex.Kind);
            Assert.AreEqual("double", graph.Types[complex.Element!.Value].Name);
            NativeRecordType exit = DeclaredType(graph, graph.Types[root.Fields[6].Type].Element!.Value);
            Assert.AreEqual("NativeExit", exit.Name);
            Assert.IsNotNull(exit.SourceDeclaration);
            Assert.Contains("noreturn", exit.SourceDeclaration);
            Assert.Contains("noreturn", graph.Types[exit.Canonical].Spelling);
            NativeRecordDeclaration small = graph.Declarations[graph.Types[graph.Types[root.Fields[7].Type].Canonical].Declaration!.Value];
            NativeRecordDeclaration untagged = graph.Declarations[graph.Types[graph.Types[root.Fields[8].Type].Canonical].Declaration!.Value];
            Assert.AreEqual("", small.Name);
            Assert.AreSequenceEqual<NativeRecordConstant>([new("SmallFirst", "1"), new("SmallLast", "3")], small.EnumValues);
            Assert.AreEqual("", untagged.Name);
            Assert.AreEqual("value", Assert.ContainsSingle(untagged.Fields).Name);
            NativeRecordType foreign = graph.Types[graph.Types[root.Fields[9].Type].Canonical];
            NativeRecordFunction? foreignFunction = graph.Types[foreign.Element!.Value].Function;
            Assert.IsNotNull(foreignFunction);
            Assert.AreEqual(System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture == System.Runtime.InteropServices.Architecture.X64
                ? OperatingSystem.IsWindows() ? 11 : 10 : 1,
                foreignFunction.CallingConvention);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Native loader, identity and cancellation failures leave the parent usable and permit a subsequent valid observation.
    /// </summary>
    [TestMethod]
    public async Task NativeRecordWorkerRejectsInvalidArtifactsAndRecovers()
    {
        const string Headers = "extern int current;";
        string directory = Path.Join(Path.GetTempPath(), $"ankus-native-record-worker-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph expected = await CollectRecordsAsync(Headers, [new("current", "current", false)], directory);
            string input = Path.Join(directory, "native-record-request.json");
            NativeRecordRequest? request = JsonSerializer.Deserialize<NativeRecordRequest>(await File.ReadAllTextAsync(input, context.CancellationToken), NativeBindingRecordWorker.JsonOptions);
            Assert.IsNotNull(request);
            NativeRecordRequest mismatch = request with
            {
                Target = request.Target with
                {
                    PostgresVersion = 170011
                }
            };
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingRecordWorker.InspectAsync(mismatch, directory, context.CancellationToken));
            Assert.Contains("target does not match", failure.Message);
            NativeRecordRequest missing = request with
            {
                Symbols = new Dictionary<string, NativeHeaderRequest>()
            };
            failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingRecordWorker.InspectAsync(missing, directory, context.CancellationToken));
            Assert.Contains("Unexpected or duplicate native record root", failure.Message);

            string invalid = Path.Join(directory, "invalid.ast");
            await File.WriteAllTextAsync(invalid, "not a compiler AST", context.CancellationToken);
            failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                NativeBindingRecordWorker.InspectAsync(request with
                {
                    Ast = invalid
                }, directory, context.CancellationToken));
            Assert.Contains("could not load the selected compiler's AST", failure.Message);
            NativeRecordGraph recovered = await NativeBindingRecordWorker.InspectAsync(request, directory, context.CancellationToken);
            Assert.AreEqual(JsonSerializer.Serialize(expected, NativeBindingRecordWorker.JsonOptions),
                JsonSerializer.Serialize(recovered, NativeBindingRecordWorker.JsonOptions));

            string observations = Path.Join(directory, "native-record-observations.json");
            byte[] before = await File.ReadAllBytesAsync(observations, context.CancellationToken);
            using var cancellation = new CancellationTokenSource();
            await cancellation.CancelAsync();
            await Assert.ThrowsExactlyAsync<OperationCanceledException>(() => NativeBindingRecordWorker.InspectAsync(request, directory, cancellation.Token));
            Assert.AreSequenceEqual(before, await File.ReadAllBytesAsync(observations, context.CancellationToken));
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// Empty selections retain only their target and never invent transitive declarations.
    /// </summary>
    [TestMethod]
    public async Task EmptyRecordSelectionRetainsOnlyTarget()
    {
        string directory = Path.Join(Path.GetTempPath(), $"ankus-empty-records-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            NativeRecordGraph graph = await CollectRecordsAsync("struct Unselected { int value; };", [], directory);
            Assert.AreEqual(180006, graph.Target.PostgresVersion);
            Assert.AreEqual(System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier, graph.Target.RuntimeIdentifier);
            Assert.IsEmpty(graph.Roots);
            Assert.IsEmpty(graph.Types);
            Assert.IsEmpty(graph.Declarations);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    /// <summary>
    /// A constant outside the C API's 64-bit value range fails before any narrowing accessor is called.
    /// </summary>
    [TestMethod]
    public async Task CollectedRecordsRejectEnumWiderThan64Bits()
    {
        const string Headers = "typedef enum WideEnum : unsigned __int128 { Huge = (unsigned __int128)1 << 64 } WideEnum; extern WideEnum current;";
        string directory = Path.Join(Path.GetTempPath(), $"ankus-wide-enum-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            InvalidOperationException failure = await Assert.ThrowsExactlyAsync<InvalidOperationException>(() =>
                CollectRecordsAsync(Headers, [new("current", "current", false)], directory));
            Assert.Contains("enum constants exceed the supported 64-bit representation", failure.Message);
        }
        finally
        {
            await DeleteDirectoryAsync(directory);
        }
    }

    private Task<NativeRecordGraph> CollectRecordsAsync(string headers, NativeHeaderRequest[] requests, string directory, params string[] frontendOptions)
        => CollectMeasuredRecordsAsync("#define PG_VERSION_NUM 180006\n" + headers, requests, directory, 18, frontendOptions);

    private async Task<NativeRecordGraph> CollectMeasuredRecordsAsync(string headers, IReadOnlyList<NativeHeaderRequest> requests, string directory,
        int major, params string[] frontendOptions)
    {
        string file = Path.Join(directory, "records.c");
        string ast = Path.Join(directory, "records.ast");
        string json = Path.Join(directory, "records.ast.json");
        string compiler = await HostCompilers.ClangAsync();
        string[] frontend = OperatingSystem.IsWindows()
            ? ["/nologo", "/std:c11", "/W4", "/WX", "/Zs", .. frontendOptions]
            : ["-std=c11", "-Wall", "-Wextra", "-Werror", "-fsyntax-only", .. frontendOptions];
        string targetHeaders = NativeBindingHeaderTarget.GenerateSource(headers, major);
        await File.WriteAllTextAsync(file, NativeBindingHeaderParser.GenerateSource(targetHeaders, requests), context.CancellationToken);
        await NativeBindingHeaderCommand.CompileAsync(compiler, [.. frontend, "-Xclang", "-ast-dump=json", file], json, directory, context.CancellationToken);
        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(json, context.CancellationToken));
        NativeHeaderTarget target = NativeBindingHeaderTarget.Read(document.RootElement, major);
        await NativeBindingHeaderCommand.CompileAsync(compiler, [.. frontend, "-Xclang", "-emit-pch", "-Xclang", "-o", "-Xclang", ast, file],
            Path.Join(directory, "records.txt"), directory, context.CancellationToken);
        string library = await NativeBindingRecordCommand.FindLibraryAsync(compiler, target.ClangMajor, context.CancellationToken);
        var request = new NativeRecordRequest(ast, library, target, requests.ToDictionary(static value => value.Name, StringComparer.Ordinal));
        return await NativeBindingRecordWorker.InspectAsync(request, directory, context.CancellationToken);
    }

    private static NativeRecordType DeclaredType(NativeRecordGraph graph, int index)
    {
        NativeRecordType type = graph.Types[index];
        while (type.Kind == "elaborated")
        {
            type = graph.Types[type.Element!.Value];
        }

        return type;
    }
}
