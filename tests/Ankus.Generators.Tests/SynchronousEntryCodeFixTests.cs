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
    /// The IDE discovers the synchronous-entry correction through its composition export.
    /// </summary>
    [TestMethod]
    public void SynchronousEntryFixIsExported()
    {
        using CompositionHost container = new ContainerConfiguration().WithAssembly(typeof(SynchronousEntryCodeFixProvider).Assembly).CreateContainer();
        SynchronousEntryCodeFixProvider provider =
            Assert.ContainsSingle(container.GetExports<CodeFixProvider>().OfType<SynchronousEntryCodeFixProvider>());
        Assert.AreSequenceEqual(
            ["ANKUS030", "ANKUS031", "ANKUS032", "ANKUS209", "ANKUS233", "ANKUS252", "ANKUS292", "ANKUS335", "ANKUS517", "ANKUS527"],
            provider.FixableDiagnosticIds);
        Assert.AreSame(WellKnownFixAllProviders.BatchFixer, provider.GetFixAllProvider());
    }

    /// <summary>
    /// An entry that never awaits loses <c>async</c>, and its result becomes the value or sequence it already returns; the
    /// entry then generates.
    /// </summary>
    /// <param name="source">The entry before the correction.</param>
    /// <param name="id">The expected diagnostic.</param>
    /// <param name="expected">The exact corrected entry.</param>
    [TestMethod]
    [DataRow("public static class Functions { [Ankus.PgFunction] public static async System.Threading.Tasks.Task<int> Value() { return 1; } }",
        "ANKUS031", "public static class Functions { [Ankus.PgFunction] public static int Value() { return 1; } }")]
    [DataRow("public static class Functions\n{\n    [Ankus.PgFunction]\n    public static async System.Threading.Tasks.ValueTask<string?> Value()\n    {\n" +
        "        System.Func<System.Threading.Tasks.Task> later = async () => await System.Threading.Tasks.Task.Yield();\n        return null;\n    }\n}\n",
        "ANKUS031", "public static class Functions\n{\n    [Ankus.PgFunction]\n    public static string? Value()\n    {\n" +
        "        System.Func<System.Threading.Tasks.Task> later = async () => await System.Threading.Tasks.Task.Yield();\n        return null;\n    }\n}\n")]
    [DataRow("public static class Functions { [Ankus.PgFunction] public static async System.Threading.Tasks.Task Touch() { } }",
        "ANKUS031", "public static class Functions { [Ankus.PgFunction] public static void Touch() { } }")]
    [DataRow("public static class Functions { [Ankus.PgFunction] public static async void Touch() { } }",
        "ANKUS030", "public static class Functions { [Ankus.PgFunction] public static void Touch() { } }")]
    [DataRow("public static class Functions { [Ankus.PgFunction] public static async System.Collections.Generic.IAsyncEnumerable<int> Values() { yield return 1; } }",
        "ANKUS032", "public static class Functions { [Ankus.PgFunction] public static System.Collections.Generic.IEnumerable<int> Values() { yield return 1; } }")]
    [DataRow("public static class Functions { [Ankus.PgTrigger] public static async System.Threading.Tasks.Task<Ankus.PgHeapTuple?> Audit(Ankus.PgTriggerContext context) => null; }",
        "ANKUS209", "public static class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }")]
    [DataRow("public static class Functions { [Ankus.PgInitialize] public static async void Start() { } }",
        "ANKUS233", "public static class Functions { [Ankus.PgInitialize] public static void Start() { } }")]
    [DataRow("public static class Workers { [Ankus.PgBackgroundWorker] public static async void Run(nuint argument) { } }",
        "ANKUS252", "public static class Workers { [Ankus.PgBackgroundWorker] public static void Run(nuint argument) { } }")]
    [DataRow("public partial class Checks { [Ankus.PgTest] public static async System.Threading.Tasks.Task Check() { } }",
        "ANKUS292", "public partial class Checks { [Ankus.PgTest] public static void Check() { } }")]
    [DataRow("public partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } " +
        "private static async System.Threading.Tasks.Task<long> Handle(int first, long second) => second; }",
        "ANKUS335", "public partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } " +
        "private static long Handle(int first, long second) => second; }")]
    [DataRow("public unsafe class Plugins { [Ankus.PgOutputPlugin] public static async void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } }",
        "ANKUS517", "public unsafe class Plugins { [Ankus.PgOutputPlugin] public static void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } }")]
    [DataRow("public static class Benchmarks { [Ankus.PgBenchmark] public static async System.Threading.Tasks.Task Run(Ankus.PgBencher bencher) { } }",
        "ANKUS527", "public static class Benchmarks { [Ankus.PgBenchmark] public static void Run(Ankus.PgBencher bencher) { } }")]
    public async Task SynchronousEntryFixKeepsTheProducedResult(string source, string id, string expected)
    {
        string prefix = FixTypesSource(source);
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace, prefix + source);
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(id, diagnostic.Id);
        CodeAction action = Assert.ContainsSingle(await CodeFixActionsAsync(new SynchronousEntryCodeFixProvider(), document, diagnostic));
        Assert.EndsWith("' synchronous", action.Title);
        ApplyChangesOperation change = Assert.IsInstanceOfType<ApplyChangesOperation>(
            Assert.ContainsSingle(await action.GetOperationsAsync(context.CancellationToken)));
        Document corrected = change.ChangedSolution.GetDocument(document.Id)!;
        Assert.AreEqual(prefix + expected, (await corrected.GetTextAsync(context.CancellationToken)).ToString());
        (Compilation output, ImmutableArray<Diagnostic> after) = await GenerateCodeFixDocumentAsync(corrected);
        Assert.IsEmpty(after);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// An entry that awaits is not offered a correction, and one whose caller awaits it stays unchanged.
    /// </summary>
    /// <param name="source">The entry and its callers.</param>
    /// <param name="offered">Whether an action is offered, which then leaves the source unchanged.</param>
    [TestMethod]
    [DataRow("public static class Functions { [Ankus.PgFunction] public static async System.Threading.Tasks.Task<int> Value() " +
        "{ await System.Threading.Tasks.Task.Yield(); return 1; } }", false)]
    [DataRow("public static class Functions { [Ankus.PgFunction] public static async System.Collections.Generic.IAsyncEnumerable<int> Values() " +
        "{ await foreach (int value in Values()) { yield return value; } } }", false)]
    [DataRow("public static class Functions { [Ankus.PgFunction] public static async System.Threading.Tasks.Task<int> Value() { return 1; } " +
        "public static async System.Threading.Tasks.Task<int> Twice() => await Value() * 2; }", true)]
    public async Task SynchronousEntryFixKeepsAwaitedEntries(string source, bool offered)
    {
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace, source);
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        List<CodeAction> actions = await CodeFixActionsAsync(new SynchronousEntryCodeFixProvider(), document, diagnostic);
        if (!offered)
        {
            Assert.IsEmpty(actions);
            return;
        }

        ApplyChangesOperation change = Assert.IsInstanceOfType<ApplyChangesOperation>(
            Assert.ContainsSingle(await Assert.ContainsSingle(actions).GetOperationsAsync(context.CancellationToken)));
        Assert.AreEqual(source, (await change.ChangedSolution.GetDocument(document.Id)!.GetTextAsync(context.CancellationToken)).ToString());
    }
}
