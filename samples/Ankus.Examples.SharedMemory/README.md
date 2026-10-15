# Shared memory example

This ports pgrx's `shmem` example. Every PostgreSQL backend shares a bounded
list, a bounded double-ended queue, a four-entry map, a struct and an integer,
each protected by its own lightweight lock, plus a lock-free atomic Boolean.

Shared memory must be reserved while PostgreSQL starts. Add the library to
`postgresql.conf` and restart the server:

```ini
shared_preload_libraries = 'Ankus.Examples.SharedMemory'
```

Without it, loading the library or creating the extension fails with
`this extension must be loaded via shared_preload_libraries.`

```sql
CREATE EXTENSION ankus_shared_memory;

SELECT vec_push('{"value1": 1, "value2": 2}');
SELECT * FROM vec_select();                -- {"value1":1,"value2":2}
SELECT vec_count(), vec_pop();             -- 1, {"value1":1,"value2":2}

SELECT deque_push_back('{"value1": 1, "value2": 1}');
SELECT deque_push_front('{"value1": 0, "value2": 0}');
SELECT * FROM deque_drain();               -- front to back

SELECT hash_insert(1, 10), hash_get(1);    -- 10
SELECT struct_set(7, -7), struct_get();    -- {"value1":7,"value2":-7}
SELECT primitive_set(42), primitive_get(); -- 42
SELECT atomic_set(true), atomic_get();     -- false (the old value), true
```

Changes are immediately visible to other backends and are not undone by
rollback. The values last until the server stops or recovers from a crash.

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| `#[derive(PostgresType, Serialize, Deserialize)] struct Pgtest` | `[PgType] readonly record struct Pgtest`; `JsonPropertyName` keeps pgrx's `value1`/`value2` JSON text. |
| `heapless::Vec<Pgtest, 400>` | `PgFixedList<Pgtest>` over a 400-element inline array. |
| `heapless::Deque<Pgtest, 400>` | `PgFixedDeque<Pgtest>` over the same inline storage shape. |
| `heapless::FnvIndexMap<i32, i32, 4>` | `PgFixedMap<int, int>` over four inline entries; removal and ordering follow `IndexMap`. |
| `PgLwLock<T>` with `share()` and `exclusive()` | `PgLwLock<T>` with `Share()` and `Exclusive()` guards in `using` statements. |
| `PgAtomic<AtomicBool>` with `load` and `swap` | `PgAtomic<bool>` with `Value` and `Exchange`. |
| `pg_shmem_init!` in `_PG_init` | `PgSharedMemory.Initialize` in `[PgModuleLoad]`. |
| `process_shared_preload_libraries_in_progress` | `NativeGlobals.process_shared_preload_libraries_in_progress`. |

Exclusive updates use `Mutate`, which edits the original shared value instead of
copying all 400 elements. Readers copy `Value` and enumerate the copy after the
shared lock is released. The full-collection warnings are written after the
exclusive lock is released because a mutation callback cannot call PostgreSQL.

## Deliberate differences

- Shared-memory names include the extension name (`ankus_shared_memory.vec`),
  because names are global to the cluster. pgrx uses `shmem_vec` and similar.
- pgrx's `hash_insert` unwraps heapless's insertion result, so a fifth distinct
  key fails with a Rust panic message and SQLSTATE `XX000`. `PgFixedMap.Set`
  throws `The fixed dictionary is full.`, reported as SQLSTATE `38000`.
  Replacing an existing key still succeeds at capacity.
- pgrx's example has no `#[pg_test]` coverage. The Ankus integration tests run
  the functions from two backends of a preloaded server, fill each collection,
  and check rollback, recovery and the error raised without preloading.

See [shared memory, locks and atomics](https://ankus.net/shared-memory/).
