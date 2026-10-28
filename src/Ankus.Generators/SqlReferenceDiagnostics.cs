using System;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Identifies one independently correctable managed SQL reference contract.
/// </summary>
internal enum SqlReferenceProblemKind
{
    /// <summary>
    /// Planner support requires a method name.
    /// </summary>
    SupportMethodName,

    /// <summary>
    /// A dependency target must be a closed declared type.
    /// </summary>
    TargetType,

    /// <summary>
    /// Parameter types require a method name.
    /// </summary>
    ParameterTypesMethod,

    /// <summary>
    /// A dependency method name must be valid text.
    /// </summary>
    MethodName,

    /// <summary>
    /// Every selected parameter must be a closed managed type.
    /// </summary>
    ParameterTypes,

    /// <summary>
    /// No method has the selected name.
    /// </summary>
    MethodMissing,

    /// <summary>
    /// Several overloads share the selected name and no parameter types were supplied.
    /// </summary>
    MethodAmbiguous,

    /// <summary>
    /// A declaration selector must be valid text.
    /// </summary>
    DeclarationId,

    /// <summary>
    /// A declaration selector must identify exactly one owned SQL declaration.
    /// </summary>
    DeclarationSelection,

    /// <summary>
    /// An assembly reference requires a declaration selector.
    /// </summary>
    AssemblyDeclaration,

    /// <summary>
    /// The referenced managed declaration emits no SQL object.
    /// </summary>
    MissingSqlObject,

    /// <summary>
    /// The referenced managed declaration emits several primary SQL objects.
    /// </summary>
    AmbiguousSqlObject,

    /// <summary>
    /// Planner support must annotate a generated PostgreSQL function.
    /// </summary>
    SupportSource,

    /// <summary>
    /// Managed and external planner support cannot both be selected.
    /// </summary>
    SupportConflict,

    /// <summary>
    /// The selected function has an incompatible planner-support signature.
    /// </summary>
    SupportSignature,

    /// <summary>
    /// An aggregate helper cannot serve as planner support.
    /// </summary>
    SupportAggregate,

    /// <summary>
    /// No overload has exactly the supplied parameter types.
    /// </summary>
    OverloadMissing,

    /// <summary>
    /// Several overloads have exactly the supplied parameter types.
    /// </summary>
    OverloadAmbiguous,

    /// <summary>
    /// The attributed managed declaration emits no SQL object.
    /// </summary>
    SourceMissingSqlObject,

    /// <summary>
    /// The attributed managed declaration emits several primary SQL objects.
    /// </summary>
    SourceAmbiguousSqlObject,
}

/// <summary>
/// Retains a fixed managed SQL reference problem, its optional authored identity and the value to correct.
/// </summary>
/// <param name="Kind">The exact failed contract.</param>
/// <param name="Identity">The declaration, method or selector used by the fixed diagnostic.</param>
/// <param name="Location">The authored value to correct, or null to report at the attribute.</param>
internal sealed record SqlReferenceProblem(SqlReferenceProblemKind Kind, string? Identity = null, GeneratorLocation? Location = null);

/// <summary>
/// Maps managed SQL reference failures to fixed actionable diagnostics.
/// </summary>
internal static class SqlReferenceDiagnostics
{
    private const string ReferenceHelp = "https://ankus.net/custom-sql/#reference-managed-declarations";
    private const string SupportHelp = "https://ankus.net/function-declarations/#planner-support-functions";

    private static readonly DiagnosticDescriptor s_supportMethodName = Support("ANKUS470", "Planner support requires a method name",
        "Supply a non-null planner-support method name");
    private static readonly DiagnosticDescriptor s_targetType = Reference("ANKUS471", "SQL dependency requires a declared type",
        "Supply a non-null closed class, struct, interface, enum or delegate type");
    private static readonly DiagnosticDescriptor s_parameterTypesMethod = Reference("ANKUS472", "SQL dependency parameter types require a method",
        "Supply a method name when ParameterTypes selects an overload");
    private static readonly DiagnosticDescriptor s_methodName = Reference("ANKUS473", "Invalid SQL dependency method name",
        "Supply a nonblank method name containing valid Unicode without zero characters");
    private static readonly DiagnosticDescriptor s_parameterTypes = Reference("ANKUS474", "Invalid SQL dependency parameter type",
        "ParameterTypes must contain only non-null closed managed types");
    private static readonly DiagnosticDescriptor s_methodMissing = Reference("ANKUS475", "SQL dependency method was not found",
        "Method '{0}' was not found on the referenced type or its base types");
    private static readonly DiagnosticDescriptor s_methodAmbiguous = Reference("ANKUS476", "SQL dependency method is ambiguous",
        "Method '{0}' has several overloads; set ParameterTypes to select one");
    private static readonly DiagnosticDescriptor s_declarationId = Reference("ANKUS477", "Invalid SQL declaration selector",
        "DeclarationId must be nonblank valid Unicode without zero characters");
    private static readonly DiagnosticDescriptor s_declarationSelection = Reference("ANKUS478", "SQL declaration selector is not unique",
        "DeclarationId '{0}' must identify exactly one SQL declaration belonging to the attributed declaration");
    private static readonly DiagnosticDescriptor s_assemblyDeclaration = Reference("ANKUS479", "Assembly SQL dependency requires a declaration selector",
        "Set DeclarationId to the assembly SQL declaration being ordered");
    private static readonly DiagnosticDescriptor s_missingSqlObject = Reference("ANKUS480", "Referenced declaration emits no SQL object",
        "Referenced declaration '{0}' does not declare a generated SQL object in this extension");
    private static readonly DiagnosticDescriptor s_ambiguousSqlObject = Reference("ANKUS481", "Referenced declaration emits several SQL objects",
        "Referenced declaration '{0}' emits several SQL objects; give one an Id and list that Id in Requires or Before instead");
    private static readonly DiagnosticDescriptor s_supportSource = Support("ANKUS482", "Planner support requires a generated function",
        "Apply PgSupportFunction to a generated PostgreSQL function");
    private static readonly DiagnosticDescriptor s_supportConflict = Support("ANKUS483", "Planner support selectors conflict",
        "Choose either PgSupportFunction or the external PgFunction.SupportFunction SQL name");
    private static readonly DiagnosticDescriptor s_supportSignature = Support("ANKUS484", "Invalid planner-support signature",
        "Planner support must take exactly one nonvariadic SQL internal argument and return scalar SQL internal");
    private static readonly DiagnosticDescriptor s_supportAggregate = Support("ANKUS485", "Aggregate helper cannot provide planner support",
        "Select an ordinary PgFunction method; aggregate helpers require an aggregate invocation");
    private static readonly DiagnosticDescriptor s_overloadMissing = Reference("ANKUS486", "No SQL dependency overload has the selected parameter types",
        "No overload of '{0}' has exactly the selected ParameterTypes");
    private static readonly DiagnosticDescriptor s_overloadAmbiguous = Reference("ANKUS487", "SQL dependency parameter types match several overloads",
        "Several overloads of '{0}' have exactly the selected ParameterTypes; give the overloads that differ only by generic arity or parameter modifiers distinct names");
    private static readonly DiagnosticDescriptor s_sourceMissingSqlObject = Reference("ANKUS488", "Dependency source emits no SQL object",
        "Attributed declaration '{0}' does not declare a generated SQL object in this extension; apply the dependency to a SQL declaration");
    private static readonly DiagnosticDescriptor s_sourceAmbiguousSqlObject = Reference("ANKUS489", "Dependency source emits several SQL objects",
        "Attributed declaration '{0}' emits several SQL objects; give the one being ordered an Id and set DeclarationId to it");

    /// <summary>
    /// Selects the fixed descriptor for one managed SQL reference failure.
    /// </summary>
    /// <param name="kind">The exact failed contract.</param>
    /// <returns>The corresponding diagnostic descriptor.</returns>
    internal static DiagnosticDescriptor Descriptor(SqlReferenceProblemKind kind) => kind switch
    {
        SqlReferenceProblemKind.SupportMethodName => s_supportMethodName,
        SqlReferenceProblemKind.TargetType => s_targetType,
        SqlReferenceProblemKind.ParameterTypesMethod => s_parameterTypesMethod,
        SqlReferenceProblemKind.MethodName => s_methodName,
        SqlReferenceProblemKind.ParameterTypes => s_parameterTypes,
        SqlReferenceProblemKind.MethodMissing => s_methodMissing,
        SqlReferenceProblemKind.MethodAmbiguous => s_methodAmbiguous,
        SqlReferenceProblemKind.DeclarationId => s_declarationId,
        SqlReferenceProblemKind.DeclarationSelection => s_declarationSelection,
        SqlReferenceProblemKind.AssemblyDeclaration => s_assemblyDeclaration,
        SqlReferenceProblemKind.MissingSqlObject => s_missingSqlObject,
        SqlReferenceProblemKind.AmbiguousSqlObject => s_ambiguousSqlObject,
        SqlReferenceProblemKind.SupportSource => s_supportSource,
        SqlReferenceProblemKind.SupportConflict => s_supportConflict,
        SqlReferenceProblemKind.SupportSignature => s_supportSignature,
        SqlReferenceProblemKind.SupportAggregate => s_supportAggregate,
        SqlReferenceProblemKind.OverloadMissing => s_overloadMissing,
        SqlReferenceProblemKind.OverloadAmbiguous => s_overloadAmbiguous,
        SqlReferenceProblemKind.SourceMissingSqlObject => s_sourceMissingSqlObject,
        SqlReferenceProblemKind.SourceAmbiguousSqlObject => s_sourceAmbiguousSqlObject,
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    private static DiagnosticDescriptor Reference(string id, string title, string message)
        => Create(id, title, message, ReferenceHelp);

    private static DiagnosticDescriptor Support(string id, string title, string message)
        => Create(id, title, message, SupportHelp);

    private static DiagnosticDescriptor Create(string id, string title, string message, string help)
        => new(id, title, message, "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: help);
}
