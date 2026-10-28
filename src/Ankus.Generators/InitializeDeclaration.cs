using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Selects and validates the assembly's managed library initialization callback.
/// </summary>
internal static class InitializeDeclaration
{
    /// <summary>
    /// Identifies initialization callbacks independently of SQL function discovery.
    /// </summary>
    /// <param name="method">The attributed method.</param>
    /// <returns>Whether the initialization marker is present.</returns>
    internal static bool IsInitializer(IMethodSymbol method)
        => method.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() is
            "Ankus.PgInitializeAttribute" or "Ankus.PgModuleLoadAttribute");

    /// <summary>
    /// Validates one canonical phase declaration and detaches its invocation and callback identity.
    /// </summary>
    /// <param name="method">The attributed method normalized to its partial definition.</param>
    /// <param name="context">The current semantic diagnostic receiver.</param>
    /// <returns>The immutable phase contract, or null after validation fails.</returns>
    internal static LifecycleDeclaration? Create(IMethodSymbol method, GeneratorDiagnostics context)
        => Validate(method, context) ? new(HasAttribute(method, "Ankus.PgModuleLoadAttribute"),
            MethodInvocation.Create(method).Target, PgFunctionGenerator.GetCallbackName(method,
                HasAttribute(method, "Ankus.PgModuleLoadAttribute") ? "module_load" : "initialize")) : null;

    /// <summary>
    /// Reports extension-wide phase collisions against the current declaring source location.
    /// </summary>
    /// <param name="location">The current canonical definition location.</param>
    /// <param name="name">The original method name.</param>
    /// <param name="moduleLoad">Whether the conflicting phase is module registration.</param>
    /// <param name="context">The current diagnostic receiver.</param>
    internal static void ReportDuplicate(Location? location, string name, bool moduleLoad, GeneratorDiagnostics context)
        => context.Report(InitializerDeclarationDiagnostics.Duplicate, location, name, moduleLoad ? "PgModuleLoad" : "PgInitialize");

    /// <summary>
    /// Rejects unsupported signatures, inaccessible containers, and SQL-only metadata.
    /// </summary>
    private static bool Validate(IMethodSymbol method, GeneratorDiagnostics context)
    {
        if (HasAttribute(method, "Ankus.PgInitializeAttribute") && HasAttribute(method, "Ankus.PgModuleLoadAttribute"))
        {
            return Report(InitializerDeclarationDiagnostics.Phase, Attribute("Ankus.PgModuleLoadAttribute"));
        }

        if (method.MethodKind != MethodKind.Ordinary)
        {
            return Report(InitializerDeclarationDiagnostics.Kind, Attribute("Ankus.PgInitializeAttribute") ?? Attribute("Ankus.PgModuleLoadAttribute"));
        }

        if (!method.IsStatic)
        {
            return Report(InitializerDeclarationDiagnostics.Static, method.Locations.FirstOrDefault());
        }

        if (method.IsAsync || method.PartialImplementationPart?.IsAsync == true)
        {
            return Report(InitializerDeclarationDiagnostics.Async, Modifier(SyntaxKind.AsyncKeyword));
        }

        if (method.IsGenericMethod)
        {
            return Report(InitializerDeclarationDiagnostics.Generic, Syntax()?.TypeParameterList?.GetLocation());
        }

        if (method.IsAbstract)
        {
            return Report(InitializerDeclarationDiagnostics.Abstract, Modifier(SyntaxKind.AbstractKeyword));
        }

        if (method.IsVirtual)
        {
            return Report(InitializerDeclarationDiagnostics.Virtual, Modifier(SyntaxKind.VirtualKeyword));
        }

        if (method.IsExtern)
        {
            return Report(InitializerDeclarationDiagnostics.Extern, Modifier(SyntaxKind.ExternKeyword));
        }

        if (!method.ReturnsVoid)
        {
            return Report(InitializerDeclarationDiagnostics.Result, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken), method.ReturnType.ToDisplayString());
        }

        if (method.Parameters.Length != 0)
        {
            return Report(InitializerDeclarationDiagnostics.Parameters, Syntax()?.ParameterList.GetLocation());
        }

        if (method.IsPartialDefinition && method.PartialImplementationPart is null)
        {
            return Report(InitializerDeclarationDiagnostics.Implementation, method.Locations.FirstOrDefault());
        }

        if (method.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
        {
            return Report(InitializerDeclarationDiagnostics.Accessibility, Modifier(method.DeclaredAccessibility is Accessibility.Private or
                Accessibility.ProtectedAndInternal ? SyntaxKind.PrivateKeyword : SyntaxKind.ProtectedKeyword));
        }

        for (INamedTypeSymbol? type = method.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.IsGenericType)
            {
                return Report(InitializerDeclarationDiagnostics.GenericContainer, type.Locations.FirstOrDefault(), type.Name);
            }

            if (type.IsFileLocal)
            {
                return Report(InitializerDeclarationDiagnostics.FileContainer, type.Locations.FirstOrDefault(), type.Name);
            }

            if (type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return Report(InitializerDeclarationDiagnostics.ContainerAccessibility, type.Locations.FirstOrDefault(), type.Name);
            }
        }

        if (Attribute("System.Diagnostics.ConditionalAttribute") is Location conditional)
        {
            return Report(InitializerDeclarationDiagnostics.Conditional, conditional);
        }

        if (Attribute("System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute") is Location unmanaged)
        {
            return Report(InitializerDeclarationDiagnostics.UnmanagedOnly, unmanaged);
        }

        AttributeData? role = method.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() is
            "Ankus.PgFunctionAttribute" or "Ankus.PgTriggerAttribute" or "Ankus.PgEventTriggerAttribute" or "Ankus.PgOperatorAttribute" or "Ankus.PgCastAttribute");
        if (role is not null)
        {
            return Report(InitializerDeclarationDiagnostics.SqlRole, role.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation(),
                role.AttributeClass!.Name.Replace("Attribute", string.Empty));
        }

        AttributeData? result = method.GetReturnTypeAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() is
            "Ankus.PgCompositeTypeAttribute" or "Ankus.PgNumericPrecisionAttribute" or "Ankus.PgColumnNamesAttribute");
        if (result is not null)
        {
            return Report(InitializerDeclarationDiagnostics.ResultMetadata, result.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation(),
                result.AttributeClass!.Name.Replace("Attribute", string.Empty));
        }

        return true;

        bool Report(DiagnosticDescriptor descriptor, Location? location, params string[] details)
        {
            context.Report(descriptor, location ?? method.Locations.FirstOrDefault(), [method.Name, .. details]);
            return false;
        }

        Location? Attribute(string name) => method.GetAttributes().FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == name)?
            .ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation();

        MethodDeclarationSyntax? Syntax() => method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) as MethodDeclarationSyntax;

        Location? Modifier(SyntaxKind kind)
        {
            IMethodSymbol authored = method.PartialImplementationPart ?? method;
            var syntax = authored.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) as MethodDeclarationSyntax;
            SyntaxToken token = syntax?.Modifiers.FirstOrDefault(value => value.IsKind(kind)) ?? default;
            return token.RawKind != 0 ? token.GetLocation() : method.Locations.FirstOrDefault();
        }
    }

    /// <summary>
    /// Finds a phase marker on a normalized method declaration.
    /// </summary>
    internal static bool HasAttribute(IMethodSymbol method, string name)
        => method.GetAttributes().Any(attribute => attribute.AttributeClass?.ToDisplayString() == name);
}
