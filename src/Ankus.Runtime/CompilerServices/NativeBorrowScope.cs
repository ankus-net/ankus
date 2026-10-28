using System;
using System.Collections.Generic;

namespace Ankus.CompilerServices;

/// <summary>
/// Owns callback input lifetimes and releases callback-scoped shared-memory lock leases.
/// </summary>
internal sealed class NativeBorrowScope(nint provider, int depth, NativeBorrowScope? parent)
{
    private readonly int _thread = Environment.CurrentManagedThreadId;
    private bool _alive = true;
    private List<NativeSharedMemoryLease>? _locks;
    private List<NativeSpinLockLease>? _spinLocks;
    private List<NativeBackgroundWorkerLease>? _workers;
    private List<PgArrayView>? _arrays;
    private List<NativeBorrowedBuffer>? _buffers;
    private List<PgErrorContext>? _errorContexts;

    /// <summary>
    /// Gets the nesting depth that owns this unique lease.
    /// </summary>
    internal int Depth { get; } = depth;

    /// <summary>
    /// Gets the still-active enclosing lease, if one was captured.
    /// </summary>
    internal NativeBorrowScope? Parent { get; } = parent;

    /// <summary>
    /// Prevents backend operations from invalidating active lock-protected references.
    /// </summary>
    internal static void CheckBackendAccess()
    {
        NativeSpinLockLease.CheckBackendAccess();
        NativeSharedMemoryLease.CheckBackendAccess();
    }

    /// <summary>
    /// Releases remaining lock leases before invalidating the callback's borrowed views.
    /// </summary>
    internal void Expire()
    {
        if (_spinLocks is not null)
        {
            for (int index = _spinLocks.Count - 1; index >= 0; index--)
            {
                _spinLocks[index].Expire();
            }

            _spinLocks = null;
        }

        if (_locks is not null)
        {
            for (int index = _locks.Count - 1; index >= 0; index--)
            {
                _locks[index].Dispose();
            }

            _locks = null;
        }

        if (_workers is not null)
        {
            for (int index = _workers.Count - 1; index >= 0; index--)
            {
                _workers[index].Dispose();
            }

            _workers = null;
        }

        while (_arrays is { Count: > 0 })
        {
            _arrays[^1].Dispose();
        }

        while (_buffers is { Count: > 0 })
        {
            _buffers[^1].Dispose();
        }

        // A context the callback leaves open would otherwise describe later, unrelated errors.
        while (_errorContexts is { Count: > 0 })
        {
            _errorContexts[^1].Dispose();
        }

        _alive = false;
    }

    /// <summary>
    /// Retains a borrowed input view so callback exit releases forgotten native cursor and detoast storage.
    /// </summary>
    internal void Register(PgArrayView view) => (_arrays ??= []).Add(view);

    /// <summary>
    /// Stops retaining a view after successful explicit or callback cleanup.
    /// </summary>
    internal void Unregister(PgArrayView view) => _arrays?.Remove(view);

    /// <summary>
    /// Retains an error context until explicit disposal or callback exit.
    /// </summary>
    internal void Register(PgErrorContext context) => (_errorContexts ??= []).Add(context);

    /// <summary>
    /// Stops retaining a popped error context.
    /// </summary>
    internal void Unregister(PgErrorContext context) => _errorContexts?.Remove(context);

    /// <summary>
    /// Retains private buffer storage until explicit disposal or callback exit.
    /// </summary>
    internal void Register(NativeBorrowedBuffer buffer) => (_buffers ??= []).Add(buffer);

    /// <summary>
    /// Stops retaining buffer storage after successful cleanup.
    /// </summary>
    internal void Unregister(NativeBorrowedBuffer buffer) => _buffers?.Remove(buffer);

    /// <summary>
    /// Retains a lock lease before acquisition so callback exit releases forgotten guards.
    /// </summary>
    /// <param name="lease">The not-yet-acquired lease.</param>
    internal void Register(NativeSharedMemoryLease lease) => (_locks ??= []).Add(lease);

    /// <summary>
    /// Stops retaining a released or unacquired lease while its callback remains alive.
    /// </summary>
    /// <param name="lease">The lease whose native ownership has ended.</param>
    internal void Unregister(NativeSharedMemoryLease lease) => _locks?.Remove(lease);

    /// <summary>
    /// Retains a prepared spinlock lease before native acquisition begins.
    /// </summary>
    internal void Register(NativeSpinLockLease lease) => (_spinLocks ??= []).Add(lease);

    /// <summary>
    /// Stops retaining a released or failed spinlock acquisition.
    /// </summary>
    internal void Unregister(NativeSpinLockLease lease) => _spinLocks?.Remove(lease);

    /// <summary>
    /// Retains a prepared worker handle so callback exit releases forgotten observation storage.
    /// </summary>
    internal void Register(NativeBackgroundWorkerLease lease) => (_workers ??= []).Add(lease);

    /// <summary>
    /// Stops retaining a released or unsuccessful worker registration handle.
    /// </summary>
    internal void Unregister(NativeBackgroundWorkerLease lease) => _workers?.Remove(lease);

    /// <summary>
    /// Rejects expired, foreign-thread or foreign-provider access before touching input storage.
    /// </summary>
    internal void Validate()
    {
        ObjectDisposedException.ThrowIf(!_alive, this);
        if (_thread != Environment.CurrentManagedThreadId)
        {
            throw new InvalidOperationException("A PostgreSQL input view must remain on its originating backend thread.");
        }

        NativeMemoryContext.CheckProvider(provider);
    }
}
