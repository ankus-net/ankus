namespace Ankus;

/// <summary>
/// Holds an exact copy of a row or composite cell whose PostgreSQL type has no managed conversion. pgrx converts a
/// field only when it is read, so such a cell does not prevent reading the rest of its row. A mapped reader can read it,
/// and writing its row back to PostgreSQL restores the original value.
/// </summary>
internal sealed class DetachedDatum
{
    private readonly byte[]? _payload;

    /// <summary>
    /// Copies a by-value datum word.
    /// </summary>
    /// <param name="typeOid">The PostgreSQL base type OID.</param>
    /// <param name="bits">The datum word.</param>
    internal DetachedDatum(uint typeOid, long bits)
    {
        TypeOid = typeOid;
        Bits = bits;
    }

    /// <summary>
    /// Copies by-reference storage.
    /// </summary>
    /// <param name="typeOid">The PostgreSQL base type OID.</param>
    /// <param name="length">The type's storage length: positive for fixed storage, -1 for a varlena, -2 for a C string.</param>
    /// <param name="payload">The fixed storage, varlena contents without the header, or C string without its terminator.</param>
    internal DetachedDatum(uint typeOid, short length, ReadOnlySpan<byte> payload)
    {
        TypeOid = typeOid;
        Bits = length;
        _payload = payload.ToArray();
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
    /// <exception cref="NotSupportedException">The cell has no managed conversion.</exception>
    internal static object? Expose(object? value)
        => value is DetachedDatum detached ? throw detached.Unsupported() : value;

    /// <summary>
    /// Creates the error for reading this cell as an ordinary managed value.
    /// </summary>
    /// <returns>The error.</returns>
    internal NotSupportedException Unsupported()
        => new($"PostgreSQL type OID {TypeOid} has no managed conversion. Read the cell with its PgDatumType, or use a raw result.");
}
