using System.Collections.Immutable;
using System.Globalization;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators.Tests;

public sealed partial class PgFunctionGeneratorTests
{
    /// <summary>
    /// Unsafe reference contracts cannot publish callbacks through advisory C# interface warnings.
    /// </summary>
    /// <param name="boundary">The state, input or final result with an incompatible implementation.</param>
    [TestMethod]
    [DataRow("state")]
    [DataRow("input")]
    [DataRow("result")]
    public void TypedAggregateRejectsUnsafeImplementationNullability(string boundary)
    {
        string source = boundary == "result" ? """
            [Ankus.PgAggregate(InitialCondition = "0")]
            public sealed class Invalid : Ankus.IPgAggregate<long, int>,
                Ankus.IPgFinalizingAggregate<long, System.ValueTuple, string>
            {
                public static long Transition(Ankus.PgAggregateContext context, long state, int input) => state + input;
                public static string? Final(Ankus.PgAggregateContext context, long state, System.ValueTuple arguments) => null;
            }
            """ : """
            [Ankus.PgAggregate]
            public sealed class Invalid : Ankus.IPgAggregate<string?, string?>
            {
                public static string Transition(Ankus.PgAggregateContext context,
            """ + (boundary == "state" ? "string state, string? input" : "string? state, string input") + """
                ) => (state ?? "") + (input ?? "");
            }
            """;
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source, path: "Aggregate.cs");
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        Assert.IsNotEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Id is "CS8766" or "CS8767"));
        using var assembly = new MemoryStream();
        Assert.IsTrue(compilation.Emit(assembly, cancellationToken: context.CancellationToken).Success);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS028", error.Id);
        Assert.AreEqual(DiagnosticSeverity.Error, error.Severity);
        Assert.Contains(boundary, error.GetMessage(CultureInfo.InvariantCulture));
        Assert.AreEqual("Aggregate.cs", error.Location.GetLineSpan().Path);
        Assert.AreEqual(boundary == "result" ? "string?" : "string",
            source.Substring(error.Location.SourceSpan.Start, error.Location.SourceSpan.Length));
        Assert.AreEqual("https://ankus.net/aggregates/#compiler-checked-aggregate-contracts", error.Descriptor.HelpLinkUri);
        Assert.DoesNotContain("CREATE AGGREGATE \"invalid\"", InstallationBody(compilation));
        Assert.IsEmpty(compilation.GetTypeByMetadataName("Ankus.Generated.ExtensionDispatchers")!
            .GetMembers().OfType<IMethodSymbol>().Where(static method => method.Name.Contains("_aggregate_", StringComparison.Ordinal)));
    }

    /// <summary>
    /// SQL inputs retain directional nullability for scalar values, arrays and tuple groups.
    /// </summary>
    /// <param name="contract">The interface's SQL argument type.</param>
    /// <param name="implementation">The implementation's argument type.</param>
    /// <param name="valid">Whether the implementation accepts every interface value.</param>
    [TestMethod]
    [DataRow("string?", "string", false)]
    [DataRow("string", "string?", true)]
    [DataRow("string?[]", "string[]", false)]
    [DataRow("string[]", "string?[]", true)]
    [DataRow("string[]?", "string[]", false)]
    [DataRow("(string? Text, int Weight)", "(string Text, int Weight)", false)]
    [DataRow("(string Text, int Weight)", "(string? Text, int Weight)", true)]
    public void TypedAggregateInputShapesPreserveNullableContracts(string contract, string implementation, bool valid)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            [Ankus.PgAggregate(InitialCondition = "0")]
            public sealed class InputTotal : Ankus.IPgAggregate<long, {{contract}}>
            {
                public static long Transition(Ankus.PgAggregateContext context, long state, {{implementation}} arguments) => state;
            }
            """);
        if (valid)
        {
            AssertAggregateCompilation(compilation, diagnostics);
            Assert.Contains("CREATE AGGREGATE \"input_total\"", InstallationBody(compilation));
            bool nullable = contract == "string?" || contract == "string[]?" || contract.StartsWith("(string?", StringComparison.Ordinal);
            Assert.Contains(nullable ? "CALLED ON NULL INPUT" : "STRICT", InstallationBody(compilation));
        }
        else
        {
            Assert.AreEqual("ANKUS028", Assert.ContainsSingle(diagnostics).Id);
            Assert.DoesNotContain("CREATE AGGREGATE \"input_total\"", InstallationBody(compilation));
            Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }
    }

    /// <summary>
    /// Standard nullable preconditions and return promises participate in interface compatibility.
    /// </summary>
    /// <param name="inputType">The implementation's input declaration.</param>
    /// <param name="inputAttribute">Its optional nullable precondition.</param>
    /// <param name="returnType">The implementation's result declaration.</param>
    /// <param name="returnAttribute">Its optional nullable postcondition.</param>
    /// <param name="valid">Whether all interface values remain safe.</param>
    [TestMethod]
    [DataRow("string", "AllowNull", "string", "", true)]
    [DataRow("string?", "DisallowNull", "string", "", false)]
    [DataRow("string?", "NotNull", "string", "", true)]
    [DataRow("string?", "", "string?", "NotNull", true)]
    [DataRow("string?", "", "string", "MaybeNull", false)]
    [DataRow("string?", "", "string?", "NotNullIfNotNull(\"context\")", true)]
    [DataRow("string?", "", "string?", "NotNullIfNotNull(\"arguments\")", true)]
    public void TypedAggregateFlowAnnotationsPreserveNullableContracts(string inputType, string inputAttribute,
        string returnType, string returnAttribute, bool valid)
    {
        string parameterMetadata = inputAttribute.Length == 0 ? "" : "[System.Diagnostics.CodeAnalysis." + inputAttribute + "]";
        string resultMetadata = returnAttribute.Length == 0 ? "" : "[return: System.Diagnostics.CodeAnalysis." + returnAttribute + "]";
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            [Ankus.PgAggregate(InitialCondition = "0")]
            public sealed class AnnotatedTotal : Ankus.IPgAggregate<long, string?>,
                Ankus.IPgFinalizingAggregate<long, System.ValueTuple, string>
            {
                public static long Transition(Ankus.PgAggregateContext context, long state, {{parameterMetadata}} {{inputType}} arguments)
                    => arguments is null ? throw new System.ArgumentNullException(nameof(arguments)) : state;
                {{resultMetadata}}
                public static {{returnType}} Final(Ankus.PgAggregateContext context, long state, System.ValueTuple arguments) => "present";
            }
            """);
        if (valid)
        {
            AssertAggregateCompilation(compilation, diagnostics);
            Assert.Contains("CREATE AGGREGATE \"annotated_total\"", InstallationBody(compilation));
            Assert.Contains("CALLED ON NULL INPUT", InstallationBody(compilation));
        }
        else
        {
            Assert.AreEqual("ANKUS028", Assert.ContainsSingle(diagnostics).Id);
            Assert.DoesNotContain("CREATE AGGREGATE \"annotated_total\"", InstallationBody(compilation));
            Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }
    }

    /// <summary>
    /// A conditional return promise only strengthens the result when its interface input is required.
    /// </summary>
    /// <param name="stateType">The interface and implementation state type.</param>
    /// <param name="valid">Whether the interface guarantees a present state.</param>
    [TestMethod]
    [DataRow("string", true)]
    [DataRow("string?", false)]
    public void TypedAggregateConditionalReturnRequiresPresentContractInput(string stateType, bool valid)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            [Ankus.PgAggregate]
            public sealed class ConditionalTotal : Ankus.IPgAggregate<{{stateType}}, string?>,
                Ankus.IPgFinalizingAggregate<{{stateType}}, System.ValueTuple, string>
            {
                public static {{stateType}} Transition(Ankus.PgAggregateContext context, {{stateType}} state, string? arguments) => state;
                [return: System.Diagnostics.CodeAnalysis.NotNullIfNotNull("state")]
                public static string? Final(Ankus.PgAggregateContext context, {{stateType}} state, System.ValueTuple arguments) => state;
            }
            """);
        if (valid)
        {
            AssertAggregateCompilation(compilation, diagnostics);
            Assert.Contains("CREATE AGGREGATE \"conditional_total\"", InstallationBody(compilation));
        }
        else
        {
            Assert.AreEqual("ANKUS028", Assert.ContainsSingle(diagnostics).Id);
            Assert.DoesNotContain("CREATE AGGREGATE \"conditional_total\"", InstallationBody(compilation));
        }
    }

    /// <summary>
    /// SQL result arrays preserve directional element and outer nullability promises.
    /// </summary>
    /// <param name="contract">The interface result type.</param>
    /// <param name="implementation">The implementation result type.</param>
    /// <param name="valid">Whether every implementation result satisfies the interface.</param>
    [TestMethod]
    [DataRow("string[]", "string?[]", false)]
    [DataRow("string?[]", "string[]", true)]
    [DataRow("string[]", "string[]?", false)]
    [DataRow("string[]?", "string[]", true)]
    public void TypedAggregateResultArraysPreserveNullableContracts(string contract, string implementation, bool valid)
    {
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            [Ankus.PgAggregate(InitialCondition = "0")]
            public sealed class ArrayTotal : Ankus.IPgAggregate<long, int>,
                Ankus.IPgFinalizingAggregate<long, System.ValueTuple, {{contract}}>
            {
                public static long Transition(Ankus.PgAggregateContext context, long state, int arguments) => state + arguments;
                public static {{implementation}} Final(Ankus.PgAggregateContext context, long state, System.ValueTuple arguments) => [];
            }
            """);
        if (valid)
        {
            AssertAggregateCompilation(compilation, diagnostics);
            Assert.Contains("CREATE AGGREGATE \"array_total\"", InstallationBody(compilation));
        }
        else
        {
            Assert.AreEqual("ANKUS028", Assert.ContainsSingle(diagnostics).Id);
            Assert.DoesNotContain("CREATE AGGREGATE \"array_total\"", InstallationBody(compilation));
            Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
        }
    }

    /// <summary>
    /// Invariant owned-state payloads cannot change nested reference annotations in either direction.
    /// </summary>
    /// <param name="container">The managed collection stored in an owned state.</param>
    /// <param name="contractElement">Its interface element type.</param>
    /// <param name="implementationElement">Its implementation element type.</param>
    [TestMethod]
    [DataRow("System.Collections.Generic.List", "string?", "string")]
    [DataRow("System.Collections.Generic.List", "string", "string?")]
    [DataRow("System.Collections.Generic.IReadOnlyList", "string?", "string")]
    [DataRow("System.Collections.Generic.IReadOnlyList", "string", "string?")]
    public void TypedAggregateOwnedPayloadNullabilityRemainsInvariant(string container, string contractElement, string implementationElement)
    {
        string state = "Ankus.PgAggregateState<" + container + "<" + contractElement + ">>?";
        string narrowed = "Ankus.PgAggregateState<" + container + "<" + implementationElement + ">>?";
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate($$"""
            [Ankus.PgAggregate]
            public sealed class OwnedTotal : Ankus.IPgAggregate<{{state}}, int>
            {
                public static {{state}} Transition(Ankus.PgAggregateContext context, {{narrowed}} state, int arguments) => null;
            }
            """);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS028", error.Id);
        Assert.Contains("state", error.GetMessage(CultureInfo.InvariantCulture));
        Assert.DoesNotContain("CREATE AGGREGATE \"owned_total\"", InstallationBody(compilation));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }

    /// <summary>
    /// Semantic implementation resolution cannot hide an unsafe contract behind another declaration form.
    /// </summary>
    /// <param name="kind">An explicit, inherited or default interface implementation.</param>
    [TestMethod]
    [DataRow("explicit")]
    [DataRow("inherited")]
    [DataRow("default")]
    public void TypedAggregateResolvedImplementationsPreserveNullableContracts(string kind)
    {
        string source = kind switch
        {
            "explicit" => """
                [Ankus.PgAggregate]
                public sealed class HiddenTotal : Ankus.IPgAggregate<string?, string?>
                {
                    static string Ankus.IPgAggregate<string?, string?>.Transition(Ankus.PgAggregateContext context, string state, string? arguments) => state;
                }
                """,
            "inherited" => """
                public class BaseTotal : Ankus.IPgAggregate<string?, string?>
                {
                    public static string Transition(Ankus.PgAggregateContext context, string state, string? arguments) => state;
                }
                [Ankus.PgAggregate]
                public sealed class HiddenTotal : BaseTotal;
                """,
            _ => """
                public interface IDefaultTotal : Ankus.IPgAggregate<string?, string?>
                {
                    static string Ankus.IPgAggregate<string?, string?>.Transition(Ankus.PgAggregateContext context, string state, string? arguments) => state;
                }
                [Ankus.PgAggregate]
                public sealed class HiddenTotal : IDefaultTotal;
                """,
        };
        (Compilation compilation, ImmutableArray<Diagnostic> diagnostics) = Generate(source);
        Diagnostic error = Assert.ContainsSingle(diagnostics);
        Assert.AreEqual("ANKUS028", error.Id);
        Assert.Contains("state", error.GetMessage(CultureInfo.InvariantCulture));
        Assert.DoesNotContain("CREATE AGGREGATE \"hidden_total\"", InstallationBody(compilation));
        Assert.IsEmpty(compilation.GetDiagnostics(context.CancellationToken).Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error));
    }
}
