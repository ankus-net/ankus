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
    /// The IDE discovers the declaration correction through its composition export.
    /// </summary>
    [TestMethod]
    public void DeclarationModifierFixIsExported()
    {
        using CompositionHost container = new ContainerConfiguration().WithAssembly(typeof(DeclarationModifierCodeFixProvider).Assembly).CreateContainer();
        DeclarationModifierCodeFixProvider provider =
            Assert.ContainsSingle(container.GetExports<CodeFixProvider>().OfType<DeclarationModifierCodeFixProvider>());
        Assert.AreSequenceEqual(["ANKUS033", "ANKUS034", "ANKUS174", "ANKUS179"], provider.FixableDiagnosticIds);
        Assert.AreSame(WellKnownFixAllProviders.BatchFixer, provider.GetFixAllProvider());
    }

    /// <summary>
    /// An instance entry method that reads no instance state becomes static, after its accessibility, and then generates.
    /// </summary>
    /// <param name="member">The diagnosed method.</param>
    /// <param name="expected">The exact corrected method.</param>
    [TestMethod]
    [DataRow("public int Add(int a, int b) => a + b;", "public static int Add(int a, int b) => a + b;")]
    [DataRow("internal unsafe int Add(int a, int b) => a + b;", "internal static unsafe int Add(int a, int b) => a + b;")]
    [DataRow("public int Add(int a, int b) { int sum = a + b; return sum; }", "public static int Add(int a, int b) { int sum = a + b; return sum; }")]
    [DataRow("public int Add(int a, int b) { System.Func<int, int> twice = value => value * 2; return twice(a) + b; }",
        "public static int Add(int a, int b) { System.Func<int, int> twice = value => value * 2; return twice(a) + b; }")]
    [DataRow("public int Add(int a, int b) => new Box { Value = a }.Value + b + nameof(Count).Length;",
        "public static int Add(int a, int b) => new Box { Value = a }.Value + b + nameof(Count).Length;")]
    public async Task StaticFixMakesEntryMethodStatic(string member, string expected)
    {
        using var workspace = new AdhocWorkspace();
        Document corrected = await ApplyFunctionFixAsync(workspace, FunctionFixSource(member), "ANKUS033", "Make 'Add' static");
        Assert.AreEqual(FunctionFixSource(expected), (await corrected.GetTextAsync(context.CancellationToken)).ToString());
        await AssertFunctionFixGeneratesAsync(corrected);
    }

    /// <summary>
    /// Adding static would not compile when the method needs a receiver, overrides, or has an instance caller.
    /// </summary>
    /// <param name="member">The diagnosed method and any caller.</param>
    [TestMethod]
    [DataRow("public int Add(int a, int b) => Count + a + b;")]
    [DataRow("public int Add(int a, int b) => this.Count + a + b;")]
    [DataRow("public int Add(int a, int b) => GetHashCode() + a + b;")]
    [DataRow("public int Add(int a, int b) { System.Func<int> read = () => Count; return read() + a + b; }")]
    [DataRow("public int Add(int a, int b) => a + b; public static int Call() => new Functions().Add(1, 2);")]
    [DataRow("public override int GetHashCode() => 42;")]
    public async Task StaticFixRequiresNoInstanceReceiver(string member)
    {
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace, FunctionFixSource(member));
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS033", diagnostic.Id);
        Assert.IsEmpty(await CodeFixActionsAsync(new DeclarationModifierCodeFixProvider(), document, diagnostic));
    }

    /// <summary>
    /// A private, protected or unstated entry method becomes internal in place of its stated accessibility.
    /// </summary>
    /// <param name="member">The diagnosed method.</param>
    /// <param name="expected">The exact corrected method.</param>
    [TestMethod]
    [DataRow("private static int Add(int a, int b) => a + b;", "internal static int Add(int a, int b) => a + b;")]
    [DataRow("static int Add(int a, int b) => a + b;", "internal static int Add(int a, int b) => a + b;")]
    [DataRow("private protected static int Add(int a, int b) => a + b;", "internal static int Add(int a, int b) => a + b;")]
    [DataRow("protected static unsafe int Add(int a, int b) => a + b;", "internal static unsafe int Add(int a, int b) => a + b;")]
    public async Task AccessFixMakesEntryMethodInternal(string member, string expected)
    {
        using var workspace = new AdhocWorkspace();
        Document corrected = await ApplyFunctionFixAsync(workspace, FunctionFixSource(member), "ANKUS034", "Make 'Add' internal");
        Assert.AreEqual(FunctionFixSource(expected), (await corrected.GetTextAsync(context.CancellationToken)).ToString());
        await AssertFunctionFixGeneratesAsync(corrected);
    }

    /// <summary>
    /// A hidden containing type becomes internal on every partial declaration that states accessibility, keeping indentation.
    /// </summary>
    /// <param name="declarations">The nested type declarations.</param>
    /// <param name="expected">The exact corrected declarations.</param>
    [TestMethod]
    [DataRow("    private sealed class Inner\n    {\n        [Ankus.PgFunction]\n        public static int Add(int a, int b) => a + b;\n    }\n",
        "    internal sealed class Inner\n    {\n        [Ankus.PgFunction]\n        public static int Add(int a, int b) => a + b;\n    }\n")]
    [DataRow("    class Inner\n    {\n        [Ankus.PgFunction]\n        public static int Add(int a, int b) => a + b;\n    }\n",
        "    internal class Inner\n    {\n        [Ankus.PgFunction]\n        public static int Add(int a, int b) => a + b;\n    }\n")]
    [DataRow("    private partial class Inner\n    {\n        [Ankus.PgFunction]\n        public static int Add(int a, int b) => a + b;\n    }\n" +
        "    private partial class Inner\n    {\n        public static int Twice(int value) => value * 2;\n    }\n",
        "    internal partial class Inner\n    {\n        [Ankus.PgFunction]\n        public static int Add(int a, int b) => a + b;\n    }\n" +
        "    internal partial class Inner\n    {\n        public static int Twice(int value) => value * 2;\n    }\n")]
    public async Task AccessFixMakesContainingTypeInternal(string declarations, string expected)
    {
        using var workspace = new AdhocWorkspace();
        Document corrected = await ApplyFunctionFixAsync(workspace, "public static class Outer\n{\n" + declarations + "}\n", "ANKUS034",
            "Make 'Inner' internal");
        Assert.AreEqual("public static class Outer\n{\n" + expected + "}\n", (await corrected.GetTextAsync(context.CancellationToken)).ToString());
        await AssertFunctionFixGeneratesAsync(corrected);
    }

    /// <summary>
    /// A hidden GUC property or containing class becomes internal and then registers its setting.
    /// </summary>
    /// <param name="source">The consumer declarations.</param>
    /// <param name="id">The expected diagnostic.</param>
    /// <param name="title">The expected action title.</param>
    /// <param name="expected">The exact corrected declarations.</param>
    [TestMethod]
    [DataRow("public static partial class Settings\n{\n    [Ankus.PgGucInt(\"ankus_fix.value\", 1, \"Value\")]\n    private static partial int Value { get; }\n}\n",
        "ANKUS174", "Make 'Value' internal",
        "public static partial class Settings\n{\n    [Ankus.PgGucInt(\"ankus_fix.value\", 1, \"Value\")]\n    internal static partial int Value { get; }\n}\n")]
    [DataRow("public static partial class Outer\n{\n    private static partial class Settings\n    {\n" +
        "        [Ankus.PgGucInt(\"ankus_fix.value\", 1, \"Value\")]\n        public static partial int Value { get; }\n    }\n}\n",
        "ANKUS179", "Make 'Settings' internal",
        "public static partial class Outer\n{\n    internal static partial class Settings\n    {\n" +
        "        [Ankus.PgGucInt(\"ankus_fix.value\", 1, \"Value\")]\n        public static partial int Value { get; }\n    }\n}\n")]
    public async Task AccessFixMakesSettingInternal(string source, string id, string title, string expected)
    {
        using var workspace = new AdhocWorkspace();
        Document corrected = await ApplyFunctionFixAsync(workspace, source, id, title);
        Assert.AreEqual(expected, (await corrected.GetTextAsync(context.CancellationToken)).ToString());
        (Compilation output, ImmutableArray<Diagnostic> after) = await GenerateCodeFixDocumentAsync(corrected);
        Assert.IsEmpty(after);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Builds a consumer whose single diagnosed entry method follows the function attribute.
    /// </summary>
    /// <param name="member">The method and any companion members.</param>
    /// <returns>The complete source.</returns>
    private static string FunctionFixSource(string member)
        => "public sealed class Box\n{\n    public int Value { get; set; }\n}\n\npublic class Functions\n{\n    public int Count;\n\n" +
            "    [Ankus.PgFunction]\n    " + member + "\n}\n";

    /// <summary>
    /// Applies the only action registered for the single expected generator diagnostic.
    /// </summary>
    /// <param name="workspace">The workspace owning the consumer.</param>
    /// <param name="source">The consumer source.</param>
    /// <param name="id">The expected diagnostic.</param>
    /// <param name="title">The expected action title.</param>
    /// <returns>The corrected document.</returns>
    private async Task<Document> ApplyFunctionFixAsync(AdhocWorkspace workspace, string source, string id, string title)
    {
        Document document = CreateCodeFixDocument(workspace, source);
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(id, diagnostic.Id);
        CodeAction action = Assert.ContainsSingle(await CodeFixActionsAsync(new DeclarationModifierCodeFixProvider(), document, diagnostic));
        Assert.AreEqual(title, action.Title);
        ImmutableArray<CodeActionOperation> operations = await action.GetOperationsAsync(context.CancellationToken);
        ApplyChangesOperation change = Assert.IsInstanceOfType<ApplyChangesOperation>(Assert.ContainsSingle(operations));
        return change.ChangedSolution.GetDocument(document.Id)!;
    }

    /// <summary>
    /// Requires the corrected source to generate without diagnostics or compiler errors.
    /// </summary>
    /// <param name="corrected">The corrected document.</param>
    /// <returns>The verification operation.</returns>
    private async Task AssertFunctionFixGeneratesAsync(Document corrected)
    {
        (Compilation output, ImmutableArray<Diagnostic> after) = await GenerateCodeFixDocumentAsync(corrected);
        Assert.IsEmpty(after);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.Contains("CREATE FUNCTION \"add\"", InstallationBody(output));
    }
}
