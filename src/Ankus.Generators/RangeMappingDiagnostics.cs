using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Identifies independently correctable mapped range contracts.
/// </summary>
internal static class RangeMappingDiagnostics
{
    private const string HelpLink = "https://ankus.net/ranges/#mapping-diagnostics";

    /// <summary>
    /// The unsupported range bound diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_carrier = new("ANKUS158", "Invalid mapped range bound",
        "PgRangeType requires a non-ref-like value type carrying PgDatumType for its scalar bounds",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// The duplicate default range mapping diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicateDefault = new("ANKUS159", "Duplicate default range mapping",
        "A managed scalar type may have only one default PgRangeType declaration",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// The invalid closed range target diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_target = new("ANKUS160", "Invalid exact range mapping target",
        "An explicit PgRangeType target must be a closed construction of the annotated scalar bound type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// The duplicate closed range mapping diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicateExact = new("ANKUS161", "Duplicate exact range mapping",
        "A closed scalar bound type may have only one exact PgRangeType declaration",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// The invalid catalog range name diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_name = new("ANKUS162", "Invalid mapped range name",
        "The range name must be a nonempty identifier of at most 63 UTF-8 bytes with valid Unicode and no zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// The invalid range schema diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_schema = new("ANKUS163", "Invalid mapped range schema",
        "Schema must be a nonempty identifier of at most 63 UTF-8 bytes with valid Unicode and no zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// The undefined range ownership diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_origin = new("ANKUS164", "Invalid mapped range origin",
        "Origin must be ThisExtension or External",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// The missing external range schema diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_externalSchema = new("ANKUS165", "External range mapping requires a schema",
        "External range mappings require an explicit Schema",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// The missing runtime range definition diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_runtimeType = new("ANKUS166", "Missing range runtime contract",
        "The range declaration must resolve the Ankus PgRange<T> runtime type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// The ambiguous closed range identity diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_globalIdentity = new("ANKUS167", "Ambiguous mapped range identity",
        "The constructed range '{0}' must resolve unambiguously through its global qualified name",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// The missing closed range selection diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_missingSelection = new("ANKUS168", "Missing closed range mapping",
        "No valid PgRangeType declaration selects the exact closed scalar bound type '{0}'",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// The unreadable imported range diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_metadata = new("ANKUS205", "Unreadable range mapping metadata",
        "The referenced PgRangeType metadata must contain a complete attribute with exact valid UTF-8 strings; rebuild the defining assembly",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
}
