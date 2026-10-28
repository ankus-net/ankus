using System;

namespace Ankus;

/// <summary>
/// Exports a synchronous static worker entry taking one native-sized unsigned argument and returning void.
/// </summary>
/// <remarks>
/// The native entry installs safe signal handlers and binds backend capabilities before invoking managed code.
/// Arguments carry values, never process-local addresses. Use shared memory for data exchanged with another process.
/// </remarks>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public sealed class PgBackgroundWorkerAttribute : Attribute
{
    /// <summary>
    /// Gets or sets the exported native symbol, defaulting to the managed method's name.
    /// </summary>
    public string? EntryPoint
    {
        get;
        set;
    }
}
