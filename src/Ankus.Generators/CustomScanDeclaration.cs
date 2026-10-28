using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Validates a custom scan provider and retains its invocation contract without compiler state.
/// </summary>
/// <param name="Name">The name PostgreSQL registers and EXPLAIN shows.</param>
/// <param name="Provider">The fully qualified provider class.</param>
/// <param name="State">The fully qualified per-scan state type.</param>
/// <param name="Rescan">Whether the provider restarts scans in place.</param>
/// <param name="Explain">Whether the provider adds EXPLAIN properties.</param>
/// <param name="Scroll">Whether the provider reads scans backward.</param>
/// <param name="Mark">Whether the provider saves and restores scan positions.</param>
/// <param name="Shared">The fully qualified shared memory layout of a parallel provider, or null.</param>
/// <param name="Callback">The assembly-specific managed dispatcher symbol.</param>
internal sealed record CustomScanDeclaration(string Name, string Provider, string State, bool Rescan, bool Explain, bool Scroll, bool Mark,
    string? Shared, string Callback)
{
    /// <summary>
    /// The optional capability interfaces, each implemented for the provider's one state type.
    /// </summary>
    private static readonly string[] s_capabilities =
        ["IPgRescannableCustomScan", "IPgExplainedCustomScan", "IPgScrollableCustomScan", "IPgMarkableCustomScan"];

    /// <summary>
    /// The longest name PostgreSQL registers, excluding the terminating zero byte of <c>EXTNODENAME_MAX_LEN</c>.
    /// </summary>
    private const int MaximumNameLength = 63;

    /// <summary>
    /// Validates one provider's class, state contract, name and callback implementations.
    /// </summary>
    /// <param name="type">The attributed class.</param>
    /// <param name="attribute">The provider attribute.</param>
    /// <param name="context">The current semantic diagnostic receiver.</param>
    /// <returns>The immutable invocation contract, or null after an error.</returns>
    internal static CustomScanDeclaration? Create(INamedTypeSymbol type, AttributeData attribute, GeneratorDiagnostics context)
    {
        INamedTypeSymbol? unusable = TypeNesting.SelfAndContainers(type).FirstOrDefault(static container =>
            container.TypeParameters.Length != 0 || container.IsFileLocal ||
            container.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal));
        if (unusable is not null)
        {
            return Report(CustomScanDiagnostics.Container, unusable.Locations.FirstOrDefault(), unusable.Name);
        }

        INamedTypeSymbol[] contracts = [.. type.AllInterfaces.Where(static value => Is(value, "IPgCustomScan"))];
        ITypeSymbol[] states = [.. contracts.Select(static value => value.TypeArguments[0]).Distinct<ITypeSymbol>(SymbolEqualityComparer.Default)];
        if (states.Length != 1)
        {
            return Report(CustomScanDiagnostics.Contract, type.Locations.FirstOrDefault(), type.Name,
                states.Length == 0 ? "it implements none" : $"it implements {states.Length} state types");
        }

        string name = AttributeValues.Get<string?>(attribute, "Name", null) ?? SqlText.SnakeCase(type.Name);
        if (!IsName(name))
        {
            return Report(CustomScanDiagnostics.Name,
                FunctionDeclarationDiagnostics.Option(attribute, "Name", context.CancellationToken) ?? type.Locations.FirstOrDefault(), name);
        }

        ITypeSymbol state = states[0];
        bool[] capabilities = [.. s_capabilities.Select(name => type.AllInterfaces.Any(value => Is(value, name) &&
            SymbolEqualityComparer.Default.Equals(value.TypeArguments[0], state)))];
        INamedTypeSymbol[] parallel = [.. type.AllInterfaces.Where(static value => Is(value, "IPgParallelCustomScan", 2))];
        if (parallel.Length > 1)
        {
            return Report(CustomScanDiagnostics.Shared, type.Locations.FirstOrDefault(), type.Name, parallel.Length.ToString(CultureInfo.InvariantCulture));
        }

        foreach (INamedTypeSymbol contract in type.AllInterfaces.Where(value => value.TypeArguments.Length is 1 or 2 &&
            SymbolEqualityComparer.Default.Equals(value.TypeArguments[0], state) &&
            (Is(value, "IPgCustomScan") || s_capabilities.Any(name => Is(value, name)) || Is(value, "IPgParallelCustomScan", 2))))
        {
            foreach (IMethodSymbol member in contract.GetMembers().OfType<IMethodSymbol>())
            {
                if (type.FindImplementationForInterfaceMember(member) is IMethodSymbol { IsAsync: true } implementation)
                {
                    SyntaxToken token = (implementation.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) as
                        MethodDeclarationSyntax)?.Modifiers.FirstOrDefault(static value => value.IsKind(SyntaxKind.AsyncKeyword)) ?? default;
                    return Report(CustomScanDiagnostics.Async, token.RawKind != 0 ? token.GetLocation() : implementation.Locations.FirstOrDefault(),
                        implementation.Name);
                }
            }
        }

        return new(name, type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat), state.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            capabilities[0], capabilities[1], capabilities[2], capabilities[3],
            parallel.Length == 0 ? null : parallel[0].TypeArguments[1].ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            PgFunctionGenerator.GetCallbackName(PgFunctionGenerator.SymbolScope(type.ContainingAssembly) + ":" +
                type.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), "custom_scan"));

        CustomScanDeclaration? Report(DiagnosticDescriptor descriptor, Location? location, params string[] details)
        {
            context.Report(descriptor, location ?? type.Locations.FirstOrDefault(), details);
            return null;
        }
    }

    /// <summary>
    /// Accepts an identifier-shaped ASCII name that fits PostgreSQL's custom node name buffer.
    /// </summary>
    /// <param name="name">The candidate name.</param>
    /// <returns>Whether PostgreSQL can register and EXPLAIN can show the name unchanged in any server encoding.</returns>
    private static bool IsName(string name)
        => name.Length is > 0 and <= MaximumNameLength && name[0] is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' &&
            name.All(static character => character is '_' or >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9');

    /// <summary>
    /// Identifies a custom scan contract interface by its exact namespace, name and arity.
    /// </summary>
    private static bool Is(INamedTypeSymbol value, string name, int arity = 1)
        => value.Name == name && value.Arity == arity && value.ContainingNamespace.ToDisplayString() == "Ankus";
}
