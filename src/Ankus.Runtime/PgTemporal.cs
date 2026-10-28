using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Shares exact PostgreSQL epoch, range, and precision checks among temporal values.
/// </summary>
internal static class PgTemporal
{
    /// <summary>
    /// Calls a PostgreSQL temporal routine on the active backend thread.
    /// </summary>
    internal static T Call<T>(TemporalOperation operation, params ReadOnlySpan<SpiParameter> parameters)
        => NativeBackend.Temporal<T>(operation, parameters);

    /// <summary>
    /// Validates a fractional-second precision before passing it as a native type modifier.
    /// </summary>
    internal static SpiParameter Precision(int precision)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(precision);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(precision, 6);
        return SpiParameter.Create(precision);
    }

    /// <summary>
    /// Parses temporal text, catching input data errors while preserving backend-access and operational failures.
    /// </summary>
    internal static bool TryParse<T>(string? text, out T value) where T : struct
    {
        value = default;
        if (text is null || text.Contains('\0', StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            value = NativeInputRecovery.Run(() => Call<T>(TemporalOperation.Parse, Text(text)));
            return true;
        }
        catch (PgException error) when ((error.NativeFlags & NativeErrorFlags.Unrecovered) == 0 && (error.SqlState is "22007" or "22008" or "22009" or "22015" or "22023"))
        {
            return false;
        }
        catch (System.Text.EncoderFallbackException)
        {
            return false;
        }
    }

    /// <summary>
    /// Validates text that will be passed to a PostgreSQL input or timezone routine.
    /// </summary>
    internal static SpiParameter Text(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (value.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("PostgreSQL temporal text cannot contain a zero character.", nameof(value));
        }

        return SpiParameter.Create(value);
    }

    /// <summary>
    /// Maps public field names to PostgreSQL's spellings without culture-sensitive enum formatting.
    /// </summary>
    internal static SpiParameter Part(PgDateTimePart part) => SpiParameter.Create(PartName(part));

    /// <summary>
    /// Gets PostgreSQL's spelling of a field name.
    /// </summary>
    internal static string PartName(PgDateTimePart part) => part switch
    {
        PgDateTimePart.Century => "century",
        PgDateTimePart.Day => "day",
        PgDateTimePart.Decade => "decade",
        PgDateTimePart.DayOfWeek => "dow",
        PgDateTimePart.DayOfYear => "doy",
        PgDateTimePart.Epoch => "epoch",
        PgDateTimePart.Hour => "hour",
        PgDateTimePart.IsoDayOfWeek => "isodow",
        PgDateTimePart.IsoYear => "isoyear",
        PgDateTimePart.Julian => "julian",
        PgDateTimePart.Microseconds => "microseconds",
        PgDateTimePart.Millennium => "millennium",
        PgDateTimePart.Milliseconds => "milliseconds",
        PgDateTimePart.Minute => "minute",
        PgDateTimePart.Month => "month",
        PgDateTimePart.Quarter => "quarter",
        PgDateTimePart.Second => "second",
        PgDateTimePart.TimeZone => "timezone",
        PgDateTimePart.TimeZoneHour => "timezone_hour",
        PgDateTimePart.TimeZoneMinute => "timezone_minute",
        PgDateTimePart.Week => "week",
        PgDateTimePart.Year => "year",
        _ => throw new ArgumentOutOfRangeException(nameof(part)),
    };

    /// <summary>
    /// Contains the .NET tick count at PostgreSQL's epoch, 2000-01-01 midnight.
    /// </summary>
    internal const long EpochTicks = 630_822_816_000_000_000;

    /// <summary>
    /// Contains the DateOnly day number at PostgreSQL's epoch.
    /// </summary>
    internal const int EpochDayNumber = 730_119;

    /// <summary>
    /// Contains the Julian day number at PostgreSQL's epoch.
    /// </summary>
    internal const int EpochJulianDay = 2_451_545;

    /// <summary>
    /// Contains the number of days from the Unix epoch to PostgreSQL's epoch.
    /// </summary>
    internal const int UnixEpochDays = 10_957;

    /// <summary>
    /// Contains the inclusive finite date minimum in days since PostgreSQL's epoch.
    /// </summary>
    internal const int MinDate = -2_451_545;

    /// <summary>
    /// Contains the exclusive finite date upper bound in days since PostgreSQL's epoch.
    /// </summary>
    internal const int EndDate = 2_145_031_949;

    /// <summary>
    /// Contains the microseconds in one 24-hour day, independent of timezone transitions.
    /// </summary>
    internal const long MicrosecondsPerDay = 86_400_000_000;

    /// <summary>
    /// Contains the inclusive finite timestamp minimum in microseconds since PostgreSQL's epoch.
    /// </summary>
    internal const long MinTimestamp = -211_813_488_000_000_000;

    /// <summary>
    /// Contains the exclusive finite timestamp upper bound in microseconds since PostgreSQL's epoch.
    /// </summary>
    internal const long EndTimestamp = 9_223_371_331_200_000_000;

    /// <summary>
    /// Rejects infinite values when a finite calendar field or epoch conversion is required.
    /// </summary>
    internal static void RequireFinite(bool isFinite)
    {
        if (!isFinite)
        {
            throw new InvalidOperationException("Infinite temporal values do not have finite calendar fields or epoch offsets.");
        }
    }

    /// <summary>
    /// Converts a finite PostgreSQL day offset with PostgreSQL's proleptic Gregorian Julian-day algorithm.
    /// </summary>
    internal static (int Year, int Month, int Day) GetDateParts(int daysSinceEpoch)
    {
        // Use Int64 intermediates because PostgreSQL's unsigned j2date arithmetic exceeds Int32 near the date limit.
        long julian = (long)daysSinceEpoch + EpochJulianDay + 32_044;
        long quad = julian / 146_097;
        long extra = (julian - quad * 146_097) * 4 + 3;
        julian += 60 + quad * 3 + extra / 146_097;
        quad = julian / 1_461;
        julian -= quad * 1_461;
        long year = julian * 4 / 1_461;
        julian = (year != 0 ? (julian + 305) % 365 : (julian + 306) % 366) + 123;
        year += quad * 4 - 4_800;
        quad = julian * 2_141 / 65_536;
        int day = (int)(julian - 7_834 * quad / 256);
        int month = (int)((quad + 10) % 12 + 1);
        return ((int)(year <= 0 ? year - 1 : year), month, day);
    }

    /// <summary>
    /// Splits nonnegative microseconds since midnight, retaining the distinct 24:00 time value.
    /// </summary>
    internal static (int Hour, int Minute, int Second, int Microseconds) GetTimeParts(long microseconds)
        => ((int)(microseconds / 3_600_000_000), (int)(microseconds / 60_000_000 % 60),
            (int)(microseconds / 1_000_000 % 60), (int)(microseconds % 1_000_000));

    /// <summary>
    /// Returns a nonnegative time-of-day remainder for any signed microsecond count.
    /// </summary>
    internal static long WrapTime(long microseconds)
    {
        long remainder = microseconds % MicrosecondsPerDay;
        return remainder < 0 ? remainder + MicrosecondsPerDay : remainder;
    }

    /// <summary>
    /// Gets the containing calendar day using floor division for negative timestamps.
    /// </summary>
    internal static int GetTimestampDays(long microseconds)
    {
        long days = microseconds / MicrosecondsPerDay;
        return (int)(microseconds % MicrosecondsPerDay < 0 ? days - 1 : days);
    }

    /// <summary>
    /// Clamps invalid timestamp encodings to PostgreSQL's corresponding infinity sentinel.
    /// </summary>
    internal static long SaturateTimestamp(long microseconds)
        => microseconds < MinTimestamp ? long.MinValue : microseconds >= EndTimestamp ? long.MaxValue : microseconds;

    /// <summary>
    /// Rejects finite timestamp values outside PostgreSQL's supported range.
    /// </summary>
    internal static void ValidateTimestamp(long value)
    {
        if (value is not (long.MinValue or long.MaxValue) and (< MinTimestamp or >= EndTimestamp))
        {
            throw new ArgumentOutOfRangeException(nameof(value), "The value is outside PostgreSQL's timestamp range.");
        }
    }

    /// <summary>
    /// Converts ticks without silently truncating sub-microsecond precision.
    /// </summary>
    internal static long ToMicroseconds(long ticks)
    {
        if (ticks % TimeSpan.TicksPerMicrosecond != 0)
        {
            throw new ArgumentException("PostgreSQL temporal values require whole microseconds.", nameof(ticks));
        }

        return ticks / TimeSpan.TicksPerMicrosecond;
    }

    /// <summary>
    /// Converts a PostgreSQL timestamp to a representable .NET date without substituting infinity sentinels.
    /// </summary>
    internal static DateTime ToDateTime(long microseconds, DateTimeKind kind)
    {
        Int128 ticks = (Int128)microseconds * TimeSpan.TicksPerMicrosecond + EpochTicks;
        if (ticks < DateTime.MinValue.Ticks || ticks > DateTime.MaxValue.Ticks)
        {
            throw new InvalidOperationException("This PostgreSQL timestamp cannot be represented by DateTime.");
        }

        return new DateTime((long)ticks, kind);
    }
}
