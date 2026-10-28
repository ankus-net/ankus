using System;
using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

public static unsafe partial class NativeBackend
{
    /// <summary>
    /// Checks a declared array element contract without dereferencing a SQL NULL datum or allocating a view.
    /// </summary>
    internal static void ValidateArrayElement(PgDatum value, uint elementType, bool exactIdentity)
        => RunArrayView(value, 12, value.Lifetime, exactIdentity ? 1 : 0, 0, static result =>
        {
            if (result._resultTypeOid == 0)
            {
                throw new InvalidOperationException("Invalid borrowed array element identity.");
            }

            return result._resultTypeOid;
        }, elementType);

    /// <summary>
    /// Borrows flat array storage, flattening into the selected owner only when required by PostgreSQL.
    /// </summary>
    internal static (PgDatum Datum, uint Element, int[] Lengths, int[] Bounds, int Count, bool HasNulls) BorrowArray(
        PgDatum value, PgDatumLifetime lifetime, uint elementType = 0, bool exactIdentity = false)
        => RunArrayView(value, 5, lifetime, exactIdentity ? 1 : 0, 0, result =>
        {
            byte[] shape = result._text.ReadBytes();
            if (shape.Length % 8 != 0 || shape.Length > 48 || result._rowCount < 0 || result._resultTypeOid == 0)
            {
                throw new InvalidOperationException("Invalid borrowed array metadata.");
            }

            ReadOnlySpan<int> dimensions = MemoryMarshal.Cast<byte, int>(shape);
            int rank = dimensions.Length / 2;
            return (new PgDatum(unchecked((nuint)result._text.Integral), value.TypeOid, false, lifetime),
                result._resultTypeOid, dimensions[..rank].ToArray(), dimensions[rank..].ToArray(),
                result._rowCount, result._rowsAffected != 0);
        }, elementType);

    /// <summary>
    /// Visits the requested flat cell without copying its by-reference storage.
    /// </summary>
    internal static PgDatum ReadArrayCell(PgDatum array, int index)
        => RunArrayView(array, 6, array.Lifetime, index, 0, result => ReadBorrowedCell(result, array.Lifetime));

    /// <summary>
    /// Allocates one native iterator in its checked bookkeeping context.
    /// </summary>
    internal static nint CreateArrayIterator(PgDatum array, PgDatumLifetime lifetime)
        => RunArrayView(array, 7, lifetime, 0, 0, static result => unchecked((nint)result._text.Integral));

    /// <summary>
    /// Advances a native iterator and retains the array's lifetime for each returned cell.
    /// </summary>
    internal static PgDatum? AdvanceArrayIterator(PgDatum array, PgDatumLifetime lifetime, nint iterator)
        => RunArrayView(array, 8, lifetime, 0, iterator,
            result => result._rowsAffected == 0 ? null : ReadBorrowedCell(result, array.Lifetime));

    /// <summary>
    /// Validates a fixed native element layout and borrows its contiguous non-NULL payload.
    /// </summary>
    internal static nint BorrowArraySlice(PgDatum array, PgBuiltInOid type, int width, int count)
        => RunArrayView(array, 11, array.Lifetime, width, 0, result =>
        {
            nint data = unchecked((nint)result._text.Integral);
            int alignment = type == PgBuiltInOid.UuidOid ? 1 : width;
            if (result._rowCount != count || result._resultTypeOid != (uint)type || data == 0 ||
                unchecked((nuint)data) % (nuint)alignment != 0)
            {
                throw new InvalidOperationException("Invalid borrowed array slice metadata.");
            }

            return data;
        }, (uint)type);

    /// <summary>
    /// Decodes exact raw bits and NULL state without assigning the cell through a converter.
    /// </summary>
    private static PgDatum ReadBorrowedCell(NativeSpiResult result, PgDatumLifetime lifetime)
        => new(unchecked((nuint)result._text.Integral), result._resultTypeOid, result._text.IsNull != 0, lifetime);

    /// <summary>
    /// Executes a guarded array operation and always releases the separately owned response buffers.
    /// </summary>
    private static T RunArrayView<T>(PgDatum array, int operation, PgDatumLifetime destination, int index, nint iterator,
        Func<NativeSpiResult, T> convert, uint elementType = 0)
    {
        CheckAccess();
        destination.Validate();
        NativeSpiRequest request = new()
        {
            _operation = SpiOperation.Datum,
            _scalarOperation = operation,
            _resultContext = destination.ContextId,
            _resultGeneration = destination.Generation,
            _limit = index,
            _arrayIterator = iterator,
            _scalarResultOid = elementType,
        };
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, [SpiParameter.Create(array)], &result);
            return convert(result);
        }
        finally
        {
            ReleaseResult(&result);
        }
    }
}
