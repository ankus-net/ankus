---
title: Aggregates
description: Define PostgreSQL aggregates with typed transitions, owned managed state, moving windows, and parallel transport.
---

Mark a class or struct with `[PgAggregate]` and implement
`IPgAggregate<TState, TArgs>`. The compiler checks its static `Transition`
method. PostgreSQL manages grouping, filtering, NULL handling, and invocation
order.

```csharp
[PgAggregate(Name = "integer_total", InitialCondition = "0")]
public sealed class IntegerTotal : IPgAggregate<long, int>
{
    public static long Transition(PgAggregateContext context, long state, int value)
        => checked(state + value);
}
```

This defines `integer_total(integer)` with a `bigint` state. Without a final
method, the state is also the result. Use `ValueTuple` for `TArgs` to define a
zero-argument aggregate invoked as `count_rows(*)`. Tuple elements become
separate SQL inputs; arrays remain single SQL values.

Use SQL normally:

```sql
SELECT category, integer_total(amount) FILTER (WHERE amount > 0)
FROM entries
GROUP BY category;
```

## Compiler-checked aggregate contracts

Implement `IPgAggregate<TState, TArgs>` on an attributed class or struct to have
the C# compiler check its transition contract. `TArgs` is a scalar input, a tuple
whose elements become separate SQL inputs, or `ValueTuple` for no inputs.
Arrays remain single SQL values. Include SQL nullability in the interface's type
arguments; it determines the generated SQL boundary even when an implementation
always returns a present value.

Ankus also checks the implementation's nullable contract. `ANKUS028` rejects
an input that requires a present value when its interface accepts NULL, or a
result that might be NULL when its interface promises a present value. These
are errors even when C# reports the interface mismatch as a warning. Arrays,
tuple elements and owned-state payload annotations are checked too.

An implementation can accept broader inputs or guarantee a non-null result.
These safe differences preserve the interface's SQL nullability and `STRICT`
policy. Standard `AllowNull`, `DisallowNull`, `MaybeNull`, `NotNull` and
`NotNullIfNotNull` annotations participate in this check; a conditional return
promise requires its referenced interface input to be non-null.

```csharp
[PgAggregate(Name = "weighted_total", InitialCondition = "0")]
public sealed class WeightedTotal : IPgAggregate<long, (int? Amount, int? Weight)>
{
    public static long Transition(
        PgAggregateContext context, long state, (int? Amount, int? Weight) arguments)
        => checked(state + (arguments.Amount ?? 0) * (arguments.Weight ?? 1));
}
```

Call this as `weighted_total(amount, weight)`. Every capability receives
`PgAggregateContext` first; it is never an SQL input. Explicit static interface
implementations, inherited implementations and default interface implementations
are supported. Containers can also be structs or `ref struct` types; no instance
is created. Ankus calls the selected interface member directly, without reflection.

Add the capabilities the aggregate needs:

| Interface | Supplied callbacks |
|---|---|
| `IPgFinalizingAggregate<TState, TDirect, TResult>` | `Final` |
| `IPgCombinableAggregate<TState>` | `Combine` |
| `IPgSerializableAggregate<TState>` | `Serialize` and `Deserialize` |
| `IPgMovingAggregate<TState, TArgs>` | `MovingTransition` and `MovingInverse` |
| `IPgMovingFinalizingAggregate<TState, TDirect, TResult>` | `MovingFinal` |

Use `ValueTuple` for `TDirect` when there are no direct arguments. Ordered-set
aggregates can use a scalar or tuple of direct arguments. Direct and aggregated
SQL input names must be distinct; choose different tuple element names or set
`PgParameter.Name`. `FinalExtra` and
`MovingFinalExtra` still provide PostgreSQL's dummy input slots for type
resolution; those slots are generated automatically and are not passed to the
typed final method. State ownership, parallel transport, inverse restart and
final modification rules are the same as for the methods described below.

A typed aggregate has exactly one transition contract and at most one of each
optional capability. Implement the corresponding interface when adding a
callback. Ankus reports `ANKUS029` when an attributed container does not implement
`IPgAggregate<TState, TArgs>`. Callback selection uses the declared interfaces;
ordinary helper methods do not declare additional aggregate capabilities.
Visible inherited methods with an aggregate role name require the corresponding
capability interface as well. Private base helpers that the aggregate cannot
access remain ordinary helpers.

For `ANKUS111` on `Combine`, the editor offers **Add missing typed aggregate
combine interfaces** when the existing public static method matches the
aggregate's state, return type and nullability. The action derives `TState`
from `IPgAggregate<TState, TArgs>` and adds `IPgCombinableAggregate<TState>`.
For a shared inherited callback, it updates each compatible attributed aggregate
in the project. Existing callback bodies and their metadata are preserved.

Editors also remove what an aggregate cannot use: `SortOperator` with more than
one input (`ANKUS104`), an initial condition for internal state (`ANKUS106`),
a C# default on a callback parameter (`ANKUS113`), attributes on the invocation
context or an empty argument group (`ANKUS114`, `ANKUS119`), and `Default` or
`Element` on a scalar input (`ANKUS127`, `ANKUS128`).

Apply parameter metadata to a tuple group with `Element` selecting its exact
C# element name. Each SQL input retains its own name, numeric constraint or
explicit raw/composite binding:

```csharp
public static decimal Transition(
    PgAggregateContext context, decimal state,
    [PgParameter(Element = "Price", Name = "unit_price")]
    [PgNumericPrecision(5, 2, Element = "Price")]
    [PgNumericPrecision(6, 3, Element = "Fee")]
    (decimal Price, decimal Fee) arguments)
    => state + arguments.Price + arguments.Fee;
```

`PgSqlType` and `PgCompositeType` also accept `Element`. Set
`PgParameter(Element = "Values", Variadic = true)` on a trailing array element
to collect variadic inputs. Omit `Element` for a scalar argument group. Aggregate
inputs do not accept SQL defaults.

## Support methods and declaration options

Every callback receives `PgAggregateContext` first, followed by the interface's
state and argument group. Scalar and tuple argument groups retain their declared
SQL types; mark a trailing array input variadic with
`PgParameter.Variadic` or a scalar array group's `params` parameter.

| Method | Managed parameters after the context | Result |
|---|---|---|
| `Transition` | State, aggregated inputs | State |
| `Final` | State, direct argument group | SQL result |
| `Combine` | Destination state, partial state | Destination state |
| `Serialize` | Managed state | `byte[]`, or null for SQL NULL |
| `Deserialize` | `byte[]` | New managed state |
| `MovingTransition` | Moving state, aggregated inputs | Moving state |
| `MovingInverse` | Moving state, departing inputs | Moving state or NULL to restart |
| `MovingFinal` | Moving state, direct argument group | Same SQL result type as ordinary execution |

All callbacks implement synchronous static interface members. Add `[PgFunction]` to a
support method for its SQL name, schema, volatility, parallel safety, search
path, NULL policy, or other ordinary function settings. Support functions are
aggregate entry points; invoke them through an aggregate in SQL. Their C# bodies
remain callable directly for managed tests.

`PgFunction.Sql` can replace a support function's SQL while retaining its native
callback. `GenerateSql = false` omits that helper's SQL, so ordered custom SQL
must provide it if the aggregate still references it. These options apply to the
helper independently; they do not suppress `CREATE AGGREGATE`. Shared helpers
are emitted once. See [function SQL controls](/custom-sql/#replace-function-sql).

The parent `PgAggregate.Sql` independently replaces `CREATE AGGREGATE`;
`PgAggregate.GenerateSql = false` omits only that statement. Support functions
remain generated with their own settings, so replacement SQL can use those
helpers in a compatible aggregate declaration. See
[declaration SQL controls](/custom-sql/#replace-other-declarations).

`PgAggregateAttribute` configures the aggregate's name, schema, dependency ID,
`Requires`/`Before` edges, `ParallelSafety`, `StateSize`, and `MovingStateSize`.
`SortOperator` names an ordering operator, such as `<` for a MIN-like aggregate,
so PostgreSQL can answer the aggregate from an index; it requires exactly one
input. `InitialCondition` and `MovingInitialCondition` are PostgreSQL input strings for
the declared state type. Omitted, empty text, and the string `NULL` are distinct.
The generator quotes strings and orders support functions before their aggregate.
Aggregate `Requires` dependencies also precede its support functions, so a custom
state type can be declared once as an aggregate dependency. Generated enum types
and declared schemas participate in this ordering automatically.
See [custom SQL](/custom-sql/) for dependencies on custom types and operators.

The inferred support-function NULL policy follows parameter nullability. A strict
transition skips any row with a NULL input. Without an initial condition,
PostgreSQL can seed a strict state from the first compatible input without
calling the transition for that row. Use nullable state and input parameters
when the method needs to observe NULLs or recover from a NULL state.

## Owned managed state

Use `PgAggregateState<T>` for state that is not an ordinary SQL datum. It maps to
PostgreSQL's `internal` type. The payload can be a class or value type; a nullable
wrapper represents SQL NULL. Return an ordinary SQL value from `Final`.

```csharp
[PgAggregate(Name = "collect_count")]
public sealed class CollectCount : IPgAggregate<PgAggregateState<List<int>>?, int?>,
    IPgFinalizingAggregate<PgAggregateState<List<int>>?, ValueTuple, int>
{
    public static PgAggregateState<List<int>> Transition(
        PgAggregateContext context, PgAggregateState<List<int>>? state, int? value)
    {
        state ??= new([]);
        if (value is { } number)
        {
            state.Value.Add(number);
        }

        return state;
    }

    public static int Final(PgAggregateContext context, PgAggregateState<List<int>>? state, ValueTuple arguments)
        => state?.Value.Count ?? 0;
}
```

Returned state is rooted until its owning PostgreSQL memory context resets.
That includes group completion, rescans, moving-window restarts, cancellation,
and errors. If the payload implements `IDisposable`, cleanup invalidates the
wrapper and releases its root before calling `Dispose` once. Ordinary managed
cleanup failures produce a PostgreSQL warning; other states still receive cleanup.
An unrecovered native error during ordinary state disposal requires rollback,
even when `Dispose` catches it. Secondary failures during transaction completion
produce warnings with their diagnostic fields, allowing the original error's
rollback to finish.
Query cancellation remains pending for the next interrupt boundary. Terminal
failures during irreversible cleanup require PostgreSQL `PANIC` after managed
unwinding, matching transaction-completion callbacks. During cleanup, SPI access
is limited to releasing owned plans and cursors.

Unsafe raw calls retain their native transaction and ownership preconditions;
use them only for operations valid during cleanup, such as releasing owned memory.

Finalization does not dispose state: PostgreSQL can call a final method repeatedly
or share state across several final methods. Retained wrappers reject payload
access after release, and attached wrappers belong to their backend thread.
Ordinary managed values copied from inputs use Ankus's owned datum representations.

Use `PgInternal` when the same state also passes through ordinary backend support
functions or native callbacks. `PgInternal.Create(value)` selects the aggregate
owner inside aggregate callbacks; `Get<T>()` retrieves the exact retained type.
Its payload is released at context reset or deletion. If its `Dispose` throws,
PostgreSQL reports an error and stops that context's cleanup until cleanup is
retried. The released payload cannot be read or passed again.

## Parallel aggregation

Set the aggregate's `ParallelSafety` and implement `Combine`. Managed internal
state also needs both `Serialize` and `Deserialize`. Serialize values into a
process-independent format; a managed or native pointer is not a portable state.
The generated deserializer has PostgreSQL's mandatory `(bytea, internal)` SQL
signature; its dummy `internal` argument is not passed to C#.

A deserialized state has a temporary owner. `Combine` must return state owned by
its destination context. When the destination is NULL, allocate a fresh wrapper
and copy the partial values. Returning the borrowed partial wrapper is rejected.
Choose an initial condition that is an identity for both transition and combine,
because PostgreSQL applies it to partial and combined states.

The `Ankus.Examples.Aggregates` sample's `IntegerAverage` implements checked sum
and count, a versioned binary transport, and a combine method that copies values
into its destination. It uses the typed capability interfaces and returns NULL
for empty and all-null input.

## Moving windows and final state modification

`MovingTransition` and `MovingInverse` maintain a window as rows enter and leave.
They can use a different state type from ordinary execution. Supply
`MovingFinal` when conversion from that state to the aggregate result is needed.
A NULL inverse result asks PostgreSQL to recompute the frame. A moving forward
transition must return a present state.

`FinalModify` and `MovingFinalModify` describe whether finalization is read-only,
shareable, or read/write. They affect state sharing and window eligibility;
declare the behavior the method actually implements. The average example reads
its state without consuming it and supports bounded moving frames.

`FinalExtra` and `MovingFinalExtra` add typed NULL input slots after the state and
direct arguments. They carry the argument types only, and they require a
non-strict final method.

## Polymorphic values

Use `PgAnyElement` to accept different PostgreSQL types, or `PgAnyArray` for
arrays. PostgreSQL resolves state and result types from the aggregate inputs:

```csharp
[PgAggregate]
public sealed class FirstValue : IPgAggregate<PgAnyElement, PgAnyElement>
{
    public static PgAnyElement Transition(PgAggregateContext context, PgAnyElement state, PgAnyElement value)
        => state;
}
```

This strict transition keeps the first non-NULL value. PostgreSQL seeds the
state from that row; empty and all-NULL inputs return NULL. Types, array bounds,
and values retain their PostgreSQL representation.

For managed state, copy retained inputs into `context.MemoryContext`. Inputs
otherwise expire when the support callback ends. `FinalExtra` lets PostgreSQL
resolve a polymorphic result when the state itself is `internal`:

```csharp
[PgAggregate(FinalExtra = true)]
public sealed class FirstStoredValue : IPgAggregate<PgAggregateState<PgAnyElement>?, PgAnyElement?>,
    IPgFinalizingAggregate<PgAggregateState<PgAnyElement>?, ValueTuple, PgAnyElement?>
{
    public static PgAggregateState<PgAnyElement>? Transition(
        PgAggregateContext context, PgAggregateState<PgAnyElement>? state,
        PgAnyElement? value)
        => state ?? (value is null ? null : new(value.CopyTo(context.MemoryContext)));

    public static PgAnyElement? Final(
        PgAggregateContext context, PgAggregateState<PgAnyElement>? state, ValueTuple arguments)
        => state?.Value;
}
```

The generated extra SQL input is always NULL and does not reach the managed
final method. Polymorphic values also work
with moving states, ordered-set comparisons, and parallel combine methods.

The [generic aggregate sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.GenericAggregates)
ports pgrx's `generic_agg` example. Its `count_changes(anyelement)` keeps a copy
of the previous value in a child of `context.MemoryContext` and compares values
with PostgreSQL's `datumIsEqual` through the generated native bindings.

## Ordered and hypothetical sets

Set `Kind = PgAggregateKind.OrderedSet` for `WITHIN GROUP` syntax. The final
capability's `TDirect` group, a scalar or tuple, declares the direct arguments.
They are evaluated once per group and are not transition inputs.

PostgreSQL does not sort ordered-set input for the transition method. Use the
callback's `PgAggregateContext` to inspect `SortKeys` and compare values with
`context.Compare(left, right, sortKey)`. The native comparator honors the key's
operator, collation, direction, and NULL placement. With
`FinalModify = ReadOnly`, sort a copy of the state, as `DiscretePercentile` does.

Use the `SpiParameter` overload when NULL operands need an explicit named type.
For a named composite, load its descriptor and bind both operands with it:

```csharp
PgTupleDescriptor descriptor = PgTupleDescriptor.Load("app.item");
int order = context.Compare(
    SpiParameter.Create(left, descriptor),
    SpiParameter.Create(right, descriptor));
```

`SpiParameter.CreateArray` supplies the element identity for composite arrays.
The generic overload uses the same type inference as ordinary SPI parameters;
a NULL tuple alone carries no named descriptor.

The sample's `DiscretePercentile` uses that comparison and ceiling-ranked
selection. For `[10,20,30]`, fraction `0.4` selects `20`, in agreement with
PostgreSQL's `percentile_disc`.

```sql
SELECT integer_percentile(0.4) WITHIN GROUP (ORDER BY value DESC)
FROM (VALUES (30), (10), (20)) AS inputs(value);
```

`PgAggregateKind.HypotheticalSet` adds PostgreSQL's hypothetical argument
matching rules. The
implementation still supplies the rank/distribution logic; PostgreSQL does not
insert the hypothetical row. Ordered-set aggregates cannot be used as windows.

## Context and errors

`PgAggregateContext` exposes state storage, aggregate/window kind, state sharing,
input collation, an optional aggregate OID, and owned sort metadata. Window callbacks have no
`Aggref`, so their aggregate OID is null. A shared transition can represent more
than one aggregate; its reported OID must not be used to select final behavior.
Metadata remains readable after callback exit. Storage lookup and comparisons
require the current aggregate context on its backend thread. Nested scalar calls
retain that aggregate owner; nested aggregates use their own.

Throw `PgException` for a deliberate SQLSTATE and diagnostics. Native comparison,
datum conversion, and backend calls use guarded boundaries so PostgreSQL ERROR
does not cross managed frames. State cleanup still runs when a query fails.

## Declaration diagnostics

Aggregate errors identify the option, callback, or parameter that needs correction.
An invalid aggregate does not emit its SQL or support functions; independent valid
declarations still generate normally.

| Diagnostic | Correction |
|---|---|
| `ANKUS080` | Use an accessible, non-generic class or struct inside accessible, non-generic containers. File-local containers are unsupported. |
| `ANKUS081` | Choose a nonempty aggregate name of at most 63 UTF-8 bytes, with valid Unicode and no zero characters. |
| `ANKUS082` | Select a defined member of the named execution-policy enum. |
| `ANKUS083` | Remove zero characters or invalid Unicode from the initial condition. |
| `ANKUS084` | Return the ordinary transition's state type. Composite state needs a named PostgreSQL type. |
| `ANKUS085` | Give `Final` the same state type as `Transition`. |
| `ANKUS086` | Use `ValueTuple` for a normal aggregate's direct argument group. |
| `ANKUS087` | Supply at least one aggregated input for ordered-set or hypothetical aggregation. |
| `ANKUS088` | Limit the combined direct and aggregated inputs to 99 SQL arguments. |
| `ANKUS089` | Reserve `internal` for state; direct and aggregated inputs must be SQL values. |
| `ANKUS090` | Give direct and aggregated inputs distinct SQL names. |
| `ANKUS091` | Add a final capability that converts internal state to a supported SQL result. |
| `ANKUS092` | Supply a polymorphic aggregate input to resolve a polymorphic state or result. |
| `ANKUS093` | End the hypothetical direct inputs with the same types as the aggregated inputs. |
| `ANKUS094` | Implement `IPgMovingAggregate` before using moving options or `MovingFinal`. |
| `ANKUS095` | Return the moving transition's state type. Composite state needs a named PostgreSQL type. |
| `ANKUS096` | Use the same aggregated input types for ordinary and moving transitions. |
| `ANKUS097` | Match the inverse callback's state, inputs, and result to the moving transition. |
| `ANKUS098` | Give moving transition and inverse callbacks the same `STRICT` policy. |
| `ANKUS099` | Return the same SQL result type from ordinary and moving execution. |
| `ANKUS100` | Combine two ordinary transition states and return that state type. |
| `ANKUS101` | Accept two nullable internal states in `Combine` and use a non-strict NULL policy. |
| `ANKUS102` | Serialize internal state to `byte[]` and deserialize it to the same state type. |
| `ANKUS103` | Use one trailing variadic array on a normal aggregate. |
| `ANKUS104` | Specify `SortOperator` only for an aggregate with one SQL input. |
| `ANKUS105` | Use a valid PostgreSQL operator token, optionally qualified by one schema. |
| `ANKUS106` | Initialize internal state in the transition callback; internal state cannot use a textual initial condition. |
| `ANKUS107` | Supply an initial condition or make the first declared and aggregated inputs binary compatible with the strict transition's state. |
| `ANKUS108` | Match the final callback's state and direct inputs to the corresponding aggregate contracts. |
| `ANKUS109` | Use a non-strict final callback with `FinalExtra` or `MovingFinalExtra`; generated extra inputs are nullable. |
| `ANKUS110` | Implement one transition contract and at most one of each optional capability. |
| `ANKUS111` | Add the capability interface for the named callback, including visible inherited callbacks. |
| `ANKUS112` | Implement the selected static interface callback with a synchronous, non-generic method. |
| `ANKUS113` | Remove optional C# defaults and pass callback parameters by value. |
| `ANKUS114` | Remove attributes and `params` from the invocation context parameter. |
| `ANKUS115` | Remove trigger, event-trigger, operator, cast, or table-column attributes from the callback. |
| `ANKUS116` | Return one state or SQL result per call; aggregate callbacks cannot return a sequence. |
| `ANKUS117` | Use a supported SQL result type or concrete `PgAggregateState<T>`. |
| `ANKUS118` | Select an existing tuple element by its exact `Element` name; do not mark the tuple group `params`. |
| `ANKUS119` | Remove metadata from an empty `ValueTuple` group, which has no SQL inputs. |
| `ANKUS120` | Limit a support function to 100 SQL arguments, including the deserializer's generated dummy argument. |
| `ANKUS121` | Give every input within a support function a distinct SQL name. |
| `ANKUS122` | Supply a polymorphic callback input; internal-state final callbacks can use `FinalExtra`. |
| `ANKUS123` | Choose a valid support-function name of at most 63 UTF-8 bytes. |
| `ANKUS124` | Use supported SQL input types or concrete managed state. |
| `ANKUS125` | Choose a nonempty input name of at most 63 UTF-8 bytes, with valid Unicode and no zero characters. |
| `ANKUS126` | Apply at most one `PgParameter` attribute to each SQL input. |
| `ANKUS127` | Remove `PgParameter.Default`; aggregate inputs do not accept SQL defaults. |
| `ANKUS128` | Use `PgParameter.Element` only for a tuple input group. |

Ordinary support-function options also use the
[function declaration diagnostics](/function-declarations/#declaration-diagnostics).
`ANKUS028` checks implementation nullability, and `ANKUS029` requires the
base aggregate interface.
