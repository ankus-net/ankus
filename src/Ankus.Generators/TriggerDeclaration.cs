using System.Linq;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Preserves the PostgreSQL callback contract with independently actionable diagnostics.
/// </summary>
internal static class TriggerDeclaration
{
    /// <summary>
    /// Identifies declarations carrying the callback marker.
    /// </summary>
    /// <param name="method">The transient attributed method.</param>
    /// <returns>Whether this callback marker is present.</returns>
    internal static bool IsTrigger(IMethodSymbol method)
        => method.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() == "Ankus.PgTriggerAttribute");

    /// <summary>
    /// Validates shape and metadata without interpreting context values as ordinary SQL inputs.
    /// </summary>
    /// <param name="method">The transient attributed callback.</param>
    /// <param name="context">The diagnostic output context.</param>
    /// <returns>Whether native and managed callback generation can proceed.</returns>
    internal static bool Validate(IMethodSymbol method, GeneratorDiagnostics context)
        => BackendCallbackDeclaration.Validate(method, context, "PgTriggerAttribute", "PgEventTriggerAttribute", "PgTriggerContext", true);
}