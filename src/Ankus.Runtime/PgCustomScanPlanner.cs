using System;
using System.Collections.Generic;

namespace Ankus;

/// <summary>
/// Describes a plain table the planner is about to scan and collects the custom scan paths offered for it.
/// </summary>
/// <remarks>
/// The planner is valid only during <see cref="IPgCustomScan{TState}.Plan"/>. Ankus offers ordinary tables and
/// materialized views, including inheritance children and partitions. It does not offer tables whose rows the query
/// locks or modifies, or whose system columns it reads, because a custom scan's rows have no physical location.
/// </remarks>
public readonly ref struct PgCustomScanPlanner
{
    private readonly List<PgCustomScanPath> _paths;

    private readonly RowType _rowType;

    private readonly ReadOnlySpan<byte> _read;

    private readonly bool _parallel;

    internal PgCustomScanPlanner(uint relationOid, string schemaName, string relationName, double rows,
        double cheapestStartupCost, double cheapestTotalCost, List<PgCustomScanPath> paths, uint rowTypeOid, int columns,
        ReadOnlySpan<byte> read, bool parallel)
        : this(relationOid, schemaName, relationName, rows, cheapestStartupCost, cheapestTotalCost, paths, new RowType(rowTypeOid, columns),
            read, parallel)
    {
    }

    /// <summary>
    /// Creates a planner over a descriptor that is already loaded.
    /// </summary>
    internal PgCustomScanPlanner(string relationName, List<PgCustomScanPath> paths, PgTupleDescriptor descriptor, bool parallel = false)
        : this(0, "public", relationName, 0, 0, 0, paths, new RowType(descriptor), [], parallel)
    {
    }

    private PgCustomScanPlanner(uint relationOid, string schemaName, string relationName, double rows, double cheapestStartupCost,
        double cheapestTotalCost, List<PgCustomScanPath> paths, RowType rowType, ReadOnlySpan<byte> read, bool parallel)
    {
        RelationOid = relationOid;
        SchemaName = schemaName;
        RelationName = relationName;
        Rows = rows;
        CheapestStartupCost = cheapestStartupCost;
        CheapestTotalCost = cheapestTotalCost;
        _paths = paths;
        _rowType = rowType;
        _read = read;
        _parallel = parallel;
    }

    /// <summary>
    /// Gets the table's OID.
    /// </summary>
    public uint RelationOid { get; }

    /// <summary>
    /// Gets the table's schema name.
    /// </summary>
    public string SchemaName { get; }

    /// <summary>
    /// Gets the table's name.
    /// </summary>
    public string RelationName { get; }

    /// <summary>
    /// Gets the planner's estimate of the rows left after the query's conditions on this table.
    /// </summary>
    public double Rows { get; }

    /// <summary>
    /// Gets the lowest startup cost among the table's existing paths, or positive infinity when it has none.
    /// </summary>
    public double CheapestStartupCost { get; }

    /// <summary>
    /// Gets the lowest total cost among the table's existing paths, or positive infinity when it has none.
    /// </summary>
    public double CheapestTotalCost { get; }

    /// <summary>
    /// Gets the table's columns, including dropped columns, in the order rows hold them.
    /// </summary>
    /// <remarks>The descriptor is loaded from the catalog on first use, so a provider that ignores a table does not
    /// pay for it.</remarks>
    public PgTupleDescriptor Descriptor => _rowType.Descriptor;

    /// <summary>
    /// Gets whether the query reads a column from this table's rows, for its output or its conditions.
    /// </summary>
    /// <param name="ordinal">The column's zero-based position in <see cref="Descriptor"/>.</param>
    /// <returns>Whether the column is read, so that a path's cost can include only the columns it must produce.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is not a column of the table.</exception>
    public bool IsColumnRead(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, _rowType.Columns);
        return ordinal >= _read.Length || _read[ordinal] != 0;
    }

    /// <summary>
    /// Offers a path. The planner keeps it only if no other path is cheaper for every purpose.
    /// </summary>
    /// <param name="path">The path's costs, optional row estimate, data and sort keys.</param>
    /// <exception cref="ArgumentOutOfRangeException">A cost or row estimate is negative or not finite, the total cost
    /// is below the startup cost, or a sort key is not a column of the table.</exception>
    /// <exception cref="ArgumentException">A sort key names a dropped column.</exception>
    public void AddPath(PgCustomScanPath path) => Add(path with { Workers = 0 });

    /// <summary>
    /// Offers a path that PostgreSQL's parallel workers and the leader run together, each producing part of the rows.
    /// </summary>
    /// <param name="path">
    /// The whole scan's costs and optional row estimate, with its data and sort keys. Ankus divides the run cost and the
    /// rows among the processes, as PostgreSQL does for a parallel sequential scan.
    /// </param>
    /// <param name="workers">The workers the path plans for, at most PostgreSQL's <c>max_parallel_workers_per_gather</c>.</param>
    /// <remarks>
    /// PostgreSQL considers the path only for a table and query that can run in parallel, and only when the scan's
    /// conditions are independent of any outer row. The processes divide the rows through the provider's shared
    /// memory; see <see cref="IPgParallelCustomScan{TState, TShared}"/>.
    /// </remarks>
    /// <exception cref="InvalidOperationException">The provider does not implement
    /// <see cref="IPgParallelCustomScan{TState, TShared}"/>.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="workers"/> is less than one, or the path is invalid
    /// as for <see cref="AddPath"/>.</exception>
    /// <exception cref="ArgumentException">A sort key names a dropped column.</exception>
    public void AddParallelPath(PgCustomScanPath path, int workers)
    {
        if (!_parallel)
        {
            throw new InvalidOperationException("Only a provider that implements IPgParallelCustomScan can offer parallel paths.");
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(workers, 1);
        Add(path with { Workers = workers });
    }

    private void Add(PgCustomScanPath path)
    {
        ValidateCost(path.StartupCost, nameof(PgCustomScanPath.StartupCost));
        ValidateCost(path.TotalCost, nameof(PgCustomScanPath.TotalCost));
        ArgumentOutOfRangeException.ThrowIfLessThan(path.TotalCost, path.StartupCost, nameof(PgCustomScanPath.TotalCost));
        if (path.Rows is double rows)
        {
            ValidateCost(rows, nameof(PgCustomScanPath.Rows));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(path.Data.Length, 0x3FFFFFFF - 4, nameof(PgCustomScanPath.Data));
        if (path.SortKeys is not { Count: > 0 } keys)
        {
            _paths.Add(path with { SortKeys = null });
            return;
        }

        // The keys are copied, so a list changed after this call cannot change the path.
        PgCustomScanSortKey[] copy = [.. keys];
        foreach (PgCustomScanSortKey key in copy)
        {
            ArgumentOutOfRangeException.ThrowIfNegative(key.Ordinal, nameof(PgCustomScanPath.SortKeys));
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(key.Ordinal, _rowType.Columns, nameof(PgCustomScanPath.SortKeys));
            if (Descriptor.Attributes[key.Ordinal].IsDropped)
            {
                throw new ArgumentException($"Custom scan sort key {key.Ordinal} names a dropped column.", nameof(path));
            }
        }

        _paths.Add(path with { SortKeys = copy });
    }

    private static void ValidateCost(double value, string name)
    {
        if (!double.IsFinite(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Custom scan costs and row estimates must be finite and nonnegative.");
        }
    }

    /// <summary>
    /// Loads the table's descriptor once, when a provider first asks for it.
    /// </summary>
    /// <param name="oid">The table's row type.</param>
    /// <param name="columns">The number of columns the planner sees, including dropped columns.</param>
    private sealed class RowType(uint oid, int columns)
    {
        private PgTupleDescriptor? _descriptor;

        /// <summary>
        /// Holds a descriptor that is already loaded.
        /// </summary>
        /// <param name="descriptor">The table's columns.</param>
        internal RowType(PgTupleDescriptor descriptor)
            : this(descriptor.TypeOid, descriptor.Attributes.Count)
            => _descriptor = descriptor;

        internal int Columns => columns;

        internal PgTupleDescriptor Descriptor
        {
            get
            {
                if (_descriptor is null)
                {
                    PgTupleDescriptor descriptor = PgTupleDescriptor.Load(oid);
                    if (descriptor.Attributes.Count != columns)
                    {
                        throw new InvalidOperationException("The custom scan table's row type does not match its planned columns.");
                    }

                    _descriptor = descriptor;
                }

                return _descriptor;
            }
        }
    }
}
