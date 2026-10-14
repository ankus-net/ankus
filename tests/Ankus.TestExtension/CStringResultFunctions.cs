namespace Ankus.TestExtension;

public static partial class CStringFunctions
{
    private static int s_disposedResults;

    /// <summary>
    /// Returns transient record columns without storing the C-string pseudo-type in a table.
    /// </summary>
    /// <param name="value">The retained native input.</param>
    /// <returns>Owned and borrowed representations of the same exact bytes.</returns>
    [PgFunction]
    public static IEnumerable<(PgCString? owned, PgCStringView? borrowed)> TableResults(PgCStringView? value)
    {
        using (value)
        {
            yield return (value?.ToOwned(), value);
            yield return (null, null);
        }
    }

    /// <summary>
    /// Emits owned C-string results, including SQL NULL and an explicitly encoded UTF-8 value.
    /// </summary>
    /// <param name="value">The first independent input.</param>
    /// <returns>Exact C-string datums for three rows.</returns>
    [PgFunction]
    public static IEnumerable<PgCString?> OwnedResults(PgCString? value)
    {
        yield return value;
        yield return null;
        yield return PgCString.FromUtf8("é");
    }

    /// <summary>
    /// Returns retained native views and observes disposal after completion, early exit or an iterator error.
    /// </summary>
    /// <param name="value">The retained native snapshot.</param>
    /// <param name="fail">Whether to reject the second row after the first native result.</param>
    /// <returns>The original value, SQL NULL, and the original value again.</returns>
    [PgFunction]
    public static IEnumerable<PgCStringView?> BorrowedResults(PgCStringView? value, bool fail)
    {
        try
        {
            using (value)
            {
                yield return value;
                _ = Spi.ExecuteScalar<int>("SELECT 42");
                if (fail)
                {
                    throw new InvalidOperationException("C-string iterator failed.");
                }

                yield return null;
                yield return value;
            }
        }
        finally
        {
            s_disposedResults++;
        }
    }

    /// <summary>
    /// Reports completed iterator disposal independently of returned rows.
    /// </summary>
    /// <returns>The backend-local number of disposed result iterators.</returns>
    [PgFunction]
    public static int DisposedResults() => s_disposedResults;

    /// <summary>
    /// Reads the same array through vectors, shaped values and both finite typed-view factories.
    /// </summary>
    /// <returns>Exact hexadecimal cells from each route, preserving NULL and empty cells.</returns>
    [PgFunction]
    public static string?[] ArrayBoundaries()
    {
        PgCString?[] vector = [null, new([]), new([128, 255])];
        PgCString?[] rebound = Spi.ExecuteScalar<PgCString?[]>("SELECT $1", [SpiParameter.Create(vector)]);
        PgArray<PgCString?> shaped = Spi.ExecuteScalar<PgArray<PgCString?>>("SELECT cstrings.create_array()");
        using PgArrayView<PgCString?> owned = Spi.ExecuteScalar<PgArrayView<PgCString?>>("SELECT cstrings.create_array()");
        using PgArrayView<PgCStringView?> borrowed = PgFunctions.Call<PgArrayView<PgCStringView?>>("cstrings.create_array");
        using SpiRawResult raw = Spi.QueryRaw("SELECT cstrings.create_array()");
        using PgArrayView<PgCString?> rawView = raw[0][0].Read<PgArrayView<PgCString?>>();
        List<string?> result = [.. rebound.Select(static item => item is null ? null : Convert.ToHexString(item.AsSpan())),
            .. ArraySnapshot(shaped), .. owned.Select(static item => item is null ? null : Convert.ToHexString(item.AsSpan())),
            .. BorrowedArraySnapshot(borrowed), .. rawView.Select(static item => item is null ? null : Convert.ToHexString(item.AsSpan()))];
        return [.. result];
    }

    /// <summary>
    /// Forces a later SPI-column conversion failure after a provisional C-string view was allocated.
    /// </summary>
    /// <returns>The exact failure followed by the remaining native private-context count.</returns>
    [PgFunction]
    public static string[] ConversionFailure()
    {
        string failure = Failure(() => Spi.ExecuteScalars<PgCStringView, int>(
            "SELECT cstrings.create_cstring('\\xff'::bytea),'invalid integer'::text"));
        return [failure, OwnerCount().ToString(System.Globalization.CultureInfo.InvariantCulture)];
    }
}
