using System;
using System.Collections;
using System.Collections.Generic;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Builds an exact sequence of PostgreSQL name components for native operator lookup.
/// </summary>
/// <remarks>
/// Components are unquoted catalog identifiers, never parsed, split, trimmed or case-folded.
/// The managed builder is reusable and needs no backend until lookup. It is not thread-safe.
/// </remarks>
public sealed class PgQualifiedNameBuilder : IReadOnlyList<string>
{
    private readonly List<string> _components = [];

    /// <summary>
    /// Gets the number of name components.
    /// </summary>
    public int Count => _components.Count;

    /// <summary>
    /// Gets one exact component in insertion order.
    /// </summary>
    /// <param name="index">The zero-based component index.</param>
    /// <returns>The component.</returns>
    public string this[int index] => _components[index];

    /// <summary>
    /// Appends one exact name component without interpreting SQL quoting or punctuation.
    /// </summary>
    /// <param name="value">The component, including an empty string if desired.</param>
    /// <returns>This builder for fluent construction.</returns>
    /// <exception cref="ArgumentNullException">The component is null.</exception>
    /// <exception cref="ArgumentException">The component contains a zero character.</exception>
    /// <exception cref="System.Text.EncoderFallbackException">The component contains invalid UTF-16.</exception>
    public PgQualifiedNameBuilder Add(string value)
    {
        NativeBackend.ValidateLookupName(value, nameof(value));
        _components.Add(value);
        return this;
    }

    /// <summary>
    /// Resolves an operator with exactly the supplied argument type OIDs using PostgreSQL's current namespace rules.
    /// </summary>
    /// <param name="leftTypeOid">The left argument type, or zero for a prefix operator.</param>
    /// <param name="rightTypeOid">The right argument type; zero permits legacy postfix lookup where the server supports it.</param>
    /// <returns>The current operator OID, or zero when the exact operator or explicit schema is absent.</returns>
    /// <remarks>
    /// One component follows search_path, two specify schema and operator, and three also specify the current database.
    /// PostgreSQL reports invalid name counts, foreign databases and schema permission errors as PgException.
    /// Lookup does not coerce argument types, consume this builder, cache OIDs, or retain native storage.
    /// </remarks>
    public uint GetOperatorOid(uint leftTypeOid, uint rightTypeOid)
        => NativeBackend.ResolveOperator(_components, leftTypeOid, rightTypeOid);

    /// <summary>
    /// Enumerates components in insertion order.
    /// </summary>
    /// <returns>The component enumerator.</returns>
    public IEnumerator<string> GetEnumerator() => _components.GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
