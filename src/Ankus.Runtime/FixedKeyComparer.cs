using System.Collections.Generic;

namespace Ankus;

/// <summary>
/// Combines standard scalar equality with explicit process-stable hashing.
/// </summary>
/// <typeparam name="T">A scalar validated by the comparer factory.</typeparam>
internal sealed class FixedKeyComparer<T> : EqualityComparer<T> where T : unmanaged
{
    /// <inheritdoc/>
    public override bool Equals(T x, T y) => EqualityComparer<T>.Default.Equals(x, y);

    /// <inheritdoc/>
    public override int GetHashCode(T obj) => PgFixedKeyComparer.Hash(obj);
}
