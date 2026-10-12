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

    internal PgCustomScanPlanner(uint relationOid, string schemaName, string relationName, double rows,
        double cheapestStartupCost, double cheapestTotalCost, List<PgCustomScanPath> paths, uint rowTypeOid, int columns,
        ReadOnlySpan<byte> read)
    {
        RelationOid = relationOid;
        SchemaName = schemaName;
        RelationName = relationName;
        Rows = rows;
        CheapestStartupCost = cheapestStartupCost;
        CheapestTotalCost = cheapestTotalCost;
        _paths = paths;
        _rowType = new RowType(rowTypeOid, columns);
        _read = read;
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
    /// <param name="path">The path's costs, optional row estimate and data.</param>
    /// <exception cref="ArgumentOutOfRangeException">A cost or row estimate is negative or not finite, or the total
    /// cost is below the startup cost.</exception>
    public void AddPath(PgCustomScanPath path)
    {
        ValidateCost(path.StartupCost, nameof(PgCustomScanPath.StartupCost));
        ValidateCost(path.TotalCost, nameof(PgCustomScanPath.TotalCost));
        ArgumentOutOfRangeException.ThrowIfLessThan(path.TotalCost, path.StartupCost, nameof(PgCustomScanPath.TotalCost));
        if (path.Rows is double rows)
        {
            ValidateCost(rows, nameof(PgCustomScanPath.Rows));
        }

        ArgumentOutOfRangeException.ThrowIfGreaterThan(path.Data.Length, 0x3FFFFFFF - 4, nameof(PgCustomScanPath.Data));
        _paths.Add(path);
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
