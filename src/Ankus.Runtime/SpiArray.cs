using System;
using System.Collections.Generic;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Statically closes supported array conversions so Native AOT needs no runtime type construction.
/// </summary>
internal static class SpiArray
{
    /// <summary>
    /// Explains why a multidimensional or offset array cannot become a managed vector.
    /// </summary>
    internal const string VectorShapeMessage =
        "Use PgArray<T> to preserve dimensions and lower bounds, or ToArray() to explicitly flatten them.";

    /// <summary>
    /// Maps each supported vector and <see cref="PgArray{T}"/> type to the built-in OID of its element type.
    /// </summary>
    private static readonly Dictionary<Type, uint> s_elementOids = CreateElementOids();

    /// <summary>
    /// Validates PostgreSQL dimension limits, exclusive upper bounds, and the row-major element count.
    /// </summary>
    /// <param name="count">The supplied number of elements, including NULLs.</param>
    /// <param name="lengths">Zero through six dimension lengths.</param>
    /// <param name="lowerBounds">One lower bound per dimension, or an empty span to use one throughout.</param>
    internal static void ValidateShape(int count, ReadOnlySpan<int> lengths, ReadOnlySpan<int> lowerBounds)
    {
        if (lengths.Length > 6 || (!lowerBounds.IsEmpty && lowerBounds.Length != lengths.Length))
        {
            throw new ArgumentException("Arrays have at most six dimensions and one lower bound per dimension.", nameof(lengths));
        }

        long product = lengths.IsEmpty ? 0 : 1;
        for (int index = 0; index < lengths.Length; index++)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(lengths[index]);
            int lower = lowerBounds.IsEmpty ? 1 : lowerBounds[index];
            if ((long)lower + lengths[index] > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(lowerBounds), "A dimension's exclusive upper bound exceeds PostgreSQL's range.");
            }

            product = checked(product * lengths[index]);
            if (product > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(lengths), "The array exceeds managed element capacity.");
            }
        }

        if (product != count)
        {
            throw new ArgumentException("The dimension lengths must describe exactly the supplied elements.", nameof(lengths));
        }
    }

    /// <summary>
    /// Maps a supported built-in scalar OID to its PostgreSQL array OID.
    /// </summary>
    /// <param name="element">The scalar element OID.</param>
    /// <returns>The array OID.</returns>
    /// <exception cref="NotSupportedException">The scalar type has no supported array mapping.</exception>
    internal static uint ArrayOid(uint element) => element switch
    {
        16 => 1000,
        17 => 1001,
        18 => 1002,
        20 => 1016,
        21 => 1005,
        23 => 1007,
        25 => 1009,
        1043 => 1015,
        2275 => 1263,
        26 => 1028,
        27 => 1010,
        28 => 1011,
        700 => 1021,
        701 => 1022,
        2950 => 2951,
        114 => 199,
        3802 => 3807,
        1700 => 1231,
        1082 => 1182,
        1083 => 1183,
        1266 => 1270,
        1114 => 1115,
        1184 => 1185,
        1186 => 1187,
        869 => 1041,
        650 => 651,
        600 => 1017,
        601 => 1018,
        602 => 1019,
        603 => 1020,
        604 => 1027,
        628 => 629,
        718 => 719,
        3904 => 3905,
        3926 => 3927,
        3906 => 3907,
        3912 => 3913,
        3908 => 3909,
        3910 => 3911,
        2249 => 2287,
        2205 => 2210,
        _ => throw new NotSupportedException($"PostgreSQL type OID {element} is not a supported array element."),
    };

    /// <summary>
    /// Resolves a statically supported vector or shape-preserving array type without constructing generic types at runtime.
    /// </summary>
    /// <param name="type">The declared managed array type, including nullable value-type elements.</param>
    /// <returns>The built-in PostgreSQL array OID, or zero when the type is unsupported.</returns>
    internal static uint GetOid(Type type)
    {
        uint element = s_elementOids.GetValueOrDefault(type);
        return element == 0 ? 0 : ArrayOid(element);
    }

    /// <summary>
    /// Converts an owned array to a supported managed array type, rejecting element or shape loss.
    /// </summary>
    /// <param name="array">The source array and its PostgreSQL shape.</param>
    /// <param name="type">The requested vector or shape-preserving array type.</param>
    /// <returns>The typed array, reusing the source when its type already matches.</returns>
    internal static object Convert(IPgArray array, Type type)
    {
        PgDatumRegistry.RejectOrdinaryArray(array.GetType());
        PgDatumRegistry.RejectOrdinaryArray(type);
        return ConvertCore(array, type);
    }

    /// <summary>
    /// Applies ordinary statically closed conversions after rejecting datum-mapped containers.
    /// </summary>
    private static object ConvertCore(IPgArray array, Type type)
        => PgTypeRegistry.FindArray(type)?.Convert(array, type) ?? PgEnumRegistry.FindArray(type)?.Convert(array, type) ??
           Convert<PgRelation>(array, type) ?? Convert<PgHeapTuple>(array, type) ?? Convert<PgRange<int>>(array, type) ?? Convert<PgRange<long>>(array, type) ?? Convert<PgRange<PgNumeric>>(array, type) ?? Convert<PgRange<decimal>>(array, type) ??
           Convert<PgRange<PgDate>>(array, type) ?? Convert<PgRange<DateOnly>>(array, type) ?? Convert<PgRange<PgTimestamp>>(array, type) ??
           Convert<PgRange<DateTime>>(array, type) ?? Convert<PgRange<PgTimestampTz>>(array, type) ?? Convert<PgRange<DateTimeOffset>>(array, type) ??
           Convert<PgPoint>(array, type) ?? Convert<PgPoint?>(array, type) ?? Convert<PgLineSegment>(array, type) ?? Convert<PgLineSegment?>(array, type) ??
           Convert<PgPath>(array, type) ?? Convert<PgBox>(array, type) ?? Convert<PgBox?>(array, type) ?? Convert<PgPolygon>(array, type) ??
           Convert<PgLine>(array, type) ?? Convert<PgLine?>(array, type) ?? Convert<PgCircle>(array, type) ?? Convert<PgCircle?>(array, type) ??
           Convert<PgInet>(array, type) ?? Convert<PgInet?>(array, type) ?? Convert<System.Net.IPAddress>(array, type) ??
           Convert<PgCidr>(array, type) ?? Convert<PgCidr?>(array, type) ?? Convert<System.Net.IPNetwork>(array, type) ?? Convert<System.Net.IPNetwork?>(array, type) ??
           Convert<bool>(array, type) ?? Convert<bool?>(array, type) ?? Convert<byte[]>(array, type) ??
           Convert<sbyte>(array, type) ?? Convert<sbyte?>(array, type) ?? Convert<short>(array, type) ?? Convert<short?>(array, type) ??
           Convert<int>(array, type) ?? Convert<int?>(array, type) ?? Convert<long>(array, type) ?? Convert<long?>(array, type) ??
           Convert<uint>(array, type) ?? Convert<uint?>(array, type) ?? Convert<float>(array, type) ?? Convert<float?>(array, type) ??
           Convert<PgItemPointer>(array, type) ?? Convert<PgItemPointer?>(array, type) ??
           Convert<PgTransactionId>(array, type) ?? Convert<PgTransactionId?>(array, type) ??
           Convert<double>(array, type) ?? Convert<double?>(array, type) ?? Convert<string>(array, type) ?? Convert<PgCString>(array, type) ??
           Convert<Guid>(array, type) ?? Convert<Guid?>(array, type) ?? Convert<PgJson>(array, type) ?? Convert<PgJson?>(array, type) ??
           Convert<System.Text.Rune>(array, type) ?? Convert<System.Text.Rune?>(array, type) ??
           Convert<PgJsonb>(array, type) ?? Convert<PgJsonb?>(array, type) ?? Convert<PgNumeric>(array, type) ?? Convert<PgNumeric?>(array, type) ??
           Convert<decimal>(array, type) ?? Convert<decimal?>(array, type) ?? Convert<PgDate>(array, type) ?? Convert<PgDate?>(array, type) ??
           Convert<DateOnly>(array, type) ?? Convert<DateOnly?>(array, type) ?? Convert<PgTime>(array, type) ?? Convert<PgTime?>(array, type) ??
           Convert<TimeOnly>(array, type) ?? Convert<TimeOnly?>(array, type) ?? Convert<PgTimeTz>(array, type) ?? Convert<PgTimeTz?>(array, type) ??
           Convert<PgTimestamp>(array, type) ?? Convert<PgTimestamp?>(array, type) ?? Convert<DateTime>(array, type) ?? Convert<DateTime?>(array, type) ??
           Convert<PgTimestampTz>(array, type) ?? Convert<PgTimestampTz?>(array, type) ?? Convert<DateTimeOffset>(array, type) ?? Convert<DateTimeOffset?>(array, type) ??
           Convert<PgInterval>(array, type) ?? Convert<PgInterval?>(array, type) ?? Convert<TimeSpan>(array, type) ?? Convert<TimeSpan?>(array, type) ??
           throw new InvalidCastException($"The PostgreSQL array cannot be read as '{type}'.");

    /// <summary>
    /// Copies a supported managed vector into an array with PostgreSQL's default lower bound of one.
    /// </summary>
    /// <param name="value">The vector; binary elements remain managed byte-array references.</param>
    /// <returns>The shape-preserving array, with rank zero for an empty vector.</returns>
    internal static IPgArray Wrap(Array value)
    {
        PgDatumRegistry.RejectOrdinaryArray(value.GetType());
        return WrapCore(value);
    }

    /// <summary>
    /// Selects ordinary array representations after checking their actual closed managed identity.
    /// </summary>
    private static IPgArray WrapCore(Array value) => PgTypeRegistry.FindArray(value.GetType())?.Wrap(value) ?? PgEnumRegistry.FindArray(value.GetType())?.Wrap(value) ?? value switch
    {
        PgCString[] items => new PgArray<PgCString>(items),
        PgHeapTuple[] items => new PgArray<PgHeapTuple>(items),
        PgRelation[] items => new PgArray<PgRelation>(items),
        PgRange<int>[] items => new PgArray<PgRange<int>>(items),
        PgRange<long>[] items => new PgArray<PgRange<long>>(items),
        PgRange<PgNumeric>[] items => new PgArray<PgRange<PgNumeric>>(items),
        PgRange<decimal>[] items => new PgArray<PgRange<decimal>>(items),
        PgRange<PgDate>[] items => new PgArray<PgRange<PgDate>>(items),
        PgRange<DateOnly>[] items => new PgArray<PgRange<DateOnly>>(items),
        PgRange<PgTimestamp>[] items => new PgArray<PgRange<PgTimestamp>>(items),
        PgRange<DateTime>[] items => new PgArray<PgRange<DateTime>>(items),
        PgRange<PgTimestampTz>[] items => new PgArray<PgRange<PgTimestampTz>>(items),
        PgRange<DateTimeOffset>[] items => new PgArray<PgRange<DateTimeOffset>>(items),
        PgPoint[] items => new PgArray<PgPoint>(items),
        PgPoint?[] items => new PgArray<PgPoint?>(items),
        PgLineSegment[] items => new PgArray<PgLineSegment>(items),
        PgLineSegment?[] items => new PgArray<PgLineSegment?>(items),
        PgPath[] items => new PgArray<PgPath>(items),
        PgPolygon[] items => new PgArray<PgPolygon>(items),
        PgBox[] items => new PgArray<PgBox>(items),
        PgBox?[] items => new PgArray<PgBox?>(items),
        PgLine[] items => new PgArray<PgLine>(items),
        PgLine?[] items => new PgArray<PgLine?>(items),
        PgCircle[] items => new PgArray<PgCircle>(items),
        PgCircle?[] items => new PgArray<PgCircle?>(items),
        PgInet[] items => new PgArray<PgInet>(items),
        PgInet?[] items => new PgArray<PgInet?>(items),
        PgCidr[] items => new PgArray<PgCidr>(items),
        PgCidr?[] items => new PgArray<PgCidr?>(items),
        System.Net.IPAddress[] items => new PgArray<System.Net.IPAddress>(items),
        System.Net.IPNetwork[] items => new PgArray<System.Net.IPNetwork>(items),
        System.Net.IPNetwork?[] items => new PgArray<System.Net.IPNetwork?>(items),
        bool[] items => new PgArray<bool>(items),
        bool?[] items => new PgArray<bool?>(items),
        byte[][] items => new PgArray<byte[]>(items),
        string[] items => new PgArray<string>(items),
        System.Text.Rune[] items => new PgArray<System.Text.Rune>(items),
        System.Text.Rune?[] items => new PgArray<System.Text.Rune?>(items),
        sbyte[] items => new PgArray<sbyte>(items),
        sbyte?[] items => new PgArray<sbyte?>(items),
        short[] items => new PgArray<short>(items),
        short?[] items => new PgArray<short?>(items),
        int[] items => new PgArray<int>(items),
        int?[] items => new PgArray<int?>(items),
        long[] items => new PgArray<long>(items),
        long?[] items => new PgArray<long?>(items),
        uint[] items => new PgArray<uint>(items),
        uint?[] items => new PgArray<uint?>(items),
        PgItemPointer[] items => new PgArray<PgItemPointer>(items),
        PgItemPointer?[] items => new PgArray<PgItemPointer?>(items),
        PgTransactionId[] items => new PgArray<PgTransactionId>(items),
        PgTransactionId?[] items => new PgArray<PgTransactionId?>(items),
        float[] items => new PgArray<float>(items),
        float?[] items => new PgArray<float?>(items),
        double[] items => new PgArray<double>(items),
        double?[] items => new PgArray<double?>(items),
        Guid[] items => new PgArray<Guid>(items),
        Guid?[] items => new PgArray<Guid?>(items),
        PgJson[] items => new PgArray<PgJson>(items),
        PgJson?[] items => new PgArray<PgJson?>(items),
        PgJsonb[] items => new PgArray<PgJsonb>(items),
        PgJsonb?[] items => new PgArray<PgJsonb?>(items),
        PgNumeric[] items => new PgArray<PgNumeric>(items),
        PgNumeric?[] items => new PgArray<PgNumeric?>(items),
        decimal[] items => new PgArray<decimal>(items),
        decimal?[] items => new PgArray<decimal?>(items),
        PgDate[] items => new PgArray<PgDate>(items),
        PgDate?[] items => new PgArray<PgDate?>(items),
        DateOnly[] items => new PgArray<DateOnly>(items),
        DateOnly?[] items => new PgArray<DateOnly?>(items),
        PgTime[] items => new PgArray<PgTime>(items),
        PgTime?[] items => new PgArray<PgTime?>(items),
        TimeOnly[] items => new PgArray<TimeOnly>(items),
        TimeOnly?[] items => new PgArray<TimeOnly?>(items),
        PgTimeTz[] items => new PgArray<PgTimeTz>(items),
        PgTimeTz?[] items => new PgArray<PgTimeTz?>(items),
        PgTimestamp[] items => new PgArray<PgTimestamp>(items),
        PgTimestamp?[] items => new PgArray<PgTimestamp?>(items),
        DateTime[] items => new PgArray<DateTime>(items),
        DateTime?[] items => new PgArray<DateTime?>(items),
        PgTimestampTz[] items => new PgArray<PgTimestampTz>(items),
        PgTimestampTz?[] items => new PgArray<PgTimestampTz?>(items),
        DateTimeOffset[] items => new PgArray<DateTimeOffset>(items),
        DateTimeOffset?[] items => new PgArray<DateTimeOffset?>(items),
        PgInterval[] items => new PgArray<PgInterval>(items),
        PgInterval?[] items => new PgArray<PgInterval?>(items),
        TimeSpan[] items => new PgArray<TimeSpan>(items),
        TimeSpan?[] items => new PgArray<TimeSpan?>(items),
        _ => throw new NotSupportedException("Use a supported scalar vector or PgArray<T> for a PostgreSQL array."),
    };

    /// <summary>
    /// Determines whether binary-compatible text and varchar elements can be read as strings or runes.
    /// </summary>
    /// <typeparam name="T">The requested element type.</typeparam>
    /// <param name="elementOid">The array's element type.</param>
    /// <returns>Whether the elements are character text the requested type reads.</returns>
    private static bool ReadsCharacterText<T>(uint elementOid)
        => elementOid is 25 or 1043 &&
            (typeof(T) == typeof(string) || typeof(T) == typeof(System.Text.Rune) || typeof(T) == typeof(System.Text.Rune?));

    /// <summary>
    /// Preserves array shape while applying exact scalar conversions and NULL checks to each element.
    /// </summary>
    /// <typeparam name="T">The requested scalar element type.</typeparam>
    /// <param name="array">The owned source array.</param>
    /// <returns>The original array when its type matches, otherwise a new typed copy.</returns>
    internal static PgArray<T> Cast<T>(IPgArray array)
    {
        PgDatumRegistry.RejectOrdinaryArray(array.GetType());
        PgDatumRegistry.RejectOrdinaryArray(typeof(PgArray<T>));
        if (array is PgArray<T> typed)
        {
            return typed;
        }

        if (PgEnumRegistry.FindArray(array.GetType()) is not null || PgTypeRegistry.FindArray(array.GetType()) is not null ||
            (SpiType.GetOid<T>() != array.ElementOid && !ReadsCharacterText<T>(array.ElementOid)))
        {
            throw new InvalidCastException($"Array elements cannot be read as '{typeof(T)}'.");
        }

        var values = new T[array.Count];
        try
        {
            for (int index = 0; index < values.Length; index++)
            {
                values[index] = SpiRow.Convert<T>(array.GetElement(index));
            }

            return new PgArray<T>(values, ([.. array.Lengths], [.. array.LowerBounds]));
        }
        catch (Exception primary)
        {
            NativeRelationScope.Release(values, primary);
            throw;
        }
    }

    /// <summary>
    /// Lists every statically supported element type under both of its array forms.
    /// </summary>
    private static Dictionary<Type, uint> CreateElementOids()
    {
        var oids = new Dictionary<Type, uint>();
        Add<PgRelation>(2205);
        Add<PgRelationIdentity>(2205);
        Add<PgRelationIdentity?>(2205);
        Add<PgHeapTuple>(2249);
        Add<PgRange<int>>(3904);
        Add<PgRange<long>>(3926);
        Add<PgRange<PgNumeric>>(3906);
        Add<PgRange<decimal>>(3906);
        Add<PgRange<PgDate>>(3912);
        Add<PgRange<DateOnly>>(3912);
        Add<PgRange<PgTimestamp>>(3908);
        Add<PgRange<DateTime>>(3908);
        Add<PgRange<PgTimestampTz>>(3910);
        Add<PgRange<DateTimeOffset>>(3910);
        Add<PgPoint>(600);
        Add<PgPoint?>(600);
        Add<PgLineSegment>(601);
        Add<PgLineSegment?>(601);
        Add<PgPath>(602);
        Add<PgBox>(603);
        Add<PgBox?>(603);
        Add<PgPolygon>(604);
        Add<PgLine>(628);
        Add<PgLine?>(628);
        Add<PgCircle>(718);
        Add<PgCircle?>(718);
        Add<PgInet>(869);
        Add<PgInet?>(869);
        Add<System.Net.IPAddress>(869);
        Add<PgCidr>(650);
        Add<PgCidr?>(650);
        Add<System.Net.IPNetwork>(650);
        Add<System.Net.IPNetwork?>(650);
        Add<bool>(16);
        Add<bool?>(16);
        Add<byte[]>(17);
        Add<sbyte>(18);
        Add<sbyte?>(18);
        Add<long>(20);
        Add<long?>(20);
        Add<short>(21);
        Add<short?>(21);
        Add<int>(23);
        Add<int?>(23);
        Add<string>(25);
        Add<PgCString>(2275);
        Add<uint>(26);
        Add<uint?>(26);
        Add<PgItemPointer>(27);
        Add<PgItemPointer?>(27);
        Add<PgTransactionId>(28);
        Add<PgTransactionId?>(28);
        Add<float>(700);
        Add<float?>(700);
        Add<double>(701);
        Add<double?>(701);
        Add<Guid>(2950);
        Add<Guid?>(2950);
        Add<System.Text.Rune>(1043);
        Add<System.Text.Rune?>(1043);
        Add<PgJson>(114);
        Add<PgJson?>(114);
        Add<PgJsonb>(3802);
        Add<PgJsonb?>(3802);
        Add<PgNumeric>(1700);
        Add<PgNumeric?>(1700);
        Add<decimal>(1700);
        Add<decimal?>(1700);
        Add<PgDate>(1082);
        Add<PgDate?>(1082);
        Add<DateOnly>(1082);
        Add<DateOnly?>(1082);
        Add<PgTime>(1083);
        Add<PgTime?>(1083);
        Add<TimeOnly>(1083);
        Add<TimeOnly?>(1083);
        Add<PgTimeTz>(1266);
        Add<PgTimeTz?>(1266);
        Add<PgTimestamp>(1114);
        Add<PgTimestamp?>(1114);
        Add<DateTime>(1114);
        Add<DateTime?>(1114);
        Add<PgTimestampTz>(1184);
        Add<PgTimestampTz?>(1184);
        Add<DateTimeOffset>(1184);
        Add<DateTimeOffset?>(1184);
        Add<PgInterval>(1186);
        Add<PgInterval?>(1186);
        Add<TimeSpan>(1186);
        Add<TimeSpan?>(1186);
        return oids;

        void Add<T>(uint oid)
        {
            oids.Add(typeof(T[]), oid);
            oids.Add(typeof(PgArray<T>), oid);
        }
    }

    private static bool Matches<T>(Type type) => type == typeof(T[]) || type == typeof(PgArray<T>);

    private static object? Convert<T>(IPgArray array, Type type)
    {
        if (!Matches<T>(type))
        {
            return null;
        }

        if (typeof(T) == typeof(PgRelation) && array.ElementOid == 2205 && type == typeof(T[]) &&
            (array.Lengths.Length > 1 || array.LowerBounds.Length == 1 && array.LowerBounds[0] != 1))
        {
            throw new InvalidOperationException("Use PgArray<T> to preserve dimensions and lower bounds, or ToArray() to explicitly flatten them.");
        }

        PgArray<T> result = Cast<T>(array);
        try
        {
            return type == typeof(T[]) ? result.ToVector() : result;
        }
        catch (Exception primary)
        {
            if (!ReferenceEquals(result, array))
            {
                NativeRelationScope.Release(result, primary);
            }

            throw;
        }
    }
}
