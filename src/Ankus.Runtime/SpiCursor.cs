using System;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Owns a PostgreSQL cursor and fetches independent managed or raw result batches.
/// Cursors opened through SPI live until closed or their transaction ends.
/// </summary>
public sealed class SpiCursor : IDisposable
{
    private readonly nint _backend;
    private bool _fetching;

    /// <summary>
    /// Creates managed ownership before allocating a native cursor.
    /// </summary>
    /// <param name="backend">The owning native backend binding.</param>
    internal SpiCursor(nint backend) => _backend = backend;

    /// <summary>
    /// Gets the PostgreSQL portal name, which can be used to find a detached cursor in the same transaction.
    /// </summary>
    public string Name
    {
        get;
        internal set;
    } = string.Empty;

    /// <summary>
    /// Gets or sets the native identity. Zero means this object owns no cursor.
    /// </summary>
    internal long Identity
    {
        get;
        set;
    }

    /// <summary>
    /// Fetches up to the requested number of rows in the forward direction.
    /// Zero uses PostgreSQL's current-row fetch semantics and may require a scrollable cursor.
    /// Fetching past the end returns an empty result.
    /// </summary>
    /// <param name="count">The nonnegative fetch count.</param>
    /// <returns>A materialized batch independent of the cursor's lifetime.</returns>
    public SpiResult Fetch(int count) => Fetch(count, forward: true);

    /// <summary>
    /// Fetches a batch in the requested direction. Backward fetching requires a scrollable PostgreSQL cursor.
    /// </summary>
    /// <param name="count">The nonnegative number of rows, or zero to fetch the current row.</param>
    /// <param name="forward">Whether to move forward; otherwise the cursor moves backward.</param>
    /// <returns>An independently owned managed batch.</returns>
    public SpiResult Fetch(int count, bool forward)
    {
        CheckAccess();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _fetching = true;
        try
        {
            return NativeBackend.FetchCursor(Identity, count, forward);
        }
        finally
        {
            _fetching = false;
        }
    }

    /// <summary>
    /// Fetches raw native values without requiring a managed mapping for their PostgreSQL types.
    /// </summary>
    /// <param name="count">The nonnegative fetch count, or zero to fetch the current row.</param>
    /// <returns>A disposable batch that survives cursor disposal and expires on disposal or callback-context cleanup.</returns>
    public SpiRawResult FetchRaw(int count) => FetchRaw(count, forward: true);

    /// <summary>
    /// Fetches raw native values in the requested direction. Backward fetching requires a scrollable cursor.
    /// </summary>
    /// <param name="count">The nonnegative fetch count, or zero to fetch the current row.</param>
    /// <param name="forward">Whether to move forward; otherwise the cursor moves backward.</param>
    /// <returns>A raw batch to dispose before leaving the backend callback.</returns>
    public SpiRawResult FetchRaw(int count, bool forward)
    {
        CheckAccess();
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        _fetching = true;
        try
        {
            return NativeBackend.FetchRawCursor(Identity, count, forward);
        }
        finally
        {
            _fetching = false;
        }
    }

    /// <summary>
    /// Relinquishes ownership without closing the portal and returns its name for a later Spi.FindCursor call.
    /// This managed cursor can no longer fetch rows after detaching.
    /// </summary>
    /// <returns>The PostgreSQL portal name.</returns>
    public string Detach()
    {
        CheckAccess();
        Identity = 0;
        return Name;
    }

    /// <summary>
    /// Closes the portal through the native guard. Repeated disposal and disposal after transaction end are harmless.
    /// Disposal requires the owning backend thread; no PostgreSQL calls run on the finalizer thread.
    /// </summary>
    public void Dispose()
    {
        if (Identity == 0)
        {
            return;
        }

        NativeBackend.CheckDisposalAccess(_backend);
        if (_fetching)
        {
            throw new InvalidOperationException("A cursor cannot be disposed recursively while it is fetching.");
        }

        NativeBackend.CloseCursor(Identity);
        Identity = 0;
    }

    private void CheckAccess()
    {
        ObjectDisposedException.ThrowIf(Identity == 0, this);
        NativeBackend.CheckAccess(_backend);
        if (_fetching)
        {
            throw new InvalidOperationException("A cursor cannot be accessed recursively while it is fetching.");
        }
    }
}
