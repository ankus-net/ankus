using System;

namespace Ankus;

/// <summary>
/// Converts owned temporal cells between full-range PostgreSQL values and the corresponding .NET types.
/// </summary>
internal static class SpiTemporal
{
    /// <summary>
    /// Applies the matching PostgreSQL/.NET temporal adapter, rejecting range, precision, and calendar-component loss.
    /// </summary>
    /// <param name="value">The non-null source temporal value.</param>
    /// <param name="target">The requested temporal type, optionally nullable.</param>
    /// <returns>The converted value.</returns>
    internal static object Convert(object value, Type target)
    {
        return value switch
        {
            PgDate date when target == typeof(DateOnly) || target == typeof(DateOnly?) => date.ToDateOnly(),
            PgTime time when target == typeof(TimeOnly) || target == typeof(TimeOnly?) => time.ToTimeOnly(),
            PgTimestamp stamp when target == typeof(DateTime) || target == typeof(DateTime?) => stamp.ToDateTime(),
            PgTimestampTz stamp when target == typeof(DateTimeOffset) || target == typeof(DateTimeOffset?) => stamp.ToDateTimeOffset(),
            PgInterval span when target == typeof(TimeSpan) || target == typeof(TimeSpan?) => span.ToTimeSpan(),
            DateOnly date when target == typeof(PgDate) || target == typeof(PgDate?) => PgDate.FromDateOnly(date),
            TimeOnly time when target == typeof(PgTime) || target == typeof(PgTime?) => PgTime.FromTimeOnly(time),
            DateTime stamp when target == typeof(PgTimestamp) || target == typeof(PgTimestamp?) => PgTimestamp.FromDateTime(stamp),
            DateTimeOffset stamp when target == typeof(PgTimestampTz) || target == typeof(PgTimestampTz?) =>
                PgTimestampTz.FromDateTimeOffset(stamp),
            TimeSpan span when target == typeof(PgInterval) || target == typeof(PgInterval?) => PgInterval.FromTimeSpan(span),
            _ => throw new InvalidCastException($"An SPI value of type '{value.GetType()}' cannot be read as '{target}'."),
        };
    }
}
