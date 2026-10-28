using System;

namespace Ankus;

/// <summary>
/// Carries a terminal report to the native dispatcher after managed finally blocks have executed.
/// </summary>
internal sealed class PgTerminalException(PgLogLevel level, PgDiagnostic diagnostic) : Exception(diagnostic.Message)
{
    /// <summary>
    /// Gets the FATAL or PANIC severity to report after managed unwinding.
    /// </summary>
    internal PgLogLevel Level { get; } = level;

    /// <summary>
    /// Gets the diagnostic fields to carry to the native reporting boundary.
    /// </summary>
    internal PgDiagnostic Diagnostic { get; } = diagnostic;
}
