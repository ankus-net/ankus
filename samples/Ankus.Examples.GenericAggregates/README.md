# Generic aggregate example

This sample ports pgrx's `generic_agg` example. It defines one polymorphic
aggregate:

```sql
count_changes(anyelement) RETURNS bigint
```

It counts how many times the input differs from the previous non-null input:

```sql
CREATE EXTENSION ankus_generic_aggregates;

SELECT count_changes(v ORDER BY ord)
FROM (VALUES (1,'a'),(2,'a'),(3,'b'),(4,'b'),(5,'b'),(6,'c')) t(ord, v);
-- 2

SELECT count_changes(v ORDER BY ord)
FROM (VALUES (1,1),(2,1),(3,2),(4,2),(5,2),(6,1),(7,1)) t(ord, v);
-- 2

SELECT count_changes(v) FROM (SELECT 1 WHERE false) t(v);
-- 0
```

SQL NULL inputs are ignored. Use an aggregate
`ORDER BY` clause: PostgreSQL does not otherwise define the input order.

## How pgrx maps to C#

pgrx's `#[pg_aggregate] impl Aggregate` becomes a class marked `[PgAggregate]`
that implements `IPgAggregate<TState, TArgs>` and
`IPgFinalizingAggregate<TState, TDirect, TResult>`. `Option<AnyElement>` becomes
`PgAnyElement?`. pgrx's `Internal` state holding a `ChangeState` struct becomes
`PgAggregateState<ChangeState>?`: PostgreSQL sees `internal`, and C# keeps an
ordinary managed object for each group.

The input's type is only known at run time, so the aggregate uses PostgreSQL's
own functions to handle it. As in pgrx, `get_typlenbyval` reads the type's
storage contract once per group, and `datumIsEqual` from `utils/datum.h`
compares two values byte for byte. Ankus exposes these as generated
`Ankus.Postgres.NativeMethods` bindings. Calling them requires an `unsafe`
block; Ankus still runs them under its PostgreSQL error guard.

pgrx calls `AggCheckCallContext`, switches to the aggregate's memory context,
and uses `datumCopy` and `pfree` to keep the previous value alive between rows.
In C#, `PgAggregateContext.MemoryContext` is that aggregate context. The state
creates a child context in it, and `PgAnyElement.CopyTo` copies each retained
value there. Before retaining a new value, the state resets the child context,
which releases the previous copy. PostgreSQL deletes the child with the
group's state. Retained values are checked handles: using one after its
context has been reset raises an error before it can read freed memory.

Ankus's generated support functions call `AggCheckCallContext` themselves and
reject a call outside an aggregate with SQLSTATE `55000`, so the sample does not
repeat pgrx's check. SQL cannot supply the `internal` state argument directly
in any case.

## Deliberate differences

- **Normalized comparison.** `CopyTo` detoasts, decompresses and flattens a
  value, and gives it a standard four-byte length header. The sample makes the
  same kind of copy of each incoming value in the transition call's short-lived
  context before comparing it. Equal values therefore compare equal even when
  one arrives with a short header or compressed from a table and the other was
  computed. pgrx compares each incoming value in the form it arrives in.
  Comparison is still byte-level: values that are equal but stored differently,
  such as `1.0` and `1.00` as `numeric`, count as a change, as they do in pgrx.
- **Overflow.** The change count uses checked addition.
- **Separate sample.** pgrx keeps `generic_agg` apart from its `aggregate`
  example. This port does the same: it is about raw datum bindings, unsafe
  native calls and polymorphic state, while `Ankus.Examples.Aggregates` covers
  typed state, parallel and moving aggregates without native calls.
