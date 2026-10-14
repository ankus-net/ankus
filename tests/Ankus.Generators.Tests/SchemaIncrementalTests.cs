using System.Collections.Immutable;
using Ankus.PgConfig;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Unrelated body edits and declaration movement retain the same cached schema statement.
    /// </summary>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void SchemaEmissionRemainsCachedAcrossUnrelatedEdits(bool move)
    {
        const string Source = "[Ankus.PgSchema(\"s\")] public static class Functions { [Ankus.PgFunction] public static int Value() => 1; }";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        string replacement = move ? "\n\n" + Source : Source.Replace("=> 1", "=> 2", StringComparison.Ordinal);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(replacement, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "SchemaEmission"));
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.StartsWith("CREATE SCHEMA IF NOT EXISTS \"s\";\n", InstallationBody(second));
        Assert.Contains("FUNCTION \"s\".\"value\"()", InstallationBody(second));
    }

    /// <summary>
    /// Changing the creation policy removes both the statement and extension attachment without permitting relocation.
    /// </summary>
    [TestMethod]
    public void SchemaEmissionTracksCreationPolicy()
    {
        const string Source = "[Ankus.PgSchema(\"s\", Create = true)] public static class Schema;";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(Source.Replace("true", "false", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Modified, ModuleStep(driver, "SchemaEmission"));
        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"s\";\n", InstallationBody(first));
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(second));
        Assert.AreEqual("false", ManifestValue(second, "Ankus.Relocatable"));
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(second, "Ankus.SqlGraph"));
        Assert.IsEmpty(Assert.ContainsSingle(graph.Items).Attachments);
    }

    /// <summary>
    /// A semantic schema constant invalidates its statement and every dependent qualified enum and function name.
    /// </summary>
    [TestMethod]
    public void SchemaEmissionTracksInheritedConstantChanges()
    {
        const string Source = """
            [Ankus.PgSchema(Names.Value)] public static class Types
            {
                [Ankus.PgEnum] public enum Mood { Ready }
                [Ankus.PgFunction] public static Mood Echo(Mood value) => value;
            }
            """;
        CSharpCompilation initial = ModuleCompilation(Source).AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("public static class Names { public const string Value = \"A\"; }", path: "Names.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Last(),
            CSharpSyntaxTree.ParseText("public static class Names { public const string Value = \"B\"; }", path: "Names.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Modified, ModuleStep(driver, "SchemaEmission"));
        Assert.AreEqual(IncrementalStepRunReason.Modified, EnumEmissionReason(driver, "mood"));
        Assert.Contains("CREATE SCHEMA IF NOT EXISTS \"A\";", InstallationBody(first));
        string sql = InstallationBody(second);
        Assert.StartsWith("CREATE SCHEMA IF NOT EXISTS \"B\";\nCREATE TYPE \"B\".\"mood\" AS ENUM (E'Ready');\n", sql);
        Assert.Contains("FUNCTION \"B\".\"echo\"(\"value\" \"B\".\"mood\")", sql);
        Assert.DoesNotContain("\"A\"", sql);
    }

    /// <summary>
    /// Dependency edits reorder the graph without rerendering the unchanged schema statement.
    /// </summary>
    [TestMethod]
    public void SchemaEmissionRemainsCachedAfterGraphPolicyChanges()
    {
        const string Source = """
            [assembly: Ankus.PgSql("seed", "SELECT 42;")]
            [Ankus.PgSchema("s", Requires = new string[] { })] public static class Schema;
            """;
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(Source.Replace("new string[] { }", "new[] { \"seed\" }", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "SchemaEmission"));
        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"s\";\nSELECT 42;\n", InstallationBody(first));
        Assert.AreEqual("SELECT 42;\nCREATE SCHEMA IF NOT EXISTS \"s\";\n", InstallationBody(second));
    }

    /// <summary>
    /// Shared schema aliases merge creation and dependencies without retaining a previous creator's SQL or attachment.
    /// </summary>
    [TestMethod]
    public void SchemaAliasesRecomputeMergedCreationPolicy()
    {
        const string Source = """
            [assembly: Ankus.PgSql("after", "SELECT 42;", Requires = new[] { "one", "two" })]
            [Ankus.PgSchema("s", Id = "one", Create = false)] public static class A;
            [Ankus.PgSchema("s", Id = "two", Create = true)] public static class B;
            """;
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText(Source.Replace("Create = true", "Create = false", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        _ = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"s\";\nSELECT 42;\n", InstallationBody(first));
        Assert.AreEqual("SELECT 42;\n", InstallationBody(second));
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(second, "Ankus.SqlGraph"));
        ExtensionSchemaItem schema = Assert.ContainsSingle(graph.Items.Where(static item => item.Kind == "schema"));
        Assert.Contains("one", schema.Names);
        Assert.Contains("two", schema.Names);
        Assert.IsEmpty(schema.Attachments);
        Assert.AreSequenceEqual<string>([schema.Id], Assert.ContainsSingle(graph.Items.Where(static item => item.Kind == "sql")).Dependencies);
    }

    /// <summary>
    /// Cached invalid models report against fresh source trees and mapped positions, then recover to an existing catalog schema.
    /// </summary>
    [TestMethod]
    public void SchemaDiagnosticsFollowCurrentTreeAndRecover()
    {
        const string Source = "[Ankus.PgSchema(\"pg_reserved\")] public static class Bad;";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _,
            out ImmutableArray<Diagnostic> firstErrors, context.CancellationToken);
        Assert.AreEqual("ANKUS063", Assert.ContainsSingle(firstErrors).Id);
        SyntaxTree current = CSharpSyntaxTree.ParseText(Source + "\n// unrelated edit", path: "Module.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), current);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(errors);

        Assert.AreEqual("ANKUS063", error.Id);
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreEqual(Assert.ContainsSingle(firstErrors).Location.SourceSpan, error.Location.SourceSpan);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "SchemaModel"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "SchemaEmission"));

        SyntaxTree shifted = CSharpSyntaxTree.ParseText("#line 120 \"MappedSchema.cs\"\n" + Source, path: "Module.cs", cancellationToken: context.CancellationToken);
        edited = edited.ReplaceSyntaxTree(current, shifted);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out errors, context.CancellationToken);
        error = Assert.ContainsSingle(errors);
        Assert.AreSame(shifted, error.Location.SourceTree);
        Assert.AreEqual("MappedSchema.cs", error.Location.GetMappedLineSpan().Path);
        Assert.AreEqual(119, error.Location.GetMappedLineSpan().StartLinePosition.Line);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "SchemaEmission"));

        CSharpCompilation repaired = edited.ReplaceSyntaxTree(shifted,
            CSharpSyntaxTree.ParseText(Source.Replace("\"pg_reserved\"", "\"pg_catalog\", Create = false", StringComparison.Ordinal),
                path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out Compilation output);
        Assert.AreEqual("-- No installable objects declared.\n", InstallationBody(output));
        Assert.AreEqual("false", ManifestValue(output, "Ankus.Relocatable"));
        Assert.AreEqual(IncrementalStepRunReason.Modified, ModuleStep(driver, "SchemaModel"));
    }

    /// <summary>
    /// Removing an earlier source file cannot leave a cached diagnostic attached to its former tree index.
    /// </summary>
    [TestMethod]
    public void SchemaDiagnosticsFollowSourceTreeRemoval()
    {
        SyntaxTree schema = CSharpSyntaxTree.ParseText("[Ankus.PgSchema(\"pg_reserved\")] public static class Bad;",
            path: "Schema.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation initial = ModuleCompilation("public static class Unrelated;").AddSyntaxTrees(schema);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _,
            out ImmutableArray<Diagnostic> firstErrors, context.CancellationToken);
        Assert.AreEqual("ANKUS063", Assert.ContainsSingle(firstErrors).Id);

        CSharpCompilation edited = initial.RemoveSyntaxTrees(initial.SyntaxTrees.First());
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(errors);
        Assert.AreEqual("ANKUS063", error.Id);
        Assert.AreSame(schema, error.Location.SourceTree);
        Assert.AreEqual(Assert.ContainsSingle(firstErrors).Location.SourceSpan, error.Location.SourceSpan);
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
    }

    /// <summary>
    /// An incomplete attribute reports normal declaration/compiler errors without crashing semantic analysis.
    /// </summary>
    /// <param name="members">The declarations inheriting the unfinished schema.</param>
    [TestMethod]
    [DataRow("")]
    [DataRow("[Ankus.PgFunction] public static int Value() => 1;")]
    [DataRow("[Ankus.PgEnum] public enum Mood { Ready }")]
    public void SchemaMissingConstructorArgumentDoesNotCrashGenerator(string members)
    {
        CSharpCompilation initial = ModuleCompilation("[Ankus.PgSchema] public static class Missing { " + members + " }");
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out Compilation output,
            out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
        Assert.DoesNotContain(static diagnostic => diagnostic.Id == "CS8785", errors);
        Assert.Contains(static diagnostic => diagnostic.Id == "CS7036", output.GetDiagnostics(context.CancellationToken));
        Assert.Contains(static diagnostic => diagnostic.Id == "ANKUS050", errors);

        CSharpCompilation repaired = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(),
            CSharpSyntaxTree.ParseText("[Ankus.PgSchema(\"repaired\")] public static class Missing { " + members + " }",
                path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out Compilation recovered);
        Assert.IsNull(Assert.ContainsSingle(driver.GetRunResult().Results).Exception);
        Assert.StartsWith("CREATE SCHEMA IF NOT EXISTS \"repaired\";\n", InstallationBody(recovered));
    }
}
