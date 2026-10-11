using System.Text.Json.Serialization;
using Ankus.Serialization;

namespace Ankus;

/// <summary>
/// Represents PostgreSQL time without time zone at microsecond precision, including 24:00:00.
/// </summary>
[JsonConverter(typeof(PgTimeConverter))]
public readonly partial record struct PgTime : IComparable<PgTime>
{
    /// <summary>
    /// Creates a time from microseconds since midnight, between zero and 86,400,000,000 inclusive.
    /// </summary>
    /// <param name="microseconds">The time of day in microseconds.</param>
    /// <exception cref="ArgumentOutOfRangeException">The offset is negative or later than 24:00:00.</exception>
    public PgTime(long microseconds)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(microseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(microseconds, PgTemporal.MicrosecondsPerDay);
        Microseconds = microseconds;
    }

    /// <summary>
    /// Gets the microseconds since midnight. The end-of-day value is distinct from midnight.
    /// </summary>
    public long Microseconds { get; }

    /// <summary>
    /// Gets PostgreSQL's 24:00:00 value.
    /// </summary>
    public static PgTime EndOfDay => new(PgTemporal.MicrosecondsPerDay);

    /// <summary>
    /// Gets SQL LOCALTIME at the requested precision in the session timezone.
    /// </summary>
    /// <param name="precision">Fractional-second digits, zero through six.</param>
    /// <returns>The current transaction's local time.</returns>
    public static PgTime GetLocalTime(int precision = 6)
        => PgTemporal.Call<PgTime>(TemporalOperation.LocalTime, PgTemporal.Precision(precision));

    /// <summary>
    /// Rounds fractional seconds using PostgreSQL's time type modifier.
    /// </summary>
    /// <param name="precision">Fractional-second digits, zero through six.</param>
    /// <returns>The rounded time, possibly 24:00.</returns>
    public PgTime Round(int precision)
        => PgTemporal.Call<PgTime>(TemporalOperation.Round, SpiParameter.Create(this), PgTemporal.Precision(precision));

    /// <summary>
    /// Attaches the session timezone's offset using PostgreSQL's current-date rules.
    /// </summary>
    /// <returns>The local time and offset.</returns>
    public PgTimeTz ToTimeTz() => PgTemporal.Call<PgTimeTz>(TemporalOperation.ToTimeTz, SpiParameter.Create(this));

    /// <summary>
    /// Adds an interval, wrapping at midnight.
    /// </summary>
    public static PgTime operator +(PgTime time, PgInterval interval)
    {
        return time.Add(interval);
    }

    /// <summary>
    /// Adds an interval, wrapping at midnight.
    /// </summary>
    public static PgTime operator +(PgInterval interval, PgTime time)
    {
        return time.Add(interval);
    }

    /// <summary>
    /// Subtracts an interval, wrapping at midnight.
    /// </summary>
    public static PgTime operator -(PgTime time, PgInterval interval)
    {
        return time.Subtract(interval);
    }

    /// <summary>
    /// Computes the signed wall-clock difference.
    /// </summary>
    public static PgInterval operator -(PgTime left, PgTime right)
    {
        return left.Subtract(right);
    }

    /// <summary>
    /// Parses PostgreSQL time syntax on the active backend thread.
    /// </summary>
    /// <param name="text">The time text.</param>
    /// <returns>The parsed time.</returns>
    public static PgTime Parse(string text) => PgTemporal.Call<PgTime>(TemporalOperation.Parse, PgTemporal.Text(text));

    /// <summary>
    /// Tries to parse PostgreSQL time syntax on the active backend thread.
    /// </summary>
    /// <param name="text">The time text.</param>
    /// <param name="value">The parsed time, or the default value on invalid input.</param>
    /// <returns>Whether the input is valid. Backend-access and operational errors still throw.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgTime value) => PgTemporal.TryParse(text, out value);

    /// <summary>
    /// Compares wall-clock times, retaining 24:00 as later than midnight, without requiring an active backend.
    /// </summary>
    /// <param name="other">The time to compare.</param>
    /// <returns>A negative value, zero, or a positive value for earlier, equal, or later times.</returns>
    public int CompareTo(PgTime other) => Microseconds.CompareTo(other.Microseconds);

    /// <summary>
    /// Tests whether the left time precedes the right time.
    /// </summary>
    public static bool operator <(PgTime left, PgTime right)
    {
        return left.CompareTo(right) < 0;
    }

    /// <summary>
    /// Tests whether the left time follows the right time.
    /// </summary>
    public static bool operator >(PgTime left, PgTime right)
    {
        return left.CompareTo(right) > 0;
    }

    /// <summary>
    /// Tests whether the left time precedes or equals the right time.
    /// </summary>
    public static bool operator <=(PgTime left, PgTime right)
    {
        return left.CompareTo(right) <= 0;
    }

    /// <summary>
    /// Tests whether the left time follows or equals the right time.
    /// </summary>
    public static bool operator >=(PgTime left, PgTime right)
    {
        return left.CompareTo(right) >= 0;
    }

    /// <summary>
    /// Constructs a time using PostgreSQL's field validation and fractional-second rounding.
    /// </summary>
    /// <param name="hour">The hour, including 24 only for the end-of-day value.</param>
    /// <param name="minute">The minute.</param>
    /// <param name="second">The seconds, including a fractional part.</param>
    /// <returns>The time.</returns>
    public static PgTime Create(int hour, int minute, double second)
        => PgTemporal.Call<PgTime>(TemporalOperation.MakeTime,
            SpiParameter.Create(hour), SpiParameter.Create(minute), SpiParameter.Create(second));

    /// <summary>
    /// Formats the time using PostgreSQL's output routine.
    /// </summary>
    /// <returns>The time text.</returns>
    public string ToPostgresString() => PgTemporal.Call<string>(TemporalOperation.Format, SpiParameter.Create(this));

    /// <summary>
    /// Formats the time using PostgreSQL's ISO JSON representation.
    /// </summary>
    /// <returns>The ISO time.</returns>
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
    /// Adds the interval's time component, wrapping at midnight as PostgreSQL does.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The resulting time.</returns>
    public PgTime Add(PgInterval interval)
        => PgTemporal.Call<PgTime>(TemporalOperation.Add, SpiParameter.Create(this), SpiParameter.Create(interval));

    /// <summary>
    /// Subtracts the interval's time component, wrapping at midnight.
    /// </summary>
    /// <param name="interval">The interval.</param>
    /// <returns>The resulting time.</returns>
    public PgTime Subtract(PgInterval interval)
        => PgTemporal.Call<PgTime>(TemporalOperation.Subtract, SpiParameter.Create(this), SpiParameter.Create(interval));

    /// <summary>
    /// Subtracts another wall-clock time without wrapping the difference.
    /// </summary>
    /// <param name="other">The time to subtract.</param>
    /// <returns>The signed interval.</returns>
    public PgInterval Subtract(PgTime other)
        => PgTemporal.Call<PgInterval>(TemporalOperation.Subtract, SpiParameter.Create(this), SpiParameter.Create(other));

    /// <summary>
    /// Converts a .NET time, rejecting sub-microsecond ticks.
    /// </summary>
    /// <param name="value">The .NET time.</param>
    /// <returns>The PostgreSQL time.</returns>
    /// <exception cref="ArgumentException">The value contains sub-microsecond ticks.</exception>
    public static PgTime FromTimeOnly(TimeOnly value) => new(PgTemporal.ToMicroseconds(value.Ticks));

    /// <summary>
    /// Converts to TimeOnly, rejecting 24:00:00 rather than wrapping to midnight.
    /// </summary>
    /// <returns>The .NET time.</returns>
    /// <exception cref="InvalidOperationException">The value is 24:00:00.</exception>
    public TimeOnly ToTimeOnly()
        => Microseconds == PgTemporal.MicrosecondsPerDay
            ? throw new InvalidOperationException("TimeOnly cannot represent 24:00:00.")
            : new TimeOnly(Microseconds * TimeSpan.TicksPerMicrosecond);
}
