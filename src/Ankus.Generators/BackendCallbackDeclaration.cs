using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Validates row and event callback declarations without retaining their transient compiler symbols.
/// </summary>
internal static class BackendCallbackDeclaration
{
    /// <summary>
    /// Preserves the callback's managed shape and rejects SQL metadata with no callback meaning.
    /// </summary>
    /// <param name="method">The transient attributed method.</param>
    /// <param name="context">The diagnostic context and current cancellation token.</param>
    /// <param name="marker">The selected callback attribute name.</param>
    /// <param name="opposite">The incompatible callback attribute name.</param>
    /// <param name="contextType">The required context's unqualified runtime type name.</param>
    /// <param name="tupleResult">Whether the callback returns a heap tuple; otherwise it returns void.</param>
    /// <returns>Whether this declaration satisfies its callback contract.</returns>
    internal static bool Validate(IMethodSymbol method, GeneratorDiagnostics context, string marker, string opposite, string contextType, bool tupleResult)
    {
        AttributeData? conflict = method.GetAttributes().FirstOrDefault(attribute =>
            attribute.AttributeClass?.ToDisplayString() == "Ankus." + opposite ||
            attribute.AttributeClass?.ToDisplayString() is "Ankus.PgOperatorAttribute" or "Ankus.PgCastAttribute");
        if (conflict is not null)
        {
            return Report(CallbackDeclarationDiagnostics.RoleConflict,
                conflict.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation(),
                marker.Replace("Attribute", string.Empty),
                conflict.AttributeClass!.Name.Replace("Attribute", string.Empty));
        }

        if (!method.IsStatic)
        {
            return Report(CallbackDeclarationDiagnostics.Static, method.Locations.FirstOrDefault());
        }

        if (method.IsAsync)
        {
            return Report(CallbackDeclarationDiagnostics.Async, Modifier(method, SyntaxKind.AsyncKeyword, context.CancellationToken));
        }

        if (method.IsGenericMethod)
        {
            return Report(CallbackDeclarationDiagnostics.Generic,
                (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) as MethodDeclarationSyntax)?.TypeParameterList?.GetLocation());
        }

        if (method.IsAbstract)
        {
            return Report(CallbackDeclarationDiagnostics.Abstract, Modifier(method, SyntaxKind.AbstractKeyword, context.CancellationToken));
        }

        if (method.ReturnsByRef || method.ReturnsByRefReadonly)
        {
            return Report(CallbackDeclarationDiagnostics.RefResult, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
        }

        if (method.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
        {
            SyntaxKind access = method.DeclaredAccessibility is Accessibility.Private or Accessibility.ProtectedAndInternal
                ? SyntaxKind.PrivateKeyword : SyntaxKind.ProtectedKeyword;
            return Report(CallbackDeclarationDiagnostics.Accessibility, Modifier(method, access, context.CancellationToken));
        }

        string expectedResult = tupleResult ? "PgHeapTuple (nullable when skipping rows)" : "void";
        if (tupleResult
            ? method.ReturnType.WithNullableAnnotation(NullableAnnotation.NotAnnotated).ToDisplayString() != "Ankus.PgHeapTuple"
            : !method.ReturnsVoid)
        {
            DiagnosticDescriptor descriptor = CallbackDeclarationDiagnostics.Result;
            if (method.ReturnType is INamedTypeSymbol result)
            {
                string space = result.ContainingNamespace.ToDisplayString();
                if (space == "System.Threading.Tasks" && result.Name is "Task" or "ValueTask")
                {
                    descriptor = CallbackDeclarationDiagnostics.TaskResult;
                }
                else if (space == "System.Collections.Generic" && result.Name == "IAsyncEnumerable")
                {
                    descriptor = CallbackDeclarationDiagnostics.AsyncEnumerableResult;
                }
            }

            return Report(descriptor, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken), expectedResult, method.ReturnType.ToDisplayString());
        }

        if (method.Parameters.Length != 1)
        {
            return Report(CallbackDeclarationDiagnostics.ParameterCount,
                (method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) as MethodDeclarationSyntax)?.ParameterList.GetLocation(), contextType);
        }

        IParameterSymbol parameter = method.Parameters[0];
        var parameterSyntax = parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) as ParameterSyntax;
        if (parameter.RefKind != RefKind.None)
        {
            SyntaxToken token = parameterSyntax?.Modifiers.FirstOrDefault(static value => value.IsKind(SyntaxKind.RefKeyword) ||
                value.IsKind(SyntaxKind.InKeyword) || value.IsKind(SyntaxKind.OutKeyword)) ?? default;
            return Report(CallbackDeclarationDiagnostics.ParameterReference, token.RawKind != 0 ? token.GetLocation() : parameter.Locations.FirstOrDefault(), contextType);
        }

        if (parameter.IsParams)
        {
            SyntaxToken token = parameterSyntax?.Modifiers.FirstOrDefault(static value => value.IsKind(SyntaxKind.ParamsKeyword)) ?? default;
            return Report(CallbackDeclarationDiagnostics.ParameterParams, token.RawKind != 0 ? token.GetLocation() : parameter.Locations.FirstOrDefault(), contextType);
        }

        if (parameter.IsOptional)
        {
            Location? optional = parameterSyntax?.Default?.GetLocation() ?? parameter.GetAttributes()
                .FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Runtime.InteropServices.OptionalAttribute")?
                .ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation();
            return Report(CallbackDeclarationDiagnostics.ParameterOptional, optional ?? parameter.Locations.FirstOrDefault(), contextType);
        }

        if (parameter.NullableAnnotation == NullableAnnotation.Annotated)
        {
            return Report(CallbackDeclarationDiagnostics.ParameterNullable, parameterSyntax?.Type?.GetLocation(), contextType);
        }

        if (parameter.Type.ToDisplayString() != "Ankus." + contextType)
        {
            return Report(CallbackDeclarationDiagnostics.ParameterType, parameterSyntax?.Type?.GetLocation(), contextType, parameter.Type.ToDisplayString());
        }

        for (INamedTypeSymbol? type = method.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.IsGenericType)
            {
                return Report(CallbackDeclarationDiagnostics.GenericContainer, type.Locations.FirstOrDefault(), type.Name);
            }

            if (type.IsFileLocal)
            {
                return Report(CallbackDeclarationDiagnostics.FileContainer, type.Locations.FirstOrDefault(), type.Name);
            }

            if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return Report(CallbackDeclarationDiagnostics.ContainerAccessibility, type.Locations.FirstOrDefault(), type.Name);
            }
        }

        AttributeData? valueMetadata = method.GetReturnTypeAttributes().Concat(parameter.GetAttributes()).FirstOrDefault(static attribute =>
            attribute.AttributeClass?.ToDisplayString() is "Ankus.PgCompositeTypeAttribute" or "Ankus.PgNumericPrecisionAttribute" or
                "Ankus.PgColumnNamesAttribute" or "Ankus.PgParameterAttribute");
        if (valueMetadata is not null)
        {
            return Report(CallbackDeclarationDiagnostics.ValueMetadata,
                valueMetadata.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation(),
                valueMetadata.AttributeClass!.Name.Replace("Attribute", string.Empty));
        }

        AttributeData? function = method.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == "Ankus.PgFunctionAttribute");
        if (function?.NamedArguments.Any(static argument => argument.Key == "Rows") == true)
        {
            return Report(CallbackDeclarationDiagnostics.Rows, FunctionDeclarationDiagnostics.Option(function, "Rows", context.CancellationToken));
        }

        if (function?.NamedArguments.Any(static argument => argument.Key == "SetMode") == true)
        {
            return Report(CallbackDeclarationDiagnostics.SetMode, FunctionDeclarationDiagnostics.Option(function, "SetMode", context.CancellationToken));
        }

        return true;

        bool Report(DiagnosticDescriptor descriptor, Location? location, params string[] details)
        {
            string[] arguments = [method.Name, .. details];
            context.Report(descriptor, location ?? method.Locations.FirstOrDefault(), arguments);
            return false;
        }
    }

    /// <summary>
    /// Locates a rejected authored method modifier without retaining its syntax.
    /// </summary>
    /// <param name="method">The transient attributed method.</param>
    /// <param name="kind">The invalid modifier token kind.</param>
    /// <param name="cancellationToken">The current generation token.</param>
    /// <returns>The modifier location or the callback identifier.</returns>
    private static Location? Modifier(IMethodSymbol method, SyntaxKind kind, CancellationToken cancellationToken)
    {
        IMethodSymbol authored = method.PartialImplementationPart ?? method;
        var syntax = authored.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) as MethodDeclarationSyntax;
        SyntaxToken token = syntax?.Modifiers.FirstOrDefault(value => value.IsKind(kind)) ?? default;
        return token.RawKind != 0 ? token.GetLocation() : method.Locations.FirstOrDefault();
    }
}
