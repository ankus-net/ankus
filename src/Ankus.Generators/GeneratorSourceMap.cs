using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;

namespace Ankus.Generators;

/// <summary>
/// Freezes only the source attribution needed by detached graph composition.
/// </summary>
/// <param name="Entries">Distinct source coordinates in stable tree and span order.</param>
internal sealed record GeneratorSourceMap(EquatableArray<GeneratorSourceMap.Entry> Entries)
{
    /// <summary>
    /// Reads relevant physical and mapped positions without retaining compiler objects.
    /// </summary>
    /// <param name="locations">Declaration and diagnostic coordinates used by composition.</param>
    /// <param name="compilation">The current compilation containing those coordinates.</param>
    /// <param name="cancellationToken">The current analysis cancellation token.</param>
    /// <returns>Comparable attribution values for the detached composition step.</returns>
    internal static GeneratorSourceMap Create(IEnumerable<GeneratorLocation?> locations, Compilation compilation, CancellationToken cancellationToken)
        => Create(locations, [.. compilation.SyntaxTrees.Select(tree => GeneratorSourceTree.Create(tree, cancellationToken))], cancellationToken);

    /// <summary>
    /// Detaches only the requested positions from independently cached source-tree anchors.
    /// </summary>
    /// <param name="locations">Declaration and diagnostic coordinates consulted by composition.</param>
    /// <param name="trees">Source-position analysis with unchanged tree traversals cached.</param>
    /// <param name="cancellationToken">Cancels source attribution.</param>
    /// <returns>The comparable physical and mapped attribution values.</returns>
    internal static GeneratorSourceMap Create(IEnumerable<GeneratorLocation?> locations, IReadOnlyList<GeneratorSourceTree> trees,
        CancellationToken cancellationToken)
    {
        var occurrences = new Dictionary<string, int>(StringComparer.Ordinal);
        var sources = new Dictionary<(string Path, int Occurrence), GeneratorSourceTree>();
        foreach (GeneratorSourceTree tree in trees)
        {
            cancellationToken.ThrowIfCancellationRequested();
            occurrences.TryGetValue(tree.Tree.FilePath, out int occurrence);
            sources.Add((tree.Tree.FilePath, occurrence), tree);
            occurrences[tree.Tree.FilePath] = occurrence + 1;
        }

        var entries = new List<Entry>();
        foreach (GeneratorLocation location in locations.Where(static location => location.HasValue).Select(static location => location!.Value)
            .Distinct().OrderBy(static location => location.Path, StringComparer.Ordinal).ThenBy(static location => location.TreeOccurrence)
            .ThenBy(static location => location.MemberKey, StringComparer.Ordinal).ThenBy(static location => location.Span.Start)
            .ThenBy(static location => location.Span.Length))
        {
            cancellationToken.ThrowIfCancellationRequested();
            Location source = sources[(location.Path, location.TreeOccurrence)].Resolve(location);
            entries.Add(new(location, AttributionLine(source.GetLineSpan()), AttributionLine(source.GetMappedLineSpan())));
        }

        return new(new EquatableArray<Entry>(entries));
    }

    /// <summary>
    /// Creates line-independent composition locations; actual lines are attached only to final SQL nodes.
    /// </summary>
    /// <param name="locations">The detached declaration and diagnostic coordinates.</param>
    /// <returns>The stable coordinates and file identities without current physical line numbers.</returns>
    internal static GeneratorSourceMap Anchors(IEnumerable<GeneratorLocation?> locations)
        => new(new EquatableArray<Entry>(locations.Where(static location => location.HasValue)
            .Select(static location => location!.Value).Distinct().Select(static location =>
            {
                var line = new LinePosition(0, 0);
                var span = new FileLinePositionSpan(location.Path, line, line);
                return new Entry(location, span, span);
            })));

    /// <summary>
    /// Freezes line attribution without absolute columns that are irrelevant to generated SQL.
    /// Exact diagnostic spans are independently resolved against the current declaration.
    /// </summary>
    /// <param name="span">The current source attribution.</param>
    /// <returns>The same file and starting line, with no body-dependent column or ending position.</returns>
    private static FileLinePositionSpan AttributionLine(FileLinePositionSpan span)
    {
        var line = new LinePosition(span.StartLinePosition.Line, 0);
        return new(span.Path, line, line);
    }

    /// <summary>
    /// Contains portable line attribution and declaration coordinates that distinguish duplicate file paths.
    /// </summary>
    /// <param name="Coordinates">The declaration-relative source coordinates.</param>
    /// <param name="Physical">The physical file and starting line used by SQL provenance.</param>
    /// <param name="Mapped">The compiler's mapped file and starting line.</param>
    internal sealed record Entry(GeneratorLocation Coordinates, FileLinePositionSpan Physical, FileLinePositionSpan Mapped);

}
