namespace Ankus.TestExtension;

/// <summary>
/// Gives each explicitly listed closed construction its own exact SQL storage contract.
/// </summary>
/// <param name="Number">The detached logical number.</param>
[PgDatumType(typeof(ExplicitBox<int>), "int4", typeof(ExplicitIntConverter), Origin = PgTypeOrigin.External, Schema = "pg_catalog")]
[PgDatumType(typeof(ExplicitBox<long>), "int8", typeof(ExplicitLongConverter), Origin = PgTypeOrigin.External, Schema = "pg_catalog")]
[PgDatumType(typeof(ExplicitBox<short>), "int2", typeof(ExplicitShortConverter), Origin = PgTypeOrigin.External, Schema = "pg_catalog")]
public readonly record struct ExplicitBox<T>(T Number);

/// <summary>
/// Observes lazy selection and native input lifetime independently of the generic mapped type.
/// </summary>
internal static class ExplicitMappingState
{
    /// <summary>
    /// Gets or sets the count of all lazily selected converter instances.
    /// </summary>
    internal static int Constructions
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the count of int4 reads.
    /// </summary>
    internal static int IntReads
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the count of int8 reads.
    /// </summary>
    internal static int LongReads
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets the count of metadata-only int2 reads.
    /// </summary>
    internal static int ShortReads
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets a checked handle without extending its native lifetime.
    /// </summary>
    internal static PgDatum? Captured
    {
        get;
        set;
    }
}

/// <summary>
/// Flips an int4 bit so direct reader and writer expectations cannot rely only on a round trip.
/// </summary>
public sealed class ExplicitIntConverter : IPgDatumReader<ExplicitBox<int>>, IPgDatumWriter<ExplicitBox<int>>
{
    /// <summary>
    /// Records lazy construction.
    /// </summary>
    public ExplicitIntConverter() => ExplicitMappingState.Constructions++;

    /// <inheritdoc />
    public ExplicitBox<int> Read(PgDatum value)
    {
        ExplicitMappingState.IntReads++;
        ExplicitMappingState.Captured = value;
        return new(value.Read<int>() ^ 0x40000000);
    }

    /// <inheritdoc />
    public PgDatum Write(ExplicitBox<int> value, uint typeOid, PgMemoryContext destination)
    {
        unsafe
        {
            return PgDatum.DangerousCreate(unchecked((nuint)(nint)(value.Number ^ 0x40000000)), typeOid, destination);
        }
    }
}

/// <summary>
/// Preserves every int8 bit while exposing an independent logical interpretation.
/// </summary>
public sealed class ExplicitLongConverter : IPgDatumReader<ExplicitBox<long>>, IPgDatumWriter<ExplicitBox<long>>
{
    /// <summary>
    /// Records lazy construction.
    /// </summary>
    public ExplicitLongConverter() => ExplicitMappingState.Constructions++;

    /// <inheritdoc />
    public ExplicitBox<long> Read(PgDatum value)
    {
        ExplicitMappingState.LongReads++;
        ExplicitMappingState.Captured = value;
        long word = value.Read<long>();
        if (word == 9011)
        {
            throw new PgException("P8603", "explicit long reader failed", detail: "int8 construction", hint: "use another value");
        }

        return new(~word);
    }

    /// <inheritdoc />
    public PgDatum Write(ExplicitBox<long> value, uint typeOid, PgMemoryContext destination)
    {
        unsafe
        {
            return PgDatum.DangerousCreate(unchecked((nuint)~value.Number), typeOid, destination);
        }
    }
}

/// <summary>
/// Reads an explicit root used only inside raw method bodies, without a generated signature or provider.
/// </summary>
public sealed class ExplicitShortConverter : IPgDatumReader<ExplicitBox<short>>
{
    /// <summary>
    /// Records lazy construction.
    /// </summary>
    public ExplicitShortConverter() => ExplicitMappingState.Constructions++;

    /// <inheritdoc />
    public ExplicitBox<short> Read(PgDatum value)
    {
        ExplicitMappingState.ShortReads++;
        ExplicitMappingState.Captured = value;
        return new(checked((short)(value.Read<short>() + 1000)));
    }
}

/// <summary>
/// Executes explicit closed mappings across exact storage, raw-only discovery and backend error boundaries.
/// </summary>
[PgSchema("explicit_mappings")]
public static class ExplicitDatumMappingFunctions
{
    /// <summary>
    /// Exposes the independently decoded int4 value.
    /// </summary>
    [PgFunction(Name = "read_int")]
    public static int ReadInt(ExplicitBox<int> value) => value.Number;

    /// <summary>
    /// Exposes the independently decoded int8 value.
    /// </summary>
    [PgFunction(Name = "read_long")]
    public static long ReadLong(ExplicitBox<long> value) => value.Number;

    /// <summary>
    /// Writes a logical int4 value without first invoking its reader.
    /// </summary>
    [PgFunction(Name = "make_int")]
    public static ExplicitBox<int> MakeInt(int number) => new(number);

    /// <summary>
    /// Writes a logical int8 value without first invoking its reader.
    /// </summary>
    [PgFunction(Name = "make_long")]
    public static ExplicitBox<long> MakeLong(long number) => new(number);

    /// <summary>
    /// Installs the nullable int4 overload of one SQL function name.
    /// </summary>
    [PgFunction(Name = "echo")]
    public static ExplicitBox<int>? EchoInt(ExplicitBox<int>? value) => value;

    /// <summary>
    /// Installs the nullable int8 overload of the same SQL function name.
    /// </summary>
    [PgFunction(Name = "echo")]
    public static ExplicitBox<long>? EchoLong(ExplicitBox<long>? value) => value;

    /// <summary>
    /// Preserves int8 array shape and nullable elements through the exact closed mapping.
    /// </summary>
    [PgFunction(Name = "echo_array")]
    public static PgArray<ExplicitBox<long>?>? EchoArray(PgArray<ExplicitBox<long>?>? value) => value;

    /// <summary>
    /// Reads the metadata-only short construction alongside independently typed SPI results.
    /// </summary>
    [PgFunction(Name = "raw_and_spi")]
    public static string RawAndSpi()
    {
        ExplicitBox<short> small;
        using (SpiRawResult result = Spi.QueryRaw("SELECT 17::smallint"))
        {
            small = result[0][0].Read<ExplicitBox<short>>();
        }

        ExplicitBox<int> narrow = Spi.ExecuteScalar<ExplicitBox<int>>("SELECT 42::integer");
        ExplicitBox<long> wide = Spi.ExecuteScalar<ExplicitBox<long>>("SELECT 4294967313::bigint");
        return $"{small.Number}|{narrow.Number}|{wide.Number}";
    }

    /// <summary>
    /// Attempts an exact sibling mapping on present and NULL cells before any converter runs.
    /// </summary>
    [PgFunction(Name = "wrong_identity")]
    public static void WrongIdentity(bool wide, bool isNull)
    {
        string sql = "SELECT " + (isNull ? "NULL" : "17") + (wide ? "::integer" : "::bigint");
        using SpiRawResult result = Spi.QueryRaw(sql);
        if (wide)
        {
            result[0][0].Read<ExplicitBox<long>?>();
        }
        else
        {
            result[0][0].Read<ExplicitBox<int>?>();
        }
    }

    /// <summary>
    /// Reports lazy construction and direction-specific reads for all three exact declarations.
    /// </summary>
    [PgFunction(Name = "counts")]
    public static string Counts() => $"{ExplicitMappingState.Constructions}|{ExplicitMappingState.IntReads}|{ExplicitMappingState.LongReads}|{ExplicitMappingState.ShortReads}";

    /// <summary>
    /// Verifies that detached values do not retain native input handles.
    /// </summary>
    [PgFunction(Name = "expired")]
    public static bool Expired()
    {
        PgDatum value = ExplicitMappingState.Captured ?? throw new InvalidOperationException("No explicit operand was captured.");
        try
        {
            _ = value.DangerousGetBits();
            return false;
        }
        catch (ObjectDisposedException)
        {
            return true;
        }
    }
}
