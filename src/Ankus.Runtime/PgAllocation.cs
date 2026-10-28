using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Owns one PostgreSQL palloc-family allocation and validates it before each managed access.
/// </summary>
/// <remarks>
/// Operations follow the native allocator's capabilities. Slab contexts require their fixed chunk
/// size; Bump contexts support allocation and context cleanup but reject individual free and resize.
/// Failed free or resize leaves the checked allocation live until its context reclaims it.
/// </remarks>
public sealed unsafe class PgAllocation : IDisposable
{
    private readonly nint _provider;
    private nint _id;
    private nuint _length;

    /// <summary>
    /// Wraps a checked allocation registered by the native provider.
    /// </summary>
    /// <param name="provider">The native extension provider.</param>
    /// <param name="id">The allocation identity.</param>
    /// <param name="length">The allocation byte length.</param>
    /// <param name="options">The allocation's original initialization and native size policies.</param>
    /// <param name="alignment">The original explicit alignment, or zero for PostgreSQL's default.</param>
    internal PgAllocation(nint provider, nint id, nuint length, PgAllocationOptions options = PgAllocationOptions.None, nuint alignment = 0)
    {
        _provider = provider;
        _id = id;
        _length = length;
        Options = options;
        Alignment = alignment;
    }

    /// <summary>
    /// Gets the allocation's byte length.
    /// </summary>
    public nuint Length => _length;

    /// <summary>
    /// Gets the original initialization and native size policies, retained through resizing.
    /// </summary>
    public PgAllocationOptions Options { get; }

    /// <summary>
    /// Gets the original explicit alignment, or zero for PostgreSQL's default alignment.
    /// </summary>
    public nuint Alignment { get; }

    /// <summary>
    /// Gets the owning context while the allocation remains live.
    /// </summary>
    public PgMemoryContext Context
    {
        get
        {
            EnsureLive();
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.Owner,
                _context = _id
            };
            Invoke(ref request, out NativeMemoryResult result);
            return PgMemoryContext.FromId(_provider, result._context);
        }
    }

    /// <summary>
    /// Copies bytes from this allocation into managed memory.
    /// </summary>
    /// <param name="destination">The destination span.</param>
    /// <param name="offset">The allocation byte offset.</param>
    public void Read(Span<byte> destination, nuint offset = 0)
    {
        EnsureRange(offset, (nuint)destination.Length);
        fixed (byte* target = destination)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.Read,
                _context = _id,
                _data = (nint)target,
                _length = (nuint)destination.Length,
                _value = (nint)offset,
            };
            Invoke(ref request);
        }
    }

    /// <summary>
    /// Copies managed bytes into this allocation.
    /// </summary>
    /// <param name="source">The source bytes.</param>
    /// <param name="offset">The allocation byte offset.</param>
    public void Write(ReadOnlySpan<byte> source, nuint offset = 0)
    {
        EnsureRange(offset, (nuint)source.Length);
        fixed (byte* sourcePointer = source)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.Write,
                _context = _id,
                _data = (nint)sourcePointer,
                _length = (nuint)source.Length,
                _value = (nint)offset,
            };
            Invoke(ref request);
        }
    }

    /// <summary>
    /// Reads one unmanaged value from the allocation.
    /// </summary>
    /// <typeparam name="T">The unmanaged value type.</typeparam>
    /// <param name="offset">The allocation byte offset.</param>
    /// <returns>The copied value.</returns>
    public T Read<T>(nuint offset = 0) where T : unmanaged
    {
        T value = default;
        Read(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)), offset);
        return value;
    }

    /// <summary>
    /// Borrows a typed view whose bounds and shared allocation lifetime are checked on every access.
    /// </summary>
    /// <typeparam name="T">The unmanaged value type.</typeparam>
    /// <param name="offset">The allocation byte offset of the complete value.</param>
    /// <returns>A view without ownership or individual release rights.</returns>
    /// <remarks>
    /// Resizing is observed by existing views. Freeing, detaching, or resetting the allocation
    /// invalidates its views; no native pointer is cached by this checked borrow.
    /// </remarks>
    public PgNativeReference<T> Borrow<T>(nuint offset = 0) where T : unmanaged
    {
        ValidateAccess(offset, (nuint)sizeof(T));
        return new PgNativeReference<T>(this, offset);
    }

    /// <summary>
    /// Writes one unmanaged value to the allocation.
    /// </summary>
    /// <typeparam name="T">The unmanaged value type.</typeparam>
    /// <param name="value">The value to copy.</param>
    /// <param name="offset">The allocation byte offset.</param>
    public void Write<T>(T value, nuint offset = 0) where T : unmanaged
    {
        Write(MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in value, 1)), offset);
    }

    /// <summary>
    /// Clears the allocation or a selected byte range.
    /// </summary>
    /// <param name="offset">The allocation byte offset.</param>
    /// <param name="length">The number of bytes to clear, or zero to clear from the offset through the end.</param>
    public void Clear(nuint offset = 0, nuint length = 0)
    {
        if (length == 0)
        {
            length = _length - offset;
        }

        EnsureRange(offset, length);
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.Clear,
            _context = _id,
            _length = length,
            _value = (nint)offset,
        };
        Invoke(ref request);
    }

    /// <summary>
    /// Resizes this allocation using PostgreSQL's matching context allocator.
    /// </summary>
    /// <param name="length">The new byte length.</param>
    /// <param name="zeroNewMemory">Whether to clear only the newly added bytes when growing.</param>
    /// <remarks>
    /// The original huge size policy and alignment remain in effect. Shrinking is permitted even
    /// when zeroNewMemory is true; unlike PostgreSQL's repalloc0, this clears only positive growth.
    /// Slab permits only its fixed size. Bump rejects resizing, preserving the original allocation.
    /// </remarks>
    public void Reallocate(nuint length, bool zeroNewMemory = false)
    {
        EnsureLive();
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.Reallocate,
            _context = _id,
            _length = length,
            _flags = zeroNewMemory ? 1 : 0,
        };
        Invoke(ref request, out NativeMemoryResult result);
        _id = result._context;
        _length = result._length;
    }

    /// <summary>
    /// Attempts resizing without raising an out-of-memory error, preserving the old allocation on failure.
    /// </summary>
    /// <param name="length">The new byte length.</param>
    /// <param name="zeroNewMemory">Whether to clear only the newly added bytes when growing.</param>
    /// <returns>True after resizing, or false for allocator exhaustion; other native errors still throw.</returns>
    public bool TryReallocate(nuint length, bool zeroNewMemory = false)
    {
        EnsureLive();
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.Reallocate,
            _context = _id,
            _length = length,
            _flags = (zeroNewMemory ? 1 : 0) | 2,
        };
        Invoke(ref request, out NativeMemoryResult result);
        if (result._pointer == 0)
        {
            return false;
        }

        _id = result._context;
        _length = result._length;
        return true;
    }

    /// <summary>
    /// Transfers this allocation to raw native ownership without freeing its storage.
    /// </summary>
    /// <returns>The live palloc-compatible pointer now owned by the caller or its native context.</returns>
    /// <remarks>
    /// This consumes the checked handle. The caller is responsible for native lifetime and must not
    /// use the raw pointer after context reset or deletion. Failed transfer leaves this handle owned.
    /// </remarks>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void* DangerousDetach()
    {
        EnsureLive();
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.Detach,
            _context = _id
        };
        Invoke(ref request, out NativeMemoryResult result);
        _id = 0;
        _length = 0;
        return (void*)result._pointer;
    }

    /// <summary>
    /// Returns the raw palloc pointer for explicitly unsafe interop.
    /// </summary>
    /// <returns>The native allocation pointer.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void* DangerousGetPointer()
    {
        EnsureLive();
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.Read,
            _context = _id
        };
        Invoke(ref request, out NativeMemoryResult result);
        return (void*)result._pointer;
    }

    /// <summary>
    /// Releases the allocation immediately. Context reset or deletion makes this operation a no-op.
    /// </summary>
    /// <remarks>
    /// Bump contexts reject individual release. A failed release preserves this handle and its
    /// storage; the native context still reclaims that storage on reset or deletion.
    /// </remarks>
    public void Dispose()
    {
        if (_id == 0)
        {
            return;
        }

        NativeMemoryContext.CheckProvider(_provider);
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.Free,
            _context = _id
        };
        NativeMemoryContext.Invoke(ref request, out _);
        _id = 0;
        _length = 0;
    }

    /// <summary>
    /// Validates a complete managed view and native identity without copying allocation bytes.
    /// </summary>
    /// <param name="offset">The first view byte.</param>
    /// <param name="length">The full accessible view length.</param>
    internal void ValidateAccess(nuint offset, nuint length)
    {
        EnsureRange(offset, length);
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.Read,
            _context = _id,
            _value = (nint)offset,
        };
        Invoke(ref request);
    }

    /// <summary>
    /// Projects a current checked allocation range into a native operation without caching its address.
    /// </summary>
    /// <param name="offset">The first view byte.</param>
    /// <param name="length">The complete source view size.</param>
    /// <returns>The allocation identity, offset and currently accessible extent.</returns>
    internal NativeMemoryRequest CreateReferenceRequest(nuint offset, nuint length)
    {
        ValidateAccess(offset, length);
        return new NativeMemoryRequest { _context = _id, _value = (nint)offset, _length = _length - offset };
    }

    private void EnsureLive()
    {
        ObjectDisposedException.ThrowIf(_id == 0, nameof(PgAllocation));

        NativeMemoryContext.CheckProvider(_provider);
    }

    private void EnsureRange(nuint offset, nuint length)
    {
        EnsureLive();
        if (offset > _length || length > _length - offset)
        {
            throw new ArgumentOutOfRangeException(nameof(length), "The requested range exceeds the native allocation.");
        }
    }

    private static void Invoke(ref NativeMemoryRequest request)
    {
        try
        {
            NativeMemoryContext.Invoke(ref request, out _);
        }
        catch (PgException exception) when (exception.SqlState == "55000")
        {
            throw new ObjectDisposedException(nameof(PgAllocation), "The PostgreSQL allocation has been reset, freed, or deleted.");
        }
    }

    private static void Invoke(ref NativeMemoryRequest request, out NativeMemoryResult result)
    {
        try
        {
            NativeMemoryContext.Invoke(ref request, out result);
        }
        catch (PgException exception) when (exception.SqlState == "55000")
        {
            throw new ObjectDisposedException(nameof(PgAllocation), "The PostgreSQL allocation has been reset, freed, or deleted.");
        }
    }
}
