using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Invalid attached declarations report distinct contracts at the exact authored syntax and preserve valid functions.
    /// </summary>
    /// <param name="declaration">The invalid operator or cast declaration.</param>
    /// <param name="id">The independently specified diagnostic code.</param>
    /// <param name="span">The exact value, result or parameter requiring correction.</param>
    [TestMethod]
    [DataRow("[Ankus.PgOperator(\"@+\")] public static System.Collections.Generic.IEnumerable<int> Bad(int value) => [value];", "ANKUS064", "System.Collections.Generic.IEnumerable<int>")]
    [DataRow("[Ankus.PgCast] public static System.Collections.Generic.IEnumerable<int> Bad(int value) => [value];", "ANKUS064", "System.Collections.Generic.IEnumerable<int>")]
    [DataRow("[Ankus.PgOperator(name: \"--\")] public static int Bad(int value) => value;", "ANKUS065", "\"--\"")]
    [DataRow("[Ankus.PgOperator(\"@+\")] public static int Bad() => 1;", "ANKUS066", "()")]
    [DataRow("[Ankus.PgOperator(\"@+\")] public static int Bad(int left, int middle, int right) => left;", "ANKUS066", "(int left, int middle, int right)")]
    [DataRow("[Ankus.PgOperator(\"@+\")] public static int Bad(params int[] values) => 1;", "ANKUS067", "params")]
    [DataRow("[Ankus.PgCast] public static int Bad(params int[] values) => 1;", "ANKUS067", "params")]
    [DataRow("[Ankus.PgOperator(\"@+\")] public static void Bad(int value) { }", "ANKUS068", "void")]
    [DataRow("[Ankus.PgCast] public static void Bad(int value) { }", "ANKUS068", "void")]
    [DataRow("[Ankus.PgOperator(\"@=\", Negator = \"@=\")] public static bool Bad(int value) => true;", "ANKUS069", "\"@=\"")]
    [DataRow("[Ankus.PgOperator(\"@=\", Commutator = \"@=\")] public static bool Bad(int value) => true;", "ANKUS070", "\"@=\"")]
    [DataRow("[Ankus.PgOperator(\"@=\", JoinEstimator = \"eqjoinsel\")] public static bool Bad(int value) => true;", "ANKUS070", "\"eqjoinsel\"")]
    [DataRow("[Ankus.PgOperator(\"@=\", Hashes = true)] public static bool Bad(int value) => true;", "ANKUS070", "true")]
    [DataRow("[Ankus.PgOperator(\"@=\", Merges = true)] public static bool Bad(int value) => true;", "ANKUS070", "true")]
    [DataRow("[Ankus.PgOperator(\"@+\", Negator = \"@-\")] public static int Bad(int left, int right) => left;", "ANKUS071", "\"@-\"")]
    [DataRow("[Ankus.PgOperator(\"@+\", RestrictionEstimator = \"eqsel\")] public static int Bad(int left, int right) => left;", "ANKUS071", "\"eqsel\"")]
    [DataRow("[Ankus.PgOperator(\"@+\", JoinEstimator = \"eqjoinsel\")] public static int Bad(int left, int right) => left;", "ANKUS071", "\"eqjoinsel\"")]
    [DataRow("[Ankus.PgOperator(\"@+\", Hashes = true)] public static int Bad(int left, int right) => left;", "ANKUS071", "true")]
    [DataRow("[Ankus.PgOperator(\"@+\", Merges = true)] public static int Bad(int left, int right) => left;", "ANKUS071", "true")]
    [DataRow("[Ankus.PgOperator(\"@=\", Commutator = \"a.b.c\")] public static bool Bad(int left, int right) => true;", "ANKUS072", "\"a.b.c\"")]
    [DataRow("[Ankus.PgOperator(\"@=\", Negator = \"a.b.c\")] public static bool Bad(int left, int right) => true;", "ANKUS072", "\"a.b.c\"")]
    [DataRow("[Ankus.PgOperator(\"@=\", RestrictionEstimator = \"a.b.c\")] public static bool Bad(int left, int right) => true;", "ANKUS073", "\"a.b.c\"")]
    [DataRow("[Ankus.PgOperator(\"@=\", JoinEstimator = \"a.b.c\")] public static bool Bad(int left, int right) => true;", "ANKUS073", "\"a.b.c\"")]
    [DataRow("[Ankus.PgCast((Ankus.PgCastContext)3)] public static long Bad(int value) => value;", "ANKUS074", "(Ankus.PgCastContext)3")]
    [DataRow("[Ankus.PgCast] public static long Bad() => 1;", "ANKUS075", "()")]
    [DataRow("[Ankus.PgCast] public static long Bad(int value, Ankus.PgMemoryContext context, short modifier) => value;", "ANKUS076", "short")]
    [DataRow("[Ankus.PgCast] public static long Bad(int value, int modifier, bool? explicitConversion) => value;", "ANKUS077", "bool?")]
    [DataRow("[Ankus.PgCast] public static int Bad(Ankus.PgHeapTuple value) => 1;", "ANKUS078", "Ankus.PgHeapTuple")]
    [DataRow("[Ankus.PgCast] public static Ankus.PgHeapTuple Bad(int value) => null!;", "ANKUS078", "Ankus.PgHeapTuple")]
    [DataRow("[Ankus.PgCast] public static int Bad(int value) => value;", "ANKUS079", "int")]
    public void OperatorAndCastErrorsIdentifyTheirAuthoredContract(string declaration, string id, string span)
    {
        CSharpCompilation input = ModuleCompilation("public static class Functions { " + declaration +
            " [Ankus.PgFunction] public static int Good() => 42; }");
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual(id, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(span, diagnostic.Location.SourceTree!.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan));
        int offset = id is "ANKUS069" or "ANKUS070" && span == "\"@=\""
            ? declaration.LastIndexOf(span, StringComparison.Ordinal)
            : declaration.IndexOf(span, StringComparison.Ordinal);
        Assert.AreEqual("public static class Functions { ".Length + offset, diagnostic.Location.SourceSpan.Start);
        Assert.AreEqual("https://ankus.net/operators-and-casts/#declaration-diagnostics", diagnostic.Descriptor.HelpLinkUri);
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        string sql = InstallationBody(output);
        Assert.Contains("FUNCTION \"good\"()", sql);
        Assert.DoesNotContain("CREATE OPERATOR", sql);
        Assert.DoesNotContain("CREATE CAST", sql);
    }

    /// <summary>
    /// C# operators and conversions retain exact authored diagnostic locations without losing compilable backing declarations.
    /// </summary>
    /// <param name="declaration">The invalid PostgreSQL contract on a valid C# special method.</param>
    /// <param name="id">The expected contract diagnostic.</param>
    /// <param name="span">The exact offending syntax.</param>
    [TestMethod]
    [DataRow("[Ankus.PgOperator(\"@+\")] public static System.Collections.Generic.IEnumerable<int> operator +(Metric left, Metric right) => [left.Value];", "ANKUS064", "System.Collections.Generic.IEnumerable<int>")]
    [DataRow("[Ankus.PgOperator(\"@+\", Hashes = true)] public static int operator +(Metric left, Metric right) => left.Value;", "ANKUS071", "true")]
    [DataRow("[Ankus.PgCast((Ankus.PgCastContext)3)] public static implicit operator int(Metric value) => value.Value;", "ANKUS074", "(Ankus.PgCastContext)3")]
    public void OperatorAndCastDiagnosticsSupportCSharpSpecialMethods(string declaration, string id, string span)
    {
        CSharpCompilation input = ModuleCompilation("[Ankus.PgType] public readonly record struct Metric(int Value) { " + declaration + " }");
        Assert.IsEmpty(input.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual(id, diagnostic.Id);
        Assert.AreEqual(span, diagnostic.Location.SourceTree!.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        Assert.DoesNotContain("CREATE OPERATOR @+", InstallationBody(output));
        Assert.DoesNotContain("CREATE CAST", InstallationBody(output));
    }

    /// <summary>
    /// Cached operand and attribute errors follow a later declaration after preceding bodies and compilation order change.
    /// </summary>
    /// <param name="cast">Whether the failure concerns a cast operand instead of an operator option.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OperatorAndCastDiagnosticsRemainCachedAfterEarlierEdits(bool cast)
    {
        string source = "public static class Functions { [Ankus.PgFunction] public static int First() => 41; " +
            (cast ? "[Ankus.PgCast] public static long Bad(int value, Ankus.PgMemoryContext context, int? modifier) => value;" :
                "[Ankus.PgOperator(\"@+\", RestrictionEstimator = \"bad.name.extra\")] public static bool Bad(int value) => true;") + " }";
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out Compilation first,
            out ImmutableArray<Diagnostic> original, context.CancellationToken);
        Diagnostic previous = Assert.ContainsSingle(original);
        string replacement = source.Replace("=> 41;", "=> 4000 + 2;", StringComparison.Ordinal);
        SyntaxTree current = CSharpSyntaxTree.ParseText(replacement, path: "Module.cs", cancellationToken: context.CancellationToken);
        SyntaxTree earlier = CSharpSyntaxTree.ParseText("internal static class Earlier;", path: "Earlier.cs",
            cancellationToken: context.CancellationToken);
        CSharpCompilation edited = initial.RemoveAllSyntaxTrees().AddSyntaxTrees(earlier, current);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        string expected = cast ? "int?" : "\"bad.name.extra\"";

        Assert.AreEqual(cast ? "ANKUS076" : "ANKUS073", diagnostic.Id);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "ExtensionComposition"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "ExtensionProblems"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "OperatorCastEmission"));
        Assert.AreSame(current, diagnostic.Location.SourceTree);
        Assert.AreNotEqual(previous.Location.SourceSpan.Start, diagnostic.Location.SourceSpan.Start);
        Assert.AreEqual(replacement.IndexOf(expected, StringComparison.Ordinal), diagnostic.Location.SourceSpan.Start);
        Assert.AreEqual(expected, current.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan));
        Assert.AreEqual(InstallationBody(first), InstallationBody(output));

        string repaired = cast ? replacement.Replace("int? modifier", "int modifier", StringComparison.Ordinal) :
            replacement.Replace("bad.name.extra", "pg_catalog.eqsel", StringComparison.Ordinal);
        RunModule(driver, edited.ReplaceSyntaxTree(current, CSharpSyntaxTree.ParseText(repaired, path: "Module.cs",
            cancellationToken: context.CancellationToken)), out Compilation corrected);
        Assert.Contains(cast ? "CREATE CAST (integer AS bigint)" : "CREATE OPERATOR @+", InstallationBody(corrected));
    }
}
