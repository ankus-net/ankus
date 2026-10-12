using System.Text;

namespace Ankus;

/// <summary>
/// Owns the catalog metadata of one physical PostgreSQL tuple attribute, including dropped slots.
/// </summary>
public sealed class PgTupleAttributeInfo
{
    private byte[]? _utf8Name;

    /// <summary>
    /// Copies an attribute description from the guarded native transport.
    /// </summary>
    internal PgTupleAttributeInfo(string name, uint typeOid, uint baseTypeOid, int typeModifier,
        uint collationOid, bool isDropped = false, bool isNotNull = false, bool isComposite = false, bool isUnavailable = false)
    {
        ArgumentNullException.ThrowIfNull(name);
        if (name.Contains('\0', StringComparison.Ordinal) || (!isDropped && (name.Length == 0 || typeOid == 0 || baseTypeOid == 0)))
        {
            throw new ArgumentException("Live tuple attributes require a name and valid declared and base type OIDs.", nameof(name));
        }

        Name = name;
        TypeOid = typeOid;
        BaseTypeOid = baseTypeOid;
        TypeModifier = typeModifier;
        CollationOid = collationOid;
        IsDropped = isDropped;
        IsNotNull = isNotNull;
        IsComposite = isComposite;
        IsUnavailable = isUnavailable;
    }

    /// <summary>
    /// Gets the name's UTF-8 bytes, computed on first use, so transported metadata can be matched without decoding it.
    /// </summary>
    internal ReadOnlySpan<byte> Utf8Name => _utf8Name ??= Encoding.UTF8.GetBytes(Name);

    /// <summary>
    /// Gets the exact catalog name. Dropped attributes retain their physical placeholder names.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the declared type OID, retaining domain identity.
    /// </summary>
    public uint TypeOid { get; }

    /// <summary>
    /// Gets the underlying type OID used to convert the field's datum.
    /// </summary>
    public uint BaseTypeOid { get; }

    /// <summary>
    /// Gets the PostgreSQL type modifier, or minus one when unrestricted.
    /// </summary>
    public int TypeModifier { get; }

    /// <summary>
    /// Gets the collation OID, or zero for a noncollatable type.
    /// </summary>
    public uint CollationOid { get; }

    /// <summary>
    /// Gets whether this physical slot is a dropped attribute and always contains SQL NULL.
    /// </summary>
    public bool IsDropped { get; }

    /// <summary>
    /// Gets the descriptor's not-null metadata. Table constraints do not automatically constrain composite values.
    /// </summary>
    public bool IsNotNull { get; }

    /// <summary>
    /// Gets whether the underlying datum is a composite tuple or anonymous record.
    /// </summary>
    public bool IsComposite { get; }

    /// <summary>
    /// Gets whether PostgreSQL has not defined this field's value in the current trigger row.
    /// Unavailable generated columns cannot be read or replaced and are distinct from SQL NULL.
    /// </summary>
    public bool IsUnavailable { get; }
}
