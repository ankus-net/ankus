using System;

namespace Ankus;

/// <summary>
/// Owns immutable bytes prepared by a configuration check hook and supplied to assignment and display hooks.
/// Retained instances remain valid after PostgreSQL changes or restores the setting.
/// </summary>
/// <param name="bytes">The bytes to copy into managed ownership.</param>
public sealed class PgGucExtra(ReadOnlySpan<byte> bytes)
{
    private readonly byte[] _bytes = bytes.ToArray();

    /// <summary>
    /// Gets the number of owned bytes. An empty instance remains distinct from absent extra data.
    /// </summary>
    public int Length => _bytes.Length;

    /// <summary>
    /// Gets the byte at a zero-based position.
    /// </summary>
    /// <param name="index">The zero-based byte position.</param>
    /// <returns>The stored byte.</returns>
    public byte this[int index] => _bytes[index];

    /// <summary>
    /// Views the owned bytes without permitting mutation.
    /// </summary>
    /// <returns>A read-only view of this instance's bytes.</returns>
    public ReadOnlySpan<byte> AsSpan() => _bytes;

    /// <summary>
    /// Copies the owned bytes to a separately mutable array.
    /// </summary>
    /// <returns>An independent copy.</returns>
    public byte[] ToArray() => [.. _bytes];
}
