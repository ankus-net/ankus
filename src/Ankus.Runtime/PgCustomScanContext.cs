namespace Ankus;

/// <summary>
/// Describes a custom scan the executor is starting.
/// </summary>
/// <remarks>
/// The context is valid only during <see cref="IPgCustomScan{TState}.Begin"/>. Copy <see cref="Data"/> to keep it.
/// </remarks>
public readonly ref struct PgCustomScanContext
{
    internal PgCustomScanContext(uint relationOid, PgTupleDescriptor descriptor, ReadOnlySpan<byte> data, bool isExplainOnly)
    {
        RelationOid = relationOid;
        Descriptor = descriptor;
        Data = data;
        IsExplainOnly = isExplainOnly;
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
}
