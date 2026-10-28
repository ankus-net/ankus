using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Dispatches PostgreSQL transaction hooks into per-backend managed registrations.
/// </summary>
internal static unsafe class NativeTransactionCallbacks
{
    private const int TransactionDispatcher = 1;
    private const int SubtransactionDispatcher = 2;

    [ThreadStatic]
    private static TransactionCallbackRegistry? s_registry;

    [ThreadStatic]
    private static int s_terminalDepth;

    [ThreadStatic]
    private static int s_dispatchDepth;

    /// <summary>
    /// Registers a one-shot outer-transaction callback.
    /// </summary>
    /// <param name="event">The selected outer-transaction event.</param>
    /// <param name="callback">The action to retain.</param>
    /// <returns>The cancellable registration.</returns>
    internal static PgTransactionCallback Register(PgTransactionEvent @event, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Validate(@event);
        CheckRegistrationPhase();
        NativeBackend.CheckAccess();
        TransactionCallbackRegistry registry = s_registry ?? new TransactionCallbackRegistry(NativeBackend.CleanupBinding);
        if (!registry.HasTransactionDispatcher)
        {
            NativeBackend.RegisterTransactionCallbacks(CallbackPointer, TransactionDispatcher);
            registry.HasTransactionDispatcher = true;
        }

        s_registry = registry;
        var registration = new PgTransactionCallback(registry, callback);
        (registry.TransactionCallbacks[(int)@event] ??= []).Add(registration);
        return registration;
    }

    /// <summary>
    /// Registers a repeating subtransaction callback.
    /// </summary>
    /// <param name="event">The selected subtransaction event.</param>
    /// <param name="callback">The action to retain.</param>
    /// <returns>The cancellable registration.</returns>
    internal static PgSubtransactionCallback Register(
        PgSubtransactionEvent @event,
        Action<PgSubtransactionId, PgSubtransactionId> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        Validate(@event);
        CheckRegistrationPhase();
        NativeBackend.CheckAccess();
        TransactionCallbackRegistry registry = s_registry ?? new TransactionCallbackRegistry(NativeBackend.CleanupBinding);
        if (!registry.HasSubtransactionDispatcher)
        {
            NativeBackend.RegisterTransactionCallbacks(CallbackPointer, SubtransactionDispatcher);
            registry.HasTransactionDispatcher = true;
            registry.HasSubtransactionDispatcher = true;
        }

        s_registry = registry;
        var registration = new PgSubtransactionCallback(registry, callback);
        (registry.SubtransactionCallbacks[(int)@event] ??= []).Add(registration);
        return registration;
    }

    /// <summary>
    /// Gets the single Native AOT dispatcher installed into PostgreSQL.
    /// </summary>
    private static nint CallbackPointer
        => (nint)(delegate* unmanaged[Cdecl]<int, int, uint, uint, NativeCallError*, nint, nint, nint, int>)&Dispatch;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Dispatch(
        int kind,
        int eventCode,
        uint subtransactionId,
        uint parentSubtransactionId,
        NativeCallError* error,
        nint execute,
        nint log,
        nint memory)
    {
        nint previousBackend = NativeBackend.Enter(execute);
        nint previousRead = NativeGuc.Enter(0);
        nint previousLog = NativeLog.Enter(log);
        nint previousMemory = NativeMemoryContext.Enter(memory);
        s_dispatchDepth++;
        try
        {
            switch (kind)
            {
                case 0:
                    Dispatch((PgTransactionEvent)eventCode);
                    break;
                case 1:
                    Dispatch((PgSubtransactionEvent)eventCode, subtransactionId, parentSubtransactionId);
                    break;
                default:
                    throw new InvalidOperationException("The native PostgreSQL transaction callback kind is invalid.");
            }

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
            s_dispatchDepth--;
            NativeMemoryContext.Exit(previousMemory);
            NativeLog.Exit(previousLog);
            NativeGuc.Exit(previousRead);
            NativeBackend.Exit(previousBackend);
        }
    }

    /// <summary>
    /// Verifies that a pending receipt is being cancelled in its active transaction and backend callback.
    /// </summary>
    /// <param name="registry">The receipt's owning registry.</param>
    /// <param name="owner">The native backend binding captured at registration.</param>
    internal static void CheckCancellationAccess(TransactionCallbackRegistry registry, nint owner)
    {
        if (!registry.IsActive || !ReferenceEquals(s_registry, registry))
        {
            throw new InvalidOperationException("The PostgreSQL transaction callback no longer belongs to the active transaction.");
        }

        if (s_dispatchDepth == 0)
        {
            NativeBackend.CheckDisposalAccess(owner);
        }
    }

    private static void Dispatch(PgTransactionEvent @event)
    {
        Validate(@event);
        TransactionCallbackRegistry? registry = s_registry;
        if (registry is null)
        {
            return;
        }

        int index = (int)@event;
        List<PgTransactionCallback>? callbacks = registry.TransactionCallbacks[index];
        registry.TransactionCallbacks[index] = null;
        bool terminal = @event is PgTransactionEvent.Abort or PgTransactionEvent.Commit or
            PgTransactionEvent.ParallelAbort or PgTransactionEvent.ParallelCommit or PgTransactionEvent.Prepare;
        if (terminal)
        {
            ReleaseUnused(registry, callbacks);
            s_terminalDepth++;
        }

        try
        {
            Invoke(callbacks);
        }
        finally
        {
            if (terminal)
            {
                Release(callbacks);
                registry.Deactivate();
                if (ReferenceEquals(s_registry, registry))
                {
                    s_registry = null;
                }

                s_terminalDepth--;
            }
            else
            {
                Release(callbacks);
            }
        }
    }

    private static void Dispatch(PgSubtransactionEvent @event, uint subtransactionId, uint parentSubtransactionId)
    {
        Validate(@event);
        TransactionCallbackRegistry? registry = s_registry;
        if (registry is null)
        {
            return;
        }

        List<PgSubtransactionCallback>? callbacks = registry.SubtransactionCallbacks[(int)@event];
        if (callbacks is null)
        {
            return;
        }

        PgSubtransactionCallback[] snapshot = [.. callbacks];
        foreach (PgSubtransactionCallback registration in snapshot)
        {
            registration.Get()?.Invoke(new PgSubtransactionId(subtransactionId), new PgSubtransactionId(parentSubtransactionId));
        }
    }

    private static void Invoke(List<PgTransactionCallback>? callbacks)
    {
        if (callbacks is null)
        {
            return;
        }

        foreach (PgTransactionCallback registration in callbacks)
        {
            registration.Take()?.Invoke();
        }
    }

    private static void ReleaseUnused(TransactionCallbackRegistry registry, List<PgTransactionCallback>? selected)
    {
        // The registry's fixed slots are indexed, so transaction end allocates no enumerator.
        List<PgTransactionCallback>?[] transactionCallbacks = registry.TransactionCallbacks;
        for (int index = 0; index < transactionCallbacks.Length; index++)
        {
            if (!ReferenceEquals(transactionCallbacks[index], selected))
            {
                Release(transactionCallbacks[index]);
            }
        }

        List<PgSubtransactionCallback>?[] subtransactionCallbacks = registry.SubtransactionCallbacks;
        for (int index = 0; index < subtransactionCallbacks.Length; index++)
        {
            if (subtransactionCallbacks[index] is { } callbacks)
            {
                foreach (PgSubtransactionCallback registration in callbacks)
                {
                    registration.Release();
                }
            }
        }
    }

    private static void Release(List<PgTransactionCallback>? callbacks)
    {
        if (callbacks is not null)
        {
            foreach (PgTransactionCallback registration in callbacks)
            {
                registration.Release();
            }
        }
    }

    private static void CheckRegistrationPhase()
    {
        if (s_terminalDepth != 0)
        {
            throw new InvalidOperationException("Transaction callbacks cannot be registered after the outer transaction has ended.");
        }
    }

    private static void Validate(PgTransactionEvent @event)
    {
        if (@event is < PgTransactionEvent.Abort or > PgTransactionEvent.PrePrepare)
        {
            throw new ArgumentOutOfRangeException(nameof(@event));
        }
    }

    private static void Validate(PgSubtransactionEvent @event)
    {
        if (@event is < PgSubtransactionEvent.Abort or > PgSubtransactionEvent.Start)
        {
            throw new ArgumentOutOfRangeException(nameof(@event));
        }
    }
}
