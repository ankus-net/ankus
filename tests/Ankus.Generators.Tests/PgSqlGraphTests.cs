using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Text;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Resolves schema/function dependencies, before constraints, and first/last SQL independently of declaration order.
    /// </summary>
    [TestMethod]
    public void SqlGraphOrdersAllDeclarationKinds()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgSql("last", "SELECT 'last';", Order = Ankus.PgSqlOrder.Finalize)]
            [assembly: Ankus.PgSql("view", "CREATE VIEW s.v AS SELECT s.f();", Requires = new[] { "function" })]
            [assembly: Ankus.PgSql("table", "CREATE TABLE s.t(n int);", Requires = new[] { "schema" }, Before = new[] { "function" })]
            [assembly: Ankus.PgSql("first", "SELECT 'first';", Order = Ankus.PgSqlOrder.Bootstrap)]
            [Ankus.PgSchema("s", Id = "schema")]
            public static class Functions
            {
                [Ankus.PgFunction(Id = "function", Requires = new[] { "table" })] public static int F() => 1;
            }
            """);
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        string sql = InstallationBody(compilation);
        Assert.StartsWith("SELECT 'first';\nCREATE SCHEMA IF NOT EXISTS \"s\";\nCREATE TABLE s.t(n int);\nCREATE FUNCTION \"s\".\"f\"()", sql);
        Assert.EndsWith("CREATE VIEW s.v AS SELECT s.f();\nSELECT 'last';\n", sql);
        Assert.AreEqual("false", ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// Independent blocks have stable output even when source declarations change order; SQL is preserved verbatim.
    /// </summary>
    [TestMethod]
    public void CustomSqlIsDeterministicAndPreservesStatementText()
    {
        const string first = """[assembly: Ankus.PgSql("a", "SELECT $$quotes' ; \\ café$$; -- tail", Relocatable = true)]""";
        const string second = """[assembly: Ankus.PgSql("b", "SELECT 2;\n", Relocatable = true)]""";
        (Compilation left, ImmutableArray<Diagnostic> diagnostics) = Generate(first + second);
        (Compilation right, ImmutableArray<Diagnostic> reordered) = Generate(second + first);
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(reordered);
        Assert.AreEqual("SELECT $$quotes' ; \\ café$$; -- tail\nSELECT 2;\n", InstallationBody(left));
        Assert.AreEqual(InstallationBody(left), InstallationBody(right));
        Assert.AreEqual("true", ManifestValue(left, "Ankus.Relocatable"));
        Assert.AreEqual("Pg_magic_func\n", ManifestValue(left, "Ankus.Exports"));
    }

    /// <summary>
    /// Invalid identifiers, references, ordering boundaries and cycles report one fixed contract at the authored value to change.
    /// </summary>
    /// <param name="source">The invalid declaration source.</param>
    /// <param name="expected">The fixed graph diagnostic.</param>
    /// <param name="span">The exact authored value to correct.</param>
    /// <param name="occurrence">The zero-based occurrence of that text in the source.</param>
    /// <param name="argument">The authored identity in the fixed message, or null when the message has none.</param>
    [TestMethod]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Requires = new[] { \"missing\" })]", "ANKUS498", "\"missing\"", 0, "missing")]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Before = new[] { \"missing\" })]", "ANKUS498", "\"missing\"", 0, "missing")]
    [DataRow("[assembly: Ankus.PgSql(\"first\", \"SELECT 1;\")][assembly: Ankus.PgSql(\"a\", \"SELECT 2;\", Requires = [\"first\", \"missing\"])]",
        "ANKUS498", "\"missing\"", 0, "missing")]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Requires = new[] { \"A\" })]", "ANKUS498", "\"A\"", 0, "A")]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Requires = new[] { \"a\" })]", "ANKUS499", "\"a\"", 1, "a -> a")]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Requires = new[] { \"b\" })] [assembly: Ankus.PgSql(\"b\", \"SELECT 2;\", Requires = new[] { \"a\" })]",
        "ANKUS499", "\"b\"", 0, "a -> b -> a")]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Order = Ankus.PgSqlOrder.Bootstrap, Requires = new[] { \"b\" })] [assembly: Ankus.PgSql(\"b\", \"SELECT 2;\")]",
        "ANKUS499", "\"b\"", 0, "a -> b -> a")]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Order = Ankus.PgSqlOrder.Finalize, Before = new[] { \"b\" })] [assembly: Ankus.PgSql(\"b\", \"SELECT 2;\")]",
        "ANKUS499", "\"b\"", 0, "a -> b -> a")]
    [DataRow("[Ankus.PgSchema(\"s\", Id = \"schema\", Requires = new[] { \"function\" })] public static class C { [Ankus.PgFunction(Id = \"function\")] public static int F() => 1; }",
        "ANKUS499", "\"function\"", 0, "schema -> function -> schema")]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\")] [assembly: Ankus.PgSql(\"a\", \"SELECT 2;\")]", "ANKUS497", "\"a\"", 1, "a")]
    [DataRow("public static class C { [Ankus.PgFunction(Id = \"x\")] public static int F() => 1; [Ankus.PgFunction(Id = \"x\")] public static int G() => 2; }",
        "ANKUS497", "\"x\"", 1, "x")]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Order = Ankus.PgSqlOrder.Bootstrap)] [assembly: Ankus.PgSql(\"b\", \"SELECT 2;\", Order = Ankus.PgSqlOrder.Bootstrap)]",
        "ANKUS500", "Ankus.PgSqlOrder.Bootstrap", 1, null)]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Order = Ankus.PgSqlOrder.Finalize)] [assembly: Ankus.PgSql(\"b\", \"SELECT 2;\", Order = Ankus.PgSqlOrder.Finalize)]",
        "ANKUS501", "Ankus.PgSqlOrder.Finalize", 1, null)]
    [DataRow("public static class C { [Ankus.PgFunction(Id = \" \")] public static int F() => 1; }", "ANKUS490", "\" \"", 0, null)]
    [DataRow("[Ankus.PgSchema(\"s\", Id = \"\")] public static class C;", "ANKUS490", "\"\"", 0, null)]
    [DataRow("public static class C { [Ankus.PgFunction(Id = \"x\\0y\")] public static int F() => 1; }", "ANKUS491", "\"x\\0y\"", 0, null)]
    [DataRow("public static class C { [Ankus.PgFunction(Id = \"\\ud800\")] public static int F() => 1; }", "ANKUS492", "\"\\ud800\"", 0, null)]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Requires = null!)]", "ANKUS493", "null!", 0, null)]
    [DataRow("[Ankus.PgSchema(\"s\", Requires = null!)] public static class C;", "ANKUS493", "null!", 0, null)]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Before = null!)]", "ANKUS493", "null!", 0, null)]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Before = new[] { \"\" })]", "ANKUS494", "\"\"", 0, null)]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Requires = new string[] { \"a\", null! })]", "ANKUS494", "null!", 0, null)]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Requires = [\"b\\0\"])]", "ANKUS495", "\"b\\0\"", 0, null)]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Before = [\"\\udc00\"])]", "ANKUS496", "\"\\udc00\"", 0, null)]
    public void InvalidSqlGraphsReportTheAuthoredValue(string source, string expected, string span, int occurrence, string? argument)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        AssertSqlControlGraphError(compilation, diagnostics, expected, span, argument is null ? [] : [argument]);
        AssertDiagnosticOccurrence(diagnostics[0], span, occurrence);
    }

    /// <summary>
    /// Every additional bootstrap or final block reports its own Order value.
    /// </summary>
    /// <param name="order">The repeated installation boundary.</param>
    /// <param name="expected">The fixed boundary diagnostic.</param>
    [TestMethod]
    [DataRow("Bootstrap", "ANKUS500")]
    [DataRow("Finalize", "ANKUS501")]
    public void RepeatedSqlBoundariesReportEachExtraBlock(string order, string expected)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(
            "[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Order = Ankus.PgSqlOrder." + order + ")]" +
            "[assembly: Ankus.PgSql(\"b\", \"SELECT 2;\", Order = Ankus.PgSqlOrder." + order + ")]" +
            "[assembly: Ankus.PgSql(\"c\", \"SELECT 3;\", Order = Ankus.PgSqlOrder." + order + ")]");
        Assert.HasCount(2, diagnostics);
        for (int index = 0; index < diagnostics.Length; index++)
        {
            AssertGraphDiagnostic(diagnostics[index], expected, "Ankus.PgSqlOrder." + order);
            AssertDiagnosticOccurrence(diagnostics[index], "Ankus.PgSqlOrder." + order, index + 1);
        }

        AssertNoSqlManifest(compilation);
    }

    /// <summary>
    /// A reference inherited from an aggregate by its support functions reports the authored entry once.
    /// </summary>
    [TestMethod]
    public void InheritedMissingDependenciesReportTheAuthoredEntryOnce()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [Ankus.PgAggregate(Requires = ["absent"], InitialCondition = "0")]
            public sealed class Total : Ankus.IPgAggregate<int,int>
            {
                public static int Transition(Ankus.PgAggregateContext context, int state, int value) => state + value;
            }
            """);
        AssertSqlControlGraphError(compilation, diagnostics, "ANKUS498", "\"absent\"", "absent");
    }

    /// <summary>
    /// Invalid custom SQL inputs fail compilation with a declaration diagnostic.
    /// </summary>
    /// <param name="source">The invalid declaration source.</param>
    /// <param name="message">The diagnostic fragment identifying the failure.</param>
    /// <param name="expected">The precise declaration diagnostic.</param>
    [TestMethod]
    [DataRow("[assembly: Ankus.PgSql(\"\", \"SELECT 1;\")]", "dependency name", "ANKUS355")]
    [DataRow("[assembly: Ankus.PgSql(null!, \"SELECT 1;\")]", "dependency name", "ANKUS355")]
    [DataRow("[assembly: Ankus.PgSql(\"a\", null!)]", "nonnull SQL string", "ANKUS359")]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT '\\0';\")]", "zero characters", "ANKUS360")]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT '\\ud800';\")]", "surrogate", "ANKUS361")]
    [DataRow("[assembly: Ankus.PgSql(\"a\", \"SELECT 1;\", Order = (Ankus.PgSqlOrder)3)]", "defined PgSqlOrder", "ANKUS358")]
    public void InvalidSqlDependenciesAreDiagnosed(string source, string message, string expected)
    {
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(expected, diagnostic.Id);
        Assert.Contains(message, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Dependency identifiers are escaped in graph messages, so a quote, backslash or line break cannot end the quoted
    /// name or split the message, while ordinary identifiers read as written.
    /// </summary>
    /// <param name="source">The declarations.</param>
    /// <param name="expected">The diagnostic.</param>
    /// <param name="message">The exact message.</param>
    [TestMethod]
    [DataRow("[assembly: Ankus.PgSql(\"a'b\\nc\", \"SELECT 1;\")] [assembly: Ankus.PgSql(\"a'b\\nc\", \"SELECT 2;\")]", "ANKUS497",
        "Dependency identifier 'a\\'b\\nc' is already declared in this extension; give each SQL declaration a distinct Id or PgSql name")]
    [DataRow("[assembly: Ankus.PgSql(\"one\", \"SELECT 1;\", Requires = [\"tab\\there\\\\\"])]", "ANKUS498",
        "No SQL declaration in this extension has dependency identifier 'tab\\there\\\\'; declare it with Id, PgSql or PgSqlFile, or correct this entry")]
    [DataRow("[assembly: Ankus.PgSql(\"x'\", \"SELECT 1;\", Requires = [\"y\"])] [assembly: Ankus.PgSql(\"y\", \"SELECT 2;\", Requires = [\"x'\"])]",
        "ANKUS499", "Installation SQL dependencies form the cycle x\\' -> y -> x\\'; remove or reverse one explicit dependency in it")]
    public void GraphMessagesEscapeDependencyIdentifiers(string source, string expected, string message)
    {
        (Compilation output, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(expected, diagnostic.Id);
        Assert.AreEqual(message, diagnostic.GetMessage(CultureInfo.InvariantCulture));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// All aliases for a shared schema resolve to one creation node without losing dependencies.
    /// </summary>
    [TestMethod]
    public void RepeatedSchemaDeclarationsShareOneGraphNode()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgSql("table", "CREATE TABLE s.t(n int);", Requires = new[] { "one", "two" })]
            [Ankus.PgSchema("s", Id = "one")] public static class A;
            [Ankus.PgSchema("s", Id = "two", Create = false)] public static class B;
            """);
        Assert.IsEmpty(diagnostics);
        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"s\";\nCREATE TABLE s.t(n int);\n", InstallationBody(compilation));
    }

    /// <summary>
    /// File paths resolve relative to the project, normalize separators and parent segments, and retain file content.
    /// </summary>
    [TestMethod]
    public void SqlFilesUseTrackedProjectRelativeInputs()
    {
        string project = Path.Combine(AppContext.BaseDirectory, "virtual-project");
        var options = new SqlOptions(project);
        var file = new SqlInput(Path.Combine(project, "sql", "seed.sql"), "SELECT 'file'; -- exact\n");
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgSqlFile("seed", "sql\\nested/../seed.sql", Relocatable = true)]
            """, [file], options);
        Assert.IsEmpty(diagnostics);
        Assert.AreEqual("SELECT 'file'; -- exact\n", InstallationBody(compilation));
        Assert.AreEqual("true", ManifestValue(compilation, "Ankus.Relocatable"));
    }

    /// <summary>
    /// A compiler-resolved project-root alias still identifies the unique tracked relative SQL input.
    /// </summary>
    [TestMethod]
    public void SqlFilesUseUniqueRelativeInputAcrossProjectRootAliases()
    {
        string root = Path.GetPathRoot(AppContext.BaseDirectory)!;
        string project = Path.Combine(root, "declared-root", "project alias");
        string resolved = Path.Combine(root, "resolved-root", "project alias", "sql setup", "seed.sql");
        var options = new SqlOptions(project);
        var file = new SqlInput(resolved, "SELECT 'aliased';\n");
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgSqlFile("seed", "sql setup/seed.sql", Relocatable = true)]
            """, [file], options);
        Assert.IsEmpty(diagnostics);
        Assert.AreEqual("SELECT 'aliased';\n", InstallationBody(compilation));
    }

    /// <summary>
    /// Missing, duplicated, and unreadable file inputs cannot silently produce an incomplete installation script.
    /// </summary>
    /// <param name="kind">The invalid file-input scenario.</param>
    [TestMethod]
    [DataRow("missing")]
    [DataRow("duplicate")]
    [DataRow("unreadable")]
    [DataRow("no project directory")]
    public void InvalidSqlFilesAreDiagnosed(string kind)
    {
        string project = Path.Combine(AppContext.BaseDirectory, "virtual-project");
        var file = new SqlInput(Path.Combine(project, "seed.sql"), kind == "unreadable" ? null : "SELECT 1;");
        AdditionalText[] files = kind == "missing" ? [] : kind == "duplicate"
            ? [file, new SqlInput(Path.Combine(project, "nested", "..", "seed.sql"), "SELECT 2;")] : [file];
        (_, ImmutableArray<Diagnostic> diagnostics) = Generate("""[assembly: Ankus.PgSqlFile("seed", "seed.sql")]""",
            files, kind == "no project directory" ? null : new SqlOptions(project));
        Assert.AreEqual(kind switch
        {
            "missing" => "ANKUS367",
            "duplicate" => "ANKUS368",
            "unreadable" => "ANKUS369",
            _ => "ANKUS366",
        }, Assert.ContainsSingle(diagnostics).Id);
    }

    /// <summary>
    /// Replacing an AdditionalFiles input invalidates cached generation without changing C# source.
    /// </summary>
    [TestMethod]
    public void SqlFileChangesInvalidateIncrementalOutput()
    {
        string project = Path.Combine(AppContext.BaseDirectory, "virtual-project");
        var original = new SqlInput(Path.Combine(project, "seed.sql"), "SELECT 1;");
        var replacement = new SqlInput(original.Path, "SELECT 2;");
        CSharpCompilation input = CSharpCompilation.Create("SqlFileTest",
            [CSharpSyntaxTree.ParseText("""[assembly: Ankus.PgSqlFile("seed", "seed.sql")]""", cancellationToken: context.CancellationToken)],
            s_references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe: true));
        GeneratorDriver driver = CSharpGeneratorDriver.Create([new PgFunctionGenerator().AsSourceGenerator()],
            [original], optionsProvider: new SqlOptions(project));
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out Compilation first, out ImmutableArray<Diagnostic> initialErrors, context.CancellationToken);
        Assert.IsEmpty(initialErrors);
        Assert.AreEqual("SELECT 1;\n", InstallationBody(first));
        driver.ReplaceAdditionalText(original, replacement).RunGeneratorsAndUpdateCompilation(input, out Compilation second,
            out ImmutableArray<Diagnostic> updatedErrors, context.CancellationToken);
        Assert.IsEmpty(updatedErrors);
        Assert.AreEqual("SELECT 2;\n", InstallationBody(second));
    }

    private sealed class SqlInput(string path, string? content) : AdditionalText
    {
        public override string Path => path;
        public override SourceText? GetText(CancellationToken cancellationToken = default) => content is null ? null : SourceText.From(content);
    }

    private sealed class SqlOptions(string project) : AnalyzerConfigOptionsProvider
    {
        public override AnalyzerConfigOptions GlobalOptions { get; } = new ProjectOptions(project);
        public override AnalyzerConfigOptions GetOptions(SyntaxTree tree) => GlobalOptions;
        public override AnalyzerConfigOptions GetOptions(AdditionalText textFile) => GlobalOptions;
    }

    private sealed class ProjectOptions(string project) : AnalyzerConfigOptions
    {
        public override bool TryGetValue(string key, out string value)
        {
            value = project;
            return key == "build_property.MSBuildProjectDirectory";
        }
    }
}
