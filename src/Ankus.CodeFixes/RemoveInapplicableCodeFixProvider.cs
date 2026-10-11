using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Ankus.CodeFixes;

/// <summary>
/// Removes an attribute or named option that cannot apply where it is written, such as <c>Conditional</c> on a
/// PostgreSQL entry, <c>Flags</c> on a PostgreSQL enum or <c>Rows</c> on a function that returns no set.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(RemoveInapplicableCodeFixProvider))]
[Shared]
public sealed class RemoveInapplicableCodeFixProvider : CodeFixProvider
{
    /// <summary>
    /// The diagnostics reported at an attribute whose removal is the correction: <c>Conditional</c> and
    /// <c>UnmanagedCallersOnly</c> on entries, <c>Flags</c> on enums, column names without a set result, and SQL value
    /// metadata where no SQL value exists or a datum mapping already fixes the type.
    /// </summary>
    private static readonly ImmutableArray<string> s_attributeIds =
    [
        "ANKUS151", "ANKUS225", "ANKUS245", "ANKUS246", "ANKUS248", "ANKUS266", "ANKUS267", "ANKUS270", "ANKUS271", "ANKUS276",
        "ANKUS277", "ANKUS279", "ANKUS343", "ANKUS396", "ANKUS524",
    ];

    /// <summary>
    /// The diagnostics reported at a named attribute option whose removal is the correction.
    /// </summary>
    private static readonly ImmutableArray<string> s_optionIds =
    [
        "ANKUS053", "ANKUS069", "ANKUS070", "ANKUS071", "ANKUS104", "ANKUS106", "ANKUS127", "ANKUS128", "ANKUS189", "ANKUS226",
        "ANKUS227", "ANKUS384",
    ];

    /// <summary>
    /// The diagnostics reported at a whole type binding whose <c>Column</c> or <c>Element</c> selector is the correction.
    /// </summary>
    private static readonly ImmutableArray<string> s_selectorIds = ["ANKUS411", "ANKUS412"];

    /// <summary>
    /// Gets the diagnostics corrected by removing what they point at.
    /// </summary>
    public override ImmutableArray<string> FixableDiagnosticIds => [.. s_attributeIds, .. s_optionIds, .. s_selectorIds];

    /// <summary>
    /// Gets batch support for independent removals.
    /// </summary>
    /// <returns>The standard Roslyn batch provider.</returns>
    public override FixAllProvider GetFixAllProvider() => WellKnownFixAllProviders.BatchFixer;

    /// <summary>
    /// Registers a removal of the diagnosed attribute or option.
    /// </summary>
    /// <param name="context">The document, diagnostics and cancellation context.</param>
    /// <returns>The registration operation.</returns>
    public override async Task RegisterCodeFixesAsync(CodeFixContext context)
    {
        SyntaxNode? root = await context.Document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
        if (root is null)
        {
            return;
        }

        foreach (Diagnostic diagnostic in context.Diagnostics)
        {
            SyntaxNode node = root.FindNode(diagnostic.Location.SourceSpan, getInnermostNodeForTie: true);
            if (s_optionIds.Contains(diagnostic.Id) && node.FirstAncestorOrSelf<AttributeArgumentSyntax>() is { NameEquals: { } name } option)
            {
                Register(context, diagnostic, $"Remove '{name.Name.Identifier.ValueText}'", RemoveOption(root, option));
            }
            else if (s_selectorIds.Contains(diagnostic.Id) && node.FirstAncestorOrSelf<AttributeSyntax>() is { ArgumentList: { } arguments } &&
                arguments.Arguments.FirstOrDefault(argument => argument.NameEquals?.Name.Identifier.ValueText ==
                    (diagnostic.Id == "ANKUS411" ? "Column" : "Element")) is { NameEquals: { } selector } selected)
            {
                Register(context, diagnostic, $"Remove '{selector.Name.Identifier.ValueText}'", RemoveOption(root, selected));
            }
            else if (s_attributeIds.Contains(diagnostic.Id) && node.FirstAncestorOrSelf<AttributeSyntax>() is { } attribute)
            {
                Register(context, diagnostic, $"Remove '{attribute.Name}'", RemoveAttribute(root, attribute));
            }
        }
    }

    /// <summary>
    /// Registers an edit that is kept only if it adds no compiler error, as when another member still converts the
    /// method to an unmanaged function pointer.
    /// </summary>
    /// <param name="context">The registration context.</param>
    /// <param name="diagnostic">The diagnostic corrected.</param>
    /// <param name="title">The action title.</param>
    /// <param name="corrected">The edited root.</param>
    private static void Register(CodeFixContext context, Diagnostic diagnostic, string title, SyntaxNode corrected)
        => context.RegisterCodeFix(CompilerErrorComparison.CreateAction(context.Document, corrected, title, "Ankus.Remove." + diagnostic.Id),
            diagnostic);

    /// <summary>
    /// Removes one named option, and the parentheses when no argument remains.
    /// </summary>
    /// <param name="root">The document root.</param>
    /// <param name="option">The named option.</param>
    /// <returns>The edited root.</returns>
    internal static SyntaxNode RemoveOption(SyntaxNode root, AttributeArgumentSyntax option)
    {
        var arguments = (AttributeArgumentListSyntax)option.Parent!;
        var attribute = (AttributeSyntax)arguments.Parent!;
        SeparatedSyntaxList<AttributeArgumentSyntax> remaining = arguments.Arguments.Remove(option);
        return root.ReplaceNode(attribute, remaining.Count == 0
            ? attribute.WithArgumentList(null).WithTrailingTrivia(arguments.GetTrailingTrivia())
            : attribute.WithArgumentList(arguments.WithArguments(remaining)));
    }

    /// <summary>
    /// Removes one attribute, and its list when no attribute remains, keeping any comments before the list and the layout
    /// of the following code.
    /// </summary>
    /// <param name="root">The document root.</param>
    /// <param name="attribute">The attribute.</param>
    /// <returns>The edited root.</returns>
    private static SyntaxNode RemoveAttribute(SyntaxNode root, AttributeSyntax attribute)
    {
        var list = (AttributeListSyntax)attribute.Parent!;
        if (list.Attributes.Count > 1)
        {
            return root.ReplaceNode(list, list.WithAttributes(list.Attributes.Remove(attribute)));
        }

        // The list's leading trivia carries its indentation and any documentation comment. On its own line the list is
        // removed with its line break, and the following token takes that trivia in place of its own indentation.
        SyntaxToken next = list.GetLastToken().GetNextToken();
        bool ownLine = list.GetTrailingTrivia().Any(static trivia => trivia.IsKind(SyntaxKind.EndOfLineTrivia));
        SyntaxTriviaList leading = ownLine ? list.GetLeadingTrivia() : list.GetLeadingTrivia().AddRange(next.LeadingTrivia);
        SyntaxNode tracked = root.TrackNodes(list);
        tracked = tracked.ReplaceToken(tracked.FindToken(next.SpanStart), next.WithLeadingTrivia(leading));
        return tracked.RemoveNode(tracked.GetCurrentNode(list)!, SyntaxRemoveOptions.KeepNoTrivia)!;
    }
}
