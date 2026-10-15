using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

/// <summary>
/// Maps the upstream mutable borrowed-array argument rejection to concrete managed reference-passing contracts.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// The upstream mutable-borrow rejection applies to concrete array arguments and highlights the actual passing modifier.
    /// </summary>
    /// <param name="passing">The unsupported managed reference-passing form.</param>
    /// <param name="element">The exact scalar element contract.</param>
    [TestMethod]
    [DataRow("ref", "int")]
    [DataRow("out", "int")]
    [DataRow("in", "int")]
    [DataRow("ref readonly", "int")]
    [DataRow("ref", "Ankus.PgTextView?")]
    [DataRow("out", "Ankus.PgTextView?")]
    [DataRow("in", "Ankus.PgTextView?")]
    [DataRow("ref readonly", "Ankus.PgTextView?")]
    public void ConcreteBorrowedArraysRejectReferencePassing(string passing, string element)
    {
        string parameter = passing + " Ankus.PgArrayView<" + element + "> values";
        string body = passing == "out" ? "{ values = default!; return 42; }" : "=> values.Count;";
        CSharpCompilation initial = ModuleCompilation("public static class Functions { [Ankus.PgFunction] public static int Read(" +
            parameter + ") " + body + " }");
        Assert.IsEmpty(initial.GetDiagnostics(context.CancellationToken).Where(static error => error.Severity == DiagnosticSeverity.Error));
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual("ANKUS038", error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreSame(initial.SyntaxTrees.Single(), error.Location.SourceTree);
        Assert.AreEqual(parameter, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.Contains("Parameter 'values' uses '" + passing + "'", error.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual("https://ankus.net/getting-started/functions/#function-signatures", error.Descriptor.HelpLinkUri);
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
        Assert.AreSequenceEqual(["Pg_magic_func"], ManifestValue(output, "Ankus.Exports").Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(output));

        string repairedSource = "public static class Functions { [Ankus.PgFunction] public static int Read(Ankus.PgArrayView<" +
            element + "> values) => values.Count; public static int Answer() => Read(default!); }";
        RunModule(driver, initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(repairedSource,
            path: "Module.cs", cancellationToken: context.CancellationToken)), out Compilation repaired);
        Assert.Contains("CREATE FUNCTION", InstallationBody(repaired));
        Assert.Contains(element == "int" ? "integer[]" : "text[]", InstallationBody(repaired));
        Assert.IsEmpty(repaired.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }
}
