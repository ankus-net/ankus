using System;

namespace Ankus;

public readonly partial record struct PgTimestamp
{
    /// <summary>
    /// Gets the calendar year without a backend. Negative years denote BC; there is no year zero.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite.</exception>
    public int Year => GetDateParts().Year;

    /// <summary>
    /// Gets the calendar month from one through twelve without a backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite.</exception>
    public int Month => GetDateParts().Month;

    /// <summary>
    /// Gets the calendar day of the month without a backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite.</exception>
    public int Day => GetDateParts().Day;

    /// <summary>
    /// Gets the hour from zero through twenty-three without a backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite.</exception>
    public int Hour => GetTimeParts().Hour;

    /// <summary>
    /// Gets the minute within the hour without a backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite.</exception>
    public int Minute => GetTimeParts().Minute;

    /// <summary>
    /// Gets the whole second within the minute without a backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite.</exception>
    public int Second => GetTimeParts().Second;

    /// <summary>
    /// Gets the microseconds within the current second, from zero through 999999, without a backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite.</exception>
    public int MicrosecondsWithinSecond => GetTimeParts().Microseconds;

    /// <summary>
    /// Gets the seconds within the minute, including the microsecond fraction, without a backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">The timestamp is infinite.</exception>
    public double FractionalSecond => Second + MicrosecondsWithinSecond / 1_000_000d;

    /// <summary>
    /// Gets whether this timestamp is positive infinity.
    /// </summary>
    public bool IsPositiveInfinity => MicrosecondsSinceEpoch == long.MaxValue;

    /// <summary>
    /// Gets whether this timestamp is negative infinity.
    /// </summary>
    public bool IsNegativeInfinity => MicrosecondsSinceEpoch == long.MinValue;

    /// <summary>
    /// Reads the full-range Gregorian date without a backend, using the containing day for negative epoch offsets.
    /// </summary>
    /// <returns>The year, month, and day. Negative years denote BC; there is no year zero.</returns>
    /// <exception cref="InvalidOperationException">The timestamp is infinite.</exception>
    public (int Year, int Month, int Day) GetDateParts()
    {
        PgTemporal.RequireFinite(IsFinite);
        return PgTemporal.GetDateParts(PgTemporal.GetTimestampDays(MicrosecondsSinceEpoch));
    }

    /// <summary>
    /// Reads exact wall-clock fields without a backend, using a nonnegative time of day before the epoch.
    /// </summary>
    /// <returns>The hour, minute, whole second, and microseconds within that second.</returns>
    /// <exception cref="InvalidOperationException">The timestamp is infinite.</exception>
    public (int Hour, int Minute, int Second, int Microseconds) GetTimeParts()
    {
        PgTemporal.RequireFinite(IsFinite);
        return PgTemporal.GetTimeParts(PgTemporal.WrapTime(MicrosecondsSinceEpoch));
    }

    /// <summary>
    /// Creates a timestamp from raw PostgreSQL microseconds, clamping values outside the finite range to infinity.
    /// </summary>
    /// <param name="microsecondsSinceEpoch">The signed offset from 2000-01-01, including infinity sentinels.</param>
    /// <returns>The finite timestamp, negative infinity below the finite minimum, or positive infinity above the finite maximum.</returns>
    public static PgTimestamp FromRawSaturating(long microsecondsSinceEpoch)
        => new(PgTemporal.SaturateTimestamp(microsecondsSinceEpoch));

    /// <summary>
    /// Formats stored diagnostic values without requiring a backend or reading finite calendar fields.
    /// </summary>
    /// <returns>The managed record diagnostic representation.</returns>
    public override string ToString() => $"PgTimestamp {{ MicrosecondsSinceEpoch = {MicrosecondsSinceEpoch}, IsFinite = {IsFinite} }}";
}
