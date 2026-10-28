using System;
using System.Text.Json.Serialization;
using Ankus.Serialization;

namespace Ankus;

/// <summary>
/// Represents PostgreSQL time with time zone as a local time and a fixed offset, without a date or zone name.
/// </summary>
[JsonConverter(typeof(PgTimeTzConverter))]
public readonly partial record struct PgTimeTz : IComparable<PgTimeTz>
{
    /// <summary>
    /// Creates a time with a signed offset east of UTC, retaining second-resolution historical offsets.
    /// </summary>
    /// <param name="time">The local time, including 24:00:00.</param>
    /// <param name="offsetSeconds">The offset east of UTC, strictly between -57,600 and 57,600 seconds.</param>
    /// <exception cref="ArgumentOutOfRangeException">The offset is outside PostgreSQL's supported range.</exception>
    public PgTimeTz(PgTime time, int offsetSeconds)
    {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(offsetSeconds, -57_600);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(offsetSeconds, 57_600);
        Time = time;
        OffsetSeconds = offsetSeconds;
    }

    /// <summary>
    /// Gets the local wall-clock time.
    /// </summary>
    public PgTime Time { get; }

    /// <summary>
    /// Gets the offset east of UTC in seconds; positive values correspond to SQL offsets such as +05:30.
    /// </summary>
    public int OffsetSeconds { get; }

    /// <summary>
    /// Gets the fixed UTC offset as a TimeSpan.
    /// </summary>
    public TimeSpan Offset => TimeSpan.FromSeconds(OffsetSeconds);

    /// <summary>
    /// Constructs a local time with the session timezone's current-date offset.
    /// </summary>
    /// <param name="hour">The hour.</param>
    /// <param name="minute">The minute.</param>
    /// <param name="second">The fractional seconds, rounded by PostgreSQL.</param>
    /// <returns>The local time and offset.</returns>
    public static PgTimeTz Create(int hour, int minute, double second) => PgTime.Create(hour, minute, second).ToTimeTz();

    /// <summary>
    /// Constructs a local time with an explicit second-resolution offset east of UTC.
    /// </summary>
    /// <param name="hour">The hour.</param>
    /// <param name="minute">The minute.</param>
    /// <param name="second">The fractional seconds, rounded by PostgreSQL.</param>
    /// <param name="offsetSeconds">The offset east of UTC, strictly between -57600 and 57600.</param>
    /// <returns>The local time and offset.</returns>
    public static PgTimeTz Create(int hour, int minute, double second, int offsetSeconds)
        => new(PgTime.Create(hour, minute, second), offsetSeconds);

    /// <summary>
    /// Gets SQL CURRENT_TIME at the requested precision in the session timezone.
    /// </summary>
    /// <param name="precision">Fractional-second digits, zero through six.</param>
    /// <returns>The current transaction's local time and offset.</returns>
    public static PgTimeTz GetCurrentTime(int precision = 6)
        => PgTemporal.Call<PgTimeTz>(TemporalOperation.CurrentTime, PgTemporal.Precision(precision));

    /// <summary>
    /// Rounds fractional seconds, retaining the offset, with PostgreSQL's type modifier.
    /// </summary>
    /// <param name="precision">Fractional-second digits, zero through six.</param>
    /// <returns>The rounded time and offset.</returns>
    public PgTimeTz Round(int precision)
        => PgTemporal.Call<PgTimeTz>(TemporalOperation.Round, SpiParameter.Create(this), PgTemporal.Precision(precision));

    /// <summary>
    /// Formats the shifted time and offset in a named zone using PostgreSQL's current-date DST rules.
    /// </summary>
    /// <param name="zone">The PostgreSQL timezone name or abbreviation.</param>
    /// <returns>The ISO time and offset.</returns>
    public string ToIsoString(string zone) => AtTimeZone(zone).ToIsoString();

    /// <summary>
    /// Adds an interval while retaining the fixed offset.
    /// </summary>
    public static PgTimeTz operator +(PgTimeTz time, PgInterval interval)
    {
        return time.Add(interval);
    }

    /// <summary>
    /// Adds an interval while retaining the fixed offset.
    /// </summary>
    public static PgTimeTz operator +(PgInterval interval, PgTimeTz time)
    {
        return time.Add(interval);
    }

    /// <summary>
    /// Subtracts an interval while retaining the fixed offset.
    /// </summary>
    public static PgTimeTz operator -(PgTimeTz time, PgInterval interval)
    {
        return time.Subtract(interval);
    }

    /// <summary>
    /// Parses PostgreSQL fixed-offset time syntax on the active backend thread.
    /// </summary>
    /// <param name="text">The time and timezone text.</param>
    /// <returns>The parsed time and offset.</returns>
    public static PgTimeTz Parse(string text) => PgTemporal.Call<PgTimeTz>(TemporalOperation.Parse, PgTemporal.Text(text));

    /// <summary>
    /// Tries to parse PostgreSQL fixed-offset time syntax on the active backend thread.
    /// </summary>
    /// <param name="text">The time and offset text.</param>
    /// <param name="value">The parsed value, or the default value on invalid input.</param>
    /// <returns>Whether the input is valid. Backend-access and operational errors still throw.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgTimeTz value) => PgTemporal.TryParse(text, out value);

    /// <summary>
    /// Compares UTC-adjusted times without wrapping at midnight, breaking ties by PostgreSQL's offset order.
    /// </summary>
    /// <param name="other">The time and offset to compare.</param>
    /// <returns>A negative value, zero, or a positive value for lesser, equal, or greater values. No backend is required.</returns>
    public int CompareTo(PgTimeTz other)
    {
        long utc = Time.Microseconds - (long)OffsetSeconds * 1_000_000;
        long otherUtc = other.Time.Microseconds - (long)other.OffsetSeconds * 1_000_000;
        int order = utc.CompareTo(otherUtc);
        return order != 0 ? order : other.OffsetSeconds.CompareTo(OffsetSeconds);
    }

    /// <summary>
    /// Tests whether the left value sorts before the right value.
    /// </summary>
    public static bool operator <(PgTimeTz left, PgTimeTz right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Tests whether the left value sorts after the right value.
    /// </summary>
    public static bool operator >(PgTimeTz left, PgTimeTz right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Tests whether the left value sorts before or equals the right value.
    /// </summary>
    public static bool operator <=(PgTimeTz left, PgTimeTz right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Tests whether the left value sorts after or equals the right value.
    /// </summary>
    public static bool operator >=(PgTimeTz left, PgTimeTz right)
    {
        return left.CompareTo(right) >= 0;
    }

    /// <summary>
    /// Formats the time and offset using PostgreSQL's output routine.
    /// </summary>
    /// <returns>The PostgreSQL text.</returns>
    public string ToPostgresString() => PgTemporal.Call<string>(TemporalOperation.Format, SpiParameter.Create(this));

    /// <summary>
    /// Formats the time and offset using PostgreSQL's ISO JSON representation.
    /// </summary>
    /// <returns>The ISO time and offset.</returns>
    public string ToIsoString() => PgTemporal.Call<string>(TemporalOperation.FormatIso, SpiParameter.Create(this));

    /// <summary>
    /// Reads a floating-point field using PostgreSQL date_part semantics.
    /// </summary>
    /// <param name="part">The field.</param>
    /// <returns>The field value.</returns>
    public double? GetPart(PgDateTimePart part)
        => PgTemporal.Call<double?>(TemporalOperation.Part, PgTemporal.Part(part), SpiParameter.Create(this));

    /// <summary>
    /// Extracts a numeric field exactly on PostgreSQL 14+; PostgreSQL 13 converts its floating-point result.
    /// </summary>
    /// <param name="part">The field to extract.</param>
    /// <returns>The numeric field.</returns>
    public PgNumeric? Extract(PgDateTimePart part)
        => PgTemporal.Call<PgNumeric?>(TemporalOperation.Extract, PgTemporal.Part(part), SpiParameter.Create(this));

    /// <summary>
    /// Converts the time to a named zone using PostgreSQL's timetz rules, including its current-date DST resolution.
    /// </summary>
    /// <param name="zone">The PostgreSQL timezone name or abbreviation.</param>
    /// <returns>The shifted local time and offset.</returns>
    public PgTimeTz AtTimeZone(string zone)
        => PgTemporal.Call<PgTimeTz>(TemporalOperation.AtTimeZone, PgTemporal.Text(zone), SpiParameter.Create(this));

    /// <summary>
    /// Removes the offset without shifting the local time.
    /// </summary>
    /// <returns>The wall-clock time.</returns>
    public PgTime ToTime() => PgTemporal.Call<PgTime>(TemporalOperation.ToTime, SpiParameter.Create(this));

    /// <summary>
    /// Adds the interval's time component while retaining the fixed offset.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The resulting time and offset.</returns>
    public PgTimeTz Add(PgInterval interval)
        => PgTemporal.Call<PgTimeTz>(TemporalOperation.Add, SpiParameter.Create(this), SpiParameter.Create(interval));

    /// <summary>
    /// Subtracts the interval's time component while retaining the fixed offset.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The resulting time and offset.</returns>
    public PgTimeTz Subtract(PgInterval interval)
        => PgTemporal.Call<PgTimeTz>(TemporalOperation.Subtract, SpiParameter.Create(this), SpiParameter.Create(interval));
}
