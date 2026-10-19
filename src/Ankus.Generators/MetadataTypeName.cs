using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Forms CLR definition names independently of C# identifier escaping.
/// </summary>
internal static class MetadataTypeName
{
    /// <summary>
    /// Preserves raw namespace segments, nested definition separators and generic arity.
    /// </summary>
    /// <param name="type">The semantic definition or constructed type.</param>
    /// <returns>The exact name accepted by metadata definition lookup.</returns>
    internal static string Create(INamedTypeSymbol type)
        => type.ContainingType is { } container ? Create(container) + "+" + type.MetadataName :
            Namespace(type.ContainingNamespace) + type.MetadataName;

    /// <summary>
    /// Uses raw metadata namespace names, free of language-specific display text.
    /// </summary>
    private static string Namespace(INamespaceSymbol space)
        => space.IsGlobalNamespace ? string.Empty : Namespace(space.ContainingNamespace) + space.MetadataName + ".";
}
