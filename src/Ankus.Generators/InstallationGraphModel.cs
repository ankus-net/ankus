using System;
using System.Collections.Generic;
using System.Linq;

namespace Ankus.Generators;

/// <summary>
/// Freezes validated installation order independently of graph state and source rendering.
/// </summary>
/// <param name="Nodes">The complete topological declaration order.</param>
internal sealed record InstallationGraphModel(EquatableArray<InstallationGraphModel.Node> Nodes)
{
    /// <summary>
    /// Attaches current physical lines after semantic graph validation, without rerunning native composition.
    /// </summary>
    /// <param name="nodes">The ordered semantic graph nodes with declaration coordinates.</param>
    /// <param name="sources">The current detached source attribution.</param>
    /// <returns>Nodes whose SQL provenance contains accurate current physical lines.</returns>
    internal static IEnumerable<Node> Attribute(IReadOnlyList<Node> nodes, GeneratorSourceMap sources)
    {
        Dictionary<GeneratorLocation, GeneratorSourceMap.Entry> entries = sources.Entries.ToDictionary(static entry => entry.Coordinates);
        foreach (Node node in nodes)
        {
            if (node.Coordinates is { } coordinates && node.Provenance.Source is { } origin)
            {
                yield return node with
                {
                    Provenance = node.Provenance with
                    {
                        Source = origin with { Line = entries[coordinates].Physical.StartLinePosition.Line + 1 },
                    },
                };
            }
            else
            {
                yield return node;
            }
        }
    }

    /// <summary>
    /// Contains exact graph metadata and independently comparable SQL provenance.
    /// </summary>
    /// <param name="Key">The stable declaration key.</param>
    /// <param name="Kind">The declaration category.</param>
    /// <param name="Provenance">The SQL and portable source/dependency comments.</param>
    /// <param name="Owner">The declaration family key, or an empty string.</param>
    /// <param name="Names">The distinct sorted dependency and selection aliases.</param>
    /// <param name="Dependencies">The distinct sorted prerequisite keys.</param>
    /// <param name="Attachments">The distinct sorted extension attachment identities.</param>
    /// <param name="Coordinates">The optional declaration coordinates used to attach current physical lines.</param>
    internal sealed record Node(string Key, string Kind, SqlProvenance.Model Provenance, string Owner,
        EquatableArray<string> Names, EquatableArray<string> Dependencies, EquatableArray<string> Attachments,
        GeneratorLocation? Coordinates = null)
    {
        /// <summary>
        /// Freezes a resolved graph node without retaining mutable entities or compiler locations.
        /// </summary>
        /// <param name="entity">The validated and ordered declaration.</param>
        /// <param name="projectDirectory">The root used for portable source attribution.</param>
        /// <param name="sources">The optional resolver identifying detached declaration coordinates.</param>
        /// <returns>The immutable rendering and serialization inputs.</returns>
        internal static Node Create(SqlEntity entity, string projectDirectory, GeneratorSourceResolver? sources = null)
            => new(entity.Key, entity.Kind, SqlProvenance.Create(entity, projectDirectory), entity.Owner?.Key ?? string.Empty,
                Sorted(entity.Names.Concat(entity.SelectionNames)), Sorted(entity.Dependencies.Select(static dependency => dependency.Key)), Sorted(entity.Attachments),
                sources?.Coordinates(entity.Location));

        /// <summary>
        /// Freezes graph fields in the established deterministic serialization order.
        /// </summary>
        private static EquatableArray<string> Sorted(IEnumerable<string> values)
            => new(values.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal));
    }

    /// <summary>
    /// Carries only the fields that affect embedded graph encoding after SQL rendering.
    /// </summary>
    /// <param name="Key">The stable declaration key.</param>
    /// <param name="Kind">The declaration category.</param>
    /// <param name="Sql">The rendered SQL with schema insertion markers retained.</param>
    /// <param name="Owner">The declaration family key.</param>
    /// <param name="Names">The sorted dependency and selection aliases.</param>
    /// <param name="Dependencies">The sorted prerequisite keys.</param>
    /// <param name="Attachments">The sorted extension attachment identities.</param>
    internal sealed record EncodedNode(string Key, string Kind, string Sql, string Owner,
        EquatableArray<string> Names, EquatableArray<string> Dependencies, EquatableArray<string> Attachments);
}
