namespace Ankus;

/// <summary>
/// Describes how <see cref="PgReplicationConnection.Receive"/> ended.
/// </summary>
public enum PgReplicationReceiveStatus
{
    /// <summary>
    /// A message arrived.
    /// </summary>
    Message,

    /// <summary>
    /// No message arrived before the timeout.
    /// </summary>
    TimedOut,

    /// <summary>
    /// The backend's latch was set before a message arrived, for example to ask a worker to stop.
    /// </summary>
    Woken,

    /// <summary>
    /// The server ended the copy.
    /// </summary>
    Ended,
}
