using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Reports independently correctable native callback declaration failures.
/// </summary>
internal static class NativeCallbackDeclarationDiagnostics
{
    /// <summary>
    /// Links native callback failures to their exact declaration requirements.
    /// </summary>
    private const string HelpLink = "https://ankus.net/raw-values/#native-callback-declaration-diagnostics";

    /// <summary>
    /// Stores the error for native callback requires one declaration.
    /// </summary>
    private static readonly DiagnosticDescriptor s_attributeCount = new("ANKUS310", "Native callback requires one declaration",
        "'{0}' requires exactly one PgNativeCallback attribute; remove duplicate declarations", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback requires one declaration.
    /// </summary>
    internal static DiagnosticDescriptor AttributeCount => s_attributeCount;

    /// <summary>
    /// Stores the error for native callback cannot declare a guc.
    /// </summary>
    private static readonly DiagnosticDescriptor s_gucConflict = new("ANKUS311", "Native callback cannot declare a GUC",
        "'{0}' cannot combine a native callback with a GUC; use separate properties", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback cannot declare a guc.
    /// </summary>
    internal static DiagnosticDescriptor GucConflict => s_gucConflict;

    /// <summary>
    /// Stores the error for native callback property must be static.
    /// </summary>
    private static readonly DiagnosticDescriptor s_staticProperty = new("ANKUS312", "Native callback property must be static",
        "'{0}' must be static; a native callback has no managed instance", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback property must be static.
    /// </summary>
    internal static DiagnosticDescriptor StaticProperty => s_staticProperty;

    /// <summary>
    /// Stores the error for native callback cannot be an indexer.
    /// </summary>
    private static readonly DiagnosticDescriptor s_indexer = new("ANKUS314", "Native callback cannot be an indexer",
        "'{0}' cannot be an indexer; declare a named callback property", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback cannot be an indexer.
    /// </summary>
    internal static DiagnosticDescriptor Indexer => s_indexer;

    /// <summary>
    /// Stores the error for native callback property must return a value.
    /// </summary>
    private static readonly DiagnosticDescriptor s_referenceProperty = new("ANKUS313", "Native callback property must return a value",
        "'{0}' must return its native pointer by value; remove ref or ref readonly", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback property must return a value.
    /// </summary>
    internal static DiagnosticDescriptor ReferenceProperty => s_referenceProperty;

    /// <summary>
    /// Stores the error for native callback property requires a getter.
    /// </summary>
    private static readonly DiagnosticDescriptor s_getter = new("ANKUS315", "Native callback property requires a getter",
        "'{0}' requires a get accessor so callers can obtain the generated callback", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback property requires a getter.
    /// </summary>
    internal static DiagnosticDescriptor Getter => s_getter;

    /// <summary>
    /// Stores the error for native callback property cannot have a setter.
    /// </summary>
    private static readonly DiagnosticDescriptor s_setter = new("ANKUS316", "Native callback property cannot have a setter",
        "'{0}' requires a getter-only property; remove set or init", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback property cannot have a setter.
    /// </summary>
    internal static DiagnosticDescriptor Setter => s_setter;

    /// <summary>
    /// Stores the error for native callback property must be partial.
    /// </summary>
    private static readonly DiagnosticDescriptor s_partialDefinition = new("ANKUS317", "Native callback property must be partial",
        "'{0}' requires a defining partial property so Ankus can supply its implementation", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback property must be partial.
    /// </summary>
    internal static DiagnosticDescriptor PartialDefinition => s_partialDefinition;

    /// <summary>
    /// Stores the error for native callback property already has an implementation.
    /// </summary>
    private static readonly DiagnosticDescriptor s_existingImplementation = new("ANKUS318", "Native callback property already has an implementation",
        "'{0}' must not supply a partial implementation; Ankus owns the guarded callback address", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback property already has an implementation.
    /// </summary>
    internal static DiagnosticDescriptor ExistingImplementation => s_existingImplementation;

    /// <summary>
    /// Stores the error for native callback requires a class or struct.
    /// </summary>
    private static readonly DiagnosticDescriptor s_containerKind = new("ANKUS319", "Native callback requires a class or struct",
        "'{0}' requires a class or struct; containing type '{1}' cannot own its implementation", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback requires a class or struct.
    /// </summary>
    internal static DiagnosticDescriptor ContainerKind => s_containerKind;

    /// <summary>
    /// Stores the error for native callback cannot have a generic container.
    /// </summary>
    private static readonly DiagnosticDescriptor s_genericContainer = new("ANKUS320", "Native callback cannot have a generic container",
        "'{0}' cannot be nested in generic type '{1}'; move it to a non-generic container", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback cannot have a generic container.
    /// </summary>
    internal static DiagnosticDescriptor GenericContainer => s_genericContainer;

    /// <summary>
    /// Stores the error for native callback cannot be file-local.
    /// </summary>
    private static readonly DiagnosticDescriptor s_fileContainer = new("ANKUS321", "Native callback cannot be file-local",
        "'{0}' cannot be nested in file-local type '{1}'; generated code requires access from another file", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback cannot be file-local.
    /// </summary>
    internal static DiagnosticDescriptor FileContainer => s_fileContainer;

    /// <summary>
    /// Stores the error for native callback container must be partial.
    /// </summary>
    private static readonly DiagnosticDescriptor s_partialContainer = new("ANKUS322", "Native callback container must be partial",
        "'{0}' requires every declaration of containing type '{1}' to be partial", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback container must be partial.
    /// </summary>
    internal static DiagnosticDescriptor PartialContainer => s_partialContainer;

    /// <summary>
    /// Stores the error for native callback requires a generated pointer type.
    /// </summary>
    private static readonly DiagnosticDescriptor s_pointerType = new("ANKUS323", "Native callback requires a generated pointer type",
        "'{0}' must use a generated unmanaged, non-generic native function-pointer type", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback requires a generated pointer type.
    /// </summary>
    internal static DiagnosticDescriptor PointerType => s_pointerType;

    /// <summary>
    /// Stores the error for native callback pointer metadata is invalid.
    /// </summary>
    private static readonly DiagnosticDescriptor s_pointerMetadata = new("ANKUS324", "Native callback pointer metadata is invalid",
        "'{0}' requires one NativeFunctionPointer attribute with a nonnegative prototype index; use the matching generated binding", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback pointer metadata is invalid.
    /// </summary>
    internal static DiagnosticDescriptor PointerMetadata => s_pointerMetadata;

    /// <summary>
    /// Stores the error for native callback pointer requires one fixed invoke signature.
    /// </summary>
    private static readonly DiagnosticDescriptor s_pointerInvocation = new("ANKUS325", "Native callback pointer requires one fixed Invoke signature",
        "'{0}' requires one public instance Invoke with a complete by-value native signature; use a supported generated binding", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback pointer requires one fixed invoke signature.
    /// </summary>
    internal static DiagnosticDescriptor PointerInvocation => s_pointerInvocation;

    /// <summary>
    /// Stores the error for native callback pointer requires native address construction.
    /// </summary>
    private static readonly DiagnosticDescriptor s_pointerConstructor = new("ANKUS326", "Native callback pointer requires native address construction",
        "'{0}' requires its generated pointer's public void* constructor; use the matching generated binding", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback pointer requires native address construction.
    /// </summary>
    internal static DiagnosticDescriptor PointerConstructor => s_pointerConstructor;

    /// <summary>
    /// Stores the error for native callback requires a handler name.
    /// </summary>
    private static readonly DiagnosticDescriptor s_handlerName = new("ANKUS327", "Native callback requires a handler name",
        "'{0}' requires a nonempty handler name; use nameof with a method in the same containing type", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback requires a handler name.
    /// </summary>
    internal static DiagnosticDescriptor HandlerName => s_handlerName;

    /// <summary>
    /// Stores the error for native callback handler was not found.
    /// </summary>
    private static readonly DiagnosticDescriptor s_missingHandler = new("ANKUS328", "Native callback handler was not found",
        "'{0}' cannot find method '{1}' in its containing type; declare the handler there or correct its name", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler was not found.
    /// </summary>
    internal static DiagnosticDescriptor MissingHandler => s_missingHandler;

    /// <summary>
    /// Stores the error for native callback handler is ambiguous.
    /// </summary>
    private static readonly DiagnosticDescriptor s_ambiguousHandler = new("ANKUS329", "Native callback handler is ambiguous",
        "'{0}' resolves more than one matching handler '{1}'; retain one exact native signature", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler is ambiguous.
    /// </summary>
    internal static DiagnosticDescriptor AmbiguousHandler => s_ambiguousHandler;

    /// <summary>
    /// Stores the error for native callback has no matching overload.
    /// </summary>
    private static readonly DiagnosticDescriptor s_noMatchingHandler = new("ANKUS330", "Native callback has no matching overload",
        "'{0}' has no overload of '{1}' matching Invoke; provide one synchronous static method with the exact by-value native signature", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback has no matching overload.
    /// </summary>
    internal static DiagnosticDescriptor NoMatchingHandler => s_noMatchingHandler;

    /// <summary>
    /// Stores the error for native callback requires an ordinary method.
    /// </summary>
    private static readonly DiagnosticDescriptor s_handlerKind = new("ANKUS331", "Native callback requires an ordinary method",
        "'{0}' cannot use handler '{1}' as an accessor or special method; declare an ordinary static method", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback requires an ordinary method.
    /// </summary>
    internal static DiagnosticDescriptor HandlerKind => s_handlerKind;

    /// <summary>
    /// Stores the error for native callback handler must be static.
    /// </summary>
    private static readonly DiagnosticDescriptor s_staticHandler = new("ANKUS332", "Native callback handler must be static",
        "'{0}' requires static handler '{1}'; a native invocation has no managed instance", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler must be static.
    /// </summary>
    internal static DiagnosticDescriptor StaticHandler => s_staticHandler;

    /// <summary>
    /// Stores the error for native callback handler cannot be extern.
    /// </summary>
    private static readonly DiagnosticDescriptor s_externalHandler = new("ANKUS334", "Native callback handler cannot be extern",
        "'{0}' cannot use extern handler '{1}'; provide a managed body inside the generated exception boundary", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler cannot be extern.
    /// </summary>
    internal static DiagnosticDescriptor ExternalHandler => s_externalHandler;

    /// <summary>
    /// Stores the error for native callback handler must be synchronous.
    /// </summary>
    private static readonly DiagnosticDescriptor s_asyncHandler = new("ANKUS335", "Native callback handler must be synchronous",
        "'{0}' cannot use async handler '{1}'; finish work on the calling PostgreSQL thread before returning", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler must be synchronous.
    /// </summary>
    internal static DiagnosticDescriptor AsyncHandler => s_asyncHandler;

    /// <summary>
    /// Stores the error for native callback partial handler lacks a body.
    /// </summary>
    private static readonly DiagnosticDescriptor s_partialHandler = new("ANKUS336", "Native callback partial handler lacks a body",
        "'{0}' requires an implementation for partial handler '{1}'; otherwise C# can omit its call", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback partial handler lacks a body.
    /// </summary>
    internal static DiagnosticDescriptor PartialHandler => s_partialHandler;

    /// <summary>
    /// Stores the error for native callback handler cannot be generic.
    /// </summary>
    private static readonly DiagnosticDescriptor s_genericHandler = new("ANKUS337", "Native callback handler cannot be generic",
        "'{0}' cannot use generic handler '{1}'; native callbacks require one closed invocation", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler cannot be generic.
    /// </summary>
    internal static DiagnosticDescriptor GenericHandler => s_genericHandler;

    /// <summary>
    /// Stores the error for native callback handler cannot be variadic.
    /// </summary>
    private static readonly DiagnosticDescriptor s_variadicHandler = new("ANKUS338", "Native callback handler cannot be variadic",
        "'{0}' cannot use __arglist handler '{1}'; match the generated fixed Invoke signature", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler cannot be variadic.
    /// </summary>
    internal static DiagnosticDescriptor VariadicHandler => s_variadicHandler;

    /// <summary>
    /// Stores the error for native callback handler must return by value.
    /// </summary>
    private static readonly DiagnosticDescriptor s_referenceResult = new("ANKUS339", "Native callback handler must return by value",
        "'{0}' requires handler '{1}' to return by value; remove ref or ref readonly", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler must return by value.
    /// </summary>
    internal static DiagnosticDescriptor ReferenceResult => s_referenceResult;

    /// <summary>
    /// Stores the error for native callback handler result is unsupported.
    /// </summary>
    private static readonly DiagnosticDescriptor s_nativeResult = new("ANKUS340", "Native callback handler result is unsupported",
        "'{0}' requires a native result for handler '{1}'; use Invoke's exact supported return type", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler result is unsupported.
    /// </summary>
    internal static DiagnosticDescriptor NativeResult => s_nativeResult;

    /// <summary>
    /// Stores the error for native callback handler argument must be passed by value.
    /// </summary>
    private static readonly DiagnosticDescriptor s_referenceArgument = new("ANKUS341", "Native callback handler argument must be passed by value",
        "'{0}' requires handler '{1}' argument '{2}' by value; remove ref, in or out", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler argument must be passed by value.
    /// </summary>
    internal static DiagnosticDescriptor ReferenceArgument => s_referenceArgument;

    /// <summary>
    /// Stores the error for native callback handler argument is unsupported.
    /// </summary>
    private static readonly DiagnosticDescriptor s_nativeArgument = new("ANKUS342", "Native callback handler argument is unsupported",
        "'{0}' requires native argument '{2}' on handler '{1}'; use Invoke's exact supported argument type", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler argument is unsupported.
    /// </summary>
    internal static DiagnosticDescriptor NativeArgument => s_nativeArgument;

    /// <summary>
    /// Stores the error for native callback handler cannot be unmanaged-only.
    /// </summary>
    private static readonly DiagnosticDescriptor s_unmanagedHandler = new("ANKUS343", "Native callback handler cannot be unmanaged-only",
        "'{0}' cannot call handler '{1}' marked UnmanagedCallersOnly; remove that attribute and let Ankus own the native boundary", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler cannot be unmanaged-only.
    /// </summary>
    internal static DiagnosticDescriptor UnmanagedHandler => s_unmanagedHandler;

    /// <summary>
    /// Stores the error for native callback handler result does not match invoke.
    /// </summary>
    private static readonly DiagnosticDescriptor s_resultMismatch = new("ANKUS344", "Native callback handler result does not match Invoke",
        "'{0}' requires handler '{1}' to return exactly {2}; native return conversions are not inferred", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler result does not match invoke.
    /// </summary>
    internal static DiagnosticDescriptor ResultMismatch => s_resultMismatch;

    /// <summary>
    /// Stores the error for native callback handler argument count does not match invoke.
    /// </summary>
    private static readonly DiagnosticDescriptor s_argumentCount = new("ANKUS345", "Native callback handler argument count does not match Invoke",
        "'{0}' requires handler '{1}' to declare exactly {2} arguments, matching Invoke", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler argument count does not match invoke.
    /// </summary>
    internal static DiagnosticDescriptor ArgumentCount => s_argumentCount;

    /// <summary>
    /// Stores the error for native callback handler argument does not match invoke.
    /// </summary>
    private static readonly DiagnosticDescriptor s_argumentType = new("ANKUS346", "Native callback handler argument does not match Invoke",
        "'{0}' requires handler '{1}' argument '{2}' to be exactly {3}; native argument conversions are not inferred", "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Gets the error for native callback handler argument does not match invoke.
    /// </summary>
    internal static DiagnosticDescriptor ArgumentType => s_argumentType;

}
