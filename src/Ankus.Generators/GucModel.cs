using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.Generators;

/// <summary>
/// Owns configuration registration, property and hook contracts without retaining compiler objects.
/// </summary>
/// <param name="Property">The managed partial property declaration.</param>
/// <param name="Name">The exact PostgreSQL setting name.</param>
/// <param name="Kind">The validated native transport discriminator.</param>
/// <param name="Default">The exact primitive boot value or dense enum ordinal.</param>
/// <param name="ShortDescription">The retained short description.</param>
/// <param name="LongDescription">The optional retained long description.</param>
/// <param name="Context">The validated PostgreSQL setting context.</param>
/// <param name="Flags">The native option bits mapped through selected headers.</param>
/// <param name="Unit">The native unit selection.</param>
/// <param name="Minimum">The optional primitive lower bound.</param>
/// <param name="Maximum">The optional primitive upper bound.</param>
/// <param name="Labels">The ordered native labels and managed enum members.</param>
/// <param name="Check">The escaped optional check invocation target.</param>
/// <param name="Assign">The escaped optional assignment invocation target.</param>
/// <param name="Show">The escaped optional show invocation target.</param>
/// <param name="Callback">The assembly-scoped managed/native callback identity.</param>
internal sealed record GucModel(GucModel.PropertyContract Property, string Name, int Kind, GucConstant Default,
    string ShortDescription, string? LongDescription, int Context, int Flags, int Unit, GucConstant Minimum, GucConstant Maximum,
    EquatableArray<GucModel.Label> Labels, string? Check, string? Assign, string? Show, string Callback)
{
    /// <summary>
    /// Gets whether registration or access can call managed hook code.
    /// </summary>
    internal bool HasHooks => Check is not null || Assign is not null || Show is not null;

    /// <summary>
    /// Gets the exact fully qualified managed property type.
    /// </summary>
    internal string ManagedType => Property.Type;

    /// <summary>
    /// Detaches validated compiler metadata while semantic analysis owns its symbols.
    /// </summary>
    /// <param name="declaration">The transient validated semantic declaration.</param>
    /// <returns>The immutable configuration contract.</returns>
    internal static GucModel Create(GucDeclaration declaration)
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
            containers.Push(PgGucEmitter.AccessibilityText(type.DeclaredAccessibility) + " " + (type.IsStatic ? "static " : string.Empty) +
                "partial " + (type.IsRecord ? "record class" : "class") + " @" + type.Name + " {");
        }

        bool hides = property.DeclaringSyntaxReferences.Any(static reference => reference.GetSyntax() is PropertyDeclarationSyntax syntax &&
            syntax.Modifiers.Any(SyntaxKind.NewKeyword));
        string owner = property.ContainingType.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        return new(new(string.Join(".", spaces), new(containers), PgGucEmitter.AccessibilityText(property.DeclaredAccessibility),
            property.Name, declaration.ManagedType, property.NullableAnnotation == NullableAnnotation.Annotated, hides),
            declaration.Name, declaration.Kind, new(declaration.Default), declaration.ShortDescription, declaration.LongDescription,
            declaration.Context, declaration.Flags, declaration.Unit, new(declaration.Minimum), new(declaration.Maximum),
            new(declaration.Labels.Select(static label => new Label(label.Field.Name, label.Name, label.Ordinal, label.Hidden))),
            Target(declaration.Check), Target(declaration.Assign), Target(declaration.Show),
            PgFunctionGenerator.GetCallbackName(property.GetMethod!, "guc"));

        string? Target(IMethodSymbol? method) => method is null ? null : owner + ".@" + method.Name;
    }

    /// <summary>
    /// Contains only the lexical containers and type/nullability needed to implement a partial getter.
    /// </summary>
    /// <param name="Namespace">The escaped namespace, or empty for the global namespace.</param>
    /// <param name="Containers">The enclosing partial declarations in outermost-first order.</param>
    /// <param name="Accessibility">The property accessibility keyword.</param>
    /// <param name="Name">The unescaped property name.</param>
    /// <param name="Type">The fully qualified type with nullable annotations.</param>
    /// <param name="Nullable">Whether a string property permits null.</param>
    /// <param name="Hides">Whether the authored declaration uses the new modifier.</param>
    internal sealed record PropertyContract(string Namespace, EquatableArray<string> Containers, string Accessibility, string Name,
        string Type, bool Nullable, bool Hides);

    /// <summary>
    /// Retains one native enum label without retaining its compiler field symbol.
    /// </summary>
    /// <param name="Member">The unescaped managed enum member name.</param>
    /// <param name="Name">The native label text.</param>
    /// <param name="Ordinal">The dense transport value, shared by aliases.</param>
    /// <param name="Hidden">Whether PostgreSQL hides the label from completion.</param>
    internal sealed record Label(string Member, string Name, int Ordinal, bool Hidden);
}
