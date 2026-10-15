using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Backend-test validation distinguishes each independently correctable declaration or metadata failure.
    /// </summary>
    /// <param name="source">The invalid but compiler-valid declaration.</param>
    /// <param name="expected">The independently expected diagnostic.</param>
    /// <param name="highlight">The precise authored cause.</param>
    [TestMethod]
    [DataRow("public partial class Checks { [Ankus.PgTest] public void Test() { } }", "ANKUS291", "Test")]
    [DataRow("public partial class Checks { [Ankus.PgTest] public static async void Test() { await System.Threading.Tasks.Task.Yield(); } }", "ANKUS292", "async")]
    [DataRow("public partial class Checks { [Ankus.PgTest] public static void Test<T>() { } }", "ANKUS293", "Test")]
    [DataRow("public partial interface Checks { [Ankus.PgTest] public static abstract void Test(); }", "ANKUS294", "abstract")]
    [DataRow("public partial class Checks { [Ankus.PgTest] public static int Test() => 7; }", "ANKUS295", "int")]
    [DataRow("public partial class Checks { [Ankus.PgTest] public static System.Threading.Tasks.Task Test() => System.Threading.Tasks.Task.CompletedTask; }", "ANKUS295", "System.Threading.Tasks.Task")]
    [DataRow("public partial class Checks { [Ankus.PgTest] private static void Test() { } }", "ANKUS296", "Test")]
    [DataRow("public partial class Checks { [Ankus.PgTest] protected static void Test() { } }", "ANKUS296", "Test")]
    [DataRow("public partial class Checks { [Ankus.PgTest] private protected static void Test() { } }", "ANKUS296", "Test")]
    [DataRow("public partial class Checks { [Ankus.PgTest] public static void Test(int value) { } }", "ANKUS297", "value")]
    [DataRow("public partial class Checks { [Ankus.PgTest] public static void Test(Ankus.PgFunctionContext call, int value) { } }", "ANKUS297", "value")]
    [DataRow("public partial class Checks { [Ankus.PgTest] public static void Test(ref Ankus.PgFunctionContext call) { } }", "ANKUS298", "call")]
    [DataRow("public partial class Checks { [Ankus.PgTest] public static void Test(in Ankus.PgMemoryContext owner) { } }", "ANKUS298", "owner")]
    [DataRow("public partial class Checks { [Ankus.PgTest, Ankus.PgFunction] public static void Test() { } }", "ANKUS299", "Ankus.PgFunction")]
    [DataRow("public partial class Checks { [Ankus.PgTest, Ankus.PgOperator(\"+\")] public static void Test() { } }", "ANKUS299", "Ankus.PgOperator(\"+\")")]
    [DataRow("public partial class Checks { [Ankus.PgTest, Ankus.PgCast] public static void Test() { } }", "ANKUS299", "Ankus.PgCast")]
    [DataRow("public partial class Checks { [Ankus.PgTest, Ankus.PgTrigger] public static void Test() { } }", "ANKUS299", "Ankus.PgTrigger")]
    [DataRow("public partial class Checks { [Ankus.PgTest, Ankus.PgEventTrigger] public static void Test() { } }", "ANKUS299", "Ankus.PgEventTrigger")]
    [DataRow("public partial class Checks { [Ankus.PgTest, Ankus.PgInitialize] public static void Test() { } }", "ANKUS299", "Ankus.PgInitialize")]
    [DataRow("public partial class Checks { [Ankus.PgTest, Ankus.PgModuleLoad] public static void Test() { } }", "ANKUS299", "Ankus.PgModuleLoad")]
    [DataRow("public partial class Checks { [Ankus.PgTest, Ankus.PgBackgroundWorker] public static void Test() { } }", "ANKUS299", "Ankus.PgBackgroundWorker")]
    [DataRow("public partial struct Checks { [Ankus.PgTest] public static void Test() { } }", "ANKUS300", "Checks")]
    [DataRow("public partial class Checks<T> { [Ankus.PgTest] public static void Test() { } }", "ANKUS301", "Checks")]
    [DataRow("public partial class Outer<T> { public partial class Checks { [Ankus.PgTest] public static void Test() { } } }", "ANKUS301", "Outer")]
    [DataRow("file partial class Checks { [Ankus.PgTest] public static void Test() { } }", "ANKUS302", "Checks")]
    [DataRow("public partial class Outer { private partial class Checks { [Ankus.PgTest] public static void Test() { } } }", "ANKUS303", "Checks")]
    [DataRow("public partial class Outer { protected partial class Checks { [Ankus.PgTest] public static void Test() { } } }", "ANKUS303", "Checks")]
    [DataRow("public class Checks { [Ankus.PgTest] public static void Test() { } }", "ANKUS304", "Checks")]
    [DataRow("public class Outer { public partial class Checks { [Ankus.PgTest] public static void Test() { } } }", "ANKUS304", "Outer")]
    [DataRow("public partial class PostgresTests { [Ankus.PgTest] public static void Test() { } }", "ANKUS305", "PostgresTests")]
    [DataRow("public partial class Checks { public static int PostgresTests => 1; [Ankus.PgTest] public static void Test() { } }", "ANKUS306", "PostgresTests")]
    [DataRow("public class Base { public static int PostgresTests => 1; } public partial class Checks : Base { [Ankus.PgTest] public static void Test() { } }", "ANKUS306", "PostgresTests")]
    [DataRow("public partial class Checks { [Ankus.PgTest(ExpectedError = \"bad\\0text\")] public static void Test() { } }", "ANKUS307", "\"bad\\0text\"")]
    [DataRow("public partial class Checks { [Ankus.PgTest(ExpectedError = \"\\ud800\")] public static void Test() { } }", "ANKUS307", "\"\\ud800\"")]
    [DataRow("public partial class Checks { [Ankus.PgTest(ExpectedError = \"\\udc00\")] public static void Test() { } }", "ANKUS307", "\"\\udc00\"")]
    [DataRow("public partial class Checks { [Ankus.PgTest(IgnoreReason = \"bad\\0text\")] public static void Test() { } }", "ANKUS308", "\"bad\\0text\"")]
    [DataRow("public partial class Checks { [Ankus.PgTest(IgnoreReason = \"\\ud800\")] public static void Test() { } }", "ANKUS308", "\"\\ud800\"")]
    [DataRow("public partial class Checks { [Ankus.PgTest(IgnoreReason = \"\\udc00\")] public static void Test() { } }", "ANKUS308", "\"\\udc00\"")]
    [DataRow("public partial class Checks { [Ankus.PgTest(IgnoreReason = \"\")] public static void Test() { } }", "ANKUS309", "\"\"")]
    [DataRow("public partial class Checks { [Ankus.PgTest(IgnoreReason = \" \")] public static void Test() { } }", "ANKUS309", "\" \"")]
    [DataRow("public partial class Checks { [Ankus.PgTest(IgnoreReason = \"\\t\\r\\n\")] public static void Test() { } }", "ANKUS309", "\"\\t\\r\\n\"")]
    public void BackendTestFailuresHaveSpecificDiagnostics(string source, string expected, string highlight)
    {
        CSharpCompilation compilation = ModuleCompilation(source);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        MethodDeclarationSyntax syntax = Assert.ContainsSingle(compilation.SyntaxTrees.SelectMany(tree => tree.GetRoot(context.CancellationToken)
            .DescendantNodes().OfType<MethodDeclarationSyntax>()).Where(static method => method.Identifier.ValueText == "Test"));
        IMethodSymbol method = compilation.GetSemanticModel(syntax.SyntaxTree).GetDeclaredSymbol(syntax, context.CancellationToken)!;
        var errors = new List<Diagnostic>();
        var diagnostics = new GeneratorDiagnostics((descriptor, location, arguments) =>
            errors.Add(Diagnostic.Create(descriptor, location, arguments)), context.CancellationToken);
        Assert.IsNull(PgTestDeclaration.Create(method, diagnostics));
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(highlight, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.Contains("'Test'", error.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual("https://ankus.net/getting-started/testing/#declaration-diagnostics", error.Descriptor.HelpLinkUri);
    }

    /// <summary>
    /// Invalid metadata does not drop a valid sibling catalog or unrelated SQL, with native tests both included and excluded.
    /// </summary>
    /// <param name="enabled">The native test publication switch.</param>
    /// <param name="option">The invalid metadata expression.</param>
    /// <param name="expected">Its dedicated diagnostic.</param>
    [TestMethod]
    [DataRow("true", "ExpectedError = \"bad\\0text\"", "ANKUS307")]
    [DataRow("false", "ExpectedError = \"bad\\0text\"", "ANKUS307")]
    [DataRow("true", "IgnoreReason = \"bad\\0text\"", "ANKUS308")]
    [DataRow("false", "IgnoreReason = \"bad\\0text\"", "ANKUS308")]
    [DataRow("true", "IgnoreReason = \" \"", "ANKUS309")]
    [DataRow("false", "IgnoreReason = \" \"", "ANKUS309")]
    public void PreciseBackendTestMetadataErrorsPreserveSiblings(string enabled, string option, string expected)
    {
        string source = "public static partial class Checks { [Ankus.PgTest(" + option + ")] public static void Invalid() { } " +
            "[Ankus.PgTest] public static void Healthy() { } } public static class Functions { [Ankus.PgFunction] public static int Answer() => 42; }";
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source, options: new BackendOptions(enabled));
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual(option[(option.IndexOf('=') + 2)..], error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        PgTestCase healthy = Assert.ContainsSingle(ReadBackendCatalog(output, "Checks+PostgresTests"));
        Assert.EndsWith(".Healthy()", healthy.Name);
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(output));
        Assert.AreEqual(enabled == "true", ManifestValue(output, "Ankus.Exports").Contains(healthy.FunctionName, StringComparison.Ordinal));
        Assert.AreEqual(enabled == "true", InstallationBody(output).Contains(healthy.FunctionName, StringComparison.Ordinal));
    }

    /// <summary>
    /// Empty expected text, null options and exact Unicode remain valid discovery metadata.
    /// </summary>
    /// <param name="options">The valid attribute options.</param>
    /// <param name="expected">The exact expected-error text.</param>
    /// <param name="ignored">The exact ignore reason.</param>
    [TestMethod]
    [DataRow("", null, null)]
    [DataRow("ExpectedError = \"\"", "", null)]
    [DataRow("ExpectedError = null, IgnoreReason = null", null, null)]
    [DataRow("ExpectedError = \"café\\n\\\"expected\\\"\", IgnoreReason = \"理由 😀\"", "café\n\"expected\"", "理由 😀")]
    public void BackendTestDiagnosticMetadataControlsRemainExact(string options, string? expected, string? ignored)
    {
        string source = "public partial class Checks { [Ankus.PgTest(" + options + ")] public static void Healthy(Ankus.PgFunctionContext call, Ankus.PgMemoryContext owner) { } }";
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source, options: new BackendOptions("true"));
        Assert.IsEmpty(errors);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        PgTestCase healthy = Assert.ContainsSingle(ReadBackendCatalog(output, "Checks+PostgresTests"));
        Assert.AreEqual(expected, healthy.ExpectedError);
        Assert.AreEqual(ignored, healthy.IgnoreReason);
        Assert.Contains(healthy.FunctionName, InstallationBody(output));
        Assert.Contains(healthy.FunctionName, ManifestValue(output, "Ankus.Exports"));
    }

    /// <summary>
    /// An inherited catalog conflict from a real dependency points to the authored test instead of foreign or missing source.
    /// </summary>
    /// <param name="portable">Whether the independent dependency is an emitted metadata image.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void ReferencedBackendCatalogConflictHasAuthoredLocation(bool portable)
    {
        CSharpCompilation library = ModuleCompilation("namespace Imported { public class Base { public static int PostgresTests => 1; } }")
            .WithAssemblyName("ReferencedBackendCatalog");
        MetadataReference reference = portable ? MetadataReference.CreateFromImage(EmitDatumMappingImage(library)) : library.ToMetadataReference();
        CSharpCompilation input = ModuleCompilation("public partial class Checks : Imported.Base { [Ankus.PgTest] public static void Test() { } }")
            .AddReferences(reference);
        Assert.IsEmpty(input.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        GeneratorDriver driver = PgTestDriver(true).RunGeneratorsAndUpdateCompilation(input, out Compilation output,
            out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual("ANKUS306", error.Id);
        Assert.AreSame(input.SyntaxTrees.Single(), error.Location.SourceTree);
        Assert.AreEqual("Test", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.Contains("choose another test container", error.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
        Assert.IsNull(output.GetTypeByMetadataName("Checks+PostgresTests"));
    }
}
