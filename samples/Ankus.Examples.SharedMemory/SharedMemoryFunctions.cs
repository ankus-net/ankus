using Ankus.Postgres;

namespace Ankus.Examples.SharedMemory;

/// <summary>
/// Ports pgrx's shared-memory example: bounded collections, a struct and a primitive behind lightweight locks,
/// plus an atomic Boolean.
/// </summary>
/// <remarks>
/// PostgreSQL must load the library through <c>shared_preload_libraries</c>. Values are shared by every backend
/// and survive SQL rollback, but not a server restart or crash recovery.
/// </remarks>
public static class SharedMemoryFunctions
{
    private static readonly PgLwLock<PgtestDeque> s_deque = new("ankus_shared_memory.deque");
    private static readonly PgLwLock<PgtestVec> s_vec = new("ankus_shared_memory.vec");
    private static readonly PgLwLock<PgtestHash> s_hash = new("ankus_shared_memory.hash");
    private static readonly PgLwLock<Pgtest> s_struct = new("ankus_shared_memory.struct");
    private static readonly PgLwLock<int> s_primitive = new("ankus_shared_memory.primitive");
    private static readonly PgAtomic<bool> s_atomic = new("ankus_shared_memory.atomic");

    /// <summary>
    /// Reserves and initializes every shared value while PostgreSQL preloads the library.
    /// </summary>
    [PgModuleLoad]
    public static void Initialize()
    {
        bool preloading;
        unsafe
        {
            preloading = NativeGlobals.process_shared_preload_libraries_in_progress;
        }

        if (!preloading)
        {
            PgLog.Error("this extension must be loaded via shared_preload_libraries.");
        }

        PgSharedMemory.Initialize(s_deque);
        PgSharedMemory.Initialize(s_vec);
        PgSharedMemory.Initialize(s_hash);
        PgSharedMemory.Initialize(s_struct);
        PgSharedMemory.Initialize(s_primitive);
        PgSharedMemory.Initialize(s_atomic);
    }

    /// <summary>
    /// Returns a snapshot of the shared list in insertion order.
    /// </summary>
    /// <returns>The list's values.</returns>
    [PgFunction]
    public static IEnumerable<Pgtest> VecSelect()
    {
        using PgLwLockShareGuard<PgtestVec> guard = s_vec.Share();
        PgtestVec copy = guard.Value;
        return copy.Items().ToArray();
    }

    /// <summary>
    /// Counts the shared list's values.
    /// </summary>
    /// <returns>The number of stored values.</returns>
    [PgFunction]
    public static int VecCount()
    {
        using PgLwLockShareGuard<PgtestVec> guard = s_vec.Share();
        PgtestVec copy = guard.Value;
        return copy.Items().Count;
    }

    /// <summary>
    /// Removes and returns every value in the shared list under one exclusive lock.
    /// </summary>
    /// <returns>The removed values in insertion order.</returns>
    [PgFunction]
    public static IEnumerable<Pgtest> VecDrain()
    {
        using PgLwLockExclusiveGuard<PgtestVec> guard = s_vec.Exclusive();
        return guard.Mutate(static (ref vec) => vec.Items().Drain());
    }

    /// <summary>
    /// Appends a value, warning instead of failing when the list is full.
    /// </summary>
    /// <param name="value">The value to append.</param>
    [PgFunction]
    public static void VecPush(Pgtest value)
    {
        bool added;
        using (PgLwLockExclusiveGuard<PgtestVec> guard = s_vec.Exclusive())
        {
            added = guard.Mutate((ref vec) => vec.Items().TryAdd(value));
        }

        if (!added)
        {
            PgLog.Warning("Vector is full, discarding update");
        }
    }

    /// <summary>
    /// Removes the most recently appended value.
    /// </summary>
    /// <returns>The removed value, or SQL NULL when the list is empty.</returns>
    [PgFunction]
    public static Pgtest? VecPop()
    {
        using PgLwLockExclusiveGuard<PgtestVec> guard = s_vec.Exclusive();
        return guard.Mutate(static (ref vec) => vec.Items().TryPop(out Pgtest value) ? value : (Pgtest?)null);
    }

    /// <summary>
    /// Returns a snapshot of the shared deque from front to back.
    /// </summary>
    /// <returns>The deque's values.</returns>
    [PgFunction]
    public static IEnumerable<Pgtest> DequeSelect()
    {
        using PgLwLockShareGuard<PgtestDeque> guard = s_deque.Share();
        PgtestDeque copy = guard.Value;
        return copy.Items().ToArray();
    }

    /// <summary>
    /// Counts the shared deque's values.
    /// </summary>
    /// <returns>The number of stored values.</returns>
    [PgFunction]
    public static int DequeCount()
    {
        using PgLwLockShareGuard<PgtestDeque> guard = s_deque.Share();
        PgtestDeque copy = guard.Value;
        return copy.Items().Count;
    }

    /// <summary>
    /// Removes and returns every value in the shared deque under one exclusive lock.
    /// </summary>
    /// <returns>The removed values from front to back.</returns>
    [PgFunction]
    public static IEnumerable<Pgtest> DequeDrain()
    {
        using PgLwLockExclusiveGuard<PgtestDeque> guard = s_deque.Exclusive();
        return guard.Mutate(static (ref deque) => deque.Items().Drain());
    }

    /// <summary>
    /// Adds a value at the back, warning instead of failing when the deque is full.
    /// </summary>
    /// <param name="value">The value to add.</param>
    [PgFunction]
    public static void DequePushBack(Pgtest value) => DequePush(value, front: false);

    /// <summary>
    /// Adds a value at the front, warning instead of failing when the deque is full.
    /// </summary>
    /// <param name="value">The value to add.</param>
    [PgFunction]
    public static void DequePushFront(Pgtest value) => DequePush(value, front: true);

    /// <summary>
    /// Removes the value at the back of the deque.
    /// </summary>
    /// <returns>The removed value, or SQL NULL when the deque is empty.</returns>
    [PgFunction]
    public static Pgtest? DequePopBack()
    {
        using PgLwLockExclusiveGuard<PgtestDeque> guard = s_deque.Exclusive();
        return guard.Mutate(static (ref deque) => deque.Items().TryPopBack(out Pgtest value) ? value : (Pgtest?)null);
    }

    /// <summary>
    /// Removes the value at the front of the deque.
    /// </summary>
    /// <returns>The removed value, or SQL NULL when the deque is empty.</returns>
    [PgFunction]
    public static Pgtest? DequePopFront()
    {
        using PgLwLockExclusiveGuard<PgtestDeque> guard = s_deque.Exclusive();
        return guard.Mutate(static (ref deque) => deque.Items().TryPopFront(out Pgtest value) ? value : (Pgtest?)null);
    }

    /// <summary>
    /// Inserts or replaces a map entry; inserting a fifth distinct key fails because the map holds four entries.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <param name="value">The value.</param>
    [PgFunction]
    public static void HashInsert(int key, int value)
    {
        using PgLwLockExclusiveGuard<PgtestHash> guard = s_hash.Exclusive();
        _ = guard.Mutate((ref hash) => hash.Items().Set(key, value));
    }

    /// <summary>
    /// Reads a map entry.
    /// </summary>
    /// <param name="key">The key.</param>
    /// <returns>The stored value, or SQL NULL when the key is absent.</returns>
    [PgFunction]
    public static int? HashGet(int key)
    {
        using PgLwLockShareGuard<PgtestHash> guard = s_hash.Share();
        PgtestHash copy = guard.Value;
        return copy.Items().TryGetValue(key, out int value) ? value : null;
    }

    /// <summary>
    /// Reads the shared struct.
    /// </summary>
    /// <returns>The current value, initially zero in both fields.</returns>
    [PgFunction]
    public static Pgtest StructGet()
    {
        using PgLwLockShareGuard<Pgtest> guard = s_struct.Share();
        return guard.Value;
    }

    /// <summary>
    /// Replaces the shared struct.
    /// </summary>
    /// <param name="value1">The first value.</param>
    /// <param name="value2">The second value.</param>
    [PgFunction]
    public static void StructSet(int value1, int value2)
    {
        using PgLwLockExclusiveGuard<Pgtest> guard = s_struct.Exclusive();
        guard.Value = new Pgtest(value1, value2);
    }

    /// <summary>
    /// Reads the shared integer.
    /// </summary>
    /// <returns>The current value, initially zero.</returns>
    [PgFunction]
    public static int PrimitiveGet()
    {
        using PgLwLockShareGuard<int> guard = s_primitive.Share();
        return guard.Value;
    }

    /// <summary>
    /// Replaces the shared integer.
    /// </summary>
    /// <param name="value">The new value.</param>
    [PgFunction]
    public static void PrimitiveSet(int value)
    {
        using PgLwLockExclusiveGuard<int> guard = s_primitive.Exclusive();
        guard.Value = value;
    }

    /// <summary>
    /// Reads the shared Boolean without a lock.
    /// </summary>
    /// <returns>The current value, initially false.</returns>
    [PgFunction]
    public static bool AtomicGet() => s_atomic.Value;

    /// <summary>
    /// Atomically replaces the shared Boolean.
    /// </summary>
    /// <param name="value">The new value.</param>
    /// <returns>The previous value.</returns>
    [PgFunction]
    public static bool AtomicSet(bool value) => s_atomic.Exchange(value);

    /// <summary>
    /// Adds a deque value outside the mutation callback's backend-call restriction before warning about a full deque.
    /// </summary>
    private static void DequePush(Pgtest value, bool front)
    {
        bool added;
        using (PgLwLockExclusiveGuard<PgtestDeque> guard = s_deque.Exclusive())
        {
            added = guard.Mutate((ref deque) => front ? deque.Items().TryPushFront(value) : deque.Items().TryPushBack(value));
        }

        if (!added)
        {
            PgLog.Warning("Deque is full, discarding update");
        }
    }
}
