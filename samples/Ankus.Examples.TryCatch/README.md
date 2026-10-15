# Try and catch example

This ports pgrx's `pgtrybuilder` example. `PgTryBuilder` emulates PostgreSQL's
`PG_TRY`/`PG_CATCH` blocks for Rust. C# already has structured exception
handling, so Ankus uses ordinary `try`, `catch` with exception filters, and
`finally`.

```sql
CREATE EXTENSION ankus_try_catch;

SELECT is_valid_number(42);                       -- 42
SELECT is_valid_number(41);                       -- ERROR 22003: number too small
SELECT get_relation_name('pg_class'::regclass);   -- pg_class, WARNING: FINALLY!
SELECT get_relation_name(4294967295);             -- <4294967295 is not a relation>, WARNING: FINALLY!
SELECT maybe_panic(true, true, 'hello');          -- three warnings, no error
SELECT maybe_panic(true, false, 'hello');         -- WARNING: FINALLY!, then ERROR 38000
```

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| `PgTryBuilder::new(\|\| body)` | `try { body }` |
| `.catch_when(code, \|cause\| ...)` | `catch (PgException error) when (error.SqlState == code)` |
| `.catch_rust_panic(\|cause\| ...)` | `catch (Exception error) when (error is not (PgException or PgQueryCanceledException))` |
| `.catch_others(\|cause\| ...)` | A final `catch (Exception error)` after more specific blocks |
| `cause.rethrow()` | `throw;`, which preserves the original exception |
| `.finally(\|\| ...)` | `finally { ... }` |
| `ereport!(ERROR, code, message)` | `throw new PgException(code, message)` |
| `panic!(...)` | Any other managed exception |

As with `PgTryBuilder`, `finally` runs after the selected catch block, including
when that block rethrows, and before an uncaught exception becomes a PostgreSQL
error. An exception filter that returns false, such as `maybe_panic`'s untrapped
case, leaves the exception unhandled without catching and rethrowing it.

### Recovering from PostgreSQL errors

A `PgException` thrown by managed code is an ordinary .NET exception: catching it
needs no rollback. An error raised by PostgreSQL itself is different. PostgreSQL
must roll back the failed work before the backend can continue. `get_relation_name`
therefore opens the relation inside `PgTransaction.RunInSubtransaction`. When
`relation_open` reports an OID that is not a relation (SQLSTATE `XX000`), the
subtransaction rolls back, the exception reaches the filtered `catch` block,
and the function returns its fallback text.

Without that scope, Ankus keeps the PostgreSQL error pending even if a `catch`
block handles it, and reports it when the function returns. pgrx documents that
catching and ignoring an internal PostgreSQL error is the caller's
responsibility; Ankus enforces the rollback instead.

## Deliberate differences

- Managed exceptions report SQLSTATE `38000`; pgrx's Rust panics report
  `XX000`. The messages are the same. Because pgrx matches `catch_when` by
  SQLSTATE, its `ERRCODE_INTERNAL_ERROR` handler would also catch a Rust panic.
  A `PgException` filter matches only PostgreSQL errors and explicitly thrown
  `PgException` values.
- pgrx's first trapped-panic warning prints its internal `ErrorReport` with
  `{:#?}`. .NET exceptions have no such report, so the sample prints the
  exception type and message, followed by the message as pgrx does.
- pgrx can raise a non-string panic payload; C# exceptions always carry a
  message, so that branch has no counterpart.
- pgrx never closes the relation it opens, which PostgreSQL reports as a
  relcache reference leak when the transaction commits. The Ankus sample
  disposes its `PgRelation`.
- `Debug.Assert(finished)` stands in for pgrx's `assert!(finished)`. C# guarantees
  that `finally` runs before control leaves the protected region.

pgrx's example has no `#[pg_test]` coverage. The Ankus integration tests check
exact results, SQLSTATEs, messages, warnings and continued use of the session.

See [transaction callbacks and recoverable work](https://ankus.net/transaction-callbacks/#recoverable-work)
and [Ankus for pgrx users](https://ankus.net/getting-started/from-pgrx/).
