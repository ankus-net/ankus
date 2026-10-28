using System;
using System.ComponentModel;

namespace Ankus.CompilerServices;

/// <summary>
/// Binds generated callbacks to guarded PostgreSQL logging without granting SQL or transaction access.
/// </summary>
[EditorBrowsable(EditorBrowsableState.Never)]
public static unsafe class NativeLog
{
    [ThreadStatic]
    private static nint s_log;

    [ThreadStatic]
    private static int s_scopeDepth;

    [ThreadStatic]
    private static int s_memoryTerminalScopeDepth;

    /// <summary>
    /// Grants terminal transport through the current cleanup memory capability without inheriting a logger or SQL access.
    /// </summary>
    /// <returns>The enclosing memory-terminal scope restored after this callback.</returns>
    internal static int EnterMemoryTerminal()
    {
        int previous = s_memoryTerminalScopeDepth;
        s_memoryTerminalScopeDepth = s_scopeDepth;
        return previous;
    }

    /// <summary>
    /// Restores the enclosing memory-terminal capability before leaving the matching logging scope.
    /// </summary>
    /// <param name="previous">The scope depth returned by the matching entry.</param>
    internal static void ExitMemoryTerminal(int previous) => s_memoryTerminalScopeDepth = previous;

    /// <summary>
    /// Gets whether this exact logging scope owns the cleanup memory transport for terminal diagnostics.
    /// </summary>
    private static bool HasMemoryTerminal => s_scopeDepth != 0 && s_scopeDepth == s_memoryTerminalScopeDepth;

    /// <summary>
    /// Enters a native logging scope on the current backend thread.
    /// </summary>
    /// <param name="log">The guarded native logging entry point, or zero to explicitly disable logging.</param>
    /// <returns>The previous binding, restored by generated code in a matching finally block.</returns>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// Use the matching dispatcher's live binding and restore the exact previous entry in reverse order.
    /// </remarks>
    [NativeUnsafeAccess]
    public static nint Enter(nint log)
    {
        nint previous = s_log;
        s_log = log;
        s_scopeDepth++;
        return previous;
    }

    /// <summary>
    /// Restores the enclosing native logging scope on its owning thread.
    /// </summary>
    /// <param name="previous">The binding returned by the matching Enter call.</param>
    /// <remarks>
    /// This raw transport contract requires an explicit unsafe context.
    /// Use the matching dispatcher's live binding and restore the exact previous entry in reverse order.
    /// </remarks>
    [NativeUnsafeAccess]
    public static void Exit(nint previous)
    {
        s_log = previous;
        s_scopeDepth--;
    }

    /// <summary>
    /// Requires the scoped logging capability or the ordinary active backend capability.
    /// </summary>
    internal static void CheckAccess(bool terminal = false)
    {
        if (terminal && HasMemoryTerminal)
        {
            _ = NativeMemoryContext.Provider;
            return;
        }

        if (!terminal)
        {
            NativeBorrowScope.CheckBackendAccess();
        }

        if (s_scopeDepth == 0)
        {
            if (terminal)
            {
                NativeBackend.CheckDisposalAccess();
            }
            else
            {
                NativeBackend.CheckCallbackAccess();
            }
        }
        else if (s_log == 0)
        {
            throw new InvalidOperationException("PostgreSQL logging is unavailable in this callback scope.");
        }
    }

    /// <summary>
    /// Tests PostgreSQL's routing thresholds through the scoped logger or ordinary backend route.
    /// </summary>
    /// <param name="level">The validated reporting severity.</param>
    /// <returns>Whether either PostgreSQL destination accepts this severity.</returns>
    internal static bool IsEnabled(PgLogLevel level)
    {
        CheckAccess();
        return s_scopeDepth == 0 ? NativeBackend.IsLogEnabled(level) : Invoke(0, level, null);
    }

    /// <summary>
    /// Retains a terminal report in the native entry frame before managed code starts unwinding.
    /// </summary>
    /// <param name="exception">The validated terminal severity and diagnostics.</param>
    internal static void RecordTerminal(PgTerminalException exception)
    {
        NativeCallError report = default;
        try
        {
            NativeError.Write(exception, &report);
            if (HasMemoryTerminal)
            {
                NativeMemoryContext.RecordTerminal(exception.Level, &report);
            }
            else if (s_scopeDepth == 0)
            {
                NativeBackend.RecordTerminal(exception.Level, &report);
            }
            else
            {
                Invoke(2, exception.Level, &report);
            }
        }
        finally
        {
            report.Release();
        }
    }

    /// <summary>
    /// Reports an enabled nonterminal diagnostic while retaining ownership of its native transport buffers.
    /// </summary>
    /// <param name="level">The validated nonterminal reporting severity.</param>
    /// <param name="diagnostic">The structured diagnostic copied only after filtering succeeds.</param>
    internal static void Report(PgLogLevel level, PgDiagnostic diagnostic)
    {
        CheckAccess();
        if (s_scopeDepth == 0)
        {
            NativeBackend.Report(level, diagnostic);
            return;
        }

        if (!IsEnabled(level))
        {
            return;
        }

        NativeCallError message = default;
        try
        {
            NativeError.WriteDiagnostic(diagnostic, &message);
            Invoke(1, level, &message);
        }
        finally
        {
            message.Release();
        }
    }

    /// <summary>
    /// Invokes the guarded logging ABI and copies any owned native failure before releasing its buffers.
    /// </summary>
    /// <param name="operation">Zero to test filtering, one to report a borrowed diagnostic, or two to retain a terminal report.</param>
    /// <param name="level">The validated reporting severity.</param>
    /// <param name="message">The borrowed report, or null when only testing filtering.</param>
    /// <returns>The native threshold result; reporting callers ignore this value.</returns>
    private static bool Invoke(int operation, PgLogLevel level, NativeCallError* message)
    {
        var log = (delegate* unmanaged[Cdecl]<int, int, NativeCallError*, NativeCallError*, int*, int>)s_log;
        NativeCallError error = default;
        int enabled = 0;
        try
        {
            if (log(operation, (int)level, message, &error, &enabled) != 0)
            {
                throw error.ToManagedException();
            }

            return enabled != 0;
        }
        finally
        {
            error.Release();
        }
    }
}
