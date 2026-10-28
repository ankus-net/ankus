using System;
using System.Runtime.CompilerServices;
using Ankus.CompilerServices;

namespace Ankus;

/// <summary>
/// Reads an unmanaged value through a reference limited to the synchronous callback.
/// </summary>
/// <typeparam name="T">The unmanaged shared value.</typeparam>
/// <typeparam name="TResult">The owned result returned by the callback.</typeparam>
/// <param name="value">The shared value, valid only during this callback.</param>
/// <returns>The callback's owned result.</returns>
public delegate TResult PgSharedReader<T, out TResult>(scoped in T value) where T : unmanaged;

/// <summary>
/// Stores an unmanaged aggregate with immutable, atomic or spinlock-protected fields in PostgreSQL shared memory.
/// </summary>
/// <typeparam name="T">The unmanaged shared value.</typeparam>
/// <remarks>
/// Register a static descriptor during shared preload. Ordinary fields are immutable after initialization;
/// use inline atomic values or spinlocks for concurrent updates. Embedded addresses must be valid in every process.
/// Each synchronous read protects the native address against retirement until its callback returns.
/// </remarks>
public sealed class PgShared<T> where T : unmanaged
{
    private readonly NativeSharedMemoryRegistration _registration;

    /// <summary>
    /// Creates an unregistered shared-value descriptor with a cluster-wide name.
    /// </summary>
    /// <param name="name">A nonempty, exact UTF-8 name without a zero byte.</param>
    public PgShared(string name)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (name.Contains('\0', StringComparison.Ordinal))
        {
            throw new ArgumentException("A PostgreSQL shared-memory name cannot contain a zero byte.", nameof(name));
        }

        Name = name;
        _registration = new NativeSharedMemoryRegistration(name, typeof(T).AssemblyQualifiedName!, Unsafe.SizeOf<T>(), NativeSharedMemoryKind.Shared);
    }

    /// <summary>
    /// Gets the exact cluster-wide shared-memory name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Invokes a synchronous reader while protecting the current shared segment from retirement.
    /// </summary>
    /// <typeparam name="TResult">The owned callback result.</typeparam>
    /// <param name="reader">The reader, whose shared reference cannot escape the callback.</param>
    /// <returns>The callback result.</returns>
    /// <remarks>
    /// Access inline atomic fields directly through the readonly reference. Copying the aggregate or a
    /// field copies its state and subsequent updates affect that copy. Multiple atomic fields do not
    /// form a single transaction. Keep callbacks finite; shutdown waits for admitted readers to finish.
    /// Access inline spinlocks directly through this reference on the backend callback thread. Guards
    /// are released before the read returns, including exceptional exits, and cannot escape its admission.
    /// </remarks>
    public unsafe TResult Read<TResult>(PgSharedReader<T, TResult> reader)
    {
        ArgumentNullException.ThrowIfNull(reader);
        using NativeSharedMemoryAccessLease lease = _registration.Open();
        NativeSharedReadScope scope = default;
        NativeSharedReadScope.Push(&scope, _registration.Provider, lease.Value, (nuint)sizeof(T));
        try
        {
            return reader(in lease.GetReference<T>());
        }
        finally
        {
            NativeSharedReadScope.Pop(&scope);
        }
    }

    /// <summary>
    /// Registers the exact aggregate layout and deferred initializer.
    /// </summary>
    /// <param name="initializer">Writes the initial value into checked native storage.</param>
    internal void Register(Action<nint, nuint> initializer) => _registration.Register(initializer);
}
