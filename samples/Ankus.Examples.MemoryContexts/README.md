# Memory contexts example

This ports pgrx's `memory_contexts` example. It creates scratch contexts for
temporary work, returns sets whose state outlives individual rows, and keeps a
background worker's loop state in `TopMemoryContext`.

```sql
CREATE EXTENSION ankus_memory_contexts;

SELECT sum_with_scratch(ARRAY[1, 2, 3, 4, 5, 6]);         -- 21
SELECT scratch_count(100);                                -- 100
SELECT reset_rejects_stale_allocation();                  -- true
SELECT sum(x)::bigint FROM iter_count(0, 5) AS x;         -- 10
SELECT square FROM materialized_pairs(10) WHERE idx = 7;  -- 49
```

`sum_with_scratch` skips SQL NULL elements, like pgrx's flattened array
iterator. Its sum uses 64-bit checked arithmetic.

## Background worker

The worker registers only when PostgreSQL preloads the library:

```ini
shared_preload_libraries = 'Ankus.Examples.MemoryContexts'
```

After a restart, the server log contains
`memory_contexts demo worker starting (arg=123)` and then one
`memory_contexts demo worker tick N` line every five seconds. A configuration
reload also wakes the worker. Each line comes from the same counter, allocated
once in `TopMemoryContext`. Terminating the worker logs
`memory_contexts demo worker exiting`. The worker is not restarted.

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| `PgMemoryContexts::new(name)` | `PgMemoryContext.Create(name)` creates a child of the current context; `Dispose` deletes it. |
| `switch_to(\|ctx\| ...)` | `context.Run(() => ...)` selects the context and restores the caller on return or exception. |
| `reset()` | `context.Reset()` reclaims allocations and keeps the context usable. |
| `SetOfIterator`, `TableIterator` | `IEnumerable<T>` and named tuples; Ankus implements the multi-call protocol. |
| `PgMemoryContexts::TopMemoryContext.switch_to(palloc0)` | `PgMemoryContext.Get(PgMemoryContextKind.Top)!.CreateContextValue(0L)` names the owner explicitly. |
| `BackgroundWorkerBuilder` in `_PG_init` | `PgBackgroundWorker.Register` in `[PgModuleLoad]`, guarded by `NativeGlobals.process_shared_preload_libraries_in_progress`. |
| `set_argument(123i32.into_datum())` | `Argument = 123`, delivered as the entry's `nuint` parameter. |
| `BackgroundWorker::wait_latch(Some(5s))` | `PgBackgroundWorker.Wait(TimeSpan.FromSeconds(5))`. |

pgrx's module documentation warns that writing through a pointer after
`reset()` is a use-after-free. Ankus checks native allocation lifetimes, so
`reset_rejects_stale_allocation` shows the same write throwing
`ObjectDisposedException` before PostgreSQL memory is accessed.

## Deliberate differences

- The managed `int?[]` argument is a copy of the PostgreSQL array, and the
  list counted by `scratch_count` lives on the managed heap. Selecting a
  PostgreSQL context only affects native allocations, such as
  `PgMemoryContext.Current.Allocate`. pgrx's Rust `Vec` is likewise not a
  PostgreSQL allocation.
- Set functions keep iterator state on the managed heap instead of in
  `multi_call_memory_ctx`. Declare a `PgMemoryContext` parameter to receive the
  multi-call context when native state must live there.
- The worker installs PostgreSQL's reload and termination handlers through its
  generated entry point, so the sample has no explicit
  `attach_signal_handlers` call.
- pgrx's worker is not exercised by `#[pg_test]`. The Ankus integration tests
  preload the library, wake the worker and check consecutive ticks from one
  process.

See [memory contexts](https://ankus.net/memory-contexts/) and
[background workers](https://ankus.net/background-workers/).
