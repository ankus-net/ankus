---
title: SPI queries
description: Query PostgreSQL from C# with typed parameters, sessions, plans, and cursors.
---

`Spi` executes SQL in the PostgreSQL backend running the extension function.
Calls participate in the caller's transaction and use that connection's role,
search path, and session settings.

The [SPI sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.Spi)
combines bound lookups and inserts, cursor results that outlive their session,
and table functions returning named C# tuples. It ports pgrx's `spi` and
`spi_srf` examples, including their seed data.

## Parameters and commands

Bind values with positional placeholders and `SpiParameter.Create`:

```csharp
long inserted = Spi.Execute(
    "INSERT INTO messages (body) VALUES ($1)",
    SpiParameter.Create("Hello, PostgreSQL!"));
```

`Execute` returns the final statement's processed-row count. Parameter types are
inferred from the declared C# type. Use an explicit nullable type for SQL NULL:

```csharp
SpiParameter optionalCount = SpiParameter.Create<int?>(null);
SpiParameter optionalText = SpiParameter.Create<string?>(null);
```

Parameters accept `bool`, `sbyte`, `short`, `int`, `long`, `uint` (OID), `float`,
`double`, `decimal`, `string`, `byte[]`, `Guid`, `PgJson`, `PgJsonb`, `PgNumeric`, the
[temporal types](/date-and-time/), [ranges](/ranges/), [geometric](/geometry/) and
[network](/network/) values, custom types and registered datum mappings, raw datums,
nullable forms, and [arrays](/arrays/). Parameter values
are sent separately from the SQL command. Each command call uses one internal
subtransaction and reports results from its final statement.

`PgTextView` and `PgByteaView` also bind as parameters, preserving their original
SQL type identity. Nullable views declare `text` and `bytea` for SQL NULL.

See [JSON and UUID values](/json-and-uuid/) for the distinction between JSON null
and SQL NULL and for source-generated JSON serialization.

[Composite tuples](/composites/) and their arrays retain owned field metadata.
Use `SpiParameter.Create(tuple, descriptor)` or `CreateArray(array, descriptor)`
for explicit named identities, including typed NULLs. `PrepareWithTypeOids`
accepts catalog parameter OIDs for named composite plans; `Prepare` with
`typeof(PgHeapTuple)` declares an anonymous `record` parameter. Both static and
session APIs support these plans and their usual cursor/ownership operations.

### Parameterized interpolation

Use `Spi.Sql` to bind interpolated values without formatting them into SQL:

```csharp
string body = "Hello, PostgreSQL!";
long inserted = Spi.Execute(Spi.Sql($"INSERT INTO messages (body) VALUES ({body})"));

int? optionalCount = null;
int? count = Spi.ExecuteScalar<int?>(Spi.Sql($"SELECT {optionalCount}"));
```

Each interpolation becomes a parenthesized positional parameter with its declared C# type.
SQL text, values and typed NULLs follow the same rules as `SpiParameter.Create`.
An untyped null interpolation binds SQL NULL as `text`; use a typed nullable
value when another SQL type is required. Pass an existing `SpiParameter` in an
interpolation to retain an explicit composite, domain or raw datum identity.

Do not quote interpolations or put them inside SQL comments, identifiers or
dollar-quoted strings. Construction rejects these contexts with
`ArgumentException`. It also rejects literal positional parameter tokens such
as `$1`; use interpolations for every binding in a `Spi.Sql` command. Dollar
text inside literal strings, identifiers and comments remains unchanged.
Separate SQL expressions with valid SQL syntax: a digit immediately after a
hole does not become part of its parameter number.

Do not use interpolations for object names or SQL fragments.
Values are sent separately from SQL. Formatting and alignment specifiers are
not supported; they would change a value's representation. String overloads take
raw SQL text. Passing a nonconstant interpolated string directly to one of these
overloads produces compiler error **ANKUS044** for unquoted runtime values:
wrap value interpolations in `Spi.Sql`, or use positional parameters with a
literal command. Interpolations containing only constants or unchanged results
of `Spi.QuoteIdentifier`, `Spi.QuoteQualifiedIdentifier` and `Spi.QuoteLiteral`
are permitted as raw SQL fragments. Insert those complete fragments outside
SQL quotes; the helpers supply their own quoting. Prepared
statements use positional parameters and explicit parameter types.

The compiler check follows reaching local strings, `StringBuilder` contents and
format-argument arrays, including aliases, reassignment, branches, loops, local
callbacks and exception cleanup. A parameter keeps its caller-supplied value on
every path that does not replace it. Clearing a builder or replacing a command with
literal SQL removes the earlier construction. Formatting a builder or tuple into a
command, and `string.Concat`, `string.Join`, `string.Format` or another string
operation that combines runtime text, counts as raw construction. Writes through
`ref` locals, reference-returning methods, spans and deconstruction can replace a
quoted local. Complete quoting results must occur outside SQL strings, quoted
identifiers, dollar strings and comments; the helpers supply their own delimiters.
Ordinary positional parameters remain valid in raw SQL.

Lambdas and local functions are checked even when the method does not invoke them.
A callback passed to LINQ, stored for later or otherwise not invoked locally is
checked with the values assigned after its creation, and its parameters are
treated as runtime values:

```csharp
// ANKUS044: the selector concatenates each runtime name into the command.
long[] counts = [.. names.Select(name => Spi.ExecuteScalar<long>("SELECT count(*) FROM " + name))];
```

The analysis is bounded and does not prove arbitrary helper implementations or
runtime-generated text safe. When construction exceeds the bound, including the
alternatives of a `string.Format` call, ANKUS044 reports the command instead of
accepting it. Callers remain responsible for constructing raw SQL safely. Use
`Spi.Sql` for values and the quoting APIs below for dynamic identifiers.

Use explicit `E'...'` syntax for literal backslash escapes. `Spi.Sql` checks
binding boundaries under both ordinary-string escape settings without a
backend call and rejects text whose bindings depend on that setting. Bound
values do not need SQL escaping.

`Spi.Sql` returns a `SpiCommand`, whose owned SQL text and binding vector can be
reused. It supports `Select`, `Query`, `Execute`, scalar and raw results, `Explain`
and cursors, including scoped sessions:

```csharp
int minimumId = 42;
SpiCommand query = Spi.Sql($"SELECT id, body FROM messages WHERE id >= {minimumId}");
SpiResult rows = Spi.Connect(session => session.Select(query, limit: 100));
```

Command construction retains each parameter's value and type; it does not copy
mutable values or native storage. Raw datum owners must remain live through
execution and still obey their callback/context lifetimes. Commands do not
change SPI snapshot selection, recovery, cancellation or ownership rules.

## Scalar values

```csharp
int answer = Spi.ExecuteScalar<int>(
    "SELECT $1 + $2",
    SpiParameter.Create(40),
    SpiParameter.Create(2));
```

`ExecuteScalar<T>` reads the first column of the first row. SQL NULL or an absent
cell returns null when `T` is a reference or nullable value type. A non-nullable
value type throws `InvalidOperationException` instead of substituting a default.
An incompatible result type throws `InvalidCastException`; numeric values are not
implicitly widened or parsed from text.

[`PgRelation`](/relations/) reads a `regclass` result as an independently
disposable relation reference. Ordinary row materialization retains its OID
without opening it; each `Get<PgRelation>` acquires a new reference. Dispose
every nonnull element of a returned relation array. `ExecuteScalars` closes
provisional relation results when a later conversion fails. Relation parameters
retain their caller's ownership, and ordinary `oid` results remain `uint`.

Use `ExecuteScalars` for two or three columns from the first row:

```csharp
(int id, string? body) = Spi.ExecuteScalars<int, string?>(
    "SELECT id, body FROM messages WHERE id = $1", SpiParameter.Create(42));
```

Each value follows the same type and NULL rules as `ExecuteScalar<T>`. A returned
row with too few columns throws `InvalidOperationException`. Extra columns and
later rows are not copied. These overloads also work on sessions and prepared statements.

Reusable [`PgDatumType` mappings](/raw-values/#reusable-scalar-mappings) work in
these scalar helpers, including mixed columns. The requested managed type selects
its reader, which returns a detached value before temporary native storage is
disposed. No writer is required for a result. A mapping without a reader is
rejected before execution, including in later tuple columns and queries that
would return no rows. Present and NULL cells must have the exact mapped SQL type;
a domain's base type or sibling domain is not interchangeable.
Mapped vectors and `PgArray<T>` use the same helpers and exact identity rules.
Their element readers return detached values; use `PgArray<T>` when dimensions
or lower bounds cannot be represented by a vector. See [mapped array elements](/arrays/#mapped-elements).

Use `PgAnyElement` or `PgAnyArray` as the result type to keep the actual PostgreSQL
type, including types without a C# mapping. SQL NULL becomes a null wrapper.
These values survive SPI session disposal and belong to the current function
call or iterator. `CopyTo(context)` gives them another memory owner.

`PgArrayView` exposes [borrowed native cells](/arrays/#borrowed-native-arrays)
from a callback-owned result snapshot. Dispose the view while the backend is
active, and use `view.Datum.CopyTo(context)` to retain a separate value.
Use `PgArrayView<T>` for checked, lazy scalar conversions with the same ownership
rules. Whole-array SQL NULL returns a null view after element identity checks;
present parameters preserve the view's original array type without element writers.

Reading scalars does not limit command execution. For example, an
`INSERT ... RETURNING` command completes all its writes even though only its first
row's requested cells are copied.

Mapped and polymorphic results are converted after SQL execution completes. If
the caller catches a managed conversion error, the completed command's writes
remain in the transaction. A PostgreSQL error during native execution still
rolls back the command's internal subtransaction.

## Rows and metadata

```csharp
SpiResult result = Spi.Select("SELECT id, body FROM messages ORDER BY id");

foreach (SpiRow row in result)
{
    int id = row.Get<int>("id");
    string? body = row.Get<string?>(1);
}
```

Ordinals are zero-based. Column names are case-sensitive, and duplicate names
resolve to the first matching column. The row indexer returns a managed object or
null. `Columns` exposes each column's name and PostgreSQL type OID, including for
queries that return no rows. Domain columns retain their declared OID and use the
underlying base type's value conversion.

A column whose type has no managed conversion, such as `tsvector` or a base type
your extension declares, is copied exactly and converted only when you read it,
as pgrx does. Read it with its [`PgDatumType`](/raw-values/#reusable-scalar-mappings),
or an array of one: `row.Get<Unsigned24>("value")`. The cell must have exactly the
mapped type. The indexer and other types throw `NotSupportedException` for such a
cell, and the rest of the row stays readable. `row.Get<PgDatum>("value")` rebuilds any
cell, including SQL NULL, as a raw datum of its type owned by the current callback.
`result.GetOrdinal(name)` resolves a column even when the query returned no rows.

Results are managed copies. Rows, text, and binary buffers remain valid after
another SPI command or after the original SPI connection is released.
PostgreSQL first materializes the complete result in SPI memory, as with pgrx's
`Spi::select`, and Ankus then copies it. One result is limited to 2,147,483,647
rows and to the backend's maximum allocation; a larger result fails with SQLSTATE
`54000` instead of being truncated. Use a [cursor](#cursors-and-batched-results)
to stream large results in batches.

`Select` follows pgrx's transaction policy. Before the transaction has a real
PostgreSQL transaction ID, it uses the caller's read-only SPI snapshot and rejects
writes and row-locking queries. It does not allocate a transaction ID for a read.
After the caller or a writable SPI operation establishes that ID, selection uses
fresh writable snapshots so it sees preceding writes. This applies across nested
SPI sessions and resets when the transaction ends.

Some native callbacks run without an active snapshot, such as a utility hook
while PostgreSQL executes `SET` or `LOCK`. A read-only query or cursor there
uses the transaction snapshot for that statement, as a writable query does.
A callback that runs no SQL leaves snapshot timing unchanged, so a REPEATABLE
READ transaction still takes its snapshot at its first query.

`Execute`, the default `Query` and `QueryRaw` overloads, and scalar helpers
establish writable intent, including when their SQL is a `SELECT`. This matches
pgrx's `run` and `get_one` helpers. Choose `Select` for transaction-aware reads;
use an explicit `readOnly` overload when you need a fixed snapshot policy.

Write-intent helpers fail on a hot standby even for a `SELECT`: PostgreSQL
cannot assign a transaction ID during recovery. Use `Select`, `SelectRaw` or
an available explicit `readOnly: true` overload for standby reads.

### Raw PostgreSQL values

Use `SelectRaw` when a PostgreSQL type has no managed mapping:

```csharp
using SpiRawResult result = Spi.SelectRaw("SELECT value FROM custom_values");
PgDatum value = result[0]["value"];
string? text = value.ToPostgresString();

Spi.Execute("INSERT INTO custom_values VALUES ($1)", SpiParameter.Create(value));
```

Each `PgDatum` preserves its exact type OID and SQL NULL flag. `Read<T>()` converts
to a supported representation; `Read(converter)` lets you supply your own conversion.
Sessions and prepared statements also offer `SelectRaw` and writable `QueryRaw`.

`result[0].Get<T>("value")` reads a column directly. Ordinary managed values are
independent copies; `PgAnyElement` and `PgAnyArray` wrappers share the raw result's
lifetime and preserve its actual type.

`Read<PgTextView>()`, `Read<PgByteaView>()`, `Read<PgArrayView>()` and
`Read<PgArrayView<T>>()` borrow the
raw result's lifetime. Dispose these views before their source ends. Typed
`ExecuteScalar` and `ExecuteScalars` calls instead retain view storage under the
enclosing callback, so the returned views survive temporary SPI result, session
and plan cleanup. If a later column fails conversion, all earlier provisional
views are released before the error reaches your code. See
[text and binary values](/text-and-binary/) and [arrays](/arrays/#borrowed-native-arrays).

Raw results own native memory and survive SPI session and plan disposal. Dispose
them within the backend callback. Their values expire when the result is disposed
or its enclosing PostgreSQL context is reclaimed. Use `value.CopyTo(context)` for
a different lifetime; resetting or deleting that context invalidates the copy.

### Editing result rows

`SpiRow.Set<T>` replaces a cell in the managed result, using either an ordinal or
an exact column name:

```csharp
SpiRow row = result[0];
row.Set("body", "edited locally");
row.Set<int?>("id", null);

int ordinal = row.GetOrdinal("body");
string body = row.Get<string>(ordinal);
uint cellType = row.GetTypeOid("id"); // int4 OID, even though the cell is NULL
```

Edits belong to that row's managed copy. Database changes use SQL commands.
`Set<T>` accepts the supported datum types and permits replacing a cell with a
different type. `GetTypeOid` tracks each cell's current type; `result.Columns`
continues to describe the original query. A domain cell retains its domain OID
until replaced. Typed NULL retains the replacement type's OID. Invalid types,
names, or ordinals leave the row's value and type intact.

`Count` gives the number of cells. Both numeric and named indexers return the
managed object or null. Managed reference values, including byte arrays, follow
ordinary .NET reference semantics when assigned to a row. The owned rows can be
read or edited after returning from an extension callback, without a backend binding.

Passing a `PgDatum` to `SpiRow.Set` or `PgHeapTuple.Set` copies its value, including
one without a managed conversion. A `PgDatumType` value is written through its
mapping and copied the same way. The copy remains usable after the raw result is
disposed. A failed conversion leaves the previous cell unchanged.

### Query options

For an explicit row limit or read-only SPI execution:

```csharp
SpiResult page = Spi.Query(
    "SELECT id FROM messages ORDER BY id",
    readOnly: true,
    limit: 100);
```

A limit of zero means unlimited. Read-only mode uses PostgreSQL's read-only SPI
snapshot and restrictions, including rejection of write commands.
`Select(sql, limit: 100)` applies that limit with transaction-aware snapshots.
`SelectRaw` accepts the same limit and keeps its usual owned native lifetime.

## Quoting SQL fragments

Use PostgreSQL's identifier rules when assembling dynamic object names:

```csharp
string table = Spi.QuoteQualifiedIdentifier("my schema", "messages");
string column = Spi.QuoteIdentifier("Message Body");
Spi.Execute($"INSERT INTO {table} ({column}) VALUES ($1)", SpiParameter.Create("hello"));
```

`QuoteIdentifier` treats its input as one identifier, including any dots.
`QuoteQualifiedIdentifier` quotes its two components independently; a null
qualifier omits the prefix, while an empty qualifier represents an empty name.
These functions use the server's keyword table and `quote_all_identifiers` setting.

ANKUS044 accepts these direct helper calls and locals whose reaching values remain completely quoted. It
continues to reject an unquoted runtime value mixed into the same raw command;
bind values separately with positional parameters, as above.
The [PGLZ inspection sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.PglzInspect)
samples a caller-selected table and column this way.

Quoted fragments can also be accumulated in a loop or joined after selecting
an actual `Spi.QuoteIdentifier` or `Spi.QuoteLiteral` method. Every dynamic
fragment must stay a complete SQL atom:

```csharp
string fields = string.Join(", ", names.Select(Spi.QuoteIdentifier));
Spi.Execute("SELECT " + fields + " FROM " + Spi.QuoteIdentifier(tableName));
```

`Spi.QuoteLiteral(text)` produces a SQL text literal, escaping apostrophes and
backslashes. Its output is valid with either `standard_conforming_strings`
setting on PostgreSQL 13–18. PostgreSQL 19 requires that setting to stay `on`
and rejects `off`; explicit escape literals still work. Quoting uses
server-encoding conversion and the active backend thread;
embedded zero characters and malformed UTF-16 are rejected before native calls.

## JSON query plans

```csharp
PgJson plan = Spi.Explain(
    "SELECT * FROM messages WHERE id = $1",
    SpiParameter.Create(42));

using JsonDocument document = plan.Parse();
JsonElement root = document.RootElement[0].GetProperty("Plan");
```

`Spi.Explain` and `SpiSession.Explain` run `EXPLAIN (FORMAT JSON)` and return an
owned JSON value. The server plans the supplied statement with its typed
parameters, using ordinary EXPLAIN rather than EXPLAIN ANALYZE. The command
accepts one SQL statement. Parse and planning errors throw `PgException`.

## Prepared statements

Prepare a statement once and bind new values on each execution:

```csharp
using SpiPreparedStatement statement = Spi.Prepare(
    "SELECT $1 + $2", typeof(int), typeof(int));

int first = statement.ExecuteScalar<int>(SpiParameter.Create(40), SpiParameter.Create(2));
int second = statement.ExecuteScalar<int>(SpiParameter.Create(10), SpiParameter.Create(5));
```

The declared CLR types determine the PostgreSQL parameter types. Nullable value
types map to the same SQL type as their underlying type. Bind SQL NULL with a
typed nullable parameter.
An incorrect parameter count or SQL type throws `ArgumentException` before the
plan executes.

`SpiPreparedStatement` provides `Select`, `SelectRaw`, `Execute`, `Query`, `ExecuteScalar`, and `ExecuteScalars` with the
same result semantics as `Spi`. `Query` also accepts `readOnly` and `limit` options.
Preparation supports multiple SQL commands; each execution's internal subtransaction
covers all commands, and results describe the final command.

Plans created by `Spi.Prepare` survive the SPI connection, the extension callback, and transaction commit
or rollback. They can be cached within a backend. PostgreSQL manages replanning
after schema or search-path changes. Result rows are independent managed copies
even when the same statement is executed again.

Execution and disposal require an extension callback on the owning backend thread.
Use `using` for local plans and explicitly dispose cached plans when replacing them.
Disposal is idempotent, and subsequent execution throws `ObjectDisposedException`.
Reentrant execution of independently retained plans is supported; disposal or retention during an active execution throws
`InvalidOperationException` to preserve the native plan's lifetime.

There is no finalizer that calls PostgreSQL: its APIs cannot run on the .NET
finalizer thread. An independently retained plan that is never explicitly disposed remains allocated in
the backend until the process exits.

## Scoped SPI sessions

`Spi.Connect` runs a synchronous callback using one native SPI connection:

```csharp
SpiResult rows = Spi.Connect(session =>
{
    session.Execute("INSERT INTO messages (body) VALUES ($1)", SpiParameter.Create("hello"));

    using SpiPreparedStatement statement = session.Prepare(
        "SELECT id, body FROM messages WHERE id >= $1 ORDER BY id", typeof(int));
    return statement.Query(SpiParameter.Create(100));
});

// The materialized rows remain valid after the native session closes.
foreach (SpiRow row in rows)
{
    int id = row.Get<int>("id");
}
```

The session offers `Select`, `SelectRaw`, `Execute`, `Query`, `ExecuteScalar`, `ExecuteScalars`, `Prepare`, and
`OpenCursor`, with the same parameter and owned-result conversions as the
standalone API. `Query` and `OpenCursor` accept explicit read-only execution mode.
Session operations run in individual internal subtransactions: a failed command
rolls back that command, and successful commands remain in the caller's
transaction. The callback's exit closes the connection, including when a managed
exception or PostgreSQL cancellation unwinds the callback.

A statement created by `session.Prepare` belongs to that session and is freed
automatically when the callback exits. Explicit disposal can release it earlier.
Use `Keep()` while the session is active to transfer ownership out of the scope:

```csharp
using SpiPreparedStatement statement = Spi.Connect(session =>
    session.Prepare("SELECT $1 + 2", typeof(int)).Keep());

int answer = statement.ExecuteScalar<int>(SpiParameter.Create(40));
```

`Keep()` returns the same statement instance. The retained statement survives
callback and transaction boundaries and requires explicit disposal. Both scoped
and retained statements participate in PostgreSQL plan invalidation after schema
changes. Cursors opened by a session or its plans have their normal portal
lifetimes and can be fetched after the session closes.

Sessions nest in stack order. While an inner session callback runs, operations on
an outer session or its scoped plans throw `InvalidOperationException`; access
resumes after the inner scope exits. A recursive extension callback must use an
independent session or the standalone SPI API. Escaped sessions and unretained
plans throw `ObjectDisposedException` when used after their scope ends. The
callback must remain synchronous and on the backend thread; asynchronous session
work is not supported.

## Cursors and batched results

Use a cursor to fetch a query incrementally:

The default cursor overload follows the same transaction policy as `Select` when
opening its snapshot. An explicit `readOnly` argument chooses a fixed policy.
These rules also apply to session cursors and prepared-plan cursors.

```csharp
using SpiCursor cursor = Spi.OpenCursor(
    "SELECT id, body FROM messages WHERE id >= $1 ORDER BY id",
    readOnly: true,
    SpiParameter.Create(100));

while (true)
{
    SpiResult batch = cursor.Fetch(128);
    if (batch.Count == 0)
    {
        break;
    }

    foreach (SpiRow row in batch)
    {
        int id = row.Get<int>("id");
        string? body = row.Get<string?>("body");
    }
}
```

Each batch owns its managed rows and buffers and remains valid after later fetches
or cursor disposal. Empty batches retain column metadata. `SpiPreparedStatement.OpenCursor`
accepts the plan's typed parameters; the resulting cursor remains usable even after
the prepared statement is disposed.

Use `cursor.FetchRaw(count)` for types without managed mappings. It returns a
disposable `SpiRawResult` with exact type OIDs, SQL NULL flags, and the same
conversion methods as `QueryRaw`. Each batch survives later fetches and cursor
disposal. Dispose raw batches before leaving the backend callback, or use
`PgDatum.CopyTo(context)` to give individual values another lifetime.

`Fetch(count)` moves forward. `Fetch(count, forward: false)` moves backward when
the underlying PostgreSQL cursor supports scrolling. Counts must be nonnegative;
zero means fetch the current row, following PostgreSQL semantics, and can require
a scrollable cursor. `Spi.FindCursor` can attach to cursors declared using SQL,
including `SCROLL` and `WITH HOLD` cursors.

To continue a cursor in another extension callback within the same transaction,
detach its ownership and retain its portal name:

```csharp
string name;
using (SpiCursor cursor = Spi.OpenCursor("SELECT id FROM messages ORDER BY id"))
{
    name = cursor.Detach();
}

using SpiCursor resumed = Spi.FindCursor(name);
SpiResult nextBatch = resumed.Fetch(128);
```

`Detach` leaves the portal open and makes the original managed object unusable.
Disposal closes an owned portal and is idempotent. If several objects refer to
the same portal, closing one invalidates the others. Normal SPI-opened cursors
end with their transaction; a savepoint rollback also closes cursors created
inside that savepoint. Externally declared holdable cursors follow PostgreSQL's
hold semantics. Stale objects produce a managed `PgException` with SQLSTATE `34000`;
reusing a portal name never makes an old object refer to the replacement.

Cursor operations require the owning backend thread. A failed fetch can leave
the portal unusable; cursor position and recovery follow PostgreSQL's rules.
Independent SPI commands remain available after the error is caught, and the
cursor can still be disposed. No cursor cleanup is performed by a .NET finalizer.

## Errors and transactions

Where PostgreSQL permits subtransactions, each call runs in an internal
subtransaction. Success retains its changes in the
enclosing transaction; a PostgreSQL error rolls back that call before throwing
`PgException`. Managed code can catch the exception and execute another SPI call:

```csharp
try
{
    Spi.Execute("INSERT INTO unique_values (value) VALUES ($1)", SpiParameter.Create(42));
}
catch (PgException exception) when (exception.SqlState == "23505")
{
    // Handle the duplicate value.
}
```

Managed `finally` blocks run normally for PostgreSQL errors and cancellation.
SPI calls are confined to the active backend thread; worker-thread calls throw
`InvalidOperationException` before accessing PostgreSQL state.

Recovery has a cost for writes. Each call that writes in its subtransaction
receives its own subtransaction ID. Once a transaction has more than 64 of them,
PostgreSQL's per-backend cache overflows, and every concurrent snapshot must
consult `pg_subtrans` until that transaction ends. Reads that write nothing do
not consume IDs. For bulk changes, prefer one set-based statement, such as
`INSERT ... SELECT` or a statement over an array parameter, instead of one call
per row.

When per-row statements are unavoidable, run them in an atomic scope. Its
statements share the scope's single subtransaction, as SPI statements do in
pgrx, so 200 writes consume one subtransaction ID instead of 200:

```csharp
PgTransaction.RunInSubtransaction(() =>
{
    foreach (int value in values)
    {
        Spi.Execute("INSERT INTO items (value) VALUES ($1)", SpiParameter.Create(value));
    }
}, PgSubtransactionMode.Atomic);
```

Inside an atomic scope, a PostgreSQL error cannot be recovered: a later backend
call in the scope rethrows it, and the whole scope rolls back before the
`PgException` reaches the caller. Catch it outside the callback. A
`PgSubtransactionMode.Recoverable` scope nested inside restores per-statement
recovery for its own statements.

PostgreSQL 13–16 prohibit subtransactions during parallel execution, including
in parallel workers. Successful SPI queries, scoped sessions, prepared statements
and cursors remain available subject to PostgreSQL's parallel restrictions.
If a native operation fails in that execution mode, the current managed callback
must end. Catching its `PgException` does not recover the PostgreSQL operation:
further SQL and raw native calls return the original failure, and Ankus reports
that failure after managed `finally` blocks finish, even if the method returns a
value or throws a replacement exception. Explicit recovery scopes cannot create
a subtransaction there either.

Dispose owned plans, cursors and memory in `finally` or `using` scopes. Resource
release and memory-context restoration remain available while ordinary backend
work is blocked. After PostgreSQL aborts the failed query, the leader can recover
through its surrounding transaction or savepoint and launch new workers.
PostgreSQL 17 and later retain per-call SPI recovery in parallel workers.
Raw native and memory operations require an explicit recovery scope on every
PostgreSQL version. A caught raw error still ends the callback unless that scope
has rolled back; see [recoverable work](/transaction-callbacks/#recoverable-work).

### Error diagnostics

`PgException` owns its diagnostic strings. They remain valid after recovery,
subsequent SPI calls, and the transaction ending. Available fields include:

| Properties | Meaning |
|---|---|
| `SqlState`, `Message`, `Detail`, `Hint` | SQLSTATE and primary/secondary diagnostics |
| `Context` | PostgreSQL execution context, including procedural and SQL frames |
| `SchemaName`, `TableName`, `ColumnName`, `DataTypeName`, `ConstraintName` | Server-supplied object names |
| `Position` | One-based character position in the client query; zero when absent |
| `InternalPosition`, `InternalQuery` | Character position and text of an internally executed query |
| `File`, `Line`, `Routine` | Original reporting source location |
| `DetailLog`, `Backtrace` | Server-only detail and native backtrace, when supplied |

Optional strings are null when absent; an explicitly empty diagnostic remains an
empty string. SPI typically reports parse positions through `InternalPosition`
and `InternalQuery`. Positions count PostgreSQL characters, not UTF-8 bytes or
UTF-16 code units.

Extension-authored errors can supply structured object/context fields:

```csharp
throw new PgException("22023", "The supplied value is invalid.", hint: "Use a positive value.")
{
    SchemaName = "app",
    TableName = "messages",
    ColumnName = "priority",
    Context = "Validating message priority",
};
```

Rethrowing a caught `PgException` preserves its source location and existing
context without duplicating PostgreSQL context callbacks. Diagnostic strings
are transported in full; if allocation or encoding fails during capture,
`DiagnosticsIncomplete` indicates a partial diagnostic and the primary message
has a bounded emergency fallback. Diagnostics use the same server-encoding
conversion rules as text values.
