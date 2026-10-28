using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace Ankus;

/// <summary>
/// Owns the exact nonzero bytes of a PostgreSQL cstring and its terminating zero byte.
/// </summary>
/// <remarks>
/// Bytes are independent of PostgreSQL memory and are not implicitly transcoded.
/// Count, indexing and enumeration exclude the terminator. A null reference represents SQL NULL;
/// an empty instance contains only its terminator. Equality and ordering compare unsigned bytes.
/// </remarks>
/// <param name="bytes">The payload without a terminator or embedded zero bytes.</param>
public sealed class PgCString(ReadOnlySpan<byte> bytes) : IReadOnlyList<byte>, IEquatable<PgCString>, IComparable<PgCString>, IComparable
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);
    private readonly byte[] _bytes = Terminate(bytes);

    /// <summary>
    /// Gets the payload length, excluding the terminating zero byte.
    /// </summary>
    public int Count => _bytes.Length - 1;

    /// <summary>
    /// Gets a payload byte by its zero-based index, excluding the terminator.
    /// </summary>
    /// <param name="index">The payload byte index.</param>
    /// <returns>The original nonzero byte.</returns>
    public byte this[int index] => AsSpan()[index];

    /// <summary>
    /// Gets readonly managed bytes without the terminator.
    /// </summary>
    /// <returns>The payload, independent of any native lifetime.</returns>
    public ReadOnlySpan<byte> AsSpan() => _bytes.AsSpan(0, Count);

    /// <summary>
    /// Gets readonly managed bytes including exactly one terminating zero byte.
    /// </summary>
    /// <returns>The complete C string, including the terminator for an empty value.</returns>
    public ReadOnlySpan<byte> AsNullTerminatedSpan() => _bytes;

    /// <summary>
    /// Copies the payload without its terminator into independent managed storage.
    /// </summary>
    /// <returns>The original payload bytes.</returns>
    public byte[] ToArray() => AsSpan().ToArray();

    /// <summary>
    /// Copies the entire payload without its terminator into a sufficiently large destination.
    /// </summary>
    /// <param name="destination">Storage for at least Count bytes.</param>
    public void CopyTo(Span<byte> destination) => AsSpan().CopyTo(destination);

    /// <summary>
    /// Decodes the bytes as strict UTF-8 without consulting the PostgreSQL server encoding.
    /// </summary>
    /// <returns>The decoded text.</returns>
    /// <exception cref="DecoderFallbackException">The bytes are not valid UTF-8.</exception>
    public string ToUtf8String() => s_utf8.GetString(AsSpan());

    /// <summary>
    /// Encodes managed text as strict UTF-8 without consulting the PostgreSQL server encoding.
    /// </summary>
    /// <param name="text">Text without embedded zero characters or unpaired surrogates.</param>
    /// <returns>An independently owned C string.</returns>
    public static PgCString FromUtf8(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return new PgCString(s_utf8.GetBytes(text));
    }

    /// <summary>
    /// Copies a complete C string with exactly one zero byte at its end.
    /// </summary>
    /// <param name="bytes">The payload followed by its required terminator.</param>
    /// <returns>An independent value excluding the terminator from Count.</returns>
    public static PgCString FromNullTerminatedBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty || bytes[^1] != 0)
        {
            throw new ArgumentException("A C string requires a terminating zero byte.", nameof(bytes));
        }

        return new PgCString(bytes[..^1]);
    }

    /// <summary>
    /// Compares complete payload bytes without decoding or normalization.
    /// </summary>
    /// <param name="other">The other value, or null.</param>
    /// <returns>Whether the two present values contain identical bytes.</returns>
    public bool Equals(PgCString? other) => other is not null && AsSpan().SequenceEqual(other.AsSpan());

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is PgCString other && Equals(other);

    /// <summary>
    /// Computes a process-local hash of the exact payload bytes, consistent with equality.
    /// </summary>
    /// <returns>The payload hash, unsuitable for persistent PostgreSQL index storage.</returns>
    public override int GetHashCode()
    {
        HashCode hash = new();
        hash.AddBytes(AsSpan());
        return hash.ToHashCode();
    }

    /// <summary>
    /// Compares unsigned payload bytes lexicographically, with null before every present value.
    /// </summary>
    /// <param name="other">The other value, or null.</param>
    /// <returns>A negative, zero or positive value for less than, equal to or greater than.</returns>
    public int CompareTo(PgCString? other) => other is null ? 1 : AsSpan().SequenceCompareTo(other.AsSpan());

    int IComparable.CompareTo(object? obj) => obj is null or PgCString
        ? CompareTo((PgCString?)obj)
        : throw new ArgumentException("The value must be a PgCString.", nameof(obj));

    /// <summary>
    /// Enumerates the payload bytes without exposing or including the terminator.
    /// </summary>
    /// <returns>An independent managed cursor.</returns>
    public IEnumerator<byte> GetEnumerator()
    {
        for (int index = 0; index < Count; index++)
        {
            yield return _bytes[index];
        }
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Compares two nullable values by their exact payload bytes.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Whether the values are equal.</returns>
    public static bool operator ==(PgCString? left, PgCString? right)
    {
        return Equals(left, right);
    }

    /// <summary>
    /// Compares two nullable values for differing payload bytes.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Whether the values differ.</returns>
    public static bool operator !=(PgCString? left, PgCString? right)
    {
        return !Equals(left, right);
    }

    /// <summary>
    /// Compares nullable values in unsigned byte order.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Whether the first value precedes the second.</returns>
    public static bool operator <(PgCString? left, PgCString? right)
    {
        return Compare(left, right) < 0;
    }

    /// <summary>
    /// Compares nullable values in unsigned byte order.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Whether the first value follows the second.</returns>
    public static bool operator >(PgCString? left, PgCString? right)
    {
        return Compare(left, right) > 0;
    }

    /// <summary>
    /// Compares nullable values in unsigned byte order.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Whether the first value precedes or equals the second.</returns>
    public static bool operator <=(PgCString? left, PgCString? right)
    {
        return Compare(left, right) <= 0;
    }

    /// <summary>
    /// Compares nullable values in unsigned byte order.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>Whether the first value follows or equals the second.</returns>
    public static bool operator >=(PgCString? left, PgCString? right)
    {
        return Compare(left, right) >= 0;
    }

    /// <summary>
    /// Orders null before present values and delegates present comparisons to their bytes.
    /// </summary>
    private static int Compare(PgCString? left, PgCString? right) => left is null ? right is null ? 0 : -1 : left.CompareTo(right);

    /// <summary>
    /// Rejects embedded zero bytes and appends one terminator to an independent payload copy.
    /// </summary>
    private static byte[] Terminate(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Contains((byte)0))
        {
            throw new ArgumentException("A C string cannot contain embedded zero bytes.", nameof(bytes));
        }

        byte[] result = new byte[checked(bytes.Length + 1)];
        bytes.CopyTo(result);
        return result;
    }
}
