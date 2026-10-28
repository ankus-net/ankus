using System.Collections.Generic;

namespace Ankus.Generators;

/// <summary>
/// Retains ordered cached source fragments before allocating a complete generated file.
/// </summary>
/// <param name="Fragments">The exact source fragments in composition order.</param>
internal sealed record GeneratorSourcePlan(EquatableArray<string> Fragments)
{
    /// <summary>
    /// Joins source fragments only after their independently comparable plan changes.
    /// </summary>
    /// <returns>The complete source using the established LF output convention.</returns>
    internal string Render() => PgFunctionGenerator.NormalizeLineEndings(string.Concat(Fragments));
}

/// <summary>
/// Builds a comparable source plan without repeatedly copying cached native and managed files.
/// </summary>
/// <param name="initial">The optional established source preamble.</param>
internal sealed class GeneratorSourceBuilder(string? initial = null)
{
    private readonly List<string> _fragments = string.IsNullOrEmpty(initial) ? [] : [initial!];

    /// <summary>
    /// Retains a cached source fragment without joining the complete source.
    /// </summary>
    /// <param name="source">The fragment, or no source.</param>
    /// <returns>This builder for ordered composition.</returns>
    internal GeneratorSourceBuilder Append(string? source)
    {
        if (!string.IsNullOrEmpty(source))
        {
            _fragments.Add(source!);
        }

        return this;
    }

    /// <summary>
    /// Retains a fragment followed by the established LF output newline.
    /// </summary>
    /// <param name="source">The optional source line.</param>
    /// <returns>This builder for ordered composition.</returns>
    internal GeneratorSourceBuilder AppendLine(string? source = null) => Append(source).Append("\n");

    /// <summary>
    /// Freezes the ordered source fragments independently of graph attribution and SQL policy.
    /// </summary>
    /// <returns>The immutable rendering input.</returns>
    internal GeneratorSourcePlan Freeze() => new(new EquatableArray<string>(_fragments));
}
