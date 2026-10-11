using System.Text;

namespace Ankus;

/// <summary>
/// Owns the cells of a PostgreSQL composite or anonymous record independently of backend memory contexts.
/// Ordinals include dropped physical slots. Nested tuples, arrays, and binary buffers are ordinary shared references.
/// </summary>
public sealed class PgHeapTuple
{
    private readonly object?[] _values;

    /// <summary>
    /// Copies converted cells and retains immutable descriptor metadata.
    /// </summary>
    internal PgHeapTuple(PgTupleDescriptor descriptor, object?[] values)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        ArgumentNullException.ThrowIfNull(values);
        if (values.Length != descriptor.Attributes.Count)
        {
            throw new ArgumentException("Tuple cells must match the descriptor's physical attribute count.", nameof(values));
        }

        for (int index = 0; index < values.Length; index++)
        {
            if ((descriptor.Attributes[index].IsDropped || descriptor.Attributes[index].IsUnavailable) && values[index] is not null)
            {
                throw new ArgumentException("Dropped and unavailable tuple attributes cannot contain a value.", nameof(values));
            }
        }

        Descriptor = descriptor;
        _values = [.. values];
        for (int index = 0; index < _values.Length; index++)
        {
            _values[index] = PgRelationIdentity.Snapshot(_values[index]);
        }
    }

    /// <summary>
    /// Gets the immutable physical tuple description, including its type identity.
    /// </summary>
    public PgTupleDescriptor Descriptor { get; }

    /// <summary>
    /// Gets the number of physical cells, including dropped attributes.
    /// </summary>
    public int Count => _values.Length;

    /// <summary>
    /// Gets an owned cell by zero-based physical ordinal. SQL NULL and dropped attributes return null; unavailable fields throw.
    /// A cell whose type has no managed conversion throws; read it with its <see cref="PgDatumTypeAttribute"/> type.
    /// </summary>
    /// <param name="ordinal">The physical ordinal.</param>
    /// <returns>The managed cell.</returns>
    public object? this[int ordinal] => DetachedDatum.Expose(GetStoredCell(ordinal));

    /// <summary>
    /// Gets the first live cell with the exact case-sensitive name.
    /// </summary>
    /// <param name="name">The exact attribute name.</param>
    /// <returns>The managed cell.</returns>
    public object? this[string name] => this[Descriptor.GetOrdinal(name)];

    /// <summary>
    /// Reads a cell with the same strict type and SQL NULL rules as an SPI row.
    /// A <see cref="PgDatumTypeAttribute"/> type, or an array of one, uses its reader when the attribute has exactly the
    /// mapped PostgreSQL type. Reading PgRelation or its arrays opens fresh references that the caller must dispose.
    /// </summary>
    /// <typeparam name="T">The requested managed type.</typeparam>
    /// <param name="ordinal">The zero-based physical ordinal.</param>
    /// <returns>The converted cell.</returns>
    public T Get<T>(int ordinal)
    {
        object? cell = GetStoredCell(ordinal);
        if (PgDatumRegistry.Find(typeof(T)) is { } mapping)
        {
            return SpiRow.Convert<T>(mapping.ReadCell(cell, Descriptor.Attributes[ordinal].TypeOid));
        }

        if (PgDatumRegistry.FindArray(typeof(T)) is { } arrays)
        {
            return (T)arrays.ReadCell(cell, Descriptor.Attributes[ordinal].TypeOid, typeof(T))!;
        }

        return SpiRow.Convert<T>(cell);
    }

    /// <summary>
    /// Reads the first live attribute with the exact name using strict managed conversion.
    /// </summary>
    /// <typeparam name="T">The requested managed type.</typeparam>
    /// <param name="name">The exact attribute name.</param>
    /// <returns>The converted cell.</returns>
    public T Get<T>(string name) => Get<T>(Descriptor.GetOrdinal(name));

    /// <summary>
    /// Replaces a cell while preserving declared type, domain, collation, and type-modifier metadata.
    /// Native output applies current catalog and domain constraints before exposing the tuple to PostgreSQL.
    /// Raw datums and <see cref="PgDatumTypeAttribute"/> values are copied into independent managed storage before
    /// assignment; relations retain only their OIDs.
    /// </summary>
    /// <typeparam name="T">The replacement's declared managed type, including its type when null.</typeparam>
    /// <param name="ordinal">The zero-based physical ordinal.</param>
    /// <param name="value">The replacement cell.</param>
    public void Set<T>(int ordinal, T value)
    {
        PgTupleAttributeInfo attribute = Descriptor.Attributes[ValidateOrdinal(ordinal)];
        ValidateAvailable(ordinal);
        if (attribute.IsDropped)
        {
            throw new InvalidOperationException("Dropped tuple attributes cannot be changed.");
        }

        if (PgDatumRegistry.Find(typeof(T)) is { } mapping)
        {
            uint mapped = mapping.GetOid();
            RequireReplaceable(ordinal, mapped);
            _values[ordinal] = mapping.WriteCell(value, mapped);
            return;
        }

        if (PgDatumRegistry.FindArray(typeof(T)) is { } arrays)
        {
            uint mapped = arrays.GetOid();
            RequireReplaceable(ordinal, mapped);
            _values[ordinal] = arrays.WriteCell(value, mapped);
            return;
        }

        if (value is not null)
        {
            PgDatumRegistry.RejectOrdinaryArray(value.GetType());
        }

        uint oid = value is PgDatum datum ? datum.TypeOid : value is PgHeapTuple tuple ? tuple.Descriptor.BaseTypeOid :
            value is null && typeof(T) == typeof(PgHeapTuple) && attribute.IsComposite
                ? attribute.BaseTypeOid : SpiType.GetOid(value);
        SetCore(ordinal, oid, value);
    }

    /// <summary>
    /// Replaces the first live attribute with an exact matching name.
    /// </summary>
    /// <typeparam name="T">The replacement's managed type.</typeparam>
    /// <param name="name">The exact attribute name.</param>
    /// <param name="value">The replacement value.</param>
    public void Set<T>(string name, T value) => Set(Descriptor.GetOrdinal(name), value);

    /// <summary>
    /// Replaces a cell using an explicit parameter identity, including a typed NULL composite or array.
    /// Raw datums are copied into independent managed values before assignment.
    /// </summary>
    /// <param name="ordinal">The zero-based physical ordinal.</param>
    /// <param name="value">The typed replacement.</param>
    public void Set(int ordinal, SpiParameter value)
    {
        if (value.DatumMapping is not null || value.DatumArrayMapping is not null)
        {
            RequireReplaceable(ordinal, value.TypeOid);
            _values[ordinal] = WriteMapped(value);
            return;
        }

        RejectOrdinaryArray(value);
        SetCore(ordinal, value.TypeOid, value.Value);
    }

    /// <summary>
    /// Replaces a named cell using an explicit parameter identity.
    /// </summary>
    /// <param name="name">The exact attribute name.</param>
    /// <param name="value">The typed replacement.</param>
    public void Set(string name, SpiParameter value) => Set(Descriptor.GetOrdinal(name), value);

    /// <summary>
    /// Copies the cell slots while sharing immutable metadata and nested reference values.
    /// Replacing a cell in either tuple leaves the other tuple's slots unchanged.
    /// </summary>
    /// <returns>A shallow detached copy.</returns>
    public PgHeapTuple Clone() => new(Descriptor, _values);

    /// <summary>
    /// Creates and registers an anonymous PostgreSQL record from named typed fields in the active backend.
    /// New field names must contain at most sixty-three UTF-8 bytes and cannot contain a zero character.
    /// Relation fields copy their OIDs without retaining the supplied reference's close obligation.
    /// </summary>
    /// <param name="fields">The field names and typed values in physical order.</param>
    /// <returns>An owned record with a canonical registered type modifier.</returns>
    public static PgHeapTuple Create(params (string Name, SpiParameter Value)[] fields)
    {
        ArgumentNullException.ThrowIfNull(fields);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(fields.Length, 1664);
        var attributes = new PgTupleAttributeInfo[fields.Length];
        object?[] values = new object?[fields.Length];
        for (int index = 0; index < fields.Length; index++)
        {
            (string name, SpiParameter value) = fields[index];
            if (value.DatumMapping is null && value.DatumArrayMapping is null)
            {
                RejectOrdinaryArray(value);
            }

            ArgumentException.ThrowIfNullOrEmpty(name);
            if (name.Contains('\0', StringComparison.Ordinal) || Encoding.UTF8.GetByteCount(name) > 63)
            {
                throw new ArgumentException("Record field names must fit a PostgreSQL identifier without zero characters.", nameof(fields));
            }

            attributes[index] = new PgTupleAttributeInfo(name, value.TypeOid, value.TypeOid, -1, 0,
                isComposite: value.Value is PgHeapTuple || value.TypeOid == 2249);
            values[index] = value.DatumMapping is null && value.DatumArrayMapping is null ? value.Value : WriteMapped(value);
        }

        return NativeBackend.CreateTuple(new PgHeapTuple(new PgTupleDescriptor(2249, -1, attributes), values));
    }

    /// <summary>
    /// Rejects an array of mapped elements whose parameter did not declare its mapping.
    /// </summary>
    private static void RejectOrdinaryArray(SpiParameter value)
    {
        if (value.Value is not null)
        {
            PgDatumRegistry.RejectOrdinaryArray(value.Value.GetType());
        }
    }

    /// <summary>
    /// Writes a mapped parameter through its declared mapping into independent managed storage.
    /// </summary>
    private static object? WriteMapped(SpiParameter value)
        => value.DatumMapping is { } mapping
            ? mapping.WriteCell(value.Value, value.TypeOid)
            : value.DatumArrayMapping!.WriteCell(value.Value, value.TypeOid);

    /// <summary>
    /// Gets a stored cell, including one that has no managed conversion, after checking that the attribute has a value.
    /// </summary>
    /// <param name="ordinal">The zero-based physical ordinal.</param>
    /// <returns>The stored cell.</returns>
    internal object? GetStoredCell(int ordinal)
    {
        ValidateAvailable(ValidateOrdinal(ordinal));
        return _values[ordinal];
    }

    private void SetCore(int ordinal, uint oid, object? value)
    {
        RequireReplaceable(ordinal, oid);
        _values[ordinal] = value is PgDatum datum ? NativeBackend.DetachDatum(datum) : PgRelationIdentity.Snapshot(value);
    }

    /// <summary>
    /// Checks that a live attribute can hold a replacement of the given type before any conversion runs.
    /// </summary>
    private void RequireReplaceable(int ordinal, uint oid)
    {
        PgTupleAttributeInfo attribute = Descriptor.Attributes[ValidateOrdinal(ordinal)];
        ValidateAvailable(ordinal);
        if (attribute.IsDropped)
        {
            throw new InvalidOperationException("Dropped tuple attributes cannot be changed.");
        }

        uint expected = attribute.BaseTypeOid is 1042 or 1043 ? 25 : attribute.BaseTypeOid;
        if (oid != expected && oid != attribute.TypeOid)
        {
            throw new InvalidCastException($"PostgreSQL type OID {oid} cannot replace tuple attribute '{attribute.Name}' of type OID {attribute.TypeOid}.");
        }
    }

    private int ValidateOrdinal(int ordinal)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(ordinal);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(ordinal, Count);
        return ordinal;
    }

    private void ValidateAvailable(int ordinal)
    {
        if (Descriptor.Attributes[ordinal].IsUnavailable)
        {
            throw new InvalidOperationException("PostgreSQL has not defined this generated column's value in the current trigger row.");
        }
    }
}
