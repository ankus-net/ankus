using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

/// <summary>
/// Distinguishes invalid SQL identifiers from conflicting PostgreSQL input signatures.
/// </summary>
public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Malformed names receive only the identifier rule, without misleading collision advice.
    /// </summary>
    /// <param name="name">The malformed identifier.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("9bad")]
    [DataRow("bad-name")]
    [DataRow("Bad")]
    [DataRow("has'quote")]
    [DataRow("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    public void InvalidNameDiagnosticDescribesOnlyIdentifierRules(string name)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Functions { [Ankus.PgFunction(Name = " +
            SymbolDisplay.FormatLiteral(name, quote: true) + ")] public static int Value() => 1; }");

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS002", diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual("SQL name '" + name + "' must start with a lowercase ASCII letter or underscore and contain 1-63 lowercase ASCII letters, digits, or underscores",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual("https://ankus.net/function-declarations/#function-names", diagnostic.Descriptor.HelpLinkUri);
    }

    /// <summary>
    /// Collisions report the exact SQL input signature at the rejected declaration, including CLR aliases and return-shape differences.
    /// </summary>
    /// <param name="second">The second method with a colliding input identity.</param>
    [TestMethod]
    [DataRow("public static int B(int value) => value;")]
    [DataRow("public static int? B(int? value) => value;")]
    [DataRow("public static long B(Ankus.PgFunctionContext context, int value) => value;")]
    [DataRow("public static System.Collections.Generic.IEnumerable<int> B(int value) => new[] { value };")]
    [DataRow("public static System.Collections.Generic.IEnumerable<(int Id, string Label)> B(int value) => System.Array.Empty<(int, string)>();")]
    public void DuplicateSignatureDiagnosticNamesConflictingSqlIdentity(string second)
    {
        const string First = "public static class First { [Ankus.PgFunction(Name = \"echo\")] public static int A(int value) => value; }";
        string rejected = "[Ankus.PgFunction(Name = \"echo\")] " + second;
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(First + "public static class Second { " + rejected + " }");

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS207", diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual("SQL input signature '\"echo\"(integer)' is already declared in this extension; use a different SQL name, schema, or input parameter types",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual("B", diagnostic.Location.SourceTree!.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan));
        Assert.AreEqual(First.Length + "public static class Second { ".Length + rejected.IndexOf(" B(", StringComparison.Ordinal) + 1,
            diagnostic.Location.SourceSpan.Start);
        Assert.AreEqual("https://ankus.net/function-declarations/#function-names", diagnostic.Descriptor.HelpLinkUri);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        Assert.ContainsSingle(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers());
    }

    /// <summary>
    /// SQL identities remain distinct by name, schema, and mapped input type.
    /// </summary>
    /// <param name="declaration">The second independently callable function.</param>
    [TestMethod]
    [DataRow("[Ankus.PgFunction(Name = \"other\")] public static int B(int value) => value;")]
    [DataRow("[Ankus.PgFunction(Name = \"echo\", Schema = \"other\")] public static int B(int value) => value;")]
    [DataRow("[Ankus.PgFunction(Name = \"echo\")] public static long B(long value) => value;")]
    public void NonconflictingSqlIdentitiesRemainCallable(string declaration)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "public static class First { [Ankus.PgFunction(Name = \"echo\")] public static int A(int value) => value; }" +
            "public static class Second { " + declaration + " }");

        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        Assert.HasCount(2, compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!.GetMembers());
    }

    /// <summary>
    /// Aggregate and generated-helper collisions use the same PostgreSQL identity diagnostic as ordinary functions.
    /// </summary>
    /// <param name="name">The ordinary function's colliding SQL name.</param>
    /// <param name="parameters">Its managed input declaration.</param>
    /// <param name="signature">The exact conflicting SQL identity.</param>
    [TestMethod]
    [DataRow("sum", "int value", "\"sum\"(integer)")]
    [DataRow("sum_transition", "int state, int value", "\"sum_transition\"(integer,integer)")]
    public void AggregateCollisionDiagnosticIncludesFullInputSignature(string name, string parameters, string signature)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("public static class Other { [Ankus.PgFunction(Name = \"" + name +
            "\")] public static int A(" + parameters + ") => value; }" +
            "[Ankus.PgAggregate] public sealed class Sum : Ankus.IPgAggregate<int,int> { " +
            "public static int Transition(Ankus.PgAggregateContext context,int state,int value) => state; }");

        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS207", diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual("SQL input signature '" + signature + "' is already declared in this extension; use a different SQL name, schema, or input parameter types",
            diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsTrue(diagnostic.Location.IsInSource);
    }
}
