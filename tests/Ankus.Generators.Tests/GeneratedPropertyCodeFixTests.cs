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
        Assert.AreSequenceEqual(["ANKUS170", "ANKUS173", "ANKUS175", "ANKUS312", "ANKUS317", "ANKUS318", "ANKUS171", "ANKUS316", "ANKUS183", "ANKUS190",
            "ANKUS192", "ANKUS195"], provider.FixableDiagnosticIds);
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
    [DataRow("public partial class Functions\n{\n    [Ankus.PgNativeCallback(nameof(Handle))]\n    public static partial Hook Callback { get; }\n" +
        "    public static partial Hook Callback => default;\n    private static long Handle(int first, long second) => second;\n}\n",
        "ANKUS318", "Remove the implementation of 'Callback'",
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

    /// <summary>
    /// A setting property takes the accessor, type and nullability its attribute requires, and reversed bounds are
    /// swapped; the setting then generates.
    /// </summary>
    /// <param name="source">The settings before the correction.</param>
    /// <param name="id">The expected diagnostic.</param>
    /// <param name="title">The expected action title.</param>
    /// <param name="expected">The exact corrected settings.</param>
    [TestMethod]
    [DataRow("public static partial class Settings { # public static partial int Value { get; set; } }",
        "ANKUS171", "Remove the 'set' accessor", "public static partial class Settings { # public static partial int Value { get; } }")]
    [DataRow("public static partial class Settings\n{\n    #\n    public static partial int Value\n    {\n        get;\n        private set;\n    }\n}\n",
        "ANKUS171", "Remove the 'set' accessor",
        "public static partial class Settings\n{\n    #\n    public static partial int Value\n    {\n        get;\n    }\n}\n")]
    [DataRow("public static partial class Settings { # public static partial int Value { set; } }",
        "ANKUS171", "Replace 'set' with 'get'", "public static partial class Settings { # public static partial int Value { get; } }")]
    [DataRow("public partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; set; } " +
        "private static long Handle(int first, long second) => second; }",
        "ANKUS316", "Remove the 'set' accessor",
        "public partial class Functions { [Ankus.PgNativeCallback(nameof(Handle))] public static partial Hook Callback { get; } " +
        "private static long Handle(int first, long second) => second; }")]
    [DataRow("public static partial class Settings { # public static partial long Value { get; } }",
        "ANKUS183", "Change the type to 'int'", "public static partial class Settings { # public static partial int Value { get; } }")]
    [DataRow("public static partial class Settings { [Ankus.PgGucBool(\"demo.value\", true, \"Value\")] public static partial bool? Value { get; } }",
        "ANKUS183", "Change the type to 'bool'",
        "public static partial class Settings { [Ankus.PgGucBool(\"demo.value\", true, \"Value\")] public static partial bool Value { get; } }")]
    [DataRow("public enum Mode { Fast, Slow } public static partial class Settings { [Ankus.PgGucEnum(\"demo.mode\", Mode.Fast, \"Mode\")] " +
        "public static partial int Value { get; } }",
        "ANKUS183", "Change the type to 'Mode'",
        "public enum Mode { Fast, Slow } public static partial class Settings { [Ankus.PgGucEnum(\"demo.mode\", Mode.Fast, \"Mode\")] " +
        "public static partial Mode Value { get; } }")]
    [DataRow("public static partial class Settings { [Ankus.PgGucString(\"demo.value\", null, \"Value\")] public static partial object Value { get; } }",
        "ANKUS183", "Change the type to 'string?'",
        "public static partial class Settings { [Ankus.PgGucString(\"demo.value\", null, \"Value\")] public static partial string? Value { get; } }")]
    [DataRow("public static partial class Settings { [Ankus.PgGucString(\"demo.value\", null, \"Value\")] public static partial string Value { get; } }",
        "ANKUS195", "Change the type to 'string?'",
        "public static partial class Settings { [Ankus.PgGucString(\"demo.value\", null, \"Value\")] public static partial string? Value { get; } }")]
    [DataRow("public static partial class Settings { [Ankus.PgGucInt(\"demo.value\", 5, \"Value\", Minimum = 10, Maximum = 1)] public static partial int Value { get; } }",
        "ANKUS190", "Swap 'Minimum' and 'Maximum'",
        "public static partial class Settings { [Ankus.PgGucInt(\"demo.value\", 5, \"Value\", Minimum = 1, Maximum = 10)] public static partial int Value { get; } }")]
    [DataRow("public static partial class Settings { [Ankus.PgGucReal(\"demo.value\", 0.5, \"Value\", Maximum = 0.0, Minimum = 1.0)] public static partial double Value { get; } }",
        "ANKUS192", "Swap 'Minimum' and 'Maximum'",
        "public static partial class Settings { [Ankus.PgGucReal(\"demo.value\", 0.5, \"Value\", Maximum = 1.0, Minimum = 0.0)] public static partial double Value { get; } }")]
    public async Task SettingPropertyFixFollowsItsAttribute(string source, string id, string title, string expected)
    {
        string prefix = FixTypesSource(source);
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace, prefix + source.Replace("#", SettingFixAttribute, StringComparison.Ordinal));
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
    /// A setter that other code assigns, or a type that other code relies on, stays in place, and a nullable annotation is
    /// not offered where annotations are disabled.
    /// </summary>
    /// <param name="source">The settings and their uses.</param>
    /// <param name="id">The expected diagnostic.</param>
    /// <param name="offered">Whether an action is offered, which then leaves the source unchanged.</param>
    [TestMethod]
    [DataRow("public static partial class Settings { # public static partial int Value { get; set; } public static void Reset() => Value = 2; }",
        "ANKUS171", true)]
    [DataRow("public static partial class Settings { # public static partial string Value { get; } public static int Length() => Value.Length; }",
        "ANKUS183", true)]
    [DataRow("#nullable disable\npublic static partial class Settings { [Ankus.PgGucString(\"demo.value\", null, \"Value\")] public static partial string Value { get; } }",
        "ANKUS195", false)]
    public async Task SettingPropertyFixKeepsCodeThatDependsOnIt(string source, string id, bool offered)
    {
        string text = source.Replace("# ", SettingFixAttribute + " ", StringComparison.Ordinal);
        using var workspace = new AdhocWorkspace();
        Document document = CreateCodeFixDocument(workspace, text);
        (_, ImmutableArray<Diagnostic> diagnostics) = await GenerateCodeFixDocumentAsync(document);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(id, diagnostic.Id);
        List<CodeAction> actions = await CodeFixActionsAsync(new GeneratedPropertyCodeFixProvider(), document, diagnostic);
        if (!offered)
        {
            Assert.IsEmpty(actions);
            return;
        }

        ApplyChangesOperation change = Assert.IsInstanceOfType<ApplyChangesOperation>(
            Assert.ContainsSingle(await Assert.ContainsSingle(actions).GetOperationsAsync(context.CancellationToken)));
        Assert.AreEqual(text, (await change.ChangedSolution.GetDocument(document.Id)!.GetTextAsync(context.CancellationToken)).ToString());
    }
}
