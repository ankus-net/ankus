using System;

namespace Ankus;

/// <summary>
/// Owns a pending outer-transaction callback registration.
/// </summary>
/// <remarks>
/// Disposing the registration cancels its callback without running it. PostgreSQL keeps the callback
/// rooted until it runs, is cancelled, or the current outer transaction ends.
/// </remarks>
public sealed class PgTransactionCallback : IDisposable
{
    private readonly TransactionCallbackRegistry _registry;
    private Action? _callback;

    internal PgTransactionCallback(TransactionCallbackRegistry registry, Action callback)
    {
        _registry = registry;
        _callback = callback;
    }

    /// <summary>
    /// Gets whether the callback is still pending.
    /// </summary>
    public bool IsPending => _callback is not null;

    /// <summary>
    /// Cancels the callback without running it.
    /// </summary>
    public void Dispose()
    {
        if (_callback is not null)
        {
            _registry.Cancel(this);
        }
    }

    /// <summary>
    /// Consumes and returns the pending action.
    /// </summary>
    /// <returns>The action, or null when it was already cancelled or consumed.</returns>
    internal Action? Take()
    {
        Action? callback = _callback;
        _callback = null;
        return callback;
    }

    /// <summary>
    /// Releases the pending action without running it.
    /// </summary>
    internal void Release() => _callback = null;
}
