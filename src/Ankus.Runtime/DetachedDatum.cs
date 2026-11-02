using System;
using System.Globalization;

namespace Ankus;

/// <summary>
/// Holds an exact copy of a row or composite cell that has no managed conversion: a PostgreSQL type without one, or
/// text a SQL_ASCII database holds that is not valid UTF-8. pgrx converts a field only when it is read, so such a cell
/// does not prevent reading the rest of its row. A mapped reader can read a cell of a type without a conversion, and
/// writing its row back to PostgreSQL restores the original value.
/// </summary>
internal sealed class DetachedDatum
{
    private readonly byte[]? _payload;

    /// <summary>
    /// Copies a by-value datum word.
    /// </summary>
    /// <param name="typeOid">The PostgreSQL base type OID.</param>
    /// <param name="bits">The datum word.</param>
    /// <param name="untranslatable">Whether the value holds text that is not valid UTF-8.</param>
    internal DetachedDatum(uint typeOid, long bits, bool untranslatable = false)
    {
        TypeOid = typeOid;
        Bits = bits;
        IsUntranslatable = untranslatable;
    }

    /// <summary>
    /// Copies by-reference storage.
    /// </summary>
    /// <param name="typeOid">The PostgreSQL base type OID.</param>
    /// <param name="length">The type's storage length: positive for fixed storage, -1 for a varlena, -2 for a C string.</param>
    /// <param name="payload">The fixed storage, varlena contents without the header, or C string without its terminator.</param>
    /// <param name="untranslatable">Whether the value holds text that is not valid UTF-8.</param>
    internal DetachedDatum(uint typeOid, short length, ReadOnlySpan<byte> payload, bool untranslatable = false)
    {
        TypeOid = typeOid;
        Bits = length;
        _payload = payload.ToArray();
        IsUntranslatable = untranslatable;
    }

    /// <summary>
    /// Gets the PostgreSQL base type OID.
    /// </summary>
    internal uint TypeOid { get; }

    /// <summary>
    /// Gets whether the value is a by-value datum word.
    /// </summary>
    internal bool IsByValue => _payload is null;

    /// <summary>
    /// Gets whether the value holds text that is not valid UTF-8, which a SQL_ASCII database stores unchecked.
    /// </summary>
    internal bool IsUntranslatable { get; }

    /// <summary>
    /// Gets the datum word of a by-value type, or the storage length of a by-reference type.
    /// </summary>
    internal long Bits { get; }

    /// <summary>
    /// Gets the by-reference storage, which is empty for a by-value type.
    /// </summary>
    internal ReadOnlySpan<byte> Payload => _payload;

    /// <summary>
    /// Returns a managed cell, rejecting a value that only a mapped reader or PostgreSQL can interpret.
    /// </summary>
    /// <param name="value">The stored cell.</param>
    /// <returns>The cell.</returns>
    /// <exception cref="NotSupportedException">The cell's type has no managed conversion.</exception>
    /// <exception cref="PgException">The cell holds text that is not valid UTF-8.</exception>
    internal static object? Expose(object? value)
        => value is DetachedDatum detached ? throw detached.Unsupported() : value;

    /// <summary>
    /// Creates the error for reading this cell as an ordinary managed value. Text that is not valid UTF-8 fails with
    /// the SQLSTATE PostgreSQL's own conversion to UTF-8 raises.
    /// </summary>
    /// <returns>The error.</returns>
    internal Exception Unsupported()
        => IsUntranslatable
            ? new PgException(PgSqlStates.CharacterNotInRepertoire,
                string.Create(CultureInfo.InvariantCulture,
                    $"A value of PostgreSQL type OID {TypeOid} holds bytes that are not valid UTF-8, so it has no managed value."),
                hint: "Read the cell as PgDatum to keep its exact bytes.")
            : new NotSupportedException(
                $"PostgreSQL type OID {TypeOid} has no managed conversion. Read the cell with its PgDatumType, or use a raw result.");
}
