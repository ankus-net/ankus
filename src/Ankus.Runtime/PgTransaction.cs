namespace Ankus;

/// <summary>
/// Runs recoverable work and registers callbacks for the current PostgreSQL transaction.
/// </summary>
public static class PgTransaction
{
    /// <summary>
    /// Runs synchronous work with SPI in the current transaction, or else in a new transaction that commits when the
    /// work returns.
    /// </summary>
    /// <param name="action">The work to run on the active backend thread.</param>
    /// <remarks>
    /// PostgreSQL calls some extension code outside a transaction, such as an output plugin's startup callback when a
    /// WAL sender starts streaming, and the same code inside one elsewhere. A new transaction needs a backend
    /// connected to a database; a failure rolls it back before the exception returns. Inside a transaction, the work
    /// runs as it would without this call.
    /// </remarks>
    public static void Run(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = Run(() =>
        {
            action();
            return true;
        });
    }

    /// <summary>
    /// Runs synchronous work with SPI in the current transaction, or else in a new transaction that commits when the
    /// work returns, and returns its result.
    /// </summary>
    /// <typeparam name="TResult">The owned result.</typeparam>
    /// <param name="action">The work to run on the active backend thread.</param>
    /// <returns>The work's result, after commit when this call started the transaction.</returns>
    /// <remarks>
    /// A new transaction needs a backend connected to a database; a failure rolls it back before the exception
    /// returns. Inside a transaction, the work runs as it would without this call.
    /// </remarks>
    public static TResult Run<TResult>(Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(action);
        return NativeBackend.HasBinding ? action() : NativeBackgroundWorker.RunBackendTransaction(action);
    }

    /// <summary>
    /// Runs synchronous work in an internal subtransaction, rolling it back if the callback fails.
    /// </summary>
    /// <param name="action">The work to run on the active backend thread.</param>
    /// <remarks>
    /// Catch failures outside this callback, after native resources have been recovered.
    /// An unrecovered native error, including a value-operation error, prevents further backend work in this scope, even if caught.
    /// Success retains changes in the enclosing transaction; it does not commit that transaction.
    /// The callback must not perform transaction control or asynchronous work. This operation
    /// is unavailable during transaction callbacks and abort cleanup. Internal guard
    /// subtransactions do not invoke consumer subtransaction callbacks.
    /// </remarks>
    public static void RunInSubtransaction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeSubtransaction.Run(() =>
        {
            action();
            return 0;
        });
    }

    /// <summary>
    /// Runs synchronous work in an internal subtransaction and returns its result after successful release.
    /// </summary>
    /// <typeparam name="TResult">The callback result type.</typeparam>
    /// <param name="action">The synchronous work to run on the active backend thread.</param>
    /// <returns>The result after the subtransaction succeeds.</returns>
    /// <remarks>
    /// Failures roll back this scope before propagating to the caller. Catch native errors outside the callback;
    /// catching one inside cannot turn a failed scope into a successful one.
    /// Nested scopes may recover independently. Native results retain PostgreSQL's memory-context
    /// lifetimes; rollback invalidates allocations and resources owned by the aborted scope.
    /// Cleanup aggregates that contain the original native failure retain all their causes after rollback.
    /// Do not perform transaction control or asynchronous work in the callback. This operation
    /// is unavailable during transaction callbacks and abort cleanup.
    /// </remarks>
    public static TResult RunInSubtransaction<TResult>(Func<TResult> action) => NativeSubtransaction.Run(action);

    /// <summary>
    /// Runs synchronous work in an internal subtransaction with the selected statement recovery.
    /// </summary>
    /// <param name="action">The work to run on the active backend thread.</param>
    /// <param name="mode">Whether statements recover individually or share the scope's subtransaction.</param>
    /// <remarks>
    /// <see cref="PgSubtransactionMode.Atomic"/> runs every statement in the scope's own subtransaction, as SPI does in
    /// pgrx, so a bulk write consumes one subtransaction ID. The first PostgreSQL error makes later backend calls in the
    /// scope rethrow it, then rolls back the whole scope before propagating; catch it outside the callback. A nested
    /// recoverable scope restores per-statement recovery for its own statements. The other rules of
    /// <see cref="RunInSubtransaction(Action)"/> apply.
    /// </remarks>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is not defined.</exception>
    public static void RunInSubtransaction(Action action, PgSubtransactionMode mode)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = RunInSubtransaction(() =>
        {
            action();
            return 0;
        }, mode);
    }

    /// <summary>
    /// Runs synchronous work in an internal subtransaction with the selected statement recovery and returns its result.
    /// </summary>
    /// <typeparam name="TResult">The callback result type.</typeparam>
    /// <param name="action">The synchronous work to run on the active backend thread.</param>
    /// <param name="mode">Whether statements recover individually or share the scope's subtransaction.</param>
    /// <returns>The result after the subtransaction succeeds.</returns>
    /// <remarks>See <see cref="RunInSubtransaction(Action, PgSubtransactionMode)"/>.</remarks>
    /// <exception cref="ArgumentException"><paramref name="mode"/> is not defined.</exception>
    public static TResult RunInSubtransaction<TResult>(Func<TResult> action, PgSubtransactionMode mode)
    {
        ArgumentNullException.ThrowIfNull(action);
        if (!Enum.IsDefined(mode))
        {
            throw new ArgumentException("The subtransaction mode is not defined.", nameof(mode));
        }

        return NativeSubtransaction.Run(action, mode == PgSubtransactionMode.Atomic);
    }

    /// <summary>
    /// Registers a one-shot callback for an outer-transaction phase.
    /// </summary>
    /// <param name="event">The phase that runs the callback.</param>
    /// <param name="callback">The action to run in registration order.</param>
    /// <returns>A registration that can cancel the pending callback.</returns>
    /// <remarks>
    /// PreCommit and PrePrepare exceptions reject the transaction. Unhandled exceptions in completed
    /// commit, abort, and prepare phases cause PostgreSQL crash recovery; committed changes remain committed.
    /// </remarks>
    public static PgTransactionCallback RegisterCallback(PgTransactionEvent @event, Action callback)
        => NativeTransactionCallbacks.Register(@event, callback);

    /// <summary>
    /// Registers a callback for every matching subtransaction in the current outer transaction.
    /// </summary>
    /// <param name="event">The subtransaction phase that runs the callback.</param>
    /// <param name="callback">
    /// The action to run with the current subtransaction ID followed by its parent subtransaction ID.
    /// </param>
    /// <returns>A registration that can cancel future invocations.</returns>
    /// <remarks>
    /// Start and PreCommit exceptions reject the operation. A Commit exception becomes a PostgreSQL error, and
    /// PostgreSQL aborts the enclosing transaction. An Abort exception is reported as a warning, because the
    /// rollback must finish.
    /// </remarks>
    public static PgSubtransactionCallback RegisterSubtransactionCallback(
        PgSubtransactionEvent @event,
        Action<PgSubtransactionId, PgSubtransactionId> callback)
        => NativeTransactionCallbacks.Register(@event, callback);
}
