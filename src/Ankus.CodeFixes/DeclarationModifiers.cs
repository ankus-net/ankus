using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.FindSymbols;

namespace Ankus.CodeFixes;

/// <summary>
/// Edits declaration modifiers while keeping each declaration's indentation and comments in place.
/// </summary>
internal static class DeclarationModifiers
{
    /// <summary>
    /// Identifies an accessibility modifier.
    /// </summary>
    /// <param name="token">A declaration modifier.</param>
    /// <returns>Whether the modifier states accessibility.</returns>
    internal static bool IsAccessibility(SyntaxToken token)
        => token.Kind() is SyntaxKind.PublicKeyword or SyntaxKind.PrivateKeyword or SyntaxKind.ProtectedKeyword or SyntaxKind.InternalKeyword;

    /// <summary>
    /// Inserts <c>static</c> after any accessibility modifiers, the conventional position.
    /// </summary>
    /// <param name="declaration">The declaration to edit.</param>
    /// <returns>The static declaration.</returns>
    internal static MemberDeclarationSyntax AddStatic(MemberDeclarationSyntax declaration)
    {
        int index = 0;
        while (index < declaration.Modifiers.Count && IsAccessibility(declaration.Modifiers[index]))
        {
            index++;
        }

        return Insert(declaration, SyntaxKind.StaticKeyword, index);
    }

    /// <summary>
    /// Appends <c>partial</c>, which C# requires immediately before the type or declared member type.
    /// </summary>
    /// <param name="declaration">The declaration to edit.</param>
    /// <returns>The partial declaration.</returns>
    internal static MemberDeclarationSyntax AddPartial(MemberDeclarationSyntax declaration)
        => Insert(declaration, SyntaxKind.PartialKeyword, declaration.Modifiers.Count);

    /// <summary>
    /// Replaces accessibility keywords with one <c>internal</c> keyword, or states it first when none is stated.
    /// </summary>
    /// <param name="declaration">The declaration to edit.</param>
    /// <returns>The declaration with internal accessibility.</returns>
    internal static MemberDeclarationSyntax WithInternal(MemberDeclarationSyntax declaration)
    {
        SyntaxTokenList modifiers = declaration.Modifiers;
        SyntaxToken[] access = [.. modifiers.Where(IsAccessibility)];
        if (access.Length == 0)
        {
            return Insert(declaration, SyntaxKind.InternalKeyword, 0);
        }

        SyntaxToken first = access[0];
        SyntaxToken replacement = SyntaxFactory.Token(first.LeadingTrivia, SyntaxKind.InternalKeyword, access[access.Length - 1].TrailingTrivia);
        return declaration.WithModifiers(SyntaxFactory.TokenList(modifiers.Where(token => !IsAccessibility(token) || token == first)
            .Select(token => token == first ? replacement : token)));
    }

    /// <summary>
    /// Removes one modifier, moving its leading trivia to the next token when it comes first.
    /// </summary>
    /// <param name="declaration">The declaration to edit.</param>
    /// <param name="index">The modifier position.</param>
    /// <returns>The edited declaration.</returns>
    internal static MemberDeclarationSyntax Remove(MemberDeclarationSyntax declaration, int index)
    {
        SyntaxToken modifier = declaration.Modifiers[index];
        if (index == 0)
        {
            SyntaxToken next = modifier.GetNextToken();
            declaration = declaration.ReplaceToken(next, next.WithLeadingTrivia(modifier.LeadingTrivia));
        }

        return declaration.WithModifiers(declaration.Modifiers.RemoveAt(index));
    }

    /// <summary>
    /// Finds whether any reference reaches the member through an instance receiver, which a static member cannot have.
    /// </summary>
    /// <param name="solution">The solution searched for references.</param>
    /// <param name="symbol">The member.</param>
    /// <param name="cancellationToken">Cancels the search.</param>
    /// <returns>Whether a reference qualifies the member with an instance.</returns>
    internal static async Task<bool> HasInstanceReferenceAsync(Solution solution, ISymbol symbol, CancellationToken cancellationToken)
    {
        foreach (ReferencedSymbol referenced in await SymbolFinder.FindReferencesAsync(symbol, solution, cancellationToken).ConfigureAwait(false))
        {
            foreach (ReferenceLocation location in referenced.Locations)
            {
                SyntaxNode? root = await location.Document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
                SemanticModel? model = await location.Document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
                SyntaxNode? name = root?.FindNode(location.Location.SourceSpan, getInnermostNodeForTie: true);
                if (name?.Parent is MemberAccessExpressionSyntax access && access.Name == name && model is not null &&
                    model.GetSymbolInfo(access.Expression, cancellationToken).Symbol is not INamedTypeSymbol)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Inserts a keyword at a modifier position, moving leading trivia to it when it becomes the first token.
    /// </summary>
    /// <param name="declaration">The declaration to edit.</param>
    /// <param name="kind">The modifier keyword.</param>
    /// <param name="index">The modifier position.</param>
    /// <returns>The edited declaration.</returns>
    private static MemberDeclarationSyntax Insert(MemberDeclarationSyntax declaration, SyntaxKind kind, int index)
    {
        SyntaxTokenList modifiers = declaration.Modifiers;
        SyntaxTriviaList space = SyntaxFactory.TriviaList(SyntaxFactory.Space);
        if (modifiers.Count == 0)
        {
            // The first token after any attributes carries the declaration's indentation.
            SyntaxToken next = declaration.AttributeLists.Count == 0
                ? declaration.GetFirstToken()
                : declaration.AttributeLists.Last().GetLastToken().GetNextToken();
            MemberDeclarationSyntax moved = declaration.ReplaceToken(next, next.WithLeadingTrivia());
            return moved.WithModifiers(SyntaxFactory.TokenList(SyntaxFactory.Token(next.LeadingTrivia, kind, space)));
        }

        if (index == 0)
        {
            SyntaxToken keyword = SyntaxFactory.Token(modifiers[0].LeadingTrivia, kind, space);
            return declaration.WithModifiers(modifiers.Replace(modifiers[0], modifiers[0].WithLeadingTrivia()).Insert(0, keyword));
        }

        return declaration.WithModifiers(modifiers.Insert(index, SyntaxFactory.Token(SyntaxFactory.TriviaList(), kind, space)));
    }
}
