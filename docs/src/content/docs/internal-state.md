---
title: Internal state
description: Pass managed state and native pointers through PostgreSQL internal callbacks.
---

`PgInternal` represents PostgreSQL's `internal` type. Use it when backend
callbacks need to share managed state or exchange a native pointer.

## Managed state

Create state once, then retrieve it with the same managed type:

```csharp
[PgAggregate]
public sealed class CountValues : IPgAggregate<PgInternal?, int?>,
    IPgFinalizingAggregate<PgInternal?, ValueTuple, long>
{
    public static PgInternal Transition(PgAggregateContext context, PgInternal? state, int? value)
    {
        state ??= PgInternal.Create(new Counter());
        if (value.HasValue)
        {
            state.Get<Counter>().Count++;
        }

        return state;
    }

    public static long Final(PgAggregateContext context, PgInternal? state, ValueTuple arguments)
        => state?.Get<Counter>().Count ?? 0;

    private sealed class Counter
    {
        public long Count
        {
            get;
            set;
        }
    }
}
```

`Create` keeps the object alive until its PostgreSQL memory context resets or
is deleted. It calls `IDisposable.Dispose` once when the object implements it.
All wrappers for that state become unusable before disposal runs, even if
disposal throws. `Get<T>` requires the exact type supplied to `Create`; value
types are returned by value. Use a mutable class for state updated across calls.

Aggregate callbacks select the aggregate's owner automatically. For an ordinary
`[PgFunction]` used as an aggregate support function, pass the injected
`PgFunctionContext.StateMemoryContext` to `Create`. You can also supply an
explicit `PgMemoryContext` to control the lifetime.

`SETOF` and `TABLE` callbacks keep state across row requests. Materialized results
keep their state until the caller releases the rows, even though enumeration has
already finished.

Parallel aggregates must implement `Serialize` and `Deserialize`: workers
serialize their partial state, and the combining process deserializes it before
`Combine`.
Their `Combine` callback must copy temporary input state into its own aggregate
owner. Native pointers and managed object identities cannot be sent to another
process.

## Native pointers

For a native `internal` input whose layout you know, use
`state.DangerousBorrow<T>()` to read or update its unmanaged value. A zero pointer
returns null. `DangerousGetBits()` returns the native word; `DangerousCreate`
wraps a word under an explicit lifetime context. These operations preserve the
pointer and never copy or free its target. You must guarantee the target's
layout and lifetime. Managed state uses an opaque identity, so it cannot be
read with `DangerousBorrow<T>()`.

`DangerousCreate` and `DangerousBorrow<T>()` require an explicit `unsafe` context.
Use `Create` and `Get<T>` for managed state with a checked type and owner.

A null `PgInternal?` means SQL NULL. A present wrapper containing zero is distinct.
Access requires PostgreSQL's backend thread and a live owner.

PostgreSQL invokes `internal` functions as backend callbacks; SQL callers cannot
call them directly. A function returning `internal` must also accept an
`internal` argument. An aggregate's support callbacks satisfy these native
contracts through their generated declarations.
