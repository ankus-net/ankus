using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Converts generated enum labels without reflection and resolves their live PostgreSQL type identities.
/// </summary>
public static class PgEnums
{
    /// <summary>
    /// Gets the exact PostgreSQL label without requiring a backend. Undefined numeric values are rejected.
    /// </summary>
    /// <typeparam name="T">A C# enum declared with PgEnum.</typeparam>
    /// <param name="value">The declared enum value.</param>
    /// <returns>The case-sensitive label.</returns>
    public static string GetLabel<[PgConvertible] T>(T value) where T : struct, Enum => PgEnumRegistry.Require(typeof(T)).ToLabel(value);

    /// <summary>
    /// Converts an exact PostgreSQL label without requiring a backend. Unknown labels are rejected.
    /// </summary>
    /// <typeparam name="T">A C# enum declared with PgEnum.</typeparam>
    /// <param name="label">The exact case-sensitive label.</param>
    /// <returns>The declared enum value.</returns>
    public static T Parse<[PgConvertible] T>(string label) where T : struct, Enum => (T)PgEnumRegistry.Require(typeof(T)).FromLabel(label);

    /// <summary>
    /// Resolves the enum type in the active extension's installation schema or its declared fixed schema.
    /// The result is looked up on each call so relocation and type recreation cannot leave stale OIDs.
    /// </summary>
    /// <typeparam name="T">A C# enum declared with PgEnum.</typeparam>
    /// <returns>The current PostgreSQL type OID.</returns>
    public static uint GetTypeOid<[PgConvertible] T>() where T : struct, Enum => PgEnumRegistry.Require(typeof(T)).GetOid();

    /// <summary>
    /// Resolves the stored PostgreSQL enum value OID from a generated value and its current catalog type.
    /// PostgreSQL validates the label and transaction visibility on the active backend.
    /// </summary>
    /// <typeparam name="T">A C# enum declared with PgEnum.</typeparam>
    /// <param name="value">The declared enum value.</param>
    /// <returns>The pg_enum row OID.</returns>
    public static uint GetValueOid<[PgConvertible] T>(T value) where T : struct, Enum
        => NativeBackend.EnumValueOid(GetTypeOid<T>(), GetLabel(value));

    /// <summary>
    /// Resolves a label of any enum type, including one without a generated managed mapping, to its pg_enum value OID.
    /// Use <see cref="PgTypes.GetOid(string)"/> to find the type by name. PostgreSQL validates the label and transaction
    /// visibility on the active backend.
    /// </summary>
    /// <param name="typeOid">The enum type OID.</param>
    /// <param name="label">The exact case-sensitive label.</param>
    /// <returns>The pg_enum row OID.</returns>
    public static uint GetValueOid(uint typeOid, string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return NativeBackend.EnumValueOid(typeOid, label);
    }

    /// <summary>
    /// Looks up an enum datum OID on the active backend and copies its label, type and sort order.
    /// This also supports enum types without a generated managed mapping.
    /// </summary>
    /// <param name="valueOid">The pg_enum row OID, as returned by GetValueOid.</param>
    /// <returns>Owned catalog information.</returns>
    public static PgEnumInfo Lookup(uint valueOid) => NativeBackend.EnumInfo(valueOid);
}
