using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Callback selection is expressed through interfaces rather than string attribute properties.
    /// </summary>
    /// <param name="role">The retired callback-selection property.</param>
    [TestMethod]
    [DataRow("Transition")]
    [DataRow("Final")]
    [DataRow("Combine")]
    [DataRow("Serialize")]
    [DataRow("Deserialize")]
    [DataRow("MovingTransition")]
    [DataRow("MovingInverse")]
    [DataRow("MovingFinal")]
    public void AggregateCallbackNamePropertiesAreRejectedByCompiler(string role)
    {
        (Compilation compilation, _) = Generate($$"""
            [Ankus.PgAggregate({{role}} = "Missing")]
            public sealed class Typed : Ankus.IPgAggregate<int, int>
            {
                public static int Transition(Ankus.PgAggregateContext context, int state, int input) => state + input;
            }
            """);

        Diagnostic error = Assert.ContainsSingle(compilation.GetDiagnostics(context.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.AreEqual("CS0246", error.Id);
        Assert.AreEqual(role, error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
    }

    /// <summary>
    /// An attributed container cannot silently omit an intended callback without declaring its contract.
    /// </summary>
    /// <param name="helper">An otherwise valid helper that resembles an optional role.</param>
    [TestMethod]
    [DataRow("Finalise")]
    [DataRow("Combnie")]
    [DataRow("MovingTransitoin")]
    public void AggregateRequiresCompilerCheckedTransitionContract(string helper)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [Ankus.PgAggregate(InitialCondition = "0")]
            public sealed class Untyped
            {
                public static int Transition(int state, int input) => state + input;
            """ + "public static int " + helper + "(int state, int input) => state + input; }");

        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS029", error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains("IPgAggregate<TState, TArgs>", error.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual("https://ankus.net/aggregates/#compiler-checked-aggregate-contracts", error.Descriptor.HelpLinkUri);
        Assert.AreEqual("Untyped", error.Location.SourceTree!.GetText(context.CancellationToken).ToString(error.Location.SourceSpan));
        Assert.DoesNotContain("CREATE AGGREGATE", InstallationBody(compilation));
        Assert.IsEmpty(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!
            .GetMembers().OfType<IMethodSymbol>().Where(static method => method.Name.Contains("_aggregate_", StringComparison.Ordinal)));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Legitimate helper names remain legal when the aggregate declares its transition capability explicitly.
    /// </summary>
    [TestMethod]
    public void TypedAggregateDoesNotGuessRolesFromHelperNames()
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [Ankus.PgAggregate(InitialCondition = "0")]
            public sealed class Typed : Ankus.IPgAggregate<int, int>
            {
                public static int Transition(Ankus.PgAggregateContext context, int state, int input)
                    => Finalise(state, input);

                public static int Finalise(int state, int input) => state + input;
            }
            """);

        AssertAggregateCompilation(compilation, diagnostics);
        Assert.Contains("CREATE AGGREGATE \"typed\"(\"input\" integer)", InstallationBody(compilation));
        Assert.DoesNotContain("FINALFUNC =", InstallationBody(compilation));
        Assert.DoesNotContain("typed_finalise", InstallationBody(compilation));
    }

    /// <summary>
    /// An optional capability cannot replace the required transition contract.
    /// </summary>
    [TestMethod]
    public void AggregateRequiresTransitionWithOnlyFinalCapability()
        => AssertInvalidAggregate("""
            [Ankus.PgAggregate]
            public sealed class OnlyFinal : Ankus.IPgFinalizingAggregate<int, System.ValueTuple, int>
            {
                public static int Final(Ankus.PgAggregateContext context, int state, System.ValueTuple arguments) => state;
            }
            """, "ANKUS029");

    /// <summary>
    /// A misspelled member of a declared optional capability is a compiler error and cannot silently remove its SQL behavior.
    /// </summary>
    /// <param name="capability">The explicitly requested optional interface.</param>
    /// <param name="implementation">An explicit implementation with a misspelled member name.</param>
    [TestMethod]
    [DataRow("Ankus.IPgFinalizingAggregate<int, System.ValueTuple, int>", """
        static int Ankus.IPgFinalizingAggregate<int, System.ValueTuple, int>.Finalise(
            Ankus.PgAggregateContext context, int state, System.ValueTuple arguments) => state;
        """)]
    [DataRow("Ankus.IPgCombinableAggregate<int>", """
        static int Ankus.IPgCombinableAggregate<int>.Combnie(
            Ankus.PgAggregateContext context, int state, int other) => state + other;
        """)]
    [DataRow("Ankus.IPgMovingAggregate<int, int>", """
        static int Ankus.IPgMovingAggregate<int, int>.MovingTransitoin(
            Ankus.PgAggregateContext context, int state, int arguments) => state + arguments;
        public static int MovingInverse(Ankus.PgAggregateContext context, int state, int arguments) => state - arguments;
        """)]
    [DataRow("Ankus.IPgMovingFinalizingAggregate<int, System.ValueTuple, int>", """
        static int Ankus.IPgMovingFinalizingAggregate<int, System.ValueTuple, int>.MovingFianl(
            Ankus.PgAggregateContext context, int state, System.ValueTuple arguments) => state;
        """)]
    public void DeclaredAggregateCapabilityRejectsMisspelledImplementation(string capability, string implementation)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate("""
            [Ankus.PgAggregate(InitialCondition = "0")]
            public sealed class Misspelled : Ankus.IPgAggregate<int, int>,
            """ + capability + """
            {
                public static int Transition(Ankus.PgAggregateContext context, int state, int input) => state + input;
            """ + implementation + "}");

        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS112", error.Id);
        string[] compilerErrors = [.. compilation.GetDiagnostics(context.CancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error).Select(static diagnostic => diagnostic.Id)];
        Assert.Contains("CS0539", compilerErrors);
        Assert.Contains("CS0535", compilerErrors);
        Assert.DoesNotContain("CREATE AGGREGATE", InstallationBody(compilation));
        Assert.IsEmpty(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!
            .GetMembers().OfType<IMethodSymbol>().Where(static method => method.Name.Contains("_aggregate_", StringComparison.Ordinal)));
    }
}
