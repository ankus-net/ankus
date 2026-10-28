using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Selects finite SQL range identities associated with exact mapped scalar bounds.
/// </summary>
internal static class RangeTypeDeclaration
{
    /// <summary>
    /// Finds a mapped scalar bound inside the exact Ankus range container.
    /// </summary>
    internal static INamedTypeSymbol? Bound(INamedTypeSymbol type)
        => type is { Name: "PgRange", Arity: 1 } && type.ContainingNamespace.ToDisplayString() == "Ankus" &&
            type.TypeArguments[0] is INamedTypeSymbol bound && DatumTypeDeclaration.IsMapped(bound) ? bound : null;

    /// <summary>
    /// Validates default and exact targets independently of which finite roots are subsequently selected.
    /// </summary>
    internal static AttributeData[]? Declarations(INamedTypeSymbol type, GeneratorDiagnostics? context, Location? usage = null,
        Compilation? compilation = null)
    {
        AttributeData[] attributes = [.. type.GetAttributes().Where(static item =>
            item.AttributeClass?.ToDisplayString() == "Ankus.PgRangeTypeAttribute")];
        if (attributes.Length != 0 && (!type.IsValueType || type.IsRefLikeType || !DatumTypeDeclaration.IsMapped(type)))
        {
            return Invalid(RangeMappingDiagnostics.Carrier, type.Locations.FirstOrDefault(static item => item.IsInSource));
        }

        var targets = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);
        bool hasDefault = false;
        foreach (AttributeData attribute in attributes)
        {
            if (attribute.ConstructorArguments.IsEmpty && type.DeclaringSyntaxReferences.Length == 0)
            {
                return Invalid(RangeMappingDiagnostics.Metadata, attribute.ApplicationSyntaxReference?.GetSyntax(context?.CancellationToken ?? default).GetLocation());
            }

            if (attribute.ConstructorArguments.Length == 1)
            {
                if (hasDefault)
                {
                    return Invalid(RangeMappingDiagnostics.DuplicateDefault, attribute.ApplicationSyntaxReference?.GetSyntax(context?.CancellationToken ?? default).GetLocation());
                }

                hasDefault = true;
            }
            else if (attribute.ConstructorArguments.Length != 2 ||
                attribute.ConstructorArguments[0].Value is not INamedTypeSymbol target || !DatumTypeDeclaration.IsClosed(target) ||
                !SymbolEqualityComparer.Default.Equals(target.OriginalDefinition, type.OriginalDefinition))
            {
                return Invalid(RangeMappingDiagnostics.Target, DatumMappingDiagnostics.Argument(attribute, 0, context?.CancellationToken ?? default));
            }
            else if (!targets.Add(target))
            {
                return Invalid(RangeMappingDiagnostics.DuplicateExact, attribute.ApplicationSyntaxReference?.GetSyntax(context?.CancellationToken ?? default).GetLocation());
            }
        }

        return attributes;

        AttributeData[]? Invalid(DiagnosticDescriptor descriptor, Location? location)
        {
            if (context is { } output)
            {
                output.Report(descriptor, DatumMappingDiagnostics.CurrentLocation(location, compilation,
                    usage ?? type.Locations.FirstOrDefault(static item => item.IsInSource)));
            }

            return null;
        }
    }

    /// <summary>
    /// Creates an optional closed range contract from a previously validated scalar mapping.
    /// </summary>
    internal static bool TryCreate(DatumTypeDeclaration scalar, out DatumTypeDeclaration? range, GeneratorDiagnostics? context = null,
        Location? usage = null, Compilation? compilation = null)
    {
        range = null;
        AttributeData[]? attributes = Declarations(scalar.Type, context, usage, compilation);
        if (attributes is null)
        {
            return false;
        }

        if (attributes.Length == 0)
        {
            return true;
        }

        AttributeData? attribute = attributes.FirstOrDefault(item => item.ConstructorArguments.Length == 2 &&
            SymbolEqualityComparer.Default.Equals(item.ConstructorArguments[0].Value as ITypeSymbol, scalar.Type)) ??
            attributes.FirstOrDefault(static item => item.ConstructorArguments.Length == 1);
        if (attribute is null)
        {
            return true;
        }

        string? name = attribute.ConstructorArguments[attribute.ConstructorArguments.Length - 1].Value as string;
        string? schema = AttributeValues.Get<string?>(attribute, "Schema", null);
        CancellationToken cancellationToken = context?.CancellationToken ?? default;
        if (compilation is not null && !MappedIdentityMetadata.TryRead(scalar.Type, attribute, compilation, cancellationToken, out name, out schema))
        {
            return Invalid(RangeMappingDiagnostics.Metadata, usage);
        }

        if (!SqlText.IsIdentifier(name))
        {
            return Invalid(RangeMappingDiagnostics.Name, DatumMappingDiagnostics.Argument(attribute, attribute.ConstructorArguments.Length - 1, cancellationToken));
        }

        if (schema is not null && !SqlText.IsIdentifier(schema))
        {
            return Invalid(RangeMappingDiagnostics.Schema, FunctionDeclarationDiagnostics.Option(attribute, "Schema", cancellationToken));
        }

        int origin = AttributeValues.Get(attribute, "Origin", 0);
        if (origin is not (0 or 1))
        {
            return Invalid(RangeMappingDiagnostics.Origin, FunctionDeclarationDiagnostics.Option(attribute, "Origin", cancellationToken));
        }

        if (origin == 1 && schema is null)
        {
            return Invalid(RangeMappingDiagnostics.ExternalSchema, FunctionDeclarationDiagnostics.Option(attribute, "Origin", cancellationToken));
        }

        INamedTypeSymbol? definition = attribute.AttributeClass!.ContainingAssembly.GetTypeByMetadataName("Ankus.PgRange`1");
        if (definition is null)
        {
            return Invalid(RangeMappingDiagnostics.RuntimeType, attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation());
        }

        range = new(definition.Construct(scalar.Type), scalar.Converter, name!, schema, origin == 1,
            scalar.CanRead, scalar.CanWrite, inferred: false, rangeBound: scalar);
        return true;

        bool Invalid(DiagnosticDescriptor descriptor, Location? location)
        {
            if (context is { } output)
            {
                output.Report(descriptor, DatumMappingDiagnostics.CurrentLocation(location, compilation,
                    usage ?? scalar.Type.Locations.FirstOrDefault(static item => item.IsInSource)));
            }

            return false;
        }
    }

}
