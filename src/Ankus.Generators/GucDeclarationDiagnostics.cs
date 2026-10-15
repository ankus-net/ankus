using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Identifies independently correctable configuration contracts.
/// </summary>
internal static class GucDeclarationDiagnostics
{
    private const string HelpLink = "https://ankus.net/configuration/#declaration-diagnostics";

    private static readonly DiagnosticDescriptor s_attributeCount = new("ANKUS169", "Conflicting GUC declarations",
        "A property must declare exactly one typed GUC attribute",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the conflicting typed attributes diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor AttributeCount => s_attributeCount;

    private static readonly DiagnosticDescriptor s_static = new("ANKUS170", "GUC property must be static",
        "A GUC property must be static",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the instance property diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Static => s_static;

    private static readonly DiagnosticDescriptor s_accessors = new("ANKUS171", "GUC requires a getter-only property",
        "A GUC property must have a getter and no setter or initializer",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid accessor shape diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Accessors => s_accessors;

    private static readonly DiagnosticDescriptor s_reference = new("ANKUS172", "GUC cannot return by reference",
        "A GUC property cannot return by reference",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the reference-valued property diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Reference => s_reference;

    private static readonly DiagnosticDescriptor s_partial = new("ANKUS173", "GUC property must be partial",
        "A GUC property must be a partial definition",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the non-partial property diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Partial => s_partial;

    private static readonly DiagnosticDescriptor s_access = new("ANKUS174", "Inaccessible GUC property",
        "A GUC property must be public, internal, or protected internal",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the inaccessible property diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Access => s_access;

    private static readonly DiagnosticDescriptor s_implementation = new("ANKUS175", "GUC property has an implementation",
        "Remove the existing property implementation; Ankus generates its native-backed getter",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the authored implementation diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Implementation => s_implementation;

    private static readonly DiagnosticDescriptor s_containerKind = new("ANKUS176", "GUC requires a class",
        "Every containing type of a GUC property must be a class",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the unsupported container kind diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor ContainerKind => s_containerKind;

    private static readonly DiagnosticDescriptor s_genericContainer = new("ANKUS177", "GUC container has type parameters",
        "Every containing class of a GUC property must be non-generic",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the generic container diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor GenericContainer => s_genericContainer;

    private static readonly DiagnosticDescriptor s_fileContainer = new("ANKUS178", "GUC container is file-local",
        "A GUC property cannot be declared in a file-local class",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the file-local container diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor FileContainer => s_fileContainer;

    private static readonly DiagnosticDescriptor s_containerAccess = new("ANKUS179", "Inaccessible GUC container",
        "Every containing class of a GUC property must be public, internal, or protected internal",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the inaccessible container diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor ContainerAccess => s_containerAccess;

    private static readonly DiagnosticDescriptor s_containerPartial = new("ANKUS180", "GUC container must be partial",
        "Every declaration of every containing class of a GUC property must be partial",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the non-partial container diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor ContainerPartial => s_containerPartial;

    private static readonly DiagnosticDescriptor s_name = new("ANKUS181", "Invalid custom setting name",
        "The GUC name must be a valid dotted custom setting name with valid Unicode and no zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid custom setting name diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Name => s_name;

    private static readonly DiagnosticDescriptor s_description = new("ANKUS182", "Invalid GUC description",
        "The short description must be nonnull valid text without zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid short description diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Description => s_description;

    private static readonly DiagnosticDescriptor s_type = new("ANKUS183", "GUC attribute and property types differ",
        "The property type must match the '{0}' attribute exactly",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the incompatible property type diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Type => s_type;

    private static readonly DiagnosticDescriptor s_longDescription = new("ANKUS184", "Invalid GUC long description",
        "LongDescription must be valid text without zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid long description diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor LongDescription => s_longDescription;

    private static readonly DiagnosticDescriptor s_context = new("ANKUS185", "Invalid GUC context",
        "Context must be one of the seven declared PgGucContext values",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the undefined native context diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Context => s_context;

    private static readonly DiagnosticDescriptor s_flags = new("ANKUS186", "Invalid GUC flags",
        "Flags must contain only declared PgGucOptions values",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the undefined flag bits diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Flags => s_flags;

    private static readonly DiagnosticDescriptor s_unit = new("ANKUS187", "Invalid GUC unit",
        "Unit must be a declared PgGucUnit value",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the undefined native unit diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Unit => s_unit;

    private static readonly DiagnosticDescriptor s_nameFlag = new("ANKUS188", "Identifier GUC requires a string",
        "The IsName flag requires a string GUC property",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the identifier flag on a non-string setting diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor NameFlag => s_nameFlag;

    private static readonly DiagnosticDescriptor s_unitKind = new("ANKUS189", "GUC units require a numeric setting",
        "A non-default Unit requires an integer or real GUC property",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the unit on a non-numeric setting diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor UnitKind => s_unitKind;

    private static readonly DiagnosticDescriptor s_integerBounds = new("ANKUS190", "Reversed integer GUC bounds",
        "Integer bounds must satisfy Minimum <= Maximum",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the reversed integer bounds diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor IntegerBounds => s_integerBounds;

    private static readonly DiagnosticDescriptor s_integerDefault = new("ANKUS191", "Integer GUC default exceeds bounds",
        "The integer default must satisfy Minimum <= Default <= Maximum",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the out-of-bounds integer default diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor IntegerDefault => s_integerDefault;

    private static readonly DiagnosticDescriptor s_realBounds = new("ANKUS192", "Invalid real GUC bounds",
        "Real bounds must satisfy Minimum <= Maximum and cannot contain NaN",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid real bounds diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor RealBounds => s_realBounds;

    private static readonly DiagnosticDescriptor s_realDefault = new("ANKUS193", "Invalid real GUC default",
        "The real default must satisfy Minimum <= Default <= Maximum and cannot be NaN",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid real default diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor RealDefault => s_realDefault;

    private static readonly DiagnosticDescriptor s_stringDefault = new("ANKUS194", "Invalid string GUC default",
        "The string default must be valid text without zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid string default diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor StringDefault => s_stringDefault;

    private static readonly DiagnosticDescriptor s_nullableDefault = new("ANKUS195", "Null GUC default requires nullable string",
        "A null default requires a nullable string property",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the null default on a non-nullable string diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor NullableDefault => s_nullableDefault;

    private static readonly DiagnosticDescriptor s_enumDefault = new("ANKUS196", "Invalid enum GUC default",
        "The enum default must be a declared value of the property's enum type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid enum boot value diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor EnumDefault => s_enumDefault;

    private static readonly DiagnosticDescriptor s_enumLabel = new("ANKUS197", "Invalid GUC enum label",
        "An enum label must be nonnull valid text without zero characters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid enum label diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor EnumLabel => s_enumLabel;

    private static readonly DiagnosticDescriptor s_duplicateLabel = new("ANKUS198", "Duplicate GUC enum label",
        "Enum labels must be distinct under PostgreSQL's ASCII case-insensitive comparison",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the duplicate enum label diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor DuplicateLabel => s_duplicateLabel;

    private static readonly DiagnosticDescriptor s_check = new("ANKUS199", "Invalid GUC check hook",
        "Check must name one accessible synchronous non-generic static method returning PgGucCheckResult<{0}> and taking ({0}, PgGucSource); {1}",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid typed check hook diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Check => s_check;

    private static readonly DiagnosticDescriptor s_assign = new("ANKUS200", "Invalid GUC assignment hook",
        "Assign must name one accessible synchronous non-generic static void method taking ({0}, PgGucExtra?); {1}",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid typed assignment hook diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Assign => s_assign;

    private static readonly DiagnosticDescriptor s_show = new("ANKUS201", "Invalid GUC display hook",
        "Show must name one accessible synchronous non-generic static method returning non-nullable string and taking ({0}, PgGucExtra?); {1}",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the invalid typed display hook diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Show => s_show;

    private static readonly DiagnosticDescriptor s_duplicateName = new("ANKUS202", "Duplicate custom setting name",
        "The GUC name '{0}' conflicts with an earlier declaration under PostgreSQL's ASCII case-insensitive comparison",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the duplicate native setting name diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor DuplicateName => s_duplicateName;

    private static readonly DiagnosticDescriptor s_indexer = new("ANKUS203", "GUC cannot be an indexer",
        "A GUC must be a named property without index parameters",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the indexed property diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Indexer => s_indexer;
}
