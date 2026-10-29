using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Describes a typed positional SQL parameter, including the PostgreSQL type of a NULL value.
/// </summary>
public readonly struct SpiParameter
{
    private SpiParameter(uint typeOid, object? value, CustomTypeMapping? customMapping = null, CustomTypeMapping? customArrayMapping = null,
        DatumTypeMapping? datumMapping = null, DatumArrayMapping? datumArrayMapping = null)
    {
        TypeOid = typeOid;
        Value = value;
        CustomMapping = customMapping;
        CustomArrayMapping = customArrayMapping;
        DatumMapping = datumMapping;
        DatumArrayMapping = datumArrayMapping;
    }

    /// <summary>
    /// Gets the PostgreSQL type OID used when binding the parameter.
    /// </summary>
    public uint TypeOid { get; }

    /// <summary>
    /// Gets the managed value or raw datum handle. Ordinary SQL NULL parameters have a null value;
    /// raw parameters carry their NULL flag in PgDatum.IsNull.
    /// </summary>
    public object? Value { get; }

    /// <summary>
    /// Gets the declared custom codec independently of a polymorphic value's runtime type.
    /// </summary>
    internal CustomTypeMapping? CustomMapping { get; }

    /// <summary>
    /// Gets the declared element codec independently of a covariant vector's runtime type.
    /// </summary>
    internal CustomTypeMapping? CustomArrayMapping { get; }

    /// <summary>
    /// Gets the declared raw converter independently of a value's runtime type and current catalog OID.
    /// </summary>
    internal DatumTypeMapping? DatumMapping { get; }

    /// <summary>
    /// Gets the declared mapped array contract independently of covariant runtime element types.
    /// </summary>
    internal DatumArrayMapping? DatumArrayMapping { get; }

    /// <summary>
    /// Creates a type-only NULL envelope for a function's default argument lookup.
    /// </summary>
    /// <param name="typeOid">The validated type identity.</param>
    /// <returns>The type-only parameter.</returns>
    internal static SpiParameter CreateType(uint typeOid) => new(typeOid, null);

    /// <summary>
    /// Binds a managed row or composite cell with its stored type, as writing its row back to PostgreSQL does.
    /// </summary>
    /// <param name="typeOid">The cell's PostgreSQL type OID.</param>
    /// <param name="value">The managed cell.</param>
    /// <returns>The typed parameter.</returns>
    internal static SpiParameter CreateCell(uint typeOid, object? value) => new(typeOid, value);

    /// <summary>
    /// Creates a positional parameter using the declared CLR type, including nullable types.
    /// Parameters appear in SQL as $1, $2, and so on.
    /// </summary>
    /// <typeparam name="T">The managed parameter type.</typeparam>
    /// <param name="value">The parameter value.</param>
    /// <returns>A typed parameter.</returns>
    public static SpiParameter Create<[PgConvertible] T>(T value)
    {
        if (PgDatumRegistry.Find(typeof(T)) is { } mapping)
        {
            mapping.RequireWrite();
            return new SpiParameter(mapping.GetOid(), value, datumMapping: mapping);
        }

        if (PgDatumRegistry.FindArray(typeof(T)) is { } arrayMapping)
        {
            arrayMapping.RequireWrite();
            return new SpiParameter(arrayMapping.GetOid(), value, datumArrayMapping: arrayMapping);
        }

        return value switch
        {
            PgDatum datum => Create(datum),
            PgAnyElement element => Create(element.Datum),
            PgAnyArray array => Create(array.Datum),
            IPgArrayView view => Create(view.Datum),
            PgByteaView view => Create(view.Datum),
            PgTextView view => Create(view.Datum),
            PgCStringView view => Create(view.Datum),
            PgInternal state => new(2281, state),
            _ => new(SpiType.GetOid(value), value, PgTypeRegistry.Find(typeof(T)), PgTypeRegistry.FindArray(typeof(T))),
        };
    }

    /// <summary>
    /// Binds a raw datum with its exact declared PostgreSQL type and SQL NULL flag.
    /// </summary>
    /// <param name="value">The raw value, whose native owner must remain live through execution.</param>
    /// <returns>The typed raw parameter.</returns>
    public static SpiParameter Create(PgDatum value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new SpiParameter(value.TypeOid, value);
    }

    /// <summary>
    /// Binds a tuple or SQL NULL using an explicit composite descriptor.
    /// A base composite can bind to its domain; PostgreSQL validates the target domain when consuming the value.
    /// </summary>
    /// <param name="value">The tuple, or null for SQL NULL.</param>
    /// <param name="descriptor">The parameter's PostgreSQL composite identity.</param>
    /// <returns>The typed parameter.</returns>
    /// <exception cref="InvalidOperationException">The descriptor has no PostgreSQL row type.</exception>
    public static SpiParameter Create(PgHeapTuple? value, PgTupleDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.RequireRowType();
        if (value is not null && value.Descriptor.BaseTypeOid != descriptor.BaseTypeOid)
        {
            throw new InvalidCastException("The tuple does not have the supplied descriptor's type identity.");
        }

        return new SpiParameter(descriptor.TypeOid, value);
    }

    /// <summary>
    /// Binds a shape-preserving composite array or SQL NULL using an explicit element descriptor.
    /// Base composite elements can bind to a domain array with the same underlying row type.
    /// </summary>
    /// <param name="value">The tuple array, or null for SQL NULL.</param>
    /// <param name="descriptor">The array element's PostgreSQL type identity.</param>
    /// <returns>The typed array parameter.</returns>
    /// <exception cref="InvalidOperationException">The descriptor has no PostgreSQL row type.</exception>
    public static SpiParameter CreateArray(PgArray<PgHeapTuple?>? value, PgTupleDescriptor descriptor)
    {
        ArgumentNullException.ThrowIfNull(descriptor);
        descriptor.RequireRowType();
        if (value is not null && value.ElementBaseTypeOid != descriptor.BaseTypeOid)
        {
            throw new InvalidCastException("The array does not have the supplied descriptor's element identity.");
        }

        uint oid = descriptor.TypeOid == 2249 ? 2287 : NativeBackend.TupleArrayOid(descriptor.TypeOid);
        return new SpiParameter(oid, value);
    }
}
