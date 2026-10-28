using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Retains a validated SQL argument independently of the managed parameter that supplies it.
/// </summary>
/// <param name="Type">The SQL identity and native conversion contract.</param>
/// <param name="Name">The validated unquoted SQL argument name.</param>
/// <param name="IsVariadic">Whether PostgreSQL collects trailing inputs into this argument.</param>
/// <param name="Precision">The validated optional numeric conversion metadata.</param>
internal sealed record AggregateParameter(AggregateType Type, string Name, bool IsVariadic, NumericPrecision? Precision)
{
    /// <summary>
    /// Resolves a SQL parameter name from its managed source declaration.
    /// </summary>
    /// <param name="parameter">The source parameter before SQL validation.</param>
    /// <returns>The explicit SQL name or the managed name converted to snake case.</returns>
    internal static string ReadName(IParameterSymbol parameter)
        => ReadName(parameter.GetAttributes(), parameter.Name);

    /// <summary>
    /// Resolves a selected SQL slot's explicit name or its managed name in snake case.
    /// </summary>
    internal static string ReadName(ImmutableArray<AttributeData> attributes, string name)
    {
        AttributeData? attribute = attributes.FirstOrDefault(static value => value.AttributeClass?.ToDisplayString() == "Ankus.PgParameterAttribute");
        return attribute is null ? SqlText.SnakeCase(name) : AttributeValues.Get(attribute, "Name", SqlText.SnakeCase(name));
    }

    /// <summary>
    /// Reads an explicit aggregate variadic selection from the selected slot's metadata.
    /// </summary>
    internal static bool ReadVariadic(ImmutableArray<AttributeData> attributes)
        => attributes.Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "Ankus.PgParameterAttribute" &&
            AttributeValues.Get(attribute, "Variadic", false));

    /// <summary>
    /// Recognizes metadata that applies to each individual SQL value inside a managed tuple container.
    /// </summary>
    internal static bool IsSqlMetadata(AttributeData attribute)
        => attribute.AttributeClass?.ToDisplayString() is "Ankus.PgParameterAttribute" or "Ankus.PgSqlTypeAttribute" or
            "Ankus.PgCompositeTypeAttribute" or "Ankus.PgNumericPrecisionAttribute";
}
