using System;
using System.Runtime.CompilerServices;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Maps checked native views to a canonical packed value without duplicating its text codec.
/// </summary>
internal sealed class PgVarlenaTypeMapping<T>(string name, string? schema, int size, int[] padding, CustomTypeMapping valueMapping)
    : CustomTypeMapping(name, schema) where T : unmanaged
{
    /// <summary>
    /// Gets the validated managed payload width on first use inside the managed error boundary.
    /// </summary>
    internal int Size => size == Unsafe.SizeOf<T>() ? size :
        throw new InvalidOperationException("The managed native layout does not match the generated size.");

    /// <summary>
    /// Gets the offset and length pairs of the layout's padding, which every write sets to zero.
    /// </summary>
    internal int[] Padding => padding;

    /// <inheritdoc />
    internal override bool IsReferenceType => true;

    /// <inheritdoc />
    internal override bool IsAlternate => true;

    /// <inheritdoc />
    internal override bool Accepts(object value) => value is PgVarlena<T>;

    /// <inheritdoc />
    internal override bool AcceptsArray(Array value) => value is PgVarlena<T>[];

    /// <inheritdoc />
    internal override object ConvertScalar(object value) => value is T native ? new PgVarlena<T>(native) : value;

    /// <inheritdoc />
    internal override object Parse(string text) => new PgVarlena<T>((T)valueMapping.Parse(text));

    /// <inheritdoc />
    internal override string Format(object value) => valueMapping.Format(((PgVarlena<T>)value).Value);

    /// <inheritdoc />
    internal override object Read(NativeValue value) => new PgVarlena<T>((T)valueMapping.Read(value));

    /// <inheritdoc />
    internal override NativeValue Write(object value) => ((PgVarlena<T>)value).ToNative(GetOid());

    /// <inheritdoc />
    internal override IPgArray Wrap(Array value) => value is PgVarlena<T>[] items ? new PgArray<PgVarlena<T>>(items) :
        throw new InvalidCastException($"Array cannot be converted to '{typeof(PgVarlena<T>)}' elements.");

    /// <inheritdoc />
    internal override object Convert(IPgArray value, Type type)
    {
        if (value is not PgArray<T> and not PgArray<T?> and not PgArray<PgVarlena<T>>)
        {
            throw new InvalidCastException($"Array cannot be converted to '{typeof(PgVarlena<T>)}' elements.");
        }

        if (type == typeof(PgVarlena<T>[]) &&
            (value.Lengths.Length > 1 || value.Lengths.Length == 1 && value.LowerBounds[0] != 1))
        {
            throw new InvalidOperationException("Use PgArray<T> to preserve dimensions and lower bounds, or ToArray() to explicitly flatten them.");
        }

        var items = new PgVarlena<T>?[value.Count];
        try
        {
            for (int index = 0; index < items.Length; index++)
            {
                items[index] = SpiRow.Convert<PgVarlena<T>?>(value.GetElement(index));
            }

            var array = new PgArray<PgVarlena<T>?>(items, ([.. value.Lengths], [.. value.LowerBounds]));
            return type == typeof(PgVarlena<T>[]) ? array.ToVector() : array;
        }
        catch (Exception primary)
        {
            if (value is not PgArray<PgVarlena<T>>)
            {
                VarlenaCleanup.Release<PgVarlena<T>?>(items, primary);
            }

            throw;
        }
    }

    /// <inheritdoc />
    internal override IPgArray ReadArray(NativeValue value, uint oid) => value.ReadArrayData<PgVarlena<T>?>(oid);
}

/// <summary>
/// Supplies a statically closed conversion from an owned native view to its canonical managed payload.
/// </summary>
internal interface IPgVarlena : IDisposable
{
    /// <summary>
    /// Gets the exact payload type without reflecting over members.
    /// </summary>
    Type ManagedType { get; }

    /// <summary>
    /// Reads an independent managed value after validating its native owner.
    /// </summary>
    object CopyValue();
}
