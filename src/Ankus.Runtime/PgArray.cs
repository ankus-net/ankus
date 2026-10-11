using System.Buffers;
using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus;

/// <summary>
/// Owns a row-major PostgreSQL array, preserving up to six dimensions and their lower bounds.
/// Use nullable value types for SQL NULL elements and nullable annotations for reference elements.
/// A null array reference represents SQL NULL.
/// </summary>
/// <typeparam name="T">A supported scalar element type, such as int?, string, PgNumeric, or a registered datum mapping.</typeparam>
public sealed class PgArray<T> : IReadOnlyList<T>, IPgArray
{
    private readonly T[] _values;
    private readonly int[] _lengths;
    private readonly int[] _lowerBounds;
    private readonly uint _elementOid;
    private readonly uint _elementBaseOid;

    /// <summary>
    /// Takes ownership of validated materialized elements and shape without copying them again.
    /// </summary>
    /// <param name="ownedValues">The converted elements, no longer mutated by the caller.</param>
    /// <param name="shape">The validated lengths and lower bounds, also transferred to this instance.</param>
    /// <param name="elementOid">An explicit composite element identity, or zero for the static scalar mapping.</param>
    /// <param name="elementBaseOid">The base composite type when the element type is a domain.</param>
    internal PgArray(T[] ownedValues, (int[] Lengths, int[] LowerBounds) shape, uint elementOid = 0, uint elementBaseOid = 0)
    {
        _values = ownedValues;
        _lengths = shape.Lengths;
        _lowerBounds = shape.LowerBounds;
        _elementOid = elementOid;
        _elementBaseOid = elementBaseOid;
    }

    /// <summary>
    /// Copies a vector. Nonempty vectors have one dimension with lower bound one.
    /// </summary>
    /// <param name="values">The elements in order.</param>
    public PgArray(IEnumerable<T> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        if (values is Array source && typeof(T).IsValueType && PgDatumRegistry.Find(typeof(T)) is not null && source.GetType() != typeof(T[]))
        {
            throw new InvalidCastException("A mapped value array must have its exact declared managed array type.");
        }

        if (PgEnumRegistry.Find(typeof(T)) is null && PgTypeRegistry.Find(typeof(T)) is null && PgDatumRegistry.Find(typeof(T)) is null)
        {
            _ = SpiArray.ArrayOid(SpiType.GetOid<T>());
        }

        _values = [.. values];
        _lengths = _values.Length == 0 ? [] : [_values.Length];
        _lowerBounds = _values.Length == 0 ? [] : [1];
    }

    /// <summary>
    /// Copies elements and dimensions. Empty arrays normalize to PostgreSQL's zero-dimensional empty array.
    /// </summary>
    /// <param name="values">The row-major elements; the last subscript varies fastest.</param>
    /// <param name="lengths">Zero through six dimension lengths whose product equals the element count.</param>
    /// <param name="lowerBounds">One lower bound per dimension, or an empty span to use one for every dimension.</param>
    public PgArray(ReadOnlySpan<T> values, ReadOnlySpan<int> lengths, ReadOnlySpan<int> lowerBounds = default)
    {
        if (PgEnumRegistry.Find(typeof(T)) is null && PgTypeRegistry.Find(typeof(T)) is null && PgDatumRegistry.Find(typeof(T)) is null)
        {
            _ = SpiArray.ArrayOid(SpiType.GetOid<T>());
        }

        SpiArray.ValidateShape(values.Length, lengths, lowerBounds);
        _values = [.. values];
        _lengths = values.IsEmpty ? [] : [.. lengths];
        _lowerBounds = values.IsEmpty ? [] : lowerBounds.IsEmpty ? [.. Enumerable.Repeat(1, lengths.Length)] : [.. lowerBounds];
    }

    /// <summary>
    /// Gets the total element count, including SQL NULL elements.
    /// </summary>
    public int Count => _values.Length;

    /// <summary>
    /// Gets the number of dimensions. PostgreSQL empty arrays have rank zero.
    /// </summary>
    public int Rank => _lengths.Length;

    /// <summary>
    /// Gets the PostgreSQL element identity, including the named composite identity of an empty or all-null array.
    /// Enum, custom type and registered datum identities are resolved in the current backend when requested.
    /// </summary>
    public uint ElementTypeOid => _elementOid != 0 ? _elementOid : SpiType.GetOid<T>();

    /// <summary>
    /// Gets the heap tuple identity underlying a composite domain element, or the declared element type.
    /// </summary>
    internal uint ElementBaseTypeOid => _elementBaseOid != 0 ? _elementBaseOid : ElementTypeOid;

    /// <summary>
    /// Gets the dimension lengths without exposing mutable storage.
    /// </summary>
    public ReadOnlySpan<int> Lengths => _lengths;

    /// <summary>
    /// Gets the PostgreSQL lower bounds without exposing mutable storage.
    /// </summary>
    public ReadOnlySpan<int> LowerBounds => _lowerBounds;

    /// <summary>
    /// Gets an element by its zero-based flat index, independently of PostgreSQL lower bounds.
    /// </summary>
    /// <param name="index">The row-major index.</param>
    /// <returns>The element.</returns>
    public T this[int index] => _values[index];

    /// <summary>
    /// Gets an element by PostgreSQL subscripts, including negative and non-one lower bounds.
    /// </summary>
    /// <param name="subscripts">Exactly one subscript per dimension.</param>
    /// <returns>The element.</returns>
    public T GetValue(params ReadOnlySpan<int> subscripts)
    {
        if (subscripts.Length != Rank || Rank == 0)
        {
            throw new ArgumentException("Supply one subscript per dimension of a nonempty array.", nameof(subscripts));
        }

        int index = 0;
        for (int dimension = 0; dimension < Rank; dimension++)
        {
            long offset = (long)subscripts[dimension] - _lowerBounds[dimension];
            if (offset < 0 || offset >= _lengths[dimension])
            {
                throw new ArgumentOutOfRangeException(nameof(subscripts), "The subscript is outside its dimension's bounds.");
            }

            index = checked(index * _lengths[dimension] + (int)offset);
        }

        return _values[index];
    }

    /// <summary>
    /// Copies all elements in row-major order, explicitly discarding shape and lower bounds.
    /// </summary>
    /// <returns>A flat managed array.</returns>
    public T[] ToArray() => [.. _values];

    /// <summary>
    /// Copies a zero- or one-dimensional array with lower bound one, rejecting shape loss.
    /// </summary>
    /// <returns>The managed vector.</returns>
    public T[] ToVector()
    {
        if (Rank > 1 || (Rank == 1 && _lowerBounds[0] != 1))
        {
            throw new InvalidOperationException(SpiArray.VectorShapeMessage);
        }

        return ToArray();
    }

    /// <summary>
    /// Enumerates elements in row-major order.
    /// </summary>
    /// <returns>The enumerator.</returns>
    public IEnumerator<T> GetEnumerator() => ((IEnumerable<T>)_values).GetEnumerator();

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    uint IPgArray.ElementOid => ElementTypeOid;
    object? IPgArray.GetElement(int index) => _values[index];

    bool IPgArray.TryWritePacked(IBufferWriter<byte> buffer) => _elementOid == 0 && SpiType.GetOid<T>() switch
    {
        16 => WritePacked<bool>(buffer),
        18 => WritePacked<sbyte>(buffer),
        21 => WritePacked<short>(buffer),
        23 => WritePacked<int>(buffer),
        26 => WritePacked<uint>(buffer),
        700 => WritePacked<float>(buffer),
        20 => WritePacked<long>(buffer),
        701 => WritePacked<double>(buffer),
        _ => false,
    };

    private bool WritePacked<TElement>(IBufferWriter<byte> buffer) where TElement : unmanaged
    {
        if (typeof(T) == typeof(TElement))
        {
            buffer.Write(MemoryMarshal.AsBytes(Unsafe.As<TElement[]>(_values).AsSpan()));
            return true;
        }

        if (typeof(T) != typeof(TElement?))
        {
            return false;
        }

        TElement?[] values = Unsafe.As<TElement?[]>(_values);
        if (Array.IndexOf(values, null) >= 0)
        {
            return false;
        }

        int width = Unsafe.SizeOf<TElement>();
        Span<byte> target = buffer.GetSpan(values.Length * width);
        for (int index = 0; index < values.Length; index++)
        {
            MemoryMarshal.Write(target[(index * width)..], values[index].GetValueOrDefault());
        }

        buffer.Advance(values.Length * width);
        return true;
    }
}
