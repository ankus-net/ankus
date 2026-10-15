using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Identifies independently correctable datum mapping contracts at their authored syntax.
/// </summary>
internal static class DatumMappingDiagnostics
{
    private const string HelpLink = "https://ankus.net/raw-values/#mapping-diagnostics";

    private static readonly DiagnosticDescriptor s_rootShape = new("ANKUS134", "Invalid datum mapping carrier",
        "PgDatumType requires a closed, concrete class, struct, or enum that is not static or ref-like",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the unsupported managed carrier diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor RootShape => s_rootShape;

    private static readonly DiagnosticDescriptor s_rootAccess = new("ANKUS135", "Inaccessible datum mapping carrier",
        "Mapped type '{0}' must be accessible to generated extension code, including its containing types and type arguments",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the inaccessible managed carrier diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor RootAccess => s_rootAccess;

    private static readonly DiagnosticDescriptor s_storageConflict = new("ANKUS136", "Conflicting datum storage declarations",
        "PgDatumType cannot be combined with PgType or PgEnum; choose one storage contract for '{0}'",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the conflicting storage declaration diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor StorageConflict => s_storageConflict;

    private static readonly DiagnosticDescriptor s_missingMapping = new("ANKUS137", "Missing closed datum mapping",
        "No PgDatumType declaration selects '{0}'; add an exact declaration for this closed managed type or a default mapping",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the absent exact mapping diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor MissingMapping => s_missingMapping;

    private static readonly DiagnosticDescriptor s_name = new("ANKUS138", "Invalid datum mapping type name",
        "The type name must be a nonempty identifier of at most 63 UTF-8 bytes with valid Unicode and no zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid catalog name diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Name => s_name;

    private static readonly DiagnosticDescriptor s_schema = new("ANKUS139", "Invalid datum mapping schema",
        "Schema must be a nonempty identifier of at most 63 UTF-8 bytes with valid Unicode and no zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid schema diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Schema => s_schema;

    private static readonly DiagnosticDescriptor s_origin = new("ANKUS140", "Invalid datum mapping origin",
        "Origin must be ThisExtension or External",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the undefined ownership value diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Origin => s_origin;

    private static readonly DiagnosticDescriptor s_externalSchema = new("ANKUS141", "External datum mapping requires a schema",
        "External datum mappings require an explicit Schema",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the missing external schema diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor ExternalSchema => s_externalSchema;

    private static readonly DiagnosticDescriptor s_converterShape = new("ANKUS142", "Invalid datum converter type",
        "The converter must be a closed, concrete class or struct that is not static or ref-like",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid converter shape diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor ConverterShape => s_converterShape;

    private static readonly DiagnosticDescriptor s_converterAccess = new("ANKUS143", "Inaccessible datum converter",
        "Converter '{0}' must be accessible to generated extension code, including its containing types and type arguments",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the inaccessible converter diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor ConverterAccess => s_converterAccess;

    private static readonly DiagnosticDescriptor s_constructor = new("ANKUS144", "Datum converter requires a constructor",
        "The converter must have an accessible parameterless constructor",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the missing converter constructor diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Constructor => s_constructor;

    private static readonly DiagnosticDescriptor s_contract = new("ANKUS145", "Mismatched datum converter contract",
        "The converter must implement IPgDatumReader<T> or IPgDatumWriter<T> for the exact non-nullable managed type '{0}'",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the mismatched conversion interface diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Contract => s_contract;

    private static readonly DiagnosticDescriptor s_requiredMembers = new("ANKUS146", "Datum converter has required members",
        "A converter with C# required members needs a parameterless constructor carrying SetsRequiredMembers",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the unsatisfied required-member constructor diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor RequiredMembers => s_requiredMembers;

    private static readonly DiagnosticDescriptor s_inference = new("ANKUS147", "Cannot infer datum converter arguments",
        "The generic datum converter's type arguments cannot be inferred from exact reader/writer interfaces; specify a closed converter",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the incomplete generic inference diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Inference => s_inference;

    private static readonly DiagnosticDescriptor s_ambiguousInference = new("ANKUS148", "Ambiguous datum converter inference",
        "The generic datum converter has more than one inferred closed construction; specify a closed converter explicitly",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the ambiguous generic inference diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor AmbiguousInference => s_ambiguousInference;

    private static readonly DiagnosticDescriptor s_constraint = new("ANKUS149", "Invalid inferred datum converter constraint",
        "Inferred datum converter '{0}' is not a valid C# constructed type (compiler diagnostic {1}); satisfy its type-parameter constraints or specify a closed converter",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the inferred C# constraint violation diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Constraint => s_constraint;

    private static readonly DiagnosticDescriptor s_globalIdentity = new("ANKUS150", "Unresolved datum mapping identity",
        "The mapped type and converter must resolve unambiguously through their global qualified names; extern-alias-only contracts are unsupported",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the unresolved global type identity diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor GlobalIdentity => s_globalIdentity;

    private static readonly DiagnosticDescriptor s_slotOverride = new("ANKUS151", "Datum mapping has a SQL override",
        "PgDatumType signatures cannot override their mapping with PgSqlType or PgCompositeType; remove the slot override",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the per-slot storage override diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor SlotOverride => s_slotOverride;

    private static readonly DiagnosticDescriptor s_reader = new("ANKUS152", "Datum mapping requires a reader",
        "The datum mapping for '{0}' does not support reading SQL arguments; implement IPgDatumReader<T> for this exact type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the missing SQL input capability diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Reader => s_reader;

    private static readonly DiagnosticDescriptor s_writer = new("ANKUS153", "Datum mapping requires a writer",
        "The datum mapping for '{0}' does not support writing SQL results; implement IPgDatumWriter<T> for this exact type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the missing SQL result capability diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Writer => s_writer;

    private static readonly DiagnosticDescriptor s_container = new("ANKUS154", "Unsupported mapped datum container",
        "Mapped datum types support scalar signatures and one array layer; nested arrays and other containers are unsupported",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the unsupported mapped container diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Container => s_container;

    private static readonly DiagnosticDescriptor s_duplicateDefault = new("ANKUS155", "Duplicate default datum mapping",
        "A managed type may have only one default PgDatumType declaration; remove the duplicate default",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the duplicate default declaration diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor DuplicateDefault => s_duplicateDefault;

    private static readonly DiagnosticDescriptor s_target = new("ANKUS156", "Invalid explicit datum mapping target",
        "An explicit PgDatumType target must be a closed construction of the annotated managed type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid explicitly selected managed target diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Target => s_target;

    private static readonly DiagnosticDescriptor s_duplicateExact = new("ANKUS157", "Duplicate exact datum mapping",
        "A closed managed type may have only one exact PgDatumType declaration; remove the duplicate target",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the duplicate exact declaration diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor DuplicateExact => s_duplicateExact;

    private static readonly DiagnosticDescriptor s_metadata = new("ANKUS204", "Unreadable datum mapping metadata",
        "The referenced PgDatumType metadata must contain a complete attribute with exact valid UTF-8 strings; rebuild the defining assembly",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the unreadable imported mapping diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Metadata => s_metadata;

    /// <summary>
    /// Keeps referenced declarations' diagnostics on source owned by the current compilation.
    /// </summary>
    /// <param name="location">The original declaration or argument location.</param>
    /// <param name="compilation">The current compilation, when performing mapping preflight.</param>
    /// <param name="fallback">The consuming source location.</param>
    /// <returns>The owned source location, or null when neither location belongs to this compilation.</returns>
    internal static Location? CurrentLocation(Location? location, Compilation? compilation, Location? fallback = null)
    {
        if (location?.SourceTree is { } tree && (compilation is null || compilation.ContainsSyntaxTree(tree)))
        {
            return location;
        }

        return fallback?.SourceTree is { } source && (compilation is null || compilation.ContainsSyntaxTree(source)) ? fallback : null;
    }

    /// <summary>
    /// Locates a positional or named constructor value without retaining source syntax.
    /// </summary>
    /// <param name="attribute">The transient mapping attribute.</param>
    /// <param name="index">The semantic constructor parameter index.</param>
    /// <param name="cancellationToken">The current generation token.</param>
    /// <returns>The value location, or the mapping attribute when no value is available.</returns>
    internal static Location? Argument(AttributeData attribute, int index, CancellationToken cancellationToken)
    {
        var syntax = attribute.ApplicationSyntaxReference?.GetSyntax(cancellationToken) as AttributeSyntax;
        string? name = attribute.AttributeConstructor?.Parameters.ElementAtOrDefault(index)?.Name;
        AttributeArgumentSyntax? argument = syntax?.ArgumentList?.Arguments.FirstOrDefault(item => item.NameEquals is null &&
            item.NameColon?.Name.Identifier.ValueText == name);
        argument ??= syntax?.ArgumentList?.Arguments.Where(static item => item.NameEquals is null)
            .ElementAtOrDefault(index);
        return argument?.Expression.GetLocation() ?? syntax?.GetLocation();
    }

    /// <summary>
    /// Locates the SQL input or output type responsible for a conversion requirement.
    /// </summary>
    /// <param name="symbol">The transient consuming declaration.</param>
    /// <param name="cancellationToken">The current generation token.</param>
    /// <returns>The authored type location, falling back to the declaration name.</returns>
    internal static Location? Slot(ISymbol symbol, CancellationToken cancellationToken)
        => symbol is IMethodSymbol method ? FunctionDeclarationDiagnostics.Result(method, cancellationToken) :
            (symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as ParameterSyntax)?.Type?.GetLocation()
                ?? symbol.Locations.FirstOrDefault();

}
