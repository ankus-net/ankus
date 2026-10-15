using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Describes separately correctable logical decoding output plugin initializer contracts.
/// </summary>
internal static class OutputPluginDiagnostics
{
    /// <summary>
    /// Links errors to extension-author output plugin guidance.
    /// </summary>
    private const string HelpLink = "https://ankus.net/logical-decoding/#declaration-diagnostics";

    /// <summary>
    /// Stores the error for an initializer that is not an ordinary method.
    /// </summary>
    private static readonly DiagnosticDescriptor s_kind = new("ANKUS515", "Output plugin initializer requires an ordinary method",
        "'{0}' must be an ordinary named method; local functions, lambdas, and explicit interface implementations cannot initialize an output plugin",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for an instance initializer.
    /// </summary>
    private static readonly DiagnosticDescriptor s_static = new("ANKUS516", "Output plugin initializer must be static",
        "'{0}' must be static so PostgreSQL can invoke it without an instance", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for an asynchronous initializer.
    /// </summary>
    private static readonly DiagnosticDescriptor s_async = new("ANKUS517", "Output plugin initializer must be synchronous",
        "'{0}' must assign its callbacks synchronously on the PostgreSQL backend thread; remove async", "Ankus", DiagnosticSeverity.Error,
        isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for a generic initializer.
    /// </summary>
    private static readonly DiagnosticDescriptor s_generic = new("ANKUS518", "Output plugin initializer cannot be generic",
        "'{0}' cannot declare method type parameters", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for an initializer without a managed body.
    /// </summary>
    private static readonly DiagnosticDescriptor s_implementation = new("ANKUS519", "Output plugin initializer requires a managed implementation",
        "'{0}' must be a concrete, non-virtual method with a managed body; abstract, virtual, extern and unimplemented partial methods cannot be exported",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for a value-returning initializer.
    /// </summary>
    private static readonly DiagnosticDescriptor s_result = new("ANKUS520", "Output plugin initializer must return void",
        "'{0}' returns '{1}'; _PG_output_plugin_init returns nothing", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for an initializer without exactly one callback-table parameter.
    /// </summary>
    private static readonly DiagnosticDescriptor s_parameter = new("ANKUS521", "Output plugin initializer requires the callback table",
        "'{0}' must declare exactly one by-value Ankus.Postgres.OutputPluginCallbacks* parameter from the generated native bindings",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for an initializer that generated code cannot call.
    /// </summary>
    private static readonly DiagnosticDescriptor s_accessibility = new("ANKUS522", "Output plugin initializer must be accessible",
        "'{0}' must be public, internal or protected internal so the generated export can call it", "Ankus", DiagnosticSeverity.Error,
        isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for an initializer inside a container that generated code cannot name.
    /// </summary>
    private static readonly DiagnosticDescriptor s_container = new("ANKUS523", "Output plugin container must be accessible",
        "'{0}' is declared in '{1}', which must be a non-generic, non-file-local type that is public, internal or protected internal",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for an initializer that only native code may call.
    /// </summary>
    private static readonly DiagnosticDescriptor s_unmanagedOnly = new("ANKUS524", "Output plugin initializer must support managed invocation",
        "'{0}' must be callable from generated managed code; remove UnmanagedCallersOnly", "Ankus", DiagnosticSeverity.Error,
        isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for an initializer that also declares another PostgreSQL role.
    /// </summary>
    private static readonly DiagnosticDescriptor s_role = new("ANKUS525", "Output plugin initializer has a conflicting role",
        "'{0}' cannot combine an output plugin initializer with {1}; declare a separate method", "Ankus", DiagnosticSeverity.Error,
        isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for a second initializer in one extension.
    /// </summary>
    private static readonly DiagnosticDescriptor s_duplicate = new("ANKUS526", "Duplicate output plugin initializer",
        "'{0}' duplicates output plugin initializer '{1}'; a library exports one _PG_output_plugin_init", "Ankus", DiagnosticSeverity.Error,
        isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for an initializer that is not an ordinary method.
    /// </summary>
    internal static DiagnosticDescriptor Kind => s_kind;

    /// <summary>
    /// Gets the error for an instance initializer.
    /// </summary>
    internal static DiagnosticDescriptor Static => s_static;

    /// <summary>
    /// Gets the error for an asynchronous initializer.
    /// </summary>
    internal static DiagnosticDescriptor Async => s_async;

    /// <summary>
    /// Gets the error for a generic initializer.
    /// </summary>
    internal static DiagnosticDescriptor Generic => s_generic;

    /// <summary>
    /// Gets the error for an initializer without a managed body.
    /// </summary>
    internal static DiagnosticDescriptor Implementation => s_implementation;

    /// <summary>
    /// Gets the error for a value-returning initializer.
    /// </summary>
    internal static DiagnosticDescriptor Result => s_result;

    /// <summary>
    /// Gets the error for an initializer without exactly one callback-table parameter.
    /// </summary>
    internal static DiagnosticDescriptor Parameter => s_parameter;

    /// <summary>
    /// Gets the error for an initializer that generated code cannot call.
    /// </summary>
    internal static DiagnosticDescriptor Accessibility => s_accessibility;

    /// <summary>
    /// Gets the error for an initializer inside a container that generated code cannot name.
    /// </summary>
    internal static DiagnosticDescriptor Container => s_container;

    /// <summary>
    /// Gets the error for an initializer that only native code may call.
    /// </summary>
    internal static DiagnosticDescriptor UnmanagedOnly => s_unmanagedOnly;

    /// <summary>
    /// Gets the error for an initializer that also declares another PostgreSQL role.
    /// </summary>
    internal static DiagnosticDescriptor Role => s_role;

    /// <summary>
    /// Gets the error for a second initializer in one extension.
    /// </summary>
    internal static DiagnosticDescriptor Duplicate => s_duplicate;
}
