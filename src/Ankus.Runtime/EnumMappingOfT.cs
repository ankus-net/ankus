using System;
using System.Collections.Generic;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Supplies a generated enum's label tables and Native AOT array instantiations.
/// </summary>
internal sealed class EnumMapping<T> : EnumMapping where T : struct, Enum
{
    private readonly Dictionary<T, string> _labels = [];
    private readonly Dictionary<string, T> _values = new(StringComparer.Ordinal);

    /// <summary>
    /// Copies a generated mapping, rejecting ambiguous values and labels.
    /// </summary>
    internal EnumMapping(string name, string? schema, KeyValuePair<T, string>[] labels) : base(name, schema)
    {
        foreach (KeyValuePair<T, string> pair in labels)
        {
            _labels.Add(pair.Key, pair.Value);
            _values.Add(pair.Value, pair.Key);
        }
    }

    /// <inheritdoc />
    internal override string ToLabel(object value) => value is T typed && _labels.TryGetValue(typed, out string? label)
        ? label : throw new ArgumentException($"Value '{value}' is not a declared member of PgEnum '{typeof(T)}'.", nameof(value));

    /// <inheritdoc />
    internal override object FromLabel(string label)
    {
        ArgumentNullException.ThrowIfNull(label);
        return _values.TryGetValue(label, out T value) ? value :
            throw new ArgumentException($"Label '{label}' is not a declared member of PgEnum '{typeof(T)}'.", nameof(label));
    }

    /// <inheritdoc />
    internal override IPgArray Wrap(Array value) => value switch
    {
        T[] items => new PgArray<T>(items),
        T?[] items => new PgArray<T?>(items),
        _ => throw new InvalidCastException($"Array cannot be converted to '{typeof(T)}' elements."),
    };

    /// <inheritdoc />
    internal override object Convert(IPgArray value, Type type)
    {
        // The CLR element type retains identity even after the backend scope has ended.
        if (value is not PgArray<T> and not PgArray<T?>)
        {
            throw new InvalidCastException($"Array cannot be converted to '{typeof(T)}' elements.");
        }

        if (type == typeof(T[]) || type == typeof(PgArray<T>))
        {
            var items = new T[value.Count];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = SpiRow.Convert<T>(value.GetElement(i));
            }

            var array = new PgArray<T>(items, ([.. value.Lengths], [.. value.LowerBounds]));
            return type == typeof(T[]) ? array.ToVector() : array;
        }
        else
        {
            var items = new T?[value.Count];
            for (int i = 0; i < items.Length; i++)
            {
                items[i] = SpiRow.Convert<T?>(value.GetElement(i));
            }

            var array = new PgArray<T?>(items, ([.. value.Lengths], [.. value.LowerBounds]));
            return type == typeof(T?[]) ? array.ToVector() : array;
        }
    }

    /// <inheritdoc />
    internal override IPgArray ReadArray(NativeValue value, uint oid) => value.ReadArrayData<T?>(oid, this);
}
