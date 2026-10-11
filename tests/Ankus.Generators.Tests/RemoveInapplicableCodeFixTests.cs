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
    /// The IDE discovers the removal correction through its composition export.
    /// </summary>
    [TestMethod]
    public void RemoveInapplicableFixIsExported()
    {
        using CompositionHost container = new ContainerConfiguration().WithAssembly(typeof(RemoveInapplicableCodeFixProvider).Assembly).CreateContainer();
        RemoveInapplicableCodeFixProvider provider =
            Assert.ContainsSingle(container.GetExports<CodeFixProvider>().OfType<RemoveInapplicableCodeFixProvider>());
        Assert.HasCount(16, provider.FixableDiagnosticIds);
        Assert.AreSame(WellKnownFixAllProviders.BatchFixer, provider.GetFixAllProvider());
    }

    /// <summary>
    /// An attribute or option that cannot apply is removed with the surrounding layout and comments intact, and the
    /// declaration then generates.
    /// </summary>
    /// <param name="source">The declaration before the correction.</param>
    /// <param name="id">The expected diagnostic.</param>
    /// <param name="title">The expected action title.</param>
    /// <param name="expected">The exact corrected declaration.</param>
    [TestMethod]
    [DataRow("public static class Functions { [Ankus.PgFunction(Rows = 10)] public static int F() => 1; }",
        "ANKUS053", "Remove 'Rows'",
        "public static class Functions { [Ankus.PgFunction] public static int F() => 1; }")]
    [DataRow("public static class Functions { [Ankus.PgFunction(SetMode = Ankus.PgSetMode.Materialize, Cost = 2)] public static int F() => 1; }",
        "ANKUS053", "Remove 'SetMode'",
        "public static class Functions { [Ankus.PgFunction(Cost = 2)] public static int F() => 1; }")]
    [DataRow("public static class Functions { [Ankus.PgOperator(\"@=\", Commutator = \"@=\")] public static bool Bad(int value) => true; }",
        "ANKUS070", "Remove 'Commutator'",
        "public static class Functions { [Ankus.PgOperator(\"@=\")] public static bool Bad(int value) => true; }")]
    [DataRow("public static class Functions { [Ankus.PgOperator(\"@+\", Negator = \"@-\")] public static int Bad(int left, int right) => left; }",
        "ANKUS071", "Remove 'Negator'",
        "public static class Functions { [Ankus.PgOperator(\"@+\")] public static int Bad(int left, int right) => left; }")]
    [DataRow("public static class Functions { [Ankus.PgModuleLoad, System.Diagnostics.Conditional(\"DEBUG\")] public static void Load() { } }",
        "ANKUS245", "Remove 'System.Diagnostics.Conditional'",
        "public static class Functions { [Ankus.PgModuleLoad] public static void Load() { } }")]
    [DataRow("public static class Functions { [Ankus.PgInitialize, System.Runtime.InteropServices.UnmanagedCallersOnly] public static void Start() { } }",
        "ANKUS246", "Remove 'System.Runtime.InteropServices.UnmanagedCallersOnly'",
        "public static class Functions { [Ankus.PgInitialize] public static void Start() { } }")]
    [DataRow("public static class Functions { [Ankus.PgInitialize] [return: Ankus.PgNumericPrecision(5)] public static void Start() { } }",
        "ANKUS248", "Remove 'Ankus.PgNumericPrecision'",
        "public static class Functions { [Ankus.PgInitialize] public static void Start() { } }")]
    [DataRow("public static class Workers { [Ankus.PgBackgroundWorker, System.Diagnostics.Conditional(\"DEBUG\")] public static void Run(nuint argument) { } }",
        "ANKUS266", "Remove 'System.Diagnostics.Conditional'",
        "public static class Workers { [Ankus.PgBackgroundWorker] public static void Run(nuint argument) { } }")]
    [DataRow("public static class Workers { [Ankus.PgBackgroundWorker, System.Runtime.InteropServices.UnmanagedCallersOnly] public static void Run(nuint argument) { } }",
        "ANKUS267", "Remove 'System.Runtime.InteropServices.UnmanagedCallersOnly'",
        "public static class Workers { [Ankus.PgBackgroundWorker] public static void Run(nuint argument) { } }")]
    [DataRow("public static class Workers { [Ankus.PgBackgroundWorker] [return: Ankus.PgNumericPrecision(5)] public static void Run(nuint value) { } }",
        "ANKUS270", "Remove 'Ankus.PgNumericPrecision'",
        "public static class Workers { [Ankus.PgBackgroundWorker] public static void Run(nuint value) { } }")]
    [DataRow("public static class Workers { [Ankus.PgBackgroundWorker] public static void Run([Ankus.PgParameter(Name = \"item\")] nuint value) { } }",
        "ANKUS271", "Remove 'Ankus.PgParameter'",
        "public static class Workers { [Ankus.PgBackgroundWorker] public static void Run(nuint value) { } }")]
    [DataRow("public static class Functions { [Ankus.PgFunction, System.Diagnostics.Conditional(\"DEBUG\")] public static void F() { } }",
        "ANKUS277", "Remove 'System.Diagnostics.Conditional'",
        "public static class Functions { [Ankus.PgFunction] public static void F() { } }")]
    [DataRow("public partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } [System.Runtime.InteropServices.UnmanagedCallersOnly] private static long Handle(int first, long second) => second; }",
        "ANKUS343", "Remove 'System.Runtime.InteropServices.UnmanagedCallersOnly'",
        "public partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } private static long Handle(int first, long second) => second; }")]
    [DataRow("public unsafe class Plugins { [Ankus.PgOutputPlugin, System.Runtime.InteropServices.UnmanagedCallersOnly] public static void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } }",
        "ANKUS524", "Remove 'System.Runtime.InteropServices.UnmanagedCallersOnly'",
        "public unsafe class Plugins { [Ankus.PgOutputPlugin] public static void Init(Ankus.Postgres.OutputPluginCallbacks* callbacks) { } }")]
    [DataRow("public static class Functions { [Ankus.PgTrigger] [return: Ankus.PgNumericPrecision(5, 2)] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }",
        "ANKUS225", "Remove 'Ankus.PgNumericPrecision'",
        "public static class Functions { [Ankus.PgTrigger] public static Ankus.PgHeapTuple? Audit(Ankus.PgTriggerContext context) => null; }")]
    [DataRow("public static class Functions\n{\n    /// <summary>\n    /// Loads.\n    /// </summary>\n    [System.Diagnostics.Conditional(\"DEBUG\")]\n    [Ankus.PgModuleLoad]\n    public static void Load() { }\n}\n",
        "ANKUS245", "Remove 'System.Diagnostics.Conditional'",
        "public static class Functions\n{\n    /// <summary>\n    /// Loads.\n    /// </summary>\n    [Ankus.PgModuleLoad]\n    public static void Load() { }\n}\n")]
    public async Task RemoveInapplicableFixRemovesTheDiagnosedElement(string source, string id, string title, string expected)
    {
        string prefix = FixTypesSource(source);
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace, prefix + source);
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics.Where(value => value.Id == id));
        CodeAction action = Assert.ContainsSingle(await CodeFixActionsAsync(new RemoveInapplicableCodeFixProvider(), document, diagnostic));
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
    /// Removing UnmanagedCallersOnly would break a function pointer that still needs it, so the source stays unchanged.
    /// </summary>
    [TestMethod]
    public async Task RemoveInapplicableFixKeepsAttributesOtherCodeNeeds()
    {
        const string Source = "public static unsafe class Functions { [Ankus.PgInitialize, System.Runtime.InteropServices.UnmanagedCallersOnly] " +
            "public static void Start() { } public static delegate* unmanaged<void> Pointer => &Start; }";
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace, Source);
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics.Where(static value => value.Id == "ANKUS246"));
        CodeAction action = Assert.ContainsSingle(await CodeFixActionsAsync(new RemoveInapplicableCodeFixProvider(), document, diagnostic));
        ApplyChangesOperation change = Assert.IsInstanceOfType<ApplyChangesOperation>(
            Assert.ContainsSingle(await action.GetOperationsAsync(context.CancellationToken)));
        Assert.AreEqual(Source, (await change.ChangedSolution.GetDocument(document.Id)!.GetTextAsync(context.CancellationToken)).ToString());
    }
}
