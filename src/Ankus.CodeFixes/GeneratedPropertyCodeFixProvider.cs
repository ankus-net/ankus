using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.CodeFixes;

/// <summary>
/// Turns a GUC or native callback auto-property into the static partial definition whose getter Ankus generates.
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
    /// Gets the generated-property diagnostics corrected by this provider.
    /// </summary>
    public override ImmutableArray<string> FixableDiagnosticIds => [StaticId, PartialId, ImplementationId, CallbackStaticId, CallbackPartialId];

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
            if (identifier.Parent is PropertyDeclarationSyntax property && property.Identifier == identifier &&
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
                else if (diagnostic.Id == ImplementationId && symbol.PartialDefinitionPart is not null)
                {
                    SyntaxNode removed = root.RemoveNode(property, SyntaxRemoveOptions.KeepNoTrivia)!;
                    context.RegisterCodeFix(CodeAction.Create($"Remove the implementation of '{symbol.Name}'",
                        _ => Task.FromResult(context.Document.WithSyntaxRoot(removed)), "Ankus.RemoveSettingImplementation"), diagnostic);
                }
            }
        }
    }

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
