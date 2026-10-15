using System.Collections.Immutable;
using Ankus.PgConfig;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Invalid provider text identifies the independently correctable field and exact authored expression.
    /// </summary>
    /// <param name="field">The provider field to invalidate.</param>
    /// <param name="expression">The exact authored constant expression.</param>
    /// <param name="expected">The independently expected rule identity.</param>
    [TestMethod]
    [DataRow("block", "null!", "ANKUS378")]
    [DataRow("block", "\"\"", "ANKUS378")]
    [DataRow("block", "\" \\t\"", "ANKUS378")]
    [DataRow("block", "\"a\\0b\"", "ANKUS379")]
    [DataRow("block", "\"\\uD800\"", "ANKUS380")]
    [DataRow("block", "\"\\uDC00\"", "ANKUS380")]
    [DataRow("block", "\"missing\"", "ANKUS381")]
    [DataRow("name", "(string)null!", "ANKUS385")]
    [DataRow("name", "\"\"", "ANKUS385")]
    [DataRow("name", "\"a\\0b\"", "ANKUS386")]
    [DataRow("name", "\"\\uD800\"", "ANKUS387")]
    [DataRow("name", "\"\\uDC00\"", "ANKUS387")]
    [DataRow("schema", "\"\"", "ANKUS389")]
    [DataRow("schema", "\"a\\0b\"", "ANKUS390")]
    [DataRow("schema", "\"\\uD800\"", "ANKUS391")]
    [DataRow("schema", "\"\\uDC00\"", "ANKUS391")]
    public void SqlTypeProviderInputFailuresPointAtAuthoredValues(string field, string expression, string expected)
    {
        string arguments = (field == "block" ? expression : "\"sql\"") + ", " +
            (field == "name" ? expression : "\"item\"") + (field == "schema" ? ", Schema = " + expression : string.Empty);
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(
            "[assembly: Ankus.PgSql(\"sql\", \"SELECT 1;\")]\n[assembly: Ankus.PgSqlTypeProvider(" + arguments + ")]" );
        AssertSqlTypeProviderErrors(output, errors, (expected, expression));
    }

    /// <summary>
    /// Named and mixed constructors identify semantic fields regardless of argument order.
    /// </summary>
    /// <param name="block">Whether to invalidate the block instead of the catalog name.</param>
    /// <param name="order">The independently legal constructor spelling.</param>
    [TestMethod]
    [DataRow(false, "reverse")]
    [DataRow(true, "reverse")]
    [DataRow(false, "mixed")]
    [DataRow(true, "mixed")]
    [DataRow(false, "nameNamed")]
    [DataRow(true, "nameNamed")]
    public void SqlTypeProviderNamedArgumentsKeepSemanticLocations(bool block, string order)
    {
        const string Invalid = "\"a\\0b\"";
        string sqlId = block ? Invalid : "\"sql\"";
        string name = block ? "\"item\"" : Invalid;
        string arguments = order switch
        {
            "reverse" => "name: " + name + ", sqlId: " + sqlId,
            "mixed" => "sqlId: " + sqlId + ", " + name,
            _ => sqlId + ", name: " + name,
        };
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(
            "[assembly: Ankus.PgSql(\"sql\", \"SELECT 1;\")]\n[assembly: Ankus.PgSqlTypeProvider(" + arguments + ")]" );
        AssertSqlTypeProviderErrors(output, errors, (block ? "ANKUS379" : "ANKUS386", Invalid));
    }

    /// <summary>
    /// Quoted catalog identifiers retain every UTF-8 byte at and immediately around PostgreSQL's truncation boundary.
    /// </summary>
    /// <param name="schema">Whether the measured identifier selects a fixed schema.</param>
    /// <param name="bytes">The independently constructed UTF-8 byte length.</param>
    [TestMethod]
    [DataRow(false, 62)]
    [DataRow(false, 63)]
    [DataRow(false, 64)]
    [DataRow(true, 62)]
    [DataRow(true, 63)]
    [DataRow(true, 64)]
    public void SqlTypeProviderIdentifierBytesPreserveBoundaries(bool schema, int bytes)
    {
        string identifier = new string('é', 31) + (bytes == 62 ? string.Empty : bytes == 63 ? "a" : "é");
        string literal = SymbolDisplay.FormatLiteral(identifier, quote: true);
        string name = schema ? "\"item\"" : literal;
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(
            "[assembly: Ankus.PgSql(\"sql\", \"SELECT 1;\", Relocatable=true)]\n" +
            "[assembly: Ankus.PgSqlTypeProvider(name: " + name + ", sqlId: \"sql\"" + (schema ? ", Schema=" + literal : string.Empty) + ")]" );
        if (bytes == 64)
        {
            AssertSqlTypeProviderErrors(output, errors, (schema ? "ANKUS392" : "ANKUS388", literal));
        }
        else
        {
            AssertSqlControlCompilation(output, errors);
            ExtensionSchemaItem block = Assert.ContainsSingle(ExtensionSchemaGraph.Parse(ManifestValue(output, "Ankus.SqlGraph")).Items);
            string qualified = schema ? "\"" + identifier + "\".\"item\"" : "\"" + identifier + "\"";
            Assert.AreEqual("TYPE " + qualified, Assert.ContainsSingle(block.Attachments));
            Assert.AreEqual(schema ? "false" : "true", ManifestValue(output, "Ankus.Relocatable"));
            Assert.AreEqual("SELECT 1;\n", InstallationBody(output));
        }
    }

    /// <summary>
    /// Managed selectors require a registered closed datum identity rather than a built-in type or null.
    /// </summary>
    /// <param name="expression">The unregistered managed constructor expression.</param>
    [TestMethod]
    [DataRow("typeof(int)")]
    [DataRow("typeof(string)")]
    [DataRow("(System.Type)null!")]
    public void SqlTypeProviderManagedSelectionRequiresRegistration(string expression)
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(
            "[assembly: Ankus.PgSql(\"sql\", \"SELECT 1;\")]\n" +
            "[assembly: Ankus.PgSqlTypeProvider(managedType: " + expression + ", sqlId: \"sql\")]" );
        AssertSqlTypeProviderErrors(output, errors, ("ANKUS382", expression));
    }

    /// <summary>
    /// An external mapping remains usable without an extension provider and identifies an invalid ownership claim precisely.
    /// </summary>
    [TestMethod]
    public void SqlTypeProviderCannotClaimExternalManagedMapping()
    {
        string source = "[assembly: Ankus.PgSql(\"sql\", \"SELECT 1;\")]\n" +
            "[assembly: Ankus.PgSqlTypeProvider(\"sql\", typeof(Value))]\n" + DatumMappingSource();
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(source);
        AssertSqlTypeProviderErrors(output, errors, ("ANKUS383", "typeof(Value)"));
    }

    /// <summary>
    /// Every authored managed schema override is rejected at its expression while the unresolved owned mapping stays visible.
    /// </summary>
    /// <param name="expression">The authored schema value, including an explicit null.</param>
    [TestMethod]
    [DataRow("null")]
    [DataRow("\"placed\"")]
    public void SqlTypeProviderManagedSchemaUsesMappingIdentity(string expression)
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(
            "[assembly: Ankus.PgSql(\"sql\", \"SELECT 1;\")]\n" +
            "[assembly: Ankus.PgSqlTypeProvider(managedType: typeof(Value), sqlId: \"sql\", Schema=" + expression + ")]\n" +
            DatumMappingSource(external: false));
        AssertSqlTypeProviderErrors(output, errors, ("ANKUS384", expression), ("ANKUS395", "Value"));
    }

    /// <summary>
    /// Repeated providers diagnose the second exact managed or catalog claim independently of the supplying block.
    /// </summary>
    /// <param name="managed">Whether ownership selects a managed mapping instead of a catalog name.</param>
    /// <param name="otherBlock">Whether the duplicate selects a different custom SQL block.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void SqlTypeProviderDuplicateClaimsIdentifySecondIdentity(bool managed, bool otherBlock)
    {
        string selector = managed ? "typeof(Value)" : "\"item\"";
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(
            "[assembly: Ankus.PgSql(\"first\", \"SELECT 1;\")]\n" +
            "[assembly: Ankus.PgSql(\"second\", \"SELECT 2;\")]\n" +
            "[assembly: Ankus.PgSqlTypeProvider(\"first\", " + selector + ")]\n" +
            "[assembly: Ankus.PgSqlTypeProvider(" + (managed ? "managedType" : "name") + ": " + selector +
            ", sqlId: \"" + (otherBlock ? "second" : "first") + "\")]\n" +
            (managed ? DatumMappingSource(external: false, name: "item") : string.Empty));
        AssertSqlTypeProviderErrors(output, errors, (managed ? "ANKUS393" : "ANKUS394", selector));
        Diagnostic error = Assert.ContainsSingle(errors);
        string source = error.Location.SourceTree!.GetText(context.CancellationToken).ToString();
        Assert.AreEqual(source.LastIndexOf(selector, StringComparison.Ordinal), error.Location.SourceSpan.Start);
    }

    /// <summary>
    /// A catalog-name claim cannot substitute for the exact managed identity of an owned mapping.
    /// </summary>
    /// <param name="catalogClaim">Whether an otherwise valid named provider is present.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SqlTypeProviderOwnedMappingRequiresManagedClaim(bool catalogClaim)
    {
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(
            "[assembly: Ankus.PgSql(\"sql\", \"SELECT 1;\")]\n" +
            (catalogClaim ? "[assembly: Ankus.PgSqlTypeProvider(\"sql\", \"item\")]\n" : string.Empty) +
            DatumMappingSource(external: false, name: "item"));
        AssertSqlTypeProviderErrors(output, errors, ("ANKUS395", "Value"));
    }

    /// <summary>
    /// Exact Unicode, embedded quotation marks and legal whitespace identifiers preserve authored catalog identity and compiled behavior.
    /// </summary>
    /// <param name="name">The exact catalog leaf name.</param>
    /// <param name="schema">The optional fixed schema, including an explicit null.</param>
    [TestMethod]
    [DataRow(" ", null)]
    [DataRow(" Mixed \" café ", " Schema \" Name ")]
    [DataRow("\uD800\uDC00", "\uDBFF\uDFFF")]
    [DataRow("\uD7FF", "\uE000")]
    public void SqlTypeProviderValidNamesPreserveExactGraphIdentity(string name, string? schema)
    {
        string literal = SymbolDisplay.FormatLiteral(name, quote: true);
        string schemaLiteral = schema is null ? "null" : SymbolDisplay.FormatLiteral(schema, quote: true);
        (Compilation output, ImmutableArray<Diagnostic> errors) = Generate(
            "[assembly: Ankus.PgSql(\"sql\", \"SELECT 'exact provider';\", Relocatable=true)]\n" +
            "[assembly: Ankus.PgSqlTypeProvider(name: " + literal + ", sqlId: \"sql\", Schema=" + schemaLiteral + ")]\n" +
            "public static class Functions { [Ankus.PgFunction(Requires=[\"sql\"])] public static int Answer() => 42; }");
        AssertSqlControlCompilation(output, errors);
        ExtensionSchemaItem block = Assert.ContainsSingle(ExtensionSchemaGraph.Parse(ManifestValue(output, "Ankus.SqlGraph"))
            .Items.Where(static item => item.Kind == "sql"));
        string qualified = (schema is null ? string.Empty : "\"" + schema.Replace("\"", "\"\"", StringComparison.Ordinal) + "\".") +
            "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
        Assert.Contains(name, block.Names);
        Assert.Contains(qualified, block.Names);
        Assert.AreEqual("TYPE " + qualified, Assert.ContainsSingle(block.Attachments));
        Assert.StartsWith("SELECT 'exact provider';\nCREATE FUNCTION ", InstallationBody(output));
        Assert.AreEqual(schema is null ? "true" : "false", ManifestValue(output, "Ankus.Relocatable"));
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(output));
    }

    /// <summary>
    /// Cached schema failures resolve to current syntax trees and removal of the offending value restores complete generated output.
    /// </summary>
    /// <param name="managed">Whether the authored schema overrides an owned managed mapping.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SqlTypeProviderSchemaDiagnosticsFollowCurrentTreesAndRecover(bool managed)
    {
        string schema = managed ? "null" : "\"\"";
        string source = "[assembly: Ankus.PgSql(\"sql\", \"SELECT 'provider';\", Relocatable=true)]\n" +
            "[assembly: Ankus.PgSqlTypeProvider(\"sql\", " + (managed ? "typeof(Value)" : "\"item\"") + ", Schema=" + schema + ")]\n" +
            "public static class Functions { [Ankus.PgFunction] public static int Answer() => 42; }\n" +
            (managed ? DatumMappingSource(external: false, name: "item") : string.Empty);
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _,
            out ImmutableArray<Diagnostic> previous, context.CancellationToken);
        string identity = managed ? "ANKUS384" : "ANKUS389";
        Assert.AreEqual(managed ? "ANKUS384,ANKUS395" : "ANKUS389", string.Join(",", previous.Select(static item => item.Id).Order(StringComparer.Ordinal)));
        SyntaxTree tree = CSharpSyntaxTree.ParseText("\n\n" + source, path: "Module.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), tree);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out Compilation failed, out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        if (managed)
        {
            AssertSqlTypeProviderErrors(failed, errors, ("ANKUS384", schema), ("ANKUS395", "Value"));
        }
        else
        {
            AssertSqlTypeProviderErrors(failed, errors, ("ANKUS389", schema));
        }

        Diagnostic current = Assert.ContainsSingle(errors.Where(item => item.Id == identity));
        Diagnostic before = Assert.ContainsSingle(previous.Where(item => item.Id == identity));
        Assert.AreSame(tree, current.Location.SourceTree);
        Assert.AreEqual(before.Location.SourceSpan.Start + 2, current.Location.SourceSpan.Start);
        Assert.AreEqual(IncrementalStepRunReason.Unchanged, ModuleStep(driver, "SqlProviderAnalysis"));
        string repairedSource = managed ? source.Replace(", Schema=null", string.Empty, StringComparison.Ordinal)
            : source.Replace("Schema=\"\"", "Schema=null", StringComparison.Ordinal);
        driver = RunModule(driver, edited.ReplaceSyntaxTree(tree, CSharpSyntaxTree.ParseText(repairedSource,
            path: "Module.cs", cancellationToken: context.CancellationToken)), out Compilation repaired);
        SqlProviderModel provider = Assert.ContainsSingle(SqlProviders(driver));
        if (managed)
        {
            Assert.IsFalse(provider.SchemaAuthored);
        }
        else
        {
            Assert.IsTrue(provider.SchemaAuthored);
            Assert.IsNull(provider.Schema);
        }

        Assert.AreEqual("true", ManifestValue(repaired, "Ankus.Relocatable"));
        Assert.AreEqual(42, InvokeSqlReferenceAnswer(repaired));
    }

    /// <summary>
    /// Requires exact rule identities and locations without losing any unresolved mapping or emitting partial installation artifacts.
    /// </summary>
    /// <param name="output">The actual generated compilation.</param>
    /// <param name="errors">The actual generator diagnostics.</param>
    /// <param name="expected">Every independently expected diagnostic and exact authored highlight.</param>
    private void AssertSqlTypeProviderErrors(Compilation output, ImmutableArray<Diagnostic> errors, params (string Id, string Expression)[] expected)
    {
        Assert.HasCount(expected.Length, errors);
        Assert.AreEqual(string.Join(",", expected.Select(static item => item.Id).Order(StringComparer.Ordinal)),
            string.Join(",", errors.Select(static item => item.Id).Order(StringComparer.Ordinal)));
        foreach ((string id, string expression) in expected)
        {
            Diagnostic error = Assert.ContainsSingle(errors.Where(item => item.Id == id));
            Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
            Assert.AreEqual(expression, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
            Assert.AreEqual("https://ankus.net/custom-sql/#type-provider-diagnostics", error.Descriptor.HelpLinkUri);
        }

        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity >= DiagnosticSeverity.Warning));
        Assert.IsNull(output.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers"));
        Assert.DoesNotContain(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute" &&
            attribute.ConstructorArguments[0].Value is "Ankus.Sql" or "Ankus.SqlGraph", output.Assembly.GetAttributes());
    }
}
