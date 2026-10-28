using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Closes vector and shaped conversions without selecting converters from runtime values or PostgreSQL OIDs.
/// </summary>
internal sealed class DatumArrayMapping<T>(DatumTypeMapping element) : DatumArrayMapping(element)
{
    /// <inheritdoc />
    protected override object ReadPresent(PgDatum value, uint elementOid, Type requested)
    {
        PgMemoryContext temporary = PgMemoryContext.Create("Ankus mapped array reader", PgMemoryContext.Callback);
        Exception? primary = null;
        try
        {
            var lifetime = new PgDatumLifetime(temporary);
            (uint actualElement, int[] lengths, int[] bounds, PgAnyElement?[] cells) = NativeBackend.ReadPolymorphicArray(value, lifetime);
            if (actualElement != elementOid)
            {
                throw new InvalidCastException($"Array element type OID {actualElement} does not match mapped type OID {elementOid}.");
            }

            SpiArray.ValidateShape(cells.Length, lengths, bounds);
            if (cells.Length == 0 && lengths.Length != 0)
            {
                throw new InvalidOperationException("Empty native arrays must have rank zero.");
            }

            bool vector = requested == typeof(T[]);
            if (vector && (lengths.Length > 1 || (lengths.Length == 1 && bounds[0] != 1)))
            {
                throw new InvalidOperationException("Use PgArray<T> to preserve dimensions and lower bounds, or ToArray() to explicitly flatten them.");
            }

            var values = new T[cells.Length];
            for (int index = 0; index < values.Length; index++)
            {
                PgDatum datum = cells[index]?.Datum ?? new PgDatum(0, elementOid, true, lifetime);
                values[index] = SpiRow.Convert<T>(Element.Read(datum));
            }

            return vector ? values : new PgArray<T>(values, (lengths, bounds));
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
    protected override IPgArray Wrap(object value)
    {
        if (value is PgArray<T> shaped)
        {
            return shaped;
        }

        if (value is T[] vector && (!typeof(T).IsValueType || value.GetType() == typeof(T[])))
        {
            return new PgArray<T>(vector);
        }

        throw new InvalidCastException($"Array cannot be converted to '{typeof(T)}' elements.");
    }
}
