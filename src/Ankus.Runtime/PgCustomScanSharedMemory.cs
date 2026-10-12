namespace Ankus;

/// <summary>
/// Gives one process's parallel custom scan the shared memory the leader and workers coordinate through.
/// </summary>
/// <typeparam name="TShared">The shared memory's layout.</typeparam>
/// <remarks>
/// PostgreSQL owns the memory and frees it when the parallel scan ends; the handle stops working at that point.
/// </remarks>
public sealed unsafe class PgCustomScanSharedMemory<TShared> : IPgCustomScanSharedMemory
    where TShared : unmanaged
{
    private TShared* _address;

    /// <summary>
    /// Wraps PostgreSQL's dynamic shared memory for the scan.
    /// </summary>
    /// <param name="address">The memory's address, aligned for any value.</param>
    internal PgCustomScanSharedMemory(TShared* address) => _address = address;

    /// <summary>
    /// Gets the shared memory. Update fields that several processes write with <see cref="PgAtomicValue{T}"/>
    /// operations.
    /// </summary>
    /// <exception cref="ObjectDisposedException">The parallel scan has ended.</exception>
    public ref TShared Value
    {
        get
        {
            ObjectDisposedException.ThrowIf(_address == null, this);
            return ref *_address;
        }
    }

    /// <inheritdoc />
    void IPgCustomScanSharedMemory.Detach() => _address = null;
}

/// <summary>
/// Detaches a parallel custom scan's shared memory without knowing its layout.
/// </summary>
internal interface IPgCustomScanSharedMemory
{
    /// <summary>
    /// Stops the handle from reaching memory PostgreSQL is about to free.
    /// </summary>
    void Detach();
}
