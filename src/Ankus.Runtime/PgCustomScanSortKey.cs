namespace Ankus;

/// <summary>
/// Names a column by which a custom scan path returns its rows in order.
/// </summary>
/// <param name="Ordinal">The column's zero-based position in <see cref="PgCustomScanPlanner.Descriptor"/>.</param>
/// <param name="Descending">Whether larger values come first.</param>
/// <param name="NullsFirst">Whether SQL NULL comes before every value.</param>
/// <remarks>
/// Values compare as the default B-tree operator class of the column's type orders them, with the column's collation,
/// as <c>ORDER BY</c> on the column would. PostgreSQL trusts the order: rows out of order give wrong query results, as a
/// corrupt index would.
/// </remarks>
public readonly record struct PgCustomScanSortKey(int Ordinal, bool Descending, bool NullsFirst)
{
    /// <summary>
    /// Initializes a key with PostgreSQL's default NULL placement: last when ascending and first when descending.
    /// </summary>
    /// <param name="ordinal">The column's zero-based position in <see cref="PgCustomScanPlanner.Descriptor"/>.</param>
    /// <param name="descending">Whether larger values come first.</param>
    public PgCustomScanSortKey(int ordinal, bool descending = false)
        : this(ordinal, descending, descending)
    {
    }
}
