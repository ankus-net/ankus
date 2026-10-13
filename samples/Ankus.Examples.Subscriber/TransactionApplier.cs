namespace Ankus.Examples.Subscriber;

/// <summary>
/// Applies one remote transaction's changes to local tables, keeping a writer and reusable rows per table until the
/// transaction commits.
/// </summary>
/// <param name="relations">The provider tables the stream has described, by the provider's OID.</param>
internal sealed class TransactionApplier(IReadOnlyDictionary<uint, RemoteRelation> relations) : IDisposable
{
    private readonly Dictionary<uint, LocalTable> _tables = [];

    /// <summary>
    /// Inserts a row. When a local row has the same key, the remote row replaces it, as pglogical's default conflict
    /// resolution does; a table without a key has no conflicts to find.
    /// </summary>
    /// <param name="reader">The insert message after its type byte.</param>
    public void Insert(ref ProtocolReader reader)
    {
        LocalTable table = Table(reader.ReadInt32());
        Expect(ref reader, (byte)'N');
        table.Read(ref reader, table.Row);
        if (table.Writer.KeyIndexOid == 0 || !table.Writer.Update(table.Row, table.Row))
        {
            table.Writer.Insert(table.Row);
        }
    }

    /// <summary>
    /// Updates the row that the old key, or else the new row, identifies. A missing row is skipped, as the built-in
    /// subscriber skips it.
    /// </summary>
    /// <param name="reader">The update message after its type byte.</param>
    public void Update(ref ProtocolReader reader)
    {
        LocalTable table = Table(reader.ReadInt32());
        byte kind = reader.ReadByte();
        PgReplicatedRow key = table.Row;
        if (kind is (byte)'K' or (byte)'O')
        {
            table.Read(ref reader, table.Key);
            key = table.Key;
            kind = reader.ReadByte();
        }

        if (kind != (byte)'N')
        {
            throw new InvalidDataException("An update message has no new row.");
        }

        table.Read(ref reader, table.Row);
        _ = table.Writer.Update(key, table.Row);
    }

    /// <summary>
    /// Deletes the row that the old key identifies. A missing row is skipped.
    /// </summary>
    /// <param name="reader">The delete message after its type byte.</param>
    public void Delete(ref ProtocolReader reader)
    {
        LocalTable table = Table(reader.ReadInt32());
        byte kind = reader.ReadByte();
        if (kind is not ((byte)'K' or (byte)'O'))
        {
            throw new InvalidDataException("A delete message has no key.");
        }

        table.Read(ref reader, table.Key);
        _ = table.Writer.Delete(table.Key);
    }

    /// <summary>
    /// Truncates the listed tables with the provider's options.
    /// </summary>
    /// <param name="reader">The truncate message after its type byte.</param>
    public void Truncate(ref ProtocolReader reader)
    {
        int count = reader.ReadInt32();
        byte options = reader.ReadByte();
        string tables = "";
        for (int index = 0; index < count; index++)
        {
            uint id = unchecked((uint)reader.ReadInt32());
            RemoteRelation relation = Relation(id);
            tables = index == 0 ? Spi.QuoteQualifiedIdentifier(relation.Schema, relation.Name)
                : tables + ", " + Spi.QuoteQualifiedIdentifier(relation.Schema, relation.Name);

            // TRUNCATE refuses a table this session still has open for writing.
            if (_tables.Remove(id, out LocalTable? table))
            {
                table.Writer.Dispose();
            }
        }

        // The tables go in one statement, as on the provider, so foreign keys among them do not block it.
        Spi.Execute("TRUNCATE " + tables + ((options & 2) != 0 ? " RESTART IDENTITY" : "") + ((options & 1) != 0 ? " CASCADE" : ""));
    }

    /// <summary>
    /// Closes every writer, which runs their tables' deferred <c>AFTER</c> triggers.
    /// </summary>
    public void Dispose()
    {
        foreach (LocalTable table in _tables.Values)
        {
            table.Writer.Dispose();
        }

        _tables.Clear();
    }

    private static void Expect(ref ProtocolReader reader, byte kind)
    {
        if (reader.ReadByte() != kind)
        {
            throw new InvalidDataException($"A change message does not have its '{(char)kind}' row.");
        }
    }

    private RemoteRelation Relation(uint id)
        => relations.TryGetValue(id, out RemoteRelation? relation) ? relation
            : throw new InvalidDataException($"The stream changed table {id} before describing it.");

    private LocalTable Table(int id)
    {
        if (_tables.TryGetValue(unchecked((uint)id), out LocalTable? table))
        {
            return table;
        }

        RemoteRelation relation = Relation(unchecked((uint)id));
        relation.LocalOid ??= Spi.ExecuteScalar<uint?>("SELECT to_regclass($1)::oid",
            SpiParameter.Create(Spi.QuoteQualifiedIdentifier(relation.Schema, relation.Name)))
            ?? throw new InvalidOperationException($"The subscriber has no table {relation.Schema}.{relation.Name}.");
        PgRelationWriter writer = PgRelationWriter.Open(relation.LocalOid.Value);
        try
        {
            int[] map = new int[relation.Columns.Count];
            for (int index = 0; index < map.Length; index++)
            {
                map[index] = writer.Descriptor.GetOrdinal(relation.Columns[index]);
            }

            table = new LocalTable(writer, map);
        }
        catch
        {
            writer.Dispose();
            throw;
        }

        _tables.Add(unchecked((uint)id), table);
        return table;
    }

    /// <summary>
    /// Holds a local table's writer, the local position of each replicated column, and rows reused for each change.
    /// </summary>
    private sealed class LocalTable(PgRelationWriter writer, int[] map)
    {
        public PgRelationWriter Writer { get; } = writer;

        public PgReplicatedRow Row { get; } = writer.CreateReplicatedRow();

        public PgReplicatedRow Key { get; } = writer.CreateReplicatedRow();

        /// <summary>
        /// Reads a row's columns into <paramref name="row"/>. An unchanged TOAST value stays unsent, so an update
        /// keeps the local value.
        /// </summary>
        public void Read(ref ProtocolReader reader, PgReplicatedRow row)
        {
            row.Clear();
            int count = reader.ReadInt16();
            if (count != map.Length)
            {
                throw new InvalidDataException($"A row has {count} columns where the table's description has {map.Length}.");
            }

            for (int index = 0; index < count; index++)
            {
                switch (reader.ReadByte())
                {
                    case (byte)'n':
                        row.SetNull(map[index]);
                        break;
                    case (byte)'u':
                        break;
                    case (byte)'t':
                        row.SetText(map[index], reader.ReadBytes(reader.ReadInt32()));
                        break;
                    case (byte)'b':
                        row.SetBinary(map[index], reader.ReadBytes(reader.ReadInt32()));
                        break;
                    default:
                        throw new InvalidDataException("A row has a column of an unknown kind.");
                }
            }
        }
    }
}
