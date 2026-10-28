using System;

namespace Ankus.CompilerServices;

public static unsafe partial class NativeBackend
{
    /// <summary>
    /// Borrows binary bytes, strictly validated UTF-8 text, or uninterpreted C-string bytes.
    /// </summary>
    internal static (PgDatum Datum, nint Data, int Length) BorrowBuffer(PgDatum value, PgDatumLifetime lifetime, NativeBufferKind kind)
    {
        CheckAccess();
        lifetime.Validate();
        NativeSpiRequest request = new()
        {
            _operation = SpiOperation.Datum,
            _scalarOperation = (int)kind,
            _resultContext = lifetime.ContextId,
            _resultGeneration = lifetime.Generation,
        };
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, [SpiParameter.Create(value)], &result);
            if (result._text.Integral == 0 || result._rowsAffected == 0 || result._rowCount < 0)
            {
                throw new InvalidOperationException("Invalid borrowed buffer metadata.");
            }

            return (new PgDatum(unchecked((nuint)result._text.Integral), value.TypeOid, false, lifetime),
                unchecked((nint)result._rowsAffected), result._rowCount);
        }
        finally
        {
            ReleaseResult(&result);
        }
    }
}
