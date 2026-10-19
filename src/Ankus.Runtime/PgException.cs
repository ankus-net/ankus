namespace Ankus;

/// <summary>
/// Represents a PostgreSQL error raised by a guarded server call or deliberately reported by an extension.
/// </summary>
public sealed class PgException : Exception
{
    /// <summary>
    /// Creates an external-routine error with a default message.
    /// </summary>
    public PgException() : this("PostgreSQL extension function failed.")
    {
    }

    /// <summary>
    /// Creates an external-routine error.
    /// </summary>
    /// <param name="message">The primary error message.</param>
    public PgException(string message) : this(message, innerException: null)
    {
    }

    /// <summary>
    /// Creates an external-routine error with a managed cause.
    /// </summary>
    /// <param name="message">The primary error message.</param>
    /// <param name="innerException">The original managed error.</param>
    public PgException(string message, Exception? innerException) : this(PgSqlStates.ExternalRoutineException, message, innerException: innerException)
    {
    }

    /// <summary>
    /// Creates an error with explicit PostgreSQL diagnostics.
    /// </summary>
    /// <param name="sqlState">The five-character uppercase SQLSTATE code.</param>
    /// <param name="message">The primary error message.</param>
    /// <param name="detail">Additional error detail.</param>
    /// <param name="hint">A suggested corrective action.</param>
    /// <param name="innerException">The original managed error, if any.</param>
    public PgException(string sqlState, string message, string? detail = null, string? hint = null, Exception? innerException = null)
        : base(message, innerException)
    {
        ArgumentNullException.ThrowIfNull(sqlState);
        if (sqlState.Length != 5 || sqlState.Any(static value => value is not (>= '0' and <= '9' or >= 'A' and <= 'Z')) ||
            sqlState == "00000")
        {
            throw new ArgumentException("An error SQLSTATE must contain five uppercase ASCII letters or digits and not be 00000.",
                nameof(sqlState));
        }

        SqlState = sqlState;
        Detail = detail;
        Hint = hint;
    }

    /// <summary>
    /// Gets the PostgreSQL SQLSTATE code.
    /// </summary>
    public string SqlState { get; }

    /// <summary>
    /// Gets additional error detail.
    /// </summary>
    public string? Detail { get; }

    /// <summary>
    /// Gets a suggested corrective action.
    /// </summary>
    public string? Hint { get; }

    /// <summary>
    /// Gets the PostgreSQL execution context, including SQL and procedural call frames when supplied by the server.
    /// </summary>
    public string? Context
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the schema associated with the error.
    /// </summary>
    public string? SchemaName
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the table associated with the error.
    /// </summary>
    public string? TableName
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the column associated with the error.
    /// </summary>
    public string? ColumnName
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the data type associated with the error.
    /// </summary>
    public string? DataTypeName
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the constraint associated with the error.
    /// </summary>
    public string? ConstraintName
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the one-based character position in the client query, or zero when no position is supplied.
    /// </summary>
    public int Position
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the one-based character position in InternalQuery, or zero when no internal position is supplied.
    /// </summary>
    public int InternalPosition
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the internally executed query associated with InternalPosition.
    /// </summary>
    public string? InternalQuery
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the source file that reported the error.
    /// </summary>
    public string? File
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the source line that reported the error, or zero when unavailable.
    /// </summary>
    public int Line
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the source routine that reported the error.
    /// </summary>
    public string? Routine
    {
        get;
        init;
    }

    /// <summary>
    /// Gets additional detail written only to the PostgreSQL server log. The client error response omits it.
    /// </summary>
    public string? DetailLog
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the native backtrace when PostgreSQL collected one for this error.
    /// </summary>
    public string? Backtrace
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the message domain PostgreSQL recorded for this error, such as an extension's own domain or PostgreSQL's
    /// <c>postgres-</c> domain, or null when it has not been reported.
    /// </summary>
    public string? Domain
    {
        get;
        init;
    }

    /// <summary>
    /// Gets whether allocating or encoding diagnostic transport failed, leaving only partially copied diagnostics.
    /// </summary>
    public bool DiagnosticsIncomplete => (NativeFlags & NativeErrorFlags.Incomplete) != 0;

    /// <summary>
    /// Gets the original server reporting flags for native rethrow without duplicating context callbacks.
    /// </summary>
    internal NativeErrorFlags NativeFlags
    {
        get;
        init;
    }
}
