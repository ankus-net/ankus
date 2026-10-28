using System;

namespace Ankus;

/// <summary>
/// Describes a custom scan the executor is starting.
/// </summary>
/// <remarks>
/// The context is valid only during <see cref="IPgCustomScan{TState}.Begin"/>. Copy <see cref="Data"/> to keep it.
/// </remarks>
public readonly ref struct PgCustomScanContext
{
    private readonly ReadOnlySpan<byte> _read;

    internal PgCustomScanContext(uint relationOid, PgTupleDescriptor descriptor, ReadOnlySpan<byte> data, bool isExplainOnly,
        ReadOnlySpan<byte> read)
    {
        RelationOid = relationOid;
        Descriptor = descriptor;
        Data = data;
        IsExplainOnly = isExplainOnly;
        _read = read;
    }

    /// <summary>
    /// Gets the scanned table's OID.
    /// </summary>
    public uint RelationOid { get; }

    /// <summary>
    /// Gets the table's columns, including dropped columns, in the order rows hold them.
    /// </summary>
    public PgTupleDescriptor Descriptor { get; }

    /// <summary>
    /// Gets the data the chosen path carried from <see cref="PgCustomScanPath.Data"/>.
    /// </summary>
    public ReadOnlySpan<byte> Data { get; }

    /// <summary>
    /// Gets whether the executor only explains the plan and will not request rows.
    /// </summary>
    public bool IsExplainOnly { get; }

    /// <summary>
    /// Gets whether PostgreSQL reads a column from the scan's rows, for the query's output or its conditions.
    /// </summary>
    /// <param name="ordinal">The column's zero-based position in <see cref="Descriptor"/>.</param>
    /// <returns>
    /// Whether the column is read. A provider can leave a column that PostgreSQL does not read unset, even when the
    /// column is <c>NOT NULL</c>, and skip the work of producing it.
    /// </returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="ordinal"/> is not a column of the table.</exception>
    public bool IsColumnRead(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, Descriptor.Attributes.Count);
        return ordinal >= _read.Length || _read[ordinal] != 0;
    }
}
