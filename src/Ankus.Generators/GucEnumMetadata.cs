using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Preserves imported GUC labels from their selected compiler-owned attributes.
/// </summary>
internal static class GucEnumMetadata
{
    /// <summary>
    /// Reads complete exact field labels from the consumer's defining portable module.
    /// </summary>
    /// <param name="type">The referenced enum's exact semantic identity.</param>
    /// <param name="compilation">The current compilation owning reference images.</param>
    /// <param name="cancellationToken">Cancels metadata traversal.</param>
    /// <returns>The exact labels, or null for a source reference or unreadable selected contract.</returns>
    internal static Dictionary<string, string?>? ReadLabels(INamedTypeSymbol type, Compilation compilation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        MetadataReference? reference = compilation.GetMetadataReference(type.ContainingAssembly) ??
            compilation.References.FirstOrDefault(item => SymbolEqualityComparer.Default.Equals(
                compilation.GetAssemblyOrModuleSymbol(item), type.ContainingModule));
        if (reference is not PortableExecutableReference || type.ContainingModule.GetMetadata() is null)
        {
            return null;
        }

        var labels = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (IFieldSymbol field in type.GetMembers().OfType<IFieldSymbol>().Where(static field => field.HasConstantValue))
        {
            cancellationToken.ThrowIfCancellationRequested();
            AttributeData? selected = field.GetAttributes().FirstOrDefault(static attribute =>
                attribute.AttributeClass?.ToDisplayString() == "Ankus.PgGucLabelAttribute");
            if (selected is null)
            {
                continue;
            }

            if (field.GetAttributes().Count(attribute => SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, selected.AttributeClass)) != 1 ||
                !ExactAttributeStrings.TryRead(field, selected, cancellationToken, out AttributeStrings? strings) || strings is null ||
                !strings.Arguments.TryGetValue(0, out string? label))
            {
                return null;
            }

            labels.Add(field.MetadataName, label);
        }

        return labels;
    }
}
