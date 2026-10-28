using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Observes and controls one dynamic registration during the originating backend callback.
/// </summary>
/// <remarks>
/// Disposing this handle releases observation storage without terminating the worker.
/// Use Terminate explicitly to request shutdown and disable future restarts.
/// </remarks>
public sealed class PgBackgroundWorkerHandle : IDisposable
{
    private readonly NativeBackgroundWorkerLease _lease;

    /// <summary>
    /// Wraps a prepared callback-owned native registration lease.
    /// </summary>
    internal PgBackgroundWorkerHandle(NativeBackgroundWorkerLease lease) => _lease = lease;

    /// <summary>
    /// Gets the backend PID selected to receive transition notifications, or zero when disabled.
    /// </summary>
    public int NotifyProcessId => _lease.NotifyProcessId;

    /// <summary>
    /// Polls the registration's current status and running process identifier.
    /// </summary>
    /// <returns>An owned state snapshot.</returns>
    public PgBackgroundWorkerState GetState() => _lease.Observe(2);

    /// <summary>
    /// Waits for the postmaster to attempt startup, or returns Untracked if this backend is not the notifier.
    /// </summary>
    /// <returns>The observed startup outcome.</returns>
    public PgBackgroundWorkerState WaitForStartup() => _lease.Observe(3);

    /// <summary>
    /// Requests worker termination and unregisters it once stopped, preventing subsequent restart.
    /// </summary>
    public void Terminate() => _lease.Invoke(4);

    /// <summary>
    /// Waits until the worker stops, or returns PostmasterDied or Untracked.
    /// </summary>
    /// <returns>The shutdown observation.</returns>
    public PgBackgroundWorkerStatus WaitForShutdown() => _lease.Observe(5).Status;

    /// <summary>
    /// Releases native observation storage once without terminating the worker.
    /// </summary>
    public void Dispose() => _lease.Dispose();
}
