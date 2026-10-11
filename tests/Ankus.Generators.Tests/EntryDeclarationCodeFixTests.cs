using System.Collections.Immutable;
using Ankus.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// The native callback type shared by entry fix cases that declare a callback property or handler.
    /// </summary>
    private const string FixHookSource = """
        [Ankus.CompilerServices.NativeFunctionPointer(7)]
        public readonly unsafe struct Hook(void* address) : Ankus.IPgNativeType
        {
            public nint Address => (nint)address;
            static int Ankus.IPgNativeType.PostgresMajor => 18;
            static string Ankus.IPgNativeType.AbiIdentity => new string('A', 64);
            static string Ankus.IPgNativeType.RuntimeIdentifier => System.Runtime.InteropServices.RuntimeInformation.RuntimeIdentifier;
            static int Ankus.IPgNativeType.NativeSize => System.IntPtr.Size;
            static int Ankus.IPgNativeType.NativeAlignment => System.IntPtr.Size;
            public long Invoke(int first, long second) => throw new System.NotSupportedException();
        }

        """;

    /// <summary>
    /// Triggers, initializers, workers, enums, backend tests, native callbacks, custom types, datum converters and output
    /// plugins get the same static, internal and partial corrections as functions, and each then generates cleanly.
    /// </summary>
    /// <param name="source">The declaration before the correction.</param>
    /// <param name="id">The expected diagnostic.</param>
    /// <param name="title">The expected action title.</param>
    /// <param name="expected">The exact corrected declaration.</param>
    [TestMethod]
    [DataRow("public class Functions { [Ankus.PgTrigger] public Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }",
        "ANKUS208", "Make 'Audit' static",
        "public class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }")]
    [DataRow("public class Functions { [Ankus.PgInitialize] public void Initialize() { } }",
        "ANKUS232", "Make 'Initialize' static",
        "public class Functions { [Ankus.PgInitialize] public static void Initialize() { } }")]
    [DataRow("public class Workers { [Ankus.PgBackgroundWorker] public void Run(nuint argument) { } }",
        "ANKUS251", "Make 'Run' static",
        "public class Workers { [Ankus.PgBackgroundWorker] public static void Run(nuint argument) { } }")]
    [DataRow("public partial class Checks { [Ankus.PgTest] public void Test() { } }",
        "ANKUS291", "Make 'Test' static",
        "public partial class Checks { [Ankus.PgTest] public static void Test() { } }")]
    [DataRow("public partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } private long Handle(int first, long second) => second; }",
        "ANKUS332", "Make 'Handle' static",
        "public partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } private static long Handle(int first, long second) => second; }")]
    [DataRow("public unsafe class Plugins { [Ankus.PgOutputPlugin] public void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } }",
        "ANKUS516", "Make 'Init' static",
        "public unsafe class Plugins { [Ankus.PgOutputPlugin] public static void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } }")]
    [DataRow("public class Functions { [Ankus.PgEventTrigger] private static void Audit(Ankus.PgEventTriggerContext context) { } }",
        "ANKUS213", "Make 'Audit' internal",
        "public class Functions { [Ankus.PgEventTrigger] internal static void Audit(Ankus.PgEventTriggerContext context) { } }")]
    [DataRow("file class Functions { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext context) { } }",
        "ANKUS222", "Make 'Functions' internal",
        "internal class Functions { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext context) { } }")]
    [DataRow("public class Outer { private class Inner { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext context) { } } }",
        "ANKUS223", "Make 'Inner' internal",
        "public class Outer { internal class Inner { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext context) { } } }")]
    [DataRow("public class Functions { [Ankus.PgInitialize] private static void Initialize() { } }",
        "ANKUS240", "Make 'Initialize' internal",
        "public class Functions { [Ankus.PgInitialize] internal static void Initialize() { } }")]
    [DataRow("file class Functions { [Ankus.PgInitialize] public static void Initialize() { } }",
        "ANKUS243", "Make 'Functions' internal",
        "internal class Functions { [Ankus.PgInitialize] public static void Initialize() { } }")]
    [DataRow("public class Outer { private class Functions { [Ankus.PgInitialize] public static void Initialize() { } } }",
        "ANKUS244", "Make 'Functions' internal",
        "public class Outer { internal class Functions { [Ankus.PgInitialize] public static void Initialize() { } } }")]
    [DataRow("public static class Workers { [Ankus.PgBackgroundWorker] private static void Run(nuint argument) { } }",
        "ANKUS261", "Make 'Run' internal",
        "public static class Workers { [Ankus.PgBackgroundWorker] internal static void Run(nuint argument) { } }")]
    [DataRow("file class Functions { [Ankus.PgBackgroundWorker] public static void Run(nuint value) { } }",
        "ANKUS264", "Make 'Functions' internal",
        "internal class Functions { [Ankus.PgBackgroundWorker] public static void Run(nuint value) { } }")]
    [DataRow("public class Outer { private protected class Functions { [Ankus.PgBackgroundWorker] public static void Run(nuint value) { } } }",
        "ANKUS265", "Make 'Functions' internal",
        "public class Outer { internal class Functions { [Ankus.PgBackgroundWorker] public static void Run(nuint value) { } } }")]
    [DataRow("[Ankus.PgEnum] file enum Mood { Happy }",
        "ANKUS281", "Make 'Mood' internal",
        "[Ankus.PgEnum] internal enum Mood { Happy }")]
    [DataRow("public class Container { [Ankus.PgEnum] private enum Mood { Happy } }",
        "ANKUS282", "Make 'Mood' internal",
        "public class Container { [Ankus.PgEnum] internal enum Mood { Happy } }")]
    [DataRow("public partial class Checks { [Ankus.PgTest] private static void Test() { } }",
        "ANKUS296", "Make 'Test' internal",
        "public partial class Checks { [Ankus.PgTest] internal static void Test() { } }")]
    [DataRow("file partial class Checks { [Ankus.PgTest] public static void Test() { } }",
        "ANKUS302", "Make 'Checks' internal",
        "internal partial class Checks { [Ankus.PgTest] public static void Test() { } }")]
    [DataRow("public partial class Outer { private partial class Checks { [Ankus.PgTest] public static void Test() { } } }",
        "ANKUS303", "Make 'Checks' internal",
        "public partial class Outer { internal partial class Checks { [Ankus.PgTest] public static void Test() { } } }")]
    [DataRow("file partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } private static long Handle(int first, long second) => second; }",
        "ANKUS321", "Make 'Functions' internal",
        "internal partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } private static long Handle(int first, long second) => second; }")]
    [DataRow("[Ankus.PgType] file class Value { }",
        "ANKUS428", "Make 'Value' internal",
        "[Ankus.PgType] internal class Value { }")]
    [DataRow("public unsafe class Plugins { [Ankus.PgOutputPlugin] private static void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } }",
        "ANKUS522", "Make 'Init' internal",
        "public unsafe class Plugins { [Ankus.PgOutputPlugin] internal static void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } }")]
    [DataRow("public class Outer { private unsafe class Plugins { [Ankus.PgOutputPlugin] public static void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } } }",
        "ANKUS523", "Make 'Plugins' internal",
        "public class Outer { internal unsafe class Plugins { [Ankus.PgOutputPlugin] public static void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } } }")]
    [DataRow("[Ankus.PgDatumType(typeof(Box<int>), \"int4\", typeof(Converter<>), Origin = Ankus.PgTypeOrigin.External, Schema = \"pg_catalog\")] public readonly record struct Box<T>(int Number); file sealed class Converter<T> : Ankus.IPgDatumReader<Box<T>> { public Box<T> Read(Ankus.PgDatum value) => default; }",
        "ANKUS143", "Make 'Converter' internal",
        "[Ankus.PgDatumType(typeof(Box<int>), \"int4\", typeof(Converter<>), Origin = Ankus.PgTypeOrigin.External, Schema = \"pg_catalog\")] public readonly record struct Box<T>(int Number); internal sealed class Converter<T> : Ankus.IPgDatumReader<Box<T>> { public Box<T> Read(Ankus.PgDatum value) => default; }")]
    [DataRow("public static class Checks { [Ankus.PgTest] public static void Test() { } }",
        "ANKUS304", "Make 'Checks' partial",
        "public static partial class Checks { [Ankus.PgTest] public static void Test() { } }")]
    [DataRow("public class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } private static long Handle(int first, long second) => second; }",
        "ANKUS322", "Make 'Functions' partial",
        "public partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } private static long Handle(int first, long second) => second; }")]
    [DataRow("public static class Settings { [Ankus.PgGucInt(\"ankus_fix.value\", 1, \"Value\")] public static partial int Value { get; } }",
        "ANKUS180", "Make 'Settings' partial",
        "public static partial class Settings { [Ankus.PgGucInt(\"ankus_fix.value\", 1, \"Value\")] public static partial int Value { get; } }")]
    public async Task EntryDeclarationFixesMatchFunctions(string source, string id, string title, string expected)
    {
        string prefix = FixTypesSource(source);
        using var workspace = new AdhocWorkspace();
        Document corrected = await ApplyFunctionFixAsync(workspace, prefix + source, id, title);
        Assert.AreEqual(prefix + expected, (await corrected.GetTextAsync(context.CancellationToken)).ToString());
        (Compilation output, ImmutableArray<Diagnostic> after) = await GenerateCodeFixDocumentAsync(corrected);
        Assert.IsEmpty(after);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// A private datum carrier and its private converter are reported one at a time, and each correction exposes the next.
    /// </summary>
    [TestMethod]
    public async Task DatumMappingFixesReachCarrierThenConverter()
    {
        const string Source = """
            public static class Holder
            {
                [Ankus.PgDatumType("int4", typeof(Converter), Origin = Ankus.PgTypeOrigin.External, Schema = "pg_catalog")]
                private struct Value;
                private sealed class Converter : Ankus.IPgDatumReader<Value>
                {
                    public Value Read(Ankus.PgDatum value) => default;
                }
            }
            """;
        using var workspace = new AdhocWorkspace();
        Document carrier = await ApplyFunctionFixAsync(workspace, Source, "ANKUS135", "Make 'Value' internal");
        (_, ImmutableArray<Diagnostic> next) = await GenerateCodeFixDocumentAsync(carrier);
        Diagnostic converter = Assert.ContainsSingle(next);
        Assert.AreEqual("ANKUS143", converter.Id);
        CodeAction action = Assert.ContainsSingle(await CodeFixActionsAsync(new DeclarationModifierCodeFixProvider(), carrier, converter));
        Assert.AreEqual("Make 'Converter' internal", action.Title);
        ApplyChangesOperation change = Assert.IsInstanceOfType<ApplyChangesOperation>(
            Assert.ContainsSingle(await action.GetOperationsAsync(context.CancellationToken)));
        Document corrected = change.ChangedSolution.GetDocument(carrier.Id)!;
        Assert.AreEqual(Source.Replace("private struct", "internal struct", StringComparison.Ordinal)
            .Replace("private sealed class", "internal sealed class", StringComparison.Ordinal),
            (await corrected.GetTextAsync(context.CancellationToken)).ToString());
        (Compilation output, ImmutableArray<Diagnostic> after) = await GenerateCodeFixDocumentAsync(corrected);
        Assert.IsEmpty(after);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Supplies the native callback and output plugin table types a fix case refers to.
    /// </summary>
    /// <param name="source">The fix case.</param>
    /// <returns>The declarations to place before the case.</returns>
    private static string FixTypesSource(string source)
        => (source.Contains("Hook", StringComparison.Ordinal) ? FixHookSource : string.Empty) +
            (source.Contains("OutputPluginCallbacks", StringComparison.Ordinal) ? OutputPluginTypesSource + "\n" : string.Empty);
}
