namespace Ankus.TestExtension;

/// <summary>
/// Exposes temporal fields, timezone conveniences and owned clock output to independent SQL oracles.
/// </summary>
public static class TemporalParityFunctions
{
    /// <summary>
    /// Reads detached date fields, tuple fields and epoch conversions.
    /// </summary>
    /// <param name="value">The finite PostgreSQL date.</param>
    /// <returns>The exact integer components.</returns>
    [PgFunction]
    public static long[] ParityDateFields(PgDate value)
    {
        (int year, int month, int day) = value.GetDateParts();
        return [value.Year, value.Month, value.Day, year, month, day,
            value.ToJulianDays(), value.ToUnixEpochDays(), value.ToUnixTimeSeconds()];
    }

    /// <summary>
    /// Reads detached timestamp fields and their tuple counterparts.
    /// </summary>
    /// <param name="value">The finite timestamp.</param>
    /// <returns>The exact date/time fields.</returns>
    [PgFunction]
    public static long[] ParityTimestampFields(PgTimestamp value)
    {
        (int year, int month, int day) = value.GetDateParts();
        (int hour, int minute, int second, int microseconds) = value.GetTimeParts();
        return [value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.MicrosecondsWithinSecond,
            year, month, day, hour, minute, second, microseconds];
    }

    /// <summary>
    /// Reads an instant's local fields without narrowing it to the finite timestamp range.
    /// </summary>
    /// <param name="value">The finite instant.</param>
    /// <returns>The session-zone date/time fields.</returns>
    [PgFunction]
    public static long[] ParityInstantFields(PgTimestampTz value)
    {
        (int year, int month, int day) = value.GetDateParts();
        (int hour, int minute, int second, int microseconds) = value.GetTimeParts();
        return [value.Year, value.Month, value.Day, value.Hour, value.Minute, value.Second, value.MicrosecondsWithinSecond,
            year, month, day, hour, minute, second, microseconds];
    }

    /// <summary>
    /// Reads wall-clock fields and tuple components, including the distinct end of day.
    /// </summary>
    /// <param name="value">The time.</param>
    /// <returns>The exact fields.</returns>
    [PgFunction]
    public static long[] ParityTimeFields(PgTime value)
    {
        (int hour, int minute, int second, int microseconds) = value.GetTimeParts();
        return [value.Hour, value.Minute, value.Second, value.MicrosecondsWithinSecond, hour, minute, second, microseconds];
    }

    /// <summary>
    /// Reads wall-clock and signed second-resolution timezone fields.
    /// </summary>
    /// <param name="value">The time with offset.</param>
    /// <returns>The exact fields and offset components.</returns>
    [PgFunction]
    public static long[] ParityTimeZoneFields(PgTimeTz value)
    {
        (int hour, int minute, int second, int microseconds) = value.GetTimeParts();
        return [value.Hour, value.Minute, value.Second, value.MicrosecondsWithinSecond, hour, minute, second, microseconds,
            value.OffsetHours, value.OffsetMinutes, value.OffsetSeconds];
    }

    /// <summary>
    /// Reads seconds including the fractional component from each supported field surface.
    /// </summary>
    /// <param name="type">The PostgreSQL type.</param>
    /// <param name="value">Its PostgreSQL input representation.</param>
    /// <returns>Seconds including its microsecond fraction.</returns>
    [PgFunction]
    public static double ParityFractionalSecond(string type, string value) => type switch
    {
        "time" => PgTime.Parse(value).FractionalSecond,
        "timetz" => PgTimeTz.Parse(value).FractionalSecond,
        "timestamp" => PgTimestamp.Parse(value).FractionalSecond,
        "timestamptz" => PgTimestampTz.Parse(value).FractionalSecond,
        _ => throw new ArgumentException("Unknown temporal type.", nameof(type)),
    };

    /// <summary>
    /// Resolves an explicit zone without altering session timezone settings.
    /// </summary>
    /// <param name="zone">A named, abbreviated or POSIX timezone.</param>
    /// <param name="instant">An instant, or SQL NULL to select transaction start.</param>
    /// <returns>The signed offset east of UTC in seconds.</returns>
    [PgFunction]
    public static long ParityZoneOffset(string zone, PgTimestampTz? instant)
        => (instant.HasValue ? PgTimeZone.GetOffset(zone, instant.Value) : PgTimeZone.GetOffset(zone)).Ticks / TimeSpan.TicksPerSecond;

    /// <summary>
    /// Attaches a named zone to the requested local clock without rotating the clock.
    /// </summary>
    /// <param name="hour">The local hour.</param>
    /// <param name="minute">The local minute.</param>
    /// <param name="second">Seconds including their fraction.</param>
    /// <param name="zone">The timezone resolved at transaction start.</param>
    /// <returns>The unchanged clock with the requested zone offset.</returns>
    [PgFunction]
    public static PgTimeTz ParityNamedTime(int hour, int minute, double second, string zone)
        => PgTimeTz.Create(hour, minute, second, zone);

    /// <summary>
    /// Copies the UTC wall-clock timestamp without any session-zone rotation.
    /// </summary>
    /// <param name="value">An instant, including either infinity.</param>
    /// <returns>The raw UTC timestamp.</returns>
    [PgFunction]
    public static PgTimestamp ParityUtcInstant(PgTimestampTz value) => value.ToUtc();

    /// <summary>
    /// Rotates an offset time to normalized UTC wall time.
    /// </summary>
    /// <param name="value">The local time and fixed offset.</param>
    /// <returns>The UTC time.</returns>
    [PgFunction]
    public static PgTime ParityUtcTime(PgTimeTz value) => value.ToUtc();

    /// <summary>
    /// Exercises saturating and modular raw factories before native datum output.
    /// </summary>
    /// <param name="type">The target PostgreSQL type.</param>
    /// <param name="raw">The raw date days or temporal microseconds.</param>
    /// <param name="secondsWest">PostgreSQL's signed raw offset west of UTC.</param>
    /// <returns>The constructed temporal value's PostgreSQL representation.</returns>
    [PgFunction]
    public static string ParityRawTemporal(string type, long raw, int secondsWest) => type switch
    {
        "date" => PgDate.FromRawSaturating(checked((int)raw)).ToPostgresString(),
        "timestamp" => PgTimestamp.FromRawSaturating(raw).ToPostgresString(),
        "timestamptz" => PgTimestampTz.FromRawSaturating(raw).ToPostgresString(),
        "time" => PgTime.FromMicrosecondsWrapping(raw).ToPostgresString(),
        "timetz" => PgTimeTz.FromRawWrapping(raw, secondsWest).ToPostgresString(),
        _ => throw new ArgumentException("Unknown temporal type.", nameof(type)),
    };

    /// <summary>
    /// Calls the native interval timezone overload selected by the PostgreSQL type.
    /// </summary>
    /// <param name="type">The timestamp, timestamptz or timetz input type.</param>
    /// <param name="value">The PostgreSQL temporal input.</param>
    /// <param name="offset">The interval offset.</param>
    /// <returns>The native result text.</returns>
    [PgFunction]
    public static string ParityIntervalZone(string type, string value, PgInterval offset) => type switch
    {
        "timestamp" => PgTimestamp.Parse(value).AtTimeZone(offset).ToPostgresString(),
        "timestamptz" => PgTimestampTz.Parse(value).AtTimeZone(offset).ToPostgresString(),
        "timetz" => PgTimeTz.Parse(value).AtTimeZone(offset).ToPostgresString(),
        _ => throw new ArgumentException("Unknown temporal type.", nameof(type)),
    };

    /// <summary>
    /// Retains a timeofday string across native buffer reuse, SPI allocation and managed collection.
    /// </summary>
    /// <returns>The original owned server clock text.</returns>
    [PgFunction]
    public static string ParityOwnedTimeOfDay()
    {
        string owned = PgTimestampTz.TimeOfDay;
        for (int index = 0; index < 16; index++)
        {
            _ = PgTimestampTz.TimeOfDay;
            _ = Spi.ExecuteScalar<string>("SELECT repeat('temporal ownership', 4096)");
        }

        GarbageCollection.Collect();
        return owned;
    }

    /// <summary>
    /// Repeats native errors with alternating successful calls and verifies managed/native recovery state.
    /// </summary>
    /// <param name="operation">The failing timezone operation.</param>
    /// <returns>SQLSTATE, failure/finally/success counts, context growth, surviving writes and unchanged settings.</returns>
    [PgFunction]
    public static string ParityTemporalRecovery(string operation) => Spi.Connect(session =>
    {
        session.Execute("CREATE TEMP TABLE parity_temporal_writes(value integer)");
        session.Execute("INSERT INTO parity_temporal_writes VALUES (1)");
        using SpiPreparedStatement plan = session.Prepare("SELECT count(*) FROM parity_temporal_writes");
        string settings = session.ExecuteScalar<string>("SELECT current_setting('TimeZone') || '|' || current_setting('DateStyle')");
        const string contexts = "SELECT count(*) FROM ankus_test_memory.contexts WHERE name IN ('Ankus SPI operation', 'Ankus error diagnostics', 'CurTransactionContext')";
        long before = session.ExecuteScalar<long>(contexts);
        int failures = 0;
        int finalized = 0;
        int successes = 0;
        string code = "no error";
        for (int index = 0; index < 50; index++)
        {
            try
            {
                PgTransaction.RunInSubtransaction(() =>
                {
                    switch (operation)
                    {
                        case "zone":
                            _ = PgTimeZone.GetOffset("Unknown/ParityZone");
                            break;
                        case "zone-empty":
                            _ = PgTimeZone.GetOffset("");
                            break;
                        case "zone-space":
                            _ = PgTimeZone.GetOffset(" ");
                            break;
                        case "zone-nul":
                            _ = PgTimeZone.GetOffset("UT\0C");
                            break;
                        case "zone-encoding":
                            _ = PgTimeZone.GetOffset("\uD800");
                            break;
                        case "zone-instant":
                            _ = PgTimeZone.GetOffset("Unknown/ParityZone", new PgTimestampTz(0));
                            break;
                        case "zone-positive-infinity":
                            _ = PgTimeZone.GetOffset("UTC", PgTimestampTz.PositiveInfinity);
                            break;
                        case "zone-negative-infinity":
                            _ = PgTimeZone.GetOffset("UTC", PgTimestampTz.NegativeInfinity);
                            break;
                        case "named-time":
                            _ = PgTimeTz.Create(12, 34, 56.123456, "Unknown/ParityZone");
                            break;
                        case "named-time-offset":
                            _ = PgTimeTz.Create(12, 34, 56.123456, "UTC+20");
                            break;
                        case "timestamp-month":
                            _ = new PgTimestamp(0).AtTimeZone(PgInterval.FromMonths(1));
                            break;
                        case "timestamp-day":
                            _ = new PgTimestamp(0).AtTimeZone(PgInterval.FromDays(1));
                            break;
                        case "timestamp-infinite":
                            _ = new PgTimestamp(0).AtTimeZone(PgInterval.PositiveInfinity);
                            break;
                        case "instant-month":
                            _ = new PgTimestampTz(0).AtTimeZone(PgInterval.FromMonths(1));
                            break;
                        case "instant-day":
                            _ = new PgTimestampTz(0).AtTimeZone(PgInterval.FromDays(1));
                            break;
                        case "instant-infinite":
                            _ = new PgTimestampTz(0).AtTimeZone(PgInterval.NegativeInfinity);
                            break;
                        case "time-month":
                            _ = new PgTimeTz(default, 0).AtTimeZone(PgInterval.FromMonths(1));
                            break;
                        case "time-day":
                            _ = new PgTimeTz(default, 0).AtTimeZone(PgInterval.FromDays(1));
                            break;
                        case "time-infinite":
                            _ = new PgTimeTz(default, 0).AtTimeZone(PgInterval.PositiveInfinity);
                            break;
                        case "time-offset":
                            _ = new PgTimeTz(default, 0).AtTimeZone(PgInterval.FromHours(16));
                            break;
                        case "time-offset-negative":
                            _ = new PgTimeTz(default, 0).AtTimeZone(PgInterval.FromHours(-16));
                            break;
                        case "timestamp-range":
                            _ = PgTimestamp.Parse("294276-12-31 23:59:59.999999").AtTimeZone(PgInterval.FromHours(-1));
                            break;
                        case "instant-range":
                            _ = PgTimestampTz.Parse("294276-12-31 23:59:59.999999+00").AtTimeZone(PgInterval.FromHours(1));
                            break;
                        default:
                            throw new ArgumentException("Unknown temporal operation.", nameof(operation));
                    }
                });
            }
            catch (PgException error)
            {
                code = error.SqlState;
                failures++;
            }
            catch (ArgumentOutOfRangeException)
            {
                code = nameof(ArgumentOutOfRangeException);
                failures++;
            }
            catch (System.Text.EncoderFallbackException)
            {
                code = nameof(System.Text.EncoderFallbackException);
                failures++;
            }
            catch (ArgumentException)
            {
                code = nameof(ArgumentException);
                failures++;
            }
            finally
            {
                finalized++;
            }

            if (PgTimeZone.GetOffset("UTC", new PgTimestampTz(0)) == TimeSpan.Zero &&
                new PgTimestamp(0).AtTimeZone(PgInterval.FromHours(1)).MicrosecondsSinceEpoch == -3_600_000_000L &&
                plan.ExecuteScalar<long>() == 1)
            {
                successes++;
            }
        }

        session.Execute("INSERT INTO parity_temporal_writes VALUES (2)");
        bool unchanged = settings == session.ExecuteScalar<string>("SELECT current_setting('TimeZone') || '|' || current_setting('DateStyle')");
        return $"{code}:{failures}:{finalized}:{successes}:" + (session.ExecuteScalar<long>(contexts) - before) + ":" +
            plan.ExecuteScalar<long>() + ":" + unchanged;
    });
}
