using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Validates a logical decoding output plugin initializer and retains its invocation contract without compiler state.
/// </summary>
/// <param name="Target">The fully qualified managed invocation target.</param>
/// <param name="CallbacksType">The fully qualified generated <c>OutputPluginCallbacks</c> record type.</param>
/// <param name="Callback">The assembly-specific managed dispatcher symbol.</param>
internal sealed record OutputPluginDeclaration(string Target, string CallbacksType, string Callback)
{
    /// <summary>
    /// The PostgreSQL loader symbol that an output plugin library exports.
    /// </summary>
    internal const string ExportName = "_PG_output_plugin_init";

    /// <summary>
    /// The attribute identity that marks the initializer.
    /// </summary>
    private const string MarkerName = "Ankus.PgOutputPluginAttribute";

    /// <summary>
    /// Identifies an output plugin initializer independently of SQL function discovery.
    /// </summary>
    /// <param name="method">The method to inspect.</param>
    /// <returns>Whether the method carries the output plugin marker.</returns>
    internal static bool IsOutputPlugin(IMethodSymbol method) => method.GetAttributes().Any(static attribute =>
        attribute.AttributeClass?.ToDisplayString() == MarkerName);

    /// <summary>
    /// Validates one normalized initializer's signature, container, metadata and generated callback-table type.
    /// </summary>
    /// <param name="method">The attributed method, normalized to its partial definition.</param>
    /// <param name="context">The current semantic diagnostic receiver.</param>
    /// <returns>The immutable invocation contract, or null after an error.</returns>
    internal static OutputPluginDeclaration? Create(IMethodSymbol method, GeneratorDiagnostics context)
    {
        if (method.MethodKind != MethodKind.Ordinary)
        {
            return Report(OutputPluginDiagnostics.Kind, Location(Find(method.GetAttributes(), MarkerName)));
        }

        if (!method.IsStatic)
        {
            return Report(OutputPluginDiagnostics.Static, method.Locations.FirstOrDefault());
        }

        if (method.IsAsync || method.PartialImplementationPart?.IsAsync == true)
        {
            return Report(OutputPluginDiagnostics.Async, Modifier(SyntaxKind.AsyncKeyword));
        }

        if (method.IsGenericMethod)
        {
            return Report(OutputPluginDiagnostics.Generic, Syntax()?.TypeParameterList?.GetLocation());
        }

        if (method.IsAbstract || method.IsVirtual || method.IsExtern)
        {
            return Report(OutputPluginDiagnostics.Implementation, Modifier(method.IsAbstract ? SyntaxKind.AbstractKeyword
                : method.IsVirtual ? SyntaxKind.VirtualKeyword : SyntaxKind.ExternKeyword));
        }

        if (method.IsPartialDefinition && method.PartialImplementationPart is null)
        {
            return Report(OutputPluginDiagnostics.Implementation, method.Locations.FirstOrDefault());
        }

        if (!method.ReturnsVoid)
        {
            return Report(OutputPluginDiagnostics.Result, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken),
                method.ReturnType.ToDisplayString());
        }

        if (method.Parameters.Length != 1 || method.Parameters[0].RefKind != RefKind.None ||
            CallbackTable(method.Parameters[0].Type) is not INamedTypeSymbol callbacks)
        {
            return Report(OutputPluginDiagnostics.Parameter, method.Parameters.Length == 1
                ? Syntax()?.ParameterList.Parameters.FirstOrDefault()?.GetLocation() : Syntax()?.ParameterList.GetLocation());
        }

        if (method.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
        {
            return Report(OutputPluginDiagnostics.Accessibility, Modifier(method.DeclaredAccessibility is Accessibility.Private or
                Accessibility.ProtectedAndInternal ? SyntaxKind.PrivateKeyword : SyntaxKind.ProtectedKeyword));
        }

        for (INamedTypeSymbol? type = method.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.IsGenericType || type.IsFileLocal ||
                type.DeclaredAccessibility is not (Accessibility.Public or Accessibility.Internal or Accessibility.ProtectedOrInternal))
            {
                return Report(OutputPluginDiagnostics.Container, type.Locations.FirstOrDefault(), type.Name);
            }
        }

        if (!ConditionalEntryDeclaration.Validate(method, context))
        {
            return null;
        }

        ImmutableArray<AttributeData> metadata = method.GetAttributes();
        AttributeData? unmanaged = Find(metadata, "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute");
        if (unmanaged is not null)
        {
            return Report(OutputPluginDiagnostics.UnmanagedOnly, Location(unmanaged));
        }

        AttributeData? role = metadata.Where(static attribute => attribute.AttributeClass?.ToDisplayString() != MarkerName)
            .Concat(method.GetReturnTypeAttributes()).Concat(method.Parameters[0].GetAttributes())
            .FirstOrDefault(static attribute => attribute.AttributeClass?.ContainingNamespace.ToDisplayString() == "Ankus");
        if (role is not null)
        {
            return Report(OutputPluginDiagnostics.Role, Location(role), role.AttributeClass!.Name.Replace("Attribute", string.Empty));
        }

        return new(MethodInvocation.Create(method).Target, callbacks.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            PgFunctionGenerator.GetCallbackName(method, "output_plugin"));

        OutputPluginDeclaration? Report(DiagnosticDescriptor descriptor, Location? location, params string[] details)
        {
            context.Report(descriptor, location ?? method.Locations.FirstOrDefault(), [method.Name, .. details]);
            return null;
        }

        Location? Location(AttributeData? attribute) => attribute?.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation();

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
    /// Selects the generated native callback-table record that a pointer parameter designates.
    /// </summary>
    /// <param name="type">The parameter type.</param>
    /// <returns>The record when the parameter is <c>Ankus.Postgres.OutputPluginCallbacks*</c>; otherwise null.</returns>
    private static INamedTypeSymbol? CallbackTable(ITypeSymbol type)
        => type is IPointerTypeSymbol { PointedAtType: INamedTypeSymbol { Name: "OutputPluginCallbacks", Arity: 0, TypeKind: TypeKind.Struct } record } &&
            record.ContainingType is null && record.ContainingNamespace.ToDisplayString() == "Ankus.Postgres" &&
            record.AllInterfaces.Any(static value => value.ToDisplayString() == "Ankus.IPgNativeType")
            ? record : null;

    /// <summary>
    /// Finds one exact semantic attribute identity without matching unrelated names.
    /// </summary>
    private static AttributeData? Find(IEnumerable<AttributeData> metadata, string name)
        => metadata.FirstOrDefault(attribute => attribute.AttributeClass?.ToDisplayString() == name);
}
