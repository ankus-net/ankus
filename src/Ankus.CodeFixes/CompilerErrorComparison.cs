using System.Globalization;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;

namespace Ankus.CodeFixes;

/// <summary>
/// Distinguishes errors introduced by an editor correction from errors already present in the same declaration.
/// </summary>
internal static class CompilerErrorComparison
{
    /// <summary>
    /// Creates an action that keeps a single-document edit only if it adds no compiler error, as when other code still
    /// assigns a removed setter or needs a removed attribute. The comparison runs when the action is previewed or applied.
    /// </summary>
    /// <param name="document">The document before the correction.</param>
    /// <param name="corrected">The edited document root.</param>
    /// <param name="title">The action title.</param>
    /// <param name="equivalenceKey">The key that groups equivalent actions.</param>
    /// <returns>The checked action.</returns>
    internal static CodeAction CreateAction(Document document, SyntaxNode corrected, string title, string equivalenceKey)
        => CodeAction.Create(title, async token =>
        {
            Compilation? original = await document.Project.GetCompilationAsync(token).ConfigureAwait(false);
            Document edited = document.WithSyntaxRoot(corrected);
            Compilation? result = await edited.Project.GetCompilationAsync(token).ConfigureAwait(false);
            return original is null || result is null || IntroducesErrors(original, result, token) ? document : edited;
        }, equivalenceKey);

    /// <summary>
    /// Compares compiler errors only after the user requests a correction or its preview.
    /// </summary>
    /// <param name="original">The project before the complete correction.</param>
    /// <param name="corrected">The project after the complete correction.</param>
    /// <param name="cancellationToken">Cancels diagnostic collection.</param>
    /// <returns>Whether a new compiler error would be introduced.</returns>
    internal static bool IntroducesErrors(Compilation original, Compilation corrected, CancellationToken cancellationToken)
    {
        Dictionary<(string Id, string Message, string File, string Owner), int> allowed = original.GetDiagnostics(cancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error)
            .GroupBy(diagnostic => Key(diagnostic, original, cancellationToken))
            .ToDictionary(static group => group.Key, static group => group.Count());
        foreach (Diagnostic diagnostic in corrected.GetDiagnostics(cancellationToken)
            .Where(static diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            (string Id, string Message, string File, string Owner) key = Key(diagnostic, corrected, cancellationToken);
            if (!allowed.TryGetValue(key, out int count) || count == 0)
            {
                return true;
            }

            allowed[key] = count - 1;
        }

        return false;
    }

    /// <summary>
    /// Identifies an error by its declaration, which stays stable when an unrelated edit shifts its span.
    /// </summary>
    /// <param name="diagnostic">The compiler error.</param>
    /// <param name="compilation">The compilation containing its source.</param>
    /// <param name="cancellationToken">Cancels source binding.</param>
    /// <returns>The error and its stable source owner.</returns>
    private static (string Id, string Message, string File, string Owner) Key(Diagnostic diagnostic, Compilation compilation,
        CancellationToken cancellationToken)
    {
        string owner = string.Empty;
        SyntaxTree? tree = diagnostic.Location.SourceTree;
        if (tree is not null)
        {
            SemanticModel model = compilation.GetSemanticModel(tree);
            for (ISymbol? symbol = model.GetEnclosingSymbol(diagnostic.Location.SourceSpan.Start, cancellationToken);
                symbol is not null; symbol = symbol.ContainingSymbol)
            {
                if (symbol.GetDocumentationCommentId() is { } identity)
                {
                    owner = identity;
                    break;
                }
            }
        }

        return (diagnostic.Id, diagnostic.GetMessage(CultureInfo.InvariantCulture), tree?.FilePath ?? string.Empty, owner);
    }
}
