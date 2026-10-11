using System.Diagnostics.CodeAnalysis;

namespace Ankus;

/// <summary>
/// Creates and parses typed PostgreSQL ranges without static members on generic types.
/// </summary>
public static class PgRange
{
    /// <summary>
    /// Creates a range with inferred bound type, inclusive lower bound and exclusive upper bound by default.
    /// </summary>
    /// <typeparam name="T">The supported bound type.</typeparam>
    /// <param name="lower">The lower bound.</param>
    /// <param name="upper">The upper bound.</param>
    /// <param name="lowerInclusive">Whether the lower bound is included.</param>
    /// <param name="upperInclusive">Whether the upper bound is included.</param>
    /// <returns>The uncanonicalized range.</returns>
    public static PgRange<T> Create<T>(T lower, T upper, bool lowerInclusive = true, bool upperInclusive = false) where T : struct
        => new(lower, upper, lowerInclusive, upperInclusive);

    /// <summary>
    /// Creates an empty range without backend access.
    /// </summary>
    /// <typeparam name="T">The supported bound type.</typeparam>
    /// <returns>The empty range.</returns>
    public static PgRange<T> Empty<T>() where T : struct => new();

    /// <summary>
    /// Creates a fully unbounded range, distinct from empty and from SQL NULL.
    /// </summary>
    /// <typeparam name="T">The supported bound type.</typeparam>
    /// <returns>The unbounded range.</returns>
    public static PgRange<T> Unbounded<T>() where T : struct => new(null, null);

    /// <summary>
    /// Resolves a .NET index range against a collection length and returns its half-open PostgreSQL integer interval.
    /// </summary>
    /// <param name="range">The .NET range, including from-end indices.</param>
    /// <param name="length">The collection length used to resolve indices.</param>
    /// <returns>The integer interval, or empty for a zero-length slice.</returns>
    public static PgRange<int> FromRange(Range range, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        (int offset, int count) = range.GetOffsetAndLength(length);
        return count == 0 ? new() : new(offset, checked(offset + count));
    }

    /// <summary>
    /// Parses and canonicalizes a built-in range using PostgreSQL on the active backend.
    /// </summary>
    /// <typeparam name="T">The supported bound type.</typeparam>
    /// <param name="text">The PostgreSQL range text.</param>
    /// <returns>The canonical range.</returns>
    public static PgRange<T> Parse<T>(string text) where T : struct
        => NativeBackend.Range<PgRange<T>>(RangeOperation.Parse, [PgTemporal.Text(text)]);

    /// <summary>
    /// Tries backend parsing, returning false for malformed bounds or subtype range errors.
    /// </summary>
    /// <typeparam name="T">The supported bound type.</typeparam>
    /// <param name="text">The PostgreSQL range text.</param>
    /// <param name="value">The canonical range, or null.</param>
    /// <returns>Whether parsing succeeded.</returns>
    /// <remarks>
    /// For built-in range types, invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse<T>(string? text, [NotNullWhen(true)] out PgRange<T>? value) where T : struct
    {
        value = null;
        if (text is null || text.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            value = NativeInputRecovery.Run(() => Parse<T>(text));
            return true;
        }
        catch (PgException error) when ((error.NativeFlags & NativeErrorFlags.Unrecovered) == 0 && (error.SqlState is "22P02" or "22000" or "22003" or "22007" or "22008" or "22009"))
        {
            return false;
        }
        catch (System.Text.EncoderFallbackException)
        {
            return false;
        }
    }
}
