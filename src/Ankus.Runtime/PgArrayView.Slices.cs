using System;
using Ankus.CompilerServices;

namespace Ankus;

public sealed partial class PgArrayView
{
    /// <summary>
    /// Borrows a contiguous, read-only native span from an array without SQL NULL elements.
    /// </summary>
    /// <typeparam name="T">Exactly sbyte, short, int, long, float or double.</typeparam>
    /// <returns>The original row-major payload, independent of native dimension lower bounds.</returns>
    /// <remarks>
    /// The element's PostgreSQL base type must match T, including for empty arrays.
    /// Domains retain their original identity and are not reassigned through constraints.
    /// Lifetime checks occur when acquiring the span. Do not use it after disposing the view,
    /// resetting or deleting its source, leaving its callback, switching threads or making
    /// another backend call. Copy the span to managed storage first when longer retention is needed.
    /// UUID arrays require DangerousGetUuidBytes because Guid has a different memory layout.
    /// </remarks>
    /// <exception cref="NotSupportedException">T has no supported native scalar layout.</exception>
    /// <exception cref="PgException">The element type is incompatible or a cell is SQL NULL.</exception>
    public unsafe ReadOnlySpan<T> DangerousGetSpan<T>() where T : unmanaged
    {
        _datum.Lifetime.Validate();
        PgBuiltInOid type = typeof(T) == typeof(sbyte) ? PgBuiltInOid.CharOid
            : typeof(T) == typeof(short) ? PgBuiltInOid.Int2Oid
            : typeof(T) == typeof(int) ? PgBuiltInOid.Int4Oid
            : typeof(T) == typeof(long) ? PgBuiltInOid.Int8Oid
            : typeof(T) == typeof(float) ? PgBuiltInOid.Float4Oid
            : typeof(T) == typeof(double) ? PgBuiltInOid.Float8Oid
            : throw new NotSupportedException($"'{typeof(T)}' has no supported native array slice layout.");
        nint data = NativeBackend.BorrowArraySlice(_datum, type, sizeof(T), Count);
        return new ReadOnlySpan<T>((void*)data, Count);
    }

    /// <summary>
    /// Borrows the array's native SQL NULL bitmap, as pgrx's <c>RawArray::nulls</c> does.
    /// </summary>
    /// <returns>
    /// One bit per element in row-major order, least significant bit first, where a set bit marks a present value; empty
    /// when the array stores no bitmap.
    /// </returns>
    /// <remarks>
    /// PostgreSQL stores a bitmap only when the array was built with SQL NULL elements. Lifetime checks occur when
    /// acquiring the span; stop using it before any backend call, owner expiry, callback exit or thread change. Copy the
    /// bytes to managed storage when they must outlive this native borrow.
    /// </remarks>
    public unsafe ReadOnlySpan<byte> DangerousGetNullBitmap()
    {
        // ArrayType is a 4-byte varlena header, ndim, dataoffset and elemtype; the dimensions, lower bounds and the
        // optional bitmap follow. The borrowed datum is always a flat array with a 4-byte header.
        byte* array = (byte*)_datum.DangerousGetBits();
        int rank = *(int*)(array + 4);
        int dataOffset = *(int*)(array + 8);
        return dataOffset == 0 ? [] : new ReadOnlySpan<byte>(array + 16 + (8 * rank), (Count + 7) / 8);
    }

    /// <summary>
    /// Borrows the contiguous network-order bytes of an array without SQL NULL UUID elements.
    /// </summary>
    /// <returns>Exactly sixteen bytes per UUID in row-major order.</returns>
    /// <remarks>
    /// Each sixteen-byte segment can be read with new Guid(segment, bigEndian: true).
    /// Do not reinterpret this storage as Guid values. Lifetime checks occur when acquiring
    /// the span; stop using it before any backend call, owner expiry, callback exit or thread change.
    /// Copy the bytes to managed storage when they must outlive this native borrow.
    /// </remarks>
    /// <exception cref="PgException">The elements are not UUIDs or a cell is SQL NULL.</exception>
    public unsafe ReadOnlySpan<byte> DangerousGetUuidBytes()
    {
        _datum.Lifetime.Validate();
        int length = checked(Count * 16);
        nint data = NativeBackend.BorrowArraySlice(_datum, PgBuiltInOid.UuidOid, 16, Count);
        return new ReadOnlySpan<byte>((void*)data, length);
    }
}
