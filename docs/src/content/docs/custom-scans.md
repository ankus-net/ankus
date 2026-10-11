---
title: Custom scan providers
description: Use selected-header native methods and managed callbacks to plan and execute custom scans.
---

A custom scan provider participates in PostgreSQL's planner and executor. Use
the generated `Ankus.Postgres` declarations and `[PgNativeCallback]` for the
native method tables. Their fields and callback signatures come from the server
headers selected when the extension is built.

The [trace-scan sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.CustomScans)
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
`EXTNODENAME_MAX_LEN`, including its terminating zero byte; PostgreSQL checks
encoded bytes, not managed character counts. Duplicate registration raises a
native error and leaves the original method table installed.
`NativeMethods.GetCustomScanMethods` returns the retained table, returns a null
address for an optional missing lookup, or raises an error when the requested
name is required. These calls use the generated native error guard.

Registration belongs to the backend, not to a SQL transaction. Rolling back the
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
barriers are left to the child; the list is diagnostic, not a complete inventory
of every dependency. Unparameterized paths omit both fields.

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

Parallel execution is tested on PostgreSQL 13 through 19 beta on Linux x64, macOS ARM64, macOS x64 and Windows x64. PostgreSQL 13–16
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
may still be finishing, so each counter is an independent snapshot, not a final
worker total or a transactionally consistent group. Do not wait for workers
inside a child shutdown callback: its parent Gather may still need to detach
full tuple queues. On an error, ordinary shutdown and end callbacks are not
guaranteed; context cleanup must not dereference a borrowed DSM address after
PostgreSQL has detached it.

Build against the server headers that will load the extension.
