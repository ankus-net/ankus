using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Validates concrete SQL invocation contracts and attributes each rejection to its authored cause.
/// </summary>
internal static class FunctionSignature
{
    private const string HelpLink = "https://ankus.net/getting-started/functions/#function-signatures";

    private static readonly DiagnosticDescriptor s_instance = new(
        "ANKUS033", "PostgreSQL function must be static",
        "'{0}' is an instance method; declare a static entry method because PostgreSQL does not supply a managed receiver",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_access = new(
        "ANKUS034", "PostgreSQL function is inaccessible",
        "'{0}' is not accessible to the generated dispatcher; make the method and every containing type accessible within the extension assembly",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_generic = new(
        "ANKUS035", "PostgreSQL function has open type parameters",
        "'{0}' has open type parameters; declare a non-generic entry method in non-generic containing types with concrete SQL types",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_abstract = new(
        "ANKUS036", "PostgreSQL function has no callable implementation",
        "'{0}' is abstract; put the SQL entry attribute on a concrete static implementation",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_referenceResult = new(
        "ANKUS037", "PostgreSQL function cannot return by reference",
        "'{0}' returns by reference; return a supported SQL value by value so PostgreSQL receives an owned datum",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_referenceParameter = new(
        "ANKUS038", "PostgreSQL argument cannot be passed by reference",
        "Parameter '{0}' uses '{1}'; pass SQL arguments and injected contexts by value because PostgreSQL has no managed by-reference argument slot",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_result = new(
        "ANKUS039", "Unsupported PostgreSQL result type",
        "Result type '{0}' has no supported SQL conversion; return a supported SQL type or declare an explicit datum mapping",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_parameter = new(
        "ANKUS040", "Unsupported PostgreSQL parameter type",
        "Parameter '{0}' has type '{1}' with no supported SQL conversion; use a supported SQL type or declare an explicit datum mapping",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_variadic = new(
        "ANKUS041", "PostgreSQL variadic parameter must map to an array",
        "Parameter '{0}' does not map to a SQL array; use params T[] with a supported SQL element type (byte[] maps to scalar bytea)",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_arguments = new(
        "ANKUS042", "Too many PostgreSQL function arguments",
        "'{0}' declares {1} SQL arguments; PostgreSQL supports at most 100, excluding injected contexts",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    private static readonly DiagnosticDescriptor s_fileLocal = new(
        "ANKUS043", "PostgreSQL function is in a file-local type",
        "Containing type '{0}' is file-local; remove file-local visibility and use a public or internal type accessible from generated source",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true, helpLinkUri: HelpLink);

    /// <summary>
    /// Rejects the first invalid invocation contract before generating SQL or native dispatchers.
    /// </summary>
    /// <param name="method">The transient authored method symbol.</param>
    /// <param name="parameters">The ordered detached conversion contracts.</param>
    /// <param name="set">The optional validated iterator shape.</param>
    /// <param name="result">The scalar conversion, or null for sets or unsupported results.</param>
    /// <param name="diagnostics">The current transient diagnostic destination.</param>
    /// <returns>Whether a concrete generated call is valid.</returns>
    internal static bool Validate(IMethodSymbol method, FunctionParameter[] parameters, SetResult? set, FunctionType? result,
        GeneratorDiagnostics diagnostics)
    {
        if (!ConditionalEntryDeclaration.Validate(method, diagnostics))
        {
            return false;
        }

        var syntax = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(diagnostics.CancellationToken) as BaseMethodDeclarationSyntax;
        Location? name = MethodSyntax.Name(syntax) ?? method.Locations.FirstOrDefault();
        if (!method.IsStatic)
        {
            return Reject(s_instance, name, diagnostics, method.Name);
        }

        if (method.IsAbstract)
        {
            return Reject(s_abstract, Modifier(syntax?.Modifiers ?? default, SyntaxKind.AbstractKeyword) ?? name, diagnostics, method.Name);
        }

        if (method.IsGenericMethod)
        {
            return Reject(s_generic, (syntax as MethodDeclarationSyntax)?.TypeParameterList?.GetLocation() ?? name, diagnostics, method.Name);
        }

        if (method.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
        {
            return Reject(s_access, name, diagnostics, method.Name);
        }

        for (INamedTypeSymbol? type = method.ContainingType; type is not null; type = type.ContainingType)
        {
            var declaration = type.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(diagnostics.CancellationToken) as TypeDeclarationSyntax;
            Location? owner = declaration?.Identifier.GetLocation() ?? type.Locations.FirstOrDefault();
            if (type.Arity != 0)
            {
                return Reject(s_generic, declaration?.TypeParameterList?.GetLocation() ?? owner, diagnostics, type.Name);
            }

            if (type.IsFileLocal)
            {
                return Reject(s_fileLocal, Modifier(declaration?.Modifiers ?? default, SyntaxKind.FileKeyword) ?? owner, diagnostics, type.Name);
            }

            if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return Reject(s_access, owner, diagnostics, type.Name);
            }
        }

        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            return Reject(s_referenceResult, MethodSyntax.Result(syntax) ?? name, diagnostics, method.Name);
        }

        if (set is null && result is null)
        {
            if (AttributeMetadataFailure.ReportConversion(method.ReturnType, MethodSyntax.Result(syntax) ?? name, diagnostics))
            {
                return false;
            }

            return Reject(s_result, MethodSyntax.Result(syntax) ?? name, diagnostics, method.ReturnType.ToDisplayString());
        }

        int sqlCount = 0;
        for (int index = 0; index < parameters.Length; index++)
        {
            FunctionParameter parameter = parameters[index];
            IParameterSymbol symbol = method.Parameters[index];
            var declaration = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(diagnostics.CancellationToken) as ParameterSyntax;
            Location? location = declaration?.Type?.GetLocation() ?? symbol.Locations.FirstOrDefault();
            if (parameter.RefKind != RefKind.None)
            {
                string passing = parameter.RefKind switch
                {
                    RefKind.Ref => "ref",
                    RefKind.In => "in",
                    RefKind.Out => "out",
                    RefKind.RefReadOnlyParameter => "ref readonly",
                    _ => parameter.RefKind.ToString(),
                };
                return Reject(s_referenceParameter, declaration?.GetLocation() ?? location, diagnostics, parameter.Name, passing);
            }

            if (parameter.IsParams && parameter.Type?.IsVector != true)
            {
                return Reject(s_variadic, Modifier(declaration?.Modifiers ?? default, SyntaxKind.ParamsKeyword) ?? location, diagnostics, parameter.Name);
            }

            if (!parameter.IsInjected)
            {
                sqlCount++;
                if (parameter.Type is null)
                {
                    if (AttributeMetadataFailure.ReportConversion(symbol.Type, location, diagnostics))
                    {
                        return false;
                    }

                    return Reject(s_parameter, location, diagnostics, parameter.Name, symbol.Type.ToDisplayString());
                }
            }
        }

        if (sqlCount > 100)
        {
            return Reject(s_arguments, syntax?.ParameterList.GetLocation() ?? name, diagnostics, method.Name,
                sqlCount.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return true;
    }

    /// <summary>
    /// Finds the authored keyword when the rejection comes from a declaration modifier.
    /// </summary>
    /// <param name="modifiers">The declaration's current syntax modifiers.</param>
    /// <param name="kind">The keyword identifying the invalid contract.</param>
    /// <returns>The current keyword location, or null when unavailable during editing.</returns>
    private static Location? Modifier(SyntaxTokenList modifiers, SyntaxKind kind)
    {
        foreach (SyntaxToken modifier in modifiers)
        {
            if (modifier.IsKind(kind))
            {
                return modifier.GetLocation();
            }
        }

        return null;
    }

    /// <summary>
    /// Captures one precise error while preserving the original fail-closed validation boundary.
    /// </summary>
    /// <param name="descriptor">The specific invalid signature contract.</param>
    /// <param name="location">The authored cause of that rejection.</param>
    /// <param name="diagnostics">The transient reporting destination.</param>
    /// <param name="arguments">The offending declaration names and types.</param>
    /// <returns>False, because no function may be generated from this contract.</returns>
    private static bool Reject(DiagnosticDescriptor descriptor, Location? location, GeneratorDiagnostics diagnostics, params string[] arguments)
    {
        diagnostics.Report(descriptor, location, arguments);
        return false;
    }
}
