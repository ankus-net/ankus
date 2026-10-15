---
title: Memory contexts
description: Allocate PostgreSQL-owned native memory with checked context and allocation lifetimes.
---

`PgMemoryContext` represents a PostgreSQL memory context. Use `Create` to own a
child context and `PgAllocation` to copy unmanaged values or byte buffers into
its native storage:

```csharp
[PgFunction]
public static int NativeValue(int value)
{
    using PgMemoryContext context = PgMemoryContext.Create("native value");
    using PgAllocation allocation = context.AllocateZeroed(sizeof(int));
    allocation.Write(value);
    return allocation.Read<int>();
}
```

These APIs require a synchronous callback on PostgreSQL's backend thread. They
are available independently of SPI, including initialization and configuration
hooks. They do not enable database queries in callback phases where PostgreSQL
prohibits queries. Do not call them from a worker thread or after `await`.

The [memory contexts sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.MemoryContexts)
ports pgrx's `memory_contexts` example. It resets scratch contexts after
temporary work, returns sets, and keeps a background worker's counter in
`TopMemoryContext`. It also shows a stale allocation rejected after reset.

For a growable PostgreSQL binary buffer, use
[`PgStringInfoStream`](/stringinfo/). It supports ordinary stream writes, strict
UTF-8 text, checked byte copies, and explicit native ownership transfers.

For native PostgreSQL `List` containers, use [`PgList<T>`](/lists/). Its typed
cells retain native integer, OID, transaction-ID or pointer identity. Container
growth keeps the original context owner, and context cleanup invalidates both
owned and borrowed handles. Pointer elements keep their independent ownership.

[`PgNativeItemPointer`](/item-pointers/#native-storage-and-ownership) supplies
checked native tuple-location storage whose fields use the selected server's
`ItemPointerData` layout. It supports independent copies, borrowed views and raw
ownership transfer under the context lifetime rules below.

## Context ownership

`Current` resolves PostgreSQL's current context at the point of the call.
`Get(PgMemoryContextKind)` resolves predefined contexts such as `Top`,
`TopTransaction`, and `CurTransaction`; it returns null when the selected context
does not exist in the current phase. `Parent` returns a borrowed parent handle.

Functions, operators, and casts can receive a borrowed `PgMemoryContext` as an
[injected parameter](/function-declarations/#injected-memory-contexts). A scalar
call receives its current context. A set factory and its `GetEnumerator` run in
the multi-call context, which remains alive across row requests. An injected
handle keeps that context's identity even when `Run` or a nested backend call
changes the ambient context. Use a fresh `Current` lookup when you need the
context selected at that later point.

For pgrx users, this is the C# counterpart of a virtual `&MemCx` argument.
Ankus supplies a checked owner snapshot; pgrx's reference follows the native
current-context pointer slot. Nullable C# annotations never change the supplied
owner or inferred SQL strictness, which depends on SQL inputs only.

`Create(name, parent, options)` creates an AllocSet child without changing the current
context. Omit `parent` to use the current context. Dispose an owned context to
delete it and its descendants. Disposing a borrowed handle leaves the native
context intact. Contexts have no finalizers: the garbage collector must never
call PostgreSQL from its finalizer thread. PostgreSQL still reclaims a context
when its parent is deleted, even if a managed handle remains reachable.

`PgMemoryContextOptions` supplies the minimum retained context size, initial block
size, and maximum block size. Its `Default`, `Small`, and `StartSmall` presets match
PostgreSQL's AllocSet presets. Sizes must satisfy the selected server's native
alignment and range limits; they need not be powers of two. Invalid sizes throw
before reaching PostgreSQL's allocator assertions.

Use `Run` to select a context temporarily:

```csharp
using PgMemoryContext context = PgMemoryContext.Create("temporary work");
int result = context.Run(() =>
{
    using PgAllocation value = PgMemoryContext.Current.Allocate(sizeof(int));
    value.Write(42);
    return value.Read<int>();
});
```

`Run` restores the previous context on ordinary return and managed exceptions.
It supports nested calls. Deleting the current context or an ancestor is
rejected; after leaving the scope, disposal can be retried. Native callback
storage and its ancestors cannot be reset while the callback is active.
Live set-function owners and their ancestors also remain protected between
cursor fetches: their executor storage must survive until PostgreSQL closes
the invocation. This includes `ResetOnly` on an ancestor. Resetting children of
a suspended set owner preserves that owner but invalidates affected child storage.
PostgreSQL's infrastructure contexts, including its transaction, portal, cache,
message and error contexts, cannot be reset or deleted through these checked
APIs. Create a child context to own storage that your extension can reclaim.

PostgreSQL error recovery resets `ErrorContext` (`PgMemoryContextKind.Error`)
and deletes its children. If a
guarded operation fails while one of those children is current, Ankus resumes in
the surviving `ErrorContext`; the child and its allocations become stale. The
enclosing `Run` restores its original caller when that caller remains alive.

Handles may be reused in a later synchronous callback from the same extension
and backend while their native owner remains alive. A child of `TopTransaction`
survives multiple SQL calls in that transaction. A child of `CurTransaction`
expires when its subtransaction is rolled back. `IsAlive` checks native context
identity; transaction cleanup and reuse of a native address cannot revive an old
handle. Borrowed contexts reset implicitly by PostgreSQL must be resolved again.

Expired-handle checks and rejection of protected context operations validate
Ankus's registry before invoking PostgreSQL. They do not raise a backend error
or require transaction rollback. Actual PostgreSQL errors, including allocator
and encoding failures, follow the recovery rules below.

`RunTransient` combines creation, selection, restoration, and deletion:

```csharp
int result = PgMemoryContext.RunTransient("temporary values", context =>
{
    using PgAllocation values = context.CopyFrom<int>([10, 20, 30]);
    return values.Read<int>(sizeof(int));
}, options: PgMemoryContextOptions.Small);
```

Return copied managed data. Checked handles that escape become stale after
successful deletion. The helper attempts deletion after both normal return and
exceptions, preserving callback, restoration, and deletion failures together.
If a cleanup callback throws, older callbacks and the context can remain pending;
the parent still owns that native context until cleanup is retried.

## Reset and allocation lifetime

| Operation | Selected context | Descendant contexts |
| --- | --- | --- |
| `Reset()` | Retained; allocations invalidated | Deleted |
| `ResetOnly()` | Retained; allocations invalidated | Unchanged |
| `ResetChildren()` | Unchanged, including allocations | Retained; allocations invalidated |
| Owned `Dispose()` | Deleted | Deleted |

An affected allocation rejects later reads, writes, owner lookup, and resizing
with `ObjectDisposedException`. Disposing an already reclaimed allocation or
owned context is harmless. Newly allocated bytes use a new identity. Context
names remain readable across explicit resets.

`Allocate` returns uninitialized storage; write it before reading. `AllocateZeroed`
clears every byte. `TryAllocate` uses PostgreSQL's no-OOM allocation flag and
returns null when that allocator returns null. Invalid sizes and other native
errors still throw `PgException`. Allocation, resize, and free use PostgreSQL's
matching allocator, and errors are caught in native code before managed execution
resumes.

A caught native error still requires PostgreSQL rollback. To recover and continue
within the same managed callback, put the operation inside
[`PgTransaction.RunInSubtransaction`](/transaction-callbacks/#recoverable-work)
and catch its exception outside the scope. Without that scope, Ankus blocks
further backend work and reports the original error when the callback returns,
even if a C# `catch` swallowed it. Owned disposal and context restoration remain
available while managed `finally` blocks unwind.

`Allocate<T>(count)`, `AllocateZeroed<T>(count)`, and `TryAllocate<T>(count)`
check multiplication by `sizeof(T)` before native access. `CopyFrom` copies a byte
span or an unmanaged value span into independent native storage. These APIs copy
unmanaged bytes: they do not infer SQL types or guarantee that an arbitrary .NET
struct matches a PostgreSQL C layout. Allocation lengths and access offsets
remain measured in bytes.

Use `PgAllocationOptions.Zeroed` to clear initial storage and `Huge` to permit
PostgreSQL's larger allocation size limit. Huge allocations still depend on
available memory. The `alignment` argument accepts zero for native default
alignment, or a power of two below 128 MiB. Alignment above the server's native
default requires PostgreSQL 16 or later. Aligned `TryAllocate` and `TryReallocate`
require PostgreSQL 16.15, 17.11, 18.6, or 19 beta 3 or later because earlier releases have
unsafe native no-OOM handling. PostgreSQL 19 development snapshots are also
rejected for these requests, because their version number cannot show whether
the fix is present. These requests raise SQLSTATE `0A000` instead of returning
null or acquiring an over-aligned allocation; ordinary alignment remains
available on older servers.

`AllocateUtf8String` copies strict UTF-8 bytes followed by one zero byte. It rejects
embedded NUL and malformed UTF-16. This is raw UTF-8 copying, including in a
LATIN1 database; it does not convert to the database encoding.

`Read` and `Write` copy spans or unmanaged values after checking the live chunk
and its bounds. `Reallocate` preserves the existing prefix when it succeeds;
an allocation error preserves the old chunk. It can be used after rollback if its
owner was created outside the recovery scope. `Clear` clears a selected range;
omitting its length clears through the end. Zero-length allocations and empty
copies are supported when the native allocator accepts their size. `Context`
returns the owner established when the allocation was created or adopted.

Resizing preserves the original huge policy and alignment. Pass
`zeroNewMemory: true` to clear only the newly added tail; shrinking is still
allowed. `TryReallocate` returns false only when the native allocator returns
null, preserving the old storage, length, and contents. Invalid sizes and other
native failures still throw. `Options` and `Alignment` describe the original
allocation policy; initial zeroing does not automatically zero future growth.

`TryReallocate` and resizes with alignment above the server's native default
allocate replacement storage, copy the retained prefix, and then free the old
chunk. Budget for both chunks while resizing, especially with `Huge`.
Zeroed allocation and zeroed growth also write the requested bytes; the huge
size policy does not make those operations sparse.

`GetAllocatedBytes()` includes descendant context storage and native allocator
overhead. `IsEmpty` returns PostgreSQL's native emptiness result. Registering the
invalidation callback marks an AllocSet context nonempty, including after an
explicit reset. Other native allocators determine emptiness from their own
blocks or live chunks, so this property does not establish a portable count of
user allocations.
These statistics describe native contexts, not the managed heap.
A resize can release a large block while allocating a regular block for its
replacement; the reported byte change includes both operations.

## Borrowed native allocators

`PgMemoryContext.DangerousBorrow(address)` wraps a native `MemoryContext` address,
such as one obtained through raw bindings, as pgrx's `PgMemoryContexts::For` does.
The handle can allocate, run code and register callbacks; it survives a reset and
becomes stale when the context is deleted. `DangerousGetOwner(allocation)` finds the
context that owns a palloc'd address, as `GetMemoryChunkContext` does, and
`DangerousGetPointer()` returns a handle's address for raw PostgreSQL calls. Ankus
checks only the context's node tag, so the address must belong to a live context
or allocation in this backend. A Bump context does not record its chunks' owner,
so `DangerousGetOwner` cannot be used with its allocations.

These three calls require an unsafe context, such as an `unsafe` block or method
(ANKUS129).

`Create` and `RunTransient` construct AllocSet contexts. Borrowed handles can also
refer to PostgreSQL's Slab, Generation, or Bump contexts created by native code.
Their operations retain the native allocator's restrictions:

| Allocator | Allocation and resize | Reclamation |
| --- | --- | --- |
| Slab | Requests must match its configured chunk size; ordinary same-size resize preserves the pointer | Individual free or context cleanup; PostgreSQL 16+ may cache empty blocks |
| Generation | Variable sizes and prefix-preserving resize | Individual free counts released chunks; whole blocks are reused or reclaimed; PostgreSQL 15+ retains a keeper block |
| Bump, PostgreSQL 17+ | Variable-size allocation; every resize is rejected | Context reset or deletion only; individual `Dispose()` is rejected |

`TryAllocate` and `TryReallocate` propagate unsupported-operation and invalid-size
errors. They return null or false only for allocator exhaustion. Failed resize
or disposal preserves the original handle, length, and bytes. Explicit alignment
does not remove these restrictions: padding a Slab request can violate its fixed
size, and an aligned Bump chunk still cannot be individually freed or resized.

For Bump storage, prefer `CreateContextValue` or `TryCreateContextValue`. Checked
access, `Context`, borrowing, and `DangerousDetach` remain available. Raw adoption
is rejected because Bump does not provide a retrievable chunk owner. Use
`DangerousBorrow` with a proven context lifetime for an existing native Bump
address. This remains an unsafe provenance obligation; an anchor cannot establish
which allocator created an arbitrary address.

Ankus checks the recorded owner before requesting an unsupported Bump operation.
Ordinary PostgreSQL builds omit Bump chunk headers, so attempting pointer-based
free or resize first would be unsafe even inside an error guard. Reset and deletion
still invalidate checked allocations and borrowed generations before later access.

## Raw allocation pointers

A checked allocation's `DangerousGetPointer()` returns its address for unsafe
interop. The pointer is valid only until free, resize, or context cleanup. The caller must enforce its lifetime and
alignment; checked managed operations do not make a retained raw pointer safe.

`DangerousDetach()` consumes the checked owner and transfers its raw pointer
without freeing the allocation. PostgreSQL still owns the storage through its
context. A recipient can take exclusive individual ownership with
`context.DangerousAdopt(pointer, length, huge, alignment)`. Adoption checks the
native context and rejects a pointer that already has a checked owner. Failed
adoption leaves ownership with the caller.

These are unsafe interoperability operations: the caller must guarantee a live
`palloc`-family pointer compatible with `pfree`, the accessible byte length,
original alignment and size policy, and exclusive ownership. A context lookup
cannot validate an arbitrary pointer. Do not free or resize a raw pointer while
a checked owner remains live. Context-only allocators such as PostgreSQL's Bump
context do not provide the individual-free contract required here.

## Native boxes and borrowed values

Use a typed wrapper when one allocation holds one unmanaged value:

| Type | Ownership | Cleanup |
| --- | --- | --- |
| `PgNativeBox<T>` | One individually owned native allocation | `Dispose()` frees it; its native context also reclaims it |
| `PgContextValue<T>` | Storage owned by the native context | Context reset or deletion reclaims it; the wrapper has no `Dispose()` |
| `PgVarlena<T>` | A registered native-layout SQL value, borrowed or independently allocated | Input views expire at callback exit; independent allocations support `Dispose()` and follow context lifetime |
| `PgNativeReference<T>` | A borrowed view with no release rights | The underlying allocation or explicit lifetime context governs access |

```csharp
using PgMemoryContext context = PgMemoryContext.Create("boxed value");
using PgNativeBox<int> box = context.CreateBox(5);
PgNativeReference<int> reference = box.Borrow();
PgContextValue<int> retained = box.ReleaseToContext();

reference.Value = 21; // retained.Value is now 21
box.Dispose();       // the consumed owner cannot free retained storage

PgContextValue<int> snapshot = reference.CloneInto(context);
reference.Value = 42; // snapshot.Value remains 21
```

`CreateBox` and `CreateContextValue` copy an initialized unmanaged value into
native memory. `TryCreateBox` and `TryCreateContextValue` return null only when
the native allocator returns null; initialization and other errors still throw.
`AllocateZeroedBox<T>` starts with zero bytes.
`DangerousAllocateUninitializedBox<T>` requires initialization before reading.
These factories do not run a constructor in native storage. Reclamation never
calls the value's `Dispose()` method or recursively releases embedded pointers.
Use a context cleanup callback for managed cleanup actions.

`Value` reads or writes a copy. It does not expose a managed reference or span
whose lifetime could escape the checks. `CloneInto` returns a context-owned
shallow copy; `CloneOwnedInto` returns an individually disposable copy. Both copy
exactly `sizeof(T)` bytes, including padding and embedded pointer values, into
the specified context or `Current` when omitted. Clones use the destination's
default allocation policy and alignment. They do not duplicate pointed-to data.

`allocation.Borrow<T>(offset)` creates a typed view at a byte offset. It checks
the range and native identity at creation and on later access. A checked view
follows its allocation when resizing moves the chunk; shrinking below its range
rejects access. `ReleaseToContext` consumes the owning wrapper while retaining
the checked allocation and existing views. Raw `DangerousDetach` consumes the
allocation's checked tracking, including shared views, without freeing storage.
Garbage collection of any wrapper never invokes PostgreSQL.

For unsafe interoperability, `context.DangerousAdoptBox<T>(address)` takes
individual ownership of a valid `palloc`-family chunk. A null address returns a
null managed reference without a backend call. `DangerousAdoptContextValue<T>`
requires a non-null compatible chunk and transfers it to context ownership.
The existing adoption requirements for provenance, native owner, alignment,
size policy and exclusivity apply.

`context.DangerousBorrow<T>(address)` borrows without acquiring ownership or
inspecting an allocation header. A null address returns null. Non-null addresses
can refer to stack, interior, or resource-owned memory when the caller guarantees
accessible, initialized bytes and the required external lifetime. The resulting
reference's `LifetimeContext` is an explicit lifetime anchor, not an inferred
allocator owner. A captured reset generation makes the view stale after context
reset even when the context itself survives. No native record is allocated for
each borrowed view.

The overload `DangerousBorrow<T>(address, byteLength)` records an explicitly
guaranteed accessible extent, which must contain the complete `T` value. Use it
when a raw base-node view covers a larger native object. Checked
[`PgNodes` casts](/raw-values/#native-postgresql-declarations) retain that extent
and the original reset generation; casting never creates additional storage or
renews an expired lifetime.

The anchor does not detect a shorter lifetime caused by stack return, external
free or resize, or closing a separate native resource. The unsafe caller must
prevent later access in those cases. Failed context cleanup does not invalidate
references prematurely: if a cleanup callback throws before native invalidation,
the payload and its views remain live until cleanup proceeds.

An unmanaged .NET type does not establish a PostgreSQL C layout or SQL type.
These wrappers copy raw bytes; arbitrary boxed values are not automatically SQL
arguments or results. Use [native PostgreSQL declarations](/raw-values/),
`PgVarlena<T>` or a reusable datum mapping when a value needs a PostgreSQL layout
or SQL type.

## Cleanup callbacks

`RegisterResetCallback(Action)` connects managed cleanup to PostgreSQL's native
context lifetime. The returned `PgMemoryCallback` remains pending until the
context resets or is deleted, or until you cancel the registration:

```csharp
using PgMemoryContext context = PgMemoryContext.Create("context cleanup");
var events = new List<string>();
PgMemoryCallback first = context.RegisterResetCallback(() => events.Add("first"));
PgMemoryCallback second = context.RegisterResetCallback(() => events.Add("second"));

context.Reset(); // events contains "second", then "first"
bool pending = first.IsPending || second.IsPending; // false
```

Callbacks run once in reverse registration order within each context. PostgreSQL
also invokes callbacks registered by a callback during the same drain. The action
is rooted even if application code drops its registration object. Its root is
released and `IsPending` becomes false before user code runs, so self-disposal
does not cancel or invoke the action again. `Dispose()` cancels a pending action
without running it; repeated disposal is harmless. Cancellation releases managed
references immediately, while a small native cancellation record remains until
the context next resets or is deleted. Registrations have no finalizer.

During ordinary reset or deletion, an exception consumes that callback and
stops the native drain. Older callbacks remain pending; retrying cleanup invokes
them. Already deleted child contexts stay deleted. The diagnostic becomes a
PostgreSQL error after the managed callback returns. An explicit managed
`Reset()` or owned `Dispose()` receives a `PgException`; recovering requires
the surrounding subtransaction to roll back before retrying.

Commit and abort cleanup must finish draining the transaction's callbacks.
A secondary callback failure is written to standard error, which is the server
log when PostgreSQL captures it, as lines prefixed `ANKUS CLEANUP [backend <pid>]`
with its SQLSTATE and diagnostic fields. This emergency path does
not allocate PostgreSQL diagnostic state, convert through the client encoding,
invoke `emit_log_hook`, or flush an active PostgreSQL error. It therefore cannot
replace the original SQL error, interrupt rollback, or turn a completed commit or
`PREPARE TRANSACTION` into a cluster restart. Backslashes, newlines, carriage
returns and tabs are written as `\\`, `\n`, `\r` and `\t`, and other control
characters and non-ASCII bytes as `\xNN`.

A real native cancellation request remains pending until a later interrupt
boundary. A callback exception with SQLSTATE `57014` is logged without creating a
cancellation request. During abort, `FATAL` terminates that backend and `PANIC`
requests PostgreSQL crash recovery. After a durable commit or preparation,
`FATAL` is promoted to `PANIC` because PostgreSQL can no longer abort that
transaction during backend exit. Durable changes remain durable. See the
[transaction-completion contract](/transaction-callbacks/).
Write callbacks to release their resources without throwing.
A `Fatal` or `Panic` `PgLog.Write` inside a cleanup callback still terminates the
backend or requests crash recovery, even if managed code catches its exception.
This does not grant ordinary logging or SQL access during cleanup.

Cleanup permits checked native allocation access and disposal of retained SPI
plans and cursors when the registration was created with backend access. Queries,
logging, configuration reads, and aggregate operations are unavailable. The resetting context's payload remains readable
until its invalidation callback runs. Destructive operations on the active owner
or its ancestors, switching into the context being reclaimed, and child creation
within the active cleanup tree are rejected. Independent contexts can still be
used and reset. Iterator and aggregate cleanup has the same native owner
protection, including during transaction abort.
Unsafe raw calls retain their native transaction and ownership preconditions.
Releasing owned storage with a valid `pfree` call remains supported during abort;
the raw guard contains errors, but cannot make an arbitrary native operation safe
in a transaction that PostgreSQL is dismantling. Checked lifetime access and
owned-resource release remain available. Ordinary reset during initialization
or idle backend execution retains its existing native-call and error contract.

A callback owned by `ErrorContext` or one of its children runs inside the error
handler's cleanup phase. It can release managed resources, but guarded memory
operations and SPI resource disposal throw `PgException` with SQLSTATE `55006`
in that phase.
Those operations could otherwise consume PostgreSQL's active error stack or
recursively reclaim the callback's own storage. A failed disposal leaves the
resource available for a later allowed phase. Choose an ordinary owned context
for cleanup that needs native memory or SPI resources. PostgreSQL 19 also resets
`ErrorContext` after an emitted outermost notice or warning; earlier supported
versions reset it during error recovery.
