using System.Collections.Immutable;
using Ankus.PgConfig;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Managed method identity orders declarations independently of names, IDs and source order.
    /// </summary>
    /// <param name="direction">The edge expressed from the consumer or prerequisite.</param>
    [TestMethod]
    [DataRow("requires")]
    [DataRow("before")]
    public void TypedDependenciesOrderExactMethods(string direction)
    {
        string requires = direction == "requires" ? "[Ankus.PgRequires(typeof(Z), nameof(Z.F))]" : string.Empty;
        string before = direction == "before" ? "[Ankus.PgBefore(typeof(A), nameof(A.F))]" : string.Empty;
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            [assembly: Ankus.PgSql("after", "SELECT 'after';")]
            [assembly: Ankus.PgRequires(typeof(A), nameof(A.F), DeclarationId = "after")]
            public static class A
            {
                {{requires}}
                [Ankus.PgFunction(Name = "a", Sql = "SELECT 'consumer';")] public static int F() => 1;
            }
            public static class Z
            {
                {{before}}
                [Ankus.PgFunction(Name = "z", Sql = "SELECT 'prerequisite';")] public static int F() => 2;
            }
            """);
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
        Assert.AreEqual("SELECT 'prerequisite';\nSELECT 'consumer';\nSELECT 'after';\n", InstallationBody(compilation));
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        Assert.AreSequenceEqual<string>([graph.Items[0].Id], graph.Items[1].Dependencies);
        Assert.AreSequenceEqual<string>([graph.Items[1].Id], graph.Items[2].Dependencies);
    }

    /// <summary>
    /// Exact managed parameter types disambiguate an overload without relying on SQL names.
    /// </summary>
    /// <param name="parameters">The explicit overload selection, including the empty signature.</param>
    /// <param name="expected">The declaration that must precede the SQL block.</param>
    [TestMethod]
    [DataRow("new System.Type[] { }", "empty")]
    [DataRow("new[] { typeof(int) }", "number")]
    [DataRow("new[] { typeof(int?) }", "nullable")]
    [DataRow("new[] { typeof(string) }", "text")]
    public void TypedDependenciesSelectOverloads(string parameters, string expected)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            [assembly: Ankus.PgSql("before", "SELECT 'before';")]
            [assembly: Ankus.PgBefore(typeof(Functions), nameof(Functions.F), ParameterTypes = {{parameters}}, DeclarationId = "before")]
            public static class Functions
            {
                [Ankus.PgFunction(Sql = "SELECT 'empty';")] public static int F() => 1;
                [Ankus.PgFunction(Sql = "SELECT 'number';")] public static int F(int value) => value;
                [Ankus.PgFunction(Name = "f_optional", Sql = "SELECT 'nullable';")] public static int? F(int? value) => value;
                [Ankus.PgFunction(Sql = "SELECT 'text';")] public static string F(string value) => value;
            }
            """);
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
        string sql = InstallationBody(compilation);
        AssertSqlControlBefore(sql, "SELECT 'before';", $"SELECT '{expected}';");
        string[] variants = ["empty", "number", "nullable", "text"];
        foreach (string other in variants.Where(other => other != expected))
        {
            AssertSqlControlBefore(sql, $"SELECT '{other}';", "SELECT 'before';");
        }
    }

    /// <summary>
    /// Type identity selects generated schemas, enums, types and aggregates without explicit IDs.
    /// </summary>
    /// <param name="declaration">The generated declaration under test.</param>
    /// <param name="sql">The principal object's installation statement.</param>
    [TestMethod]
    [DataRow("[Ankus.PgSchema(\"declared\")] public static class Value;", "CREATE SCHEMA IF NOT EXISTS \"declared\";")]
    [DataRow("[Ankus.PgEnum] public enum Value { First }", "CREATE TYPE \"value\" AS ENUM")]
    [DataRow("[Ankus.PgType] public readonly record struct Value(int Number);", "CREATE TYPE \"value\" (")]
    [DataRow("[Ankus.PgAggregate(InitialCondition = \"0\")] public sealed class Value : Ankus.IPgAggregate<int,int> { " +
        "public static int Transition(Ankus.PgAggregateContext context,int state, int value) => state + value; }", "CREATE AGGREGATE \"value\"")]
    public void TypedDependenciesSelectTypeDeclarations(string declaration, string sql)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgSql("before", "SELECT 'before';")]
            [assembly: Ankus.PgBefore(typeof(Value), DeclarationId = "before")]
            """ + declaration);
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
        AssertSqlControlBefore(InstallationBody(compilation), "SELECT 'before';", sql);
    }

    /// <summary>
    /// Inherited method lookup and shared schema aliases retain the actual registered declaration identity.
    /// </summary>
    [TestMethod]
    public void TypedDependenciesResolveInheritedMethodsAndSharedSchemas()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgSql("before", "SELECT 'before';")]
            [assembly: Ankus.PgBefore(typeof(Derived), nameof(Derived.F), DeclarationId = "before")]
            [assembly: Ankus.PgRequires(typeof(First), DeclarationId = "before")]
            [assembly: Ankus.PgRequires(typeof(Second), DeclarationId = "before")]
            [Ankus.PgSchema("shared")] public static class First;
            [Ankus.PgSchema("shared", Create = false)] public static class Second;
            public class Base
            {
                [Ankus.PgFunction(Sql = "SELECT 'inherited';")] public static int F() => 1;
            }
            public class Derived : Base;
            """);
        Assert.IsEmpty(diagnostics);
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
        Assert.AreEqual("CREATE SCHEMA IF NOT EXISTS \"shared\";\nSELECT 'before';\nSELECT 'inherited';\n", InstallationBody(compilation));
    }

    /// <summary>
    /// Invalid source and destination references report the authored value to correct and emit no installation manifest.
    /// </summary>
    /// <param name="attribute">The invalid dependency.</param>
    /// <param name="reason">The independent diagnostic reason.</param>
    /// <param name="expected">The fixed diagnostic ID.</param>
    /// <param name="span">The exact authored text to correct.</param>
    [TestMethod]
    [DataRow("[assembly: Ankus.PgRequires(typeof(Functions))]", "Set DeclarationId", "ANKUS479", "Ankus.PgRequires(typeof(Functions))")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(Functions), DeclarationId = \"missing\")]", "'missing' must identify exactly one", "ANKUS478", "\"missing\"")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(Functions), DeclarationId = \" \" )]", "DeclarationId must be nonblank", "ANKUS477", "\" \"")]
    [DataRow("[assembly: Ankus.PgRequires(null!, DeclarationId = \"sql\")]", "non-null closed", "ANKUS471", "null!")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(int[]), DeclarationId = \"sql\")]", "non-null closed", "ANKUS471", "typeof(int[])")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(System.Collections.Generic.List<>), DeclarationId = \"sql\")]", "non-null closed", "ANKUS471",
        "typeof(System.Collections.Generic.List<>)")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(Functions), DeclarationId = \"sql\")]", "'Functions' does not declare a generated SQL object", "ANKUS480",
        "typeof(Functions)")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(Functions), \"missing\", DeclarationId = \"sql\")]", "'Functions.missing' was not found", "ANKUS475", "\"missing\"")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(Functions), \"\", DeclarationId = \"sql\")]", "nonblank method name", "ANKUS473", "\"\"")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(Functions), nameof(Functions.F), DeclarationId = \"sql\")]", "has several overloads; set ParameterTypes",
        "ANKUS476", "nameof(Functions.F)")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(Functions), nameof(Functions.F), ParameterTypes = new[] { typeof(string) }, DeclarationId = \"sql\")]",
        "No overload of 'Functions.F' has exactly the selected ParameterTypes", "ANKUS486", "new[] { typeof(string) }")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(Functions), nameof(Functions.G), ParameterTypes = new[] { typeof(int) }, DeclarationId = \"sql\")]",
        "Several overloads of 'Functions.G' have exactly the selected ParameterTypes", "ANKUS487", "new[] { typeof(int) }")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(Functions), nameof(Functions.F), ParameterTypes = new System.Type[] { null! }, DeclarationId = \"sql\")]",
        "only non-null", "ANKUS474", "null!")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(Functions), nameof(Functions.F), ParameterTypes = [typeof(int), typeof(System.Collections.Generic.List<>)], DeclarationId = \"sql\")]",
        "closed managed types", "ANKUS474", "typeof(System.Collections.Generic.List<>)")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(Functions), ParameterTypes = new System.Type[] { }, DeclarationId = \"sql\")]", "method name", "ANKUS472",
        "new System.Type[] { }")]
    [DataRow("[assembly: Ankus.PgRequires(typeof(Functions), nameof(Functions.Ordinary), DeclarationId = \"sql\")]",
        "'Functions.Ordinary()' does not declare a generated SQL object", "ANKUS480", "nameof(Functions.Ordinary)")]
    public void TypedDependenciesRejectInvalidReferences(string attribute, string reason, string expected, string span)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(attribute + """
            [assembly: Ankus.PgSql("sql", "SELECT 1;")]
            public static class Functions
            {
                [Ankus.PgFunction] public static int F() => 1;
                [Ankus.PgFunction] public static int F(int value) => value;
                [Ankus.PgFunction] public static int G(int value) => value;
                public static int G(ref int value) => value;
                public static int Ordinary() => 2;
            }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(expected, diagnostic.Id);
        Assert.Contains(reason, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(span, DiagnosticText(diagnostic));
        Assert.EndsWith("#reference-managed-declarations", diagnostic.Descriptor.HelpLinkUri);
        Assert.IsFalse(compilation.Assembly.GetAttributes().Any(static item => item.ConstructorArguments.Length == 2 &&
            item.ConstructorArguments[0].Value is "Ankus.Sql"));
    }

    /// <summary>
    /// Typed edges participate in the same complete cycle validation as explicit string identifiers.
    /// </summary>
    /// <param name="self">Whether the edge directly references its own declaration.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void TypedDependenciesRejectCycles(bool self)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            public static class Functions
            {
                [Ankus.PgFunction, Ankus.PgRequires(typeof(Functions), nameof({{(self ? "A" : "Z")}}))]
                public static int A() => 1;
                [Ankus.PgFunction, Ankus.PgRequires(typeof(Functions), nameof(A))]
                public static int Z() => 2;
            }
            """);
        AssertSqlControlGraphError(compilation, diagnostics, "ANKUS499", "Ankus.PgRequires(typeof(Functions), nameof(" + (self ? "A" : "Z") + "))",
            self ? "Functions.A() -> Functions.A()" : "Functions.A() -> Functions.Z() -> Functions.A()");
    }

    /// <summary>
    /// Disabled SQL retains its typed prerequisite and remains a dependency target.
    /// </summary>
    [TestMethod]
    public void TypedDependenciesRetainDisabledDeclarations()
    {
        Compilation compilation = GenerateSqlControl("""
            [assembly: Ankus.PgSql("after", "SELECT 'after';")]
            [assembly: Ankus.PgRequires(typeof(Functions), nameof(Functions.A), DeclarationId = "after")]
            public static class Functions
            {
                [Ankus.PgFunction(GenerateSql = false), Ankus.PgRequires(typeof(Functions), nameof(Z))]
                public static int A() => 1;
                [Ankus.PgFunction(Sql = "SELECT 'before';")] public static int Z() => 2;
            }
            """);
        Assert.AreEqual("SELECT 'before';\nSELECT 'after';\n", InstallationBody(compilation));
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        Assert.HasCount(3, graph.Items);
        Assert.IsEmpty(graph.Items[1].Sql);
        Assert.AreSequenceEqual<string>([graph.Items[0].Id], graph.Items[1].Dependencies);
        Assert.AreSequenceEqual<string>([graph.Items[1].Id], graph.Items[2].Dependencies);
    }

    /// <summary>
    /// Attached operator prerequisites are hoisted ahead of a complete function SQL replacement.
    /// </summary>
    [TestMethod]
    public void TypedDependenciesPreserveReplacementFamilies()
    {
        Compilation compilation = GenerateSqlControl("""
            public static class Functions
            {
                [Ankus.PgFunction(Sql = "SELECT 'replacement';"), Ankus.PgOperator("@", Id = "operator")]
                [Ankus.PgRequires(typeof(Functions), nameof(Z), DeclarationId = "operator")]
                public static int A(int value) => value;
                [Ankus.PgFunction(Sql = "SELECT 'prerequisite';")] public static int Z() => 2;
            }
            """);
        Assert.AreEqual("SELECT 'prerequisite';\nSELECT 'replacement';\n", InstallationBody(compilation));
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        ExtensionSchemaItem replacement = Assert.ContainsSingle(graph.Items.Where(static item => item.Sql.Contains("replacement", StringComparison.Ordinal)));
        Assert.Contains(graph.Items[0].Id, replacement.Dependencies);
        Assert.ContainsSingle(graph.Items.Where(static item => item.Kind == "operator"));
    }

    /// <summary>
    /// Aggregate Requires constraints precede every compiler-selected support method, including independent moving helpers.
    /// </summary>
    [TestMethod]
    public void TypedDependenciesOrderAggregateHelpers()
    {
        Compilation compilation = GenerateSqlControl("""
            [assembly: Ankus.PgSql("after", "SELECT 'after helper';")]
            [assembly: Ankus.PgRequires(typeof(Total), nameof(Total.Transition), DeclarationId = "after")]
            [Ankus.PgAggregate(InitialCondition = "0", MovingInitialCondition = "0")]
            [Ankus.PgRequires(typeof(Z), nameof(Z.F))]
            public sealed class Total : Ankus.IPgAggregate<int,int>,Ankus.IPgMovingAggregate<int,int>
            {
                [Ankus.PgFunction(Name = "shared_transition")]
                public static int Transition(Ankus.PgAggregateContext context,int state, int value) => Add(state,value);
                public static int MovingTransition(Ankus.PgAggregateContext context,int state, int value) => Add(state,value);
                public static int MovingInverse(Ankus.PgAggregateContext context,int state, int value) => state - value;
                private static int Add(int state,int value) => state + value;
            }
            public static class Z
            {
                [Ankus.PgFunction(Sql = "SELECT 'prerequisite';")] public static int F() => 1;
            }
            """);
        string sql = ManifestValue(compilation, "Ankus.Sql");
        AssertSqlControlBefore(sql, "SELECT 'prerequisite';", "CREATE FUNCTION \"shared_transition\"");
        AssertSqlControlBefore(sql, "CREATE FUNCTION \"shared_transition\"", "SELECT 'after helper';");
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        ExtensionSchemaItem prerequisite = Assert.ContainsSingle(graph.Items.Where(static item => item.Sql.Contains("prerequisite", StringComparison.Ordinal)));
        foreach (ExtensionSchemaItem item in graph.Items.Where(item => item.Id != prerequisite.Id && item.Kind is "function" or "aggregate"))
        {
            Assert.Contains(prerequisite.Id, item.Dependencies);
        }
    }

    /// <summary>
    /// A typed edge can place a shell consumer before its inferred completed-type provider.
    /// </summary>
    [TestMethod]
    public void TypedDependenciesPreserveExplicitShellOrdering()
    {
        Compilation compilation = GenerateSqlControl("""
            [assembly: Ankus.PgSql("shell", "CREATE TYPE item;")]
            [assembly: Ankus.PgSql("types", "SELECT 'complete';")]
            [assembly: Ankus.PgRequires(typeof(Functions), nameof(Functions.Input), DeclarationId = "types")]
            [assembly: Ankus.PgSqlTypeProvider("types", "item")]
            public static class Functions
            {
                [Ankus.PgFunction(Requires = ["shell"], Sql = "SELECT 'io';")]
                [return: Ankus.PgSqlType("item")]
                public static Ankus.PgDatum? Input() => null;
                [Ankus.PgFunction(Sql = "SELECT 'consumer';")]
                public static int Read([Ankus.PgSqlType("item")] Ankus.PgDatum value) => 7;
            }
            """);
        Assert.AreEqual("CREATE TYPE item;\nSELECT 'io';\nSELECT 'complete';\nSELECT 'consumer';\n", InstallationBody(compilation));
    }

    /// <summary>
    /// A dependency on an ordinary declaration is diagnosed at the source even when it supplies a valid selector or target.
    /// </summary>
    /// <param name="attribute">The dependency attached to a declaration that emits no SQL.</param>
    [TestMethod]
    [DataRow("Ankus.PgRequires(typeof(int))")]
    [DataRow("Ankus.PgRequires(typeof(Functions), nameof(Functions.F), DeclarationId = \"f\")")]
    [DataRow("Ankus.PgBefore(typeof(Functions), nameof(Functions.F))")]
    public void TypedDependenciesRejectUnmappedSource(string attribute)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            [{{attribute}}] public static class Ordinary;
            public static class Functions { [Ankus.PgFunction] public static int F() => 1; }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS488", diagnostic.Id);
        Assert.Contains("Attributed declaration 'Ordinary' does not declare a generated SQL object",
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual(attribute, DiagnosticText(diagnostic));
        Assert.IsFalse(compilation.Assembly.GetAttributes().Any(static item => item.ConstructorArguments.Length == 2 &&
            item.ConstructorArguments[0].Value is "Ankus.Sql"));
    }

    /// <summary>
    /// Trigger entry points participate without requiring a redundant PgFunction attribute.
    /// </summary>
    /// <param name="declaration">The trigger or event-trigger method.</param>
    [TestMethod]
    [DataRow("[Ankus.PgTrigger] public static Ankus.PgHeapTuple? F(Ankus.PgTriggerContext context) => null;")]
    [DataRow("[Ankus.PgEventTrigger] public static void F(Ankus.PgEventTriggerContext context) { }")]
    public void TypedDependenciesSelectTriggerDeclarations(string declaration)
    {
        Compilation compilation = GenerateSqlControl($$"""
            [assembly: Ankus.PgSql("before", "SELECT 'before';")]
            [assembly: Ankus.PgBefore(typeof(Functions), nameof(Functions.F), DeclarationId = "before")]
            public static class Functions { {{declaration}} }
            """);
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        Assert.HasCount(2, graph.Items);
        Assert.AreEqual("/* <begin connected objects> */\n-- before\n\nSELECT 'before';\n/* </end connected objects> */\n\n", graph.Items[0].Sql);
        Assert.AreEqual("function", graph.Items[1].Kind);
        Assert.AreSequenceEqual<string>([graph.Items[0].Id], graph.Items[1].Dependencies);
    }

    /// <summary>
    /// Managed references preserve global bootstrap and final-block constraints.
    /// </summary>
    /// <param name="order">The installation boundary.</param>
    /// <param name="edge">The constraint that conflicts with that boundary.</param>
    [TestMethod]
    [DataRow("Bootstrap", "PgRequires")]
    [DataRow("Finalize", "PgBefore")]
    public void TypedDependenciesRejectBoundaryCycles(string order, string edge)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            [assembly: Ankus.PgSql("boundary", "SELECT 1;", Order = Ankus.PgSqlOrder.{{order}})]
            [assembly: Ankus.{{edge}}(typeof(Functions), nameof(Functions.F), DeclarationId = "boundary")]
            public static class Functions { [Ankus.PgFunction] public static int F() => 1; }
            """);
        AssertSqlControlGraphError(compilation, diagnostics, "ANKUS499", "Ankus." + edge + "(typeof(Functions), nameof(Functions.F), DeclarationId = \"boundary\")",
            "Functions.F() -> boundary -> Functions.F()");
    }

    /// <summary>
    /// A method-level source identifier cannot silently order an unrelated declaration.
    /// </summary>
    [TestMethod]
    public void TypedDependenciesRejectForeignSourceIdentifiers()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgSql("foreign", "SELECT 1;")]
            public static class Functions
            {
                [Ankus.PgFunction, Ankus.PgRequires(typeof(Functions), nameof(Z), DeclarationId = "foreign")]
                public static int A() => 1;
                [Ankus.PgFunction] public static int Z() => 2;
            }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS478", diagnostic.Id);
        Assert.Contains("belonging to the attributed declaration", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual("\"foreign\"", DiagnosticText(diagnostic));
        Assert.IsFalse(compilation.Assembly.GetAttributes().Any(static item => item.ConstructorArguments.Length == 2 &&
            item.ConstructorArguments[0].Value is "Ankus.Sql"));
    }

    /// <summary>
    /// A target type with multiple primary SQL declarations requires a string identifier instead of an arbitrary first match.
    /// </summary>
    [TestMethod]
    public void TypedDependenciesRejectAmbiguousTypes()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [assembly: Ankus.PgSql("after", "SELECT 1;")]
            [assembly: Ankus.PgRequires(typeof(Total), DeclarationId = "after")]
            [Ankus.PgSchema("declared"), Ankus.PgAggregate(InitialCondition = "0")]
            public sealed class Total : Ankus.IPgAggregate<int,int>
            {
                public static int Transition(Ankus.PgAggregateContext context, int state, int value) => state + value;
            }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS481", diagnostic.Id);
        Assert.Contains("'Total' emits several SQL objects; give one an Id and list that Id in Requires or Before",
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual("typeof(Total)", DiagnosticText(diagnostic));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static item => item.Severity == DiagnosticSeverity.Error));
        Assert.IsFalse(compilation.Assembly.GetAttributes().Any(static item => item.ConstructorArguments.Length == 2 &&
            item.ConstructorArguments[0].Value is "Ankus.Sql"));
    }

    /// <summary>
    /// A source type with multiple primary SQL declarations requires DeclarationId, which then orders only the selected declaration.
    /// </summary>
    [TestMethod]
    public void TypedDependenciesRequireSelectorsForAmbiguousSources()
    {
        const string Source = """
            [Ankus.PgRequires(typeof(Prerequisite), nameof(Prerequisite.Before))]
            [Ankus.PgSchema("declared"), Ankus.PgAggregate(Id = "total", InitialCondition = "0")]
            public sealed class Total : Ankus.IPgAggregate<int,int>
            {
                public static int Transition(Ankus.PgAggregateContext context, int state, int value) => state + value;
            }
            public static class Prerequisite
            {
                [Ankus.PgFunction(Sql = "SELECT 'prerequisite';")] public static int Before() => 1;
            }
            """;
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(Source);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS489", diagnostic.Id);
        Assert.Contains("'Total' emits several SQL objects; give the one being ordered an Id and set DeclarationId",
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual("Ankus.PgRequires(typeof(Prerequisite), nameof(Prerequisite.Before))", DiagnosticText(diagnostic));
        Assert.IsFalse(compilation.Assembly.GetAttributes().Any(static item => item.ConstructorArguments.Length == 2 &&
            item.ConstructorArguments[0].Value is "Ankus.Sql"));

        Compilation repaired = GenerateSqlControl(Source.Replace("nameof(Prerequisite.Before))", "nameof(Prerequisite.Before), DeclarationId = \"total\")",
            StringComparison.Ordinal));
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(repaired, "Ankus.SqlGraph"));
        ExtensionSchemaItem prerequisite = Assert.ContainsSingle(graph.Items.Where(static item => item.Sql.Contains("'prerequisite'", StringComparison.Ordinal)));
        ExtensionSchemaItem aggregate = Assert.ContainsSingle(graph.Items.Where(static item => item.Kind == "aggregate"));
        ExtensionSchemaItem schema = Assert.ContainsSingle(graph.Items.Where(static item => item.Kind == "schema"));
        Assert.Contains(prerequisite.Id, aggregate.Dependencies);
        Assert.DoesNotContain(prerequisite.Id, schema.Dependencies);
    }
}
