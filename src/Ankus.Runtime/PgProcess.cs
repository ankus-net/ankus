using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus;

/// <summary>
/// Runs code when the current PostgreSQL process exits, as a C extension's <c>before_shmem_exit</c> callback does.
/// </summary>
public static unsafe class PgProcess
{
    private static readonly List<Registration> s_callbacks = [];

    private static int s_registeredProcess;

    /// <summary>
    /// Registers a callback that runs when this process exits, before it detaches from shared memory.
    /// </summary>
    /// <param name="callback">The callback, given the exit code: zero for a normal exit, nonzero after a FATAL error
    /// or a termination request.</param>
    /// <returns>A registration whose disposal removes the callback before it runs.</returns>
    /// <remarks>
    /// <para>
    /// Callbacks run newest first, after a normal exit, after a FATAL error and when PostgreSQL stops a worker, but not
    /// when the process crashes or the server restarts after a crash. A background worker uses one to release the
    /// shared-memory slot it claimed, as pglogical's workers do.
    /// </para>
    /// <para>
    /// A callback can use shared memory and lightweight locks, but not SQL or logging, because the transaction may be
    /// aborted and PostgreSQL is shutting the process down. It must not wait for a lock this process may already hold.
    /// An exception is reported as a warning and the remaining callbacks still run. Registration belongs to the calling
    /// process; register in the backend or worker that needs the callback, after the library has loaded there.
    /// </para>
    /// </remarks>
    public static IDisposable OnExit(Action<int> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        int process = Environment.ProcessId;
        if (s_registeredProcess != process)
        {
            NativeMemoryRequest request = new()
            {
                _operation = NativeMemoryOperation.ProcessExit,
                _pointer = (nint)(delegate* unmanaged[Cdecl]<nint, nint, NativeCallError*, int>)&Exit,
            };
            NativeMemoryContext.Invoke(ref request, out _);
            s_registeredProcess = process;
        }

        var registration = new Registration(callback, process);
        lock (s_callbacks)
        {
            s_callbacks.Add(registration);
        }

        return registration;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Exit(nint code, nint memory, NativeCallError* error)
    {
        nint previousMemory = NativeMemoryContext.Enter(memory);
        nint previousLog = NativeLog.Enter(0);
        int previousTerminal = NativeLog.EnterMemoryTerminal();
        try
        {
            Registration[] callbacks;
            lock (s_callbacks)
            {
                callbacks = [.. s_callbacks];
                s_callbacks.Clear();
            }

            // A forked child inherits the parent's managed list, but only its own registrations belong to it.
            int process = Environment.ProcessId;
            Exception? failure = null;
            for (int index = callbacks.Length - 1; index >= 0; index--)
            {
                if (callbacks[index].Process != process)
                {
                    continue;
                }

                try
                {
                    callbacks[index].Run(checked((int)code));
                }
                // codeql[cs/catch-of-all-exceptions] Every exit callback runs, and the first failure goes back to PostgreSQL as an error.
                catch (Exception exception)
                {
                    failure ??= exception;
                }
            }

            if (failure is null)
            {
                return 0;
            }

            NativeError.Write(failure, error);
            return 1;
        }
        finally
        {
            NativeLog.ExitMemoryTerminal(previousTerminal);
            NativeLog.Exit(previousLog);
            NativeMemoryContext.Exit(previousMemory);
        }
    }

    /// <summary>
    /// Holds one callback until it runs or is removed.
    /// </summary>
    private sealed class Registration(Action<int> callback, int process) : IDisposable
    {
        private Action<int>? _callback = callback;

        public int Process { get; } = process;

        public void Run(int code) => Interlocked.Exchange(ref _callback, null)?.Invoke(code);

        public void Dispose()
        {
            _callback = null;
            lock (s_callbacks)
            {
                s_callbacks.Remove(this);
            }
        }
    }
}
