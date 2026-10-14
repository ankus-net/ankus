using System.Collections.Immutable;
using Ankus.PgConfig;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Inline and file SQL validation stays cached after unrelated method edits or source movement.
    /// </summary>
    /// <param name="file">Whether the SQL comes from a tracked file.</param>
    /// <param name="move">Whether the attribute moves instead of changing an unrelated method.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void CustomSqlResolutionCachesUnrelatedEdits(bool file, bool move)
    {
        string project = Path.Join(AppContext.BaseDirectory, "sql-cache-project");
        string attribute = file ? "[assembly: Ankus.PgSqlFile(\"seed\", \"seed.sql\", Relocatable = true)]" :
            "[assembly: Ankus.PgSql(\"seed\", \"SELECT 'café'; -- exact\", Relocatable = true)]";
        string source = attribute + "\npublic static class Ordinary { public static int Value() => 1; }";
        CSharpCompilation initial = ModuleCompilation(source);
        AdditionalText[] files = file ? [new SqlInput(Path.Join(project, "seed.sql"), "SELECT 'café'; -- exact")] : [];
        GeneratorDriver driver = RunModule(CustomSqlDriver(project, files), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            move ? "\n\n" + source : source.Replace("=> 1", "=> 2", StringComparison.Ordinal),
            path: "Moved.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomSql(driver, "seed").Reason);
        Assert.AreEqual("SELECT 'café'; -- exact\n", InstallationBody(first));
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.AreEqual("true", ManifestValue(second, "Ankus.Relocatable"));
        Assert.Contains("Moved.cs:", ManifestValue(second, "Ankus.Sql"));
    }

    /// <summary>
    /// Editing a block's content changes its exact SQL without invalidating another block's validation.
    /// </summary>
    /// <param name="file">Whether content changes through an additional file.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CustomSqlResolutionTracksContent(bool file)
    {
        string project = Path.Join(AppContext.BaseDirectory, "sql-cache-project");
        var original = new SqlInput(Path.Join(project, "seed.sql"), "SELECT 'first';\n");
        string source = (file ? "[assembly: Ankus.PgSqlFile(\"a\", \"seed.sql\") ]" :
            "[assembly: Ankus.PgSql(\"a\", \"SELECT 'first';\\n\") ]") +
            "\n[assembly: Ankus.PgSql(\"b\", \"SELECT 'survivor';\") ]";
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(CustomSqlDriver(project, file ? [original] : []), initial, out Compilation first);
        CSharpCompilation edited = file ? initial : initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            source.Replace("first", "second", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        if (file)
        {
            driver = driver.ReplaceAdditionalText(original, new SqlInput(original.Path, "SELECT 'second';\n"));
        }

        driver = RunModule(driver, edited, out Compilation second);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedCustomSql(driver, "a").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomSql(driver, "b").Reason);
        Assert.AreEqual("SELECT 'first';\nSELECT 'survivor';\n", InstallationBody(first));
        Assert.AreEqual("SELECT 'second';\nSELECT 'survivor';\n", InstallationBody(second));
    }

    /// <summary>
    /// Unrelated tracked-file edits preserve cached validation of inline and file SQL.
    /// </summary>
    [TestMethod]
    public void CustomSqlResolutionIgnoresUnrelatedFiles()
    {
        string project = Path.Join(AppContext.BaseDirectory, "sql-cache-project");
        var used = new SqlInput(Path.Join(project, "seed.sql"), "SELECT 'used';");
        var unused = new SqlInput(Path.Join(project, "unused.sql"), "SELECT 'unused';");
        CSharpCompilation input = ModuleCompilation("""
            [assembly: Ankus.PgSqlFile("a", "seed.sql")]
            [assembly: Ankus.PgSql("b", "SELECT 'inline';")]
            """);
        GeneratorDriver driver = RunModule(CustomSqlDriver(project, [used, unused]), input, out Compilation first);
        driver = driver.ReplaceAdditionalText(unused, new SqlInput(unused.Path, "SELECT 'changed';"));
        driver = RunModule(driver, input, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomSql(driver, "a").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomSql(driver, "b").Reason);
        Assert.AreEqual("SELECT 'used';\nSELECT 'inline';\n", InstallationBody(second));
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
    }

    /// <summary>
    /// Current ordering, dependency and relocation options compose cached SQL text into a fresh graph.
    /// </summary>
    /// <param name="option">The graph-only option to change.</param>
    [TestMethod]
    [DataRow("Requires")]
    [DataRow("Before")]
    [DataRow("Order")]
    [DataRow("Relocatable")]
    public void CustomSqlResolutionComposesCurrentOptions(string option)
    {
        const string Source = """
            [assembly: Ankus.PgSql("a", "SELECT 'a';", Relocatable = true)]
            [assembly: Ankus.PgSql("b", "SELECT 'b';", Relocatable = true)]
            """;
        string replacement = option switch
        {
            "Requires" => Source.Replace("SELECT 'a';\", Relocatable = true", "SELECT 'a';\", Relocatable = true, Requires = new[] { \"b\" }", StringComparison.Ordinal),
            "Before" => Source.Replace("SELECT 'b';\", Relocatable = true", "SELECT 'b';\", Relocatable = true, Before = new[] { \"a\" }", StringComparison.Ordinal),
            "Order" => Source.Replace("SELECT 'b';\", Relocatable = true", "SELECT 'b';\", Relocatable = true, Order = Ankus.PgSqlOrder.Bootstrap", StringComparison.Ordinal),
            _ => Source.Replace("SELECT 'a';\", Relocatable = true", "SELECT 'a';\", Relocatable = false", StringComparison.Ordinal),
        };
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(CustomSqlDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            replacement, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(second, "Ankus.SqlGraph"));
        ExtensionSchemaItem item = Assert.ContainsSingle(graph.Items.Where(static value => value.Names.Contains("a")));

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomSql(driver, "a").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomSql(driver, "b").Reason);
        Assert.AreEqual("SELECT 'a';\nSELECT 'b';\n", InstallationBody(first));
        Assert.AreEqual(option == "Relocatable" ? "SELECT 'a';\nSELECT 'b';\n" : "SELECT 'b';\nSELECT 'a';\n", InstallationBody(second));
        Assert.AreEqual(option == "Relocatable" ? "false" : "true", ManifestValue(second, "Ankus.Relocatable"));
        if (option is "Requires" or "Before")
        {
            Assert.AreEqual(Assert.ContainsSingle(graph.Items.Where(static value => value.Names.Contains("b"))).Id,
                Assert.ContainsSingle(item.Dependencies));
        }
    }

    /// <summary>
    /// Selected file content follows the project directory while same-content aliases reuse validation.
    /// </summary>
    /// <param name="same">Whether both roots contain the same exact SQL.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CustomSqlResolutionTracksProjectDirectory(bool same)
    {
        string firstRoot = Path.Join(AppContext.BaseDirectory, "sql-first");
        string secondRoot = Path.Join(AppContext.BaseDirectory, "sql-second");
        CSharpCompilation input = ModuleCompilation("""[assembly: Ankus.PgSqlFile("a", "seed.sql")]""");
        GeneratorDriver driver = RunModule(CustomSqlDriver(firstRoot,
            [new SqlInput(Path.Join(firstRoot, "seed.sql"), "SELECT 1;"),
                new SqlInput(Path.Join(secondRoot, "seed.sql"), same ? "SELECT 1;" : "SELECT 2;")]), input, out Compilation first);
        driver = driver.WithUpdatedAnalyzerConfigOptions(new SqlOptions(secondRoot));
        driver = RunModule(driver, input, out Compilation second);

        Assert.AreEqual(same ? IncrementalStepRunReason.Cached : IncrementalStepRunReason.Modified, TrackedCustomSql(driver, "a").Reason);
        Assert.AreEqual("SELECT 1;\n", InstallationBody(first));
        Assert.AreEqual(same ? "SELECT 1;\n" : "SELECT 2;\n", InstallationBody(second));
    }

    /// <summary>
    /// Missing, ambiguous, unreadable and invalid inputs fail closed, report current mapped coordinates and recover.
    /// </summary>
    /// <param name="kind">The invalid selection or declaration partition.</param>
    [TestMethod]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("unreadable")]
    [DataRow("invalid text")]
    [DataRow("invalid Unicode")]
    [DataRow("invalid arguments")]
    [DataRow("invalid path")]
    [DataRow("invalid name")]
    [DataRow("invalid order")]
    public void CustomSqlDiagnosticsFollowCurrentInputsAndRecover(string kind)
    {
        string project = Path.Join(AppContext.BaseDirectory, "sql-cache-project");
        string source = kind switch
        {
            "invalid arguments" => "[assembly: Ankus.PgSql(\"a\")]",
            "invalid path" => "[assembly: Ankus.PgSqlFile(\"a\", \"bad\\0.sql\")]",
            "invalid name" => "[assembly: Ankus.PgSql(\"a\\0b\", \"SELECT 1;\")]",
            "invalid order" => "[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Order = (Ankus.PgSqlOrder)3)]",
            _ => "[assembly: Ankus.PgSqlFile(\"a\", \"seed.sql\")]",
        };
        var file = new SqlInput(Path.Join(project, "seed.sql"), kind switch
        {
            "unreadable" => null,
            "invalid text" => "SELECT '\0';",
            "invalid Unicode" => "SELECT '\ud800';",
            _ => "SELECT 1;",
        });
        AdditionalText[] files = kind == "missing" ? [] : kind == "duplicate"
            ? [file, new SqlInput(Path.Join(project, "nested", "..", "seed.sql"), "SELECT 2;")] : [file];
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = CustomSqlDriver(project, files).RunGeneratorsAndUpdateCompilation(initial, out _,
            out ImmutableArray<Diagnostic> firstErrors, context.CancellationToken);
        Diagnostic before = Assert.ContainsSingle(firstErrors);
        const string Prefix = "#line 100 \"mapped-sql.cs\"\n";
        SyntaxTree moved = CSharpSyntaxTree.ParseText(Prefix + source, path: "Moved.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), moved);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(errors);

        Assert.AreEqual(kind switch
        {
            "invalid arguments" => "ANKUS354",
            "invalid path" => "ANKUS363",
            "invalid name" => "ANKUS356",
            "invalid order" => "ANKUS358",
            "invalid text" => "ANKUS360",
            "invalid Unicode" => "ANKUS361",
            "missing" => "ANKUS367",
            "duplicate" => "ANKUS368",
            _ => "ANKUS369",
        }, error.Id);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "CustomSqlResolution"));
        Assert.Contains(kind switch
        {
            "invalid arguments" => "Supply a dependency name",
            "invalid path" or "invalid name" or "invalid text" => "zero characters",
            "invalid order" => "defined PgSqlOrder",
            "invalid Unicode" => "surrogate",
            "missing" => "no tracked path matches",
            "duplicate" => "duplicate or ambiguous",
            _ => "tracked text is unavailable",
        }, error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(before.GetMessage(System.Globalization.CultureInfo.InvariantCulture), error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreSame(moved, error.Location.SourceTree);
        Assert.AreEqual(before.Location.SourceSpan.Start + Prefix.Length, error.Location.SourceSpan.Start);
        Assert.AreEqual(before.Location.SourceSpan.Length, error.Location.SourceSpan.Length);
        Assert.AreEqual("mapped-sql.cs", error.Location.GetMappedLineSpan().Path);
        Assert.AreEqual(99, error.Location.GetMappedLineSpan().StartLinePosition.Line);
        Assert.IsEmpty(Assert.ContainsSingle(driver.GetRunResult().Results).GeneratedSources);

        driver = driver.ReplaceAdditionalTexts([new SqlInput(file.Path, "SELECT 1;")]);
        CSharpCompilation repaired = edited.ReplaceSyntaxTree(moved, CSharpSyntaxTree.ParseText(
            """[assembly: Ankus.PgSqlFile("a", "seed.sql")]""", path: "Repaired.cs", cancellationToken: context.CancellationToken));
        _ = RunModule(driver, repaired, out Compilation output);
        Assert.AreEqual("SELECT 1;\n", InstallationBody(output));
        Assert.Contains("Repaired.cs:", ManifestValue(output, "Ankus.Sql"));
    }

    /// <summary>
    /// Source-file aliases with identical text preserve validation while refreshing authored provenance.
    /// </summary>
    /// <param name="inline">Whether the replacement switches to inline SQL instead of a second file.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CustomSqlResolutionTracksAuthoredFileProvenance(bool inline)
    {
        string project = Path.Join(AppContext.BaseDirectory, "sql-cache-project");
        const string Source = "[assembly: Ankus.PgSqlFile(\"a\", \"seed.sql\")]";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(CustomSqlDriver(project,
            [new SqlInput(Path.Join(project, "seed.sql"), "SELECT 1;"),
                new SqlInput(Path.Join(project, "alternate.sql"), "SELECT 1;")]), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            inline ? "[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\")]" : Source.Replace("seed.sql", "alternate.sql", StringComparison.Ordinal),
            path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomSql(driver, "a").Reason);
        Assert.AreEqual("SELECT 1;\n", InstallationBody(second));
        Assert.Contains("seed.sql", ManifestValue(first, "Ankus.Sql"));
        Assert.DoesNotContain("seed.sql", ManifestValue(second, "Ankus.Sql"));
        if (inline)
        {
            Assert.DoesNotContain("alternate.sql", ManifestValue(second, "Ankus.Sql"));
        }
        else
        {
            Assert.Contains("alternate.sql", ManifestValue(second, "Ankus.Sql"));
        }
    }

    /// <summary>
    /// Referenced constants update dependent names and content while assembly identity and unused constants preserve cached text.
    /// </summary>
    [TestMethod]
    public void CustomSqlResolutionTracksReferencedConstants()
    {
        CSharpCompilation initial = ModuleCompilation("[assembly: Ankus.PgSql(Names.Name, Names.Sql)]").AddSyntaxTrees(
            CSharpSyntaxTree.ParseText("public static class Names { public const string Name = \"a\"; public const string Sql = \"SELECT 1;\"; public const int Unused = 1; }",
                path: "Names.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(CustomSqlDriver(), initial, out Compilation first);
        CSharpCompilation renamed = initial.WithAssemblyName("AnotherAssembly");
        driver = RunModule(driver, renamed, out Compilation second);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomSql(driver, "a").Reason);
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        SyntaxTree constants = renamed.SyntaxTrees.Last();
        CSharpCompilation changed = renamed.ReplaceSyntaxTree(constants, CSharpSyntaxTree.ParseText(
            constants.ToString().Replace("Name = \"a\"", "Name = \"b\"", StringComparison.Ordinal)
                .Replace("SELECT 1;", "SELECT 2;", StringComparison.Ordinal), path: "Names.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, changed, out Compilation output);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedCustomSql(driver, "b").Reason);
        Assert.AreEqual("SELECT 2;\n", InstallationBody(output));
        Assert.AreEqual("b", Assert.ContainsSingle(ExtensionSchemaGraph.Parse(ManifestValue(output, "Ankus.SqlGraph")).Items).Names.Single());
        SyntaxTree modifiedConstants = changed.SyntaxTrees.Last();
        driver = RunModule(driver, changed.ReplaceSyntaxTree(modifiedConstants, CSharpSyntaxTree.ParseText(
            modifiedConstants.ToString().Replace("Unused = 1", "Unused = 2", StringComparison.Ordinal),
            path: "Names.cs", cancellationToken: context.CancellationToken)), out Compilation stable);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomSql(driver, "b").Reason);
        Assert.AreEqual(InstallationBody(output), InstallationBody(stable));
    }

    /// <summary>
    /// Removing either block updates graph membership, and removing the final block removes every generated output.
    /// </summary>
    /// <param name="first">Whether the first block is removed before the second.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void CustomSqlRemovalUpdatesGraph(bool first)
    {
        const string A = """[assembly: Ankus.PgSql("a", "SELECT 'a';")]""";
        const string B = """[assembly: Ankus.PgSql("b", "SELECT 'b';")]""";
        CSharpCompilation initial = ModuleCompilation(A + "\n" + B);
        GeneratorDriver driver = RunModule(CustomSqlDriver(), initial, out Compilation output);
        Assert.HasCount(2, ExtensionSchemaGraph.Parse(ManifestValue(output, "Ankus.SqlGraph")).Items);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            first ? B : A, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation survivor);
        Assert.AreEqual(first ? "SELECT 'b';\n" : "SELECT 'a';\n", InstallationBody(survivor));
        Assert.AreEqual(first ? "b" : "a", Assert.ContainsSingle(ExtensionSchemaGraph.Parse(ManifestValue(survivor, "Ankus.SqlGraph")).Items).Names.Single());
        CSharpCompilation empty = edited.ReplaceSyntaxTree(edited.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            "", path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, empty, out _);
        Assert.IsEmpty(Assert.ContainsSingle(driver.GetRunResult().Results).GeneratedSources);
    }

    /// <summary>
    /// Newly authored function-provider ownership attaches to the current graph without revalidating unchanged SQL.
    /// </summary>
    [TestMethod]
    public void CustomSqlResolutionComposesCurrentProviders()
    {
        const string Source = """[assembly: Ankus.PgSql("a", "CREATE FUNCTION answer() RETURNS integer LANGUAGE SQL AS 'SELECT 42';")]""";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(CustomSqlDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            """[assembly: Ankus.PgSqlFunctionProvider("a", "answer()")]""", path: "Provider.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);
        ExtensionSchemaItem before = Assert.ContainsSingle(ExtensionSchemaGraph.Parse(ManifestValue(first, "Ankus.SqlGraph")).Items);
        ExtensionSchemaItem after = Assert.ContainsSingle(ExtensionSchemaGraph.Parse(ManifestValue(second, "Ankus.SqlGraph")).Items);

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedCustomSql(driver, "a").Reason);
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.IsEmpty(before.Attachments);
        Assert.AreEqual("FUNCTION answer()", Assert.ContainsSingle(after.Attachments));
        Assert.Contains("answer()", after.Names);
    }

    /// <summary>
    /// Creates a tracked production driver with only compiler-visible file and directory inputs.
    /// </summary>
    private static CSharpGeneratorDriver CustomSqlDriver(string? project = null, AdditionalText[]? files = null)
        => CSharpGeneratorDriver.Create([new PgFunctionGenerator().AsSourceGenerator()], files ?? [],
            optionsProvider: new SqlOptions(project ?? string.Empty),
            driverOptions: new GeneratorDriverOptions(IncrementalGeneratorOutputKind.None, trackIncrementalGeneratorSteps: true));

    /// <summary>
    /// Reads the real Roslyn cache decision for one live resolved block.
    /// </summary>
    private static (CustomSqlPipeline.Resolution Resolution, IncrementalStepRunReason Reason) TrackedCustomSql(GeneratorDriver driver, string name)
    {
        (object trackedValue, IncrementalStepRunReason reason) = Assert.ContainsSingle(
            Assert.ContainsSingle(driver.GetRunResult().Results).TrackedSteps["CustomSqlResolution"]
                .SelectMany(static step => step.Outputs).Where(value => value.Reason != IncrementalStepRunReason.Removed &&
                    value.Value is CustomSqlPipeline.Resolution resolution && resolution.Name == name));
        return (Assert.IsInstanceOfType<CustomSqlPipeline.Resolution>(trackedValue), reason);
    }
}
