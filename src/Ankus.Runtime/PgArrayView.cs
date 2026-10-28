using System;
using System.Collections;
using System.Collections.Generic;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Borrows a PostgreSQL array and its raw elements under a checked native lifetime.
/// </summary>
/// <remarks>
/// Flat arrays retain their original storage. PostgreSQL may flatten compressed, external or expanded
/// storage into a private child context. Direct scalar input views expire and are cleaned up
/// at callback exit; retained set and aggregate inputs use independent snapshots.
/// Dispose explicitly created views while their backend is active. Copy Datum or a cell to
/// another memory context before its source expires when an independent lifetime is needed.
/// Copied type and shape metadata remain readable after disposal; native access does not.
/// Resetting only the source context also expires all views and cells, even when private child contexts survive.
/// Indexed access is O(1) for fixed-size elements stored without a NULL bitmap,
/// and O(n) otherwise; enumeration visits the elements in one linear pass.
/// </remarks>
public sealed partial class PgArrayView : IReadOnlyList<PgDatum>, IPgArrayView
{
    private readonly PgMemoryContext _context;
    private readonly PgDatum _datum;
    private readonly int[] _lengths;
    private readonly int[] _lowerBounds;

    /// <summary>
    /// Borrows a present array without copying its flat elements or rechecking domain constraints.
    /// </summary>
    /// <param name="value">The array datum whose owner must remain alive for the view's lifetime.</param>
    public PgArrayView(PgDatum value) : this(value, 0, false)
    {
    }

    /// <summary>
    /// Borrows storage after checking an optional declared element contract, even for empty arrays.
    /// </summary>
    internal PgArrayView(PgDatum value, uint elementType, bool exactIdentity)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Lifetime.Validate();
        if (value.IsNull)
        {
            throw new ArgumentException("A borrowed array requires a non-NULL datum.", nameof(value));
        }

        PgMemoryContext parent = PgMemoryContext.FromId(NativeMemoryContext.Provider, value.Lifetime.ContextId);
        _context = PgMemoryContext.Create("Ankus borrowed array", parent);
        try
        {
            var lifetime = new PgDatumLifetime(_context, source: value.Lifetime);
            (_datum, ElementTypeOid, _lengths, _lowerBounds, Count, HasNulls) = NativeBackend.BorrowArray(value, lifetime, elementType, exactIdentity);
            lifetime.Scope?.Register(this);
        }
        catch (Exception primary)
        {
            PgResultCleanup.Dispose(_context, primary);
            throw;
        }
    }

    /// <summary>
    /// Gets the original array type OID, including a domain over an array.
    /// </summary>
    public uint TypeOid => _datum.TypeOid;

    /// <summary>
    /// Gets the exact native element type, including domain, enum and composite identities.
    /// </summary>
    public uint ElementTypeOid { get; }

    /// <summary>
    /// Gets the total number of cells, including SQL NULL cells.
    /// </summary>
    public int Count { get; }

    /// <summary>
    /// Gets the number of dimensions; empty arrays have rank zero.
    /// </summary>
    public int Rank => _lengths.Length;

    /// <summary>
    /// Gets whether at least one cell is SQL NULL.
    /// </summary>
    public bool HasNulls { get; }

    /// <summary>
    /// Gets the copied dimension lengths.
    /// </summary>
    public ReadOnlySpan<int> Lengths => _lengths;

    /// <summary>
    /// Gets the copied PostgreSQL lower bounds.
    /// </summary>
    public ReadOnlySpan<int> LowerBounds => _lowerBounds;

    /// <summary>
    /// Gets the checked flat datum, valid until this view or its source owner expires.
    /// </summary>
    public PgDatum Datum
    {
        get
        {
            _datum.Lifetime.Validate();
            return _datum;
        }
    }

    /// <summary>
    /// Borrows a cell at a zero-based row-major index using PostgreSQL's native array lookup.
    /// </summary>
    /// <param name="index">The flat index, independent of PostgreSQL lower bounds.</param>
    /// <returns>A typed datum whose NULL flag is independent of its native bits.</returns>
    public PgDatum this[int index]
    {
        get
        {
            _datum.Lifetime.Validate();
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, Count);
            return NativeBackend.ReadArrayCell(_datum, index);
        }
    }

    /// <summary>
    /// Borrows a cell using one PostgreSQL subscript per dimension.
    /// </summary>
    /// <param name="subscripts">The native subscripts, including negative or non-one lower bounds.</param>
    /// <returns>The cell under this view's lifetime.</returns>
    public PgDatum GetValue(params ReadOnlySpan<int> subscripts)
    {
        _datum.Lifetime.Validate();
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

        return this[index];
    }

    /// <summary>
    /// Creates an independent native cursor for a linear pass over the borrowed cells.
    /// </summary>
    /// <returns>An enumerator that should be disposed after use; escaped cells retain the view's lifetime.</returns>
    public IEnumerator<PgDatum> GetEnumerator() => new Enumerator(this);

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Releases private flattening and cursor storage and invalidates this view and all borrowed cells.
    /// </summary>
    public void Dispose()
    {
        _datum.Lifetime.CheckThread();
        _context.Dispose();
        _datum.Lifetime.Scope?.Unregister(this);
    }

    /// <summary>
    /// Owns one native cursor without taking ownership of the array's elements.
    /// </summary>
    private sealed class Enumerator : IEnumerator<PgDatum>
    {
        private readonly PgDatum _array;
        private readonly PgMemoryContext _context;
        private readonly PgDatumLifetime _lifetime;
        private readonly nint _iterator;
        private PgDatum? _current;
        private bool _finished;

        /// <summary>
        /// Allocates cursor bookkeeping beneath the array owner.
        /// </summary>
        internal Enumerator(PgArrayView view)
        {
            _array = view.Datum;
            _context = PgMemoryContext.Create("Ankus borrowed array iterator", view._context);
            try
            {
                _lifetime = new PgDatumLifetime(_context, source: _array.Lifetime);
                _iterator = NativeBackend.CreateArrayIterator(_array, _lifetime);
            }
            catch (Exception primary)
            {
                PgResultCleanup.Dispose(_context, primary);
                throw;
            }
        }

        /// <inheritdoc />
        public PgDatum Current
        {
            get
            {
                _lifetime.Validate();
                return _current ?? throw new InvalidOperationException("The enumerator is not positioned on a cell.");
            }
        }

        object IEnumerator.Current => Current;

        /// <inheritdoc />
        public bool MoveNext()
        {
            _lifetime.Validate();
            if (_finished)
            {
                return false;
            }

            _current = NativeBackend.AdvanceArrayIterator(_array, _lifetime, _iterator);
            _finished = _current is null;
            return !_finished;
        }

        /// <inheritdoc />
        public void Reset() => throw new NotSupportedException("Create a new array enumerator to restart iteration.");

        /// <inheritdoc />
        public void Dispose()
        {
            _lifetime.CheckThread();
            _context.Dispose();
            _current = null;
        }
    }
}
