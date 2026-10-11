---
title: PostgreSQL lists
description: Use typed native PostgreSQL lists with checked values, mutation and memory-context ownership.
---

`PgList<T>` is a mutable .NET collection over PostgreSQL's native `List`. Use it
when a backend API accepts or returns a native list. It implements `IList<T>`,
`IReadOnlyList<T>` and `IDisposable`; it is separate from SQL arrays and ordinary
managed `List<T>` storage.

The supported cell types retain distinct native tags:

| C# cell type | Native list | Values |
|---|---|---|
| `int` | `T_IntList` | Exact signed 32-bit integers |
| `uint` | `T_OidList` | Exact OID bits, including zero |
| `PgTransactionId` | `T_XidList`, PostgreSQL 16 and later | Exact transaction-ID bits, including zero |
| `nint` | `T_List` | Opaque pointer bits, including null pointers |

Other unmanaged types are rejected. Pointer lists own their container and cell
storage only; they never dereference, clone or free the pointed-to objects.
The native bridge uses the selected server's headers, without copying its union
or `NodeTag` layout into C#.

```csharp
using PgList<int> values = PgList.Create<int>([10, 30]);
values.Insert(1, 20);
values.AddRange([40, 50]);
values[0] = -10;

int[] removed = values.Drain(1, 2); // 20, 30
int[] remaining = values.ToArray(); // -10, 40, 50
```

## Empty lists and capacity

PostgreSQL represents an empty list as `NIL`, a null native pointer. An empty
`PgList<T>` is still a non-null managed collection. Removing the final cell or
calling `Clear()` frees the native container and restores NIL, so its capacity
becomes zero. This differs from a managed `List<T>` that retains capacity after
clearing.

`new PgList<int>()` creates backend-independent NIL. Count, capacity, empty
iteration, empty copies and raw NIL pointer access need no backend until the
first allocating mutation binds the list to the current context.
`PgList.Create<T>(context)` binds immediately, including when empty. A bound
empty list therefore expires when its context resets. Binding a transaction-ID
list on PostgreSQL before 16 throws a native feature-not-supported error with
SQLSTATE `0A000`. The other cell kinds remain available on those servers.

`TryAdd(value)` appends without allocating native storage and returns false for
NIL or a full buffer. `TryReserve(additionalCount)` returns false for NIL;
otherwise it ensures capacity for the current count plus the requested number
of cells. It can allocate and can throw a native allocation error. `Add`,
`AddRange` and `Insert` allocate as necessary. Failed native growth preserves
the existing cells and metadata. Capacity is measured in cells, and the exact
allocation strategy is not an API guarantee.

`TryGet`, `TryGetFirst`, `TryGetLast` and `TryPop` distinguish an absent cell
from a present zero value or null pointer. The indexer throws for an invalid
index. `CopyTo(Span<T>, sourceIndex)` selects a source range using the span's
length; `CopyTo(T[], arrayIndex)` copies the whole collection to a destination
offset. Copies and `ToArray()` use independent managed storage.

## Iteration and draining

Ordinary `foreach` borrows the list and checks its lifetime on each access.
Changes to cell values or list contents invalidate its enumerators. Cell copies avoid
keeping a span or reference into storage that PostgreSQL may relocate.

`Drain(index, count)` eagerly copies and removes a range, then returns the removed
values as a managed array. The retained tail is already repaired when it returns.
Stopping enumeration of the returned array early cannot leave the native list
partly drained. This matches pgrx's `drain` results without exposing a live
reference into native storage.

For consuming iteration, explicitly dispose the returned enumerator:

```csharp
using IEnumerator<int> iterator = values.GetConsumingEnumerator();
while (iterator.MoveNext())
{
    int value = iterator.Current;
    // Use the copied value synchronously in this backend callback.
}
```

`GetConsumingEnumerator()` immediately makes the original wrapper unusable and
transfers its obligations to the enumerator. Reaching the end or disposing early
releases an owned container. Disposing before the first advance also releases it.
Borrowed containers remain with their native owner.

## Ownership and native interoperability

Factories allocate in the supplied `PgMemoryContext`, or the current context when
omitted. Growth keeps that owner even when another context becomes current.
`LifetimeContext` exposes the checked owner; it is null for an unbound NIL list.
`Clone(context)` copies cells into a new native container, sharing opaque pointees.

Context reset, deletion, or transaction cleanup invalidates bound handles before
accessing their storage, including empty copies and capacity checks. Disposal
after native context cleanup is harmless during a callback from the same
provider. Every bound operation requires the active backend thread and extension
provider; no finalizer calls PostgreSQL. Bump contexts cannot supply individually
released list storage. Other allocators retain their native size/resize rules.

`PgList.DangerousBorrow<T>(pointer, owner)` requires an exclusively accessible,
palloc-compatible native list and its exact allocator owner. A null pointer is
valid NIL. The native tag is checked before reading a cell. A mismatched tag
throws `ArgumentException`; `DangerousTryBorrow` returns false with a null result
for that mismatch. Invalid metadata, expired ownership and unsupported server
versions still throw.

Borrowed disposal leaves native storage intact. Borrowed mutations can grow,
replace or free the container, so the native caller must obtain and retain the
current pointer after structural changes. The caller must prevent external
mutation/free while the wrapper is live. A context anchor cannot detect an earlier
external free, and borrowing does not establish ownership of pointer elements.

| Unsafe operation | Result and obligations |
|---|---|
| `DangerousGetPointer()` | Current native `List*`, or null for NIL; it can change after the list becomes empty |
| `DangerousGetCellsPointer()` | Current `ListCell*` using the selected server layout; growth may relocate it; caller enforces type, bounds and lifetime |
| `DangerousDetach()` | Consumes the wrapper and returns its current native pointer without freeing storage; the original context lifetime still applies |

Transferring a borrowed pointer does not acquire allocator release rights.
Transferred owned containers require native `list_free` or context reclamation.
See [memory contexts](/memory-contexts/) for backend capabilities and lifetime
rules, and [arrays](/arrays/) for SQL array values.
