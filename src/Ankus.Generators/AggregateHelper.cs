using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Represents one aggregate support method and its context-free SQL signature.
/// </summary>
internal sealed class AggregateHelper(IMethodSymbol method, string role,
    ImmutableArray<AggregateParameter> parameters, AggregateType result, FunctionDeclaration declaration, AggregateInvocation invocation)
{
    /// <summary>
    /// Gets the implementation selected by its aggregate capability interface.
    /// </summary>
    internal IMethodSymbol Method { get; } = method;

    /// <summary>
    /// Gets the aggregate support role used by default SQL naming and native behavior.
    /// </summary>
    internal string Role { get; } = role;

    /// <summary>
    /// Gets validated SQL argument slots, excluding the managed context and native deserializer dummy.
    /// </summary>
    internal ImmutableArray<AggregateParameter> Parameters { get; } = parameters;

    /// <summary>
    /// Gets SQL/native type contracts, excluding a deserializer's synthetic dummy argument.
    /// </summary>
    internal AggregateType[] Types { get; } = [.. parameters.Select(static parameter => parameter.Type)];

    /// <summary>
    /// Gets the support function's result contract.
    /// </summary>
    internal AggregateType Result { get; } = result;

    /// <summary>
    /// Gets the common planner, name and privilege options.
    /// </summary>
    internal FunctionDeclaration Declaration { get; } = declaration;

    /// <summary>
    /// Gets the compiler-checked capability dispatch, including its invocation context.
    /// </summary>
    internal AggregateInvocation Invocation { get; } = invocation;

    /// <summary>
    /// Freezes the validated conversion and invocation values needed by helper rendering.
    /// </summary>
    /// <returns>The immutable support contract without method symbols or attributes.</returns>
    internal AggregateHelperModel Freeze() => new(Role, new(Parameters), Result, Declaration, Invocation,
        NumericConstraint.Read(Method.GetReturnTypeAttributes()));

    /// <summary>
    /// Gets whether PostgreSQL supplies an extra SQL-nonnull internal dummy argument.
    /// </summary>
    internal bool Deserialize => Role == "Deserialize";

    /// <summary>
    /// Gets the complete SQL function identity for duplicate detection.
    /// </summary>
    internal string Signature => Declaration.QualifiedName + "(" + string.Join(",", Types.Select(static type => type.Sql)
        .Concat(Deserialize ? ["internal"] : [])) + ")";

    /// <summary>
    /// Gets named SQL parameter declarations, retaining variadic input and adding the native deserializer dummy.
    /// </summary>
    internal string Arguments(SqlTypeProviders providers) => string.Join(", ", Parameters.Select(parameter =>
        (parameter.IsVariadic ? "VARIADIC " : string.Empty) + SqlText.Identifier(parameter.Name) + " " + (parameter.Type.Datum is { } datum ? SqlSchemaTemplate.Type(datum, providers) : "internal"))
        .Concat(Deserialize ? ["internal"] : []));
}
