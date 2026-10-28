using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace Ankus.Generators;

/// <summary>
/// Gives immutable model collections ordered value equality at incremental pipeline boundaries.
/// </summary>
/// <typeparam name="T">The immutable element type.</typeparam>
/// <param name="values">The ordered values to snapshot.</param>
internal sealed class EquatableArray<T>(IEnumerable<T> values) : IReadOnlyList<T>, IEquatable<EquatableArray<T>>
{
    private readonly ImmutableArray<T> _values = [.. values];

    /// <summary>
    /// Gets the number of stored values.
    /// </summary>
    public int Count => _values.Length;

    /// <summary>
    /// Gets whether the collection contains no values.
    /// </summary>
    internal bool IsEmpty => _values.IsEmpty;

    /// <summary>
    /// Gets the value at its original position.
    /// </summary>
    /// <param name="index">The zero-based position.</param>
    /// <returns>The stored value.</returns>
    public T this[int index] => _values[index];

    /// <summary>
    /// Compares each immutable value in declaration order.
    /// </summary>
    /// <param name="other">The other collection.</param>
    /// <returns>Whether both collections contain the same ordered values.</returns>
    public bool Equals(EquatableArray<T>? other)
        => other is not null && _values.SequenceEqual(other._values);

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is EquatableArray<T> other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        int hash = 17;
        foreach (T value in _values)
        {
            hash = unchecked(hash * 31 + (value is null ? 0 : EqualityComparer<T>.Default.GetHashCode(value)));
        }

        return hash;
    }

    /// <inheritdoc />
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_values).GetEnumerator();

    /// <inheritdoc />
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
