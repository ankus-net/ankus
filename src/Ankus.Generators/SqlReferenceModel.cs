using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Retains compiler-checked dependency selection independently of mutable SQL graph ownership.
/// </summary>
/// <param name="Source">The attributed managed declaration.</param>
/// <param name="Kind">The original dependency attribute's class name.</param>
/// <param name="DeclarationId">The optional source SQL declaration selector.</param>
/// <param name="ExternalSupport">Whether the source already declares external planner support.</param>
/// <param name="Target">The compiler-selected target declaration, absent after semantic failure.</param>
/// <param name="Error">A constructor problem that precedes source selection.</param>
/// <param name="TargetError">A semantic target problem deferred until source selection succeeds.</param>
/// <param name="Location">The current attribute diagnostic coordinates.</param>
/// <param name="TargetLocation">The authored method name, or the type when no method is named.</param>
/// <param name="DeclarationIdLocation">The authored source declaration selector, when present.</param>
internal sealed record SqlReferenceModel(SqlReferenceModel.Declaration Source, string Kind, string? DeclarationId,
    bool ExternalSupport, SqlReferenceModel.Declaration? Target, SqlReferenceProblem? Error, SqlReferenceProblem? TargetError,
    GeneratorLocation? Location, GeneratorLocation? TargetLocation, GeneratorLocation? DeclarationIdLocation)
{
    /// <summary>
    /// Selects overloads and inherited members using the compiler's exact type comparer without retaining symbols.
    /// </summary>
    /// <param name="source">The attributed declaration.</param>
    /// <param name="attribute">The original dependency or support attribute.</param>
    /// <param name="compilation">The compiler state owning source coordinates.</param>
    /// <param name="cancellationToken">The current analysis cancellation token.</param>
    /// <returns>The immutable selection and original deferred validation failures.</returns>
    internal static SqlReferenceModel Create(ISymbol source, AttributeData attribute, Compilation compilation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string kind = attribute.AttributeClass!.Name;
        string? declarationId = AttributeValues.Get<string?>(attribute, "DeclarationId", null);
        GeneratorLocation? member = Detach(DatumMappingDiagnostics.Argument(attribute, 1, cancellationToken), compilation);
        SqlReferenceProblem? error = kind == "PgSupportFunctionAttribute" &&
            (attribute.ConstructorArguments.Length != 2 || attribute.ConstructorArguments[1].Value is not string)
            ? new(SqlReferenceProblemKind.SupportMethodName, Location: member) : null;
        AttributeData? function = source.GetAttributes().FirstOrDefault(static candidate =>
            candidate.AttributeClass?.ToDisplayString() == "Ankus.PgFunctionAttribute");
        GeneratorLocation? type = Detach(DatumMappingDiagnostics.Argument(attribute, 0, cancellationToken), compilation);
        GeneratorLocation? target = attribute.ConstructorArguments.Length == 2 && attribute.ConstructorArguments[1].Value is string ? member : type;
        (Declaration? selected, SqlReferenceProblem? targetError) = SelectTarget(attribute, type, target, compilation, cancellationToken);
        return new(Declaration.Create(source), kind, declarationId,
            function is not null && AttributeValues.Get<string?>(function, "SupportFunction", null) is not null,
            selected, error, targetError, Detach(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation(), compilation),
            target, declarationId is null ? null
                : Detach(FunctionDeclarationDiagnostics.Option(attribute, "DeclarationId", cancellationToken), compilation));
    }

    /// <summary>
    /// Preserves constructor validation, exact parameter selection and first declaring base-type lookup.
    /// </summary>
    /// <param name="attribute">The original dependency or support attribute.</param>
    /// <param name="typeLocation">The authored type coordinates.</param>
    /// <param name="target">The authored method-name coordinates, or the type when no method is named.</param>
    /// <param name="compilation">The compiler state owning source coordinates.</param>
    /// <param name="cancellationToken">The current analysis cancellation token.</param>
    /// <returns>The selected declaration, or the failed contract and its authored value.</returns>
    private static (Declaration? Target, SqlReferenceProblem? Error) SelectTarget(AttributeData attribute, GeneratorLocation? typeLocation,
        GeneratorLocation? target, Compilation compilation, CancellationToken cancellationToken)
    {
        if (attribute.ConstructorArguments.Length != 2 ||
            attribute.ConstructorArguments[0].Value is not INamedTypeSymbol { TypeKind: not TypeKind.Error, IsUnboundGenericType: false } type)
        {
            return (null, new(SqlReferenceProblemKind.TargetType, Location: typeLocation));
        }

        string? member = attribute.ConstructorArguments[1].Value as string;
        TypedConstant parameters = attribute.NamedArguments.FirstOrDefault(static argument => argument.Key == "ParameterTypes").Value;
        bool selectedParameters = parameters.Kind == TypedConstantKind.Array && !parameters.IsNull;
        GeneratorLocation? ParameterTypes() => Detach(FunctionDeclarationDiagnostics.Option(attribute, "ParameterTypes", cancellationToken), compilation);
        if (member is null)
        {
            return selectedParameters ? (null, new(SqlReferenceProblemKind.ParameterTypesMethod, Location: ParameterTypes()))
                : (Declaration.Create(type), null);
        }

        if (string.IsNullOrWhiteSpace(member) || !SqlText.IsText(member))
        {
            return (null, new(SqlReferenceProblemKind.MethodName, Location: target));
        }

        int invalid = -1;
        for (int index = 0; selectedParameters && invalid < 0 && index < parameters.Values.Length; index++)
        {
            if (parameters.Values[index].Value is not ITypeSymbol { TypeKind: not TypeKind.Error } parameter ||
                parameter is INamedTypeSymbol { IsUnboundGenericType: true })
            {
                invalid = index;
            }
        }

        if (invalid >= 0)
        {
            return (null, new(SqlReferenceProblemKind.ParameterTypes, Location: Detach(FunctionDeclarationDiagnostics.OptionElement(attribute,
                "ParameterTypes", invalid, cancellationToken), compilation)));
        }

        ISymbol[] members = [];
        for (INamedTypeSymbol? container = type; container is not null && members.Length == 0; container = container.BaseType)
        {
            members = [.. container.GetMembers(member)];
        }

        IMethodSymbol[] methods = [.. members.OfType<IMethodSymbol>().Where(method => !selectedParameters ||
            method.Parameters.Length == parameters.Values.Length && method.Parameters.Select(static parameter => parameter.Type)
                .SequenceEqual(parameters.Values.Select(static parameter => (ITypeSymbol)parameter.Value!), SymbolEqualityComparer.Default))];
        if (methods.Length == 1)
        {
            return (Declaration.Create(methods[0]), null);
        }

        string identity = type.ToDisplayString() + "." + member;
        return (null, (methods.Length == 0, selectedParameters) switch
        {
            (true, false) => new(SqlReferenceProblemKind.MethodMissing, identity, target),
            (false, false) => new(SqlReferenceProblemKind.MethodAmbiguous, identity, target),
            (true, true) => new(SqlReferenceProblemKind.OverloadMissing, identity, ParameterTypes()),
            (false, true) => new(SqlReferenceProblemKind.OverloadAmbiguous, identity, ParameterTypes()),
        });
    }

    /// <summary>
    /// Detaches an authored value from the current syntax tree.
    /// </summary>
    private static GeneratorLocation? Detach(Location? location, Compilation compilation) => GeneratorLocation.Create(location, compilation);

    /// <summary>
    /// Retains only declaration identity and graph-selection category after compiler validation.
    /// </summary>
    /// <param name="Identity">The exact assembly-qualified declaration identity.</param>
    /// <param name="Display">The original managed diagnostic spelling.</param>
    /// <param name="Method">Whether primary selection prefers a function node.</param>
    /// <param name="Assembly">Whether source selection requires an explicit SQL declaration ID.</param>
    internal sealed record Declaration(DeclarationIdentity Identity, string Display, bool Method, bool Assembly)
    {
        /// <summary>
        /// Freezes one compiler declaration without keeping its containing compilation alive.
        /// </summary>
        /// <param name="symbol">The selected compiler declaration.</param>
        /// <returns>The minimal exact graph-selection identity.</returns>
        internal static Declaration Create(ISymbol symbol)
            => new(DeclarationIdentity.Create(symbol), symbol.ToDisplayString(), symbol is IMethodSymbol, symbol is IAssemblySymbol);
    }
}
