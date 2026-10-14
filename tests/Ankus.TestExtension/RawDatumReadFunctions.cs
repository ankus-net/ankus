using System.Globalization;

namespace Ankus.TestExtension;

/// <summary>
/// Separates access to stored domain values from new native assignments.
/// </summary>
[PgSchema("raw_read_domains", Id = "raw-read-schema")]
public static class RawDatumReadFunctions
{
    private static uint s_droppedOid;

    /// <summary>
    /// Performs exactly one selected access after optionally changing constraints on the already captured value.
    /// </summary>
    [PgFunction(Name = "probe")]
    public static string Probe(string sql, int operation, string? mutation)
    {
        using SpiRawResult source = Spi.QueryRaw(sql);
        PgDatum datum = source[0][0];
        if (mutation is not null)
        {
            Spi.Execute(mutation);
        }

        string result = Read(datum, operation);
        _ = datum.DangerousGetBits();
        return string.Create(CultureInfo.InvariantCulture, $"{datum.TypeOid}|{datum.IsNull}|{result}|alive");
    }

    /// <summary>
    /// Copies a large by-reference value before deleting the source result and table rows, then verifies all copied elements.
    /// </summary>
    [PgFunction(Name = "copy_vector")]
    public static int[] CopyVector(string? mutation)
    {
        using PgMemoryContext destination = PgMemoryContext.Create("raw read vector copy");
        PgDatum original;
        PgDatum copy;
        using (SpiRawResult source = Spi.QueryRaw("SELECT value FROM raw_read_vector_source"))
        {
            original = source[0][0];
            if (mutation is not null)
            {
                Spi.Execute(mutation);
            }

            copy = original.CopyTo(destination);
            if (copy.TypeOid != original.TypeOid || copy.IsNull != original.IsNull)
            {
                throw new InvalidOperationException("Copy changed the stored vector identity.");
            }
        }

        RequireExpired(original);
        Spi.Execute("DELETE FROM raw_read_vector_source");
        int[] result = copy.Read<int[]>();
        destination.Reset();
        RequireExpired(copy);
        return result;
    }

    /// <summary>
    /// Binds raw, mapped scalar, or mapped array values before a nontransactional target-side effect.
    /// </summary>
    [PgFunction(Name = "bind")]
    public static string Bind(string sql, int route, int value, int nullMode)
    {
        using SpiRawResult source = Spi.QueryRaw(sql);
        PgDatum original = source[0][0];
        try
        {
            SpiParameter parameter = route switch
            {
                0 => SpiParameter.Create(original),
                1 => SpiParameter.Create<RawReadNumber?>(nullMode == 1 ? null : new(value, nullMode == 2)),
                _ => SpiParameter.Create<RawReadNumber?[]>([new(-7), nullMode == 1 ? null : new(value, nullMode == 2)]),
            };
            Spi.Execute("SELECT nextval('raw_read_target'),$1", parameter);
            return "assigned";
        }
        catch (PgException error)
        {
            _ = original.DangerousGetBits();
            return $"{error.SqlState}|{error.Message}|source alive";
        }
    }

    /// <summary>
    /// Returns an independently copied raw stored value through actual generated domain output validation.
    /// </summary>
    [PgFunction(Name = "raw_output", Requires = ["raw-read-types"])]
    [return: PgSqlType("number", Schema = "raw_read_domains")]
    public static PgDatum RawOutput(string sql)
    {
        using SpiRawResult source = Spi.QueryRaw(sql);
        return source[0][0].CopyTo(PgMemoryContext.Current);
    }

    /// <summary>
    /// Produces present, framework NULL, and writer-produced NULL mapped scalar outputs.
    /// </summary>
    [PgFunction(Name = "mapped_output")]
    public static RawReadNumber? MappedOutput(int value, int nullMode)
        => nullMode == 1 ? null : new(value, nullMode == 2);

    /// <summary>
    /// Constructs an array whose later cell must still receive native domain checks.
    /// </summary>
    [PgFunction(Name = "array_output")]
    public static RawReadNumber?[] ArrayOutput(int value, int nullMode)
        => [new(-7), nullMode == 1 ? null : new(value, nullMode == 2)];

    /// <summary>
    /// Counts mapped present reads independently of any SQL constraint counter.
    /// </summary>
    [PgFunction(Name = "reads")]
    public static int Reads() => RawReadNumberConverter.Reads;

    /// <summary>
    /// Uses a legitimate typed NULL captured before a dedicated temporary domain is dropped.
    /// </summary>
    [PgFunction(Name = "dropped_null")]
    public static string DroppedNull(int operation)
    {
        Spi.Execute("CREATE DOMAIN pg_temp.raw_read_dropped AS integer");
        using SpiRawResult source = Spi.QueryRaw("SELECT NULL::pg_temp.raw_read_dropped");
        PgDatum datum = source[0][0];
        s_droppedOid = datum.TypeOid;
        Spi.Execute("DROP DOMAIN pg_temp.raw_read_dropped");
        return Read(datum, operation);
    }

    /// <summary>
    /// Reports only the captured identity after a rejected dropped-type access.
    /// </summary>
    [PgFunction(Name = "dropped_oid")]
    public static uint DroppedOid() => s_droppedOid;

    /// <summary>
    /// Reads one opcode without using an additional formatter as the assertion oracle.
    /// </summary>
    private static string Read(PgDatum datum, int operation)
    {
        if (operation == 0)
        {
            return datum.Read<int?>()?.ToString(CultureInfo.InvariantCulture) ?? "NULL";
        }

        if (operation == 1)
        {
            return datum.Read<RawReadNumber?>()?.Value.ToString(CultureInfo.InvariantCulture) ?? "NULL";
        }

        if (operation == 2)
        {
            return datum.ToPostgresString() ?? "NULL";
        }

        if (operation == 3)
        {
            using PgMemoryContext destination = PgMemoryContext.Create("raw read scalar copy");
            PgDatum copy = datum.CopyTo(destination);
            return string.Create(CultureInfo.InvariantCulture,
                $"{copy.TypeOid}|{copy.IsNull}|{unchecked((int)copy.DangerousGetBits())}");
        }

        if (operation == 4)
        {
            PgAnyArray? array = datum.Read<PgAnyArray?>();
            return array is null ? "NULL" : string.Create(CultureInfo.InvariantCulture,
                $"{array.ElementTypeOid}|{array.Rank}|{(array.Rank == 0 ? 0 : array.GetLowerBound(0))}|{string.Join(',', array.Select(static cell => cell is null ? "NULL" : unchecked((int)cell.Datum.DangerousGetBits()).ToString(CultureInfo.InvariantCulture)))}");
        }

        if (operation == 5)
        {
            PgArray<RawReadNumber?>? array = datum.Read<PgArray<RawReadNumber?>?>();
            return array is null ? "NULL" : string.Create(CultureInfo.InvariantCulture,
                $"{array.ElementTypeOid}|{array.Rank}|{(array.Rank == 0 ? 0 : array.LowerBounds[0])}|{string.Join(',', array.Select(static cell => cell is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : "NULL"))}");
        }

        PgAnyArray? values = datum.Read<PgAnyArray?>();
        return values is null ? "NULL" : string.Join(',', values.Select(static cell => cell?.Read<int?>()?.ToString(CultureInfo.InvariantCulture) ?? "NULL"));
    }

    /// <summary>
    /// Rejects retained handles after owner deletion/reset without dereferencing their native storage.
    /// </summary>
    private static void RequireExpired(PgDatum value)
    {
        try
        {
            _ = value.DangerousGetBits();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        throw new InvalidOperationException("A destroyed raw datum owner remained accessible.");
    }
}
