using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Composes one closed array representation with its shared scalar datum converter.
/// </summary>
internal abstract class DatumArrayMapping(DatumTypeMapping element)
{
    /// <summary>
    /// Gets the shared scalar contract without constructing its converter.
    /// </summary>
    protected DatumTypeMapping Element { get; } = element;

    /// <summary>
    /// Requires the element reader before execution or result allocation.
    /// </summary>
    internal void RequireRead() => Element.RequireRead();

    /// <summary>
    /// Requires the element writer before execution or NULL bypass.
    /// </summary>
    internal void RequireWrite() => Element.RequireWrite();

    /// <summary>
    /// Resolves the current true array identity of the exact mapped element.
    /// </summary>
    internal uint GetOid() => NativeBackend.EnumArrayOid(Element.GetOid());

    /// <summary>
    /// Reads an exactly typed raw array into the requested detached vector or shaped representation.
    /// </summary>
    internal object? Read(PgDatum value, Type requested)
    {
        RequireRead();
        value.Lifetime.Validate();
        uint elementOid = Element.GetOid();
        uint arrayOid = NativeBackend.EnumArrayOid(elementOid);
        if (value.TypeOid != arrayOid)
        {
            throw new InvalidCastException($"PostgreSQL datum type OID {value.TypeOid} does not match mapped array type OID {arrayOid}.");
        }

        return value.IsNull ? null : ReadPresent(value, elementOid, requested);
    }

    /// <summary>
    /// Builds an owned native array while retaining its declared element converter and captured parameter identity.
    /// </summary>
    internal NativeValue Write(object? value, uint capturedOid = 0)
    {
        RequireWrite();
        uint elementOid = Element.GetOid();
        uint arrayOid = NativeBackend.EnumArrayOid(elementOid);
        if (capturedOid != 0 && capturedOid != arrayOid)
        {
            throw new InvalidOperationException("The mapped PostgreSQL parameter type has changed since the parameter was created.");
        }

        if (value is null)
        {
            return new NativeValue { IsNull = 1 };
        }

        return NativeValue.FromPolymorphic(Build(Wrap(value), elementOid, arrayOid, PgMemoryContext.Current));
    }

    /// <summary>
    /// Reads a managed row or composite cell with the same exact type check as a raw read. A present cell is rebuilt as an
    /// array in a temporary context, which is deleted after its elements are detached.
    /// </summary>
    internal object? ReadCell(object? cell, uint cellTypeOid, Type requested)
    {
        RequireRead();
        uint elementOid = Element.GetOid();
        uint arrayOid = NativeBackend.EnumArrayOid(elementOid);
        if (cellTypeOid != arrayOid)
        {
            throw new InvalidCastException($"PostgreSQL datum type OID {cellTypeOid} does not match mapped array type OID {arrayOid}.");
        }

        if (cell is null)
        {
            return null;
        }

        using PgMemoryContext temporary = PgMemoryContext.Create("Ankus mapped cell");
        return ReadPresent(NativeBackend.RebuildCell(cell, arrayOid, new PgDatumLifetime(temporary)), elementOid, requested);
    }

    /// <summary>
    /// Writes an array for a managed row or composite cell, which keeps a copy independent of the writers' native storage.
    /// </summary>
    internal object? WriteCell(object? value, uint capturedOid)
    {
        RequireWrite();
        uint elementOid = Element.GetOid();
        uint arrayOid = NativeBackend.EnumArrayOid(elementOid);
        if (capturedOid != arrayOid)
        {
            throw new InvalidOperationException("The mapped PostgreSQL type has changed since the value was created.");
        }

        if (value is null)
        {
            return null;
        }

        using PgMemoryContext temporary = PgMemoryContext.Create("Ankus mapped cell");
        return NativeBackend.DetachDatum(Build(Wrap(value), elementOid, arrayOid, temporary));
    }

    /// <summary>
    /// Builds an exactly typed native array in the destination, releasing the element writers' storage afterward.
    /// </summary>
    private PgDatum Build(IPgArray array, uint elementOid, uint arrayOid, PgMemoryContext destination)
    {
        var lifetime = new PgDatumLifetime(destination);
        PgMemoryContext temporary = PgMemoryContext.Create("Ankus mapped array writer", PgMemoryContext.Callback);
        Exception? primary = null;
        PgDatum result;
        try
        {
            var parameters = new SpiParameter[checked(array.Count + 1)];
            parameters[0] = SpiParameter.Create(NativeBackend.EncodeMappedArrayShape(array, elementOid));
            for (int index = 0; index < array.Count; index++)
            {
                object? item = array.GetElement(index);
                parameters[index + 1] = item is null ? SpiParameter.CreateType(elementOid) :
                    SpiParameter.Create(Element.WriteDatum(item, elementOid, temporary));
            }

            result = NativeBackend.BuildMappedArray(parameters, arrayOid, lifetime);
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

        return result;
    }

    /// <summary>
    /// Converts present array cells after validating the complete source identity.
    /// </summary>
    protected abstract object ReadPresent(PgDatum value, uint elementOid, Type requested);

    /// <summary>
    /// Requires the statically registered container identity before exposing its elements.
    /// </summary>
    protected abstract IPgArray Wrap(object value);
}
