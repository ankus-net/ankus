using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Invalid schema arguments retain their exact source location through detached graph composition.
    /// </summary>
    /// <param name="name">The invalid schema name.</param>
    /// <param name="id">The specific identifier or reserved-prefix diagnostic.</param>
    [TestMethod]
    [DataRow(null, "ANKUS050")]
    [DataRow("", "ANKUS050")]
    [DataRow("bad\0name", "ANKUS050")]
    [DataRow("🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘🐘", "ANKUS050")]
    [DataRow("pg_reserved", "ANKUS063")]
    [DataRow("pg_", "ANKUS063")]
    public void SchemaDiagnosticsIdentifyTheConstructorValue(string? name, string id)
    {
        string literal = name is null ? "null!" : SymbolDisplay.FormatLiteral(name, quote: true);
        CSharpCompilation input = ModuleCompilation("[Ankus.PgSchema(name: " + literal + ")] public static class Bad; " +
            "[Ankus.PgSchema(\"accepted\")] public static class Good;");
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
        Assert.AreEqual(id, diagnostic.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, diagnostic.Severity);
        Assert.AreEqual(literal, diagnostic.Location.SourceTree!.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan));
        Assert.AreEqual("https://ankus.net/function-declarations/#declaration-diagnostics", diagnostic.Descriptor.HelpLinkUri);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"accepted\";\n", InstallationBody(output));
    }

    /// <summary>
    /// PostgreSQL reserves only the exact lowercase pg_ prefix, so a quoted mixed-case schema is created and inherited.
    /// </summary>
    [TestMethod]
    public void SchemaReservationMatchesPostgresCaseSensitivePrefix()
    {
        (Compilation output, ImmutableArray<Diagnostic> diagnostics) = Generate("[Ankus.PgSchema(\"PG_Stage\")] public static class Functions { " +
            "[Ankus.PgFunction] public static int Value() => 1; }");

        Assert.IsEmpty(diagnostics);
        Assert.StartsWith("CREATE SCHEMA IF NOT EXISTS \"PG_Stage\";\n", InstallationBody(output));
        Assert.Contains("CREATE FUNCTION \"PG_Stage\".\"value\"()", InstallationBody(output));
        Assert.AreEqual("false", ManifestValue(output, "Ankus.Relocatable"));
    }

    /// <summary>
    /// An inherited invalid schema points to its declaration in another partial file and recovers after correction.
    /// </summary>
    [TestMethod]
    public void InheritedSchemaDiagnosticsLocateTheOtherPartialFile()
    {
        const string Declaration = "[Ankus.PgSchema(name: \"\")] public static partial class Functions;";
        SyntaxTree schema = CSharpSyntaxTree.ParseText(Declaration, path: "Schema.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation input = ModuleCompilation("public static partial class Functions { " +
            "[Ankus.PgFunction] public static int Value() => 42; " +
            "[Ankus.PgFunction(Schema = \"fixed\")] public static int Good() => 1; }").AddSyntaxTrees(schema);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Assert.HasCount(2, diagnostics);
        foreach (Diagnostic diagnostic in diagnostics)
        {
            Assert.AreEqual("ANKUS050", diagnostic.Id);
            Assert.AreSame(schema, diagnostic.Location.SourceTree);
            Assert.AreEqual("\"\"", schema.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan));
        }

        Assert.Contains("FUNCTION \"fixed\".\"good\"()", InstallationBody(output));
        Assert.DoesNotContain(".\"value\"()", InstallationBody(output));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));

        SyntaxTree repaired = CSharpSyntaxTree.ParseText(Declaration.Replace("\"\"", "\"repaired\"", StringComparison.Ordinal),
            path: "Schema.cs", cancellationToken: context.CancellationToken);
        driver = RunModule(driver, input.ReplaceSyntaxTree(schema, repaired), out Compilation corrected);
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS \"repaired\";", InstallationBody(corrected));
        Assert.Contains("FUNCTION \"repaired\".\"value\"()", InstallationBody(corrected));
        Assert.Contains("FUNCTION \"fixed\".\"good\"()", InstallationBody(corrected));
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedFunctionDeclaration(driver, "good").Reason);
    }
}
