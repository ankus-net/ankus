# Logical replication toolkit: inventory

This records what a port of a logical replication extension such as
[pglogical](https://github.com/willibrandon/pglogical) needs from Ankus, which
PostgreSQL interfaces provide it on PostgreSQL 13 through 19, and how pgrx
compares. It backs the plan in
[PROGRESS.md](../../../PROGRESS.md#beyond-pgrx-logical-replication-toolkit).
Line references are to pglogical at `5dabf03` and PostgreSQL `master` unless a
branch is named.

## Comparison with pgrx

pgrx 0.19.3 offers background workers whose transactions are scoped closures,
fixed-size shared memory through `pg_shmem_init!`, and raw `pg_sys` bindings.
It has no replication connection, apply helpers, error-context or exit
callbacks, child-process support or output plugin aliases. Everything below is
an Ankus addition; the raw bindings stay available for what it does not cover.

## What pglogical uses

| Area | pglogical | Notes |
| --- | --- | --- |
| Workers | Static supervisor, dynamic manager, apply and sync workers; `RegisterDynamicBackgroundWorker` with `bgw_notify_pid`; `before_shmem_exit` | Ankus covers registration, notify PIDs, connection by OID and latch waits. |
| Transactions | The apply worker starts a transaction lazily on the first change (`pglogical_apply.c:260`) and commits at the remote COMMIT (`:375`), holding it across `WaitLatchOrSocket` and per-message `MessageContext` resets (`:1390`, `:1544`) | Ankus worker transactions are callback-scoped (`NativeBackgroundWorker.cs:202`) and cannot span loop iterations. |
| Network | libpq: `PQconnectdbParams`, `PQexec`/`PQexecParams`, `START_REPLICATION` with `PQgetCopyData`/`PQputCopyData`, `COPY TO` from the origin and `COPY FROM` into a loopback connection | Ankus has no client library. |
| Apply | Own executor state, `simple_table_tuple_*`, `ExecInsertIndexTuples`, before/after row triggers, `heap_multi_insert`; conflict lookup with a dirty snapshot, `XactLockTableWait` and `table_tuple_lock`; origin and commit timestamp of the existing row | Ankus has no executor-level relation writes. |
| Origins | `replorigin_by_name`, `_create`, `_session_setup`, `_advance`, `_session_get_progress`, `_drop_by_name` and the session globals | Raw bindings only. |
| Shared memory | A worker array sized from `max_worker_processes` (`pglogical_worker.c:735`), one LWLock tranche | Ankus shared values have a compile-time size. |
| Errors | Error context callbacks (`pglogical_apply.c:1149`, `:1580`, `pglogical_functions.c:1320`); three `PG_ENSURE_ERROR_CLEANUP` sites (`pglogical_sync.c:923`, `:932`, `:1113`); two places that swallow errors without a subtransaction (`pglogical_conflict_history.c:274`, `pglogical_functions.c:631`) | A port should record conflicts in a subtransaction. |
| Child processes | `pg_dump` and `pg_restore` via `fork`/`execv`/`waitpid`, or `CreateProcess` on Windows (`pglogical_sync.c:117`) | .NET `Process` takes over `SIGCHLD`. |
| Output plugin | The slot plugin `pglogical_output` is a shim library whose `_PG_output_plugin_init` loads the main library's entry | Ankus exports one plugin per library, named after it. |

## PostgreSQL interfaces

### Replication connection: `libpqwalreceiver`

Core's subscriber does not link libpq. It calls `load_file("libpqwalreceiver",
false)` and uses the `WalReceiverFunctions` table (`replication/walreceiver.h`,
`PGDLLIMPORT` on 13–19). Functions that change across versions:

```c
/* connect */        13–15: (conninfo, bool logical, appname, char **err)
                     16:    (conninfo, bool logical, bool must_use_password, appname, err)
                     17+:   (conninfo, bool replication, bool logical, bool must_use_password, appname, err)
/* identify_system*/ 13–18: (conn, TimeLineID *)        19: + XLogRecPtr *server_lsn
/* create_slot */    13–14: (conn, slotname, temporary, CRSSnapshotAction, XLogRecPtr *)
                     15–16: + bool two_phase            17+: + bool failover
```

`receive(conn, char **buffer, pgsocket *wait_fd)` returns a length, 0 to wait
on `wait_fd`, or -1 at the end of COPY; the buffer is freed by the next call.
`exec(conn, query, nRetTypes, retTypes)` returns a typed tuplestore and reports
SQL failures as `WALRCV_ERROR` (with `sqlstate` from 14), while connection and
protocol failures raise ERROR. The table's field order changes in 17, so the
bridge compiles against each version's headers. Before 17, connections are
replication connections; `replication=database` still accepts SQL.

`create_slot` and `startstreaming` hard-code pgoutput options. A pglogical port
sends `CREATE_REPLICATION_SLOT` and `START_REPLICATION` through `exec`, which
returns `WALRCV_OK_COPY_BOTH` for streaming. Core's table sync reads `COPY ...
TO STDOUT` through `receive` and loads it with in-process `BeginCopyFrom` and a
read callback (`tablesync.c:1198`, `:1214`); that replaces pglogical's loopback
`COPY FROM`. `exec` takes no bind parameters, so values are quoted with the
backend's `quote_literal_cstr` and `quote_identifier`.

### Apply: `execReplication.c`

```c
bool RelationFindReplTupleByIndex(Relation, Oid idxoid, LockTupleMode, TupleTableSlot *search, TupleTableSlot *out);
bool RelationFindReplTupleSeq(Relation, LockTupleMode, TupleTableSlot *search, TupleTableSlot *out);
/* 13 */  void ExecSimpleRelationInsert(EState *, TupleTableSlot *);
/* 14+ */ void ExecSimpleRelationInsert(ResultRelInfo *, EState *, TupleTableSlot *);  /* Update and Delete likewise */
```

The executor state around them is built in `worker.c`'s static
`create_edata_for_relation`; `ExecInitRangeTable` gains permission info in 16
and unpruned relations in 18, result relations move to
`es_opened_result_relations` in 14, `EvalPlanQualInit` gains an argument in 16,
and 18 adds conflict indexes. From 16 an index lookup may use an index other
than the replica identity.

### Replication origins

`replorigin_session_setup` gains `acquired_by` in 16. PostgreSQL 19 renames
`RepOriginId` to `ReplOriginId` and replaces the three session globals with
`replorigin_xact_state`.

## Capabilities to build

| Capability | Built on | Covers |
| --- | --- | --- |
| Replication connection | `walrcv_*`, `WaitLatchOrSocket` | Connecting, SQL with rows, slot and streaming commands, COPY out, feedback |
| Relation writes and lookups | `execReplication.c`, executor state, `BeginCopyFrom` | Insert, update and delete with indexes, constraints and triggers; locked lookup by index or scan; bulk load from a COPY stream |
| Replication origins | `replorigin_*` | Session origin, progress, advance |
| Long-lived worker transactions | `StartTransactionCommand` and Ankus recovery | A transaction held across waits and messages |
| Startup-sized shared memory | `shmem_request_hook` | Arrays sized from settings |
| Error context and exit callbacks | `error_context_stack`, `before_shmem_exit` | Context lines and FATAL-safe cleanup |
| Child processes | `posix_spawn`, `CreateProcess` | `pg_dump` and `pg_restore` |
| Output plugin aliases | Generated C library | A second plugin name |
