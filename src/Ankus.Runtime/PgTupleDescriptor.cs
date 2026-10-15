namespace Ankus;

/// <summary>
/// Owns immutable PostgreSQL tuple metadata independently of native memory contexts and catalog cache entries.
/// </summary>
public sealed class PgTupleDescriptor
{
    private readonly PgTupleAttributeInfo[] _attributes;

    /// <summary>
    /// Copies physical attributes, optionally retaining relation metadata without a row type.
    /// </summary>
    internal PgTupleDescriptor(uint typeOid, int typeModifier, PgTupleAttributeInfo[] attributes,
        uint baseTypeOid = 0, bool physicalMetadata = false)
    {
        if (!physicalMetadata || baseTypeOid != 0 || typeModifier != -1)
        {
            ArgumentOutOfRangeException.ThrowIfZero(typeOid);
        }

        ArgumentNullException.ThrowIfNull(attributes);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(attributes.Length, 1664);
        if (attributes.Any(static attribute => attribute is null))
        {
            throw new ArgumentException("Tuple attributes cannot be null.", nameof(attributes));
        }

        TypeOid = typeOid;
        BaseTypeOid = baseTypeOid == 0 ? typeOid : baseTypeOid;
        TypeModifier = typeModifier;
        _attributes = [.. attributes];
        Attributes = Array.AsReadOnly(_attributes);
        HasUnavailable = Array.Exists(_attributes, static attribute => attribute.IsUnavailable);
    }

    /// <summary>
    /// Gets the composite or explicitly loaded domain OID, or PostgreSQL's record OID for an anonymous tuple.
    /// Tuples read from PostgreSQL datums carry the physical base composite identity.
    /// Physical relation metadata can have zero when PostgreSQL supplies no row type, such as indexes on PostgreSQL 13.
    /// </summary>
    public uint TypeOid { get; }

    /// <summary>
    /// Gets the underlying composite identity when the declared type is a domain, otherwise the declared type OID.
    /// </summary>
    public uint BaseTypeOid { get; }

    /// <summary>
    /// Gets the anonymous record's registered type modifier, or minus one when no modifier is registered.
    /// </summary>
    public int TypeModifier { get; }

    /// <summary>
    /// Gets the immutable physical attribute list, including dropped slots.
    /// </summary>
    public IReadOnlyList<PgTupleAttributeInfo> Attributes { get; }

    /// <summary>
    /// Gets the physical attribute list as a span, which per-row paths index without an interface call.
    /// </summary>
    internal ReadOnlySpan<PgTupleAttributeInfo> AttributeSpan => _attributes;

    /// <summary>
    /// Gets whether any attribute is a generated column PostgreSQL has not computed for this row.
    /// </summary>
    internal bool HasUnavailable { get; }

    /// <summary>
    /// Loads a current composite descriptor using PostgreSQL's identifier and search-path rules.
    /// </summary>
    /// <param name="name">The optionally schema-qualified PostgreSQL type name.</param>
    /// <returns>An owned descriptor.</returns>
    public static PgTupleDescriptor Load(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        return NativeBackend.LoadTupleDescriptor(name, 0);
    }

    /// <summary>
    /// Loads a current named composite descriptor by its catalog identity.
    /// </summary>
    /// <param name="typeOid">The PostgreSQL composite type OID.</param>
    /// <returns>An owned descriptor.</returns>
    public static PgTupleDescriptor Load(uint typeOid)
    {
        ArgumentOutOfRangeException.ThrowIfZero(typeOid);
        return NativeBackend.LoadTupleDescriptor(null, typeOid);
    }

    /// <summary>
    /// Copies a native tuple descriptor, such as one a hook or custom scan receives, as pgrx's
    /// <c>PgTupleDesc::from_pg_copy</c> does.
    /// </summary>
    /// <param name="tupleDescriptor">The native TupleDesc address.</param>
    /// <returns>An owned descriptor.</returns>
    /// <remarks>
    /// The caller must supply the address of a live descriptor with a row type in this backend; any other address is
    /// undefined behavior.
    /// </remarks>
    [NativeUnsafeAccess]
    public static PgTupleDescriptor DangerousCopy(nint tupleDescriptor)
    {
        ArgumentOutOfRangeException.ThrowIfZero(tupleDescriptor);
        return NativeBackend.CopyNativeTuple(tupleDescriptor, 0).Descriptor;
    }

    /// <summary>
    /// Creates a nonnull tuple whose physical fields are all SQL NULL. It may be populated before native validation.
    /// </summary>
    /// <returns>A detached mutable tuple.</returns>
    /// <exception cref="InvalidOperationException">This physical descriptor has no PostgreSQL row type.</exception>
    public PgHeapTuple CreateTuple()
    {
        RequireRowType();
        return new(this);
    }

    /// <summary>
    /// Creates a vector with this descriptor's explicit type identity, including empty and all-null arrays.
    /// </summary>
    /// <param name="values">The tuple values, copied into the array; nested tuples remain shared references.</param>
    /// <returns>A detached array with lower bound one, or rank zero when empty.</returns>
    /// <exception cref="InvalidOperationException">This physical descriptor has no PostgreSQL row type.</exception>
    public PgArray<PgHeapTuple?> CreateArray(ReadOnlySpan<PgHeapTuple?> values)
        => CreateArray(values, values.IsEmpty ? [] : [values.Length]);

    /// <summary>
    /// Copies tuple elements and PostgreSQL shape while retaining an explicit element type for every array state.
    /// Nonnull elements must share this descriptor's underlying composite identity.
    /// </summary>
    /// <param name="values">The row-major tuple elements, including SQL NULLs.</param>
    /// <param name="lengths">The dimension lengths.</param>
    /// <param name="lowerBounds">The lower bounds, or an empty span to use one.</param>
    /// <returns>An array whose element OID is this descriptor's type OID.</returns>
    /// <exception cref="InvalidOperationException">This physical descriptor has no PostgreSQL row type.</exception>
    public PgArray<PgHeapTuple?> CreateArray(ReadOnlySpan<PgHeapTuple?> values, ReadOnlySpan<int> lengths,
        ReadOnlySpan<int> lowerBounds = default)
    {
        RequireRowType();
        SpiArray.ValidateShape(values.Length, lengths, lowerBounds);
        foreach (PgHeapTuple? value in values)
        {
            if (value is not null && (value.Descriptor.BaseTypeOid != BaseTypeOid ||
                (BaseTypeOid == 2249 && value.Descriptor.TypeModifier != TypeModifier)))
            {
                throw new InvalidCastException("Every composite array element must have the descriptor's type identity.");
            }
        }

        return new PgArray<PgHeapTuple?>([.. values],
            (values.IsEmpty ? [] : [.. lengths], values.IsEmpty ? [] : lowerBounds.IsEmpty ?
                [.. Enumerable.Repeat(1, lengths.Length)] : [.. lowerBounds]), TypeOid, BaseTypeOid);
    }

    /// <summary>
    /// Resolves the first live attribute with the exact case-sensitive name.
    /// </summary>
    /// <param name="name">The exact attribute name.</param>
    /// <returns>The zero-based physical ordinal.</returns>
    public int GetOrdinal(string name)
    {
        ArgumentNullException.ThrowIfNull(name);
        ReadOnlySpan<PgTupleAttributeInfo> attributes = _attributes;
        for (int index = 0; index < attributes.Length; index++)
        {
            PgTupleAttributeInfo attribute = attributes[index];
            if (!attribute.IsDropped && attribute.Name == name)
            {
                return index;
            }
        }

        throw new ArgumentException($"The tuple has no live attribute named '{name}'.", nameof(name));
    }

    /// <summary>
    /// Rejects physical metadata without a row identity before constructing or binding a typed value.
    /// </summary>
    internal void RequireRowType()
    {
        if (TypeOid == 0)
        {
            throw new InvalidOperationException("The physical descriptor has no PostgreSQL row type.");
        }
    }
}
