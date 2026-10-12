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

    internal PgCustomScanPlanner(uint relationOid, string schemaName, string relationName, double rows,
        double cheapestStartupCost, double cheapestTotalCost, List<PgCustomScanPath> paths)
    {
        RelationOid = relationOid;
        SchemaName = schemaName;
        RelationName = relationName;
        Rows = rows;
        CheapestStartupCost = cheapestStartupCost;
        CheapestTotalCost = cheapestTotalCost;
        _paths = paths;
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
}
