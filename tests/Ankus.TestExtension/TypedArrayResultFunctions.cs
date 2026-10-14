using System.Globalization;

namespace Ankus.TestExtension;

public static partial class BorrowedArrayFunctions
{
    /// <summary>
    /// Selects statically closed array factories across raw, SPI and native function result boundaries.
    /// </summary>
    /// <param name="sql">The independent source query.</param>
    /// <param name="mode">The result ownership boundary.</param>
    /// <param name="kind">The exact requested element representation.</param>
    /// <returns>Copied typed values after the selected boundary has transferred ownership.</returns>
    [PgFunction]
    public static string?[] TypedArrayResult(string sql, int mode, int kind)
        => kind switch
        {
            0 => ReadTypedResult<int?>(sql, mode, static cell => cell?.ToString(CultureInfo.InvariantCulture)),
            1 => ReadTypedResult<ArrayValue?>(sql, mode, static cell => cell is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : null),
            2 => ReadTypedResult<EnumMood?>(sql, mode, static cell => cell?.ToString()),
            3 => ReadTypedResult<PgTextView?>(sql, mode, static cell =>
            {
                using (cell)
                {
                    return cell?.ToString();
                }
            }),
            4 => ReadTypedResult<PgVarlena<NativeLayoutTypeFunctions.Packet>?>(sql, mode, static cell =>
            {
                using (cell)
                {
                    return cell?.Value.Leaf.Number.ToString(CultureInfo.InvariantCulture);
                }
            }),
            5 => ReadTypedResult<MappedPositive?>(sql, mode, static cell => cell is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : null),
            6 => ReadTypedResult<WriteMappedInt?>(sql, mode, static cell => cell is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : null),
            7 => ReadTypedResult<ArrayText?>(sql, mode, static cell => cell?.Value),
            8 => ReadTypedResult<NativeLayoutTypeFunctions.Packet?>(sql, mode, static cell =>
                cell?.Leaf.Number.ToString(CultureInfo.InvariantCulture)),
            9 => ReadTypedResult<CustomTypeFunctions.Number?>(sql, mode, static cell => cell is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : null),
            10 => ReadTypedResult<CustomTypeFunctions.Message?>(sql, mode, static cell => cell?.Value),
            11 => ReadTypedResult<PgByteaView?>(sql, mode, static cell =>
            {
                using (cell)
                {
                    return cell is null ? null : Convert.ToHexString([.. cell]);
                }
            }),
            12 => ReadTypedResult<PgHeapTuple?>(sql, mode, static cell => cell?.Get<string>(0)),
            13 => ReadTypedResult<PgRange<int>?>(sql, mode, static cell => cell?.ToString()),
            14 => ReadTypedResult<PgRange<RangeNumber<int>>?>(sql, mode, static cell => cell is null ? null : cell.IsEmpty ? "empty" :
                $"{cell.Lower?.Number.ToString(CultureInfo.InvariantCulture)}:{cell.Upper?.Number.ToString(CultureInfo.InvariantCulture)}"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>
    /// Captures exact result-conversion errors followed by same-session recovery and native owner counts.
    /// </summary>
    /// <param name="sql">The deliberately incompatible result query.</param>
    /// <param name="mode">The result boundary.</param>
    /// <param name="kind">The requested element contract.</param>
    /// <returns>The rejection, recovery value and remaining borrowed array owners.</returns>
    [PgFunction]
    public static string[] TypedArrayResultFailure(string sql, int mode, int kind)
        => [TypedFailure(() => TypedArrayResult(sql, mode, kind)),
            Spi.ExecuteScalar<int>("SELECT 42").ToString(CultureInfo.InvariantCulture),
            Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident LIKE 'Ankus borrowed array%'")
                .ToString(CultureInfo.InvariantCulture)];

    /// <summary>
    /// Fails after two successful typed array columns and observes their provisional native cleanup.
    /// </summary>
    /// <param name="nativeFailure">Whether the last column fails native identity checking or managed scalar conversion.</param>
    /// <param name="mode">The direct, session, retained-plan or session-plan scalar boundary.</param>
    /// <returns>The exact primary error, owner count and recovered scalar value.</returns>
    [PgFunction]
    public static string[] TypedArrayResultCleanup(bool nativeFailure, int mode)
    {
        string failure = nativeFailure
            ? TypedFailure(() => ReadTypedColumns<PgArrayView<int?>?>("SELECT ARRAY[7,NULL], ARRAY['text'], NULL::real[]", mode))
            : TypedFailure(() => ReadTypedColumns<int>("SELECT ARRAY[7,NULL], ARRAY['text'], 'wrong'::text", mode));
        return [failure,
            Spi.ExecuteScalar<long>("SELECT count(*) FROM ankus_test_memory.contexts WHERE ident LIKE 'Ankus borrowed array%'")
                .ToString(CultureInfo.InvariantCulture), Spi.ExecuteScalar<int>("SELECT 42").ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// Contrasts raw-result borrowing with SPI scalar ownership after source rows have been disposed.
    /// </summary>
    /// <returns>Exact invalidation and surviving independent typed values.</returns>
    [PgFunction]
    public static string?[] TypedArrayResultOwners()
    {
        using SpiRawResult raw = Spi.QueryRaw("SELECT ARRAY['raw',NULL]");
        using PgArrayView<PgTextView?> borrowed = raw[0][0].Read<PgArrayView<PgTextView?>>();
        using PgTextView cell = borrowed[0]!;
        using PgArrayView<string?> owned = Spi.ExecuteScalar<PgArrayView<string?>>("SELECT ARRAY['owned',NULL]");
        string before = cell.ToString();
        raw.Dispose();
        return [before, TypedFailure(() => _ = borrowed[0]), TypedFailure(() => cell.ToString()), owned[0], owned[1]];
    }

    /// <summary>
    /// Binds present views by their original OID and nullable views by their declared element array type.
    /// </summary>
    /// <param name="sql">The source array query.</param>
    /// <param name="absent">Whether to bind a typed NULL view.</param>
    /// <returns>Parameter OID, received OID, native text and NULL flag.</returns>
    [PgFunction]
    public static string?[] TypedArrayParameter(string sql, bool absent)
    {
        using SpiRawResult raw = Spi.QueryRaw(sql);
        using PgArrayView<int?>? view = absent ? null : raw[0][0].Read<PgArrayView<int?>>();
        SpiParameter parameter = SpiParameter.Create(view);
        using SpiPreparedStatement statement = Spi.Connect(session => session.Prepare(
            "SELECT ARRAY[pg_typeof($1)::oid::text,$1::text,($1 IS NULL)::text]", typeof(PgArrayView<int?>)).Keep());
        string?[] received = absent ? statement.ExecuteScalar<string?[]>(parameter)
            : Spi.ExecuteScalar<string?[]>("SELECT ARRAY[pg_typeof($1)::oid::text,$1::text,($1 IS NULL)::text]", parameter);
        return [parameter.TypeOid.ToString(CultureInfo.InvariantCulture), .. received];
    }

    /// <summary>
    /// Transports an existing array without invoking a missing element writer or converting its cells back to native values.
    /// </summary>
    /// <returns>The converted managed cells followed by the unchanged native values.</returns>
    [PgFunction]
    public static int?[] TypedReadOnlyArrayParameter()
    {
        using PgArrayView<ReadMappedInt?> view = Spi.ExecuteScalar<PgArrayView<ReadMappedInt?>>("SELECT ARRAY[7,NULL,11]");
        int?[] received = Spi.ExecuteScalar<int?[]>("SELECT $1", SpiParameter.Create(view));
        return [.. view.Select(static cell => cell is { } present ? present.Value : (int?)null), .. received];
    }

    /// <summary>
    /// Exercises the provisional owner scope through each scalar tuple boundary.
    /// </summary>
    private static void ReadTypedColumns<T>(string sql, int mode)
    {
        if (mode == 0)
        {
            Spi.ExecuteScalars<PgArrayView<int?>, PgArrayView<string?>, T>(sql);
        }
        else if (mode == 1)
        {
            Spi.Connect(session => session.ExecuteScalars<PgArrayView<int?>, PgArrayView<string?>, T>(sql));
        }
        else if (mode == 2)
        {
            using SpiPreparedStatement statement = Spi.Connect(session => session.Prepare(sql).Keep());
            statement.ExecuteScalars<PgArrayView<int?>, PgArrayView<string?>, T>();
        }
        else if (mode == 3)
        {
            Spi.Connect(session =>
            {
                using SpiPreparedStatement statement = session.Prepare(sql);
                return statement.ExecuteScalars<PgArrayView<int?>, PgArrayView<string?>, T>();
            });
        }
        else
        {
            throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <summary>
    /// Reads values while raw row owners remain live, or after scalar APIs transfer native storage.
    /// </summary>
    private static string?[] ReadTypedResult<T>(string sql, int mode, Func<T, string?> format)
    {
        if (mode == 4)
        {
            using SpiRawResult raw = Spi.QueryRaw(sql);
            using PgArrayView<T>? borrowed = raw[0][0].Read<PgArrayView<T>?>();
            return FormatTypedResult(borrowed, format);
        }

        if (mode == 5)
        {
            using SpiCursor cursor = Spi.OpenCursor(sql);
            using SpiRawResult batch = cursor.FetchRaw(1);
            using PgArrayView<T>? borrowed = batch[0][0].Read<PgArrayView<T>?>();
            return FormatTypedResult(borrowed, format);
        }

        if (mode == 8)
        {
            using SpiRawResult raw = Spi.QueryRaw(sql);
            nint address = unchecked((nint)Spi.ExecuteScalar<long>("""
                SELECT tests.function_address(oid) FROM pg_proc
                WHERE pronamespace='pg_catalog'::regnamespace AND proname='array_cat' AND pronargs=2
                """));
            PgArrayView<T>? result;
            unsafe
            {
                result = PgFunctions.DangerousCall<PgArrayView<T>?>(address, 0, [raw[0][0], raw[0][0]]);
            }

            using PgArrayView<T>? direct = result;
            return FormatTypedResult(direct, format);
        }

        using PgArrayView<T>? value = mode switch
        {
            0 => Spi.ExecuteScalar<PgArrayView<T>?>(sql),
            1 => Spi.Connect(session => session.ExecuteScalar<PgArrayView<T>?>(sql)),
            2 => ReadKeptTypedResult<T>(sql),
            3 => Spi.Connect(session =>
            {
                using SpiPreparedStatement statement = session.Prepare(sql);
                return statement.ExecuteScalar<PgArrayView<T>?>();
            }),
            6 => PgFunctions.Call<PgArrayView<T>?>("pg_temp.typed_array_result"),
            7 => PgFunctions.Call<PgArrayView<T>?>(Spi.ExecuteScalar<uint>("SELECT 'pg_temp.typed_array_result()'::regprocedure::oid")),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };
        return FormatTypedResult(value, format);
    }

    /// <summary>
    /// Returns a callback-owned view after a retained prepared plan has been disposed.
    /// </summary>
    private static PgArrayView<T>? ReadKeptTypedResult<T>(string sql)
    {
        using SpiPreparedStatement statement = Spi.Connect(session => session.Prepare(sql).Keep());
        return statement.ExecuteScalar<PgArrayView<T>?>();
    }

    /// <summary>
    /// Distinguishes absent arrays from empty arrays while preserving every nullable cell.
    /// </summary>
    private static string?[] FormatTypedResult<T>(PgArrayView<T>? value, Func<T, string?> format)
        => value is null ? ["SQL NULL"] : [string.Join(',', value.Lengths.ToArray()),
            string.Join(',', value.LowerBounds.ToArray()), .. value.Select(format)];
}
