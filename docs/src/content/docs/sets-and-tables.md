---
title: Sets and tables
description: Return PostgreSQL sets and named tables from ordinary C# iterators.
---

Declare a `[PgFunction]` method with an `IEnumerable<T>` return type to produce
`SETOF T`. Use `yield return`, a collection expression, or another synchronous
sequence. Ankus generates the PostgreSQL iterator protocol and owns its cleanup.

```csharp
[PgFunction]
public static IEnumerable<int> Series(int start, int count)
{
    for (int offset = 0; offset < count; offset++)
    {
        yield return checked(start + offset);
    }
}
```

```sql
SELECT * FROM series(10, 3); -- 10, 11, 12
```

The declared return type must be `IEnumerable<T>`. Scalar mappings work unchanged
inside sets: `int[]` returns a PostgreSQL integer array, while
`IEnumerable<int[]>` returns a set of integer arrays. Each element can use any
supported [function type](/function-declarations/), including generated enums,
shaped arrays, ranges, numeric values, and full-range temporal values.

Generated `[PgType]` values and their arrays also work as set elements.
Types declared without an explicit schema follow the owning extension's schema,
including after `ALTER EXTENSION ... SET SCHEMA`; lookup does not depend on the
session's search path. This applies to streaming and materialized results.

For named or anonymous composite rows, use `IEnumerable<PgHeapTuple?>` and
[composite bindings](/composites/). TABLE fields can also contain composites.
PostgreSQL expands a single composite TABLE output into its underlying row
attributes; multi-column TABLE results retain composite cells as individual
columns. Materialized composite rows represent a NULL tuple as all-NULL fields.

`IEnumerable<PgAnyElement?>` and `IEnumerable<PgAnyArray?>` declare polymorphic
sets. Include a `PgAnyElement` or `PgAnyArray` input so PostgreSQL can resolve
the output type. Polymorphic TABLE columns follow the same rule. Their native
input values remain live across iterator advances.

[`PgRelation`](/relations/) inputs, including array elements, retain their
references until iterator disposal. New relation results transfer ownership to
the generated boundary and close after their OIDs are copied. Yielding an input
reference preserves its iterator lifetime, allowing it to be read or yielded
again. These rules also cover TABLE columns and error or early-exit cleanup.

## Named TABLE rows

Return named C# tuples to generate `RETURNS TABLE`. Tuple element names become
snake_case SQL column names, in their declared order.

```csharp
[PgFunction]
public static IEnumerable<(int WordNumber, string Word)> Words(string text)
{
    string[] words = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
    for (int index = 0; index < words.Length; index++)
    {
        yield return (index + 1, words[index]);
    }
}
```

```sql
SELECT word_number, word FROM words('hello PostgreSQL');
SELECT * FROM words('one two') WITH ORDINALITY;
```

Use `[return: PgColumnNames(...)]` to provide exact SQL names. It also expresses a
one-column TABLE without a tuple wrapper:

```csharp
[PgFunction]
[return: PgColumnNames("value")]
public static IEnumerable<int?> Values() => [1, null, 3];

[PgFunction]
[return: PgColumnNames("id", "display_name")]
public static IEnumerable<(int, string?)> People() => [(1, "Ada"), (2, null)];
```

Output names must be distinct PostgreSQL identifiers. An output column may have
the same name as an input parameter; PostgreSQL keeps the two roles separate.
Flat tuples support up to PostgreSQL's 1,664-field record limit;
nested tuples and nullable tuple rows are rejected. Make individual fields
nullable to represent SQL NULL. `ValueTuple<T>` is supported with an explicit
single column name.

Invalid result shapes report the precise declaration contract. `ANKUS396`
rejects `PgColumnNames` on a method that does not return `IEnumerable<T>`, `ANKUS397` rejects an
unsupported element or tuple shape, `ANKUS398` rejects a null names array,
`ANKUS399` requires names for unnamed table columns, and `ANKUS400` rejects
invalid, duplicate, missing, or excess column names.

## Empty sets and NULL

An empty sequence returns zero rows. A nullable sequence that returns `null`
also returns zero rows. A nullable *element* produces a row containing SQL NULL:

```csharp
[PgFunction]
public static IEnumerable<int?>? OptionalValues(bool absent)
    => absent ? null : [null, 42];
```

Strict NULL-input handling returns an empty set without invoking the method.
For a mixed nullable/non-nullable signature, a NULL for any required parameter
also returns an empty set. `PgNullInput` options apply as for scalar functions.

## Streaming and materialization

The default `PgSetMode.Auto` produces one row per PostgreSQL call whenever the
caller supports it. If the caller accepts only a materialized result, Ankus
fills a PostgreSQL tuple store. `ValuePerCall` and `Materialize` require their
respective caller modes and reject an incompatible context.

```csharp
[PgFunction(SetMode = PgSetMode.Materialize, Rows = 100)]
public static IEnumerable<(int WordNumber, string Word)> AllWords(string text)
    => Words(text);
```

`Rows` sets PostgreSQL's positive, finite planner estimate; the default is 1000.
It does not limit the result. Materialization uses PostgreSQL's `work_mem` and
can spill to temporary files. Converted per-row storage is released between rows.

SQL placement affects when rows are consumed. A set in a SELECT list can stop
early under `LIMIT`:

```sql
SELECT series(0, 1000000) LIMIT 2;
```

PostgreSQL normally materializes a function in `FROM` before applying an outer
`LIMIT`, even when the function uses the value-per-call protocol. Forced
materialization also consumes the complete sequence before a SELECT-list
`LIMIT` can stop reading its rows.

## Iterator lifetime and errors

The sequence factory and `GetEnumerator` run once for each set invocation.
`MoveNext` and `Current` run as rows are requested. Ankus roots the iterator
across native calls and calls `Dispose` exactly once after acquisition, including
empty results, early termination, portal closure, errors, and cancellation.
An iterator's `finally` blocks therefore run when PostgreSQL abandons the result.

An injected `PgMemoryContext` parameter represents the multi-call owner. The
factory and `GetEnumerator` run with that context current. Later row callbacks
can use temporary executor or materialization contexts, so allocate through the
injected handle when storage must survive across rows. PostgreSQL reclaims the
owner at invocation cleanup; retained checked handles then become stale.
Live owners and their ancestors cannot be reset or deleted through managed
memory APIs, including while a cursor is suspended between fetches.

Iterator disposal can read storage allocated directly in its owner before native
cleanup invalidates it. Descendant contexts follow PostgreSQL's child-before-parent
cleanup order; their storage may already be gone during abort disposal.

Each normal callback has the usual guarded backend binding, so iterator bodies
can use SPI and enum catalog APIs after resumption. Ordinary completion and early
termination also permit backend calls during disposal. During PostgreSQL abort
cleanup, queries and catalog operations are unavailable: the transaction or
executor may already be shutting down. Disposal of owned SPI plans and cursors
still releases those resources through a restricted native guard. Keep other
abort cleanup independent of database work.

Unsafe raw calls must satisfy the native function's transaction and ownership
preconditions. Valid releases such as `pfree` remain supported during abort;
the error guard does not establish whether arbitrary backend work is safe there.
Checked lifetime access and disposal of owned resources remain available.

Cursor closure during rollback is deferred until PostgreSQL finishes scanning
its cursor registry, including when the iterator adopted a cursor opened before
the failed savepoint.

Factory, iteration, conversion, and normal disposal exceptions become PostgreSQL
errors after managed frames unwind. If disposal also fails while another error
is being handled, its secondary diagnostic is reported as a warning while
preserving the original error and completing rollback. Native failures during
ordinary execution cannot be swallowed to finish the query successfully.
After an unrecovered native row error, disposal can release
owned resources but cannot begin new backend work. Cancellation and terminal
reports retain their effect through cleanup.

PostgreSQL interrupt checks run between rows; they cannot preempt a
managed `MoveNext` that never returns or calls a guarded backend API.

`[return: PgNumericPrecision(...)]` constrains each numeric element of a scalar
set. TABLE fields can be rescaled explicitly in the iterator. Operators and
casts cannot return sets. SQL graph IDs, prerequisites, enum dependencies, and
function execution options work as they do for scalar functions.

See the [sets sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.Sets)
for streaming integers and streaming/materialized word tables.
