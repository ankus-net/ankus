---
title: Calling PostgreSQL functions
description: Inspect routine metadata and call built-in and extension functions by name, catalog OID, or native entry point.
---

Use `PgFunctions.Call<T>` inside an extension function to call a PostgreSQL
function without writing a SQL query:

```csharp
int positive = PgFunctions.Call<int>(
    "pg_catalog.abs", PgFunctionArgument.Create(-42));
```

This works with built-ins and functions written in C#, SQL, PL/pgSQL, or other
PostgreSQL languages. Names follow PostgreSQL's quoting, overload, and search-path
rules. You can also pass a function's catalog OID instead of its name.

`T` must match the result type. Use a nullable type when the result can be NULL:

```csharp
int? result = PgFunctions.Call<int?>(
    "pg_catalog.abs", PgFunctionArgument.Create<int?>(null));
```

Strict functions are not invoked when an argument is NULL. For a function
returning `void`, use `PgFunctions.Call` without a type argument. Only ordinary
functions that return one value can be called. Procedures, aggregates, window and
set-returning functions fail with SQLSTATE `42809`; run them through SQL, and use
`Spi.Select` to read a set of rows. `internal` arguments or results fail with
`0A000`; a C function taking them can be called by address through
[`DangerousCall`](#native-entry-points).

Use `Call<PgAnyElement>` when the result's PostgreSQL type is determined at
runtime, or `Call<PgAnyArray>` for an array. These wrappers retain the actual
type and belong to the current function call or iterator. SQL NULL returns a
null wrapper. Use `CopyTo(context)` to give a value another owner. An array call
checks the declared result is an array before invoking the function.

Use `Call<PgArrayView>` to inspect [borrowed native cells](/arrays/#borrowed-native-arrays)
in the array result snapshot. Its type checks follow the same array contract.
Dispose the view while the backend is active; `view.Datum.CopyTo(context)` gives
the value an independent owner.
`Call<PgArrayView<T>>` additionally checks the declared element type before the
function executes and converts individual cells on access. These checks also
apply to SQL NULL, empty and all-NULL results. A mapped element needs a reader;
its exact SQL identity is required even when its base representation matches.

Reusable [`PgDatumType` mappings](/raw-values/#reusable-scalar-mappings) can also
be the result of a named or OID call. Only a reader is required. Ankus checks the
declared result's exact mapped type before invoking the function or evaluating
default expressions; a domain's base type and sibling domains are distinct.
Nullable results retain that identity check even when SQL returns NULL. The
reader returns a detached managed value before temporary native storage is
disposed. A mapping without a reader is rejected before the call executes.
This also applies to mapped `T[]` and `PgArray<T>` results: the declared return
type must be the exact array type belonging to the mapped element. Element
conversion preserves SQL NULL and rejects lossy vector shapes.

## Inspecting routine metadata

`PgFunctions.GetInfo(oid)` returns a detached `PgFunctionInfo` snapshot for any
`pg_proc` entry: an ordinary function, procedure, aggregate, or window function.
Zero and missing OIDs return null. Lookup reads PostgreSQL's system cache without
invoking the routine or requiring its `EXECUTE` permission:

```csharp
PgFunctionInfo? info = PgFunctions.GetInfo(functionOid);
IReadOnlyList<uint>? inputTypes = info?.InputArgumentTypeOids;
```

The snapshot includes ownership and language OIDs, exact cost and row estimates,
planner support and variadic identities, security and strictness flags, volatility,
parallel safety, source, optional binary information, and local configuration.
Its strings and read-only collections survive callback return and catalog
changes. Obtain a fresh snapshot to observe `ALTER FUNCTION` or `DROP FUNCTION`.

`InputArgumentTypeOids` follows the call signature; `AllArgumentTypeOids`
also includes output arguments. `ArgumentModes` distinguishes IN, OUT, INOUT,
VARIADIC, and TABLE. An absent modes array becomes a sequence of IN values.
For names, an absent catalog array becomes one null per input argument;
an unnamed entry in a present array stays an empty string. These distinctions
match pgrx's `PgProc`. Collections use ordinary zero-based .NET indexing.
The fields retain PostgreSQL's [language-specific catalog meanings](https://www.postgresql.org/docs/18/catalog-pg-proc.html),
including an empty source string for a SQL-standard function body.

### Native default expressions

`info.GetDefaultArguments(context)` returns null when no defaults exist.
Otherwise it creates an owned `PgList<nint>` containing actual native expression
tree pointers for the last `DefaultArgumentCount` input arguments, in signature
order. Parsing does not evaluate a default or advance a sequence. Repeated calls
create independent trees from the captured catalog snapshot.

The explicit `PgMemoryContext` owns both the list and its nodes. Dispose the list
to release its container; its pointees remain until that context resets or is
deleted. List operations reject an expired context. Raw pointers require the
selected PostgreSQL version's node declarations and must not outlive their
owner. Use [`PgNodes`](/raw-values/#native-postgresql-declarations) to view these pointers as checked generated
node types.

## Default arguments

Omit trailing defaults or request one explicitly:

```csharp
int price = PgFunctions.Call<int>("discount_price",
    PgFunctionArgument.Create(100),
    PgFunctionArgument.Default<int>());
```

The default's type helps select the correct overload. PostgreSQL evaluates the
declared default expression during the call, including expressions such as
`nextval(...)`. A NULL value and a request for the default are distinct.

## Collation and variadic arguments

Pass `PgFunctionCallOptions` when a call needs a particular collation:

```csharp
[PgFunction]
public static string Lower(string value, PgFunctionContext call)
    => PgFunctions.Call<string>("pg_catalog.lower",
        new PgFunctionCallOptions { CollationOid = call.CollationOid },
        PgFunctionArgument.Create(value));
```

Without an override, PostgreSQL determines collation from argument types. Zero
explicitly selects no collation.

A named variadic call accepts individual arguments. To pass the final array
directly, set `Variadic = true`, matching SQL's `VARIADIC` keyword. Calls by OID
always use the declared parameter list, including any variadic array.

## Raw values

Use `CallRaw` for exact type OIDs, domains, or types without a managed mapping:

```csharp
using PgMemoryContext owner = PgMemoryContext.Create("function result");
PgDatum value = PgFunctions.CallRaw("current_status", owner);
string? text = value.ToPostgresString();
```

The result belongs to `owner` and expires when it resets or is deleted. A managed
read such as `Read<int>()` or `ToPostgresString()` returns an independent value.
Pass a raw input with `PgFunctionArgument.Create(datum)`. To reuse a parameter
with an explicit composite descriptor, use `PgFunctionArgument.Create(parameter)`.

## Errors and permissions

Calls by name or OID use the current role and honor `EXECUTE` permissions, security-definer
functions, and function-local settings. Where PostgreSQL permits internal
subtransactions, an error during native execution becomes a `PgException` after
the failed call's transactional database changes roll back. The caller can catch
the error and continue. Managed results are copied before native execution
storage is freed.

PostgreSQL 13–16 prohibit internal subtransactions during parallel execution.
In that mode, a native error ends the current managed callback: catching it does
not permit further ordinary backend calls, and the original error is reported
after managed cleanup finishes. See [parallel error recovery](/spi/#errors-and-transactions)
for resource disposal and recovery through an enclosing transaction or savepoint.

A mapped reader runs after native execution completes. Catching its managed
conversion error does not roll back the function's completed database changes.

## Native entry points

`DangerousCall<T>` and `DangerousCallRaw` call a native PostgreSQL version-1
function address with `PgDatum` arguments and an explicit collation. SQL NULL
remains separate from a zero datum. PostgreSQL errors still become `PgException`.

Both APIs require an explicit `unsafe` context. ANKUS129 checks authored and
generated callers; use the smallest block that contains the raw operation.

These APIs require a valid address and exact argument/result representations.
They supply null `flinfo`, `context`, and `resultinfo` fields to the native
function. Use name or OID calls when the function needs catalog metadata.
Copying a pointer-bearing value such as `internal` preserves its pointer; it
does not copy the pointed-to object or extend that object's lifetime.

`DangerousCall<T>` also selects registered scalar readers and their `T[]` or
`PgArray<T>` forms. Only a reader is required; a missing reader is rejected before
invocation. The current mapping supplies the result type you assert the address
returns. There is no independent catalog declaration to check. Array extraction
checks the physical element type and shape, while the actual nominal SQL type
and representation remain your responsibility.

The reader returns detached managed data before temporary native storage is
released. Whole SQL NULL skips the reader and its factory, after current mapping
identity and nullability checks. A caught reader or factory error occurs after
the native call has completed and does not undo its completed database changes.
Use `DangerousCallRaw` and `Read<T>()` when you want an explicit result owner or
delayed conversion.
