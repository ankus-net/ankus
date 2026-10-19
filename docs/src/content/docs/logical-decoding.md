---
title: Logical decoding output plugins
description: Export a logical decoding output plugin and write its callbacks in C#.
---

A logical decoding output plugin turns committed changes from PostgreSQL's
write-ahead log into the data a replication slot returns. PostgreSQL loads the
plugin library by name and calls its `_PG_output_plugin_init` export with a
table of callbacks. Mark one static method with `[PgOutputPlugin]` to export it
as that initializer, then assign the callbacks the plugin implements from
[`[PgNativeCallback]` properties](/raw-values/#managed-native-callbacks-and-hooks).

The [WAL decoder sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.WalDecoder)
ports pgrx's `wal_decoder` example: it writes each transaction's BEGIN, row
changes and COMMIT as JSON documents.

## Declare the plugin

```csharp
using Ankus.Postgres;

public static unsafe partial class Decoder
{
    [PgNativeCallback(nameof(Begin))]
    private static partial OutputPluginCallbacks_begin_cbCallback BeginCallback { get; }

    [PgNativeCallback(nameof(Change))]
    private static partial OutputPluginCallbacks_change_cbCallback ChangeCallback { get; }

    [PgNativeCallback(nameof(Commit))]
    private static partial OutputPluginCallbacks_commit_cbCallback CommitCallback { get; }

    [PgOutputPlugin]
    public static void Initialize(OutputPluginCallbacks* callbacks)
    {
        callbacks->begin_cb = BeginCallback;
        callbacks->change_cb = ChangeCallback;
        callbacks->commit_cb = CommitCallback;
    }

    private static void Begin(LogicalDecodingContext* context, ReorderBufferTXN* transaction) { /* ... */ }

    private static void Change(LogicalDecodingContext* context, ReorderBufferTXN* transaction,
        RelationData* relation, ReorderBufferChange* change) { /* ... */ }

    private static void Commit(LogicalDecodingContext* context, ReorderBufferTXN* transaction, ulong commitLsn) { /* ... */ }
}
```

The initializer is a synchronous, accessible static method returning `void` with
one by-value `OutputPluginCallbacks*` parameter from the generated bindings.
An extension declares at most one. PostgreSQL requires the begin, change and
commit callbacks; startup, shutdown, truncate, message and origin filtering are
optional. Streaming and two-phase callbacks have their own requirements in
PostgreSQL's [output plugin documentation](https://www.postgresql.org/docs/current/logicaldecoding-output-plugin.html).

Use the field-specific callback types such as `OutputPluginCallbacks_change_cbCallback`.
Their names follow the record and field, so they stay the same on every
supported PostgreSQL version even when the canonical typedef name differs.

The callback table belongs to PostgreSQL and is valid only while the initializer
runs. The export enters managed code through the same guarded boundary as other
native callbacks: it initializes the extension's runtime on first use, and an
exception unwinds every managed frame before PostgreSQL reports an error.

## Use the plugin

Logical decoding requires `wal_level = logical`, or on PostgreSQL 19 `replica`,
which enables it once a logical slot exists, and free replication slots. The
plugin name is the extension library's file name without its suffix:
`AnkusLibraryName`, or `TargetName` by default, with `-<version>` added for a
versioned library:

```sql
SELECT pg_create_logical_replication_slot('changes', 'MyDecoder');
INSERT INTO accounts VALUES (1, 'Ada');
SELECT data FROM pg_logical_slot_get_changes('changes', NULL, NULL);
```

PostgreSQL loads the library from `dynamic_library_path` when it creates a
decoding context. Installing the extension's files is enough;
`CREATE EXTENSION` and `shared_preload_libraries` are not required. Ordinary
backends load the plugin for the SQL slot functions, and a WAL sender loads it
for `START_REPLICATION` on a logical replication connection.

PostgreSQL 14.24, 15.19, 16.15, 17.11, 18.6, 19 beta 3 and later releases load
only the output plugins listed in `output_plugin_libraries`, for every role,
including superusers. The setting's default lists only `pgoutput` and
`test_decoding`. PostgreSQL 13 has no such setting. A DBA adds a trusted
library and reloads the configuration:

```ini
output_plugin_libraries = 'pgoutput, test_decoding, MyDecoder'
```

An unlisted plugin fails with `library "MyDecoder" may not be used as an output
plugin` and SQLSTATE `42501`. List only plugins that are safe for every role with
the `REPLICATION` attribute.

## Load the plugin under another name

A slot names its plugin by library file, so a plugin has one name unless another
library loads it. pglogical ships a small `pglogical_output` library for this.
Add an `AnkusOutputPluginAlias` item for each extra name:

```xml
<ItemGroup>
  <AnkusOutputPluginAlias Include="my_decoder_output" />
</ItemGroup>
```

`dotnet publish` builds `my_decoder_output` with the library's suffix, such as
`my_decoder_output.so`, beside the extension library, using the same compiler
and PostgreSQL headers. It is a few lines of C that load the extension library
by name and forward `_PG_output_plugin_init` to it, so both names run the same
managed plugin in one loaded library. `ankus install` and `ankus package` copy
it with the library, and publishing without the item removes it.

```sql
SELECT pg_create_logical_replication_slot('changes', 'my_decoder_output');
```

PostgreSQL checks `output_plugin_libraries` against the name the slot uses, so
the DBA lists the alias itself. Listing only the extension library does not
trust its aliases. An alias uses ASCII letters, digits, `.`, `_`, `+` and `-`,
without a directory or suffix, and cannot be the library's own name. pgrx has no
counterpart; a pgrx extension builds such a library itself.

## Write output

Select text or binary output in the startup callback through
`options->output_type`. Text output must be in the database encoding: convert
UTF-8 bytes with `NativeMethods.pg_any_to_server` before appending them. Write
each message between `NativeMethods.OutputPluginPrepareWrite` and
`NativeMethods.OutputPluginWrite`, appending bytes to `context->@out` with
`appendBinaryStringInfo`. Do not pass data as the format argument of a
`printf`-style function.

State shared by a decoding context's callbacks belongs in PostgreSQL memory.
Allocate it in `context->context` during startup and store its address in
`output_plugin_private`. PostgreSQL deletes that memory context with the decoding
context, including after an error, when the shutdown callback does not run.

`PgRelation.DangerousBorrow(relation)` reads the changed relation's names and
descriptor. `NativeMethods.heap_getattr` reads a column, and
`PgDatum.DangerousCreate(datum, typeOid, PgMemoryContext.Current).Read<T>()`
converts it with detoasting and encoding conversion. The tuple representation
in `ReorderBufferChange` changed in PostgreSQL 17; select it with the
[PostgreSQL compilation symbols](/reference/build-settings/#postgresql-compilation-symbols).
Run per-change work inside `PgMemoryContext.RunTransient` so detoasted values are
released after each change.

## Errors

A callback error fails the SQL slot function or the replication command and
leaves the change unconsumed, so the next attempt fails the same way. Correct
the plugin, or move the slot past the change with `pg_replication_slot_advance`,
which decodes in fast-forward mode without the plugin's callbacks. Decoding runs
inside a transaction for row changes, but the startup callback can run without
one, for example when a replication connection creates a slot.

## Declaration diagnostics

The generator points at the declaration, parameter, modifier or attribute that
must change. Valid SQL functions and other declarations continue to generate
their output.

| Diagnostic | Required correction |
| --- | --- |
| ANKUS515 | Use an ordinary named method. Local functions, lambdas and explicit interface implementations can't be callbacks. |
| ANKUS516 | Make the initializer static. |
| ANKUS517 | Remove `async`. |
| ANKUS518 | Remove method type parameters. |
| ANKUS519 | Provide a concrete, non-virtual managed implementation, including the implementation of a partial method. |
| ANKUS520 | Return `void`. |
| ANKUS521 | Declare exactly one by-value `Ankus.Postgres.OutputPluginCallbacks*` parameter. |
| ANKUS522 | Use public, internal or protected internal accessibility. |
| ANKUS523 | Use accessible enclosing types without generic parameters or `file` locality. |
| ANKUS524 | Remove `UnmanagedCallersOnly`; Ankus generates the native export. |
| ANKUS525 | Put SQL, trigger, test, worker and initialization roles, and SQL metadata, on separate methods. |
| ANKUS526 | Declare one initializer per extension; the library exports one `_PG_output_plugin_init`. |
| ANKUS277 | Remove `Conditional` so every native invocation runs the initializer. |

Editors offer **Make static** for ANKUS516, **Make synchronous** for ANKUS517,
**Make internal** for ANKUS522 and ANKUS523, and **Remove** for ANKUS277 and
ANKUS524.
