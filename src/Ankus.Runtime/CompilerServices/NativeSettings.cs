using System.Runtime.CompilerServices;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;

namespace Ankus.CompilerServices;

/// <summary>
/// Runs a managed callback while PostgreSQL holds configuration values that it restores afterwards.
/// </summary>
internal static unsafe class NativeSettings
{
    /// <summary>
    /// Applies the settings, runs the callback, and restores the previous values before returning or rethrowing.
    /// </summary>
    /// <typeparam name="TResult">The managed result type.</typeparam>
    /// <param name="settings">The setting names and values, applied in order.</param>
    /// <param name="action">The synchronous callback.</param>
    /// <returns>The callback's result.</returns>
    internal static TResult Run<TResult>(IEnumerable<KeyValuePair<string, string>> settings, Func<TResult> action)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(action);
        List<SpiParameter> parameters = [];
        foreach ((string name, string value) in settings)
        {
            ArgumentException.ThrowIfNullOrEmpty(name, nameof(settings));
            ArgumentNullException.ThrowIfNull(value, nameof(settings));
            parameters.Add(SpiParameter.Create(name));
            parameters.Add(SpiParameter.Create(value));
        }

        NativeBackend.CheckAccess();
        TResult result = default!;
        var invocation = new Invocation(() => result = action());
        GCHandle handle = GCHandle.Alloc(invocation);
        try
        {
            NativeBackend.RunWithSettings((nint)(delegate* unmanaged[Cdecl]<nint, int>)&Dispatch,
                GCHandle.ToIntPtr(handle), CollectionsMarshal.AsSpan(parameters));
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
    /// Returns every managed exception to native code as a status, so the settings are restored before it propagates.
    /// </summary>
    /// <param name="state">The live handle borrowed by this synchronous invocation.</param>
    /// <returns>Zero on success, or one after the callback threw.</returns>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int Dispatch(nint state)
    {
        Invocation? invocation = null;
        try
        {
            invocation = (Invocation)GCHandle.FromIntPtr(state).Target!;
            invocation.Action();
            return 0;
        }
        // codeql[cs/catch-of-all-exceptions] Native code calls this, so every exception is kept until the settings are restored instead of unwinding through it.
        catch (Exception exception)
        {
            if (invocation is not null)
            {
                invocation.Failure = exception;
            }

            return 1;
        }
    }

    /// <summary>
    /// Retains a synchronous callback and the exception it threw.
    /// </summary>
    /// <param name="action">The callback with its managed result destination.</param>
    private sealed class Invocation(Action action)
    {
        /// <summary>
        /// Gets the work to invoke once from the native scope.
        /// </summary>
        internal Action Action { get; } = action;

        /// <summary>
        /// Gets or sets the exception to rethrow after the settings are restored.
        /// </summary>
        internal Exception? Failure
        {
            get;
            set;
        }
    }
}
