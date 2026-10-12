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
    private const string HelpLink = "https://willibrandon.github.io/ankus/custom-scans/#managed-providers";

    /// <summary>
    /// Stores the error for a provider without exactly one state contract.
    /// </summary>
    private static readonly DiagnosticDescriptor s_contract = new("ANKUS535", "Custom scan provider must implement IPgCustomScan",
        "'{0}' must implement IPgCustomScan<TState> for exactly one state type; {1}", "Ankus", DiagnosticSeverity.Error,
        isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for a name PostgreSQL cannot register.
    /// </summary>
    private static readonly DiagnosticDescriptor s_name = new("ANKUS536", "Invalid custom scan name",
        "'{0}' is not a valid custom scan name; use 1 to 63 ASCII letters, digits or underscores, starting with a letter or underscore",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for a name another provider already registers.
    /// </summary>
    private static readonly DiagnosticDescriptor s_duplicate = new("ANKUS537", "Duplicate custom scan name",
        "Custom scan name '{0}' is already registered by '{1}'; PostgreSQL keeps one provider per name in a backend", "Ankus",
        DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for a provider generated code cannot reach.
    /// </summary>
    private static readonly DiagnosticDescriptor s_container = new("ANKUS538", "Invalid custom scan provider",
        "'{0}' cannot provide a custom scan; providers belong to non-generic, non-file-local public or internal classes", "Ankus",
        DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for an asynchronous provider callback.
    /// </summary>
    private static readonly DiagnosticDescriptor s_async = new("ANKUS539", "Custom scan callback must be synchronous",
        "'{0}' cannot use async; custom scan callbacks run on the PostgreSQL backend thread and must finish before returning", "Ankus",
        DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the missing or ambiguous state contract diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Contract => s_contract;

    /// <summary>
    /// Gets the invalid name diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Name => s_name;

    /// <summary>
    /// Gets the duplicate name diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Duplicate => s_duplicate;

    /// <summary>
    /// Gets the unreachable provider diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Container => s_container;

    /// <summary>
    /// Gets the asynchronous callback diagnostic.
    /// </summary>
    internal static DiagnosticDescriptor Async => s_async;
}
