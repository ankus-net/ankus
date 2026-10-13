namespace Ankus.Examples.Subscriber;

/// <summary>
/// Holds the local position of each replicated column for one local table descriptor, and the rows reused for each
/// change to that table.
/// </summary>
/// <param name="writer">A writer for the local table, whose descriptor and rows the map keeps.</param>
/// <param name="positions">The local position of each replicated column, in the order rows send them.</param>
internal sealed class ColumnMap(PgRelationWriter writer, int[] positions)
{
    /// <summary>
    /// Gets the local table's descriptor.
    /// </summary>
    public PgTupleDescriptor Descriptor { get; } = writer.Descriptor;

    /// <summary>
    /// Gets the local position of each replicated column.
    /// </summary>
    public int[] Positions { get; } = positions;

    /// <summary>
    /// Gets the row reused for new values.
    /// </summary>
    public PgReplicatedRow Row { get; } = writer.CreateReplicatedRow();

    /// <summary>
    /// Gets the row reused for old keys.
    /// </summary>
    public PgReplicatedRow Key { get; } = writer.CreateReplicatedRow();
}
