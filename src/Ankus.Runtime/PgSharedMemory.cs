using System.Runtime.CompilerServices;

namespace Ankus;

/// <summary>
/// Registers unmanaged values in PostgreSQL's named shared memory during shared preload.
/// </summary>
/// <remarks>
/// Register static descriptors from a <see cref="PgModuleLoadAttribute"/> method. Initialization
/// runs when PostgreSQL creates shared memory, not while the descriptor is registered. Existing
/// storage is attached without running its initializer or overwriting its contents.
/// </remarks>
public static class PgSharedMemory
{
    /// <summary>
    /// Registers a zero-initialized immutable shared value or aggregate of atomic values.
    /// </summary>
    /// <typeparam name="T">The unmanaged aggregate type.</typeparam>
    /// <param name="storage">The static named shared descriptor.</param>
    public static void Initialize<T>(PgShared<T> storage) where T : unmanaged
        => Initialize(storage, static () => default);

    /// <summary>
    /// Registers a shared aggregate whose factory runs only when PostgreSQL creates its storage.
    /// </summary>
    /// <typeparam name="T">The unmanaged aggregate type.</typeparam>
    /// <param name="storage">The static named shared descriptor.</param>
    /// <param name="initializer">The factory run at shared-memory startup.</param>
    public static unsafe void Initialize<T>(PgShared<T> storage, Func<T> initializer) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(initializer);
        storage.Register((destination, length) =>
        {
            if (destination == 0 || length != (nuint)sizeof(T))
            {
                throw new InvalidOperationException("The PostgreSQL shared initializer has invalid value storage.");
            }

            T value = initializer();
            Unsafe.WriteUnaligned((void*)destination, value);
        });
    }

    /// <summary>
    /// Registers a zero-initialized atomic scalar.
    /// </summary>
    /// <typeparam name="T">The supported unmanaged scalar type.</typeparam>
    /// <param name="storage">The static named atomic descriptor.</param>
    public static void Initialize<T>(PgAtomic<T> storage) where T : unmanaged
        => Initialize(storage, static () => default);

    /// <summary>
    /// Registers an atomic scalar whose factory runs only when PostgreSQL creates its shared storage.
    /// </summary>
    /// <typeparam name="T">The supported unmanaged scalar type.</typeparam>
    /// <param name="storage">The static named atomic descriptor.</param>
    /// <param name="initializer">The factory run at shared-memory startup.</param>
    public static unsafe void Initialize<T>(PgAtomic<T> storage, Func<T> initializer) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(initializer);
        storage.Register((destination, length) =>
        {
            if (destination == 0 || length != (nuint)sizeof(T))
            {
                throw new InvalidOperationException("The PostgreSQL atomic initializer has invalid value storage.");
            }

            T value = initializer();
            Unsafe.WriteUnaligned((void*)destination, value);
        });
    }

    /// <summary>
    /// Registers a reader/writer lock with a zero-initialized value.
    /// </summary>
    /// <typeparam name="T">The unmanaged value shared by PostgreSQL processes.</typeparam>
    /// <param name="storage">The static named descriptor.</param>
    public static void Initialize<T>(PgLwLock<T> storage) where T : unmanaged
        => Initialize(storage, static () => default);

    /// <summary>
    /// Registers a reader/writer lock whose value is constructed at shared-memory startup.
    /// </summary>
    /// <typeparam name="T">The unmanaged value shared by PostgreSQL processes.</typeparam>
    /// <param name="storage">The static named descriptor.</param>
    /// <param name="initializer">The factory used only when creating the shared value.</param>
    /// <remarks>
    /// The value must contain no managed references or process-local addresses. The unmanaged
    /// constraint cannot establish that an embedded native address is valid in another process.
    /// Registration is idempotent for the same descriptor; another descriptor with the same name
    /// in one extension is rejected. Shared values and their locks live until PostgreSQL shuts down or recreates
    /// its shared memory after a backend crash; a replacement segment runs the initializer again.
    /// </remarks>
    public static unsafe void Initialize<T>(PgLwLock<T> storage, Func<T> initializer) where T : unmanaged
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(initializer);
        storage.Register((destination, length) =>
        {
            if (destination == 0 || length != (nuint)sizeof(T))
            {
                throw new InvalidOperationException("The PostgreSQL shared-memory initializer has invalid value storage.");
            }

            T value = initializer();
            Unsafe.WriteUnaligned((void*)destination, value);
        });
    }

    /// <summary>
    /// Registers a lock-protected header and array of the given length, all zero.
    /// </summary>
    /// <typeparam name="THeader">The unmanaged header.</typeparam>
    /// <typeparam name="TElement">The unmanaged element.</typeparam>
    /// <param name="storage">The static named descriptor.</param>
    /// <param name="length">The number of elements, such as a setting fixed at server start.</param>
    public static void Initialize<THeader, TElement>(PgLwLock<THeader, TElement> storage, int length)
        where THeader : unmanaged where TElement : unmanaged
        => Initialize(storage, length, static () => default, static _ => default);

    /// <summary>
    /// Registers a lock-protected header and array whose values are built at shared-memory startup.
    /// </summary>
    /// <typeparam name="THeader">The unmanaged header.</typeparam>
    /// <typeparam name="TElement">The unmanaged element.</typeparam>
    /// <param name="storage">The static named descriptor.</param>
    /// <param name="length">The number of elements, such as a setting fixed at server start.</param>
    /// <param name="header">The header's factory, run only when PostgreSQL creates the storage.</param>
    /// <param name="element">Each element's factory, given its index.</param>
    /// <remarks>
    /// Every process must register the same length. Register from a <see cref="PgModuleLoadAttribute"/> method, where
    /// settings such as <c>max_worker_processes</c> already have their server-start values.
    /// </remarks>
    public static unsafe void Initialize<THeader, TElement>(PgLwLock<THeader, TElement> storage, int length, Func<THeader> header,
        Func<int, TElement> element) where THeader : unmanaged where TElement : unmanaged
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(header);
        ArgumentNullException.ThrowIfNull(element);
        storage.Register(length, (destination, size) =>
        {
            if (destination == 0 || size != storage.Size)
            {
                throw new InvalidOperationException("The PostgreSQL shared-memory initializer has invalid array storage.");
            }

            Unsafe.WriteUnaligned((void*)destination, header());
            byte* elements = (byte*)destination + PgLwLock<THeader, TElement>.ElementOffset;
            for (int index = 0; index < storage.Length; index++)
            {
                Unsafe.WriteUnaligned(elements + ((nint)index * sizeof(TElement)), element(index));
            }
        });
    }
}
