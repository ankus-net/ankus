using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Reconstructs one managed argument from its flattened SQL slots.
/// </summary>
/// <param name="Managed">The exact closed managed argument spelling.</param>
/// <param name="Start">The first SQL slot consumed by this argument.</param>
/// <param name="Count">The number of SQL slots.</param>
/// <param name="Tuple">Whether the argument is a tuple; otherwise it is one scalar.</param>
internal sealed record AggregateArgument(string Managed, int Start, int Count, bool Tuple)
{
    /// <summary>
    /// Reconstructs the argument from expressions that already own any borrowed values.
    /// </summary>
    /// <param name="values">Expressions ordered by native SQL slot.</param>
    /// <returns>The managed scalar, tuple or empty argument group.</returns>
    internal string Read(IReadOnlyList<string> values)
        => Count == 0 ? "default(" + Managed + ")" : !Tuple ? values[Start] :
            Count == 1 ? "new " + Managed + "(" + values[Start] + ")" : "(" + string.Join(", ", values.Skip(Start).Take(Count)) + ")";

    /// <summary>
    /// Formats a type without erasing reference nullability.
    /// </summary>
    internal static string Format(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat.WithMiscellaneousOptions(
        SymbolDisplayFormat.FullyQualifiedFormat.MiscellaneousOptions | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier));
}
