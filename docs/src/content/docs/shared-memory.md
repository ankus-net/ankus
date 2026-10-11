---
title: Shared memory, locks and atomics
description: Share unmanaged values between PostgreSQL backends with lightweight locks, spinlocks and atomic scalars.
---

Use `PgLwLock<T>` for a value shared by PostgreSQL processes. Keep the descriptor
in a static field, then register it from `[PgModuleLoad]`:

```csharp
public readonly record struct Counters(long Completed, long Failed);

public static class SharedCounters
{
    private static readonly PgLwLock<Counters> State = new("my_extension.counters");

    [PgModuleLoad]
    public static void Register()
    {
        PgSharedMemory.Initialize(State, static () => new Counters(0, 0));
    }

    [PgFunction]
    public static long Completed()
    {
        using PgLwLockShareGuard<Counters> guard = State.Share();
        return guard.Value.Completed;
    }

    [PgFunction]
    public static void RecordCompletion()
    {
        using PgLwLockExclusiveGuard<Counters> guard = State.Exclusive();
        Counters value = guard.Value;
        guard.Value = value with { Completed = checked(value.Completed + 1) };
    }
}
```

Add the published library to `shared_preload_libraries` and restart PostgreSQL:

```ini
shared_preload_libraries = 'MyExtension'
```

The [shared memory sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.SharedMemory)
ports pgrx's `shmem` example: a bounded list, deque and map, a struct and an
integer behind separate lightweight locks, and an atomic Boolean.

Registration reserves space during startup. The initializer runs when PostgreSQL
creates the shared value, after registration finishes. It can read configuration,
write log messages and use memory contexts; SQL is unavailable in the postmaster.
Factories run in registration order and may read values whose initialization has
already completed. Access to a value before its initialization completes fails.
`PgSharedMemory.Initialize(State)` uses the default zero value instead of a
factory. Registering the same descriptor again keeps its original initializer.

Shared-memory names are cluster-wide, case-sensitive UTF-8 strings. Include your
extension's name to avoid collisions. Names must be nonempty, contain no zero
byte, and fit PostgreSQL's shared-memory index. Names that exceed its limit are
rejected without truncation. Registering another descriptor with the same name
in one extension is rejected.

## Values and lifetime

`T` must be unmanaged. Primitive values and structs containing unmanaged fields
can be copied directly. Strings, arrays, delegates and other managed references
cannot be stored in shared memory. An unmanaged struct can still contain an
address: you are responsible for ensuring that address is valid in every process
that uses it. A pointer to a managed object or a process-local allocation does
not meet that contract.

Shared values live until PostgreSQL shuts down or recreates its shared memory
after a backend crash. They are not durable storage. Creating a replacement
shared segment runs the initializers again. Ordinary backend attachment preserves
existing values without rerunning their initializer or replacing their bytes.
Windows backends attach explicitly; this does not depend on inheriting
process-local pointers from the postmaster.

Each `Value` read returns a copy. Modify that copy and assign it back through an
exclusive guard to publish the update. Writes take effect immediately; a SQL
rollback or exception does not undo them. Readers in other backends see the
updated value after the writer releases its lock.

Use a guard's `Read` callback when you need original inline atomic or spinlock
fields. Both shared and exclusive guards support it:

```csharp
public readonly struct LiveCounters(long initial)
{
    public readonly PgAtomicValue<long> Completed = new(initial);
    public readonly PgSpinLockValue<long> Failed = new(0);
}
```

For a registered `PgLwLock<LiveCounters>` named `state`:

```csharp
using PgLwLockShareGuard<LiveCounters> guard = state.Share();
long failed = guard.Read(static (in LiveCounters value) =>
{
    using PgSpinLockGuard<long> child = value.Failed.Lock();
    child.Value++;
    return child.Value;
});
```

The readonly reference lasts only for the synchronous callback. Atomic and
spinlock fields provide their own synchronization, so their operations work
under a shared guard. Ordinary fields remain readonly. `Value` still returns a
copy during the callback; copying an inline field copies its state.

While a reader is active, replacing the parent's value or disposing it is
rejected, including through another reference to that guard. Child spinlock
guards expire before the reader returns or throws. You can nest reads on the
same guard or on other guards acquired before entering the callback.

SQL, native calls, memory-context operations, configuration reads and
nonterminal logging are rejected during these callbacks. PostgreSQL error
recovery can release lightweight locks; this boundary keeps it from doing so
while the original reference is borrowed. Managed exceptions and Error-level
`PgLog.Write` unwind normally. Backend calls become available again after all
readers return. Keep callbacks synchronous and finite, and perform backend work
outside them. Owning a lightweight-lock guard without an active `Read` callback
continues to permit backend calls with the error behavior described below.

`PgInterrupts.Check()` is permitted under ordinary shared and exclusive guards.
PostgreSQL defers pending cancellation while the lock is held and delivers it
after release. Checks are rejected inside `Read` and `Mutate` callbacks, where
an original shared reference is borrowed, and while holding a spinlock.

## Mutating original values

An exclusive lightweight-lock guard or a spinlock guard provides `Mutate` for
direct updates through a scoped `ref` parameter. For a registered
`PgLwLock<Counters>` named `state`, using the value type from the first example:

```csharp
using PgLwLockExclusiveGuard<Counters> guard = state.Exclusive();
long completed = guard.Mutate(static (ref Counters value) =>
{
    value = value with { Completed = checked(value.Completed + 1) };
    return value.Completed;
});
```

Mutable structs can update their fields directly. Changes affect original
storage immediately and remain visible if the callback throws or the SQL
transaction rolls back. The callback can return an owned result; its reference
and any collection views borrowing that reference cannot escape.

During mutation, `Value` can return a snapshot. Other references to the same
guard cannot start `Read` or another `Mutate`, assign `Value`, or dispose the
guard. A mutation also cannot start while that guard has an active reader.
Backend calls remain unavailable until the mutable callback returns.

Access child spinlocks in a separate `Read` callback. Acquiring or querying a
spinlock inside the value being mutated is rejected, including when an outer
shared reader covers the same storage. This prevents overwriting a child lock
while a child guard still owns it. The parent stays held between callbacks, so
ordinary field updates and child-lock operations can remain within one parent
acquisition. Prepare initialized spinlock values before acquiring a spinlock
parent; do not publish copies of a held lock's state.

## Lock ownership and errors

`Share()` permits concurrent readers. `Exclusive()` waits for readers and writers
and permits one writer. These use PostgreSQL lightweight locks across processes.
Recursive acquisition in the same backend is rejected, including attempts to
upgrade a shared guard to exclusive access.

Use `using` to release each guard promptly. Callback exit also releases forgotten
guards. A guard cannot be used after disposal or callback exit, from another
thread, or through another extension's native capability. Keep guarded work
synchronous; do not retain a guard for another SQL call.

Managed exceptions unwind guards before being reported to PostgreSQL. Native
error recovery can release PostgreSQL locks before managed cleanup runs. Such a
guard no longer permits access, and disposing it cannot release a later
acquisition of the same lock. After recovery, acquire a new guard before accessing
the value. Shared-memory writes made before an error remain visible.
Checking an expired guard does not raise another PostgreSQL error. The check
rejects access before copying or borrowing shared storage.

## Atomic scalars

Use `PgAtomic<T>` when a single scalar needs atomic reads and updates across
PostgreSQL processes and managed threads:

```csharp
public static class SharedRequests
{
    private static readonly PgAtomic<long> Completed = new("my_extension.completed");

    [PgModuleLoad]
    public static void Register() => PgSharedMemory.Initialize(Completed);

    [PgFunction]
    public static long RecordCompletion() => Completed.Increment();

    [PgFunction]
    public static long ReadCompleted() => Completed.Value;
}
```

Register the descriptor during shared preload, just like a `PgLwLock<T>`.
The supported scalar types are `bool`, `char`, signed and unsigned 8-, 16-,
32- and 64-bit integers, `nint`, `nuint`, `float`, `double`, and enums.
Other unmanaged types, including `decimal` and structs, are rejected.

`Value` reads or replaces the scalar atomically. `Exchange(value)` returns the
old value. `CompareExchange(value, comparand)` replaces it only when the current
bits match `comparand`; it returns the old value whether comparison succeeds
or fails. Floating-point comparisons distinguish NaN payloads and positive
from negative zero.

Integer `Add`, `Subtract`, `Increment` and `Decrement` wrap on overflow and
return the new value. Integer and Boolean `And`, `Or` and `Xor` return the old
value. These operations use .NET `Interlocked` ordering. Separate operations
on multiple descriptors do not form an atomic transaction; use a lock around
an unmanaged aggregate when its fields must change together.

Once startup has attached the value, managed worker threads can access it
without a PostgreSQL callback or a lock guard. Registration and initialization
still belong to PostgreSQL startup. An operation checks that the current process
has attached and protects the address while accessing it. New operations fail
during shared-memory retirement; replacement storage becomes available after
startup initializes it. Writes survive SQL errors and rollback, and ordinary
backend attachment preserves the existing value.

In a Unix postmaster, follow the [preload thread lifetime rules](/initialization/#preloading).

## Shared aggregates

Use `PgShared<T>` for immutable unmanaged data or a struct containing independent
atomic or spinlock fields. `Read` supplies a readonly reference valid for the duration of its
synchronous callback:

```csharp
public readonly struct RequestState(int version)
{
    public readonly PgAtomicValue<long> Completed = new(0);
    public readonly PgAtomicValue<bool> Enabled = new(true);
    public int Version { get; } = version;
}

public static class SharedRequests
{
    private static readonly PgShared<RequestState> State = new("my_extension.state");

    [PgModuleLoad]
    public static void Register() => PgSharedMemory.Initialize(State, () => new RequestState(1));

    [PgFunction]
    public static long RecordCompletion() => State.Read(static (in RequestState value) =>
        value.Enabled.Value ? value.Completed.Increment() : value.Completed.Value);

    [PgFunction]
    public static bool SetEnabled(bool enabled) => State.Read((in RequestState value) =>
        value.Enabled.Exchange(enabled));
}
```

The initializer runs only when PostgreSQL creates the shared segment. Ordinary
fields remain immutable afterward; structs, tuples, wide numeric values and
unmanaged inline arrays retain their exact layout. As with lock-protected values,
any embedded address must be valid in every process that uses it.

`PgAtomicValue<T>` supports the same scalar types and update methods as
`PgAtomic<T>`. Read its getter-only `Value`, use `Exchange` or `CompareExchange`
to replace it, and use the arithmetic and bitwise extension methods for other
updates. A default atomic field starts at zero. Each field occupies eight bytes
and requires eight-byte alignment; unaligned packed layouts fail before access.

Operate directly on fields of the `in` parameter. Assigning a field or the whole
aggregate to a local variable copies its storage; later updates to that copy do
not change the shared value. Separate field reads and updates are independent
atomic operations, not a consistent snapshot or transaction across all fields.
Use `PgLwLock<T>` when updates must preserve a relationship between fields.

The scoped callback prevents safe C# from retaining the borrowed reference.
It may return an owned result, and exceptions release its admission. Managed
threads may call `Read` after their process has attached, without invoking
PostgreSQL. Keep callbacks finite: shutdown closes admission and waits for active
callbacks before unmapping their shared segment. New callbacks fail during
retirement and can read replacement storage after startup publishes it.
A FATAL report from PostgreSQL inside a callback, for example
`pg_terminate_backend` during a query, exits without resuming that callback.
The exiting backend does not wait for its own abandoned reads.

## Spinlocks

Use `PgSpinLockValue<T>` for a small value inside a shared aggregate that needs
an exclusive update lasting only a few instructions:

```csharp
public readonly struct SpinCounters(int initial)
{
    public readonly PgSpinLockValue<int> Completed = new(initial);
}

public static class SharedSpinCounters
{
    private static readonly PgShared<SpinCounters> State = new("my_extension.spin_counters");

    [PgModuleLoad]
    public static void Register() => PgSharedMemory.Initialize(State, () => new SpinCounters(0));

    [PgFunction]
    public static int RecordCompletion() => State.Read(static (in SpinCounters state) =>
    {
        using PgSpinLockGuard<int> guard = state.Completed.Lock();
        guard.Value++;
        return guard.Value;
    });
}
```

Construct every spinlock field explicitly in the shared initializer. Its
constructor initializes the lock using the selected PostgreSQL headers.
A default field has no initialized lock. Access the original field directly
through the `in` reference; locking a detached copy is rejected. The field
requires eight-byte alignment, so incompatible packed layouts are rejected.

`Lock()` waits for exclusive access. A guard's `Value` getter returns a copy;
assigning `Value` writes the protected storage immediately. Updates survive
exceptions and SQL rollback. Use `using` to release the guard promptly. Shared
read exit also releases forgotten guards before the shared address can retire.
An escaped, disposed or expired guard cannot access storage or unlock a later
acquisition. Recursive acquisition of the same lock is rejected.

Spinlock construction, acquisition and guard access require the owning backend's
native callback thread. Keep critical sections synchronous and limited to small
managed computations. Release the guard before SQL, logging, GUC reads, memory
context operations or other PostgreSQL calls; those APIs reject access while a
spinlock is held. Managed exceptions, including an Error-level `PgLog.Write`,
unwind the guard before PostgreSQL reports the error. Prefer `PgLwLock<T>` for
longer work.

For a value local to one backend, use `PgSpinLock<T>`:

```csharp
PgSpinLock<int> counter = new(0);
using PgSpinLockGuard<int> guard = counter.Lock();
guard.Value = 1;
```

This owner keeps its storage pinned, and a live guard keeps that storage alive.
Callback exit releases a forgotten local guard. Local values are not shared
between PostgreSQL processes; use an inline field in `PgShared<T>` for that.

When the protected value contains atomic or spinlock fields, use the guard's
`Read` method to operate on their original storage. For example, this local
owner protects the `SpinCounters` aggregate defined above:

```csharp
PgSpinLock<SpinCounters> counters = new(new SpinCounters(0));
using PgSpinLockGuard<SpinCounters> parent = counters.Lock();
int completed = parent.Read(static (in SpinCounters state) =>
{
    using PgSpinLockGuard<int> child = state.Completed.Lock();
    child.Value++;
    return child.Value;
});
```

The same method works on a guard acquired from an inline shared spinlock. Its
readonly reference lasts only for the synchronous callback. The callback may
return an owned result; copying the value or a field creates independent storage.
It cannot replace `parent.Value` or dispose `parent` while a reader is active,
including through another reference to the same guard. Forgotten child guards
expire before that reader returns or throws. These reads keep the parent locked
and preserve the same short-critical-section and backend-call restrictions.

Both forms expose `IsLocked` before PostgreSQL 19. This is an instantaneous
observation, not permission to read the protected value without a guard.
PostgreSQL 19 removed the native state query, so `IsLocked` throws
`NotSupportedException` there; locking and guarded values remain available.

## Bounded collections

Use `PgFixedList<T>`, `PgFixedDeque<T>` and `PgFixedMap<TKey, TValue>` for
collections with a fixed capacity. They borrow a buffer and its metadata;
ordinary C# inline arrays own the elements inside an unmanaged struct.
The collection view is a `ref struct`, so it cannot escape the lifetime of
that storage or become a field of the shared value itself.

For example, store a bounded queue of work identifiers behind a lightweight lock:

```csharp
using System.Diagnostics.CodeAnalysis;
using System.Runtime.CompilerServices;
using Ankus;

[InlineArray(128)]
public struct WorkItems
{
    private int _element;
}

public struct WorkQueue
{
    private WorkItems _items;
    private int _count;
    private int _head;

    [UnscopedRef]
    public PgFixedDeque<int> Items() => new(_items, ref _count, ref _head);
}

public static class SharedWork
{
    private static readonly PgLwLock<WorkQueue> Queue = new("my_extension.work");

    [PgModuleLoad]
    public static void Register() => PgSharedMemory.Initialize(Queue);

    [PgFunction]
    public static bool Enqueue(int identifier)
    {
        using PgLwLockExclusiveGuard<WorkQueue> guard = Queue.Exclusive();
        return guard.Mutate((ref WorkQueue value) => value.Items().TryPushBack(identifier));
    }

    [PgFunction]
    public static int? Dequeue()
    {
        using PgLwLockExclusiveGuard<WorkQueue> guard = Queue.Exclusive();
        return guard.Mutate(static (ref WorkQueue value) =>
            value.Items().TryPopFront(out int identifier) ? (int?)identifier : null);
    }
}
```

`UnscopedRef` lets an instance method return a view borrowing its struct's
fields. C# still checks the caller's storage lifetime. Copying `WorkQueue`
copies its elements and metadata. Copying a `PgFixedDeque<int>` view aliases
its existing storage. A view created inside `Mutate` updates shared memory
directly. A view over a guard's copied `Value` instead updates that copy; assign
the owning value back through the exclusive guard to publish those changes.

All buffers and metadata start at zero. Constructing another view preserves
existing contents. Keep a collection's buffers and metadata together and
synchronize their use; these views do not acquire locks themselves.

| Collection | Operations |
|---|---|
| `PgFixedList<T>` | `Add`/`TryAdd`, `AddRange`/`TryAddRange`, insertion, indexing, ordered `RemoveAt`, unordered `SwapRemoveAt`, and `Pop`/`TryPop` |
| `PgFixedDeque<T>` | `PushFront`/`PushBack`, `PopFront`/`PopBack`, their `Try` forms, logical indexing, and wrapped buffer access through `GetSpans` |
| `PgFixedMap<TKey, TValue>` | `Add`/`TryAdd`, `Set`/`TrySet`, indexing, `TryGetValue`, `ContainsKey`, `Remove`, and readonly `Entries` |

The list and deque expose `Drain`, which returns an owned array and empties
the collection. All three provide `ToArray`, `Clear` and allocation-free
`foreach` enumeration. Constructing a view over existing storage does not
allocate a collection or comparer. List `AsSpan` and
deque `GetSpans` borrow initialized elements; structural changes invalidate
their previous logical extent. Deque `CopyTo` requires a large enough,
nonoverlapping destination. List range appends permit overlapping source data.

Full collections reject new elements without changing their contents.
`Try` operations report capacity or empty-state failures without throwing;
their throwing counterparts report `InvalidOperationException`. Invalid
metadata or unsupported map keys still throw. These collections never resize.
Elements and map keys must be unmanaged; custom structs can contain inline
arrays and other unmanaged fields.

### Fixed maps and key equality

A map owner needs equally sized buffers of `PgFixedMapEntry<TKey, TValue>`
and `int`, plus an element count:

```csharp
[InlineArray(4)]
public struct MapEntries
{
    private PgFixedMapEntry<int, long> _element;
}

[InlineArray(4)]
public struct MapIndices
{
    private int _element;
}

public struct Counters
{
    private MapEntries _entries;
    private MapIndices _indices;
    private int _count;

    [UnscopedRef]
    public PgFixedMap<int, long> Values() => new(_entries, _indices, ref _count);
}
```

`Set(key, value)` returns the previous value, or null for a new key.
`TrySet(key, value, out previous)` returns false if a new key cannot fit.
Replacing an existing entry works even at capacity and preserves the original
key and position. Iteration follows insertion order until removal: `Remove`
moves the last entry into the removed position, matching `heapless::IndexMap`.
Never modify a populated map's raw entry or index buffers.

The default comparer uses .NET value equality and deterministic hashing for
Boolean, character, integer primitives, enums, `Int128`, `UInt128`, `Guid`,
`Half`, `float`, `double` and `decimal`. Equal NaNs, signed zeros and decimal
values with different scales find the same entry. This differs from an atomic
compare-exchange, which compares exact bits. Hashes are internal to the map;
they are not a Rust-compatible or durable serialization format.

Other key structs require an explicit `IEqualityComparer<TKey>` supplied to
the map constructor. Its equality and hash results must be identical in every
backend using that storage. Use deliberate, deterministic field comparisons
and hashing. Do not rely on record-generated `GetHashCode`, `HashCode.Combine`,
process-local addresses, mutable settings, or randomized hashing. Use the same
comparer semantics for every attachment to a populated map.

Background workers can share these values; see [background workers](/background-workers/).
