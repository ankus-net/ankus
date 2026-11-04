using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Describes separately correctable custom scan provider contracts.
/// </summary>
internal static class CustomScanDiagnostics
{
    /// <summary>
    /// Links errors to extension-author custom scan guidance.
    /// </summary>
    private const string HelpLink = "https://ankus.net/custom-scans/#managed-providers";

    /// <summary>
    /// Stores the error for a provider without exactly one state contract.
    /// </summary>
    /// <summary>
    /// The missing or ambiguous state contract diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_contract = new("ANKUS535", "Custom scan provider must implement IPgCustomScan",
        "'{0}' must implement IPgCustomScan<TState> for exactly one state type; {1}", "Ankus", DiagnosticSeverity.Error,
        isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for a name PostgreSQL cannot register.
    /// </summary>
    /// <summary>
    /// The invalid name diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_name = new("ANKUS536", "Invalid custom scan name",
        "'{0}' is not a valid custom scan name; use 1 to 63 ASCII letters, digits or underscores, starting with a letter or underscore",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for a name another provider already registers.
    /// </summary>
    /// <summary>
    /// The duplicate name diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicate = new("ANKUS537", "Duplicate custom scan name",
        "Custom scan name '{0}' is already registered by '{1}'; PostgreSQL keeps one provider per name in a backend", "Ankus",
        DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for a provider generated code cannot reach.
    /// </summary>
    /// <summary>
    /// The unreachable provider diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_container = new("ANKUS538", "Invalid custom scan provider",
        "'{0}' cannot provide a custom scan; providers belong to non-generic, non-file-local public or internal classes", "Ankus",
        DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for a parallel provider with more than one shared memory layout.
    /// </summary>
    /// <summary>
    /// The ambiguous shared memory layout diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_shared = new("ANKUS540", "Parallel custom scan needs one shared memory layout",
        "'{0}' implements IPgParallelCustomScan<TState, TShared> for {1} shared memory types; a parallel scan has one", "Ankus",
        DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for an asynchronous provider callback.
    /// </summary>
    /// <summary>
    /// The asynchronous callback diagnostic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_async = new("ANKUS539", "Custom scan callback must be synchronous",
        "'{0}' cannot use async; custom scan callbacks run on the PostgreSQL backend thread and must finish before returning", "Ankus",
        DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
}
