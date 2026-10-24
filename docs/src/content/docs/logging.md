---
title: Logging and errors
description: Report PostgreSQL notices, structured diagnostics, and errors from C#.
---

`PgLog` sends messages through PostgreSQL's reporting system:

```csharp
PgLog.Notice("Refresh complete.");
```

Calls require the active PostgreSQL backend thread. `Console.WriteLine` does not
produce a PostgreSQL notice.

Configuration check, assign, and show hooks can log during reload, rollback, and
client parameter reporting, including outside transactions. Their logging binding
does not grant SQL access. See [configuration hooks](/configuration/#hooks) for
phase restrictions and the failure policy for an unhandled `Error`.

## Levels and filtering

`Debug5` through `Debug1` provide decreasing detail. `Log` reports operational
messages, and `ServerOnly` keeps them out of the client connection. `Info` always
reaches clients. `Notice` and `Warning` report events without stopping execution.

Each level has a named helper with literal-text and `PgDiagnostic` overloads:
`PgLog.Debug5` through `Debug1`, `Log`, `ServerOnly`, `Info`, `Notice`, `Warning`,
`Error`, `Fatal` and `Panic`. Use `PgLog.Write` when the severity is selected at
runtime. The helpers retain the same filtering, SQLSTATE and diagnostic fields.

Nonterminal message emission temporarily holds PostgreSQL interrupts. Pending
cancellation is processed at a later interrupt check, after the report finishes.
Call `PgInterrupts.Check()` periodically in loops that only log messages. Native
reporting errors, such as an encoding failure, still follow normal error recovery.
A report below `Error` needs no subtransaction unless an `emit_log_hook` is
installed, including your own. With a hook, each report runs in one, so a failure
inside the hook rolls back whatever the hook had done.

Messages convert exactly to the database encoding, including outside a
transaction and before a background worker's first transaction. In a database that cannot represent a character, such as `€` in
LATIN1, an ordinary report fails with SQLSTATE `22P05`. `Fatal` and `Panic`
reports never fail that way: the backend still terminates at the requested level,
and each byte it cannot convert appears as a `\xNN` escape. The same rule
applies when PostgreSQL diagnostics are copied into a `PgException`. Text that
is not valid UTF-8 in a SQL_ASCII database, for example, keeps its ASCII
characters and escapes the other bytes. PostgreSQL 13 cannot convert without
raising, so outside a transaction it escapes every non-ASCII byte in those
diagnostics.

PostgreSQL applies `client_min_messages` and `log_min_messages` separately. In the
server log, `Log` ranks above `Error`; it is not an ordinary numeric threshold.

`PgLog.Write` formats an interpolated message only when PostgreSQL would report
its level, as pgrx's logging macros do. At PostgreSQL's default thresholds the
call below evaluates nothing, so `Summarize` never runs:

```csharp
PgLog.Write(PgLogLevel.Debug1, $"Prepared {items.Count} items: {Summarize(items)}.");
```

The named helpers such as `PgLog.Debug1` take finished text. Check the current
settings before building a message for them, or for a `PgDiagnostic`:

```csharp
if (PgLog.IsEnabled(PgLogLevel.Debug1))
{
    PgLog.Debug1(new PgDiagnostic($"Prepared {items.Count} items.") { Detail = Describe(items) });
}
```

Messages are literal text. Percent signs have no special meaning.

## Structured messages

```csharp
PgLog.Warning(new PgDiagnostic("Entry has expired.")
{
    SqlState = PgSqlStates.Warning,
    Detail = "The entry was last refreshed yesterday.",
    Hint = "Refresh it before the next query.",
    TableName = "cached_entries",
});
```

`PgDiagnostic` also accepts context, object names, query positions, and source
locations. `DetailLog` replaces `Detail` in the server log and is never sent to
the client. Messages retain their full text across the native boundary.

`Domain` sets the message domain, as C's `ereport_domain` and pgrx's
`ereport_domain!` do. PostgreSQL uses it to look up translations and records it
with the error, so code that catches the error reads it from
`PgException.Domain`. Errors that PostgreSQL itself raises carry its own
`postgres-` domain, such as `postgres-18`.

Without an explicit SQLSTATE, warnings use `01000`, errors use `XX000`, and lower
levels use `00000`. An explicit code has five uppercase ASCII letters or digits;
error levels cannot use `00000`.

## Errors

`PgLog.Error(...)` throws `PgException`. Extension code can catch
it and continue. An unhandled exception becomes PostgreSQL `ERROR` after the
managed method and its `finally` blocks have finished.

`Error`, `Fatal` and `Panic` are marked as never returning normally, including
when a report fails validation. `PgLog.Write(PgLogLevel.Error, ...)` remains
available for code that chooses the severity at runtime.

Client drivers can apply their own connection policy. For example,
[Npgsql closes connections for SQLSTATE class XX](https://github.com/npgsql/npgsql/blob/v10.0.3/src/Npgsql/PostgresErrorCodes.cs#L432),
including the default `XX000`, even when PostgreSQL can recover from the `ERROR`.
For expected application errors, supply a condition-specific SQLSTATE such as
`PgSqlStates.InvalidParameterValue`.

Throwing `PgException` directly is also supported:

```csharp
throw new PgException(PgSqlStates.InvalidParameterValue, "Count must be positive.", hint: "Pass a count greater than zero.");
```

`PgSqlStates` provides named strings for every SQLSTATE in the PostgreSQL 13–18
and 19 RC1 source catalogs, including native aliases and retired names. Use
them directly with `PgException`, `PgDiagnostic.SqlState`, and exception filters:

```csharp
catch (PgException error) when (error.SqlState == PgSqlStates.UniqueViolation)
{
    PgLog.Write(PgLogLevel.Notice, "The entry already exists.");
}
```

Names distinguish conditions that share wording across classes, such as
`WarningStringDataRightTruncation` (`01004`) and `StringDataRightTruncation`
(`22001`). A constant does not imply that its server feature exists in every
version: for example, `TransactionTimeout` was added in PostgreSQL 17, while
`SnapshotTooOld` belongs to PostgreSQL 13–16. The API reference records version
differences. Custom codes remain ordinary strings and retain their exact value;
they do not need registration or a catalog entry.

### Error context

A C extension describes what it was doing when something failed with an error
context callback, such as which remote transaction an apply worker was applying.
`PgErrorContext` adds such a line:

```csharp
using PgErrorContext context = PgErrorContext.Push("applying remote transaction");
foreach (Change change in changes)
{
    context.Text = $"applying {change.Kind} on {change.Table} in remote transaction {change.Xid}";
    Apply(change);
}
```

While it is active, PostgreSQL adds the text to the context of every error,
warning and log message that a backend call reports, after any inner lines, and
a managed exception thrown in the meantime is reported with it too. Contexts
nest, innermost first, and each appears once when the error comes from a C#
callback that a backend call ran, such as a trigger fired by an SPI insert.
Change `Text` as the work moves on: PostgreSQL never calls back into .NET to
build the line, so the context costs nothing until something is reported.
Disposing removes the context; a function's leftover contexts are removed when
it returns, and a background worker's last until it disposes them or exits.
pgrx has no wrapper; a pgrx extension links an `ErrorContextCallback` into
`pg_sys::error_context_stack` itself.

PostgreSQL query cancellation throws `PgQueryCanceledException`, which derives
from `OperationCanceledException`. Its `Diagnostic` property retains SQLSTATE
`57014` and the original PostgreSQL error fields. Catching it allows managed
cleanup, but the query still fails at the native boundary. Further server work
is rejected until that boundary returns. This also applies inside
`PgTransaction.RunInSubtransaction` and recursive SQL calls.

Background workers have their own recovery boundaries. A canceled worker
transaction must roll back before continuing. Outside a transaction, a worker
can catch cancellation from `Wait` or `PgInterrupts.Check()` and continue after
that operation finishes. See [worker cancellation](/background-workers/#database-work-and-errors).

For long loops that stay in managed code, call `PgInterrupts.Check()` periodically:

```csharp
for (int index = 0; index < values.Length; index++)
{
    PgInterrupts.Check();
    Process(values[index]);
}
```

The check processes query cancellation, statement timeouts and backend shutdown
through PostgreSQL's native interrupt handler. It also dispatches queued signals
on Windows. With no pending work it reads native flags without a native call.
PostgreSQL interrupt holdoffs still apply. A retained cancellation remains pending
on subsequent checks even after a managed catch.

Call it on the active backend thread. A task or thread-pool continuation has no
backend capability. Release spinlocks and leave `Read` and `Mutate` callbacks
before polling; a lightweight-lock guard defers cancellation until its release.

`PgInterrupts.IsPending` reports whether an interrupt is waiting without processing
it, as pgrx's `interrupt_pending()` does. Reading it never enters PostgreSQL, so it
is allowed while a spinlock or scoped shared-memory borrow is held. Use it to leave a
critical section early, then call `Check()` to process the interrupt.

`Fatal` and `Panic` also unwind managed code before reporting at the native
boundary. Their severity and diagnostics remain pending even if a
`catch (Exception)` block swallows the managed exception or a subtransaction
rolls back. `Fatal` ends the backend connection.
`Panic` aborts the backend and causes PostgreSQL to terminate peer backends and
perform crash recovery.

The [errors sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Errors)
provides complete runnable functions for managed failures, guarded native errors,
INFO, WARNING, ERROR, FATAL, and PANIC. The
[bad ideas sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.BadIdeas)
shows `catch` blocks that swallow an ERROR but cannot discard FATAL or PANIC.
