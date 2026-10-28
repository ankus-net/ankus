using System;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Unicode;

namespace Ankus.CompilerServices;

/// <summary>
/// Owns optional detoast and transcoding allocations while checking the source's native lifetime.
/// </summary>
internal sealed class NativeBorrowedBuffer : IDisposable
{
    private readonly PgMemoryContext? _context;
    private readonly PgDatum _datum;
    private readonly nint _data;
    private readonly NativeBufferKind _kind;
    private bool _released;

    /// <summary>
    /// Borrows a present bytea, text or C-string datum: a flat value in place, otherwise under a private child of its
    /// source owner that holds any detoasted or transcoded copy.
    /// </summary>
    internal NativeBorrowedBuffer(PgDatum value, NativeBufferKind kind)
    {
        ArgumentNullException.ThrowIfNull(value);
        value.Lifetime.Validate();
        if (value.IsNull)
        {
            throw new ArgumentException("A borrowed buffer requires a non-NULL datum.", nameof(value));
        }

        _kind = kind;
        if (TryBorrowFlat(value, kind, out _data, out int length))
        {
            _datum = new PgDatum(value.UncheckedBits, value.TypeOid, false, new PgDatumLifetime(value.Lifetime, this));
            Length = length;
            return;
        }

        PgMemoryContext parent = PgMemoryContext.FromId(NativeMemoryContext.Provider, value.Lifetime.ContextId);
        _context = PgMemoryContext.Create("Ankus borrowed buffer", parent);
        try
        {
            var lifetime = new PgDatumLifetime(_context, source: value.Lifetime);
            (_datum, _data, Length) = NativeBackend.BorrowBuffer(value, lifetime, kind);
            lifetime.Scope?.Register(this);
        }
        catch (Exception primary)
        {
            PgResultCleanup.Dispose(_context, primary);
            throw;
        }
    }

    /// <summary>
    /// Gets whether the view has been disposed.
    /// </summary>
    internal bool IsReleased => _released;

    /// <summary>
    /// Gets the copied payload length in bytes.
    /// </summary>
    internal int Length { get; }

    /// <summary>
    /// Gets the original type identity, including a domain over the underlying buffer type.
    /// </summary>
    internal uint TypeOid => _datum.TypeOid;

    /// <summary>
    /// Gets the original server representation after validating the owner and callback lease.
    /// </summary>
    internal PgDatum Datum
    {
        get
        {
            Validate();
            return _datum;
        }
    }

    /// <summary>
    /// Rejects access after source reset, deletion, disposal, callback exit or backend changes.
    /// </summary>
    internal void Validate()
    {
        ObjectDisposedException.ThrowIf(_released, this);
        _datum.Lifetime.Validate();
    }

    /// <summary>
    /// Validates the lifetime once; callers must not invoke backend operations while using the span.
    /// </summary>
    internal unsafe ReadOnlySpan<byte> GetSpan(bool includeTerminator = false)
    {
        Validate();
        if (includeTerminator && _kind != NativeBufferKind.CString)
        {
            throw new InvalidOperationException("Only a C-string view includes a terminating zero byte.");
        }

        return new ReadOnlySpan<byte>((void*)_data, includeTerminator ? checked(Length + 1) : Length);
    }

    /// <summary>
    /// Releases only private detoast or transcoding storage and invalidates this view.
    /// </summary>
    public void Dispose()
    {
        _datum.Lifetime.CheckThread();
        if (_context is null)
        {
            _datum.Lifetime.CheckAccess();
        }
        else
        {
            _context.Dispose();
            _datum.Lifetime.Scope?.Unregister(this);
        }

        _released = true;
    }

    /// <summary>
    /// Borrows the bytes of a flat text or bytea value in place, without a native call. PostgreSQL stores such a value
    /// with a one- or four-byte header; a compressed or external value, a domain, another database encoding or text
    /// PostgreSQL would reject takes the native path, which detoasts, transcodes or reports the error.
    /// </summary>
    private static bool TryBorrowFlat(PgDatum value, NativeBufferKind kind, out nint data, out int length)
    {
        bool text = kind == NativeBufferKind.Text;
        bool supported = (text ? value.TypeOid is 25 or 1042 or 1043 : kind == NativeBufferKind.Bytea && value.TypeOid == 17) &&
            NativeMemoryContext.CanReadDirectly(text);
        data = 0;
        length = 0;
        return supported && TryGetFlatBytes(value.UncheckedBits, text, out data, out length);
    }

    /// <summary>
    /// Locates the payload of a flat varlena, and for text checks it is the UTF-8 PostgreSQL would accept.
    /// </summary>
    /// <param name="address">The varlena address.</param>
    /// <param name="text">Whether the payload must be valid UTF-8 without zero bytes.</param>
    /// <param name="data">The payload address.</param>
    /// <param name="length">The payload length.</param>
    /// <returns>Whether the value is flat and acceptable.</returns>
    internal static unsafe bool TryGetFlatBytes(nuint address, bool text, out nint data, out int length)
    {
        data = 0;
        length = 0;
        byte* header = (byte*)address;
        if (!BitConverter.IsLittleEndian || header == null)
        {
            return false;
        }

        // Little-endian varlena headers: a set low bit marks a one-byte header, whose value 1 is an external pointer;
        // otherwise the low two bits are zero for an uncompressed four-byte header.
        if ((header[0] & 0x01) != 0)
        {
            if (header[0] == 0x01)
            {
                return false;
            }

            length = (header[0] >> 1) - 1;
            data = (nint)(header + 1);
        }
        else
        {
            uint word = Unsafe.ReadUnaligned<uint>(header);
            if ((word & 0x03) != 0)
            {
                return false;
            }

            length = (int)(word >> 2) - 4;
            data = (nint)(header + 4);
        }

        if (length < 0)
        {
            return false;
        }

        ReadOnlySpan<byte> bytes = new((void*)data, length);
        return !text || (!bytes.Contains((byte)0) && Utf8.IsValid(bytes));
    }
}
