using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Stores one atomic scalar in PostgreSQL shared memory for use across processes and managed threads.
/// </summary>
/// <typeparam name="T">A primitive or enum type supported by <see cref="Interlocked"/>.</typeparam>
/// <remarks>
/// Keep the descriptor in a static field and register it with <see cref="PgSharedMemory"/> during
/// shared preload. Operations use the strong ordering of .NET Interlocked and do not invoke
/// PostgreSQL. Each operation protects its address against shared-memory retirement. Values
/// survive transaction errors but are initialized again when PostgreSQL recreates shared memory.
/// </remarks>
public sealed class PgAtomic<T> where T : unmanaged
{
    private readonly NativeSharedMemoryRegistration _registration;

    /// <summary>
    /// Creates an unregistered atomic descriptor with a cluster-wide shared-memory name.
    /// </summary>
    /// <param name="name">A nonempty, exact UTF-8 name without a zero byte.</param>
    /// <exception cref="NotSupportedException">T is not a supported primitive or enum type.</exception>
    public PgAtomic(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A PostgreSQL shared-memory name cannot contain a zero byte.", nameof(name));
        }

        NativeAtomicScalar.Validate<T>();
        Name = name;
        _registration = new NativeSharedMemoryRegistration(name, typeof(T).AssemblyQualifiedName!, Unsafe.SizeOf<T>(), NativeSharedMemoryKind.Atomic);
    }

    /// <summary>
    /// Gets the exact cluster-wide shared-memory name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets or replaces the scalar atomically, preserving its exact bits.
    /// </summary>
    public T Value
    {
        get
        {
            using NativeSharedMemoryAccessLease lease = Open();
            return Interlocked.CompareExchange(ref lease.GetReference<T>(), default, default);
        }
        set => Exchange(value);
    }

    /// <summary>
    /// Atomically replaces the scalar and returns its previous value.
    /// </summary>
    /// <param name="value">The replacement value.</param>
    /// <returns>The value before replacement.</returns>
    public T Exchange(T value)
    {
        using NativeSharedMemoryAccessLease lease = Open();
        return Interlocked.Exchange(ref lease.GetReference<T>(), value);
    }

    /// <summary>
    /// Atomically replaces the scalar when its bits equal the comparand and returns the previous value.
    /// </summary>
    /// <param name="value">The replacement value when comparison succeeds.</param>
    /// <param name="comparand">The exact bit pattern required for replacement.</param>
    /// <returns>The value before comparison, whether or not replacement occurred.</returns>
    public T CompareExchange(T value, T comparand)
    {
        using NativeSharedMemoryAccessLease lease = Open();
        return Interlocked.CompareExchange(ref lease.GetReference<T>(), value, comparand);
    }

    /// <summary>
    /// Registers the exact layout and process-local deferred initializer.
    /// </summary>
    /// <param name="initializer">Writes the initial value into checked native storage.</param>
    internal void Register(Action<nint, nuint> initializer) => _registration.Register(initializer);

    /// <summary>
    /// Admits one bounded operation on the current shared segment.
    /// </summary>
    /// <returns>The operation's address lease.</returns>
    internal NativeSharedMemoryAccessLease Open() => _registration.Open();
}
