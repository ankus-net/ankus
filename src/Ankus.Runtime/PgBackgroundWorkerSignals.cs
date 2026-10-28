using System;

namespace Ankus;

/// <summary>
/// Selects native signals observed by an Ankus background-worker entry.
/// </summary>
[Flags]
public enum PgBackgroundWorkerSignals
{
    /// <summary>
    /// No signal has been selected or observed.
    /// </summary>
    None = 0,
    /// <summary>
    /// Configuration reload was requested through SIGHUP.
    /// </summary>
    Reload = 1,
    /// <summary>
    /// Cooperative shutdown was requested through SIGTERM.
    /// </summary>
    Terminate = 2,
    /// <summary>
    /// SIGINT was received.
    /// </summary>
    Interrupt = 4,
    /// <summary>
    /// A child-process transition was reported through SIGCHLD.
    /// </summary>
    Child = 8,
}
