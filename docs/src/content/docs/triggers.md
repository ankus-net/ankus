---
title: Triggers
description: Handle PostgreSQL row and statement triggers with owned OLD and NEW tuples.
---

Mark a synchronous static method with `[PgTrigger]`. Its single parameter is a
nonnull `PgTriggerContext`. Return `PgHeapTuple?` when the callback can skip a row,
or `PgHeapTuple` when it always returns a row.
Ankus generates a zero-argument SQL function returning `trigger`.
Use `[PgFunction]` alongside the marker for names, schemas, security, search paths,
and dependency identifiers.

```csharp
[PgTrigger]
[PgFunction(Id = "normalize-name")]
public static PgHeapTuple? NormalizeName(PgTriggerContext context)
{
    PgHeapTuple row = context.New
        ?? throw new InvalidOperationException("This trigger requires an INSERT or UPDATE row.");
    string? name = row.Get<string?>("name")?.Trim();
    if (string.IsNullOrEmpty(name))
    {
        return null;
    }

    row.Set("name", name);
    return row;
}
```

Attach the function to a table or view with ordinary SQL. Custom SQL can create
the trigger during extension installation and order it after its table and function:

```csharp
[assembly: PgSql("pet-table", "CREATE TABLE pets (name text NOT NULL);", Relocatable = true)]
[assembly: PgSql("pet-trigger", """
    CREATE TRIGGER normalize_pet BEFORE INSERT OR UPDATE ON pets
    FOR EACH ROW EXECUTE FUNCTION normalize_name();
    """, Requires = ["pet-table", "normalize-name"], Relocatable = true)]
```

See [custom SQL](/custom-sql/) for dependencies and schema relocation.

Use `PgFunction.Sql` to replace the trigger function's SQL declaration, including
`RETURNS trigger`, or `GenerateSql = false` to keep its native callback without
installing the function. These controls do not attach a table trigger. See
[function SQL controls](/custom-sql/#replace-function-sql).

`Rows`, `SetMode`, and ordinary SQL parameter/result binding attributes do not
apply to trigger callbacks. Trigger functions cannot be invoked with `SELECT`.

## Context and row ownership

`Operation` is `Insert`, `Update`, `Delete`, or `Truncate`; `Timing` is `Before`,
`After`, or `InsteadOf`; `Level` is `Row` or `Statement`.
`Event` preserves PostgreSQL's event bits, including deferred-constraint flags.

| Trigger event | `Old` | `New` |
|---|---|---|
| Row INSERT | null | inserted row |
| Row UPDATE | previous row | proposed row |
| Row DELETE | deleted row | null |
| Any statement trigger | null | null |

`Name`, `TriggerOid`, `RelationOid`, `TableName`, and `TableSchema` identify the
current trigger and relation. `Descriptor` describes the relation's physical
columns. `Arguments` contains the exact strings from `EXECUTE FUNCTION f('arg', ...)`.
Arguments are trigger configuration strings, not SQL function parameters.

`UpdateOfColumns` lists the zero-based ordinals named by `UPDATE OF`, and is empty
for a trigger that fires for any column. For an UPDATE, `UpdatedColumns` lists the
columns the statement assigns, which can include a column set to its current value;
it is null for other operations. A constraint trigger reports its `ConstraintOid`,
`IsDeferrable` and `IsInitiallyDeferred`; an ordinary trigger has a zero OID.

Rows, descriptors, strings, and arguments are managed copies. They remain valid
after the callback and after further SPI calls. Use the [composite value APIs](/composites/)
to read cells by name or zero-based physical ordinal, change values, and clone rows.
`Clone()` copies cell slots and shares nested reference values.
Row fields use Ankus's composite conversions. A column whose type has no managed
conversion, such as `tsvector`, is copied exactly: read it with a
[`PgDatumType`](/raw-values/#reusable-scalar-mappings) mapping or as a
`Get<PgDatum>` raw value, and a returned row keeps its original value.

## Return behavior

| Timing and operation | Return a tuple | Return null |
|---|---|---|
| BEFORE or INSTEAD OF INSERT/UPDATE row | Use the returned row | Skip the row |
| BEFORE or INSTEAD OF DELETE row | Continue deletion; tuple contents are ignored | Skip deletion |
| BEFORE statement | PostgreSQL error `39P01` | Continue |
| AFTER row or statement | Return value ignored | Return value ignored |

Returning OLD from a BEFORE UPDATE still performs an update and fires applicable
AFTER triggers. A nonnull tuple whose fields are all NULL remains a row;
returning null skips it. For an INSTEAD OF trigger, perform the underlying table
changes through SPI and return a tuple to report the handled row.

A replacement INSERT/UPDATE row must match the triggering relation's row type
and physical layout. PostgreSQL enforces field domains and type modifiers;
table constraints are checked by the operation after BEFORE triggers finish.
Exceptions thrown by the callback still abort the statement even when its return
value would have been ignored.

## Generated and dropped columns

A stored generated column in NEW is unavailable during BEFORE triggers because
PostgreSQL computes it afterward. Virtual generated columns, supported by
PostgreSQL 18+, have no physical value in OLD or NEW trigger tuples.
In `Old.Descriptor` or `New.Descriptor`, those attributes have
`IsUnavailable = true`; `context.Descriptor` does not mark them. Reading or
assigning these cells throws `InvalidOperationException`. This differs from SQL
NULL.

Change ordinary source columns and return the row. PostgreSQL preserves or
recomputes generated values at the appropriate stage. An unavailable trigger
row cannot be sent as an ordinary composite through SPI or a scalar function.
Dropped columns retain their physical slots and remain NULL.

## Transition tables and SPI

`OldTransitionTableName` and `NewTransitionTableName` expose the names requested
by `REFERENCING OLD TABLE AS ... NEW TABLE AS ...`. PostgreSQL determines which
trigger declarations allow transition tables.

Every SPI connection opened during a trigger callback can query its transition
tables. For example, an AFTER INSERT statement trigger can count its inserted rows:

```csharp
string name = context.NewTransitionTableName
    ?? throw new InvalidOperationException("A NEW transition table is required.");
long count = Spi.ExecuteScalar<long>($"SELECT count(*) FROM {Spi.QuoteIdentifier(name)}");
```

The quoting helper supplies the complete identifier fragment, so ANKUS044
permits this raw command. Bind data values through `Spi.Sql` or positional
parameters rather than formatting them into the command text.

Stateless queries, `Spi.Connect` sessions, prepared statements, and cursors share
this access. Nested triggers receive their own transition tables; the enclosing
callback's tables remain available when the nested call returns.

Transition tables belong to the current trigger invocation. Cursors opened in
an SPI query environment containing transition tables close when that invocation
ends, including detached cursors. Fetched managed rows remain valid. A retained
plan resolves transition tables against the active invocation when executed; it
does not retain their rows. See [execution and lifetime](/reference/execution/).

Managed exceptions and native SPI errors use the same guarded error boundary as
ordinary functions. Catching a `PgException` from SPI permits subsequent queries
in the callback after the failed operation has rolled back.

The public `samples/Ankus.Examples.Triggers` extension normalizes pet names,
skips blank rows, and demonstrates trigger arguments and ordered installation SQL.
The [notify sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.Notify)
uses AFTER row triggers to send cache-invalidation notifications, either for every
changed row or once per changed category at commit.

## Declaration diagnostics

Row and event callbacks must run synchronously on the invoking PostgreSQL
backend thread. Each declaration error identifies its own contract and
highlights the relevant modifier, type, parameter, attribute or option.
Row callbacks return `PgHeapTuple` or `PgHeapTuple?`; event callbacks return
`void`. Their required context types are `PgTriggerContext` and
`PgEventTriggerContext`, respectively.

| Diagnostic | Correction |
| --- | --- |
| `ANKUS208` | Declare a static callback. |
| `ANKUS209`, `ANKUS228`, `ANKUS229` | Perform the work synchronously; callbacks cannot use `async`, return `Task`/`ValueTask`, or return `IAsyncEnumerable`. |
| `ANKUS210` | Remove callback method type parameters. |
| `ANKUS211` | Provide a concrete method implementation. |
| `ANKUS212`, `ANKUS214` | Return the callback's required result by value. |
| `ANKUS213`, `ANKUS223` | Make the method and its enclosing types accessible to generated code: public, internal, or protected internal. |
| `ANKUS215` | Declare exactly one required context parameter. |
| `ANKUS216`, `ANKUS217`, `ANKUS218` | Pass that context by value without `ref`, `in`, `out`, `params`, a default, or `Optional`. |
| `ANKUS219`, `ANKUS220` | Use the correct, nonnullable context type. |
| `ANKUS221`, `ANKUS222` | Declare callbacks in non-generic types that are not file-local. |
| `ANKUS224` | Choose one row/event callback or operator/cast export role. |
| `ANKUS225` | Remove SQL value binding attributes from the context and result. |
| `ANKUS226`, `ANKUS227` | Remove `Rows` and `SetMode`, which describe ordinary set-returning functions. |
| `ANKUS277` | Remove `[Conditional]`, so every invocation runs the callback. |

Editors offer **Make static** for `ANKUS208`, **Make internal** for `ANKUS213`,
`ANKUS222` and `ANKUS223`, **Remove** for `ANKUS225` and `ANKUS277`, and **Pass
by value** for an `in` or `ref readonly` context (`ANKUS216`).

Invalid callback declarations do not prevent unrelated valid functions from
receiving their generated dispatchers. Shared `[PgFunction]` names, schemas,
security and dependency options retain their ordinary declaration diagnostics.
