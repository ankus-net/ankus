using System;
using System.Buffers.Binary;

namespace Ankus.CompilerServices;

public static unsafe partial class NativeBackend
{
    /// <summary>
    /// Encodes only shape and nominal element identity, independently of PostgreSQL's physical array layout.
    /// </summary>
    internal static byte[] EncodeMappedArrayShape(IPgArray array, uint elementOid)
    {
        SpiArray.ValidateShape(array.Count, array.Lengths, array.LowerBounds);
        int rank = array.Lengths.Length;
        byte[] shape = new byte[12 + rank * 8];
        BinaryPrimitives.WriteInt32BigEndian(shape, rank);
        BinaryPrimitives.WriteInt32BigEndian(shape.AsSpan(4), array.Count);
        BinaryPrimitives.WriteUInt32BigEndian(shape.AsSpan(8), elementOid);
        for (int dimension = 0; dimension < rank; dimension++)
        {
            BinaryPrimitives.WriteInt32BigEndian(shape.AsSpan(12 + dimension * 8), array.Lengths[dimension]);
            BinaryPrimitives.WriteInt32BigEndian(shape.AsSpan(16 + dimension * 8), array.LowerBounds[dimension]);
        }

        return shape;
    }

    /// <summary>
    /// Eagerly constructs an exactly typed array and copies it into the final checked destination.
    /// </summary>
    internal static PgDatum BuildMappedArray(ReadOnlySpan<SpiParameter> parameters, uint arrayOid, PgDatumLifetime destination)
    {
        CheckAccess();
        destination.Validate();
        NativeSpiRequest request = new()
        {
            _operation = SpiOperation.Array,
            _scalarResultOid = arrayOid,
            _resultContext = destination.ContextId,
            _resultGeneration = destination.Generation,
        };
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, parameters, &result);
            if (result._resultTypeOid != arrayOid || result._text.IsNull != 0)
            {
                throw new InvalidOperationException("The native mapped array builder returned an invalid result identity.");
            }

            return new PgDatum(unchecked((nuint)result._text.Integral), arrayOid, false, destination);
        }
        finally
        {
            ReleaseResult(&result);
        }
    }
}
