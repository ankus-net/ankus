---
title: Running .NET inside PostgreSQL
description: Understand backend threads, tasks, signals, managed memory, and PostgreSQL resource lifetimes.
---

Your extension is a native library loaded into a PostgreSQL process. Its managed
code runs in that process with the server's operating-system permissions.
Ankus's native boundary protects PostgreSQL error unwinding; it does not isolate
extension code from the host process.

## Backend threads and tasks

Your function runs synchronously on the PostgreSQL backend thread that called
it. SPI, native bindings, logging and PostgreSQL-owned resource APIs require
that active thread and the appropriate invocation phase. Calling them from
`Task.Run` throws `InvalidOperationException`.

Generated SQL functions are synchronous. An `async` method, `Task`/`ValueTask` result or
`IAsyncEnumerable<T>` does not become an asynchronous PostgreSQL function.
Await continuations, thread-pool work and timer callbacks do not inherit backend
access. `ConfigureAwait(false)` does not change that rule, and synchronously
waiting for a task does not grant its worker thread access either.

The generator reports these contracts separately, at the asynchronous keyword or
return type:

| Diagnostic | Unsupported SQL function contract |
| --- | --- |
| `ANKUS030` | An `async` entry method, including `async void` and partial implementations. |
| `ANKUS031` | A `Task`, `Task<T>`, `ValueTask` or `ValueTask<T>` result. Return the completed SQL value. |
| `ANKUS032` | An `IAsyncEnumerable<T>` result. SETOF and TABLE functions use synchronous `IEnumerable<T>`. |

These checks use framework type identity; a user-defined SQL type named `Task`
does not become an asynchronous result. Keep the PostgreSQL entry method
synchronous and finish its work before returning.

Managed worker threads can compute over detached managed values using operations
that do not call PostgreSQL. Some owned-value APIs, such as `PgNumeric`
arithmetic, still need backend access. Keep borrowed native values and transaction
resources on their owning thread. Complete work
that belongs to a SQL call before returning from it, and bound concurrency:
multiple PostgreSQL backends can each create their own managed work. For
long-running database work with its own transaction and shutdown lifecycle, use
a [PostgreSQL background worker](/background-workers/).

The [threads sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.Threads)
ports pgrx's `pgthread` example. Its managed thread is rejected before running
SPI, and its tasks sum a copied array without calling PostgreSQL.

Exceptions on other threads never reach Ankus's boundary. Waiting for a task
rethrows its exception on the backend thread, where it becomes ERROR. An exception
left unhandled on a thread the extension started is different: .NET terminates
the process, so the backend exits abnormally and PostgreSQL restarts every
session to recover. Catch exceptions inside threads you start, or use tasks and
wait for them. The [bad ideas sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.BadIdeas)
demonstrates both.

Call `PgInterrupts.Check()` periodically in long managed loops on the backend
thread. A managed loop does not otherwise guarantee a PostgreSQL interrupt check;
an unrelated .NET `CancellationToken` is not automatically connected to query
cancellation. See [cancellation and logging](/logging/#errors).

## Initialization and process lifetime

`[PgInitialize]` runs when the library first loads in a backend, before its SQL
functions. Successful initialization and managed static state belong to that
backend process. Failed initialization can be retried; PostgreSQL rolls back SQL
work with its transaction, while managed mutations remain. See
[extension initialization](/initialization/) for preload and transaction rules.

Shared preload uses Ankus's packaged runtime support for PostgreSQL startup and
fork behavior. On Unix, the postmaster retires runtime service threads between
managed callbacks. Finish explicitly created threads before returning from a
preload callback. Timers and queued work resume when managed code reenters or a
backend starts; use PostgreSQL workers for continuous server work.

Each backend has its own managed static state. Independently published extension
libraries also carry their own runtime and state; a static field is not shared
storage between extensions or backend processes. Use PostgreSQL tables or
[registered shared memory](/shared-memory/) when values must be shared.

Dropping SQL objects does not unload the native library or reset managed statics.
End sessions that loaded a library before replacing it, then reconnect.
Shared-preload changes require a server restart. Native AOT libraries cannot be unloaded with
`dlclose` or `FreeLibrary`, as described in Microsoft's
[native library guidance](https://github.com/dotnet/samples/blob/main/core/nativeaot/NativeLibrary/README.md).

## Process signals

PostgreSQL uses signals for cancellation, shutdown and child-process handling.
Ordinary .NET application APIs can also participate in process-wide signal
handling:

| API | Process-level effect |
|---|---|
| `PosixSignalRegistration.Create` | Registers a handler for a selected signal; its callback can cancel default handling. |
| `Console.CancelKeyPress` | Registers .NET handlers for `SIGINT` and `SIGQUIT` on Unix. |
| `Process.Start` | Initializes .NET's Unix terminal/signal support and registers child-exit handling for `SIGCHLD`. |

These effects are documented by the
[signal registration API](https://learn.microsoft.com/en-us/dotnet/api/system.runtime.interopservices.posixsignalregistration.create?view=net-10.0)
and the .NET 10 implementations of
[Console](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Console/src/System/Console.cs)
and [Process](https://github.com/dotnet/runtime/blob/v10.0.12/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Unix.cs).

The kernel can deliver a signal sent to the backend process to any thread that
leaves it unblocked, but PostgreSQL's handlers must interrupt the backend thread.
Threads the .NET runtime starts, including the finalizer, thread-pool workers and
threads you create, therefore block PostgreSQL's asynchronous signals. On macOS,
where PostgreSQL's mask changes reach every thread, a handler entered on another
thread passes its signal to the backend thread instead. A native library that
starts its own threads inside a backend should block these signals in them too.

Keep PostgreSQL's signal ownership intact. Ankus's runtime support does not grant
arbitrary application signal handlers PostgreSQL semantics. Use `PgInterrupts`
for query interrupts and the background-worker APIs for worker signals. Run
application hosting or child-process orchestration outside the backend when it
needs to own that process lifecycle. Use `PgLog` for server messages;
`Console.WriteLine` does not produce a PostgreSQL notice.

## Managed memory and database memory

The .NET garbage collector owns managed objects such as strings, arrays, lists
and dictionaries. PostgreSQL memory contexts own separate native allocations.
Resetting a memory context does not collect the managed heap. Ankus lifetime
adapters release their managed roots when the corresponding native owner ends;
the collector reclaims eligible objects later.

PostgreSQL's [`work_mem`](https://www.postgresql.org/docs/18/runtime-config-resource.html#GUC-WORK-MEM)
controls query-operation memory such as sorts and hash tables. It does not cap
your managed collections or static caches. Managed allocations are also absent
from PostgreSQL memory-context accounting. Measure total process memory as well
as managed allocations, especially with large result sets or retained values.

Budget for the number of backend and parallel-worker processes and for the
extension runtimes loaded in each process. Keep caches bounded and release
references when they are no longer needed. A GC setting on one runtime is not
a server-wide memory budget. Prefer streaming where the API permits it, and
dispose PostgreSQL resources on the owning backend thread rather than relying
on a managed finalizer.

## Values

Strings, byte arrays, JSON values, and SPI result rows are managed copies. You
can keep them after the function returns. Editing a `SpiRow` changes that copy;
use SQL to update the database.

Borrowed views and raw native values retain their owner's lifetime. Use the
documented copy operation when data must survive it. Garbage collection does
not extend a native buffer's validity or make a pointer safe to retain.

## Server resources

A `SpiSession` is valid only inside its `Spi.Connect` callback. Nested sessions
temporarily suspend access to the enclosing session.

Prepared statements can outlive a callback. A session-owned statement needs
`Keep()` before the session closes. Dispose it on the backend thread when it is
no longer needed.

Cursors normally end with their transaction. `Detach()` transfers responsibility
for closing the cursor; it does not extend the PostgreSQL portal's lifetime.

`PgMemoryContext` and `PgAllocation` follow PostgreSQL's native ownership tree.
A live handle can be reused by a later synchronous callback from the same
extension and backend. Reset, parent deletion, and transaction cleanup invalidate
affected handles; checked access then throws before dereferencing freed storage.
Dispose owned resources on the backend thread. `Run` temporarily selects a
context and restores the previous one even when the callback throws. See
[memory contexts](/memory-contexts/) for reset variants and allocation rules.

Cursors opened in an SPI environment containing trigger transition tables close
when that trigger callback ends, including detached cursors. Retained plans
resolve transition tables from the current invocation; they do not preserve
previous transition rows. Owned `PgTriggerContext` metadata and fetched rows
survive callback completion. See [triggers](/triggers/).

Event trigger metadata and DDL/drop/rewrite snapshots also remain owned after
callback completion. Query their helpers only through the current invocation's
context and in the matching event phase. Nested callbacks restore their parent
context before returning. See [event triggers](/event-triggers/).

## Conditional entry methods

Declare PostgreSQL functions, event triggers and `[PgTest]` methods without
`System.Diagnostics.Conditional`. C# can omit a conditional method call entirely
when its symbol is absent at the generated call site. That would let a database
entry return successfully without performing its work, or let a backend test
pass without executing its assertions.

Ankus reports `ANKUS277` at the attribute, including attributes on either partial
declaration. It rejects the attribute even when the symbol is currently enabled,
so each publication retains the same entry contract. Put optional behavior inside
the method body instead. Native callback handlers have the same restriction;
see [managed native callbacks](../../raw-values/#managed-native-callbacks-and-hooks).

## Errors

An unhandled managed exception becomes PostgreSQL ERROR after `finally` blocks
and `using` scopes finish. Every generated entry point has this boundary; unlike
pgrx's `#[pg_extern(no_guard)]`, it cannot be turned off.

SPI calls use internal subtransactions. A failed call rolls back its work before
throwing `PgException`, allowing your function to catch it and continue. Successful
calls remain part of the caller's transaction.

Raw, memory and direct value calls preserve PostgreSQL's resource rules. A native error
outside a recoverable subtransaction remains pending even if managed code
catches its exception. Ankus blocks further backend work and raises the original
error when managed cleanup has unwound. Use
[`PgTransaction.RunInSubtransaction`](/transaction-callbacks/#recoverable-work)
for operations that need rollback and recovery. `TryParse` helpers return false
for invalid input without leaving an error pending. For built-in numeric, date and
time, network, geometry and range types on PostgreSQL 16 and later they use
PostgreSQL's soft input errors, which need no rollback; otherwise they roll back an
internal subtransaction first. As with PostgreSQL's `pg_input_is_valid`, only
invalid input counts there: a hard error such as running out of memory still
throws and remains pending. Native scalar JSON converters also roll back input
errors before wrapping them in `JsonException`.

PostgreSQL forbids independent rollback inside transaction callbacks and parallel
operations before version 17. Valid input remains usable when the invocation phase
permits backend value calls. Native invalid-input errors propagate as `PgException`
and remain pending until PostgreSQL aborts. Before PostgreSQL 16, `TryParse` cannot return false for an
unrecovered native error, and a JSON converter cannot turn it into a catchable `JsonException`. From
PostgreSQL 16, `TryParse` of the built-in types above still returns false there,
because a soft input error needs no rollback.

Query cancellation, `Fatal` and `Panic` remain pending across managed catches
and subtransaction rollback. Handle them for cleanup, without treating the
current call as successful. Managed static mutations and external side effects
are not undone by a PostgreSQL rollback.

A background worker can recover cancellation after its transaction has rolled
back, or after an idle `Wait` or `PgInterrupts.Check()` outside a transaction has
finished. FATAL and PANIC still end the worker. See
[worker recovery](/background-workers/#database-work-and-errors).
