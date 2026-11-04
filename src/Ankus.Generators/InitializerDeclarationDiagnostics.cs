using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Describes separately correctable extension initialization contracts.
/// </summary>
internal static class InitializerDeclarationDiagnostics
{
    /// <summary>
    /// Links errors to extension-author initialization guidance.
    /// </summary>
    private const string HelpLink = "https://ankus.net/initialization/#declaration-diagnostics";
    /// <summary>
    /// Stores the error for conflicting postgresql initialization phases.
    /// </summary>
    /// <summary>
    /// The error for conflicting postgresql initialization phases.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_phase = new("ANKUS230", "Conflicting PostgreSQL initialization phases",
        "'{0}' cannot combine PgInitialize and PgModuleLoad; declare a separate method for each phase", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization requires an ordinary method.
    /// </summary>
    /// <summary>
    /// The error for initialization requires an ordinary method.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_kind = new("ANKUS231", "Initialization requires an ordinary method",
        "'{0}' must be an ordinary named method; local functions, lambdas, and explicit interface implementations cannot initialize an extension", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization callback must be static.
    /// </summary>
    /// <summary>
    /// The error for initialization callback must be static.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_static = new("ANKUS232", "Initialization callback must be static",
        "'{0}' must be static so PostgreSQL can invoke it without an instance", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization callback must be synchronous.
    /// </summary>
    /// <summary>
    /// The error for initialization callback must be synchronous.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_async = new("ANKUS233", "Initialization callback must be synchronous",
        "'{0}' must complete synchronously on the PostgreSQL backend thread; remove async", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization callback cannot be generic.
    /// </summary>
    /// <summary>
    /// The error for initialization callback cannot be generic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_generic = new("ANKUS234", "Initialization callback cannot be generic",
        "'{0}' cannot declare method type parameters", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization callback requires a concrete method.
    /// </summary>
    /// <summary>
    /// The error for initialization callback requires a concrete method.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_abstract = new("ANKUS235", "Initialization callback requires a concrete method",
        "'{0}' cannot be abstract; provide a concrete implementation", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization callback cannot be virtual.
    /// </summary>
    /// <summary>
    /// The error for initialization callback cannot be virtual.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_virtual = new("ANKUS236", "Initialization callback cannot be virtual",
        "'{0}' cannot be virtual; provide a concrete non-virtual static callback", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization callback requires managed implementation.
    /// </summary>
    /// <summary>
    /// The error for initialization callback requires managed implementation.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_extern = new("ANKUS237", "Initialization callback requires managed implementation",
        "'{0}' cannot be extern; provide a managed implementation", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization callback must return void.
    /// </summary>
    /// <summary>
    /// The error for initialization callback must return void.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_result = new("ANKUS238", "Initialization callback must return void",
        "'{0}' must return void; its declared result is {1}", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization callback must be parameterless.
    /// </summary>
    /// <summary>
    /// The error for initialization callback must be parameterless.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_parameters = new("ANKUS239", "Initialization callback must be parameterless",
        "'{0}' cannot declare parameters; PostgreSQL supplies no initialization arguments", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization callback must be accessible.
    /// </summary>
    /// <summary>
    /// The error for initialization callback must be accessible.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_accessibility = new("ANKUS240", "Initialization callback must be accessible",
        "'{0}' must be public, internal, or protected internal so generated code can invoke it", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for partial initialization callback requires implementation.
    /// </summary>
    /// <summary>
    /// The error for partial initialization callback requires implementation.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_implementation = new("ANKUS241", "Partial initialization callback requires implementation",
        "'{0}' requires a partial method implementation; an unimplemented definition cannot initialize an extension", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization container cannot be generic.
    /// </summary>
    /// <summary>
    /// The error for initialization container cannot be generic.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_genericContainer = new("ANKUS242", "Initialization container cannot be generic",
        "'{0}' must be declared in non-generic types; enclosing type '{1}' is generic", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization container cannot be file-local.
    /// </summary>
    /// <summary>
    /// The error for initialization container cannot be file-local.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_fileContainer = new("ANKUS243", "Initialization container cannot be file-local",
        "'{0}' cannot be declared in file-local type '{1}'", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization container must be accessible.
    /// </summary>
    /// <summary>
    /// The error for initialization container must be accessible.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_containerAccessibility = new("ANKUS244", "Initialization container must be accessible",
        "'{0}' requires accessible enclosing types; '{1}' must be public, internal, or protected internal", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization callback cannot be conditional.
    /// </summary>
    /// <summary>
    /// The error for initialization callback cannot be conditional.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_conditional = new("ANKUS245", "Initialization callback cannot be conditional",
        "'{0}' must run unconditionally; remove Conditional from the callback", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization callback must support managed invocation.
    /// </summary>
    /// <summary>
    /// The error for initialization callback must support managed invocation.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_unmanagedOnly = new("ANKUS246", "Initialization callback must support managed invocation",
        "'{0}' must be callable from generated managed code; remove UnmanagedCallersOnly", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for initialization callback cannot be a sql export.
    /// </summary>
    /// <summary>
    /// The error for initialization callback cannot be a sql export.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_sqlRole = new("ANKUS247", "Initialization callback cannot be a SQL export",
        "'{0}' cannot combine initialization with {1}; declare a separate SQL callback", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for sql result metadata does not apply to initialization.
    /// </summary>
    /// <summary>
    /// The error for sql result metadata does not apply to initialization.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_resultMetadata = new("ANKUS248", "SQL result metadata does not apply to initialization",
        "'{0}' returns no SQL value; remove {1} from its result", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Stores the error for duplicate postgresql initialization phase.
    /// </summary>
    /// <summary>
    /// The error for duplicate postgresql initialization phase.
    /// </summary>
    internal static readonly DiagnosticDescriptor s_duplicate = new("ANKUS249", "Duplicate PostgreSQL initialization phase",
        "An assembly can declare only one {1} callback; '{0}' is an additional declaration", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);
}
