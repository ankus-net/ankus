namespace Ankus;

/// <summary>
/// Reports a command that failed on the server at the other end of a <see cref="PgReplicationConnection"/>.
/// </summary>
/// <remarks>
/// The failure happened on the server, so this backend's transaction is unaffected and the connection stays usable.
/// </remarks>
public sealed class PgRemoteException : Exception
{
    /// <summary>
    /// Creates an exception with a generic message.
    /// </summary>
    public PgRemoteException()
        : this("The command failed on the server.")
    {
    }

    /// <summary>
    /// Creates an exception with the server's message.
    /// </summary>
    /// <param name="message">The server's message.</param>
    public PgRemoteException(string message)
        : this(null, message)
    {
    }

    /// <summary>
    /// Creates an exception with the server's message and an inner exception.
    /// </summary>
    /// <param name="message">The server's message.</param>
    /// <param name="innerException">The exception that caused this one.</param>
    public PgRemoteException(string message, Exception? innerException)
        : base(message, innerException)
    {
    }

    /// <summary>
    /// Creates an exception with the server's SQLSTATE and message.
    /// </summary>
    /// <param name="sqlState">The server's five-character SQLSTATE, or null when the server's version does not report
    /// it to PostgreSQL 13.</param>
    /// <param name="message">The server's message.</param>
    public PgRemoteException(string? sqlState, string message)
        : base(message) => SqlState = sqlState;

    /// <summary>
    /// Gets the server's five-character SQLSTATE, or null when it was not reported.
    /// </summary>
    public string? SqlState { get; }
}
