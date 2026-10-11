using System.Text.Json.Serialization;
using Ankus.Serialization;

namespace Ankus;

/// <summary>
/// Represents PostgreSQL timestamp without time zone, including BC values and infinities.
/// The default value is 2000-01-01 00:00:00.
/// </summary>
[JsonConverter(typeof(PgTimestampConverter))]
public readonly partial record struct PgTimestamp : IComparable<PgTimestamp>
{
    /// <summary>
    /// Creates a timestamp from microseconds relative to 2000-01-01. Int64 extremes represent infinities.
    /// </summary>
    /// <param name="microsecondsSinceEpoch">The signed PostgreSQL timestamp offset.</param>
    /// <exception cref="ArgumentOutOfRangeException">The finite value is outside PostgreSQL's timestamp range.</exception>
    public PgTimestamp(long microsecondsSinceEpoch)
    {
        PgTemporal.ValidateTimestamp(microsecondsSinceEpoch);
        MicrosecondsSinceEpoch = microsecondsSinceEpoch;
    }

    /// <summary>
    /// Gets the PostgreSQL microsecond offset, including its infinity sentinels.
    /// </summary>
    public long MicrosecondsSinceEpoch { get; }

    /// <summary>
    /// Gets positive infinity.
    /// </summary>
    public static PgTimestamp PositiveInfinity => new(long.MaxValue);

    /// <summary>
    /// Gets negative infinity.
    /// </summary>
    public static PgTimestamp NegativeInfinity => new(long.MinValue);

    /// <summary>
    /// Gets whether this timestamp is finite.
    /// </summary>
    public bool IsFinite => MicrosecondsSinceEpoch is not (long.MinValue or long.MaxValue);

    /// <summary>
    /// Constructs a timestamp using PostgreSQL's calendar, field validation, and fractional-second rounding.
    /// </summary>
    /// <param name="year">The signed year; negative means BC and zero is invalid.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day of month.</param>
    /// <param name="hour">The hour.</param>
    /// <param name="minute">The minute.</param>
    /// <param name="second">The fractional seconds.</param>
    /// <returns>The timestamp.</returns>
    public static PgTimestamp Create(int year, int month, int day, int hour, int minute, double second)
        => PgTemporal.Call<PgTimestamp>(TemporalOperation.MakeTimestamp, SpiParameter.Create(year), SpiParameter.Create(month),
            SpiParameter.Create(day), SpiParameter.Create(hour), SpiParameter.Create(minute), SpiParameter.Create(second));

    /// <summary>
    /// Gets SQL LOCALTIMESTAMP at the requested precision in the session timezone.
    /// </summary>
    /// <param name="precision">Fractional-second digits, zero through six.</param>
    /// <returns>The current transaction's local timestamp.</returns>
    public static PgTimestamp GetLocalTimestamp(int precision = 6)
        => PgTemporal.Call<PgTimestamp>(TemporalOperation.LocalTimestamp, PgTemporal.Precision(precision));

    /// <summary>
    /// Rounds fractional seconds using PostgreSQL's timestamp type modifier.
    /// </summary>
    /// <param name="precision">Fractional-second digits, zero through six.</param>
    /// <returns>The rounded timestamp.</returns>
    public PgTimestamp Round(int precision)
        => PgTemporal.Call<PgTimestamp>(TemporalOperation.Round, SpiParameter.Create(this), PgTemporal.Precision(precision));

    /// <summary>
    /// Adds a calendar interval using PostgreSQL's rules.
    /// </summary>
    public static PgTimestamp operator +(PgTimestamp timestamp, PgInterval interval)
    {
        return timestamp.Add(interval);
    }

    /// <summary>
    /// Adds a calendar interval using PostgreSQL's rules.
    /// </summary>
    public static PgTimestamp operator +(PgInterval interval, PgTimestamp timestamp)
    {
        return timestamp.Add(interval);
    }

    /// <summary>
    /// Subtracts a calendar interval using PostgreSQL's rules.
    /// </summary>
    public static PgTimestamp operator -(PgTimestamp timestamp, PgInterval interval)
    {
        return timestamp.Subtract(interval);
    }

    /// <summary>
    /// Computes the elapsed difference.
    /// </summary>
    public static PgInterval operator -(PgTimestamp left, PgTimestamp right)
    {
        return left.Subtract(right);
    }

    /// <summary>
    /// Parses PostgreSQL wall-clock timestamp syntax using the backend's DateStyle.
    /// </summary>
    /// <param name="text">The timestamp text.</param>
    /// <returns>The timestamp.</returns>
    public static PgTimestamp Parse(string text) => PgTemporal.Call<PgTimestamp>(TemporalOperation.Parse, PgTemporal.Text(text));

    /// <summary>
    /// Tries to parse PostgreSQL wall-clock timestamp syntax on the active backend thread.
    /// </summary>
    /// <param name="text">The timestamp text.</param>
    /// <param name="value">The parsed value, or the default value on invalid input.</param>
    /// <returns>Whether the input is valid. Backend-access and operational errors still throw.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgTimestamp value) => PgTemporal.TryParse(text, out value);

    /// <summary>
    /// Compares wall-clock timestamps and infinities without requiring an active backend.
    /// </summary>
    /// <param name="other">The timestamp to compare.</param>
    /// <returns>A negative value, zero, or a positive value for earlier, equal, or later timestamps.</returns>
    public int CompareTo(PgTimestamp other) => MicrosecondsSinceEpoch.CompareTo(other.MicrosecondsSinceEpoch);

    /// <summary>
    /// Tests whether the left timestamp precedes the right timestamp.
    /// </summary>
    public static bool operator <(PgTimestamp left, PgTimestamp right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Tests whether the left timestamp follows the right timestamp.
    /// </summary>
    public static bool operator >(PgTimestamp left, PgTimestamp right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Tests whether the left timestamp precedes or equals the right timestamp.
    /// </summary>
    public static bool operator <=(PgTimestamp left, PgTimestamp right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Tests whether the left timestamp follows or equals the right timestamp.
    /// </summary>
    public static bool operator >=(PgTimestamp left, PgTimestamp right)
    {
        return left.CompareTo(right) >= 0;
    }

    /// <summary>
    /// Formats the timestamp using the backend's DateStyle.
    /// </summary>
    /// <returns>The PostgreSQL text.</returns>
    public string ToPostgresString() => PgTemporal.Call<string>(TemporalOperation.Format, SpiParameter.Create(this));

    /// <summary>
    /// Formats the timestamp using PostgreSQL's ISO JSON representation independently of DateStyle.
    /// </summary>
    /// <returns>The ISO timestamp, or PostgreSQL's infinity spelling.</returns>
    public string ToIsoString() => PgTemporal.Call<string>(TemporalOperation.FormatIso, SpiParameter.Create(this));

    /// <summary>
    /// Adds an interval using PostgreSQL's calendar and month-end rules.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The resulting timestamp.</returns>
    public PgTimestamp Add(PgInterval interval)
        => PgTemporal.Call<PgTimestamp>(TemporalOperation.Add, SpiParameter.Create(this), SpiParameter.Create(interval));

    /// <summary>
    /// Subtracts an interval using PostgreSQL's calendar and month-end rules.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The resulting timestamp.</returns>
    public PgTimestamp Subtract(PgInterval interval)
        => PgTemporal.Call<PgTimestamp>(TemporalOperation.Subtract, SpiParameter.Create(this), SpiParameter.Create(interval));

    /// <summary>
    /// Computes the elapsed difference with PostgreSQL's interval normalization.
    /// </summary>
    /// <param name="other">The timestamp to subtract.</param>
    /// <returns>The signed interval.</returns>
    public PgInterval Subtract(PgTimestamp other)
        => PgTemporal.Call<PgInterval>(TemporalOperation.Subtract, SpiParameter.Create(this), SpiParameter.Create(other));

    /// <summary>
    /// Computes a symbolic difference retaining calendar years and months.
    /// </summary>
    /// <param name="other">The timestamp to subtract.</param>
    /// <returns>The calendar interval returned by PostgreSQL age.</returns>
    public PgInterval Age(PgTimestamp other)
        => PgTemporal.Call<PgInterval>(TemporalOperation.Age, SpiParameter.Create(this), SpiParameter.Create(other));

    /// <summary>
    /// Truncates to a PostgreSQL calendar field.
    /// </summary>
    /// <param name="part">The truncation field.</param>
    /// <returns>The truncated timestamp.</returns>
    public PgTimestamp Truncate(PgDateTimePart part)
        => PgTemporal.Call<PgTimestamp>(TemporalOperation.Truncate, PgTemporal.Part(part), SpiParameter.Create(this));

    /// <summary>
    /// Reads a floating-point field using PostgreSQL date_part semantics.
    /// </summary>
    /// <param name="part">The field.</param>
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
    /// Interprets this wall-clock time in a named timezone using PostgreSQL's DST gap and overlap rules.
    /// </summary>
    /// <param name="zone">The PostgreSQL timezone name or abbreviation.</param>
    /// <returns>The corresponding UTC instant.</returns>
    public PgTimestampTz AtTimeZone(string zone)
        => PgTemporal.Call<PgTimestampTz>(TemporalOperation.AtTimeZone, PgTemporal.Text(zone), SpiParameter.Create(this));

    /// <summary>
    /// Interprets this wall-clock time in the current session's timezone.
    /// </summary>
    /// <returns>The corresponding UTC instant.</returns>
    public PgTimestampTz ToTimestampTz() => PgTemporal.Call<PgTimestampTz>(TemporalOperation.ToTimestampTz, SpiParameter.Create(this));

    /// <summary>
    /// Extracts the calendar date using PostgreSQL's infinity rules.
    /// </summary>
    /// <returns>The date.</returns>
    public PgDate ToDate() => PgTemporal.Call<PgDate>(TemporalOperation.ToDate, SpiParameter.Create(this));

    /// <summary>
    /// Extracts the wall-clock time, or null for infinity.
    /// </summary>
    /// <returns>The time, or null when no finite time exists.</returns>
    public PgTime? ToTime() => PgTemporal.Call<PgTime?>(TemporalOperation.ToTime, SpiParameter.Create(this));

    /// <summary>
    /// Converts an unspecified DateTime, rejecting UTC/local kinds and sub-microsecond ticks.
    /// </summary>
    /// <param name="value">A timezone-free .NET timestamp.</param>
    /// <returns>The PostgreSQL timestamp.</returns>
    /// <exception cref="ArgumentException">The kind is not Unspecified or the value contains sub-microsecond ticks.</exception>
    public static PgTimestamp FromDateTime(DateTime value)
    {
        if (value.Kind != DateTimeKind.Unspecified)
        {
            throw new ArgumentException("A timestamp without time zone requires DateTimeKind.Unspecified.", nameof(value));
        }

        return new(PgTemporal.ToMicroseconds(value.Ticks - PgTemporal.EpochTicks));
    }

    /// <summary>
    /// Converts a representable finite value to DateTime with Kind Unspecified; other values throw InvalidOperationException.
    /// </summary>
    /// <returns>The timezone-free .NET timestamp.</returns>
    /// <exception cref="InvalidOperationException">The timestamp is infinite or outside the DateTime range.</exception>
    public DateTime ToDateTime() => PgTemporal.ToDateTime(MicrosecondsSinceEpoch, DateTimeKind.Unspecified);
}
