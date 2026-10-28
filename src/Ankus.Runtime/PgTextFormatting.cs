using System;

namespace Ankus;

/// <summary>
/// Formats PostgreSQL's canonical text for the value types that implement <see cref="ISpanFormattable"/>.
/// </summary>
internal static class PgTextFormatting
{
    /// <summary>
    /// Returns the canonical text, the only supported format.
    /// </summary>
    internal static string Format(string text, string? format)
        => string.IsNullOrEmpty(format) ? text : throw Unsupported(format);

    /// <summary>
    /// Copies the canonical text into a destination span.
    /// </summary>
    internal static bool TryFormat(string text, Span<char> destination, out int charsWritten, ReadOnlySpan<char> format)
    {
        if (!format.IsEmpty)
        {
            throw Unsupported(format.ToString());
        }

        if (text.TryCopyTo(destination))
        {
            charsWritten = text.Length;
            return true;
        }

        charsWritten = 0;
        return false;
    }

    private static FormatException Unsupported(string format)
        => new($"The format '{format}' is not supported: PostgreSQL text has one canonical form; use a null or empty format.");
}
