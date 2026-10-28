using System;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Shares geometry input, floating-point ordering, formatting and transport-size contracts.
/// </summary>
internal static class PgGeometry
{
    /// <summary>
    /// Parses a supported geometric result through the guarded native input routines.
    /// </summary>
    /// <typeparam name="T">The geometric type.</typeparam>
    /// <param name="text">The PostgreSQL input.</param>
    /// <returns>The detached value.</returns>
    internal static T Parse<T>(string text) => NativeBackend.Geometry<T>([PgTemporal.Text(text)]);

    /// <summary>
    /// Returns false for malformed geometry or overflowing coordinates, preserving operational failures.
    /// </summary>
    /// <typeparam name="T">The geometric type.</typeparam>
    /// <param name="text">The PostgreSQL input.</param>
    /// <param name="value">The parsed value, or default.</param>
    /// <returns>Whether parsing succeeded.</returns>
    internal static bool TryParse<T>(string? text, [NotNullWhen(true)] out T value)
    {
        value = default!;
        if (text is null || text.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            value = NativeInputRecovery.Run(() => Parse<T>(text)) ??
                throw new InvalidOperationException("PostgreSQL geometry input returned NULL.");
            return true;
        }
        catch (PgException error) when ((error.NativeFlags & NativeErrorFlags.Unrecovered) == 0 && (error.SqlState is "22P02" or "22003"))
        {
            return false;
        }
        catch (EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Compares coordinates using PostgreSQL float8 ordering, where NaN is greatest and signed zeroes compare equal.
    /// </summary>
    /// <param name="left">The first coordinate.</param>
    /// <param name="right">The second coordinate.</param>
    /// <returns>The comparison result.</returns>
    internal static int Compare(double left, double right) => double.IsNaN(left) ? double.IsNaN(right) ? 0 : 1
        : double.IsNaN(right) ? -1 : left.CompareTo(right);

    /// <summary>
    /// Formats a round-trippable coordinate in PostgreSQL-compatible invariant notation.
    /// </summary>
    /// <param name="value">The coordinate.</param>
    /// <returns>The invariant text.</returns>
    internal static string Format(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>
    /// Formats a sequence of points without temporary per-vertex strings beyond point formatting.
    /// </summary>
    /// <param name="points">The vertices.</param>
    /// <param name="open">The opening delimiter.</param>
    /// <param name="close">The closing delimiter.</param>
    /// <returns>The PostgreSQL geometry text.</returns>
    internal static string FormatPoints(ReadOnlySpan<PgPoint> points, char open, char close)
    {
        StringBuilder text = new StringBuilder().Append(open);
        for (int index = 0; index < points.Length; index++)
        {
            if (index != 0)
            {
                text.Append(',');
            }

            text.Append(points[index]);
        }

        return text.Append(close).ToString();
    }

    /// <summary>
    /// Reserves enough space for the largest native polygon header within PostgreSQL's allocation limit.
    /// </summary>
    /// <param name="count">The vertex count.</param>
    internal static void ValidateCount(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, (0x3FFFFFFF - 40) / 16);
    }
}
