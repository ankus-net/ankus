using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Invalid aggregate options identify the authored value and preserve a compilable independent aggregate.
    /// </summary>
    /// <param name="option">The invalid attribute option.</param>
    /// <param name="id">The independently specified contract diagnostic.</param>
    /// <param name="span">The exact authored value requiring correction.</param>
    [TestMethod]
    [DataRow("Name = \"\"", "ANKUS081", "\"\"")]
    [DataRow("Schema = \"\"", "ANKUS050", "\"\"")]
    [DataRow("Kind = (Ankus.PgAggregateKind)3", "ANKUS082", "(Ankus.PgAggregateKind)3")]
    [DataRow("ParallelSafety = (Ankus.PgParallelSafety)3", "ANKUS082", "(Ankus.PgParallelSafety)3")]
    [DataRow("FinalModify = (Ankus.PgAggregateFinalModify)4", "ANKUS082", "(Ankus.PgAggregateFinalModify)4")]
    [DataRow("MovingFinalModify = (Ankus.PgAggregateFinalModify)4", "ANKUS082", "(Ankus.PgAggregateFinalModify)4")]
    [DataRow("InitialCondition = \"\\0\"", "ANKUS083", "\"\\0\"")]
    [DataRow("MovingInitialCondition = \"\\0\"", "ANKUS083", "\"\\0\"")]
    [DataRow("MovingStateSize = 1", "ANKUS094", "1")]
    [DataRow("SortOperator = \"--\"", "ANKUS105", "\"--\"")]
    public void AggregateOptionsIdentifyTheirAuthoredContract(string option, string id, string span)
    {
        string source = "[Ankus.PgAggregate(" + option + ")] public sealed class Bad : Ankus.IPgAggregate<int, int> { " +
            "public static int Transition(Ankus.PgAggregateContext context, int state, int value) => state + value; }" +
            OtherAggregateSource;
        CSharpCompilation input = ModuleCompilation(source);
        ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual(id, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(source.IndexOf(span, StringComparison.Ordinal), error.Location.SourceSpan.Start);
        Assert.AreEqual(span, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual("https://ankus.net/" +
            (id == "ANKUS050" ? "function-declarations" : "aggregates") + "/#declaration-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        string sql = InstallationBody(output);
        Assert.Contains("CREATE AGGREGATE \"other\"", sql);
        Assert.DoesNotContain("CREATE AGGREGATE \"bad\"", sql);
        Assert.DoesNotContain("CREATE FUNCTION \"bad_transition\"", sql);
    }

    /// <summary>
    /// Typed callback and metadata failures identify the exact source contract without emitting an invalid aggregate.
    /// </summary>
    /// <param name="state">The interface state type.</param>
    /// <param name="input">The interface argument group.</param>
    /// <param name="method">The authored transition declaration.</param>
    /// <param name="id">The expected specific contract diagnostic.</param>
    /// <param name="span">The exact syntax requiring correction.</param>
    [TestMethod]
    [DataRow("int", "int", "[Ankus.PgFunction(Name = \"\")] public static int Transition(Ankus.PgAggregateContext context, int state, int value) => state;", "ANKUS123", "\"\"")]
    [DataRow("int", "int", "public static int Transition([Ankus.PgParameter] Ankus.PgAggregateContext context, int state, int value) => state;", "ANKUS114", "Ankus.PgParameter")]
    [DataRow("int", "int", "public static int Transition(Ankus.PgAggregateContext context, int state, int value = 0) => state;", "ANKUS113", "int value = 0")]
    [DataRow("int", "int", "[Ankus.PgOperator(\"+\")] public static int Transition(Ankus.PgAggregateContext context, int state, int value) => state;", "ANKUS115", "Ankus.PgOperator(\"+\")")]
    [DataRow("object", "int", "public static object Transition(Ankus.PgAggregateContext context, object state, int value) => state;", "ANKUS117", "object")]
    [DataRow("System.Collections.Generic.IEnumerable<int>", "int", "public static System.Collections.Generic.IEnumerable<int> Transition(Ankus.PgAggregateContext context, System.Collections.Generic.IEnumerable<int> state, int value) => state;", "ANKUS116", "System.Collections.Generic.IEnumerable<int>")]
    [DataRow("Ankus.PgHeapTuple?", "int", "public static Ankus.PgHeapTuple? Transition(Ankus.PgAggregateContext context, Ankus.PgHeapTuple? state, int value) => state;", "ANKUS084", "Ankus.PgHeapTuple?")]
    [DataRow("int", "System.Uri", "public static int Transition(Ankus.PgAggregateContext context, int state, System.Uri value) => state;", "ANKUS124", "System.Uri")]
    [DataRow("int", "int", "public static int Transition(Ankus.PgAggregateContext context, int state, [Ankus.PgParameter(Name = \"\")] int value) => state;", "ANKUS125", "\"\"")]
    [DataRow("int", "int", "public static int Transition(Ankus.PgAggregateContext context, int state, [Ankus.PgParameter(Name = \"first\"), Ankus.PgParameter(Name = \"second\")] int value) => state;", "ANKUS126", "Ankus.PgParameter(Name = \"second\")")]
    [DataRow("int", "int", "public static int Transition(Ankus.PgAggregateContext context, int state, [Ankus.PgParameter(Default = \"42\")] int value) => state;", "ANKUS127", "\"42\"")]
    [DataRow("int", "int", "public static int Transition(Ankus.PgAggregateContext context, int state, [Ankus.PgParameter(Element = \"member\")] int value) => state;", "ANKUS128", "\"member\"")]
    [DataRow("int", "(int Left, int Right)", "public static int Transition(Ankus.PgAggregateContext context, int state, [Ankus.PgParameter(Element = \"missing\")] (int Left, int Right) arguments) => state;", "ANKUS118", "\"missing\"")]
    [DataRow("int", "System.ValueTuple", "public static int Transition(Ankus.PgAggregateContext context, int state, [Ankus.PgParameter(Name = \"empty\")] System.ValueTuple arguments) => state;", "ANKUS119", "Ankus.PgParameter(Name = \"empty\")")]
    [DataRow("int", "int", "public static int Transition(Ankus.PgAggregateContext context, int state, [Ankus.PgParameter(Name = \"state\")] int value) => state;", "ANKUS121", "\"state\"")]
    public void AggregateContractsIdentifyTheirAuthoredContract(string state, string input, string method, string id, string span)
    {
        string prefix = "[Ankus.PgAggregate(InitialCondition = \"0\")] public sealed class Bad : Ankus.IPgAggregate<" + state + ", " + input + "> { ";
        CSharpCompilation compilation = ModuleCompilation(prefix + method + " }" + OtherAggregateSource);
        ModuleDriver().RunGeneratorsAndUpdateCompilation(compilation, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual(id, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual(prefix.Length + method.IndexOf(span, StringComparison.Ordinal), error.Location.SourceSpan.Start);
        Assert.AreEqual(span, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual("https://ankus.net/aggregates/#declaration-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        string sql = InstallationBody(output);
        Assert.Contains("CREATE AGGREGATE \"other\"", sql);
        Assert.DoesNotContain("CREATE AGGREGATE \"bad\"", sql);
        Assert.DoesNotContain("CREATE FUNCTION \"bad_transition\"", sql);
    }

    /// <summary>
    /// Independently invalid relationships between valid typed callbacks report the PostgreSQL contract that needs correction.
    /// </summary>
    /// <param name="contracts">The implemented aggregate capabilities.</param>
    /// <param name="callbacks">The compiler-valid callback implementations.</param>
    /// <param name="id">The specific PostgreSQL catalog diagnostic.</param>
    [TestMethod]
    [DataRow("Ankus.IPgAggregate<int, Ankus.PgInternal?>", """
        public static int Transition(Ankus.PgAggregateContext context, int state, Ankus.PgInternal? value) => state;
        """, "ANKUS089")]
    [DataRow("Ankus.IPgAggregate<int, int>, Ankus.IPgMovingAggregate<Ankus.PgHeapTuple?, int>", """
        public static int Transition(Ankus.PgAggregateContext context, int state, int value) => state;
        public static Ankus.PgHeapTuple? MovingTransition(Ankus.PgAggregateContext context, Ankus.PgHeapTuple? state, int value) => state;
        public static Ankus.PgHeapTuple? MovingInverse(Ankus.PgAggregateContext context, Ankus.PgHeapTuple? state, int value) => state;
        """, "ANKUS095")]
    [DataRow("Ankus.IPgAggregate<int, int>, Ankus.IPgMovingAggregate<int, long>", """
        public static int Transition(Ankus.PgAggregateContext context, int state, int value) => state;
        public static int MovingTransition(Ankus.PgAggregateContext context, int state, long value) => state;
        public static int MovingInverse(Ankus.PgAggregateContext context, int state, long value) => state;
        """, "ANKUS096")]
    [DataRow("Ankus.IPgAggregate<int, int>, Ankus.IPgMovingAggregate<Ankus.PgHeapTuple?, int>", """
        public static int Transition(Ankus.PgAggregateContext context, int state, int value) => state;
        [return: Ankus.PgCompositeType("first")]
        public static Ankus.PgHeapTuple? MovingTransition(Ankus.PgAggregateContext context,
            [Ankus.PgCompositeType("first")] Ankus.PgHeapTuple? state, int value) => state;
        [return: Ankus.PgCompositeType("second")]
        public static Ankus.PgHeapTuple? MovingInverse(Ankus.PgAggregateContext context,
            [Ankus.PgCompositeType("first")] Ankus.PgHeapTuple? state, int value) => state;
        """, "ANKUS097")]
    [DataRow("Ankus.IPgAggregate<int?, int?>, Ankus.IPgMovingAggregate<int?, int?>", """
        public static int? Transition(Ankus.PgAggregateContext context, int? state, int? value) => state;
        [Ankus.PgFunction(NullInput = Ankus.PgNullInput.Strict)]
        public static int? MovingTransition(Ankus.PgAggregateContext context, int? state, int? value) => state;
        public static int? MovingInverse(Ankus.PgAggregateContext context, int? state, int? value) => state;
        """, "ANKUS098")]
    [DataRow("Ankus.IPgAggregate<Ankus.PgAggregateState<int>?, int>, Ankus.IPgFinalizingAggregate<Ankus.PgAggregateState<int>?, System.ValueTuple, int>", """
        public static Ankus.PgAggregateState<int>? Transition(Ankus.PgAggregateContext context, Ankus.PgAggregateState<int>? state, int value) => state;
        public static int Final(Ankus.PgAggregateContext context, Ankus.PgAggregateState<int>? state, System.ValueTuple arguments) => 0;
        """, "ANKUS106")]
    [DataRow("Ankus.IPgAggregate<int?, int?>, Ankus.IPgMovingAggregate<int?, int?>, Ankus.IPgMovingFinalizingAggregate<long?, System.ValueTuple, int?>", """
        public static int? Transition(Ankus.PgAggregateContext context, int? state, int? value) => state;
        public static int? MovingTransition(Ankus.PgAggregateContext context, int? state, int? value) => state;
        public static int? MovingInverse(Ankus.PgAggregateContext context, int? state, int? value) => state;
        public static int? MovingFinal(Ankus.PgAggregateContext context, long? state, System.ValueTuple arguments) => 0;
        """, "ANKUS108")]
    public void AggregateRelationshipsReportTheirOwnContract(string contracts, string callbacks, string id)
    {
        CSharpCompilation input = ModuleCompilation("[Ankus.PgAggregate(InitialCondition = \"0\")] public sealed class Bad : " +
            contracts + " { " + callbacks + " }" + OtherAggregateSource);
        ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual(id, error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.AreEqual("https://ankus.net/aggregates/#declaration-diagnostics", error.Descriptor.HelpLinkUri);
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        string sql = InstallationBody(output);
        Assert.Contains("CREATE AGGREGATE \"other\"", sql);
        Assert.DoesNotContain("CREATE AGGREGATE \"bad\"", sql);
        Assert.DoesNotContain("CREATE FUNCTION \"bad_", sql);
    }

    /// <summary>
    /// The combined direct and aggregated SQL input limit is checked independently of each valid support function's limit.
    /// </summary>
    /// <param name="directCount">The number of direct inputs added to sixty aggregated inputs.</param>
    [TestMethod]
    [DataRow(39)]
    [DataRow(40)]
    [DataRow(41)]
    public void AggregateCombinedArgumentLimitIncludesDirectInputs(int directCount)
    {
        string arguments = "(" + string.Join(", ", Enumerable.Range(0, 60).Select(static index => "int value" + index)) + ")";
        string direct = "(" + string.Join(", ", Enumerable.Range(0, directCount).Select(static index => "int direct" + index)) + ")";
        CSharpCompilation input = ModuleCompilation("[Ankus.PgAggregate(Kind = Ankus.PgAggregateKind.OrderedSet, InitialCondition = \"0\")] " +
            "public sealed class Many : Ankus.IPgAggregate<int, " + arguments + ">, Ankus.IPgFinalizingAggregate<int, " + direct + ", int> { " +
            "public static int Transition(Ankus.PgAggregateContext context, int state, " + arguments + " arguments) => state; " +
            "public static int Final(Ankus.PgAggregateContext context, int state, " + direct + " arguments) => state; }");
        ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);

        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        string sql = InstallationBody(output);
        if (directCount == 39)
        {
            Assert.IsEmpty(diagnostics);
            Assert.Contains("CREATE AGGREGATE \"many\"", sql);
            Assert.Contains("\"direct38\" integer ORDER BY \"value0\" integer", sql);
            Assert.Contains("\"value59\" integer)", sql);
        }
        else
        {
            Assert.AreEqual("ANKUS088", Assert.ContainsSingle(diagnostics).Id);
            Assert.DoesNotContain("CREATE AGGREGATE", sql);
            Assert.DoesNotContain("CREATE FUNCTION", sql);
        }
    }

    /// <summary>
    /// Fine option and callback diagnostics remain cached while their current locations follow earlier source edits.
    /// </summary>
    /// <param name="parameter">Whether to test callback metadata instead of an aggregate option.</param>
    [TestMethod]
    [DataRow(false)]
    [DataRow(true)]
    public void AggregateDiagnosticsFollowEarlierEdits(bool parameter)
    {
        string source = "public static class Functions { [Ankus.PgFunction] public static int First() => 41; } " +
            "[Ankus.PgAggregate(InitialCondition = \"0\"" + (parameter ? "" : ", ParallelSafety = (Ankus.PgParallelSafety)3") +
            ")] public sealed class Bad : Ankus.IPgAggregate<int, int> { " +
            "public static int Transition(Ankus.PgAggregateContext context, int state, " +
            (parameter ? "[Ankus.PgParameter(Default = \"42\")] " : "") + "int value) => state + value; }";
        CSharpCompilation initial = ModuleCompilation(source);
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(initial, out Compilation first,
            out ImmutableArray<Diagnostic> original, context.CancellationToken);
        Diagnostic previous = Assert.ContainsSingle(original);
        string edited = source.Replace("=> 41;", "=> 4000 + 2;", StringComparison.Ordinal);
        SyntaxTree current = CSharpSyntaxTree.ParseText(edited, path: "Module.cs", cancellationToken: context.CancellationToken);
        SyntaxTree earlier = CSharpSyntaxTree.ParseText("internal static class Earlier;", path: "Earlier.cs",
            cancellationToken: context.CancellationToken);
        CSharpCompilation input = initial.RemoveAllSyntaxTrees().AddSyntaxTrees(earlier, current);
        driver = driver.RunGeneratorsAndUpdateCompilation(input, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        string span = parameter ? "\"42\"" : "(Ankus.PgParallelSafety)3";

        Assert.AreEqual(parameter ? "ANKUS127" : "ANKUS082", error.Id);
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "ExtensionComposition"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "ExtensionProblems"));
        Assert.AreEqual(IncrementalStepRunReason.Cached, ModuleStep(driver, "AggregateSqlEmission"));
        Assert.AreSame(current, error.Location.SourceTree);
        Assert.AreNotEqual(previous.Location.SourceSpan.Start, error.Location.SourceSpan.Start);
        Assert.AreEqual(edited.IndexOf(span, StringComparison.Ordinal), error.Location.SourceSpan.Start);
        Assert.AreEqual(span, current.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.AreEqual(InstallationBody(first), InstallationBody(output));
        string repaired = parameter ? edited.Replace("[Ankus.PgParameter(Default = \"42\")] ", "", StringComparison.Ordinal) :
            edited.Replace("(Ankus.PgParallelSafety)3", "Ankus.PgParallelSafety.Safe", StringComparison.Ordinal);
        RunModule(driver, input.ReplaceSyntaxTree(current, CSharpSyntaxTree.ParseText(repaired, path: "Module.cs",
            cancellationToken: context.CancellationToken)), out Compilation corrected);
        Assert.Contains("CREATE AGGREGATE \"bad\"", InstallationBody(corrected));
    }

    /// <summary>
    /// An inherited role error points into the declaring file and adding its typed capability repairs the generated aggregate.
    /// </summary>
    [TestMethod]
    public void InheritedAggregateCapabilityErrorsLocateTheDeclaringFile()
    {
        const string Source = """
            [Ankus.PgAggregate(InitialCondition = "0")]
            public sealed class Bad : Parent, Ankus.IPgAggregate<int, int>
            {
                public static int Transition(Ankus.PgAggregateContext context, int state, int value) => state + value;
            }
            """;
        SyntaxTree parent = CSharpSyntaxTree.ParseText("""
            public abstract class Parent
            {
                public static int Combine(Ankus.PgAggregateContext context, int state, int other) => state + other;
            }
            """, path: "Parent.cs", cancellationToken: context.CancellationToken);
        CSharpCompilation input = ModuleCompilation(Source).AddSyntaxTrees(parent);
        SyntaxTree declaration = input.SyntaxTrees.First();
        GeneratorDriver driver = ModuleDriver().RunGeneratorsAndUpdateCompilation(input, out Compilation output,
            out ImmutableArray<Diagnostic> diagnostics, context.CancellationToken);
        Diagnostic error = Assert.ContainsSingle(diagnostics);

        Assert.AreEqual("ANKUS111", error.Id);
        Assert.AreSame(parent, error.Location.SourceTree);
        Assert.AreEqual("Combine", parent.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.IsEmpty(output.GetDiagnostics(context.CancellationToken).Where(static value => value.Severity == DiagnosticSeverity.Error));
        Assert.DoesNotContain("CREATE AGGREGATE", InstallationBody(output));
        string repaired = Source.Replace("Ankus.IPgAggregate<int, int>",
            "Ankus.IPgAggregate<int, int>, Ankus.IPgCombinableAggregate<int>", StringComparison.Ordinal);
        RunModule(driver, input.ReplaceSyntaxTree(declaration, CSharpSyntaxTree.ParseText(repaired, path: "Module.cs",
            cancellationToken: context.CancellationToken)), out Compilation corrected);
        Assert.Contains("COMBINEFUNC = \"bad_combine\"", InstallationBody(corrected));
    }
}
