using System;
using System.Collections;
using System.Collections.Generic;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Borrows a PostgreSQL bytea payload under a checked native lifetime.
/// </summary>
/// <remarks>
/// Flat and short values retain their original bytes. PostgreSQL detoasts other storage into a
/// private child context. The source owner must remain alive; direct scalar input views expire
/// at callback exit. Copied length and type metadata remain readable after disposal.
/// Dispose explicitly created views while their backend is active.
/// </remarks>
/// <param name="value">The present bytea datum, optionally wrapped in a domain.</param>
public sealed class PgByteaView(PgDatum value) : IReadOnlyList<byte>, IDisposable
{
    private readonly NativeBorrowedBuffer _buffer = new(value, NativeBufferKind.Bytea);

    /// <summary>
    /// Gets the payload length, including embedded zero bytes.
    /// </summary>
    public int Count => _buffer.Length;

    /// <summary>
    /// Gets the original PostgreSQL type OID, preserving domain identity.
    /// </summary>
    public uint TypeOid => _buffer.TypeOid;

    /// <summary>
    /// Gets the checked datum, valid until this view or its source expires.
    /// </summary>
    public PgDatum Datum => _buffer.Datum;

    /// <summary>
    /// Reads one byte after checking the native lifetime and zero-based index.
    /// </summary>
    /// <param name="index">The zero-based byte index.</param>
    /// <returns>The original byte, including zero.</returns>
    public byte this[int index]
    {
        get
        {
            ReadOnlySpan<byte> bytes = _buffer.GetSpan();
            ArgumentOutOfRangeException.ThrowIfNegative(index);
            ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(index, bytes.Length);
            return bytes[index];
        }
    }

    /// <summary>
    /// Copies the entire payload after checking its lifetime and the destination capacity.
    /// </summary>
    /// <param name="destination">Storage large enough for Count bytes.</param>
    public void CopyTo(Span<byte> destination) => _buffer.GetSpan().CopyTo(destination);

    /// <summary>
    /// Copies the payload into an independent managed array.
    /// </summary>
    /// <returns>Bytes that remain valid after the native owner expires.</returns>
    public byte[] ToArray() => _buffer.GetSpan().ToArray();

    /// <summary>
    /// Checks the lifetime once and exposes the native payload without copying.
    /// </summary>
    /// <returns>A span whose validity is the caller's responsibility after this method returns.</returns>
    /// <remarks>
    /// Never reset or delete the source owner, dispose this view, or exit its callback while using
    /// the span. Keep it on the originating backend thread and do not retain it across backend calls.
    /// The span cannot check subsequent native lifetime changes; prefer checked indexing or copies.
    /// </remarks>
    public ReadOnlySpan<byte> DangerousGetSpan() => _buffer.GetSpan();

    /// <summary>
    /// Creates an independent cursor that checks the native lifetime on every access.
    /// </summary>
    /// <returns>A byte enumerator that does not own the underlying view.</returns>
    public IEnumerator<byte> GetEnumerator()
    {
        _buffer.Validate();
        return new Enumerator(this);
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>
    /// Releases private storage and invalidates this view and its enumerators.
    /// </summary>
    public void Dispose() => _buffer.Dispose();

    /// <summary>
    /// Retains an independent byte position without allowing unchecked access after expiry.
    /// </summary>
    private sealed class Enumerator(PgByteaView view) : IEnumerator<byte>
    {
        private int _index = -1;
        private bool _disposed;

        /// <inheritdoc />
        public byte Current
        {
            get
            {
                Validate();
                if (_index < 0 || _index == view.Count)
                {
                    throw new InvalidOperationException("The enumerator is not positioned on a byte.");
                }

                return view[_index];
            }
        }

        object IEnumerator.Current => Current;

        /// <inheritdoc />
        public bool MoveNext()
        {
            Validate();
            if (_index < view.Count)
            {
                _index++;
            }

            return _index < view.Count;
        }

        /// <inheritdoc />
        public void Reset() => throw new NotSupportedException("Create another enumerator to restart.");

        /// <inheritdoc />
        public void Dispose() => _disposed = true;

        /// <summary>
        /// Rejects disposed cursors and expired native storage before reading any bytes.
        /// </summary>
        private void Validate()
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            view._buffer.Validate();
        }
    }
}
