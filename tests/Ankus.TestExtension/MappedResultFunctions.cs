using System.Globalization;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises requested mapped scalar results without adding mapped types to canonical untyped rows.
/// </summary>
[PgSchema("mapped_results")]
public static class MappedResultFunctions
{
    /// <summary>
    /// Reads independent scalar, pair, and triple contracts through every SPI convenience owner.
    /// </summary>
    /// <param name="surface">Static, session, retained plan, or session plan.</param>
    /// <param name="width">The requested number of columns.</param>
    /// <param name="sql">The test-controlled result command.</param>
    /// <returns>The independently interpreted selected values.</returns>
    [PgFunction(Name = "values")]
    public static string Values(int surface, int width, string sql)
    {
        if (width == 1)
        {
            return Number(Read<ResultInt?>(surface, sql)?.Value);
        }

        if (width == 2)
        {
            (ResultInt? first, ReadMappedInt? second) = Read<ResultInt?, ReadMappedInt?>(surface, sql);
            return $"{Number(first?.Value)}|{Number(second?.Value)}";
        }

        (ResultInt? number, ReadMappedInt? alias, ResultText? text) = Read<ResultInt?, ReadMappedInt?, ResultText?>(surface, sql);
        return $"{Number(number?.Value)}|{Number(alias?.Value)}|{text?.Value ?? "<NULL>"}";
    }

    /// <summary>
    /// Requires an actually present mapped integer, preserving zero rather than treating it as NULL.
    /// </summary>
    /// <param name="surface">The SPI entry point.</param>
    /// <param name="sql">The scalar command.</param>
    /// <returns>The exact present integer.</returns>
    [PgFunction(Name = "required")]
    public static int Required(int surface, string sql) => Read<ResultInt>(surface, sql).Value;

    /// <summary>
    /// Converts an exact domain result while allowing ordinary nullable absence.
    /// </summary>
    /// <param name="surface">The SPI entry point.</param>
    /// <param name="sql">The domain query.</param>
    /// <returns>The domain value or SQL NULL.</returns>
    [PgFunction(Name = "positive")]
    public static int? Positive(int surface, string sql) => Read<ResultPositive?>(surface, sql)?.Value;

    /// <summary>
    /// Detaches a text value before its temporary, session, and plan owners end.
    /// </summary>
    /// <param name="surface">The SPI entry point.</param>
    /// <param name="sql">The text query.</param>
    /// <returns>The complete text or SQL NULL.</returns>
    [PgFunction(Name = "text")]
    public static string? Text(int surface, string sql) => Read<ResultText?>(surface, sql)?.Value;

    /// <summary>
    /// Allows NULL and absent results to bypass a deliberately failing converter constructor.
    /// </summary>
    /// <param name="surface">The SPI owner selector.</param>
    /// <param name="sql">The scalar query.</param>
    /// <returns>The absent value without constructing its reader.</returns>
    [PgFunction(Name = "factory_optional")]
    public static int? FactoryOptional(int surface, string sql) => Read<ResultFactoryValue?>(surface, sql)?.Value;

    /// <summary>
    /// Reads both fields of fixed-size by-reference storage after the query owner has ended.
    /// </summary>
    /// <param name="surface">The SPI entry point.</param>
    /// <param name="sql">The fixed-storage query.</param>
    /// <returns>The independently inspectable IEEE words.</returns>
    [PgFunction(Name = "complex")]
    public static string Complex(int surface, string sql)
    {
        MappedComplex value = Read<MappedComplex>(surface, sql);
        return string.Create(CultureInfo.InvariantCulture,
            $"{BitConverter.DoubleToInt64Bits(value.Real)}|{BitConverter.DoubleToInt64Bits(value.Imaginary)}");
    }

    /// <summary>
    /// Keeps copied mapped results and separately retained polymorphic results live together.
    /// </summary>
    /// <param name="surface">The SPI owner selector.</param>
    /// <param name="array">Whether the middle result is an array.</param>
    /// <returns>Exact independent values, OIDs, shape, bounds, and nullable cells.</returns>
    [PgFunction(Name = "mixed")]
    public static string Mixed(int surface, bool array)
    {
        if (array)
        {
            (ResultText text, PgAnyArray values, int tail) = Read<ResultText, PgAnyArray, int>(surface,
                "SELECT 'owned'::text, '[-2:0]={7,NULL,-9}'::integer[], 73");
            return string.Create(CultureInfo.InvariantCulture,
                $"{text.Value}|{values.TypeOid}|{PolymorphicFunctions.PolyArrayShape(values)}|{tail}");
        }

        (int head, ResultInt number, PgAnyElement value) = Read<int, ResultInt, PgAnyElement>(surface,
            "SELECT 73, 42, 'kept'::text");
        return string.Create(CultureInfo.InvariantCulture, $"{head}|{number.Value}|{value.TypeOid}|{value.Datum.ToPostgresString()}");
    }

    /// <summary>
    /// Proves unsupported later positions reject before executing any result-producing SQL.
    /// </summary>
    /// <param name="surface">The SPI entry point.</param>
    /// <param name="width">Scalar, pair, triple, or deferred-array triple.</param>
    /// <param name="sql">A query containing a nontransactional side effect.</param>
    /// <returns>A sentinel only if capability checking fails.</returns>
    [PgFunction(Name = "denied")]
    public static int Denied(int surface, int width, string sql)
    {
        switch (width)
        {
            case 1:
                Read<WriteMappedInt?>(surface, sql);
                break;
            case 2:
                Read<PgAnyElement?, WriteMappedInt?>(surface, sql);
                break;
            case 3:
                Read<ResultInt?, PgAnyElement?, WriteMappedInt?>(surface, sql);
                break;
            default:
                Read<ResultInt?, PgAnyElement?, WriteMappedInt[]?>(surface, sql);
                break;
        }

        return -1;
    }

    /// <summary>
    /// Reports independent lazy-construction and present-read counters.
    /// </summary>
    /// <returns>Integer factory/reads, text factory/reads, domain reads, and failing factory calls.</returns>
    [PgFunction(Name = "counts")]
    public static string Counts() => string.Create(CultureInfo.InvariantCulture,
        $"{ResultIntConverter.Constructions}|{ResultIntConverter.Reads}|{ResultTextConverter.Constructions}|{ResultTextConverter.Reads}|{ResultPositiveConverter.Reads}|{ResultFactoryConverter.Constructions}");

    /// <summary>
    /// Tests the checked input handle after its result owner has been disposed.
    /// </summary>
    /// <param name="text">Whether to inspect the last text rather than integer input.</param>
    /// <returns>False only when the captured present input has expired.</returns>
    [PgFunction(Name = "captured_alive")]
    public static bool CapturedAlive(bool text)
    {
        PgDatum value = (text ? ResultTextConverter.Captured : ResultIntConverter.Captured)
            ?? throw new InvalidOperationException("No present result was captured.");
        try
        {
            _ = value.DangerousGetBits();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    /// <summary>
    /// Calls an exact mapped domain through name or OID and omitted, explicit-default, or present arguments.
    /// </summary>
    /// <param name="name">The catalog function name.</param>
    /// <param name="oid">Zero for name lookup, or the exact function OID.</param>
    /// <param name="argument">Omitted arguments, explicit default, or the integer eleven.</param>
    /// <returns>The domain value or SQL NULL.</returns>
    [PgFunction(Name = "call_positive")]
    public static int? CallPositive(string name, uint oid, int argument)
        => Call<ResultPositive?>(name, oid, argument)?.Value;

    /// <summary>
    /// Exercises a reader-only base result through the catalog path and captures temporary lifetime.
    /// </summary>
    /// <param name="name">The zero-argument target.</param>
    /// <param name="oid">Zero for name lookup.</param>
    /// <returns>The exact copied integer or SQL NULL.</returns>
    [PgFunction(Name = "call_integer")]
    public static int? CallInteger(string name, uint oid) => Call<ResultInt?>(name, oid, 0)?.Value;

    /// <summary>
    /// Keeps ordinary domain-to-base catalog conversion in the compatibility mode.
    /// </summary>
    /// <param name="name">The defaulted domain-returning target.</param>
    /// <param name="oid">Zero for name lookup.</param>
    /// <returns>The underlying integer.</returns>
    [PgFunction(Name = "call_ordinary")]
    public static int CallOrdinary(string name, uint oid) => Call<int>(name, oid, 0);

    /// <summary>
    /// Rejects writer-only catalog results before executing a name- or OID-selected function.
    /// </summary>
    /// <param name="name">The side-effecting target.</param>
    /// <param name="oid">Zero for name lookup.</param>
    /// <returns>A sentinel only if read preflight incorrectly succeeds.</returns>
    [PgFunction(Name = "call_denied")]
    public static int CallDenied(string name, uint oid) => Call<WriteMappedInt?>(name, oid, 0)?.Value ?? -1;

    /// <summary>
    /// Preserves independent alias interpretation on catalog results without requiring a writer.
    /// </summary>
    /// <param name="name">The zero-argument target.</param>
    /// <param name="oid">Zero for name lookup.</param>
    /// <returns>The integer increased by one hundred.</returns>
    [PgFunction(Name = "call_alias")]
    public static int? CallAlias(string name, uint oid) => Call<ReadMappedInt?>(name, oid, 0)?.Value;

    /// <summary>
    /// Returns copied text after the catalog result owner's deletion.
    /// </summary>
    /// <param name="name">The zero-argument target.</param>
    /// <param name="oid">Zero for name lookup.</param>
    /// <returns>The complete detached text.</returns>
    [PgFunction(Name = "call_text")]
    public static string? CallText(string name, uint oid) => Call<ResultText?>(name, oid, 0)?.Value;

    /// <summary>
    /// Converts fixed-size catalog results before disposing their temporary storage.
    /// </summary>
    /// <param name="name">The zero-argument target.</param>
    /// <param name="oid">Zero for name lookup.</param>
    /// <returns>The exact two floating-point words.</returns>
    [PgFunction(Name = "call_complex")]
    public static string CallComplex(string name, uint oid)
    {
        MappedComplex value = Call<MappedComplex>(name, oid, 0);
        return string.Create(CultureInfo.InvariantCulture,
            $"{BitConverter.DoubleToInt64Bits(value.Real)}|{BitConverter.DoubleToInt64Bits(value.Imaginary)}");
    }

    /// <summary>
    /// Catches post-execution conversion errors so completed SQL effects remain observable to the caller.
    /// </summary>
    /// <param name="surface">The SPI owner selector.</param>
    /// <param name="kind">Reader, PostgreSQL factory, ordinary factory, identity, or mixed ordinary failure.</param>
    /// <param name="sql">The side-effecting result command.</param>
    /// <returns>The exact managed diagnostic without raising a new SQL error.</returns>
    [PgFunction(Name = "catch_query")]
    public static string CatchQuery(int surface, int kind, string sql)
    {
        try
        {
            switch (kind)
            {
                case 0:
                    Read<ResultText>(surface, sql);
                    break;
                case 1:
                    Read<ResultFactoryValue>(surface, sql);
                    break;
                case 2:
                    Read<ResultOrdinaryFactoryValue>(surface, sql);
                    break;
                case 3:
                    Read<ResultPositive?>(surface, sql);
                    break;
                default:
                    Read<ResultInt, int>(surface, sql);
                    break;
            }

            return "unexpected success";
        }
        catch (PgException error)
        {
            return $"{error.SqlState}|{error.Message}|{error.Detail}|{error.Hint}";
        }
        catch (Exception error) when (error is FormatException or InvalidCastException or InvalidOperationException)
        {
            return $"{error.GetType().Name}|{error.Message}";
        }
    }

    /// <summary>
    /// Surfaces lazy factory diagnostics through the catalog path after successful execution.
    /// </summary>
    /// <param name="name">The zero-argument target.</param>
    /// <param name="oid">Zero for name lookup.</param>
    /// <param name="ordinary">Whether to select an ordinary constructor exception.</param>
    /// <returns>A sentinel only if the failing factory incorrectly succeeds.</returns>
    [PgFunction(Name = "call_factory")]
    public static int CallFactory(string name, uint oid, bool ordinary)
        => ordinary ? Call<ResultOrdinaryFactoryValue>(name, oid, 0).Value : Call<ResultFactoryValue>(name, oid, 0).Value;

    /// <summary>
    /// Catches catalog result conversion failures after the callee has completed its guarded SQL work.
    /// </summary>
    /// <param name="name">The side-effecting target.</param>
    /// <param name="oid">Zero for name lookup, or the catalog identity.</param>
    /// <param name="factory">Whether to select a failing factory instead of a failing text reader.</param>
    /// <returns>The exact managed PostgreSQL diagnostic without raising a new SQL error.</returns>
    [PgFunction(Name = "catch_call")]
    public static string CatchCall(string name, uint oid, bool factory)
    {
        try
        {
            if (factory)
            {
                Call<ResultFactoryValue>(name, oid, 0);
            }
            else
            {
                Call<ResultText>(name, oid, 0);
            }

            return "unexpected success";
        }
        catch (PgException error)
        {
            return $"{error.SqlState}|{error.Message}|{error.Detail}|{error.Hint}";
        }
    }

    /// <summary>
    /// Resolves the current external type on every typed query or catalog conversion.
    /// </summary>
    /// <param name="catalog">Whether to call the temporary catalog function.</param>
    /// <returns>The current external value.</returns>
    [PgFunction(Name = "live")]
    public static int Live(bool catalog) => catalog
        ? PgFunctions.Call<ResultLive>("pg_temp.live_result").Value
        : Spi.ExecuteScalar<ResultLive>("SELECT 42::mapped_result_live.value").Value;

    /// <summary>
    /// Reads a canonical row or tuple cell through its mapped reader.
    /// </summary>
    /// <param name="tuple">Whether to read a heap tuple rather than an untyped SPI row.</param>
    /// <returns>The value the reader returned.</returns>
    [PgFunction(Name = "ordinary_read")]
    public static int OrdinaryRead(bool tuple) => tuple
        ? PgHeapTuple.Create(("value", SpiParameter.Create(42))).Get<ResultInt>(0).Value
        : Spi.Query("SELECT 42")[0].Get<ResultInt>(0).Value;

    /// <summary>
    /// Formats absence separately from the exact integer zero.
    /// </summary>
    private static string Number(int? value) => value?.ToString(CultureInfo.InvariantCulture) ?? "<NULL>";

    /// <summary>
    /// Dispatches the same requested scalar type through independent owner entry points.
    /// </summary>
    private static T Read<T>(int surface, string sql)
    {
        if (surface == 0)
        {
            return Spi.ExecuteScalar<T>(sql);
        }

        if (surface == 1)
        {
            return Spi.Connect(session => session.ExecuteScalar<T>(sql));
        }

        if (surface == 2)
        {
            using SpiPreparedStatement plan = Spi.Connect(session => session.Prepare(sql).Keep());
            return plan.ExecuteScalar<T>();
        }

        return Spi.Connect(session =>
        {
            using SpiPreparedStatement plan = session.Prepare(sql);
            return plan.ExecuteScalar<T>();
        });
    }

    /// <summary>
    /// Dispatches requested pair conversions without weakening eager per-slot checks.
    /// </summary>
    private static (TFirst First, TSecond Second) Read<TFirst, TSecond>(int surface, string sql)
    {
        if (surface == 0)
        {
            return Spi.ExecuteScalars<TFirst, TSecond>(sql);
        }

        if (surface == 1)
        {
            return Spi.Connect(session => session.ExecuteScalars<TFirst, TSecond>(sql));
        }

        if (surface == 2)
        {
            using SpiPreparedStatement plan = Spi.Connect(session => session.Prepare(sql).Keep());
            return plan.ExecuteScalars<TFirst, TSecond>();
        }

        return Spi.Connect(session =>
        {
            using SpiPreparedStatement plan = session.Prepare(sql);
            return plan.ExecuteScalars<TFirst, TSecond>();
        });
    }

    /// <summary>
    /// Dispatches requested triple conversions while allowing independent managed and native lifetimes.
    /// </summary>
    private static (TFirst First, TSecond Second, TThird Third) Read<TFirst, TSecond, TThird>(int surface, string sql)
    {
        if (surface == 0)
        {
            return Spi.ExecuteScalars<TFirst, TSecond, TThird>(sql);
        }

        if (surface == 1)
        {
            return Spi.Connect(session => session.ExecuteScalars<TFirst, TSecond, TThird>(sql));
        }

        if (surface == 2)
        {
            using SpiPreparedStatement plan = Spi.Connect(session => session.Prepare(sql).Keep());
            return plan.ExecuteScalars<TFirst, TSecond, TThird>();
        }

        return Spi.Connect(session =>
        {
            using SpiPreparedStatement plan = session.Prepare(sql);
            return plan.ExecuteScalars<TFirst, TSecond, TThird>();
        });
    }

    /// <summary>
    /// Selects name/OID lookup independently from explicit or omitted argument defaults.
    /// </summary>
    private static T Call<T>(string name, uint oid, int argument)
    {
        if (argument == 0)
        {
            return oid == 0 ? PgFunctions.Call<T>(name) : PgFunctions.Call<T>(oid);
        }

        PgFunctionArgument value = argument == 1 ? PgFunctionArgument.Default<int>() : PgFunctionArgument.Create(11);
        return oid == 0 ? PgFunctions.Call<T>(name, value) : PgFunctions.Call<T>(oid, value);
    }
}
