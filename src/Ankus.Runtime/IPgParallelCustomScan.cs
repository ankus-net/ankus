namespace Ankus;

/// <summary>
/// Runs a custom scan in parallel: PostgreSQL's workers and the leader each read part of the rows, coordinating through
/// shared memory the provider lays out as <typeparamref name="TShared"/>.
/// </summary>
/// <typeparam name="TState">The per-scan state, one in each process that runs the scan.</typeparam>
/// <typeparam name="TShared">
/// The shared memory's layout. Use <see cref="PgAtomicValue{T}"/> fields for values processes update concurrently.
/// </typeparam>
/// <remarks>
/// Offer parallel paths with <see cref="PgCustomScanPlanner.AddParallelPath"/>. A provider that implements this interface
/// declares that its scans can run in parallel workers, so its other paths can run there too. PostgreSQL can still run a
/// parallel plan without workers, for example inside a cursor; the scan then gets no shared memory and must produce all
/// the rows itself.
/// </remarks>
public interface IPgParallelCustomScan<TState, TShared> : IPgCustomScan<TState>
    where TState : class
    where TShared : unmanaged
{
    /// <summary>
    /// Initializes the shared memory in the leader before workers start, and again before a rescan starts new ones.
    /// </summary>
    /// <param name="state">The leader's state.</param>
    /// <param name="memory">The shared memory, zeroed each time.</param>
    static abstract void InitializeShared(TState state, ref TShared memory);

    /// <summary>
    /// Gives a process's scan the shared memory before its first read, in the leader after
    /// <see cref="InitializeShared"/> and in each worker after <see cref="IPgCustomScan{TState}.Begin"/>.
    /// </summary>
    /// <param name="state">This process's state.</param>
    /// <param name="memory">The shared memory, valid until the scan ends.</param>
    static abstract void AttachShared(TState state, PgCustomScanSharedMemory<TShared> memory);
}
