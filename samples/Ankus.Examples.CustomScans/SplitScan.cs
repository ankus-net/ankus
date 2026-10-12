using System.Buffers.Binary;

namespace Ankus.Examples.CustomScans;

/// <summary>
/// Splits the integers 1 to N among the processes of a parallel scan, for tables whose names start with <c>split</c>,
/// with no capability beyond the parallel one.
/// </summary>
/// <remarks>
/// The provider offers only parallel paths, so tables that cannot be scanned in parallel keep PostgreSQL's own scans.
/// It cannot rescan in place: PostgreSQL restarts it by beginning a new state, which Ankus gives the same shared memory.
/// </remarks>
[PgCustomScan]
public sealed class SplitScan : IPgParallelCustomScan<SplitScan.State, NumberScan.Counter>
{
    private static readonly long[] s_counts = new long[3];

    /// <inheritdoc />
    public static void Plan(PgCustomScanPlanner planner)
    {
        int count = NumberScan.Numbers;
        if (count == 0 || !planner.RelationName.StartsWith("split", StringComparison.Ordinal))
        {
            return;
        }

        byte[] data = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(data, count);
        planner.AddParallelPath(new PgCustomScanPath(0, count * 0.001) { Rows = count, Data = data }, workers: 2);
    }

    /// <inheritdoc />
    public static State Begin(PgCustomScanContext context)
    {
        s_counts[0]++;
        return new State(BinaryPrimitives.ReadInt32LittleEndian(context.Data), context.Descriptor.GetOrdinal("value"));
    }

    /// <inheritdoc />
    public static bool Read(State state, PgHeapTuple row)
    {
        long value = state.Memory is { } memory ? memory.Value.Claim() : ++state.Position;
        if (value > state.Count)
        {
            return false;
        }

        row.Set(state.Value, value);
        return true;
    }

    /// <inheritdoc />
    public static void InitializeShared(State state, ref NumberScan.Counter memory) => memory = new NumberScan.Counter(0);

    /// <inheritdoc />
    public static void AttachShared(State state, PgCustomScanSharedMemory<NumberScan.Counter> memory)
    {
        s_counts[2]++;
        state.Memory = memory;
    }

    /// <summary>
    /// Reads begin, disposal and shared memory attachment counts in this backend.
    /// </summary>
    /// <returns>Three counters in that order.</returns>
    [PgFunction]
    public static long[] SplitScanCounts() => [.. s_counts];

    /// <summary>
    /// Holds one process's scan.
    /// </summary>
    /// <param name="count">The number of integers the scan produces.</param>
    /// <param name="value">The ordinal of the <c>value</c> column.</param>
    public sealed class State(int count, int value) : IDisposable
    {
        /// <summary>
        /// Gets the number of integers the scan produces.
        /// </summary>
        public int Count { get; } = count;

        /// <summary>
        /// Gets the last integer this process read when it reads them all itself.
        /// </summary>
        public long Position
        {
            get;
            set;
        }

        /// <summary>
        /// Gets the shared counter, or null when this process reads every integer itself.
        /// </summary>
        public PgCustomScanSharedMemory<NumberScan.Counter>? Memory
        {
            get;
            set;
        }

        internal int Value { get; } = value;

        /// <inheritdoc />
        public void Dispose() => s_counts[1]++;
    }
}
