using System;
using System.Runtime.CompilerServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Locates a shared array's header and elements inside its admitted storage.
/// </summary>
internal static class NativeSharedArray
{
    /// <summary>
    /// Rejects an index outside the array.
    /// </summary>
    /// <param name="index">The zero-based index.</param>
    /// <param name="length">The number of elements.</param>
    /// <returns>The index.</returns>
    internal static int Check(int index, int length)
        => (uint)index < (uint)length ? index
            : throw new ArgumentOutOfRangeException(nameof(index), index, $"The index must be at least zero and less than the length, {length}.");
}

/// <summary>
/// Copies a shared array's header out, after writing it when <paramref name="write"/> is set.
/// </summary>
/// <param name="value">The header to write.</param>
/// <param name="write">Whether to write before reading.</param>
internal readonly unsafe struct NativeSharedArrayHeader<THeader>(THeader value, bool write) : INativeSharedBlock<THeader> where THeader : unmanaged
{
    /// <inheritdoc />
    public THeader Invoke(nint address)
    {
        if (write)
        {
            Unsafe.WriteUnaligned((void*)address, value);
        }

        return Unsafe.ReadUnaligned<THeader>((void*)address);
    }
}

/// <summary>
/// Copies one element out, after writing it when <paramref name="write"/> is set.
/// </summary>
/// <param name="index">The checked element index.</param>
/// <param name="value">The element to write.</param>
/// <param name="write">Whether to write before reading.</param>
internal readonly unsafe struct NativeSharedArrayElement<THeader, TElement>(int index, TElement value, bool write) : INativeSharedBlock<TElement>
    where THeader : unmanaged where TElement : unmanaged
{
    /// <inheritdoc />
    public TElement Invoke(nint address)
    {
        void* element = (byte*)address + PgLwLock<THeader, TElement>.ElementOffset + ((nint)index * sizeof(TElement));
        if (write)
        {
            Unsafe.WriteUnaligned(element, value);
        }

        return Unsafe.ReadUnaligned<TElement>(element);
    }
}

/// <summary>
/// Passes a shared array to a reader as a readonly header reference and element span.
/// </summary>
/// <param name="reader">The reader.</param>
/// <param name="length">The number of elements.</param>
internal readonly unsafe struct NativeSharedArrayReader<THeader, TElement, TResult>(PgSharedArrayReader<THeader, TElement, TResult> reader, int length)
    : INativeSharedBlock<TResult> where THeader : unmanaged where TElement : unmanaged
{
    /// <inheritdoc />
    public TResult Invoke(nint address)
        => reader(in Unsafe.AsRef<THeader>((void*)address),
            new ReadOnlySpan<TElement>((byte*)address + PgLwLock<THeader, TElement>.ElementOffset, length));
}

/// <summary>
/// Passes a shared array to a mutator as a mutable header reference and element span.
/// </summary>
/// <param name="mutator">The mutator.</param>
/// <param name="length">The number of elements.</param>
internal readonly unsafe struct NativeSharedArrayMutator<THeader, TElement, TResult>(PgSharedArrayMutator<THeader, TElement, TResult> mutator, int length)
    : INativeSharedBlock<TResult> where THeader : unmanaged where TElement : unmanaged
{
    /// <inheritdoc />
    public TResult Invoke(nint address)
        => mutator(ref Unsafe.AsRef<THeader>((void*)address),
            new Span<TElement>((byte*)address + PgLwLock<THeader, TElement>.ElementOffset, length));
}
