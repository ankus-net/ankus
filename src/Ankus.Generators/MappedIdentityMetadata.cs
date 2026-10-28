using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Reads mapped SQL identities without Roslyn's imported-string normalization.
/// </summary>
internal static class MappedIdentityMetadata
{
    /// <summary>
    /// Reads the selected datum or range mapping's complete exact attribute contract.
    /// </summary>
    /// <param name="type">The selected closed managed carrier.</param>
    /// <param name="attribute">The selected default or exact mapping declaration.</param>
    /// <param name="compilation">The current compilation owning its reference images.</param>
    /// <param name="cancellationToken">Cancels metadata traversal.</param>
    /// <param name="name">The exact constructor name.</param>
    /// <param name="schema">The exact schema, or null when omitted or explicitly null.</param>
    /// <returns>Whether the entire selected contract is readable without string normalization.</returns>
    internal static bool TryRead(INamedTypeSymbol type, AttributeData attribute, Compilation compilation,
        CancellationToken cancellationToken, out string? name, out string? schema)
    {
        cancellationToken.ThrowIfCancellationRequested();
        name = null;
        schema = null;
        if (attribute.AttributeClass?.ToDisplayString() is not ("Ankus.PgDatumTypeAttribute" or "Ankus.PgRangeTypeAttribute") ||
            (attribute.AttributeClass.Name == "PgDatumTypeAttribute" ? attribute.ConstructorArguments.Length is not (2 or 3) :
                attribute.ConstructorArguments.Length is not (1 or 2)))
        {
            return false;
        }

        if (type.DeclaringSyntaxReferences.Length == 0)
        {
            MetadataReference? reference = compilation.GetMetadataReference(type.ContainingAssembly) ??
                compilation.References.FirstOrDefault(item => SymbolEqualityComparer.Default.Equals(
                    compilation.GetAssemblyOrModuleSymbol(item), type.ContainingModule));
            if (reference is not (CompilationReference or PortableExecutableReference))
            {
                return false;
            }
        }

        if (!ExactAttributeStrings.TryRead(type, attribute, cancellationToken, out AttributeStrings? strings) || strings is null)
        {
            return false;
        }

        int nameIndex = attribute.ConstructorArguments.Length - (attribute.AttributeClass.Name == "PgDatumTypeAttribute" ? 2 : 1);
        if (!strings.Arguments.TryGetValue(nameIndex, out name))
        {
            return false;
        }

        schema = strings.Property("Schema", null);
        return true;
    }
}
