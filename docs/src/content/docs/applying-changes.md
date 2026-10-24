---
title: Applying replicated changes
description: Write rows the way PostgreSQL's apply worker does, detect conflicts and track progress with replication origins.
---

A logical replication subscriber applies another server's changes to local
tables and remembers how far it got. `PgRelationWriter` writes rows through the
executor's own replication helpers, as PostgreSQL's built-in apply worker does, so
constraints, indexes and row triggers behave the same way. `PgReplicationOrigin`
records each source's progress and marks the commits made on its behalf.

pgrx has no equivalent. A pgrx extension builds executor state and calls the
`pg_sys` replication functions itself, as pglogical's C code does, and must follow
the signature changes between PostgreSQL versions.

The [subscriber sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Subscriber)
puts these together with [replication connections](/replication-connections/):
a background worker copies a publication's tables, then streams and applies the
provider's changes.

## Writing rows

```csharp
using (PgRelationWriter writer = PgRelationWriter.Open(relationOid))
{
    PgHeapTuple row = writer.CreateRow();
    row.Set("id", 7);
    row.Set("name", "seven");
    writer.Insert(row);

    PgHeapTuple key = writer.CreateRow();
    key.Set("id", 3);
    bool deleted = writer.Delete(key);
}
```

`Open` locks the table in `ROW EXCLUSIVE` mode for the current transaction.
`CreateRow` returns an empty row with the table's columns, and every row passed
to the writer uses them. Each change is visible to the next.

`Update` and `Delete` find the row to change by the table's replica identity
index, or else its primary key, reading only that index's columns from the key
row. For a table with neither, every column must match. Both return `false`
when no row matches, so the caller decides what a missing row means.
Constraint and unique index violations raise their usual PostgreSQL errors, such
as `23505`.

Row triggers fire as `session_replication_role` selects. An apply worker
usually sets it to `replica`, so only triggers enabled with `ENABLE REPLICA` or
`ENABLE ALWAYS` fire. Each operation checks the table privilege its statement
would: `INSERT`, `UPDATE` or `DELETE`, and `SELECT` for `Find`. Like the built-in
subscriber, the writer does not apply row-level security policies, so it refuses
a table whose policies apply to the user with `0A000`.

A change's `AFTER` triggers fire when the change completes, as they do for the
built-in subscriber. Dispose the writer before the transaction commits; a
transaction that commits with a writer still open fails with `25000` and rolls
back. A writer works in any transaction, including one that
[`PgBackgroundWorker.RunTransaction`](/background-workers/) starts.

### Batching inserts

A transaction that inserts many rows into one table can write them in batches.
`InsertBuffered` holds up to 1,000 rows and writes them together
through PostgreSQL's multi-insert, then adds their index entries and queues
their `AFTER ROW` triggers, as `COPY` and pglogical's apply do:

```csharp
foreach (PgReplicatedRow row in rows)
{
    writer.InsertBuffered(row);
}

writer.Flush();
```

The writer also writes the batch when it is full, before any other operation
on the writer, and when it is disposed. Until then the rows are not in the
table, so SQL does not see them. `NOT NULL` and check constraints are checked
as each row is added, while unique and foreign key violations are raised when
the batch is written. As with `COPY`, a table with `BEFORE` or `INSTEAD OF` row
insert triggers, or with a volatile column default other than `nextval`,
receives each row at once.

## Rows in replication's form

Logical replication sends each column as text, as binary, as NULL, or not at
all when an update leaves a TOAST value unchanged. A `PgReplicatedRow` holds
those values without converting them to .NET types:

```csharp
PgReplicatedRow row = writer.CreateReplicatedRow();
row.SetText(writer.Descriptor.GetOrdinal("id"), idBytes);
row.SetText(writer.Descriptor.GetOrdinal("name"), "seven");
row.SetNull(writer.Descriptor.GetOrdinal("note"));
writer.Insert(row);
```

The writer converts each value with its column type's input or receive
function, as the built-in apply worker does, so any type works, including
extension types. An insert gives columns it does not send their defaults; an
update keeps their current values. A sent NULL stays NULL. Text bytes from a
replication connection are already in the database's encoding, so the writer
only checks them; text from a string is converted. `Clear` resets a row
for the next change, so an apply loop can reuse one row per table.

Writers for a table share one `Descriptor` instance until the table's
definition changes. An apply loop can keep its column positions and rows
across transactions while `ReferenceEquals(saved, writer.Descriptor)`, and
rebuild them when the table changes. The
[subscriber sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Subscriber)
does this.

## Detecting conflicts

`Find` locks and returns the row that matches a key, along with the transaction
that wrote it:

```csharp
PgFoundRow? local = writer.Find(key);
if (local is { CommitTime: { } localTime } && localTime > remoteCommitTime)
{
    return; // The local row is newer; keep it.
}
```

`TransactionId` is the row's `xmin`. With `track_commit_timestamp` on,
`CommitOrigin` is the origin that wrote the row, or zero for a local write, and
`CommitTime` is when it committed. With it off, PostgreSQL does not record
them, so the origin is zero and the time is null. These are the values the
built-in subscriber uses to detect conflicts.

Before an insert, `FindConflict` looks for any row the new one would collide
with, as pglogical's apply does. It searches the replica identity index, then
each other valid unique index without expressions or a predicate, and returns
the first row it finds and locks. `IndexOid` names the index it matched:

```csharp
if (writer.FindConflict(row) is { } existing)
{
    // Resolve the conflict, for example by updating the existing row.
}
else
{
    writer.Insert(row);
}
```

Like a unique check, the search waits for a transaction that is still writing
a matching row. A NULL matches only in an index declared `NULLS NOT DISTINCT`,
and a column a replicated row does not send counts as NULL. An insert that
conflicts in an index the search skips still fails with `23505`. For `Find`,
`IndexOid` is `KeyIndexOid`, or zero when the table has no key.

## Tracking progress with origins

A replication origin stores how far one source has been applied:

```csharp
ushort origin = PgReplicationOrigin.Find("shop_provider") ?? PgReplicationOrigin.Create("shop_provider");
PgReplicationOrigin.SetupSession(origin);
PgLsn start = PgReplicationOrigin.GetSessionProgress();

// For each source transaction, in a local transaction:
PgReplicationOrigin.SetTransactionOrigin(remoteCommitLsn, remoteCommitTime);
// ... apply its rows, then commit.
```

Once a session has an origin, each commit it makes records that origin, the
source's commit position and the source's commit time, which becomes the commit
timestamp. The commit also advances the origin's progress, so the position
survives a crash along with the changes. After a restart, stream from
`GetSessionProgress`. Logical decoding can use the origin to skip changes a
subscriber applied, which avoids replication loops between two servers.

PostgreSQL keeps the position and time for later commits until they are set
again, so set them in every applied transaction. Only one session can use an
origin at a time; another session gets `55006`. `ResetSession` releases it.
`Advance` sets a starting position, and moves backward only when asked. `Drop`
fails with `55006` while a session uses the origin.

`PgLsn` is a write-ahead log position. It compares like the position it holds
and parses and formats PostgreSQL's `16/B374D848` text form.

These operations make the same checks as PostgreSQL's
`pg_replication_origin_*` functions, except privileges. Names starting with
`pg_` are reserved, as are `any` and `none` from PostgreSQL 16. Creating,
finding, dropping and advancing origins need a transaction. The SQL functions
are limited to superusers by default, so an extension should decide who may call
the functions that use these operations.
