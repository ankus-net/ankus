using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Walks the types that lexically contain a declaration.
/// </summary>
internal static class TypeNesting
{
    /// <summary>
    /// Enumerates a type and each type that contains it, innermost first.
    /// </summary>
    /// <param name="type">The innermost type.</param>
    /// <returns>The type, then its containing types outward.</returns>
    internal static IEnumerable<INamedTypeSymbol> SelfAndContainers(INamedTypeSymbol type)
    {
        for (INamedTypeSymbol? current = type; current is not null; current = current.ContainingType)
        {
            yield return current;
        }
    }
}
