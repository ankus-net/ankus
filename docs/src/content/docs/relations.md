---
title: Relation access
description: Open PostgreSQL relations with checked cache references, explicit locks, and typed regclass values.
---

`PgRelation` holds a native PostgreSQL relation cache reference. A relation can
be a table, index, view, sequence, composite type, or another `pg_class` object.
Use it inside the backend callback that owns it and dispose references you open:

```csharp
using PgRelation relation = PgRelation.Open("public.messages");
string name = relation.Name;
PgTupleDescriptor descriptor = relation.TupleDescriptor;
```

The default lock is `PgLockMode.AccessShare`. Disposal closes the reference and
releases that lock acquisition. Other locks held by the transaction remain in
place. `Clone()` acquires an independent reference and AccessShare lock; dispose
each clone separately.

## Opening and locking

`Open(uint oid)` uses the exact relation OID. `Open(string name)` uses
PostgreSQL's `regclass` input, including native quoting, search paths, namespace
permissions and numeric OID syntax. `TryOpen(name)` uses `to_regclass` and returns
null when it returns SQL NULL; it accepts a numeric OID on PostgreSQL 16 and
later. Permission failures, lock failures and
concurrent deletion errors propagate as `PgException`.

```csharp
using PgRelation? relation = PgRelation.TryOpen("\"Sales\".\"Order Items\"");
if (relation is null)
{
    return "missing";
}

return relation.NamespaceName + "." + relation.Name;
```

Select any of PostgreSQL's eight relation lock modes through `PgLockMode`.
The native lock manager determines conflicts and honors settings such as
`lock_timeout`. See PostgreSQL's [explicit locking documentation](https://www.postgresql.org/docs/18/explicit-locking.html).
Opening a relation does not check whether the role may SELECT its rows; SQL
access still follows PostgreSQL's normal permissions.

`DangerousOpenWithoutLock(oid)` and its name overload acquire only a cache
reference. The caller must already hold at least AccessShare on the resolved
relation and keep the required protection throughout access. These references
close without releasing an external lock. Ordinary `Open` rejects
`PgLockMode.None`.

## Metadata and related relations

Names, namespace identity, kind and row estimates are read from the live native
relation. Changes processed through relcache invalidation are visible on later
reads. `TupleDescriptor` copies the actual physical descriptor, including dropped
columns and index attributes. Copied strings and descriptors remain usable after
the relation closes.

`TupleDescriptor.TypeOid` retains PostgreSQL's descriptor identity. Index
descriptors on PostgreSQL 13 have no row type and report zero; PostgreSQL 14+
uses the anonymous `record` OID. A descriptor with zero identity is usable for
metadata inspection, but cannot create tuples, arrays or typed SPI parameters.
Those operations throw `InvalidOperationException` before accessing the backend.

`Kind` preserves the exact native `relkind` character. Convenience properties
cover ordinary tables, materialized views, ordinary indexes, views, sequences,
composite types, foreign tables, partitioned tables and TOAST tables. A
partitioned index has kind `I` and does not satisfy `IsIndex`, matching pgrx.

`EstimatedTupleCount` preserves the native float4 `reltuples` estimate. Following
pgrx, exactly zero becomes null; the unknown estimate `-1` remains `-1`.

`GetIndices(lockMode)` eagerly opens every attached index with independent
ownership. A failed open closes earlier acquisitions. Dispose every returned
element:

```csharp
IReadOnlyList<PgRelation> indexes = relation.GetIndices();
try
{
    foreach (PgRelation index in indexes)
    {
        PgLog.Write(PgLogLevel.Notice, index.Name);
    }
}
finally
{
    foreach (PgRelation index in indexes)
    {
        index.Dispose();
    }
}
```

`DangerousOpenHeap()` returns an index's owning heap reference, or null when the
native relation has no index metadata. It acquires no heap lock: the caller must
already hold it. `DangerousGetIndicesWithoutLock()` similarly requires existing
protection for every returned index.

## SQL values and ownership

`PgRelation` maps to `regclass`; `uint` continues to map to `oid`. Nullable
relations represent SQL NULL. A nonnull `regclass` value opens its relation with
AccessShare, so zero, stale and nonexistent OIDs do not become usable handles.

```csharp
[PgFunction]
public static string RelationName(PgRelation relation) => relation.Name;

[PgFunction]
public static PgRelation FindRelation(string name) => PgRelation.Open(name);
```

Generated scalar callbacks close converted relation arguments on every exit
path. Returning a relation transfers its close obligation to the generated
boundary, which copies the OID and closes the reference. Use `Clone()` when
returning a separately owned reference that you also need to retain.
Aggregate support callbacks apply the same cleanup to relation inputs, state
and results. A `regclass` transition state retains its OID between calls and
opens a reference for each managed invocation.

Vectors and `PgArray<PgRelation?>` preserve element identity and SQL NULLs.
`PgArray` also preserves dimensions and lower bounds. Failed element or shape
conversion closes provisional references. As with other arrays, a vector rejects
shapes it cannot represent.

Set arguments stay live until the iterator is disposed, including early query
termination, errors, and iterators disposed before their first advance. New
relation values returned in a row are closed after their OIDs are copied. An
input reference yielded as a result stays owned by the iterator, so it can be
read or yielded again. These rules also apply to relation arrays and TABLE
columns. Relation references opened inside your own iterator still need ordinary
`using` ownership unless transferred as results.

SPI and function-call reads return caller-owned references:

```csharp
using PgRelation relation = Spi.ExecuteScalar<PgRelation>(
    "SELECT 'public.messages'::regclass");
```

Materializing an SPI row copies the `regclass` identity without opening the
relation. `row.Get<PgRelation>(ordinal)` acquires a fresh reference on each explicit
read. Reading a relation array acquires its nonnull elements; dispose them all.

Assigning a relation or relation array through `SpiRow.Set`, `PgHeapTuple.Set`
or `PgHeapTuple.Create` copies its OIDs, including array shape and NULL elements.
You can dispose the supplied handles immediately; later typed reads open fresh
references. These assignments preserve the cells' `regclass` type identity.

If a later column in `ExecuteScalars` fails conversion, earlier provisional
relation references are closed. Passing a relation as an SPI or function-call
argument copies its OID and retains the caller's ownership.

`ToDatum(context)` copies a live relation's OID into a checked `regclass` datum
with the supplied context lifetime. It does not consume the relation. A typed
`datum.Read<PgRelation>()` opens a new independently owned reference.

## Native lifetime and interoperability

PostgreSQL resource owners also reclaim cache references during statement,
portal or transaction cleanup. Checked access after native release
fails; later disposal is harmless. Automatic release follows PostgreSQL's native
lock rules and can transfer locks to the enclosing transaction. Dispose a live
reference explicitly when you need to release its own lock acquisition promptly.
Saving a managed wrapper does not extend its native resource owner's lifetime.
Garbage collection and finalizers never call PostgreSQL.

A relation opened inside `Spi.Connect` belongs to the statement that called
`Spi.Connect`, as one opened inside pgrx's `Spi::connect` does. Closing the
session does not close the relation or release its lock, so you can return it
from the callback and keep using it.

`DangerousGetPointer()` borrows the selected PostgreSQL version's `Relation`
pointer. The caller must preserve the reference and required locks for every raw
use. It does not establish a portable managed `RelationData` layout.

`DangerousBorrow(address)` takes no native reference and never closes the external
reference. `DangerousAdopt(address)` takes exactly one external close obligation
and calls `RelationClose` on disposal, without releasing an external lock.
`DangerousTakeOwnership()` consumes a borrowed wrapper and transfers that same
close obligation without incrementing the reference. These unsafe operations
require a proven live pointer, compatible selected headers, the current native
resource owner, appropriate locks and exclusive transfer of any close obligation.
Checks cannot detect an earlier external close or validate an arbitrary address.
Failed adoption or ownership transfer leaves the external close obligation with
the caller.

## Statistics

`CountHeapScan`, `CountIndexScan`, `CountHeapGetNext`, `CountHeapFetch`,
`CountIndexTuples`, `CountBufferRead` and `CountBufferHit` invoke the selected
PostgreSQL headers' statistics operations. They follow native statistics enablement
and association rules. `CountIndexTuples(long count)` preserves the signed count,
including negative adjustments. These helpers record activity; they do not scan,
fetch, or modify relation contents.
