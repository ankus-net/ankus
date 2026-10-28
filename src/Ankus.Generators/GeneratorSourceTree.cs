using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Ankus.Generators;

/// <summary>
/// Caches declaration anchors per immutable syntax tree before detaching physical attribution.
/// </summary>
/// <param name="Tree">The compiler tree retained only inside source-position analysis.</param>
/// <param name="RootStart">The compilation-unit anchor for assembly attributes.</param>
/// <param name="Members">Stable declaration keys and starts in the coordinate system used by diagnostic inputs.</param>
internal sealed record GeneratorSourceTree(SyntaxTree Tree, int RootStart, EquatableArray<GeneratorSourceTree.MemberAnchor> Members)
{
    /// <summary>
    /// Enumerates a source tree's declaration anchors once, independently of unrelated tree edits.
    /// </summary>
    /// <param name="tree">The immutable current source tree.</param>
    /// <param name="cancellationToken">Cancels syntax traversal.</param>
    /// <returns>The transient tree and cached declaration starts.</returns>
    internal static GeneratorSourceTree Create(SyntaxTree tree, CancellationToken cancellationToken)
    {
        SyntaxNode root = tree.GetRoot(cancellationToken);
        return new(tree, root.SpanStart, new(GeneratorLocation.Members(root)
            .Select(static member => new MemberAnchor(GeneratorLocation.Key(member), member.SpanStart))));
    }

    /// <summary>
    /// Resolves one declaration-relative span without enumerating the syntax tree again.
    /// </summary>
    /// <param name="coordinates">The current relative diagnostic or graph coordinates.</param>
    /// <returns>The current compiler location used only by source attribution analysis.</returns>
    internal Location Resolve(GeneratorLocation coordinates)
    {
        int anchor = coordinates.MemberKey is null ? RootStart : Members.Single(member => member.Key == coordinates.MemberKey).Start;
        return Location.Create(Tree, new TextSpan(anchor + coordinates.Span.Start, coordinates.Span.Length));
    }

    /// <summary>
    /// Retains one stable declaration identity and its current source start.
    /// </summary>
    /// <param name="Key">The declaration-header path.</param>
    /// <param name="Start">The declaration's current source start.</param>
    internal sealed record MemberAnchor(string Key, int Start);
}
