using System;
using System.Collections;
using System.Collections.Generic;

namespace Ankus;

/// <summary>
/// Owns raw SPI datums in a PostgreSQL memory context until disposal or parent-context cleanup.
/// </summary>
/// <remarks>
/// Use a using declaration inside the backend callback. Copied metadata remains readable after disposal;
/// native datum access does not. No finalizer calls PostgreSQL from a managed worker thread.
/// </remarks>
public sealed class SpiRawResult : IReadOnlyList<SpiRawRow>, IDisposable
{
    private readonly PgMemoryContext _context;
    private readonly SpiRawRow[] _rows;

    /// <summary>
    /// Adopts the native result storage and managed metadata.
    /// </summary>
    /// <param name="context">The owned storage context.</param>
    /// <param name="columns">The copied column metadata.</param>
    /// <param name="rows">The raw result rows.</param>
    /// <param name="rowsAffected">The final statement's processed-row count.</param>
    internal SpiRawResult(PgMemoryContext context, IReadOnlyList<SpiColumn> columns, SpiRawRow[] rows, long rowsAffected)
    {
        _context = context;
        Columns = columns;
        _rows = rows;
        RowsAffected = rowsAffected;
    }

    /// <summary>
    /// Gets copied column metadata, including for an empty result.
    /// </summary>
    public IReadOnlyList<SpiColumn> Columns { get; }

    /// <summary>
    /// Gets the final statement's processed-row count.
    /// </summary>
    public long RowsAffected { get; }

    /// <summary>
    /// Gets the number of returned rows.
    /// </summary>
    public int Count => _rows.Length;

    /// <summary>
    /// Gets a row by zero-based index.
    /// </summary>
    /// <param name="index">The row index.</param>
    /// <returns>The raw row.</returns>
    public SpiRawRow this[int index] => _rows[index];

    /// <summary>
    /// Resolves an exact, case-sensitive column name to its zero-based ordinal, including when the query returned no rows.
    /// Duplicate names resolve to the first column.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <returns>The zero-based ordinal.</returns>
    public int GetOrdinal(string name) => SpiColumn.GetOrdinal(Columns, name);

    /// <summary>
    /// Releases all result datums and invalidates their checked native access.
    /// </summary>
    public void Dispose() => _context.Dispose();

    /// <summary>
    /// Enumerates rows in result order.
    /// </summary>
    /// <returns>The row enumerator.</returns>
    public IEnumerator<SpiRawRow> GetEnumerator() => ((IEnumerable<SpiRawRow>)_rows).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
