using System;

namespace Ankus;

/// <summary>
/// Represents a non-null PostgreSQL anyelement value with its resolved type and checked native lifetime.
/// </summary>
/// <param name="datum">The present native value.</param>
public sealed class PgAnyElement(PgDatum datum)
{
    /// <summary>
    /// Gets the checked native value and its actual PostgreSQL type, including domain identity.
    /// </summary>
    public PgDatum Datum { get; } = RequireValue(datum);

    /// <summary>
    /// Gets the resolved PostgreSQL type OID.
    /// </summary>
    public uint TypeOid => Datum.TypeOid;

    /// <summary>
    /// Reads a managed value with exact type checking; polymorphic wrappers retain this value's native lifetime.
    /// </summary>
    /// <typeparam name="T">The managed representation.</typeparam>
    /// <returns>The managed copy or wrapper sharing this value's lifetime.</returns>
    public T Read<[PgConvertible] T>() => Datum.Read<T>();

    /// <summary>
    /// Copies the value into a different native owner.
    /// </summary>
    /// <param name="context">The destination memory context.</param>
    /// <returns>The independently owned value.</returns>
    public PgAnyElement CopyTo(PgMemoryContext context) => new(Datum.CopyTo(context));

    /// <summary>
    /// Rejects absent inputs; nullable wrappers represent SQL NULL.
    /// </summary>
    /// <param name="datum">The native value.</param>
    /// <returns>The present value.</returns>
    internal static PgDatum RequireValue(PgDatum datum)
    {
        ArgumentNullException.ThrowIfNull(datum);
        if (datum.IsNull)
        {
            throw new ArgumentException("Use a null polymorphic wrapper for SQL NULL.", nameof(datum));
        }

        return datum;
    }
}
