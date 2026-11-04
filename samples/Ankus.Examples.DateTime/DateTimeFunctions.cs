namespace Ankus.Examples.DateTime;

/// <summary>
/// Ports pgrx's temporal example with native arithmetic, timezone rules and PostgreSQL clocks.
/// </summary>
public static class DateTimeFunctions
{
    /// <summary>
    /// Formats a timestamp with time zone using PostgreSQL's current session timezone.
    /// </summary>
    /// <param name="value">The timestamp to format.</param>
    /// <returns>The native ISO representation.</returns>
    [PgFunction(Name = "to_iso_string", Volatility = PgVolatility.Stable, ParallelSafety = PgParallelSafety.Safe)]
    public static string ToIsoString([PgParameter(Name = "tsz")] PgTimestampTz value) => value.ToIsoString();

    /// <summary>
    /// Formats a timestamp in an explicitly selected PostgreSQL timezone.
    /// </summary>
    /// <param name="value">The timestamp to format.</param>
    /// <param name="zone">The PostgreSQL timezone name.</param>
    /// <returns>The native ISO representation in that timezone.</returns>
    [PgFunction(Name = "to_iso_string", Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static string ToIsoString([PgParameter(Name = "tsz")] PgTimestampTz value,
        [PgParameter(Name = "tz")] string zone) => value.ToIsoString(zone);

    /// <summary>
    /// Subtracts an interval from a date and returns PostgreSQL's timestamp result.
    /// </summary>
    /// <param name="value">The date.</param>
    /// <param name="interval">The interval to subtract.</param>
    /// <returns>The native timestamp result.</returns>
    [PgFunction(Name = "subtract_interval", Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgTimestamp SubtractInterval([PgParameter(Name = "v")] PgDate value,
        [PgParameter(Name = "i")] PgInterval interval) => value - interval;

    /// <summary>
    /// Subtracts an interval from a time with PostgreSQL's midnight wrapping rules.
    /// </summary>
    /// <param name="value">The time.</param>
    /// <param name="interval">The interval to subtract.</param>
    /// <returns>The native time result.</returns>
    [PgFunction(Name = "subtract_interval", Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgTime SubtractInterval([PgParameter(Name = "v")] PgTime value,
        [PgParameter(Name = "i")] PgInterval interval) => value - interval;

    /// <summary>
    /// Subtracts an interval from a time while preserving its timezone offset.
    /// </summary>
    /// <param name="value">The time with time zone.</param>
    /// <param name="interval">The interval to subtract.</param>
    /// <returns>The native time-with-time-zone result.</returns>
    [PgFunction(Name = "subtract_interval", Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgTimeTz SubtractInterval([PgParameter(Name = "v")] PgTimeTz value,
        [PgParameter(Name = "i")] PgInterval interval) => value - interval;

    /// <summary>
    /// Subtracts an interval from a timestamp without introducing a timezone.
    /// </summary>
    /// <param name="value">The timestamp.</param>
    /// <param name="interval">The interval to subtract.</param>
    /// <returns>The native timestamp result.</returns>
    [PgFunction(Name = "subtract_interval", Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgTimestamp SubtractInterval([PgParameter(Name = "v")] PgTimestamp value,
        [PgParameter(Name = "i")] PgInterval interval) => value - interval;

    /// <summary>
    /// Subtracts an interval using PostgreSQL's timestamp-with-time-zone calendar rules.
    /// </summary>
    /// <param name="value">The timestamp with time zone.</param>
    /// <param name="interval">The interval to subtract.</param>
    /// <returns>The native timestamp-with-time-zone result.</returns>
    [PgFunction(Name = "subtract_interval", Volatility = PgVolatility.Stable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgTimestampTz SubtractInterval([PgParameter(Name = "v")] PgTimestampTz value,
        [PgParameter(Name = "i")] PgInterval interval) => value - interval;

    /// <summary>
    /// Subtracts the native month, day and microsecond interval components.
    /// </summary>
    /// <param name="value">The first interval.</param>
    /// <param name="interval">The interval to subtract.</param>
    /// <returns>The native interval result.</returns>
    [PgFunction(Name = "subtract_interval", Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgInterval SubtractInterval([PgParameter(Name = "v")] PgInterval value,
        [PgParameter(Name = "i")] PgInterval interval) => value - interval;

    /// <summary>
    /// Adds an interval to a date and returns PostgreSQL's timestamp result.
    /// </summary>
    /// <param name="value">The date.</param>
    /// <param name="interval">The interval to add.</param>
    /// <returns>The native timestamp result.</returns>
    [PgFunction(Name = "add_interval", Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgTimestamp AddInterval([PgParameter(Name = "v")] PgDate value,
        [PgParameter(Name = "i")] PgInterval interval) => value + interval;

    /// <summary>
    /// Adds an interval to a time with PostgreSQL's midnight wrapping rules.
    /// </summary>
    /// <param name="value">The time.</param>
    /// <param name="interval">The interval to add.</param>
    /// <returns>The native time result.</returns>
    [PgFunction(Name = "add_interval", Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgTime AddInterval([PgParameter(Name = "v")] PgTime value,
        [PgParameter(Name = "i")] PgInterval interval) => value + interval;

    /// <summary>
    /// Adds an interval to a time while preserving its timezone offset.
    /// </summary>
    /// <param name="value">The time with time zone.</param>
    /// <param name="interval">The interval to add.</param>
    /// <returns>The native time-with-time-zone result.</returns>
    [PgFunction(Name = "add_interval", Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgTimeTz AddInterval([PgParameter(Name = "v")] PgTimeTz value,
        [PgParameter(Name = "i")] PgInterval interval) => value + interval;

    /// <summary>
    /// Adds an interval to a timestamp without introducing a timezone.
    /// </summary>
    /// <param name="value">The timestamp.</param>
    /// <param name="interval">The interval to add.</param>
    /// <returns>The native timestamp result.</returns>
    [PgFunction(Name = "add_interval", Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgTimestamp AddInterval([PgParameter(Name = "v")] PgTimestamp value,
        [PgParameter(Name = "i")] PgInterval interval) => value + interval;

    /// <summary>
    /// Adds an interval using PostgreSQL's timestamp-with-time-zone calendar rules.
    /// </summary>
    /// <param name="value">The timestamp with time zone.</param>
    /// <param name="interval">The interval to add.</param>
    /// <returns>The native timestamp-with-time-zone result.</returns>
    [PgFunction(Name = "add_interval", Volatility = PgVolatility.Stable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgTimestampTz AddInterval([PgParameter(Name = "v")] PgTimestampTz value,
        [PgParameter(Name = "i")] PgInterval interval) => value + interval;

    /// <summary>
    /// Adds the native month, day and microsecond interval components.
    /// </summary>
    /// <param name="value">The first interval.</param>
    /// <param name="interval">The interval to add.</param>
    /// <returns>The native interval result.</returns>
    [PgFunction(Name = "add_interval", Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgInterval AddInterval([PgParameter(Name = "v")] PgInterval value,
        [PgParameter(Name = "i")] PgInterval interval) => value + interval;

    /// <summary>
    /// Scales an interval using PostgreSQL's component rounding rules.
    /// </summary>
    /// <param name="value">The interval.</param>
    /// <param name="factor">The native double-precision multiplier.</param>
    /// <returns>The native interval result.</returns>
    [PgFunction(Name = "mul_interval", Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgInterval MultiplyInterval([PgParameter(Name = "i")] PgInterval value,
        [PgParameter(Name = "v")] double factor) => value * factor;

    /// <summary>
    /// Divides an interval using PostgreSQL's component rounding rules.
    /// </summary>
    /// <param name="value">The interval.</param>
    /// <param name="divisor">The native double-precision divisor.</param>
    /// <returns>The native interval result.</returns>
    [PgFunction(Name = "div_interval", Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgInterval DivideInterval([PgParameter(Name = "i")] PgInterval value,
        [PgParameter(Name = "v")] double divisor) => value / divisor;

    /// <summary>
    /// Samples the upstream example's hour range 0–22 and minute/second ranges 0–58.
    /// </summary>
    /// <returns>An exact whole-second time.</returns>
    [PgFunction(ParallelSafety = PgParallelSafety.Safe)]
    public static PgTime RandomTime()
    {
        var value = new TimeOnly(Random.Shared.Next(0, 23),
            Random.Shared.Next(0, 59), Random.Shared.Next(0, 59));
        return PgTime.FromTimeOnly(value);
    }

    /// <summary>
    /// Samples the same valid-date distribution as pgrx's upper-exclusive ranges and rejected invalid days.
    /// </summary>
    /// <returns>A valid 1978–2022 date in January–November with day at most 30.</returns>
    [PgFunction(ParallelSafety = PgParallelSafety.Safe)]
    public static PgDate RandomDate()
    {
        int year = Random.Shared.Next(1978, 2023);
        int month = Random.Shared.Next(1, 12);
        int lastDay = Math.Min(30, System.DateTime.DaysInMonth(year, month));
        var value = new DateOnly(year, month, Random.Shared.Next(1, lastDay + 1));
        return PgDate.FromDateOnly(value);
    }

    /// <summary>
    /// Combines a date and time using PostgreSQL's native timestamp operation.
    /// </summary>
    /// <param name="date">The native date.</param>
    /// <param name="time">The native time.</param>
    /// <returns>The native timestamp, including date infinities and end-of-day rollover.</returns>
    [PgFunction(Volatility = PgVolatility.Immutable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgTimestamp ComposeTimestamp([PgParameter(Name = "d")] PgDate date,
        [PgParameter(Name = "t")] PgTime time) => date + time;

    /// <summary>
    /// Interprets a timestamp in the current session timezone and projects it into a requested timezone.
    /// </summary>
    /// <param name="value">The timestamp interpreted in the current session timezone.</param>
    /// <param name="zone">The destination PostgreSQL timezone name.</param>
    /// <returns>The destination local timestamp.</returns>
    [PgFunction(Name = "set_timezone", Volatility = PgVolatility.Stable, ParallelSafety = PgParallelSafety.Safe)]
    public static PgTimestamp SetTimeZone([PgParameter(Name = "ts")] PgTimestamp value,
        [PgParameter(Name = "timezone")] string zone) => value.ToTimestampTz().AtTimeZone(zone);

    /// <summary>
    /// Samples all four PostgreSQL clocks eagerly and returns exactly one named row.
    /// </summary>
    /// <returns>The now alias, transaction start, statement start and current wall-clock timestamps.</returns>
    [PgFunction]
    public static IEnumerable<(PgTimestampTz Now, PgTimestampTz TransactionTimestamp,
        PgTimestampTz StatementTimestamp, PgTimestampTz ClockTimestamp)> AllTimes()
        => [(PgTimestampTz.TransactionTimestamp, PgTimestampTz.TransactionTimestamp,
            PgTimestampTz.StatementTimestamp, PgTimestampTz.ClockTimestamp)];
}
