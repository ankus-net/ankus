using System;

namespace Ankus;

public sealed unsafe partial class PgMemoryContext
{
    /// <summary>
    /// PostgreSQL's MaxAllocSize, the largest single allocation.
    /// </summary>
    private const int MaxAllocationSize = 0x3FFF_FFFF;

    /// <summary>
    /// PostgreSQL's MaxArraySize, the most elements an array can hold.
    /// </summary>
    private const int MaxArrayElements = MaxAllocationSize / 8;

    /// <summary>
    /// PostgreSQL's MAXDIM, the most dimensions an array can have.
    /// </summary>
    private const int MaxArrayDimensions = 6;

    /// <summary>
    /// Allocates a zeroed flat array of fixed-size scalars in this context, as pgrx's <c>FlatArray::new_zeroed_in</c> does.
    /// </summary>
    /// <typeparam name="T">The element: sbyte (<c>"char"</c>), short, int, long, uint (<c>oid</c>), float, double or bool.</typeparam>
    /// <param name="lengths">The length of each dimension; none creates an empty array.</param>
    /// <param name="lowerBounds">The lower bound of each dimension, or none for one-based dimensions.</param>
    /// <returns>The array, filled with zero and living until this context resets or is deleted.</returns>
    /// <exception cref="NotSupportedException">T is not a supported fixed-size scalar.</exception>
    /// <exception cref="ArgumentException">The lower bounds do not match the dimensions.</exception>
    /// <exception cref="ArgumentOutOfRangeException">
    /// There are more than six dimensions, a dimension is not positive, a bound overflows, or the array exceeds
    /// PostgreSQL's element or allocation limit.
    /// </exception>
    public PgFlatArray<T> CreateFlatArray<T>(ReadOnlySpan<int> lengths, ReadOnlySpan<int> lowerBounds = default) where T : unmanaged
    {
        uint elementType = typeof(T) == typeof(sbyte) ? 18u
            : typeof(T) == typeof(short) ? 21u
            : typeof(T) == typeof(int) ? 23u
            : typeof(T) == typeof(long) ? 20u
            : typeof(T) == typeof(uint) ? 26u
            : typeof(T) == typeof(float) ? 700u
            : typeof(T) == typeof(double) ? 701u
            : typeof(T) == typeof(bool) ? 16u
            : throw new NotSupportedException($"'{typeof(T)}' has no fixed-size PostgreSQL array element layout.");
        ArgumentOutOfRangeException.ThrowIfGreaterThan(lengths.Length, MaxArrayDimensions, nameof(lengths));
        if (!lowerBounds.IsEmpty && lowerBounds.Length != lengths.Length)
        {
            throw new ArgumentException("Supply one lower bound per dimension, or none.", nameof(lowerBounds));
        }

        long count = lengths.IsEmpty ? 0 : 1;
        int[] bounds = new int[lengths.Length];
        for (int dimension = 0; dimension < lengths.Length; dimension++)
        {
            if (lengths[dimension] <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(lengths),
                    "Array dimensions must be positive; create an empty array with no dimensions.");
            }

            bounds[dimension] = lowerBounds.IsEmpty ? 1 : lowerBounds[dimension];
            if ((long)bounds[dimension] + lengths[dimension] - 1 > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(lowerBounds), "An array upper bound exceeds the integer range.");
            }

            count *= lengths[dimension];
            if (count > MaxArrayElements)
            {
                throw new ArgumentOutOfRangeException(nameof(lengths),
                    $"The array exceeds PostgreSQL's limit of {MaxArrayElements} elements.");
            }
        }

        // ArrayType: varlena header, ndim, dataoffset and elemtype, then the dimensions and lower bounds; elements start
        // at the next maximally aligned offset. Without a NULL bitmap, dataoffset stays zero.
        nuint header = (nuint)(16 + (8 * lengths.Length));
        nuint dataOffset = (header + 7) & ~(nuint)7;
        long size = (long)dataOffset + (count * sizeof(T));
        if (size > MaxAllocationSize)
        {
            throw new ArgumentOutOfRangeException(nameof(lengths),
                $"The array's {size} bytes exceed PostgreSQL's single allocation limit of {MaxAllocationSize} bytes.");
        }

        // Arrays always use a 4-byte varlena header; SET_VARSIZE stores the length shifted on little-endian targets.
        PgAllocation storage = AllocateZeroed((nuint)size);
        storage.Write(BitConverter.IsLittleEndian ? (uint)size << 2 : (uint)size & 0x3FFF_FFFF, 0);
        storage.Write(lengths.Length, 4);
        storage.Write(0, 8);
        storage.Write(elementType, 12);
        for (int dimension = 0; dimension < lengths.Length; dimension++)
        {
            storage.Write(lengths[dimension], (nuint)(16 + (4 * dimension)));
            storage.Write(bounds[dimension], (nuint)(16 + (4 * (lengths.Length + dimension))));
        }

        PgDatum datum = PgDatum.DangerousCreate((nuint)storage.DangerousGetPointer(), SpiArray.ArrayOid(elementType), this);
        return new PgFlatArray<T>(storage, dataOffset, lengths.ToArray(), bounds, (int)count, datum);
    }
}
