using System;
using System.Collections;
using System.Collections.Generic;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Reads a borrowed PostgreSQL array through a checked scalar element conversion.
/// </summary>
/// <typeparam name="T">A supported scalar representation, nullable when value-type cells can be SQL NULL.</typeparam>
/// <remarks>
/// The array retains its native storage, dimensions, lower bounds and exact type identity.
/// Elements are converted on access using PgDatum.Read&lt;T&gt;; ordinary values are copied,
/// while borrowed text and bytea elements retain the source lifetime. Dispose such elements
/// when finished. Disposing an enumerator releases only its cursor; returned elements stay alive.
/// Nonnullable value types reject arrays containing SQL NULL during construction.
/// Indexed lookup is O(1) for fixed-size native elements stored without a NULL bitmap,
/// and O(n) otherwise; enumeration visits the array in one linear pass and converts each
/// cell once. Native access expires with this view, its source owner or its source callback.
/// Copied metadata remains readable after expiry. Construct this view from a checked PgDatum,
/// or request it through PgDatum.Read, SPI scalar helpers or PgFunctions. Raw reads borrow their
/// source lifetime; scalar SPI and function results use callback-owned snapshots. Whole-array
/// SQL NULL returns a null view after checking the declared element identity. Present parameters
/// transport the original datum without requiring an element writer.
/// Generated scalar, SETOF, TABLE and aggregate signatures use the concrete element's SQL array
/// type. Scalar inputs expire with their callback; retained inputs receive independent snapshots.
/// Returns preserve the original datum and must match the declared native array identity.
/// </remarks>
public sealed class PgArrayView<T> : IReadOnlyList<T>, IPgArrayView
{
    private readonly PgArrayView _view;

    /// <summary>
    /// Borrows a present array after validating its element identity, including empty and all-NULL arrays.
    /// </summary>
    /// <param name="value">The checked source datum, whose owner must remain alive.</param>
    /// <exception cref="ArgumentException">The array datum is SQL NULL.</exception>
    /// <exception cref="NotSupportedException">T has no readable scalar conversion.</exception>
    /// <exception cref="InvalidCastException">A nonnullable value type cannot represent a NULL cell.</exception>
    /// <exception cref="PgException">The PostgreSQL element type does not match T.</exception>
    public PgArrayView(PgDatum value)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Lifetime.Validate();
        if (value.IsNull)
        {
            throw new ArgumentException("A borrowed array requires a non-NULL datum.", nameof(value));
        }

        Type type = typeof(T);
        if (type.IsArray && type != typeof(byte[]) || type.IsGenericType && type.GetGenericTypeDefinition() == typeof(PgArray<>))
        {
            throw new NotSupportedException("A borrowed array requires a scalar element conversion; nested arrays are not supported.");
        }

        DatumTypeMapping? mapping = PgDatumRegistry.Find(type);
        mapping?.RequireRead();
        _view = new PgArrayView(value, SpiType.GetOid<T>(), mapping is not null);
        try
        {
            if (default(T) is not null && _view.HasNulls)
            {
                throw new InvalidCastException($"SQL NULL array cells cannot be represented by '{type}'. Use a nullable element type.");
            }
        }
        catch (Exception primary)
        {
            PgResultCleanup.Dispose(_view, primary);
            throw;
        }
    }

    /// <summary>
    /// Gets the original array type OID, including a domain over an array.
    /// </summary>
    public uint TypeOid => _view.TypeOid;

    /// <summary>
    /// Gets the exact element OID, including domain, enum and composite identities.
    /// </summary>
    public uint ElementTypeOid => _view.ElementTypeOid;

    /// <summary>
    /// Gets the total number of cells, including SQL NULL cells.
    /// </summary>
    public int Count => _view.Count;

    /// <summary>
    /// Gets the number of dimensions; empty arrays have rank zero.
    /// </summary>
    public int Rank => _view.Rank;

    /// <summary>
    /// Gets whether the array contains a SQL NULL cell.
    /// </summary>
    public bool HasNulls => _view.HasNulls;

    /// <summary>
    /// Gets the copied dimension lengths.
    /// </summary>
    public ReadOnlySpan<int> Lengths => _view.Lengths;

    /// <summary>
    /// Borrows the array's native SQL NULL bitmap, with the lifetime rules of <see cref="PgArrayView.DangerousGetNullBitmap"/>.
    /// </summary>
    /// <returns>One bit per element, least significant bit first, set for a present value; empty without a bitmap.</returns>
    public ReadOnlySpan<byte> DangerousGetNullBitmap() => _view.DangerousGetNullBitmap();

    /// <summary>
    /// Gets the copied PostgreSQL lower bounds.
    /// </summary>
    public ReadOnlySpan<int> LowerBounds => _view.LowerBounds;

    /// <summary>
    /// Gets the checked flat datum under this view's native lifetime.
    /// </summary>
    public PgDatum Datum => _view.Datum;

    /// <summary>
    /// Converts a cell at a zero-based row-major index using PostgreSQL's native array lookup.
    /// </summary>
    /// <param name="index">The flat index, independent of PostgreSQL lower bounds.</param>
    /// <returns>The copied value or checked borrowed element.</returns>
    public T this[int index] => _view[index].Read<T>();

    /// <summary>
    /// Converts a cell using one PostgreSQL subscript per dimension.
    /// </summary>
    /// <param name="subscripts">The native subscripts, including negative or non-one lower bounds.</param>
    /// <returns>The copied value or checked borrowed element.</returns>
    public T GetValue(params ReadOnlySpan<int> subscripts) => _view.GetValue(subscripts).Read<T>();

    /// <summary>
    /// Creates an independent native cursor that converts each cell once in row-major order.
    /// </summary>
    /// <returns>An enumerator which must be disposed after use.</returns>
    public IEnumerator<T> GetEnumerator() => new Enumerator(_view.GetEnumerator());

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Releases private native storage and invalidates this view and its borrowed elements.
    /// </summary>
    public void Dispose() => _view.Dispose();

    /// <summary>
    /// Caches a single conversion per cell while checking the source lifetime on every access.
    /// </summary>
    private sealed class Enumerator(IEnumerator<PgDatum> cursor) : IEnumerator<T>
    {
        private T _current = default!;
        private bool _positioned;

        /// <inheritdoc />
        public T Current
        {
            get
            {
                _ = cursor.Current;
                return _positioned ? _current : throw new InvalidOperationException("The enumerator is not positioned on a converted cell.");
            }
        }

        object? IEnumerator.Current => Current;

        /// <inheritdoc />
        public bool MoveNext()
        {
            _positioned = false;
            _current = default!;
            if (!cursor.MoveNext())
            {
                return false;
            }

            _current = cursor.Current.Read<T>();
            _positioned = true;
            return true;
        }

        /// <inheritdoc />
        public void Reset() => cursor.Reset();

        /// <inheritdoc />
        public void Dispose()
        {
            cursor.Dispose();
            _positioned = false;
            _current = default!;
        }
    }
}
