using System;

namespace Ankus;

/// <summary>
/// A flat PostgreSQL array of fixed-size scalars allocated in a memory context and filled in place, as pgrx's
/// <c>FlatArray</c> is.
/// </summary>
/// <typeparam name="T">The element: sbyte (<c>"char"</c>), short, int, long, uint (<c>oid</c>), float, double or bool.</typeparam>
/// <remarks>
/// The storage uses PostgreSQL's native array layout without a NULL bitmap, so every element is present and starts at
/// zero. It lives until its memory context resets or is deleted. Build arrays with SQL NULL elements with
/// <see cref="PgArray{T}"/>: PostgreSQL does not store NULL elements, so their positions cannot be filled in place.
/// Return the array from a function through <see cref="PgArrayView{T}"/> over <see cref="Datum"/>.
/// </remarks>
public sealed unsafe class PgFlatArray<T> where T : unmanaged
{
    private readonly PgAllocation _storage;
    private readonly nuint _dataOffset;
    private readonly int[] _lengths;
    private readonly int[] _lowerBounds;

    /// <summary>
    /// Wraps storage whose header, dimensions and bounds are already written.
    /// </summary>
    internal PgFlatArray(PgAllocation storage, nuint dataOffset, int[] lengths, int[] lowerBounds, int count, PgDatum datum)
    {
        _storage = storage;
        _dataOffset = dataOffset;
        _lengths = lengths;
        _lowerBounds = lowerBounds;
        Count = count;
        Datum = datum;
    }

    /// <summary>
    /// Gets the array datum, whose lifetime is the owning memory context.
    /// </summary>
    public PgDatum Datum { get; }

    /// <summary>
    /// Gets the number of elements.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Gets the number of dimensions; an empty array has none.
    /// </summary>
    public int Rank => _lengths.Length;

    /// <summary>
    /// Gets the length of each dimension.
    /// </summary>
    public ReadOnlySpan<int> Lengths => _lengths;

    /// <summary>
    /// Gets the lower bound of each dimension.
    /// </summary>
    public ReadOnlySpan<int> LowerBounds => _lowerBounds;

    /// <summary>
    /// Borrows the elements in row-major order for reading and writing.
    /// </summary>
    /// <returns>The native element storage.</returns>
    /// <remarks>
    /// Ownership is checked when acquiring the span. Stop using it before the memory context resets or is deleted, the
    /// callback ends or the thread changes.
    /// </remarks>
    public Span<T> DangerousGetSpan() => new((byte*)_storage.DangerousGetPointer() + _dataOffset, Count);
}
