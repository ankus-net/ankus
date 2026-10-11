---
title: Ranges
description: Represent PostgreSQL range bounds and use native canonicalization and range operations.
---

`PgRange<T>` owns a PostgreSQL range. Its bound type selects the SQL type:

| Bound type | PostgreSQL |
| --- | --- |
| `int` | `int4range` |
| `long` | `int8range` |
| `PgNumeric`, `decimal` | `numrange` |
| `PgDate`, `DateOnly` | `daterange` |
| `PgTimestamp`, `DateTime` | `tsrange` |
| `PgTimestampTz`, `DateTimeOffset` | `tstzrange` |

The ordinary .NET types use the same checked conversions as [scalar numeric](/numeric/)
and [temporal values](/date-and-time/). Use the `Pg*` types for values outside .NET's
range, explicit temporal infinities, and full-precision numeric bounds.

## Declare a function

```csharp
[PgFunction]
public static PgRange<int> IntegerWindow(int start, int end) => new(start, end);

[PgFunction]
public static bool Includes(PgRange<DateOnly> dates, DateOnly date)
    => dates.Contains(date);
```

```sql
SELECT integer_window(1, 5); -- [1,5)
SELECT includes('[2024-01-01,2024-02-01)', '2024-01-15'); -- true
```

The [complete range sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.Ranges)
ports pgrx's nine range example functions and its table of stored ranges. It
includes publishable project files and SQL examples for every constructor.

## Bounds and empty values

Construction and inspection work without a backend:

```csharp
PgRange<int> finite = PgRange.Create(1, 5);                 // [1,5)
PgRange<int> closed = PgRange.Create(1, 5, upperInclusive: true);
var from = new PgRange<int>(1, null);              // [1,)
var until = new PgRange<int>(null, 5);             // (,5)
PgRange<int> all = PgRange.Unbounded<int>();               // (,)
PgRange<int> empty = PgRange.Empty<int>();                 // empty
PgRange<int>? missing = null;                     // SQL NULL
```

- `Lower` and `Upper` are nullable bounds. A null bound means an unbounded end.
- `LowerInclusive` and `UpperInclusive` are false for unbounded ends.
- `IsEmpty` distinguishes an empty range from an interval.
- `IsUnbounded` identifies a nonempty range without either bound.
- A null **range reference** represents SQL NULL.

An explicit temporal infinity is still a bound. For example,
`new PgRange<PgDate>(null, PgDate.PositiveInfinity)` has an unbounded lower end
and a bounded upper end that excludes the `infinity` date value.

Numeric infinity bounds require PostgreSQL 14 or later, just like scalar numeric
infinity. PostgreSQL 13 rejects an explicit `Infinity` or `-Infinity` numeric
bound; unbounded ends remain available through null bounds or `(,)` SQL syntax.

The parameterless constructor creates an empty range. No constructor calls
PostgreSQL. It retains finite bounds and inclusion flags even if PostgreSQL will
later canonicalize the interval to a different representation or reject it.

`PgRange.FromRange(2..^2, length: 10)` resolves a C# index range to `[2,8)`.
The length is required to resolve from-end indices. A zero-length slice becomes
an empty range.

## Canonicalization and equality

PostgreSQL validates and canonicalizes ranges whenever they cross into SQL:

```csharp
var requested = new PgRange<int>(1, 5, lowerInclusive: false, upperInclusive: true);
PgRange<int> canonical = requested.Canonicalize(); // [2,6)
```

Integer, bigint, and date ranges use discrete canonical forms. Continuous numeric
and timestamp ranges retain inclusion flags where the bounds allow it. Equal
exclusive ends become empty; reversed bounds and overflowing discrete successors
raise `PgException`.

`Equals` and `GetHashCode` compare the stored bounds and flags without a backend.
Canonicalize first when comparing independently constructed ranges for PostgreSQL
semantic equality.

## Parsing and operations

These methods use PostgreSQL on the active backend:

```csharp
PgRange<int> first = PgRange.Parse<int>("[1,5)");
PgRange<int> second = PgRange.Parse<int>("[4,9)");

bool contains = first.Contains(3);
bool overlaps = first.Overlaps(second);
PgRange<int> intersection = first.Intersect(second); // [4,5)
PgRange<int> union = first.Union(second);             // [1,9)
PgRange<int> difference = first.Except(second);       // [1,4)
```

`Contains(range)` tests range containment, and `IsAdjacentTo(range)` tests
adjacency. `Merge(range)` returns the smallest interval spanning both operands,
including a gap. `Union` and `Except` raise `PgException` when the result would
require two separate intervals.

`PgRange.TryParse<T>` returns false for malformed text, reversed bounds, and
PostgreSQL subtype range errors. Backend-access errors and checked .NET narrowing
failures propagate. For built-in range types, invalid input leaves no error
pending: PostgreSQL 16 and later report it as a soft input error, and earlier
majors roll back an internal subtransaction before `TryParse` returns false.
Catch other native range errors outside `PgTransaction.RunInSubtransaction` to
recover before continuing with backend work.

In transaction callbacks and parallel operations before PostgreSQL 17, native
input errors must propagate because independent rollback is unavailable.
`TryParse` on a built-in range type still returns false there on PostgreSQL 16
and later. See
[error recovery](/reference/execution/#errors).

`ToPostgresString()` returns canonical SQL input text using the session's
`DateStyle` and `TimeZone`. `ToString()` is a detached diagnostic representation
of the stored managed bounds; it is not a SQL serialization contract.

## Arrays and SPI

Ranges support all typed SPI paths and [arrays](/arrays/):

```csharp
PgRange<decimal> value = Spi.ExecuteScalar<PgRange<decimal>>(
    "SELECT $1", SpiParameter.Create(PgRange.Create(1.2300m, 2.450m)));

PgRange<int>?[] ranges = Spi.ExecuteScalar<PgRange<int>?[]>(
    "SELECT ARRAY['[1,5)'::int4range, 'empty', NULL]");
```

Use `PgArray<PgRange<T>?>` to preserve dimensions and lower bounds. NULL array
elements, empty ranges, and unbounded ranges remain distinct. Returned values
own their bounds and survive result, cursor, or session disposal.

## Mapped bounds

A value type with a [`PgDatumType` converter](/raw-values/#reusable-scalar-mappings)
can also declare the SQL range that uses it as a subtype. The scalar and range
have separate names, schemas and ownership:

```csharp
using Ankus;

[assembly: PgSql("count-range", "CREATE TYPE count_range AS RANGE (subtype=integer);",
    Relocatable = true)]
[assembly: PgSqlTypeProvider("count-range", typeof(PgRange<Count>))]

[PgDatumType("int4", typeof(CountConverter),
    Origin = PgTypeOrigin.External, Schema = "pg_catalog")]
[PgRangeType("count_range")]
public readonly record struct Count(int Value);

public sealed class CountConverter : IPgDatumReader<Count>, IPgDatumWriter<Count>
{
    public Count Read(PgDatum value) => new(value.Read<int>());

    public PgDatum Write(Count value, uint typeOid, PgMemoryContext destination)
    {
        unsafe
        {
            return PgDatum.DangerousCreate(unchecked((nuint)(nint)value.Value), typeOid, destination);
        }
    }
}

public static class Functions
{
    [PgFunction]
    public static PgRange<Count> Window(int first, int last)
        => new(new Count(first), new Count(last));
}
```

`PgRangeType` registers conversion and SQL metadata. Supply the range's actual
`CREATE TYPE ... AS RANGE` statement in its provider, including any canonical
or subtype-difference function. The example has no canonical function, so its
range retains continuous inclusion semantics even though its subtype is integer.
For an existing range such as `int4range`, use
`[PgRangeType("int4range", Origin = PgTypeOrigin.External, Schema = "pg_catalog")]`
and omit the range provider.

An owned range requires its own `PgSqlTypeProvider` naming `typeof(PgRange<Count>)`.
If the bound is also owned, its completed provider precedes the range provider.
Both can use one SQL block. Unqualified owned identities follow extension
relocation; external identities remain fixed to their declared schemas. Every
conversion checks the current range OID and its exact subtype OID, including
whole SQL NULL and empty ranges. A domain subtype is distinct from its base type.

The scalar converter is shared with range bounds. Empty ranges and infinite
ends never invoke it. Readers receive temporary checked handles and must return
independent managed data; those handles expire after range conversion. Writers
receive temporary storage that remains live until the complete range is copied
to its destination. A caller-owned datum returned by a writer keeps its original
owner. A finite bound writer returning SQL NULL is rejected; use a null managed
bound to request an infinite end. Reading stored domain bounds does not rerun
CHECK constraints, while writing them enforces those constraints.

Mapped ranges support the operations above, typed SPI scalar results, function
calls, raw `PgDatum.Read<PgRange<Count>>()`, generated scalar/SETOF/TABLE/aggregate
signatures, and arrays such as `PgArray<PgRange<Count>?>`. Each direction requires
the corresponding scalar reader or writer; operations that write operands and
read range results require both. For detached ordinary SPI rows or tuples, use
raw cells and explicit mapped reads as described in the mapping guide.

Generic bounds follow the same finite selection rules as scalar mappings.
`PgRangeType(typeof(NumberBox<int>), "number_range")` selects an exact closed
bound and overrides an optional default declaration. An exact local declaration
also registers a raw-only root. Open converter definitions are inferred from
the scalar contract. Invalid range metadata produces a [mapping diagnostic](#mapping-diagnostics) without partial
generated output. Reference-type bounds, automatically derived range metadata
for `PgType`/`PgEnum`, and multiranges are not supported.

## Mapping diagnostics

Range diagnostics identify the argument or declaration that needs correction.
For a mapping in another assembly, they identify the consuming function's type.
Invalid mappings prevent generation of partial SQL or native registration code.
Names and schemas retain their exact text across project and assembly references;
zero characters and malformed UTF-8 are rejected.
C# namespaces with escaped identifiers, such as `@class`, work normally.
The selected range attribute retains its actual defining assembly and module,
including when another assembly declares an attribute with the same metadata name.

| Diagnostic | Required correction |
| --- | --- |
| `ANKUS158` | Apply `PgRangeType` to a non-ref-like value type with a scalar `PgDatumType` mapping. |
| `ANKUS159`, `ANKUS161` | Keep one default declaration and at most one declaration for each exact closed bound type. |
| `ANKUS160` | Use a closed construction of the annotated bound type as the exact target. |
| `ANKUS162`, `ANKUS163` | Use valid range and schema identifiers of at most 63 UTF-8 bytes, without zero characters. |
| `ANKUS164` | Select `ThisExtension` or `External` as the origin. |
| `ANKUS165` | Supply an explicit schema for an external range. |
| `ANKUS166`, `ANKUS167` | Reference a matching Ankus runtime and resolve the range's managed type unambiguously. |
| `ANKUS168` | Add a default or exact `PgRangeType` declaration for the consumed bound type. |
| `ANKUS205` | Rebuild the defining assembly so its range attribute contains complete, valid UTF-8 metadata. |
