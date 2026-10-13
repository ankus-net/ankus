using System.Globalization;

namespace Ankus.TestExtension;

/// <summary>
/// Applies row changes to tables through <see cref="PgRelationWriter"/>, as a logical replication apply worker would.
/// </summary>
public static class RelationWriterFunctions
{
    /// <summary>
    /// Inserts three rows into a table of <c>(id integer, name text)</c>, updates, deletes and finds rows by key, and
    /// reports what each step saw.
    /// </summary>
    /// <param name="table">The table's OID.</param>
    /// <returns>The update and delete outcomes, the found row and a missing key's lookup.</returns>
    [PgFunction]
    public static string WriterApply(uint table)
    {
        using PgRelationWriter writer = PgRelationWriter.Open(table);
        writer.Insert(Row(writer, 1, "one"));
        writer.Insert(Row(writer, 2, "two"));
        writer.Insert(Row(writer, 3, "three"));
        bool updated = writer.Update(Row(writer, 2, null), Row(writer, 2, "zwei"));
        bool deleted = writer.Delete(Row(writer, 3, null));
        bool missing = writer.Update(Row(writer, 9, null), Row(writer, 9, "nine"));
        PgFoundRow? found = writer.Find(Row(writer, 2, null));
        return string.Join('|', updated, deleted, missing, found?.Row.Get<int>("id"), found?.Row.Get<string>("name"),
            writer.Find(Row(writer, 3, null)) is null);
    }

    /// <summary>
    /// Inserts one row and returns without disposing the writer, so the transaction commits with it still open.
    /// </summary>
    /// <param name="table">The table's OID.</param>
    /// <returns>A marker; the commit fails before the caller sees it.</returns>
    [PgFunction]
    public static string WriterLeftOpen(uint table)
    {
        PgRelationWriter writer = PgRelationWriter.Open(table);
        writer.Insert(Row(writer, 1, "left open"));
        return "inserted";
    }

    /// <summary>
    /// Inserts one row.
    /// </summary>
    /// <param name="table">The table's OID.</param>
    /// <param name="id">The row's id.</param>
    /// <param name="name">The row's name.</param>
    [PgFunction]
    public static void WriterInsert(uint table, int id, string? name)
    {
        using PgRelationWriter writer = PgRelationWriter.Open(table);
        writer.Insert(Row(writer, id, name));
    }

    /// <summary>
    /// Updates a row of a table that has no key, so the whole old row identifies it.
    /// </summary>
    /// <param name="table">The table's OID.</param>
    /// <returns>Whether the row was found.</returns>
    [PgFunction]
    public static bool WriterUpdateWithoutKey(uint table)
    {
        using PgRelationWriter writer = PgRelationWriter.Open(table);
        return writer.Update(Row(writer, 1, "one"), Row(writer, 1, "uno")) && !writer.Update(Row(writer, 2, "missing"), Row(writer, 2, "x"));
    }

    /// <summary>
    /// Finds a row and reports the transaction that wrote it.
    /// </summary>
    /// <param name="table">The table's OID.</param>
    /// <param name="id">The row's id.</param>
    /// <returns>The commit origin, whether a commit time is known, and the writing transaction's ID.</returns>
    [PgFunction]
    public static string WriterFindCommit(uint table, int id)
    {
        using PgRelationWriter writer = PgRelationWriter.Open(table);
        PgFoundRow found = writer.Find(Row(writer, id, null)) ?? throw new InvalidOperationException("No row " + id);
        return found.CommitOrigin.ToString(CultureInfo.InvariantCulture) + "|" + (found.CommitTime is not null) + "|" +
            found.TransactionId.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Applies rows in logical replication's form to a table of <c>(id integer PRIMARY KEY, name text, amount numeric
    /// DEFAULT 7, note text DEFAULT 'default', payload bytea)</c>: two inserts, an update that sends only the name, a
    /// delete, a delete of a missing key and a lookup.
    /// </summary>
    /// <param name="table">The table's OID.</param>
    /// <returns>The update and delete outcomes and the found row's name.</returns>
    [PgFunction]
    public static string WriterApplyReplicated(uint table)
    {
        using PgRelationWriter writer = PgRelationWriter.Open(table);
        int id = writer.Descriptor.GetOrdinal("id");
        int name = writer.Descriptor.GetOrdinal("name");
        PgReplicatedRow row = writer.CreateReplicatedRow();
        row.SetText(id, "1"u8);
        row.SetText(name, "café");
        row.SetNull(writer.Descriptor.GetOrdinal("note"));
        row.SetBinary(writer.Descriptor.GetOrdinal("payload"), [0x00, 0xFF]);
        writer.Insert(row);
        row.Clear();
        row.SetText(id, "2"u8);
        row.SetText(name, "zwei"u8);
        writer.Insert(row);

        PgReplicatedRow key = writer.CreateReplicatedRow();
        key.SetText(id, "1"u8);
        row.Clear();
        row.SetText(name, "uno");
        bool updated = writer.Update(key, row);
        key.SetText(id, "2"u8);
        bool deleted = writer.Delete(key);
        key.SetText(id, "9"u8);
        bool missing = writer.Delete(key);
        key.SetText(id, "1"u8);
        return string.Join('|', updated, deleted, missing, writer.Find(key)?.Row.Get<string>("name"));
    }

    /// <summary>
    /// Inserts a replicated row with id 5 and one column's value in text or binary form.
    /// </summary>
    /// <param name="table">The table's OID.</param>
    /// <param name="column">The column to set.</param>
    /// <param name="binary">Whether the value is in binary form.</param>
    /// <param name="value">The value's bytes.</param>
    [PgFunction]
    public static void WriterInsertReplicated(uint table, string column, bool binary, byte[] value)
    {
        using PgRelationWriter writer = PgRelationWriter.Open(table);
        PgReplicatedRow row = writer.CreateReplicatedRow();
        row.SetText(writer.Descriptor.GetOrdinal("id"), "5"u8);
        int ordinal = writer.Descriptor.GetOrdinal(column);
        if (binary)
        {
            row.SetBinary(ordinal, value);
        }
        else
        {
            row.SetText(ordinal, value);
        }

        writer.Insert(row);
    }

    /// <summary>
    /// Inserts rows through one writer and reports the backend's memory in use after the first thousand rows and after
    /// the last, from <c>pg_backend_memory_contexts</c>.
    /// </summary>
    /// <param name="table">The table's OID.</param>
    /// <param name="rows">The number of rows to insert.</param>
    /// <returns>The two byte counts.</returns>
    [PgFunction]
    public static string WriterMemory(uint table, int rows)
    {
        const string Used = "SELECT sum(used_bytes)::bigint FROM pg_backend_memory_contexts";
        using PgRelationWriter writer = PgRelationWriter.Open(table);
        long early = 0;
        for (int id = 1; id <= rows; id++)
        {
            writer.Insert(Row(writer, id, "row " + id.ToString(CultureInfo.InvariantCulture)));
            if (id == 1000)
            {
                early = Spi.ExecuteScalar<long>(Used);
            }
        }

        return early.ToString(CultureInfo.InvariantCulture) + "|" + Spi.ExecuteScalar<long>(Used).ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Builds a row of <c>(id, name)</c> for a writer's table.
    /// </summary>
    private static PgHeapTuple Row(PgRelationWriter writer, int id, string? name)
    {
        PgHeapTuple row = writer.CreateRow();
        row.Set("id", id);
        row.Set("name", name);
        return row;
    }
}
