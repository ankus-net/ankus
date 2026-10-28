using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Identifies a managed declaration without retaining its compiler symbol or conflating referenced assemblies.
/// </summary>
/// <param name="Assembly">The complete declaring assembly identity.</param>
/// <param name="Kind">The declaration category.</param>
/// <param name="Signature">The qualified managed signature, including overload parameters and constructed types.</param>
internal sealed record DeclarationIdentity(string Assembly, SymbolKind Kind, string Signature)
{
    /// <summary>
    /// Extracts the semantic identity used by typed SQL dependency references.
    /// </summary>
    /// <param name="symbol">The resolved managed declaration.</param>
    /// <returns>An immutable declaration key.</returns>
    internal static DeclarationIdentity Create(ISymbol symbol)
        => new((symbol as IAssemblySymbol ?? symbol.ContainingAssembly)?.Identity.ToString() ?? string.Empty, symbol.Kind,
            Key(symbol));

    /// <summary>
    /// Includes overload types and explicit interface identities without relying on ambiguous display names.
    /// </summary>
    private static string Key(ISymbol symbol)
        => symbol switch
        {
            ITypeSymbol type => TypeKey(type),
            IMethodSymbol method => Join(method.ContainingType is { } containing ? TypeKey(containing) : string.Empty, method.MetadataName,
                method.Arity.ToString(CultureInfo.InvariantCulture), method.RefKind.ToString(), TypeKey(method.ReturnType),
                Join([.. method.Parameters.Select(ParameterKey)]),
                Join([.. method.ExplicitInterfaceImplementations.Select(Key)])),
            IPropertySymbol property => Join(TypeKey(property.ContainingType), property.MetadataName,
                Join([.. property.Parameters.Select(ParameterKey)])),
            IAssemblySymbol assembly => assembly.Identity.ToString(),
            _ => Join(symbol.ContainingType is { } container ? TypeKey(container) : symbol.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                symbol.MetadataName),
        };

    /// <summary>
    /// Keeps by-reference overload distinctions alongside the parameter's assembly-qualified type.
    /// </summary>
    private static string ParameterKey(IParameterSymbol parameter)
        => Join(parameter.RefKind.ToString(), TypeKey(parameter.Type));

    /// <summary>
    /// Preserves constructed and externally aliased type identities while ignoring nullable annotations and tuple labels.
    /// </summary>
    private static string TypeKey(ITypeSymbol type)
    {
        if (type is INamedTypeSymbol { IsTupleType: true, TupleUnderlyingType: { } tuple })
        {
            return TypeKey(tuple);
        }

        return type switch
        {
            IArrayTypeSymbol array => Join("array", array.Rank.ToString(CultureInfo.InvariantCulture), array.IsSZArray.ToString(), TypeKey(array.ElementType)),
            IPointerTypeSymbol pointer => Join("pointer", TypeKey(pointer.PointedAtType)),
            IFunctionPointerTypeSymbol function => Join("function", function.Signature.CallingConvention.ToString(),
                Join([.. function.Signature.UnmanagedCallingConventionTypes.Select(TypeKey)]), Key(function.Signature)),
            ITypeParameterSymbol parameter => Join("parameter", parameter.TypeParameterKind.ToString(),
                parameter.Ordinal.ToString(CultureInfo.InvariantCulture), parameter.ContainingSymbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat)),
            INamedTypeSymbol named => Join("named", named.ContainingAssembly?.Identity.ToString() ?? string.Empty,
                named.ContainingType is { } container ? TypeKey(container) : named.ContainingNamespace?.ToDisplayString() ?? string.Empty,
                named.MetadataName, Join([.. named.TypeArguments.Select(TypeKey)])),
            _ => Join(type.Kind.ToString(), type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat)),
        };
    }

    /// <summary>
    /// Length-prefixes identity components so metadata names cannot introduce separator collisions.
    /// </summary>
    private static string Join(params string[] values)
        => string.Concat(values.Select(static value => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value));
}
