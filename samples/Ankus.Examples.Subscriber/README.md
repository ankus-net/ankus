# Subscriber example

A small logical replication subscriber written with the toolkit a port of an
extension such as pglogical needs. It has no pgrx counterpart. Each subscription
runs in its own background worker, which:

1. connects to the provider through `PgReplicationConnection`;
2. creates a `pgoutput` slot and copies every published table in the slot's
   snapshot with `CopyInto`, in one local transaction that also records the slot's
   starting position with the subscription's replication origin;
3. streams changes and applies each remote transaction in one
   `PgBackgroundWorker.RunTransaction` call, writing rows through
   `PgRelationWriter` and `PgReplicatedRow` and recording the remote commit with
   `PgReplicationOrigin.SetTransactionOrigin`;
4. reports its durable progress to the provider once a second.

Like the built-in subscriber, it applies changes with `session_replication_role`
set to `replica` and `synchronous_commit` off, keeps local columns' defaults, and
skips updates and deletes whose row is missing. An insert whose key already exists replaces the local row,
as pglogical does by default. An error rolls back the remote transaction and
stops the worker; PostgreSQL restarts it five seconds later, and it resumes from
the last transaction it committed.

The provider needs `wal_level = logical` and a publication. The subscriber needs
the same tables, which may add columns with defaults, and a role that may write
them. The sample reads names as UTF-8, so both databases should use UTF-8.

```sql
-- On the provider
CREATE PUBLICATION shop FOR TABLE items, notes;

-- On the subscriber
CREATE EXTENSION ankus_subscriber;
SELECT subscribe('shop', 'host=provider dbname=shop user=replicator', 'shop');
SELECT name, pid FROM subscription;
SELECT * FROM pg_replication_origin_status;

SELECT unsubscribe('shop');
```

`unsubscribe` stops the worker, then drops the provider's slot and the
subscription's origin. Workers are registered dynamically, so after a server
restart, call `subscribe` again or adapt the sample to start them from a
preloaded supervisor, as pglogical does.

Each table's column positions and reusable rows last across transactions until
the local table changes, which writers signal by handing out a new descriptor.
`WriterBenchmarks` measures opening a writer and inserting through it inside
PostgreSQL; run them with `ankus bench --pg 18` in this directory.

The sample keeps to `pgoutput`'s protocol version 1. It does not stream large
transactions before they commit, synchronize tables added to the publication
later, or resolve conflicts beyond replacing a row with the same key. See
[applying replicated changes](../../docs/src/content/docs/applying-changes.md)
and [replication connections](../../docs/src/content/docs/replication-connections.md).
