using System;
using System.Globalization;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Classifies an exact unsigned OID as invalid, custom, or a recognized built-in constant.
/// </summary>
/// <remarks>
/// This corresponds to pgrx's tagged PgOid helper. PostgreSQL oid datums use uint in Ankus.
/// Classification does not validate existence or object category. Equality retains both the tag and numeric value;
/// an explicitly custom value remains distinct from an invalid or built-in value with the same number.
/// </remarks>
public readonly record struct PgOid
{
    private PgOid(uint value, PgOidKind kind)
    {
        Value = value;
        Kind = kind;
    }

    /// <summary>
    /// Gets the invalid variant with value zero; this is also the default value.
    /// </summary>
    public static PgOid Invalid => default;

    /// <summary>
    /// Gets the exact unsigned numeric value, independently of its classification.
    /// </summary>
    public uint Value { get; }

    /// <summary>
    /// Gets the retained classification tag.
    /// </summary>
    public PgOidKind Kind { get; }

    /// <summary>
    /// Gets the numeric built-in member only when this value has the BuiltIn tag.
    /// </summary>
    public PgBuiltInOid? BuiltIn => Kind == PgOidKind.BuiltIn ? (PgBuiltInOid)Value : null;

    /// <summary>
    /// Retains an explicitly custom value without reclassification, including zero or a known built-in number.
    /// </summary>
    /// <param name="value">The exact custom value.</param>
    /// <returns>The custom variant.</returns>
    public static PgOid Custom(uint value) => new(value, PgOidKind.Custom);

    /// <summary>
    /// Classifies an unsigned OID using the active backend's major version.
    /// </summary>
    /// <param name="value">The exact OID.</param>
    /// <returns>The invalid, built-in, or custom variant.</returns>
    public static PgOid FromValue(uint value) => FromValue(value, NativeBackend.GetPostgresMajor());

    /// <summary>
    /// Classifies an unsigned OID using an explicit major version without requiring a backend.
    /// </summary>
    /// <param name="value">The exact OID.</param>
    /// <param name="postgresMajor">The PostgreSQL major version, from 13 through 19.</param>
    /// <returns>The invalid, built-in, or custom variant.</returns>
    public static PgOid FromValue(uint value, int postgresMajor)
    {
        if (PgBuiltInOids.TryFromValue(value, postgresMajor, out _, out PgOidLookupError error))
        {
            return new(value, PgOidKind.BuiltIn);
        }

        return error == PgOidLookupError.Invalid ? Invalid : Custom(value);
    }

    /// <summary>
    /// Creates a built-in variant after checking active-version membership.
    /// </summary>
    /// <param name="value">The built-in candidate.</param>
    /// <returns>The built-in variant.</returns>
    public static PgOid FromBuiltIn(PgBuiltInOid value) => FromBuiltIn(value, NativeBackend.GetPostgresMajor());

    /// <summary>
    /// Creates a built-in variant after checking explicit-version membership.
    /// </summary>
    /// <param name="value">The built-in candidate; undefined and unavailable members are rejected.</param>
    /// <param name="postgresMajor">The PostgreSQL major version, from 13 through 19.</param>
    /// <returns>The built-in variant.</returns>
    public static PgOid FromBuiltIn(PgBuiltInOid value, int postgresMajor)
    {
        if (!PgBuiltInOids.TryFromValue((uint)value, postgresMajor, out _, out _))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "The selected PostgreSQL version does not recognize this built-in OID.");
        }

        return new((uint)value, PgOidKind.BuiltIn);
    }

    /// <summary>
    /// Creates an oid datum, mapping only the Invalid tag to SQL NULL.
    /// </summary>
    /// <param name="context">The explicit lifetime anchor for the by-value datum.</param>
    /// <returns>A checked oid datum; Custom(0) remains a present zero.</returns>
    /// <remarks>
    /// This preserves pgrx's PgOid IntoDatum distinction between Invalid and Custom(0).
    /// Raw uint parameters and results independently preserve every unsigned value, including zero.
    /// </remarks>
    public PgDatum ToDatum(PgMemoryContext context) => PgDatum.DangerousCreate(Value, (uint)PgBuiltInOid.OidOid, context, Kind == PgOidKind.Invalid);

    /// <summary>
    /// Formats the exact numeric value as culture-independent unsigned decimal text.
    /// </summary>
    /// <returns>The decimal OID.</returns>
    public override string ToString() => Value.ToString(CultureInfo.InvariantCulture);
}
