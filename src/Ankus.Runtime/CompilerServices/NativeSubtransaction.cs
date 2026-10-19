using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Contains managed callback failures until native subtransaction recovery has finished.
/// </summary>
internal static unsafe class NativeSubtransaction
{
    [ThreadStatic]
    private static Invocation? s_current;

    /// <summary>
    /// Runs a callback through the native transaction guard and preserves its original exception.
    /// </summary>
    /// <typeparam name="TResult">The managed result type.</typeparam>
    /// <param name="action">The synchronous callback.</param>
    /// <param name="atomic">
    /// Whether statements in the scope share its subtransaction; otherwise each recovers individually.
    /// </param>
    /// <param name="rollBack">Whether the subtransaction is rolled back even when the callback succeeds.</param>
    /// <returns>The result after native release succeeds.</returns>
    internal static TResult Run<TResult>(Func<TResult> action, bool atomic = false, bool rollBack = false)
    {
        ArgumentNullException.ThrowIfNull(action);
        NativeBackend.CheckAccess();
        TResult result = default!;
        var invocation = new Invocation(() => result = action());
        GCHandle handle = GCHandle.Alloc(invocation);
        try
        {
            NativeBackend.RunSubtransaction((nint)(delegate* unmanaged[Cdecl]<nint, int>)&Dispatch,
                GCHandle.ToIntPtr(handle), atomic, rollBack);
        }
        catch (Exception exception) when (invocation.Failure is not null && exception is not PgQueryCanceledException)
        {
            ExceptionDispatchInfo.Capture(invocation.Failure).Throw();
            throw;
        }
        finally
        {
            handle.Free();
        }

        if (invocation.Failure is { } failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }

        return result;
    }

    /// <summary>
    /// Runs a callback in a subtransaction that is always rolled back, discarding its changes without an exception, as
    /// benchmarks that must start each iteration from the same data require.
    /// </summary>
    /// <param name="action">The synchronous callback.</param>
    internal static void RunAndRollBack(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);
        _ = Run<object?>(() =>
        {
            action();
            return null;
        }, rollBack: true);
    }

    /// <summary>
    /// Marks the innermost scope for rollback after an unrecovered native operation fails.
    /// </summary>
    /// <param name="exception">The owned native diagnostic.</param>
    internal static void RecordFailure(Exception exception)
    {
        if (s_current is { } current)
        {
            current.Failure ??= exception;
        }
    }

    /// <summary>
    /// Prevents additional SQL and raw native work before the failed scope rolls back.
    /// </summary>
    internal static void CheckAccess()
    {
        if (s_current?.Failure is { } failure)
        {
            ExceptionDispatchInfo.Capture(failure).Throw();
        }
    }

    /// <summary>
    /// Gets whether rollback must reclaim native SPI frames itself; otherwise ordinary session close reclaims them.
    /// </summary>
    internal static bool HasFailure => s_current?.Failure is not null;

    /// <summary>
    /// Returns every managed exception to native code as a status before recovery begins.
    /// </summary>
    /// <param name="state">The live handle borrowed by this synchronous invocation.</param>
    /// <returns>Zero on success, or one when the scope must roll back.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Dispatch(nint state)
    {
        Invocation? invocation = null;
        Invocation? previous = s_current;
        try
        {
            invocation = (Invocation)GCHandle.FromIntPtr(state).Target!;
            s_current = invocation;
            invocation.Action();
            return invocation.Failure is null ? 0 : 1;
        }
        // codeql[cs/catch-of-all-exceptions] Native code calls this, so every exception is kept for the rollback instead of unwinding through it.
        catch (Exception exception)
        {
            // Preserve a cleanup aggregate that still contains the original
            // native failure. An unrelated replacement cannot hide that failure.
            if (invocation is not null && (invocation.Failure is null || exception is AggregateException aggregate &&
                aggregate.Flatten().InnerExceptions.Any(failure => ReferenceEquals(failure, invocation.Failure))))
            {
                invocation.Failure = exception;
            }

            return 1;
        }
        finally
        {
            s_current = previous;
        }
    }

    /// <summary>
    /// Retains a synchronous callback and the first failure requiring rollback.
    /// </summary>
    /// <param name="action">The callback with its managed result destination.</param>
    private sealed class Invocation(Action action)
    {
        /// <summary>
        /// Gets the work to invoke once from the native guard.
        /// </summary>
        internal Action Action { get; } = action;

        /// <summary>
        /// Gets or sets the first error to rethrow after rollback.
        /// </summary>
        internal Exception? Failure
        {
            get;
            set;
        }
    }
}
