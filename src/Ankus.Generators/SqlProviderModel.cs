using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Retains authored provider inventory independently of compiler attributes and current SQL graph ownership.
/// </summary>
/// <param name="Function">Whether the provider declares a SQL function signature; otherwise it declares a type.</param>
/// <param name="BlockId">The original custom SQL block identifier.</param>
/// <param name="Name">The exact type name or authored function signature.</param>
/// <param name="Schema">The optional explicitly selected type schema.</param>
/// <param name="Managed">Whether type selection uses the managed constructor overload.</param>
/// <param name="SchemaAuthored">Whether Schema was explicitly supplied, including null.</param>
/// <param name="Type">The exact managed type identity, absent for named or invalid selectors.</param>
/// <param name="Location">The current attribute diagnostic coordinates.</param>
/// <param name="BlockLocation">The exact authored SQL block argument.</param>
/// <param name="NameLocation">The exact authored catalog-name or function-signature argument.</param>
/// <param name="SchemaLocation">The exact authored schema override expression.</param>
internal sealed record SqlProviderModel(bool Function, string? BlockId, string? Name, string? Schema,
    bool Managed, bool SchemaAuthored, ManagedTypeIdentity? Type, GeneratorLocation? Location, GeneratorLocation? BlockLocation,
    GeneratorLocation? NameLocation, GeneratorLocation? SchemaLocation)
{
    /// <summary>
    /// Freezes constructor shape and exact compiler identity without resolving catalog ownership or executing SQL.
    /// </summary>
    /// <param name="attribute">The selected assembly-level provider attribute.</param>
    /// <param name="compilation">The current compiler state owning source coordinates.</param>
    /// <param name="cancellationToken">The current analysis cancellation token.</param>
    /// <returns>The immutable declaration values used by current provider validation.</returns>
    internal static SqlProviderModel Create(AttributeData attribute, Compilation compilation, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        bool validArity = attribute.ConstructorArguments.Length == 2;
        bool managed = attribute.AttributeConstructor is { Parameters.Length: 2 } constructor &&
            constructor.Parameters[1].Type.ToDisplayString() == "System.Type";
        return new(attribute.AttributeClass!.Name == "PgSqlFunctionProviderAttribute",
            validArity ? attribute.ConstructorArguments[0].Value as string : null,
            validArity ? attribute.ConstructorArguments[1].Value as string : null,
            AttributeValues.Get<string?>(attribute, "Schema", null), managed,
            attribute.NamedArguments.Any(static argument => argument.Key == "Schema"),
            validArity && attribute.ConstructorArguments[1].Value is ITypeSymbol supplied ? ManagedTypeIdentity.Create(supplied) : null,
            GeneratorLocation.Create(attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken).GetLocation(), compilation),
            ArgumentLocation(attribute, 0, compilation, cancellationToken), ArgumentLocation(attribute, 1, compilation, cancellationToken),
            SchemaArgumentLocation(attribute, compilation, cancellationToken));
    }

    /// <summary>
    /// Locates the authored schema property independently of constructor argument order.
    /// </summary>
    /// <param name="attribute">The transient selected provider attribute.</param>
    /// <param name="compilation">The current compilation owning the source coordinates.</param>
    /// <param name="cancellationToken">The current generator cancellation token.</param>
    /// <returns>The detached schema expression coordinates, when authored.</returns>
    private static GeneratorLocation? SchemaArgumentLocation(AttributeData attribute, Compilation compilation, CancellationToken cancellationToken)
    {
        var syntax = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken) as AttributeSyntax;
        AttributeArgumentSyntax? argument = syntax?.ArgumentList?.Arguments.FirstOrDefault(static item =>
            item.NameEquals?.Name.Identifier.ValueText == "Schema");
        return GeneratorLocation.Create(argument?.Expression.GetLocation(), compilation);
    }

    /// <summary>
    /// Locates constructor values by semantic parameter, whatever the authored argument order.
    /// </summary>
    /// <param name="attribute">The transient selected provider attribute.</param>
    /// <param name="position">The constructor parameter index.</param>
    /// <param name="compilation">The current compilation owning the source coordinates.</param>
    /// <param name="cancellationToken">The current generator cancellation token.</param>
    /// <returns>The exact detached value coordinates, with an attribute fallback for incomplete syntax.</returns>
    private static GeneratorLocation? ArgumentLocation(AttributeData attribute, int position, Compilation compilation, CancellationToken cancellationToken)
    {
        var syntax = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken) as AttributeSyntax;
        string? parameter = attribute.AttributeConstructor?.Parameters.ElementAtOrDefault(position)?.Name;
        AttributeArgumentSyntax? argument = syntax?.ArgumentList?.Arguments.FirstOrDefault(item => parameter is not null
            && item.NameColon?.Name.Identifier.ValueText == parameter);
        if (argument is null && syntax?.ArgumentList?.Arguments.ElementAtOrDefault(position) is { NameColon: null, NameEquals: null } positional)
        {
            argument = positional;
        }

        return GeneratorLocation.Create(argument?.Expression.GetLocation() ?? syntax?.GetLocation(), compilation);
    }
}
