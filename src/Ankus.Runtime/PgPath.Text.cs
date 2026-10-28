using System;
using System.Diagnostics.CodeAnalysis;

namespace Ankus;

/// <summary>
/// Implements .NET's parsing and span formatting contracts over PostgreSQL's text form.
/// </summary>
public sealed partial class PgPath : ISpanFormattable, IParsable<PgPath>
{
    /// <summary>
    /// Parses PostgreSQL text with the type's input function on the active backend thread, as
    /// <see cref="Parse(string)"/> does.
    /// </summary>
    /// <param name="s">The PostgreSQL text.</param>
    /// <param name="provider">Ignored: PostgreSQL's input function and session settings decide the syntax.</param>
    /// <returns>The parsed value.</returns>
    static PgPath IParsable<PgPath>.Parse(string s, IFormatProvider? provider) => Parse(s);

    /// <summary>
    /// Tries to parse PostgreSQL text on the active backend thread, as <see cref="TryParse(string, out PgPath)"/> does.
    /// </summary>
    /// <param name="s">The candidate text.</param>
    /// <param name="provider">Ignored: PostgreSQL's input function and session settings decide the syntax.</param>
    /// <param name="result">The parsed value, or null on invalid input.</param>
    /// <returns>Whether the input was valid.</returns>
    static bool IParsable<PgPath>.TryParse([NotNullWhen(true)] string? s, IFormatProvider? provider, [MaybeNullWhen(false)] out PgPath result)
        => TryParse(s, out result);

    /// <summary>
    /// Formats the value as PostgreSQL's canonical text, the same text as <see cref="ToString()"/>.
    /// </summary>
    /// <param name="format">Null or empty; PostgreSQL text has one canonical form.</param>
    /// <param name="formatProvider">Ignored: the text does not depend on culture.</param>
    /// <returns>The canonical text.</returns>
    /// <exception cref="FormatException">A format is specified.</exception>
    public string ToString(string? format, IFormatProvider? formatProvider) => PgTextFormatting.Format(ToString(), format);

    /// <summary>
    /// Copies the value's canonical PostgreSQL text into a span, as interpolated strings and other span-based
    /// formatters request it.
    /// </summary>
    /// <param name="destination">The span that receives the text.</param>
    /// <param name="charsWritten">The number of characters written.</param>
    /// <param name="format">Empty; PostgreSQL text has one canonical form.</param>
    /// <param name="provider">Ignored: the text does not depend on culture.</param>
    /// <returns>Whether the text fit in the destination.</returns>
    /// <exception cref="FormatException">A format is specified.</exception>
    public bool TryFormat(Span<char> destination, out int charsWritten, ReadOnlySpan<char> format, IFormatProvider? provider)
        => PgTextFormatting.TryFormat(ToString(), destination, out charsWritten, format);
}
