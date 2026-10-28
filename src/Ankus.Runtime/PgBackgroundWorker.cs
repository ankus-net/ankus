using System;
using System.Diagnostics.CodeAnalysis;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Registers PostgreSQL background workers and supplies operations for a generated worker entry.
/// </summary>
/// <remarks>
/// Backend operations are synchronous and remain on the owning PostgreSQL thread.
/// Generated entries receive shared-memory access; database work requires a connection and a transaction callback.
/// </remarks>
public static class PgBackgroundWorker
{
    /// <summary>
    /// Registers a static worker during shared preload; backend replay of shared preload does not register it again.
    /// </summary>
    /// <param name="options">The complete worker declaration.</param>
    public static void Register(PgBackgroundWorkerOptions options) => NativeBackgroundWorker.Register(options, dynamic: false);

    /// <summary>
    /// Attempts to register a dynamic worker, returning false when PostgreSQL has no available worker slot.
    /// </summary>
    /// <param name="options">The complete worker declaration.</param>
    /// <param name="worker">The callback-owned observation handle when registration succeeds.</param>
    /// <returns>Whether PostgreSQL accepted the registration.</returns>
    /// <remarks>
    /// Registration does not guarantee startup. Inspect or wait on the handle to observe the process.
    /// PostgreSQL can register the worker before allocating its observation handle. An allocation error
    /// can therefore throw while the worker remains registered; retrying may start another worker.
    /// Dynamic registration requires an initialized backend after shared preload has completed.
    /// Disposing the handle or leaving this backend callback does not terminate the registered worker.
    /// PostgreSQL sanitizes dynamic names, types, library paths and symbols; values it would alter are rejected.
    /// Extra text preserves UTF-8 independently of those metadata restrictions.
    /// </remarks>
    public static bool TryStart(PgBackgroundWorkerOptions options, [NotNullWhen(true)] out PgBackgroundWorkerHandle? worker)
    {
        worker = NativeBackgroundWorker.Register(options, dynamic: true);
        return worker is not null;
    }

    /// <summary>
    /// Gets the current worker's name as an owned string.
    /// </summary>
    public static string Name => NativeBackgroundWorker.ReadText(0);

    /// <summary>
    /// Gets the current worker's type as an owned string.
    /// </summary>
    public static string Type => NativeBackgroundWorker.ReadText(1);

    /// <summary>
    /// Gets the extra text copied into the current worker's registration.
    /// </summary>
    public static string Extra => NativeBackgroundWorker.ReadText(2);

    /// <summary>
    /// Gets whether the postmaster is alive and termination has not been requested.
    /// </summary>
    public static bool CanContinue => NativeBackgroundWorker.Boolean(14);

    /// <summary>
    /// Adds native signal observers before unblocking signals in the worker.
    /// </summary>
    /// <param name="signals">The desired signals; reload and termination observers always remain installed.</param>
    /// <remarks>
    /// Observing <see cref="PgBackgroundWorkerSignals.Interrupt"/> replaces PostgreSQL's SIGINT query-cancellation handler.
    /// Without that observer, SIGINT can cancel an active worker transaction.
    /// </remarks>
    public static void AttachSignalHandlers(PgBackgroundWorkerSignals signals)
        => NativeBackgroundWorker.SignalOperation(8, signals);

    /// <summary>
    /// Returns and clears selected pending signal observations.
    /// </summary>
    /// <param name="signals">The observations to consume.</param>
    /// <returns>The selected signals received since their previous consumption.</returns>
    public static PgBackgroundWorkerSignals ConsumeSignals(PgBackgroundWorkerSignals signals =
        PgBackgroundWorkerSignals.Reload | PgBackgroundWorkerSignals.Terminate | PgBackgroundWorkerSignals.Interrupt | PgBackgroundWorkerSignals.Child)
        => NativeBackgroundWorker.SignalOperation(9, signals);

    /// <summary>
    /// Waits on the worker's latch, returning false for postmaster death or termination, even after consuming its observation.
    /// </summary>
    /// <param name="timeout">A whole-millisecond timeout, or null to wait indefinitely.</param>
    /// <returns>Whether the worker may continue after waking.</returns>
    /// <exception cref="PgQueryCanceledException">PostgreSQL cancels the wait through its default SIGINT handler.</exception>
    /// <remarks>
    /// Cancellation outside a transaction ends only this wait and can be caught before waiting or starting a transaction again.
    /// Inside RunTransaction, cancellation still requires the transaction to abort before the worker resumes.
    /// </remarks>
    public static bool Wait(TimeSpan? timeout = null) => NativeBackgroundWorker.Wait(timeout);

    /// <summary>
    /// Initializes this worker's database connection by name, once per worker process.
    /// </summary>
    /// <param name="database">The database name, or null for a connection without a selected database.</param>
    /// <param name="user">The role name, or null for PostgreSQL's bootstrap superuser.</param>
    public static void Connect(string? database, string? user = null) => NativeBackgroundWorker.Connect(database, user);

    /// <summary>
    /// Initializes this worker's database connection by OID, once per worker process.
    /// </summary>
    /// <param name="databaseOid">The database OID, or zero for no selected database.</param>
    /// <param name="userOid">The role OID, or zero for PostgreSQL's bootstrap superuser.</param>
    public static void Connect(uint databaseOid, uint userOid = 0) => NativeBackgroundWorker.Connect(databaseOid, userOid);

    /// <summary>
    /// Executes synchronous database work in a new worker transaction and returns its owned result after commit.
    /// </summary>
    /// <typeparam name="TResult">The owned result.</typeparam>
    /// <param name="action">The synchronous transaction body.</param>
    /// <returns>The callback result after successful commit.</returns>
    /// <remarks>
    /// Failure aborts the transaction before the original managed exception or owned PostgreSQL error returns.
    /// Query cancellation remains pending throughout the callback, even if caught there. After rollback,
    /// the caller can catch <see cref="PgQueryCanceledException"/> and start another transaction.
    /// FATAL and PANIC reports still terminate the worker. Nested worker transactions are rejected.
    /// </remarks>
    public static TResult RunTransaction<TResult>(Func<TResult> action) => NativeBackgroundWorker.RunTransaction(action);

    /// <summary>
    /// Executes synchronous database work in a new worker transaction and commits when the callback returns.
    /// </summary>
    /// <param name="action">The synchronous transaction body.</param>
    /// <remarks>
    /// A canceled callback always rolls back. Its caller can catch <see cref="PgQueryCanceledException"/>
    /// after rollback and continue the worker; catching cancellation inside the callback cannot commit it.
    /// FATAL and PANIC reports still terminate the worker. Nested worker transactions are rejected.
    /// </remarks>
    public static void RunTransaction(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = NativeBackgroundWorker.RunTransaction(() =>
        {
            action();
            return true;
        });
    }

    /// <summary>
    /// Reloads PostgreSQL configuration within the current worker after a reload notification.
    /// </summary>
    public static void ReloadConfiguration() => NativeBackgroundWorker.Invoke(15);
}
