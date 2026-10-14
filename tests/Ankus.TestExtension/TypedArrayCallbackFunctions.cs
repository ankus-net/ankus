namespace Ankus.TestExtension;

public static partial class BorrowedArrayFunctions
{
    private static PgArrayView<PgTextView?>? s_generatedArray;
    private static PgTextView? s_generatedCell;
    private static IEnumerator<PgTextView?>? s_generatedCursor;

    /// <summary>
    /// Transfers the checked original integer array before the input callback expires.
    /// </summary>
    [PgFunction]
    public static PgArrayView<int?>? TypedArrayIdentity(PgArrayView<int?>? value) => value;

    /// <summary>
    /// Transfers native text arrays without copying or converting their individual cells.
    /// </summary>
    [PgFunction]
    public static PgArrayView<PgTextView?>? TypedTextArrayIdentity(PgArrayView<PgTextView?>? value) => value;

    /// <summary>
    /// Transfers native bytea arrays including nullable and empty binary cells.
    /// </summary>
    [PgFunction]
    public static PgArrayView<PgByteaView?>? TypedByteaArrayIdentity(PgArrayView<PgByteaView?>? value) => value;

    /// <summary>
    /// Reads through a one-way converter while returning the original unchanged native storage.
    /// </summary>
    [PgFunction]
    public static PgArrayView<ReadMappedInt?>? TypedReadOnlyArrayIdentity(PgArrayView<ReadMappedInt?>? value)
    {
        if (value is { Count: > 0 } && value[0]?.Value != 107)
        {
            throw new InvalidOperationException("The read-only element converter was not applied.");
        }

        return value;
    }

    /// <summary>
    /// Returns a statically named composite array through its declared SQL contract.
    /// </summary>
    [PgFunction(Requires = ["composite-types"])]
    [return: PgCompositeType("dog", Schema = "tuple_values")]
    public static PgArrayView<PgHeapTuple?>? TypedCompositeArrayIdentity(
        [PgCompositeType("dog", Schema = "tuple_values")] PgArrayView<PgHeapTuple?>? value) => value;

    /// <summary>
    /// Returns arrays of generated enum and native-layout types through closed element registrations.
    /// </summary>
    [PgFunction]
    public static PgArrayView<EnumMood?>? TypedEnumArrayIdentity(PgArrayView<EnumMood?>? value) => value;

    /// <summary>
    /// Returns a generated custom type array without reconstructing any native element.
    /// </summary>
    [PgFunction]
    public static PgArrayView<CustomTypeFunctions.Number?>? TypedCustomArrayIdentity(PgArrayView<CustomTypeFunctions.Number?>? value) => value;

    /// <summary>
    /// Requires a completely present integer array before managed execution begins.
    /// </summary>
    [PgFunction]
    public static int TypedArrayRequired(PgArrayView<int> value) => value.Count;

    /// <summary>
    /// Builds a flat array of squares in place, reads it back through a view as pgrx's <c>read_array_back</c> does, and
    /// returns it.
    /// </summary>
    /// <param name="count">The number of elements; zero builds an empty array.</param>
    /// <returns>The squares of 0 through count - 1.</returns>
    [PgFunction]
    public static PgArrayView<int> FlatArraySquares(int count)
    {
        PgFlatArray<int> array = PgMemoryContext.Current.CreateFlatArray<int>(count == 0 ? [] : [count]);
        Span<int> cells = array.DangerousGetSpan();
        for (int index = 0; index < cells.Length; index++)
        {
            cells[index] = index * index;
        }

        var view = new PgArrayView<int>(array.Datum);
        int position = 0;
        foreach (int value in view)
        {
            if (value != position * position)
            {
                throw new InvalidOperationException("The flat array did not read back its own values.");
            }

            position++;
        }

        return position == count ? view : throw new InvalidOperationException("The flat array read back the wrong count.");
    }

    /// <summary>
    /// Builds a two-dimensional flat bigint array with explicit lower bounds.
    /// </summary>
    /// <returns>A 2 by 3 array starting at [-1][5] counting down from the largest bigint.</returns>
    [PgFunction]
    public static PgArrayView<long> FlatArrayGrid()
    {
        PgFlatArray<long> array = PgMemoryContext.Current.CreateFlatArray<long>([2, 3], [-1, 5]);
        Span<long> cells = array.DangerousGetSpan();
        for (int index = 0; index < cells.Length; index++)
        {
            cells[index] = long.MaxValue - index;
        }

        return new PgArrayView<long>(array.Datum);
    }

    /// <summary>
    /// Builds flat arrays of each remaining element type and formats them through SPI.
    /// </summary>
    /// <returns>The PostgreSQL text of the "char", smallint, oid, real, double precision and boolean arrays.</returns>
    [PgFunction]
    public static string FlatArrayElementTypes()
    {
        PgMemoryContext context = PgMemoryContext.Current;
        PgFlatArray<sbyte> chars = context.CreateFlatArray<sbyte>([2]);
        chars.DangerousGetSpan()[0] = 65;
        chars.DangerousGetSpan()[1] = 122;
        PgFlatArray<short> shorts = context.CreateFlatArray<short>([2]);
        shorts.DangerousGetSpan()[0] = short.MinValue;
        shorts.DangerousGetSpan()[1] = short.MaxValue;
        PgFlatArray<uint> oids = context.CreateFlatArray<uint>([1]);
        oids.DangerousGetSpan()[0] = uint.MaxValue;
        PgFlatArray<float> singles = context.CreateFlatArray<float>([2]);
        singles.DangerousGetSpan()[0] = -0.5f;
        singles.DangerousGetSpan()[1] = float.PositiveInfinity;
        PgFlatArray<double> doubles = context.CreateFlatArray<double>([3]);
        doubles.DangerousGetSpan()[1] = 1.5;
        doubles.DangerousGetSpan()[2] = double.NaN;
        PgFlatArray<bool> flags = context.CreateFlatArray<bool>([3]);
        flags.DangerousGetSpan()[0] = true;
        flags.DangerousGetSpan()[2] = true;
        return string.Join('|', new[] { chars.Datum, shorts.Datum, oids.Datum, singles.Datum, doubles.Datum, flags.Datum }
            .Select(static datum => Spi.ExecuteScalar<string>("SELECT $1::text || ':' || pg_typeof($1)::text", SpiParameter.Create(datum))));
    }

    /// <summary>
    /// Counts true cells of a borrowed boolean array, as pgrx's <c>borrow_count_true</c> does.
    /// </summary>
    [PgFunction]
    public static int TypedCountTrue(PgArrayView<bool> values) => values.Count(static value => value);

    /// <summary>
    /// Returns a separately selected native array to exercise exact declared-result identity checking.
    /// </summary>
    [PgFunction]
    public static PgArrayView<int?> TypedArrayReturnQuery(string sql) => Spi.ExecuteScalar<PgArrayView<int?>>(sql);

    /// <summary>
    /// Captures a typed array and borrowed text cells under a generated scalar callback lease.
    /// </summary>
    [PgFunction]
    public static string? TypedArrayCallbackSave(PgArrayView<PgTextView?> value, bool fail)
    {
        s_generatedArray = value;
        s_generatedCursor = value.GetEnumerator();
        _ = s_generatedCursor.MoveNext();
        s_generatedCell = s_generatedCursor.Current;
        if (fail)
        {
            throw new InvalidOperationException("Typed array callback failed.");
        }

        return s_generatedCell?.ToString();
    }

    /// <summary>
    /// Observes each escaped alias in a later callback without reviving its original source.
    /// </summary>
    [PgFunction]
    public static string[] TypedArrayCallbackExpired()
        => [Failure(() => _ = s_generatedArray!.Datum), Failure(() => s_generatedCell!.ToString()),
            Failure(() => s_generatedCursor!.MoveNext()), Failure(() => _ = s_generatedCursor!.Current)];

    /// <summary>
    /// Nested callback expiration leaves the enclosing array and already converted cell alive.
    /// </summary>
    [PgFunction]
    public static string?[] TypedArrayNested(PgArrayView<PgTextView?> value)
    {
        using IEnumerator<PgTextView?> cursor = value.GetEnumerator();
        _ = cursor.MoveNext();
        using PgTextView? cell = cursor.Current;
        string? before = cell?.ToString();
        string nested = Spi.ExecuteScalar<string>("SELECT borrowed_arrays.typed_array_callback_save(ARRAY['nested'],false)");
        return [before, nested, Failure(() => s_generatedCell!.ToString()), cell?.ToString(),
            ReferenceEquals(cell, cursor.Current) ? "same cell" : "different cell"];
    }

    /// <summary>
    /// Retains an independently copied typed array across multiple lazy set callbacks.
    /// </summary>
    [PgFunction]
    public static IEnumerable<PgArrayView<int?>?> TypedArrayRows(PgArrayView<int?>? value, bool fail)
    {
        using (value)
        {
            yield return value;
            if (fail)
            {
                throw new InvalidOperationException("Typed array iterator failed.");
            }

            yield return null;
            yield return value;
        }
    }

    /// <summary>
    /// Preserves an input snapshot and concrete array identity for each TABLE result column.
    /// </summary>
    [PgFunction]
    public static IEnumerable<(PgArrayView<ReadMappedInt?>? Cell, int Position)> TypedArrayTable(PgArrayView<ReadMappedInt?>? value)
    {
        if (value is null)
        {
            yield return (null, -1);
            yield return (null, -1);
            yield break;
        }

        using (value)
        {
            yield return (value, value[0]?.Value ?? -1);
            yield return (value, value[2]?.Value ?? -1);
        }
    }

    /// <summary>
    /// Keeps the first present typed array alive until the aggregate returns its exact native value.
    /// </summary>
    [PgAggregate(Name = "typed_array_first")]
    public sealed class FirstTypedArray : IPgAggregate<PgAggregateState<PgArrayView<int?>>?, PgArrayView<int?>?>,
        IPgFinalizingAggregate<PgAggregateState<PgArrayView<int?>>?, ValueTuple, PgArrayView<int?>?>
    {
        /// <summary>
        /// Retains the first snapshot and releases later inputs without traversing their cells.
        /// </summary>
        public static PgAggregateState<PgArrayView<int?>>? Transition(PgAggregateContext context,
            PgAggregateState<PgArrayView<int?>>? state, PgArrayView<int?>? value)
        {
            if (state is null && value is not null)
            {
                return new(value);
            }

            value?.Dispose();
            return state;
        }

        /// <summary>
        /// Returns the stored native array after the callbacks that created it have ended.
        /// </summary>
        public static PgArrayView<int?>? Final(PgAggregateContext context, PgAggregateState<PgArrayView<int?>>? state, ValueTuple arguments) => state?.Value;
    }

    /// <summary>
    /// Uses a concrete SQL array as aggregate state, independently of managed internal state ownership.
    /// </summary>
    [PgAggregate(Name = "typed_array_sql_first")]
    public sealed class FirstTypedSqlArray : IPgAggregate<PgArrayView<int?>?, PgArrayView<int?>?>,
        IPgFinalizingAggregate<PgArrayView<int?>?, ValueTuple, PgArrayView<int?>?>
    {
        /// <summary>
        /// Transfers the first present array state and releases an unused copied input.
        /// </summary>
        public static PgArrayView<int?>? Transition(PgAggregateContext context, PgArrayView<int?>? state, PgArrayView<int?>? value)
        {
            if (state is null)
            {
                return value;
            }

            value?.Dispose();
            return state;
        }

        /// <summary>
        /// Returns the final concrete SQL array state.
        /// </summary>
        public static PgArrayView<int?>? Final(PgAggregateContext context, PgArrayView<int?>? state, ValueTuple arguments) => state;
    }
}
