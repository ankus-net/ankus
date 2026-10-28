using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Owns stable local storage for a value protected by a PostgreSQL spinlock.
/// </summary>
/// <typeparam name="T">The unmanaged protected value.</typeparam>
/// <remarks>
/// This local owner pins its storage for its managed lifetime. Use PgSpinLockValue inside
/// PgShared for cross-process storage. Construction and access require the owning backend's
/// native callback. Prefer PgLwLock for work longer than a few instructions.
/// </remarks>
public sealed class PgSpinLock<T> where T : unmanaged
{
    private readonly PgSpinLockValue<T>[] _storage;
    private readonly nint _provider;

    /// <summary>
    /// Creates stable local storage and initializes its selected-header native spinlock.
    /// </summary>
    /// <param name="value">The initial protected value.</param>
    public PgSpinLock(T value)
    {
        NativeSpinLockLease.CheckBackendAccess();
        _provider = NativeMemoryContext.Provider;
        _storage = GC.AllocateArray<PgSpinLockValue<T>>(1, pinned: true);
        _storage[0] = new(value);
    }

    /// <summary>
    /// Gets an instantaneous lock-state observation on PostgreSQL versions before 19.
    /// </summary>
    public bool IsLocked
    {
        get
        {
            Validate();
            return _storage[0].Query(_storage);
        }
    }

    /// <summary>
    /// Acquires exclusive access until disposal or callback exit.
    /// </summary>
    /// <returns>The checked guard, which keeps this owner's storage alive.</returns>
    public PgSpinLockGuard<T> Lock()
    {
        Validate();
        return _storage[0].Acquire(_storage);
    }

    /// <summary>
    /// Rejects access outside the owner's active backend capability.
    /// </summary>
    private void Validate()
    {
        NativeMemoryContext.CheckProvider(_provider);
    }
}

/// <summary>
/// Holds one PostgreSQL spinlock acquisition with copied values and scoped original-storage access.
/// </summary>
/// <typeparam name="T">The unmanaged protected value.</typeparam>
public sealed class PgSpinLockGuard<T> : IDisposable where T : unmanaged
{
    private readonly NativeSpinLockLease _lease;

    /// <summary>
    /// Creates the managed guard before its native acquisition begins.
    /// </summary>
    internal PgSpinLockGuard(NativeSpinLockLease lease) => _lease = lease;

    /// <summary>
    /// Gets or immediately replaces the protected value while this guard owns the lock.
    /// </summary>
    public T Value
    {
        get => _lease.Read<T>();
        set => _lease.Write(value);
    }

    /// <summary>
    /// Reads the original protected value through a reference limited to the synchronous callback.
    /// </summary>
    /// <typeparam name="TResult">The owned callback result.</typeparam>
    /// <param name="reader">The reader, whose protected reference cannot escape the callback.</param>
    /// <returns>The callback result.</returns>
    /// <remarks>
    /// Access nested atomic values and spinlocks directly through the readonly reference.
    /// Replacing Value or disposing this guard is rejected until the reader returns. Nested spinlock
    /// guards expire before the read ends, including exceptional exits. Keep the callback short;
    /// PostgreSQL calls remain forbidden while the parent spinlock is held.
    /// </remarks>
    public TResult Read<TResult>(PgSharedReader<T, TResult> reader) => _lease.Read(reader);

    /// <summary>
    /// Mutates the original protected value through a reference limited to the synchronous callback.
    /// </summary>
    /// <typeparam name="TResult">The owned callback result.</typeparam>
    /// <param name="mutator">The update, whose protected reference cannot escape the callback.</param>
    /// <returns>The callback result.</returns>
    /// <remarks>
    /// Writes remain visible if the callback throws. Value can return a copy, but aliases cannot
    /// read the original value, start another mutation, replace Value or dispose this guard until
    /// the callback returns. Nested spinlock access is rejected during mutation; use a separate
    /// Read callback while retaining this guard. PostgreSQL calls remain forbidden while it is held.
    /// </remarks>
    public TResult Mutate<TResult>(PgSharedMutator<T, TResult> mutator) => _lease.Mutate(mutator);

    /// <summary>
    /// Releases the acquisition once; aliases and expired guards cannot release a later owner.
    /// </summary>
    public void Dispose() => _lease.Dispose();
}
