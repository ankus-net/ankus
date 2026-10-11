using System.Text.Json.Serialization;
using Ankus.Serialization;

namespace Ankus;

/// <summary>
/// Represents PostgreSQL date, including BC dates, its full finite range, and both infinities.
/// The default value is 2000-01-01.
/// </summary>
[JsonConverter(typeof(PgDateConverter))]
public readonly partial record struct PgDate : IComparable<PgDate>
{
    /// <summary>
    /// Creates a date from days relative to 2000-01-01. Int32 extremes represent infinities.
    /// </summary>
    /// <param name="daysSinceEpoch">The signed PostgreSQL day offset.</param>
    /// <exception cref="ArgumentOutOfRangeException">The finite offset is outside PostgreSQL's date range.</exception>
    public PgDate(int daysSinceEpoch)
    {
        if (daysSinceEpoch is not (int.MinValue or int.MaxValue) and (< -2_451_545 or >= 2_145_031_949))
        {
            throw new ArgumentOutOfRangeException(nameof(daysSinceEpoch));
        }

        DaysSinceEpoch = daysSinceEpoch;
    }

    /// <summary>
    /// Gets the PostgreSQL day offset, including its infinity sentinels.
    /// </summary>
    public int DaysSinceEpoch { get; }

    /// <summary>
    /// Gets positive infinity.
    /// </summary>
    public static PgDate PositiveInfinity => new(int.MaxValue);

    /// <summary>
    /// Gets negative infinity.
    /// </summary>
    public static PgDate NegativeInfinity => new(int.MinValue);

    /// <summary>
    /// Gets whether this date is finite.
    /// </summary>
    public bool IsFinite => DaysSinceEpoch is not (int.MinValue or int.MaxValue);

    /// <summary>
    /// Gets the current transaction's date in the session timezone, like SQL CURRENT_DATE.
    /// </summary>
    public static PgDate CurrentDate => PgTemporal.Call<PgDate>(TemporalOperation.CurrentDate);

    /// <summary>
    /// Parses PostgreSQL date syntax using the current backend's DateStyle.
    /// </summary>
    /// <param name="text">The date text, including PostgreSQL special values.</param>
    /// <returns>The parsed date.</returns>
    public static PgDate Parse(string text) => PgTemporal.Call<PgDate>(TemporalOperation.Parse, PgTemporal.Text(text));

    /// <summary>
    /// Tries to parse PostgreSQL date syntax on the active backend thread.
    /// </summary>
    /// <param name="text">The date text.</param>
    /// <param name="value">The parsed date, or the default value on invalid input.</param>
    /// <returns>Whether the input is valid. Backend-access and operational errors still throw.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgDate value) => PgTemporal.TryParse(text, out value);

    /// <summary>
    /// Compares dates, including infinities, without requiring an active backend.
    /// </summary>
    /// <param name="other">The date to compare.</param>
    /// <returns>A negative value, zero, or a positive value for earlier, equal, or later dates.</returns>
    public int CompareTo(PgDate other) => DaysSinceEpoch.CompareTo(other.DaysSinceEpoch);

    /// <summary>
    /// Tests whether the left date precedes the right date.
    /// </summary>
    public static bool operator <(PgDate left, PgDate right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Tests whether the left date follows the right date.
    /// </summary>
    public static bool operator >(PgDate left, PgDate right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Tests whether the left date precedes or equals the right date.
    /// </summary>
    public static bool operator <=(PgDate left, PgDate right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Tests whether the left date follows or equals the right date.
    /// </summary>
    public static bool operator >=(PgDate left, PgDate right)
    {
        return left.CompareTo(right) >= 0;
    }

    /// <summary>
    /// Constructs a date using PostgreSQL's calendar; negative years denote BC and year zero is invalid.
    /// </summary>
    /// <param name="year">The signed year.</param>
    /// <param name="month">The month, one through twelve.</param>
    /// <param name="day">The day of month.</param>
    /// <returns>The date.</returns>
    public static PgDate Create(int year, int month, int day)
        => PgTemporal.Call<PgDate>(TemporalOperation.MakeDate,
            SpiParameter.Create(year), SpiParameter.Create(month), SpiParameter.Create(day));

    /// <summary>
    /// Formats the date using the current backend's DateStyle.
    /// </summary>
    /// <returns>The PostgreSQL text.</returns>
    public string ToPostgresString() => PgTemporal.Call<string>(TemporalOperation.Format, SpiParameter.Create(this));

    /// <summary>
    /// Formats the date using PostgreSQL's ISO JSON date representation, independently of DateStyle.
    /// </summary>
    /// <returns>The ISO date, or PostgreSQL's infinity spelling.</returns>
    public string ToIsoString() => PgTemporal.Call<string>(TemporalOperation.FormatIso, SpiParameter.Create(this));

    /// <summary>
    /// Reads a field with PostgreSQL date_part semantics, returning floating-point values.
    /// </summary>
    /// <param name="part">The field to read.</param>
    /// <returns>The field, or null for an undefined field of an infinite value.</returns>
    public double? GetPart(PgDateTimePart part)
        => PgTemporal.Call<double?>(TemporalOperation.Part, PgTemporal.Part(part), SpiParameter.Create(this));

    /// <summary>
    /// Extracts a numeric field exactly on PostgreSQL 14+; PostgreSQL 13 converts its floating-point result.
    /// </summary>
    /// <param name="part">The field to extract.</param>
    /// <returns>The numeric field, or null for an undefined field of infinity.</returns>
    public PgNumeric? Extract(PgDateTimePart part)
        => PgTemporal.Call<PgNumeric?>(TemporalOperation.Extract, PgTemporal.Part(part), SpiParameter.Create(this));

    /// <summary>
    /// Adds calendar days with PostgreSQL range and infinity rules.
    /// </summary>
    /// <param name="days">The signed number of days.</param>
    /// <returns>The resulting date.</returns>
    public PgDate AddDays(int days) => PgTemporal.Call<PgDate>(TemporalOperation.Add, SpiParameter.Create(this), SpiParameter.Create(days));

    /// <summary>
    /// Subtracts calendar days, including Int32.MinValue, with PostgreSQL range checks.
    /// </summary>
    /// <param name="days">The signed number of days.</param>
    /// <returns>The resulting date.</returns>
    public PgDate SubtractDays(int days) => PgTemporal.Call<PgDate>(TemporalOperation.Subtract, SpiParameter.Create(this), SpiParameter.Create(days));

    /// <summary>
    /// Adds calendar days using PostgreSQL's rules.
    /// </summary>
    public static PgDate operator +(PgDate date, int days)
    {
        return date.AddDays(days);
    }

    /// <summary>
    /// Adds calendar days using PostgreSQL's rules.
    /// </summary>
    public static PgDate operator +(int days, PgDate date)
    {
        return date.AddDays(days);
    }

    /// <summary>
    /// Subtracts calendar days using PostgreSQL's rules.
    /// </summary>
    public static PgDate operator -(PgDate date, int days)
    {
        return date.SubtractDays(days);
    }

    /// <summary>
    /// Computes the signed day difference.
    /// </summary>
    public static int operator -(PgDate left, PgDate right)
    {
        return left.Subtract(right);
    }

    /// <summary>
    /// Adds a calendar interval to a date.
    /// </summary>
    public static PgTimestamp operator +(PgDate date, PgInterval interval)
    {
        return date.Add(interval);
    }

    /// <summary>
    /// Adds a calendar interval to a date.
    /// </summary>
    public static PgTimestamp operator +(PgInterval interval, PgDate date)
    {
        return date.Add(interval);
    }

    /// <summary>
    /// Subtracts a calendar interval from a date.
    /// </summary>
    public static PgTimestamp operator -(PgDate date, PgInterval interval)
    {
        return date.Subtract(interval);
    }

    /// <summary>
    /// Combines a date and wall-clock time.
    /// </summary>
    public static PgTimestamp operator +(PgDate date, PgTime time)
    {
        return date.AtTime(time);
    }

    /// <summary>
    /// Combines a date and wall-clock time.
    /// </summary>
    public static PgTimestamp operator +(PgTime time, PgDate date)
    {
        return date.AtTime(time);
    }

    /// <summary>
    /// Combines a date and fixed-offset time.
    /// </summary>
    public static PgTimestampTz operator +(PgDate date, PgTimeTz time)
    {
        return date.AtTime(time);
    }

    /// <summary>
    /// Combines a date and fixed-offset time.
    /// </summary>
    public static PgTimestampTz operator +(PgTimeTz time, PgDate date)
    {
        return date.AtTime(time);
    }

    /// <summary>
    /// Adds a calendar interval, yielding a timestamp.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The resulting timestamp.</returns>
    public PgTimestamp Add(PgInterval interval)
        => PgTemporal.Call<PgTimestamp>(TemporalOperation.Add, SpiParameter.Create(this), SpiParameter.Create(interval));

    /// <summary>
    /// Subtracts a calendar interval, yielding a timestamp.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The resulting timestamp.</returns>
    public PgTimestamp Subtract(PgInterval interval)
        => PgTemporal.Call<PgTimestamp>(TemporalOperation.Subtract, SpiParameter.Create(this), SpiParameter.Create(interval));

    /// <summary>
    /// Computes the signed day difference using PostgreSQL's infinity rules.
    /// </summary>
    /// <param name="other">The date to subtract.</param>
    /// <returns>The number of days.</returns>
    public int Subtract(PgDate other)
        => PgTemporal.Call<int>(TemporalOperation.Subtract, SpiParameter.Create(this), SpiParameter.Create(other));

    /// <summary>
    /// Combines a date and wall-clock time, accepting PostgreSQL's 24:00 value.
    /// </summary>
    /// <param name="time">The time of day.</param>
    /// <returns>The timestamp.</returns>
    public PgTimestamp AtTime(PgTime time)
        => PgTemporal.Call<PgTimestamp>(TemporalOperation.Add, SpiParameter.Create(this), SpiParameter.Create(time));

    /// <summary>
    /// Combines a date and fixed-offset time to produce an instant.
    /// </summary>
    /// <param name="time">The time and fixed offset.</param>
    /// <returns>The UTC instant.</returns>
    public PgTimestampTz AtTime(PgTimeTz time)
        => PgTemporal.Call<PgTimestampTz>(TemporalOperation.Add, SpiParameter.Create(this), SpiParameter.Create(time));

    /// <summary>
    /// Converts the date to midnight without a timezone, checking PostgreSQL's narrower timestamp range.
    /// </summary>
    /// <returns>The midnight timestamp.</returns>
    public PgTimestamp ToTimestamp() => PgTemporal.Call<PgTimestamp>(TemporalOperation.ToTimestamp, SpiParameter.Create(this));

    /// <summary>
    /// Converts the date to midnight in the current session's timezone.
    /// </summary>
    /// <returns>The UTC instant.</returns>
    public PgTimestampTz ToTimestampTz() => PgTemporal.Call<PgTimestampTz>(TemporalOperation.ToTimestampTz, SpiParameter.Create(this));

    /// <summary>
    /// Converts a .NET date exactly; its minimum and maximum dates remain finite.
    /// </summary>
    /// <param name="value">The .NET date.</param>
    /// <returns>The PostgreSQL date.</returns>
    public static PgDate FromDateOnly(DateOnly value) => new(value.DayNumber - PgTemporal.EpochDayNumber);

    /// <summary>
    /// Converts a finite date in the .NET range to DateOnly; other values throw InvalidOperationException.
    /// </summary>
    /// <returns>The .NET date.</returns>
    /// <exception cref="InvalidOperationException">The date is infinite or outside years 1–9999.</exception>
    public DateOnly ToDateOnly()
    {
        long day = (long)DaysSinceEpoch + PgTemporal.EpochDayNumber;
        if (day < DateOnly.MinValue.DayNumber || day > DateOnly.MaxValue.DayNumber)
        {
            throw new InvalidOperationException("This PostgreSQL date cannot be represented by DateOnly.");
        }

        return DateOnly.FromDayNumber((int)day);
    }
}
