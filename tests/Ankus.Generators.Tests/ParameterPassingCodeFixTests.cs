using System.Collections.Immutable;
using System.Composition.Hosting;
using Ankus.CodeFixes;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.Text;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// The IDE discovers the parameter correction and its project-verified batch fix through the composition export.
    /// </summary>
    [TestMethod]
    public void ParameterPassingFixIsExported()
    {
        using CompositionHost container = new ContainerConfiguration().WithAssembly(typeof(ParameterPassingCodeFixProvider).Assembly).CreateContainer();
        ParameterPassingCodeFixProvider provider =
            Assert.ContainsSingle(container.GetExports<CodeFixProvider>().OfType<ParameterPassingCodeFixProvider>());
        Assert.AreSequenceEqual(["ANKUS038", "ANKUS216", "ANKUS259", "ANKUS298", "ANKUS341"], provider.FixableDiagnosticIds);
        Assert.AreNotSame(WellKnownFixAllProviders.BatchFixer, provider.GetFixAllProvider());
        Assert.AreSame(provider.GetFixAllProvider(), provider.GetFixAllProvider());
    }

    /// <summary>
    /// A read-only reference becomes a value parameter, callers stop passing a reference, and the function then registers.
    /// </summary>
    /// <param name="declaration">The functions and callers before the correction.</param>
    /// <param name="expected">The exact corrected source.</param>
    [TestMethod]
    [DataRow("""
        public static class Functions
        {
            [Ankus.PgFunction]
            public static int Twice(in int value) => value * 2;

            public static int Call(int local) => Twice(in local) + Twice(local) + Twice(3);
        }
        """, """
        public static class Functions
        {
            [Ankus.PgFunction]
            public static int Twice(int value) => value * 2;

            public static int Call(int local) => Twice(local) + Twice(local) + Twice(3);
        }
        """)]
    [DataRow("""
        public static class Functions
        {
            [Ankus.PgFunction]
            public static int Twice(scoped ref readonly int value) => value * 2;

            public static int Call(int local) => Twice(value: ref local) + Twice(
                in local);
        }
        """, """
        public static class Functions
        {
            [Ankus.PgFunction]
            public static int Twice(int value) => value * 2;

            public static int Call(int local) => Twice(value: local) + Twice(
                local);
        }
        """)]
    [DataRow("""
        public static class Functions
        {
            [Ankus.PgFunction]
            public static int Twice(
                [Ankus.PgParameter(Name = "input")] in int value,
                in int other) => value * 2 + other;
        }
        """, """
        public static class Functions
        {
            [Ankus.PgFunction]
            public static int Twice(
                [Ankus.PgParameter(Name = "input")] int value,
                in int other) => value * 2 + other;
        }
        """)]
    [DataRow("""
        public static partial class Functions
        {
            [Ankus.PgFunction]
            public static partial int Twice(in int value);

            public static partial int Twice(in int value) => value * 2;

            public static int Call(int local) => Twice(in local);
        }
        """, """
        public static partial class Functions
        {
            [Ankus.PgFunction]
            public static partial int Twice(int value);

            public static partial int Twice(int value) => value * 2;

            public static int Call(int local) => Twice(local);
        }
        """)]
    public async Task ParameterFixPassesReadOnlyReferenceByValue(string declaration, string expected)
    {
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace, declaration);
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS038", diagnostic.Id);
        CodeAction action = Assert.ContainsSingle(await CodeFixActionsAsync(new ParameterPassingCodeFixProvider(), document, diagnostic));
        Assert.AreEqual("Pass 'value' by value", action.Title);
        ApplyChangesOperation change = Assert.IsInstanceOfType<ApplyChangesOperation>(
            Assert.ContainsSingle(await action.GetOperationsAsync(context.CancellationToken)));
        Document corrected = change.ChangedSolution.GetDocument(document.Id)!;
        Assert.AreEqual(expected, (await corrected.GetTextAsync(context.CancellationToken)).ToString());
        (Compilation output, ImmutableArray<Diagnostic> after) = await GenerateCodeFixDocumentAsync(corrected);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        if (!expected.Contains("in int other", StringComparison.Ordinal))
        {
            Assert.IsEmpty(after);
            Assert.Contains("CREATE FUNCTION \"twice\"(\"value\" integer)", InstallationBody(output));
        }
        else
        {
            // The generator reports one parameter at a time, so the next one surfaces only after this correction.
            Diagnostic remaining = Assert.ContainsSingle(after);
            Assert.AreEqual("ANKUS038", remaining.Id);
            Assert.AreEqual("in int other", remaining.Location.SourceTree!.GetText(context.CancellationToken).ToString(remaining.Location.SourceSpan));
        }
    }

    /// <summary>
    /// Trigger and event callback contexts, worker arguments, backend test contexts and native callback handler arguments
    /// get the same correction, and each declaration then generates.
    /// </summary>
    /// <param name="source">The declaration before the correction.</param>
    /// <param name="name">The diagnosed parameter.</param>
    /// <param name="expected">The exact corrected declaration.</param>
    [TestMethod]
    [DataRow("public static class Functions { [Ankus.PgEventTrigger] public static void Audit(in Ankus.PgEventTriggerContext context) { } }", "context",
        "public static class Functions { [Ankus.PgEventTrigger] public static void Audit(Ankus.PgEventTriggerContext context) { } }")]
    [DataRow("public static class Workers { [Ankus.PgBackgroundWorker] public static void Run(in nuint argument) { } }", "argument",
        "public static class Workers { [Ankus.PgBackgroundWorker] public static void Run(nuint argument) { } }")]
    [DataRow("public static partial class Checks { [Ankus.PgTest] public static void Test(ref readonly Ankus.PgFunctionContext call) { } }", "call",
        "public static partial class Checks { [Ankus.PgTest] public static void Test(Ankus.PgFunctionContext call) { } }")]
    [DataRow("public partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } " +
        "private static long Handle(in int first, long second) => second; }", "first",
        "public partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } " +
        "private static long Handle(int first, long second) => second; }")]
    public async Task ParameterFixCoversEntryKinds(string source, string name, string expected)
    {
        string prefix = FixTypesSource(source);
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace, prefix + source);
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        CodeAction action = Assert.ContainsSingle(await CodeFixActionsAsync(new ParameterPassingCodeFixProvider(), document, diagnostic));
        Assert.AreEqual($"Pass '{name}' by value", action.Title);
        ApplyChangesOperation change = Assert.IsInstanceOfType<ApplyChangesOperation>(
            Assert.ContainsSingle(await action.GetOperationsAsync(context.CancellationToken)));
        Document corrected = change.ChangedSolution.GetDocument(document.Id)!;
        Assert.AreEqual(prefix + expected, (await corrected.GetTextAsync(context.CancellationToken)).ToString());
        (Compilation output, ImmutableArray<Diagnostic> after) = await GenerateCodeFixDocumentAsync(corrected);
        Assert.IsEmpty(after);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// A writable reference has no by-value equivalent because the caller would stop seeing the method's writes.
    /// </summary>
    /// <param name="parameter">The diagnosed parameter.</param>
    [TestMethod]
    [DataRow("ref int value")]
    [DataRow("out int value")]
    public async Task ParameterFixIgnoresWritableReferences(string parameter)
    {
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace,
            $"public static class Functions {{ [Ankus.PgFunction] public static int Apply({parameter}) {{ value = 42; return value; }} }}");
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS038", diagnostic.Id);
        Assert.IsEmpty(await CodeFixActionsAsync(new ParameterPassingCodeFixProvider(), document, diagnostic));
    }

    /// <summary>
    /// The correction leaves source unchanged when a delegate, overload or interface still needs the reference.
    /// </summary>
    /// <param name="members">The diagnosed function and the member that depends on its signature.</param>
    [TestMethod]
    [DataRow("[Ankus.PgFunction] public static int Apply(in int value) => value; public delegate int Reader(in int value); public static Reader Read => Apply;")]
    [DataRow("[Ankus.PgFunction] public static int Apply(in int value) => value; public static int Apply(int value) => value + 1;")]
    [DataRow("[Ankus.PgFunction] public static unsafe int Apply(in int value) => value; public static unsafe delegate*<in int, int> Pointer => &Apply;")]
    public async Task ParameterFixKeepsDependentSignatures(string members)
    {
        using var workspace = new AdhocWorkspace();
        string source = $"public static class Functions {{ {members} }}";
        Document document = CreateCodeFixDocument(workspace, source);
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(static value => value.Id == "ANKUS038", diagnostics);
        CodeAction action = Assert.ContainsSingle(await CodeFixActionsAsync(new ParameterPassingCodeFixProvider(), document, diagnostic));
        ApplyChangesOperation change = Assert.IsInstanceOfType<ApplyChangesOperation>(
            Assert.ContainsSingle(await action.GetOperationsAsync(context.CancellationToken)));
        Assert.AreEqual(source, (await change.ChangedSolution.GetDocument(document.Id)!.GetTextAsync(context.CancellationToken)).ToString());
    }

    /// <summary>
    /// Fixing every parameter in a project edits callers in other documents and leaves unrelated source untouched.
    /// </summary>
    [TestMethod]
    public async Task ParameterFixAllUpdatesCallersAcrossDocuments()
    {
        using var workspace = new AdhocWorkspace();
        Document first = CreateCodeFixDocument(workspace, """
            public static class First
            {
                [Ankus.PgFunction]
                public static int One(in int value) => value + 1;

                [Ankus.PgFunction]
                public static int Two(ref readonly long value) => (int)value + 2;
            }
            """);
        Document caller = workspace.AddDocument(first.Project.Id, "Caller.cs", SourceText.From("""
            public static class Caller
            {
                public static int Call(int small, long large) => First.One(in small) + First.Two(in large) + First.Two(ref large);
            }
            """));
        const string OtherSource = "// Keep this source exactly.\ninternal static class Other { internal static int Read(in int value) => value; }\n";
        Document untouched = workspace.AddDocument(first.Project.Id, "Other.cs", SourceText.From(OtherSource));
        first = workspace.CurrentSolution.GetDocument(first.Id)!;
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(first);
        Assert.HasCount(2, diagnostics);
        Assert.IsTrue(diagnostics.All(static diagnostic => diagnostic.Id == "ANKUS038"));
        var provider = new ParameterPassingCodeFixProvider();
        var fixContext = new FixAllContext(first, provider, FixAllScope.Project, nameof(ParameterPassingCodeFixProvider),
            provider.FixableDiagnosticIds, new ParameterDiagnosticProvider(diagnostics), context.CancellationToken);
        CodeAction? action = await provider.GetFixAllProvider().GetFixAsync(fixContext);
        Assert.IsNotNull(action);
        ApplyChangesOperation change = Assert.IsInstanceOfType<ApplyChangesOperation>(
            Assert.ContainsSingle(await action.GetOperationsAsync(context.CancellationToken)));
        Document corrected = change.ChangedSolution.GetDocument(first.Id)!;
        (Compilation output, ImmutableArray<Diagnostic> after) = await GenerateCodeFixDocumentAsync(corrected);

        Assert.IsEmpty(after);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        Assert.Contains("CREATE FUNCTION \"one\"(\"value\" integer)", InstallationBody(output));
        Assert.Contains("CREATE FUNCTION \"two\"(\"value\" bigint)", InstallationBody(output));
        Assert.Contains("public static int Call(int small, long large) => First.One(small) + First.Two(large) + First.Two(large);",
            (await change.ChangedSolution.GetDocument(caller.Id)!.GetTextAsync(context.CancellationToken)).ToString());
        Assert.AreEqual(OtherSource, (await change.ChangedSolution.GetDocument(untouched.Id)!.GetTextAsync(context.CancellationToken)).ToString());
    }

    /// <summary>
    /// Supplies the actual generator results to the project batch fixer.
    /// </summary>
    /// <param name="diagnostics">Diagnostics from the unchanged project compilation.</param>
    private sealed class ParameterDiagnosticProvider(ImmutableArray<Diagnostic> diagnostics) : FixAllContext.DiagnosticProvider
    {
        /// <summary>
        /// Selects the diagnostics attached to the requested syntax tree.
        /// </summary>
        public override async Task<IEnumerable<Diagnostic>> GetDocumentDiagnosticsAsync(Document document, CancellationToken cancellationToken)
        {
            SyntaxTree? tree = await document.GetSyntaxTreeAsync(cancellationToken);
            return diagnostics.Where(diagnostic => ReferenceEquals(diagnostic.Location.SourceTree, tree));
        }

        /// <summary>
        /// Returns no source-free project diagnostics because parameter diagnostics always have a source location.
        /// </summary>
        public override Task<IEnumerable<Diagnostic>> GetProjectDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<Diagnostic>>([]);

        /// <summary>
        /// Returns all diagnosed parameters in the original compilation.
        /// </summary>
        public override Task<IEnumerable<Diagnostic>> GetAllDiagnosticsAsync(Project project, CancellationToken cancellationToken)
            => Task.FromResult<IEnumerable<Diagnostic>>(diagnostics);
    }
}
