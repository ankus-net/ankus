using System.Collections.Immutable;
using System.Composition;
using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Simplification;

namespace Ankus.CodeFixes;

/// <summary>
/// Turns a GUC or native callback auto-property into the static partial definition whose getter Ankus generates, and
/// corrects a GUC property type, nullable default or reversed bounds that its attribute determines.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(GeneratedPropertyCodeFixProvider))]
[Shared]
public sealed class GeneratedPropertyCodeFixProvider : CodeFixProvider
{
    /// <summary>
    /// The instance GUC property diagnostic.
    /// </summary>
    private const string StaticId = "ANKUS170";

    /// <summary>
    /// The non-partial GUC property diagnostic.
    /// </summary>
    private const string PartialId = "ANKUS173";

    /// <summary>
    /// The authored GUC implementation diagnostic.
    /// </summary>
    private const string ImplementationId = "ANKUS175";

    /// <summary>
    /// The instance native callback property diagnostic.
    /// </summary>
    private const string CallbackStaticId = "ANKUS312";

    /// <summary>
    /// The non-partial native callback property diagnostic.
    /// </summary>
    private const string CallbackPartialId = "ANKUS317";

    /// <summary>
    /// The authored native callback implementation diagnostic.
    /// </summary>
    private const string CallbackImplementationId = "ANKUS318";

    /// <summary>
    /// The GUC property setter or init accessor diagnostic.
    /// </summary>
    private const string AccessorId = "ANKUS171";

    /// <summary>
    /// The native callback property setter or init accessor diagnostic.
    /// </summary>
    private const string CallbackAccessorId = "ANKUS316";

    /// <summary>
    /// The GUC property type that differs from its attribute's setting kind.
    /// </summary>
    private const string TypeId = "ANKUS183";

    /// <summary>
    /// The reversed integer GUC bounds diagnostic.
    /// </summary>
    private const string IntegerBoundsId = "ANKUS190";

    /// <summary>
    /// The invalid real GUC bounds diagnostic.
    /// </summary>
    private const string RealBoundsId = "ANKUS192";

    /// <summary>
    /// The null string GUC default on a non-nullable property.
    /// </summary>
    private const string NullableId = "ANKUS195";

    /// <summary>
    /// Gets the generated-property diagnostics corrected by this provider.
    /// </summary>
    public override ImmutableArray<string> FixableDiagnosticIds =>
    [
        StaticId, PartialId, ImplementationId, CallbackStaticId, CallbackPartialId, CallbackImplementationId, AccessorId, CallbackAccessorId,
        TypeId, IntegerBoundsId, RealBoundsId, NullableId,
    ];

    /// <summary>
    /// Gets batch support for independent property corrections.
    /// </summary>
    /// <returns>The standard Roslyn batch provider.</returns>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <summary>
    /// Registers a correction only where it compiles without further edits.
    /// </summary>
    /// <param name="context">The document, diagnostics and cancellation context.</param>
    /// <returns>The registration operation.</returns>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        CancellationToken cancellationToken = context.CancellationToken;
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
        SemanticModel? model = await context.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
        if (root is null || model is null)
        {
            return;
        }

        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            SyntaxToken identifier = root.FindToken(diagnostic.Location.SourceSpan.Start);
            if (diagnostic.Id is AccessorId or CallbackAccessorId && identifier.Parent is AccessorDeclarationSyntax accessor &&
                accessor.Keyword == identifier && accessor.Parent is AccessorListSyntax accessors)
            {
                RegisterAccessor(context, diagnostic, root, accessors, accessor);
            }
            else if (diagnostic.Id is TypeId or NullableId && root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true) is TypeSyntax type &&
                type.Parent is PropertyDeclarationSyntax { Type: var declared } typed && declared == type &&
                model.GetDeclaredSymbol(typed, cancellationToken) is { } setting)
            {
                RegisterType(context, diagnostic, model, root, type, setting);
            }
            else if (diagnostic.Id is IntegerBoundsId or RealBoundsId &&
                root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true).FirstAncestorOrSelf<AttributeSyntax>() is
                    { ArgumentList: { } arguments } && Bound(arguments, "Minimum") is { } minimum && Bound(arguments, "Maximum") is { } maximum &&
                model.GetConstantValue(minimum.Expression, cancellationToken) is { HasValue: true, Value: { } lower } &&
                model.GetConstantValue(maximum.Expression, cancellationToken) is { HasValue: true, Value: { } upper } &&
                Convert.ToDouble(lower, CultureInfo.InvariantCulture) > Convert.ToDouble(upper, CultureInfo.InvariantCulture))
            {
                SyntaxNode swapped = root.ReplaceNodes([minimum.Expression, maximum.Expression], (original, _) => original == minimum.Expression
                    ? maximum.Expression.WithTriviaFrom(original)
                    : minimum.Expression.WithTriviaFrom(original));
                context.RegisterCodeFix(CodeAction.Create("Swap 'Minimum' and 'Maximum'",
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(swapped)), "Ankus." + diagnostic.Id), diagnostic);
            }
            else if (identifier.Parent is PropertyDeclarationSyntax property && property.Identifier == identifier &&
                model.GetDeclaredSymbol(property, cancellationToken) is { } symbol)
            {
                if (diagnostic.Id is StaticId or CallbackStaticId && IsAutoGetter(property) && !symbol.IsStatic && !symbol.IsOverride &&
                    !symbol.IsVirtual && !symbol.IsAbstract && symbol.ExplicitInterfaceImplementations.IsEmpty && !ImplementsInterface(symbol) &&
                    !await DeclarationModifiers.HasInstanceReferenceAsync(context.Document.Project.Solution, symbol, cancellationToken)
                        .ConfigureAwait(false))
                {
                    Register(context, diagnostic, $"Make '{symbol.Name}' static", root, property, DeclarationModifiers.AddStatic(property));
                }
                else if (diagnostic.Id is PartialId or CallbackPartialId && IsAutoGetter(property) && property.Initializer is null &&
                    !property.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    Register(context, diagnostic, $"Make '{symbol.Name}' partial", root, property, DeclarationModifiers.AddPartial(property));
                }
                else if (diagnostic.Id is ImplementationId or CallbackImplementationId && symbol.PartialDefinitionPart is not null)
                {
                    SyntaxNode removed = root.RemoveNode(property, SyntaxRemoveOptions.KeepNoTrivia)!;
                    context.RegisterCodeFix(CodeAction.Create($"Remove the implementation of '{symbol.Name}'",
                        _ => Task.FromResult(context.Document.WithSyntaxRoot(removed)), "Ankus." + diagnostic.Id), diagnostic);
                }
            }
        }
    }

    /// <summary>
    /// Removes a setter or init accessor beside a getter, or turns a lone automatic one into the getter. The edit is kept
    /// only if no other code assigns the property.
    /// </summary>
    /// <param name="context">The registration context.</param>
    /// <param name="diagnostic">The accessor diagnostic.</param>
    /// <param name="root">The current document root.</param>
    /// <param name="accessors">The property's accessor list.</param>
    /// <param name="accessor">The diagnosed setter or init accessor.</param>
    private static void RegisterAccessor(CodeFixContext context, Diagnostic diagnostic, SyntaxNode root, AccessorListSyntax accessors,
        AccessorDeclarationSyntax accessor)
    {
        string keyword = accessor.Keyword.ValueText;
        if (accessors.Accessors.Any(static item => item.IsKind(SyntaxKind.GetAccessorDeclaration)))
        {
            context.RegisterCodeFix(CompilerErrorComparison.CreateAction(context.Document,
                root.ReplaceNode(accessors, accessors.WithAccessors(accessors.Accessors.Remove(accessor))),
                $"Remove the '{keyword}' accessor", "Ankus." + diagnostic.Id), diagnostic);
        }
        else if (accessor.Body is null && accessor.ExpressionBody is null)
        {
            SyntaxTriviaList leading = accessor.Modifiers.Count == 0 ? accessor.Keyword.LeadingTrivia : accessor.Modifiers[0].LeadingTrivia;
            AccessorDeclarationSyntax getter = SyntaxFactory.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                .WithKeyword(SyntaxFactory.Token(leading, SyntaxKind.GetKeyword, accessor.Keyword.TrailingTrivia))
                .WithSemicolonToken(accessor.SemicolonToken);
            context.RegisterCodeFix(CompilerErrorComparison.CreateAction(context.Document, root.ReplaceNode(accessor, getter),
                $"Replace '{keyword}' with 'get'", "Ankus." + diagnostic.Id), diagnostic);
        }
    }

    /// <summary>
    /// Replaces a GUC property type with the one its attribute requires, or annotates a string property whose default is null.
    /// The type edit is kept only if every use of the property still compiles.
    /// </summary>
    /// <param name="context">The registration context.</param>
    /// <param name="diagnostic">The type diagnostic.</param>
    /// <param name="model">The document's semantic model.</param>
    /// <param name="root">The current document root.</param>
    /// <param name="type">The authored property type.</param>
    /// <param name="property">The bound property.</param>
    private static void RegisterType(CodeFixContext context, Diagnostic diagnostic, SemanticModel model, SyntaxNode root, TypeSyntax type,
        IPropertySymbol property)
    {
        if (property.GetAttributes().FirstOrDefault(static attribute => attribute.AttributeClass?.ContainingNamespace?.Name == "Ankus" &&
            attribute.AttributeClass.Name.StartsWith("PgGuc", StringComparison.Ordinal)) is not { ConstructorArguments.Length: > 1 } attribute)
        {
            return;
        }

        bool annotations = model.GetNullableContext(type.SpanStart).AnnotationsEnabled();
        if (diagnostic.Id == NullableId)
        {
            if (annotations && type is not NullableTypeSyntax)
            {
                SyntaxNode annotated = root.ReplaceNode(type, SyntaxFactory.NullableType(type.WithoutTrivia()).WithTriviaFrom(type));
                context.RegisterCodeFix(CodeAction.Create("Change the type to 'string?'",
                    _ => Task.FromResult(context.Document.WithSyntaxRoot(annotated)), "Ankus." + diagnostic.Id), diagnostic);
            }

            return;
        }

        TypedConstant value = attribute.ConstructorArguments[1];
        string? expected = attribute.AttributeClass!.Name switch
        {
            "PgGucBoolAttribute" => "bool",
            "PgGucIntAttribute" => "int",
            "PgGucRealAttribute" => "double",
            "PgGucStringAttribute" => value.IsNull && annotations ? "string?" : "string",
            "PgGucEnumAttribute" when value.Type is INamedTypeSymbol { TypeKind: TypeKind.Enum } enumeration =>
                enumeration.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat),
            _ => null,
        };
        if (expected is null)
        {
            return;
        }

        TypeSyntax replacement = SyntaxFactory.ParseTypeName(expected).WithTriviaFrom(type).WithAdditionalAnnotations(Simplifier.Annotation);
        string title = expected.StartsWith("global::", StringComparison.Ordinal) ? value.Type!.ToMinimalDisplayString(model, type.SpanStart) : expected;
        context.RegisterCodeFix(CompilerErrorComparison.CreateAction(context.Document, root.ReplaceNode(type, replacement),
            $"Change the type to '{title}'", "Ankus." + diagnostic.Id), diagnostic);
    }

    /// <summary>
    /// Finds a named GUC bound in an attribute.
    /// </summary>
    /// <param name="arguments">The attribute arguments.</param>
    /// <param name="name">The bound name.</param>
    /// <returns>The named argument, or null when the attribute omits it.</returns>
    private static AttributeArgumentSyntax? Bound(AttributeArgumentListSyntax arguments, string name)
        => arguments.Arguments.FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText == name);

    /// <summary>
    /// Requires accessors without bodies, which read no state and leave the getter for Ankus to generate.
    /// </summary>
    /// <param name="property">The authored property.</param>
    /// <returns>Whether the property is an automatic property without an expression body.</returns>
    private static bool IsAutoGetter(PropertyDeclarationSyntax property)
        => property.ExpressionBody is null && property.AccessorList is { } accessors &&
            accessors.Accessors.All(static accessor => accessor.Body is null && accessor.ExpressionBody is null);

    /// <summary>
    /// Finds whether the property implicitly implements an interface member, which an instance property must do.
    /// </summary>
    /// <param name="property">The bound property.</param>
    /// <returns>Whether any interface member of its type resolves to it.</returns>
    private static bool ImplementsInterface(IPropertySymbol property)
        => property.ContainingType.AllInterfaces.SelectMany(static type => type.GetMembers()).Any(member =>
            SymbolEqualityComparer.Default.Equals(property.ContainingType.FindImplementationForInterfaceMember(member), property));

    /// <summary>
    /// Registers a replacement of one declaration in the current document.
    /// </summary>
    /// <param name="context">The registration context.</param>
    /// <param name="diagnostic">The diagnostic corrected.</param>
    /// <param name="title">The action title.</param>
    /// <param name="root">The current document root.</param>
    /// <param name="original">The diagnosed declaration.</param>
    /// <param name="corrected">Its replacement.</param>
    private static void Register(CodeFixContext context, Diagnostic diagnostic, string title, SyntaxNode root,
        MemberDeclarationSyntax original, MemberDeclarationSyntax corrected)
    {
        SyntaxNode replaced = root.ReplaceNode(original, corrected);
        context.RegisterCodeFix(CodeAction.Create(title, _ => Task.FromResult(context.Document.WithSyntaxRoot(replaced)),
            "Ankus." + diagnostic.Id), diagnostic);
    }
}
