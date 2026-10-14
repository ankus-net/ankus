using System.Globalization;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises full-range numeric values and checked decimal adapters in Native AOT.
/// </summary>
public static class NumericFunctions
{
    /// <summary>
    /// Returns numeric through a selected function/SPI ownership path.
    /// </summary>
    /// <param name="value">The value or SQL NULL.</param>
    /// <param name="mode">The ownership path.</param>
    /// <returns>The owned numeric.</returns>
    [PgFunction]
    public static PgNumeric? ExchangeNumeric(PgNumeric? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Returns decimal through a selected function/SPI ownership path.
    /// </summary>
    /// <param name="value">The value or SQL NULL.</param>
    /// <param name="mode">The ownership path.</param>
    /// <returns>The exact decimal.</returns>
    [PgFunction]
    public static decimal? ExchangeDecimal(decimal? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Parses text with PostgreSQL's input rules.
    /// </summary>
    /// <param name="text">The candidate text.</param>
    /// <returns>The parsed numeric.</returns>
    [PgFunction]
    public static PgNumeric NumericFromText(string text) => PgNumeric.Parse(text);

    /// <summary>
    /// Checks failed TryParse input and its default out value.
    /// </summary>
    /// <param name="text">The candidate input.</param>
    /// <returns>The parse flag and numeric text.</returns>
    [PgFunction]
    public static string NumericTryParse(string? text) => PgNumeric.TryParse(text, out PgNumeric value) + ":" + value.Text;

    /// <summary>
    /// Returns the exact decimal result constructed in managed code.
    /// </summary>
    /// <returns>A scaled decimal result.</returns>
    [PgFunction]
    public static decimal DecimalFromManaged() => 12345678901234567890.123456789m;

    /// <summary>
    /// Exercises floating-point to numeric conversion.
    /// </summary>
    /// <param name="value">The floating-point value.</param>
    /// <returns>The PostgreSQL conversion.</returns>
    [PgFunction]
    public static PgNumeric NumericFromDouble(double value) => PgNumeric.FromDouble(value);

    /// <summary>
    /// Exercises numeric to floating-point conversion.
    /// </summary>
    /// <param name="value">The numeric value.</param>
    /// <returns>The PostgreSQL conversion.</returns>
    [PgFunction]
    public static double NumericToDouble(PgNumeric value) => value.ToDouble();

    /// <summary>
    /// Compares managed numeric ordering and equality with native SQL comparators.
    /// </summary>
    /// <param name="left">The first value.</param>
    /// <param name="right">The second value.</param>
    /// <returns>The order sign, equality, and equality of hashes.</returns>
    [PgFunction]
    public static string NumericComparison(PgNumeric left, PgNumeric right)
        => $"{Math.Sign(left.CompareTo(right))}:{left == right}:{left.GetHashCode() == right.GetHashCode()}";

    /// <summary>
    /// Runs an arithmetic or precision operation.
    /// </summary>
    /// <param name="operation">The operation.</param>
    /// <param name="left">The primary operand.</param>
    /// <param name="right">The secondary operand.</param>
    /// <param name="precision">The precision or rounding scale.</param>
    /// <param name="scale">The rescaling scale.</param>
    /// <returns>The computed numeric.</returns>
    [PgFunction]
    public static PgNumeric NumericApply(string operation, PgNumeric left, PgNumeric right, int precision, int scale) => operation switch
    {
        "add" => left + right,
        "subtract" => left - right,
        "multiply" => left * right,
        "divide" => left / right,
        "remainder" => left % right,
        "negate" => -left,
        "abs" => left.Abs(),
        "round" => left.Round(precision),
        "truncate" => left.Truncate(precision),
        "ceiling" => left.Ceiling(),
        "floor" => left.Floor(),
        "sqrt" => left.Sqrt(),
        "exp" => left.Exp(),
        "log" => left.Log(),
        "logbase" => left.Log(right),
        "power" => left.Pow(right),
        "gcd" => left.GreatestCommonDivisor(right),
        "lcm" => left.LeastCommonMultiple(right),
        "rescale" => left.Rescale(precision, scale),
        _ => throw new ArgumentException("Unknown numeric operation.", nameof(operation)),
    };

    /// <summary>
    /// Extracts exact temporal fields through all six temporal value types.
    /// </summary>
    /// <param name="type">The temporal type.</param>
    /// <param name="input">The temporal text.</param>
    /// <param name="part">The field.</param>
    /// <returns>The numeric field or SQL NULL.</returns>
    [PgFunction]
    public static PgNumeric? NumericExtract(string type, string input, string part)
    {
        PgDateTimePart field = Enum.Parse<PgDateTimePart>(part);
        return type switch
        {
            "date" => PgDate.Parse(input).Extract(field),
            "time" => PgTime.Parse(input).Extract(field),
            "timetz" => PgTimeTz.Parse(input).Extract(field),
            "timestamp" => PgTimestamp.Parse(input).Extract(field),
            "timestamptz" => PgTimestampTz.Parse(input).Extract(field),
            "interval" => PgInterval.Parse(input).Extract(field),
            _ => throw new ArgumentException("Unknown temporal type.", nameof(type)),
        };
    }

    /// <summary>
    /// Extracts a numeric field from an independently supplied interval datum without parsing text.
    /// </summary>
    /// <param name="input">The full-range interval.</param>
    /// <param name="part">The field.</param>
    /// <returns>The numeric field or SQL NULL.</returns>
    [PgFunction]
    public static PgNumeric? NumericIntervalExtract(PgInterval input, string part)
        => input.Extract(Enum.Parse<PgDateTimePart>(part));

    /// <summary>
    /// Retains numeric domain values across session disposal and subsequent calls.
    /// </summary>
    /// <returns>The owned text and decimal conversion.</returns>
    [PgFunction]
    public static string NumericDomain()
    {
        SpiRow row = Spi.Connect(session => session.Query("SELECT value FROM numeric_domain_values")[0]);
        Spi.Execute("SELECT repeat('overwrite temporary memory', 10000)");
        return row.Get<PgNumeric>(0).Text + ":" + row.Get<decimal>(0).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Checks native and managed errors preserve session state and prior writes.
    /// </summary>
    /// <param name="operation">The failing operation.</param>
    /// <param name="left">The primary operand text.</param>
    /// <param name="right">The secondary operand text.</param>
    /// <param name="precision">The precision or rounding scale.</param>
    /// <param name="scale">The declared scale.</param>
    /// <returns>The failure code, finally count, and surviving write count.</returns>
    [PgFunction]
    public static string NumericRecovery(string operation, string left, string right, int precision, int scale)
        => Spi.Connect(session =>
        {
            session.Execute("CREATE TEMP TABLE numeric_writes(value integer)");
            session.Execute("INSERT INTO numeric_writes VALUES (1)");
            string failure = "no error";
            int finalized = 0;
            try
            {
                if (operation == "decimal")
                {
                    session.ExecuteScalar<decimal>("SELECT $1::numeric", SpiParameter.Create(left));
                }
                else
                {
                    PgTransaction.RunInSubtransaction(() =>
                        NumericApply(operation, PgNumeric.Parse(left), PgNumeric.Parse(right), precision, scale));
                }
            }
            catch (PgException error)
            {
                failure = error.SqlState;
            }
            catch (OverflowException)
            {
                failure = "decimal overflow";
            }
            finally
            {
                finalized++;
            }

            session.Execute("INSERT INTO numeric_writes VALUES (2)");
            return $"{failure}:{finalized}:" + session.ExecuteScalar<long>("SELECT count(*) FROM numeric_writes");
        });

    /// <summary>
    /// Checks repeated operations and conversion failures release temporary native contexts.
    /// </summary>
    /// <returns>The extra context count and surviving plan result.</returns>
    [PgFunction]
    public static string NumericContextGrowth()
        => Spi.Connect(session =>
        {
            const string count = """
                SELECT count(*) FROM ankus_test_memory.contexts
                WHERE name IN ('Ankus SPI operation', 'Ankus error diagnostics', 'CurTransactionContext')
                """;
            long before = session.ExecuteScalar<long>(count);
            using SpiPreparedStatement plan = session.Prepare("SELECT $1 + 2", typeof(decimal));
            for (int index = 0; index < 100; index++)
            {
                PgNumeric value = PgNumeric.Parse("123456789012345678901234567890.1234500");
                _ = (value * PgNumeric.FromDecimal(2m)).Rescale(50, 8).Text;
                try
                {
                    _ = PgTransaction.RunInSubtransaction(() => value / default(PgNumeric));
                }
                catch (PgException error) when (error.SqlState == "22012")
                {
                    // Each division by zero fails on purpose; the count shows what the failures leave behind.
                }
            }

            return session.ExecuteScalar<long>(count) - before + ":" + plan.ExecuteScalar<decimal>(SpiParameter.Create(40m));
        });

    private static T Exchange<T>(T value, int mode)
    {
        const string sql = "SELECT $1";
        switch (mode)
        {
            case 0:
                return value;
            case 1:
                return Spi.ExecuteScalar<T>(sql, SpiParameter.Create(value));
            case 2:
                using (SpiPreparedStatement plan = Spi.Prepare(sql, typeof(T)))
                {
                    return plan.ExecuteScalar<T>(SpiParameter.Create(value));
                }

            case 3:
                return Spi.Connect(session => session.ExecuteScalar<T>(sql, SpiParameter.Create(value)));
            case 4:
                using (SpiCursor cursor = Spi.OpenCursor(sql, SpiParameter.Create(value)))
                {
                    SpiRow row = cursor.Fetch(1)[0];
                    cursor.Fetch(1);
                    return row.Get<T>(0);
                }

            case 5:
                return Spi.Connect(session =>
                {
                    using SpiPreparedStatement plan = session.Prepare(sql, typeof(T));
                    return plan.ExecuteScalar<T>(SpiParameter.Create(value));
                });
            case 6:
                using (SpiPreparedStatement plan = Spi.Connect(session => session.Prepare(sql, typeof(T)).Keep()))
                {
                    return plan.ExecuteScalar<T>(SpiParameter.Create(value));
                }

            case 7:
                SpiRow edited = Spi.Query("SELECT 42 AS value")[0];
                edited.Set("value", value);
                return Spi.ExecuteScalar<T>(sql, SpiParameter.Create(edited.Get<T>(0)));
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }
}
