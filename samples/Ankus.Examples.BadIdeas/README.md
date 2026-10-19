# Bad ideas example

This ports pgrx's `bad_ideas` example: functions that do things an extension
should not do. Each one shows what Ankus does about it: the bad idea is rejected,
converted to a PostgreSQL error, contained by the sample, or documented because
no extension framework can contain it.

```sql
CREATE EXTENSION ankus_bad_ideas;

SELECT error('swallowed');           -- true: the managed ERROR was caught and discarded
SELECT warning('careful');           -- true, WARNING: careful
SELECT crash_postgres();             -- ERROR 38000: oh no!  (the session continues)
SELECT task_panic();                 -- ERROR 38000: oh no, from a task!
SELECT drop_struct();                -- INFO: before foo drop, INFO: Foo was dropped, ERROR 38000
SET statement_timeout = '1s';
SELECT loop_forever();               -- ERROR 57014: canceling statement due to statement timeout
BEGIN; SELECT random_abort(); COMMIT; -- INFO: in xact callback pre-commit; about half of the commits fail
SELECT write_file('/tmp/x', '\x00'); -- superusers only; others get ERROR 42501
SELECT fatal('bye');                 -- FATAL: this session ends
SELECT panic('bye');                 -- PANIC: every session ends while PostgreSQL recovers
SELECT crash_postgres_from_thread(); -- the backend process terminates; PostgreSQL recovers
```

Run the last three only on a disposable server.

## What happens to each bad idea

| pgrx function | Bad idea | Ankus outcome |
| --- | --- | --- |
| `panic` | Catch a `PANIC!` with `catch_unwind` | **Retained.** `PgLog.Panic` records the report before throwing. A `catch` block cannot discard it; PostgreSQL panics after managed frames unwind, as with pgrx. |
| `fatal` | Catch a `FATAL!` with `catch_unwind` | **Retained.** The session ends after managed frames unwind. Other sessions continue. |
| `error` | Swallow an `error!` with `catch_unwind` | **Swallowed, as in pgrx.** `PgLog.Error` throws an ordinary `PgException`; PostgreSQL has nothing to roll back. An error raised by PostgreSQL itself is different: Ankus keeps it pending unless a subtransaction rolls it back. See the [try and catch sample](../Ankus.Examples.TryCatch/). |
| `warning` | Report a warning | Reported; the function returns `true`. |
| `exec` | Run a program from the backend | **Documented only.** `Process.Start` installs .NET's process-wide terminal and `SIGCHLD` handling in the backend. Run programs outside PostgreSQL, or with PostgreSQL's own `COPY ... PROGRAM`. See [process signals](https://ankus.net/reference/execution/#process-signals). |
| `write_file` | Write arbitrary files as the server's operating-system user | **Contained by the sample.** Ankus does not sandbox extension code. The installation script revokes `EXECUTE` from `PUBLIC`, so only superusers and roles you grant can call it. |
| `http` | Make a blocking network request | **Documented only.** A blocking call cannot observe query cancellation unless the code checks `PgInterrupts` or links a cancellation token. Use a [background worker](https://ankus.net/background-workers/) for network work. |
| `loop_forever` | Loop forever | **Converted to an error.** `PgInterrupts.Check()` lets cancellation and `statement_timeout` stop it with `57014`; the session continues. |
| `random_abort` | Abort commits at random from a pre-commit callback | **Converted to an error.** A `PreCommit` exception rejects the commit: PostgreSQL rolls the transaction back and reports `38000`. |
| `_PG_init` callback | Register a transaction callback at load | **Not ported.** pgrx's callback body is commented out and does nothing. |
| `crash_postgres` | Opt out of pgrx's guard with `#[pg_extern(no_guard)]` and panic | **Rejected.** Ankus has no unguarded entry points. The exception becomes `ERROR 38000` after managed frames unwind, and the session continues. |
| `drop_struct` | Rely on `Drop` while an error unwinds | Disposal runs: `using` disposes `Foo` before PostgreSQL reports the error. |

Two more bad ideas are specific to .NET threads:

| Ankus function | Bad idea | Ankus outcome |
| --- | --- | --- |
| `task_panic` | Throw inside a task | **Converted to an error.** Waiting for the task rethrows the exception on the backend thread, which reports `38000`. |
| `crash_postgres_from_thread` | Leave an exception unhandled on a thread the extension started | **Documented; cannot be contained.** .NET terminates the process for an unhandled exception on any thread, so the backend dies. PostgreSQL treats that as a crash and restarts every session. Catch exceptions inside threads you start, or use tasks. |

## Deliberate differences

- pgrx's `drop_struct` opens `table doesn't exist`. PostgreSQL 13–15 reject that
  as invalid name syntax before `to_regclass` can return NULL, so the Ankus sample
  opens `table_does_not_exist` with `PgRelation.TryOpen` and reports the same
  `unable to open table` message on every PostgreSQL version.
- Managed exceptions report SQLSTATE `38000`; pgrx's Rust panics report `XX000`.
- `exec` and `http` are not ported, for the reasons above. `random_abort` uses
  `Random.Shared` where pgrx uses the `rand` crate.
- pgrx's example has no tests. The Ankus integration tests check every outcome
  above, including FATAL, PANIC and the thread crash in isolated servers.

See [Running .NET inside PostgreSQL](https://ankus.net/reference/execution/)
and [logging and errors](https://ankus.net/logging/).
