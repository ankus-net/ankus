using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Requires generated PostgreSQL entry calls to survive ordinary C# conditional compilation.
/// </summary>
internal static class ConditionalEntryDeclaration
{
    /// <summary>
    /// Describes an entry call that the C# compiler can omit entirely.
    /// </summary>
    private static readonly DiagnosticDescriptor s_conditional = new(
        "ANKUS277", "PostgreSQL entry method cannot be conditional",
        "PostgreSQL entry '{0}' cannot declare Conditional; remove the attribute so every invocation executes its body",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/reference/execution/#conditional-entry-methods");

    /// <summary>
    /// Rejects conditional metadata on either partial declaration before emitting its caller.
    /// </summary>
    /// <param name="method">The transient attributed entry method.</param>
    /// <param name="context">The diagnostic destination and current cancellation token.</param>
    /// <returns>Whether every compiled caller retains the entry call.</returns>
    internal static bool Validate(IMethodSymbol method, GeneratorDiagnostics context)
    {
        AttributeData? conditional = method.GetAttributes()
            .Concat(method.PartialDefinitionPart?.GetAttributes() ?? [])
            .Concat(method.PartialImplementationPart?.GetAttributes() ?? [])
            .FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.ConditionalAttribute");
        if (conditional is null)
        {
            return true;
        }

        context.Report(s_conditional,
            conditional.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation() ?? method.Locations.FirstOrDefault(),
            method.Name);
        return false;
    }
}
