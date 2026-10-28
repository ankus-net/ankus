using System;

namespace Ankus;

/// <summary>
/// Describes an owned PostgreSQL message with structured diagnostics for client and server reporting.
/// </summary>
public sealed class PgDiagnostic
{
    /// <summary>
    /// Creates a message whose SQLSTATE defaults to PostgreSQL's severity-dependent code.
    /// </summary>
    /// <param name="message">The primary message, treated as literal text.</param>
    public PgDiagnostic(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        Message = message;
    }

    /// <summary>
    /// Gets the primary message.
    /// </summary>
    public string Message { get; }

    /// <summary>
    /// Gets the optional five-character uppercase ASCII SQLSTATE.
    /// </summary>
    public string? SqlState
    {
        get;
        init;
    }

    /// <summary>
    /// Gets additional detail for clients and the server log.
    /// </summary>
    public string? Detail
    {
        get;
        init;
    }

    /// <summary>
    /// Gets a suggested corrective action.
    /// </summary>
    public string? Hint
    {
        get;
        init;
    }

    /// <summary>
    /// Gets additional execution context.
    /// </summary>
    public string? Context
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the associated schema name.
    /// </summary>
    public string? SchemaName
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the associated table name.
    /// </summary>
    public string? TableName
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the associated column name.
    /// </summary>
    public string? ColumnName
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the associated data type name.
    /// </summary>
    public string? DataTypeName
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the associated constraint name.
    /// </summary>
    public string? ConstraintName
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the one-based character position in the client query.
    /// </summary>
    public int Position
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the internally executed query.
    /// </summary>
    public string? InternalQuery
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the one-based character position in InternalQuery.
    /// </summary>
    public int InternalPosition
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the source file name when explicitly supplied by the extension.
    /// </summary>
    public string? File
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the source line number.
    /// </summary>
    public int Line
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the source routine name.
    /// </summary>
    public string? Routine
    {
        get;
        init;
    }

    /// <summary>
    /// Gets detail which replaces Detail in the server log and is never sent to clients.
    /// </summary>
    public string? DetailLog
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the message domain PostgreSQL records for the report, as C's <c>ereport_domain</c> and pgrx's
    /// <c>ereport_domain!</c> supply, or null for PostgreSQL's own domain.
    /// </summary>
    /// <remarks>
    /// The domain names the gettext catalog for message translation. PostgreSQL stores it with the error, so callers
    /// that catch the report read it back from <see cref="PgException.Domain"/>.
    /// </remarks>
    public string? Domain
    {
        get;
        init;
    }

    /// <summary>
    /// Creates an ERROR exception preserving every diagnostic field, defaulting an unspecified SQLSTATE to XX000.
    /// </summary>
    /// <returns>The exception to unwind through managed code before native reporting.</returns>
    internal PgException ToException() => new(SqlState ?? PgSqlStates.InternalError, Message, Detail, Hint)
    {
        Context = Context,
        SchemaName = SchemaName,
        TableName = TableName,
        ColumnName = ColumnName,
        DataTypeName = DataTypeName,
        ConstraintName = ConstraintName,
        Position = Position,
        InternalQuery = InternalQuery,
        InternalPosition = InternalPosition,
        File = File,
        Line = Line,
        Routine = Routine,
        DetailLog = DetailLog,
        Domain = Domain,
    };
}
