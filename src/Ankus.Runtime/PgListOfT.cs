using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Provides a checked mutable collection over PostgreSQL's typed native List container.
/// </summary>
/// <typeparam name="T">Exactly int, uint (OID), PgTransactionId, or nint (opaque pointer).</typeparam>
/// <remarks>
/// A default instance is backend-independent NIL until its first allocating mutation.
/// Bound lists require the owning backend callback and expire when their context resets.
/// Disposal releases owned containers, never pointer elements. There is no native finalizer.
/// Safe reads copy cells; unsafe pointers can become invalid after mutation or context cleanup.
/// </remarks>
public sealed class PgList<T> : IList<T>, IReadOnlyList<T>, IDisposable where T : unmanaged
{
    private readonly int _kind = NativeListValues.GetKind<T>();
    private nint _provider;
    private nint _handle;
    private int _version;
    private bool _disposed;

    /// <summary>
    /// Creates an unbound NIL list without accessing PostgreSQL or allocating native storage.
    /// </summary>
    public PgList()
    {
    }

    /// <summary>
    /// Gets the current number of cells after checking the list's lifetime.
    /// </summary>
    public int Count => checked((int)Inspect()._length);

    /// <summary>
    /// Gets the current native cell capacity, or zero for NIL.
    /// </summary>
    public int Capacity => checked((int)Inspect()._value);

    /// <summary>
    /// Gets whether the checked list is NIL.
    /// </summary>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Gets false because this collection supports mutation.
    /// </summary>
    public bool IsReadOnly => false;

    /// <summary>
    /// Gets the checked native owner, or null for an unbound NIL list.
    /// </summary>
    public PgMemoryContext? LifetimeContext
    {
        get
        {
            NativeMemoryResult state = Inspect();
            return _handle == 0 ? null : PgMemoryContext.FromId(_provider, state._context);
        }
    }

    /// <summary>
    /// Gets or replaces one cell without changing its type, length or ownership.
    /// </summary>
    /// <param name="index">The zero-based cell index.</param>
    /// <returns>The exact cell value.</returns>
    public T this[int index]
    {
        get
        {
            ValidateRange(index, 1);
            Span<ulong> bits = stackalloc ulong[1];
            Transfer(NativeListOperation.Read, bits, index);
            return NativeListValues.FromBits<T>(bits[0]);
        }
        set
        {
            ValidateRange(index, 1);
            Span<ulong> bits = [NativeListValues.ToBits(value)];
            Transfer(NativeListOperation.Write, bits, index);
            _version++;
        }
    }

    /// <summary>
    /// Appends one cell, binding an unbound NIL list to the current context as needed.
    /// </summary>
    /// <param name="item">The cell value.</param>
    public void Add(T item)
    {
        EnsureBound();
        Span<ulong> bits = [NativeListValues.ToBits(item)];
        Transfer(NativeListOperation.Add, bits);
        _version++;
    }

    /// <summary>
    /// Appends an independent copy of all source values in one guarded native mutation.
    /// </summary>
    /// <param name="values">The values to append.</param>
    public void AddRange(ReadOnlySpan<T> values)
    {
        if (values.IsEmpty)
        {
            _ = Inspect();
            return;
        }

        ulong[] bits = new ulong[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            bits[index] = NativeListValues.ToBits(values[index]);
        }

        EnsureBound();
        Transfer(NativeListOperation.Add, bits);
        _version++;
    }

    /// <summary>
    /// Appends one cell only if a nonempty list already has spare capacity, without allocating native storage.
    /// </summary>
    /// <param name="item">The cell to append.</param>
    /// <returns>False for NIL or full capacity; true after appending.</returns>
    public bool TryAdd(T item)
    {
        EnsureLive();
        if (_handle == 0)
        {
            return false;
        }

        Span<ulong> bits = [NativeListValues.ToBits(item)];
        bool added = Transfer(NativeListOperation.TryAdd, bits)._value != 0;
        if (added)
        {
            _version++;
        }

        return added;
    }

    /// <summary>
    /// Ensures room for additional cells on a nonempty list, allocating in its original owner if necessary.
    /// </summary>
    /// <param name="additionalCount">The number of cells beyond the current length.</param>
    /// <returns>False for NIL, otherwise true. Allocation errors still throw.</returns>
    public bool TryReserve(int additionalCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalCount);
        EnsureLive();
        return _handle != 0 && Invoke(NativeListOperation.Reserve, length: additionalCount)._value != 0;
    }

    /// <summary>
    /// Inserts one cell at an existing index or at the end.
    /// </summary>
    /// <param name="index">The zero-based insertion index, from zero through Count.</param>
    /// <param name="item">The cell to insert.</param>
    public void Insert(int index, T item)
    {
        ValidateRange(index, 0);
        EnsureBound();
        Span<ulong> bits = [NativeListValues.ToBits(item)];
        Transfer(NativeListOperation.Insert, bits, index);
        _version++;
    }

    /// <summary>
    /// Attempts to read a cell without treating an out-of-range index as an exception.
    /// </summary>
    /// <param name="index">The zero-based index.</param>
    /// <param name="value">The exact cell on success, otherwise default.</param>
    /// <returns>Whether the checked list contains this index.</returns>
    public bool TryGet(int index, out T value)
    {
        int count = Count;
        if ((uint)index >= (uint)count)
        {
            value = default;
            return false;
        }

        value = this[index];
        return true;
    }

    /// <summary>
    /// Attempts to read the first cell, preserving a zero pointer or value as a successful result.
    /// </summary>
    /// <param name="value">The first cell, otherwise default.</param>
    /// <returns>Whether the list is nonempty.</returns>
    public bool TryGetFirst(out T value) => TryGet(0, out value);

    /// <summary>
    /// Attempts to read the final cell.
    /// </summary>
    /// <param name="value">The final cell, otherwise default.</param>
    /// <returns>Whether the list is nonempty.</returns>
    public bool TryGetLast(out T value) => TryGet(Count - 1, out value);

    /// <summary>
    /// Removes and returns the final cell, freeing the container when it becomes NIL.
    /// </summary>
    /// <param name="value">The removed cell, otherwise default.</param>
    /// <returns>Whether one cell was removed.</returns>
    public bool TryPop(out T value)
    {
        int count = Count;
        if (count == 0)
        {
            value = default;
            return false;
        }

        Span<ulong> bits = stackalloc ulong[1];
        Transfer(NativeListOperation.Drain, bits, count - 1);
        value = NativeListValues.FromBits<T>(bits[0]);
        _version++;
        return true;
    }

    /// <summary>
    /// Finds the first cell with the same exact value using ordinary managed equality.
    /// </summary>
    /// <param name="item">The value to find.</param>
    /// <returns>The first matching index, or minus one.</returns>
    public int IndexOf(T item)
    {
        int count = Count;
        for (int index = 0; index < count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(this[index], item))
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// Tests for an exactly equal cell value.
    /// </summary>
    /// <param name="item">The cell to find.</param>
    /// <returns>Whether a matching cell exists.</returns>
    public bool Contains(T item) => IndexOf(item) >= 0;

    /// <summary>
    /// Removes the first matching cell without freeing pointer elements.
    /// </summary>
    /// <param name="item">The value to remove.</param>
    /// <returns>Whether a cell was removed.</returns>
    public bool Remove(T item)
    {
        int index = IndexOf(item);
        if (index < 0)
        {
            return false;
        }

        RemoveAt(index);
        return true;
    }

    /// <summary>
    /// Removes one cell and shifts the remaining tail, restoring NIL when emptied.
    /// </summary>
    /// <param name="index">The zero-based index to remove.</param>
    public void RemoveAt(int index)
    {
        ValidateRange(index, 1);
        Invoke(NativeListOperation.Remove, length: 1, index: index);
        _version++;
    }

    /// <summary>
    /// Frees the container and cell storage and restores NIL, retaining the context binding.
    /// </summary>
    public void Clear()
    {
        EnsureLive();
        if (_handle != 0)
        {
            Invoke(NativeListOperation.Clear);
        }

        _version++;
    }

    /// <summary>
    /// Copies and removes a range immediately, repairing the native tail before returning.
    /// </summary>
    /// <param name="index">The first cell to remove.</param>
    /// <param name="count">The number of cells to remove, including zero.</param>
    /// <returns>An independent array of removed values in their original order.</returns>
    /// <remarks>
    /// The mutation completes before enumeration of the result; abandoning that enumeration
    /// cannot leave a partially drained container. Emptying the list releases its storage.
    /// </remarks>
    public T[] Drain(int index, int count)
    {
        ValidateRange(index, count);
        T[] result = new T[count];
        ulong[] bits = new ulong[count];
        if (count != 0)
        {
            Transfer(NativeListOperation.Drain, bits, index);
            _version++;
        }

        for (int offset = 0; offset < count; offset++)
        {
            result[offset] = NativeListValues.FromBits<T>(bits[offset]);
        }

        return result;
    }

    /// <summary>
    /// Copies an exact native cell range into caller-owned managed storage.
    /// </summary>
    /// <param name="destination">The destination span; its length selects the number of cells.</param>
    /// <param name="sourceIndex">The first source cell.</param>
    public void CopyTo(Span<T> destination, int sourceIndex = 0)
    {
        ValidateRange(sourceIndex, destination.Length);
        if (destination.IsEmpty)
        {
            return;
        }

        ulong[] bits = new ulong[destination.Length];
        Transfer(NativeListOperation.Read, bits, sourceIndex);
        for (int index = 0; index < destination.Length; index++)
        {
            destination[index] = NativeListValues.FromBits<T>(bits[index]);
        }
    }

    /// <summary>
    /// Copies the entire list into a managed array starting at the requested destination index.
    /// </summary>
    /// <param name="array">The destination array.</param>
    /// <param name="arrayIndex">The first destination index.</param>
    public void CopyTo(T[] array, int arrayIndex)
    {
        ArgumentNullException.ThrowIfNull(array);
        ArgumentOutOfRangeException.ThrowIfNegative(arrayIndex);
        int count = Count;
        if (arrayIndex > array.Length || count > array.Length - arrayIndex)
        {
            throw new ArgumentException("The destination array cannot contain the list.", nameof(array));
        }

        CopyTo(array.AsSpan(arrayIndex, count));
    }

    /// <summary>
    /// Copies every cell into an independent managed array.
    /// </summary>
    /// <returns>The exact ordered cell values.</returns>
    public T[] ToArray()
    {
        T[] result = new T[Count];
        CopyTo(result.AsSpan());
        return result;
    }

    /// <summary>
    /// Creates an independent native container with copied cell values in the selected context.
    /// </summary>
    /// <param name="context">The new owner, or null for the current context.</param>
    /// <returns>An owned copy; pointer pointees are shared and never cloned or freed.</returns>
    public PgList<T> Clone(PgMemoryContext? context = null) => PgList.Create<T>(ToArray(), context);

    /// <summary>
    /// Enumerates copied cells while checking wrapper mutation, disposal and context lifetime.
    /// </summary>
    /// <returns>A borrowed enumerator; disposing it leaves this list intact.</returns>
    public IEnumerator<T> GetEnumerator()
    {
        _ = Inspect();
        return new Enumerator(this, ownsList: false);
    }

    /// <summary>
    /// Enumerates copied cells through the nongeneric collection interface.
    /// </summary>
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Consumes this wrapper and gives an enumerator its ownership and lifetime obligations.
    /// </summary>
    /// <returns>An enumerator that disposes its list at the end or on early disposal.</returns>
    /// <remarks>
    /// The caller must dispose the returned enumerator, including when it is never advanced.
    /// Owned containers are freed; borrowed containers remain with their native owner.
    /// This wrapper becomes unusable immediately after successful transfer.
    /// </remarks>
    public IEnumerator<T> GetConsumingEnumerator()
    {
        _ = Inspect();
        var transferred = new PgList<T> { _provider = _provider, _handle = _handle };
        var enumerator = new Enumerator(transferred, ownsList: true);
        _disposed = true;
        return enumerator;
    }

    /// <summary>
    /// Returns the current native List pointer, including null for NIL.
    /// </summary>
    /// <returns>The checked header pointer.</returns>
    /// <remarks>
    /// The caller preserves allocator provenance and exclusive access, and must not retain
    /// the pointer beyond a mutation that empties the list or its native lifetime.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public unsafe void* DangerousGetPointer() => (void*)Inspect()._pointer;

    /// <summary>
    /// Returns the current ListCell buffer using the selected server version's native layout.
    /// </summary>
    /// <returns>The cell buffer pointer, including null for NIL.</returns>
    /// <remarks>
    /// No managed union layout is promised. Growth may relocate cells; the caller enforces
    /// bounds, cell type, exclusive access and a lifetime no longer than the container's.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public unsafe void* DangerousGetCellsPointer() => (void*)Inspect()._data;

    /// <summary>
    /// Consumes this wrapper and returns the native container without releasing its storage.
    /// </summary>
    /// <returns>The current List pointer, including null for NIL.</returns>
    /// <remarks>
    /// Native context ownership is unchanged. A borrowed transfer does not acquire release rights.
    /// </remarks>
    public unsafe void* DangerousDetach()
    {
        EnsureLive();
        nint pointer = _handle == 0 ? 0 : Invoke(NativeListOperation.Detach)._pointer;
        _disposed = true;
        return (void*)pointer;
    }

    /// <summary>
    /// Releases an owned container or closes a borrowed wrapper; repeated disposal is harmless.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_handle != 0)
        {
            Invoke(NativeListOperation.Dispose);
        }

        _disposed = true;
    }

    /// <summary>
    /// Initializes an existing managed wrapper so native acquisition cannot precede its allocation.
    /// </summary>
    internal unsafe bool Initialize(ReadOnlySpan<T> values, PgMemoryContext owner, nint address, bool borrow)
    {
        ulong[] bits = new ulong[values.Length];
        for (int index = 0; index < values.Length; index++)
        {
            bits[index] = NativeListValues.ToBits(values[index]);
        }

        nint context = owner.GetId();
        nint provider = NativeMemoryContext.Provider;
        fixed (ulong* data = bits)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.List,
                _flags = (int)(borrow ? NativeListOperation.Borrow : NativeListOperation.Create),
                _context = context,
                _pointer = address,
                _alignment = (nuint)_kind,
                _data = (nint)data,
                _length = (nuint)bits.Length,
            };
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            if (result._pointer == 0)
            {
                if (borrow)
                {
                    return false;
                }

                throw new InvalidOperationException("PostgreSQL did not return a list identity.");
            }

            _provider = provider;
            _handle = result._pointer;
            return true;
        }
    }

    /// <summary>
    /// Parses the private catalog node representation into independently owned native expression trees.
    /// </summary>
    internal unsafe void InitializeDefaults(string source, int count, PgMemoryContext owner)
    {
        byte[] utf8 = System.Text.Encoding.UTF8.GetBytes(source);
        nint context = owner.GetId();
        nint provider = NativeMemoryContext.Provider;
        fixed (byte* data = utf8)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.List,
                _flags = (int)NativeListOperation.ParseDefaults,
                _context = context,
                _data = (nint)data,
                _length = (nuint)utf8.Length,
                _value = count,
            };
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            if (result._pointer == 0)
            {
                throw new InvalidOperationException("PostgreSQL did not return a defaults list identity.");
            }

            _provider = provider;
            _handle = result._pointer;
        }
    }

    /// <summary>
    /// Binds a previously backend-independent NIL to the active context before allocation.
    /// </summary>
    private void EnsureBound()
    {
        EnsureLive();
        if (_handle == 0)
        {
            Initialize([], PgMemoryContext.Current, 0, borrow: false);
        }
    }

    /// <summary>
    /// Validates managed disposal and, when bound, the active backend provider.
    /// </summary>
    private void EnsureLive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_handle != 0)
        {
            NativeMemoryContext.CheckProvider(_provider);
        }
    }

    /// <summary>
    /// Reads checked native metadata or returns the backend-independent NIL snapshot.
    /// </summary>
    private NativeMemoryResult Inspect()
    {
        EnsureLive();
        return _handle == 0 ? default : Invoke(NativeListOperation.Inspect);
    }

    /// <summary>
    /// Checks a managed range without overflowing the count arithmetic.
    /// </summary>
    private void ValidateRange(int index, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        int length = Count;
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index, length);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, length - index);
    }

    /// <summary>
    /// Pins a wire buffer only for one synchronous guarded native call.
    /// </summary>
    private unsafe NativeMemoryResult Transfer(NativeListOperation operation, Span<ulong> bits, int index = 0)
    {
        fixed (ulong* data = bits)
        {
            return Invoke(operation, (nint)data, bits.Length, index);
        }
    }

    /// <summary>
    /// Invokes native operations and translates expired registry identities into disposal failures.
    /// </summary>
    private NativeMemoryResult Invoke(NativeListOperation operation, nint data = 0, int length = 0, int index = 0)
    {
        EnsureLive();
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.List,
            _flags = (int)operation,
            _context = _handle,
            _data = data,
            _length = (nuint)length,
            _value = index,
        };
        try
        {
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            return result;
        }
        catch (PgException exception) when (exception.SqlState == PgSqlStates.ObjectNotInPrerequisiteState)
        {
            throw new ObjectDisposedException(nameof(PgList<>), "The PostgreSQL list or its context has been reclaimed.");
        }
    }

    /// <summary>
    /// Copies each current value while retaining either a borrowed or transferred wrapper.
    /// </summary>
    private sealed class Enumerator(PgList<T> list, bool ownsList) : IEnumerator<T>
    {
        private readonly int _version = list._version;
        private int _index = -1;
        private bool _finished;
        private bool _disposed;
        private T _current;

        /// <summary>
        /// Gets the last copied value while positioned on a live, unchanged list.
        /// </summary>
        public T Current
        {
            get
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                if (_index < 0 || _finished)
                {
                    throw new InvalidOperationException("The enumerator is not positioned on a cell.");
                }

                Check();
                return _current;
            }
        }

        /// <summary>
        /// Gets the current cell through the nongeneric interface.
        /// </summary>
        object IEnumerator.Current => Current;

        /// <summary>
        /// Advances through checked native copies and releases a consumed list on completion.
        /// </summary>
        public bool MoveNext()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_finished)
            {
                if (!ownsList)
                {
                    Check();
                }

                return false;
            }

            Check();
            int next = _index + 1;
            if (next < list.Count)
            {
                _current = list[next];
                _index = next;
                return true;
            }

            if (ownsList)
            {
                list.Dispose();
            }

            _finished = true;
            return false;
        }

        /// <summary>
        /// Rejects resetting a native enumerator.
        /// </summary>
        public void Reset() => throw new NotSupportedException("PostgreSQL list enumerators cannot be reset.");

        /// <summary>
        /// Releases a consumed list, including when enumeration ends early.
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            if (ownsList)
            {
                list.Dispose();
            }

            _disposed = true;
        }

        /// <summary>
        /// Rejects disposal, invalidated native ownership and mutation through the wrapper.
        /// </summary>
        private void Check()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_version != list._version)
            {
                throw new InvalidOperationException("The PostgreSQL list changed during enumeration.");
            }

            _ = list.Inspect();
        }
    }
}
