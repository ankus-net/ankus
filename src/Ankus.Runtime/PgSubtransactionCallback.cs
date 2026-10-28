using System;

namespace Ankus;

/// <summary>
/// Owns a pending subtransaction callback registration.
/// </summary>
/// <remarks>
/// A pending callback can run for several subtransactions in the current outer transaction.
/// Disposing the registration cancels future invocations without running the callback.
/// </remarks>
public sealed class PgSubtransactionCallback : IDisposable
{
    private readonly TransactionCallbackRegistry _registry;
    private Action<PgSubtransactionId, PgSubtransactionId>? _callback;

    internal PgSubtransactionCallback(
        TransactionCallbackRegistry registry,
        Action<PgSubtransactionId, PgSubtransactionId> callback)
    {
        _registry = registry;
        _callback = callback;
    }

    /// <summary>
    /// Gets whether the callback remains registered.
    /// </summary>
    public bool IsPending => _callback is not null;

    /// <summary>
    /// Cancels future callback invocations.
    /// </summary>
    public void Dispose()
    {
        if (_callback is not null)
        {
            _registry.Cancel(this);
        }
    }

    /// <summary>
    /// Gets the pending action without consuming a repeating registration.
    /// </summary>
    /// <returns>The action, or null when the registration was cancelled or released.</returns>
    internal Action<PgSubtransactionId, PgSubtransactionId>? Get() => _callback;

    /// <summary>
    /// Releases the pending action without running it.
    /// </summary>
    internal void Release() => _callback = null;
}
