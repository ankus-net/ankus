using System;

namespace Ankus.CompilerServices;

/// <summary>
/// Retains a checked native observation identity until explicit disposal or callback exit.
/// </summary>
internal sealed class NativeBackgroundWorkerLease(NativeBorrowScope scope, int notifyProcessId) : IDisposable
{
    private readonly int _process = Environment.ProcessId;
    private nint _identity;

    /// <summary>
    /// Gets the requested transition notification process.
    /// </summary>
    internal int NotifyProcessId { get; } = notifyProcessId;

    /// <summary>
    /// Publishes the native observation identity after registration succeeds.
    /// </summary>
    internal void Registered(nint identity)
    {
        if (identity <= 0)
        {
            throw new InvalidOperationException("PostgreSQL returned an invalid background-worker handle.");
        }

        _identity = identity;
    }

    /// <summary>
    /// Executes a status operation and rejects impossible state or PID combinations.
    /// </summary>
    internal PgBackgroundWorkerState Observe(int operation)
    {
        NativeMemoryResult result = Invoke(operation);
        int status = checked((int)result._value);
        int processId = checked((int)result._pointer);
        bool allowed = operation switch
        {
            2 => status is >= 0 and <= 2,
            3 => status is 0 or 2 or 3 or 4,
            5 => status is 2 or 3 or 4,
            _ => false,
        };
        if (!allowed || (status == 0 ? processId <= 0 : processId != 0))
        {
            throw new InvalidOperationException("PostgreSQL returned an invalid background-worker state.");
        }

        return new((PgBackgroundWorkerStatus)status, status == 0 ? processId : null);
    }

    /// <summary>
    /// Validates lifetime and ownership before accessing the native registry.
    /// </summary>
    internal NativeMemoryResult Invoke(int operation)
    {
        ObjectDisposedException.ThrowIf(_identity == 0, this);
        scope.Validate();
        if (_process != Environment.ProcessId)
        {
            throw new InvalidOperationException("A background-worker handle belongs to its registering backend process.");
        }

        return NativeBackgroundWorker.Invoke(operation, _identity);
    }

    /// <summary>
    /// Releases native observation storage without sending any worker signal.
    /// </summary>
    public void Dispose()
    {
        if (_identity != 0)
        {
            _ = Invoke(6);
            _identity = 0;
        }

        scope.Unregister(this);
    }
}
