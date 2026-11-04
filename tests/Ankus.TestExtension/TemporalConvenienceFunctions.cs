using System.Globalization;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises temporal construction, operators, precision and explicit timezone formatting.
/// </summary>
public static class TemporalConvenienceFunctions
{
    /// <summary>
    /// Constructs timestamp or timetz fields inside a generated callback.
    /// </summary>
    /// <param name="type">The target temporal type.</param>
    /// <param name="year">The year, or fixed offset seconds for timetz.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day.</param>
    /// <param name="hour">The hour.</param>
    /// <param name="minute">The minute.</param>
    /// <param name="second">The fractional seconds.</param>
    /// <param name="zone">An optional explicit timezone.</param>
    /// <returns>The PostgreSQL text.</returns>
    [PgFunction]
    public static string TemporalFactory(string type, int year, int month, int day, int hour, int minute, double second, string? zone)
        => type switch
        {
            "timestamp" => PgTimestamp.Create(year, month, day, hour, minute, second).ToPostgresString(),
            "timestamptz" => (zone is null ? PgTimestampTz.Create(year, month, day, hour, minute, second)
                : PgTimestampTz.Create(year, month, day, hour, minute, second, zone)).ToPostgresString(),
            "timetz" => PgTimeTz.Create(hour, minute, second).ToPostgresString(),
            "offset" => PgTimeTz.Create(hour, minute, second, year).ToPostgresString(),
            _ => throw new ArgumentException("Unknown type.", nameof(type)),
        };

    /// <summary>
    /// Constructs a seven-component interval through the scalar dispatcher.
    /// </summary>
    /// <param name="years">Years.</param>
    /// <param name="months">Months.</param>
    /// <param name="weeks">Weeks.</param>
    /// <param name="days">Days.</param>
    /// <param name="hours">Hours.</param>
    /// <param name="minutes">Minutes.</param>
    /// <param name="seconds">Seconds.</param>
    /// <returns>The constructed interval.</returns>
    [PgFunction]
    public static PgInterval IntervalFactory(int years, int months, int weeks, int days, int hours, int minutes, double seconds)
        => PgInterval.Create(years, months, weeks, days, hours, minutes, seconds);

    /// <summary>
    /// Repeats failed interval construction while observing managed unwinding, native cleanup, and surviving SQL state.
    /// </summary>
    /// <param name="years">Years.</param>
    /// <param name="months">Months.</param>
    /// <param name="weeks">Weeks.</param>
    /// <param name="days">Days.</param>
    /// <param name="hours">Hours.</param>
    /// <param name="minutes">Minutes.</param>
    /// <param name="seconds">Seconds.</param>
    /// <returns>Exact diagnostics, unwind counts, retained state, and a subsequent valid interval.</returns>
    [PgFunction]
    public static string IntervalFactoryRecovery(int years, int months, int weeks, int days, int hours, int minutes, double seconds)
        => Spi.Connect(session =>
        {
            session.Execute("CREATE TEMP TABLE interval_factory_writes(value int)");
            session.Execute("INSERT INTO interval_factory_writes VALUES (1)");
            using SpiPreparedStatement plan = session.Prepare("SELECT count(*) FROM interval_factory_writes");
            const string count = "SELECT count(*) FROM ankus_test_memory.contexts WHERE name IN ('Ankus SPI operation', 'Ankus error diagnostics', 'CurTransactionContext')";
            long before = session.ExecuteScalar<long>(count);
            int failures = 0;
            int finalized = 0;
            bool consistent = true;
            string diagnostic = "no error";
            for (int index = 0; index < 50; index++)
            {
                try
                {
                    PgTransaction.RunInSubtransaction(() =>
                    {
                        _ = PgInterval.Create(years, months, weeks, days, hours, minutes, seconds);
                    });
                }
                catch (PgException error)
                {
                    string next = error.SqlState + "|" + error.Message;
                    consistent &= failures == 0 || diagnostic == next;
                    diagnostic = next;
                    failures++;
                }
                finally
                {
                    finalized++;
                }
            }

            PgInterval control = PgInterval.Create(1, -2, 3, -4, 5, -6, 7.1234567);
            session.Execute("INSERT INTO interval_factory_writes VALUES (2)");
            return $"{diagnostic}|{failures}|{finalized}|{consistent}|{session.ExecuteScalar<long>(count) - before}|" +
                $"{plan.ExecuteScalar<long>()}|{control.Months},{control.Days},{control.Microseconds}";
        });

    /// <summary>
    /// Constructs unit intervals and exercises checked component absolute values.
    /// </summary>
    /// <param name="unit">The constructor or operation.</param>
    /// <param name="value">The amount or interval text.</param>
    /// <returns>The interval.</returns>
    [PgFunction]
    public static PgInterval IntervalUnit(string unit, string value) => unit switch
    {
        "years" => PgInterval.FromYears(int.Parse(value, CultureInfo.InvariantCulture)),
        "months" => PgInterval.FromMonths(int.Parse(value, CultureInfo.InvariantCulture)),
        "weeks" => PgInterval.FromWeeks(int.Parse(value, CultureInfo.InvariantCulture)),
        "days" => PgInterval.FromDays(int.Parse(value, CultureInfo.InvariantCulture)),
        "hours" => PgInterval.FromHours(int.Parse(value, CultureInfo.InvariantCulture)),
        "minutes" => PgInterval.FromMinutes(int.Parse(value, CultureInfo.InvariantCulture)),
        "seconds" => PgInterval.FromSeconds(double.Parse(value, CultureInfo.InvariantCulture)),
        "microseconds" => PgInterval.FromMicroseconds(long.Parse(value, CultureInfo.InvariantCulture)),
        "abs" => PgInterval.Parse(value).Abs(),
        _ => throw new ArgumentException("Unknown unit.", nameof(unit)),
    };

    /// <summary>
    /// Compares the interval sign convention to PostgreSQL.
    /// </summary>
    /// <param name="value">The interval.</param>
    /// <returns>The comparison sign.</returns>
    [PgFunction]
    public static int IntervalSign(PgInterval value) => value.Sign;

    /// <summary>
    /// Rounds temporal values through their native type modifiers.
    /// </summary>
    /// <param name="type">The temporal type.</param>
    /// <param name="value">The temporal input.</param>
    /// <param name="precision">The requested precision.</param>
    /// <returns>The rounded PostgreSQL text.</returns>
    [PgFunction]
    public static string TemporalRound(string type, string value, int precision) => type switch
    {
        "time" => PgTime.Parse(value).Round(precision).ToPostgresString(),
        "timetz" => PgTimeTz.Parse(value).Round(precision).ToPostgresString(),
        "timestamp" => PgTimestamp.Parse(value).Round(precision).ToPostgresString(),
        "timestamptz" => PgTimestampTz.Parse(value).Round(precision).ToPostgresString(),
        _ => throw new ArgumentException("Unknown type.", nameof(type)),
    };

    /// <summary>
    /// Reads the SQL current/local clock family.
    /// </summary>
    /// <param name="type">The clock type.</param>
    /// <param name="precision">The fractional-second precision.</param>
    /// <returns>The PostgreSQL clock text.</returns>
    [PgFunction]
    public static string TemporalCurrent(string type, int precision) => type switch
    {
        "date" => PgDate.CurrentDate.ToPostgresString(),
        "time" => PgTime.GetLocalTime(precision).ToPostgresString(),
        "timetz" => PgTimeTz.GetCurrentTime(precision).ToPostgresString(),
        "timestamp" => PgTimestamp.GetLocalTimestamp(precision).ToPostgresString(),
        "timestamptz" => PgTimestampTz.GetCurrentTimestamp(precision).ToPostgresString(),
        _ => throw new ArgumentException("Unknown type.", nameof(type)),
    };

    /// <summary>
    /// Formats instants and offset times in an explicit zone.
    /// </summary>
    /// <param name="type">The temporal type.</param>
    /// <param name="value">The temporal input.</param>
    /// <param name="zone">The output timezone.</param>
    /// <returns>The ISO representation.</returns>
    [PgFunction]
    public static string TemporalIsoZone(string type, string value, string zone)
        => type == "timetz" ? PgTimeTz.Parse(value).ToIsoString(zone) : PgTimestampTz.Parse(value).ToIsoString(zone);

    /// <summary>
    /// Extracts local time and offset from an instant.
    /// </summary>
    /// <param name="value">The instant.</param>
    /// <returns>The local time and offset, or SQL NULL for infinity.</returns>
    [PgFunction]
    public static PgTimeTz? TimestampTimeTz(PgTimestampTz value) => value.ToTimeTz();

    /// <summary>
    /// Checks new native routines unwind managed frames and preserve prior writes on error.
    /// </summary>
    /// <param name="operation">The invalid constructor, format or rounding operation.</param>
    /// <returns>The native error code, finally count, context growth and surviving writes.</returns>
    [PgFunction]
    public static string TemporalConvenienceRecovery(string operation) => Spi.Connect(session =>
    {
        session.Execute("CREATE TEMP TABLE convenience_writes(value int)");
        session.Execute("INSERT INTO convenience_writes VALUES (1)");
        using SpiPreparedStatement plan = session.Prepare("SELECT count(*) FROM convenience_writes");
        const string count = "SELECT count(*) FROM ankus_test_memory.contexts WHERE name IN ('Ankus SPI operation', 'Ankus error diagnostics', 'CurTransactionContext')";
        long before = session.ExecuteScalar<long>(count);
        int finalized = 0;
        string failure = "no error";
        for (int index = 0; index < 50; index++)
        {
            try
            {
                PgTransaction.RunInSubtransaction(() =>
                {
                    Action call = operation switch
                    {
                        "year" => () => PgTimestamp.Create(0, 1, 1, 0, 0, 0),
                        "month" => () => PgTimestamp.Create(2024, 13, 1, 0, 0, 0),
                        "day" => () => PgTimestamp.Create(2024, 2, 30, 0, 0, 0),
                        "zone" => () => PgTimestampTz.Create(2024, 1, 1, 0, 0, 0, "Unknown/Zone"),
                        "years" => () => PgInterval.FromYears(int.MaxValue),
                        "weeks" => () => PgInterval.FromWeeks(int.MinValue),
                        "seconds" => () => PgInterval.FromSeconds(double.NaN),
                        "round" => () => PgTimestamp.Parse("294276-12-31 23:59:59.999999").Round(0),
                        "round-tz" => () => PgTimestampTz.Parse("294276-12-31 23:59:59.999999+00").Round(0),
                        "format" => () => PgTimestampTz.TransactionTimestamp.ToIsoString("Unknown/Zone"),
                        "format-range" => () => PgTimestampTz.Parse("294276-12-31 23:59:59.999999+00").ToIsoString("Asia/Tokyo"),
                        _ => throw new ArgumentException("Unknown operation.", nameof(operation)),
                    };
                    call();
                });
            }
            catch (PgException error)
            {
                failure = error.SqlState;
            }
            finally
            {
                finalized++;
            }
        }

        _ = PgTimestampTz.Create(2024, 7, 1, 12, 0, 0, "UTC").ToIsoString("America/New_York");
        session.Execute("INSERT INTO convenience_writes VALUES (2)");
        return $"{failure}:{finalized}:" + (session.ExecuteScalar<long>(count) - before) + ":" + plan.ExecuteScalar<long>();
    });

    /// <summary>
    /// Exercises all temporal arithmetic operators, including reversed addition and multiplication.
    /// </summary>
    /// <param name="type">The primary type.</param>
    /// <param name="operation">The operator.</param>
    /// <param name="left">The primary operand text.</param>
    /// <param name="right">The secondary operand text.</param>
    /// <returns>The resulting PostgreSQL text.</returns>
    [PgFunction]
    public static string TemporalOperator(string type, string operation, string left, string right) => (type, operation) switch
    {
        ("date", "days") => (PgDate.Parse(left) + int.Parse(right, CultureInfo.InvariantCulture)).ToPostgresString(),
        ("date", "rdays") => (int.Parse(right, CultureInfo.InvariantCulture) + PgDate.Parse(left)).ToPostgresString(),
        ("date", "mdays") => (PgDate.Parse(left) - int.Parse(right, CultureInfo.InvariantCulture)).ToPostgresString(),
        ("date", "difference") => (PgDate.Parse(left) - PgDate.Parse(right)).ToString(CultureInfo.InvariantCulture),
        ("date", "add") => (PgDate.Parse(left) + PgInterval.Parse(right)).ToPostgresString(),
        ("date", "radd") => (PgInterval.Parse(right) + PgDate.Parse(left)).ToPostgresString(),
        ("date", "subtract") => (PgDate.Parse(left) - PgInterval.Parse(right)).ToPostgresString(),
        ("date", "time") => (PgDate.Parse(left) + PgTime.Parse(right)).ToPostgresString(),
        ("date", "rtime") => (PgTime.Parse(right) + PgDate.Parse(left)).ToPostgresString(),
        ("date", "timetz") => (PgDate.Parse(left) + PgTimeTz.Parse(right)).ToPostgresString(),
        ("date", "rtimetz") => (PgTimeTz.Parse(right) + PgDate.Parse(left)).ToPostgresString(),
        ("time", "add") => (PgTime.Parse(left) + PgInterval.Parse(right)).ToPostgresString(),
        ("time", "radd") => (PgInterval.Parse(right) + PgTime.Parse(left)).ToPostgresString(),
        ("time", "subtract") => (PgTime.Parse(left) - PgInterval.Parse(right)).ToPostgresString(),
        ("time", "difference") => (PgTime.Parse(left) - PgTime.Parse(right)).ToPostgresString(),
        ("timetz", "add") => (PgTimeTz.Parse(left) + PgInterval.Parse(right)).ToPostgresString(),
        ("timetz", "radd") => (PgInterval.Parse(right) + PgTimeTz.Parse(left)).ToPostgresString(),
        ("timetz", "subtract") => (PgTimeTz.Parse(left) - PgInterval.Parse(right)).ToPostgresString(),
        ("timestamp", "add") => (PgTimestamp.Parse(left) + PgInterval.Parse(right)).ToPostgresString(),
        ("timestamp", "radd") => (PgInterval.Parse(right) + PgTimestamp.Parse(left)).ToPostgresString(),
        ("timestamp", "subtract") => (PgTimestamp.Parse(left) - PgInterval.Parse(right)).ToPostgresString(),
        ("timestamp", "difference") => (PgTimestamp.Parse(left) - PgTimestamp.Parse(right)).ToPostgresString(),
        ("timestamptz", "add") => (PgTimestampTz.Parse(left) + PgInterval.Parse(right)).ToPostgresString(),
        ("timestamptz", "radd") => (PgInterval.Parse(right) + PgTimestampTz.Parse(left)).ToPostgresString(),
        ("timestamptz", "subtract") => (PgTimestampTz.Parse(left) - PgInterval.Parse(right)).ToPostgresString(),
        ("timestamptz", "difference") => (PgTimestampTz.Parse(left) - PgTimestampTz.Parse(right)).ToPostgresString(),
        ("interval", "add") => (PgInterval.Parse(left) + PgInterval.Parse(right)).ToPostgresString(),
        ("interval", "subtract") => (PgInterval.Parse(left) - PgInterval.Parse(right)).ToPostgresString(),
        ("interval", "negate") => (-PgInterval.Parse(left)).ToPostgresString(),
        ("interval", "multiply") => (PgInterval.Parse(left) * double.Parse(right, CultureInfo.InvariantCulture)).ToPostgresString(),
        ("interval", "rmultiply") => (double.Parse(right, CultureInfo.InvariantCulture) * PgInterval.Parse(left)).ToPostgresString(),
        ("interval", "divide") => (PgInterval.Parse(left) / double.Parse(right, CultureInfo.InvariantCulture)).ToPostgresString(),
        _ => throw new ArgumentException("Unknown operator.", nameof(operation)),
    };
}
