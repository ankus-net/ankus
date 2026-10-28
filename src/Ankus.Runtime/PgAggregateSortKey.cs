using System;

namespace Ankus;

/// <summary>
/// Owns one PostgreSQL aggregate ordering key, including its operator, collation and NULL placement.
/// </summary>
public sealed class PgAggregateSortKey
{
    /// <summary>
    /// Validates and copies the native ordering description.
    /// </summary>
    internal PgAggregateSortKey(int argumentIndex, uint typeOid, uint operatorOid, uint collationOid, bool nullsFirst)
    {
        if (argumentIndex < 0 || typeOid == 0 || operatorOid == 0)
        {
            throw new InvalidOperationException("Aggregate sort keys require a nonnegative argument index and valid type and operator OIDs.");
        }

        ArgumentIndex = argumentIndex;
        TypeOid = typeOid;
        OperatorOid = operatorOid;
        CollationOid = collationOid;
        NullsFirst = nullsFirst;
    }

    /// <summary>
    /// Gets the zero-based aggregated input position. An executor-only expression can exceed the public input count.
    /// </summary>
    public int ArgumentIndex { get; }

    /// <summary>
    /// Gets the ordering expression's PostgreSQL type OID.
    /// </summary>
    public uint TypeOid { get; }

    /// <summary>
    /// Gets the ordering operator OID, preserving ascending, descending and custom operator semantics.
    /// </summary>
    public uint OperatorOid { get; }

    /// <summary>
    /// Gets the ordering expression's collation OID, or zero for a noncollatable value.
    /// </summary>
    public uint CollationOid { get; }

    /// <summary>
    /// Gets whether SQL NULL sorts before nonnull values for this key.
    /// </summary>
    public bool NullsFirst { get; }
}
