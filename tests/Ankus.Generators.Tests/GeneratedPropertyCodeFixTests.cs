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
    /// The attribute shared by the setting fix cases.
    /// </summary>
    private const string SettingFixAttribute = "[Ankus.PgGucInt(\"ankus_fix.value\", 1, \"Value\")]";

    /// <summary>
    /// The IDE discovers the generated-property correction through its composition export.
    /// </summary>
    [TestMethod]
    public void GeneratedPropertyFixIsExported()
    {
        using CompositionHost container = new ContainerConfiguration().WithAssembly(typeof(GeneratedPropertyCodeFixProvider).Assembly).CreateContainer();
        GeneratedPropertyCodeFixProvider provider =
            Assert.ContainsSingle(container.GetExports<CodeFixProvider>().OfType<GeneratedPropertyCodeFixProvider>());
        Assert.AreSequenceEqual(["ANKUS170", "ANKUS173", "ANKUS175", "ANKUS312", "ANKUS317"], provider.FixableDiagnosticIds);
        Assert.AreSame(WellKnownFixAllProviders.BatchFixer, provider.GetFixAllProvider());
    }

    /// <summary>
    /// Each correction leaves the static partial definition that Ankus implements, and the setting or callback then generates.
    /// </summary>
    /// <param name="declaration">The container and property before the correction.</param>
    /// <param name="id">The expected diagnostic.</param>
    /// <param name="title">The expected action title.</param>
    /// <param name="expected">The exact corrected declarations.</param>
    [TestMethod]
    [DataRow("public partial class Settings\n{\n    #\n    public partial int Value { get; }\n}\n", "ANKUS170", "Make 'Value' static",
        "public partial class Settings\n{\n    #\n    public static partial int Value { get; }\n}\n")]
    [DataRow("public static partial class Settings\n{\n    #\n    public static int Value { get; }\n}\n", "ANKUS173", "Make 'Value' partial",
        "public static partial class Settings\n{\n    #\n    public static partial int Value { get; }\n}\n")]
    [DataRow("public static partial class Settings\n{\n    #\n    public static partial int Value { get; }\n\n    public static partial int Value => 1;\n}\n",
        "ANKUS175", "Remove the implementation of 'Value'",
        "public static partial class Settings\n{\n    #\n    public static partial int Value { get; }\n}\n")]
    [DataRow("public partial class Functions\n{\n    [Ankus.PgNativeCallback(nameof(Handle))]\n    public partial Hook Callback { get; }\n" +
        "    private static long Handle(int first, long second) => second;\n}\n", "ANKUS312", "Make 'Callback' static",
        "public partial class Functions\n{\n    [Ankus.PgNativeCallback(nameof(Handle))]\n    public static partial Hook Callback { get; }\n" +
        "    private static long Handle(int first, long second) => second;\n}\n")]
    [DataRow("public partial class Functions\n{\n    [Ankus.PgNativeCallback(nameof(Handle))]\n    public static Hook Callback { get; }\n" +
        "    private static long Handle(int first, long second) => second;\n}\n", "ANKUS317", "Make 'Callback' partial",
        "public partial class Functions\n{\n    [Ankus.PgNativeCallback(nameof(Handle))]\n    public static partial Hook Callback { get; }\n" +
        "    private static long Handle(int first, long second) => second;\n}\n")]
    public async Task GeneratedPropertyFixLeavesGeneratedDefinition(string declaration, string id, string title, string expected)
    {
        string prefix = FixTypesSource(declaration);
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace, prefix + declaration.Replace("#", SettingFixAttribute, StringComparison.Ordinal));
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(id, diagnostic.Id);
        CodeAction action = Assert.ContainsSingle(await CodeFixActionsAsync(new GeneratedPropertyCodeFixProvider(), document, diagnostic));
        Assert.AreEqual(title, action.Title);
        ApplyChangesOperation change = Assert.IsInstanceOfType<ApplyChangesOperation>(
            Assert.ContainsSingle(await action.GetOperationsAsync(context.CancellationToken)));
        Document corrected = change.ChangedSolution.GetDocument(document.Id)!;
        Assert.AreEqual(prefix + expected.Replace("#", SettingFixAttribute, StringComparison.Ordinal),
            (await corrected.GetTextAsync(context.CancellationToken)).ToString());
        (Compilation output, ImmutableArray<Diagnostic> after) = await GenerateCodeFixDocumentAsync(corrected);
        Assert.IsEmpty(after);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// No correction is offered where it would need an instance, discard an initializer, or keep an authored getter.
    /// </summary>
    /// <param name="declaration">The container and property.</param>
    /// <param name="id">The expected diagnostic.</param>
    [TestMethod]
    [DataRow("public partial class Settings\n{\n    #\n    public partial int Value { get; }\n    public static int Read() => new Settings().Value;\n}\n", "ANKUS170")]
    [DataRow("public static partial class Settings\n{\n    #\n    public static int Value { get; } = 5;\n}\n", "ANKUS173")]
    [DataRow("public static partial class Settings\n{\n    #\n    public static int Value => 5;\n}\n", "ANKUS173")]
    public async Task GeneratedPropertyFixRequiresAnAutomaticGetter(string declaration, string id)
    {
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace, declaration.Replace("#", SettingFixAttribute, StringComparison.Ordinal));
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(id, diagnostic.Id);
        Assert.IsEmpty(await CodeFixActionsAsync(new GeneratedPropertyCodeFixProvider(), document, diagnostic));
    }
}
