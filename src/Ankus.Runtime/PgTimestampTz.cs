using System;
using System.Text.Json.Serialization;
using Ankus.Serialization;

namespace Ankus;

/// <summary>
/// Represents a PostgreSQL timestamp with time zone as a UTC instant, including infinities.
/// PostgreSQL does not retain the original offset or zone name.
/// </summary>
[JsonConverter(typeof(PgTimestampTzConverter))]
public readonly partial record struct PgTimestampTz : IComparable<PgTimestampTz>
{
    /// <summary>
    /// Creates an instant from microseconds relative to 2000-01-01 UTC. Int64 extremes represent infinities.
    /// </summary>
    /// <param name="microsecondsSinceEpoch">The signed PostgreSQL timestamp offset.</param>
    /// <exception cref="ArgumentOutOfRangeException">The finite value is outside PostgreSQL's timestamp range.</exception>
    public PgTimestampTz(long microsecondsSinceEpoch)
    {
        PgTemporal.ValidateTimestamp(microsecondsSinceEpoch);
        MicrosecondsSinceEpoch = microsecondsSinceEpoch;
    }

    /// <summary>
    /// Gets the UTC microsecond offset, including PostgreSQL's infinity sentinels.
    /// </summary>
    public long MicrosecondsSinceEpoch { get; }

    /// <summary>
    /// Gets positive infinity.
    /// </summary>
    public static PgTimestampTz PositiveInfinity => new(long.MaxValue);

    /// <summary>
    /// Gets negative infinity.
    /// </summary>
    public static PgTimestampTz NegativeInfinity => new(long.MinValue);

    /// <summary>
    /// Gets whether this instant is finite.
    /// </summary>
    public bool IsFinite => MicrosecondsSinceEpoch is not (long.MinValue or long.MaxValue);

    /// <summary>
    /// Constructs an instant from local fields in the session timezone using PostgreSQL's DST rules.
    /// </summary>
    /// <param name="year">The signed year; negative means BC and zero is invalid.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day.</param>
    /// <param name="hour">The hour.</param>
    /// <param name="minute">The minute.</param>
    /// <param name="second">The fractional seconds, rounded by PostgreSQL.</param>
    /// <returns>The UTC instant.</returns>
    public static PgTimestampTz Create(int year, int month, int day, int hour, int minute, double second)
        => PgTemporal.Call<PgTimestampTz>(TemporalOperation.MakeTimestampTz, SpiParameter.Create(year), SpiParameter.Create(month),
            SpiParameter.Create(day), SpiParameter.Create(hour), SpiParameter.Create(minute), SpiParameter.Create(second));

    /// <summary>
    /// Constructs an instant from local fields in a named timezone using PostgreSQL's DST rules.
    /// </summary>
    /// <param name="year">The signed year; negative means BC and zero is invalid.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day.</param>
    /// <param name="hour">The hour.</param>
    /// <param name="minute">The minute.</param>
    /// <param name="second">The fractional seconds, rounded by PostgreSQL.</param>
    /// <param name="zone">The PostgreSQL timezone name or abbreviation.</param>
    /// <returns>The UTC instant.</returns>
    public static PgTimestampTz Create(int year, int month, int day, int hour, int minute, double second, string zone)
        => PgTemporal.Call<PgTimestampTz>(TemporalOperation.MakeTimestampTz, SpiParameter.Create(year), SpiParameter.Create(month),
            SpiParameter.Create(day), SpiParameter.Create(hour), SpiParameter.Create(minute), SpiParameter.Create(second), PgTemporal.Text(zone));

    /// <summary>
    /// Gets SQL CURRENT_TIMESTAMP rounded to the requested precision.
    /// </summary>
    /// <param name="precision">Fractional-second digits, zero through six.</param>
    /// <returns>The current transaction's UTC instant.</returns>
    public static PgTimestampTz GetCurrentTimestamp(int precision = 6)
        => PgTemporal.Call<PgTimestampTz>(TemporalOperation.CurrentTimestamp, PgTemporal.Precision(precision));

    /// <summary>
    /// Rounds fractional seconds using PostgreSQL's timestamptz type modifier.
    /// </summary>
    /// <param name="precision">Fractional-second digits, zero through six.</param>
    /// <returns>The rounded instant.</returns>
    public PgTimestampTz Round(int precision)
        => PgTemporal.Call<PgTimestampTz>(TemporalOperation.Round, SpiParameter.Create(this), PgTemporal.Precision(precision));

    /// <summary>
    /// Formats this instant in an explicit zone using the offset applicable at this instant, independently of session settings.
    /// </summary>
    /// <param name="zone">The PostgreSQL timezone name or abbreviation.</param>
    /// <returns>The ISO timestamp and offset, or an infinity spelling.</returns>
    public string ToIsoString(string zone)
        => PgTemporal.Call<string>(TemporalOperation.FormatIsoZone, SpiParameter.Create(this), PgTemporal.Text(zone));

    /// <summary>
    /// Extracts local time and offset in the session timezone, or null for infinity.
    /// </summary>
    /// <returns>The local time and offset.</returns>
    public PgTimeTz? ToTimeTz() => PgTemporal.Call<PgTimeTz?>(TemporalOperation.ToTimeTz, SpiParameter.Create(this));

    /// <summary>
    /// Adds a calendar interval in the session timezone.
    /// </summary>
    public static PgTimestampTz operator +(PgTimestampTz timestamp, PgInterval interval)
    {
        return timestamp.Add(interval);
    }

    /// <summary>
    /// Adds a calendar interval in the session timezone.
    /// </summary>
    public static PgTimestampTz operator +(PgInterval interval, PgTimestampTz timestamp)
    {
        return timestamp.Add(interval);
    }

    /// <summary>
    /// Subtracts a calendar interval in the session timezone.
    /// </summary>
    public static PgTimestampTz operator -(PgTimestampTz timestamp, PgInterval interval)
    {
        return timestamp.Subtract(interval);
    }

    /// <summary>
    /// Computes the elapsed difference.
    /// </summary>
    public static PgInterval operator -(PgTimestampTz left, PgTimestampTz right)
    {
        return left.Subtract(right);
    }

    /// <summary>
    /// Parses PostgreSQL timestamp syntax, resolving omitted zones with the session timezone.
    /// </summary>
    /// <param name="text">The timestamp text.</param>
    /// <returns>The UTC instant.</returns>
    public static PgTimestampTz Parse(string text) => PgTemporal.Call<PgTimestampTz>(TemporalOperation.Parse, PgTemporal.Text(text));

    /// <summary>
    /// Tries to parse PostgreSQL timestamp syntax on the active backend thread.
    /// </summary>
    /// <param name="text">The timestamp text.</param>
    /// <param name="value">The parsed instant, or the default value on invalid input.</param>
    /// <returns>Whether the input is valid. Backend-access and operational errors still throw.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgTimestampTz value) => PgTemporal.TryParse(text, out value);

    /// <summary>
    /// Compares UTC instants and infinities without requiring an active backend.
    /// </summary>
    /// <param name="other">The instant to compare.</param>
    /// <returns>A negative value, zero, or a positive value for earlier, equal, or later instants.</returns>
    public int CompareTo(PgTimestampTz other) => MicrosecondsSinceEpoch.CompareTo(other.MicrosecondsSinceEpoch);

    /// <summary>
    /// Tests whether the left instant precedes the right instant.
    /// </summary>
    public static bool operator <(PgTimestampTz left, PgTimestampTz right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Tests whether the left instant follows the right instant.
    /// </summary>
    public static bool operator >(PgTimestampTz left, PgTimestampTz right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Tests whether the left instant precedes or equals the right instant.
    /// </summary>
    public static bool operator <=(PgTimestampTz left, PgTimestampTz right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Tests whether the left instant follows or equals the right instant.
    /// </summary>
    public static bool operator >=(PgTimestampTz left, PgTimestampTz right)
    {
        return left.CompareTo(right) >= 0;
    }

    /// <summary>
    /// Formats the instant using the session timezone and DateStyle.
    /// </summary>
    /// <returns>The PostgreSQL text.</returns>
    public string ToPostgresString() => PgTemporal.Call<string>(TemporalOperation.Format, SpiParameter.Create(this));

    /// <summary>
    /// Formats the instant in the session timezone using PostgreSQL's ISO JSON representation.
    /// </summary>
    /// <returns>The ISO timestamp with offset, or PostgreSQL's infinity spelling.</returns>
    public string ToIsoString() => PgTemporal.Call<string>(TemporalOperation.FormatIso, SpiParameter.Create(this));

    /// <summary>
    /// Gets the start of the current PostgreSQL transaction.
    /// </summary>
    public static PgTimestampTz TransactionTimestamp => PgTemporal.Call<PgTimestampTz>(TemporalOperation.TransactionTimestamp);

    /// <summary>
    /// Gets the start of the current PostgreSQL statement.
    /// </summary>
    public static PgTimestampTz StatementTimestamp => PgTemporal.Call<PgTimestampTz>(TemporalOperation.StatementTimestamp);

    /// <summary>
    /// Gets PostgreSQL's current wall-clock timestamp, which can change during a statement.
    /// </summary>
    public static PgTimestampTz ClockTimestamp => PgTemporal.Call<PgTimestampTz>(TemporalOperation.ClockTimestamp);

    /// <summary>
    /// Converts fractional Unix epoch seconds with PostgreSQL's to_timestamp rules.
    /// </summary>
    /// <param name="seconds">Seconds relative to 1970-01-01 UTC.</param>
    /// <returns>The UTC instant.</returns>
    public static PgTimestampTz FromUnixTimeSeconds(double seconds)
        => PgTemporal.Call<PgTimestampTz>(TemporalOperation.FromUnixTimeSeconds, SpiParameter.Create(seconds));

    /// <summary>
    /// Adds a calendar interval in the session timezone, retaining daylight-saving distinctions.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The resulting UTC instant.</returns>
    public PgTimestampTz Add(PgInterval interval)
        => PgTemporal.Call<PgTimestampTz>(TemporalOperation.Add, SpiParameter.Create(this), SpiParameter.Create(interval));

    /// <summary>
    /// Subtracts a calendar interval in the session timezone.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The resulting UTC instant.</returns>
    public PgTimestampTz Subtract(PgInterval interval)
        => PgTemporal.Call<PgTimestampTz>(TemporalOperation.Subtract, SpiParameter.Create(this), SpiParameter.Create(interval));

    /// <summary>
    /// Computes the elapsed difference using PostgreSQL interval normalization.
    /// </summary>
    /// <param name="other">The instant to subtract.</param>
    /// <returns>The signed interval.</returns>
    public PgInterval Subtract(PgTimestampTz other)
        => PgTemporal.Call<PgInterval>(TemporalOperation.Subtract, SpiParameter.Create(this), SpiParameter.Create(other));

    /// <summary>
    /// Computes a symbolic calendar difference in the session timezone.
    /// </summary>
    /// <param name="other">The instant to subtract.</param>
    /// <returns>The calendar interval returned by PostgreSQL age.</returns>
    public PgInterval Age(PgTimestampTz other)
        => PgTemporal.Call<PgInterval>(TemporalOperation.Age, SpiParameter.Create(this), SpiParameter.Create(other));

    /// <summary>
    /// Truncates to a calendar field in the session timezone.
    /// </summary>
    /// <param name="part">The truncation field.</param>
    /// <returns>The truncated instant.</returns>
    public PgTimestampTz Truncate(PgDateTimePart part)
        => PgTemporal.Call<PgTimestampTz>(TemporalOperation.Truncate, PgTemporal.Part(part), SpiParameter.Create(this));

    /// <summary>
    /// Truncates to a calendar field in an explicit timezone without changing session configuration.
    /// </summary>
    /// <param name="part">The truncation field.</param>
    /// <param name="zone">The PostgreSQL timezone name.</param>
    /// <returns>The truncated instant.</returns>
    public PgTimestampTz Truncate(PgDateTimePart part, string zone)
        => PgTemporal.Call<PgTimestampTz>(TemporalOperation.Truncate,
            PgTemporal.Part(part), SpiParameter.Create(this), PgTemporal.Text(zone));

    /// <summary>
    /// Reads a floating-point field in the session timezone with PostgreSQL date_part semantics.
    /// </summary>
    /// <param name="part">The field.</param>
    /// <returns>The field, or null for an undefined field of an infinite value.</returns>
    public double? GetPart(PgDateTimePart part)
        => PgTemporal.Call<double?>(TemporalOperation.Part, PgTemporal.Part(part), SpiParameter.Create(this));

    /// <summary>
    /// Extracts a field in the session timezone, exactly on PostgreSQL 14+; PostgreSQL 13 converts its floating-point result.
    /// </summary>
    /// <param name="part">The field to extract.</param>
    /// <returns>The numeric field, or null for an undefined field of infinity.</returns>
    public PgNumeric? Extract(PgDateTimePart part)
        => PgTemporal.Call<PgNumeric?>(TemporalOperation.Extract, PgTemporal.Part(part), SpiParameter.Create(this));

    /// <summary>
    /// Converts the instant to a wall-clock timestamp in a named timezone.
    /// </summary>
    /// <param name="zone">The PostgreSQL timezone name or abbreviation.</param>
    /// <returns>The local wall-clock timestamp.</returns>
    public PgTimestamp AtTimeZone(string zone)
        => PgTemporal.Call<PgTimestamp>(TemporalOperation.AtTimeZone, PgTemporal.Text(zone), SpiParameter.Create(this));

    /// <summary>
    /// Converts the instant to a wall-clock timestamp in the session timezone.
    /// </summary>
    /// <returns>The local timestamp.</returns>
    public PgTimestamp ToTimestamp() => PgTemporal.Call<PgTimestamp>(TemporalOperation.ToTimestamp, SpiParameter.Create(this));

    /// <summary>
    /// Extracts the calendar date in the session timezone.
    /// </summary>
    /// <returns>The date.</returns>
    public PgDate ToDate() => PgTemporal.Call<PgDate>(TemporalOperation.ToDate, SpiParameter.Create(this));

    /// <summary>
    /// Extracts the wall-clock time in the session timezone, or null for infinity.
    /// </summary>
    /// <returns>The local time, or null when no finite time exists.</returns>
    public PgTime? ToTime() => PgTemporal.Call<PgTime?>(TemporalOperation.ToTime, SpiParameter.Create(this));

    /// <summary>
    /// Converts an offset timestamp to its UTC instant, rejecting sub-microsecond ticks.
    /// </summary>
    /// <param name="value">The .NET instant with an offset.</param>
    /// <returns>The PostgreSQL instant.</returns>
    /// <exception cref="ArgumentException">The value contains sub-microsecond ticks.</exception>
    public static PgTimestampTz FromDateTimeOffset(DateTimeOffset value)
        => new(PgTemporal.ToMicroseconds(value.UtcTicks - PgTemporal.EpochTicks));

    /// <summary>
    /// Converts a representable finite instant to a zero-offset DateTimeOffset; other values throw InvalidOperationException.
    /// </summary>
    /// <returns>The UTC .NET instant.</returns>
    /// <exception cref="InvalidOperationException">The instant is infinite or outside the DateTimeOffset range.</exception>
    public DateTimeOffset ToDateTimeOffset() => new(PgTemporal.ToDateTime(MicrosecondsSinceEpoch, DateTimeKind.Utc));
}
