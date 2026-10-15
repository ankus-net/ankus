using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Declaration errors identify the specific contract and offending syntax without exporting invalid SQL.
    /// </summary>
    /// <param name="declaration">The invalid entry declaration.</param>
    /// <param name="id">The specific diagnostic identifier.</param>
    /// <param name="span">The exact authored syntax requiring correction.</param>
    [TestMethod]
    [DataRow("[Ankus.PgFunction(Volatility = (Ankus.PgVolatility)3)] public static int Bad() => 1;", "ANKUS045", "(Ankus.PgVolatility)3")]
    [DataRow("[Ankus.PgFunction(ParallelSafety = (Ankus.PgParallelSafety)(-1))] public static int Bad() => 1;", "ANKUS045", "(Ankus.PgParallelSafety)(-1)")]
    [DataRow("[Ankus.PgFunction(NullInput = (Ankus.PgNullInput)3)] public static int Bad() => 1;", "ANKUS045", "(Ankus.PgNullInput)3")]
    [DataRow("[Ankus.PgFunction(Cost = double.Epsilon)] public static int Bad() => 1;", "ANKUS046", "double.Epsilon")]
    [DataRow("[Ankus.PgFunction] public static Ankus.PgAnyElement Bad(int value) => null!;", "ANKUS048", "Ankus.PgAnyElement")]
    [DataRow("[Ankus.PgFunction(NullInput = Ankus.PgNullInput.CalledOnNull)] public static int Bad(int value) => value;", "ANKUS049", "Ankus.PgNullInput.CalledOnNull")]
    [DataRow("[Ankus.PgFunction(Schema = \"\")] public static int Bad() => 1;", "ANKUS050", "\"\"")]
    [DataRow("[Ankus.PgFunction(Rows = 0)] public static System.Collections.Generic.IEnumerable<int> Bad() => [1];", "ANKUS051", "0")]
    [DataRow("[Ankus.PgFunction(SetMode = (Ankus.PgSetMode)3)] public static System.Collections.Generic.IEnumerable<int> Bad() => [1];", "ANKUS052", "(Ankus.PgSetMode)3")]
    [DataRow("[Ankus.PgFunction(Rows = 10)] public static int Bad() => 1;", "ANKUS053", "10")]
    [DataRow("[Ankus.PgFunction(SetMode = Ankus.PgSetMode.ValuePerCall)] public static int Bad() => 1;", "ANKUS053", "Ankus.PgSetMode.ValuePerCall")]
    [DataRow("[Ankus.PgFunction(SupportFunction = \"a.b.c\")] public static int Bad() => 1;", "ANKUS054", "\"a.b.c\"")]
    [DataRow("[Ankus.PgFunction(SearchPath = new[] { \"valid\", \"\" })] public static int Bad() => 1;", "ANKUS055", "\"\"")]
    [DataRow("[Ankus.PgFunction] public static int Bad([Ankus.PgParameter] Ankus.PgFunctionContext call) => 1;", "ANKUS056", "Ankus.PgParameter")]
    [DataRow("[Ankus.PgFunction] public static int Bad([Ankus.PgParameter(Element = \"value\")] int value) => value;", "ANKUS057", "\"value\"")]
    [DataRow("[Ankus.PgFunction] public static int Bad([Ankus.PgParameter(Name = \"\")] int value) => value;", "ANKUS058", "\"\"")]
    [DataRow("[Ankus.PgFunction] public static int Bad(int URL, int Url) => URL;", "ANKUS059", "Url")]
    [DataRow("[Ankus.PgFunction] public static int Bad([Ankus.PgParameter(Default = \" \" )] int value) => value;", "ANKUS060", "\" \"")]
    [DataRow("[Ankus.PgFunction] public static string Bad(string value = \"\\0\") => value;", "ANKUS061", "\"\\0\"")]
    [DataRow("[Ankus.PgFunction] public static int Bad([Ankus.PgParameter(Default = \"42\")] int value, int other) => value;", "ANKUS062", "other")]
    public void FunctionOptionsIdentifyTheirOffendingSyntax(string declaration, string id, string span)
    {
        CSharpCompilation input = ModuleCompilation("public static class Functions { " + declaration +
            " [Ankus.PgFunction] public static int Good() => 42; }");
        ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out Compilation output, out ImmutableArray<Diagnostic> diagnostics,
            context.CancellationToken);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(id, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(span, diagnostic.Location.SourceTree!.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan));
        Assert.AreEqual("https://ankus.net/function-declarations/#declaration-diagnostics", diagnostic.Descriptor.HelpLinkUri);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        string sql = InstallationBody(output);
        Assert.Contains("FUNCTION \"good\"()", sql);
        Assert.DoesNotContain("FUNCTION \"bad\"", sql);
    }

    /// <summary>
    /// Cached option errors retain their exact value location when earlier methods grow and source files are inserted.
    /// </summary>
    [TestMethod]
    public void FunctionOptionDiagnosticsRemainCachedAfterEarlierEdits()
    {
        const string Source = "public static class Functions { [Ankus.PgFunction] public static int Good() => 1; " +
            "[Ankus.PgFunction(Cost = 0)] public static int Bad() => 2; }";
        CSharpCompilation input = ModuleCompilation(Source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out Compilation first,
            out ImmutableArray<Diagnostic> original, context.CancellationToken);
        Diagnostic previous = Assert.ContainsSingle(original);
        string replacement = Source.Replace("=> 1;", "=> 1000;", StringComparison.Ordinal);
        SyntaxTree current = CSharpSyntaxTree.ParseText(replacement, path: "Module.cs", cancellationToken: context.CancellationToken);
        SyntaxTree earlier = CSharpSyntaxTree.ParseText("internal static class Earlier;", path: "Earlier.cs",
            cancellationToken: context.CancellationToken);
        CSharpCompilation edited = input.RemoveAllSyntaxTrees().AddSyntaxTrees(earlier, current);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual("ANKUS046", diagnostic.Id);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "ExtensionComposition"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "ExtensionProblems"));
        Assert.AreSame(current, diagnostic.Location.SourceTree);
        Assert.AreNotEqual(previous.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Start);
        Assert.AreEqual(replacement.IndexOf("Cost = 0", StringComparison.Ordinal) + "Cost = ".Length, diagnostic.Location.SourceSpan.Start);
        Assert.AreEqual("0", current.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan));
        Assert.AreEqual(InstallationBody(first), InstallationBody(output));
        Assert.Contains("FUNCTION \"good\"()", InstallationBody(output));
        Assert.DoesNotContain("FUNCTION \"bad\"", InstallationBody(output));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
    }
}
