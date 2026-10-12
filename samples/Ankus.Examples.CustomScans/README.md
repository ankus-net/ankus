# Custom scans

This extension wraps PostgreSQL sequential, index and index-only scan paths in
a native custom scan. It retains the original child path, costs, ordering,
qualifications and parameters, then
delegates tuple execution to that child. EXPLAIN reports the custom provider and
its per-node row, call, rescan, mark and restore counters.

After publishing and installing `ankus_trace_scan`, load it in the session that
will plan queries:

```sql
CREATE EXTENSION ankus_trace_scan;
SELECT trace_scan_reset();
SET ankus_trace_scan.enabled = on;
EXPLAIN (ANALYZE, FORMAT JSON) SELECT * FROM your_table;
SELECT trace_scan_counts();
```

In a later connection, call `trace_scan_reset()` to load the library before
enabling tracing. The eight backend-local counters report planning, state
creation, begin, execution, end, rescan, managed unwind and native query-context
cleanup, in that order. Returned arrays are snapshots. Counters in parallel
workers belong to those workers; they are not summed into the leader's counters.

Parallel execution is verified on PostgreSQL 17 and 18. PostgreSQL 15's
worker initialization and guarded SQL recovery remain incomplete; successful
parameter remapping alone does not establish older-worker support.

Parallel-aware trace nodes additionally report shared rows, executor calls,
worker attachments, worker shutdowns and the DSM execution generation in
EXPLAIN. `TraceScan.Parallel.cs` implements PostgreSQL's estimate, initialize,
reinitialize, worker-attach and shutdown callbacks with selected-header native
atomics. PostgreSQL owns both the segment and the child scan's work distribution.
Shutdown copies the shared observations before releasing the borrowed address.
For an early LIMIT, these are snapshots while workers may still be finishing,
not final totals. A rescan resets shared counters and increments the generation;
ordinary per-backend lifecycle counters retain their original meaning.

`TraceScan.cs` demonstrates field-named `[PgNativeCallback]` declarations,
backend-lived method tables, previous-hook chaining, copyable plan state, a
native state prefix, scan projection, rescan, EXPLAIN and context cleanup.
`TraceScan.Position.cs` delegates mark/restore to native index children whose
access methods support it. `Trace Marks` and `Trace Restores` in EXPLAIN observe
those callbacks during merge joins. Backward scanning is advertised only when
the actual child plan supports it; sequential children do not gain mark/restore.
`TraceScan.Parameters.cs` retains copyable outer-variable expressions, maps them
through partition ancestry, and places them in the final plan's expression list
for PostgreSQL's reference adjustments. EXPLAIN reports `Trace Parameters` and
`Trace Parameter Remaps` without evaluating the original clauses again.
The SDK's selected-major compilation symbol chooses PostgreSQL 13–15's
relation-id helper arguments or PostgreSQL 16 and later's relation pointers;
PostgreSQL 13–14's unsigned variable index is converted with overflow checking.
Disabling the setting affects future planning; already prepared custom plans
continue using the registered methods until PostgreSQL discards them.
The predecessor planner hook still runs while tracing is disabled. Native
method registration lasts for the backend lifetime, including across SQL
rollback; a duplicate name cannot replace an existing provider's table.

## Managed numbers scan

`NumberScan.cs` is a managed provider. For tables whose names start with
`numbers`, it returns the integers 1 to N in a `value` column and text in an
optional `label` column. The table's stored rows are ignored:

```sql
SELECT trace_scan_reset();
SET ankus_trace_scan.numbers = 5;
CREATE TEMP TABLE numbers(value bigint, label text);
EXPLAIN (ANALYZE) SELECT * FROM numbers WHERE value > 2;
SELECT number_scan_counts();
```

It shows path data, ordered rows, rescans, backward reads, mark and restore,
EXPLAIN properties and state disposal. Its rows come in order of `value`, so
`ORDER BY value` needs no sort, a cursor can fetch backward and a merge join
reads repeated keys again without a `Materialize` node.
`number_scan_counts()` reports begins, disposals, rescans, EXPLAIN-only
begins, marks and restores. Setting `ankus_trace_scan.numbers_fail_at` makes the scan throw at that
integer.
