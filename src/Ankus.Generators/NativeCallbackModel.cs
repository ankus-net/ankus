using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Retains a validated callback's lexical and exact transport contracts without compiler objects.
/// </summary>
/// <param name="Namespace">The escaped namespace, or empty for the global namespace.</param>
/// <param name="Containers">The outermost-first partial containing declarations.</param>
/// <param name="Accessibility">The property accessibility keywords.</param>
/// <param name="Name">The managed property name.</param>
/// <param name="ManagedType">The fully qualified native callback value type.</param>
/// <param name="Hides">Whether the property uses the new modifier.</param>
/// <param name="Identity">The assembly-qualified callback registration identity.</param>
/// <param name="NativeSignature">The selected native prototype index.</param>
/// <param name="Handler">The escaped fully qualified static invocation target.</param>
/// <param name="Arguments">The exact ordered argument transport contracts.</param>
/// <param name="Result">The result contract, or null for void.</param>
internal sealed record NativeCallbackModel(string Namespace, EquatableArray<string> Containers, string Accessibility,
    string Name, string ManagedType, bool Hides, string Identity, int NativeSignature, string Handler,
    EquatableArray<NativeCallbackModel.ValueContract> Arguments, NativeCallbackModel.ValueContract? Result)
{
    /// <summary>
    /// Detaches a complete validated callback while semantic analysis owns its symbols.
    /// </summary>
    /// <param name="declaration">The validated transient callback declaration.</param>
    /// <returns>The immutable lexical and transport contract.</returns>
    internal static NativeCallbackModel Create(NativeCallbackDeclaration declaration)
    {
        IPropertySymbol property = declaration.Property;
        var spaces = new Stack<string>();
        for (INamespaceSymbol space = property.ContainingNamespace; !space.IsGlobalNamespace; space = space.ContainingNamespace)
        {
            spaces.Push("@" + space.Name);
        }

        var containers = new Stack<string>();
        for (INamedTypeSymbol? type = property.ContainingType; type is not null; type = type.ContainingType)
        {
            string kind = type.TypeKind == TypeKind.Struct ? "struct" : "class";
            containers.Push(PgNativeCallbackEmitter.Access(type.DeclaredAccessibility) + " " +
                (type.IsStatic ? "static " : string.Empty) + "unsafe partial " +
                (type.IsRecord ? "record " : string.Empty) + kind + " @" + type.Name);
        }

        bool hides = property.DeclaringSyntaxReferences.Any(static reference => reference.GetSyntax() is PropertyDeclarationSyntax syntax &&
            syntax.Modifiers.Any(SyntaxKind.NewKeyword));
        return new(string.Join(".", spaces), new(containers), PgNativeCallbackEmitter.Access(property.DeclaredAccessibility),
            property.Name, NameOf(property.Type), hides, declaration.Identity, declaration.NativeSignature,
            NameOf(declaration.Handler.ContainingType) + ".@" + declaration.Handler.Name,
            new(declaration.Signature.Parameters.Select(static parameter => Value(parameter.Type))),
            declaration.Signature.ReturnsVoid ? null : Value(declaration.Signature.ReturnType));
    }

    /// <summary>
    /// Captures the name and native aggregate transport distinction of one validated value.
    /// </summary>
    private static ValueContract Value(ITypeSymbol type)
        => new(NameOf(type), NativeCallbackDeclaration.IsNativeValue(type), type.TypeKind == TypeKind.Pointer);

    /// <summary>
    /// Formats an exact escaped managed type while its compiler symbol remains transient.
    /// </summary>
    private static string NameOf(ITypeSymbol type) => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

    /// <summary>
    /// Identifies exact primitive, enum or generated aggregate storage transport.
    /// </summary>
    /// <param name="Name">The escaped fully qualified managed type.</param>
    /// <param name="Native">Whether transport uses selected-header native size and binding checks.</param>
    /// <param name="Pointer">
    /// Whether the address uses pointer-sized transport because its type is illegal as a generic type argument.
    /// </param>
    internal sealed record ValueContract(string Name, bool Native, bool Pointer)
    {
        /// <summary>
        /// Gets the generic-compatible type used inside the native transport frame.
        /// </summary>
        internal string Storage => Pointer ? "nint" : Name;

        /// <summary>
        /// Gets the checked native size expression or exact managed primitive storage size.
        /// </summary>
        internal string Size => Native ? $"global::Ankus.CompilerServices.NativeRawCallback.NativeSize<{Name}>()" : $"sizeof({Name})";
    }
}
