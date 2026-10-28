using System;
using System.ComponentModel;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Owns or borrows a native PostgreSQL ItemPointerData while copying its fields through selected-version headers.
/// </summary>
/// <remarks>
/// Created values own one palloc allocation. Borrowed views never acquire release rights.
/// Checked views share their owner's invalidation; external raw pointers require an explicit lifetime anchor.
/// Dispose owned values deterministically on the backend thread; no finalizer calls PostgreSQL.
/// </remarks>
public sealed unsafe class PgNativeItemPointer : IDisposable
{
    private readonly nint _provider;
    private readonly nint _context;
    private readonly nint _generation;
    private readonly nint _address;
    private readonly AllocationState? _allocation;
    private readonly bool _owned;
    private bool _disposed;

    private PgNativeItemPointer(nint provider, nint context, nint generation, nint address, AllocationState? allocation, bool owned)
    {
        _provider = provider;
        _context = context;
        _generation = generation;
        _address = address;
        _allocation = allocation;
        _owned = owned;
    }

    /// <summary>
    /// Allocates a native item pointer in a selected context and copies both raw fields, including invalid values.
    /// </summary>
    /// <param name="value">The initial location.</param>
    /// <param name="context">The native owner, or null for the current memory context.</param>
    /// <returns>The individually disposable owner.</returns>
    public static PgNativeItemPointer Create(PgItemPointer value, PgMemoryContext? context = null)
    {
        nint owner = (context ?? PgMemoryContext.Current).GetId();
        nint provider = NativeMemoryContext.Provider;
        var allocation = new AllocationState();
        var result = new PgNativeItemPointer(provider, owner, 0, 0, allocation, owned: true);
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.ItemPointer,
            _flags = 1,
            _context = owner,
            _value = checked((nint)value.BlockNumber),
            _length = value.OffsetNumber,
        };
        NativeMemoryContext.Invoke(ref request, out NativeMemoryResult native);
        allocation.Id = native._pointer;
        return result;
    }

    /// <summary>
    /// Borrows an initialized native ItemPointerData without requiring palloc provenance or acquiring ownership.
    /// </summary>
    /// <param name="address">The native pointer, or null.</param>
    /// <param name="lifetimeContext">The explicit reset-sensitive lifetime anchor.</param>
    /// <returns>A borrowed view, or null without accessing the backend for a null address.</returns>
    /// <remarks>
    /// The caller guarantees an accessible ItemPointerData compiled against the selected server headers,
    /// and honors shorter lifetimes such as stack return or an external free. The context need not own the bytes.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public static PgNativeItemPointer? DangerousBorrow(void* address, PgMemoryContext lifetimeContext)
    {
        if (address is null)
        {
            return null;
        }

        ArgumentNullException.ThrowIfNull(lifetimeContext);
        nint context = lifetimeContext.GetId();
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.CaptureGeneration,
            _context = context
        };
        NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
        return new PgNativeItemPointer(NativeMemoryContext.Provider, context, result._value, (nint)address, null, owned: false);
    }

    /// <summary>
    /// Gets or sets a copied location after checking the native allocation or external lifetime anchor.
    /// </summary>
    public PgItemPointer Value
    {
        get
        {
            NativeMemoryResult result = Invoke(2);
            return new PgItemPointer(checked((uint)result._value), checked((ushort)result._length));
        }

        set => Invoke(3, value);
    }

    /// <summary>
    /// Gets the allocation owner or the external pointer's explicit lifetime anchor after validating access.
    /// </summary>
    public PgMemoryContext LifetimeContext
    {
        get
        {
            NativeMemoryResult result = Invoke(2);
            return PgMemoryContext.FromId(_provider, result._context);
        }
    }

    /// <summary>
    /// Creates a borrowed view sharing the current owner's invalidation without acquiring release rights.
    /// </summary>
    /// <returns>The independently disposable borrowed view.</returns>
    public PgNativeItemPointer Borrow()
    {
        _ = Invoke(2);
        return new PgNativeItemPointer(_provider, _context, _generation, _address, _allocation, owned: false);
    }

    /// <summary>
    /// Copies both fields into independent, individually owned native storage.
    /// </summary>
    /// <param name="context">The destination context, or null for the current context.</param>
    /// <returns>The new owner.</returns>
    public PgNativeItemPointer CloneInto(PgMemoryContext? context = null) => Create(Value, context);

    /// <summary>
    /// Returns the current native address without extending its checked or external lifetime.
    /// </summary>
    /// <returns>The native ItemPointerData address.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void* DangerousGetPointer() => (void*)Invoke(2)._pointer;

    /// <summary>
    /// Closes this wrapper and returns its native pointer, transferring individual ownership only when this wrapper owns it.
    /// </summary>
    /// <returns>The native ItemPointerData address.</returns>
    /// <remarks>
    /// Detaching an owner invalidates all its checked borrows and leaves storage for its context or caller to reclaim.
    /// Detaching a borrowed view closes only that view and never grants release rights.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void* DangerousDetach()
    {
        void* pointer = DangerousGetPointer();
        if (_owned)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.Detach,
                _context = _allocation!.Id
            };
            InvokeRequest(ref request);
            _allocation.Id = 0;
        }

        _disposed = true;
        return pointer;
    }

    /// <summary>
    /// Frees individually owned native storage or closes a borrowed view without freeing its referent.
    /// </summary>
    /// <remarks>
    /// Native free failures leave ownership intact for retry. Disposing storage already reclaimed by its context is harmless.
    /// </remarks>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_owned && _allocation!.Id != 0)
        {
            NativeMemoryContext.CheckProvider(_provider);
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.Free,
                _context = _allocation.Id
            };
            InvokeRequest(ref request);
            _allocation.Id = 0;
        }

        _disposed = true;
    }

    private NativeMemoryResult Invoke(int operation, PgItemPointer value = default)
    {
        ObjectDisposedException.ThrowIf(_disposed || _allocation?.Id == 0, this);
        NativeMemoryContext.CheckProvider(_provider);
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.ItemPointer,
            _flags = operation,
            _context = _allocation?.Id ?? _context,
            _other = _generation,
            _pointer = _address,
            _value = checked((nint)value.BlockNumber),
            _length = value.OffsetNumber,
        };
        return InvokeRequest(ref request);
    }

    private static NativeMemoryResult InvokeRequest(ref NativeMemoryRequest request)
    {
        try
        {
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            return result;
        }
        catch (PgException exception) when (exception.SqlState == "55000")
        {
            throw new ObjectDisposedException(nameof(PgNativeItemPointer), "The native item-pointer allocation or lifetime anchor is stale.");
        }
    }

    /// <summary>
    /// Shares an allocation identity so disposing or detaching the owner invalidates every checked borrow.
    /// </summary>
    private sealed class AllocationState
    {
        /// <summary>
        /// Gets or sets the native allocation identity, or zero after ownership ends.
        /// </summary>
        internal nint Id
        {
            get;
            set;
        }
    }
}
