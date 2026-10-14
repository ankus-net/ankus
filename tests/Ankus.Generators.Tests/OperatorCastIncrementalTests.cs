using System.Collections.Immutable;
using System.Text;
using Ankus.PgConfig;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Alias-only backing functions and attached declarations reuse real cached artifacts after body or source edits.
    /// </summary>
    /// <param name="cast">Whether to declare a cast instead of a prefix operator.</param>
    /// <param name="move">Whether the declaration moves instead of changing its body.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void AliasFunctionEmissionCachesImplementationEdits(bool cast, bool move)
    {
        string source = AliasCacheSource(cast);
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        FunctionEmission boundary = TrackedAliasEmission(driver, "value").Emission;
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            move ? "\n\n" + source : source.Replace("value + 1", "value + 2", StringComparison.Ordinal),
            path: "Moved.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedAliasEmission(driver, "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedAliasSql(driver, "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedOperatorCast(driver, "value").Reason);
        Assert.AreEqual(boundary, TrackedAliasEmission(driver, "value").Emission);
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.EndsWith(cast ? "CREATE CAST (integer AS bigint) WITH FUNCTION \"value\"(integer);\n" :
            "CREATE OPERATOR @+ (FUNCTION = \"value\", RIGHTARG = integer);\n", InstallationBody(second));
        Assert.Contains("Moved.cs:", ManifestValue(second, "Ankus.Sql"));
    }

    /// <summary>
    /// Managed return nullability changes conversion without rerendering unchanged backing or attached SQL.
    /// </summary>
    /// <param name="cast">Whether to use a cast instead of an operator.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OperatorCastEmissionCachesReturnNullabilityChanges(bool cast)
    {
        string source = AliasCacheSource(cast);
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        FunctionEmission previous = TrackedAliasEmission(driver, "value").Emission;
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            source.Replace(cast ? "long Value" : "int Value", cast ? "long? Value" : "int? Value", StringComparison.Ordinal),
            path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedAliasEmission(driver, "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedAliasSql(driver, "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedOperatorCast(driver, "value").Reason);
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        Assert.AreNotEqual(TrackedAliasEmission(driver, "value").Emission.Managed, previous.Managed);
    }

    /// <summary>
    /// Referenced operator names and cast contexts invalidate only dependent attached DDL.
    /// </summary>
    /// <param name="cast">Whether to change a cast context instead of an operator name.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OperatorCastEmissionTracksDependentOptions(bool cast)
    {
        string source = cast
            ? "public static class Functions { [Ankus.PgCast(Options.Context)] public static long Value(int value) => value; [Ankus.PgCast] public static int Other(short value) => value; }"
            : "public static class Functions { [Ankus.PgOperator(Options.Name)] public static int Value(int value) => value; [Ankus.PgOperator(\"@-\")] public static int Other(int value) => value; }";
        string constants = cast ? "public static class Options { public const Ankus.PgCastContext Context = Ankus.PgCastContext.Explicit; }"
            : "public static class Options { public const string Name = \"@+\"; }";
        CSharpCompilation initial = ModuleCompilation(source).AddSyntaxTrees(CSharpSyntaxTree.ParseText(
            constants, path: "Options.cs", cancellationToken: context.CancellationToken));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Last(), CSharpSyntaxTree.ParseText(
            cast ? constants.Replace("Explicit", "Assignment", StringComparison.Ordinal) : constants.Replace("@+", "@*", StringComparison.Ordinal),
            path: "Options.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedOperatorCast(driver, "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedOperatorCast(driver, "other").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedAliasEmission(driver, "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedAliasSql(driver, "value").Reason);
        Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        Assert.Contains(cast ? "WITH FUNCTION \"value\"(integer) AS ASSIGNMENT;" : "CREATE OPERATOR @* (FUNCTION = \"value\", RIGHTARG = integer);", InstallationBody(second));
        Assert.DoesNotContain(cast ? "WITH FUNCTION \"value\"(integer);" : "CREATE OPERATOR @+", InstallationBody(second));
    }

    /// <summary>
    /// Current type providers qualify cached operand/result fragments and add actual backing prerequisites.
    /// </summary>
    /// <param name="cast">Whether the provider owns a cast result instead of operator operands.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OperatorCastEmissionComposesCurrentProviders(bool cast)
    {
        string source = cast ? "public static class Functions { [return: Ankus.PgSqlType(\"item\")] [Ankus.PgCast] public static Ankus.PgDatum Value(int value) => default; }" :
            "public static class Functions { [Ankus.PgOperator(\"@=\")] public static bool Value([Ankus.PgSqlType(\"item\")] Ankus.PgDatum left, [Ankus.PgSqlType(\"item\")] Ankus.PgDatum right) => true; }";
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.AddSyntaxTrees(CSharpSyntaxTree.ParseText("""
            [assembly: Ankus.PgSql("types", "CREATE TYPE item AS (number integer);", Relocatable = true)]
            [assembly: Ankus.PgSqlTypeProvider("types", "item")]
            """, path: "Provider.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);
        string original = Encoding.UTF8.GetString(Convert.FromBase64String(ManifestValue(first, "Ankus.SqlGraph")));
        string current = Encoding.UTF8.GetString(Convert.FromBase64String(ManifestValue(second, "Ankus.SqlGraph")));
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(second, "Ankus.SqlGraph"));
        ExtensionSchemaItem attached = Assert.ContainsSingle(graph.Items.Where(value => value.Kind == (cast ? "cast" : "operator")));
        ExtensionSchemaItem function = Assert.ContainsSingle(graph.Items.Where(static value => value.Kind == "function"));
        ExtensionSchemaItem provider = Assert.ContainsSingle(graph.Items.Where(static value => value.Kind == "sql"));

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedOperatorCast(driver, "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedAliasEmission(driver, "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedAliasSql(driver, "value").Reason);
        Assert.DoesNotContain("\0\"item\"", original);
        Assert.Contains(cast ? "CREATE CAST (integer AS \0\"item\")" : "LEFTARG = \0\"item\", RIGHTARG = \0\"item\"", current);
        Assert.Contains(cast ? "CAST (integer AS \0\"item\")" : "OPERATOR \0@=(\0\"item\",\0\"item\")", current);
        Assert.AreEqual(function.Id, Assert.ContainsSingle(attached.Dependencies));
        Assert.Contains(provider.Id, function.Dependencies);
        Assert.StartsWith("CREATE TYPE item AS (number integer);\nCREATE FUNCTION ", InstallationBody(second));
    }

    /// <summary>
    /// Dependency-only alias edits recompose current graph edges while native and attached SQL remain cached.
    /// </summary>
    /// <param name="cast">Whether to apply the dependency to a cast instead of an operator.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OperatorCastEmissionComposesCurrentDependencies(bool cast)
    {
        string source = "[assembly: Ankus.PgSql(\"seed\", \"SELECT 1;\")]\n" + AliasCacheSource(cast);
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        OperatorCastEmission previous = TrackedOperatorCast(driver, "value").Emission;
        string changed = cast ? source.Replace("[Ankus.PgCast]", "[Ankus.PgCast(Requires = new[] { \"seed\" })]", StringComparison.Ordinal) :
            source.Replace("[Ankus.PgOperator(\"@+\")]", "[Ankus.PgOperator(\"@+\", Requires = new[] { \"seed\" })]", StringComparison.Ordinal);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            changed, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);
        ExtensionSchemaItem before = Assert.ContainsSingle(ExtensionSchemaGraph.Parse(ManifestValue(first, "Ankus.SqlGraph")).Items.Where(value => value.Kind == (cast ? "cast" : "operator")));
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(second, "Ankus.SqlGraph"));
        ExtensionSchemaItem after = Assert.ContainsSingle(graph.Items.Where(value => value.Kind == (cast ? "cast" : "operator")));

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedOperatorCast(driver, "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedAliasEmission(driver, "value").Reason);
        Assert.AreEqual(previous, TrackedOperatorCast(driver, "value").Emission);
        string expected = cast ? "CREATE CAST (integer AS bigint) WITH FUNCTION \"value\"(integer);" :
            "CREATE OPERATOR @+ (FUNCTION = \"value\", RIGHTARG = integer);";
        Assert.Contains(expected, before.Sql);
        Assert.Contains(expected, after.Sql);
        Assert.DoesNotContain("--   seed\n", before.Sql);
        Assert.Contains("--   seed\n", after.Sql);
        Assert.HasCount(1, before.Dependencies);
        Assert.HasCount(2, after.Dependencies);
        Assert.Contains(Assert.ContainsSingle(graph.Items.Where(static value => value.Kind == "sql")).Id, after.Dependencies);
        Assert.Contains("-- requires:", ManifestValue(second, "Ankus.Sql"));
    }

    /// <summary>
    /// Assembly identity updates backing native exports while attached SQL still names the same catalog function.
    /// </summary>
    /// <param name="cast">Whether to declare a cast instead of an operator.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OperatorCastEmissionPreservesCatalogAcrossAssemblyRename(bool cast)
    {
        CSharpCompilation initial = ModuleCompilation(AliasCacheSource(cast));
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        FunctionEmission previous = TrackedAliasEmission(driver, "value").Emission;
        driver = RunModule(driver, initial.WithAssemblyName("RenamedAssembly"), out Compilation second);
        FunctionEmission current = TrackedAliasEmission(driver, "value").Emission;
        ExtensionSchemaItem before = Assert.ContainsSingle(ExtensionSchemaGraph.Parse(ManifestValue(first, "Ankus.SqlGraph")).Items.Where(value => value.Kind == (cast ? "cast" : "operator")));
        ExtensionSchemaItem after = Assert.ContainsSingle(ExtensionSchemaGraph.Parse(ManifestValue(second, "Ankus.SqlGraph")).Items.Where(value => value.Kind == (cast ? "cast" : "operator")));

        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedAliasEmission(driver, "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedAliasSql(driver, "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedOperatorCast(driver, "value").Reason);
        Assert.AreNotEqual(previous.NativeName, current.NativeName);
        Assert.DoesNotContain(previous.NativeName, ManifestValue(second, "Ankus.Exports"));
        Assert.Contains(current.NativeName, ManifestValue(second, "Ankus.Exports"));
        Assert.AreEqual(before.Sql, after.Sql);
        Assert.AreSequenceEqual(before.Attachments, after.Attachments);
    }

    /// <summary>
    /// Current inherited schemas and backing-function names invalidate only their dependent typed DDL.
    /// </summary>
    /// <param name="cast">Whether to declare a cast instead of an operator.</param>
    /// <param name="rename">Whether to rename the backing method instead of changing its inherited schema.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void OperatorCastEmissionTracksBackingCatalogIdentity(bool cast, bool rename)
    {
        string source = "[Ankus.PgSchema(\"first\")]\n" + AliasCacheSource(cast);
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            rename ? source.Replace("Value(", "Renamed(", StringComparison.Ordinal) : source.Replace("first", "second", StringComparison.Ordinal),
            path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedOperatorCast(driver, rename ? "renamed" : "value").Reason);
        Assert.Contains("\"first\".\"value\"", InstallationBody(first));
        Assert.Contains(rename ? "\"first\".\"renamed\"" : "\"second\".\"value\"", InstallationBody(second));
        Assert.DoesNotContain("\"first\".\"value\"", InstallationBody(second));
        if (!rename)
        {
            Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedAliasEmission(driver, "value").Reason);
            Assert.AreEqual(ManifestValue(first, "Ankus.NativeSource"), ManifestValue(second, "Ankus.NativeSource"));
        }
        else
        {
            Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedAliasEmission(driver, "renamed").Reason);
        }
    }

    /// <summary>
    /// Equivalent PostgreSQL inequality spellings reuse DDL while refreshing authored selection aliases.
    /// </summary>
    [TestMethod]
    public void OperatorCastEmissionNormalizesCatalogSpellingAndKeepsCurrentAliases()
    {
        const string Source = "public static class Functions { [Ankus.PgOperator(\"!=\")] public static bool Value(int left, int right) => left != right; }";
        CSharpCompilation initial = ModuleCompilation(Source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            Source.Replace("PgOperator(\"!=\")", "PgOperator(\"<>\")", StringComparison.Ordinal),
            path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);
        ExtensionSchemaItem before = Assert.ContainsSingle(ExtensionSchemaGraph.Parse(ManifestValue(first, "Ankus.SqlGraph")).Items.Where(static item => item.Kind == "operator"));
        ExtensionSchemaItem after = Assert.ContainsSingle(ExtensionSchemaGraph.Parse(ManifestValue(second, "Ankus.SqlGraph")).Items.Where(static item => item.Kind == "operator"));

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedOperatorCast(driver, "value").Reason);
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.EndsWith("CREATE OPERATOR <> (FUNCTION = \"value\", LEFTARG = integer, RIGHTARG = integer);\n", InstallationBody(second));
        Assert.Contains("!=", before.Names);
        Assert.DoesNotContain("!=", after.Names);
        Assert.Contains("<>", after.Names);
        Assert.Contains("<>(integer,integer)", after.Names);
    }

    /// <summary>
    /// Removing the alias removes its backing and attached nodes while preserving ordinary functions and their cached rendering.
    /// </summary>
    /// <param name="cast">Whether to remove a cast instead of an operator.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OperatorCastRemovalUpdatesCurrentInventory(bool cast)
    {
        const string Survivor = "public static class Ordinary { [Ankus.PgFunction] public static int Other() => 42; }";
        CSharpCompilation initial = ModuleCompilation(AliasCacheSource(cast) + Survivor);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        FunctionEmission removed = TrackedAliasEmission(driver, "value").Emission;
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            Survivor, path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);
        ExtensionSchemaItem item = Assert.ContainsSingle(ExtensionSchemaGraph.Parse(ManifestValue(second, "Ankus.SqlGraph")).Items);
        Assert.AreEqual("function", item.Kind);
        Assert.Contains("Ordinary.Other()", item.Names);
        Assert.IsEmpty(item.Dependencies);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedFunctionEmission(driver, "other").Reason);
        Assert.DoesNotContain(removed.NativeName, ManifestValue(second, "Ankus.NativeSource"));
        Assert.DoesNotContain(removed.NativeName, ManifestValue(second, "Ankus.Exports"));
        Assert.DoesNotContain("CREATE CAST", InstallationBody(second));
        Assert.DoesNotContain("CREATE OPERATOR", InstallationBody(second));
        Assert.Contains("CREATE FUNCTION \"other\"()", InstallationBody(second));
        CSharpCompilation empty = edited.ReplaceSyntaxTree(edited.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            "", path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, empty, out _);
        Assert.IsEmpty(Assert.ContainsSingle(driver.GetRunResult().Results).GeneratedSources);
        Assert.HasCount(3, ExtensionSchemaGraph.Parse(ManifestValue(first, "Ankus.SqlGraph")).Items);
    }

    /// <summary>
    /// Current duplicate-signature diagnostics survive render cache hits and disappear after the catalog signatures diverge.
    /// </summary>
    /// <param name="cast">Whether to duplicate cast signatures instead of operator operands.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OperatorCastDuplicatesUseCurrentInventoryAndRecover(bool cast)
    {
        string marker = cast ? "[Ankus.PgCast]" : "[Ankus.PgOperator(\"@+\")]";
        string result = cast ? "long" : "int";
        string source = "public static class Functions { " + marker + " public static " + result + " First(int value) => value; " +
            marker + " public static " + result + " Second(int value) => value; }";
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _, out ImmutableArray<Diagnostic> first, context.CancellationToken);
        Diagnostic before = Assert.ContainsSingle(first);
        const string Prefix = "#line 100 \"mapped-duplicates.cs\"\n";
        SyntaxTree moved = CSharpSyntaxTree.ParseText(Prefix + source, path: "Moved.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), moved);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(errors);

        AssertGraphDiagnostic(before, cast ? "ANKUS509" : "ANKUS508", marker[1..^1], cast ? "integer AS bigint" : "@+(NONE,integer)");
        AssertGraphDiagnostic(error, cast ? "ANKUS509" : "ANKUS508", marker[1..^1], cast ? "integer AS bigint" : "@+(NONE,integer)");
        AssertDiagnosticOccurrence(error, marker[1..^1], 1);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedOperatorCast(driver, "first").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedOperatorCast(driver, "second").Reason);
        Assert.AreSame(moved, error.Location.SourceTree);
        Assert.AreEqual(before.Location.SourceSpan.Start + Prefix.Length, error.Location.SourceSpan.Start);
        Assert.AreEqual(before.Location.SourceSpan.Length, error.Location.SourceSpan.Length);
        Assert.AreEqual("mapped-duplicates.cs", error.Location.GetMappedLineSpan().Path);
        Assert.AreEqual(99, error.Location.GetMappedLineSpan().StartLinePosition.Line);
        Assert.IsEmpty(Assert.ContainsSingle(driver.GetRunResult().Results).GeneratedSources);
        CSharpCompilation repaired = edited.ReplaceSyntaxTree(moved, CSharpSyntaxTree.ParseText(
            source.Replace("Second(int", "Second(short", StringComparison.Ordinal), path: "Repaired.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, repaired, out Compilation output);
        Assert.HasCount(4, ExtensionSchemaGraph.Parse(ManifestValue(output, "Ankus.SqlGraph")).Items);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedOperatorCast(driver, "first").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Modified, TrackedOperatorCast(driver, "second").Reason);
        Assert.Contains(cast ? "CREATE CAST (smallint AS bigint) WITH FUNCTION \"second\"(smallint);" :
            "CREATE OPERATOR @+ (FUNCTION = \"second\", RIGHTARG = smallint);", InstallationBody(output));
    }

    /// <summary>
    /// Invalid names, operator references/capabilities and cast signatures retain current mapped diagnostics and recover.
    /// </summary>
    /// <param name="kind">The invalid operator or cast rule.</param>
    /// <param name="id">The specific contract diagnostic.</param>
    [TestMethod]
    [DataRow("name", "ANKUS065")]
    [DataRow("self negator", "ANKUS069")]
    [DataRow("unary capability", "ANKUS070")]
    [DataRow("nonboolean capability", "ANKUS071")]
    [DataRow("reference", "ANKUS073")]
    [DataRow("context", "ANKUS074")]
    [DataRow("modifier", "ANKUS076")]
    [DataRow("same type", "ANKUS079")]
    [DataRow("set", "ANKUS064")]
    public void OperatorCastDiagnosticsFollowCurrentTreesAndRecover(string kind, string id)
    {
        string source = kind switch
        {
            "name" => "public static class Functions { [Ankus.PgOperator(\"--\")] public static int Value(int value) => value; }",
            "self negator" => "public static class Functions { [Ankus.PgOperator(\"@=\", Negator = \"@=\")] public static bool Value(int value) => true; }",
            "unary capability" => "public static class Functions { [Ankus.PgOperator(\"@+\", Hashes = true)] public static bool Value(int value) => true; }",
            "nonboolean capability" => "public static class Functions { [Ankus.PgOperator(\"@+\", Hashes = true)] public static int Value(int left, int right) => left; }",
            "reference" => "public static class Functions { [Ankus.PgOperator(\"@+\", RestrictionEstimator = \"bad.name.extra\")] public static bool Value(int value) => true; }",
            "context" => "public static class Functions { [Ankus.PgCast((Ankus.PgCastContext)3)] public static long Value(int value) => value; }",
            "modifier" => "public static class Functions { [Ankus.PgCast] public static long Value(int value, int? modifier) => value; }",
            "same type" => "public static class Functions { [Ankus.PgCast] public static int Value(int value) => value; }",
            _ => "public static class Functions { [Ankus.PgOperator(\"@+\")] public static System.Collections.Generic.IEnumerable<int> Value(int value) => new[] { value }; }",
        };
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out _, out ImmutableArray<Diagnostic> first, context.CancellationToken);
        Diagnostic before = Assert.ContainsSingle(first);
        const string Prefix = "#line 100 \"mapped-operator.cs\"\n";
        SyntaxTree moved = CSharpSyntaxTree.ParseText(Prefix + source, path: "Moved.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), moved);
        driver = driver.RunGeneratorsAndUpdateCompilation(edited, out _, out ImmutableArray<Diagnostic> errors, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(errors);

        Assert.AreEqual(id, error.Id);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "OperatorCastEmission"));
        Assert.AreEqual(before.GetMessage(System.Globalization.CultureInfo.InvariantCulture), error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.Contains(kind switch
        {
            "name" => "valid PostgreSQL operator characters",
            "self negator" => "own negator",
            "unary capability" => "Only binary operators",
            "nonboolean capability" => "Only boolean operators",
            "reference" => "must name one estimator function",
            "context" => "cast context must be",
            "modifier" => "optional second parameter",
            "same type" => "distinct PostgreSQL types",
            _ => "cannot return sets",
        }, error.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreSame(moved, error.Location.SourceTree);
        Assert.AreEqual(before.Location.SourceSpan.Start + Prefix.Length, error.Location.SourceSpan.Start);
        Assert.AreEqual(before.Location.SourceSpan.Length, error.Location.SourceSpan.Length);
        Assert.AreEqual("mapped-operator.cs", error.Location.GetMappedLineSpan().Path);
        Assert.AreEqual(99, error.Location.GetMappedLineSpan().StartLinePosition.Line);
        CSharpCompilation repaired = edited.ReplaceSyntaxTree(moved, CSharpSyntaxTree.ParseText(
            AliasCacheSource(kind is "context" or "modifier" or "same type"), path: "Repaired.cs", cancellationToken: context.CancellationToken));
        _ = RunModule(driver, repaired, out Compilation output);
        Assert.Contains("Repaired.cs:", ManifestValue(output, "Ankus.Sql"));
        Assert.Contains(kind is "context" or "modifier" or "same type" ? "CREATE CAST (integer AS bigint)" : "CREATE OPERATOR @+", InstallationBody(output));
    }

    /// <summary>
    /// Dual operator/cast roles share one backing function and retain both authored connected objects.
    /// </summary>
    /// <param name="explicitFunction">Whether a PgFunction attribute supplies explicit backing options.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void OperatorCastMultipleRolesShareOneBackingFunction(bool explicitFunction)
    {
        string source = "public static class Functions { " + (explicitFunction ? "[Ankus.PgFunction] " : string.Empty) +
            "[Ankus.PgOperator(\"@+\"), Ankus.PgCast] public static long Value(int value) => value; }";
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation output);
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(output, "Ankus.SqlGraph"));
        ExtensionSchemaItem function = Assert.ContainsSingle(graph.Items.Where(static value => value.Kind == "function"));
        Assert.HasCount(3, graph.Items);
        Assert.AreEqual(function.Id, Assert.ContainsSingle(Assert.ContainsSingle(graph.Items.Where(static value => value.Kind == "operator")).Dependencies));
        Assert.AreEqual(function.Id, Assert.ContainsSingle(Assert.ContainsSingle(graph.Items.Where(static value => value.Kind == "cast")).Dependencies));
        Assert.Contains("CREATE OPERATOR @+ (FUNCTION = \"value\", RIGHTARG = integer);", InstallationBody(output));
        Assert.Contains("CREATE CAST (integer AS bigint) WITH FUNCTION \"value\"(integer);", InstallationBody(output));
        Assert.AreEqual(IncrementalStepRunReason.New, explicitFunction ? TrackedFunctionEmission(driver, "value").Reason : TrackedAliasEmission(driver, "value").Reason);
    }

    /// <summary>
    /// Partial definition and implementation markers retain one backing invocation after body changes.
    /// </summary>
    /// <param name="cast">Whether the declaration is a cast instead of an operator.</param>
    /// <param name="implementation">Whether the attribute is authored on the implementation.</param>
    [TestMethod]
    [DataRow(false, false)]
    [DataRow(false, true)]
    [DataRow(true, false)]
    [DataRow(true, true)]
    public void AliasPartialFunctionsRetainCachedInvocation(bool cast, bool implementation)
    {
        string marker = cast ? "[Ankus.PgCast]" : "[Ankus.PgOperator(\"@+\")]";
        string result = cast ? "long" : "int";
        string source = "public static partial class Functions { " + (implementation ? "" : marker) +
            "public static partial " + result + " Value(int value); " + (implementation ? marker : "") +
            "public static partial " + result + " Value(int value) => value + 1; }";
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = RunModule(ModuleDriver(), initial, out Compilation first);
        CSharpCompilation edited = initial.ReplaceSyntaxTree(initial.SyntaxTrees.Single(), CSharpSyntaxTree.ParseText(
            source.Replace("value + 1", "value + 2", StringComparison.Ordinal), path: "Module.cs", cancellationToken: context.CancellationToken));
        driver = RunModule(driver, edited, out Compilation second);

        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedAliasEmission(driver, "value").Reason);
        Assert.AreEqual(IncrementalStepRunReason.Cached, TrackedOperatorCast(driver, "value").Reason);
        Assert.AreEqual(InstallationBody(first), InstallationBody(second));
        Assert.HasCount(2, ExtensionSchemaGraph.Parse(ManifestValue(second, "Ankus.SqlGraph")).Items);
    }

    /// <summary>
    /// Creates an alias-only declaration with a scalar catalog conversion suitable for independent cache tests.
    /// </summary>
    private static string AliasCacheSource(bool cast)
        => cast ? "public static class Functions { [Ankus.PgCast] public static long Value(int value) => value + 1; }" :
            "public static class Functions { [Ankus.PgOperator(\"@+\")] public static int Value(int value) => value + 1; }";

    /// <summary>
    /// Reads the real alias-only backing boundary artifact and cache decision.
    /// </summary>
    private static (FunctionEmission Emission, IncrementalStepRunReason Reason) TrackedAliasEmission(GeneratorDriver driver, string name)
    {
        (object value, IncrementalStepRunReason reason) = Assert.ContainsSingle(Assert.ContainsSingle(driver.GetRunResult().Results)
            .TrackedSteps["AliasFunctionEmission"].SelectMany(static step => step.Outputs).Where(output =>
                output.Reason != IncrementalStepRunReason.Removed && output.Value is FunctionEmission emission &&
                emission.NativeName.EndsWith("_" + name, StringComparison.Ordinal)));
        return (Assert.IsInstanceOfType<FunctionEmission>(value), reason);
    }

    /// <summary>
    /// Reads the real alias-only backing SQL artifact and cache decision.
    /// </summary>
    private static (FunctionSqlEmission Emission, IncrementalStepRunReason Reason) TrackedAliasSql(GeneratorDriver driver, string name)
    {
        (object value, IncrementalStepRunReason reason) = Assert.ContainsSingle(Assert.ContainsSingle(driver.GetRunResult().Results)
            .TrackedSteps["AliasFunctionSqlEmission"].SelectMany(static step => step.Outputs).Where(output =>
                output.Reason != IncrementalStepRunReason.Removed && output.Value is FunctionSqlEmission emission &&
                emission.Header.EndsWith("\"" + name + "\"(", StringComparison.Ordinal)));
        return (Assert.IsInstanceOfType<FunctionSqlEmission>(value), reason);
    }

    /// <summary>
    /// Reads one live attached declaration's independent SQL-render cache decision.
    /// </summary>
    private static (OperatorCastEmission Emission, IncrementalStepRunReason Reason) TrackedOperatorCast(GeneratorDriver driver, string name)
    {
        (object value, IncrementalStepRunReason reason) = Assert.ContainsSingle(Assert.ContainsSingle(driver.GetRunResult().Results)
            .TrackedSteps["OperatorCastEmission"].SelectMany(static step => step.Outputs).Where(output =>
                output.Reason != IncrementalStepRunReason.Removed && output.Value is OperatorCastEmission emission &&
                emission.Sql.Any(part => part.Text.Contains("FUNCTION = ", StringComparison.Ordinal) && part.Text.EndsWith("\"" + name + "\"", StringComparison.Ordinal) ||
                    part.Text.Contains("WITH FUNCTION ", StringComparison.Ordinal) && part.Text.EndsWith("\"" + name + "\"(", StringComparison.Ordinal))));
        return (Assert.IsInstanceOfType<OperatorCastEmission>(value), reason);
    }
}
