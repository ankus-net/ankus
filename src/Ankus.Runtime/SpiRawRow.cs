using System.Collections;
using System.Collections.Generic;

namespace Ankus;

/// <summary>
/// Provides raw datums with explicit PostgreSQL types and NULL flags for one SPI row.
/// </summary>
public sealed class SpiRawRow : IReadOnlyList<PgDatum>
{
    private readonly PgDatum[] _values;
    private readonly IReadOnlyList<SpiColumn> _columns;

    /// <summary>
    /// Captures values and the result's immutable column metadata.
    /// </summary>
    /// <param name="values">The checked native values.</param>
    /// <param name="columns">The column metadata.</param>
    internal SpiRawRow(PgDatum[] values, IReadOnlyList<SpiColumn> columns)
    {
        _values = values;
        _columns = columns;
    }

    /// <summary>
    /// Gets the number of cells.
    /// </summary>
    public int Count => _values.Length;

    /// <summary>
    /// Gets a raw value by its zero-based ordinal. SQL NULL has IsNull set on its datum.
    /// </summary>
    /// <param name="index">The column ordinal.</param>
    /// <returns>The raw value.</returns>
    public PgDatum this[int index] => _values[index];

    /// <summary>
    /// Gets the first column with an exact case-sensitive name.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <returns>The raw value, including its SQL NULL flag.</returns>
    public PgDatum this[string name] => _values[GetOrdinal(name)];

    /// <summary>
    /// Resolves an exact, case-sensitive column name to its zero-based ordinal. Duplicate names resolve to the first column.
    /// </summary>
    /// <param name="name">The column name.</param>
    /// <returns>The zero-based ordinal.</returns>
    public int GetOrdinal(string name) => SpiColumn.GetOrdinal(_columns, name);

    /// <summary>
    /// Reads a column as a managed value or a polymorphic wrapper sharing this result's lifetime.
    /// </summary>
    /// <typeparam name="T">The requested representation.</typeparam>
    /// <param name="ordinal">The zero-based column ordinal.</param>
    /// <returns>The copied managed value or checked native wrapper.</returns>
    public T Get<[PgConvertible] T>(int ordinal) => this[ordinal].Read<T>();

    /// <summary>
    /// Reads the first column with an exact case-sensitive name.
    /// </summary>
    /// <typeparam name="T">The requested representation.</typeparam>
    /// <param name="name">The column name.</param>
    /// <returns>The copied managed value or a wrapper sharing this result's lifetime.</returns>
    public T Get<[PgConvertible] T>(string name) => this[name].Read<T>();

    /// <summary>
    /// Enumerates datums in column order.
    /// </summary>
    /// <returns>The value enumerator.</returns>
    public IEnumerator<PgDatum> GetEnumerator() => ((IEnumerable<PgDatum>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
