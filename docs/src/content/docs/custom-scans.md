---
title: Custom scan providers
description: Write custom scans as managed providers or with PostgreSQL's native method tables.
---

A custom scan provider participates in PostgreSQL's planner and executor. There
are two ways to write one:

- A managed provider implements `IPgCustomScan<TState>` and produces rows for a
  table, optionally in order, backward, from a marked position or in parallel.
  Ankus generates and registers everything PostgreSQL needs.
- A native provider uses the generated `Ankus.Postgres` declarations and
  `[PgNativeCallback]` for PostgreSQL's method tables. It can also wrap
  PostgreSQL's own child plans and reparameterize them. Its fields and callback
  signatures come from the server headers selected when the extension is built.

## Managed providers

Mark a class with `[PgCustomScan]` and implement `IPgCustomScan<TState>`:

```csharp
[PgCustomScan]
public sealed class NumberScan : IPgCustomScan<NumberScan.State>
{
    public static void Plan(PgCustomScanPlanner planner)
    {
        if (planner.RelationName == "numbers")
        {
            planner.AddPath(new PgCustomScanPath(StartupCost: 0, TotalCost: 1) { Rows = 100 });
        }
    }

    public static State Begin(PgCustomScanContext context) => new();

    public static bool Read(State state, PgHeapTuple row)
    {
        if (state.Next > 100)
        {
            return false;
        }

        row.Set(0, state.Next++);
        return true;
    }

    public sealed class State
    {
        public long Next { get; set; } = 1;
    }
}
```

When the library loads, Ankus registers the provider with PostgreSQL and adds a
relation path hook after any existing one. The calls happen in this order:

1. `Plan` runs when the planner considers a scan of a plain table or
   materialized view. `AddPath` offers a path with its costs, an optional row
   estimate and optional bytes. `Descriptor` and `IsColumnRead` let a provider
   price a path by the columns the query reads. PostgreSQL keeps the path only if it is cheaper
   than its own scans. The bytes are stored in the plan, so prepared statements
   keep them.
2. `Begin` runs when the executor starts the scan. The context has the table's
   descriptor and the path's bytes. `IsExplainOnly` is true for EXPLAIN without
   ANALYZE, which starts the scan but never reads it. `IsColumnRead` tells
   whether PostgreSQL reads a column, for the query's output or its
   conditions; a provider can leave the other columns unset and skip producing
   them.
3. `Read` fills one row and returns `true`, or returns `false` when the scan has
   no more rows. Columns left unset are SQL NULL. PostgreSQL applies the query's
   conditions and projection to every row, so a provider can return more rows
   than the query needs. Look up column ordinals in `Begin` and keep them in
   the state.
4. When the scan ends, Ankus disposes a state that implements `IDisposable`.
   This also happens after an early stop such as `LIMIT`, a closed cursor or an
   error.

PostgreSQL restarts a scan on the inner side of a nested loop or in a subquery
that depends on an outer row. Implement `IPgRescannableCustomScan<TState>` to
restart in place. Otherwise Ankus disposes the state and calls `Begin` again.
Implement `IPgExplainedCustomScan<TState>` to add EXPLAIN properties with
`PgExplainWriter`. EXPLAIN shows the provider's name as the custom plan
provider.

### Ordered rows, cursors and merge joins

A path whose rows come in order says so with `SortKeys`:

```csharp
planner.AddPath(new PgCustomScanPath(0, 1)
{
    SortKeys = [new(planner.Descriptor.GetOrdinal("value"))],
});
```

Each key names a column, ascending unless `Descending` is set, with NULL last
when ascending and first when descending unless `NullsFirst` says otherwise.
Values compare as `ORDER BY` on the column compares them: by the default B-tree
operator class of the column's type, with the column's collation. PostgreSQL
then skips a sort for a matching `ORDER BY`, stops a `LIMIT` early or merge
joins on the column. It does not check the order, so rows out of order give
wrong results, as a corrupt index would. The planner keeps only the leading keys
the query can use.

Two more capabilities let PostgreSQL reuse rows without storing them:

- `IPgScrollableCustomScan<TState>` adds `ReadBackward`. A cursor over the scan
  can then fetch backward, even without `SCROLL`, and a `SCROLL` cursor needs no
  `Materialize` node. The scan has a current row, like a cursor: `Read` moves to
  the next row and `ReadBackward` to the previous one. A read that finds no row
  leaves the scan before the first row or after the last, so reading the other
  way returns the first or the last row.
- `IPgMarkableCustomScan<TState>` adds `Mark` and `Restore`. A merge join marks
  the inner scan at the first row of a key and restores it for each repeated
  outer key; the first `Read` after `Restore` returns the row the first `Read`
  after `Mark` returned. Without it, PostgreSQL stores the inner rows in a
  `Materialize` node. A merge join needs its inner rows in order, so this pairs
  with `SortKeys`.

An exception from any of these methods becomes a PostgreSQL error, and the
session can run the next statement. A row that leaves a `NOT NULL` column empty
is rejected with an error, unless PostgreSQL does not read that column.

Ankus does not offer a provider's paths in these cases:

- the query reads a system column such as `ctid`, locks rows, or modifies the
  table;
- the scan is of an inheritance parent or uses `TABLESAMPLE`;
- the extension has not finished loading, for example after its load failed.

### Parallel scans

A provider that implements `IPgParallelCustomScan<TState, TShared>` can offer
paths that PostgreSQL's parallel workers and the leader run together. Each
process has its own state and reads part of the rows; they divide the rows
through shared memory laid out as `TShared`:

```csharp
public readonly struct Counter(long claimed)
{
    private readonly PgAtomicValue<long> _claimed = new(claimed);

    public long Claim() => _claimed.Increment();
}

public static void Plan(PgCustomScanPlanner planner)
{
    var path = new PgCustomScanPath(0, 100) { Rows = 100_000 };
    planner.AddPath(path);
    planner.AddParallelPath(path, workers: 2);
}

public static void InitializeShared(State state, ref Counter memory) => memory = new Counter(0);

public static void AttachShared(State state, PgCustomScanSharedMemory<Counter> memory) => state.Memory = memory;

public static bool Read(State state, PgHeapTuple row)
{
    // In parallel each process claims the next integer; alone, the scan reads them all.
    long next = state.Memory is { } memory ? memory.Value.Claim() : ++state.Position;
    if (next > state.Count)
    {
        return false;
    }

    row.Set(0, next);
    return true;
}
```

`AddParallelPath` takes the whole scan's costs and rows; Ankus divides the run
cost and the rows among the processes as PostgreSQL does for a parallel
sequential scan, and caps the workers at `max_parallel_workers_per_gather`.
PostgreSQL considers the path only when the table and query can run in
parallel, which rules out temporary tables, and not for a scan whose conditions
depend on an outer row.

Ankus clears the shared memory and calls `InitializeShared` in the leader
before workers start, and again before a rescan starts new ones. Every process
that reads the scan, the leader included, then gets `AttachShared` before its
first read. Use `PgAtomicValue<T>` fields for values that several processes
update. The handle stops working when the parallel scan shuts down. A provider
that cannot rescan in place gets a new state for each restart, and Ankus gives
it the same shared memory. PostgreSQL can also run a parallel plan without
workers or shared memory; the scan then reads every row itself.

Implementing the interface also declares that the provider's ordinary paths are
safe to run inside a worker, for example on the inner side of a parallel join.
Without it, managed paths are parallel-unsafe. Sort keys on a parallel path
mean that each process returns its own rows in that order, and PostgreSQL
merges them with Gather Merge.

The name defaults to the class name in snake_case. Set `Name` to choose another.
A name is 1 to 63 ASCII letters, digits or underscores and does not start with a
digit. PostgreSQL keeps one registry per backend, so two loaded extensions cannot
use the same name. The library has to be loaded before a query is planned; call
one of its functions first, or add it to `session_preload_libraries`.

| Diagnostic | Meaning |
|---|---|
| ANKUS535 | The class implements `IPgCustomScan<TState>` for no state type or for several |
| ANKUS536 | The name is not a valid custom scan name |
| ANKUS537 | Another provider in the extension uses the same name |
| ANKUS538 | The class or a containing type is generic, file-local, private or protected |
| ANKUS539 | A provider method is `async` |
| ANKUS540 | The class implements `IPgParallelCustomScan` for more than one shared memory type |

The sample's [`NumberScan.cs`](https://github.com/ankus-net/ankus/blob/main/samples/Ankus.Examples.CustomScans/NumberScan.cs)
is a complete provider with path data, ordered rows, rescans, backward reads,
mark and restore, parallel scans, EXPLAIN properties and disposal.

## Native providers

The [trace-scan sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.CustomScans)
wraps sequential, index and index-only scan paths and delegates to their real
child plans. It keeps the original path costs, ordering, qualifications, output
values and parameters, and adds per-node EXPLAIN observations. After installing
the published extension:

```sql
CREATE EXTENSION ankus_trace_scan;
SELECT trace_scan_reset();
SET ankus_trace_scan.enabled = on;
EXPLAIN (ANALYZE, FORMAT JSON) SELECT * FROM your_table;
SELECT trace_scan_counts();
```

Load the library in each session before planning queries. Calling
`trace_scan_reset()` loads it and clears that backend's counters. An existing
cached plan continues to use its registered methods even if tracing is disabled
afterwards. PostgreSQL can still invalidate the plan; replanning uses the current
setting.

## Registering the native contracts

There are three method tables:

| Table | Responsibility |
|---|---|
| `CustomPathMethods` | Convert a selected custom path and its child plans into a `CustomScan` |
| `CustomScanMethods` | Allocate a `CustomScanState` with the correct node tag and executor methods |
| `CustomExecMethods` | Begin, execute, end and rescan; optionally explain, mark/restore or coordinate parallel execution |

Declare a managed handler using the name derived from its native field:

```csharp
[PgNativeCallback(nameof(Execute))]
private static partial CustomExecMethods_ExecCustomScanCallback Executor { get; }
```

The handler must exactly match the generated type's `Invoke` signature. Assign
that property to `CustomExecMethods.ExecCustomScan`. Callback addresses remain
valid for the loaded library's lifetime; copying an address does not extend the
lifetime of any state it uses.

Register `CustomScanMethods` from `[PgModuleLoad]` with
`NativeMethods.RegisterCustomScanMethods`. PostgreSQL retains the supplied
pointer; it does not copy the table or provide an unregister operation. The
table, its name and all method tables referenced by cached plans need storage
that survives individual queries and transactions. The sample allocates them in
a child of `TopMemoryContext`.

Registry keys are case-sensitive. A name must fit the selected headers'
`EXTNODENAME_MAX_LEN`, including its terminating zero byte; PostgreSQL counts
the encoded bytes. Duplicate registration raises a
native error and leaves the original method table installed.
`NativeMethods.GetCustomScanMethods` returns the retained table, returns a null
address for an optional missing lookup, or raises an error when the requested
name is required. These calls use the generated native error guard.

Registration belongs to the backend and outlives the transaction. Rolling back the
registering transaction does not remove its entry. Keep successful registrations
alive until the backend exits, and reclaim attempted storage when registration
fails. A different backend has its own registry and must load the provider too.

Keep previous planner hooks when installing a provider. The trace sample invokes
its predecessor with the original planner, relation, range-table index and entry
before checking its own enabled setting or wrapping paths. Disabling tracing
therefore preserves the other provider's behavior. Errors from that predecessor
propagate through the guarded callback boundary; managed frames unwind before
PostgreSQL receives ERROR, and the hook chain remains available after recovery.

## Planning and execution

The relation path hook runs after PostgreSQL creates its ordinary access paths.
A provider can retain those paths as `CustomPath.custom_paths`. PostgreSQL
converts the children to plans before calling `PlanCustomPath`.

Create an exact `CustomScan` value. Its private plan data must be native nodes
that PostgreSQL can copy and serialize. Put expressions that need planner
reference adjustment in the appropriate expression fields. A
`custom_scan_tlist` describes the tuples the scan will produce; its shape must
agree with the executor's slots and projection.

`CreateCustomScanState` may allocate a larger native value with `CustomScanState`
as its first member. Initially set the node tag and method table and leave the
standard fields zeroed. PostgreSQL initializes those fields before
`BeginCustomScan`. Publish initialized child states in `custom_ps` so ordinary
executor traversal can find them.

The sample calls guarded `NativeMethods.ExecScan` with generated access and
recheck callbacks. The access callback runs the actual child using
`NativeMethods.ExecProcNode`, then copies its tuple into the custom scan's own
slot with `NativeMethods.ExecCopySlot`. This preserves the slot operations
PostgreSQL expects from the custom node, including fully deformed virtual
tuples. PostgreSQL applies scan projection and preserves SQL NULL and the native
tuple representation. Rescan propagates changed
parameters to the child; end invokes its ordinary executor cleanup.

## Ownership, errors and optional capabilities

Native state belongs to the relevant PostgreSQL memory context. `EndCustomScan`
is an ordinary completion callback and is not guaranteed after an error. Put
abort cleanup under the query context's lifetime. The sample uses a context
reset callback to observe reclamation, including error exits.

Use guarded `NativeMethods` calls and generated callback values for operations
that can raise a PostgreSQL error. These raw APIs require an unsafe context and
preserve pointer types such as `CustomScanState*` and `TupleTableSlot*`.
Native errors become `PgException` while
managed frames unwind; PostgreSQL receives ERROR only after the managed frames
have returned. A raw unmanaged call must not let longjmp cross managed code.

Advertise only capabilities the provider implements. The trace sample delegates
reads and supported direction changes, marks and restores index positions, and
coordinates parallel observations. It also remaps its own parameter expressions
when PostgreSQL selects an outer partition for a parameterized join.

## Parameters and partition ancestry

PostgreSQL reparameterizes a custom path's child paths. Expressions retained in
`CustomPath.custom_private` belong to the provider, which must transform them
through `CustomPathMethods.ReparameterizeCustomPathByChild` when an outer
partition replaces its parent relation.

The sample retains copied direct outer variables from the path's parameter
clauses. Its callback uses `NativeMethods.adjust_appendrel_attrs_multilevel`
with the chosen child and its top parent, so column positions follow the whole
partition ancestry, including reordered and dropped columns. PostgreSQL 13–15
accept relation-id sets for this helper; PostgreSQL 16 and later accept relation
pointers. The sample selects the matching declaration with the SDK's
[`ANKUS_PG13` through `ANKUS_PG19` symbols](/reference/build-settings/#postgresql-compilation-symbols).
It returns new native nodes rather than mutating another path's data.

During plan creation, these expressions move into `CustomScan.custom_exprs`.
PostgreSQL then performs its ordinary outer-variable-to-parameter and plan
reference adjustments. Keep expressions needing those adjustments out of opaque
private plan data. Native copyable nodes also allow prepared plans to retain the
expressions after the original planning memory is reclaimed.

EXPLAIN's `Trace Parameters` displays the retained variables through PostgreSQL's
deparser and ancestor plan context. `Trace Parameter Remaps` counts the provider's
partition transformations. These diagnostics do not evaluate the original
clauses or repeat their volatile calls. Variables behind placeholder evaluation
barriers are left to the child, so the list is a diagnostic that can omit some
dependencies. Unparameterized paths omit both fields.

## Marking and restoring positions

PostgreSQL's merge executor may save an inner scan position and revisit it for
duplicate join keys. The trace provider advertises
`CUSTOMPATH_SUPPORT_MARK_RESTORE` only when the original, nonparallel child path
supports that protocol, as reported by `NativeMethods.ExecSupportsMarkRestore`.
Its `MarkPosCustomScan` and `RestrPosCustomScan` callbacks delegate to guarded
`NativeMethods.ExecMarkPos` and `NativeMethods.ExecRestrPos` on that child.
EXPLAIN's `Trace Marks` and `Trace Restores` report completed calls per node.

Restoring a position means that the next read produces the same tuple as the
first read after the mark. The previously returned slot is not a saved position:
its contents may change, and callers must discard it after restore. Let the
native access method own its position state. Sequential scans do not provide
mark/restore; a custom wrapper must not claim it merely because it can rescan.

Backward scanning is a separate capability. The sample checks
`NativeMethods.ExecSupportsBackwardScan` on the completed child plan before
setting `CUSTOMPATH_SUPPORT_BACKWARD_SCAN`. This follows the actual index access
method's capabilities and excludes parallel-aware partial scans. Index-only
children keep their native visibility checks and may still fetch heap tuples
when the visibility map requires it.

## Parallel shared state

Parallel execution is tested on PostgreSQL 13 through 19 RC1 on Linux x64, macOS ARM64, macOS x64 and Windows x64. PostgreSQL 13–16
cannot use internal subtransactions during parallel execution; see the
[SPI error boundary](/spi/#errors-and-transactions) before handling worker
errors. Selected-header compatibility alone does not establish validation of
every supported server version and platform.

A parallel-aware custom scan can request dynamic shared memory (DSM) through
`CustomExecMethods`. PostgreSQL owns the segment and supplies a coordinate
address to the leader and every worker. The address may differ between
processes. Store only process-independent values in shared memory, never managed
references or pointers into another process's address space.

The trace sample implements these callbacks:

| Callback | Responsibility |
|---|---|
| `EstimateDSMCustomScan` | Reserve the selected-header size of the provider's native atomic counters |
| `InitializeDSMCustomScan` | Initialize counters before workers attach |
| `InitializeWorkerCustomScan` | Borrow the local coordinate address and record the worker attachment |
| `ReInitializeDSMCustomScan` | Reset shared observations for a new execution after previous workers finish |
| `ShutdownCustomScan` | Copy observations into private state and clear the borrowed address |

Set `parallel_aware` only when the provider implements the corresponding
protocol. PostgreSQL still initializes and coordinates the sample's real
parallel scan child. The provider counts rows and executor calls with
PostgreSQL's `pg_atomic_uint64` operations; worker attachments and shutdowns
use the same native atomics.

`ReInitializeDSMCustomScan` resets shared state; `ReScanCustomScan` resets the
local scan and propagates parameters to its child. Keep the two responsibilities
independent rather than relying on their relative invocation order. PostgreSQL
can also execute a parallel plan entirely in the leader when workers are
unavailable.

EXPLAIN adds `Shared Trace Rows`, `Shared Trace Calls`, `Trace Worker Attachments`,
`Trace Worker Shutdowns` and `Trace DSM Generation` for parallel-aware trace
nodes. A complete execution reports combined leader/worker observations.
Generation starts at one and increments when the same parallel plan is
reinitialized. A node that never starts, such as beneath `LIMIT 0`, reports zeros.
The existing `Trace Rows`, `Trace Calls`, `Trace Rescans` and
`trace_scan_counts()` remain local to the reporting backend.

Shared EXPLAIN values are copied at shutdown. After early termination, workers
may still be finishing, so each counter is an independent snapshot that may
change before workers finish and may not match the others. Do not wait for workers
inside a child shutdown callback: its parent Gather may still need to detach
full tuple queues. On an error, ordinary shutdown and end callbacks are not
guaranteed; context cleanup must not dereference a borrowed DSM address after
PostgreSQL has detached it.

Build against the server headers that will load the extension.
