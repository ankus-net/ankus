using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Reports independently correctable backend-test declaration failures.
/// </summary>
internal static class PgTestDeclarationDiagnostics
{
    /// <summary>
    /// Links backend-test diagnostics to their declaration requirements.
    /// </summary>
    private const string HelpLink = "https://ankus.net/getting-started/testing/#declaration-diagnostics";

    /// <summary>
    /// Stores the error for PostgreSQL backend test must be static.
    /// </summary>
    private static readonly DiagnosticDescriptor s_static = new("ANKUS291", "PostgreSQL backend test must be static",
        "'{0}' must be static; backend tests have no managed instance to invoke", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test must be static.
    /// </summary>
    internal static DiagnosticDescriptor Static => s_static;

    /// <summary>
    /// Stores the error for PostgreSQL backend test must be synchronous.
    /// </summary>
    private static readonly DiagnosticDescriptor s_synchronous = new("ANKUS292", "PostgreSQL backend test must be synchronous",
        "'{0}' cannot use async; complete backend test work on the calling PostgreSQL thread before returning", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test must be synchronous.
    /// </summary>
    internal static DiagnosticDescriptor Synchronous => s_synchronous;

    /// <summary>
    /// Stores the error for PostgreSQL backend test cannot be generic.
    /// </summary>
    private static readonly DiagnosticDescriptor s_genericMethod = new("ANKUS293", "PostgreSQL backend test cannot be generic",
        "'{0}' cannot declare type parameters; backend discovery requires one closed test method", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test cannot be generic.
    /// </summary>
    internal static DiagnosticDescriptor GenericMethod => s_genericMethod;

    /// <summary>
    /// Stores the error for PostgreSQL backend test must have an implementation.
    /// </summary>
    private static readonly DiagnosticDescriptor s_abstract = new("ANKUS294", "PostgreSQL backend test must have an implementation",
        "'{0}' cannot be abstract; give the backend test a concrete implementation", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test must have an implementation.
    /// </summary>
    internal static DiagnosticDescriptor Abstract => s_abstract;

    /// <summary>
    /// Stores the error for PostgreSQL backend test must return void.
    /// </summary>
    private static readonly DiagnosticDescriptor s_result = new("ANKUS295", "PostgreSQL backend test must return void",
        "'{0}' must return void; report failed expectations by throwing within the backend test", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test must return void.
    /// </summary>
    internal static DiagnosticDescriptor Result => s_result;

    /// <summary>
    /// Stores the error for PostgreSQL backend test must be accessible.
    /// </summary>
    private static readonly DiagnosticDescriptor s_accessibility = new("ANKUS296", "PostgreSQL backend test must be accessible",
        "'{0}' must be public, internal or protected internal so generated dispatchers can invoke it", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test must be accessible.
    /// </summary>
    internal static DiagnosticDescriptor Accessibility => s_accessibility;

    /// <summary>
    /// Stores the error for PostgreSQL backend test cannot have SQL arguments.
    /// </summary>
    private static readonly DiagnosticDescriptor s_sqlArgument = new("ANKUS297", "PostgreSQL backend test cannot have SQL arguments",
        "'{0}' cannot declare SQL argument '{1}'; only injected backend context parameters are supported", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test cannot have SQL arguments.
    /// </summary>
    internal static DiagnosticDescriptor SqlArgument => s_sqlArgument;

    /// <summary>
    /// Stores the error for PostgreSQL backend test context must be passed by value.
    /// </summary>
    private static readonly DiagnosticDescriptor s_referenceArgument = new("ANKUS298", "PostgreSQL backend test context must be passed by value",
        "'{0}' must take injected context '{1}' by value; remove ref, in or out", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test context must be passed by value.
    /// </summary>
    internal static DiagnosticDescriptor ReferenceArgument => s_referenceArgument;

    /// <summary>
    /// Stores the error for PostgreSQL backend test has a conflicting role.
    /// </summary>
    private static readonly DiagnosticDescriptor s_conflictingRole = new("ANKUS299", "PostgreSQL backend test has a conflicting role",
        "'{0}' cannot combine PgTest with {1}; declare production callbacks separately", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test has a conflicting role.
    /// </summary>
    internal static DiagnosticDescriptor ConflictingRole => s_conflictingRole;

    /// <summary>
    /// Stores the error for PostgreSQL backend test requires a class.
    /// </summary>
    private static readonly DiagnosticDescriptor s_containerKind = new("ANKUS300", "PostgreSQL backend test requires a class",
        "'{0}' must be declared in a class; containing type '{1}' cannot own the generated backend catalog", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test requires a class.
    /// </summary>
    internal static DiagnosticDescriptor ContainerKind => s_containerKind;

    /// <summary>
    /// Stores the error for PostgreSQL backend test cannot have a generic container.
    /// </summary>
    private static readonly DiagnosticDescriptor s_genericContainer = new("ANKUS301", "PostgreSQL backend test cannot have a generic container",
        "'{0}' cannot be nested in generic type '{1}'; move it to a closed non-generic container", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test cannot have a generic container.
    /// </summary>
    internal static DiagnosticDescriptor GenericContainer => s_genericContainer;

    /// <summary>
    /// Stores the error for PostgreSQL backend test cannot be file-local.
    /// </summary>
    private static readonly DiagnosticDescriptor s_fileContainer = new("ANKUS302", "PostgreSQL backend test cannot be file-local",
        "'{0}' cannot be nested in file-local type '{1}'; generated catalogs and dispatchers need access from another file", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test cannot be file-local.
    /// </summary>
    internal static DiagnosticDescriptor FileContainer => s_fileContainer;

    /// <summary>
    /// Stores the error for PostgreSQL backend test container must be accessible.
    /// </summary>
    private static readonly DiagnosticDescriptor s_containerAccessibility = new("ANKUS303", "PostgreSQL backend test container must be accessible",
        "'{0}' requires public, internal or protected internal containing type '{1}'", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test container must be accessible.
    /// </summary>
    internal static DiagnosticDescriptor ContainerAccessibility => s_containerAccessibility;

    /// <summary>
    /// Stores the error for PostgreSQL backend test container must be partial.
    /// </summary>
    private static readonly DiagnosticDescriptor s_partialContainer = new("ANKUS304", "PostgreSQL backend test container must be partial",
        "'{0}' requires every declaration of containing class '{1}' to be partial so its catalog can be generated", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test container must be partial.
    /// </summary>
    internal static DiagnosticDescriptor PartialContainer => s_partialContainer;

    /// <summary>
    /// Stores the error for PostgreSQL backend test owner has a reserved name.
    /// </summary>
    private static readonly DiagnosticDescriptor s_reservedOwner = new("ANKUS305", "PostgreSQL backend test owner has a reserved name",
        "'{0}' cannot be declared in a class named PostgresTests; that name is reserved for the generated catalog", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test owner has a reserved name.
    /// </summary>
    internal static DiagnosticDescriptor ReservedOwner => s_reservedOwner;

    /// <summary>
    /// Stores the error for PostgreSQL backend test catalog name is already declared.
    /// </summary>
    private static readonly DiagnosticDescriptor s_reservedMember = new("ANKUS306", "PostgreSQL backend test catalog name is already declared",
        "'{0}' cannot generate its catalog because '{1}' already declares PostgresTests; rename that member or choose another test container", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test catalog name is already declared.
    /// </summary>
    internal static DiagnosticDescriptor ReservedMember => s_reservedMember;

    /// <summary>
    /// Stores the error for Invalid PostgreSQL backend test expected error.
    /// </summary>
    private static readonly DiagnosticDescriptor s_expectedError = new("ANKUS307", "Invalid PostgreSQL backend test expected error",
        "'{0}' requires valid Unicode without zero characters in ExpectedError", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for Invalid PostgreSQL backend test expected error.
    /// </summary>
    internal static DiagnosticDescriptor ExpectedError => s_expectedError;

    /// <summary>
    /// Stores the error for Invalid PostgreSQL backend test ignore text.
    /// </summary>
    private static readonly DiagnosticDescriptor s_ignoreText = new("ANKUS308", "Invalid PostgreSQL backend test ignore text",
        "'{0}' requires valid Unicode without zero characters in IgnoreReason", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for Invalid PostgreSQL backend test ignore text.
    /// </summary>
    internal static DiagnosticDescriptor IgnoreText => s_ignoreText;

    /// <summary>
    /// Stores the error for PostgreSQL backend test requires a nonempty ignore reason.
    /// </summary>
    private static readonly DiagnosticDescriptor s_ignoreReason = new("ANKUS309", "PostgreSQL backend test requires a nonempty ignore reason",
        "'{0}' cannot use an empty or whitespace-only IgnoreReason; explain why the test is ignored or remove the option", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for PostgreSQL backend test requires a nonempty ignore reason.
    /// </summary>
    internal static DiagnosticDescriptor IgnoreReason => s_ignoreReason;
}
