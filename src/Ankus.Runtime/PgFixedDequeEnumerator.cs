using System;

namespace Ankus;

/// <summary>
/// Enumerates the two borrowed segments of a fixed deque in logical front-to-back order.
/// </summary>
/// <typeparam name="T">The unmanaged element.</typeparam>
/// <remarks>
/// Structural changes to the queue invalidate its enumerators. This enumerator does not own storage.
/// </remarks>
public ref struct PgFixedDequeEnumerator<T> where T : unmanaged
{
    private readonly ReadOnlySpan<T> _first;
    private readonly ReadOnlySpan<T> _second;
    private int _index;

    /// <summary>
    /// Borrows validated deque segments with their combined length bounded by the queue's capacity.
    /// </summary>
    internal PgFixedDequeEnumerator(ReadOnlySpan<T> first, ReadOnlySpan<T> second)
    {
        _first = first;
        _second = second;
        _index = -1;
    }

    /// <summary>
    /// Gets the current element, rejecting access before the first or after the final element.
    /// </summary>
    public readonly T Current
    {
        get
        {
            if ((uint)_index >= (uint)(_first.Length + _second.Length))
            {
                throw new InvalidOperationException("The deque enumerator is not positioned on an element.");
            }

            return _index < _first.Length ? _first[_index] : _second[_index - _first.Length];
        }
    }

    /// <summary>
    /// Advances to the next element, remaining finished after the end is reached.
    /// </summary>
    /// <returns>Whether another element is available.</returns>
    public bool MoveNext()
    {
        int count = _first.Length + _second.Length;
        if (_index < count)
        {
            _index++;
        }

        return _index < count;
    }
}
