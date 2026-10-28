using System;

namespace Ankus;

public readonly partial record struct PgDate
{
    /// <summary>
    /// Gets the calendar year without requiring a backend. Negative years denote BC; there is no year zero.
    /// </summary>
    /// <exception cref="InvalidOperationException">The date is infinite.</exception>
    public int Year => GetDateParts().Year;

    /// <summary>
    /// Gets the calendar month from one through twelve without requiring a backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">The date is infinite.</exception>
    public int Month => GetDateParts().Month;

    /// <summary>
    /// Gets the day of the month without requiring a backend.
    /// </summary>
    /// <exception cref="InvalidOperationException">The date is infinite.</exception>
    public int Day => GetDateParts().Day;

    /// <summary>
    /// Gets whether this date is positive infinity.
    /// </summary>
    public bool IsPositiveInfinity => DaysSinceEpoch == int.MaxValue;

    /// <summary>
    /// Gets whether this date is negative infinity.
    /// </summary>
    public bool IsNegativeInfinity => DaysSinceEpoch == int.MinValue;

    /// <summary>
    /// Reads the full-range Gregorian date without requiring a backend.
    /// </summary>
    /// <returns>The year, month, and day. Negative years denote BC; there is no year zero.</returns>
    /// <exception cref="InvalidOperationException">The date is infinite.</exception>
    public (int Year, int Month, int Day) GetDateParts()
    {
        PgTemporal.RequireFinite(IsFinite);
        return PgTemporal.GetDateParts(DaysSinceEpoch);
    }

    /// <summary>
    /// Converts this finite date to its Julian day number without requiring a backend.
    /// </summary>
    /// <returns>The Julian day number, with zero denoting 4714-11-24 BC in the proleptic Gregorian calendar.</returns>
    /// <exception cref="InvalidOperationException">The date is infinite.</exception>
    public int ToJulianDays()
    {
        PgTemporal.RequireFinite(IsFinite);
        return DaysSinceEpoch + PgTemporal.EpochJulianDay;
    }

    /// <summary>
    /// Converts this finite date to the signed number of days since 1970-01-01 without requiring a backend.
    /// </summary>
    /// <returns>The signed Unix epoch day offset.</returns>
    /// <exception cref="InvalidOperationException">The date is infinite.</exception>
    public int ToUnixEpochDays()
    {
        PgTemporal.RequireFinite(IsFinite);
        return DaysSinceEpoch + PgTemporal.UnixEpochDays;
    }

    /// <summary>
    /// Converts this finite date's midnight to Unix epoch seconds, treating every day as 86400 seconds.
    /// </summary>
    /// <returns>The signed epoch seconds without any session timezone adjustment or backend access.</returns>
    /// <exception cref="InvalidOperationException">The date is infinite.</exception>
    public long ToUnixTimeSeconds() => (long)ToUnixEpochDays() * 86_400;

    /// <summary>
    /// Creates a date from a raw PostgreSQL day offset, clamping values outside the finite range to infinity.
    /// </summary>
    /// <param name="daysSinceEpoch">The signed number of days since 2000-01-01, including infinity sentinels.</param>
    /// <returns>The finite date, negative infinity below the finite minimum, or positive infinity above the finite maximum.</returns>
    public static PgDate FromRawSaturating(int daysSinceEpoch)
        => daysSinceEpoch < PgTemporal.MinDate ? NegativeInfinity
            : daysSinceEpoch >= PgTemporal.EndDate ? PositiveInfinity : new(daysSinceEpoch);

    /// <summary>
    /// Formats stored diagnostic values without requiring a backend or reading finite calendar fields.
    /// </summary>
    /// <returns>The managed record diagnostic representation.</returns>
    public override string ToString() => $"PgDate {{ DaysSinceEpoch = {DaysSinceEpoch}, IsFinite = {IsFinite} }}";
}
