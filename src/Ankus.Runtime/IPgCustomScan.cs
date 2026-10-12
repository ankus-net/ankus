namespace Ankus;

/// <summary>
/// Defines a custom scan provider that produces the rows of a plain table from managed code.
/// </summary>
/// <typeparam name="TState">The per-scan state, created when the executor starts the scan.</typeparam>
/// <remarks>
/// Apply <see cref="PgCustomScanAttribute"/> to the implementing class. PostgreSQL still evaluates the query's
/// conditions and projection on each row the provider produces. A state that implements <see cref="IDisposable"/> is
/// disposed when the scan ends, including when the query fails. Implement
/// <see cref="IPgRescannableCustomScan{TState}"/> to restart a scan in place and
/// <see cref="IPgExplainedCustomScan{TState}"/> to add EXPLAIN properties.
/// </remarks>
public interface IPgCustomScan<TState>
    where TState : class
{
    /// <summary>
    /// Offers paths for one table while PostgreSQL plans a query.
    /// </summary>
    /// <param name="planner">The table being planned and the costs of its existing paths.</param>
    static abstract void Plan(PgCustomScanPlanner planner);

    /// <summary>
    /// Starts a scan the planner chose, including for EXPLAIN without ANALYZE.
    /// </summary>
    /// <param name="context">The scanned table and the data its path carried.</param>
    /// <returns>The state passed to the scan's other calls.</returns>
    static abstract TState Begin(PgCustomScanContext context);

    /// <summary>
    /// Reads the scan's next row, as <see cref="System.Data.Common.DbDataReader.Read"/> advances a reader.
    /// </summary>
    /// <param name="state">The scan's state.</param>
    /// <param name="row">A new row of the table's columns, all NULL until set.</param>
    /// <returns>Whether a row was read; false ends the scan.</returns>
    static abstract bool Read(TState state, PgHeapTuple row);
}

/// <summary>
/// Restarts a custom scan in place when the executor rescans it, such as the inner side of a nested loop.
/// </summary>
/// <typeparam name="TState">The per-scan state.</typeparam>
/// <remarks>
/// Without this capability, Ankus disposes the state and calls <see cref="IPgCustomScan{TState}.Begin"/> again.
/// </remarks>
public interface IPgRescannableCustomScan<TState> : IPgCustomScan<TState>
    where TState : class
{
    /// <summary>
    /// Restarts the scan so the next call to <see cref="IPgCustomScan{TState}.Read"/> returns its first row.
    /// </summary>
    /// <param name="state">The scan's state.</param>
    static abstract void Rescan(TState state);
}

/// <summary>
/// Adds properties to the custom scan's EXPLAIN output.
/// </summary>
/// <typeparam name="TState">The per-scan state.</typeparam>
public interface IPgExplainedCustomScan<TState> : IPgCustomScan<TState>
    where TState : class
{
    /// <summary>
    /// Writes the scan's EXPLAIN properties, after any rows have been produced for EXPLAIN ANALYZE.
    /// </summary>
    /// <param name="state">The scan's state.</param>
    /// <param name="explain">The property writer for this scan node.</param>
    static abstract void Explain(TState state, PgExplainWriter explain);
}
