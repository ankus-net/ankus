namespace Ankus;

/// <summary>
/// Describes one way a custom scan can produce a table's rows, with the costs the planner compares against other
/// paths.
/// </summary>
/// <param name="StartupCost">The cost before the first row, in PostgreSQL's planner units.</param>
/// <param name="TotalCost">The cost to produce every row, at least <paramref name="StartupCost"/>.</param>
/// <remarks>
/// Costs use the same units as the table's existing paths, so a path cheaper than
/// <see cref="PgCustomScanPlanner.CheapestTotalCost"/> usually wins.
/// </remarks>
public readonly record struct PgCustomScanPath(double StartupCost, double TotalCost)
{
    /// <summary>
    /// Gets the estimated row count before the query's conditions, or null to keep the planner's estimate.
    /// </summary>
    /// <remarks>
    /// A path that depends on an outer query's values, such as inside a lateral join, always uses the planner's
    /// estimate.
    /// </remarks>
    public double? Rows
    {
        get;
        init;
    }

    /// <summary>
    /// Gets bytes the plan keeps and passes to <see cref="IPgCustomScan{TState}.Begin"/>, such as an encoded choice.
    /// </summary>
    /// <remarks>
    /// A cached plan can be executed many times, so the data must remain valid without state from planning.
    /// </remarks>
    public ReadOnlyMemory<byte> Data
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the columns by which the path returns its rows in order, most significant first, or null when the rows have no
    /// order.
    /// </summary>
    /// <remarks>
    /// An ordered path can save a query a sort, for <c>ORDER BY</c>, a merge join or a <c>LIMIT</c>. The planner keeps
    /// only the leading keys the query can use.
    /// </remarks>
    public IReadOnlyList<PgCustomScanSortKey>? SortKeys
    {
        get;
        init;
    }
}
