using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Defines an owned generated enum contract and statically closed array conversions.
/// </summary>
internal abstract class EnumMapping(string name, string? schema)
{
    /// <summary>
    /// Resolves the current catalog identity, optionally ignoring an absent or non-enum type.
    /// </summary>
    internal uint GetOid(bool missingOk = false) => NativeBackend.ResolveEnum(name, schema, missingOk);

    /// <summary>
    /// Resolves the enum's current array type.
    /// </summary>
    internal uint GetArrayOid() => NativeBackend.EnumArrayOid(GetOid());

    /// <summary>
    /// Gets the exact label of a defined boxed enum value.
    /// </summary>
    internal abstract string ToLabel(object value);

    /// <summary>
    /// Materializes a defined enum value from an exact label.
    /// </summary>
    internal abstract object FromLabel(string label);

    /// <summary>
    /// Copies a statically supported enum vector.
    /// </summary>
    internal abstract IPgArray Wrap(Array value);

    /// <summary>
    /// Converts owned enum arrays to a requested vector or shaped array.
    /// </summary>
    internal abstract object Convert(IPgArray value, Type type);

    /// <summary>
    /// Materializes enum array transport once its PostgreSQL identity has been checked.
    /// </summary>
    internal abstract IPgArray ReadArray(NativeValue value, uint oid);
}
