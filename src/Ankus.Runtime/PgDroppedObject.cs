using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;

namespace Ankus;

/// <summary>
/// Owns the identity and dependency metadata of one dropped object after its catalog entries have been removed.
/// </summary>
public sealed class PgDroppedObject
{
    /// <summary>
    /// Copies the explicit dropped-object projection and takes independent ownership of address vectors.
    /// </summary>
    internal PgDroppedObject(SpiRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        ClassId = row.Get<uint>(0);
        ObjectId = row.Get<uint>(1);
        ObjectSubId = row.Get<int>(2);
        Original = row.Get<bool>(3);
        Normal = row.Get<bool>(4);
        IsTemporary = row.Get<bool>(5);
        ObjectType = row.Get<string?>(6) ?? throw new InvalidOperationException("A dropped object requires an object type.");
        SchemaName = row.Get<string?>(7);
        ObjectName = row.Get<string?>(8);
        ObjectIdentity = row.Get<string?>(9);
        AddressNames = CopyAddress(row, 10);
        AddressArguments = CopyAddress(row, 11);
    }

    /// <summary>
    /// Gets the OID of the catalog that contained this object.
    /// </summary>
    public uint ClassId { get; }

    /// <summary>
    /// Gets the OID of the dropped object.
    /// </summary>
    public uint ObjectId { get; }

    /// <summary>
    /// Gets the subobject number, or zero for a whole object.
    /// </summary>
    public int ObjectSubId { get; }

    /// <summary>
    /// Gets whether the object was a root of the deletion operation.
    /// </summary>
    public bool Original { get; }

    /// <summary>
    /// Gets whether a normal dependency relationship led to this object being dropped.
    /// </summary>
    public bool Normal { get; }

    /// <summary>
    /// Gets whether this object was temporary. Temporary objects can still have nonnull address vectors.
    /// </summary>
    public bool IsTemporary { get; }

    /// <summary>
    /// Gets PostgreSQL's description of the dropped object type.
    /// </summary>
    public string ObjectType { get; }

    /// <summary>
    /// Gets the schema name, or null when none applies. Temporary schemas are reported using the pg_temp alias.
    /// </summary>
    public string? SchemaName { get; }

    /// <summary>
    /// Gets the object name, or null when PostgreSQL cannot identify it by name alone.
    /// </summary>
    public string? ObjectName { get; }

    /// <summary>
    /// Gets PostgreSQL's quoted object identity, or null when unavailable.
    /// </summary>
    public string? ObjectIdentity { get; }

    /// <summary>
    /// Gets the immutable address names used by pg_get_object_address, preserving null separately from an empty vector.
    /// </summary>
    public IReadOnlyList<string>? AddressNames { get; }

    /// <summary>
    /// Gets the immutable address arguments used by pg_get_object_address, preserving null separately from an empty vector.
    /// </summary>
    public IReadOnlyList<string>? AddressArguments { get; }

    private static ReadOnlyCollection<string>? CopyAddress(SpiRow row, int ordinal)
    {
        string[]? values = row.Get<string[]?>(ordinal);
        if (values is null)
        {
            return null;
        }

        if (values.Any(static value => value is null))
        {
            throw new InvalidOperationException("Dropped object address vectors cannot contain SQL NULL elements.");
        }

        return Array.AsReadOnly<string>([.. values]);
    }
}
