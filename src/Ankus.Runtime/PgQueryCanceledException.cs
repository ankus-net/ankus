using System;

namespace Ankus;

/// <summary>
/// Represents PostgreSQL query cancellation, which remains pending until the managed callback returns.
/// </summary>
/// <remarks>
/// Catching this exception permits managed cleanup but cannot turn a canceled query into success.
/// The native boundary preserves SQLSTATE 57014 and the original PostgreSQL diagnostics.
/// A worker's <see cref="PgBackgroundWorker.RunTransaction(Action)"/> aborts before returning this exception
/// to its caller, which may then start a new transaction.
/// </remarks>
public sealed class PgQueryCanceledException : OperationCanceledException
{
    /// <summary>
    /// Creates a query cancellation with the default message.
    /// </summary>
    public PgQueryCanceledException() : this("PostgreSQL query was canceled.")
    {
    }

    /// <summary>
    /// Creates a query cancellation with a message.
    /// </summary>
    /// <param name="message">The cancellation message.</param>
    public PgQueryCanceledException(string message) : this(message, null)
    {
    }

    /// <summary>
    /// Creates a query cancellation with a managed cause.
    /// </summary>
    /// <param name="message">The cancellation message.</param>
    /// <param name="innerException">The original cause, if any.</param>
    public PgQueryCanceledException(string message, Exception? innerException)
        : this(new PgException(PgSqlStates.QueryCanceled, message, innerException: innerException))
    {
    }

    /// <summary>
    /// Retains complete owned diagnostics captured by the native guard.
    /// </summary>
    /// <param name="diagnostic">The original PostgreSQL cancellation.</param>
    internal PgQueryCanceledException(PgException diagnostic) : base(diagnostic.Message, diagnostic.InnerException)
    {
        Diagnostic = diagnostic;
    }

    /// <summary>
    /// Gets the original SQLSTATE, message, positions and optional PostgreSQL diagnostic fields.
    /// </summary>
    public PgException Diagnostic { get; }
}
