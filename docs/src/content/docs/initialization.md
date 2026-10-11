---
title: Extension initialization
description: Initialize managed extension state when PostgreSQL loads the library.
---

Use `[PgInitialize]` on one accessible, synchronous, non-generic, parameterless
static `void` method per extension assembly:

```csharp
public static class Startup
{
    [PgInitialize]
    public static void Initialize()
    {
        PgLog.Write(PgLogLevel.Notice, "Extension initialized.");
    }
}
```

Ankus generates PostgreSQL's `_PG_init` entry point. PostgreSQL calls it before
the first extension function. A library with only an initializer can load with:

```sql
LOAD 'Ankus.Examples.Initialization';
```

Installing an extension does not load an initializer-only library.

## Native hook and provider registration

Use `[PgModuleLoad]` when native registration must happen as soon as PostgreSQL
loads the library, before a parallel worker's first executor entry. It uses the
same method requirements as `[PgInitialize]`. An assembly may declare one method
for each phase; a single method cannot declare both attributes.

```csharp
public static class Startup
{
    [PgModuleLoad]
    public static void Register()
    {
        PgLog.Write(PgLogLevel.Notice, "Extension module loaded.");
    }

    [PgInitialize]
    public static void Initialize()
    {
        PgLog.Write(PgLogLevel.Notice, "Extension initialized.");
    }
}
```

For native registration, see the complete
[executor hook example](/raw-values/#managed-native-callbacks-and-hooks).
Ankus registers configuration settings before calling `PgModuleLoad`, then calls
`PgInitialize`. A parallel worker loads libraries before it restores the
leader's settings, and `PgModuleLoad` runs during that load. On PostgreSQL
13–17, where the worker restores libraries and settings together before starting
its parallel transaction, `PgInitialize` waits until restoration finishes. On 18
and later it runs during the load, before the leader's settings are restored, as
pgrx's `_PG_init` does on every version. SQL is unavailable during early
registration, including in nested native callbacks. To see the leader's session
settings on every version, read them when a function runs rather than during
initialization.
On PostgreSQL 13–16, worker SQL follows the parallel error boundary described in
[SPI queries](/spi/#errors-and-transactions).

Each successful phase runs once. If `PgModuleLoad` fails, the next load retries it.
A callback that the failed attempt already installed also retries registration
before its handler runs. If `PgInitialize` fails after successful registration,
registration remains in place and only initialization retries. Installed native
hooks and managed state are not automatically rolled back; registration code owns
their cleanup. Record each hook installation as soon as it succeeds, so a retry
does not install the same hook again and save it as its own previous hook. See
[idempotent hook installation](/raw-values/#managed-native-callbacks-and-hooks)
and the [hooks sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.Hooks).

## Database access and failure

`Spi` works when PostgreSQL loads the library inside a transaction. It is not
available during postmaster startup. Transaction-dependent APIs throw
`InvalidOperationException` when no transaction exists. Logging and configuration
reads remain available.

Managed exceptions unwind `finally` blocks before Ankus raises a PostgreSQL
error. `PgException` preserves SQLSTATE, message, detail, and hint. Other managed
exceptions use SQLSTATE `38000`.

Initialization can run again after a failed load. Managed static changes are not
rolled back. SQL changes follow the surrounding transaction or savepoint. Loading
the same library recursively raises SQLSTATE `55000`.

## Preloading

Use `session_preload_libraries` to initialize separately in each backend:

```ini
session_preload_libraries = 'Ankus.Examples.Initialization'
```

PostgreSQL 15 and later load session libraries inside the startup transaction,
so an initializer can use `Spi`. PostgreSQL 13 and 14 load them after that
transaction ends: configuration reads, logging and memory operations remain
available, but SQL is rejected. Perform database work from a later function call
on those versions.

Use `shared_preload_libraries` to initialize when the server starts:

```ini
shared_preload_libraries = 'Ankus.Examples.Initialization'
```

On Linux and macOS, the postmaster initializes once and each forked backend
receives its own copy of the initialized managed state. Windows starts each backend
as a new process, so every backend loads the library and runs `PgModuleLoad` and
`PgInitialize` again; shared-memory descriptors attach to the existing values.
Changes made in one backend do not change another backend or the postmaster. Tasks, timers,
garbage collection, finalizers, and managed configuration hooks continue to work
after startup. Extension projects use this automatically; no host setup is needed.

On Linux and macOS, the postmaster retires runtime service threads between managed
callbacks so PostgreSQL can fork safely. Complete explicitly created threads before
returning from a preload callback. Timers and queued work resume when the runtime
reenters or a backend starts; a continuously running postmaster thread does not fit
PostgreSQL's Unix process model.

To share values across backends, register static `PgShared<T>`, `PgLwLock<T>` or `PgAtomic<T>` descriptors from
`PgModuleLoad` using `PgSharedMemory.Initialize`. See
[shared memory, locks and atomics](/shared-memory/) for storage and guard lifetimes.

To register a PostgreSQL process that runs a managed entry, use
`PgBackgroundWorker.Register` during shared preload. See
[background workers](/background-workers/) for registration, signals and database transactions.

## Declaration diagnostics

Each initialization error identifies the declaration requirement and highlights
the relevant source. The same rules apply to `[PgInitialize]` and
`[PgModuleLoad]`. A valid callback remains a synchronous, accessible,
nongeneric, parameterless static `void` method with a managed implementation.

| Diagnostic | Correction |
| --- | --- |
| `ANKUS230` | Declare separate methods for the two phases. |
| `ANKUS231` | Use an ordinary named method instead of a local function, lambda or explicit interface implementation. |
| `ANKUS232`, `ANKUS233` | Declare a static callback and complete its work synchronously on the backend thread. |
| `ANKUS234` | Remove callback method type parameters. |
| `ANKUS235`, `ANKUS236`, `ANKUS237` | Provide a concrete, nonvirtual managed implementation; callbacks cannot be abstract or extern. |
| `ANKUS238`, `ANKUS239` | Return `void` and remove callback parameters. |
| `ANKUS240`, `ANKUS244` | Make the method and enclosing types accessible to generated code: public, internal, or protected internal. |
| `ANKUS241` | Implement the partial method definition. |
| `ANKUS242`, `ANKUS243` | Use nongeneric enclosing types that are not file-local. |
| `ANKUS245`, `ANKUS246` | Remove `Conditional` or `UnmanagedCallersOnly`; the callback must be callable unconditionally from managed code. |
| `ANKUS247`, `ANKUS248` | Declare separate SQL exports and remove SQL result metadata from initialization. |
| `ANKUS249` | Keep one callback per phase in the extension assembly. |

Editors offer **Make static** for `ANKUS232`, **Make synchronous** for
`ANKUS233`, **Make internal** for `ANKUS240`, `ANKUS243` and `ANKUS244`, and
**Remove** for `ANKUS245`, `ANKUS246` and `ANKUS248`.

Invalid initialization declarations do not remove unrelated valid SQL
dispatchers. A rejected phase does not receive a PostgreSQL loader export.
