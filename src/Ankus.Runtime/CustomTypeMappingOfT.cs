using System;
using System.Buffers;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Supplies one generated type's codec and Native AOT collection instantiations.
/// </summary>
internal sealed class CustomTypeMapping<T, TOptional>(string name, string? schema, Func<PgTypeCodec<T>> createCodec) : CustomTypeMapping(name, schema)
{
    private readonly Lazy<PgTypeCodec<T>> _codec = new(() => createCodec() ??
        throw new InvalidOperationException("A custom type codec factory returned null."));

    /// <inheritdoc />
    internal override bool IsReferenceType => !typeof(T).IsValueType;

    /// <inheritdoc />
    internal override object ConvertScalar(object value) => value is IPgVarlena native && native.ManagedType == typeof(T)
        ? native.CopyValue() : value;

    /// <inheritdoc />
    internal override bool Accepts(object value) => value is T;

    /// <inheritdoc />
    internal override bool AcceptsArray(Array value) => IsReferenceType ? value is T[] :
        value.GetType() == typeof(T[]) || value.GetType() == typeof(TOptional[]);

    /// <inheritdoc />
    internal override object Parse(string text) => _codec.Value.Parse(text) ??
        throw new InvalidOperationException("A custom type codec returned null for a present text input.");

    /// <inheritdoc />
    internal override string Format(object value) => _codec.Value.Format((T)value) ??
        throw new InvalidOperationException("A custom type codec returned null text for a present value.");

    /// <inheritdoc />
    internal override object Read(NativeValue value)
    {
        ReadOnlySpan<byte> payload = value.ReadCustomPayload(GetOid());
        return _codec.Value.Read(payload) ?? throw new InvalidOperationException("A custom type codec returned null for a present stored value.");
    }

    /// <inheritdoc />
    internal override NativeValue Write(object value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        _codec.Value.Write((T)value, buffer);
        return NativeValue.FromCustomPayload(buffer.WrittenSpan, GetOid());
    }

    /// <inheritdoc />
    internal override IPgArray Wrap(Array value) => value switch
    {
        _ when !AcceptsArray(value) => throw new InvalidCastException($"Array cannot be converted to '{typeof(T)}' elements."),
        T[] items => new PgArray<T>(items),
        TOptional[] items => new PgArray<TOptional>(items),
        _ => throw new InvalidCastException($"Array cannot be converted to '{typeof(T)}' elements."),
    };

    /// <inheritdoc />
    internal override object Convert(IPgArray value, Type type)
    {
        if (value is not PgArray<T> && value is not PgArray<TOptional> &&
            (PgTypeRegistry.FindArray(value.GetType()) is not { IsAlternate: true } alternate ||
             alternate.GetOid() != GetOid()))
        {
            throw new InvalidCastException($"Array cannot be converted to '{typeof(T)}' elements.");
        }

        if (type == typeof(T[]) || type == typeof(PgArray<T>))
        {
            var items = new T[value.Count];
            for (int index = 0; index < items.Length; index++)
            {
                items[index] = SpiRow.Convert<T>(value.GetElement(index));
            }

            var array = new PgArray<T>(items, ([.. value.Lengths], [.. value.LowerBounds]));
            return type == typeof(T[]) ? array.ToVector() : array;
        }

        var optionalItems = new TOptional[value.Count];
        for (int index = 0; index < optionalItems.Length; index++)
        {
            optionalItems[index] = SpiRow.Convert<TOptional>(value.GetElement(index));
        }

        var optionalArray = new PgArray<TOptional>(optionalItems, ([.. value.Lengths], [.. value.LowerBounds]));
        return type == typeof(TOptional[]) ? optionalArray.ToVector() : optionalArray;
    }

    /// <inheritdoc />
    internal override IPgArray ReadArray(NativeValue value, uint oid) => value.ReadArrayData<TOptional>(oid);
}
