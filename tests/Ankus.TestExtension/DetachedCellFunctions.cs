using System.Globalization;

[assembly: PgSql("detached-cells", """
    CREATE SCHEMA detached_cells;
    CREATE TYPE detached_cells.unconverted AS (
        number datum_mappings.u24, pair datum_mappings.complex, words tsvector, address macaddr, amount money,
        tags tsvector[], position pg_lsn, label text);
    """, Requires = ["mapped-u24", "mapped-complex"])]

namespace Ankus.TestExtension;

/// <summary>
/// Reads a text-search vector through PostgreSQL's own output function.
/// </summary>
/// <param name="Text">The canonical text.</param>
[PgDatumType("tsvector", typeof(WordsConverter), Origin = PgTypeOrigin.External, Schema = "pg_catalog")]
public sealed record MappedWords(string Text);

/// <summary>
/// Converts text-search vectors for detached-cell reads.
/// </summary>
public sealed class WordsConverter : IPgDatumReader<MappedWords>
{
    /// <inheritdoc />
    public MappedWords Read(PgDatum value) => new(value.ToPostgresString()!);
}

/// <summary>
/// Copies the six bytes of a fixed-length MAC address.
/// </summary>
/// <param name="Text">The colon-separated bytes.</param>
[PgDatumType("macaddr", typeof(MacAddressConverter), Origin = PgTypeOrigin.External, Schema = "pg_catalog")]
public sealed record MappedMacAddress(string Text);

/// <summary>
/// Reads MAC address storage directly.
/// </summary>
public sealed class MacAddressConverter : IPgDatumReader<MappedMacAddress>
{
    /// <inheritdoc />
    public unsafe MappedMacAddress Read(PgDatum value)
        => new(Convert.ToHexString(new ReadOnlySpan<byte>((byte*)value.DangerousGetBits(), 6)));
}

/// <summary>
/// Holds a money amount in cents, stored in a by-value datum word.
/// </summary>
/// <param name="Cents">The amount in cents.</param>
[PgDatumType("money", typeof(MoneyConverter), Origin = PgTypeOrigin.External, Schema = "pg_catalog")]
public readonly record struct MappedMoney(long Cents);

/// <summary>
/// Converts money datum words.
/// </summary>
public sealed class MoneyConverter : IPgDatumReader<MappedMoney>, IPgDatumWriter<MappedMoney>
{
    /// <inheritdoc />
    public MappedMoney Read(PgDatum value) => new(unchecked((long)value.DangerousGetBits()));

    /// <inheritdoc />
    public PgDatum Write(MappedMoney value, uint typeOid, PgMemoryContext destination)
    {
        unsafe
        {
            return PgDatum.DangerousCreate(unchecked((nuint)value.Cents), typeOid, destination);
        }
    }
}

/// <summary>
/// Reads and writes row and composite cells whose types have no managed conversion.
/// </summary>
[PgSchema("detached_cells", Create = false)]
public static class DetachedCellFunctions
{
    private const string Cells = """
        SELECT '42'::datum_mappings.u24 AS number, '1.25,-2.5'::datum_mappings.complex AS pair,
            'b:2 a:1'::tsvector AS words, '08:00:2b:01:02:03'::macaddr AS address, '-12.34'::money AS amount,
            'word'::text AS label, NULL::datum_mappings.u24 AS missing
        """;

    /// <summary>
    /// Reads every representation through its mapped reader from an SPI row.
    /// </summary>
    /// <returns>The values read.</returns>
    [PgFunction(Name = "spi_mapped", Requires = ["detached-cells"])]
    public static string SpiMapped()
    {
        SpiRow row = Spi.Select(Cells)[0];
        return Describe(row.Get<MappedU24>("number"), row.Get<MappedComplex>("pair"), row.Get<MappedWords>("words"),
            row.Get<MappedMacAddress>("address"), row.Get<MappedMoney>("amount"), row.Get<MappedText>("label"),
            row.Get<MappedU24?>("missing"));
    }

    /// <summary>
    /// Reports the error from each unsupported read of an SPI row, then reads a converted cell from the same row.
    /// </summary>
    /// <returns>The error types and the converted label.</returns>
    [PgFunction(Name = "spi_errors", Requires = ["detached-cells"])]
    public static string SpiErrors()
    {
        SpiRow row = Spi.Select(Cells)[0];
        return string.Join("|",
            Error(() => row["number"]),
            Error(() => row.Get<object>("words")),
            Error(() => row.Get<int>("amount")),
            Error(() => row.Get<MappedU24>("label")),
            Error(() => row.Get<MappedU24>("pair")),
            Error(() => row.Get<MappedU24>("missing")),
            row.Get<string>("label"));
    }

    /// <summary>
    /// Replaces SPI row cells with a mapped value and a raw datum, then reads both back.
    /// </summary>
    /// <returns>The replaced values and type identities.</returns>
    [PgFunction(Name = "spi_set", Requires = ["detached-cells"])]
    public static string SpiSet()
    {
        SpiRow row = Spi.Select(Cells)[0];
        row.Set("label", new MappedMoney(-5));
        row.Set("number", new MappedU24(7));
        using SpiRawResult raw = Spi.SelectRaw("SELECT 'c:3'::tsvector");
        row.Set("pair", raw[0][0]);
        return string.Join("|", row.Get<MappedMoney>("label").Cents, row.Get<MappedU24>("number").Value,
            row.Get<MappedWords>("pair").Text, row.GetTypeOid("label") == row.GetTypeOid("amount"));
    }

    /// <summary>
    /// Changes converted and mapped fields of a composite whose other fields have no managed conversion.
    /// </summary>
    /// <param name="value">The composite.</param>
    /// <returns>The changed composite.</returns>
    [PgFunction(Name = "composite_update", Requires = ["detached-cells"])]
    [return: PgCompositeType("unconverted", Schema = "detached_cells")]
    public static PgHeapTuple CompositeUpdate([PgCompositeType("unconverted", Schema = "detached_cells")] PgHeapTuple value)
    {
        PgHeapTuple copy = value.Clone();
        if (copy.Get<MappedU24?>("number") is { } number)
        {
            copy.Set("number", new MappedU24(number.Value + 1));
        }

        copy.Set("label", "updated");
        return copy;
    }

    /// <summary>
    /// Reads the mapped fields of a composite.
    /// </summary>
    /// <param name="value">The composite.</param>
    /// <returns>The values read.</returns>
    [PgFunction(Name = "composite_read", Requires = ["detached-cells"])]
    public static string CompositeRead([PgCompositeType("unconverted", Schema = "detached_cells")] PgHeapTuple value)
        => Describe(value.Get<MappedU24?>("number"), value.Get<MappedComplex?>("pair"), value.Get<MappedWords?>("words"),
            value.Get<MappedMacAddress?>("address"), value.Get<MappedMoney?>("amount"), null, null)
            + "|" + Error(() => value["tags"]) + "|" + Error(() => value.Get<MappedWords>("tags"));

    /// <summary>
    /// Builds a record from a mapped parameter and a raw datum, then reads both fields back.
    /// </summary>
    /// <returns>The values read.</returns>
    [PgFunction(Name = "record_create", Requires = ["detached-cells"])]
    public static string RecordCreate()
    {
        using SpiRawResult raw = Spi.SelectRaw("SELECT '08:00:2b:0a:0b:0c'::macaddr");
        PgHeapTuple record = PgHeapTuple.Create(("number", SpiParameter.Create(new MappedU24(9))),
            ("address", SpiParameter.Create(raw[0][0])));
        return $"{record.Get<MappedU24>("number").Value}|{record.Get<MappedMacAddress>("address").Text}";
    }

    /// <summary>
    /// Reads, replaces and builds arrays of mapped elements in rows and records.
    /// </summary>
    /// <returns>The arrays read.</returns>
    [PgFunction(Name = "array_cells", Requires = ["detached-cells"])]
    public static string ArrayCells()
    {
        SpiRow row = Spi.Select("SELECT ARRAY['1', NULL, '3']::datum_mappings.u24[] AS numbers, ARRAY['a:1'::tsvector] AS words")[0];
        MappedU24?[] numbers = row.Get<MappedU24?[]>("numbers");
        PgArray<MappedWords> words = row.Get<PgArray<MappedWords>>("words");
        string mismatch = Error(() => row.Get<MappedU24?[]>("words"));
        row.Set("words", new MappedU24?[] { new(4), new(5) });
        PgHeapTuple record = PgHeapTuple.Create(("numbers", SpiParameter.Create(new MappedU24?[] { new(6), null })));
        return string.Join("|", Numbers(numbers), words[0].Text, mismatch, Numbers(row.Get<MappedU24?[]>("words")),
            Numbers(record.Get<MappedU24?[]>("numbers")));
    }

    /// <summary>
    /// Reads row and composite cells as raw datums, and resolves a column of a result without rows.
    /// </summary>
    /// <param name="value">The composite.</param>
    /// <returns>The datums' text, NULL flags and type identities.</returns>
    [PgFunction(Name = "raw_cells", Requires = ["detached-cells"])]
    public static string RawCells([PgCompositeType("unconverted", Schema = "detached_cells")] PgHeapTuple value)
    {
        SpiResult empty = Spi.Select("SELECT 1 AS id, 'a:1'::tsvector AS words WHERE false");
        SpiRow row = Spi.Select("SELECT 'b:2'::tsvector AS words, NULL::macaddr AS address, 7 AS id")[0];
        PgDatum words = value.Get<PgDatum>("words");
        PgDatum number = value.Get<PgDatum>("number");
        PgDatum rowWords = row.Get<PgDatum>("words");
        PgDatum address = row.Get<PgDatum>("address");
        return string.Join("|", empty.GetOrdinal("words"), words.ToPostgresString(), number.IsNull,
            number.TypeOid == value.Descriptor.Attributes[0].TypeOid, rowWords.ToPostgresString(), address.IsNull,
            address.TypeOid == row.GetTypeOid("address"), row.Get<PgDatum>("id").Read<int>());
    }

    /// <summary>
    /// Forms a native heap tuple from a composite and copies it back with a native descriptor, then converts the
    /// composite to a raw datum.
    /// </summary>
    /// <param name="value">The composite.</param>
    /// <returns>The copied fields, the descriptor's shape and the datum's text.</returns>
    [PgFunction(Name = "native_tuples", Requires = ["detached-cells"])]
    public static unsafe string NativeTuples([PgCompositeType("unconverted", Schema = "detached_cells")] PgHeapTuple value)
    {
        nint descriptor = (nint)Ankus.Postgres.NativeMethods.lookup_rowtype_tupdesc_copy(value.Descriptor.TypeOid, -1);
        nint heap = value.DangerousToNative(PgMemoryContext.Current);
        PgHeapTuple copy = PgHeapTuple.DangerousCopy(heap, descriptor);
        PgTupleDescriptor shape = PgTupleDescriptor.DangerousCopy(descriptor);
        return string.Join("|", copy.Get<MappedU24?>("number")?.Value, copy.Get<MappedWords?>("words")?.Text, copy.Get<string>("label"),
            shape.Attributes.Count, shape.TypeOid == value.Descriptor.TypeOid,
            PgDatum.Create(value, PgMemoryContext.Current).ToPostgresString());
    }

    /// <summary>
    /// Changes a document's label and amount, leaving fields without a managed conversion as they were.
    /// </summary>
    /// <param name="context">The trigger invocation.</param>
    /// <returns>The changed row.</returns>
    [PgTrigger]
    [PgFunction(Name = "document_update", Requires = ["detached-cells"])]
    public static PgHeapTuple? DocumentUpdate(PgTriggerContext context)
    {
        PgHeapTuple row = context.New!.Clone();
        row.Set("label", row.Get<string>("label").ToUpperInvariant());
        row.Set("amount", new MappedMoney(row.Get<MappedMoney>("amount").Cents + 1));
        return row;
    }

    private static string Describe(MappedU24? number, MappedComplex? pair, MappedWords? words, MappedMacAddress? address,
        MappedMoney? amount, MappedText? label, MappedU24? missing)
        => string.Join("|",
            number is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : "null",
            pair is { } complex ? string.Create(CultureInfo.InvariantCulture, $"{complex.Real:R},{complex.Imaginary:R}") : "null",
            words?.Text ?? "null",
            address?.Text ?? "null",
            amount?.Cents.ToString(CultureInfo.InvariantCulture) ?? "null",
            label?.Value ?? "null",
            missing is null ? "null" : "present");

    private static string Numbers(MappedU24?[] values)
        => string.Join(',', values.Select(static value => value is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : "NULL"));

    private static string Error(Func<object?> read)
    {
        try
        {
            read();
            return "none";
        }
        catch (Exception error) when (error is NotSupportedException or InvalidCastException or InvalidOperationException)
        {
            return error.GetType().Name;
        }
    }
}
