using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Prevents advisory interface warnings from weakening a generated aggregate's SQL contract.
/// </summary>
internal static class AggregateContractNullability
{
    private static readonly DiagnosticDescriptor s_mismatch = new(
        "ANKUS028", "Incompatible aggregate nullability",
        "'{0}' has an incompatible {1} nullability contract: implementation '{2}' must preserve interface '{3}'",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/aggregates/#compiler-checked-aggregate-contracts");

    /// <summary>
    /// Checks contravariant inputs and covariant results before creating SQL helpers.
    /// </summary>
    /// <param name="contract">The constructed static interface member.</param>
    /// <param name="implementation">Its actual implementation, including inherited or explicit members.</param>
    /// <param name="context">The diagnostic destination.</param>
    /// <returns>Whether the implementation preserves every nullable value promised by the interface.</returns>
    internal static bool Validate(IMethodSymbol contract, IMethodSymbol implementation, GeneratorDiagnostics context)
    {
        if (!SqlNullability.ValidateValue(implementation.ReturnType, implementation, implementation.Name + " result", context))
        {
            return false;
        }

        bool outputNullable = ReturnsNullable(contract, implementation);
        if (!Compatible(implementation.ReturnType, contract.ReturnType, compareTop: false) ||
            outputNullable && !ReturnsNullable(contract, contract))
        {
            Location? location = implementation.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is MethodDeclarationSyntax method
                ? method.ReturnType.GetLocation() : implementation.Locations.FirstOrDefault();
            Report(location, "result", implementation.ReturnType, contract.ReturnType);
            return false;
        }

        for (int index = 1; index < contract.Parameters.Length; index++)
        {
            IParameterSymbol source = contract.Parameters[index];
            IParameterSymbol target = implementation.Parameters[index];
            if (!ValidateSqlValue(target.Type, target, context))
            {
                return false;
            }

            if (!Compatible(source.Type, target.Type, compareTop: false) || AcceptsNullable(source) && !AcceptsNullable(target))
            {
                Location? location = target.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) is ParameterSyntax parameter
                    ? parameter.Type?.GetLocation() : target.Locations.FirstOrDefault();
                Report(location, target.Name, target.Type, source.Type);
                return false;
            }
        }

        return true;

        void Report(Location? location, string boundary, ITypeSymbol actual, ITypeSymbol expected)
            => context.Report(s_mismatch, location, implementation.Name, boundary, AggregateArgument.Format(actual), AggregateArgument.Format(expected));
    }

    /// <summary>
    /// Requires explicit SQL annotations for actual grouped inputs as well as their interface slots.
    /// </summary>
    private static bool ValidateSqlValue(ITypeSymbol type, IParameterSymbol source, GeneratorDiagnostics context)
    {
        if (type is INamedTypeSymbol { IsTupleType: true } tuple)
        {
            return tuple.TupleElements.All(element => ValidateSqlValue(element.Type, source, context));
        }

        return SqlNullability.ValidateValue(type, source, source.Name, context);
    }

    /// <summary>
    /// Compares nested annotations using C# tuple, array and generic variance rules.
    /// </summary>
    private static bool Compatible(ITypeSymbol source, ITypeSymbol target, bool compareTop = true)
    {
        if (!SymbolEqualityComparer.Default.Equals(source, target) ||
            compareTop && source.IsReferenceType && source.NullableAnnotation == NullableAnnotation.Annotated &&
            target.NullableAnnotation == NullableAnnotation.NotAnnotated)
        {
            return false;
        }

        if (source is IArrayTypeSymbol sourceArray && target is IArrayTypeSymbol targetArray)
        {
            return Compatible(sourceArray.ElementType, targetArray.ElementType);
        }

        if (source is not INamedTypeSymbol sourceNamed || target is not INamedTypeSymbol targetNamed)
        {
            return true;
        }

        if (sourceNamed.ContainingType is { } containing &&
            !SymbolEqualityComparer.IncludeNullability.Equals(containing, targetNamed.ContainingType))
        {
            return false;
        }

        if (sourceNamed.IsTupleType && targetNamed.IsTupleType)
        {
            return sourceNamed.TupleElements.Zip(targetNamed.TupleElements,
                static (left, right) => Compatible(left.Type, right.Type)).All(static compatible => compatible);
        }

        for (int index = 0; index < sourceNamed.TypeArguments.Length; index++)
        {
            ITypeSymbol left = sourceNamed.TypeArguments[index];
            ITypeSymbol right = targetNamed.TypeArguments[index];
            bool valid = sourceNamed.OriginalDefinition.TypeParameters[index].Variance switch
            {
                VarianceKind.Out => Compatible(left, right),
                VarianceKind.In => Compatible(right, left),
                _ => SymbolEqualityComparer.IncludeNullability.Equals(left, right),
            };
            if (!valid)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Reads input preconditions without confusing them with postconditions on the same parameter.
    /// </summary>
    private static bool AcceptsNullable(IParameterSymbol parameter)
        => parameter.Type.IsReferenceType && !Has(parameter.GetAttributes(), "DisallowNullAttribute") &&
            (parameter.NullableAnnotation == NullableAnnotation.Annotated || Has(parameter.GetAttributes(), "AllowNullAttribute"));

    /// <summary>
    /// Reads return promises, including a conditional promise whose interface input is always present.
    /// </summary>
    private static bool ReturnsNullable(IMethodSymbol contract, IMethodSymbol implementation)
    {
        if (!implementation.ReturnType.IsReferenceType)
        {
            return false;
        }

        ImmutableArray<AttributeData> attributes = implementation.GetReturnTypeAttributes();
        if (Has(attributes, "NotNullAttribute") || attributes.Any(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis.NotNullIfNotNullAttribute" &&
            attribute.ConstructorArguments.Length == 1 && attribute.ConstructorArguments[0].Value is string name &&
            implementation.Parameters.Select((parameter, index) => (parameter, index)).Any(pair => pair.parameter.Name == name &&
                !AcceptsNullable(contract.Parameters[pair.index]) &&
                contract.Parameters[pair.index].Type is not INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })))
        {
            return false;
        }

        return implementation.ReturnNullableAnnotation == NullableAnnotation.Annotated || Has(attributes, "MaybeNullAttribute");
    }

    /// <summary>
    /// Identifies standard flow annotations by their fully qualified metadata name.
    /// </summary>
    private static bool Has(ImmutableArray<AttributeData> attributes, string name)
        => attributes.Any(attribute => attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.CodeAnalysis." + name);
}
