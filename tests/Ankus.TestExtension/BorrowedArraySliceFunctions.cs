using System.Globalization;

namespace Ankus.TestExtension;

public static partial class BorrowedArrayFunctions
{
    /// <summary>
    /// Copies exact values from native slices before returning to PostgreSQL.
    /// </summary>
    /// <param name="value">The direct borrowed array.</param>
    /// <param name="mode">The requested scalar layout, including an explicit UUID-byte layout.</param>
    /// <returns>Original type/shape metadata followed by literal scalar values or floating point bits.</returns>
    [PgFunction]
    public static string[] ArraySliceSnapshot(PgArrayView value, int mode)
        => [value.TypeOid.ToString(CultureInfo.InvariantCulture), value.ElementTypeOid.ToString(CultureInfo.InvariantCulture),
            string.Join(',', value.Lengths.ToArray()), string.Join(',', value.LowerBounds.ToArray()), .. SliceValues(value, mode)];

    /// <summary>
    /// Compares a borrowed span to the native payload address observed before managed conversion.
    /// </summary>
    /// <param name="value">The generated borrowed input.</param>
    /// <param name="address">The independently observed payload address.</param>
    /// <returns>Whether the span refers to the original contiguous payload.</returns>
    [PgFunction]
    public static bool ArraySliceAddress(PgArrayView value, long address)
    {
        return (PgBuiltInOid)value.ElementTypeOid switch
        {
            PgBuiltInOid.CharOid => SliceAddress(value.DangerousGetSpan<sbyte>(), address),
            PgBuiltInOid.Int2Oid => SliceAddress(value.DangerousGetSpan<short>(), address),
            PgBuiltInOid.Int4Oid => SliceAddress(value.DangerousGetSpan<int>(), address),
            PgBuiltInOid.Int8Oid => SliceAddress(value.DangerousGetSpan<long>(), address),
            PgBuiltInOid.Float4Oid => SliceAddress(value.DangerousGetSpan<float>(), address),
            PgBuiltInOid.Float8Oid => SliceAddress(value.DangerousGetSpan<double>(), address),
            PgBuiltInOid.UuidOid => SliceAddress(value.DangerousGetUuidBytes(), address),
            _ => throw new InvalidOperationException("Unexpected native slice witness type."),
        };
    }

    /// <summary>
    /// Exposes exact NULL, type and layout rejection while proving same-callback recovery.
    /// </summary>
    /// <param name="value">The array with the deliberately incompatible element contract.</param>
    /// <param name="mode">The requested native layout.</param>
    /// <returns>The exact error and the result of an independent query after recovery.</returns>
    [PgFunction]
    public static string[] ArraySliceFailure(PgArrayView value, int mode)
        => [Failure(() => SliceValues(value, mode)), Spi.ExecuteScalar<int>("SELECT 42").ToString(CultureInfo.InvariantCulture)];

    /// <summary>
    /// Reads existing domain storage without rebinding its value through the domain constraints.
    /// </summary>
    /// <param name="value">The original domain over an array of domain elements.</param>
    /// <returns>Original domain identities and exact integer slice contents.</returns>
    [PgFunction]
    public static string[] ArraySliceDomain([PgSqlType("any", Schema = "pg_catalog")] PgDatum value)
    {
        using var view = new PgArrayView(value);
        return ArraySliceSnapshot(view, 2);
    }

    /// <summary>
    /// Invalidates original and nested slice views while retaining an explicit independent managed copy.
    /// </summary>
    /// <param name="mode">View disposal, source reset, source deletion or source-only reset.</param>
    /// <returns>Exact expiry diagnostics and copied scalar values.</returns>
    [PgFunction]
    public static string[] ArraySliceOwners(int mode)
    {
        using PgMemoryContext source = PgMemoryContext.Create("native slice source");
        using SpiRawResult result = Spi.QueryRaw("SELECT ARRAY[-7,0,19]");
        using var first = new PgArrayView(result[0][0].CopyTo(source));
        using var nested = new PgArrayView(first.Datum);
        int[] copy = nested.DangerousGetSpan<int>().ToArray();
        switch (mode)
        {
            case 0:
                first.Dispose();
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

        return [Failure(() => first.DangerousGetSpan<int>()), Failure(() => nested.DangerousGetSpan<int>()),
            string.Join(',', copy), nested.Count.ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// Exercises flat, packed, compressed, external and expanded integer payload ownership.
    /// </summary>
    /// <param name="bits">The caller-retained physical datum address.</param>
    /// <param name="type">The original SQL type.</param>
    /// <param name="kind">The native storage form observed before conversion.</param>
    /// <returns>Storage identity, owner, literal sum and exact cleanup counts.</returns>
    [PgFunction]
    public static string[] ArraySliceNative(long bits, uint type, string kind)
    {
        PgDatum source;
        unsafe
        {
            source = PgDatum.DangerousCreate(unchecked((nuint)bits), type, PgMemoryContext.Current);
        }

        long before = OwnerCount();
        string owner = "borrowed";
        bool copied = false;
        long total = 0;
        int count = 0;
        for (int iteration = 0; iteration < 3; iteration++)
        {
            using var value = new PgArrayView(source);
            copied = value.Datum.DangerousGetBits() != source.DangerousGetBits();
            ReadOnlySpan<int> values = value.DangerousGetSpan<int>();
            count = values.Length;
            total = 0;
            foreach (int cell in values)
            {
                total += cell;
            }

            if (copied)
            {
                owner = Spi.ExecuteScalar<string>("SELECT tests.array_owner($1)",
                    SpiParameter.Create(unchecked((long)value.Datum.DangerousGetBits())));
            }
        }

        return [kind, copied.ToString(), owner, count.ToString(CultureInfo.InvariantCulture),
            total.ToString(CultureInfo.InvariantCulture), before.ToString(CultureInfo.InvariantCulture),
            OwnerCount().ToString(CultureInfo.InvariantCulture)];
    }

    /// <summary>
    /// Copies scalar values without any backend call during the span borrow.
    /// </summary>
    private static string[] SliceValues(PgArrayView value, int mode)
    {
        switch (mode)
        {
            case 0:
                return FormatSlice(value.DangerousGetSpan<sbyte>());
            case 1:
                return FormatSlice(value.DangerousGetSpan<short>());
            case 2:
                return FormatSlice(value.DangerousGetSpan<int>());
            case 3:
                return FormatSlice(value.DangerousGetSpan<long>());
            case 4:
                return [.. value.DangerousGetSpan<float>().ToArray().Select(static cell =>
                BitConverter.SingleToInt32Bits(cell).ToString("X8", CultureInfo.InvariantCulture))];
            case 5:
                return [.. value.DangerousGetSpan<double>().ToArray().Select(static cell =>
                BitConverter.DoubleToInt64Bits(cell).ToString("X16", CultureInfo.InvariantCulture))];
            case 6:
                ReadOnlySpan<byte> bytes = value.DangerousGetUuidBytes();
                string[] values = new string[value.Count];
                for (int index = 0; index < values.Length; index++)
                {
                    values[index] = new Guid(bytes.Slice(index * 16, 16), bigEndian: true).ToString();
                }

                return values;
            case 7:
                return [value.DangerousGetSpan<Guid>().Length.ToString(CultureInfo.InvariantCulture)];
            default:
                throw new ArgumentOutOfRangeException(nameof(mode));
        }
    }

    /// <summary>
    /// Formats integer scalars using invariant decimal notation after copying to managed storage.
    /// </summary>
    private static string[] FormatSlice<T>(ReadOnlySpan<T> values) where T : unmanaged, IFormattable
        => [.. values.ToArray().Select(static cell => cell.ToString(null, CultureInfo.InvariantCulture))];

    /// <summary>
    /// Checks the original payload address for nonempty native spans without copying their elements.
    /// </summary>
    private static unsafe bool SliceAddress<T>(ReadOnlySpan<T> values, long address) where T : unmanaged
    {
        fixed (T* data = values)
        {
            return (long)data == address;
        }
    }
}
