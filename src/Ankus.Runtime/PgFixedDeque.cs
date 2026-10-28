using System;
using System.Runtime.CompilerServices;

namespace Ankus;

/// <summary>
/// Borrows a fixed circular buffer, element count and head index as a double-ended queue.
/// </summary>
/// <typeparam name="T">An unmanaged element stored directly in the buffer.</typeparam>
/// <remarks>
/// The caller owns and synchronizes all three storage locations. Zero-initialized storage is
/// empty. Views alias their buffers; copying an owning inline-array struct copies the queue.
/// </remarks>
public readonly ref struct PgFixedDeque<T> where T : unmanaged
{
    private readonly Span<T> _buffer;
    private readonly ref int _count;
    private readonly ref int _head;

    /// <summary>
    /// Borrows existing queue storage without changing its contents.
    /// </summary>
    /// <param name="buffer">The complete circular element buffer.</param>
    /// <param name="count">The number of initialized elements.</param>
    /// <param name="head">The first element's physical index, or zero for zero-capacity storage.</param>
    public PgFixedDeque(Span<T> buffer, ref int count, ref int head)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, buffer.Length);
        ArgumentOutOfRangeException.ThrowIfNegative(head);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(head, Math.Max(0, buffer.Length - 1));
        if (Unsafe.AreSame(ref count, ref head))
        {
            throw new ArgumentException("The queue count and head require distinct storage.", nameof(head));
        }

        _buffer = buffer;
        _count = ref count;
        _head = ref head;
    }

    /// <summary>
    /// Gets the fixed number of elements the queue can hold.
    /// </summary>
    public int Capacity => _buffer.Length;

    /// <summary>
    /// Gets the count, rejecting an uninitialized view or invalid count or head metadata.
    /// </summary>
    public int Count
    {
        get
        {
            if (Unsafe.IsNullRef(ref _count) || Unsafe.IsNullRef(ref _head) || (uint)_count > (uint)Capacity ||
                (uint)_head > (uint)Math.Max(0, Capacity - 1))
            {
                throw new InvalidOperationException("The fixed deque has invalid count or head storage.");
            }

            return _count;
        }
    }

    /// <summary>
    /// Gets whether the queue is empty.
    /// </summary>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Gets whether the queue has no unused capacity.
    /// </summary>
    public bool IsFull => Count == Capacity;

    /// <summary>
    /// Gets or replaces an element in logical front-to-back order.
    /// </summary>
    /// <param name="index">The zero-based logical index.</param>
    public T this[int index]
    {
        get => _buffer[ElementIndex(index)];
        set => _buffer[ElementIndex(index)] = value;
    }

    /// <summary>
    /// Appends an element at the back if capacity remains.
    /// </summary>
    /// <param name="value">The element to append.</param>
    /// <returns>False without changes when full.</returns>
    public bool TryPushBack(T value)
    {
        int count = Count;
        if (count == Capacity)
        {
            return false;
        }

        _buffer[PhysicalIndex(count)] = value;
        _count = count + 1;
        return true;
    }

    /// <summary>
    /// Prepends an element at the front if capacity remains.
    /// </summary>
    /// <param name="value">The element to prepend.</param>
    /// <returns>False without changes when full.</returns>
    public bool TryPushFront(T value)
    {
        int count = Count;
        if (count == Capacity)
        {
            return false;
        }

        int head = _head == 0 ? Capacity - 1 : _head - 1;
        _buffer[head] = value;
        _head = head;
        _count = count + 1;
        return true;
    }

    /// <summary>
    /// Appends an element at the back, throwing when full.
    /// </summary>
    /// <param name="value">The element to append.</param>
    public void PushBack(T value)
    {
        if (!TryPushBack(value))
        {
            throw new InvalidOperationException("The fixed deque is full.");
        }
    }

    /// <summary>
    /// Prepends an element at the front, throwing when full.
    /// </summary>
    /// <param name="value">The element to prepend.</param>
    public void PushFront(T value)
    {
        if (!TryPushFront(value))
        {
            throw new InvalidOperationException("The fixed deque is full.");
        }
    }

    /// <summary>
    /// Removes the back element if present, clearing its former slot.
    /// </summary>
    /// <param name="value">The removed element, or default when empty.</param>
    /// <returns>Whether an element was removed.</returns>
    public bool TryPopBack(out T value)
    {
        int count = Count;
        if (count == 0)
        {
            value = default;
            return false;
        }

        int index = PhysicalIndex(count - 1);
        value = _buffer[index];
        _buffer[index] = default;
        _count = count - 1;
        if (count == 1)
        {
            _head = 0;
        }

        return true;
    }

    /// <summary>
    /// Removes the front element if present, clearing its former slot.
    /// </summary>
    /// <param name="value">The removed element, or default when empty.</param>
    /// <returns>Whether an element was removed.</returns>
    public bool TryPopFront(out T value)
    {
        int count = Count;
        if (count == 0)
        {
            value = default;
            return false;
        }

        value = _buffer[_head];
        _buffer[_head] = default;
        _head = count == 1 || _head == Capacity - 1 ? 0 : _head + 1;
        _count = count - 1;
        return true;
    }

    /// <summary>
    /// Removes and returns the back element, throwing when empty.
    /// </summary>
    /// <returns>The removed element.</returns>
    public T PopBack() => TryPopBack(out T value) ? value : throw new InvalidOperationException("The fixed deque is empty.");

    /// <summary>
    /// Removes and returns the front element, throwing when empty.
    /// </summary>
    /// <returns>The removed element.</returns>
    public T PopFront() => TryPopFront(out T value) ? value : throw new InvalidOperationException("The fixed deque is empty.");

    /// <summary>
    /// Borrows the queue's one or two contiguous segments in front-to-back order.
    /// </summary>
    /// <param name="first">The segment starting at the head.</param>
    /// <param name="second">The wrapped segment starting at buffer index zero, possibly empty.</param>
    public void GetSpans(out Span<T> first, out Span<T> second)
    {
        int count = Count;
        int length = Math.Min(count, Capacity - _head);
        first = _buffer.Slice(_head, length);
        second = _buffer[..(count - length)];
    }

    /// <summary>
    /// Enumerates elements in front-to-back order without allocating or rearranging the buffer.
    /// </summary>
    /// <returns>An enumerator invalidated by structural changes to the queue.</returns>
    public PgFixedDequeEnumerator<T> GetEnumerator()
    {
        GetSpans(out Span<T> first, out Span<T> second);
        return new(first, second);
    }

    /// <summary>
    /// Copies the queue in front-to-back order to nonoverlapping storage.
    /// </summary>
    /// <param name="destination">Storage with room for at least Count elements.</param>
    /// <remarks>
    /// A short destination or any overlap with the queue buffer is rejected before copying.
    /// </remarks>
    public void CopyTo(Span<T> destination)
    {
        int count = Count;
        if (destination.Length < count || destination.Overlaps(_buffer))
        {
            throw new ArgumentException("The destination must fit the queue and must not overlap its buffer.", nameof(destination));
        }

        GetSpans(out Span<T> first, out Span<T> second);
        first.CopyTo(destination);
        second.CopyTo(destination[first.Length..]);
    }

    /// <summary>
    /// Copies the queue into an independently owned array in front-to-back order.
    /// </summary>
    /// <returns>The queue's elements.</returns>
    public T[] ToArray()
    {
        T[] values = new T[Count];
        CopyTo(values);
        return values;
    }

    /// <summary>
    /// Copies all elements into an owned array, then empties the queue.
    /// </summary>
    /// <returns>The removed elements in front-to-back order.</returns>
    public T[] Drain()
    {
        T[] values = ToArray();
        Clear();
        return values;
    }

    /// <summary>
    /// Clears initialized slots and resets the count and head to zero.
    /// </summary>
    public void Clear()
    {
        GetSpans(out Span<T> first, out Span<T> second);
        first.Clear();
        second.Clear();
        _count = 0;
        _head = 0;
    }

    /// <summary>
    /// Validates a logical element index and translates it to buffer storage.
    /// </summary>
    private int ElementIndex(int index)
    {
        int count = Count;
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, count);
        return PhysicalIndex(index);
    }

    /// <summary>
    /// Translates a valid logical offset without overflowing an integer addition.
    /// </summary>
    private int PhysicalIndex(int index)
    {
        int remaining = Capacity - _head;
        return index < remaining ? _head + index : index - remaining;
    }
}
