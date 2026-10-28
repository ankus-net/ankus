using System;
using System.ComponentModel;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Appends binary data and UTF-8 text to a checked PostgreSQL StringInfo buffer.
/// </summary>
/// <remarks>
/// This write-only stream requires the active backend thread. Native buffers retain their
/// PostgreSQL context ownership across ambient context switches. Disposal frees owned storage;
/// borrowed storage remains with its native owner. No finalizer calls PostgreSQL.
/// </remarks>
public sealed unsafe class PgStringInfoStream : Stream
{
    private static readonly UTF8Encoding s_utf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    private readonly nint _provider;
    private readonly nint _handle;
    private readonly nint _address;
    private readonly nint _generation;
    private readonly bool _readOnly;
    private bool _disposed;

    /// <summary>
    /// Retains an owned registry identity or an explicitly anchored borrowed pointer.
    /// </summary>
    private PgStringInfoStream(nint provider, nint handle, nint address, nint generation, bool readOnly)
    {
        _provider = provider;
        _handle = handle;
        _address = address;
        _generation = generation;
        _readOnly = readOnly;
    }

    /// <summary>
    /// Creates an empty PostgreSQL buffer with at least the requested payload capacity.
    /// </summary>
    /// <param name="capacity">The minimum byte capacity, excluding the trailing NUL.</param>
    /// <param name="context">The destination context, or null for the current context.</param>
    /// <returns>An individually owned native buffer.</returns>
    public static PgStringInfoStream Create(int capacity = 0, PgMemoryContext? context = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        return CreateCore([], capacity, context);
    }

    /// <summary>
    /// Creates an owned buffer containing an independent copy of arbitrary bytes.
    /// </summary>
    /// <param name="value">The initial bytes, including any embedded NUL or invalid UTF-8.</param>
    /// <param name="context">The destination context, or null for the current context.</param>
    /// <returns>The initialized native buffer.</returns>
    public static PgStringInfoStream Create(ReadOnlySpan<byte> value, PgMemoryContext? context = null)
        => CreateCore(value, value.Length, context);

    /// <summary>
    /// Creates an owned buffer containing strict UTF-8, independently of the database encoding.
    /// </summary>
    /// <param name="value">The initial text; embedded NUL characters are preserved.</param>
    /// <param name="context">The destination context, or null for the current context.</param>
    /// <returns>The initialized native buffer.</returns>
    /// <exception cref="EncoderFallbackException">The text contains an unpaired UTF-16 surrogate.</exception>
    public static PgStringInfoStream Create(string value, PgMemoryContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Create(s_utf8.GetBytes(value), context);
    }

    /// <summary>
    /// Borrows an external PostgreSQL StringInfo without acquiring release rights.
    /// </summary>
    /// <param name="address">The initialized native StringInfoData address, or null.</param>
    /// <param name="lifetimeContext">The explicit reset-sensitive lifetime anchor.</param>
    /// <returns>A borrowed buffer, or null for a null address.</returns>
    /// <remarks>
    /// The caller guarantees accessible struct and payload storage until the shorter of its
    /// external lifetime and the anchor lifetime. Mutable buffers must support PostgreSQL's
    /// repalloc contract. Read-only buffers may use non-palloc storage without a trailing NUL.
    /// The anchor does not establish allocator provenance or detect external free or resize.
    /// </remarks>
    public static PgStringInfoStream? DangerousBorrow(void* address, PgMemoryContext lifetimeContext)
    {
        if (address == null)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(lifetimeContext);
        nint context = lifetimeContext.GetId();
        nint provider = NativeMemoryContext.Provider;
        NativeMemoryRequest capture = new()
        {
            _operation = NativeMemoryOperation.CaptureGeneration,
            _context = context
        };
        NativeMemoryContext.Invoke(ref capture, out NativeMemoryResult generation);
        var borrowed = new PgStringInfoStream(provider, context, (nint)address, generation._value, readOnly: false);
        NativeMemoryResult state = borrowed.Invoke(NativeStringInfoOperation.Inspect);
        return new PgStringInfoStream(provider, context, (nint)address, generation._value, state._value == 0);
    }

    /// <summary>
    /// Gets false because stream reads are unsupported; use checked byte copies instead.
    /// </summary>
    public override bool CanRead => false;

    /// <summary>
    /// Gets false because writes always append to the native buffer.
    /// </summary>
    public override bool CanSeek => false;

    /// <summary>
    /// Gets whether this undisposed wrapper was created for a writable native buffer.
    /// </summary>
    /// <remarks>
    /// This capability does not prove that its native lifetime remains live; each operation checks that lifetime.
    /// </remarks>
    public override bool CanWrite => !_disposed && !_readOnly;

    /// <summary>
    /// Gets the current payload byte length, excluding any trailing NUL.
    /// </summary>
    public override long Length => checked((long)Invoke(NativeStringInfoOperation.Inspect)._length);

    /// <summary>
    /// Rejects stream positioning because writes always append.
    /// </summary>
    public override long Position
    {
        get => throw new NotSupportedException("PostgreSQL StringInfo streams do not support seeking.");
        set => throw new NotSupportedException("PostgreSQL StringInfo streams do not support seeking.");
    }

    /// <summary>
    /// Gets the payload capacity excluding NUL, or the payload length for a read-only external buffer.
    /// </summary>
    public int Capacity => GetCapacity(Invoke(NativeStringInfoOperation.Inspect));

    /// <summary>
    /// Gets whether the checked native buffer currently contains no payload bytes.
    /// </summary>
    public bool IsEmpty => Length == 0;

    /// <summary>
    /// Gets whether the native buffer is marked read-only.
    /// </summary>
    public bool IsReadOnly => Invoke(NativeStringInfoOperation.Inspect)._value == 0;

    /// <summary>
    /// Gets the native owner for an owned buffer or the explicit lifetime anchor for a borrowed buffer.
    /// </summary>
    public PgMemoryContext LifetimeContext
    {
        get
        {
            NativeMemoryResult state = Invoke(NativeStringInfoOperation.Inspect);
            return PgMemoryContext.FromId(_provider, state._context);
        }
    }

    /// <summary>
    /// Appends arbitrary bytes, retaining embedded NUL and invalid UTF-8.
    /// </summary>
    /// <param name="buffer">The payload to append.</param>
    public override void Write(ReadOnlySpan<byte> buffer)
    {
        EnsureWritable();
        fixed (byte* data = buffer)
        {
            Invoke(NativeStringInfoOperation.Append, (nint)data, buffer.Length);
        }
    }

    /// <summary>
    /// Appends the selected managed byte range.
    /// </summary>
    /// <param name="buffer">The source array.</param>
    /// <param name="offset">The first source byte.</param>
    /// <param name="count">The number of bytes to append.</param>
    public override void Write(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        Write(buffer.AsSpan(offset, count));
    }

    /// <summary>
    /// Appends one byte, including zero.
    /// </summary>
    /// <param name="value">The byte to append.</param>
    public override void WriteByte(byte value) => Write([value]);

    /// <summary>
    /// Appends strict UTF-8 text without database-encoding conversion.
    /// </summary>
    /// <param name="value">The text, including any embedded NUL.</param>
    /// <exception cref="EncoderFallbackException">The text contains an unpaired UTF-16 surrogate.</exception>
    public void Write(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        Write(s_utf8.GetBytes(value));
    }

    /// <summary>
    /// Appends one complete Unicode scalar as UTF-8.
    /// </summary>
    /// <param name="value">The scalar to append.</param>
    public void Write(Rune value)
    {
        Span<byte> bytes = stackalloc byte[4];
        int length = value.EncodeToUtf8(bytes);
        Write(bytes[..length]);
    }

    /// <summary>
    /// Appends one non-surrogate UTF-16 character as UTF-8.
    /// </summary>
    /// <param name="value">The character to append; use a string or Rune for a surrogate pair.</param>
    public void Write(char value) => Write(new Rune(value));

    /// <summary>
    /// Copies an exact payload range without exposing a span over resizable native storage.
    /// </summary>
    /// <param name="destination">The managed destination; its length selects the byte count.</param>
    /// <param name="offset">The first native payload byte to copy.</param>
    public void CopyTo(Span<byte> destination, int offset = 0)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        fixed (byte* data = destination)
        {
            Invoke(NativeStringInfoOperation.Read, (nint)data, destination.Length, offset);
        }
    }

    /// <summary>
    /// Replaces an existing payload range without appending or changing its length.
    /// </summary>
    /// <param name="offset">The first native payload byte to replace.</param>
    /// <param name="source">The replacement bytes.</param>
    public void WriteAt(int offset, ReadOnlySpan<byte> source)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        EnsureWritable();
        fixed (byte* data = source)
        {
            Invoke(NativeStringInfoOperation.Write, (nint)data, source.Length, offset);
        }
    }

    /// <summary>
    /// Copies the complete payload into independent managed storage.
    /// </summary>
    /// <returns>The exact payload bytes without the native terminator.</returns>
    public byte[] ToArray()
    {
        byte[] bytes = new byte[checked((int)Length)];
        CopyTo(bytes);
        return bytes;
    }

    /// <summary>
    /// Decodes the complete payload as strict UTF-8, preserving embedded NUL characters.
    /// </summary>
    /// <returns>The decoded text.</returns>
    /// <exception cref="DecoderFallbackException">The payload is not valid UTF-8.</exception>
    public override string ToString() => s_utf8.GetString(ToArray());

    /// <summary>
    /// Formats arbitrary bytes for display by explicitly replacing malformed UTF-8 sequences.
    /// </summary>
    /// <returns>Display text containing replacement characters for invalid sequences.</returns>
    /// <remarks>
    /// This matches pgrx's lossy Display formatting. Use ToArray or strict ToString when preserving values.
    /// </remarks>
    public string ToStringLossy() => Encoding.UTF8.GetString(ToArray());

    /// <summary>
    /// Ensures capacity for at least the requested number of additional payload bytes.
    /// </summary>
    /// <param name="additionalByteCount">Additional bytes beyond the current length.</param>
    public void Enlarge(int additionalByteCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(additionalByteCount);
        EnsureWritable();
        Invoke(NativeStringInfoOperation.Enlarge, value: additionalByteCount);
    }

    /// <summary>
    /// Ensures an absolute minimum payload capacity while preserving all existing bytes.
    /// </summary>
    /// <param name="capacity">The minimum payload capacity excluding NUL.</param>
    /// <returns>The resulting payload capacity.</returns>
    public int EnsureCapacity(int capacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(capacity);
        EnsureWritable();
        return GetCapacity(Invoke(NativeStringInfoOperation.EnsureCapacity, value: capacity));
    }

    /// <summary>
    /// Clears the payload and native cursor while retaining the allocated capacity.
    /// </summary>
    public void Reset()
    {
        EnsureWritable();
        Invoke(NativeStringInfoOperation.Reset);
    }

    /// <summary>
    /// Validates the buffer; appended bytes already reside in PostgreSQL storage.
    /// </summary>
    public override void Flush() => Invoke(NativeStringInfoOperation.Inspect);

    /// <summary>
    /// Writes synchronously on the current backend thread and returns a completed task.
    /// </summary>
    /// <param name="buffer">The source array.</param>
    /// <param name="offset">The first source byte.</param>
    /// <param name="count">The number of bytes to append.</param>
    /// <param name="cancellationToken">Cancellation checked before accessing native storage.</param>
    /// <returns>A completed task; no work is queued to another thread.</returns>
    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer, offset, count);
        return Task.CompletedTask;
    }

    /// <summary>
    /// Writes synchronously on the current backend thread and returns a completed operation.
    /// </summary>
    /// <param name="buffer">The source bytes.</param>
    /// <param name="cancellationToken">Cancellation checked before accessing native storage.</param>
    /// <returns>A completed operation; no native capability flows to another thread.</returns>
    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Validates the buffer synchronously on the invoking backend thread.
    /// </summary>
    /// <param name="cancellationToken">Cancellation checked before validation.</param>
    /// <returns>A completed task.</returns>
    public override Task FlushAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Flush();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Releases owned storage synchronously on the invoking backend thread.
    /// </summary>
    /// <returns>A completed operation.</returns>
    public override ValueTask DisposeAsync() => base.DisposeAsync();

    /// <summary>
    /// Rejects stream reads; use ToArray or CopyTo with a destination span.
    /// </summary>
    /// <param name="buffer">The unused destination.</param>
    /// <param name="offset">The unused destination offset.</param>
    /// <param name="count">The unused requested length.</param>
    /// <returns>No value; this method throws NotSupportedException.</returns>
    public override int Read(byte[] buffer, int offset, int count)
        => throw new NotSupportedException("PostgreSQL StringInfo streams support appending only; they cannot be read.");

    /// <summary>
    /// Rejects seeking because stream writes always append.
    /// </summary>
    /// <param name="offset">The unused offset.</param>
    /// <param name="origin">The unused origin.</param>
    /// <returns>No value; this method throws NotSupportedException.</returns>
    public override long Seek(long offset, SeekOrigin origin)
        => throw new NotSupportedException("PostgreSQL StringInfo streams do not support seeking.");

    /// <summary>
    /// Rejects arbitrary length changes; use Reset to clear the payload.
    /// </summary>
    /// <param name="value">The unused length.</param>
    public override void SetLength(long value)
        => throw new NotSupportedException("Use Reset to clear a PostgreSQL StringInfo buffer.");

    /// <summary>
    /// Returns the checked native StringInfoData address for explicitly unsafe interoperability.
    /// </summary>
    /// <returns>The native struct pointer.</returns>
    /// <remarks>
    /// The caller must not free owned storage or change its ownership while this wrapper remains live.
    /// A borrowed struct may have a lifetime shorter than its context anchor.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void* DangerousGetPointer() => (void*)Invoke(NativeStringInfoOperation.Inspect)._pointer;

    /// <summary>
    /// Returns the current payload pointer, which may change on any growth operation.
    /// </summary>
    /// <returns>The native data pointer.</returns>
    /// <remarks>
    /// The caller enforces bounds and lifetime. Read-only data may lack a trailing NUL.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public byte* DangerousGetDataPointer() => (byte*)Invoke(NativeStringInfoOperation.Inspect)._data;

    /// <summary>
    /// Appends an explicitly accessible native byte range, including a slice of this buffer's payload.
    /// </summary>
    /// <param name="data">The accessible source pointer, or null for an empty append.</param>
    /// <param name="length">The source byte count.</param>
    /// <remarks>
    /// The caller guarantees the source range remains readable until copied. A slice of the current
    /// payload is resolved again after growth, so self-appending does not retain an obsolete pointer.
    /// </remarks>
    public void DangerousAppend(void* data, int length)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (data == null && length != 0)
        {
            throw new ArgumentNullException(nameof(data));
        }

        EnsureWritable();
        Invoke(NativeStringInfoOperation.Append, (nint)data, length);
    }

    /// <summary>
    /// Consumes this wrapper and returns the whole native StringInfo without freeing its storage.
    /// </summary>
    /// <returns>The native StringInfoData pointer with its original PostgreSQL lifetime.</returns>
    public void* DangerousDetach() => (void*)Detach(NativeStringInfoOperation.Detach);

    /// <summary>
    /// Consumes this wrapper and returns its payload without freeing the payload storage.
    /// </summary>
    /// <returns>The original payload pointer with its existing native lifetime.</returns>
    /// <remarks>
    /// For owned buffers, the now-unneeded native struct is freed. Borrowed structs and payloads
    /// remain untouched. This operation does not promise C-string termination or transfer a borrowed allocator's rights.
    /// </remarks>
    public byte* DangerousDetachData() => (byte*)Detach(NativeStringInfoOperation.DetachData);

    /// <summary>
    /// Consumes the buffer only after proving its payload contains exactly one trailing NUL and no interior NUL.
    /// </summary>
    /// <returns>The original NUL-terminated data pointer, with the same ownership rules as DangerousDetachData.</returns>
    /// <remarks>
    /// For a read-only external buffer, the unsafe caller must prove Length plus one bytes are
    /// accessible. Rejection leaves this wrapper live and its bytes unchanged.
    /// </remarks>
    public byte* DangerousDetachCString() => (byte*)Detach(NativeStringInfoOperation.DetachCString);

    /// <summary>
    /// Frees both native allocations when owned; borrowed disposal only closes this wrapper.
    /// </summary>
    /// <param name="disposing">Whether explicit managed disposal is occurring.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            if (_address == 0)
            {
                Invoke(NativeStringInfoOperation.Dispose);
            }

            _disposed = true;
        }

        base.Dispose(disposing);
    }

    /// <summary>
    /// Creates and initializes both native allocations within one guarded acquisition.
    /// </summary>
    private static PgStringInfoStream CreateCore(ReadOnlySpan<byte> value, int capacity, PgMemoryContext? context)
    {
        PgMemoryContext destination = context ?? PgMemoryContext.Current;
        nint contextId = destination.GetId();
        nint provider = NativeMemoryContext.Provider;
        fixed (byte* data = value)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.StringInfo,
                _flags = (int)NativeStringInfoOperation.Create,
                _context = contextId,
                _data = (nint)data,
                _length = (nuint)value.Length,
                _value = capacity,
            };
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            if (result._pointer == 0)
            {
                throw new InvalidOperationException("PostgreSQL did not return a StringInfo identity.");
            }

            return new PgStringInfoStream(provider, result._pointer, 0, 0, readOnly: false);
        }
    }

    /// <summary>
    /// Converts the native capacity sentinel into a payload byte capacity.
    /// </summary>
    private static int GetCapacity(NativeMemoryResult state)
        => state._value == 0 ? checked((int)state._length) : checked((int)state._value - 1);

    /// <summary>
    /// Rejects disposed, foreign or initially read-only handles before mutation.
    /// </summary>
    private void EnsureWritable()
    {
        EnsureLive();
        if (_readOnly)
        {
            throw new NotSupportedException("The PostgreSQL StringInfo buffer is read-only.");
        }
    }

    /// <summary>
    /// Validates managed disposal and the active extension provider.
    /// </summary>
    private void EnsureLive()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        NativeMemoryContext.CheckProvider(_provider);
    }

    /// <summary>
    /// Consumes a wrapper only after a successful native transfer.
    /// </summary>
    private nint Detach(NativeStringInfoOperation operation)
    {
        NativeMemoryResult result = Invoke(operation);
        _disposed = true;
        return result._pointer;
    }

    /// <summary>
    /// Invokes the existing guarded memory capability with this buffer's exact ownership identity.
    /// </summary>
    private NativeMemoryResult Invoke(NativeStringInfoOperation operation, nint data = 0, int length = 0, int value = 0)
    {
        EnsureLive();
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.StringInfo,
            _flags = (int)operation,
            _context = _handle,
            _other = _generation,
            _pointer = _address,
            _data = data,
            _length = (nuint)length,
            _value = value,
        };
        try
        {
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            return result;
        }
        catch (PgException exception) when (exception.SqlState == PgSqlStates.ObjectNotInPrerequisiteState)
        {
            throw new ObjectDisposedException(nameof(PgStringInfoStream), "The PostgreSQL StringInfo buffer or its lifetime anchor has been reclaimed.");
        }
    }
}
