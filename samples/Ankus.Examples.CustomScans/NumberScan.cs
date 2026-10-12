using System.Buffers.Binary;

namespace Ankus.Examples.CustomScans;

/// <summary>
/// Answers scans of tables named <c>numbers…</c> with the integers 1 to N from managed code, as a managed custom scan
/// provider.
/// </summary>
/// <remarks>
/// The table supplies the columns; its stored rows are ignored. Set <c>ankus_trace_scan.numbers</c> to N before
/// planning. A <c>value</c> column receives the integer and an optional <c>label</c> column its text. PostgreSQL still
/// applies the query's conditions and projection to every row.
/// </remarks>
[PgCustomScan]
public sealed partial class NumberScan : IPgRescannableCustomScan<NumberScan.State>, IPgExplainedCustomScan<NumberScan.State>
{
    private static readonly long[] s_counts = new long[4];

    /// <summary>
    /// Gets how many integers a numbers table produces; zero leaves those tables to PostgreSQL's own scans.
    /// </summary>
    [PgGucInt("ankus_trace_scan.numbers", 0, "Integers produced by the managed numbers scan", Minimum = 0, Maximum = 1_000_000)]
    public static partial int Numbers { get; }

    /// <summary>
    /// Gets the integer at which the scan fails, to show error recovery; zero never fails.
    /// </summary>
    [PgGucInt("ankus_trace_scan.numbers_fail_at", 0, "Integer at which the managed numbers scan raises an error", Minimum = 0)]
    public static partial int FailAt { get; }

    /// <inheritdoc />
    public static void Plan(PgCustomScanPlanner planner)
    {
        int count = Numbers;
        if (count == 0 || !planner.RelationName.StartsWith("numbers", StringComparison.Ordinal))
        {
            return;
        }

        byte[] data = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(data, count);
        planner.AddPath(new PgCustomScanPath(0, count * 0.001) { Rows = count, Data = data });
    }

    /// <inheritdoc />
    public static State Begin(PgCustomScanContext context)
    {
        s_counts[0]++;
        if (context.IsExplainOnly)
        {
            s_counts[3]++;
        }

        return new State(BinaryPrimitives.ReadInt32LittleEndian(context.Data), context.Descriptor);
    }

    /// <inheritdoc />
    public static bool Read(State state, PgHeapTuple row)
    {
        if (state.Next > state.Count)
        {
            return false;
        }

        long value = state.Next++;
        if (value == FailAt)
        {
            throw new InvalidOperationException($"The numbers scan failed at {value}.");
        }

        row.Set(state.Value, value);
        if (state.Label >= 0 && value % 3 != 0)
        {
            row.Set(state.Label, "number " + value.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        return true;
    }

    /// <inheritdoc />
    public static void Rescan(State state)
    {
        s_counts[2]++;
        state.Next = 1;
    }

    /// <inheritdoc />
    public static void Explain(State state, PgExplainWriter explain)
    {
        explain.Add("Numbers", state.Count);
        explain.Add("Numbers Read", state.Next - 1L);
    }

    /// <summary>
    /// Reads begin, disposal and rescan counts, and the begins made only for EXPLAIN, in this backend.
    /// </summary>
    /// <returns>Four counters in that order.</returns>
    [PgFunction]
    public static long[] NumberScanCounts() => [.. s_counts];

    /// <summary>
    /// Holds one scan's position.
    /// </summary>
    public sealed class State : IDisposable
    {
        internal State(int count, PgTupleDescriptor descriptor)
        {
            Count = count;
            Value = descriptor.GetOrdinal("value");
            Label = descriptor.Attributes.Select(static (attribute, index) => (attribute, index))
                .FirstOrDefault(static item => item.attribute.Name == "label" && !item.attribute.IsDropped, (null!, -1)).index;
        }

        /// <summary>
        /// Gets the number of integers the scan produces.
        /// </summary>
        public int Count { get; }

        /// <summary>
        /// Gets the integer the next row holds.
        /// </summary>
        public long Next
        {
            get;
            set;
        } = 1;

        internal int Value { get; }

        internal int Label { get; }

        /// <inheritdoc />
        public void Dispose() => s_counts[1]++;
    }
}
