using System;
using System.Runtime.CompilerServices;

namespace Ankus;

/// <summary>
/// Borrows a fixed buffer and its element count as an ordered, bounded list.
/// </summary>
/// <typeparam name="T">An unmanaged element stored directly in the buffer.</typeparam>
/// <remarks>
/// The caller owns the buffer and count, and must synchronize access to both. Zero-initialized
/// storage represents an empty list. Copying this view aliases the same storage; copying an
/// owning inline-array struct instead creates independent storage. No operation grows the buffer.
/// </remarks>
public readonly ref struct PgFixedList<T> where T : unmanaged
{
    private readonly Span<T> _buffer;
    private readonly ref int _count;

    /// <summary>
    /// Borrows existing list storage without changing its contents.
    /// </summary>
    /// <param name="buffer">The complete element buffer, including unused capacity.</param>
    /// <param name="count">The number of initialized elements at the start of the buffer.</param>
    public PgFixedList(Span<T> buffer, ref int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length);
        _buffer = buffer;
        _count = ref count;
    }

    /// <summary>
    /// Gets the fixed number of elements the buffer can hold.
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Gets the current element count, rejecting an uninitialized view or invalid metadata.
    /// </summary>
    public int Count
    {
        get
        {
            if (Unsafe.IsNullRef(ref _count) || (uint)_count > (uint)_buffer.Length)
            {
                throw new InvalidOperationException("The fixed list has invalid count storage.");
            }

            return _count;
        }
    }

    /// <summary>
    /// Gets whether the list is empty.
    /// </summary>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Gets whether the list has no unused capacity.
    /// </summary>
    public bool IsFull => Count == Capacity;

    /// <summary>
    /// Gets or replaces an existing element without changing the count.
    /// </summary>
    /// <param name="index">The zero-based element index.</param>
    public T this[int index]
    {
        get => AsSpan()[index];
        set => AsSpan()[index] = value;
    }

    /// <summary>
    /// Appends an element, returning false without changes when the buffer is full.
    /// </summary>
    /// <param name="value">The element to append.</param>
    /// <returns>Whether the element was appended.</returns>
    public bool TryAdd(T value)
    {
        int count = Count;
        if (count == Capacity)
        {
            return false;
        }

        _buffer[count] = value;
        _count = count + 1;
        return true;
    }

    /// <summary>
    /// Appends an element, throwing when the buffer is full.
    /// </summary>
    /// <param name="value">The element to append.</param>
    public void Add(T value)
    {
        if (!TryAdd(value))
        {
            throw new InvalidOperationException("The fixed list is full.");
        }
    }

    /// <summary>
    /// Appends all supplied elements, including overlapping source storage, if they fit.
    /// </summary>
    /// <param name="values">The elements to append.</param>
    /// <returns>False without changes when there is insufficient remaining capacity.</returns>
    public bool TryAddRange(ReadOnlySpan<T> values)
    {
        int count = Count;
        if (values.Length > Capacity - count)
        {
            return false;
        }

        values.CopyTo(_buffer[count..]);
        _count = count + values.Length;
        return true;
    }

    /// <summary>
    /// Appends all supplied elements, throwing without changes if they do not fit.
    /// </summary>
    /// <param name="values">The elements to append.</param>
    public void AddRange(ReadOnlySpan<T> values)
    {
        if (!TryAddRange(values))
        {
            throw new InvalidOperationException("The fixed list has insufficient remaining capacity.");
        }
    }

    /// <summary>
    /// Inserts an element at the specified position, shifting subsequent elements right.
    /// </summary>
    /// <param name="index">An insertion index from zero through Count.</param>
    /// <param name="value">The element to insert.</param>
    public void Insert(int index, T value)
    {
        int count = Count;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, count);
        if (count == Capacity)
        {
            throw new InvalidOperationException("The fixed list is full.");
        }

        _buffer[index..count].CopyTo(_buffer[(index + 1)..]);
        _buffer[index] = value;
        _count = count + 1;
    }

    /// <summary>
    /// Removes the last element if present and clears its former slot.
    /// </summary>
    /// <param name="value">The removed element, or default when empty.</param>
    /// <returns>Whether an element was removed.</returns>
    public bool TryPop(out T value)
    {
        int count = Count;
        if (count == 0)
        {
            value = default;
            return false;
        }

        value = _buffer[count - 1];
        _buffer[count - 1] = default;
        _count = count - 1;
        return true;
    }

    /// <summary>
    /// Removes and returns the last element, throwing when empty.
    /// </summary>
    /// <returns>The removed element.</returns>
    public T Pop() => TryPop(out T value) ? value : throw new InvalidOperationException("The fixed list is empty.");

    /// <summary>
    /// Removes an element and shifts subsequent elements left, preserving their order.
    /// </summary>
    /// <param name="index">The zero-based index to remove.</param>
    /// <returns>The removed element.</returns>
    public T RemoveAt(int index)
    {
        Span<T> values = AsSpan();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, values.Length);
        T removed = values[index];
        values[(index + 1)..].CopyTo(values[index..]);
        values[^1] = default;
        _count = values.Length - 1;
        return removed;
    }

    /// <summary>
    /// Removes an element by moving the last element into its position.
    /// </summary>
    /// <param name="index">The zero-based index to remove.</param>
    /// <returns>The removed element. The remaining order can change.</returns>
    public T SwapRemoveAt(int index)
    {
        Span<T> values = AsSpan();
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, values.Length);
        T removed = values[index];
        values[index] = values[^1];
        values[^1] = default;
        _count = values.Length - 1;
        return removed;
    }

    /// <summary>
    /// Borrows the initialized elements for indexed access or span enumeration.
    /// </summary>
    /// <returns>A view of the current elements; structural changes invalidate its logical extent.</returns>
    public Span<T> AsSpan() => _buffer[..Count];

    /// <summary>
    /// Enumerates initialized elements without allocating a snapshot.
    /// </summary>
    /// <returns>An enumerator invalidated by structural changes to the list.</returns>
    public Span<T>.Enumerator GetEnumerator() => AsSpan().GetEnumerator();

    /// <summary>
    /// Copies the initialized elements into an independently owned array.
    /// </summary>
    /// <returns>The elements in list order.</returns>
    public T[] ToArray() => AsSpan().ToArray();

    /// <summary>
    /// Copies all elements into an owned array, then empties the list.
    /// </summary>
    /// <returns>The removed elements in their original order.</returns>
    public T[] Drain()
    {
        T[] values = ToArray();
        Clear();
        return values;
    }

    /// <summary>
    /// Clears all initialized slots and resets the count to zero.
    /// </summary>
    public void Clear()
    {
        AsSpan().Clear();
        _count = 0;
    }
}
