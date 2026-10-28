using System.Globalization;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Resolves a partial native callback property to one exact static managed handler.
/// </summary>
internal sealed class NativeCallbackDeclaration(IPropertySymbol property, IMethodSymbol handler, IMethodSymbol signature, int nativeSignature)
{
    /// <summary>
    /// Rejects handlers whose invocation can disappear during ordinary C# compilation.
    /// </summary>
    private static readonly DiagnosticDescriptor s_conditional = new(
        "ANKUS276", "Native callback handler cannot be conditional",
        "Native callback '{0}' cannot use conditional handler '{1}'; remove Conditional so every native invocation runs the handler",
        "Ankus", DiagnosticSeverity.Error, isEnabledByDefault: true,
        helpLinkUri: "https://ankus.net/raw-values/#managed-native-callbacks-and-hooks");

    /// <summary>
    /// Gets the defining partial property.
    /// </summary>
    internal IPropertySymbol Property { get; } = property;

    /// <summary>
    /// Gets the ordinary static method called within the generated exception boundary.
    /// </summary>
    internal IMethodSymbol Handler { get; } = handler;

    /// <summary>
    /// Gets the generated pointer's exact managed invocation signature.
    /// </summary>
    internal IMethodSymbol Signature { get; } = signature;

    /// <summary>
    /// Gets the canonical prototype index verified against the selected native graph when linking.
    /// </summary>
    internal int NativeSignature { get; } = nativeSignature;

    /// <summary>
    /// Gets the assembly-qualified identity which distinguishes independent callback properties.
    /// </summary>
    internal string Identity
    {
        get
        {
            using SHA256 hash = SHA256.Create();
            string identity = PgFunctionGenerator.SymbolScope(Property.ContainingAssembly) + ":" +
                Property.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            return string.Concat(hash.ComputeHash(Encoding.UTF8.GetBytes(identity)).Take(16)
                .Select(static value => value.ToString("x2", CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// Identifies a native callback property attribute.
    /// </summary>
    /// <param name="attribute">The semantic attribute.</param>
    /// <returns>Whether it requests a static native callback.</returns>
    internal static bool IsAttribute(AttributeData attribute)
        => attribute.AttributeClass?.ToDisplayString() == "Ankus.PgNativeCallbackAttribute";

    /// <summary>
    /// Validates declaration and handler contracts before creating a native import or managed dispatcher.
    /// </summary>
    /// <param name="property">The candidate property.</param>
    /// <param name="context">The diagnostic destination.</param>
    /// <returns>A complete callback declaration, or null after an error diagnostic.</returns>
    internal static NativeCallbackDeclaration? Create(IPropertySymbol property, GeneratorDiagnostics context)
    {
        AttributeData[] attributes = [.. property.GetAttributes().Where(IsAttribute)];
        if (attributes.Length != 1)
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.AttributeCount, property.Locations.FirstOrDefault());
        }

        AttributeData marker = attributes[0];
        AttributeData? guc = property.GetAttributes().FirstOrDefault(GucDeclaration.IsGucAttribute);
        if (guc is not null)
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.GucConflict, AttributeLocation(guc));
        }

        if (property.IsIndexer)
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.Indexer, property.Locations.FirstOrDefault());
        }

        if (!property.IsStatic)
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.StaticProperty, property.Locations.FirstOrDefault());
        }

        if (property.RefKind != RefKind.None)
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.ReferenceProperty, PropertyType());
        }

        if (property.GetMethod is null)
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.Getter, property.Locations.FirstOrDefault());
        }

        if (property.SetMethod is not null)
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.Setter, property.SetMethod.Locations.FirstOrDefault());
        }

        if (!property.IsPartialDefinition)
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.PartialDefinition, property.Locations.FirstOrDefault());
        }

        if (property.PartialImplementationPart is not null)
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.ExistingImplementation, property.PartialImplementationPart.Locations.FirstOrDefault());
        }

        for (INamedTypeSymbol? type = property.ContainingType; type is not null; type = type.ContainingType)
        {
            if (type.TypeKind is not (TypeKind.Class or TypeKind.Struct))
            {
                return Invalid(NativeCallbackDeclarationDiagnostics.ContainerKind, type.Locations.FirstOrDefault(), type.Name);
            }

            if (type.Arity != 0)
            {
                return Invalid(NativeCallbackDeclarationDiagnostics.GenericContainer, type.Locations.FirstOrDefault(), type.Name);
            }

            if (type.IsFileLocal)
            {
                return Invalid(NativeCallbackDeclarationDiagnostics.FileContainer, type.Locations.FirstOrDefault(), type.Name);
            }

            SyntaxNode? incomplete = type.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(context.CancellationToken))
                .FirstOrDefault(static declaration => declaration is not TypeDeclarationSyntax syntax || !syntax.Modifiers.Any(SyntaxKind.PartialKeyword));
            if (incomplete is not null)
            {
                return Invalid(NativeCallbackDeclarationDiagnostics.PartialContainer,
                    (incomplete as TypeDeclarationSyntax)?.Identifier.GetLocation() ?? incomplete.GetLocation(), type.Name);
            }
        }

        if (property.Type is not INamedTypeSymbol { IsUnmanagedType: true, IsGenericType: false, IsRefLikeType: false } value || !IsNativeValue(value))
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.PointerType, PropertyType());
        }

        AttributeData[] metadata = [.. value.GetAttributes().Where(static attribute =>
            attribute.AttributeClass?.ToDisplayString() == "Ankus.CompilerServices.NativeFunctionPointerAttribute")];
        if (metadata.Length != 1 || metadata[0].ConstructorArguments.Length != 1 ||
            metadata[0].ConstructorArguments[0].Value is not int index || index < 0)
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.PointerMetadata, PropertyType());
        }

        IMethodSymbol[] signatures = [.. value.GetMembers("Invoke").OfType<IMethodSymbol>().Where(static method =>
            !method.IsStatic && method.DeclaredAccessibility == Accessibility.Public && IsSignature(method))];
        if (signatures.Length != 1)
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.PointerInvocation, PropertyType());
        }

        if (!value.InstanceConstructors.Any(static method => method.DeclaredAccessibility == Accessibility.Public &&
            method.Parameters.Length == 1 && method.Parameters[0].RefKind == RefKind.None &&
            method.Parameters[0].Type is IPointerTypeSymbol { PointedAtType.SpecialType: SpecialType.System_Void }))
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.PointerConstructor, PropertyType());
        }

        if (marker.ConstructorArguments.Length != 1 || marker.ConstructorArguments[0].Value is not string name || string.IsNullOrWhiteSpace(name))
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.HandlerName, FunctionDeclarationDiagnostics.ConstructorArgument(marker, context.CancellationToken));
        }

        IMethodSymbol signature = signatures[0];
        IMethodSymbol[] named = [.. property.ContainingType.GetMembers(name).OfType<IMethodSymbol>()];
        IMethodSymbol[] handlers = [.. named.Where(method =>
            method.MethodKind == MethodKind.Ordinary && method.IsStatic && !method.IsAbstract && !method.IsExtern &&
            !method.IsAsync && method.PartialImplementationPart?.IsAsync != true &&
            (!method.IsPartialDefinition || method.PartialImplementationPart is not null) && IsSignature(method) &&
            !method.GetAttributes().Any(static attribute => attribute.AttributeClass?.ToDisplayString() ==
                "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute") &&
            SymbolEqualityComparer.Default.Equals(method.ReturnType, signature.ReturnType) &&
            method.Parameters.Length == signature.Parameters.Length &&
            method.Parameters.Zip(signature.Parameters, static (actual, expected) =>
                SymbolEqualityComparer.Default.Equals(actual.Type, expected.Type)).All(static same => same))];
        if (handlers.Length > 1)
        {
            return Invalid(NativeCallbackDeclarationDiagnostics.AmbiguousHandler, FunctionDeclarationDiagnostics.ConstructorArgument(marker, context.CancellationToken), name);
        }

        if (handlers.Length == 0)
        {
            if (named.Length == 0)
            {
                return Invalid(NativeCallbackDeclarationDiagnostics.MissingHandler, FunctionDeclarationDiagnostics.ConstructorArgument(marker, context.CancellationToken), name);
            }

            if (named.Length != 1)
            {
                return Invalid(NativeCallbackDeclarationDiagnostics.NoMatchingHandler, FunctionDeclarationDiagnostics.ConstructorArgument(marker, context.CancellationToken), name);
            }

            IMethodSymbol method = named[0];
            if (method.MethodKind != MethodKind.Ordinary)
            {
                return HandlerInvalid(NativeCallbackDeclarationDiagnostics.HandlerKind, method.Locations.FirstOrDefault());
            }

            if (!method.IsStatic)
            {
                return HandlerInvalid(NativeCallbackDeclarationDiagnostics.StaticHandler, method.Locations.FirstOrDefault());
            }

            if (method.IsExtern)
            {
                return HandlerInvalid(NativeCallbackDeclarationDiagnostics.ExternalHandler, Modifier(method, SyntaxKind.ExternKeyword));
            }

            if (method.IsAsync || method.PartialImplementationPart?.IsAsync == true)
            {
                return HandlerInvalid(NativeCallbackDeclarationDiagnostics.AsyncHandler, Modifier(method, SyntaxKind.AsyncKeyword));
            }

            if (method.IsPartialDefinition && method.PartialImplementationPart is null)
            {
                return HandlerInvalid(NativeCallbackDeclarationDiagnostics.PartialHandler, method.Locations.FirstOrDefault());
            }

            if (method.IsGenericMethod)
            {
                return HandlerInvalid(NativeCallbackDeclarationDiagnostics.GenericHandler, method.Locations.FirstOrDefault());
            }

            if (method.IsVararg)
            {
                return HandlerInvalid(NativeCallbackDeclarationDiagnostics.VariadicHandler, method.Locations.FirstOrDefault());
            }

            if (method.RefKind != RefKind.None)
            {
                return HandlerInvalid(NativeCallbackDeclarationDiagnostics.ReferenceResult, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
            }

            if (!method.ReturnsVoid && !IsValue(method.ReturnType))
            {
                return HandlerInvalid(NativeCallbackDeclarationDiagnostics.NativeResult, FunctionDeclarationDiagnostics.Result(method, context.CancellationToken));
            }

            foreach (IParameterSymbol parameter in method.Parameters)
            {
                if (parameter.RefKind != RefKind.None)
                {
                    return HandlerInvalid(NativeCallbackDeclarationDiagnostics.ReferenceArgument, parameter.Locations.FirstOrDefault(), parameter.Name);
                }

                if (!IsValue(parameter.Type))
                {
                    return HandlerInvalid(NativeCallbackDeclarationDiagnostics.NativeArgument, parameter.Locations.FirstOrDefault(), parameter.Name);
                }
            }

            AttributeData? unmanaged = method.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() ==
                "System.Runtime.InteropServices.UnmanagedCallersOnlyAttribute");
            if (unmanaged is not null)
            {
                return HandlerInvalid(NativeCallbackDeclarationDiagnostics.UnmanagedHandler, AttributeLocation(unmanaged));
            }

            if (!SymbolEqualityComparer.Default.Equals(method.ReturnType, signature.ReturnType))
            {
                return HandlerInvalid(NativeCallbackDeclarationDiagnostics.ResultMismatch,
                    FunctionDeclarationDiagnostics.Result(method, context.CancellationToken), signature.ReturnType.ToDisplayString());
            }

            if (method.Parameters.Length != signature.Parameters.Length)
            {
                return HandlerInvalid(NativeCallbackDeclarationDiagnostics.ArgumentCount, method.Locations.FirstOrDefault(),
                    signature.Parameters.Length.ToString(CultureInfo.InvariantCulture));
            }

            for (int argument = 0; argument < method.Parameters.Length; argument++)
            {
                IParameterSymbol parameter = method.Parameters[argument];
                if (!SymbolEqualityComparer.Default.Equals(parameter.Type, signature.Parameters[argument].Type))
                {
                    return HandlerInvalid(NativeCallbackDeclarationDiagnostics.ArgumentType, parameter.Locations.FirstOrDefault(),
                        parameter.Name, signature.Parameters[argument].Type.ToDisplayString());
                }
            }

            return Invalid(NativeCallbackDeclarationDiagnostics.NoMatchingHandler, FunctionDeclarationDiagnostics.ConstructorArgument(marker, context.CancellationToken), name);

            NativeCallbackDeclaration? HandlerInvalid(DiagnosticDescriptor descriptor, Location? location, params string[] details)
                => Invalid(descriptor, location, [method.Name, .. details]);
        }

        AttributeData? conditional = handlers[0].GetAttributes()
            .Concat(handlers[0].PartialImplementationPart?.GetAttributes() ?? [])
            .FirstOrDefault(static attribute => attribute.AttributeClass?.ToDisplayString() == "System.Diagnostics.ConditionalAttribute");
        if (conditional is not null)
        {
            context.Report(s_conditional, AttributeLocation(conditional) ?? handlers[0].Locations.FirstOrDefault(), property.Name, handlers[0].Name);
            return null;
        }

        return new(property, handlers[0], signature, index);

        NativeCallbackDeclaration? Invalid(DiagnosticDescriptor descriptor, Location? location, params string[] details)
        {
            context.Report(descriptor, location ?? property.Locations.FirstOrDefault(), [property.Name, .. details]);
            return null;
        }

        Location? AttributeLocation(AttributeData attribute) => attribute.ApplicationSyntaxReference?.GetSyntax(context.CancellationToken).GetLocation();

        Location? PropertyType() => property.DeclaringSyntaxReferences.Select(reference => reference.GetSyntax(context.CancellationToken))
            .OfType<PropertyDeclarationSyntax>().Select(static syntax => syntax.Type.GetLocation()).FirstOrDefault();

        Location? Modifier(IMethodSymbol method, SyntaxKind kind)
            => new[] { method, method.PartialImplementationPart, method.PartialDefinitionPart }.OfType<IMethodSymbol>()
                .SelectMany(static declaration => declaration.DeclaringSyntaxReferences)
                .Select(reference => reference.GetSyntax(context.CancellationToken)).OfType<MethodDeclarationSyntax>()
                .SelectMany(static declaration => declaration.Modifiers).Where(modifier => modifier.IsKind(kind))
                .Select(static modifier => modifier.GetLocation()).FirstOrDefault();
    }

    /// <summary>
    /// Distinguishes generated native values from primitives and enums for exact storage transport.
    /// </summary>
    /// <param name="type">The semantic value type.</param>
    /// <returns>Whether the value declares the native storage contract.</returns>
    internal static bool IsNativeValue(ITypeSymbol type)
        => type.AllInterfaces.Any(static contract => contract.ToDisplayString() == "Ankus.IPgNativeType");

    private static bool IsSignature(IMethodSymbol method)
        => !method.IsGenericMethod && !method.IsVararg && method.RefKind == RefKind.None &&
            (method.ReturnsVoid || IsValue(method.ReturnType)) &&
            method.Parameters.All(static parameter => parameter.RefKind == RefKind.None && IsValue(parameter.Type));

    private static bool IsValue(ITypeSymbol type)
        => type.IsUnmanagedType && !type.IsRefLikeType &&
            (type.SpecialType is SpecialType.System_Boolean or SpecialType.System_SByte or SpecialType.System_Byte or
                SpecialType.System_Int16 or SpecialType.System_UInt16 or SpecialType.System_Int32 or SpecialType.System_UInt32 or
                SpecialType.System_Int64 or SpecialType.System_UInt64 or SpecialType.System_IntPtr or SpecialType.System_UIntPtr or
                SpecialType.System_Single or SpecialType.System_Double || type.TypeKind is TypeKind.Enum or TypeKind.Pointer || IsNativeValue(type));
}
