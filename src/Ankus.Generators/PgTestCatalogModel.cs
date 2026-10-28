using System.Collections.Generic;
using Microsoft.CodeAnalysis;

namespace Ankus.Generators;

/// <summary>
/// Retains only the lexical owner and ordered discovery values that affect a backend-test catalog.
/// </summary>
/// <param name="Container">The accessible partial type and its enclosing declarations.</param>
/// <param name="Cases">The exact discovery cases in managed signature order.</param>
internal sealed record PgTestCatalogModel(PgTestCatalogModel.Owner Container, EquatableArray<PgTestCatalogModel.Case> Cases)
{
    /// <summary>
    /// Identifies a catalog owner without retaining compiler types or source positions.
    /// </summary>
    /// <param name="Managed">The qualified owner identity.</param>
    /// <param name="Namespace">The escaped namespace, or null for the global namespace.</param>
    /// <param name="Declarations">The ordered outer-to-inner partial type declarations.</param>
    internal sealed record Owner(string Managed, string? Namespace, EquatableArray<string> Declarations)
    {
        /// <summary>
        /// Detaches the already validated partial containers needed by generated discovery code.
        /// </summary>
        /// <param name="owner">The type declaring the backend test.</param>
        /// <returns>The exact lexical catalog contract.</returns>
        internal static Owner Create(INamedTypeSymbol owner)
        {
            var containers = new Stack<string>();
            foreach (INamedTypeSymbol type in TypeNesting.SelfAndContainers(owner))
            {
                string accessibility = type.DeclaredAccessibility switch
                {
                    Accessibility.Public => "public",
                    Accessibility.ProtectedOrInternal => "protected internal",
                    _ => "internal",
                };
                containers.Push(accessibility + " " +
                    (type.IsStatic ? "static " : string.Empty) + "partial " + (type.IsRecord ? "record class" : "class") + " @" + type.Name);
            }

            return new(owner.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
                owner.ContainingNamespace.IsGlobalNamespace ? null : owner.ContainingNamespace.ToDisplayString(), new(containers));
        }
    }

    /// <summary>
    /// Preserves host discovery metadata independently of the native test's implementation.
    /// </summary>
    /// <param name="Name">The exact managed display name.</param>
    /// <param name="FunctionName">The assembly-specific stable SQL entry name.</param>
    /// <param name="Schema">The explicit schema, or null for the extension schema.</param>
    /// <param name="ExpectedError">The exact expected diagnostic, or null for successful tests.</param>
    /// <param name="IgnoreReason">The nonempty explicit ignore reason, or null for an enabled test.</param>
    internal sealed record Case(string Name, string FunctionName, string? Schema, string? ExpectedError, string? IgnoreReason);
}
