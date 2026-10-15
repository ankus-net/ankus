using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Reports independently correctable catalog and managed type-provider contracts.
/// </summary>
internal static class SqlTypeProviderDiagnostics
{
    /// <summary>
    /// Requires a nonempty custom SQL dependency identifier.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_emptyBlock = new(
        "ANKUS378", "Type provider requires a SQL block identifier",
        "Supply a nonnull SQL block identifier containing at least one non-whitespace character",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Rejects zero characters in the exact custom SQL dependency identifier.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_blockZero = new(
        "ANKUS379", "Type provider block identifier cannot contain zero characters",
        "Remove embedded zero characters from the type provider's SQL block identifier",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Requires lossless Unicode in the exact custom SQL dependency identifier.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_blockUnicode = new(
        "ANKUS380", "Type provider block identifier requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in the type provider's SQL block identifier",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Requires a resolved custom SQL block rather than another declaration alias.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_missingBlock = new(
        "ANKUS381", "Type provider must name a custom SQL block",
        "Type provider '{0}' must name a PgSql or PgSqlFile block; declare that block or correct sqlId",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Requires a registered closed managed datum identity.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_unregisteredManaged = new(
        "ANKUS382", "Managed type provider requires a registered datum mapping",
        "A managed type provider must name a registered PgDatumType mapping or registered PgRange<T> identity",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Rejects an extension provider for a catalog type owned elsewhere.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_externalManaged = new(
        "ANKUS383", "External datum mapping cannot have an extension provider",
        "External datum mappings cannot have an extension type provider; remove this provider for '{0}' or declare extension ownership",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Preserves the schema supplied by the registered managed mapping.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_managedSchema = new(
        "ANKUS384", "Managed type provider cannot override its mapping schema",
        "A managed type provider obtains its schema from PgDatumType and cannot specify Schema; remove the authored Schema assignment, including null",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Requires a nonempty exact catalog type name.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_emptyName = new(
        "ANKUS385", "Type provider requires a catalog name",
        "Supply a nonnull, nonempty unquoted catalog type name",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Rejects zero characters in the exact catalog type name.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_nameZero = new(
        "ANKUS386", "Type provider name cannot contain zero characters",
        "Remove embedded zero characters from the provider's catalog type name",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Requires lossless Unicode in the exact catalog type name.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_nameUnicode = new(
        "ANKUS387", "Type provider name requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in the provider's catalog type name",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Rejects catalog type names PostgreSQL would truncate.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_nameLength = new(
        "ANKUS388", "Type provider name exceeds the PostgreSQL identifier limit",
        "Shorten the provider's catalog type name to at most 63 UTF-8 bytes; Ankus does not silently truncate it",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Rejects an explicitly empty fixed schema while retaining null as unqualified.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_emptySchema = new(
        "ANKUS389", "Type provider schema cannot be empty",
        "Supply a nonempty unquoted schema name, or use null for an unqualified catalog type",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Rejects zero characters in the exact fixed schema name.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_schemaZero = new(
        "ANKUS390", "Type provider schema cannot contain zero characters",
        "Remove embedded zero characters from the provider's schema name",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Requires lossless Unicode in the exact fixed schema name.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_schemaUnicode = new(
        "ANKUS391", "Type provider schema requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in the provider's schema name",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Rejects fixed schema names PostgreSQL would truncate.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_schemaLength = new(
        "ANKUS392", "Type provider schema exceeds the PostgreSQL identifier limit",
        "Shorten the provider's schema name to at most 63 UTF-8 bytes; Ankus does not silently truncate it",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Rejects a repeated provider claim for one closed managed datum identity.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicateManaged = new(
        "ANKUS393", "Managed datum type has multiple providers",
        "Managed datum type '{0}' has more than one provider; retain exactly one provider naming this managed identity",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Preserves unique catalog ownership and generated type reservations.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicateCatalog = new(
        "ANKUS394", "PostgreSQL type has multiple providers",
        "PostgreSQL type {0} has more than one provider, including generated type or enum declarations; retain one custom block or the generated declaration",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");

    /// <summary>
    /// Requires an owned closed mapping to have an exact managed provider.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_missingManaged = new(
        "ANKUS395", "Extension-owned datum mapping requires a managed provider",
        "Managed datum type '{0}' requires a PgSqlTypeProvider naming its managed identity; a catalog-name provider does not satisfy this requirement",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#type-provider-diagnostics");
}
