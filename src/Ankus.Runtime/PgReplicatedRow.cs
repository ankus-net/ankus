using System;
using System.Text;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Holds a row as logical replication carries it, for <see cref="PgRelationWriter"/>: each column's value in
/// PostgreSQL's text or binary form, SQL NULL, or not sent.
/// </summary>
/// <remarks>
/// <para>
/// The writer converts each value with its column type's input or receive function, as the built-in apply worker
/// does, so the values need no managed type. A column that is not sent takes its default in an insert and keeps its
/// current value in an update, as an unchanged TOAST value does.
/// </para>
/// <para>
/// Columns are numbered from zero as in the writer's <see cref="PgRelationWriter.Descriptor"/>, including dropped
/// columns, which must stay unsent or NULL. Text received from a <see cref="PgReplicationConnection"/> is already in
/// the database's encoding and can be passed as bytes; text from a string is converted. Setting a column again
/// replaces its value, and <see cref="Clear"/> makes the row reusable.
/// </para>
/// </remarks>
public sealed class PgReplicatedRow
{
    private const byte Missing = (byte)'m';

    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly byte[] _status;

    private readonly int[] _start;

    private readonly int[] _length;

    private byte[] _data = new byte[256];

    private int _used;

    internal PgReplicatedRow(int count)
    {
        _status = new byte[count];
        _start = new int[count];
        _length = new int[count];
        _status.AsSpan().Fill(Missing);
    }

    /// <summary>
    /// Gets the number of columns, including dropped columns.
    /// </summary>
    public int Count => _status.Length;

    /// <summary>
    /// Sets a column to text in the database's encoding, such as a value a replication connection received.
    /// </summary>
    /// <param name="column">The zero-based column number.</param>
    /// <param name="value">The text's bytes, without a terminating zero.</param>
    public void SetText(int column, ReadOnlySpan<byte> value) => Set(column, (byte)'t', value);

    /// <summary>
    /// Sets a column to text, which the writer converts to the database's encoding.
    /// </summary>
    /// <param name="column">The zero-based column number.</param>
    /// <param name="value">The text.</param>
    public void SetText(int column, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        int length = s_utf8.GetByteCount(value);
        Span<byte> target = Reserve(column, (byte)'u', length);
        s_utf8.GetBytes(value, target);
    }

    /// <summary>
    /// Sets a column to the binary form its type's receive function reads, as <c>COPY BINARY</c> and binary logical
    /// replication send it.
    /// </summary>
    /// <param name="column">The zero-based column number.</param>
    /// <param name="value">The value's bytes.</param>
    public void SetBinary(int column, ReadOnlySpan<byte> value) => Set(column, (byte)'b', value);

    /// <summary>
    /// Sets a column to SQL NULL.
    /// </summary>
    /// <param name="column">The zero-based column number.</param>
    public void SetNull(int column) => Reserve(column, (byte)'n', 0);

    /// <summary>
    /// Marks every column as not sent.
    /// </summary>
    public void Clear()
    {
        _status.AsSpan().Fill(Missing);
        _used = 0;
    }

    /// <summary>
    /// Pins one or two rows, points the request's data, and its pointer when there is a second row, at their native
    /// views, and runs the request.
    /// </summary>
    internal static unsafe NativeMemoryResult Invoke(ref NativeMemoryRequest request, PgReplicatedRow first, PgReplicatedRow? second)
    {
        fixed (byte* status = first._status, data = first._data, secondStatus = second?._status, secondData = second?._data)
        fixed (int* start = first._start, length = first._length, secondStart = second?._start, secondLength = second?._length)
        {
            NativeReplicatedRow row = new() { _status = status, _start = start, _length = length, _data = data, _count = first.Count };
            NativeReplicatedRow other = new()
            {
                _status = secondStatus,
                _start = secondStart,
                _length = secondLength,
                _data = secondData,
                _count = second?.Count ?? 0,
            };
            request._data = (nint)(&row);
            if (second is not null)
            {
                request._pointer = (nint)(&other);
            }

            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            return result;
        }
    }

    private void Set(int column, byte status, ReadOnlySpan<byte> value) => value.CopyTo(Reserve(column, status, value.Length));

    private Span<byte> Reserve(int column, byte status, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(column);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(column, _status.Length);
        if (_data.Length - _used < length)
        {
            Array.Resize(ref _data, Math.Max(checked(_used + length), _data.Length * 2));
        }

        _status[column] = status;
        _start[column] = _used;
        _length[column] = length;
        _used += length;
        return _data.AsSpan(_start[column], length);
    }
}

/// <summary>
/// Passes a <see cref="PgReplicatedRow"/>'s pinned arrays to native code.
/// </summary>
internal unsafe struct NativeReplicatedRow
{
    /// <summary>
    /// Points to each column's status: <c>t</c> text in the database encoding, <c>u</c> UTF-8 text, <c>b</c> binary,
    /// <c>n</c> NULL or <c>m</c> not sent.
    /// </summary>
    internal byte* _status;

    /// <summary>
    /// Points to each column's offset in the data.
    /// </summary>
    internal int* _start;

    /// <summary>
    /// Points to each column's length in bytes.
    /// </summary>
    internal int* _length;

    /// <summary>
    /// Points to the values' bytes.
    /// </summary>
    internal byte* _data;

    /// <summary>
    /// Holds the number of columns.
    /// </summary>
    internal int _count;
}
