using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Identifies independently correctable row and event callback declaration contracts.
/// </summary>
internal static class CallbackDeclarationDiagnostics
{
    /// <summary>
    /// Links both callback contracts to their shared declaration guidance.
    /// </summary>
    private const string HelpLink = "https://ankus.net/triggers/#declaration-diagnostics";
    /// <summary>
    /// Stores the error for postgresql callback must be static.
    /// </summary>
    private static readonly DiagnosticDescriptor s_static = new("ANKUS208", "PostgreSQL callback must be static",
        "'{0}' must be static to run as a PostgreSQL callback", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback must be static.
    /// </summary>
    internal static DiagnosticDescriptor Static => s_static;

    /// <summary>
    /// Stores the error for postgresql callback must be synchronous.
    /// </summary>
    private static readonly DiagnosticDescriptor s_async = new("ANKUS209", "PostgreSQL callback must be synchronous",
        "'{0}' must execute synchronously on the PostgreSQL backend thread; async is not supported", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback must be synchronous.
    /// </summary>
    internal static DiagnosticDescriptor Async => s_async;

    /// <summary>
    /// Stores the error for postgresql callback cannot be generic.
    /// </summary>
    private static readonly DiagnosticDescriptor s_generic = new("ANKUS210", "PostgreSQL callback cannot be generic",
        "'{0}' cannot declare method type parameters; use a non-generic callback", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback cannot be generic.
    /// </summary>
    internal static DiagnosticDescriptor Generic => s_generic;

    /// <summary>
    /// Stores the error for postgresql callback requires an implementation.
    /// </summary>
    private static readonly DiagnosticDescriptor s_abstract = new("ANKUS211", "PostgreSQL callback requires an implementation",
        "'{0}' must have a concrete implementation; abstract callbacks cannot be generated", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback requires an implementation.
    /// </summary>
    internal static DiagnosticDescriptor Abstract => s_abstract;

    /// <summary>
    /// Stores the error for postgresql callback must return by value.
    /// </summary>
    private static readonly DiagnosticDescriptor s_refResult = new("ANKUS212", "PostgreSQL callback must return by value",
        "'{0}' must return by value; ref and ref readonly results are not supported", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback must return by value.
    /// </summary>
    internal static DiagnosticDescriptor RefResult => s_refResult;

    /// <summary>
    /// Stores the error for postgresql callback must be accessible.
    /// </summary>
    private static readonly DiagnosticDescriptor s_accessibility = new("ANKUS213", "PostgreSQL callback must be accessible",
        "'{0}' must be public, internal, or protected internal so generated code can call it", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback must be accessible.
    /// </summary>
    internal static DiagnosticDescriptor Accessibility => s_accessibility;

    /// <summary>
    /// Stores the error for invalid postgresql callback result.
    /// </summary>
    private static readonly DiagnosticDescriptor s_result = new("ANKUS214", "Invalid PostgreSQL callback result",
        "'{0}' must return {1}; its declared result is {2}", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for invalid postgresql callback result.
    /// </summary>
    internal static DiagnosticDescriptor Result => s_result;

    /// <summary>
    /// Stores the error for postgresql callback requires one context.
    /// </summary>
    private static readonly DiagnosticDescriptor s_parameterCount = new("ANKUS215", "PostgreSQL callback requires one context",
        "'{0}' must declare exactly one required {1} parameter", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback requires one context.
    /// </summary>
    internal static DiagnosticDescriptor ParameterCount => s_parameterCount;

    /// <summary>
    /// Stores the error for postgresql callback context must be passed by value.
    /// </summary>
    private static readonly DiagnosticDescriptor s_parameterReference = new("ANKUS216", "PostgreSQL callback context must be passed by value",
        "'{0}' must receive {1} by value; ref, in, and out are not supported", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback context must be passed by value.
    /// </summary>
    internal static DiagnosticDescriptor ParameterReference => s_parameterReference;

    /// <summary>
    /// Stores the error for postgresql callback context cannot be variadic.
    /// </summary>
    private static readonly DiagnosticDescriptor s_parameterParams = new("ANKUS217", "PostgreSQL callback context cannot be variadic",
        "'{0}' requires one {1} context; params arrays are not supported", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback context cannot be variadic.
    /// </summary>
    internal static DiagnosticDescriptor ParameterParams => s_parameterParams;

    /// <summary>
    /// Stores the error for postgresql callback context cannot be optional.
    /// </summary>
    private static readonly DiagnosticDescriptor s_parameterOptional = new("ANKUS218", "PostgreSQL callback context cannot be optional",
        "'{0}' requires its {1} context; remove the default value or Optional attribute", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback context cannot be optional.
    /// </summary>
    internal static DiagnosticDescriptor ParameterOptional => s_parameterOptional;

    /// <summary>
    /// Stores the error for postgresql callback context cannot be nullable.
    /// </summary>
    private static readonly DiagnosticDescriptor s_parameterNullable = new("ANKUS219", "PostgreSQL callback context cannot be nullable",
        "'{0}' receives a nonnull {1} context; remove its nullable annotation", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback context cannot be nullable.
    /// </summary>
    internal static DiagnosticDescriptor ParameterNullable => s_parameterNullable;

    /// <summary>
    /// Stores the error for invalid postgresql callback context type.
    /// </summary>
    private static readonly DiagnosticDescriptor s_parameterType = new("ANKUS220", "Invalid PostgreSQL callback context type",
        "'{0}' requires a {1} context parameter; its declared type is {2}", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for invalid postgresql callback context type.
    /// </summary>
    internal static DiagnosticDescriptor ParameterType => s_parameterType;

    /// <summary>
    /// Stores the error for postgresql callback container cannot be generic.
    /// </summary>
    private static readonly DiagnosticDescriptor s_genericContainer = new("ANKUS221", "PostgreSQL callback container cannot be generic",
        "'{0}' must be declared in non-generic types; enclosing type '{1}' is generic", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback container cannot be generic.
    /// </summary>
    internal static DiagnosticDescriptor GenericContainer => s_genericContainer;

    /// <summary>
    /// Stores the error for postgresql callback container cannot be file-local.
    /// </summary>
    private static readonly DiagnosticDescriptor s_fileContainer = new("ANKUS222", "PostgreSQL callback container cannot be file-local",
        "'{0}' cannot be declared in file-local type '{1}'; generated code must access the callback", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback container cannot be file-local.
    /// </summary>
    internal static DiagnosticDescriptor FileContainer => s_fileContainer;

    /// <summary>
    /// Stores the error for postgresql callback container must be accessible.
    /// </summary>
    private static readonly DiagnosticDescriptor s_containerAccessibility = new("ANKUS223", "PostgreSQL callback container must be accessible",
        "'{0}' requires accessible enclosing types; '{1}' must be public, internal, or protected internal", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback container must be accessible.
    /// </summary>
    internal static DiagnosticDescriptor ContainerAccessibility => s_containerAccessibility;

    /// <summary>
    /// Stores the error for conflicting postgresql callback roles.
    /// </summary>
    private static readonly DiagnosticDescriptor s_roleConflict = new("ANKUS224", "Conflicting PostgreSQL callback roles",
        "'{0}' cannot combine {1} with {2}; choose one callback or SQL export role", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for conflicting postgresql callback roles.
    /// </summary>
    internal static DiagnosticDescriptor RoleConflict => s_roleConflict;

    /// <summary>
    /// Stores the error for sql value metadata does not apply to callbacks.
    /// </summary>
    private static readonly DiagnosticDescriptor s_valueMetadata = new("ANKUS225", "SQL value metadata does not apply to callbacks",
        "'{0}' cannot use {1} on its context parameter or result; remove that SQL value metadata", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for sql value metadata does not apply to callbacks.
    /// </summary>
    internal static DiagnosticDescriptor ValueMetadata => s_valueMetadata;

    /// <summary>
    /// Stores the error for postgresql callback cannot declare rows.
    /// </summary>
    private static readonly DiagnosticDescriptor s_rows = new("ANKUS226", "PostgreSQL callback cannot declare Rows",
        "'{0}' is a PostgreSQL callback; Rows applies only to set-returning functions", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback cannot declare rows.
    /// </summary>
    internal static DiagnosticDescriptor Rows => s_rows;

    /// <summary>
    /// Stores the error for postgresql callback cannot declare setmode.
    /// </summary>
    private static readonly DiagnosticDescriptor s_setMode = new("ANKUS227", "PostgreSQL callback cannot declare SetMode",
        "'{0}' is a PostgreSQL callback; SetMode applies only to set-returning functions", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback cannot declare setmode.
    /// </summary>
    internal static DiagnosticDescriptor SetMode => s_setMode;

    /// <summary>
    /// Stores the error for postgresql callback cannot return a task.
    /// </summary>
    private static readonly DiagnosticDescriptor s_taskResult = new("ANKUS228", "PostgreSQL callback cannot return a task",
        "'{0}' cannot return Task or ValueTask; return {1} synchronously on the PostgreSQL backend thread", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback cannot return a task.
    /// </summary>
    internal static DiagnosticDescriptor TaskResult => s_taskResult;

    /// <summary>
    /// Stores the error for postgresql callback cannot return an asynchronous set.
    /// </summary>
    private static readonly DiagnosticDescriptor s_asyncEnumerableResult = new("ANKUS229", "PostgreSQL callback cannot return an asynchronous set",
        "'{0}' cannot return IAsyncEnumerable; return {1} directly on the PostgreSQL backend thread", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for postgresql callback cannot return an asynchronous set.
    /// </summary>
    internal static DiagnosticDescriptor AsyncEnumerableResult => s_asyncEnumerableResult;

}
