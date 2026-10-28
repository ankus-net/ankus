using System.Linq;

namespace Ankus.Generators;

/// <summary>
/// Stores an aggregate support function's validated conversion and constrained-call contracts without compiler state.
/// </summary>
/// <param name="Role">The conventional PostgreSQL support role.</param>
/// <param name="Parameters">The exact flattened SQL input contracts.</param>
/// <param name="Result">The result datum or owned-state contract.</param>
/// <param name="Declaration">The validated SQL execution and naming options.</param>
/// <param name="Invocation">The closed constrained capability call.</param>
/// <param name="ResultPrecision">The optional validated output rescaling policy.</param>
internal sealed record AggregateHelperModel(string Role, EquatableArray<AggregateParameter> Parameters, AggregateType Result,
    FunctionDeclaration Declaration, AggregateInvocation Invocation, NumericPrecision? ResultPrecision)
{
    /// <summary>
    /// Gets SQL input conversions before the synthetic native deserializer dummy.
    /// </summary>
    internal EquatableArray<AggregateType> Types => new(Parameters.Select(static parameter => parameter.Type));

    /// <summary>
    /// Gets whether PostgreSQL supplies a synthetic internal deserializer argument.
    /// </summary>
    internal bool Deserialize => Role == "Deserialize";

    /// <summary>
    /// Gets the complete SQL function identity for duplicate detection.
    /// </summary>
    internal string Signature => Declaration.QualifiedName + "(" + string.Join(",", Types.Select(static type => type.Sql)
        .Concat(Deserialize ? ["internal"] : [])) + ")";

    /// <summary>
    /// Composes named arguments with the current graph's type ownership and native deserializer dummy.
    /// </summary>
    /// <param name="providers">The current catalog provider inventory.</param>
    /// <returns>The complete specialized SQL input clauses.</returns>
    internal string Arguments(SqlTypeProviders providers) => string.Join(", ", Parameters.Select(parameter =>
        (parameter.IsVariadic ? "VARIADIC " : string.Empty) + SqlText.Identifier(parameter.Name) + " " +
        (parameter.Type.Datum is { } datum ? SqlSchemaTemplate.Type(datum, providers) : "internal"))
        .Concat(Deserialize ? ["internal"] : []));
}
