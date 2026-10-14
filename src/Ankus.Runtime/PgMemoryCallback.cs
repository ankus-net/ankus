using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus;

/// <summary>
/// Owns a one-shot PostgreSQL memory-context cleanup registration.
/// </summary>
/// <remarks>
/// Disposing the registration cancels its action without running it. The native context retains a
/// cancellation record until its next reset or deletion. There is no finalizer; PostgreSQL owns the
/// pending callback lifetime even when the registration is no longer referenced by application code.
/// Ordinary reset failures stop callback drain and require rollback before retry. During transaction
/// completion, secondary failures become warnings so commit or abort cleanup can finish. Terminal
/// reports remain pending even if their managed exception is caught; irreversible completion uses
/// PostgreSQL PANIC after managed unwinding. Warning reporter failures during abort preserve the
/// primary error and callback drain; a repeatedly failing log hook uses PostgreSQL's native stderr
/// fallback. A callback's cancellation SQLSTATE does not create a native cancellation request.
/// Cleanup does not inherit ordinary logging or SQL access.
/// </remarks>
public sealed unsafe class PgMemoryCallback : IDisposable
{
    [ThreadStatic]
    private static Dictionary<nint, PgMemoryCallback>? s_pending;

    private static long s_nextId;
    private readonly nint _provider;
    private readonly nint _context;
    private readonly nint _id;
    private readonly nint _execute = NativeBackend.CleanupBinding;
    private Action? _callback;

    private PgMemoryCallback(nint provider, nint context, nint id, Action callback)
    {
        _provider = provider;
        _context = context;
        _id = id;
        _callback = callback;
    }

    /// <summary>
    /// Gets whether this registration still owns an action awaiting native cleanup.
    /// </summary>
    public bool IsPending => _callback is not null;

    /// <summary>
    /// Cancels a pending callback and releases its managed references without executing it.
    /// </summary>
    public void Dispose()
    {
        if (_callback is null)
        {
            return;
        }

        NativeMemoryContext.CheckProvider(_provider);
        if (s_pending is null || !s_pending.TryGetValue(_id, out PgMemoryCallback? registration) ||
            !ReferenceEquals(registration, this))
        {
            throw new InvalidOperationException("The PostgreSQL memory callback must be cancelled on its owning backend thread.");
        }

        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.CancelCallback,
            _context = _context,
            _other = _id,
        };
        NativeMemoryContext.Invoke(ref request, out _);
        s_pending.Remove(_id);
        _callback = null;
    }

    /// <summary>
    /// Roots the action before registering the native one-shot dispatcher.
    /// </summary>
    /// <param name="provider">The validated native provider.</param>
    /// <param name="context">The validated native context identity.</param>
    /// <param name="callback">The action released or invoked by native cleanup.</param>
    /// <returns>The rooted registration.</returns>
    internal static PgMemoryCallback Register(nint provider, nint context, Action callback)
    {
        nint id = NativeAggregate.AllocateStateId(ref s_nextId);
        var registration = new PgMemoryCallback(provider, context, id, callback);
        (s_pending ??= []).Add(id, registration);
        NativeMemoryRequest request = new()
        {
            _operation = NativeMemoryOperation.RegisterCallback,
            _context = context,
            _other = id,
            _pointer = (nint)(delegate* unmanaged[Cdecl]<nint, nint, NativeCallError*, int>)&Invoke,
        };
        try
        {
            NativeMemoryContext.Invoke(ref request, out _);
            return registration;
        }
        catch
        {
            s_pending.Remove(id);
            registration._callback = null;
            throw;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Invoke(nint id, nint memory, NativeCallError* error)
    {
        nint previousMemory = NativeMemoryContext.Enter(memory);
        nint previousBackend = 0;
        bool backendEntered = false;
        nint previousLog = NativeLog.Enter(0);
        int previousTerminal = NativeLog.EnterMemoryTerminal();
        nint previousRead = NativeGuc.Enter(0);
        NativeAggregate.EnterCleanup();
        try
        {
            if (s_pending is null || !s_pending.TryGetValue(id, out PgMemoryCallback? registration))
            {
                throw new InvalidOperationException("The PostgreSQL memory callback registration is stale or belongs to another backend thread.");
            }

            NativeMemoryContext.CheckProvider(registration._provider);
            s_pending.Remove(id);
            Action? callback = registration._callback;
            registration._callback = null;
            previousBackend = NativeBackend.Enter(registration._execute, abortCleanup: true);
            backendEntered = true;
            callback?.Invoke();
            return 0;
        }
        // codeql[cs/catch-of-all-exceptions] Native code calls this, so every exception goes back to PostgreSQL as an error instead of unwinding through it.
        catch (Exception exception)
        {
            NativeError.Write(exception, error);
            return 1;
        }
        finally
        {
            NativeAggregate.ExitCleanup();
            NativeGuc.Exit(previousRead);
            NativeLog.ExitMemoryTerminal(previousTerminal);
            NativeLog.Exit(previousLog);
            if (backendEntered)
            {
                NativeBackend.Exit(previousBackend, abortCleanup: true);
            }

            NativeMemoryContext.Exit(previousMemory);
        }
    }
}
