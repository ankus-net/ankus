using System;

namespace Ankus;

/// <summary>
/// Releases temporary result owners without replacing an operation's primary failure.
/// </summary>
internal static class PgResultCleanup
{
    /// <summary>
    /// Disposes the owner, retaining primary and cleanup failures in their original order.
    /// </summary>
    /// <param name="owner">The temporary owner, or null for independent managed results.</param>
    /// <param name="primary">The operation's failure, already being propagated, or null on success.</param>
    internal static void Dispose(IDisposable? owner, Exception? primary)
    {
        try
        {
            owner?.Dispose();
        }
        catch (Exception cleanup) when (primary is not null)
        {
            throw new AggregateException("PostgreSQL result processing and cleanup failed.", primary, cleanup);
        }
    }
}
