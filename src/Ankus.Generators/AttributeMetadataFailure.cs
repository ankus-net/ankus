using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Retains an exact attribute decoding failure without retaining compiler symbols.
/// </summary>
/// <param name="Attribute">The unreadable attribute's managed name.</param>
/// <param name="Owner">The defining declaration's managed name.</param>
internal sealed record AttributeMetadataFailure(string Attribute, string Owner)
{
    private static readonly DiagnosticDescriptor s_diagnostic = new("ANKUS206", "Unreadable declaration attribute metadata",
        "Attribute '{0}' on '{1}' cannot be decoded exactly; rebuild its defining assembly with complete valid UTF-8 metadata",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-types/#referenced-attribute-metadata");

    /// <summary>
    /// Detaches the defining declaration and selected attribute identities.
    /// </summary>
    internal static AttributeMetadataFailure Create(ISymbol owner, AttributeData attribute)
        => new(attribute.AttributeClass?.ToDisplayString() ?? "unknown", owner.ToDisplayString());

    /// <summary>
    /// Reports the exact decoding failure at a source location owned by the consumer.
    /// </summary>
    internal void Report(Location? location, GeneratorDiagnostics diagnostics)
        => diagnostics.Report(s_diagnostic, location, Attribute, Owner);

    /// <summary>
    /// Diagnoses unreadable enum or base-type metadata when a SQL conversion cannot be constructed.
    /// </summary>
    /// <param name="type">The rejected SQL slot's managed type.</param>
    /// <param name="location">The current consuming slot's source location.</param>
    /// <param name="diagnostics">The transient diagnostic destination.</param>
    /// <returns>Whether an exact metadata failure was reported.</returns>
    internal static bool ReportConversion(ITypeSymbol type, Location? location, GeneratorDiagnostics diagnostics)
    {
        AttributeMetadataFailure? failure = null;
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        Visit(type);
        failure?.Report(location, diagnostics);
        return failure is not null;

        void Visit(ITypeSymbol candidate)
        {
            diagnostics.CancellationToken.ThrowIfCancellationRequested();
            if (failure is not null || !visited.Add(candidate))
            {
                return;
            }

            if (candidate is IArrayTypeSymbol array)
            {
                Visit(array.ElementType);
                return;
            }

            if (candidate is not INamedTypeSymbol named)
            {
                return;
            }

            EnumDeclaration.Create(named, cancellationToken: diagnostics.CancellationToken, metadataFailure: value => failure = value);
            if (failure is null)
            {
                CustomTypeDeclaration.Create(named, cancellationToken: diagnostics.CancellationToken, metadataFailure: value => failure = value);
            }

            foreach (ITypeSymbol argument in named.TypeArguments)
            {
                Visit(argument);
            }
        }
    }
}
