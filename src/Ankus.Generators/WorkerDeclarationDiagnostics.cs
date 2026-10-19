using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Describes separately correctable background-worker declaration contracts.
/// </summary>
internal static class WorkerDeclarationDiagnostics
{
    /// <summary>
    /// Links errors to extension-author worker declaration guidance.
    /// </summary>
    private const string HelpLink = "https://ankus.net/background-workers/#declaration-diagnostics";
    /// <summary>
    /// Stores the error for worker entry requires an ordinary method.
    /// </summary>
    private static readonly DiagnosticDescriptor s_kind = new("ANKUS250", "Worker entry requires an ordinary method",
        "'{0}' must be an ordinary named method; local functions, lambdas, and explicit interface implementations cannot be worker entries", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry requires an ordinary method.
    /// </summary>
    internal static DiagnosticDescriptor Kind => s_kind;

    /// <summary>
    /// Stores the error for worker entry must be static.
    /// </summary>
    private static readonly DiagnosticDescriptor s_static = new("ANKUS251", "Worker entry must be static",
        "'{0}' must be static so PostgreSQL can invoke it without an instance", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry must be static.
    /// </summary>
    internal static DiagnosticDescriptor Static => s_static;

    /// <summary>
    /// Stores the error for worker entry must be synchronous.
    /// </summary>
    private static readonly DiagnosticDescriptor s_async = new("ANKUS252", "Worker entry must be synchronous",
        "'{0}' must execute synchronously on the PostgreSQL backend thread; remove async", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry must be synchronous.
    /// </summary>
    internal static DiagnosticDescriptor Async => s_async;

    /// <summary>
    /// Stores the error for worker entry cannot be generic.
    /// </summary>
    private static readonly DiagnosticDescriptor s_generic = new("ANKUS253", "Worker entry cannot be generic",
        "'{0}' cannot declare method type parameters", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry cannot be generic.
    /// </summary>
    internal static DiagnosticDescriptor Generic => s_generic;

    /// <summary>
    /// Stores the error for worker entry requires a concrete method.
    /// </summary>
    private static readonly DiagnosticDescriptor s_abstract = new("ANKUS254", "Worker entry requires a concrete method",
        "'{0}' cannot be abstract; provide a concrete implementation", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry requires a concrete method.
    /// </summary>
    internal static DiagnosticDescriptor Abstract => s_abstract;

    /// <summary>
    /// Stores the error for worker entry cannot be virtual.
    /// </summary>
    private static readonly DiagnosticDescriptor s_virtual = new("ANKUS255", "Worker entry cannot be virtual",
        "'{0}' cannot be virtual; provide a concrete non-virtual static callback", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry cannot be virtual.
    /// </summary>
    internal static DiagnosticDescriptor Virtual => s_virtual;

    /// <summary>
    /// Stores the error for worker entry requires managed implementation.
    /// </summary>
    private static readonly DiagnosticDescriptor s_extern = new("ANKUS256", "Worker entry requires managed implementation",
        "'{0}' cannot be extern; provide a managed implementation", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry requires managed implementation.
    /// </summary>
    internal static DiagnosticDescriptor Extern => s_extern;

    /// <summary>
    /// Stores the error for worker entry must return void.
    /// </summary>
    private static readonly DiagnosticDescriptor s_result = new("ANKUS257", "Worker entry must return void",
        "'{0}' must return void; its declared result is {1}", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry must return void.
    /// </summary>
    internal static DiagnosticDescriptor Result => s_result;

    /// <summary>
    /// Stores the error for worker entry requires one argument.
    /// </summary>
    private static readonly DiagnosticDescriptor s_parameterCount = new("ANKUS258", "Worker entry requires one argument",
        "'{0}' must declare exactly one nuint parameter for PostgreSQL's Datum argument", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry requires one argument.
    /// </summary>
    internal static DiagnosticDescriptor ParameterCount => s_parameterCount;

    /// <summary>
    /// Stores the error for worker argument must be passed by value.
    /// </summary>
    private static readonly DiagnosticDescriptor s_parameterRefKind = new("ANKUS259", "Worker argument must be passed by value",
        "'{0}' parameter '{1}' cannot use ref, in, or out; PostgreSQL passes its Datum by value", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker argument must be passed by value.
    /// </summary>
    internal static DiagnosticDescriptor ParameterRefKind => s_parameterRefKind;

    /// <summary>
    /// Stores the error for worker argument must be a native unsigned integer.
    /// </summary>
    private static readonly DiagnosticDescriptor s_parameterType = new("ANKUS260", "Worker argument must be a native unsigned integer",
        "'{0}' must accept nuint or System.UIntPtr; its declared parameter type is {1}", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker argument must be a native unsigned integer.
    /// </summary>
    internal static DiagnosticDescriptor ParameterType => s_parameterType;

    /// <summary>
    /// Stores the error for worker entry must be accessible.
    /// </summary>
    private static readonly DiagnosticDescriptor s_accessibility = new("ANKUS261", "Worker entry must be accessible",
        "'{0}' must be public, internal, or protected internal so generated code can invoke it", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry must be accessible.
    /// </summary>
    internal static DiagnosticDescriptor Accessibility => s_accessibility;

    /// <summary>
    /// Stores the error for partial worker entry requires implementation.
    /// </summary>
    private static readonly DiagnosticDescriptor s_implementation = new("ANKUS262", "Partial worker entry requires implementation",
        "'{0}' requires a partial method implementation; an unimplemented definition cannot start a worker", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for partial worker entry requires implementation.
    /// </summary>
    internal static DiagnosticDescriptor Implementation => s_implementation;

    /// <summary>
    /// Stores the error for worker container cannot be generic.
    /// </summary>
    private static readonly DiagnosticDescriptor s_genericContainer = new("ANKUS263", "Worker container cannot be generic",
        "'{0}' must be declared in non-generic types; enclosing type '{1}' is generic", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker container cannot be generic.
    /// </summary>
    internal static DiagnosticDescriptor GenericContainer => s_genericContainer;

    /// <summary>
    /// Stores the error for worker container cannot be file-local.
    /// </summary>
    private static readonly DiagnosticDescriptor s_fileContainer = new("ANKUS264", "Worker container cannot be file-local",
        "'{0}' cannot be declared in file-local type '{1}'", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker container cannot be file-local.
    /// </summary>
    internal static DiagnosticDescriptor FileContainer => s_fileContainer;

    /// <summary>
    /// Stores the error for worker container must be accessible.
    /// </summary>
    private static readonly DiagnosticDescriptor s_containerAccessibility = new("ANKUS265", "Worker container must be accessible",
        "'{0}' requires accessible enclosing types; '{1}' must be public, internal, or protected internal", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker container must be accessible.
    /// </summary>
    internal static DiagnosticDescriptor ContainerAccessibility => s_containerAccessibility;

    /// <summary>
    /// Stores the error for worker entry cannot be conditional.
    /// </summary>
    private static readonly DiagnosticDescriptor s_conditional = new("ANKUS266", "Worker entry cannot be conditional",
        "'{0}' must run unconditionally; remove Conditional from the worker entry", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry cannot be conditional.
    /// </summary>
    internal static DiagnosticDescriptor Conditional => s_conditional;

    /// <summary>
    /// Stores the error for worker entry must support managed invocation.
    /// </summary>
    private static readonly DiagnosticDescriptor s_unmanagedOnly = new("ANKUS267", "Worker entry must support managed invocation",
        "'{0}' must be callable from generated managed code; remove UnmanagedCallersOnly", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry must support managed invocation.
    /// </summary>
    internal static DiagnosticDescriptor UnmanagedOnly => s_unmanagedOnly;

    /// <summary>
    /// Stores the error for worker entry cannot initialize the extension.
    /// </summary>
    private static readonly DiagnosticDescriptor s_initializationPhase = new("ANKUS268", "Worker entry cannot initialize the extension",
        "'{0}' cannot combine a worker entry with {1}; declare a separate initialization callback", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry cannot initialize the extension.
    /// </summary>
    internal static DiagnosticDescriptor InitializationPhase => s_initializationPhase;

    /// <summary>
    /// Stores the error for worker entry cannot be a sql export.
    /// </summary>
    private static readonly DiagnosticDescriptor s_sqlRole = new("ANKUS269", "Worker entry cannot be a SQL export",
        "'{0}' cannot combine a worker entry with {1}; declare a separate SQL callback", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for worker entry cannot be a sql export.
    /// </summary>
    internal static DiagnosticDescriptor SqlRole => s_sqlRole;

    /// <summary>
    /// Stores the error for sql result metadata does not apply to workers.
    /// </summary>
    private static readonly DiagnosticDescriptor s_resultMetadata = new("ANKUS270", "SQL result metadata does not apply to workers",
        "'{0}' returns no SQL value; remove {1} from its result", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for sql result metadata does not apply to workers.
    /// </summary>
    internal static DiagnosticDescriptor ResultMetadata => s_resultMetadata;

    /// <summary>
    /// Stores the error for sql argument metadata does not apply to workers.
    /// </summary>
    private static readonly DiagnosticDescriptor s_parameterMetadata = new("ANKUS271", "SQL argument metadata does not apply to workers",
        "'{0}' receives a native Datum and has no SQL argument; remove {1} from its parameter", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for sql argument metadata does not apply to workers.
    /// </summary>
    internal static DiagnosticDescriptor ParameterMetadata => s_parameterMetadata;

    /// <summary>
    /// Stores the error for invalid native worker export identifier.
    /// </summary>
    private static readonly DiagnosticDescriptor s_invalidExport = new("ANKUS272", "Invalid native worker export identifier",
        "'{0}' export '{1}' must start with an ASCII letter and contain only ASCII letters, digits, or underscores", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for invalid native worker export identifier.
    /// </summary>
    internal static DiagnosticDescriptor InvalidExport => s_invalidExport;

    /// <summary>
    /// Stores the error for native worker export is too long.
    /// </summary>
    private static readonly DiagnosticDescriptor s_exportLength = new("ANKUS273", "Native worker export is too long",
        "'{0}' export '{1}' exceeds PostgreSQL's 95-byte symbol limit; choose a shorter ASCII identifier", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native worker export is too long.
    /// </summary>
    internal static DiagnosticDescriptor ExportLength => s_exportLength;

    /// <summary>
    /// Stores the error for native worker export is reserved.
    /// </summary>
    private static readonly DiagnosticDescriptor s_reservedExport = new("ANKUS274", "Native worker export is reserved",
        "'{0}' export '{1}' is a C keyword, PostgreSQL loader symbol, or reserved Ankus symbol; choose a different identifier", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native worker export is reserved.
    /// </summary>
    internal static DiagnosticDescriptor ReservedExport => s_reservedExport;

    /// <summary>
    /// Stores the error for duplicate native worker export.
    /// </summary>
    private static readonly DiagnosticDescriptor s_duplicateExport = new("ANKUS275", "Duplicate native worker export",
        "'{0}' export '{1}' is already declared by another worker; choose a distinct EntryPoint or method name", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for duplicate native worker export.
    /// </summary>
    internal static DiagnosticDescriptor DuplicateExport => s_duplicateExport;

}
