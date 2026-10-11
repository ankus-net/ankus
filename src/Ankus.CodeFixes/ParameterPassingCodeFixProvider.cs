using System.Collections.Immutable;
using System.Composition;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.Operations;

namespace Ankus.CodeFixes;

/// <summary>
/// Passes a read-only reference parameter by value, which keeps every read the method makes.
/// </summary>
[ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(ParameterPassingCodeFixProvider))]
[Shared]
public sealed class ParameterPassingCodeFixProvider : CodeFixProvider
{
    /// <summary>
    /// The by-reference diagnostics for function, trigger and event callback, background worker, backend test and native
    /// callback handler parameters.
    /// </summary>
    private static readonly ImmutableArray<string> s_referenceParameterIds = ["ANKUS038", "ANKUS216", "ANKUS259", "ANKUS298", "ANKUS341"];

    /// <summary>
    /// Composes all selected parameters before one compiler comparison per changed project.
    /// </summary>
    private static readonly FixAllProvider s_fixAll = new ProjectCodeFixAllProvider("Pass parameters by value", ApplyProjectAsync);

    /// <summary>
    /// Gets the by-reference parameter diagnostics corrected by this provider.
    /// </summary>
    public override ImmutableArray<string> FixableDiagnosticIds => s_referenceParameterIds;

    /// <summary>
    /// Gets project batching for independent parameter corrections.
    /// </summary>
    /// <returns>The provider that verifies the complete correction once.</returns>
    public override FixAllProvider GetFixAllProvider() => s_fixAll;

    /// <summary>
    /// Registers the correction for <c>in</c> and <c>ref readonly</c> parameters; <c>ref</c> and <c>out</c> let the method
    /// write the caller's variable, so dropping them would change what the caller observes.
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

        foreach (Diagnostic diagnostic in context.Diagnostics.Where(static value => s_referenceParameterIds.Contains(value.Id)))
        {
            if (FindParameter(root, model, diagnostic, cancellationToken) is { } parameter)
            {
                context.RegisterCodeFix(CodeAction.Create($"Pass '{parameter.Name}' by value", async token =>
                    await PassByValueAsync(context.Document.Project.Solution, [parameter], token).ConfigureAwait(false)
                        ?? context.Document.Project.Solution, nameof(ParameterPassingCodeFixProvider)), diagnostic);
            }
        }
    }

    /// <summary>
    /// Resolves the diagnosed source parameter when only its read-only reference can be removed.
    /// </summary>
    /// <param name="root">The current syntax root.</param>
    /// <param name="model">Its semantic binding.</param>
    /// <param name="diagnostic">The diagnostic located on the parameter declaration, its modifier or its name.</param>
    /// <param name="cancellationToken">Cancels symbol binding.</param>
    /// <returns>The read-only reference parameter, or null when no correction preserves the method.</returns>
    private static IParameterSymbol? FindParameter(SyntaxNode root, SemanticModel model, Diagnostic diagnostic,
        CancellationToken cancellationToken)
        => root.FindToken(diagnostic.Location.SourceSpan.Start).Parent?.FirstAncestorOrSelf<ParameterSyntax>() is { } syntax &&
            syntax.Span.Contains(diagnostic.Location.SourceSpan) &&
            model.GetDeclaredSymbol(syntax, cancellationToken) is { RefKind: RefKind.In or RefKind.RefReadOnlyParameter } parameter &&
            parameter.ContainingSymbol is IMethodSymbol { MethodKind: MethodKind.Ordinary }
            ? parameter
            : null;

    /// <summary>
    /// Collects every selected parameter before applying and verifying the combined correction.
    /// </summary>
    /// <param name="project">The original editable project.</param>
    /// <param name="diagnostics">The selected source diagnostics by document.</param>
    /// <param name="cancellationToken">Cancels discovery, editing and final verification.</param>
    /// <returns>The verified correction or the original solution.</returns>
    private static async Task<Solution> ApplyProjectAsync(Project project,
        ImmutableDictionary<DocumentId, ImmutableArray<Diagnostic>> diagnostics, CancellationToken cancellationToken)
    {
        var parameters = new List<IParameterSymbol>();
        foreach (KeyValuePair<DocumentId, ImmutableArray<Diagnostic>> selection in diagnostics)
        {
            Document document = project.GetDocument(selection.Key)!;
            SyntaxNode? root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            SemanticModel? model = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (root is null || model is null)
            {
                continue;
            }

            foreach (Diagnostic diagnostic in selection.Value.Where(static value => s_referenceParameterIds.Contains(value.Id)))
            {
                if (FindParameter(root, model, diagnostic, cancellationToken) is { } parameter)
                {
                    parameters.Add(parameter);
                }
            }
        }

        return parameters.Count == 0 ? project.Solution
            : await PassByValueAsync(project.Solution, parameters, cancellationToken).ConfigureAwait(false) ?? project.Solution;
    }

    /// <summary>
    /// Removes the reference modifiers from each declaration of the parameters and from arguments that pass a variable to them,
    /// keeping the correction only when no changed project gains a compiler error.
    /// </summary>
    /// <param name="solution">The original solution.</param>
    /// <param name="parameters">The read-only reference parameters.</param>
    /// <param name="cancellationToken">Cancels reference search, editing and verification.</param>
    /// <returns>The verified solution, or null when the correction would break a declaration, conversion or caller.</returns>
    private static async Task<Solution?> PassByValueAsync(Solution solution, IReadOnlyList<IParameterSymbol> parameters,
        CancellationToken cancellationToken)
    {
        var targets = new HashSet<IParameterSymbol>(SymbolEqualityComparer.Default);
        var methods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (IParameterSymbol parameter in parameters)
        {
            var method = (IMethodSymbol)parameter.ContainingSymbol;
            IMethodSymbol?[] parts = [method, method.PartialDefinitionPart, method.PartialImplementationPart];
            foreach (IMethodSymbol part in parts.OfType<IMethodSymbol>())
            {
                targets.Add(part.Parameters[parameter.Ordinal]);
                methods.Add(part);
            }
        }

        var edits = new Dictionary<DocumentId, HashSet<SyntaxNode>>();
        foreach (IParameterSymbol target in targets)
        {
            foreach (SyntaxReference reference in target.DeclaringSyntaxReferences)
            {
                if (solution.GetDocument(reference.SyntaxTree) is not { } document)
                {
                    return null;
                }

                Add(edits, document.Id, await reference.GetSyntaxAsync(cancellationToken).ConfigureAwait(false));
            }
        }

        foreach (IMethodSymbol method in methods)
        {
            foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(method, solution, cancellationToken).ConfigureAwait(false))
            {
                foreach (ReferenceLocation location in referenced.Locations)
                {
                    SyntaxNode? root = await location.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                    SemanticModel? model = await location.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                    if (root?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true)
                        .FirstAncestorOrSelf<InvocationExpressionSyntax>() is not { } invocation || model is null ||
                        model.GetOperation(invocation, cancellationToken) is not IInvocationOperation operation)
                    {
                        continue;
                    }

                    foreach (IArgumentOperation argument in operation.Arguments)
                    {
                        if (argument.Parameter is { } bound && targets.Contains(bound.OriginalDefinition) &&
                            argument.Syntax is ArgumentSyntax passed && !passed.RefKindKeyword.IsKind(SyntaxKind.None))
                        {
                            Add(edits, location.Document.Id, passed);
                        }
                    }
                }
            }
        }

        Solution corrected = solution;
        foreach (KeyValuePair<DocumentId, HashSet<SyntaxNode>> edit in edits)
        {
            SyntaxNode root = (await solution.GetDocument(edit.Key)!.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false))!;
            corrected = corrected.WithDocumentSyntaxRoot(edit.Key, root.ReplaceNodes(edit.Value, static (_, rewritten) => rewritten switch
            {
                ParameterSyntax parameter => ByValue(parameter),
                ArgumentSyntax argument => ByValue(argument),
                _ => rewritten,
            }));
        }

        foreach (ProjectId changed in edits.Keys.Select(static document => document.ProjectId).Distinct())
        {
            Compilation? original = await solution.GetProject(changed)!.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            Compilation? result = await corrected.GetProject(changed)!.GetCompilationAsync(cancellationToken).ConfigureAwait(false);
            if (original is null || result is null || CompilerErrorComparison.IntroducesErrors(original, result, cancellationToken))
            {
                return null;
            }
        }

        return corrected;
    }

    /// <summary>
    /// Records one syntax edit for a document.
    /// </summary>
    /// <param name="edits">The edits by document.</param>
    /// <param name="document">The edited document.</param>
    /// <param name="node">The parameter or argument to edit.</param>
    private static void Add(Dictionary<DocumentId, HashSet<SyntaxNode>> edits, DocumentId document, SyntaxNode node)
    {
        if (!edits.TryGetValue(document, out HashSet<SyntaxNode>? nodes))
        {
            nodes = [];
            edits.Add(document, nodes);
        }

        nodes.Add(node);
    }

    /// <summary>
    /// Removes <c>in</c>, <c>ref</c>, <c>readonly</c> and the <c>scoped</c> that limited the reference, keeping <c>this</c>.
    /// </summary>
    /// <param name="parameter">The read-only reference parameter.</param>
    /// <returns>The by-value parameter with its leading layout preserved.</returns>
    private static ParameterSyntax ByValue(ParameterSyntax parameter)
    {
        SyntaxTokenList modifiers = parameter.Modifiers;
        SyntaxToken[] kept = [.. modifiers.Where(static token => token.Kind() is not (SyntaxKind.InKeyword or SyntaxKind.RefKeyword or
            SyntaxKind.ReadOnlyKeyword or SyntaxKind.ScopedKeyword))];
        SyntaxTriviaList leading = modifiers.Count == 0 || kept.Length != 0 && kept[0] == modifiers[0] ? default : modifiers[0].LeadingTrivia;
        ParameterSyntax result = parameter.WithModifiers(SyntaxFactory.TokenList(kept));
        if (leading.Count == 0)
        {
            return result;
        }

        SyntaxToken next = kept.Length != 0 ? result.Modifiers[0] : result.Type!.GetFirstToken();
        return result.ReplaceToken(next, next.WithLeadingTrivia(leading.AddRange(next.LeadingTrivia)));
    }

    /// <summary>
    /// Removes the argument's <c>in</c> or <c>ref</c> keyword, keeping any line layout it carried.
    /// </summary>
    /// <param name="argument">The argument passed by reference.</param>
    /// <returns>The by-value argument.</returns>
    private static ArgumentSyntax ByValue(ArgumentSyntax argument)
    {
        SyntaxTriviaList leading = argument.RefKindKeyword.LeadingTrivia;
        ExpressionSyntax expression = argument.Expression;
        return argument.WithRefKindKeyword(default)
            .WithExpression(expression.WithLeadingTrivia(leading.AddRange(expression.GetLeadingTrivia())));
    }
}
