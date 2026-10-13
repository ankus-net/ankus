namespace Ankus.Examples.Subscriber;

/// <summary>
/// Describes a provider table from a <c>pgoutput</c> relation message, which precedes the table's first change in a
/// stream and follows any change to its definition.
/// </summary>
/// <param name="schema">The table's schema.</param>
/// <param name="name">The table's name.</param>
/// <param name="columns">The replicated columns' names, in the order rows send them.</param>
internal sealed class RemoteRelation(string schema, string name, string[] columns)
{
    /// <summary>
    /// Gets the table's schema.
    /// </summary>
    public string Schema { get; } = schema;

    /// <summary>
    /// Gets the table's name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets the replicated columns' names.
    /// </summary>
    public IReadOnlyList<string> Columns { get; } = columns;

    /// <summary>
    /// Gets or sets the local table's OID once looked up. A new relation message replaces this description, so a
    /// renamed or redefined table is looked up again, as the built-in subscriber's relation map does.
    /// </summary>
    public uint? LocalOid { get; set; }

    /// <summary>
    /// Gets or sets where each replicated column goes in the local table, with rows reused for each change. Writers
    /// share a descriptor until the local table changes, so the map is rebuilt only then.
    /// </summary>
    public ColumnMap? Map { get; set; }

    /// <summary>
    /// Reads a relation message after its type byte.
    /// </summary>
    /// <param name="reader">The message.</param>
    /// <param name="id">The provider's OID for the table.</param>
    /// <returns>The relation.</returns>
    public static RemoteRelation Read(ref ProtocolReader reader, out uint id)
    {
        id = unchecked((uint)reader.ReadInt32());
        string schema = reader.ReadString();
        string name = reader.ReadString();
        _ = reader.ReadByte();
        string[] columns = new string[reader.ReadInt16()];
        for (int index = 0; index < columns.Length; index++)
        {
            _ = reader.ReadByte();
            columns[index] = reader.ReadString();
            _ = reader.ReadInt32();
            _ = reader.ReadInt32();
        }

        // pgoutput leaves the schema empty for pg_catalog.
        return new RemoteRelation(schema.Length == 0 ? "pg_catalog" : schema, name, columns);
    }
}
