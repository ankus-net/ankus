using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Composes a finite range with its scalar reader and writer while owning temporary native bounds.
/// </summary>
internal sealed class DatumRangeConverter<T>(DatumTypeMapping bound) : IPgDatumReader<PgRange<T>>, IPgDatumWriter<PgRange<T>> where T : struct
{
    /// <inheritdoc />
    public PgRange<T> Read(PgDatum value)
    {
        bound.RequireRead();
        uint expected = bound.GetOid();
        PgMemoryContext temporary = PgMemoryContext.Create("Ankus mapped range reader", PgMemoryContext.Callback);
        Exception? primary = null;
        try
        {
            (uint subtype, int flags, PgDatum? lower, PgDatum? upper) = NativeBackend.ReadMappedRange(value, new PgDatumLifetime(temporary));
            if (subtype != expected)
            {
                throw new InvalidCastException($"PostgreSQL range subtype OID {subtype} does not match mapped bound type OID {expected}.");
            }

            if (flags == 1)
            {
                return new();
            }

            T? lowerValue = lower is null ? null : SpiRow.Convert<T>(bound.Read(lower));
            T? upperValue = upper is null ? null : SpiRow.Convert<T>(bound.Read(upper));
            return new(lowerValue, upperValue, (flags & 2) != 0, (flags & 4) != 0);
        }
        catch (Exception exception)
        {
            primary = exception;
            throw;
        }
        finally
        {
            PgResultCleanup.Dispose(temporary, primary);
        }
    }

    /// <inheritdoc />
    public PgDatum Write(PgRange<T> value, uint typeOid, PgMemoryContext destination)
    {
        bound.RequireWrite();
        uint subtype = bound.GetOid();
        var lifetime = new PgDatumLifetime(destination);
        int flags = value.IsEmpty ? 1 : (value.Lower is null ? 8 : value.LowerInclusive ? 2 : 0) |
            (value.Upper is null ? 16 : value.UpperInclusive ? 4 : 0);
        PgMemoryContext temporary = PgMemoryContext.Create("Ankus mapped range writer", PgMemoryContext.Callback);
        Exception? primary = null;
        try
        {
            SpiParameter[] parameters = [SpiParameter.Create(flags), Convert(value.Lower), Convert(value.Upper)];
            return NativeBackend.BuildMappedRange(parameters, typeOid, lifetime);
        }
        catch (Exception exception)
        {
            primary = exception;
            throw;
        }
        finally
        {
            PgResultCleanup.Dispose(temporary, primary);
        }

        SpiParameter Convert(T? item)
        {
            if (!item.HasValue)
            {
                return SpiParameter.CreateType(subtype);
            }

            PgDatum datum = bound.WriteDatum(item.Value, subtype, temporary);
            if (datum.IsNull)
            {
                throw new InvalidOperationException("A finite range bound writer cannot return SQL NULL.");
            }

            return SpiParameter.Create(datum);
        }
    }
}
