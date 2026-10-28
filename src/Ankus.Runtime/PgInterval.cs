using System;
using System.Text.Json.Serialization;
using Ankus.Serialization;

namespace Ankus;

/// <summary>
/// Preserves PostgreSQL interval's independent month, day, and microsecond components, including mixed signs.
/// Equality and ordering use PostgreSQL's thirty-day-month comparison convention without changing stored components.
/// JSON uses the session-independent ISO 8601 form returned by ToIsoString.
/// </summary>
[JsonConverter(typeof(PgIntervalConverter))]
public readonly partial record struct PgInterval : IComparable<PgInterval>
{
    private readonly int _infinity;

    private PgInterval(int infinity) => _infinity = infinity;

    /// <summary>
    /// Creates an interval without normalizing months to days or days to hours.
    /// </summary>
    /// <param name="months">The signed month component.</param>
    /// <param name="days">The signed day component.</param>
    /// <param name="microseconds">The signed time component.</param>
    public PgInterval(int months, int days, long microseconds)
    {
        Months = months;
        Days = days;
        Microseconds = microseconds;
    }

    /// <summary>
    /// Gets the month component, without assuming a fixed month length.
    /// </summary>
    public int Months { get; }

    /// <summary>
    /// Gets the calendar-day component, distinct from twenty-four elapsed hours across daylight-saving changes.
    /// </summary>
    public int Days { get; }

    /// <summary>
    /// Gets the time component in microseconds, which may exceed a day.
    /// </summary>
    public long Microseconds { get; }

    /// <summary>
    /// Gets positive infinity, supported by PostgreSQL 17 and later.
    /// Its finite component properties are zero.
    /// </summary>
    public static PgInterval PositiveInfinity => new(1);

    /// <summary>
    /// Gets negative infinity, supported by PostgreSQL 17 and later.
    /// Its finite component properties are zero.
    /// </summary>
    public static PgInterval NegativeInfinity => new(-1);

    /// <summary>
    /// Gets whether this interval has finite components. Infinity is distinct from any finite component combination.
    /// </summary>
    public bool IsFinite => _infinity == 0;

    /// <summary>
    /// Gets the native transport discriminator: negative one for negative infinity, zero for finite, or one for positive infinity.
    /// </summary>
    internal int Infinity => _infinity;

    /// <summary>
    /// Constructs an interval with PostgreSQL's make_interval rules, allowing mixed component signs.
    /// </summary>
    /// <param name="years">Calendar years.</param>
    /// <param name="months">Additional calendar months.</param>
    /// <param name="weeks">Calendar weeks.</param>
    /// <param name="days">Additional calendar days.</param>
    /// <param name="hours">Elapsed hours.</param>
    /// <param name="minutes">Elapsed minutes.</param>
    /// <param name="seconds">Elapsed fractional seconds, rounded by PostgreSQL.</param>
    /// <returns>The interval, retaining calendar and elapsed-time components.</returns>
    /// <remarks>
    /// Component arithmetic and conversion to microseconds reject overflow on every supported PostgreSQL version.
    /// Fractional seconds use PostgreSQL's native rounding rules.
    /// </remarks>
    public static PgInterval Create(int years = 0, int months = 0, int weeks = 0, int days = 0,
        int hours = 0, int minutes = 0, double seconds = 0)
        => PgTemporal.Call<PgInterval>(TemporalOperation.MakeInterval, SpiParameter.Create(years), SpiParameter.Create(months),
            SpiParameter.Create(weeks), SpiParameter.Create(days), SpiParameter.Create(hours), SpiParameter.Create(minutes), SpiParameter.Create(seconds));

    /// <summary>
    /// Constructs calendar years with PostgreSQL overflow checks.
    /// </summary>
    /// <param name="years">The signed number of years.</param>
    /// <returns>The interval.</returns>
    public static PgInterval FromYears(int years) => Create(years: years);
    /// <summary>
    /// Constructs calendar months without requiring backend access.
    /// </summary>
    /// <param name="months">The signed number of months.</param>
    /// <returns>The interval.</returns>
    public static PgInterval FromMonths(int months) => new(months, 0, 0);
    /// <summary>
    /// Constructs calendar weeks with PostgreSQL overflow checks.
    /// </summary>
    /// <param name="weeks">The signed number of weeks.</param>
    /// <returns>The interval.</returns>
    public static PgInterval FromWeeks(int weeks) => Create(weeks: weeks);
    /// <summary>
    /// Constructs calendar days without requiring backend access.
    /// </summary>
    /// <param name="days">The signed number of days.</param>
    /// <returns>The interval.</returns>
    public static PgInterval FromDays(int days) => new(0, days, 0);
    /// <summary>
    /// Constructs elapsed hours using PostgreSQL's make_interval rules.
    /// </summary>
    /// <param name="hours">The signed number of hours.</param>
    /// <returns>The interval.</returns>
    public static PgInterval FromHours(int hours) => Create(hours: hours);
    /// <summary>
    /// Constructs elapsed minutes using PostgreSQL's make_interval rules.
    /// </summary>
    /// <param name="minutes">The signed number of minutes.</param>
    /// <returns>The interval.</returns>
    public static PgInterval FromMinutes(int minutes) => Create(minutes: minutes);
    /// <summary>
    /// Constructs elapsed seconds using PostgreSQL's fractional rounding and range rules.
    /// </summary>
    /// <param name="seconds">The signed fractional seconds.</param>
    /// <returns>The interval.</returns>
    public static PgInterval FromSeconds(double seconds) => Create(seconds: seconds);
    /// <summary>
    /// Constructs exact elapsed microseconds without a floating-point intermediary or backend access.
    /// </summary>
    /// <param name="microseconds">The signed microseconds.</param>
    /// <returns>The interval.</returns>
    public static PgInterval FromMicroseconds(long microseconds) => new(0, 0, microseconds);

    /// <summary>
    /// Converts finite components to PostgreSQL's comparison approximation of thirty days per month.
    /// </summary>
    /// <returns>The comparison value, which is not a calendar-aware elapsed duration.</returns>
    /// <exception cref="InvalidOperationException">The interval is infinite.</exception>
    public Int128 ToComparisonMicroseconds()
    {
        if (!IsFinite)
        {
            throw new InvalidOperationException("An infinite interval has no finite comparison duration.");
        }

        return ((Int128)Months * 30 + Days) * PgTemporal.MicrosecondsPerDay + Microseconds;
    }

    /// <summary>
    /// Compares intervals using PostgreSQL's thirty-day months and twenty-four-hour days without requiring a backend.
    /// Infinity sorts outside every finite interval. Distinct stored components can compare as equal.
    /// </summary>
    /// <param name="other">The interval to compare.</param>
    /// <returns>A negative value, zero, or a positive value for a smaller, equivalent, or larger comparison duration.</returns>
    public int CompareTo(PgInterval other)
        => IsFinite && other.IsFinite
            ? ToComparisonMicroseconds().CompareTo(other.ToComparisonMicroseconds())
            : _infinity.CompareTo(other._infinity);

    /// <summary>
    /// Tests equality using PostgreSQL's interval comparison, preserving the original calendar components.
    /// </summary>
    /// <param name="other">The interval to compare.</param>
    /// <returns>Whether both intervals have the same comparison duration or the same infinity.</returns>
    public bool Equals(PgInterval other) => CompareTo(other) == 0;

    /// <summary>
    /// Hashes PostgreSQL's comparison duration so equivalent intervals have equal managed hashes.
    /// </summary>
    /// <returns>A managed hash consistent with equality, without requiring backend access.</returns>
    public override int GetHashCode()
        => IsFinite ? HashCode.Combine(0, ToComparisonMicroseconds()) : HashCode.Combine(_infinity);

    /// <summary>
    /// Tests whether the left interval sorts before the right interval using PostgreSQL's comparison approximation.
    /// </summary>
    public static bool operator <(PgInterval left, PgInterval right) => left.CompareTo(right) < 0;

    /// <summary>
    /// Tests whether the left interval sorts after the right interval using PostgreSQL's comparison approximation.
    /// </summary>
    public static bool operator >(PgInterval left, PgInterval right) => left.CompareTo(right) > 0;

    /// <summary>
    /// Tests whether the left interval sorts before or compares as equivalent to the right interval.
    /// </summary>
    public static bool operator <=(PgInterval left, PgInterval right) => left.CompareTo(right) <= 0;

    /// <summary>
    /// Tests whether the left interval sorts after or compares as equivalent to the right interval.
    /// </summary>
    public static bool operator >=(PgInterval left, PgInterval right) => left.CompareTo(right) >= 0;

    /// <summary>
    /// Gets -1, 0, or 1 by comparing with zero under PostgreSQL's thirty-day-month comparison, including infinities.
    /// Mixed-sign components contribute to one comparison duration. No backend is required.
    /// </summary>
    public int Sign => IsFinite ? ToComparisonMicroseconds().CompareTo(Int128.Zero) : _infinity;

    /// <summary>
    /// Gets the time-of-day part of the interval's time component, wrapped into one day, as PostgreSQL's
    /// <c>interval::time</c> cast does. Months and days are ignored.
    /// </summary>
    /// <returns>The time of day.</returns>
    /// <exception cref="PgException">The interval is infinite (SQLSTATE 22008).</exception>
    public PgTime ToTime()
    {
        if (!IsFinite)
        {
            throw new PgException(PgSqlStates.DatetimeValueOutOfRange, "cannot convert infinite interval to time");
        }

        long time = Microseconds % PgTemporal.MicrosecondsPerDay;
        return new PgTime(time < 0 ? time + PgTemporal.MicrosecondsPerDay : time);
    }

    /// <summary>
    /// Gets the absolute value under PostgreSQL's interval comparison, consistent with Sign, CompareTo, and equality.
    /// No backend is required.
    /// </summary>
    /// <returns>
    /// This interval with unchanged components when its Sign is zero or positive, otherwise its negation.
    /// Both infinities return positive infinity.
    /// </returns>
    /// <remarks>
    /// A negative interval negates every component together, preserving their separation and mixed signs.
    /// One month minus thirty-one days therefore becomes minus one month plus thirty-one days, which compares as one day.
    /// The result never sorts below zero, and equivalent intervals have equivalent absolute values.
    /// </remarks>
    /// <exception cref="OverflowException">A negative interval has a component equal to its signed minimum value.</exception>
    public PgInterval Abs()
    {
        if (!IsFinite)
        {
            return PositiveInfinity;
        }

        return Sign >= 0 ? this : new(checked(-Months), checked(-Days), checked(-Microseconds));
    }

    /// <summary>
    /// Adds interval components using PostgreSQL's rules.
    /// </summary>
    public static PgInterval operator +(PgInterval left, PgInterval right)
    {
        return left.Add(right);
    }

    /// <summary>
    /// Subtracts interval components using PostgreSQL's rules.
    /// </summary>
    public static PgInterval operator -(PgInterval left, PgInterval right)
    {
        return left.Subtract(right);
    }

    /// <summary>
    /// Negates an interval using PostgreSQL's rules.
    /// </summary>
    public static PgInterval operator -(PgInterval value)
    {
        return value.Negate();
    }

    /// <summary>
    /// Scales an interval using PostgreSQL's fractional-month/day rules.
    /// </summary>
    public static PgInterval operator *(PgInterval interval, double factor)
    {
        return interval.Multiply(factor);
    }

    /// <summary>
    /// Scales an interval using PostgreSQL's fractional-month/day rules.
    /// </summary>
    public static PgInterval operator *(double factor, PgInterval interval)
    {
        return interval.Multiply(factor);
    }

    /// <summary>
    /// Divides an interval using PostgreSQL's fractional-month/day rules.
    /// </summary>
    public static PgInterval operator /(PgInterval interval, double divisor)
    {
        return interval.Divide(divisor);
    }

    /// <summary>
    /// Parses PostgreSQL interval syntax on the active backend thread.
    /// </summary>
    /// <param name="text">The interval text.</param>
    /// <returns>The interval.</returns>
    public static PgInterval Parse(string text) => PgTemporal.Call<PgInterval>(TemporalOperation.Parse, PgTemporal.Text(text));

    /// <summary>
    /// Tries to parse PostgreSQL interval syntax on the active backend thread.
    /// </summary>
    /// <param name="text">The interval text.</param>
    /// <param name="value">The parsed interval, or the default value on invalid input.</param>
    /// <returns>Whether the input is valid. Backend-access and operational errors still throw.</returns>
    /// <remarks>
    /// Invalid input leaves no error pending: PostgreSQL 16 and later report it as a soft input error, and earlier majors
    /// roll back an internal subtransaction. Before PostgreSQL 16, transaction callbacks and parallel operations forbid
    /// that rollback; native errors there remain pending and throw PgException.
    /// </remarks>
    public static bool TryParse(string? text, out PgInterval value) => PgTemporal.TryParse(text, out value);

    /// <summary>
    /// Formats the interval using the session's IntervalStyle.
    /// </summary>
    /// <returns>The PostgreSQL interval text. Use ToIsoString for session-independent text.</returns>
    public string ToPostgresString() => PgTemporal.Call<string>(TemporalOperation.Format, SpiParameter.Create(this));

    /// <summary>
    /// Adds interval components using PostgreSQL's overflow and infinity rules.
    /// </summary>
    /// <param name="other">The interval to add.</param>
    /// <returns>The resulting interval.</returns>
    public PgInterval Add(PgInterval other)
        => PgTemporal.Call<PgInterval>(TemporalOperation.Add, SpiParameter.Create(this), SpiParameter.Create(other));

    /// <summary>
    /// Subtracts interval components using PostgreSQL's overflow and infinity rules.
    /// </summary>
    /// <param name="other">The interval to subtract.</param>
    /// <returns>The resulting interval.</returns>
    public PgInterval Subtract(PgInterval other)
        => PgTemporal.Call<PgInterval>(TemporalOperation.Subtract, SpiParameter.Create(this), SpiParameter.Create(other));

    /// <summary>
    /// Scales the interval with PostgreSQL's fractional-month and fractional-day rules.
    /// </summary>
    /// <param name="factor">The multiplier.</param>
    /// <returns>The scaled interval.</returns>
    public PgInterval Multiply(double factor)
        => PgTemporal.Call<PgInterval>(TemporalOperation.Multiply, SpiParameter.Create(this), SpiParameter.Create(factor));

    /// <summary>
    /// Divides the interval with PostgreSQL's fractional-month and fractional-day rules.
    /// </summary>
    /// <param name="divisor">The divisor.</param>
    /// <returns>The divided interval.</returns>
    public PgInterval Divide(double divisor)
        => PgTemporal.Call<PgInterval>(TemporalOperation.Divide, SpiParameter.Create(this), SpiParameter.Create(divisor));

    /// <summary>
    /// Negates all interval components with PostgreSQL overflow and infinity handling.
    /// </summary>
    /// <returns>The negated interval.</returns>
    public PgInterval Negate() => PgTemporal.Call<PgInterval>(TemporalOperation.Negate, SpiParameter.Create(this));

    /// <summary>
    /// Normalizes thirty-day groups into months using PostgreSQL justify_days.
    /// </summary>
    /// <returns>The normalized interval.</returns>
    public PgInterval JustifyDays() => PgTemporal.Call<PgInterval>(TemporalOperation.JustifyDays, SpiParameter.Create(this));

    /// <summary>
    /// Normalizes twenty-four-hour groups into calendar days using PostgreSQL justify_hours.
    /// </summary>
    /// <returns>The normalized interval.</returns>
    public PgInterval JustifyHours() => PgTemporal.Call<PgInterval>(TemporalOperation.JustifyHours, SpiParameter.Create(this));

    /// <summary>
    /// Normalizes months, days, hours, and mixed component signs using PostgreSQL justify_interval.
    /// </summary>
    /// <returns>The normalized interval.</returns>
    public PgInterval Justify() => PgTemporal.Call<PgInterval>(TemporalOperation.Justify, SpiParameter.Create(this));

    /// <summary>
    /// Truncates an interval to a PostgreSQL field.
    /// </summary>
    /// <param name="part">The truncation field.</param>
    /// <returns>The truncated interval.</returns>
    public PgInterval Truncate(PgDateTimePart part)
        => PgTemporal.Call<PgInterval>(TemporalOperation.Truncate, PgTemporal.Part(part), SpiParameter.Create(this));

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
    /// Compares with PostgreSQL's thirty-day-month convention, consistent with managed equality.
    /// </summary>
    /// <param name="other">The interval to compare.</param>
    /// <returns>A negative value, zero, or a positive value when this interval sorts before, equals, or sorts after the other.</returns>
    public int CompareInPostgres(PgInterval other)
        => PgTemporal.Call<int>(TemporalOperation.Compare, SpiParameter.Create(this), SpiParameter.Create(other));

    /// <summary>
    /// Converts a fixed duration to elapsed microseconds without adding calendar-day semantics.
    /// </summary>
    /// <param name="value">The elapsed duration at whole-microsecond precision.</param>
    /// <returns>An interval with zero months and days.</returns>
    /// <exception cref="ArgumentException">The duration contains sub-microsecond ticks.</exception>
    public static PgInterval FromTimeSpan(TimeSpan value) => new(0, 0, PgTemporal.ToMicroseconds(value.Ticks));

    /// <summary>
    /// Converts an interval containing only elapsed time. Calendar months or days require a reference timestamp.
    /// </summary>
    /// <returns>The fixed duration.</returns>
    /// <exception cref="InvalidOperationException">The interval is infinite or contains calendar months or days.</exception>
    /// <exception cref="OverflowException">The microseconds exceed TimeSpan's range.</exception>
    public TimeSpan ToTimeSpan()
    {
        if (!IsFinite || Months != 0 || Days != 0)
        {
            throw new InvalidOperationException("Only finite intervals without calendar months or days represent a fixed duration.");
        }

        return new TimeSpan(checked(Microseconds * TimeSpan.TicksPerMicrosecond));
    }
}
