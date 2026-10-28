using System.Collections.Generic;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Roots managed callbacks for one PostgreSQL outer transaction and enforces backend-thread ownership.
/// </summary>
/// <param name="owner">The guarded backend binding that owns registrations outside callback dispatch.</param>
internal sealed class TransactionCallbackRegistry(nint owner)
{
    /// <summary>
    /// Gets the event-indexed one-shot outer-transaction callbacks.
    /// </summary>
    internal List<PgTransactionCallback>?[] TransactionCallbacks { get; } = new List<PgTransactionCallback>?[8];

    /// <summary>
    /// Gets the event-indexed repeating subtransaction callbacks.
    /// </summary>
    internal List<PgSubtransactionCallback>?[] SubtransactionCallbacks { get; } = new List<PgSubtransactionCallback>?[4];

    /// <summary>
    /// Gets or sets whether the native outer-transaction dispatcher is installed.
    /// </summary>
    internal bool HasTransactionDispatcher
    {
        get;
        set;
    }

    /// <summary>
    /// Gets or sets whether the native subtransaction dispatcher is installed.
    /// </summary>
    internal bool HasSubtransactionDispatcher
    {
        get;
        set;
    }

    /// <summary>
    /// Gets whether this registry still represents the active outer transaction.
    /// </summary>
    internal bool IsActive
    {
        get;
        private set;
    } = true;

    /// <summary>
    /// Cancels an outer-transaction callback on its owning backend thread.
    /// </summary>
    /// <param name="callback">The pending registration.</param>
    internal void Cancel(PgTransactionCallback callback)
    {
        NativeTransactionCallbacks.CheckCancellationAccess(this, owner);
        callback.Release();
    }

    /// <summary>
    /// Cancels a subtransaction callback on its owning backend thread.
    /// </summary>
    /// <param name="callback">The pending registration.</param>
    internal void Cancel(PgSubtransactionCallback callback)
    {
        NativeTransactionCallbacks.CheckCancellationAccess(this, owner);
        callback.Release();
    }

    /// <summary>
    /// Prevents further cancellation after the outer transaction has ended.
    /// </summary>
    internal void Deactivate() => IsActive = false;
}
