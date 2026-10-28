using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;

namespace Ankus;

/// <summary>
/// Borrows fixed unmanaged entry and index buffers as an insertion-ordered dictionary.
/// </summary>
/// <typeparam name="TKey">The unmanaged key.</typeparam>
/// <typeparam name="TValue">The unmanaged value.</typeparam>
/// <remarks>
/// The caller owns and synchronizes the buffers and count. Zero-initialized storage is empty.
/// The index buffer has one slot per entry; both remain fixed in size. Removal moves the last
/// entry into the removed position, as with heapless IndexMap. A custom comparer must produce
/// identical equality and hash results in every process using this storage, without mutable
/// external state. Never use randomized hashes, process-local addresses or a different comparer
/// when attaching to populated storage. Do not modify the raw buffers while the dictionary is populated.
/// </remarks>
public readonly ref struct PgFixedMap<TKey, TValue> where TKey : unmanaged where TValue : unmanaged
{
    private readonly Span<PgFixedMapEntry<TKey, TValue>> _entries;
    private readonly Span<int> _indices;
    private readonly ref int _count;
    private readonly IEqualityComparer<TKey>? _comparer;

    /// <summary>
    /// Borrows existing dictionary storage without clearing or rehashing it.
    /// </summary>
    /// <param name="entries">The complete dense entry buffer.</param>
    /// <param name="indices">An equally sized index buffer; zero denotes an unused slot.</param>
    /// <param name="count">The number of populated entries.</param>
    /// <param name="comparer">An explicit process-stable comparer, or the built-in scalar comparer.</param>
    public PgFixedMap(Span<PgFixedMapEntry<TKey, TValue>> entries, Span<int> indices, ref int count,
        IEqualityComparer<TKey>? comparer = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, entries.Length);
        if (indices.Length != entries.Length)
        {
            throw new ArgumentException("The dictionary entry and index buffers must have equal lengths.", nameof(indices));
        }

        _entries = entries;
        _indices = indices;
        _count = ref count;
        if (comparer is null)
        {
            PgFixedKeyComparer.Validate<TKey>();
        }

        _comparer = comparer;
    }

    /// <summary>
    /// Gets the maximum entry count.
    /// </summary>
    public int Capacity => _entries.Length;

    /// <summary>
    /// Gets the current count, rejecting a default view or invalid count metadata.
    /// </summary>
    public int Count
    {
        get
        {
            if (Unsafe.IsNullRef(ref _count) || (uint)_count > (uint)Capacity)
            {
                throw new InvalidOperationException("The fixed dictionary has invalid count storage.");
            }

            return _count;
        }
    }

    /// <summary>
    /// Gets whether the dictionary contains no entries.
    /// </summary>
    public bool IsEmpty => Count == 0;

    /// <summary>
    /// Gets whether a new key would exceed capacity. Existing values can still be replaced.
    /// </summary>
    public bool IsFull => Count == Capacity;

    /// <summary>
    /// Gets an existing value, or inserts or replaces a value through the setter.
    /// </summary>
    /// <param name="key">The lookup or insertion key.</param>
    public TValue this[TKey key]
    {
        get => TryGetValue(key, out TValue value) ? value : throw new KeyNotFoundException("The key is absent from the fixed dictionary.");
        set => Set(key, value);
    }

    /// <summary>
    /// Borrows the initialized entries in their current order, without permitting key mutation.
    /// </summary>
    public ReadOnlySpan<PgFixedMapEntry<TKey, TValue>> Entries => _entries[..Count];

    /// <summary>
    /// Enumerates entries in their current order without allocating a snapshot.
    /// </summary>
    /// <returns>An enumerator invalidated by structural changes to the map.</returns>
    public ReadOnlySpan<PgFixedMapEntry<TKey, TValue>>.Enumerator GetEnumerator() => Entries.GetEnumerator();

    /// <summary>
    /// Looks up a key using the shared index and stable equality contract.
    /// </summary>
    /// <param name="key">The lookup key.</param>
    /// <param name="value">The associated value, or default if absent.</param>
    /// <returns>Whether the key exists.</returns>
    public bool TryGetValue(TKey key, out TValue value)
    {
        _ = Count;
        int found = Find(key, KeyHash(key), out _);
        value = found >= 0 ? _entries[found].Value : default;
        return found >= 0;
    }

    /// <summary>
    /// Determines whether the key has an entry.
    /// </summary>
    /// <param name="key">The lookup key.</param>
    /// <returns>Whether the key exists.</returns>
    public bool ContainsKey(TKey key) => TryGetValue(key, out _);

    /// <summary>
    /// Adds a new key, leaving storage unchanged if the key already exists or the buffer is full.
    /// </summary>
    /// <param name="key">The new key.</param>
    /// <param name="value">The new value.</param>
    /// <returns>Whether a new entry was appended.</returns>
    public bool TryAdd(TKey key, TValue value)
    {
        int count = Count;
        uint hash = KeyHash(key);
        int found = Find(key, hash, out int slot);
        if (found >= 0 || count == Capacity)
        {
            return false;
        }

        Append(key, value, hash, slot, count);
        return true;
    }

    /// <summary>
    /// Adds a new entry, throwing for an existing key or insufficient capacity.
    /// </summary>
    /// <param name="key">The new key.</param>
    /// <param name="value">The new value.</param>
    public void Add(TKey key, TValue value)
    {
        int count = Count;
        uint hash = KeyHash(key);
        int found = Find(key, hash, out int slot);
        if (found >= 0)
        {
            throw new ArgumentException("The fixed dictionary already contains the key.", nameof(key));
        }

        if (count == Capacity)
        {
            throw new InvalidOperationException("The fixed dictionary is full.");
        }

        Append(key, value, hash, slot, count);
    }

    /// <summary>
    /// Inserts or replaces a value, preserving the original key and position on replacement.
    /// </summary>
    /// <param name="key">The key to insert or update.</param>
    /// <param name="value">The new value.</param>
    /// <param name="previous">The old value for replacement, or null for insertion or a full-buffer rejection.</param>
    /// <returns>False without changes only when a new key cannot fit.</returns>
    public bool TrySet(TKey key, TValue value, out TValue? previous)
    {
        int count = Count;
        uint hash = KeyHash(key);
        int found = Find(key, hash, out int slot);
        previous = null;
        if (found >= 0)
        {
            PgFixedMapEntry<TKey, TValue> entry = _entries[found];
            previous = entry.Value;
            _entries[found] = new(entry.Key, value, entry.Hash);
            return true;
        }

        if (count == Capacity)
        {
            return false;
        }

        Append(key, value, hash, slot, count);
        return true;
    }

    /// <summary>
    /// Inserts or replaces a value, throwing if a new key cannot fit.
    /// </summary>
    /// <param name="key">The key to insert or update.</param>
    /// <param name="value">The new value.</param>
    /// <returns>The old value on replacement, or null on insertion.</returns>
    public TValue? Set(TKey key, TValue value) => TrySet(key, value, out TValue? previous) ? previous :
        throw new InvalidOperationException("The fixed dictionary is full.");

    /// <summary>
    /// Removes a key, moving the last entry into its position and repairing the probe chain.
    /// </summary>
    /// <param name="key">The key to remove.</param>
    /// <param name="value">The removed value, or default when absent.</param>
    /// <returns>Whether an entry was removed.</returns>
    public bool Remove(TKey key, out TValue value)
    {
        int count = Count;
        int found = Find(key, KeyHash(key), out int slot);
        if (found < 0)
        {
            value = default;
            return false;
        }

        int last = count - 1;
        int lastSlot = found == last ? slot : FindEntrySlot(last);
        int probe = NextSlot(slot);
        for (int visited = 0; visited < Capacity - 1 && _indices[probe] != 0; visited++)
        {
            if ((uint)(_indices[probe] - 1) >= (uint)count)
            {
                throw new InvalidOperationException("The fixed dictionary contains an invalid entry index.");
            }

            probe = NextSlot(probe);
        }

        value = _entries[found].Value;
        _entries[found] = _entries[last];
        _entries[last] = default;
        _indices[lastSlot] = found + 1;
        _indices[slot] = 0;
        int hole = slot;
        int next = NextSlot(hole);
        for (int visited = 0; visited < Capacity - 1 && _indices[next] != 0; visited++)
        {
            int entry = _indices[next] - 1;
            int home = (int)(_entries[entry].Hash % (uint)Capacity);
            if (Distance(home, hole) < Distance(home, next))
            {
                _indices[hole] = _indices[next];
                _indices[next] = 0;
                hole = next;
            }

            next = NextSlot(next);
        }

        _count = last;
        return true;
    }

    /// <summary>
    /// Copies entries into an independently owned array in their current order.
    /// </summary>
    /// <returns>The key-value pairs.</returns>
    public KeyValuePair<TKey, TValue>[] ToArray()
    {
        var values = new KeyValuePair<TKey, TValue>[Count];
        for (int index = 0; index < values.Length; index++)
        {
            values[index] = new(_entries[index].Key, _entries[index].Value);
        }

        return values;
    }

    /// <summary>
    /// Clears the entry and index buffers and resets the count.
    /// </summary>
    public void Clear()
    {
        _entries[..Count].Clear();
        _indices.Clear();
        _count = 0;
    }

    /// <summary>
    /// Finds a key or its vacant slot using bounded linear probing, including a completely full table.
    /// </summary>
    private int Find(TKey key, uint hash, out int slot)
    {
        slot = Capacity == 0 ? -1 : (int)(hash % (uint)Capacity);
        for (int visited = 0; visited < Capacity; visited++)
        {
            int position = _indices[slot];
            if (position == 0)
            {
                return -1;
            }

            if ((uint)(position - 1) >= (uint)_count)
            {
                throw new InvalidOperationException("The fixed dictionary contains an invalid entry index.");
            }

            PgFixedMapEntry<TKey, TValue> entry = _entries[position - 1];
            if (entry.Hash == hash && (_comparer is null ? EqualityComparer<TKey>.Default.Equals(entry.Key, key) : _comparer.Equals(entry.Key, key)))
            {
                return position - 1;
            }

            slot = NextSlot(slot);
        }

        slot = -1;
        return -1;
    }

    /// <summary>
    /// Finds the index slot for a dense entry without invoking user equality or hashing.
    /// </summary>
    private int FindEntrySlot(int entry)
    {
        int slot = (int)(_entries[entry].Hash % (uint)Capacity);
        for (int visited = 0; visited < Capacity; visited++)
        {
            if (_indices[slot] == entry + 1)
            {
                return slot;
            }

            slot = NextSlot(slot);
        }

        throw new InvalidOperationException("The fixed dictionary is missing an entry index.");
    }

    /// <summary>
    /// Publishes a new dense entry only after lookup and capacity checks succeed.
    /// </summary>
    private void Append(TKey key, TValue value, uint hash, int slot, int count)
    {
        if (slot < 0)
        {
            throw new InvalidOperationException("The fixed dictionary has no free index slot.");
        }

        _entries[count] = new(key, value, hash);
        _indices[slot] = count + 1;
        _count = count + 1;
    }

    /// <summary>
    /// Advances a probe, wrapping at capacity without overflowing.
    /// </summary>
    private int NextSlot(int slot) => slot == Capacity - 1 ? 0 : slot + 1;

    /// <summary>
    /// Measures circular distance without overflowing an integer addition.
    /// </summary>
    private int Distance(int start, int end) => end >= start ? end - start : Capacity - start + end;

    /// <summary>
    /// Selects the built-in stable scalar hash without constructing a comparer for each view.
    /// </summary>
    private uint KeyHash(TKey key) => unchecked((uint)(_comparer is null ? PgFixedKeyComparer.Hash(key) : _comparer.GetHashCode(key)));
}
