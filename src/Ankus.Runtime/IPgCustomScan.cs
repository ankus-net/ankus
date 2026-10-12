namespace Ankus;

/// <summary>
/// Defines a custom scan provider that produces the rows of a plain table from managed code.
/// </summary>
/// <typeparam name="TState">The per-scan state, created when the executor starts the scan.</typeparam>
/// <remarks>
/// Apply <see cref="PgCustomScanAttribute"/> to the implementing class. PostgreSQL still evaluates the query's
/// conditions and projection on each row the provider produces. A state that implements <see cref="IDisposable"/> is
/// disposed when the scan ends, including when the query fails. Implement
/// <see cref="IPgRescannableCustomScan{TState}"/> to restart a scan in place,
/// <see cref="IPgScrollableCustomScan{TState}"/> to read it backward,
/// <see cref="IPgMarkableCustomScan{TState}"/> to return to a saved position and
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
/// Reads a custom scan backward, so a cursor can fetch earlier rows without PostgreSQL storing the rows it has read.
/// </summary>
/// <typeparam name="TState">The per-scan state.</typeparam>
/// <remarks>
/// The scan has a current row, the one the last read returned, like a cursor. Reading forward moves to the next row and
/// reading backward to the previous one. A read that finds no row leaves the scan before the first row or after the last,
/// so reading in the other direction then returns the first or the last row.
/// </remarks>
public interface IPgScrollableCustomScan<TState> : IPgCustomScan<TState>
    where TState : class
{
    /// <summary>
    /// Reads the row before the current one.
    /// </summary>
    /// <param name="state">The scan's state.</param>
    /// <param name="row">A new row of the table's columns, all NULL until set.</param>
    /// <returns>Whether a row was read; false leaves the scan before its first row.</returns>
    static abstract bool ReadBackward(TState state, PgHeapTuple row);
}

/// <summary>
/// Saves and restores a custom scan's position, so a merge join can read the same inner rows again without PostgreSQL
/// storing them.
/// </summary>
/// <typeparam name="TState">The per-scan state.</typeparam>
/// <remarks>
/// A merge join needs its inner rows in order, so a provider that implements this interface usually offers paths with
/// <see cref="PgCustomScanPath.SortKeys"/>.
/// </remarks>
public interface IPgMarkableCustomScan<TState> : IPgCustomScan<TState>
    where TState : class
{
    /// <summary>
    /// Saves the scan's current position, replacing any earlier one.
    /// </summary>
    /// <param name="state">The scan's state.</param>
    static abstract void Mark(TState state);

    /// <summary>
    /// Returns to the saved position, so the next read returns the row the first read after
    /// <see cref="Mark"/> returned.
    /// </summary>
    /// <param name="state">The scan's state.</param>
    static abstract void Restore(TState state);
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
