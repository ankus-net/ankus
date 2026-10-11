using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.CodeFixes;

/// <summary>
/// Turns a GUC auto-property into the static partial definition whose getter Ankus generates.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(SettingDeclarationCodeFixProvider))]
[Shared]
public sealed class SettingDeclarationCodeFixProvider : CodeFixProvider
{
    /// <summary>
    /// The instance-property diagnostic.
    /// </summary>
    private const string StaticId = "ANKUS170";

    /// <summary>
    /// The non-partial property diagnostic.
    /// </summary>
    private const string PartialId = "ANKUS173";

    /// <summary>
    /// The authored-implementation diagnostic.
    /// </summary>
    private const string ImplementationId = "ANKUS175";

    /// <summary>
    /// The non-partial container diagnostic.
    /// </summary>
    private const string ContainerPartialId = "ANKUS180";

    /// <summary>
    /// Gets the setting declaration diagnostics corrected by this provider.
    /// </summary>
    public override ImmutableArray<string> FixableDiagnosticIds => [StaticId, PartialId, ImplementationId, ContainerPartialId];

    /// <summary>
    /// Gets batch support for independent setting corrections.
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
                model.GetDeclaredSymbol(property, cancellationToken) is { } setting)
            {
                if (diagnostic.Id == StaticId && IsAutoGetter(property) && !setting.IsStatic && !setting.IsOverride && !setting.IsVirtual &&
                    !setting.IsAbstract && setting.ExplicitInterfaceImplementations.IsEmpty && !ImplementsInterface(setting) &&
                    !await DeclarationModifiers.HasInstanceReferenceAsync(context.Document.Project.Solution, setting, cancellationToken)
                        .ConfigureAwait(false))
                {
                    Register(context, diagnostic, $"Make '{setting.Name}' static", root, property, DeclarationModifiers.AddStatic(property));
                }
                else if (diagnostic.Id == PartialId && IsAutoGetter(property) && property.Initializer is null &&
                    !property.Modifiers.Any(SyntaxKind.PartialKeyword))
                {
                    Register(context, diagnostic, $"Make '{setting.Name}' partial", root, property, DeclarationModifiers.AddPartial(property));
                }
                else if (diagnostic.Id == ImplementationId && setting.PartialDefinitionPart is not null)
                {
                    SyntaxNode removed = root.RemoveNode(property, SyntaxRemoveOptions.KeepNoTrivia)!;
                    context.RegisterCodeFix(CodeAction.Create($"Remove the implementation of '{setting.Name}'",
                        _ => Task.FromResult(context.Document.WithSyntaxRoot(removed)), "Ankus.RemoveSettingImplementation"), diagnostic);
                }
            }
            else if (diagnostic.Id == ContainerPartialId && identifier.Parent is ClassDeclarationSyntax container &&
                container.Identifier == identifier && !container.Modifiers.Any(SyntaxKind.PartialKeyword))
            {
                Register(context, diagnostic, $"Make '{container.Identifier.ValueText}' partial", root, container,
                    DeclarationModifiers.AddPartial(container));
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
