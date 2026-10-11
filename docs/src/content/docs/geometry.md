---
title: Geometric values
description: Use PostgreSQL points, lines, boxes, circles, paths, and polygons from C#.
---

Ankus maps PostgreSQL's built-in geometric types to double-precision C# values:

| C# | PostgreSQL | Contents |
| --- | --- | --- |
| `PgPoint` | `point` | `X`, `Y` |
| `PgLine` | `line` | Coefficients `A`, `B`, `C` for Ax + By + C = 0 |
| `PgLineSegment` | `lseg` | Ordered `Start` and `End` points |
| `PgBox` | `box` | Normalized `High` and `Low` corners |
| `PgCircle` | `circle` | `Center` and `Radius` |
| `PgPath` | `path` | Owned vertices and `IsClosed` |
| `PgPolygon` | `polygon` | Owned vertices and `BoundingBox` |

The first five are immutable value types. Paths and polygons are sealed classes
that copy their input vertices and expose them through `IReadOnlyList<PgPoint>`
and a read-only `Points` span.

## Write a function

```csharp
[PgFunction]
public static PgBox Bounds(PgPolygon polygon) => polygon.BoundingBox;

[PgFunction]
public static PgPath ClosePath(PgPath path) => path.WithClosed(true);
```

```sql
SELECT bounds('((3,-2),(4,5),(-7,1))'); -- (4,5),(-7,-2)
SELECT close_path('[(1,2),(3,4)]');    -- ((1,2),(3,4))
```

Coordinates cross the native boundary in binary form. Conversion retains signed
zero, infinities, and NaN coordinate bits without decimal formatting.

## Construct and inspect

Constructors, indexing, formatting, box normalization, and polygon bounds work
without a PostgreSQL backend:

```csharp
var point = new PgPoint(1.5, -2.5);
var segment = new PgLineSegment(point, new PgPoint(4, 7));
var circle = new PgCircle(point, 3);
var box = new PgBox(new PgPoint(1, 9), new PgPoint(7, -2));

var path = new PgPath([point, new PgPoint(4, 7)], isClosed: false);
var polygon = new PgPolygon([new(3, -2), new(4, 5), new(-7, 1)]);
PgPoint first = polygon[0];
int count = polygon.Count;
PgBox bounds = polygon.BoundingBox;
```

Boxes and polygon bounds use PostgreSQL floating-point ordering: NaN sorts above
infinity, and signed zeroes compare equal. Paths retain their vertex order and
closure flag. Polygons retain their vertex order as supplied.

Fixed-size values use .NET record equality. This is exact coordinate/coefficient
equality, with normal `double.Equals` behavior for NaN and signed zero. PostgreSQL
geometric predicates may use tolerances, compare areas, or identify scaled line
coefficients as equivalent. Use SQL through [SPI](/spi/) for those predicates.

## Parsing and validation

Each type has `Parse` and `TryParse` methods using PostgreSQL's text input routine
on the active backend:

```csharp
PgPoint point = PgPoint.Parse("(1.5,-2.5)");
PgLine line = PgLine.Parse("[(0,0),(1,1)]");
PgPath path = PgPath.Parse("[(1,2),(3,4)]");
```

`TryParse` returns false for malformed input and coordinate range errors.
Backend-access and operational errors propagate. Invalid input leaves no error
pending: PostgreSQL 16 and later report it as a soft input error, and earlier
majors roll back an internal subtransaction. Catch other native input errors outside
`PgTransaction.RunInSubtransaction` before continuing with backend work.
`ToString()` uses invariant coordinate formatting and works on detached values.
Every geometry type also implements `IParsable<T>` and `ISpanFormattable`, so
generic parsing code and string interpolation follow the same rules. Format
strings are rejected. `ToString()` uses .NET's round-trip number format, which
can differ from PostgreSQL's output, such as `1E+20` for `1e+20`; use
`PgDatum.Create(value, context).ToPostgresString()` when you need the server's text.

`PgLine` and `PgCircle` constructors retain the supplied coefficients and radius.
PostgreSQL validates them when converting to SQL: near-zero `A` and `B`
coefficients together are invalid, as is a negative radius. In particular,
`default(PgLine)` must be initialized before it is used as a SQL line. An invalid
line or circle raises a PostgreSQL error when it is converted, not when it is
constructed.

Empty paths and polygons can be constructed and exchanged, matching pgrx's owned
geometries. An empty polygon has a zero bounding box. PostgreSQL's text parser
requires at least one point, so empty collection formatting is not a text-input
round trip.

In transaction callbacks, and in parallel operations before PostgreSQL 17, native
input errors must propagate because independent rollback is unavailable.
`TryParse` still returns false there on PostgreSQL 16 and later. See
[error recovery](/reference/execution/#errors).

## Arrays and SPI

All seven types support nullable signatures, arrays, queries, prepared
statements, sessions, cursors, and editable rows:

```csharp
PgPoint?[] points = Spi.ExecuteScalar<PgPoint?[]>(
    "SELECT ARRAY[point(1,2), NULL, point(3,4)]");

PgPolygon polygon = Spi.ExecuteScalar<PgPolygon>(
    "SELECT $1", SpiParameter.Create(new PgPolygon([new(1,2), new(3,4)])));
```

Use `PgArray<T>` to retain multidimensional shapes and nonstandard lower bounds.
Geometric arrays follow the same [array conversion rules](/arrays/) as other
scalar types. Returned paths and polygons remain valid after native storage,
result batches, or cursors are released.
