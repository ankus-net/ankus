---
title: Transaction callbacks
description: Run managed work when PostgreSQL transactions and savepoints change state.
---

`PgTransaction` registers callbacks for the current PostgreSQL transaction. An
implicit transaction ends with the current statement; use `BEGIN` when later
statements must share the registration.

## Recoverable work

Use `PgTransaction.RunInSubtransaction` to group synchronous backend work in a
recovery scope. Success retains its changes in the enclosing transaction. An
exception rolls back the scope before it reaches your `catch` block:

```csharp
try
{
    PgTransaction.RunInSubtransaction(() =>
    {
        Spi.Execute("INSERT INTO audit_log(message) VALUES ('attempt')");
        Spi.Execute("SELECT perform_work()");
    });
}
catch (PgException error)
{
    // Both commands have rolled back. Backend operations are usable again.
    PgLog.Write(PgLogLevel.Notice, error.Message);
}
```

The [try and catch sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.TryCatch)
ports pgrx's `PgTryBuilder` examples to filtered `catch` and `finally` blocks,
using this scope to recover from a PostgreSQL error before returning a fallback.

The generic overload returns the callback's result after successful release.
Nested scopes recover independently: catch an inner scope's exception outside
its callback to continue the outer scope. A managed exception also rolls back
the scope and retains its original exception type and instance.
An `AggregateException` from action and cleanup failures retains its original
causes, including the native failure, after rollback.

By default, each SQL statement inside the scope also recovers individually, so
an inner `catch` can continue the scope. Pass `PgSubtransactionMode.Atomic` to
run the statements directly in the scope's subtransaction instead, as SPI does
in pgrx. A PostgreSQL error then makes later backend calls in the scope rethrow
it, and the whole scope rolls back before the caller sees the error. Many writes
consume one subtransaction ID rather than one each. See
[errors and transactions](/spi/#errors-and-transactions).

Catch raw native and memory-operation errors outside the scope. Catching one
inside the callback does not make the scope successful: additional backend work
is rejected, and the scope rolls back with the original error. Outside an
explicit recovery scope, Ankus retains the error until the managed callback
returns and reports it to PostgreSQL even if user code catches it or throws a
replacement exception. PostgreSQL then aborts the enclosing operation.

Managed `finally` blocks still run. Dispose owned resources and restore selected
memory contexts there; begin new backend work only after rollback has finished.
Put storage that must survive recovery in an owner created outside the scope.

The callback must stay synchronous on the backend thread and must not perform
transaction control. Recovery scopes are unavailable during transaction
callbacks and abort cleanup. Their internal subtransactions do not appear as
consumer subtransaction events. Native results retain their memory-context
lifetimes; rollback invalidates allocations and resources owned by that scope.
PostgreSQL 13–16 also prohibit starting a subtransaction during parallel
execution. See [parallel SQL error handling](/spi/#errors-and-transactions).

## Register a callback

```csharp
_ = PgTransaction.RegisterCallback(PgTransactionEvent.PreCommit, () =>
{
    Spi.Execute("INSERT INTO audit_log(message) VALUES ('committing')");
});

PgTransactionCallback cleanup = PgTransaction.RegisterCallback(
    PgTransactionEvent.Abort,
    static () => PgLog.Write(PgLogLevel.Notice, "Transaction rolled back."));

// Cancel the callback if cleanup is no longer needed.
cleanup.Dispose();
```

Outer-transaction callbacks run once, in registration order. PostgreSQL keeps
the callback alive even when its returned registration is discarded. Dispose
the registration to cancel a callback that has not run.

Preparation ends these registrations too. `PREPARE TRANSACTION` runs
`PrePrepare`, then `Prepare`, and releases unused callbacks and their captured
objects. A later `COMMIT PREPARED` or `ROLLBACK PREPARED` does not invoke the
original backend's callbacks, even when issued by that same backend. Writes made
by `PrePrepare` belong to the prepared transaction and become visible only if it
is committed.

The [notify sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Notify)
ports pgrx's coalesced cache invalidation. A row trigger records changed
categories, a `PreCommit` callback sends one notification per category, an
`Abort` callback discards them, and a `PrePrepare` callback rejects a prepared
transaction whose categories could no longer be sent.

| Event | Timing | SPI |
|---|---|---|
| `PreCommit` | Before commit | Yes |
| `Commit` | After commit | No |
| `Abort` | After rollback | No |
| `PrePrepare` | Before `PREPARE TRANSACTION` | Yes |
| `Prepare` | After preparation | No |
| `ParallelPreCommit` | Before a parallel worker commits | No |
| `ParallelCommit` | After a parallel worker commits | No |
| `ParallelAbort` | After a parallel worker rolls back | No |

`PreCommit`, `PrePrepare` and `ParallelPreCommit` may reject the operation by
throwing. PostgreSQL fires deferred triggers before these callbacks run. Ankus
fires the deferred triggers and constraint checks that callback SQL queues, such
as a deferred foreign key, before PostgreSQL continues, so they can still reject
the commit. Events after
commit, rollback, or preparation are for cleanup and logging. An unhandled
exception in those events causes PostgreSQL to disconnect all sessions and run
crash recovery, as with pgrx. Committed changes remain committed; a transaction
that reached `Prepare` remains prepared and can still be committed or rolled
back after recovery. Use a reversible phase to reject work safely; handle
expected failures inside cleanup callbacks.

Registrations made inside a parallel worker belong to that worker's transaction.
Successful completion runs `ParallelPreCommit` and `ParallelCommit`; failure
runs `ParallelAbort`. A `ParallelPreCommit` error fails the leader's query.
The leader's registrations remain separate, and SQL is unavailable in all three
parallel callback phases. After the failed query is rolled back, another query
can launch fresh workers.

## Savepoints and subtransactions

Subtransaction callbacks repeat for every matching savepoint or internal
subtransaction in the current outer transaction:

```csharp
PgSubtransactionCallback registration = PgTransaction.RegisterSubtransactionCallback(
    PgSubtransactionEvent.Start,
    static (id, parentId) =>
        PgLog.Write(PgLogLevel.Debug1, $"Started subtransaction {id} under {parentId}."));
```

| Event | Timing | SPI |
|---|---|---|
| `Start` | After the subtransaction starts | Yes, without opening a subtransaction |
| `PreCommit` | Before it commits | Yes, without opening a subtransaction |
| `Commit` | After it commits | No |
| `Abort` | After it rolls back | No |

The callback receives `PgSubtransactionId` values for the current and parent IDs.
Dispose its registration to stop future calls. Subtransaction IDs are
backend-local counters, separate from transaction IDs. Subtransactions that Ankus opens
for its private error guards do not appear as events, including those opened by
another Ankus extension in the same session. Savepoints and PL/pgSQL `EXCEPTION`
blocks inside SQL that a guard runs do appear.

SQL in `Start` and `PreCommit` callbacks must not open another subtransaction,
for example through a PL/pgSQL `EXCEPTION` block. PostgreSQL cannot start one
while a savepoint is starting or being released, and it terminates the session
with a FATAL error if SQL tries.

An exception in a savepoint `Commit` callback becomes a PostgreSQL error, and
PostgreSQL aborts the enclosing transaction. An exception in a savepoint `Abort`
callback is reported as a warning with its SQLSTATE and message, because the
rollback must finish; raising an error there would repeat the same rollback.

## Errors

An uncaught managed exception in a reversible phase becomes a PostgreSQL error
after managed `finally` blocks finish. A PostgreSQL error raised by SPI also aborts
that phase, even when callback code catches the managed `PgException`; continuing
would leave PostgreSQL in a failed transaction state. Nested callback dispatch is
supported when `PreCommit` or `PrePrepare` SQL creates another subtransaction.
