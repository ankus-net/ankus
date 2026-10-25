namespace Ankus.CompilerServices;

/// <summary>
/// Holds the backend state that every raw native call checks, so a call finds all of it with one thread-static read.
/// </summary>
/// <remarks>
/// Each thread-static field costs a lookup of its own, and a raw call used to make four of them before it entered
/// PostgreSQL. The callback's capability, the innermost failed scope and the held lock leases now share this object.
/// </remarks>
internal sealed class NativeThread
{
    [ThreadStatic]
    private static NativeThread? s_current;

    /// <summary>
    /// The active callback's native capability, or zero outside a callback.
    /// </summary>
    internal nint _api;

    /// <summary>
    /// How many generated callbacks are active on this thread.
    /// </summary>
    internal int _depth;

    /// <summary>
    /// The lease for buffers retained until the innermost callback exits.
    /// </summary>
    internal NativeBorrowScope? _borrowScope;

    /// <summary>
    /// The innermost guarded scope, whose failure blocks further SQL and raw native work.
    /// </summary>
    internal NativeSubtransaction.Invocation? _invocation;

    /// <summary>
    /// The most recently acquired spinlock guard that is still held.
    /// </summary>
    internal NativeSpinLockLease? _spinLease;

    /// <summary>
    /// How many scoped lightweight-lock accesses are in progress.
    /// </summary>
    internal int _sharedBorrows;

    /// <summary>
    /// Gets this thread's state, or null before the thread first enters a callback or takes a lease.
    /// </summary>
    internal static NativeThread? Current => s_current;

    /// <summary>
    /// Gets this thread's state, creating it on first use.
    /// </summary>
    internal static NativeThread Get() => s_current ??= new NativeThread();

    /// <summary>
    /// Gets whether a raw call may enter PostgreSQL without the checks that would otherwise refuse it: no guarded scope
    /// has failed and no spinlock guard or lightweight-lock access is held.
    /// </summary>
    internal bool AdmitsBackendCalls => _invocation?.Failure is null && _spinLease is null && _sharedBorrows == 0;
}
