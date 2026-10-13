namespace Ankus;

/// <summary>
/// Writes rows to one table as PostgreSQL's logical replication apply worker does, through the executor's own
/// replication helpers, so constraints, indexes and row triggers behave as they do for the built-in subscriber.
/// </summary>
/// <remarks>
/// <para>
/// A writer locks the table in <c>ROW EXCLUSIVE</c> mode and keeps its executor state for the rest of the transaction
/// or until it is disposed. Dispose it before the transaction commits: its <c>AFTER</c> triggers run then, and a
/// transaction that commits with a writer still open fails. Rows use the table's own columns, from
/// <see cref="Descriptor"/>.
/// </para>
/// <para>
/// Updates, deletes and lookups find a row by the table's replica identity index, or else its primary key, using the
/// key's columns; a table with neither matches every column. The row found is locked until the transaction ends.
/// Triggers fire as <c>session_replication_role</c> selects. Each operation needs the matching table privilege, and,
/// as for the built-in apply worker, a table whose row-level security applies to the user is refused, because these
/// helpers do not apply policies. Each change is visible to the next. Only ordinary tables can be written.
/// </para>
/// </remarks>
public sealed unsafe class PgRelationWriter : IDisposable
{
    private readonly int _thread = Environment.CurrentManagedThreadId;

    private nint _id;

    private PgRelationWriter(nint id, uint relationOid, PgTupleDescriptor descriptor, uint keyIndexOid)
    {
        _id = id;
        RelationOid = relationOid;
        Descriptor = descriptor;
        KeyIndexOid = keyIndexOid;
    }

    /// <summary>
    /// Gets the table's OID.
    /// </summary>
    public uint RelationOid { get; }

    /// <summary>
    /// Gets the table's columns, including dropped columns, in the order rows hold them.
    /// </summary>
    public PgTupleDescriptor Descriptor { get; }

    /// <summary>
    /// Gets the OID of the index that identifies rows, the replica identity index or else the primary key, or zero
    /// when lookups compare every column.
    /// </summary>
    public uint KeyIndexOid { get; }

    /// <summary>
    /// Opens a writer for a table in the current transaction.
    /// </summary>
    /// <param name="relationOid">The table's OID.</param>
    /// <returns>The writer.</returns>
    /// <exception cref="PgException">There is no transaction, the table does not exist or is not an ordinary
    /// table.</exception>
    public static PgRelationWriter Open(uint relationOid)
    {
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.RelationWriter,
            _flags = 1,
            _value = (nint)relationOid,
        };
        NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
        PgTupleDescriptor descriptor;
        try
        {
            descriptor = PgTupleDescriptor.Load(checked((uint)result._value));
        }
        catch
        {
            Close(result._context);
            throw;
        }

        return new PgRelationWriter(result._context, relationOid, descriptor, unchecked((uint)result._data));
    }

    /// <summary>
    /// Creates an empty row with the table's columns, all SQL NULL until set.
    /// </summary>
    /// <returns>The row.</returns>
    public PgHeapTuple CreateRow() => Descriptor.CreateTuple();

    /// <summary>
    /// Creates a row for values in logical replication's text or binary form, with no column sent yet.
    /// </summary>
    /// <returns>The row.</returns>
    public PgReplicatedRow CreateReplicatedRow() => new(Descriptor.Attributes.Count);

    /// <summary>
    /// Inserts a row, checking constraints, adding index entries and firing row triggers.
    /// </summary>
    /// <param name="row">The row, with the table's columns.</param>
    /// <exception cref="PgException">A constraint or unique index rejects the row, or a trigger fails.</exception>
    public void Insert(PgHeapTuple row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _ = Run(2, row, null, null);
    }

    /// <summary>
    /// Inserts a row whose values are in logical replication's form; columns not sent take their defaults.
    /// </summary>
    /// <param name="row">The row.</param>
    /// <exception cref="PgException">A value is not valid for its type, a constraint or unique index rejects the row,
    /// or a trigger fails.</exception>
    public void Insert(PgReplicatedRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        _ = Run(2, row, null, null);
    }

    /// <summary>
    /// Replaces the row that matches <paramref name="key"/> with <paramref name="row"/>.
    /// </summary>
    /// <param name="key">A row whose key columns identify the row to change.</param>
    /// <param name="row">The new row, with every column.</param>
    /// <returns>Whether a matching row was found and updated.</returns>
    /// <exception cref="PgException">A constraint or unique index rejects the new row, or a trigger fails.</exception>
    public bool Update(PgHeapTuple key, PgHeapTuple row)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(row);
        return Run(3, key, row, null)._value == 1;
    }

    /// <summary>
    /// Replaces the row that matches <paramref name="key"/>, keeping the values of columns <paramref name="row"/>
    /// does not send, as an update with unchanged TOAST values does.
    /// </summary>
    /// <param name="key">A row whose key columns identify the row to change.</param>
    /// <param name="row">The new values.</param>
    /// <returns>Whether a matching row was found and updated.</returns>
    /// <exception cref="PgException">A value is not valid for its type, a constraint or unique index rejects the new
    /// row, or a trigger fails.</exception>
    public bool Update(PgReplicatedRow key, PgReplicatedRow row)
    {
        ArgumentNullException.ThrowIfNull(key);
        ArgumentNullException.ThrowIfNull(row);
        return Run(3, key, row, null)._value == 1;
    }

    /// <summary>
    /// Deletes the row that matches <paramref name="key"/>.
    /// </summary>
    /// <param name="key">A row whose key columns identify the row to delete.</param>
    /// <returns>Whether a matching row was found and deleted.</returns>
    /// <exception cref="PgException">A trigger or foreign key rejects the delete.</exception>
    public bool Delete(PgHeapTuple key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Run(4, key, null, null)._value == 1;
    }

    /// <summary>
    /// Deletes the row that matches <paramref name="key"/>.
    /// </summary>
    /// <param name="key">A row whose key columns identify the row to delete.</param>
    /// <returns>Whether a matching row was found and deleted.</returns>
    /// <exception cref="PgException">A key value is not valid for its type, or a trigger or foreign key rejects the
    /// delete.</exception>
    public bool Delete(PgReplicatedRow key)
    {
        ArgumentNullException.ThrowIfNull(key);
        return Run(4, key, null, null)._value == 1;
    }

    /// <summary>
    /// Finds and locks the row that matches <paramref name="key"/>, as a conflict check before applying a change
    /// would.
    /// </summary>
    /// <param name="key">A row whose key columns identify the row to find.</param>
    /// <returns>The row and the transaction that wrote it, or null when no row matches.</returns>
    public PgFoundRow? Find(PgReplicatedRow key)
    {
        ArgumentNullException.ThrowIfNull(key);
        NativeValue row = default;
        return ReadFound(Run(5, key, null, &row), row);
    }

    /// <summary>
    /// Finds and locks the row that matches <paramref name="key"/>, as a conflict check before applying a change
    /// would.
    /// </summary>
    /// <param name="key">A row whose key columns identify the row to find.</param>
    /// <returns>The row and the transaction that wrote it, or null when no row matches.</returns>
    public PgFoundRow? Find(PgHeapTuple key)
    {
        ArgumentNullException.ThrowIfNull(key);
        NativeValue row = default;
        return ReadFound(Run(5, key, null, &row), row);
    }

    /// <summary>
    /// Runs the table's queued <c>AFTER</c> triggers and closes the writer. The table stays locked until the
    /// transaction ends.
    /// </summary>
    public void Dispose()
    {
        if (_id == 0)
        {
            return;
        }

        nint id = Id;
        _id = 0;
        Close(id);
    }

    private nint Id
    {
        get
        {
            ObjectDisposedException.ThrowIf(_id == 0, this);
            if (Environment.CurrentManagedThreadId != _thread)
            {
                throw new InvalidOperationException("A relation writer belongs to the backend thread that opened it.");
            }

            return _id;
        }
    }

    private static void Close(nint id)
    {
        NativeMemoryRequest request = new() { _operation = NativeMemoryOperation.RelationWriter, _flags = 6, _context = id };
        NativeMemoryContext.Invoke(ref request, out _);
    }

    private static PgFoundRow? ReadFound(NativeMemoryResult result, NativeValue row)
    {
        if (result._value != 1)
        {
            return null;
        }

        PgTimestampTz? commitTime = result._data == 0 ? null : new PgTimestampTz(result._data);
        return new PgFoundRow(row.ReadTuple(), unchecked((ushort)result._context), commitTime, unchecked((uint)result._length));
    }

    private NativeMemoryResult Run(int operation, PgReplicatedRow first, PgReplicatedRow? second, NativeValue* output)
    {
        if (first.Count != Descriptor.Attributes.Count || (second is not null && second.Count != first.Count))
        {
            throw new ArgumentException("The row was not created for this writer's table.", second is null ? nameof(first) : nameof(second));
        }

        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.RelationWriter,
            _flags = operation,
            _context = Id,
            _pointer = (nint)output,
            _other = 1,
        };
        return PgReplicatedRow.Invoke(ref request, first, second);
    }

    private NativeMemoryResult Run(int operation, PgHeapTuple first, PgHeapTuple? second, NativeValue* output)
    {
        nint id = Id;
        NativeValue key = NativeValue.FromTuple(first);
        NativeValue row = second is null ? default : NativeValue.FromTuple(second);
        try
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.RelationWriter,
                _flags = operation,
                _context = id,
                _data = (nint)(&key),
                _pointer = second is null ? (nint)output : (nint)(&row),
            };
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            return result;
        }
        finally
        {
            key.Release();
            row.Release();
        }
    }
}
