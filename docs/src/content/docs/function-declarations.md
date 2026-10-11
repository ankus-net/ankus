---
title: Function declarations
description: Name arguments, provide defaults, choose schemas, and control PostgreSQL function declarations.
---

Function options below configure generated SQL. Use `PgFunction.Sql` for a
complete replacement string, or `GenerateSql = false` to retain native entry
points without installing the function. Replacement text owns its SQL options
and must preserve the managed wrapper's argument, result and NULL contracts.
See [custom SQL](/custom-sql/#replace-function-sql) for placeholders, dependencies
and relocation.

## Function names

Ankus converts the C# method name to a snake-case SQL name. Set
`[PgFunction(Name = "chosen_name")]` to choose it explicitly. A function name
must contain 1–63 lowercase ASCII letters, digits or underscores, starting with
a letter or underscore. Invalid names produce `ANKUS002`.

Overloads must have distinct SQL input signatures within their schema. Different
C# types can map to the same SQL type, and injected contexts do not distinguish
SQL overloads. Rename the function or change its SQL input types when `ANKUS207`
reports a duplicate signature. A different return type alone does not resolve
the conflict.

## SQL nullability

Enable nullable annotations with `<Nullable>enable</Nullable>` in your project,
or `#nullable enable annotations` around your SQL declarations. A reference such
as `string` requires a value; `string?` accepts SQL NULL. Array elements, set
columns and aggregate callback values follow the same rule.

Disabled annotations leave reference nullability ambiguous. Ankus reports
`ANKUS024` at the affected declaration instead of inferring a required SQL value.
Explicit `NullInput` settings do not replace the managed value contract. Value
types and injected contexts do not need reference annotations.

## Named arguments and defaults

C# parameter names become snake-case SQL argument names. Optional constants
become PostgreSQL defaults, so callers can omit arguments or name them:

```csharp
[PgFunction]
public static string Greet(string firstName = "world", int repeatCount = 1)
    => string.Join(" ", Enumerable.Repeat($"Hello, {firstName}!", repeatCount));
```

```sql
SELECT greet();
SELECT greet(repeat_count => 2, first_name => 'Ada');
```

Use `PgParameter` to override a name or supply a SQL expression:

```csharp
[PgFunction]
public static PgDate ReportDate(
    [PgParameter(Name = "as_of", Default = "current_date")] PgDate date) => date;
```

PostgreSQL evaluates `current_date` when the function is called. A SQL default
does not make the argument optional in direct C# calls. When both are present,
`PgParameter.Default` overrides the C# default for SQL calls only.

Every argument following a defaulted argument must also have a default. Ankus
reports `ANKUS062` on an argument missing that default. Other invalid options
have [specific declaration diagnostics](#declaration-diagnostics).
PostgreSQL validates SQL expressions during installation. Expressions are
trusted extension source, just like handwritten installation SQL. An expression
may end with a `--` comment: Ankus then closes its `DEFAULT (...)` clause on the
next line, so the comment cannot hide the rest of the generated declaration.

Constants retain decimal scale, floating-point signed zero and special values,
escaped text, and integer bounds. Supported value-type `default` arguments use
their actual value: `default(PgDate)` is 2000-01-01, while `default(DateOnly)` is
0001-01-01. Nullable defaults become SQL NULL.

Variadic arguments can have an explicit SQL default too:

```csharp
[PgFunction]
public static int Total(
    [PgParameter(Default = "ARRAY[]::integer[]")] params int[] values) => values.Sum();
```

This allows both `total()` and `total(1, 2, 3)`. See [arrays](/arrays/) for
variadic calls without a default.

## Injected memory contexts

Add a `PgMemoryContext` parameter to receive the callable's native allocation
context. It adds no SQL argument:

```csharp
[PgFunction]
public static int CopyValue(PgMemoryContext context, int value)
{
    using PgAllocation storage = context.Allocate<int>(1);
    storage.Write(value);
    return storage.Read<int>();
}
```

Call this as `SELECT copy_value(42)`. Context parameters can appear before,
between, or after SQL parameters, and multiple context parameters are allowed.
SQL names, defaults, strictness, overload identity, and the 100-argument limit
count only SQL parameters. Operators and casts follow the same rule.

The injected handle is borrowed and always non-null. Nullable annotations and
optional null defaults affect direct C# calls only. A `PgParameter` attribute on
the context reports `ANKUS056` because there is no corresponding SQL name or
default. Context arrays, context results, and `ref`, `in`, or `out` parameters
are unsupported.

Scalar functions receive the current native context; set functions receive
their multi-call context. The handle preserves that owner across temporary
context switches. See [memory contexts](/memory-contexts/) and
[set lifetime](/sets-and-tables/#iterator-lifetime-and-errors) for its lifetime.

## Function-call context

Add a `PgFunctionContext` parameter to inspect the PostgreSQL call:

```csharp
[PgFunction]
public static uint InputCollation(string text, PgFunctionContext call)
    => call.CollationOid;
```

```sql
SELECT input_collation('hello' COLLATE "C");
```

The context adds no SQL argument. It exposes `FunctionOid`, `ResultTypeOid`,
`CollationOid`, and zero-based `Arguments`. Each argument is a `PgDatum` with its
actual PostgreSQL type, SQL NULL flag, and conversion methods such as `Read<int>()`.
A collation OID of zero means no collation applies.

Metadata remains readable after the call. Raw arguments are independent copies
owned by the scalar call's memory context or the iterator's multi-call context.
They survive nested calls and iterator yields; native access fails after their
owner is reclaimed. Use `argument.CopyTo(context)` for a longer native lifetime,
or `Read<T>()` for an independent managed value.

Context parameters can appear anywhere among the SQL parameters, including in
operators and casts. Repeated `PgFunctionContext` parameters receive the same
snapshot. As with injected memory contexts, SQL defaults, names, and strictness
apply only to SQL arguments.

## Cached function state

Use `GetOrCreateState` to reuse an object across calls from the same PostgreSQL
expression:

```csharp
public sealed class Counter
{
    public int Value { get; set; }
}

[PgFunction]
public static int CountCalls(int value, PgFunctionContext call)
{
    Counter counter = call.GetOrCreateState(static () => new Counter());
    return ++counter.Value;
}
```

```sql
SELECT count_calls(n) FROM generate_series(1, 3) n; -- 1, 2, 3
```

Each expression has its own state. PostgreSQL usually releases it at query end;
a cursor keeps it between fetches. A new query starts fresh. Set functions can
also keep state across iterator instances at the same call site.

The first successful factory result is cached, including null. A failed factory
can be retried. Use the same state type at each call site. Lookup requires the
backend thread, and recursive initialization of the same state is rejected.

If the state implements `IDisposable`, Ankus disposes it when PostgreSQL releases
its owner. Do not dispose the shared state yourself or use it afterward. For
native data kept in state, allocate in `call.StateMemoryContext` or copy a raw
argument with `argument.CopyTo(call.StateMemoryContext)`; individual arguments
can expire sooner than the cache.

## Schemas

Without a schema declaration, functions use the schema selected by
`CREATE EXTENSION`. Such an extension can be relocated with
`ALTER EXTENSION ... SET SCHEMA`.

Apply `PgSchema` to a class to create an extension-owned schema and place its
functions there:

```csharp
[PgSchema("reporting")]
public static class Reports
{
    [PgFunction]
    public static int Double(int value) => checked(value * 2);
}
```

Call it as `reporting.double(21)`. Nested classes inherit the nearest
`PgSchema` declaration. An empty attributed class can declare a schema without
any functions.

Schema creation happens before function declarations. PostgreSQL records the
new schema as an extension member and removes it with `DROP EXTENSION`.
Installation fails if an unrelated object already owns that schema name.
To use an existing schema without adopting it, set `Create = false`:

```csharp
[PgSchema("public", Create = false)]
public static class SharedFunctions
{
    [PgFunction]
    public static int Increment(int value) => checked(value + 1);
}
```

A function's `Schema` option overrides its containing class and refers to an
existing schema. Declare that schema separately with `PgSchema` if the extension
should create it. Schema and argument identifiers are quoted exactly and must
fit PostgreSQL's 63-byte UTF-8 limit.

Any fixed schema makes the extension non-relocatable. Ankus records this in
the generated control file, including when all fixed schemas already exist.

The [schemas sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.Schemas)
ports pgrx's `schemas` example. It places functions and types in the
installation schema, an extension-owned schema, `public` and `pg_catalog`.

Use `Id` and `Requires` to order schemas and functions alongside
[custom installation SQL](/custom-sql/). A function default that calls a
SQL-created routine should require the block that creates it.

## Execution options

Declare PostgreSQL's planner and execution contracts on `PgFunction`:

```csharp
[PgFunction(Volatility = PgVolatility.Immutable,
    ParallelSafety = PgParallelSafety.Safe, Cost = 2.5)]
public static int Maximum(int left, int right) => Math.Max(left, right);
```

| Option | Default | Meaning |
| --- | --- | --- |
| `Volatility` | `Volatile` | `Stable` promises statement-stable results; `Immutable` promises equal results for equal arguments forever. Both prohibit database writes. |
| `ParallelSafety` | `Unsafe` | `Restricted` runs in the parallel leader; `Safe` also permits parallel workers. |
| `NullInput` | `Inferred` | Infers strictness from SQL parameter nullability. `Strict` skips any NULL call; `CalledOnNull` requires all SQL parameters to be nullable. |
| `SecurityDefiner` | `false` | Uses the function owner's privileges when true. Otherwise uses the caller's. |
| `Leakproof` | `false` | Claims the function reveals no argument information except through its result. Installation requires a superuser. |
| `Cost` | `1` | Positive, finite planner cost in `cpu_operator_cost` units. |
| `CreateOrReplace` | `false` | Emits `CREATE OR REPLACE FUNCTION`, retaining compatible function identities and dependencies. |
| `SearchPath` | `null` | An ordered list of schema names scoped to the call. `PgSearchPath.ExtensionSchema` selects the extension's installation schema. An empty array clears the path; null preserves the caller's setting. |
| `SupportFunction` | `null` | An existing planner support routine, optionally schema-qualified. PostgreSQL validates its signature during installation. |
| `Rows` | `1000` | Positive finite row estimate for an `IEnumerable<T>` return. |
| `SetMode` | `Auto` | Prefer one row per call, or require `ValuePerCall`/`Materialize` for a set return. |

These options are promises to PostgreSQL. Choose them to match the method's
behavior; the generator does not infer purity, privilege requirements, or
parallel safety from its body.

For owner-privileged functions, pin name resolution to trusted schemas and put
`pg_temp` last:

```csharp
[PgFunction(SecurityDefiner = true,
    SearchPath = ["pg_catalog", "reporting", "pg_temp"])]
public static long ReportCount()
    => Spi.ExecuteScalar<long>("SELECT count(*) FROM reporting.reports");
```

The owner's privileges and function-local search path end when the call
returns or throws. Native error guards and managed exception unwinding apply
to every execution mode.

Use `PgSearchPath.ExtensionSchema` when the path should follow the schema chosen
by `CREATE EXTENSION ... WITH SCHEMA`, rather than a schema name known at build
time:

```csharp
[PgFunction(SearchPath = ["pg_catalog", PgSearchPath.ExtensionSchema, "pg_temp"])]
public static int ReadAnswer()
    => Spi.ExecuteScalar<int>("SELECT answer FROM extension_values");
```

The constant is PostgreSQL's reserved `@extschema@` token. PostgreSQL replaces it
with the quoted installation schema during extension installation. Ordinary
entries remain literal schema names. This generated path makes the extension
non-relocatable: changing the schema afterward would leave stored function paths
pointing at the original schema. Ankus rejects a conflicting `relocatable = true`
control setting. A null path or ordinary schema names do not impose this rule.
Selected SQL needs the actual installation schema; see
[selecting declarations](/getting-started/publishing/#select-declarations).

`PgOperator` and `PgCast` also expose static methods as functions. Add `PgFunction`
alongside them to select the options described here. See [operators and casts](/operators-and-casts/).

`IEnumerable<T>` returns a set; named tuple elements declare TABLE output columns.
See [sets and tables](/sets-and-tables/) for column names, NULL rows, execution modes,
iterator disposal and cancellation.

## Planner support functions

Use `PgSupportFunction` to select another generated method as the planner support
routine. Ankus resolves its configured SQL name and schema, validates its
`internal → internal` SQL signature, and creates it before the consuming function.
The following example supplies a row estimate for a set-returning function:

```csharp
using Ankus;
using Ankus.Postgres;

public static class Numbers
{
    [PgFunction(Rows = 1000)]
    [PgSupportFunction(typeof(Numbers), nameof(Estimate))]
    public static IEnumerable<int> Values() => [1, 2, 3];

    [PgFunction]
    public static unsafe PgInternal Estimate(PgInternal request)
    {
        PgMemoryContext owner = PgMemoryContext.Current;
        void* address = (void*)request.Datum.DangerousGetBits();
        PgNodeReference<Node> node = PgNodes.Borrow(owner.DangerousBorrow<Node>(address)!);
        if (node.Tag != (uint)NodeTag.T_SupportRequestRows)
        {
            return new PgInternal(PgDatum.DangerousCreate(0, (uint)PgBuiltInOid.InternalOid, owner));
        }

        PgNodeReference<SupportRequestRows> rows =
            PgNodes.Borrow(owner.DangerousBorrow<SupportRequestRows>(address)!);
        SupportRequestRows changed = rows.Value;
        changed.rows = 37;
        rows.Value = changed;
        return request;
    }
}
```

PostgreSQL owns the request. Inspect its tag before reading the corresponding
generated structure, and keep these borrows within the callback. Return the
request after handling it. For an unsupported request, return a **present zero
pointer**, as above. A null `PgInternal` represents SQL NULL and is not PostgreSQL's
“request unsupported” response. New PostgreSQL versions can add request kinds.

The estimate changes planning, not the function's returned values. Other request
kinds have their own PostgreSQL contracts. Installation with a support routine
requires a superuser. The usual managed/native error boundary also applies when
PostgreSQL invokes the method during planning.

`ParameterTypes` selects an overloaded method using exact managed types, as with
[typed SQL dependencies](/custom-sql/#reference-managed-declarations). Injected
contexts do not count as SQL arguments. The selected function must accept one
nonvariadic SQL `internal` argument and return scalar SQL `internal`; `PgInternal`
and explicit raw internal mappings can express this contract. Trigger functions,
operator and cast backing functions, and aggregate helpers can also declare a
support routine. When one method generates several SQL functions, such as an
inherited helper used by several aggregates, each of them receives the routine.

The support routine itself must be an ordinary generated function. An aggregate
helper requires an aggregate invocation, which PostgreSQL does not provide during
planning, even when its SQL argument and result types are both `internal`.

Planner-support diagnostics identify the exact failed contract and point at the
value to correct. `ANKUS470` requires a method name. The shared `ANKUS471`,
`ANKUS473`–`ANKUS476`, `ANKUS480` and `ANKUS486`–`ANKUS487` diagnostics identify
invalid or ambiguous targets. `ANKUS482` requires the attributed method to
generate a function, `ANKUS483` rejects conflicting managed and external
selectors, `ANKUS484` validates the SQL signature, and `ANKUS485` rejects
aggregate helpers. Dependency cycles remain graph errors.
Disabled and replaced SQL retain their support prerequisites; authored
replacement SQL must include the desired `SUPPORT` clause itself.

Keep `PgFunction.SupportFunction = "pg_catalog.textlike_support"` for an existing
external SQL routine. PostgreSQL validates its signature at installation. Choose
either the external SQL name or `PgSupportFunction` for a declaration.

## Named SQL type bindings

`PgSqlType` binds a `PgDatum` value to an exact PostgreSQL type.
`PgCompositeType` binds `PgHeapTuple` or its array element to a named composite.
On a TABLE result, use `Column` to select one output. `Element` is reserved for
typed aggregate tuple inputs.

Binding diagnostics identify the exact contract:

| Diagnostic | Required correction |
| --- | --- |
| ANKUS401 | Use valid type and schema identifiers. |
| ANKUS402 | Replace `Element` with `Column` on a TABLE result. |
| ANKUS403 | Select an existing TABLE output name. |
| ANKUS404 | Set `Column` when more than one TABLE output could match. |
| ANKUS405 | Apply `PgSqlType` only to `PgDatum`. |
| ANKUS406 | Apply `PgCompositeType` only to `PgHeapTuple` or its array element. |
| ANKUS407 | Retain one binding for a TABLE output. |
| ANKUS408 | Add `PgSqlType` for every raw TABLE output. |
| ANKUS409 | Retain one binding on a parameter or scalar result. |
| ANKUS410 | Add `PgSqlType` to an unbound `PgDatum` parameter or scalar result. |
| ANKUS411 | Use `Column` only on TABLE results. |
| ANKUS412 | Use `Element` only on typed aggregate tuple inputs. |

## Declaration diagnostics

For `ANKUS056`, supported C# editors offer **Remove SQL metadata from injected
context**. This removes `PgParameter` from injected `PgFunctionContext` and
`PgMemoryContext` parameters while retaining ordinary SQL inputs, their metadata,
and unrelated attributes. The correction also supports the editor's **Fix all**
action. For `ANKUS053`, **Remove** deletes `Rows` or `SetMode` from a function
without a set result. It also removes `PgColumnNames` from a function without a
set result (`ANKUS396`) and a `Column` or `Element` selector where none applies
(`ANKUS411`, `ANKUS412`).

**Make synchronous** removes `async` from an entry that awaits nothing and returns
the value or sequence it already produces: `Task<T>` becomes `T`, `Task` becomes
`void`, and `IAsyncEnumerable<T>` becomes `IEnumerable<T>` (`ANKUS030`–`ANKUS032`).
**Return by value** removes `ref` from a result (`ANKUS037`). For `ANKUS057`,
**Remove** deletes `Element`, and **Declare with `params`** replaces
`Variadic = true` on a final array parameter. These corrections leave the source
unchanged when a caller would no longer compile, such as one that awaits the
entry.

Declaration errors point to the option value, result type or parameter that
needs correction. Schema errors point to the schema argument, including named
constructor arguments.

| Diagnostic | Required correction |
| --- | --- |
| ANKUS045 | Use a defined member of the option's volatility, parallel-safety or NULL-policy enum. |
| ANKUS046 | Set `Cost` to a positive, finite value representable as PostgreSQL's single-precision planner cost. |
| ANKUS047 | Pair a SQL `internal` result with a SQL `internal` input. |
| ANKUS048 | Supply a polymorphic SQL input that determines the polymorphic result type. |
| ANKUS049 | Make every SQL parameter nullable when using `CalledOnNull`, or choose a compatible NULL policy. |
| ANKUS050 | Use a nonempty schema identifier containing at most 63 UTF-8 bytes, valid Unicode and no zero characters. |
| ANKUS051 | Set `Rows` to a positive, finite value representable as PostgreSQL's single-precision row estimate. |
| ANKUS052 | Choose `Auto`, `ValuePerCall` or `Materialize` for `SetMode`. |
| ANKUS053 | Use `Rows` and `SetMode` only on set-returning functions. |
| ANKUS054 | Specify one support-function name, optionally preceded by one schema name. |
| ANKUS055 | Give each `SearchPath` element a valid schema identifier. |
| ANKUS056 | Remove `PgParameter` from injected contexts, which do not consume SQL arguments. |
| ANKUS057 | Use one ordinary `PgParameter` attribute. `Element` and `Variadic` options belong to typed aggregate arguments; ordinary functions use C# `params`. |
| ANKUS058 | Give the SQL parameter a valid identifier of at most 63 UTF-8 bytes. |
| ANKUS059 | Give each SQL parameter a distinct name, including names produced by snake-case conversion. |
| ANKUS060 | Supply one complete SQL default expression: valid Unicode without zero characters, balanced parentheses, closed strings, quoted names, dollar quotes and block comments, and no semicolon outside them. Both `standard_conforming_strings` settings must read it the same way. |
| ANKUS061 | Supply `PgParameter.Default` when the C# optional value has no exact SQL translation. |
| ANKUS062 | Give every input after a defaulted SQL argument its own SQL default. |
| ANKUS063 | Create schemas outside PostgreSQL's reserved `pg_` namespace. As in PostgreSQL, only the exact lowercase prefix is reserved; quoted names such as `PG_Stage` are allowed. Use `Create = false` when referencing an existing schema. |
