# Threads example

This ports pgrx's `pgthread` example. A PostgreSQL backend is single-threaded:
only the thread that PostgreSQL used to call the extension may call back into
PostgreSQL. Managed worker threads may compute over copied values, but SPI,
logging, memory contexts and native bindings reject them before PostgreSQL runs.

```sql
CREATE EXTENSION ankus_threads;

SELECT hello_pgthread();                        -- Hello, pgthread
SELECT thread_sum(ARRAY[1, 2, 3, 4, 5]);        -- 15, computed on thread-pool threads
SELECT start_thread();
-- ERROR:  thread SPI work failed: PostgreSQL APIs can only be used on the active PostgreSQL backend thread.

DO $$ BEGIN EXECUTE 'SELECT 1;'; END $$;        -- ERROR:  oh no
SELECT 6 * 7;                                   -- the session continues
```

`start_thread` starts a managed thread that tries to run `SELECT 1;` through
SPI, waits for it, and reports the rejection. `thread_sum` divides a copied
`bigint[]` among four tasks, waits for them, and returns the exact sum from the
backend thread. Overflow fails with SQLSTATE `38000`.

Loading the library installs a `post_parse_analyze_hook`, as pgrx's `_PG_init`
does. The hook allocates one byte in the current memory context and rejects
statements whose complete text is exactly `SELECT 1;`, including statements
run through SPI. It chains to the previously installed hook otherwise. The
example uses `EXECUTE` to control the exact statement text, because a client
can send a top-level `SELECT 1;` with or without its semicolon.

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| `#[pg_extern] fn hello_pgthread()` | `[PgFunction] HelloPgthread()` |
| `post_parse_analyze_hook` saved and replaced in `_PG_init` | `[PgModuleLoad]` with `NativeGlobals.post_parse_analyze_hook` and a `[PgNativeCallback]` property |
| `pg_guard_ffi_boundary(\|\| prev_hook(...))` | `s_previous.Invoke(...)`, which guards the native call |
| `pg_sys::palloc(1)` | `NativeMethods.palloc(1)` |
| `panic!("oh no")` in the hook | `PgLog.Error("oh no")`, raised after managed frames unwind |
| pgrx's active-thread check | Ankus's backend-thread check on every PostgreSQL API |
| C `start_thread` running SPI on a POSIX thread | Managed `start_thread`, whose thread is rejected before SPI starts |

## Deliberate differences

- pgrx's companion C extension compiles `c_ext.c`, which saves and replaces
  PostgreSQL's global memory context and resource owner and runs SPI on a
  POSIX thread. Its own comment says this is not how PostgreSQL is meant to be
  used. Ankus extensions do not embed custom C, and the backend-thread rule makes
  that pattern fail safely: the managed thread receives
  `InvalidOperationException` and the backend thread reports it.
- pgrx marks the hook `#[pg_guard(unsafe_entry_thread)]`, opting out of its
  check that PostgreSQL entry points run on the thread that first called pgrx.
  Ankus has no such opt-out. Its native callbacks grant PostgreSQL access to the
  thread on which PostgreSQL invokes them; managed threads created by the
  extension never receive that access.
- The hook raises `XX000` through `PgLog.Error`, matching pgrx's panic SQLSTATE.
- pgrx's `#[pg_test]` only checks the greeting. The Ankus integration tests also
  check the thread rejection and recovery, the exact-text hook, chaining and a
  repeated library load.

See [Running .NET inside PostgreSQL](https://willibrandon.github.io/ankus/reference/execution/#backend-threads-and-tasks)
and [managed native callbacks and hooks](https://willibrandon.github.io/ankus/raw-values/#managed-native-callbacks-and-hooks).
