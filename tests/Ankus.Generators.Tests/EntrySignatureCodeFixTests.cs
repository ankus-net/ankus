using System.Collections.Immutable;
using System.Composition.Hosting;
using Ankus.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// The IDE discovers the entry parameter correction through its composition export.
    /// </summary>
    [TestMethod]
    public void EntrySignatureFixIsExported()
    {
        using CompositionHost container = new ContainerConfiguration().WithAssembly(typeof(EntrySignatureCodeFixProvider).Assembly).CreateContainer();
        EntrySignatureCodeFixProvider provider =
            Assert.ContainsSingle(container.GetExports<CodeFixProvider>().OfType<EntrySignatureCodeFixProvider>());
        Assert.AreSequenceEqual(
        [
            "ANKUS037", "ANKUS057", "ANKUS067", "ANKUS076", "ANKUS077", "ANKUS113", "ANKUS114", "ANKUS119", "ANKUS172", "ANKUS212", "ANKUS215",
            "ANKUS218", "ANKUS219", "ANKUS258", "ANKUS260", "ANKUS313", "ANKUS339", "ANKUS532",
        ], provider.FixableDiagnosticIds);
        Assert.AreSame(WellKnownFixAllProviders.BatchFixer, provider.GetFixAllProvider());
    }

    /// <summary>
    /// Each entry takes the parameter shape its native caller supplies, and the entry then generates.
    /// </summary>
    /// <param name="source">The entry before the correction.</param>
    /// <param name="id">The expected diagnostic.</param>
    /// <param name="title">The expected action title.</param>
    /// <param name="expected">The exact corrected entry.</param>
    [TestMethod]
    [DataRow("public static class Functions { [Ankus.PgFunction] public static int Value([Ankus.PgParameter(Element = \"missing\")] int value) => 0; }",
        "ANKUS057", "Remove 'Element'",
        "public static class Functions { [Ankus.PgFunction] public static int Value([Ankus.PgParameter] int value) => 0; }")]
    [DataRow("public static class Functions { [Ankus.PgFunction] public static int Count(int first, [Ankus.PgParameter(Name = \"items\", Variadic = true)] int[] values) => first; }",
        "ANKUS057", "Declare 'values' with 'params'",
        "public static class Functions { [Ankus.PgFunction] public static int Count(int first, [Ankus.PgParameter(Name = \"items\")] params int[] values) => first; }")]
    [DataRow("public static class Functions { [Ankus.PgOperator(\"@+\")] public static int Size(params int[] values) => values.Length; }",
        "ANKUS067", "Remove 'params'",
        "public static class Functions { [Ankus.PgOperator(\"@+\")] public static int Size(int[] values) => values.Length; }")]
    [DataRow("public static class Functions { [Ankus.PgCast] public static long Widen(int value, short modifier) => value; }",
        "ANKUS076", "Change the type of 'modifier' to 'int'",
        "public static class Functions { [Ankus.PgCast] public static long Widen(int value, int modifier) => value; }")]
    [DataRow("public static class Functions { [Ankus.PgCast] public static long Widen(int value, int modifier, bool? explicitConversion) => value; }",
        "ANKUS077", "Change the type of 'explicitConversion' to 'bool'",
        "public static class Functions { [Ankus.PgCast] public static long Widen(int value, int modifier, bool explicitConversion) => value; }")]
    [DataRow("[Ankus.PgAggregate(InitialCondition = \"0\")] public sealed class Total : Ankus.IPgAggregate<int, int> " +
        "{ public static int Transition(Ankus.PgAggregateContext context, int state, int value = 0) => state; }",
        "ANKUS113", "Remove the default value of 'value'",
        "[Ankus.PgAggregate(InitialCondition = \"0\")] public sealed class Total : Ankus.IPgAggregate<int, int> " +
        "{ public static int Transition(Ankus.PgAggregateContext context, int state, int value) => state; }")]
    [DataRow("[Ankus.PgAggregate(InitialCondition = \"0\")] public sealed class Total : Ankus.IPgAggregate<int, int> " +
        "{ public static int Transition([Ankus.PgParameter(Name = \"call\")] Ankus.PgAggregateContext context, int state, int value) => state; }",
        "ANKUS114", "Remove the attributes of 'context'",
        "[Ankus.PgAggregate(InitialCondition = \"0\")] public sealed class Total : Ankus.IPgAggregate<int, int> " +
        "{ public static int Transition(Ankus.PgAggregateContext context, int state, int value) => state; }")]
    [DataRow("[Ankus.PgAggregate(InitialCondition = \"0\")] public sealed class Rows : Ankus.IPgAggregate<int, System.ValueTuple> " +
        "{ public static int Transition(Ankus.PgAggregateContext context, int state, [Ankus.PgParameter(Name = \"empty\")] System.ValueTuple arguments) => state; }",
        "ANKUS119", "Remove the attributes of 'arguments'",
        "[Ankus.PgAggregate(InitialCondition = \"0\")] public sealed class Rows : Ankus.IPgAggregate<int, System.ValueTuple> " +
        "{ public static int Transition(Ankus.PgAggregateContext context, int state, System.ValueTuple arguments) => state; }")]
    [DataRow("public static class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit() => null; }",
        "ANKUS215", "Add the 'PgTriggerContext context' parameter",
        "public static class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }")]
    [DataRow("public static class Functions { [Ankus.PgEventTrigger] public static void Audit() { } }",
        "ANKUS215", "Add the 'PgEventTriggerContext context' parameter",
        "public static class Functions { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext context) { } }")]
    [DataRow("public static class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context = null!) => null; }",
        "ANKUS218", "Remove the default value of 'context'",
        "public static class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }")]
    [DataRow("public static class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit([System.Runtime.InteropServices.Optional] Ankus.PgTriggerContext context) => null; }",
        "ANKUS218", "Remove 'System.Runtime.InteropServices.Optional'",
        "public static class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }")]
    [DataRow("public static class Functions { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext? context) { } }",
        "ANKUS219", "Change the type of 'context' to 'Ankus.PgEventTriggerContext'",
        "public static class Functions { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext context) { } }")]
    [DataRow("public static class Workers { [Ankus.PgBackgroundWorker] public static void Run() { } }",
        "ANKUS258", "Add the 'nuint argument' parameter",
        "public static class Workers { [Ankus.PgBackgroundWorker] public static void Run(nuint argument) { } }")]
    [DataRow("public static class Workers { [Ankus.PgBackgroundWorker] public static void Run(long argument) { } }",
        "ANKUS260", "Change the type of 'argument' to 'nuint'",
        "public static class Workers { [Ankus.PgBackgroundWorker] public static void Run(nuint argument) { } }")]
    [DataRow("public static class Benchmarks { [Ankus.PgBenchmark] public static void Run() { } }",
        "ANKUS532", "Add the 'PgBencher bencher' parameter",
        "public static class Benchmarks { [Ankus.PgBenchmark] public static void Run(Ankus.PgBencher bencher) { } }")]
    [DataRow("public static class Benchmarks { [Ankus.PgBenchmark] public static void Run(object bencher) { } }",
        "ANKUS532", "Change the type of 'bencher' to 'PgBencher'",
        "public static class Benchmarks { [Ankus.PgBenchmark] public static void Run(Ankus.PgBencher bencher) { } }")]
    [DataRow("public static class Functions { private static int s_value; [Ankus.PgFunction] public static ref int Value() { return ref s_value; } }",
        "ANKUS037", "Return 'Value' by value",
        "public static class Functions { private static int s_value; [Ankus.PgFunction] public static int Value() { return s_value; } }")]
    [DataRow("public static class Functions { private static readonly Ankus.PgHeapTuple? s_row = null; [Ankus.PgTrigger] " +
        "public static ref readonly Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => ref s_row; }",
        "ANKUS212", "Return 'Audit' by value",
        "public static class Functions { private static readonly Ankus.PgHeapTuple? s_row = null; [Ankus.PgTrigger] " +
        "public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => s_row; }")]
    [DataRow("public static partial class Settings { [Ankus.PgGucInt(\"demo.value\", 1, \"Value\")] public static partial ref int Value { get; } }",
        "ANKUS172", "Return 'Value' by value",
        "public static partial class Settings { [Ankus.PgGucInt(\"demo.value\", 1, \"Value\")] public static partial int Value { get; } }")]
    [DataRow("public partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial ref Hook Callback { get; } " +
        "private static long Handle(int first, long second) => second; }",
        "ANKUS313", "Return 'Callback' by value",
        "public partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } " +
        "private static long Handle(int first, long second) => second; }")]
    [DataRow("public partial class Functions { private static long s_last; [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } " +
        "private static ref long Handle(int first, long second) { System.Func<int> other = () => first; return ref s_last; } }",
        "ANKUS339", "Return 'Handle' by value",
        "public partial class Functions { private static long s_last; [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } " +
        "private static long Handle(int first, long second) { System.Func<int> other = () => first; return s_last; } }")]
    public async Task EntrySignatureFixMatchesTheNativeCaller(string source, string id, string title, string expected)
    {
        string prefix = FixTypesSource(source);
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace, prefix + source);
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(id, diagnostic.Id);
        CodeAction action = Assert.ContainsSingle(await CodeFixActionsAsync(new EntrySignatureCodeFixProvider(), document, diagnostic));
        Assert.AreEqual(title, action.Title);
        ApplyChangesOperation change = Assert.IsInstanceOfType<ApplyChangesOperation>(
            Assert.ContainsSingle(await action.GetOperationsAsync(context.CancellationToken)));
        Document corrected = change.ChangedSolution.GetDocument(document.Id)!;
        Assert.AreEqual(prefix + expected, (await corrected.GetTextAsync(context.CancellationToken)).ToString());
        (Compilation output, ImmutableArray<Diagnostic> after) = await GenerateCodeFixDocumentAsync(corrected);
        Assert.IsEmpty(after);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// A correction that a caller or body would no longer compile against leaves the source unchanged.
    /// </summary>
    /// <param name="source">The entry and the code that depends on its parameter.</param>
    /// <param name="id">The expected diagnostic.</param>
    [TestMethod]
    [DataRow("public static class Workers { [Ankus.PgBackgroundWorker] public static void Run(string argument) { _ = argument.Length; } }", "ANKUS260")]
    [DataRow("public static class Functions { [Ankus.PgOperator(\"@+\")] public static int Size(params int[] values) => values.Length; " +
        "public static int Three() => Size(1, 2, 3); }", "ANKUS067")]
    [DataRow("public static class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context = null!) => null; " +
        "public static Ankus.PgHeapTuple? Direct() => Audit(); }", "ANKUS218")]
    public async Task EntrySignatureFixKeepsDependentCode(string source, string id)
    {
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace, source);
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(id, diagnostic.Id);
        CodeAction action = Assert.ContainsSingle(await CodeFixActionsAsync(new EntrySignatureCodeFixProvider(), document, diagnostic));
        ApplyChangesOperation change = Assert.IsInstanceOfType<ApplyChangesOperation>(
            Assert.ContainsSingle(await action.GetOperationsAsync(context.CancellationToken)));
        Assert.AreEqual(source, (await change.ChangedSolution.GetDocument(document.Id)!.GetTextAsync(context.CancellationToken)).ToString());
    }
}
