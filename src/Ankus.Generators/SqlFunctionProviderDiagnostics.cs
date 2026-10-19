using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Reports independently correctable function-provider declaration contracts.
/// </summary>
internal static class SqlFunctionProviderDiagnostics
{
    /// <summary>
    /// Requires a nonempty custom SQL dependency identifier.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_emptyBlock = new(
        "ANKUS370", "Function provider requires a SQL block identifier",
        "Supply a nonnull SQL block identifier containing at least one non-whitespace character",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#function-provider-diagnostics");

    /// <summary>
    /// Rejects zero characters in the exact custom SQL dependency identifier.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_blockZero = new(
        "ANKUS371", "Function provider block identifier cannot contain zero characters",
        "Remove embedded zero characters from the function provider's SQL block identifier",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#function-provider-diagnostics");

    /// <summary>
    /// Requires lossless Unicode in the exact custom SQL dependency identifier.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_blockUnicode = new(
        "ANKUS372", "Function provider block identifier requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in the function provider's SQL block identifier",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#function-provider-diagnostics");

    /// <summary>
    /// Requires a successfully resolved custom SQL block; another declaration's alias does not qualify.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_missingBlock = new(
        "ANKUS373", "Function provider must name a custom SQL block",
        "SQL block '{0}' does not name an existing PgSql or PgSqlFile block; declare that block or correct sqlId",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#function-provider-diagnostics");

    /// <summary>
    /// Requires a nonempty authored SQL function identity.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_emptySignature = new(
        "ANKUS374", "Function provider requires a SQL signature",
        "Supply a nonnull SQL function signature containing at least one non-whitespace character, including its argument types",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#function-provider-diagnostics");

    /// <summary>
    /// Rejects zero characters in the authored SQL function identity.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_signatureZero = new(
        "ANKUS375", "Function provider signature cannot contain zero characters",
        "Remove embedded zero characters from the function provider's SQL signature",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#function-provider-diagnostics");

    /// <summary>
    /// Requires lossless Unicode in the authored SQL function identity.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_signatureUnicode = new(
        "ANKUS376", "Function provider signature requires well-formed Unicode",
        "Replace unpaired UTF-16 surrogate characters in the function provider's SQL signature; Ankus preserves exact text without replacement",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#function-provider-diagnostics");

    /// <summary>
    /// Rejects a second claim for the same exact authored function identity.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicateSignature = new(
        "ANKUS377", "SQL function signature has multiple providers",
        "SQL function signature '{0}' may have only one custom provider; remove the duplicate claim or select a distinct overload",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/custom-sql/#function-provider-diagnostics");
}
