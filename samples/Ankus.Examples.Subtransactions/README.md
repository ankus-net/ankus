# Subtransaction information example

This ports pgrx's `subtrans_infos` example. For any transaction ID, it reports
the transaction's status, its direct and top-level parents, its subtransaction
nesting level, and its commit time.

```sql
subtrans_infos(xid_input bigint)
RETURNS TABLE (
    xid integer,
    status text,                -- 'in progress', 'committed' or 'aborted'
    parent_xid integer,
    top_parent_xid integer,
    sub_level integer,
    commit_timestamp timestamp
)
```

The input's low 32 bits are the transaction ID; the high 32 bits are its epoch,
as returned by `pg_current_xact_id()`.

```sql
CREATE EXTENSION ankus_subtransactions;
CREATE TABLE t1 (id int);

BEGIN;
INSERT INTO t1 VALUES (1);
SAVEPOINT a;
INSERT INTO t1 VALUES (2);
SAVEPOINT b;
INSERT INTO t1 VALUES (3);

SELECT si.*
FROM pg_locks pgl
CROSS JOIN LATERAL subtrans_infos(pgl.transactionid::text::bigint) si
WHERE pgl.transactionid IS NOT NULL
ORDER BY si.xid;
--  xid  |   status    | parent_xid | top_parent_xid | sub_level | commit_timestamp
-- ------+-------------+------------+----------------+-----------+------------------
--  1647 | in progress |            |                |           |
--  1648 | in progress |       1647 |           1647 |         1 |
--  1649 | in progress |       1648 |           1647 |         2 |
COMMIT;

SELECT * FROM subtrans_infos(1647);
--  1647 | committed |  |  |  | 2025-09-29 10:18:52.488217
```

After `ROLLBACK TO SAVEPOINT`, the rolled-back subtransactions report
`aborted` with their parents. Transaction IDs are examples and will vary.

`parent_xid`, `top_parent_xid` and `sub_level` are NULL for top-level
transactions. They are also NULL for transactions older than the calling
transaction's `TransactionXmin`, because PostgreSQL may already have truncated
their `pg_subtrans` entries. `commit_timestamp` requires
`track_commit_timestamp = on`. IDs 1 and 2 (bootstrap and frozen) are always
committed. Invalid, future and truncated IDs fail with
`Invalid transaction ID ...` and SQLSTATE `XX000`.

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| `pg_sys::SubTransGetParent`, `TransactionIdDidCommit` and other transaction functions | `NativeMethods` with the same names, in an `unsafe` block |
| `pg_sys::TransactionXmin`, `track_commit_timestamp`, `MainLWLockArray` | `NativeGlobals` with the same names |
| `PgLwLockGuard` around `MainLWLockArray[44]` | `LWLockAcquire` and `LWLockRelease` in `try`/`finally` |
| `TableIterator::once((...))` | A one-element array of a named tuple, producing `RETURNS TABLE` |
| `Option<i32>`, `Option<Timestamp>` | `int?`, `PgTimestamp?` |
| `Timestamp::saturating_from_raw` | `PgTimestamp.FromRawSaturating` |
| `error!(...)` | `PgLog.Error(...)`, reported after the lock is released |

Native errors from these calls cross Ankus's guarded boundary as `PgException`;
PostgreSQL's error recovery releases the lightweight lock if the `finally`
block cannot.

## Deliberate differences

- On PostgreSQL 17 and later, pgrx substitutes `FirstNormalTransactionId` for
  the oldest commit-log ID because the shared variable was renamed. Ankus reads
  `TransamVariables->oldestClogXid` (`ShmemVariableCache` before 17) while
  holding `XactTruncationLock`, as the original C function does, so a truncated
  ID is reported as unavailable instead of failing in the commit-log lookup.
- pgrx calls `GetActiveSnapshot()` unconditionally and checks for NULL; that
  function asserts an active snapshot. Ankus calls `ActiveSnapshotSet()` first.
- pgrx's unit tests call private helpers directly. Samples have no unit-test
  assembly, so the integration tests cover those paths through SQL, including
  invalid, future and epoch-qualified IDs.

`PgTransaction.RegisterSubtransactionCallback` observes savepoints as they
start, commit and roll back. Its `PgSubtransactionId` values are PostgreSQL's
backend-local subtransaction counters, not the `xid` values reported here. See
[transaction callbacks](https://ankus.net/transaction-callbacks/#savepoints-and-subtransactions)
and [transaction IDs](https://ankus.net/transaction-ids/).
