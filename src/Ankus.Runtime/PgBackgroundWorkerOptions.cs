using System;

namespace Ankus;

/// <summary>
/// Describes an independently registered PostgreSQL background-worker process.
/// </summary>
/// <param name="name">The worker's display name.</param>
/// <param name="library">The PostgreSQL library containing the native entry.</param>
/// <param name="entryPoint">The exact exported native entry symbol.</param>
public sealed class PgBackgroundWorkerOptions(string name, string library, string entryPoint)
{
    private PgBackgroundWorkerStartTime? _startTime;

    /// <summary>
    /// Gets the worker's display name.
    /// </summary>
    public string Name { get; } = name;

    /// <summary>
    /// Gets the PostgreSQL library name or path.
    /// </summary>
    public string Library { get; } = library;

    /// <summary>
    /// Gets the exact native entry symbol.
    /// </summary>
    public string EntryPoint { get; } = entryPoint;

    /// <summary>
    /// Gets the worker type reported by PostgreSQL, defaulting to its name.
    /// </summary>
    public string Type
    {
        get;
        init;
    } = name;

    /// <summary>
    /// Gets whether the worker may initialize a database connection.
    /// </summary>
    public bool DatabaseAccess
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the startup phase, defaulting to recovery completion for database workers and postmaster start otherwise.
    /// </summary>
    public PgBackgroundWorkerStartTime StartTime
    {
        get => _startTime ?? (DatabaseAccess ? PgBackgroundWorkerStartTime.RecoveryFinished : PgBackgroundWorkerStartTime.PostmasterStart);
        init => _startTime = value;
    }

    /// <summary>
    /// Gets the delay before restarting a failed worker, or null to disable automatic restart.
    /// </summary>
    /// <remarks>
    /// PostgreSQL accepts whole seconds. Zero requests immediate restart; successful exits do not restart.
    /// </remarks>
    public TimeSpan? RestartDelay
    {
        get;
        init;
    }

    /// <summary>
    /// Gets the exact by-value native Datum word delivered to the entry.
    /// </summary>
    public nuint Argument
    {
        get;
        init;
    }

    /// <summary>
    /// Gets optional UTF-8 text copied into the worker's fixed extra-data field.
    /// </summary>
    public string Extra
    {
        get;
        init;
    } = string.Empty;

    /// <summary>
    /// Gets the backend PID to notify when a dynamic worker starts or stops; zero disables notifications.
    /// </summary>
    /// <remarks>
    /// Set this to the registering backend's PID when waiting for startup or shutdown. Static workers require zero.
    /// </remarks>
    public int NotifyProcessId
    {
        get;
        init;
    }
}
