using System.Runtime.CompilerServices;

namespace Ankus;

/// <summary>
/// Reads a lock-protected header and its elements through references limited to the synchronous callback.
/// </summary>
/// <typeparam name="THeader">The unmanaged header.</typeparam>
/// <typeparam name="TElement">The unmanaged element.</typeparam>
/// <typeparam name="TResult">The owned result returned by the callback.</typeparam>
/// <param name="header">The original header, valid only during this callback.</param>
/// <param name="elements">The original elements, valid only during this callback.</param>
/// <returns>The callback's owned result.</returns>
public delegate TResult PgSharedArrayReader<THeader, TElement, out TResult>(scoped in THeader header, scoped ReadOnlySpan<TElement> elements)
    where THeader : unmanaged where TElement : unmanaged;

/// <summary>
/// Updates a lock-protected header and its elements through references limited to the synchronous callback.
/// </summary>
/// <typeparam name="THeader">The unmanaged header.</typeparam>
/// <typeparam name="TElement">The unmanaged element.</typeparam>
/// <typeparam name="TResult">The owned result returned by the callback.</typeparam>
/// <param name="header">The original header, valid only during this callback.</param>
/// <param name="elements">The original elements, valid only during this callback.</param>
/// <returns>The callback's owned result.</returns>
/// <remarks>Writes take effect immediately, including when the callback throws.</remarks>
public delegate TResult PgSharedArrayMutator<THeader, TElement, out TResult>(scoped ref THeader header, scoped Span<TElement> elements)
    where THeader : unmanaged where TElement : unmanaged;

/// <summary>
/// Describes a header and an array of elements in shared memory, protected by one PostgreSQL lightweight lock, whose
/// length is chosen when the extension registers it.
/// </summary>
/// <typeparam name="THeader">The unmanaged header, such as counters for the whole array.</typeparam>
/// <typeparam name="TElement">The unmanaged element, such as one worker's slot.</typeparam>
/// <remarks>
/// <para>
/// A C extension sizes such arrays from settings that are fixed at server start, such as one slot per
/// <c>max_worker_processes</c>; pgrx's shared values have a size fixed at compile time. Register the descriptor from a
/// <see cref="PgModuleLoadAttribute"/> method with <see cref="PgSharedMemory"/> and the length. Every process must
/// compute the same length; on Windows each backend loads the library again and attaches to the existing storage, which
/// fails if the size differs.
/// </para>
/// <para>
/// Elements start at the first 8-byte boundary after the header and follow each other without padding, as a C array
/// does. Guards follow the rules of <see cref="PgLwLock{T}"/>: they belong to the acquiring callback and thread, and
/// callback references cannot escape the callback.
/// </para>
/// </remarks>
public sealed class PgLwLock<THeader, TElement> where THeader : unmanaged where TElement : unmanaged
{
    private readonly string _identity = typeof(THeader).AssemblyQualifiedName + "[" + typeof(TElement).AssemblyQualifiedName + "]";

    private NativeSharedMemoryRegistration? _registration;

    /// <summary>
    /// Creates an unregistered descriptor with a cluster-wide shared-memory name.
    /// </summary>
    /// <param name="name">A nonempty, exact UTF-8 name without a zero byte.</param>
    public PgLwLock(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A PostgreSQL shared-memory name cannot contain a zero byte.", nameof(name));
        }

        Name = name;
    }

    /// <summary>
    /// Gets the exact name used by PostgreSQL's shared-memory index and named lock tranche.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the number of elements, or zero before registration.
    /// </summary>
    public int Length { get; private set; }

    /// <summary>
    /// Gets the elements' offset from the header's start: the first 8-byte boundary after the header.
    /// </summary>
    internal static int ElementOffset => (Unsafe.SizeOf<THeader>() + 7) & ~7;

    /// <summary>
    /// Gets the registered size in bytes.
    /// </summary>
    internal nuint Size => (nuint)ElementOffset + ((nuint)Length * (nuint)Unsafe.SizeOf<TElement>());

    private NativeSharedMemoryRegistration Registration
        => _registration ?? throw new InvalidOperationException("Register the PostgreSQL shared descriptor during shared preload before accessing it.");

    /// <summary>
    /// Acquires shared read access, waiting for an exclusive writer to release the lock.
    /// </summary>
    /// <returns>A callback-scoped shared guard.</returns>
    public PgLwLockShareGuard<THeader, TElement> Share() => new(Registration.Acquire(exclusive: false), Length, Size);

    /// <summary>
    /// Acquires exclusive read/write access, waiting for all other holders to release the lock.
    /// </summary>
    /// <returns>A callback-scoped exclusive guard.</returns>
    public PgLwLockExclusiveGuard<THeader, TElement> Exclusive() => new(Registration.Acquire(exclusive: true), Length, Size);

    /// <summary>
    /// Registers the layout, its length and the process-local initializer.
    /// </summary>
    /// <param name="length">The number of elements.</param>
    /// <param name="initializer">Writes the initial header and elements after validating the native destination.</param>
    internal void Register(int length, Action<nint, nuint> initializer)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (_registration is not null)
        {
            if (length != Length)
            {
                throw new InvalidOperationException($"Shared array '{Name}' is already registered with {Length} elements.");
            }

            _registration.Register(initializer);
            return;
        }

        long size = ElementOffset + ((long)length * Unsafe.SizeOf<TElement>());
        if (size > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(length), length, "The shared array does not fit in 2 GB.");
        }

        var registration = new NativeSharedMemoryRegistration(Name, _identity, (int)size);
        registration.Register(initializer);
        _registration = registration;
        Length = length;
    }
}

/// <summary>
/// Holds shared read access to a lock-protected header and its elements until disposal or callback exit.
/// </summary>
/// <typeparam name="THeader">The unmanaged header.</typeparam>
/// <typeparam name="TElement">The unmanaged element.</typeparam>
public sealed class PgLwLockShareGuard<THeader, TElement> : IDisposable where THeader : unmanaged where TElement : unmanaged
{
    private readonly NativeSharedMemoryLease _lease;

    private readonly nuint _size;

    /// <summary>
    /// Owns one acquired native lease.
    /// </summary>
    internal PgLwLockShareGuard(NativeSharedMemoryLease lease, int length, nuint size)
    {
        _lease = lease;
        Length = length;
        _size = size;
    }

    /// <summary>
    /// Gets the number of elements.
    /// </summary>
    public int Length { get; }

    /// <summary>
    /// Gets a copy of the header while this guard holds the lock.
    /// </summary>
    public THeader Header => _lease.ReadBlock<NativeSharedArrayHeader<THeader>, THeader>(_size, new(default, write: false));

    /// <summary>
    /// Gets a copy of an element while this guard holds the lock.
    /// </summary>
    /// <param name="index">The zero-based element index.</param>
    /// <returns>The element.</returns>
    public TElement this[int index]
        => _lease.ReadBlock<NativeSharedArrayElement<THeader, TElement>, TElement>(_size, new(NativeSharedArray.Check(index, Length), default, write: false));

    /// <summary>
    /// Reads the original header and elements through references limited to the synchronous callback.
    /// </summary>
    /// <typeparam name="TResult">The owned callback result.</typeparam>
    /// <param name="reader">The reader, whose references cannot escape the callback.</param>
    /// <returns>The callback result.</returns>
    public TResult Read<TResult>(PgSharedArrayReader<THeader, TElement, TResult> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return _lease.ReadBlock<NativeSharedArrayReader<THeader, TElement, TResult>, TResult>(_size, new(reader, Length));
    }

    /// <summary>
    /// Releases this guard once.
    /// </summary>
    public void Dispose() => _lease.Dispose();
}

/// <summary>
/// Holds exclusive read/write access to a lock-protected header and its elements until disposal or callback exit.
/// </summary>
/// <typeparam name="THeader">The unmanaged header.</typeparam>
/// <typeparam name="TElement">The unmanaged element.</typeparam>
/// <remarks>Writes take effect immediately and are not rolled back by a transaction abort or exception.</remarks>
public sealed class PgLwLockExclusiveGuard<THeader, TElement> : IDisposable where THeader : unmanaged where TElement : unmanaged
{
    private readonly NativeSharedMemoryLease _lease;

    private readonly nuint _size;

    /// <summary>
    /// Owns one acquired native lease.
    /// </summary>
    internal PgLwLockExclusiveGuard(NativeSharedMemoryLease lease, int length, nuint size)
    {
        _lease = lease;
        Length = length;
        _size = size;
    }

    /// <summary>
    /// Gets the number of elements.
    /// </summary>
    public int Length { get; }

    /// <summary>
    /// Gets or sets a copy of the header while this guard holds the lock.
    /// </summary>
    public THeader Header
    {
        get => _lease.ReadBlock<NativeSharedArrayHeader<THeader>, THeader>(_size, new(default, write: false));
        set => _lease.MutateBlock<NativeSharedArrayHeader<THeader>, THeader>(_size, new(value, write: true));
    }

    /// <summary>
    /// Gets or sets a copy of an element while this guard holds the lock.
    /// </summary>
    /// <param name="index">The zero-based element index.</param>
    /// <returns>The element.</returns>
    public TElement this[int index]
    {
        get => _lease.ReadBlock<NativeSharedArrayElement<THeader, TElement>, TElement>(_size, new(NativeSharedArray.Check(index, Length), default, write: false));
        set => _lease.MutateBlock<NativeSharedArrayElement<THeader, TElement>, TElement>(_size,
            new(NativeSharedArray.Check(index, Length), value, write: true));
    }

    /// <summary>
    /// Reads the original header and elements through references limited to the synchronous callback.
    /// </summary>
    /// <typeparam name="TResult">The owned callback result.</typeparam>
    /// <param name="reader">The reader, whose references cannot escape the callback.</param>
    /// <returns>The callback result.</returns>
    public TResult Read<TResult>(PgSharedArrayReader<THeader, TElement, TResult> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        return _lease.ReadBlock<NativeSharedArrayReader<THeader, TElement, TResult>, TResult>(_size, new(reader, Length));
    }

    /// <summary>
    /// Updates the original header and elements through references limited to the synchronous callback.
    /// </summary>
    /// <typeparam name="TResult">The owned callback result.</typeparam>
    /// <param name="mutator">The mutator, whose references cannot escape the callback.</param>
    /// <returns>The callback result.</returns>
    public TResult Mutate<TResult>(PgSharedArrayMutator<THeader, TElement, TResult> mutator)
    {
        ArgumentNullException.ThrowIfNull(mutator);
        return _lease.MutateBlock<NativeSharedArrayMutator<THeader, TElement, TResult>, TResult>(_size, new(mutator, Length));
    }

    /// <summary>
    /// Releases this guard once.
    /// </summary>
    public void Dispose() => _lease.Dispose();
}
