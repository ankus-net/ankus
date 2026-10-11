---
title: Composite values
description: Read, construct, and return PostgreSQL composites and anonymous records as owned heap tuples.
---

`PgHeapTuple` represents a PostgreSQL composite value or anonymous `record`.
It owns managed cells and immutable `PgTupleDescriptor` metadata. This is the
dynamic tuple model corresponding to pgrx's `PgHeapTuple` and `PgTupleDesc`.

## Named composite types

Create the SQL type through custom SQL and bind parameters and returns with
`PgCompositeType`. The attribute names an existing type; it does not generate
`CREATE TYPE`.

```csharp
using Ankus;

[assembly: PgSql("dog-type", "CREATE TYPE dog AS (name text, age integer);", Relocatable = true)]

public static class Dogs
{
    [PgFunction(Requires = ["dog-type"])]
    [return: PgCompositeType("dog")]
    public static PgHeapTuple Birthday([PgCompositeType("dog")] PgHeapTuple dog)
    {
        PgHeapTuple copy = dog.Clone();
        copy.Set("age", checked(dog.Get<int?>("age") + 1));
        return copy;
    }
}
```

```sql
SELECT (birthday(ROW('Ada', 3)::dog)).*; -- Ada, 4
```

Set `Schema` on the attribute for a fixed schema. Without it, the SQL type name
is unqualified and resolves through PostgreSQL's installation search path;
it does not inherit the function's `PgSchema`. Use explicit SQL dependencies
to create types before functions. Fixed schema bindings make the extension
non-relocatable. See [custom SQL](/custom-sql/).

## Access, metadata, and ownership

`Get<T>` and `Set<T>` accept zero-based ordinals or exact, case-sensitive names.
Ordinals preserve physical dropped-column positions. Dropped cells are NULL,
cannot be changed, and do not resolve by name. An incompatible read or write
throws; a failed write leaves the cell unchanged. NULL cells require reference
or nullable value types when read.

A field whose type has no managed conversion, such as `tsvector` or `macaddr`, is
copied exactly. Read or write it with a [`PgDatumType`](/raw-values/#reusable-scalar-mappings)
mapping, or an array of one, which must have exactly the field's type. The indexer
and other managed types throw `NotSupportedException`. `Get<PgDatum>` rebuilds any
field that is not dropped, including SQL NULL, as a raw datum owned by the current
memory context. A tuple returned to PostgreSQL writes the copy back unchanged.

`Descriptor.Attributes` records names, declared and base type OIDs, type
modifiers, collations, dropped slots, and catalog `NOT NULL` metadata.
Descriptors loaded for a domain retain its declared `TypeOid` and expose the
underlying composite as `BaseTypeOid`. A tuple read from a PostgreSQL datum
carries its physical base composite OID; SPI column metadata retains the query's
declared domain type. `TypeModifier` identifies registered anonymous records.

Physical metadata from `PgRelation.TupleDescriptor` can have `TypeOid == 0`
when the server supplies no row type, as with PostgreSQL 13 indexes. Such a
descriptor preserves its attributes for inspection; constructing tuples, arrays
or typed SPI parameters from it throws `InvalidOperationException`.

Trigger rows mark undefined generated-column values with `IsUnavailable`.
Reading or changing those cells throws; they are not SQL NULL. See [triggers](/triggers/)
for availability by trigger timing and safe row returns.
A table row's column `NOT NULL` metadata does not make an
ordinary standalone composite subject to that table's insertion constraints.

Cells and metadata survive SPI calls, cursor closure, backend memory-context
cleanup, and garbage collection. `Clone()` copies the cell slots. Nested tuples,
arrays, and binary buffers remain shared managed references, so replace or copy
those values when independent mutation is needed.

Hooks and custom scans receive native rows. `PgHeapTuple.DangerousCopy(heapTuple,
tupleDescriptor)` and `PgTupleDescriptor.DangerousCopy(tupleDescriptor)` copy them
from their addresses, as pgrx's `from_heap_tuple` and `PgTupleDesc::from_pg_copy`
do. `tuple.DangerousToNative(context)` forms a native `HeapTuple` in a memory
context, as pgrx's `into_pg` does, and `PgDatum.Create(tuple, context)` gives its
composite datum. The addresses must belong to live values in this backend, and
like other raw operations these calls need an `unsafe` block (ANKUS129).

Native reconstruction validates the current type and physical layout. A tuple
captured before incompatible `ALTER TYPE` or drop/recreate operations cannot be
silently reinterpreted. PostgreSQL assignment coercion applies field type
modifiers and domain constraints: for example, `numeric(6,2)` rounds to scale
two, while an overlong nonblank `varchar(3)` value fails. Native errors unwind
through the normal guarded boundary.

## Constructing tuples and records

Load a named descriptor inside an active backend call:

```csharp
PgTupleDescriptor descriptor = PgTupleDescriptor.Load("pets.dog");
PgHeapTuple dog = descriptor.CreateTuple();
dog.Set("name", "Ada");
dog.Set("age", 3);
```

`Load(uint)` accepts a catalog OID. Name lookup follows PostgreSQL's identifier
and search-path rules. A newly created tuple is nonnull with every cell NULL;
populate it before returning it when its field domains require values.

Unannotated `PgHeapTuple` signatures use PostgreSQL `record`. Construct one
from named, typed fields:

```csharp
PgHeapTuple record = PgHeapTuple.Create(
    ("name", SpiParameter.Create("Ada")),
    ("age", SpiParameter.Create<int?>(null)));
```

PostgreSQL registers its descriptor. A caller using a record-returning function
in `FROM` supplies the expected columns, such as
`AS value(name text, age integer)`. Its types must match the returned record.

A NULL tuple and a nonnull tuple with all NULL fields are distinct values.
PostgreSQL's row `IS NULL` predicate is true for both; use a suitable scalar
operation such as `record_send(value) IS NULL` when testing whole-value NULL.

## Arrays, sets, and SPI

Composite signatures support vectors and `PgArray<PgHeapTuple?>`, including
multidimensional arrays, nondefault lower bounds, NULL elements, and nested
composite fields. Construct explicitly typed arrays from a descriptor:

```csharp
PgArray<PgHeapTuple?> dogs = descriptor.CreateArray([null, dog]);
PgArray<PgHeapTuple?> empty = descriptor.CreateArray([]);
```

The element type comes from the descriptor even for empty and all-NULL arrays.
Plain tuple vectors and directly constructed `PgArray<PgHeapTuple?>` values
use `record[]`; their first element never determines a named array identity.
`PgCompositeType` supplies the named type for generated function signatures.

Use `IEnumerable<PgHeapTuple?>` for composite SETOF results and apply the return
attribute as usual. TABLE rows can contain composite fields; use the attribute's
`Column` to select their final SQL output names. Multiple composite columns
require separate bindings. Streaming preserves NULL tuple identity; a
materialized composite set stores expanded rows, so a NULL tuple becomes a row
whose fields are all NULL. See [sets and tables](/sets-and-tables/).

SPI scalars, rows, plans, sessions, and cursors use the same owned conversions.
For a named typed NULL, bind an explicit descriptor. For a plan, use its actual
OID rather than `typeof(PgHeapTuple)`, which means `record`:

```csharp
SpiParameter parameter = SpiParameter.Create(dog, descriptor);
using SpiPreparedStatement plan = Spi.PrepareWithTypeOids("SELECT $1", parameter.TypeOid);
PgHeapTuple echoed = plan.ExecuteScalar<PgHeapTuple>(parameter);
SpiParameter absent = SpiParameter.Create(null, descriptor);
SpiParameter absentArray = SpiParameter.CreateArray(null, descriptor);
```

The [composites sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.Composites)
ports pgrx's `composite_type` example. Bootstrap SQL creates the types, and C#
functions create, copy and nest them, add an operator over a composite and
aggregate composite inputs. It also covers arrays, SETOF and anonymous record
construction.
