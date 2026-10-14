using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Ankus;

/// <summary>
/// Holds a native-layout PostgreSQL value with checked borrowing and copy-on-write ownership.
/// </summary>
/// <remarks>
/// Direct scalar inputs borrow until their callback exits. A detoasted temporary is writable but
/// still expires with that callback. The first write to borrowed storage creates a private native
/// allocation in the captured source context. Explicit clones use their selected context lifetime.
/// Access requires the originating backend thread and a live native owner. No managed reference
/// to expiring native storage escapes through Value. Dispose owns no PostgreSQL input buffer.
/// </remarks>
/// <typeparam name="T">A statically registered PgType with NativeLayout enabled.</typeparam>
public sealed unsafe class PgVarlena<T> : IDisposable, IPgVarlena where T : unmanaged
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private readonly PgMemoryContext _context;
    private readonly nint _inputHeader;
    private readonly nint _inputPayload;
    private readonly bool _inputWritable;
    private NativeBorrowScope? _scope;
    private PgDatumLifetime? _lifetime;
    private PgAllocation? _allocation;
    private nuint _offset;
    private bool _disposed;

    /// <summary>
    /// Creates a zero-initialized native varlena in the selected context.
    /// </summary>
    /// <param name="context">The owner, or the current context when omitted.</param>
    public PgVarlena(PgMemoryContext? context = null) : this(default, context)
    {
    }

    /// <summary>
    /// Creates an independent native varlena containing the supplied value.
    /// </summary>
    /// <param name="value">The complete native payload.</param>
    /// <param name="context">The owner, or the current context when omitted.</param>
    public PgVarlena(T value, PgMemoryContext? context = null)
    {
        _ = PgTypeRegistry.RequireVarlena<T>().Size;
        _context = context ?? PgMemoryContext.Current;
        (_allocation, _offset) = Allocate(value);
    }

    /// <summary>
    /// Captures a validated native input without copying its payload.
    /// </summary>
    internal PgVarlena(nint header, nint payload, bool writable, PgMemoryContext context, NativeBorrowScope scope)
    {
        _context = context;
        _inputHeader = header;
        _inputPayload = payload;
        _inputWritable = writable;
        _scope = scope;
        _lifetime = new PgDatumLifetime(context);
    }

    /// <summary>
    /// Gets whether the next write requires a private copy of PostgreSQL's input.
    /// </summary>
    /// <remarks>
    /// False includes writable detoast temporaries, which remain callback-scoped until explicitly copied.
    /// </remarks>
    public bool IsBorrowed
    {
        get
        {
            Validate();
            return _allocation is null && !_inputWritable;
        }
    }

    /// <summary>
    /// Gets the checked storage context. A borrowed input also requires its callback lease to remain active.
    /// </summary>
    public PgMemoryContext Context
    {
        get
        {
            Validate();
            return _allocation?.Context ?? PgMemoryContext.FromId(NativeMemoryContext.Provider, _context.Id);
        }
    }

    /// <summary>
    /// Gets a copied value or replaces the complete native payload, copying borrowed storage before writing.
    /// </summary>
    public T Value
    {
        get
        {
            Validate();
            return _allocation is { } allocation ? allocation.Read<T>(_offset) : MemoryMarshal.Read<T>(InputBytes);
        }
        set
        {
            Validate();
            if (_allocation is { } allocation)
            {
                allocation.Write(value, _offset);
                ClearPadding(allocation, _offset);
            }
            else if (_inputWritable)
            {
                MemoryMarshal.Write(InputBytes, in value);
                NativeLayoutPadding.Clear(InputBytes, PgTypeRegistry.RequireVarlena<T>().Padding);
            }
            else
            {
                (PgAllocation copy, nuint offset) = Allocate(value);
                _allocation = copy;
                _offset = offset;
                _scope = null;
                _lifetime = null;
            }
        }
    }

    /// <summary>
    /// Copies this value into independent native storage.
    /// </summary>
    /// <param name="destination">The destination context, or this value's captured source context.</param>
    /// <returns>An independently disposable value.</returns>
    public PgVarlena<T> Clone(PgMemoryContext? destination = null) => new(Value, destination ?? Context);

    /// <summary>
    /// Transfers owned storage to a checked datum and consumes this wrapper after success.
    /// </summary>
    /// <returns>The datum, valid until its native context resets or is deleted.</returns>
    /// <remarks>
    /// Callback inputs, including writable detoast temporaries, are copied before transfer.
    /// Failure preserves this wrapper's original ownership. Normal function returns and SPI bindings
    /// copy their payload and do not implicitly consume a wrapper shared by managed aliases.
    /// </remarks>
    public PgDatum IntoDatum()
    {
        Validate();
        uint oid = PgTypeRegistry.RequireVarlena<T>().GetOid();
        bool provisional = _allocation is null;
        PgAllocation allocation = _allocation ?? Allocate(Value).Allocation;
        try
        {
            PgMemoryContext context = allocation.Context;
            var lifetime = new PgDatumLifetime(context);
            nuint pointer = (nuint)allocation.DangerousGetPointer();
            var result = new PgDatum(pointer, oid, isNull: false, lifetime);
            _ = allocation.DangerousDetach();
            _allocation = null;
            _disposed = true;
            return result;
        }
        catch (Exception primary)
        {
            if (provisional)
            {
                ReleaseFailedAllocation(allocation, primary);
            }

            throw;
        }
    }

    /// <summary>
    /// Returns the native varlena header address for explicitly unsafe interop.
    /// </summary>
    /// <returns>The currently valid header pointer.</returns>
    /// <remarks>
    /// This pointer can expire or change on copy-on-write. It must not be retained or used to mutate borrowed input.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void* DangerousGetPointer()
    {
        Validate();
        return _allocation is { } allocation ? allocation.DangerousGetPointer() : (void*)_inputHeader;
    }

    /// <summary>
    /// Releases independently owned storage or invalidates an input view without freeing PostgreSQL's input buffer.
    /// </summary>
    /// <remarks>
    /// Failed native release preserves ownership for retry. PostgreSQL context cleanup remains responsible
    /// for forgotten handles; no finalizer calls the backend.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        CheckThread();
        _allocation?.Dispose();
        _allocation = null;
        _disposed = true;
    }

    /// <summary>
    /// Copies the validated representation into an allocator-matched output envelope.
    /// </summary>
    internal NativeValue ToNative(uint oid)
    {
        Validate();
        ReadOnlySpan<byte> bytes = _allocation is { } allocation
            ? new((byte*)allocation.DangerousGetPointer() + _offset, sizeof(T)) : InputBytes;
        return NativeValue.FromCustomPayload(bytes, oid);
    }

    /// <inheritdoc />
    Type IPgVarlena.ManagedType => typeof(T);

    /// <inheritdoc />
    object IPgVarlena.CopyValue() => Value;

    /// <summary>
    /// Borrows an input payload only after the caller validates both its scope and context.
    /// </summary>
    private Span<byte> InputBytes => new((void*)_inputPayload, sizeof(T));

    /// <summary>
    /// Zeroes the layout's padding in a written native payload.
    /// </summary>
    /// <param name="allocation">The payload's allocation.</param>
    /// <param name="offset">The payload's offset in it.</param>
    private static void ClearPadding(PgAllocation allocation, nuint offset)
    {
        int[] padding = PgTypeRegistry.RequireVarlena<T>().Padding;
        if (padding.Length != 0)
        {
            NativeLayoutPadding.Clear(new Span<byte>((byte*)allocation.DangerousGetPointer() + offset, sizeof(T)), padding);
        }
    }

    /// <summary>
    /// Allocates a native header and initializes the payload before publishing ownership.
    /// </summary>
    private (PgAllocation Allocation, nuint Offset) Allocate(T value)
    {
        (PgAllocation allocation, nuint offset) = _context.AllocateVarlena((nuint)sizeof(T));
        try
        {
            allocation.Write(value, offset);
            ClearPadding(allocation, offset);
            return (allocation, offset);
        }
        catch (Exception primary)
        {
            ReleaseFailedAllocation(allocation, primary);
            throw;
        }
    }

    /// <summary>
    /// Preserves initialization and cleanup failures while leaving native context ownership intact.
    /// </summary>
    private static void ReleaseFailedAllocation(PgAllocation allocation, Exception primary)
    {
        try
        {
            allocation.Dispose();
        }
        // codeql[cs/catch-of-all-exceptions] A failed cleanup is reported together with the failure that required it.
        catch (Exception cleanup)
        {
            throw new AggregateException("Initializing a PostgreSQL varlena and releasing its allocation failed.", primary, cleanup);
        }
    }

    /// <summary>
    /// Validates the complete lifetime before exposing an input address or a checked allocation.
    /// </summary>
    private void Validate()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckThread();
        if (_allocation is { } allocation)
        {
            _ = allocation.DangerousGetPointer();
        }
        else
        {
            _scope!.Validate();
            _lifetime!.Validate();
        }
    }

    /// <summary>
    /// Rejects a different managed thread even if it installed a matching native capability.
    /// </summary>
    private void CheckThread()
    {
        if (_thread != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("A PostgreSQL varlena must remain on its originating backend thread.");
        }
    }
}
