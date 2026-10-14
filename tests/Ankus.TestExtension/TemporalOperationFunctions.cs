using System.Globalization;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises PostgreSQL-backed temporal APIs from real Native AOT callbacks.
/// </summary>
public static class TemporalOperationFunctions
{
    /// <summary>
    /// Runs a temporal operation and returns its native textual representation.
    /// </summary>
    /// <param name="type">The temporal type.</param>
    /// <param name="operation">The method to exercise.</param>
    /// <param name="left">The primary operand.</param>
    /// <param name="right">The secondary operand, field, or timezone.</param>
    /// <returns>The operation result or SQL NULL.</returns>
    [PgFunction]
    public static string? TemporalOperation(string type, string operation, string left, string right)
        => type switch
        {
            "date" => DateOperation(PgDate.Parse(left), operation, right),
            "time" => TimeOperation(PgTime.Parse(left), operation, right),
            "timetz" => TimeTzOperation(PgTimeTz.Parse(left), operation, right),
            "timestamp" => TimestampOperation(PgTimestamp.Parse(left), operation, right),
            "timestamptz" => TimestampTzOperation(PgTimestampTz.Parse(left), operation, right),
            "interval" => IntervalOperation(PgInterval.Parse(left), operation, right),
            _ => throw new ArgumentException("Unknown temporal type.", nameof(type)),
        };

    /// <summary>
    /// Constructs a date with native calendar validation.
    /// </summary>
    /// <param name="year">The signed year.</param>
    /// <param name="month">The month.</param>
    /// <param name="day">The day.</param>
    /// <returns>The date.</returns>
    [PgFunction]
    public static PgDate MakePgDate(int year, int month, int day) => PgDate.Create(year, month, day);

    /// <summary>
    /// Constructs a time with native fractional-second handling.
    /// </summary>
    /// <param name="hour">The hour.</param>
    /// <param name="minute">The minute.</param>
    /// <param name="second">The fractional seconds.</param>
    /// <returns>The time.</returns>
    [PgFunction]
    public static PgTime MakePgTime(int hour, int minute, double second) => PgTime.Create(hour, minute, second);

    /// <summary>
    /// Checks TryParse results and their output value on both success and failure.
    /// </summary>
    /// <param name="type">The temporal type.</param>
    /// <param name="text">The candidate input.</param>
    /// <returns>The success flag and formatted out value.</returns>
    [PgFunction]
    public static string TryParseTemporal(string type, string? text) => type switch
    {
        "date" => PgDate.TryParse(text, out PgDate date) + ":" + date.ToPostgresString(),
        "time" => PgTime.TryParse(text, out PgTime time) + ":" + time.ToPostgresString(),
        "timetz" => PgTimeTz.TryParse(text, out PgTimeTz timetz) + ":" + timetz.ToPostgresString(),
        "timestamp" => PgTimestamp.TryParse(text, out PgTimestamp timestamp) + ":" + timestamp.ToPostgresString(),
        "timestamptz" => PgTimestampTz.TryParse(text, out PgTimestampTz instant) + ":" + instant.ToPostgresString(),
        "interval" => PgInterval.TryParse(text, out PgInterval interval) + ":" + interval.ToPostgresString(),
        _ => throw new ArgumentException("Unknown temporal type.", nameof(type)),
    };

    /// <summary>
    /// Checks invalid managed UTF-16 is rejected before native parsing.
    /// </summary>
    /// <returns>The parse flag and out value.</returns>
    [PgFunction]
    public static string TryParseInvalidUtf16() => PgDate.TryParse("\ud800", out PgDate value) + ":" + value.ToPostgresString();

    /// <summary>
    /// Checks native errors can be caught inside an active session while retaining prior writes and finally execution.
    /// </summary>
    /// <param name="type">The temporal type.</param>
    /// <param name="operation">The operation that should fail.</param>
    /// <param name="left">The primary operand.</param>
    /// <param name="right">The secondary operand.</param>
    /// <returns>The SQLSTATE, native source availability, finally count, and surviving write count.</returns>
    [PgFunction]
    public static string TemporalOperationRecovery(string type, string operation, string left, string right)
        => Spi.Connect(session =>
        {
            session.Execute("CREATE TEMP TABLE temporal_writes(value integer)");
            session.Execute("INSERT INTO temporal_writes VALUES (1)");
            string state = "no error";
            int finalized = 0;
            bool source = false;
            try
            {
                PgTransaction.RunInSubtransaction(() => TemporalOperation(type, operation, left, right));
            }
            catch (PgException error)
            {
                state = error.SqlState;
                source = error.Routine is not null;
            }
            finally
            {
                finalized++;
            }

            session.Execute("INSERT INTO temporal_writes VALUES (2)");
            return $"{state}:{source}:{finalized}:" + session.ExecuteScalar<long>("SELECT count(*) FROM temporal_writes");
        });

    /// <summary>
    /// Truncates an instant in an explicit timezone.
    /// </summary>
    /// <param name="value">The UTC instant.</param>
    /// <param name="part">The truncation field.</param>
    /// <param name="zone">The timezone.</param>
    /// <returns>The truncated instant.</returns>
    [PgFunction]
    public static PgTimestampTz TruncateInZone(PgTimestampTz value, string part, string zone) => value.Truncate(Part(part), zone);

    /// <summary>
    /// Verifies direct calls do not replace the active SPI connection or retain operation contexts.
    /// </summary>
    /// <returns>The before/after connection and context deltas, plus the surviving plan result.</returns>
    [PgFunction]
    public static string TemporalOperationsPreserveSession()
        => Spi.Connect(session =>
        {
            const string contexts = """
                SELECT count(*) FROM ankus_test_memory.contexts
                WHERE name IN ('Ankus SPI operation', 'Ankus error diagnostics', 'CurTransactionContext')
                """;
            const string connections = "SELECT count(*) FROM ankus_test_memory.contexts WHERE name = 'SPI Proc'";
            long before = session.ExecuteScalar<long>(contexts);
            long connectionsBefore = session.ExecuteScalar<long>(connections);
            using SpiPreparedStatement plan = session.Prepare("SELECT $1 + 2", typeof(int));
            for (int index = 0; index < 100; index++)
            {
                PgTimestampTz instant = PgTimestamp.Parse("2024-03-10 02:30:00").AtTimeZone("America/New_York");
                instant.Add(new PgInterval(0, 1, index)).ToIsoString();
                try
                {
                    PgTransaction.RunInSubtransaction(() => PgInterval.Parse("1 day").Divide(0));
                }
                catch (PgException error) when (error.SqlState == "22012")
                {
                    // Each division by zero fails on purpose; the count shows what the failures leave behind.
                }
            }

            return session.ExecuteScalar<long>(contexts) - before + ":" +
                (session.ExecuteScalar<long>(connections) - connectionsBefore) + ":" +
                plan.ExecuteScalar<int>(SpiParameter.Create(40));
        });

    /// <summary>
    /// Reads server clocks in one statement, including direct operations inside an open SPI session.
    /// </summary>
    /// <returns>The clock checks performed entirely in the backend.</returns>
    [PgFunction]
    public static string TemporalClocks()
        => Spi.Connect(session =>
        {
            PgTimestampTz transaction = PgTimestampTz.TransactionTimestamp;
            PgTimestampTz statement = PgTimestampTz.StatementTimestamp;
            PgTimestampTz clock = PgTimestampTz.ClockTimestamp;
            PgTimestampTz nativeTransaction = session.ExecuteScalar<PgTimestampTz>("SELECT transaction_timestamp()");
            PgTimestampTz nativeStatement = session.ExecuteScalar<PgTimestampTz>("SELECT statement_timestamp()");
            PgTimestampTz nativeClock = session.ExecuteScalar<PgTimestampTz>("SELECT clock_timestamp()");
            return $"{transaction == nativeTransaction}:{statement == nativeStatement}:" +
                $"{clock.MicrosecondsSinceEpoch <= nativeClock.MicrosecondsSinceEpoch}:" +
                PgTimestampTz.FromUnixTimeSeconds(0.000001).ToIsoString();
        });

    private static PgDateTimePart Part(string part) => Enum.Parse<PgDateTimePart>(part);

    private static string? DateOperation(PgDate value, string operation, string right) => operation switch
    {
        "format" => value.ToPostgresString(),
        "iso" => value.ToIsoString(),
        "days" => value.AddDays(int.Parse(right, CultureInfo.InvariantCulture)).ToPostgresString(),
        "add" => value.Add(PgInterval.Parse(right)).ToPostgresString(),
        "subtract" => value.Subtract(PgInterval.Parse(right)).ToPostgresString(),
        "difference" => value.Subtract(PgDate.Parse(right)).ToString(CultureInfo.InvariantCulture),
        "time" => value.AtTime(PgTime.Parse(right)).ToPostgresString(),
        "timetz" => value.AtTime(PgTimeTz.Parse(right)).ToPostgresString(),
        "timestamp" => value.ToTimestamp().ToPostgresString(),
        "timestamptz" => value.ToTimestampTz().ToPostgresString(),
        "part" => value.GetPart(Part(right))?.ToString("R", CultureInfo.InvariantCulture),
        "compare" => value.CompareTo(PgDate.Parse(right)).ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Unknown date operation."),
    };

    private static string? TimeOperation(PgTime value, string operation, string right) => operation switch
    {
        "format" => value.ToPostgresString(),
        "iso" => value.ToIsoString(),
        "add" => value.Add(PgInterval.Parse(right)).ToPostgresString(),
        "subtract" => value.Subtract(PgInterval.Parse(right)).ToPostgresString(),
        "difference" => value.Subtract(PgTime.Parse(right)).ToPostgresString(),
        "part" => value.GetPart(Part(right))?.ToString("R", CultureInfo.InvariantCulture),
        "compare" => value.CompareTo(PgTime.Parse(right)).ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Unknown time operation."),
    };

    private static string? TimeTzOperation(PgTimeTz value, string operation, string right) => operation switch
    {
        "format" => value.ToPostgresString(),
        "iso" => value.ToIsoString(),
        "add" => value.Add(PgInterval.Parse(right)).ToPostgresString(),
        "subtract" => value.Subtract(PgInterval.Parse(right)).ToPostgresString(),
        "zone" => value.AtTimeZone(right).ToPostgresString(),
        "time" => value.ToTime().ToPostgresString(),
        "part" => value.GetPart(Part(right))?.ToString("R", CultureInfo.InvariantCulture),
        "compare" => value.CompareTo(PgTimeTz.Parse(right)).ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Unknown timetz operation."),
    };

    private static string? TimestampOperation(PgTimestamp value, string operation, string right) => operation switch
    {
        "format" => value.ToPostgresString(),
        "iso" => value.ToIsoString(),
        "add" => value.Add(PgInterval.Parse(right)).ToPostgresString(),
        "subtract" => value.Subtract(PgInterval.Parse(right)).ToPostgresString(),
        "difference" => value.Subtract(PgTimestamp.Parse(right)).ToPostgresString(),
        "age" => value.Age(PgTimestamp.Parse(right)).ToPostgresString(),
        "truncate" => value.Truncate(Part(right)).ToPostgresString(),
        "zone" => value.AtTimeZone(right).ToPostgresString(),
        "timestamptz" => value.ToTimestampTz().ToPostgresString(),
        "date" => value.ToDate().ToPostgresString(),
        "time" => value.ToTime()?.ToPostgresString(),
        "part" => value.GetPart(Part(right))?.ToString("R", CultureInfo.InvariantCulture),
        "compare" => value.CompareTo(PgTimestamp.Parse(right)).ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Unknown timestamp operation."),
    };

    private static string? TimestampTzOperation(PgTimestampTz value, string operation, string right) => operation switch
    {
        "format" => value.ToPostgresString(),
        "iso" => value.ToIsoString(),
        "add" => value.Add(PgInterval.Parse(right)).ToPostgresString(),
        "subtract" => value.Subtract(PgInterval.Parse(right)).ToPostgresString(),
        "difference" => value.Subtract(PgTimestampTz.Parse(right)).ToPostgresString(),
        "age" => value.Age(PgTimestampTz.Parse(right)).ToPostgresString(),
        "truncate" => value.Truncate(Part(right)).ToPostgresString(),
        "zone" => value.AtTimeZone(right).ToPostgresString(),
        "timestamp" => value.ToTimestamp().ToPostgresString(),
        "date" => value.ToDate().ToPostgresString(),
        "time" => value.ToTime()?.ToPostgresString(),
        "part" => value.GetPart(Part(right))?.ToString("R", CultureInfo.InvariantCulture),
        "compare" => value.CompareTo(PgTimestampTz.Parse(right)).ToString(CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Unknown timestamptz operation."),
    };

    private static string? IntervalOperation(PgInterval value, string operation, string right) => operation switch
    {
        "format" => value.ToPostgresString(),
        "add" => value.Add(PgInterval.Parse(right)).ToPostgresString(),
        "subtract" => value.Subtract(PgInterval.Parse(right)).ToPostgresString(),
        "multiply" => value.Multiply(double.Parse(right, CultureInfo.InvariantCulture)).ToPostgresString(),
        "divide" => value.Divide(double.Parse(right, CultureInfo.InvariantCulture)).ToPostgresString(),
        "negate" => value.Negate().ToPostgresString(),
        "days" => value.JustifyDays().ToPostgresString(),
        "hours" => value.JustifyHours().ToPostgresString(),
        "justify" => value.Justify().ToPostgresString(),
        "truncate" => value.Truncate(Part(right)).ToPostgresString(),
        "compare" => value.CompareInPostgres(PgInterval.Parse(right)).ToString(CultureInfo.InvariantCulture),
        "part" => value.GetPart(Part(right))?.ToString("R", CultureInfo.InvariantCulture),
        _ => throw new ArgumentException("Unknown interval operation."),
    };
}
