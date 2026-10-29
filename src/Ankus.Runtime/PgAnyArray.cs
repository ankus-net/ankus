using System.Collections;
using System.Collections.Generic;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Represents a PostgreSQL anyarray with its actual element type, dimensions, lower bounds, and nullable cells.
/// </summary>
public sealed class PgAnyArray : IReadOnlyList<PgAnyElement?>
{
    private readonly PgAnyElement?[] _values;
    private readonly int[] _dimensions;
    private readonly int[] _lowerBounds;

    /// <summary>
    /// Reads an array's shape and cells into the supplied datum's checked native owner.
    /// </summary>
    /// <param name="datum">A present PostgreSQL array value.</param>
    /// <remarks>
    /// Extraction preserves existing domain values without reapplying their constraints.
    /// </remarks>
    public PgAnyArray(PgDatum datum)
    {
        Datum = PgAnyElement.RequireValue(datum);
        (ElementTypeOid, _dimensions, _lowerBounds, _values) = NativeBackend.ReadPolymorphicArray(datum);
    }

    /// <summary>
    /// Gets the array's native value and actual PostgreSQL type.
    /// </summary>
    public PgDatum Datum { get; }

    /// <summary>
    /// Gets the actual array type OID, including a domain over an array.
    /// </summary>
    public uint TypeOid => Datum.TypeOid;

    /// <summary>
    /// Gets the declared array element type OID, including domains and unregistered enum types.
    /// </summary>
    public uint ElementTypeOid { get; }

    /// <summary>
    /// Gets the number of dimensions; an empty PostgreSQL array has zero dimensions.
    /// </summary>
    public int Rank => _dimensions.Length;

    /// <summary>
    /// Gets the total number of cells in row-major order.
    /// </summary>
    public int Count => _values.Length;

    /// <summary>
    /// Gets a checked cell by its zero-based flattened ordinal, or null for SQL NULL.
    /// </summary>
    /// <param name="index">The flattened ordinal.</param>
    public PgAnyElement? this[int index] => _values[index];

    /// <summary>
    /// Gets the length of a zero-based dimension.
    /// </summary>
    /// <param name="dimension">The zero-based dimension.</param>
    /// <returns>The dimension length.</returns>
    public int GetLength(int dimension) => _dimensions[dimension];

    /// <summary>
    /// Gets the PostgreSQL lower bound of a zero-based dimension.
    /// </summary>
    /// <param name="dimension">The zero-based dimension.</param>
    /// <returns>The actual lower bound.</returns>
    public int GetLowerBound(int dimension) => _lowerBounds[dimension];

    /// <summary>
    /// Reads a managed array with exact type checking; polymorphic wrappers retain this array's native lifetime.
    /// </summary>
    /// <typeparam name="T">The requested array representation.</typeparam>
    /// <returns>The managed array copy or checked polymorphic wrapper.</returns>
    public T Read<[PgConvertible] T>() => Datum.Read<T>();

    /// <summary>
    /// Copies the complete array into an independent native owner.
    /// </summary>
    /// <param name="context">The destination context.</param>
    /// <returns>The independently owned array.</returns>
    public PgAnyArray CopyTo(PgMemoryContext context) => new(Datum.CopyTo(context));

    /// <summary>
    /// Enumerates nullable cells in PostgreSQL row-major order.
    /// </summary>
    /// <returns>The checked cell iterator.</returns>
    public IEnumerator<PgAnyElement?> GetEnumerator() => ((IEnumerable<PgAnyElement?>)_values).GetEnumerator();

    /// <summary>
    /// Enumerates nullable cells through the non-generic collection contract.
    /// </summary>
    /// <returns>The cell iterator.</returns>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
