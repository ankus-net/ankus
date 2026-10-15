# Hooks example

This ports pgrx's `hooks` example, a safety catch for data-removal commands:

- `DELETE` must have a `WHERE` clause.
- Only superusers can run `TRUNCATE`.

The library installs PostgreSQL's executor-run, parse-analysis and utility
hooks when it loads. Each hook chains to the hook that was installed before it,
or to PostgreSQL's standard implementation. The extension declares no SQL
functions, so creating it does not load the library. Load it in a session, or
preload it to protect every session:

```sql
CREATE EXTENSION ankus_hooks;
LOAD 'Ankus.Examples.Hooks';

CREATE TEMP TABLE t AS SELECT 1 AS one;
DELETE FROM t WHERE 0 = 0;   -- DELETE 1
DELETE FROM t;               -- ERROR:  DELETE queries must have a WHERE clause
TRUNCATE t;                  -- allowed for a superuser

CREATE ROLE bob;
SET ROLE bob;
TRUNCATE t;                  -- ERROR:  Only superusers can truncate
```

```ini
shared_preload_libraries = 'Ankus.Examples.Hooks'
```

Errors raised below a hook keep their structured diagnostics. A unique
violation in PL/pgSQL still reports its constraint, table and detail through
`GET STACKED DIAGNOSTICS` after crossing the executor hook.

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| `static mut PREV_..._HOOK` | `private static ..._hook_type s_previous...` |
| `pg_sys::ExecutorRun_hook = Some(executor_run_hook)` | `NativeGlobals.ExecutorRun_hook = ExecutorRunHook` |
| `#[pg_guard] unsafe extern "C-unwind" fn` | A static handler named by a `[PgNativeCallback]` property |
| `pg_guard_ffi_boundary(\|\| prev_hook(...))` | `s_previous.Invoke(...)`, through the native error guard |
| `pg_sys::standard_ExecutorRun(...)` | `NativeMethods.standard_ExecutorRun(...)` |
| `#[cfg(feature = "pg18")]` signatures | `#if ANKUS_PG13` and similar symbols for the selected headers |
| `PgBox<pg_sys::Query>` field access | `Query*` field access in an `unsafe` context |
| `pgrx::is_a(node, T_TruncateStmt)` | `node->type == NodeTag.T_TruncateStmt` |
| `panic!(...)` in a hook | `PgLog.Error(...)`, raised after managed frames unwind |
| `_PG_init` | `[PgModuleLoad]` |

## Idempotent installation

`[PgModuleLoad]` is retried after a failed load. The retry can come from a later
`LOAD`, or from the next invocation of a hook that the failed attempt already
installed. An installation that runs again saves the extension's own hook as
the previous hook, and chaining then calls the same hook forever.

The sample therefore records each hook's installation immediately after
assigning it and skips installed hooks on a retry. Separate flags matter: if a
later step fails, a single flag set at the end would not stop the earlier hooks
from being installed twice. A check that the global still contains this
extension's hook is not enough either, because another library can install its
own hook after this one.

If a hook is nevertheless installed twice, Ankus reports the recursion as
PostgreSQL's `stack depth limit exceeded` error (SQLSTATE `54001`) instead of
overflowing the native stack and restarting the server. The affected session
remains connected; other sessions are unaffected.

## Deliberate differences

- pgrx also installs a `ClientAuthentication_hook` whose body calls `todo!()`,
  so a preloaded pgrx library would reject every connection. Ankus's native
  bindings do not yet include pgrx's hand-written `libpq` module
  (`ClientAuthentication_hook`, `Port`), so this sample omits that hook.
- Hook errors use `PgLog.Error`, keeping pgrx's panic SQLSTATE `XX000`. Some
  clients treat internal errors as fatal to the connection; Npgsql, for
  example, closes it. Pass a `PgDiagnostic` with an explicit `SqlState`, such
  as `PgSqlStates.InsufficientPrivilege`, when clients should keep the session.
- pgrx's tests load the library through generated test functions. The Ankus
  integration tests load it with `LOAD` and through `shared_preload_libraries`,
  and check that other sessions are unaffected and that loading again does not
  install hooks twice.

See [managed native callbacks and hooks](https://ankus.net/raw-values/#managed-native-callbacks-and-hooks)
and [native hook registration](https://ankus.net/initialization/#native-hook-and-provider-registration).
