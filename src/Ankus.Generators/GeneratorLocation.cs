using System;
using System.Globalization;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Ankus.Generators;

/// <summary>
/// Retains declaration-relative diagnostic coordinates without keeping a syntax tree or an old compilation alive.
/// </summary>
/// <param name="Path">The source tree's physical path.</param>
/// <param name="TreeOccurrence">The occurrence among source trees sharing that exact path.</param>
/// <param name="MemberKey">The stable declaration-header path, or null for a compilation-unit location.</param>
/// <param name="Span">The exact source span relative to its enclosing declaration.</param>
internal readonly record struct GeneratorLocation(string Path, int TreeOccurrence, string? MemberKey, TextSpan Span)
{
    /// <summary>
    /// Detaches a source location from the semantic analysis that produced it.
    /// </summary>
    /// <param name="location">The optional authored location.</param>
    /// <param name="compilation">The compilation that owns the source tree.</param>
    /// <returns>The coordinates, or null for a location outside the compilation.</returns>
    internal static GeneratorLocation? Create(Location? location, Compilation compilation)
    {
        if (location?.SourceTree is not { } source)
        {
            return null;
        }

        int occurrence = 0;
        foreach (SyntaxTree tree in compilation.SyntaxTrees)
        {
            if (tree == source)
            {
                SyntaxNode root = tree.GetRoot();
                MemberDeclarationSyntax? member = root.FindNode(location.SourceSpan).AncestorsAndSelf()
                    .OfType<MemberDeclarationSyntax>().FirstOrDefault();
                SyntaxNode anchor = member ?? root;
                return new(tree.FilePath, occurrence, member is null ? null : Key(member),
                    new TextSpan(location.SourceSpan.Start - anchor.SpanStart, location.SourceSpan.Length));
            }

            if (tree.FilePath == source.FilePath)
            {
                occurrence++;
            }
        }

        return null;
    }

    /// <summary>
    /// Reattaches the coordinates to the current tree, preserving line mappings and editor navigation.
    /// </summary>
    /// <param name="compilation">The current compilation used to report diagnostics.</param>
    /// <returns>The current source location.</returns>
    internal Location Resolve(Compilation compilation)
    {
        string path = Path;
        SyntaxTree tree = compilation.SyntaxTrees.Where(tree => tree.FilePath == path).ElementAt(TreeOccurrence);
        SyntaxNode root = tree.GetRoot();
        string? memberKey = MemberKey;
        SyntaxNode anchor = memberKey is null ? root : Members(root).Single(member => Key(member) == memberKey);
        return Location.Create(tree, new TextSpan(anchor.SpanStart + Span.Start, Span.Length));
    }

    /// <summary>
    /// Builds a declaration path from whitespace-independent headers and same-header sibling occurrences.
    /// </summary>
    /// <param name="member">The declaration whose stable source anchor is required.</param>
    /// <returns>The declaration key, independent of unrelated member insertion and implementation-body length.</returns>
    internal static string Key(MemberDeclarationSyntax member)
    {
        var key = new StringBuilder();
        foreach (MemberDeclarationSyntax part in member.AncestorsAndSelf().OfType<MemberDeclarationSyntax>().Reverse())
        {
            string header = Header(part);
            int occurrence = 0;
            if (part.Parent is SyntaxNode parent)
            {
                foreach (MemberDeclarationSyntax sibling in parent.ChildNodes().OfType<MemberDeclarationSyntax>())
                {
                    if (sibling == part)
                    {
                        break;
                    }

                    if (Header(sibling) == header)
                    {
                        occurrence++;
                    }
                }
            }

            key.Append(header.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(header)
                .Append('#').Append(occurrence.ToString(CultureInfo.InvariantCulture)).Append(';');
        }

        return key.ToString();
    }

    /// <summary>
    /// Enumerates authored declarations without descending into implementation bodies.
    /// </summary>
    /// <param name="root">The current compilation-unit syntax.</param>
    /// <returns>The ordered declaration anchors, independent of body text length.</returns>
    internal static MemberDeclarationSyntax[] Members(SyntaxNode root)
        => [.. root.DescendantNodes(static node => node is not BaseMethodDeclarationSyntax and not AccessorDeclarationSyntax
            and not AnonymousFunctionExpressionSyntax).OfType<MemberDeclarationSyntax>()];

    /// <summary>
    /// Encodes tokens through a declaration's implementation boundary without source trivia.
    /// </summary>
    /// <remarks>
    /// A namespace header ends with its name. File-scoped namespaces contain the rest of the file, and block namespaces
    /// being typed can lack an opening brace; neither form may make member text part of every nested declaration key.
    /// </remarks>
    private static string Header(MemberDeclarationSyntax member)
    {
        int end = member switch
        {
            BaseMethodDeclarationSyntax { Body: { } body } => body.SpanStart,
            BaseMethodDeclarationSyntax { ExpressionBody: { } expression } => expression.SpanStart,
            PropertyDeclarationSyntax { AccessorList: { } accessors } => accessors.SpanStart,
            PropertyDeclarationSyntax { ExpressionBody: { } expression } => expression.SpanStart,
            IndexerDeclarationSyntax { AccessorList: { } accessors } => accessors.SpanStart,
            IndexerDeclarationSyntax { ExpressionBody: { } expression } => expression.SpanStart,
            EventDeclarationSyntax { AccessorList: { } accessors } => accessors.SpanStart,
            TypeDeclarationSyntax { OpenBraceToken.IsMissing: false } type => type.OpenBraceToken.SpanStart,
            EnumDeclarationSyntax { OpenBraceToken.IsMissing: false } type => type.OpenBraceToken.SpanStart,
            BaseNamespaceDeclarationSyntax space => space.Name.Span.End,
            _ => member.Span.End,
        };
        var header = new StringBuilder();
        foreach (SyntaxToken token in member.DescendantTokens(descendIntoTrivia: false))
        {
            if (token.SpanStart >= end)
            {
                break;
            }

            string text = token.Text;
            header.Append(token.RawKind.ToString(CultureInfo.InvariantCulture)).Append(':')
                .Append(text.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(text).Append(';');
        }

        return header.ToString();
    }

    /// <summary>
    /// Resolves graph attribution without retaining or consulting a compiler object.
    /// </summary>
    /// <param name="sources">The detached physical and mapped source positions.</param>
    /// <returns>A transient graph location with the original source coordinates.</returns>
    internal Location Resolve(GeneratorSourceResolver sources) => sources.Resolve(this);
}
