using System.Globalization;

namespace Ankus.TestExtension;

/// <summary>
/// Exercises closed mapped arrays through generated callbacks, raw owners, and typed backend operations.
/// </summary>
[PgSchema("mapped_arrays")]
public static class MappedArrayFunctions
{
    private static readonly int[] s_ordinaryValues = [7];
    private static int s_disposals;
    private static SpiParameter s_saved;

    /// <summary>
    /// Reports independent lazy construction and present element calls.
    /// </summary>
    [PgFunction(Name = "counts")]
    public static string Counts() => string.Create(CultureInfo.InvariantCulture,
        $"{ArrayValueConverter.Constructions}|{ArrayValueConverter.Reads}|{ArrayValueConverter.Writes}");

    /// <summary>
    /// Clears operation counters and escaped handles without resetting converter construction.
    /// </summary>
    [PgFunction(Name = "reset")]
    public static void Reset()
    {
        ArrayValueConverter.Reset();
        ArrayTextConverter.Inputs.Clear();
        ArrayTextConverter.Destinations.Clear();
    }

    /// <summary>
    /// Reports every captured input and destination after an operation has ended.
    /// </summary>
    [PgFunction(Name = "owners")]
    public static string Owners()
    {
        int inputs = ArrayValueConverter.Inputs.Count + ArrayTextConverter.Inputs.Count;
        int live = ArrayValueConverter.Inputs.Concat(ArrayTextConverter.Inputs).Count(IsLive);
        int destinations = ArrayValueConverter.Destinations.Count + ArrayTextConverter.Destinations.Count;
        int alive = ArrayValueConverter.Destinations.Concat(ArrayTextConverter.Destinations).Count(static owner => owner.IsAlive);
        return string.Create(CultureInfo.InvariantCulture, $"{inputs}|{live}|{destinations}|{alive}");
    }

    /// <summary>
    /// Preserves manual U24 arrays through independent typed ownership paths.
    /// </summary>
    [PgFunction(Name = "u24")]
    public static PgArray<MappedU24?>? U24(PgArray<MappedU24?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Gives the same SQL array a different declared managed element interpretation.
    /// </summary>
    [PgFunction(Name = "alias")]
    public static string Alias(PgArray<MappedU24Alias?> value)
        => string.Join('|', value.Select(static item => item is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : "NULL"));

    /// <summary>
    /// Constructs alias output independently, including a value beyond the primary wrapper's valid maximum.
    /// </summary>
    [PgFunction(Name = "alias_construct")]
    public static MappedU24Alias?[] AliasConstruct() => [new(1), new(43), null, new(16777216)];

    /// <summary>
    /// Concatenates two independently mapped array operands through a generated manual operator.
    /// </summary>
    [PgFunction(Name = "concatenate")]
    [PgOperator("##")]
    public static PgArray<MappedU24?> Concatenate(PgArray<MappedU24?> left, PgArray<MappedU24?> right)
        => new([.. left, .. right]);

    /// <summary>
    /// Gives a mapped array a distinct explicit scalar cast without changing ordinary array text formatting.
    /// </summary>
    [PgFunction(Name = "total")]
    [PgCast]
    public static long Total(PgArray<MappedU24?> value) => value.Sum(static item => item is { } present ? present.Value : 0);

    /// <summary>
    /// Copies dedicated nullable integer arrays through generated input and output.
    /// </summary>
    [PgFunction(Name = "values")]
    public static PgArray<ArrayValue?>? Values(PgArray<ArrayValue?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Preserves fixed-size by-reference fields and SQL NULL cells.
    /// </summary>
    [PgFunction(Name = "complex")]
    public static PgArray<MappedComplex?>? Complex(PgArray<MappedComplex?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Preserves large text cells without retaining native source memory.
    /// </summary>
    [PgFunction(Name = "text")]
    public static PgArray<ArrayText?>? Text(PgArray<ArrayText?>? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Retains every mapped enum value, including unnamed underlying numbers.
    /// </summary>
    [PgFunction(Name = "sign")]
    public static MappedSign?[]? Sign(MappedSign?[]? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Distinguishes a byte-backed mapped enum vector from scalar binary bytes.
    /// </summary>
    [PgFunction(Name = "byte_enum")]
    public static ArrayByte?[]? ByteEnum(ArrayByte?[]? value, int mode) => Exchange(value, mode);

    /// <summary>
    /// Produces shape and values independently of a PostgreSQL input array.
    /// </summary>
    [PgFunction(Name = "construct")]
    public static PgArray<ArrayValue?> Construct()
        => new([new(11), null, new(-7), new(0), new(5), new(9)], [2, 3], [-2, 4]);

    /// <summary>
    /// Describes exact managed shape and PostgreSQL subscripting after generated input conversion.
    /// </summary>
    [PgFunction(Name = "inspect")]
    public static string Inspect(PgArray<ArrayValue?>? value) => Describe(value);

    /// <summary>
    /// Uses the same lazy converter directly through its scalar registration.
    /// </summary>
    [PgFunction(Name = "scalar")]
    public static int Scalar(ArrayValue value) => value.Value;

    /// <summary>
    /// Reads required or optional elements through vector or shaped raw target adapters.
    /// </summary>
    [PgFunction(Name = "raw")]
    public static string Raw(string sql, bool vector, bool required)
    {
        using SpiRawResult result = Spi.QueryRaw(sql);
        PgDatum datum = result[0][0];
        if (required)
        {
            ArrayValue[]? values = vector ? datum.Read<ArrayValue[]?>() : datum.Read<PgArray<ArrayValue>?>()?.ToArray();
            return values is null ? "NULL" : string.Join('|', values.Select(static item => item.Value.ToString(CultureInfo.InvariantCulture)));
        }

        if (vector)
        {
            ArrayValue?[]? values = datum.Read<ArrayValue?[]?>();
            return values is null ? "NULL" : Describe(new PgArray<ArrayValue?>(values));
        }

        return Describe(datum.Read<PgArray<ArrayValue?>?>());
    }

    /// <summary>
    /// Selects exact domain-array identity or an explicitly mapped outer-domain scalar.
    /// </summary>
    [PgFunction(Name = "positive")]
    public static string Positive(string sql, bool scalar)
    {
        using SpiRawResult result = Spi.QueryRaw(sql);
        if (scalar)
        {
            return result[0][0].Read<ArrayDomainScalar?>()?.Value ?? "NULL";
        }

        PgArray<MappedPositive?>? values = result[0][0].Read<PgArray<MappedPositive?>?>();
        return values is null ? "NULL" : string.Create(CultureInfo.InvariantCulture,
            $"{values.ElementTypeOid}|{values.Rank}|{values.Count}|{string.Join(',', values.Select(static item => item is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : "NULL"))}");
    }

    /// <summary>
    /// Violates only the public nominal tag while preserving real allocated same-layout array storage.
    /// </summary>
    [PgFunction(Name = "header")]
    public static string Header(int kind)
    {
        string sql = kind switch
        {
            0 => "SELECT ARRAY[7,11]::integer[]",
            1 => "SELECT ARRAY[]::integer[]",
            _ => "SELECT ARRAY[NULL,NULL]::integer[]",
        };
        using PgMemoryContext owner = PgMemoryContext.Create("mapped array header witness");
        using SpiRawResult raw = Spi.QueryRaw(sql);
        PgDatum original = raw[0][0].CopyTo(owner);
        uint expected = Spi.ExecuteScalar<uint>("SELECT typarray FROM pg_type WHERE oid='datum_mappings.positive'::regtype");
        PgDatum forged;
        unsafe
        {
            forged = PgDatum.DangerousCreate(original.DangerousGetBits(), expected, owner);
        }

        try
        {
            forged.Read<PgArray<ResultPositive?>>();
            return "unexpected success";
        }
        catch (PgException error)
        {
            return string.Create(CultureInfo.InvariantCulture,
                $"{error.SqlState}|{error.Message}|{original.ToPostgresString()}|{ResultPositiveConverter.Reads}|{owner.IsAlive}");
        }
    }

    /// <summary>
    /// Proves raw mapped extraction deletes only its temporary element owner, including later failures.
    /// </summary>
    [PgFunction(Name = "raw_owners")]
    public static string RawOwners(bool fail)
    {
        Reset();
        using SpiRawResult raw = Spi.QueryRaw(fail ? "SELECT ARRAY[7,-777,99]" : "SELECT ARRAY[7,NULL,11]");
        string diagnostic;
        try
        {
            PgArray<ArrayValue?> values = raw[0][0].Read<PgArray<ArrayValue?>>();
            diagnostic = Describe(values);
        }
        catch (PgException error)
        {
            diagnostic = $"{error.SqlState}|{error.Message}|{error.Detail}|{error.Hint}";
        }

        return $"{diagnostic}~{Counts()}~{Owners()}~{raw[0][0].ToPostgresString()}";
    }

    /// <summary>
    /// Exercises each first-row result width through static, session, retained-plan and session-plan paths.
    /// </summary>
    [PgFunction(Name = "query")]
    public static string Query(int surface, int width, string sql)
    {
        if (width == 1)
        {
            return Describe(Read<PgArray<ArrayValue?>?>(surface, sql));
        }

        if (width == 2)
        {
            (ReadMappedInt? first, PgArray<ArrayValue?>? values) = Read<ReadMappedInt?, PgArray<ArrayValue?>?>(surface, sql);
            return $"{(first is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : "NULL")}~{Describe(values)}";
        }

        if (width == 3)
        {
            (PgArray<ArrayValue?>? values, PgAnyElement? second, ResultText? third) = Read<PgArray<ArrayValue?>?, PgAnyElement?, ResultText?>(surface, sql);
            return $"{Describe(values)}~{second?.Datum.ToPostgresString() ?? "NULL"}~{third?.Value ?? "NULL"}";
        }

        (int? head, ResultText? text, ArrayValue?[]? tail) = Read<int?, ResultText?, ArrayValue?[]?>(surface, sql);
        return $"{head?.ToString(CultureInfo.InvariantCulture) ?? "NULL"}~{text?.Value ?? "NULL"}~{(tail is null ? "NULL" : Describe(new PgArray<ArrayValue?>(tail)))}";
    }

    /// <summary>
    /// Rejects unavailable result and parameter directions before a caller's SQL side effect.
    /// </summary>
    [PgFunction(Name = "denied")]
    public static int Denied(int surface, int width, int kind)
    {
        string array = kind switch
        {
            0 => "NULL::integer[]",
            1 => "ARRAY[]::integer[]",
            2 => "ARRAY[NULL]::integer[]",
            _ => "ARRAY[7]"
        };
        string sql = $"SELECT nextval('mapped_array_effect'), {array}, {array}";
        if (width == 1)
        {
            Read<WriteMappedInt?[]?>(surface, sql);
        }
        else if (width == 2)
        {
            Read<PgAnyElement?, PgArray<WriteMappedInt?>?>(surface, sql);
        }
        else if (width == 3)
        {
            Read<PgAnyElement?, PgArray<ArrayValue?>?, WriteMappedInt?[]?>(surface, sql);
        }
        else
        {
            ReadMappedInt?[]? values = kind switch
            {
                0 => null,
                1 => [],
                2 => [null],
                _ => [new(7)]
            };
            Spi.Execute("SELECT nextval('mapped_array_effect'),$1", SpiParameter.Create(values));
        }

        return -1;
    }

    /// <summary>
    /// Reads a genuinely read-only mapped array in a generated function.
    /// </summary>
    [PgFunction(Name = "read_only")]
    public static int ReadOnly(ReadMappedInt?[] values) => values.Sum(static value => value is { } present ? present.Value : 0);

    /// <summary>
    /// Writes a genuinely write-only mapped vector without attempting reverse conversion.
    /// </summary>
    [PgFunction(Name = "write_only")]
    public static WriteMappedInt?[]? WriteOnly(int kind) => kind switch
    {
        0 => [new(7), null, new(-3)],
        1 => [],
        _ => null,
    };

    /// <summary>
    /// Binds writer-only elements through each SPI owner while requesting ordinary integer results.
    /// </summary>
    [PgFunction(Name = "writer_parameter")]
    public static int?[]? WriterParameter(int surface, int kind)
    {
        SpiParameter parameter = SpiParameter.Create(WriteOnly(kind));
        if (surface == 0)
        {
            return Spi.ExecuteScalar<int?[]?>("SELECT $1", parameter);
        }

        if (surface == 1)
        {
            return Spi.Connect(session => session.ExecuteScalar<int?[]?>("SELECT $1", parameter));
        }

        if (surface == 2)
        {
            using SpiPreparedStatement plan = Spi.Connect(session => session.Prepare("SELECT $1", typeof(WriteMappedInt?[])).Keep());
            return plan.ExecuteScalar<int?[]?>(parameter);
        }

        return Spi.Connect(session =>
        {
            using SpiPreparedStatement plan = session.Prepare("SELECT $1", typeof(WriteMappedInt?[]));
            return plan.ExecuteScalar<int?[]?>(parameter);
        });
    }

    /// <summary>
    /// Uses type-only metadata/defaults for a mapping that intentionally has no writer.
    /// </summary>
    [PgFunction(Name = "default_call")]
    public static int DefaultCall(bool byOid)
    {
        PgFunctionArgument argument = PgFunctionArgument.Default<ReadMappedInt?[]>();
        return byOid
            ? PgFunctions.Call<int>(Spi.ExecuteScalar<uint>("SELECT 'pg_temp.mapped_array_default(integer[])'::regprocedure::oid"), argument)
            : PgFunctions.Call<int>("pg_temp.mapped_array_default", argument);
    }

    /// <summary>
    /// Constructs writer-produced NULL cells and invalid later outputs independently of SQL input.
    /// </summary>
    [PgFunction(Name = "writer")]
    public static ArrayValue?[] Writer(int mode) => [new(42), null, new(mode), new(7)];

    /// <summary>
    /// Invokes native NOT NULL and CHECK domain checks on every constructed element.
    /// </summary>
    [PgFunction(Name = "required_writer")]
    public static MappedRequired?[] RequiredWriter(bool managedNull)
        => [new(1), managedNull ? null : new(0)];

    /// <summary>
    /// Produces a later invalid CHECK-domain element while the first remains valid.
    /// </summary>
    [PgFunction(Name = "positive_writer")]
    public static MappedPositive[] PositiveWriter(int value) => [new(7), new(value)];

    /// <summary>
    /// Forces array construction and domain validation before the target SQL can execute.
    /// </summary>
    [PgFunction(Name = "write_before_sql")]
    public static void WriteBeforeSql(int mode)
        => Spi.Execute("SELECT nextval('mapped_array_effect'),$1", SpiParameter.Create<ArrayValue?[]>([new(7), new(mode)]));

    /// <summary>
    /// Returns independently owned text from a writer without consuming that source owner.
    /// </summary>
    [PgFunction(Name = "borrowed_writer")]
    public static string BorrowedWriter()
    {
        Reset();
        using PgMemoryContext owner = PgMemoryContext.Create("mapped array caller source");
        using SpiRawResult raw = Spi.QueryRaw("SELECT 'caller-owned'::text");
        PgDatum original = raw[0][0].CopyTo(owner);
        ArrayTextConverter.Borrowed = original;
        try
        {
            string?[] values = Spi.ExecuteScalar<string?[]>("SELECT $1", SpiParameter.Create<ArrayText?[]>([new("borrowed"), null, new("other")]));
            return $"{string.Join('|', values.Select(static item => item ?? "NULL"))}~{original.Read<string>()}~{Owners()}~{owner.IsAlive}";
        }
        finally
        {
            ArrayTextConverter.Borrowed = null;
        }
    }

    /// <summary>
    /// Rejects CLR enum-array compatibility and preserves declared reference covariance.
    /// </summary>
    [PgFunction(Name = "clr_identity")]
    public static string ClrIdentity(bool reject)
    {
        if (reject)
        {
            object values = new[] { -1, 0, 42 };
            MappedSign[] disguised = (MappedSign[])values;
            Spi.Execute("SELECT nextval('mapped_array_effect'),$1", SpiParameter.Create(disguised));
            return "unexpected success";
        }

        DerivedMappedText[] derived = [new("alpha"), new("beta")];
        MappedText[] source = derived;
        MappedText[] result = Spi.ExecuteScalar<MappedText[]>("SELECT $1", SpiParameter.Create<MappedText[]>(source));
        string before = string.Join('|', result.Select(static value => value.Value));
        result[0] = new MappedText("replacement");
        return $"{before}|{result[0].Value}|{source[0].Value}|{result.GetType() == typeof(MappedText[])}|{source.GetType() == typeof(DerivedMappedText[])}";
    }

    /// <summary>
    /// Calls a declared array result with an exact target and optional server-evaluated default.
    /// </summary>
    [PgFunction(Name = "call")]
    public static string Call(string name, uint oid, bool useDefault)
    {
        PgArray<ResultPositive?>? value;
        if (useDefault)
        {
            PgFunctionArgument argument = PgFunctionArgument.Default<int>();
            value = oid == 0 ? PgFunctions.Call<PgArray<ResultPositive?>?>(name, argument)
                : PgFunctions.Call<PgArray<ResultPositive?>?>(oid, argument);
        }
        else
        {
            value = oid == 0 ? PgFunctions.Call<PgArray<ResultPositive?>?>(name) : PgFunctions.Call<PgArray<ResultPositive?>?>(oid);
        }

        return value is null ? "NULL" : string.Join('|', value.Select(static item => item is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : "NULL"));
    }

    /// <summary>
    /// Catches post-execution array conversions without undoing completed SPI or catalog writes.
    /// </summary>
    [PgFunction(Name = "catch")]
    public static string Catch(string sql, uint oid, int kind)
    {
        try
        {
            if (kind == 0)
            {
                Spi.ExecuteScalar<ArrayValue[]>(sql);
            }
            else if (kind == 1)
            {
                Spi.ExecuteScalar<ResultFactoryValue[]>(sql);
            }
            else if (kind == 2)
            {
                ArrayValue[] result = oid == 0 ? PgFunctions.Call<ArrayValue[]>(sql) : PgFunctions.Call<ArrayValue[]>(oid);
                return string.Join('|', result.Select(static item => item.Value.ToString(CultureInfo.InvariantCulture)));
            }
            else if (kind == 4)
            {
                if (oid == 0)
                {
                    PgFunctions.Call<ResultFactoryValue[]>(sql);
                }
                else
                {
                    PgFunctions.Call<ResultFactoryValue[]>(oid);
                }
            }
            else
            {
                Spi.ExecuteScalars<PgArray<ArrayValue?>, int>(sql);
            }

            return "unexpected success";
        }
        catch (PgException error)
        {
            return $"{error.SqlState}|{error.Message}|{error.Detail}|{error.Hint}";
        }
        catch (Exception error) when (error is InvalidCastException or InvalidOperationException)
        {
            return $"{error.GetType().Name}|{error.Message}";
        }
    }

    /// <summary>
    /// Distinguishes a nullable variadic array from NULL cells and empty values.
    /// </summary>
    [PgFunction(Name = "variadic")]
    public static string Variadic(params ArrayValue?[]? values) => values is null ? "NULL" : string.Create(CultureInfo.InvariantCulture,
        $"{values.Sum(static value => value is { } present ? present.Value : 0)}|{values.Count(static value => value is not null)}");

    /// <summary>
    /// Delays all managed input inspection until after iterator initialization and nested SPI activity.
    /// </summary>
    [PgFunction(Name = "rows", SetMode = PgSetMode.ValuePerCall)]
    public static IEnumerable<PgArray<ArrayText?>?> Rows(PgArray<ArrayText?>? value, bool fail, int count = 4)
    {
        try
        {
            if (count == 0)
            {
                yield break;
            }

            _ = Spi.ExecuteScalar<int>("SELECT 73");
            yield return value;
            if (fail)
            {
                throw new PgException("P8525", "mapped array iterator failed");
            }

            if (count == 1)
            {
                yield break;
            }

            _ = Spi.ExecuteScalar<string>("SELECT repeat('disturb',1000)");
            yield return new PgArray<ArrayText?>([]);
            yield return null;
            yield return value;
        }
        finally
        {
            s_disposals++;
        }
    }

    /// <summary>
    /// Uses the independent materializing executor with the same delayed input contract.
    /// </summary>
    [PgFunction(Name = "materialized", SetMode = PgSetMode.Materialize)]
    public static IEnumerable<PgArray<ArrayText?>?> Materialized(PgArray<ArrayText?>? value, bool fail, int count = 4) => Rows(value, fail, count);

    /// <summary>
    /// Writes nullable mapped arrays into independent TABLE columns after delayed input conversion.
    /// </summary>
    [PgFunction(Name = "table")]
    public static IEnumerable<(PgArray<ArrayValue?>? Values, int Position)> Table(PgArray<ArrayValue?>? value)
    {
        _ = Spi.ExecuteScalar<int>("SELECT 17");
        yield return (value, 1);
        yield return (null, 2);
        yield return (new PgArray<ArrayValue?>([]), 3);
    }

    /// <summary>
    /// Materializes the same independent array and ordinal TABLE columns.
    /// </summary>
    [PgFunction(Name = "table_materialized", SetMode = PgSetMode.Materialize)]
    public static IEnumerable<(PgArray<ArrayValue?>? Values, int Position)> TableMaterialized(PgArray<ArrayValue?>? value) => Table(value);

    /// <summary>
    /// Reports completed iterator finally blocks.
    /// </summary>
    [PgFunction(Name = "disposals")]
    public static int Disposals() => s_disposals;

    /// <summary>
    /// Saves a parameter's exact array OID or binds a new parameter after external DDL.
    /// </summary>
    [PgFunction(Name = "live")]
    public static string Live(int kind, bool save)
    {
        ArrayLive?[]? values = kind switch
        {
            0 => [new(7), null, new(11)],
            1 => [],
            _ => null
        };
        SpiParameter parameter = SpiParameter.Create(values);
        if (save)
        {
            s_saved = parameter;
        }

        using SpiRawResult raw = Spi.QueryRaw("SELECT $1", parameter);
        PgArray<ArrayLive?>? result = raw[0][0].Read<PgArray<ArrayLive?>?>();
        return string.Create(CultureInfo.InvariantCulture,
            $"{parameter.TypeOid}|{(result is null ? "NULL" : string.Join(',', result.Select(static item => item is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : "NULL")))}|{ArrayLiveConverter.Writes}");
    }

    /// <summary>
    /// Refuses to rebind a saved identity before any target SQL or element writer executes.
    /// </summary>
    [PgFunction(Name = "saved")]
    public static void Saved() => Spi.Execute("SELECT nextval('mapped_array_effect'),$1", s_saved);

    /// <summary>
    /// Exposes live-mapping write count without resolving a dropped catalog type.
    /// </summary>
    [PgFunction(Name = "live_writes")]
    public static int LiveWrites() => ArrayLiveConverter.Writes;

    /// <summary>
    /// Selects a fresh external array identity for catalog results after type replacement.
    /// </summary>
    [PgFunction(Name = "live_call")]
    public static string LiveCall(uint oid)
    {
        ArrayLive?[]? result = oid == 0 ? PgFunctions.Call<ArrayLive?[]?>("pg_temp.mapped_array_live_result")
            : PgFunctions.Call<ArrayLive?[]?>(oid);
        return result is null ? "NULL" : string.Join(',', result.Select(static item => item is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : "NULL"));
    }

    /// <summary>
    /// Reads a canonical row or tuple array through its mapped element reader.
    /// </summary>
    [PgFunction(Name = "ordinary_read")]
    public static int OrdinaryRead(bool tuple) => tuple
        ? PgHeapTuple.Create(("value", SpiParameter.Create(s_ordinaryValues))).Get<PgArray<ArrayValue>>(0).Count
        : Spi.Query("SELECT ARRAY[7]")[0].Get<ArrayValue[]>(0).Length;

    /// <summary>
    /// Describes rank, dimensions, bounds and every value independently of SQL text formatting.
    /// </summary>
    internal static string Describe(PgArray<ArrayValue?>? value) => value is null ? "NULL" : string.Create(CultureInfo.InvariantCulture,
        $"{value.ElementTypeOid}|{value.Rank}|{value.Count}|{string.Join(',', value.Lengths.ToArray())}|{string.Join(',', value.LowerBounds.ToArray())}|{string.Join(',', value.Select(static item => item is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : "NULL"))}");

    /// <summary>
    /// Reads retained text and its original shape during later aggregate callbacks.
    /// </summary>
    internal static string DescribeText(PgArray<ArrayText?>? value) => value is null ? "NULL" :
        $"{string.Join(',', value.LowerBounds.ToArray())}:{string.Join(',', value.Select(static item => item?.Value ?? "NULL"))}";

    /// <summary>
    /// Validates a retained checked handle without dereferencing expired memory.
    /// </summary>
    private static bool IsLive(PgDatum value)
    {
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
    /// Exchanges mapped arrays only through target-directed result APIs.
    /// </summary>
    private static T Exchange<T>(T value, int mode)
    {
        if (mode == 0)
        {
            return value;
        }

        SpiParameter parameter = SpiParameter.Create(value);
        if (mode == 1)
        {
            return Spi.ExecuteScalar<T>("SELECT $1", parameter);
        }

        if (mode == 2)
        {
            return Spi.Connect(session => session.ExecuteScalar<T>("SELECT $1", parameter));
        }

        if (mode == 3)
        {
            using SpiPreparedStatement plan = Spi.Connect(session => session.Prepare("SELECT $1", typeof(T)).Keep());
            return plan.ExecuteScalar<T>(parameter);
        }

        if (mode == 4)
        {
            return Spi.Connect(session =>
            {
                using SpiPreparedStatement plan = session.Prepare("SELECT $1", typeof(T));
                return plan.ExecuteScalar<T>(parameter);
            });
        }

        if (mode == 5)
        {
            using SpiRawResult result = Spi.QueryRaw("SELECT $1", parameter);
            return result[0][0].Read<T>();
        }

        using SpiCursor cursor = Spi.OpenCursor("SELECT $1", parameter);
        using SpiRawResult batch = cursor.FetchRaw(1);
        T copy = batch[0][0].Read<T>();
        using SpiRawResult empty = cursor.FetchRaw(1);
        return empty.Count == 0 ? copy : throw new InvalidOperationException("The single-row cursor returned an extra row.");
    }

    /// <summary>
    /// Reads a selected scalar under one of four independent SPI owner paths.
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
    /// Reads two independently selected result contracts.
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
    /// Reads three independently selected result contracts with eager capability validation.
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
}

/// <summary>
/// Retains detached reference vectors independently of shaped aggregate arguments.
/// </summary>
[PgAggregate(Name = "retain_vector", Schema = "mapped_arrays")]
public sealed class MappedArrayRetainVectorAggregate : IPgAggregate<PgAggregateState<List<ArrayText?[]?>>?, ArrayText?[]?>,
    IPgFinalizingAggregate<PgAggregateState<List<ArrayText?[]?>>?, ValueTuple, string>
{
    /// <summary>
    /// Rechecks all previous vectors in later transitions before retaining the next input.
    /// </summary>
    public static PgAggregateState<List<ArrayText?[]?>> Transition(PgAggregateContext context,
        PgAggregateState<List<ArrayText?[]?>>? state, ArrayText?[]? value)
    {
        state ??= new([]);
        foreach (ArrayText?[]? previous in state.Value)
        {
            _ = Describe(previous);
        }

        state.Value.Add(value);
        return state;
    }

    /// <summary>
    /// Distinguishes every retained vector, NULL cell, empty vector and whole SQL NULL at finalization.
    /// </summary>
    public static string Final(PgAggregateContext context, PgAggregateState<List<ArrayText?[]?>>? state, ValueTuple arguments)
        => state is null ? "empty" : string.Join(';', state.Value.Select(Describe));

    /// <summary>
    /// Reads the complete retained managed reference values.
    /// </summary>
    private static string Describe(ArrayText?[]? value)
        => value is null ? "NULL" : string.Join(',', value.Select(static item => item?.Value ?? "NULL"));
}

/// <summary>
/// Retains detached shaped text arrays across aggregate transitions and finalization.
/// </summary>
[PgAggregate(Name = "retain", Schema = "mapped_arrays")]
public sealed class MappedArrayRetainAggregate : IPgAggregate<PgAggregateState<List<PgArray<ArrayText?>?>>?, PgArray<ArrayText?>?>,
    IPgFinalizingAggregate<PgAggregateState<List<PgArray<ArrayText?>?>>?, ValueTuple, string>
{
    /// <summary>
    /// Rechecks every previous array after its original callback storage has ended.
    /// </summary>
    public static PgAggregateState<List<PgArray<ArrayText?>?>> Transition(
        PgAggregateContext context, PgAggregateState<List<PgArray<ArrayText?>?>>? state, PgArray<ArrayText?>? value)
    {
        state ??= new([]);
        foreach (PgArray<ArrayText?>? previous in state.Value)
        {
            _ = MappedArrayFunctions.DescribeText(previous);
        }

        state.Value.Add(value);
        return state;
    }

    /// <summary>
    /// Reads retained text, SQL NULL, empty arrays, and nondefault bounds independently.
    /// </summary>
    public static string Final(PgAggregateContext context, PgAggregateState<List<PgArray<ArrayText?>?>>? state, ValueTuple arguments)
        => state is null ? "empty" : string.Join(';', state.Value.Select(MappedArrayFunctions.DescribeText));
}

/// <summary>
/// Uses a mapped array as ordinary and moving aggregate state and output.
/// </summary>
[PgAggregate(Name = "collect", Schema = "mapped_arrays", InitialCondition = "{}", MovingInitialCondition = "{}")]
public sealed class MappedArrayCollectAggregate : IPgAggregate<PgArray<ArrayValue?>, ArrayValue?>,
    IPgFinalizingAggregate<PgArray<ArrayValue?>, ValueTuple, PgArray<ArrayValue?>>,
    IPgCombinableAggregate<PgArray<ArrayValue?>>, IPgMovingAggregate<PgArray<ArrayValue?>, ArrayValue?>,
    IPgMovingFinalizingAggregate<PgArray<ArrayValue?>, ValueTuple, PgArray<ArrayValue?>>
{
    /// <summary>
    /// Appends one mapped scalar without collapsing a NULL element.
    /// </summary>
    public static PgArray<ArrayValue?> Transition(PgAggregateContext context, PgArray<ArrayValue?> state, ArrayValue? value)
        => new([.. state, value]);

    /// <summary>
    /// Concatenates independently decoded mapped array states.
    /// </summary>
    public static PgArray<ArrayValue?> Combine(PgAggregateContext context, PgArray<ArrayValue?> state, PgArray<ArrayValue?> other)
        => new([.. state, .. other]);

    /// <summary>
    /// Returns the complete ordinary state as a mapped array result.
    /// </summary>
    public static PgArray<ArrayValue?> Final(PgAggregateContext context, PgArray<ArrayValue?> state, ValueTuple arguments) => state;

    /// <summary>
    /// Appends a row under the moving aggregate transport.
    /// </summary>
    public static PgArray<ArrayValue?> MovingTransition(PgAggregateContext context, PgArray<ArrayValue?> state, ArrayValue? value)
        => Transition(context, state, value);

    /// <summary>
    /// Removes precisely the outgoing first row, including a NULL value.
    /// </summary>
    public static PgArray<ArrayValue?> MovingInverse(PgAggregateContext context, PgArray<ArrayValue?> state, ArrayValue? value)
    {
        if (state[0] != value)
        {
            throw new InvalidOperationException("Mapped moving inverse received a different outgoing value.");
        }

        return new(state.Skip(1));
    }

    /// <summary>
    /// Returns mapped array state under the moving final helper.
    /// </summary>
    public static PgArray<ArrayValue?> MovingFinal(PgAggregateContext context, PgArray<ArrayValue?> state, ValueTuple arguments) => state;
}
