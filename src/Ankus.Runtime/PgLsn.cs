using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace Ankus;

/// <summary>
/// Represents a position in PostgreSQL's write-ahead log, as the <c>pg_lsn</c> type holds it.
/// </summary>
/// <param name="Value">The byte position.</param>
/// <remarks>
/// The text form is two hexadecimal numbers, the high and low 32 bits, as in <c>16/B374D848</c>.
/// <see cref="ToString()"/> writes them without padding, as PostgreSQL 13–18, Npgsql and rust-postgres do; PostgreSQL 19
/// pads the low half to eight digits. Parsing accepts both forms, and so does every supported PostgreSQL version.
/// </remarks>
public readonly record struct PgLsn(ulong Value) : IComparable<PgLsn>, IComparable, ISpanFormattable, ISpanParsable<PgLsn>
{
    /// <summary>
    /// Gets the invalid position, <c>0/0</c>.
    /// </summary>
    public static PgLsn Invalid => default;

    /// <summary>
    /// Gets whether the position is valid.
    /// </summary>
    public bool IsValid => Value != 0;

    /// <summary>
    /// Compares two positions.
    /// </summary>
    /// <param name="left">The first position.</param>
    /// <param name="right">The second position.</param>
    /// <returns>Whether <paramref name="left"/> comes first.</returns>
    public static bool operator <(PgLsn left, PgLsn right) => left.Value < right.Value;

    /// <summary>
    /// Compares two positions.
    /// </summary>
    /// <param name="left">The first position.</param>
    /// <param name="right">The second position.</param>
    /// <returns>Whether <paramref name="left"/> comes later.</returns>
    public static bool operator >(PgLsn left, PgLsn right) => left.Value > right.Value;

    /// <summary>
    /// Compares two positions.
    /// </summary>
    /// <param name="left">The first position.</param>
    /// <param name="right">The second position.</param>
    /// <returns>Whether <paramref name="left"/> does not come later.</returns>
    public static bool operator <=(PgLsn left, PgLsn right) => left.Value <= right.Value;

    /// <summary>
    /// Compares two positions.
    /// </summary>
    /// <param name="left">The first position.</param>
    /// <param name="right">The second position.</param>
    /// <returns>Whether <paramref name="left"/> does not come first.</returns>
    public static bool operator >=(PgLsn left, PgLsn right) => left.Value >= right.Value;

    /// <summary>
    /// Parses PostgreSQL's text form, which does not depend on culture.
    /// </summary>
    /// <param name="s">The text, such as <c>16/B374D848</c>.</param>
    /// <returns>The position.</returns>
    /// <exception cref="FormatException">The text is not a position.</exception>
    public static PgLsn Parse(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        return Parse(s.AsSpan());
    }

    /// <summary>
    /// Parses PostgreSQL's text form, which does not depend on culture.
    /// </summary>
    /// <param name="s">The text, such as <c>16/B374D848</c>.</param>
    /// <returns>The position.</returns>
    /// <exception cref="FormatException">The text is not a position.</exception>
    public static PgLsn Parse(ReadOnlySpan<char> s)
        => TryParse(s, out PgLsn result) ? result : throw new FormatException($"Invalid pg_lsn text: {s}");

    /// <summary>
    /// Parses PostgreSQL's text form, which does not depend on culture.
    /// </summary>
    /// <param name="s">The text, such as <c>16/B374D848</c>.</param>
    /// <param name="result">The position, or the invalid position when parsing fails.</param>
    /// <returns>Whether the text is a position.</returns>
    public static bool TryParse([NotNullWhen(true)] string? s, out PgLsn result) => TryParse(s.AsSpan(), out result);

    /// <summary>
    /// Parses PostgreSQL's text form, which does not depend on culture.
    /// </summary>
    /// <param name="s">The text, such as <c>16/B374D848</c>.</param>
    /// <param name="result">The position, or the invalid position when parsing fails.</param>
    /// <returns>Whether the text is a position.</returns>
    public static bool TryParse(ReadOnlySpan<char> s, out PgLsn result)
    {
        result = default;
        int slash = s.IndexOf('/');
        if (slash is < 1 or > 8 || s.Length - slash - 1 is < 1 or > 8 ||
            !uint.TryParse(s[..slash], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint high) ||
            !uint.TryParse(s[(slash + 1)..], NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out uint low))
        {
            return false;
        }

        result = new PgLsn(((ulong)high << 32) | low);
        return true;
    }

    /// <inheritdoc />
    static PgLsn IParsable<PgLsn>.Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc />
    static PgLsn ISpanParsable<PgLsn>.Parse(ReadOnlySpan<char> s, IFormatProvider? provider) => Parse(s);

    /// <inheritdoc />
    static bool IParsable<PgLsn>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, out PgLsn result)
        => TryParse(s, out result);

    /// <inheritdoc />
    static bool ISpanParsable<PgLsn>.TryParse(ReadOnlySpan<char> s, IFormatProvider? provider, out PgLsn result)
        => TryParse(s, out result);

    /// <inheritdoc />
    public int CompareTo(PgLsn other) => Value.CompareTo(other.Value);

    /// <inheritdoc />
    public int CompareTo(object? obj) => obj switch
    {
        null => 1,
        PgLsn other => CompareTo(other),
        _ => throw new ArgumentException("The object is not a PgLsn.", nameof(obj)),
    };

    /// <summary>
    /// Formats the position as PostgreSQL does, such as <c>16/B374D848</c>.
    /// </summary>
    /// <returns>The text form.</returns>
    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{(uint)(Value >> 32):X}/{(uint)Value:X}");

    /// <inheritdoc />
    public string ToString(string? format, IFormatProvider? formatProvider) => ToString();

    /// <inheritdoc />
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => destination.TryWrite(CultureInfo.InvariantCulture, $"{(uint)(Value >> 32):X}/{(uint)Value:X}", out charsWritten);
}
