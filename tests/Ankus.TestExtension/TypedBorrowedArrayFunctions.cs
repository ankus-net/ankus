using System.Globalization;

namespace Ankus.TestExtension;

public static partial class BorrowedArrayFunctions
{
    private static PgArrayView<PgTextView?>? s_savedTypedArray;
    private static IEnumerator<PgTextView?>? s_savedTypedCursor;
    private static PgTextView? s_savedTypedText;

    /// <summary>
    /// Reads typed cells alongside their independently retained native metadata.
    /// </summary>
    /// <param name="value">The checked array datum.</param>
    /// <param name="mode">The requested scalar conversion.</param>
    /// <returns>Exact identities, dimensions, bounds and converted cell contents.</returns>
    [PgFunction]
    public static string?[] TypedArraySnapshot([PgSqlType("any", Schema = "pg_catalog")] PgDatum value, int mode)
        => mode switch
        {
            0 => TypedSnapshot<int?>(value, static cell => cell?.ToString(CultureInfo.InvariantCulture)),
            1 => TypedSnapshot<int>(value, static cell => cell.ToString(CultureInfo.InvariantCulture)),
            2 => TypedSnapshot<string?>(value, static cell => cell),
            3 => TypedSnapshot<byte[]?>(value, static cell => cell is null ? null : Convert.ToHexString(cell)),
            4 => TypedSnapshot<double?>(value, static cell => cell is null ? null :
                BitConverter.DoubleToUInt64Bits(cell.Value).ToString("X16", CultureInfo.InvariantCulture)),
            5 => TypedSnapshot<Guid?>(value, static cell => cell?.ToString("D")),
            6 => TypedSnapshot<EnumMood?>(value, static cell => cell?.ToString()),
            7 => TypedSnapshot<ArrayValue?>(value, static cell => cell is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : null),
            8 => TypedSnapshot<PgNumeric?>(value, static cell => cell?.ToString()),
            9 => TypedSnapshot<PgHeapTuple?>(value, static cell => cell?.Get<string>(0)),
            10 => TypedSnapshot<PgTextView?>(value, static cell =>
            {
                using (cell)
                {
                    return cell?.ToString();
                }
            }),
            11 => TypedSnapshot<PgByteaView?>(value, static cell =>
            {
                using (cell)
                {
                    return cell is null ? null : Convert.ToHexString([.. cell]);
                }
            }),
            12 => TypedSnapshot<MappedPositive?>(value, static cell => cell is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : null),
            13 => TypedSnapshot<WriteMappedInt?>(value, static cell => cell is { } present ? present.Value.ToString(CultureInfo.InvariantCulture) : null),
            14 => TypedSnapshot<DateOnly?>(value, static cell => cell?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)),
            15 => TypedSnapshot<PgVarlena<NativeLayoutTypeFunctions.Packet>?>(value, static cell =>
            {
                using (cell)
                {
                    return cell?.Value.Leaf.Number.ToString(CultureInfo.InvariantCulture);
                }
            }),
            16 => TypedSnapshot<NativeLayoutTypeFunctions.Packet?>(value, static cell =>
                cell?.Leaf.Number.ToString(CultureInfo.InvariantCulture)),
            17 => TypedSnapshot<ArrayText?>(value, static cell => cell?.Value),
            _ => throw new ArgumentOutOfRangeException(nameof(mode)),
        };

    /// <summary>
    /// Checks a rejected typed conversion followed by normal SQL in the same backend.
    /// </summary>
    /// <param name="value">The deliberately incompatible array.</param>
    /// <param name="mode">The scalar representation to request.</param>
    /// <returns>The exact failure and a subsequent successful scalar result.</returns>
    [PgFunction]
    public static string[] TypedArrayFailure([PgSqlType("any", Schema = "pg_catalog")] PgDatum value, int mode)
        => [TypedFailure(() => TypedArraySnapshot(value, mode)),
            Spi.ExecuteScalar<int>("SELECT 42").ToString(CultureInfo.InvariantCulture)];

    /// <summary>
    /// Observes lazy mapped conversion, cached Current, NULL bypass and recovery after a later cell fails.
    /// </summary>
    /// <returns>Exact cursor states, converter entry counts and escaped input lifetime checks.</returns>
    [PgFunction]
    public static string?[] TypedArrayMappedCursor()
    {
        ArrayValueConverter.Reset();
        using SpiRawResult result = Spi.QueryRaw("SELECT ARRAY[5,NULL,-777,9]");
        using var view = new PgArrayView<ArrayValue?>(result[0][0]);
        using IEnumerator<ArrayValue?> cursor = view.GetEnumerator();
        var observed = new List<string?>
        {
            ArrayValueConverter.Reads.ToString(CultureInfo.InvariantCulture),
            cursor.MoveNext().ToString(),
            cursor.Current?.Value.ToString(CultureInfo.InvariantCulture),
            cursor.Current?.Value.ToString(CultureInfo.InvariantCulture),
            ArrayValueConverter.Reads.ToString(CultureInfo.InvariantCulture),
            cursor.MoveNext().ToString(),
            cursor.Current?.Value.ToString(CultureInfo.InvariantCulture),
            ArrayValueConverter.Reads.ToString(CultureInfo.InvariantCulture),
            TypedFailure(() => cursor.MoveNext()),
            TypedFailure(() => _ = cursor.Current),
            ArrayValueConverter.Reads.ToString(CultureInfo.InvariantCulture),
            cursor.MoveNext().ToString(),
            cursor.Current?.Value.ToString(CultureInfo.InvariantCulture),
            ArrayValueConverter.Reads.ToString(CultureInfo.InvariantCulture),
            cursor.MoveNext().ToString(),
            TypedFailure(() => _ = cursor.Current)
        };
        view.Dispose();
        observed.AddRange(ArrayValueConverter.Inputs.Select(static cell => TypedFailure(() => cell.DangerousGetBits())));
        observed.Add(Spi.ExecuteScalar<int>("SELECT 42").ToString(CultureInfo.InvariantCulture));
        return [.. observed];
    }

    /// <summary>
    /// Expires typed array sources while preserving independent text and byte copies.
    /// </summary>
    /// <param name="mode">Dispose view, reset source, delete source or reset only the source.</param>
    /// <returns>Failures for every alias, surviving copies and readable metadata.</returns>
    [PgFunction]
    public static string?[] TypedArrayOwners(int mode)
    {
        using PgMemoryContext source = PgMemoryContext.Create("typed borrowed source");
        using SpiRawResult result = Spi.QueryRaw("SELECT '[4:5]={café,NULL}'::text[], ARRAY[decode('00ff07','hex'),NULL]");
        using var text = new PgArrayView<PgTextView?>(result[0][0].CopyTo(source));
        using var bytes = new PgArrayView<PgByteaView?>(result[0][1].CopyTo(source));
        using IEnumerator<PgTextView?> cursor = text.GetEnumerator();
        _ = cursor.MoveNext();
        using PgTextView element = cursor.Current!;
        using PgByteaView binary = bytes[0]!;
        string copy = element.ToString();
        byte[] copiedBytes = [.. binary];
        switch (mode)
        {
            case 0:
                text.Dispose();
                bytes.Dispose();
                break;
            case 1:
                source.Reset();
                break;
            case 2:
                source.Dispose();
                break;
            case 3:
                source.ResetOnly();
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }

        return [TypedFailure(() => _ = text[0]), TypedFailure(() => text.GetValue(4)), TypedFailure(() => _ = text.Datum),
            TypedFailure(() => text.GetEnumerator()), TypedFailure(() => cursor.MoveNext()), TypedFailure(() => _ = cursor.Current),
            TypedFailure(() => element.ToString()), TypedFailure(() => _ = binary[0]), copy, Convert.ToHexString(copiedBytes),
            text.Count.ToString(CultureInfo.InvariantCulture), text.LowerBounds[0].ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// Interleaves independent cursors and checks flat and PostgreSQL subscript boundaries.
    /// </summary>
    /// <returns>Exact cursor values, NULLs, bounds diagnostics and valid adjacent reads.</returns>
    [PgFunction]
    public static string?[] TypedArrayCursorsAndBounds()
    {
        using SpiRawResult result = Spi.QueryRaw("SELECT '[-2:-1][4:6]={{0,NULL,7},{-9,11,15}}'::integer[], ARRAY[]::integer[]");
        using var view = new PgArrayView<int?>(result[0][0]);
        using var empty = new PgArrayView<int>(result[0][1]);
        using IEnumerator<int?> first = view.GetEnumerator();
        using IEnumerator<int?> second = view.GetEnumerator();
        using var untyped = (IDisposable)((System.Collections.IEnumerable)view).GetEnumerator();
        var legacy = (System.Collections.IEnumerator)untyped;
        var observed = new List<string?> { TypedFailure(() => _ = first.Current), first.MoveNext().ToString(),
            first.Current?.ToString(CultureInfo.InvariantCulture), second.MoveNext().ToString(),
            second.Current?.ToString(CultureInfo.InvariantCulture), first.MoveNext().ToString(),
            first.Current?.ToString(CultureInfo.InvariantCulture), legacy.MoveNext().ToString(), legacy.Current?.ToString() };
        first.Dispose();
        observed.Add(TypedFailure(() => first.MoveNext()));
        while (second.MoveNext())
        {
            observed.Add(second.Current?.ToString(CultureInfo.InvariantCulture));
        }

        observed.AddRange([second.MoveNext().ToString(), TypedFailure(() => _ = second.Current), TypedFailure(second.Reset),
            TypedFailure(() => _ = view[-1]), TypedFailure(() => _ = view[6]), TypedFailure(() => view.GetValue(-2)),
            TypedFailure(() => view.GetValue(-3, 4)), TypedFailure(() => view.GetValue(0, 4)),
            TypedFailure(() => view.GetValue(-2, 3)), TypedFailure(() => view.GetValue(-2, 7)),
            view.GetValue(-2, 4)?.ToString(CultureInfo.InvariantCulture), view.GetValue(-2, 5)?.ToString(CultureInfo.InvariantCulture),
            view.GetValue(-1, 6)?.ToString(CultureInfo.InvariantCulture), view[3]?.ToString(CultureInfo.InvariantCulture),
            TypedFailure(() => _ = empty[0]), TypedFailure(() => empty.GetValue())]);
        using IEnumerator<int> none = empty.GetEnumerator();
        observed.AddRange([none.MoveNext().ToString(), none.MoveNext().ToString(), TypedFailure(() => _ = none.Current)]);
        return [.. observed];
    }

    /// <summary>
    /// Leaves a typed view and its borrowed element to be expired by the input callback's native scope.
    /// </summary>
    /// <param name="value">The directly borrowed raw array input.</param>
    /// <param name="fail">Whether to end the callback through an error.</param>
    /// <returns>The first cell before callback expiry.</returns>
    [PgFunction]
    public static string? TypedArraySave(PgArrayView value, bool fail)
    {
        s_savedTypedArray = new PgArrayView<PgTextView?>(value.Datum);
        s_savedTypedCursor = s_savedTypedArray.GetEnumerator();
        _ = s_savedTypedCursor.MoveNext();
        s_savedTypedText = s_savedTypedCursor.Current;
        if (fail)
        {
            throw new PgException("P8525", "Typed array callback failed.");
        }

        return s_savedTypedText?.ToString();
    }

    /// <summary>
    /// Observes input aliases from a later callback after automatic owner cleanup.
    /// </summary>
    /// <returns>Exact expired-array, cursor and element errors.</returns>
    [PgFunction]
    public static string[] TypedArrayExpired()
        => [TypedFailure(() => _ = s_savedTypedArray!.Datum), TypedFailure(() => _ = s_savedTypedCursor!.Current),
            TypedFailure(() => s_savedTypedText!.ToString()),
            Spi.ExecuteScalar<int>("SELECT 42").ToString(CultureInfo.InvariantCulture)];

    /// <summary>
    /// Tests source-level NULL and stale handles independently of strict SQL argument dispatch.
    /// </summary>
    /// <returns>Constructor diagnostics followed by same-session recovery.</returns>
    [PgFunction]
    public static string[] TypedArrayConstructionErrors()
    {
        using SpiRawResult result = Spi.QueryRaw("SELECT NULL::integer[], ARRAY[7]");
        PgDatum stale = result[0][1];
        string absent = TypedFailure(() => new PgArrayView<int?>(result[0][0]).Dispose());
        result.Dispose();
        return [absent, TypedFailure(() => new PgArrayView<int?>(stale).Dispose()),
            Spi.ExecuteScalar<int>("SELECT 42").ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// Copies metadata and formatted typed cells without conflating NULL with an empty value.
    /// </summary>
    private static string?[] TypedSnapshot<T>(PgDatum value, Func<T, string?> format)
    {
        using var view = new PgArrayView<T>(value);
        return [view.TypeOid.ToString(CultureInfo.InvariantCulture), view.ElementTypeOid.ToString(CultureInfo.InvariantCulture),
            view.Rank.ToString(CultureInfo.InvariantCulture), view.Count.ToString(CultureInfo.InvariantCulture), view.HasNulls.ToString(),
            string.Join(',', view.Lengths.ToArray()), string.Join(',', view.LowerBounds.ToArray()), .. view.Select(format)];
    }

    /// <summary>
    /// Preserves precise native diagnostics and managed conversion failures for external assertions.
    /// </summary>
    private static string TypedFailure(Action action)
    {
        try
        {
            return Failure(action);
        }
        catch (Exception error) when (error is InvalidCastException or OverflowException)
        {
            return error.GetType().Name;
        }
    }
}
