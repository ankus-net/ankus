using System.Collections.Immutable;
using Ankus.PgConfig;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Empty inline and tracked SQL remains an ordered dependency node without restoring or inventing statements.
    /// </summary>
    /// <param name="file">Whether the anchor selects a tracked SQL file.</param>
    /// <param name="sql">The exact empty, whitespace or comment-only SQL.</param>
    [TestMethod]
    [DataRow(false, "")]
    [DataRow(false, " \t")]
    [DataRow(false, "\r\n")]
    [DataRow(false, "-- anchor only")]
    [DataRow(true, "")]
    [DataRow(true, " \t")]
    [DataRow(true, "\r\n")]
    [DataRow(true, "-- anchor only")]
    public void EmptyCustomSqlPreservesDependencies(bool file, string sql)
    {
        string project = Path.Join(AppContext.BaseDirectory, "sql-anchor-contracts");
        string source = """
            [assembly: Ankus.PgSql("a-consumer", "SELECT 'consumer';", Requires = new[] { "m-anchor" }, Relocatable = true)]
            [assembly: Ankus.PgSql("z-producer", "SELECT 'producer';", Relocatable = true)]
            """ + "[assembly: Ankus." + (file ? "PgSqlFile" : "PgSql") + "(\"m-anchor\", " +
            Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(file ? "anchor.sql" : sql, quote: true) +
            ", Requires = new[] { \"z-producer\" }, Relocatable = true)]";
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source,
            file ? [new SqlInput(Path.Join(project, "anchor.sql"), sql)] : [], new SqlOptions(project));
        Assert.IsEmpty(errors);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static error => error.Severity >= DiagnosticSeverity.Warning));
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(output, "Ankus.SqlGraph"));
        Assert.AreSequenceEqual(["z-producer", "m-anchor", "a-consumer"], graph.Items.Select(static item => item.Names.Single()));
        ExtensionSchemaItem anchor = graph.Items[1];
        Assert.AreEqual("sql", anchor.Kind);
        Assert.AreSequenceEqual([graph.Items[0].Id], anchor.Dependencies);
        Assert.AreSequenceEqual([anchor.Id], graph.Items[2].Dependencies);
        Assert.AreEqual("true", ManifestValue(output, "Ankus.Relocatable"));
        string normalized = sql.Length == 0 ? string.Empty : sql.EndsWith('\n') ? sql : sql + "\n";
        Assert.AreEqual("SELECT 'producer';\n" + normalized + "SELECT 'consumer';\n", InstallationBody(output));
        if (sql.Length == 0)
        {
            Assert.AreEqual(string.Empty, anchor.Sql);
        }
        else
        {
            Assert.Contains("\n\n" + normalized + "/* </end connected objects> */", anchor.Sql);
        }
    }

    /// <summary>
    /// Empty custom blocks still take part in explicit and bootstrap/final dependency-cycle validation.
    /// </summary>
    /// <param name="options">The authored cycle-producing dependency options.</param>
    /// <param name="file">Whether the anchor comes from a tracked file.</param>
    /// <param name="span">The authored dependency entry that participates in the cycle.</param>
    /// <param name="occurrence">The zero-based occurrence of that text in the source.</param>
    /// <param name="cycle">The reported cycle.</param>
    [TestMethod]
    [DataRow("Requires = new[] { \"anchor\" }", false, "\"anchor\"", 1, "anchor -> anchor")]
    [DataRow("Requires = new[] { \"anchor\" }", true, "\"anchor\"", 1, "anchor -> anchor")]
    [DataRow("Requires = new[] { \"consumer\" }, Order = Ankus.PgSqlOrder.Bootstrap", false, "\"consumer\"", 0, "anchor -> consumer -> anchor")]
    [DataRow("Requires = new[] { \"consumer\" }, Order = Ankus.PgSqlOrder.Bootstrap", true, "\"consumer\"", 0, "anchor -> consumer -> anchor")]
    [DataRow("Before = new[] { \"consumer\" }, Order = Ankus.PgSqlOrder.Finalize", false, "\"consumer\"", 0, "anchor -> consumer -> anchor")]
    [DataRow("Before = new[] { \"consumer\" }, Order = Ankus.PgSqlOrder.Finalize", true, "\"consumer\"", 0, "anchor -> consumer -> anchor")]
    public void EmptyCustomSqlStillRejectsCycles(string options, bool file, string span, int occurrence, string cycle)
    {
        string project = Path.Join(AppContext.BaseDirectory, "sql-anchor-contracts");
        string source = "[assembly: Ankus." + (file ? "PgSqlFile" : "PgSql") + "(\"anchor\", " +
            (file ? "\"anchor.sql\"" : "\"\"") + ", " + options + ")]" +
            "[assembly: Ankus.PgSql(\"consumer\", \"SELECT 1;\")]";
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source,
            file ? [new SqlInput(Path.Join(project, "anchor.sql"), string.Empty)] : [], new SqlOptions(project));
        AssertSqlControlGraphError(output, errors, "ANKUS499", span, cycle);
        AssertDiagnosticOccurrence(errors[0], span, occurrence);
    }
}
