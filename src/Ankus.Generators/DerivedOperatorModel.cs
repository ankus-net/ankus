using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Identifies one independently correctable generated-operator contract.
/// </summary>
internal enum DerivedOperatorError
{
    /// <summary>
    /// The datum mapping cannot read the declared value.
    /// </summary>
    Reader,

    /// <summary>
    /// The managed value lacks exact equality semantics.
    /// </summary>
    Equality,

    /// <summary>
    /// The managed value lacks exact ordering semantics.
    /// </summary>
    Ordering,

    /// <summary>
    /// The managed value lacks PostgreSQL-compatible hashing semantics.
    /// </summary>
    Hashing,
}

/// <summary>
/// Detaches generated value semantics and authored graph policy from compiler symbols.
/// </summary>
/// <param name="Identity">The exact declaring type identity.</param>
/// <param name="Display">The managed graph and selection spelling.</param>
/// <param name="DiagnosticName">The unqualified diagnostic name.</param>
/// <param name="Managed">The globally qualified managed type spelling.</param>
/// <param name="Value">The scalar conversion contract, or null for an unsupported root.</param>
/// <param name="Enumeration">Whether operations use enum value semantics.</param>
/// <param name="Underlying">The globally qualified enum integral type.</param>
/// <param name="Symbol">The original assembly-scoped native helper identity.</param>
/// <param name="Equality">The authored equality options, when declared.</param>
/// <param name="Ordering">The authored ordering options, when declared.</param>
/// <param name="Hashing">The authored hashing options, when declared.</param>
/// <param name="Location">The current detached declaration coordinates.</param>
/// <param name="Error">The optional semantic validation failure.</param>
internal sealed record DerivedOperatorModel(DeclarationIdentity Identity, string Display, string DiagnosticName, string Managed,
    FunctionType? Value, bool Enumeration, string Underlying, string Symbol, SqlDeclarationOptions? Equality,
    SqlDeclarationOptions? Ordering, SqlDeclarationOptions? Hashing, GeneratorLocation? Location, DerivedOperatorError? Error)
{
    /// <summary>
    /// Analyzes exact interface contracts once without resolving extension graph ownership.
    /// </summary>
    /// <param name="type">The selected concrete managed root.</param>
    /// <param name="compilation">The compilation owning its source locations.</param>
    /// <param name="cancellationToken">The current analysis cancellation token.</param>
    /// <returns>The immutable semantic result and authored metadata.</returns>
    internal static DerivedOperatorModel Create(INamedTypeSymbol type, Compilation compilation, CancellationToken cancellationToken)
    {
        AttributeData? equality = Attribute("Ankus.PgEqualityAttribute");
        AttributeData? ordering = Attribute("Ankus.PgOrderingAttribute");
        AttributeData? hashing = Attribute("Ankus.PgHashingAttribute");
        string managed = type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        FunctionType? value = FunctionType.Create(type.WithNullableAnnotation(NullableAnnotation.NotAnnotated));
        bool enumeration = type.TypeKind == TypeKind.Enum;
        DerivedOperatorError? error = null;
        if (value?.DatumType is { CanRead: false })
        {
            error = DerivedOperatorError.Reader;
        }
        else if (!enumeration && !Implements("System.IEquatable<T>", type))
        {
            error = DerivedOperatorError.Equality;
        }
        else if (ordering is not null && !enumeration && !Implements("System.IComparable<T>", type))
        {
            error = DerivedOperatorError.Ordering;
        }
        else if (hashing is not null && !enumeration && !Implements("Ankus.IPgHashable", null))
        {
            error = DerivedOperatorError.Hashing;
        }

        using SHA256 hash = SHA256.Create();
        byte[] digest = hash.ComputeHash(Encoding.UTF8.GetBytes(PgFunctionGenerator.SymbolScope(type.ContainingAssembly) + ":" + managed));
        string symbol = string.Concat(digest.Take(16).Select(static item => item.ToString("x2", CultureInfo.InvariantCulture)));
        return new(DeclarationIdentity.Create(type), type.ToDisplayString(), type.Name, managed, value, enumeration,
            type.EnumUnderlyingType?.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat) ?? string.Empty, symbol,
            SqlDeclarationOptions.Read(equality, compilation, cancellationToken), SqlDeclarationOptions.Read(ordering, compilation, cancellationToken),
            SqlDeclarationOptions.Read(hashing, compilation, cancellationToken),
            GeneratorLocation.Create(type.Locations.FirstOrDefault(), compilation), error);

        AttributeData? Attribute(string name) => type.GetAttributes().FirstOrDefault(item => item.AttributeClass?.ToDisplayString() == name);

        bool Implements(string definition, INamedTypeSymbol? argument) => type.AllInterfaces.Any(contract =>
            contract.OriginalDefinition.ToDisplayString() == definition &&
            (argument is null || contract.TypeArguments.Length == 1 && SymbolEqualityComparer.Default.Equals(contract.TypeArguments[0], argument)));
    }
}
