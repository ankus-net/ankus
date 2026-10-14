using System.Collections.Immutable;
using Ankus.PgConfig;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Managed references use the target's SQL identity and preserve its prerequisite in the encoded graph.
    /// </summary>
    /// <param name="schema">The support function's optional fixed schema.</param>
    [TestMethod]
    [DataRow(default(string))]
    [DataRow("planner")]
    public void PlannerSupportUsesDeclaredIdentity(string? schema)
    {
        string schemaOption = schema is null ? string.Empty : ", Schema = \"" + schema + "\"";
        Compilation compilation = GenerateSqlControl($$"""
            public static class Functions
            {
                [Ankus.PgFunction, Ankus.PgSupportFunction(typeof(Functions), nameof(ZSupport))]
                public static int A(int value) => value;
                [Ankus.PgFunction(Name = "renamed_support"{{schemaOption}})]
                public static Ankus.PgInternal? ZSupport(Ankus.PgInternal request) => null;
            }
            """);
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        Assert.HasCount(2, graph.Items);
        string identity = (schema is null ? string.Empty : "\"planner\".") + "\"renamed_support\"";
        Assert.StartsWith("CREATE FUNCTION " + identity, DeclarationBodies(compilation).First());
        Assert.Contains("SUPPORT " + identity, graph.Items[1].Sql);
        Assert.AreSequenceEqual<string>([graph.Items[0].Id], graph.Items[1].Dependencies);
    }

    /// <summary>
    /// All generated function families receive the resolved support clause and graph edge.
    /// </summary>
    /// <param name="consumer">The function declaration under test.</param>
    [TestMethod]
    [DataRow("[Ankus.PgFunction] public static int A(int value) => value;")]
    [DataRow("[Ankus.PgFunction] public static System.Collections.Generic.IEnumerable<int> A() => [1];")]
    [DataRow("[Ankus.PgTrigger] public static Ankus.PgHeapTuple? A(Ankus.PgTriggerContext context) => null;")]
    [DataRow("[Ankus.PgEventTrigger] public static void A(Ankus.PgEventTriggerContext context) { }")]
    public void PlannerSupportCoversFunctionFamilies(string consumer)
    {
        Compilation compilation = GenerateSqlControl($$"""
            public static class Functions
            {
                [Ankus.PgSupportFunction(typeof(Functions), nameof(ZSupport))]
                {{consumer}}
                [Ankus.PgFunction]
                public static Ankus.PgInternal? ZSupport(Ankus.PgInternal request) => null;
            }
            """);
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        Assert.HasCount(2, graph.Items);
        Assert.Contains("SUPPORT \"z_support\"", graph.Items[1].Sql);
        Assert.AreSequenceEqual<string>([graph.Items[0].Id], graph.Items[1].Dependencies);
    }

    /// <summary>
    /// Exact managed overload selection permits injected contexts without counting them as SQL arguments.
    /// </summary>
    [TestMethod]
    public void PlannerSupportResolvesInheritedOverloadsAndInjectedContext()
    {
        Compilation compilation = GenerateSqlControl("""
            public static class Functions
            {
                [Ankus.PgFunction]
                [Ankus.PgSupportFunction(typeof(Derived), nameof(Derived.Support),
                    ParameterTypes = new[] { typeof(Ankus.PgFunctionContext), typeof(Ankus.PgInternal) })]
                public static int A() => 1;
            }
            public class Base
            {
                [Ankus.PgFunction(Name = "selected_support")]
                public static Ankus.PgInternal? Support(Ankus.PgFunctionContext context, Ankus.PgInternal request) => null;
                [Ankus.PgFunction(Name = "unrelated")]
                public static int Support(int value) => value;
            }
            public class Derived : Base;
            """);
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        ExtensionSchemaItem consumer = Assert.ContainsSingle(graph.Items.Where(static item => item.Kind == "function" && item.Names.Contains("a")));
        ExtensionSchemaItem support = Assert.ContainsSingle(graph.Items.Where(static item => item.Kind == "function" && item.Names.Contains("selected_support")));
        Assert.Contains("CREATE FUNCTION \"a\"", consumer.Sql);
        Assert.Contains("CREATE FUNCTION \"selected_support\"", support.Sql);
        Assert.Contains("SUPPORT \"selected_support\"", consumer.Sql);
        Assert.AreSequenceEqual<string>([support.Id], consumer.Dependencies);
    }

    /// <summary>
    /// A valid ordinary SQL function with an incompatible planner signature cannot become a support routine.
    /// </summary>
    /// <param name="support">The otherwise valid exported method.</param>
    [TestMethod]
    [DataRow("public static int Support(int value) => value;")]
    [DataRow("public static int Support(Ankus.PgInternal request) => 1;")]
    [DataRow("public static Ankus.PgInternal Support(Ankus.PgInternal request, int extra) => request;")]
    [DataRow("public static System.Collections.Generic.IEnumerable<Ankus.PgInternal> Support(Ankus.PgInternal request) => [request];")]
    public void PlannerSupportRejectsWrongSqlSignatures(string support)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            public static class Functions
            {
                [Ankus.PgFunction, Ankus.PgSupportFunction(typeof(Functions), nameof(Support))]
                public static int A() => 1;
                [Ankus.PgFunction] {{support}}
            }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS484", diagnostic.Id);
        Assert.Contains("one nonvariadic SQL internal argument", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual("nameof(Support)", DiagnosticText(diagnostic));
        Assert.IsFalse(compilation.Assembly.GetAttributes().Any(static item => item.ConstructorArguments.Length == 2 &&
            item.ConstructorArguments[0].Value is "Ankus.Sql"));
    }

    /// <summary>
    /// Invalid references and conflicting selectors report the authored value to correct without emitting partial SQL.
    /// </summary>
    /// <param name="reference">The invalid support reference.</param>
    /// <param name="options">Additional ordinary function options.</param>
    /// <param name="reason">The required diagnostic detail.</param>
    /// <param name="expected">The fixed diagnostic ID.</param>
    /// <param name="anchor">The documentation section for the failed contract.</param>
    /// <param name="span">The exact authored text to correct.</param>
    [TestMethod]
    [DataRow("typeof(Functions), null!", "", "non-null planner-support method name", "ANKUS470", "#planner-support-functions", "null!")]
    [DataRow("null!, \"Support\"", "", "non-null closed", "ANKUS471", "#reference-managed-declarations", "null!")]
    [DataRow("typeof(Functions), \"Missing\"", "", "'Functions.Missing' was not found on the referenced type or its base types", "ANKUS475",
        "#reference-managed-declarations", "\"Missing\"")]
    [DataRow("typeof(Functions), nameof(Ordinary)", "", "'Functions.Ordinary(Ankus.PgInternal)' does not declare a generated SQL object", "ANKUS480",
        "#reference-managed-declarations", "nameof(Ordinary)")]
    [DataRow("typeof(Functions), nameof(Support)", "", "has several overloads; set ParameterTypes", "ANKUS476",
        "#reference-managed-declarations", "nameof(Support)")]
    [DataRow("typeof(Functions), nameof(Support), ParameterTypes = new System.Type[] { }", "", "No overload of 'Functions.Support' has exactly",
        "ANKUS486", "#reference-managed-declarations", "new System.Type[] { }")]
    [DataRow("typeof(Functions), nameof(Support), ParameterTypes = new[] { typeof(Ankus.PgInternal) }", "SupportFunction = \"pg_catalog.textlike_support\"",
        "Choose either", "ANKUS483", "#planner-support-functions",
        "Ankus.PgSupportFunction(typeof(Functions), nameof(Support), ParameterTypes = new[] { typeof(Ankus.PgInternal) })")]
    public void PlannerSupportRejectsInvalidReferences(string reference, string options, string reason, string expected, string anchor, string span)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            public static class Functions
            {
                [Ankus.PgFunction({{options}}), Ankus.PgSupportFunction({{reference}})]
                public static int A() => 1;
                [Ankus.PgFunction] public static Ankus.PgInternal? Support(Ankus.PgInternal request) => null;
                [Ankus.PgFunction] public static int Support(int value) => value;
                public static Ankus.PgInternal? Ordinary(Ankus.PgInternal request) => null;
            }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual(expected, diagnostic.Id);
        Assert.Contains(reason, diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.EndsWith(anchor, diagnostic.Descriptor.HelpLinkUri);
        Assert.AreEqual(span, DiagnosticText(diagnostic));
        Assert.IsFalse(compilation.Assembly.GetAttributes().Any(static item => item.ConstructorArguments.Length == 2 &&
            item.ConstructorArguments[0].Value is "Ankus.Sql"));
    }

    /// <summary>
    /// Planner support cannot attach to an ordinary managed method that emits no PostgreSQL function, even with a valid target.
    /// </summary>
    /// <param name="reference">A valid target or an independently missing target.</param>
    [TestMethod]
    [DataRow("nameof(Support)")]
    [DataRow("\"Missing\"")]
    public void PlannerSupportRequiresGeneratedSourceFunction(string reference)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            public static class Functions
            {
                [Ankus.PgSupportFunction(typeof(Functions), {{reference}})]
                public static int Ordinary(int value) => value;
                [Ankus.PgFunction]
                public static Ankus.PgInternal? Support(Ankus.PgInternal request) => null;
            }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS482", diagnostic.Id);
        Assert.Contains("generated PostgreSQL function", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual("Ankus.PgSupportFunction(typeof(Functions), " + reference + ")", DiagnosticText(diagnostic));
        Assert.IsFalse(compilation.Assembly.GetAttributes().Any(static item => item.ConstructorArguments.Length == 2 &&
            item.ConstructorArguments[0].Value is "Ankus.Sql"));
    }

    /// <summary>
    /// Overloads that differ only by parameter modifiers cannot be distinguished by managed parameter types.
    /// </summary>
    [TestMethod]
    public void PlannerSupportRejectsIndistinguishableOverloads()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            public static class Functions
            {
                [Ankus.PgFunction, Ankus.PgSupportFunction(typeof(Functions), nameof(Support), ParameterTypes = new[] { typeof(Ankus.PgInternal) })]
                public static int A() => 1;
                [Ankus.PgFunction] public static Ankus.PgInternal? Support(Ankus.PgInternal request) => null;
                public static Ankus.PgInternal? Support(ref Ankus.PgInternal request) => null;
            }
            """);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS487", diagnostic.Id);
        Assert.Contains("Several overloads of 'Functions.Support' have exactly the selected ParameterTypes",
            diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual("new[] { typeof(Ankus.PgInternal) }", DiagnosticText(diagnostic));
        Assert.IsFalse(compilation.Assembly.GetAttributes().Any(static item => item.ConstructorArguments.Length == 2 &&
            item.ConstructorArguments[0].Value is "Ankus.Sql"));
    }

    /// <summary>
    /// An operator's generated backing function receives the support clause without a separate PgFunction attribute.
    /// </summary>
    [TestMethod]
    public void PlannerSupportConfiguresOperatorBackingFunctions()
    {
        Compilation compilation = GenerateSqlControl("""
            public static class Functions
            {
                [Ankus.PgOperator("@+"), Ankus.PgSupportFunction(typeof(Functions), nameof(Z))]
                public static int Add(int left, int right) => left + right;
                [Ankus.PgFunction] public static Ankus.PgInternal? Z(Ankus.PgInternal request) => null;
            }
            """);
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        ExtensionSchemaItem support = Assert.ContainsSingle(graph.Items.Where(static item => item.Kind == "function" && item.Names.Contains("z")));
        ExtensionSchemaItem backing = Assert.ContainsSingle(graph.Items.Where(static item => item.Kind == "function" && item.Names.Contains("add")));
        Assert.Contains("SUPPORT \"z\"", backing.Sql);
        Assert.Contains(support.Id, backing.Dependencies);
        Assert.ContainsSingle(graph.Items.Where(static item => item.Kind == "operator"));
    }

    /// <summary>
    /// Planner support participates in the existing cycle checks rather than recursive declaration reconstruction.
    /// </summary>
    /// <param name="self">Whether the dependency points directly to the same function.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void PlannerSupportRejectsCycles(bool self)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            public static class Functions
            {
                [Ankus.PgFunction, Ankus.PgSupportFunction(typeof(Functions), nameof({{(self ? "A" : "Z")}}))]
                public static Ankus.PgInternal A(Ankus.PgInternal request) => request;
                [Ankus.PgFunction, Ankus.PgSupportFunction(typeof(Functions), nameof(A))]
                public static Ankus.PgInternal Z(Ankus.PgInternal request) => request;
            }
            """);
        AssertSqlControlGraphError(compilation, diagnostics, "ANKUS499", "Ankus.PgSupportFunction(typeof(Functions), nameof(" + (self ? "A" : "Z") + "))",
            self ? "Functions.A(Ankus.PgInternal) -> Functions.A(Ankus.PgInternal)"
                : "Functions.A(Ankus.PgInternal) -> Functions.Z(Ankus.PgInternal) -> Functions.A(Ankus.PgInternal)");
    }

    /// <summary>
    /// Supplying custom SQL preserves support prerequisites and does not rewrite the authored fragment.
    /// </summary>
    /// <param name="policy">The disabled or replaced SQL policy.</param>
    /// <param name="expectedSql">The retained source SQL fragment.</param>
    [TestMethod]
    [DataRow("GenerateSql = false", "")]
    [DataRow("Sql = \"SELECT 'authored';\"", "SELECT 'authored';")]
    public void PlannerSupportRetainsReplacedAndDisabledContracts(string policy, string expectedSql)
    {
        Compilation compilation = GenerateSqlControl($$"""
            public static class Functions
            {
                [Ankus.PgFunction({{policy}}), Ankus.PgSupportFunction(typeof(Functions), nameof(Z))]
                public static int A() => 1;
                [Ankus.PgFunction] public static Ankus.PgInternal Z(Ankus.PgInternal request) => request;
            }
            """);
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        Assert.HasCount(2, graph.Items);
        if (expectedSql.Length == 0)
        {
            Assert.IsEmpty(graph.Items[1].Sql);
        }
        else
        {
            Assert.AreEqual(expectedSql + "\n", DeclarationBodies(compilation).Last());
        }

        Assert.AreSequenceEqual<string>([graph.Items[0].Id], graph.Items[1].Dependencies);
    }

    /// <summary>
    /// Aggregate transition helpers receive planner options without losing aggregate-specific parameter handling.
    /// </summary>
    [TestMethod]
    public void PlannerSupportConfiguresAggregateHelpers()
    {
        Compilation compilation = GenerateSqlControl("""
            [Ankus.PgAggregate(InitialCondition = "0")]
            public sealed class Total : Ankus.IPgAggregate<int,int>
            {
                [Ankus.PgSupportFunction(typeof(SupportFunctions), nameof(SupportFunctions.Z))]
                public static int Transition(Ankus.PgAggregateContext context,int state, int value) => state + value;
            }
            public static class SupportFunctions
            {
                [Ankus.PgFunction] public static Ankus.PgInternal? Z(Ankus.PgInternal request) => null;
            }
            """);
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        ExtensionSchemaItem support = Assert.ContainsSingle(graph.Items.Where(static item => item.Kind == "function" && item.Names.Contains("z")));
        ExtensionSchemaItem helper = Assert.ContainsSingle(graph.Items.Where(static item => item.Sql.Contains("SUPPORT \"z\"", StringComparison.Ordinal)));
        Assert.AreEqual("function", helper.Kind);
        Assert.AreSequenceEqual<string>([support.Id], helper.Dependencies);
    }

    /// <summary>
    /// One inherited helper generates a function for each aggregate; every function calls the method and receives its support routine.
    /// </summary>
    [TestMethod]
    public void PlannerSupportConfiguresEveryFunctionOfSharedHelpers()
    {
        Compilation compilation = GenerateSqlControl("""
            public abstract class Summing
            {
                [Ankus.PgSupportFunction(typeof(SupportFunctions), nameof(SupportFunctions.Z))]
                public static int Transition(Ankus.PgAggregateContext context, int state, int value) => state + value;
            }
            [Ankus.PgAggregate(InitialCondition = "0")]
            public sealed class First : Summing, Ankus.IPgAggregate<int,int>;
            [Ankus.PgAggregate(InitialCondition = "0")]
            public sealed class Second : Summing, Ankus.IPgAggregate<int,int>;
            public static class SupportFunctions
            {
                [Ankus.PgFunction] public static Ankus.PgInternal? Z(Ankus.PgInternal request) => null;
            }
            """);
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        ExtensionSchemaItem support = Assert.ContainsSingle(graph.Items.Where(static item => item.Kind == "function" && item.Names.Contains("z")));
        ExtensionSchemaItem[] helpers = [.. graph.Items.Where(item => item.Kind == "function" && item.Id != support.Id)];
        Assert.HasCount(2, helpers);
        foreach (ExtensionSchemaItem helper in helpers)
        {
            Assert.Contains("SUPPORT \"z\"", helper.Sql);
            Assert.AreSequenceEqual<string>([support.Id], helper.Dependencies);
        }

        Assert.HasCount(2, graph.Items.Where(static item => item.Kind == "aggregate"));
    }

    /// <summary>
    /// Aggregate helpers with compatible SQL signatures still require an aggregate invocation and cannot service planning requests.
    /// </summary>
    /// <param name="state">The aggregate's internal state representation.</param>
    [TestMethod]
    [DataRow("Ankus.PgInternal")]
    [DataRow("Ankus.PgAggregateState<int>")]
    public void PlannerSupportRejectsAggregateInvocationRequirements(string state)
    {
        const string reference = "[Ankus.PgSupportFunction(typeof(Counter), nameof(Counter.Transition))]";
        string source = $$"""
            [Ankus.PgAggregate]
            public sealed class Counter : Ankus.IPgAggregate<{{state}}?,System.ValueTuple>,
                Ankus.IPgFinalizingAggregate<{{state}}?,System.ValueTuple,int>
            {
                public static {{state}}? Transition(Ankus.PgAggregateContext context,{{state}}? state,System.ValueTuple arguments) => state;
                public static int Final(Ankus.PgAggregateContext context,{{state}}? state,System.ValueTuple direct) => 0;
            }
            public static class Functions
            {
                [Ankus.PgFunction]
                {{reference}}
                public static int A() => 1;
            }
            """;
        Compilation valid = GenerateSqlControl(source.Replace(reference, string.Empty, StringComparison.Ordinal));
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(valid, "Ankus.SqlGraph"));
        ExtensionSchemaItem transition = Assert.ContainsSingle(graph.Items.Where(static item =>
            item.Kind == "function" && item.Names.Contains("counter_transition")));
        Assert.Contains("(\"state\" internal)\nRETURNS internal", transition.Sql);

        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Diagnostic diagnostic = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS485", diagnostic.Id);
        Assert.Contains("aggregate invocation", diagnostic.GetMessage(System.Globalization.CultureInfo.InvariantCulture));
        Assert.AreEqual("nameof(Counter.Transition)", DiagnosticText(diagnostic));
        Assert.IsFalse(compilation.Assembly.GetAttributes().Any(static item => item.ConstructorArguments.Length == 2 &&
            item.ConstructorArguments[0].Value is "Ankus.Sql"));
    }

    /// <summary>
    /// Explicit raw internal mappings participate in semantic support validation without requiring one CLR wrapper type.
    /// </summary>
    [TestMethod]
    public void PlannerSupportAcceptsRawInternalContracts()
    {
        Compilation compilation = GenerateSqlControl("""
            public static class Functions
            {
                [Ankus.PgFunction, Ankus.PgSupportFunction(typeof(Functions), nameof(Z))]
                public static int A() => 1;
                [Ankus.PgFunction]
                [return: Ankus.PgSqlType("internal")]
                public static Ankus.PgDatum Z([Ankus.PgSqlType("internal")] Ankus.PgDatum request) => request;
            }
            """);
        ExtensionSchemaGraph graph = ExtensionSchemaGraph.Parse(ManifestValue(compilation, "Ankus.SqlGraph"));
        Assert.Contains("RETURNS \"internal\"", graph.Items[0].Sql);
        Assert.Contains("SUPPORT \"z\"", graph.Items[1].Sql);
        Assert.AreSequenceEqual<string>([graph.Items[0].Id], graph.Items[1].Dependencies);
    }

    /// <summary>
    /// Reads the exact current source text selected by a diagnostic.
    /// </summary>
    /// <param name="diagnostic">The reported diagnostic.</param>
    /// <returns>The authored text at the diagnostic span.</returns>
    private string DiagnosticText(Diagnostic diagnostic)
    {
        Assert.IsNotNull(diagnostic.Location.SourceTree);
        return diagnostic.Location.SourceTree.GetText(context.CancellationToken).ToString(diagnostic.Location.SourceSpan);
    }
}
