---
title: Arrays
description: Use C# vectors, preserve PostgreSQL dimensions, and declare variadic functions.
---

Use `T[]` for one-dimensional arrays with the usual PostgreSQL lower bound of one.
Use `PgArray<T>` when dimensions or lower bounds matter. Both support the scalar
types listed in [Write a function](/getting-started/functions/#types).

The [arrays sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.Arrays)
ports pgrx's array examples: borrowed iteration, mutable copies, nullable names,
set-returning arrays and a generated custom-type array. Its `vectors` schema
compares iteration, copied cells, native spans and sixteen-lane accumulation.
These functions preserve the upstream floating-point accumulation order;
the sample does not promise a measured speedup.

Reusable scalar mappings declared with `[PgDatumType]` also support these array
forms, including nullable elements. They use the declared element's reader or
writer and preserve its exact PostgreSQL identity. See
[mapped elements](#mapped-elements).

[Composite arrays](/composites/#arrays-sets-and-spi) use `PgHeapTuple?[]` or
`PgArray<PgHeapTuple?>`. `PgCompositeType` binds named function signatures;
`PgTupleDescriptor.CreateArray` retains an explicit element identity even for
empty and all-NULL arrays. `PgArray<T>.ElementTypeOid` exposes that identity.

## Vectors

```csharp
[PgFunction]
public static int?[] ReverseValues(int?[] values) => [.. values.Reverse()];
```

```sql
SELECT reverse_values(ARRAY[1, NULL, 3]); -- {3,NULL,1}
```

`byte[]` remains a single `bytea` value. Use `byte[][]` or `PgArray<byte[]>` for
`bytea[]`. Other jagged arrays and C# rectangular arrays are not SQL mappings;
represent a multidimensional PostgreSQL array with `PgArray<T>`.

Converting an array to `T[]`, as a parameter, a result or `Get<T[]>()`, rejects
multiple dimensions or a non-one lower bound. It never silently flattens the value.

Arrays of `bool`, `sbyte`, `short`, `int`, `long`, `uint`, `float` and `double`
without NULL elements convert with one block copy in each direction, since
PostgreSQL stores them as a packed native array. Other element types, and arrays
containing NULL, convert element by element. To read the native elements without
any copy, use a [contiguous native slice](#contiguous-native-slices).

## Dimensions and indexing

`PgArray<T>` stores elements in row-major order: the last subscript varies fastest.
Its constructors copy the element sequence, dimension lengths, and lower bounds.

```csharp
[PgFunction]
public static PgArray<int?> Grid()
    => new([11, null, -7, 0, 5, 9], [2, 3], [-2, 4]);
```

```sql
SELECT grid();        -- [-2:-1][4:6]={{11,NULL,-7},{0,5,9}}
SELECT (grid())[-1][6]; -- 9
```

```csharp
PgArray<int?> grid = Grid();
int? flat = grid[2];           // -7; zero-based flat index
int? cell = grid.GetValue(-1, 6); // 9; PostgreSQL subscripts

int rank = grid.Rank;          // 2
int count = grid.Count;        // 6, including NULL elements
ReadOnlySpan<int> lengths = grid.Lengths;     // [2, 3]
ReadOnlySpan<int> bounds = grid.LowerBounds;  // [-2, 4]
```

Omitting lower bounds uses one for each dimension. PostgreSQL supports up to six
dimensions. Empty arrays normalize to rank zero, with empty lengths and bounds.

`ToVector()` copies only a rank-zero empty array or a one-dimensional array whose
lower bound is one. `ToArray()` explicitly flattens any shape into a new vector.
Enumeration follows the same row-major order.

## NULLs and element conversions

The array and its elements have separate nullability:

| C# declaration | Meaning |
| --- | --- |
| `int[]` | Required array; NULL elements raise a conversion error |
| `int?[]` | Required array; elements may be NULL |
| `int?[]?` | Array and elements may be NULL |
| `PgArray<int?>?` | Nullable array and elements, preserving shape |
| `string?[]?` | Nullable text array and reference elements |

Use nullable value types for NULL elements. Reference elements can receive null;
annotate them accordingly. An empty array is distinct from a null array.

The [numeric](/numeric/) and [temporal](/date-and-time/) conversion rules apply to
each element. For example, `decimal[]` rejects values requiring rounding, and
`DateOnly[]` rejects PostgreSQL infinity. Use `PgNumeric` and the full-range
temporal types to retain those values.

Elements retain the selected server's feature limits: numeric infinity requires
PostgreSQL 14+, and interval infinity requires PostgreSQL 17+. Older servers
reject those inputs before invoking the array function. Finite values, SQL NULL,
empty arrays, dimensions and lower bounds remain independently supported.

[Network](/network/) arrays preserve address families and prefixes.
`IPAddress[]` rejects subnet prefixes; use `PgInet[]` to retain them.

## SPI

Arrays work with queries, prepared statements, sessions, cursors, and local row edits:

```csharp
int?[] values = [1, null, 3];
int?[] copy = Spi.ExecuteScalar<int?[]>(
    "SELECT $1", SpiParameter.Create(values));

PgArray<int?> shaped = Spi.ExecuteScalar<PgArray<int?>>(
    "SELECT '[0:2]={1,NULL,3}'::int[]");

SpiParameter nullArray = SpiParameter.Create<int?[]?>(null);
```

Untyped row cells contain `PgArray<T>` with nullable value-type elements, such as
`PgArray<int?>`. Use `row.Get<T[]>()` or `row.Get<PgArray<T>>()` for an explicit
conversion. Domains use their underlying scalar conversion; `varchar[]` and
`char(n)[]` become text elements, preserving any stored padding.

Results own their managed storage and survive later SPI calls. Binary elements
are ordinary managed byte arrays. Copying an element sequence does not clone
those inner byte arrays.

## Mapped elements

A [`PgDatumType` mapping](/raw-values/#reusable-scalar-mappings) supplies the
element conversion for `T[]`, `T?[]`, `PgArray<T>`, and `PgArray<T?>`. A reader
supports generated inputs, explicit raw reads, SPI scalar-result helpers, `SpiRow`
and `PgHeapTuple` cells, and named/OID and native-address function-call results.
A writer supports generated outputs, typed parameters, row and tuple cells, and
`PgDatum.Create`. The same converter instance serves scalar and array conversions.
No separate array converter or type provider is needed.

A bound with [`PgRangeType`](/ranges/#mapped-bounds) also supports arrays of
its ranges, including `PgRange<Count>?[]` and `PgArray<PgRange<Count>?>`.
Their element identity is the declared range type. NULL cells, empty ranges,
infinite ends and finite bounds stay distinct. The range provider supplies the
range type; its array needs no additional provider.

For example, with the `Count` reader from the mapping guide:

```csharp
Count?[] values = Spi.ExecuteScalar<Count?[]>("SELECT ARRAY[7,NULL,11]");
PgArray<Count?> shaped = Spi.ExecuteScalar<PgArray<Count?>>(
    "SELECT '[0:2]={7,NULL,11}'::integer[]");
```

Arrays use the current exact array type belonging to the mapped element. If the
element maps to a domain, its array differs from the base-type array and from
sibling-domain arrays. A domain over the entire array is also a distinct type;
declare a separate scalar mapping when that is the representation you need.
Identity checks still apply to whole-array NULL, empty arrays and all-NULL arrays.

Reading existing domain elements does not rerun their constraints. Raw
`PgAnyArray` extraction also preserves existing values of a domain over an array
without assigning the array back to that domain. These reads retain the exact
type and ownership checks described above.

Whole-array NULL skips element conversion. A NULL element skips its reader or
writer, but still receives PostgreSQL's domain validation when written. A present
element writer may return a typed SQL NULL; its type and native lifetime are
checked as well. A NOT NULL element domain rejects either source of NULL.
The required conversion direction is checked even for NULL or empty arrays;
type-only defaults and prepared-statement metadata do not require a writer.

Readers receive temporary native elements and must return detached managed
values. Those element handles expire after the array read; an explicitly owned
raw source array remains usable. Array writers receive a temporary destination
that stays live until the complete array is constructed and copied to its
destination. Returning a datum owned by the caller does not transfer that owner
to Ankus. Copying the managed array container does not clone user reference
objects returned by an element reader.

Mapped arrays work in scalar, variadic, SETOF, TABLE and aggregate signatures,
and through sessions and prepared statements. `SpiRow` and `PgHeapTuple` read
`T[]` and `PgArray<T>` through the element mapping when the cell has exactly the
mapped array type, and `Set<T>` writes through it; raw cells offer the same reads
with `Read<T[]>()` and `Read<PgArray<T>>()`. Nested mapped arrays and rectangular
CLR arrays are rejected. This follows pgrx's rejection of nested SQL array
containers: represent multiple PostgreSQL dimensions in one `PgArray<T>`, with
the mapped scalar as `T`, rather than nesting `PgArray` or vector types.

For `DangerousCall<T[]>` or `DangerousCall<PgArray<T>>`, the caller must prove
that the native function returns the mapping's array type and representation.
The address provides no catalog return declaration. See
[native entry points](/calling-functions/#native-entry-points) for that contract.

## Variadic functions

C# `params T[]` declares SQL `VARIADIC`:

```csharp
[PgFunction]
public static int SumValues(params int?[] values)
    => values.Sum(static value => value ?? 0);
```

```sql
SELECT sum_values(2, NULL, 3);                     -- 5
SELECT sum_values(VARIADIC ARRAY[2, NULL, 3]);     -- 5
SELECT sum_values(VARIADIC ARRAY[]::integer[]);   -- 0
```

PostgreSQL requires the explicit `VARIADIC` array form for an empty list. A nullable
array declaration can also receive `VARIADIC NULL::integer[]`. A required array
uses the ordinary strict/null rules for [function parameters](/getting-started/functions/#sql-null).
`params byte[]` is rejected because `byte[]` maps to scalar `bytea`; use
`params byte[][]` for variadic binary values.

Enums declared with `[PgEnum]` also support vectors, shaped arrays, nullable
elements and variadic functions. Enum identity is preserved even for byte-backed
enums. See [enumerated types](/enums/).

## Arrays of any PostgreSQL type

Use `PgAnyArray` to declare an `anyarray` parameter. Its `ElementTypeOid`, `Rank`,
`GetLength`, and `GetLowerBound` retain the actual type and shape. Indexing and
enumeration return nullable `PgAnyElement` cells in row-major order. Each cell's
`Read<T>()` checks the requested managed type; `Datum.ToPostgresString()` works
with PostgreSQL types that have no C# mapping.

The array and cells belong to the function call or iterator. `CopyTo(context)`
gives them another owner; resetting or deleting that context invalidates them.

## Borrowed native arrays

Use `PgArrayView` for an `anyarray` parameter whose elements you want to inspect
without first copying a flat native array. It exposes exact `TypeOid` and
`ElementTypeOid`, `Rank`, `Count`, `Lengths`, `LowerBounds`, and `HasNulls`.
Each cell is a checked `PgDatum`; a NULL cell retains its element type and is
distinct from a present zero value.

```csharp
[PgFunction]
public static string?[] DescribeCells(PgArrayView values)
    => [.. values.Select(static cell => cell.ToPostgresString())];
```

```sql
SELECT describe_cells(ARRAY[0, NULL, -7]); -- {0,NULL,-7}
```

The view preserves native dimensions and lower bounds. `values[0]` selects the
first cell in row-major order; `values.GetValue(-1, 6)` uses PostgreSQL
subscripts. PostgreSQL locates fixed-size cells directly when the array has no
NULL bitmap; other storage can require scanning preceding cells. Use `foreach` for a linear
pass. Enumerators advance independently, and disposing one does not invalidate
cells already obtained from it.

Flat arrays and their by-reference cells share the original native storage.
PostgreSQL flattens packed, compressed, external or expanded arrays into a private
child context when necessary. Reading existing domain arrays or domain elements
does not recheck their constraints. Enum and composite element OIDs remain exact.

Direct scalar parameters expire when their managed callback exits; Ankus also
releases any remaining private view and iterator contexts then. Nested callbacks
have separate lifetimes. Retained set and aggregate inputs receive independent
snapshots so they remain valid across callbacks. Returning a borrowed array or
cell transfers a copy to the result owner before the input expires.

You can also construct a view from a live raw datum or request one through SPI:

```csharp
using PgArrayView values = Spi.ExecuteScalar<PgArrayView>(
    "SELECT '[-1:1]={first,NULL,last}'::text[]");
PgDatum first = values.GetValue(-1);
string? text = first.ToPostgresString();
```

`PgFunctions.Call<PgArrayView>()`, `datum.Read<PgArrayView>()`, and
`new PgArrayView(datum)` expose the same checked view. SPI and function results
use a callback-owned snapshot. A view constructed from a datum shares that
datum's lifetime. Dispose explicitly created views and iterators while their
backend is active. Disposing the view, resetting or deleting its source owner,
or expiring its input callback invalidates all native aliases. `ResetOnly()`
also expires views, escaped cells and iterators, even though their private child
contexts survive. A view constructed from another view retains the original
source lifetime as well. Copied type and
shape metadata remain readable. Native access must stay on the originating
backend thread.

Use `view.Datum.CopyTo(owner)` or `cell.CopyTo(owner)` before the source expires
to retain an independent value. A nullable `PgArrayView?` parameter accepts
whole-array SQL NULL; `Read<PgArrayView?>()` returns null for it. The explicit
constructor requires a present array. Empty arrays have rank zero and no cells.

### Typed borrowed cells

Construct `PgArrayView<T>` from a checked array datum to convert cells on access
without first materializing the entire array:

```csharp
using SpiRawResult result = Spi.QueryRaw(
    "SELECT '[-1:1]={7,NULL,19}'::integer[]");
using var values = new PgArrayView<int?>(result[0][0]);

int? first = values.GetValue(-1); // 7
int? absent = values[1];         // null
int?[] copy = values.ToArray();  // Explicitly copies in row-major order.
```

The typed view exposes `TypeOid`, `ElementTypeOid`, `Count`, `Rank`, `HasNulls`,
`Lengths`, `LowerBounds`, and the checked original `Datum`. Its indexer uses a
zero-based flat index; `GetValue` uses PostgreSQL subscripts. Native lookup is O(1)
for fixed-size elements stored without a NULL bitmap and O(n) otherwise. Element
conversion can add its own cost. Enumeration visits the cells in one linear pass, with independent cursors
and one conversion per cell. Repeated `Current` reads return that same converted
value after checking the source lifetime.

Construction validates the element type even for empty and all-NULL arrays.
`PgArrayView<int>` rejects any NULL cell; use `PgArrayView<int?>` when NULL is
possible. Reference cells can be null, so annotate them accordingly. Domains
retain their exact OIDs while ordinary scalar conversions read the base value
without reapplying domain constraints. A `[PgDatumType]` element instead requires
its declared exact element identity and a reader, including when every cell is
NULL. Enums, custom types, native-layout types and composites use their existing
scalar readers. Nested managed array elements are unsupported except `byte[]`,
which represents one `bytea` cell.

Ordinary strings, bytes and value types are copied when read. With
`PgArrayView<PgTextView?>`, `PgArrayView<PgByteaView?>` or
`PgArrayView<PgCStringView?>`, each present cell is
another checked native view: dispose each returned element when finished. It
retains the array's source lifetime and expires when the array or its source
expires. Disposing a cursor does not dispose its returned elements. Copy text or
bytes explicitly before retaining them beyond that lifetime; copying a sequence
of borrowed views only copies their references.

`PgCString` cells preserve exact nonzero bytes without server-encoding
conversion. PostgreSQL permits `cstring[]` in native function signatures, but
does not permit it as a stored table or composite attribute. See
[C strings and native type I/O](/text-and-binary/#c-strings-and-native-type-io).

Typed views also work through raw datum reads, SPI scalar helpers, sessions,
prepared statements and named/OID function calls:

```csharp
using PgArrayView<int?>? values = Spi.ExecuteScalar<PgArrayView<int?>?>(
    "SELECT '[-1:1]={7,NULL,19}'::integer[]");
SpiParameter parameter = SpiParameter.Create(values);
```

`datum.Read<PgArrayView<T>>()` shares the raw source's lifetime. SPI scalar and
function results copy native storage into the enclosing callback before their
temporary result owner ends. Dispose each returned view while the backend is
active. If a later requested SPI column fails conversion, Ankus releases earlier
provisional views. Cells remain lazy; no element reader runs until a cell is
accessed. A mapping without a reader is rejected before scalar SQL execution.

Whole-array SQL NULL returns a null view after validating its declared element
type. This differs from the explicit constructor, which requires a present
array. Named/OID calls check the declared element type before invoking the
function. `DangerousCall<PgArrayView<T>>()` requires the caller to prove the
native result is the array type belonging to `T`; a native address provides no
catalog return declaration. Use a raw call with an explicit type and owner when
the native result has a more specific identity, such as a named composite array.

Present parameters retain the original array OID, including a domain over an
array, without reapplying element writers. A read-only element mapping therefore
supports transporting an existing view. A typed null parameter and prepared-plan
type metadata select the array type belonging to `T`; they cannot infer a more
specific identity from an absent value.

Use the typed view directly in scalar, SETOF, TABLE and aggregate signatures:

```csharp
[PgFunction]
public static PgArrayView<int?>? Echo(PgArrayView<int?>? values) => values;
```

This declares `integer[]` for both the argument and result. The outer `?`
accepts a whole-array SQL NULL; the element's `?` accepts NULL cells. The
returned array keeps its original dimensions, lower bounds and cell values.
`PgCompositeType` binds a named composite when the element is `PgHeapTuple`.
Mapped elements retain their SQL type provider dependencies. Polymorphic,
arbitrary raw-datum and nested-array elements remain unsupported; use the raw
`PgArrayView` when the SQL array type itself must be polymorphic.

Scalar inputs borrow the current callback and expire when it returns, including
on error. Iterator and aggregate inputs receive independent snapshots that
survive their individual callbacks. A returned view transfers native storage to the result
owner before its input lease ends. Returning an existing view requires no
element writer and does not apply element conversions in reverse. Its complete
native array identity must match the declared SQL result type.

### Contiguous native slices

`DangerousGetSpan<T>()` exposes the original contiguous payload for `sbyte`,
`short`, `int`, `long`, `float`, and `double`. PostgreSQL's element base type must
match the requested type, even for an empty array. A same-width type is not
interchangeable: an `integer[]` cannot be borrowed as `float` values. Arrays
containing any SQL NULL element are rejected; use the view's cells or iterator
when you need to handle NULLs.

```csharp
[PgFunction]
public static long SumNativeIntegers(PgArrayView values)
{
    ReadOnlySpan<int> cells = values.DangerousGetSpan<int>();
    long total = 0;
    foreach (int cell in cells)
    {
        total += cell;
    }

    return total;
}
```

Multidimensional arrays expose one row-major span; `Lengths` and `LowerBounds`
retain their original shape. Domain arrays and domain elements retain their
identities without rechecking existing constraints. Packed, compressed, external,
or expanded arrays use the view's private flattening storage when needed.

UUID arrays expose `DangerousGetUuidBytes()`: sixteen network-order bytes per
element. Read a segment with `new Guid(bytes.Slice(index * 16, 16), bigEndian:
true)`. `DangerousGetSpan<Guid>()` is rejected because a .NET `Guid` has a
different native memory layout.

`DangerousGetNullBitmap()` exposes PostgreSQL's SQL NULL bitmap: one bit per
element in row-major order, least significant bit first, set for a present value.
It is empty when the array stores no bitmap, which PostgreSQL omits for arrays
built without NULL elements.

These methods validate ownership when acquiring the span. The span itself cannot
check later lifetime changes. Finish reading it before making another backend
call, disposing the view, resetting or deleting its source owner, leaving its
callback, or switching threads. Use `ToArray()` while the borrow is valid to
retain an independent managed copy.

## Building arrays in place

`PgMemoryContext.CreateFlatArray<T>()` allocates a zeroed PostgreSQL array of
`sbyte` (`"char"`), `short`, `int`, `long`, `uint` (`oid`), `float`, `double` or
`bool` directly in a memory context, like pgrx's `FlatArray::new_zeroed_in`. Fill
it through `DangerousGetSpan()` and return it as a `PgArrayView<T>`:

```csharp
[PgFunction]
public static PgArrayView<int> Squares(int count)
{
    PgFlatArray<int> array = PgMemoryContext.Current.CreateFlatArray<int>([count]);
    Span<int> cells = array.DangerousGetSpan();
    for (int index = 0; index < cells.Length; index++)
    {
        cells[index] = index * index;
    }

    return new PgArrayView<int>(array.Datum);
}
```

Pass no lengths for an empty array, and optional lower bounds for each dimension.
Every element is present; build arrays with SQL NULL elements with `PgArray<T>`,
because PostgreSQL does not store NULL elements. A zero-length dimension, more
than six dimensions, more than 134,217,727 elements or more than PostgreSQL's
1 GB allocation limit is rejected before anything is allocated. The array lives
until its memory context resets or is deleted.
