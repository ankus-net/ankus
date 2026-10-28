using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Embeds a PostgreSQL spinlock and unmanaged value inside a shared aggregate.
/// </summary>
/// <typeparam name="T">The unmanaged value protected by the spinlock.</typeparam>
/// <remarks>
/// Construct this field in the shared initializer and access it directly through the reference
/// supplied by the owning shared value's or lock guard's Read callback. Default values and detached copies cannot be locked. Use
/// PgSpinLock for ordinary local storage. Keep critical sections short and synchronous.
/// </remarks>
[StructLayout(LayoutKind.Sequential)]
public readonly unsafe struct PgSpinLockValue<T> where T : unmanaged
{
    private readonly long _storage;
    private readonly long _format;
    private readonly T _value;

    /// <summary>
    /// Initializes the selected PostgreSQL lock representation and the protected value.
    /// </summary>
    /// <param name="value">The initial value.</param>
    public PgSpinLockValue(T value)
    {
        _storage = 0;
        _format = 0;
        _value = value;
        fixed (long* address = &Unsafe.AsRef(in _storage))
        {
            _format = NativeSpinLockLease.Initialize((nint)address);
        }
    }

    /// <summary>
    /// Gets whether the lock is currently held, on PostgreSQL versions before 19.
    /// </summary>
    /// <remarks>
    /// This is an instantaneous observation. It does not grant access to the protected value.
    /// PostgreSQL 19 and later throw NotSupportedException because SpinLockFree was removed.
    /// </remarks>
    public bool IsLocked => Query(null);

    /// <summary>
    /// Acquires an exclusive guard within the current admitted shared read.
    /// </summary>
    /// <returns>A guard released by disposal, shared-read exit or callback exit.</returns>
    public PgSpinLockGuard<T> Lock() => Acquire(null);

    /// <summary>
    /// Acquires verified native shared storage or the local owner's permanently pinned cell.
    /// </summary>
    internal PgSpinLockGuard<T> Acquire(object? owner)
    {
        fixed (PgSpinLockValue<T>* address = &Unsafe.AsRef(in this))
        {
            nint frame = Validate(address, owner);
            var lease = new NativeSpinLockLease((nint)address, (nint)(&address->_value), frame, owner);
            var guard = new PgSpinLockGuard<T>(lease);
            lease.Acquire();
            return guard;
        }
    }

    /// <summary>
    /// Queries a verified native or pinned cell without inspecting the lock's opaque bytes.
    /// </summary>
    internal bool Query(object? owner)
    {
        fixed (PgSpinLockValue<T>* address = &Unsafe.AsRef(in this))
        {
            _ = Validate(address, owner);
            if ((_format & 1) == 0)
            {
                throw new NotSupportedException("PostgreSQL 19 and later do not expose spinlock state queries.");
            }

            bool result = NativeSpinLockLease.Query((nint)address);
            GC.KeepAlive(owner);
            return result;
        }
    }

    /// <summary>
    /// Rejects default, unaligned and detached inline storage before invoking native code.
    /// </summary>
    private nint Validate(PgSpinLockValue<T>* address, object? owner)
    {
        if ((_format & ~1L) != NativeSpinLockLease.Format || (nuint)address % sizeof(long) != 0)
        {
            throw new InvalidOperationException("A PostgreSQL spinlock requires explicitly initialized, eight-byte aligned storage.");
        }

        return owner is null ? NativeSharedReadScope.Find((nint)address, (nuint)sizeof(PgSpinLockValue<T>)) : 0;
    }
}
