using System.Collections.Immutable;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Inline, tracked and replacement SQL retains exact line endings inside PostgreSQL quoted values.
    /// </summary>
    /// <param name="kind">The authored SQL input path.</param>
    /// <param name="value">The exact quoted text value.</param>
    [TestMethod]
    [DataRow("inline", "A\rB")]
    [DataRow("inline", "A\r\nB")]
    [DataRow("inline", "A\nB")]
    [DataRow("file", "A\rB")]
    [DataRow("file", "A\r\nB")]
    [DataRow("file", "A\nB")]
    [DataRow("replacement", "A\rB")]
    [DataRow("replacement", "A\r\nB")]
    [DataRow("replacement", "A\nB")]
    public void AuthoredSqlKeepsQuotedLineEndings(string kind, string value)
    {
        string project = Path.Join(AppContext.BaseDirectory, "sql-text-contracts");
        string sql = "SELECT '" + value + "';";
        string literal = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(sql, quote: true);
        string source = kind switch
        {
            "inline" => "[assembly: Ankus.PgSql(\"seed\", " + literal + ", Relocatable = true)]",
            "file" => "[assembly: Ankus.PgSqlFile(\"seed\", \"seed.sql\", Relocatable = true)]",
            _ => "public static class Functions { [Ankus.PgFunction(Sql = " + literal +
                ", SqlRelocatable = true)] public static int F() => 1; }",
        };
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source,
            kind == "file" ? [new SqlInput(Path.Join(project, "seed.sql"), sql)] : [], new SqlOptions(project));
        Assert.IsEmpty(errors);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static error => error.Severity >= DiagnosticSeverity.Warning));
        Assert.AreEqual(sql + "\n", InstallationBody(output));
        Assert.AreEqual("true", ManifestValue(output, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Generated declarations preserve authored default expressions and escape enum-label line endings.
    /// </summary>
    /// <param name="enumeration">Whether the declaration emits an enum label instead of a parameter default.</param>
    /// <param name="value">The exact authored text.</param>
    [TestMethod]
    [DataRow(false, "A\rB")]
    [DataRow(false, "A\r\nB")]
    [DataRow(false, "A\nB")]
    [DataRow(true, "A\rB")]
    [DataRow(true, "A\r\nB")]
    [DataRow(true, "A\nB")]
    public void GeneratedSqlKeepsAuthoredTextLineEndings(bool enumeration, string value)
    {
        string literal = Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(enumeration ? value : "'" + value + "'", quote: true);
        string source = enumeration ? "[Ankus.PgEnum] public enum Labelled { [Ankus.PgEnumLabel(" + literal + ")] Value }" :
            "public static class Functions { [Ankus.PgFunction] public static string F([Ankus.PgParameter(Default = " + literal +
            ")] string value) => value; }";
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source);
        Assert.IsEmpty(errors);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static error => error.Severity >= DiagnosticSeverity.Warning));
        string sql = InstallationBody(output);
        if (enumeration)
        {
            string escaped = value.Replace("\r", "\\r").Replace("\n", "\\n");
            Assert.AreEqual("CREATE TYPE \"labelled\" AS ENUM (E'" + escaped + "');\n", sql);
        }
        else
        {
            Assert.Contains("DEFAULT ('" + value + "')", sql);
            Assert.HasCount(value.Count(static character => character == '\r'), sql.Where(static character => character == '\r'));
        }
    }
}
