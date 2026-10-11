namespace Ankus.TestExtension;

/// <summary>
/// Exercises array dimensions, element conversions, and each native ownership path.
/// </summary>
public static class ArrayFunctions
{
    /// <summary>
    /// Exchanges integer arrays, including SQL NULL and NULL elements.
    /// </summary>
    [PgFunction]
    public static PgArray<int?>? ArrayInt(PgArray<int?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Sums a copied array with checked arithmetic, as pgrx's <c>sum_array</c> traps an <c>i32</c> overflow.
    /// </summary>
    /// <param name="values">The integers; SQL NULL elements count as zero.</param>
    /// <returns>The sum.</returns>
    [PgFunction]
    public static int SumArrayChecked(int?[] values)
    {
        int sum = 0;
        foreach (int? value in values)
        {
            sum = checked(sum + (value ?? 0));
        }

        return sum;
    }

    /// <summary>
    /// Sums a borrowed array with checked arithmetic, as pgrx's borrowed <c>sum_array</c> does.
    /// </summary>
    /// <param name="values">The integers; SQL NULL elements count as zero.</param>
    /// <returns>The sum.</returns>
    [PgFunction]
    public static int SumArrayViewChecked(PgArrayView<int?> values)
    {
        int sum = 0;
        foreach (int? value in values)
        {
            sum = checked(sum + (value ?? 0));
        }

        return sum;
    }

    /// <summary>
    /// Exchanges Boolean arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<bool?>? ArrayBool(PgArray<bool?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges signed-byte arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<sbyte?>? ArrayChar(PgArray<sbyte?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges small integer arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<short?>? ArrayShort(PgArray<short?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges big integer arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<long?>? ArrayLong(PgArray<long?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges OID arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<uint?>? ArrayOid(PgArray<uint?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges PostgreSQL transaction ID arrays without treating their elements as OIDs.
    /// </summary>
    [PgFunction]
    public static PgArray<PgTransactionId?>? ArrayXid(PgArray<PgTransactionId?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges single-precision arrays with exact IEEE bits.
    /// </summary>
    [PgFunction]
    public static PgArray<float?>? ArrayFloat(PgArray<float?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges double-precision arrays with exact IEEE bits.
    /// </summary>
    [PgFunction]
    public static PgArray<double?>? ArrayDouble(PgArray<double?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges owned text arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<string?>? ArrayText(PgArray<string?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges binary arrays without confusing them with scalar bytea.
    /// </summary>
    [PgFunction]
    public static PgArray<byte[]?>? ArrayBytes(PgArray<byte[]?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges UUID arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<Guid?>? ArrayUuid(PgArray<Guid?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges exact JSON text arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<PgJson?>? ArrayJson(PgArray<PgJson?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges normalized JSONB arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<PgJsonb?>? ArrayJsonb(PgArray<PgJsonb?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges full-range numeric arrays without narrowing to decimal.
    /// </summary>
    [PgFunction]
    public static PgArray<PgNumeric?>? ArrayNumeric(PgArray<PgNumeric?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges full-range date arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<PgDate?>? ArrayDate(PgArray<PgDate?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges wall-clock time arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<PgTime?>? ArrayTime(PgArray<PgTime?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges fixed-offset time arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<PgTimeTz?>? ArrayTimetz(PgArray<PgTimeTz?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges wall-clock timestamp arrays.
    /// </summary>
    [PgFunction]
    public static PgArray<PgTimestamp?>? ArrayTimestamp(PgArray<PgTimestamp?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges timestamp arrays with timezone.
    /// </summary>
    [PgFunction]
    public static PgArray<PgTimestampTz?>? ArrayTimestamptz(PgArray<PgTimestampTz?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges intervals retaining separate months, days, microseconds, and infinity.
    /// </summary>
    [PgFunction]
    public static PgArray<PgInterval?>? ArrayInterval(PgArray<PgInterval?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exchanges ordinary integer vectors and rejects shape loss.
    /// </summary>
    [PgFunction]
    public static int?[]? ArrayVector(int?[]? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Rejects NULL elements for non-nullable vector elements.
    /// </summary>
    [PgFunction]
    public static int[] ArrayRequired(int[] value) => value;

    /// <summary>
    /// Uses ordinary C# params to declare a SQL variadic array parameter.
    /// </summary>
    [PgFunction]
    public static int[] ArrayVariadic(int start, params int?[] values)
        => [start, values.Length, values.Count(static value => value is null), values.Sum(static value => value ?? 0)];

    /// <summary>
    /// Distinguishes a NULL variadic array from an array containing NULL.
    /// </summary>
    [PgFunction]
    public static string ArrayVariadicNullable(params string?[]? values)
        => values is null ? "null array" : string.Join('|', values.Select(static value => value ?? "null element"));

    /// <summary>
    /// Builds binary arrays with embedded zero bytes independently of native array input.
    /// </summary>
    [PgFunction]
    public static byte[]?[] ArrayBinaryConstruct() => [[0, 1, 2, 255], [3, 0, 4], [], null];

    /// <summary>
    /// Returns a later untranslatable element to exercise native output cleanup.
    /// </summary>
    [PgFunction]
    public static string[] ArrayUntranslatable() => ["café", "🐘"];

    /// <summary>
    /// Exercises nullable binary vectors, whose elements are scalar bytea values.
    /// </summary>
    [PgFunction]
    public static byte[]?[]? ArrayByteVectors(byte[]?[]? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exercises exact decimal vector narrowing.
    /// </summary>
    [PgFunction]
    public static decimal?[]? ArrayDecimal(decimal?[]? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exercises the ordinary .NET date array adapter.
    /// </summary>
    [PgFunction]
    public static DateOnly?[]? ArrayDateOnly(DateOnly?[]? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exercises the ordinary .NET time array adapter.
    /// </summary>
    [PgFunction]
    public static TimeOnly?[]? ArrayTimeOnly(TimeOnly?[]? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exercises the ordinary .NET timestamp array adapter.
    /// </summary>
    [PgFunction]
    public static DateTime?[]? ArrayDateTime(DateTime?[]? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exercises the ordinary .NET instant array adapter.
    /// </summary>
    [PgFunction]
    public static DateTimeOffset?[]? ArrayDateTimeOffset(DateTimeOffset?[]? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Exercises the ordinary .NET interval array adapter.
    /// </summary>
    [PgFunction]
    public static TimeSpan?[]? ArrayTimeSpan(TimeSpan?[]? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Returns a text array unchanged, so its result crosses as lengths and bytes and is laid out in one pass.
    /// </summary>
    [PgFunction]
    public static string?[] ArrayTextEcho(string?[] values) => values;

    /// <summary>
    /// Reads domain and text-alias arrays through SPI while retaining ownership after later calls.
    /// </summary>
    [PgFunction]
    public static PgArray<string?> ArrayTextQuery(string query)
    {
        PgArray<string?> value = Spi.Query(query)[0].Get<PgArray<string?>>(0);
        Spi.Execute("SELECT repeat('replacement', 10000)");
        return value;
    }

    /// <summary>
    /// Independently constructs a shaped array in managed code.
    /// </summary>
    [PgFunction]
    public static PgArray<int?> ArrayConstruct() => new([11, null, -7, 0, int.MaxValue, int.MinValue], [2, 3], [-2, 4]);

    /// <summary>
    /// Exposes shape and indexing independently of the native writer.
    /// </summary>
    [PgFunction]
    public static string ArrayInspect(PgArray<int?> value)
        => $"{value.Rank}:{value.Count}:{string.Join(',', value.Lengths.ToArray())}:{string.Join(',', value.LowerBounds.ToArray())}:{value[2]}:{value.GetValue(-1, 6)}";

    /// <summary>
    /// Catches native and managed array failures without losing prior writes or a prepared plan.
    /// </summary>
    [PgFunction]
    public static string ArrayRecover()
        => Spi.Connect(session =>
        {
            session.Execute("CREATE TEMP TABLE array_writes(value int); INSERT INTO array_writes VALUES(1)");
            using SpiPreparedStatement plan = session.Prepare("SELECT $1", typeof(PgArray<int?>));
            int failures = 0;
            int unwound = 0;
            const string contexts = "SELECT count(*) FROM ankus_test_memory.contexts WHERE name IN ('Ankus SPI operation', 'CurTransactionContext')";
            long before = session.ExecuteScalar<long>(contexts);
            for (int index = 0; index < 30; index++)
            {
                try
                {
                    session.Execute("SELECT $1", SpiParameter.Create(new PgArray<PgJsonb>([new("{\"ok\":1}"), new("\"\\u0000\"")])));
                }
                catch (PgException error) when (error.SqlState == "22P05")
                {
                    failures++;
                }
                finally
                {
                    unwound++;
                }

                try
                {
                    _ = session.ExecuteScalar<int[]>("SELECT ARRAY[1,NULL,3]");
                }
                catch (InvalidOperationException)
                {
                    failures++;
                }
            }

            long growth = session.ExecuteScalar<long>(contexts) - before;
            PgArray<int?> retained = plan.ExecuteScalar<PgArray<int?>>(SpiParameter.Create(new PgArray<int?>([9, null, 4])));
            return $"{failures}:{unwound}:{session.ExecuteScalar<long>("SELECT count(*) FROM array_writes")}:{retained[2]}:{growth}";
        });

    /// <summary>
    /// Exchanges a scalar or array through each SPI ownership path for backend transport probes.
    /// </summary>
    /// <typeparam name="T">The supported value type.</typeparam>
    /// <param name="value">The input.</param>
    /// <param name="mode">The direct, query, plan, session, cursor, retained-plan or edited-row path.</param>
    /// <returns>The detached round-tripped value.</returns>
    internal static T Exchange<T>(T value, int mode)
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
                    using SpiCursor cursor = plan.OpenCursor(SpiParameter.Create(value));
                    return cursor.Fetch(1)[0].Get<T>(0);
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
