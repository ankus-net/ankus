using System;

namespace Ankus.CompilerServices;

public static unsafe partial class NativeBackend
{
    /// <summary>
    /// Resolves a range's current scalar subtype under the native catalog error guard.
    /// </summary>
    internal static uint RangeSubtype(uint rangeOid)
        => Scalar<uint>(SpiOperation.Range, (int)RangeOperation.Subtype, [SpiParameter.Create(rangeOid)]);

    /// <summary>
    /// Copies only finite raw bounds into a selected owner, preserving empty and infinite flags.
    /// </summary>
    internal static (uint Subtype, int Flags, PgDatum? Lower, PgDatum? Upper) ReadMappedRange(PgDatum value, PgDatumLifetime destination)
        => RunDatum(value, 4, destination, result =>
        {
            int flags = checked((int)result._text.Integral);
            if (result._text.IsNull != 0 || result._resultTypeOid == 0 || result._rowCount != 2 ||
                result._columnCount != 1 || result._values == null || (flags & ~31) != 0 ||
                (flags & 1) != 0 && flags != 1 || (flags & 10) == 10 || (flags & 20) == 20)
            {
                throw new InvalidOperationException("Invalid native mapped range header.");
            }

            PgDatum? lower = ReadBound(0, flags == 1 || (flags & 8) != 0);
            PgDatum? upper = ReadBound(1, flags == 1 || (flags & 16) != 0);
            return (result._resultTypeOid, flags, lower, upper);

            PgDatum? ReadBound(int index, bool absent)
            {
                NativeValue bound = result._values[index];
                if (bound.IsNull != (absent ? 1 : 0))
                {
                    throw new InvalidOperationException("Native mapped range bound does not match its inclusion flags.");
                }

                return absent ? null : new PgDatum(unchecked((nuint)bound.Integral), result._resultTypeOid, false, destination);
            }
        });

    /// <summary>
    /// Constructs a range from exact raw bounds before releasing temporary converter storage.
    /// </summary>
    internal static PgDatum BuildMappedRange(ReadOnlySpan<SpiParameter> parameters, uint rangeOid, PgDatumLifetime destination)
    {
        CheckAccess();
        destination.Validate();
        NativeSpiRequest request = new()
        {
            _operation = SpiOperation.Range,
            _scalarOperation = (int)RangeOperation.BuildMapped,
            _scalarResultOid = rangeOid,
            _resultContext = destination.ContextId,
            _resultGeneration = destination.Generation,
        };
        NativeSpiResult result = default;
        try
        {
            InvokeParameters(&request, parameters, &result);
            if (result._resultTypeOid != rangeOid || result._text.IsNull != 0)
            {
                throw new InvalidOperationException("The native mapped range builder returned an invalid result identity.");
            }

            return new PgDatum(unchecked((nuint)result._text.Integral), rangeOid, false, destination);
        }
        finally
        {
            ReleaseResult(&result);
        }
    }

    /// <summary>
    /// Reads an allowlisted range operation's result through its exact converter under temporary ownership.
    /// </summary>
    private static T MappedRangeScalar<T>(RangeOperation operation, ReadOnlySpan<SpiParameter> parameters, DatumTypeMapping mapping)
    {
        CheckAccess();
        mapping.RequireRead();
        uint typeOid = mapping.GetOid();
        PgMemoryContext temporary = PgMemoryContext.Create("Ankus mapped range result", PgMemoryContext.Callback);
        NativeSpiResult result = default;
        Exception? primary = null;
        try
        {
            var lifetime = new PgDatumLifetime(temporary);
            NativeSpiRequest request = new()
            {
                _operation = SpiOperation.Range,
                _scalarOperation = (int)operation,
                _scalarResultOid = typeOid,
                _resultContext = lifetime.ContextId,
                _resultGeneration = lifetime.Generation,
            };
            InvokeParameters(&request, parameters, &result);
            if (result._resultTypeOid != typeOid)
            {
                throw new InvalidOperationException("The native range operation returned an invalid result identity.");
            }

            var datum = new PgDatum(unchecked((nuint)result._text.Integral), typeOid, result._text.IsNull != 0, lifetime);
            return SpiRow.Convert<T>(mapping.Read(datum));
        }
        catch (Exception exception)
        {
            primary = exception;
            throw;
        }
        finally
        {
            ReleaseResult(&result);
            PgResultCleanup.Dispose(temporary, primary);
        }
    }
}
