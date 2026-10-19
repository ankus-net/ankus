using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Reports independently correctable PostgreSQL enum declaration contracts.
/// </summary>
internal static class EnumDeclarationDiagnostics
{
    /// <summary>
    /// Links enum declaration failures to their extension-author guidance.
    /// </summary>
    private const string HelpLink = "https://ankus.net/enums/#declaration-diagnostics";

    /// <summary>
    /// Stores the error for PostgreSQL enum requires an enum declaration.
    /// </summary>
    private static readonly DiagnosticDescriptor s_kind = new("ANKUS278", "PostgreSQL enum requires an enum declaration",
        "'{0}' must be an enum declaration to use PgEnum", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL enum requires an enum declaration.
    /// </summary>
    internal static DiagnosticDescriptor Kind => s_kind;

    /// <summary>
    /// Stores the error for PostgreSQL enum cannot use Flags.
    /// </summary>
    private static readonly DiagnosticDescriptor s_flags = new("ANKUS279", "PostgreSQL enum cannot use Flags",
        "'{0}' must be declared without Flags; PostgreSQL labels are individual values that cannot combine as bit masks", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL enum cannot use Flags.
    /// </summary>
    internal static DiagnosticDescriptor Flags => s_flags;

    /// <summary>
    /// Stores the error for PostgreSQL enum cannot have a generic container.
    /// </summary>
    private static readonly DiagnosticDescriptor s_genericContainer = new("ANKUS280", "PostgreSQL enum cannot have a generic container",
        "Enum '{0}' cannot be nested in generic type '{1}'; move it to a non-generic container", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL enum cannot have a generic container.
    /// </summary>
    internal static DiagnosticDescriptor GenericContainer => s_genericContainer;

    /// <summary>
    /// Stores the error for PostgreSQL enum cannot be file-local.
    /// </summary>
    private static readonly DiagnosticDescriptor s_fileContainer = new("ANKUS281", "PostgreSQL enum cannot be file-local",
        "Enum '{0}' and containing type '{1}' cannot be file-local; generated registrations must access them from another file", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL enum cannot be file-local.
    /// </summary>
    internal static DiagnosticDescriptor FileContainer => s_fileContainer;

    /// <summary>
    /// Stores the error for PostgreSQL enum must be accessible.
    /// </summary>
    private static readonly DiagnosticDescriptor s_accessibility = new("ANKUS282", "PostgreSQL enum must be accessible",
        "Enum '{0}' and containing type '{1}' must be public, internal or protected internal so generated registrations can access them", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL enum must be accessible.
    /// </summary>
    internal static DiagnosticDescriptor Accessibility => s_accessibility;

    /// <summary>
    /// Stores the error for Invalid PostgreSQL enum type name.
    /// </summary>
    private static readonly DiagnosticDescriptor s_name = new("ANKUS283", "Invalid PostgreSQL enum type name",
        "Enum '{0}' must have a nonempty SQL type name of at most 63 UTF-8 bytes, with valid Unicode and no zero characters", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for Invalid PostgreSQL enum type name.
    /// </summary>
    internal static DiagnosticDescriptor Name => s_name;

    /// <summary>
    /// Stores the error for Invalid PostgreSQL enum schema.
    /// </summary>
    private static readonly DiagnosticDescriptor s_schema = new("ANKUS284", "Invalid PostgreSQL enum schema",
        "Enum '{0}' must have a nonempty SQL schema name of at most 63 UTF-8 bytes, with valid Unicode and no zero characters", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for Invalid PostgreSQL enum schema.
    /// </summary>
    internal static DiagnosticDescriptor Schema => s_schema;

    /// <summary>
    /// Stores the error for PostgreSQL enum cannot inherit a null schema.
    /// </summary>
    private static readonly DiagnosticDescriptor s_inheritedSchema = new("ANKUS285", "PostgreSQL enum cannot inherit a null schema",
        "Enum '{0}' cannot inherit a null schema; give PgSchema a non-null identifier", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL enum cannot inherit a null schema.
    /// </summary>
    internal static DiagnosticDescriptor InheritedSchema => s_inheritedSchema;

    /// <summary>
    /// Stores the error for PostgreSQL enum label cannot be null.
    /// </summary>
    private static readonly DiagnosticDescriptor s_nullLabel = new("ANKUS286", "PostgreSQL enum label cannot be null",
        "Enum '{0}' member '{1}' requires a non-null label; an empty label is allowed", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL enum label cannot be null.
    /// </summary>
    internal static DiagnosticDescriptor NullLabel => s_nullLabel;

    /// <summary>
    /// Stores the error for Invalid PostgreSQL enum label text.
    /// </summary>
    private static readonly DiagnosticDescriptor s_labelText = new("ANKUS287", "Invalid PostgreSQL enum label text",
        "Enum '{0}' member '{1}' requires valid Unicode without zero characters in its label", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for Invalid PostgreSQL enum label text.
    /// </summary>
    internal static DiagnosticDescriptor LabelText => s_labelText;

    /// <summary>
    /// Stores the error for PostgreSQL enum label exceeds UTF-8 limit.
    /// </summary>
    private static readonly DiagnosticDescriptor s_labelLength = new("ANKUS288", "PostgreSQL enum label exceeds UTF-8 limit",
        "Enum '{0}' member '{1}' must have a label of at most 63 UTF-8 bytes; shorten the label", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL enum label exceeds UTF-8 limit.
    /// </summary>
    internal static DiagnosticDescriptor LabelLength => s_labelLength;

    /// <summary>
    /// Stores the error for Duplicate PostgreSQL enum label.
    /// </summary>
    private static readonly DiagnosticDescriptor s_duplicateLabel = new("ANKUS289", "Duplicate PostgreSQL enum label",
        "Enum '{0}' member '{1}' repeats an earlier label; labels must be distinct", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for Duplicate PostgreSQL enum label.
    /// </summary>
    internal static DiagnosticDescriptor DuplicateLabel => s_duplicateLabel;

    /// <summary>
    /// Stores the error for PostgreSQL enum cannot contain numeric aliases.
    /// </summary>
    private static readonly DiagnosticDescriptor s_numericAlias = new("ANKUS290", "PostgreSQL enum cannot contain numeric aliases",
        "Enum '{0}' member '{1}' repeats an earlier numeric value; members must have distinct numeric values", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL enum cannot contain numeric aliases.
    /// </summary>
    internal static DiagnosticDescriptor NumericAlias => s_numericAlias;
}
