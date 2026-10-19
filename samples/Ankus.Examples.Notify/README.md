# LISTEN/NOTIFY cache invalidation example

This ports pgrx's `notify` example. `Notifications` wraps PostgreSQL's
`commands/async.h` functions (`Async_Notify`, `Async_Listen`, `Async_Unlisten`
and `Async_UnlistenAll`) and drives a cache-invalidation broadcast. An
`AFTER INSERT OR UPDATE OR DELETE` row trigger on `products` sends
`NOTIFY cache_invalidation, '<id>'` for every changed row, so an application
that listens on that channel can drop the matching cache entry without polling.

```sql
CREATE EXTENSION ankus_notify;

-- session 1: subscribe
LISTEN cache_invalidation;

-- session 2: change data
INSERT INTO products (name) VALUES ('widget');   -- id 1
UPDATE products SET name = 'gadget' WHERE id = 1;
DELETE FROM products WHERE id = 1;
```

Session 1 receives three notifications, each with the changed row's ID:

```text
Asynchronous notification "cache_invalidation" with payload "1" received from server process ...
Asynchronous notification "cache_invalidation" with payload "1" received from server process ...
Asynchronous notification "cache_invalidation" with payload "1" received from server process ...
```

The wrappers are also available as SQL functions with pgrx's names:

```sql
SELECT pgrx_listen('alpha');           -- LISTEN, committed with the transaction
SELECT pgrx_notify('alpha', 'hello');  -- NOTIFY alpha, 'hello'
SELECT pgrx_unlisten('alpha');         -- UNLISTEN alpha
SELECT pgrx_unlisten_all();            -- UNLISTEN *
```

Channel names are used exactly as given, without SQL identifier folding:
`pgrx_listen('MixedCase')` receives `pg_notify('MixedCase', ...)` but not
`NOTIFY MixedCase`, which folds to lowercase.

Notifications and subscription changes take effect when the transaction
commits and disappear when it rolls back. PostgreSQL reports an empty channel,
a channel of 64 or more bytes, or a payload of 8000 or more bytes (with the
default block size) with SQLSTATE `22023`. Lengths are measured in the
database encoding: 4000 copies of `é` exceed the payload limit in UTF-8 but not
in LATIN1. The wrappers convert text to the database encoding before calling
PostgreSQL.

## Coalesced invalidation

The per-row trigger is fine for single-row changes, but a bulk
`UPDATE products SET ... WHERE category = 5` touching a million rows would
queue a million notifications and can overrun the notification queue
(`max_notify_queue_pages`).

`CoalescedInvalidation` shows the correct pattern on an
`inventory(id, sku, category)` table:

1. A row trigger does not notify. It records the affected `category` in a
   per-transaction set in backend-local memory.
2. One `PreCommit` transaction callback, registered with
   `PgTransaction.RegisterCallback`, sends one
   `NOTIFY category_invalidation, '<category>'` per distinct category, in
   ascending order, just before commit.
3. An `Abort` callback discards the set when the transaction rolls back.

```sql
-- session 1: subscribe
LISTEN category_invalidation;

-- session 2: one statement, 500 rows across 2 categories
INSERT INTO inventory (sku, category)
SELECT 'sku' || g, g % 2 FROM generate_series(1, 500) g;
```

Session 1 receives exactly two notifications, with payloads `0` and `1`. An
UPDATE that moves a row to another category notifies both categories. As in
pgrx, a category recorded in a savepoint that rolls back is still notified when
the enclosing transaction commits; invalidating a cache entry that did not
change is harmless.

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| `notify::notify`, `listen`, `unlisten`, `unlisten_all` | `Notifications.Notify`, `Listen`, `Unlisten`, `UnlistenAll` |
| `pg_sys::Async_Notify(channel.as_ptr(), payload.as_ptr())` | `NativeMethods.Async_Notify(channel, payload)` with server-encoded strings |
| `CString::new(...)?` and `NulError` | `ArgumentException` for an embedded NUL character |
| `#[pg_extern] fn pgrx_notify(...) -> Result<(), String>` | `[PgFunction] PgrxNotify(string channel, string payload)` |
| `#[pg_trigger] fn products_notify` | `[PgTrigger] ProductsNotify(PgTriggerContext context)` |
| `trigger.new().or_else(\|\| trigger.old())` | `context.New ?? context.Old` |
| `row.get_by_name::<i64>("id")?` | `row.Get<long?>("id")` |
| `extension_sql!(..., requires = [products_notify])` | `[assembly: PgSql(..., Requires = ["products-notify"])]` |
| `thread_local!` `BTreeSet<i64>` and `Cell<bool>` | Static `SortedSet<long>` and `bool`; a backend runs one transaction on one thread |
| `pgrx::register_xact_callback(PreCommit, flush_dirty)` | `PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, FlushDirty)` |
| `#[pg_test]` tests of the wrappers | `[PgTest]` methods in `NotifyTests` |
| Delivery tests opening `postgres` client connections | Integration tests with separate Npgsql listening sessions |

## Deliberate differences

- pgrx documents that calling the wrappers outside a transaction is reported by
  PostgreSQL as an error. PostgreSQL does not check this: the native functions
  record work in transaction memory, which is released or about to be released.
  The C# wrappers check `IsTransactionState()` and throw
  `InvalidOperationException` before entering PostgreSQL, for example from a
  rollback callback. A backend test exercises this from a subtransaction's
  rollback callback.
- pgrx passes Rust strings to PostgreSQL unchanged. The C# wrappers convert
  text to the database encoding, so channels, payloads and byte limits are
  correct in databases that are not UTF-8.
- pgrx's coalescing trigger registers only `PreCommit` and `Abort` callbacks. A
  prepared transaction runs neither. pgrx keeps both registrations, so the next
  transaction to commit in that backend sends the prepared transaction's
  categories whether or not the prepared transaction ever commits. Ankus
  releases callbacks when a transaction is prepared, which would leave the
  categories recorded and stop later transactions from registering a flush. The
  sample therefore rejects `PREPARE TRANSACTION` with SQLSTATE `0A000`, as
  PostgreSQL does after `NOTIFY`, and the rollback discards the categories.
- The coalescing trigger registers its rollback callback before its commit
  callback and records a category after registration, so a failed registration
  cannot leave state for the next transaction.
- PostgreSQL text cannot contain NUL characters, so the SQL functions never
  reach the NUL check. The backend tests call the wrappers directly, as pgrx's
  tests do.

The integration tests check delivery from the writer's backend, commit-time
delivery, rollback and savepoint recovery, coalescing, byte limits in UTF-8 and
LATIN1 databases, subscription changes and prepared transactions.

See [triggers](https://ankus.net/triggers/) and
[transaction callbacks](https://ankus.net/transaction-callbacks/).
