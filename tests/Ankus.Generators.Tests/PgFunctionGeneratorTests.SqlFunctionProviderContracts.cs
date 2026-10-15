using System.Collections.Immutable;
using Ankus.PgConfig;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Invalid provider values identify the exact argument and independently correctable text or ownership contract.
    /// </summary>
    /// <param name="field">The constructor field to invalidate.</param>
    /// <param name="expression">The exact authored C# constant expression.</param>
    /// <param name="expected">The independently required diagnostic identity.</param>
    [TestMethod]
    [DataRow("block", "null!", "ANKUS370")]
    [DataRow("block", "\"\"", "ANKUS370")]
    [DataRow("block", "\" \\t\"", "ANKUS370")]
    [DataRow("block", "\"a\\0b\"", "ANKUS371")]
    [DataRow("block", "\"\\uD800\"", "ANKUS372")]
    [DataRow("block", "\"\\uDC00\"", "ANKUS372")]
    [DataRow("block", "\"missing\"", "ANKUS373")]
    [DataRow("signature", "null!", "ANKUS374")]
    [DataRow("signature", "\"\"", "ANKUS374")]
    [DataRow("signature", "\" \\t\"", "ANKUS374")]
    [DataRow("signature", "\"f\\0()\"", "ANKUS375")]
    [DataRow("signature", "\"\\uD800()\"", "ANKUS376")]
    [DataRow("signature", "\"\\uDC00()\"", "ANKUS376")]
    public void SqlFunctionProviderInputFailuresPointAtAuthoredValues(string field, string expression, string expected)
    {
        string source = "[assembly: Ankus.PgSql(\"sql\", \"SELECT 1;\")]\n[assembly: Ankus.PgSqlFunctionProvider(" +
            (field == "block" ? expression + ", \"f()\"" : "\"sql\", " + expression) + ")]";
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source);
        AssertSqlFunctionProviderError(output, errors, expression, expected);
    }

    /// <summary>
    /// Named, reordered and mixed arguments keep their semantic field locations.
    /// </summary>
    /// <param name="block">Whether the SQL block argument is invalid.</param>
    /// <param name="order">The independently valid constructor spelling.</param>
    [TestMethod]
    [DataRow(false, "reverse")]
    [DataRow(true, "reverse")]
    [DataRow(false, "mixed")]
    [DataRow(true, "mixed")]
    [DataRow(false, "signatureNamed")]
    [DataRow(true, "signatureNamed")]
    public void SqlFunctionProviderNamedArgumentsKeepSemanticLocations(bool block, string order)
    {
        const string Invalid = "\"a\\0b\"";
        string sqlId = block ? Invalid : "\"sql\"";
        string signature = block ? "\"f()\"" : Invalid;
        string arguments = order switch
        {
            "reverse" => "signature: " + signature + ", sqlId: " + sqlId,
            "mixed" => "sqlId: " + sqlId + ", " + signature,
            _ => sqlId + ", signature: " + signature,
        };
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(
            "[assembly: Ankus.PgSql(\"sql\", \"SELECT 1;\")]\n[assembly: Ankus.PgSqlFunctionProvider(" + arguments + ")]");
        AssertSqlFunctionProviderError(output, errors, Invalid, block ? "ANKUS371" : "ANKUS375");
    }

    /// <summary>
    /// Duplicate exact signatures identify the second claim even when its block and argument spelling differ.
    /// </summary>
    /// <param name="otherBlock">Whether the second provider selects another valid block.</param>
    /// <param name="signature">The exact catalog signature claimed twice.</param>
    [TestMethod]
    [DataRow(false, "f(integer)")]
    [DataRow(true, "f(integer)")]
    [DataRow(true, "\"café\".\"function\"(text)")]
    public void SqlFunctionProviderDuplicateClaimsIdentifySecondSignature(bool otherBlock, string signature)
    {
        string literal = SymbolDisplay.FormatLiteral(signature, quote: true);
        string source = "[assembly: Ankus.PgSql(\"first\", \"SELECT 1;\")]\n" +
            "[assembly: Ankus.PgSql(\"second\", \"SELECT 2;\")]\n" +
            "[assembly: Ankus.PgSqlFunctionProvider(\"first\", " + literal + ")]\n" +
            "[assembly: Ankus.PgSqlFunctionProvider(signature: " + literal + ", sqlId: \"" + (otherBlock ? "second" : "first") + "\")]";
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source);
        AssertSqlFunctionProviderError(output, errors, literal, "ANKUS377");
        Diagnostic error = Assert.ContainsSingle(errors);
        string current = error.Location.SourceTree!.GetText(context.CancellationToken).ToString();
        Assert.AreEqual(current.LastIndexOf(literal, StringComparison.Ordinal), error.Location.SourceSpan.Start);
        Assert.Contains(signature, error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// A declaration dependency alias cannot stand in for a custom SQL block.
    /// </summary>
    /// <param name="schema">Whether the existing alias identifies a schema rather than a function.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SqlFunctionProviderRejectsOtherDeclarationAliases(bool schema)
    {
        string source = "[assembly: Ankus.PgSqlFunctionProvider(\"alias\", \"f()\")]\n" +
            (schema ? "[Ankus.PgSchema(\"app\", Id=\"alias\")] public static class Functions {}" :
                "public static class Functions { [Ankus.PgFunction(Id=\"alias\")] public static int Answer() => 42; }");
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source);
        AssertSqlFunctionProviderError(output, errors, "\"alias\"", "ANKUS373");
    }

    /// <summary>
    /// Exact Unicode, quoting and overload spelling remain trusted authored inventory with unchanged SQL and managed behavior.
    /// </summary>
    /// <param name="block">The exact custom SQL dependency identity.</param>
    /// <param name="signature">The exact authored SQL signature.</param>
    [TestMethod]
    [DataRow("a name.with punctuation", "\"café\".\"echo\"(text)")]
    [DataRow("\uD7FF", "f(integer)")]
    [DataRow("\uD800\uDC00", "\"\uDBFF\uDFFF\"()")]
    [DataRow(" a ", "F(text)")]
    public void SqlFunctionProviderValidInputsPreserveExactInventory(string block, string signature)
    {
        string blockLiteral = SymbolDisplay.FormatLiteral(block, quote: true);
        string signatureLiteral = SymbolDisplay.FormatLiteral(signature, quote: true);
        const string Sql = "SELECT 'exact text';";
        string source = "[assembly: Ankus.PgSql(" + blockLiteral + ", \"" + Sql + "\", Relocatable=true)]\n" +
            "[assembly: Ankus.PgSqlFunctionProvider(sqlId: " + blockLiteral + ", signature: " + signatureLiteral + ")]\n" +
            "public static class Functions { [Ankus.PgFunction] public static int Answer() => 42; }";
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source);
        Assert.IsEmpty(errors);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(output, "Ankus.SqlGraph"));
        ExtensionSchemaItem supplied = Assert.ContainsSingle(graph.Items.Where(static item => item.Kind == "sql"));
        Assert.Contains(block, supplied.Names);
        Assert.Contains(signature, supplied.Names);
        Assert.AreEqual("FUNCTION " + signature, Assert.ContainsSingle(supplied.Attachments));
        int start = supplied.Sql.IndexOf("\n\n", StringComparison.Ordinal) + 2;
        int end = supplied.Sql.LastIndexOf("\n/* </end connected objects> */", StringComparison.Ordinal);
        Assert.AreEqual(Sql, supplied.Sql[start..end]);
        Assert.AreEqual("true", ManifestValue(output, "Ankus.Relocatable"));
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(output));
    }

    /// <summary>
    /// Verifies actionable identity, exact editor location and rejection of incomplete installation metadata.
    /// </summary>
    /// <param name="output">The actual generated compilation.</param>
    /// <param name="errors">The actual generator diagnostics.</param>
    /// <param name="expression">The exact authored expression requiring correction.</param>
    /// <param name="expected">The independently expected rule identity.</param>
    private void AssertSqlFunctionProviderError(Compilation output, ImmutableArray<Diagnostic> errors, string expression, string expected)
    {
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual(expected, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(expression, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual("https://ankus.net/custom-sql/#function-provider-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        Assert.DoesNotContain(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
            attribute.ConstructorArguments[0].Value is "Ankus.Sql" or "Ankus.SqlGraph", output.Assembly.GetAttributes());
    }
}
