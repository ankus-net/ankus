# WAL decoder example

This ports pgrx's `wal_decoder` example, a change data capture (CDC) output
plugin. PostgreSQL's [logical decoding](https://www.postgresql.org/docs/current/logicaldecoding-explanation.html)
replays committed changes from the write-ahead log and calls the plugin's
callbacks: one BEGIN, one change per inserted, updated or deleted row, and one
COMMIT per transaction. Each callback writes one JSON document to the
replication slot, which clients consume through SQL functions or the streaming
replication protocol.

`[PgOutputPlugin]` exports `WalDecoder.Initialize` as the library's
`_PG_output_plugin_init`. It assigns `[PgNativeCallback]` properties to the
startup, begin, change, commit and shutdown callbacks.

## Requirements

Logical decoding needs `wal_level = logical`, which takes effect after a
restart. PostgreSQL 14.24, 15.19, 16.15, 17.11, 18.6, 19 and later minor
releases also load only output plugins listed in `output_plugin_libraries`, for
every role including superusers. Add the library to that list and
reload the configuration:

```ini
wal_level = logical
output_plugin_libraries = 'pgoutput, test_decoding, Ankus.Examples.WalDecoder'
```

Without that entry, creating or reading a slot fails with
`library "Ankus.Examples.WalDecoder" may not be used as an output plugin`
(SQLSTATE `42501`). Trust only plugins that are safe for every user with the
`REPLICATION` attribute. Older minor releases have no such setting and accept
the plugin without it.

The plugin's name is the published library's name, `Ankus.Examples.WalDecoder`.
PostgreSQL loads it from `dynamic_library_path` when a decoding context starts,
so `shared_preload_libraries` is unnecessary. `CREATE EXTENSION ankus_wal_decoder`
is optional and creates no SQL objects.

## Example

```sql
CREATE TABLE person (name TEXT, age INT);
ALTER TABLE person REPLICA IDENTITY FULL;
CREATE PUBLICATION gotham_pub FOR TABLE person;

SELECT pg_create_logical_replication_slot('gotham_slot', 'Ankus.Examples.WalDecoder');

INSERT INTO person VALUES ('Bruce Wayne', 42), ('Clark Kent', 33);

SELECT * FROM pg_logical_slot_get_changes('gotham_slot', NULL, NULL);
--     lsn    | xid |                                     data
-- -----------+-----+------------------------------------------------------------------------------
--  0/16A87C8 | 581 | {"typ":"BEGIN"}
--  0/16A87C8 | 581 | {"typ":"INSERT","rel":"public.person","new":{"name":"Bruce Wayne","age":42}}
--  0/16A8810 | 581 | {"typ":"INSERT","rel":"public.person","new":{"name":"Clark Kent","age":33}}
--  0/16A8888 | 581 | {"typ":"COMMIT","committed":779145498360779,"change_count":2}

UPDATE person SET name = 'Batman' WHERE name = 'Bruce Wayne';

SELECT xid, jsonb_pretty(data::jsonb) FROM pg_logical_slot_get_changes('gotham_slot', NULL, NULL);
-- {"typ":"BEGIN"}
-- {"typ":"UPDATE","rel":"public.person","old":{"name":"Bruce Wayne","age":42},"new":{"name":"Batman","age":42}}
-- {"typ":"COMMIT","committed":779179731927669,"change_count":1}
```

LSNs, transaction IDs and times vary. `committed` is the commit time in
PostgreSQL's native units, microseconds since 2000-01-01 UTC. A publication is
not required by this plugin; the example keeps pgrx's statement.

A WAL sender loads the same plugin for `START_REPLICATION SLOT ... LOGICAL` on
a replication connection and streams the same documents.

## JSON format

Documents follow pgrx's member order: `typ`, `committed`, `rel`, `old`, `new`,
`change_count`, omitting members that do not apply. `rel` and the column names
use PostgreSQL's identifier quoting, so a column named `Value` appears as
`"\"Value\""`. Text uses serde_json's compact escaping: quotation marks,
backslashes and control characters are escaped, and other characters, including
non-ASCII text, are written as is.

## Limitations

These are pgrx's limitations, which the port keeps:

- Only REPLICA IDENTITY FULL fully supports old rows. With the default identity,
  an UPDATE that keeps its key logs no old row and reports `"old":{}`; a DELETE
  reports only the key columns, or `{}` for a table without a key.
- Only `integer` and `text` columns are serialized. Another type fails decoding
  with SQLSTATE `0A000` and leaves the change in the slot. Use
  `pg_replication_slot_advance` to move past it; it decodes in fast-forward mode
  without the plugin's callbacks. NULL values of any type are omitted.
- An UPDATE that keeps a large TOASTed value does not log that value again. Like
  pgrx's `FromDatum`, the plugin reads it from the TOAST table while decoding, so
  the new row is complete. A replication slot does not stop VACUUM from removing
  user data, though: if a later change makes the value dead and VACUUM removes it
  before a lagging slot decodes the UPDATE, decoding fails. Built-in plugins
  report such values as unchanged instead of reading them.

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| `#[no_mangle] #[pg_guard] extern "C-unwind" fn _PG_output_plugin_init` | `[PgOutputPlugin] Initialize(OutputPluginCallbacks* callbacks)` |
| `callbacks.change_cb = Some(pg_decode_change)` | `callbacks->change_cb = ChangeCallback` with a `[PgNativeCallback]` property |
| `#[pg_guard] unsafe extern "C-unwind" fn pg_decode_change(...)` | A static handler with the selected headers' exact signature |
| `serde::Serialize` for `Action` and `Tuple` | `DecodedAction` and `DecodedRow` written with `Utf8JsonWriter` |
| `serde_json` escaping | A `JavaScriptEncoder` that escapes only JSON syntax and control characters |
| `PgRelation::from_pg(relation)`, `namespace()`, `name()`, `tuple_desc()` | `PgRelation.DangerousBorrow(relation)`, `NamespaceName`, `Name`, `TupleDescriptor` |
| `pg_sys::heap_getattr` and `FromDatum` | `NativeMethods.heap_getattr` and `PgDatum.DangerousCreate(...).Read<string>()` |
| `txn.commit_time`, `txn.xact_time.commit_time`, `__bindgen_anon_1.commit_time` | `commit_time` on PostgreSQL 13, 14 and 19, `xact_time.commit_time` on 15–18 |
| `ReorderBufferTupleBuf` (13–16) and `HeapTupleData` (17+) | The same split under `#if ANKUS_PG13 \|\| ... ANKUS_PG16` |
| `OutputPluginPrepareWrite`, `appendStringInfo`, `OutputPluginWrite` | `NativeMethods.OutputPluginPrepareWrite`, `appendBinaryStringInfo`, `OutputPluginWrite` |
| `test_action_begin` | `[PgTest] ActionBeginSerializesItsType` |

## Deliberate differences

- pgrx's README asks for `shared_preload_libraries = 'wal_decoder'`. PostgreSQL
  loads output plugins on demand, so this port does not preload the library. Its
  README adds the `output_plugin_libraries` entry that current PostgreSQL
  releases require, which pgrx's README predates.
- pgrx passes the JSON as the format string of `appendStringInfo`, so a `%` in
  data is interpreted as a format directive. Ankus appends the bytes with
  `appendBinaryStringInfo`.
- pgrx writes UTF-8 JSON regardless of the database encoding. Ankus converts it
  to the database encoding, as PostgreSQL requires for text output, so a LATIN1
  database returns correct text.
- pgrx allocates the decoding state with Rust's allocator and frees it in the
  shutdown callback, which does not run after an error. Ankus allocates it in
  the decoding context's memory, which PostgreSQL always releases. Per-change
  work runs in a transient memory context, so detoasted values do not
  accumulate during a large transaction.
- pgrx calls `todo!()` for an unsupported type and does not skip dropped
  columns, whose old values can still be present. Ankus skips dropped columns
  and reports unsupported types with SQLSTATE `0A000` and a message naming the
  column and type OID.
- pgrx logs `Anon: output plugin initialized` at `DEBUG1`, a leftover from
  another extension; Ankus logs `wal_decoder: output plugin initialized`.
- pgrx's callback test is empty and ignored, because a backend test runs inside
  an uncommitted transaction. The Ankus integration tests decode committed
  changes through SQL slot functions and a WAL sender, and check transaction
  IDs and commit times against `track_commit_timestamp`.

See [logical decoding output plugins](https://willibrandon.github.io/ankus/logical-decoding/)
for the declaration, output, memory and error rules.
