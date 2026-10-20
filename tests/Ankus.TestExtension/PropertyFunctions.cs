namespace Ankus.TestExtension;

/// <summary>
/// Ports pgrx's <c>proptests</c>: each date and time type survives SPI parameters and its own text literal for
/// generated raw values, and a backend error inside a property is reported as a failing input.
/// </summary>
public static class PropertyFunctions
{
    /// <summary>
    /// Returns a date unchanged, as pgrx's <c>nop_date</c> does.
    /// </summary>
    /// <param name="value">The date.</param>
    /// <returns>The same date.</returns>
    [PgFunction]
    public static PgDate NopDate(PgDate value) => value;

    /// <summary>
    /// Returns a time unchanged, as pgrx's <c>nop_time</c> does.
    /// </summary>
    /// <param name="value">The time.</param>
    /// <returns>The same time.</returns>
    [PgFunction]
    public static PgTime NopTime(PgTime value) => value;

    /// <summary>
    /// Returns a timestamp unchanged, as pgrx's <c>nop_timestamp</c> does.
    /// </summary>
    /// <param name="value">The timestamp.</param>
    /// <returns>The same timestamp.</returns>
    [PgFunction]
    public static PgTimestamp NopTimestamp(PgTimestamp value) => value;

    /// <summary>
    /// Returns a time with time zone unchanged, as pgrx's <c>nop_timetz</c> does.
    /// </summary>
    /// <param name="value">The time with time zone.</param>
    /// <returns>The same value.</returns>
    [PgFunction(Name = "nop_timetz")]
    public static PgTimeTz NopTimeTz(PgTimeTz value) => value;

    /// <summary>
    /// Runs one of pgrx's eight temporal round-trip properties with pgrx's strategies over raw values.
    /// </summary>
    /// <param name="name">The pgrx test name, such as <c>date_spi_roundtrip</c>.</param>
    /// <param name="seed">The run's seed.</param>
    /// <returns>The number of inputs checked.</returns>
    [PgFunction]
    public static int TemporalRoundTripProperty(string name, long seed)
    {
        var runner = new PgPropertyRunner(new PgPropertyOptions { Seed = unchecked((ulong)seed) });
        int checkedInputs = 0;
        switch (name)
        {
            case "date_spi_roundtrip":
                runner.Run(PgGenerators.Number<int>().Select(PgDate.FromRawSaturating), value => RoundTrip(value, "nop_date", ref checkedInputs));
                break;
            case "date_literal_spi_roundtrip":
                runner.Run(PgGenerators.Number<int>().Select(PgDate.FromRawSaturating), value => LiteralRoundTrip(value, "nop_date", ref checkedInputs));
                break;
            case "time_spi_roundtrip":
                runner.Run(PgGenerators.Number<long>().Select(PgTime.FromMicrosecondsWrapping), value => RoundTrip(value, "nop_time", ref checkedInputs));
                break;
            case "time_literal_spi_roundtrip":
                runner.Run(PgGenerators.Number<long>().Select(PgTime.FromMicrosecondsWrapping), value => LiteralRoundTrip(value, "nop_time", ref checkedInputs));
                break;
            case "timestamp_spi_roundtrip":
                runner.Run(PgGenerators.Number<long>().Select(PgTimestamp.FromRawSaturating), value => RoundTrip(value, "nop_timestamp", ref checkedInputs));
                break;
            case "timestamp_literal_spi_roundtrip":
                runner.Run(PgGenerators.Number<long>().Select(PgTimestamp.FromRawSaturating), value => LiteralRoundTrip(value, "nop_timestamp", ref checkedInputs));
                break;
            case "timetz_spi_roundtrip":
                runner.Run(TimesWithTimeZone(), value => RoundTrip(value, "nop_timetz", ref checkedInputs));
                break;
            case "timetz_literal_spi_roundtrip":
                runner.Run(TimesWithTimeZone(), value => LiteralRoundTrip(value, "nop_timetz", ref checkedInputs));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown property test.");
        }

        return checkedInputs;
    }

    /// <summary>
    /// Runs a property whose query divides by zero for multiples of seven, writing each input to a temporary table.
    /// </summary>
    /// <param name="seed">The run's seed.</param>
    /// <returns>
    /// The minimal input, the failure's SQLSTATE and message, how many failing inputs left rows, and whether passing
    /// inputs kept theirs.
    /// </returns>
    [PgFunction]
    public static string PropertyBackendErrorShrinks(long seed)
    {
        _ = Spi.Execute("CREATE TEMP TABLE property_inputs (value integer NOT NULL)");
        try
        {
            new PgPropertyRunner(new PgPropertyOptions { Seed = unchecked((ulong)seed) }).Run(PgGenerators.Number<int>(), value =>
            {
                _ = Spi.Execute("INSERT INTO property_inputs VALUES ($1)", SpiParameter.Create(value));
                _ = Spi.ExecuteScalar<int>("SELECT 100 / ($1 % 7)", SpiParameter.Create(value));
            });
            return "passed";
        }
        catch (PgPropertyException error) when (error.InnerException is PgException failure)
        {
            long failed = Spi.ExecuteScalar<long>("SELECT count(*) FROM property_inputs WHERE value % 7 = 0");
            long passed = Spi.ExecuteScalar<long>("SELECT count(*) FROM property_inputs WHERE value % 7 <> 0");
            return string.Join('|', error.Input, failure.SqlState, failure.Message, failed, passed >= error.PassedCases);
        }
        finally
        {
            _ = Spi.Execute("DROP TABLE property_inputs");
        }
    }

    private static PgGenerator<PgTimeTz> TimesWithTimeZone()
        => from microseconds in PgGenerators.Number<long>()
           from offset in PgGenerators.Number<int>()
           select PgTimeTz.FromRawWrapping(microseconds, offset);

    private static void RoundTrip<T>(T value, string function, ref int checkedInputs)
    {
        T result = Spi.ExecuteScalar<T>($"SELECT {Spi.QuoteQualifiedIdentifier("datatype", function)}($1)", SpiParameter.Create(value));
        Require(EqualityComparer<T>.Default.Equals(value, result), value, result);
        checkedInputs++;
    }

    private static void LiteralRoundTrip<T>(T value, string function, ref int checkedInputs)
    {
        string text = Spi.ExecuteScalar<string>("SELECT $1::text", SpiParameter.Create(value));
        T result = Spi.ExecuteScalar<T>($"SELECT {Spi.QuoteQualifiedIdentifier("datatype", function)}({Spi.QuoteLiteral(text)})");
        Require(EqualityComparer<T>.Default.Equals(value, result), value, result);
        checkedInputs++;
    }

    private static void Require<T>(bool condition, T expected, T actual)
    {
        if (!condition)
        {
            throw new InvalidOperationException($"Expected {expected} but PostgreSQL returned {actual}.");
        }
    }
}
