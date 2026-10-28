using System;

namespace Ankus;

public readonly partial record struct PgTimestampTz
{
    /// <summary>
    /// Gets the year in the backend's session timezone. Negative years denote BC; there is no year zero.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite or no backend is active.</exception>
    public int Year => (int)GetFinitePart(PgDateTimePart.Year);

    /// <summary>
    /// Gets the month from one through twelve in the backend's session timezone.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite or no backend is active.</exception>
    public int Month => (int)GetFinitePart(PgDateTimePart.Month);

    /// <summary>
    /// Gets the day of the month in the backend's session timezone.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite or no backend is active.</exception>
    public int Day => (int)GetFinitePart(PgDateTimePart.Day);

    /// <summary>
    /// Gets the hour from zero through twenty-three in the backend's session timezone.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite or no backend is active.</exception>
    public int Hour => (int)GetFinitePart(PgDateTimePart.Hour);

    /// <summary>
    /// Gets the minute within the hour in the backend's session timezone.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite or no backend is active.</exception>
    public int Minute => (int)GetFinitePart(PgDateTimePart.Minute);

    /// <summary>
    /// Gets the whole second within the minute in the backend's session timezone.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite or no backend is active.</exception>
    public int Second => (int)GetFinitePart(PgDateTimePart.Microseconds) / 1_000_000;

    /// <summary>
    /// Gets the microseconds within the local second, from zero through 999999, using the active backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite or no backend is active.</exception>
    public int MicrosecondsWithinSecond => (int)GetFinitePart(PgDateTimePart.Microseconds) % 1_000_000;

    /// <summary>
    /// Gets the local seconds within the minute, including the microsecond fraction, using the active backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite or no backend is active.</exception>
    public double FractionalSecond => GetFinitePart(PgDateTimePart.Second);

    /// <summary>
    /// Gets whether this instant is positive infinity.
    /// </summary>
    public bool IsPositiveInfinity => MicrosecondsSinceEpoch == long.MaxValue;

    /// <summary>
    /// Gets whether this instant is negative infinity.
    /// </summary>
    public bool IsNegativeInfinity => MicrosecondsSinceEpoch == long.MinValue;

    /// <summary>
    /// Reads calendar fields in the backend's session timezone without narrowing through a wall-clock timestamp conversion.
    /// </summary>
    /// <returns>The local year, month, and day. Negative years denote BC; there is no year zero.</returns>
    /// <exception cref="InvalidOperationException">The timestamp is infinite or no backend is active.</exception>
    public (int Year, int Month, int Day) GetDateParts() => (Year, Month, Day);

    /// <summary>
    /// Reads exact wall-clock fields in the backend's session timezone.
    /// </summary>
    /// <returns>The local hour, minute, whole second, and microseconds within that second.</returns>
    /// <exception cref="InvalidOperationException">The timestamp is infinite or no backend is active.</exception>
    public (int Hour, int Minute, int Second, int Microseconds) GetTimeParts()
    {
        int microseconds = (int)GetFinitePart(PgDateTimePart.Microseconds);
        return (Hour, Minute, microseconds / 1_000_000, microseconds % 1_000_000);
    }

    /// <summary>
    /// Reads the UTC wall-clock timestamp directly from this instant's stored value, preserving infinities.
    /// </summary>
    /// <returns>The UTC timestamp without requiring a backend or consulting session settings.</returns>
    public PgTimestamp ToUtc() => new(MicrosecondsSinceEpoch);

    /// <summary>
    /// Creates an instant from raw PostgreSQL microseconds, clamping values outside the finite range to infinity.
    /// </summary>
    /// <param name="microsecondsSinceEpoch">The signed offset from 2000-01-01 UTC, including infinity sentinels.</param>
    /// <returns>The finite instant, negative infinity below the finite minimum, or positive infinity above the finite maximum.</returns>
    public static PgTimestampTz FromRawSaturating(long microsecondsSinceEpoch)
        => new(PgTemporal.SaturateTimestamp(microsecondsSinceEpoch));

    /// <summary>
    /// Formats stored diagnostic values without requiring a backend or reading local calendar fields.
    /// </summary>
    /// <returns>The managed record diagnostic representation.</returns>
    public override string ToString() => $"PgTimestampTz {{ MicrosecondsSinceEpoch = {MicrosecondsSinceEpoch}, IsFinite = {IsFinite} }}";

    /// <summary>
    /// Reads a finite local calendar field directly from PostgreSQL, including instants at the finite range boundaries.
    /// </summary>
    private double GetFinitePart(PgDateTimePart part)
    {
        PgTemporal.RequireFinite(IsFinite);
        return GetPart(part)!.Value;
    }
}
