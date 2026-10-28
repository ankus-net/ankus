using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Embeds one atomic scalar in an unmanaged shared aggregate.
/// </summary>
/// <typeparam name="T">A primitive or enum type supported by Interlocked.</typeparam>
/// <remarks>
/// Operate directly on a field of the readonly reference supplied by a shared reader. Copying this
/// value snapshots its current storage into an independent value. Each value occupies eight bytes and must be
/// eight-byte aligned; packed unaligned layouts are rejected. Operations use Interlocked ordering.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly struct PgAtomicValue<T> where T : unmanaged
{
    private readonly long _storage;

    /// <summary>
    /// Creates an inline atomic scalar with the supplied bits.
    /// </summary>
    /// <param name="value">The initial scalar.</param>
    public PgAtomicValue(T value)
    {
        NativeAtomicScalar.Validate<T>();
        _storage = 0;
        Unsafe.As<long, T>(ref _storage) = value;
    }

    /// <summary>
    /// Gets the scalar atomically without copying its containing storage.
    /// </summary>
    public T Value => Interlocked.CompareExchange(ref GetReference(), default, default);

    /// <summary>
    /// Atomically replaces the scalar and returns its previous value.
    /// </summary>
    /// <param name="value">The replacement value.</param>
    /// <returns>The value before replacement.</returns>
    public T Exchange(T value) => Interlocked.Exchange(ref GetReference(), value);

    /// <summary>
    /// Replaces the scalar when its bits match the comparand and returns its previous value.
    /// </summary>
    /// <param name="value">The replacement value when comparison succeeds.</param>
    /// <param name="comparand">The exact required bit pattern.</param>
    /// <returns>The value before comparison, whether or not replacement occurred.</returns>
    public T CompareExchange(T value, T comparand) => Interlocked.CompareExchange(ref GetReference(), value, comparand);

    /// <summary>
    /// Gets aligned inline storage only for the duration of the caller's operation.
    /// </summary>
    /// <returns>The supported scalar storage.</returns>
    internal unsafe ref T GetReference()
    {
        NativeAtomicScalar.Validate<T>();
        ref long storage = ref Unsafe.AsRef(in _storage);
        if ((nuint)Unsafe.AsPointer(ref storage) % sizeof(long) != 0)
        {
            throw new InvalidOperationException("Inline PostgreSQL atomic values require eight-byte alignment.");
        }

        return ref Unsafe.As<long, T>(ref storage);
    }
}
