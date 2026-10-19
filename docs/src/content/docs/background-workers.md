---
title: Background workers
description: Run PostgreSQL background-worker processes with managed entries, transactions and shared state.
---

A background worker is a PostgreSQL process with its own managed state. Declare
its entry with `[PgBackgroundWorker]` on an accessible synchronous static method
returning `void` and accepting one `nuint` argument:

```csharp
public static class Workers
{
    [PgBackgroundWorker]
    public static void Run(nuint argument)
    {
        PgBackgroundWorker.Connect("postgres");
        while (PgBackgroundWorker.Wait(TimeSpan.FromSeconds(10)))
        {
            if ((PgBackgroundWorker.ConsumeSignals(PgBackgroundWorkerSignals.Reload)
                & PgBackgroundWorkerSignals.Reload) != 0)
            {
                PgBackgroundWorker.ReloadConfiguration();
            }

            PgBackgroundWorker.RunTransaction(() =>
            {
                long count = Spi.ExecuteScalar<long>("SELECT count(*) FROM pg_database");
                PgLog.Write(PgLogLevel.Log, $"Worker {argument}: {count} databases");
            });
        }
    }
}
```

The native symbol defaults to the method name. Set `EntryPoint` on the attribute
to choose another symbol. The generator rejects invalid signatures, conflicting
SQL attributes and duplicate worker symbols. Worker entries do not create SQL
functions. A library can declare worker entries without a `[PgModuleLoad]` or
`[PgInitialize]` method.

Start with a complete worker project and tests:

```console
ankus new MyExtension --background-worker
cd MyExtension
dotnet test
```

The generated backend fixture preloads the extension and verifies a worker's
committed query result from another PostgreSQL process. The
[database observer sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.BackgroundWorkers)
also publishes shared counters for SQL sessions.

## Declaration diagnostics

A worker entry must be an accessible, implemented static method that completes
synchronously and returns `void`. Its single by-value `nuint` argument preserves
PostgreSQL's native `Datum` width; `System.UIntPtr` is the same managed type.
Declare entries in accessible, non-generic types. Local functions, lambdas and
explicit interface implementations cannot supply the native worker entry.

The generator points at the specific declaration, argument, modifier or attribute
that must change. Fix that contract; valid workers and unrelated SQL declarations
continue to generate their own output.

| Diagnostic | Required correction |
| --- | --- |
| ANKUS250 | Use an ordinary named method for the entry. |
| ANKUS251 | Make the entry static. |
| ANKUS252 | Remove `async`; keep backend work on the worker thread. |
| ANKUS253 | Remove method type parameters. |
| ANKUS254–256 | Provide a concrete, non-virtual managed implementation. |
| ANKUS257 | Return `void`. |
| ANKUS258 | Declare exactly one argument. |
| ANKUS259 | Pass the argument by value, without `ref`, `in` or `out`. |
| ANKUS260 | Use `nuint` or `System.UIntPtr` for the argument. |
| ANKUS261 | Use public, internal or protected internal accessibility. |
| ANKUS262 | Implement the partial method. |
| ANKUS263–265 | Use accessible enclosing types without generic parameters or `file` locality. |
| ANKUS266–267 | Remove `Conditional` or `UnmanagedCallersOnly` so generated managed code can invoke the entry. |
| ANKUS268–269 | Put initialization and SQL callback roles on separate methods. |
| ANKUS270–271 | Remove SQL result or argument metadata; a worker has a native argument and no SQL result. |
| ANKUS272 | Start the export name with an ASCII letter; use only ASCII letters, digits and underscores. |
| ANKUS273 | Limit the export to 95 ASCII bytes. |
| ANKUS274 | Avoid C keywords, PostgreSQL loader symbols, and the `ankus_` or `pg_finfo_` prefixes. |
| ANKUS275 | Give every worker a distinct method name or explicit `EntryPoint`. |

Editors offer **Make static** for ANKUS251, **Make synchronous** for ANKUS252,
**Make internal** for ANKUS261, ANKUS264 and ANKUS265, **Remove** for ANKUS266,
ANKUS267, ANKUS270 and ANKUS271, **Add the parameter** `nuint argument` for
ANKUS258, **Change the type** to `nuint` for ANKUS260, and **Pass by value**
for an `in` or `ref readonly` argument (ANKUS259).

## Register during preload

Register a static worker from `[PgModuleLoad]`:

```csharp
[PgModuleLoad]
public static void Register()
{
    PgBackgroundWorker.Register(new("Database observer", "MyExtension", nameof(Workers.Run))
    {
        DatabaseAccess = true,
        Argument = 42,
        RestartDelay = TimeSpan.FromSeconds(5),
    });
}
```

Here `MyExtension` is the published library name. Add it to PostgreSQL's
configuration and restart the server:

```ini
shared_preload_libraries = 'MyExtension'
```

Static registration requires shared preload. Windows backend replay attaches
the extension's state without registering another static worker. Set
`max_worker_processes` high enough for your extensions and PostgreSQL's other
workers.

`DatabaseAccess` permits database initialization. `StartTime` defaults to
`RecoveryFinished` for a database worker and to `PostmasterStart` otherwise;
`ConsistentState` is also available. Registering a database worker that starts at
`PostmasterStart` throws `ArgumentException`.

`RestartDelay` accepts whole seconds. `null` disables restart, zero requests an
immediate restart after failure, and a positive value delays it. Successful
return from the entry does not restart the worker. Explicit termination also
prevents subsequent restarts.

## Start dynamically

A backend callback can register a worker at runtime, after shared preload has
completed:

```csharp
var options = new PgBackgroundWorkerOptions("Database observer", "MyExtension", nameof(Workers.Run))
{
    DatabaseAccess = true,
    Argument = 42,
    NotifyProcessId = Environment.ProcessId,
};
if (!PgBackgroundWorker.TryStart(options, out PgBackgroundWorkerHandle? worker))
{
    throw new InvalidOperationException("No PostgreSQL worker slot is available.");
}

using (worker)
{
    PgBackgroundWorkerState startup = worker.WaitForStartup();
    if (startup.Status == PgBackgroundWorkerStatus.Started)
    {
        PgLog.Write(PgLogLevel.Notice, $"Worker PID: {startup.ProcessId}");
    }
}
```

`TryStart` returning true means registration succeeded. Startup may still fail,
for example when the library or entry cannot be loaded. `GetState` polls the
registration. `WaitForStartup` waits for a startup outcome; a `Started` snapshot
contains a process ID, while the other states have no process ID. After a
restart, query the handle again to obtain the new process ID.

PostgreSQL registers a dynamic worker before allocating its observation handle.
If that allocation fails, `TryStart` throws a `PgException`, but the registered
worker can still start. Ankus releases its local observation storage; it cannot
undo that registration without a handle. Check your worker's shared state or
other application-level completion signal before retrying if duplicate work
would be a problem.

Set `NotifyProcessId` to the registering backend's PID to use startup and
shutdown waits. They return `Untracked` if that backend is not the notifier.
Polling and termination remain available without notifications. Static workers
require a notification PID of zero.

The handle belongs to the current backend callback and thread. Disposing it,
or returning from the callback, releases observation storage; the worker keeps
running. Call `Terminate()` to request shutdown, then `WaitForShutdown()` to
observe it before disposing the handle. Keep the handle within its originating
callback; it cannot be retained for a later SQL invocation.

## Database work and errors

Call `Connect` once in a database-enabled worker, using database and optional
role names or their OIDs. A null database name or zero database OID requests no
selected database. A null role name or zero role OID selects PostgreSQL's
bootstrap superuser. Choose an explicit role when the worker should use that
role's database privileges. The role's login permission, database `CONNECT`
permission and table privileges apply. An empty name requests a database whose
name is empty. The same null-versus-empty distinction applies to role names.
PostgreSQL can terminate the worker if connection initialization fails, including
a missing database or role, a role without login permission, or denied database
access. Observe the stopped worker and server diagnostic, then start another
worker to retry.

Ankus checks worker phase and connection state before calling PostgreSQL.
Rejected attempts such as connecting twice do not change native error state.
An actual PostgreSQL failure still follows the worker's transaction or terminal
error behavior.

Use `RunTransaction` for synchronous database work after connecting. The callback
can use `Spi` and return an owned managed result. Commit completes before the
method returns. A managed exception aborts the transaction and is rethrown with
its identity preserved. PostgreSQL errors, including commit failures, become
owned `PgException` diagnostics after native cleanup. The worker may then start
another transaction.

After each transaction the worker publishes its table and I/O counts, as
PostgreSQL's own example worker does, and `Wait` flushes any that PostgreSQL's
rate limit held back before it sleeps. Autovacuum and `pg_stat_user_tables` see
a running worker's writes, and `pg_stat_activity` shows it as `active` during a
transaction and `idle` between them.

Query cancellation raises `PgQueryCanceledException`. Catching it inside the
transaction callback permits managed cleanup, but the transaction still aborts.
Catch it around `RunTransaction` to continue the worker after rollback and start
another transaction. FATAL and PANIC reports still terminate the worker, even
when managed code catches the exception.

Cancellation during `Wait` or `PgInterrupts.Check()` outside a transaction also
raises `PgQueryCanceledException`. Catch it around that operation to continue:
no transaction is active and the completed interrupt check has no transaction
resources to roll back. A subsequent interrupt check, wait, log call or
`RunTransaction` remains usable. Inside a transaction, both operations keep the
transaction cancellation rules above. FATAL and PANIC still terminate the worker.

Worker transactions cannot nest. Do not retain transaction-owned native views
or return asynchronous work from a transaction callback. Keep PostgreSQL calls
on the worker's owning thread; a task or timer callback is not a backend entry.

A transaction lasts as long as its callback, which may loop, wait on the latch
and receive from a [replication connection](/replication-connections/) many
times. An apply worker therefore runs each remote transaction in one callback,
from its begin message to its commit, where a C extension would start a
transaction in one handler and commit in another. See
[applying replicated changes](/applying-changes/).

### Cleanup at exit

A worker that claims a slot in shared memory must give it back however it
stops. `PgProcess.OnExit` registers a callback that runs when the process exits,
before it detaches from shared memory, as a C extension's `before_shmem_exit`
callback does:

```csharp
int slot = ClaimSlot();
PgProcess.OnExit(code => ReleaseSlot(slot));
```

Callbacks run newest first after a normal exit, a FATAL error or a stop request,
with the exit code; they do not run when the process crashes. They can use
shared memory and lightweight locks but not SQL or logging, and must not wait
for a lock the process may already hold. An exception is reported as a warning
and the other callbacks still run. Register each callback from the process that
needs it, after the library has loaded there. Disposing the returned
registration removes the callback.

C's `PG_ENSURE_ERROR_CLEANUP` exists because a FATAL error exits without
unwinding the C stack. Ankus unwinds managed code before it reports a FATAL that
a backend call raised, so an ordinary `try`/`finally` covers that case; use
`OnExit` for cleanup that must also run when the process exits from inside
PostgreSQL, such as on postmaster death during a wait.

### Running programs

A logical replication extension copies a schema by running `pg_dump` and
`pg_restore`. `PgChildProcess.Run` starts a program and waits for its exit code:

```csharp
string pgDump = Path.Combine(binDirectory, OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump");
int code = PgChildProcess.Run(pgDump, "--schema-only", "--file=" + schemaFile, "--dbname=" + provider);
if (code != 0)
{
    throw new PgException(PgSqlStates.ExternalRoutineException, $"pg_dump exited with code {code}.");
}
```

Do not use `System.Diagnostics.Process` in PostgreSQL: it installs its own
`SIGCHLD` handling. `Run` starts the program with `posix_spawn` on Unix and
`CreateProcessW` on Windows and passes each argument exactly, including spaces,
quotes and non-ASCII text. On Windows, PostgreSQL's programs and others with a
narrow `main` receive their arguments in the system's ANSI code page, so
non-ASCII text survives only if that code page contains it. It waits on the latch, so a query cancellation,
statement timeout or termination stops the program before the usual error. The
program inherits the server's environment, working directory and output streams,
so its messages go to the server log; give it a file to write results to. On
Unix, a program ended by a signal returns 128 plus the signal number.

## Signals and shared values

Generated entries install native reload and termination handlers before calling
managed code. `Wait` sleeps on PostgreSQL's latch, wakes for a signal or timeout,
and returns false for termination or postmaster death. Its optional timeout
accepts whole milliseconds; null waits indefinitely and zero polls immediately.
It consumes the termination observation. The shutdown request remains pending:
`Wait` and `CanContinue` keep returning false after termination, even if
`ConsumeSignals` has already cleared that observation. `CanContinue` also checks
postmaster liveness without waiting.

`ConsumeSignals` returns and clears only the selected observations. To observe
interrupt or child signals, first add them with `AttachSignalHandlers`. Signal
handlers record flags and wake the latch; managed code processes them after
waking. These observations are flags: multiple deliveries before consumption
can coalesce. Consuming one selection leaves other pending observations intact;
consuming the same selection again returns no flags unless another signal arrived.
Termination that arrives while `RunTransaction` is executing also ends the
running statement, as PostgreSQL's default handler does for any backend: the
transaction rolls back and the worker exits with FATAL `57P01`, so
`pg_terminate_backend` and fast shutdown do not wait for a long query. Between
transactions, termination is only an observation: `Wait` returns false, and a
final cleanup transaction started after that still runs.
Attaching `Interrupt` makes SIGINT only an observation, and PostgreSQL does not
cancel the query. Otherwise a `DatabaseAccess` worker keeps PostgreSQL's handler, so
SIGINT, including `pg_cancel_backend`, cancels its wait or active transaction;
a worker without database access ignores SIGINT.
The reload and termination handlers also set PostgreSQL's corresponding native
flags, so backend helpers see the same requests. Reloading configuration is
explicit through `ReloadConfiguration()`, which clears the native reload flag
before processing the configuration. A signal received during reload remains
pending for another pass.

`Name`, `Type` and `Extra` return owned strings from the current registration.
`Type` defaults to `Name`. Registration text uses strict UTF-8 and must fit the
selected PostgreSQL version's fixed fields, including its terminating zero.
Oversized text and embedded zero characters are rejected without truncation.
PostgreSQL sanitizes dynamic-worker names, types, library paths and entry names.
`TryStart` rejects characters it would replace: these four fields accept ASCII
characters from space through DEL, plus tab, carriage return and newline.
The extra payload preserves UTF-8, including non-ASCII characters. Static
registration retains its original UTF-8 metadata.

`Argument` preserves one native-sized unsigned value. Pass an identifier or
other by-value data. Do not pass a process-local address. Managed static fields
are independent in each worker. For values shared with other processes, register
`PgAtomic<T>`, `PgShared<T>` or `PgLwLock<T>` descriptors during preload; see
[shared memory, locks and atomics](/shared-memory/).
