using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Borrows a copied unmanaged view without acquiring allocation or release rights.
/// </summary>
/// <typeparam name="T">The complete unmanaged representation exposed by the view.</typeparam>
/// <remarks>
/// Checked allocation views observe resizing and share invalidation with their owner. Dangerous raw
/// views use a context as a lifetime anchor; that context is not inferred to own the referenced bytes.
/// The caller must still honor shorter external lifetimes such as stack return or resource closure.
/// </remarks>
public sealed unsafe class PgNativeReference<T> where T : unmanaged
{
    private readonly PgAllocation? _allocation;
    private readonly nuint _offset;
    private readonly nint _provider;
    private readonly nint _context;
    private readonly nint _generation;
    private readonly nint _address;
    private readonly nuint _availableLength;

    /// <summary>
    /// Borrows a complete typed range from an existing checked allocation.
    /// </summary>
    /// <param name="allocation">The shared checked allocation.</param>
    /// <param name="offset">The typed value's byte offset.</param>
    internal PgNativeReference(PgAllocation allocation, nuint offset)
    {
        _allocation = allocation;
        _offset = offset;
    }

    /// <summary>
    /// Borrows an external pointer with an explicit context identity and reset generation anchor.
    /// </summary>
    /// <param name="provider">The native capability provider.</param>
    /// <param name="context">The live lifetime anchor identity.</param>
    /// <param name="generation">The anchor generation captured when borrowing.</param>
    /// <param name="address">The caller-guaranteed initialized raw address.</param>
    /// <param name="availableLength">The caller-guaranteed complete accessible extent.</param>
    internal PgNativeReference(nint provider, nint context, nint generation, nint address, nuint availableLength)
    {
        _provider = provider;
        _context = context;
        _generation = generation;
        _address = address;
        _availableLength = availableLength;
    }

    /// <summary>
    /// Gets or sets a copied unmanaged value after checking its bounds and lifetime anchor.
    /// </summary>
    public T Value
    {
        get
        {
            T value = default;
            ReadBytes(MemoryMarshal.AsBytes(MemoryMarshal.CreateSpan(ref value, 1)));
            return value;
        }

        set
        {
            ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(in value, 1));
            if (_allocation is not null)
            {
                _allocation.Write(bytes, _offset);
                return;
            }

            fixed (byte* data = bytes)
            {
                InvokeRaw(NativeMemoryOperation.WriteReference, (nint)data, (nuint)bytes.Length);
            }
        }
    }

    /// <summary>
    /// Gets the checked allocation owner or the explicitly supplied raw-reference lifetime anchor.
    /// </summary>
    /// <remarks>
    /// For a raw reference this is only a lifetime anchor. It says nothing about which native
    /// allocator owns the memory.
    /// </remarks>
    public PgMemoryContext LifetimeContext
    {
        get
        {
            if (_allocation is not null)
            {
                _allocation.ValidateAccess(_offset, (nuint)sizeof(T));
                return _allocation.Context;
            }

            InvokeRaw(NativeMemoryOperation.ReadReference, 0, 0);
            return PgMemoryContext.FromId(_provider, _context);
        }
    }

    /// <summary>
    /// Copies every representation byte, including padding and pointer fields, into independent context-owned storage.
    /// </summary>
    /// <param name="context">The destination context, or null for the current context.</param>
    /// <returns>A shallow context-owned copy with default allocation policies and alignment.</returns>
    public PgContextValue<T> CloneInto(PgMemoryContext? context = null) => new(CloneAllocation(context));

    /// <summary>
    /// Copies every representation byte, including padding and pointer fields, into independent individually owned storage.
    /// </summary>
    /// <param name="context">The destination context, or null for the current context.</param>
    /// <returns>A shallow individually owned copy with default allocation policies and alignment.</returns>
    public PgNativeBox<T> CloneOwnedInto(PgMemoryContext? context = null) => new(CloneAllocation(context));

    /// <summary>
    /// Returns the current checked native address without extending the allocation or external resource lifetime.
    /// </summary>
    /// <returns>The address of the complete borrowed value.</returns>
    [EditorBrowsable(EditorBrowsableState.Never)]
    public void* DangerousGetPointer()
    {
        if (_allocation is not null)
        {
            _allocation.ValidateAccess(_offset, (nuint)sizeof(T));
            return (byte*)_allocation.DangerousGetPointer() + _offset;
        }

        return (void*)InvokeRaw(NativeMemoryOperation.ReadReference, 0, 0)._pointer;
    }

    /// <summary>
    /// Gets the currently available extent after validating the complete source view and its lifetime.
    /// </summary>
    internal nuint AvailableLength
    {
        get
        {
            if (_allocation is not null)
            {
                _allocation.ValidateAccess(_offset, (nuint)sizeof(T));
                return _allocation.Length - _offset;
            }

            InvokeRaw(NativeMemoryOperation.ReadReference, 0, 0);
            return _availableLength;
        }
    }

    /// <summary>
    /// Reinterprets a complete range without changing its allocation, offset, or original raw lifetime anchor.
    /// </summary>
    /// <typeparam name="TTarget">The complete target representation.</typeparam>
    /// <returns>A borrowed view with the original storage extent and lifetime.</returns>
    /// <remarks>
    /// Callers must establish the target's semantic and ABI compatibility separately.
    /// </remarks>
    internal PgNativeReference<TTarget> Reinterpret<TTarget>() where TTarget : unmanaged
    {
        nuint available = AvailableLength;
        if ((nuint)sizeof(TTarget) > available)
        {
            throw new InvalidCastException("The target representation exceeds the borrowed native storage.");
        }

        return _allocation is not null
            ? new PgNativeReference<TTarget>(_allocation, _offset)
            : new PgNativeReference<TTarget>(_provider, _context, _generation, _address, available);
    }

    /// <summary>
    /// Retains the original storage identity and lifetime in a checked native node operation.
    /// </summary>
    /// <returns>The allocation range or original raw address, extent and reset generation.</returns>
    internal NativeMemoryRequest CreateNodeRequest()
    {
        if (_allocation is not null)
        {
            return _allocation.CreateReferenceRequest(_offset, (nuint)sizeof(T));
        }

        InvokeRaw(NativeMemoryOperation.ReadReference, 0, 0);
        return new NativeMemoryRequest
        {
            _context = _context,
            _other = _generation,
            _pointer = _address,
            _length = _availableLength,
        };
    }

    private void ReadBytes(Span<byte> destination)
    {
        if (_allocation is not null)
        {
            _allocation.Read(destination, _offset);
            return;
        }

        fixed (byte* data = destination)
        {
            InvokeRaw(NativeMemoryOperation.ReadReference, (nint)data, (nuint)destination.Length);
        }
    }

    private PgAllocation CloneAllocation(PgMemoryContext? context)
    {
        byte[] bytes = new byte[sizeof(T)];
        ReadBytes(bytes);
        return (context ?? PgMemoryContext.Current).CopyFrom(bytes);
    }

    private NativeMemoryResult InvokeRaw(NativeMemoryOperation operation, nint data, nuint length)
    {
        NativeMemoryContext.CheckProvider(_provider);
        NativeMemoryRequest request = new()
        {
            _operation = operation,
            _context = _context,
            _other = _generation,
            _pointer = _address,
            _data = data,
            _length = length,
        };
        try
        {
            NativeMemoryContext.Invoke(ref request, out NativeMemoryResult result);
            return result;
        }
        catch (PgException exception) when (exception.SqlState == "55000")
        {
            throw new ObjectDisposedException(nameof(PgNativeReference<>), "The PostgreSQL reference lifetime anchor has been reset or deleted.");
        }
    }
}
