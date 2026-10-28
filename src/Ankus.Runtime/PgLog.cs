using System;
using System.Linq;
using System.Runtime.CompilerServices;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Reports through PostgreSQL's client and server diagnostics on the active backend thread.
/// </summary>
public static partial class PgLog
{
    /// <summary>
    /// Tests PostgreSQL's current reporting thresholds before formatting a message.
    /// </summary>
    /// <param name="level">The reporting severity.</param>
    /// <returns>Whether PostgreSQL would process this level for either destination.</returns>
    public static bool IsEnabled(PgLogLevel level)
    {
        ValidateLevel(level);
        return NativeLog.IsEnabled(level);
    }

    /// <summary>
    /// Reports literal text. ERROR throws PgException; FATAL and PANIC unwind to the generated native boundary before reporting.
    /// </summary>
    /// <param name="level">The reporting severity.</param>
    /// <param name="message">The primary message.</param>
    public static void Write(PgLogLevel level, string message) => Write(level, new PgDiagnostic(message));

    /// <summary>
    /// Reports an interpolated message, evaluating and formatting its expressions only when PostgreSQL would report
    /// the level, as pgrx's logging macros do. ERROR throws PgException; FATAL and PANIC unwind to the generated native
    /// boundary before reporting.
    /// </summary>
    /// <param name="level">The reporting severity.</param>
    /// <param name="message">The interpolated primary message.</param>
    public static void Write(PgLogLevel level, [InterpolatedStringHandlerArgument(nameof(level))] ref PgLogInterpolatedStringHandler message)
    {
        if (message.IsEnabled)
        {
            Write(level, message.ToStringAndClear());
        }
    }

    /// <summary>
    /// Reports structured diagnostics using PostgreSQL's filtering and routing rules.
    /// Terminal reports remain pending until the native boundary even if extension code catches their managed exception.
    /// </summary>
    /// <remarks>
    /// Messages below ERROR defer PostgreSQL interrupts while the native reporter emits the message.
    /// Call <see cref="PgInterrupts.Check"/> periodically in loops that otherwise only report messages.
    /// Reporting failures still follow the ordinary native error recovery contract.
    /// </remarks>
    /// <param name="level">The reporting severity.</param>
    /// <param name="diagnostic">The message and optional diagnostic fields.</param>
    public static void Write(PgLogLevel level, PgDiagnostic diagnostic)
    {
        if (level >= PgLogLevel.Error)
        {
            throw CreateTerminal(level, diagnostic);
        }

        ValidateDiagnostic(level, diagnostic);
        NativeLog.Report(level, diagnostic);
    }

    /// <summary>
    /// Creates an error after validating its capability and retains terminal intent before managed unwinding.
    /// </summary>
    /// <param name="level">ERROR, FATAL or PANIC.</param>
    /// <param name="diagnostic">The owned message and exact diagnostic fields.</param>
    /// <returns>The exception to throw without a normally returning terminal-report path.</returns>
    private static Exception CreateTerminal(PgLogLevel level, PgDiagnostic diagnostic)
    {
        ValidateDiagnostic(level, diagnostic);
        if (level == PgLogLevel.Error)
        {
            return diagnostic.ToException();
        }

        var terminal = new PgTerminalException(level, diagnostic);
        NativeLog.RecordTerminal(terminal);
        return terminal;
    }

    /// <summary>
    /// Validates a report before filtering, native encoding or managed error construction.
    /// </summary>
    /// <param name="level">The reporting severity.</param>
    /// <param name="diagnostic">The message and optional SQLSTATE.</param>
    private static void ValidateDiagnostic(PgLogLevel level, PgDiagnostic diagnostic)
    {
        ArgumentNullException.ThrowIfNull(diagnostic);
        ValidateLevel(level);
        NativeLog.CheckAccess(terminal: level >= PgLogLevel.Error);
        if (diagnostic.SqlState is string state &&
            (state.Length != 5 || state.Any(static value => value is not (>= '0' and <= '9' or >= 'A' and <= 'Z')) ||
            (level >= PgLogLevel.Error && state == "00000")))
        {
            throw new ArgumentException("SQLSTATE must contain five uppercase ASCII letters or digits; errors cannot use 00000.",
                nameof(diagnostic));
        }
    }

    private static void ValidateLevel(PgLogLevel level)
    {
        if (level is < PgLogLevel.Debug5 or > PgLogLevel.Panic)
        {
            throw new ArgumentOutOfRangeException(nameof(level));
        }
    }
}
