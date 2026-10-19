---
title: Numeric values
description: Use decimal or PostgreSQL's full-range numeric values without silent precision loss.
---

Use `decimal` when its range and precision fit your data:

```csharp
[PgFunction]
public static decimal AddTax(decimal amount) => amount * 1.20m;
```

Ankus maps `decimal` to PostgreSQL `numeric`. Reads must be exact: values that
would overflow, round, or underflow a decimal throw `OverflowException`. This
includes NaN and infinities. SQL NULL maps to `decimal?`.

## Full-range numeric

[`PgNumeric`](/api/ankus.pgnumeric/) retains PostgreSQL's full numeric range and
display scale, including NaN and both infinities:

```csharp
[PgFunction]
public static PgNumeric Multiply(PgNumeric left, PgNumeric right) => left * right;

[PgFunction]
public static PgNumeric? ReadNumeric(string text)
    => PgNumeric.TryParse(text, out PgNumeric value) ? value : null;
```

The default value is zero. `Parse` accepts PostgreSQL numeric syntax, including
exponents. `Text` and `ToString()` return owned, culture-independent output text;
`ToNormalizedString()` removes insignificant fractional zeroes. `Scale` reports
display scale and returns null for special values. `PgNumeric` implements
`IParsable<PgNumeric>` and `ISpanFormattable` with the same rules; format strings
such as `N2` are rejected, and culture is ignored.

Equality ignores display scale: `1.2300` equals `1.23` and their hashes agree.
As in PostgreSQL, NaN equals NaN and sorts above all other numeric values.
`IsFinite`, `IsNaN`, and the nullable `Sign` property distinguish special values.
Numeric infinities require PostgreSQL 14 or later. On PostgreSQL 13, parsing
numeric infinity text reports SQLSTATE `22P02`; converting floating-point
infinity to numeric reports the unsupported-feature code `0A000`.

Numeric values own detached binary copies. They remain valid after the SPI
session or native memory context that supplied them ends. Display text is
formatted when requested; accessing it does not require a PostgreSQL backend.

## Arithmetic and precision

Operators `+`, `-`, `*`, `/`, `%`, and unary `-` use PostgreSQL's numeric routines.
Methods include `Abs`, `Round`, `Truncate`, `Ceiling`, `Floor`, `Sqrt`, `Exp`,
`Log`, `Pow`, `GreatestCommonDivisor`, and `LeastCommonMultiple`.

`value.Log(b)` is the logarithm of `value` in base `b`, so `((PgNumeric)8).Log(2)`
is 3, as SQL's `log(2, 8)`. pgrx's `log(base)` passes its arguments to PostgreSQL in the
other order and returns the logarithm of `base` in base `value`; reverse the
operands when porting such a call.

```csharp
[PgFunction]
public static PgNumeric Price(PgNumeric value) => value.Rescale(precision: 10, scale: 2);
```

`Rescale` applies PostgreSQL's declared precision and scale, including its rounding
and range checks. PostgreSQL 15+ supports negative scales and scales larger than
precision. Earlier servers reject those scale declarations with SQLSTATE
`22023`, including declarations applied by `[PgNumericPrecision]`.
`Round` breaks ties away from zero; .NET's default decimal rounding
uses ties-to-even.

## Function constraints

Declare precision and scale on parameters and return values:

```csharp
[PgFunction]
[return: PgNumericPrecision(10, 2)]
public static PgNumeric AddTax([PgNumericPrecision(10, 2)] PgNumeric amount)
    => amount * 1.20m;
```

The input is rounded before the method runs. The return value is rounded after
the method returns, including after its `finally` blocks. Overflow raises a native
PostgreSQL range error. For `decimal` parameters, rescaling happens before checked
decimal conversion. Nullable parameters and results retain their SQL NULL behavior.

Precision must be from 1 through 1000 and scale from -1000 through 1000; omitted scale means zero.
Negative scales and scales above precision require PostgreSQL 15+. Invalid values
or an attribute on a nonnumeric type produce compiler diagnostic `ANKUS003`.

PostgreSQL discards type modifiers in function signatures. Ankus enforces these
constraints through the guarded dispatcher; they do not distinguish SQL overloads
and do not apply to direct C# method calls. Use `Rescale` for an explicit conversion
inside managed code.

Parsing and arithmetic require the active PostgreSQL backend thread. A native
error must propagate to PostgreSQL or roll back before more backend work. To
handle it and continue, catch it outside `PgTransaction.RunInSubtransaction`.
`TryParse` returns false for invalid input without leaving an error pending: on
PostgreSQL 16 and later through PostgreSQL's soft input errors, and on earlier
majors by rolling back an internal subtransaction. Text access, comparison,
hashing, and exact .NET conversions also work outside PostgreSQL.

In transaction callbacks, and in parallel operations before PostgreSQL 17, native
input errors must propagate because independent rollback is unavailable.
`TryParse` still returns false there on PostgreSQL 16 and later. See
[error recovery](/reference/execution/#errors).

## .NET conversions and SPI

`FromDecimal` and `ToDecimal` preserve both the value and the display scale, so
`FromDecimal(value.ToDecimal())` restores the same numeric. `ToDecimal` and the
explicit `decimal` cast throw `OverflowException` for NaN, infinities, values that
`decimal` would round, and display scales that `decimal` cannot carry. This
includes trailing fractional zeros beyond 28 digits or beyond decimal's 96-bit
coefficient, so `1.00000000000000000000000000000` (scale 29) is rejected.
Converting a `System.Data.SqlTypes.SqlDecimal` to `decimal` rejects scales above
28 in the same way. To accept
such values, reduce the scale explicitly with `Round`, `Truncate`, `Rescale`, or a
SQL cast such as `::numeric(38,28)`. `decimal` function parameters, SPI results
and array elements use the same checked conversion. `FromBigInteger` and
`ToBigInteger` handle finite integers within PostgreSQL's range; fractional inputs
are rejected by `ToBigInteger`.

`FromInteger<T>` and `ToInteger<T>` support .NET binary integer types, including
unsigned values, `Int128`, `UInt128`, and `BigInteger`. Narrowing is exact and
checked: fractions, negative-to-unsigned conversions, and overflow throw
`OverflowException`. These operations work without a backend.

```csharp
PgNumeric large = ulong.MaxValue;        // exact implicit conversion
ulong original = large.ToInteger<ulong>();
PgNumeric scaled = 12.3400m;             // retains decimal scale
```

Integer and decimal operands can be mixed with `PgNumeric` arithmetic. The type
also implements the .NET arithmetic, comparison, and identity operator interfaces
for generic algorithms. `PgNumeric.Sum(values)` enumerates once, uses PostgreSQL
addition, and returns zero for an empty sequence.

`ToInt16`, `ToInt32`, and `ToInt64` use PostgreSQL casts: they round ties away from
zero and raise `PgException` for out-of-range or nonfinite input. Use `ToInteger<T>`
when rounding is not acceptable.

`FromSingle`/`ToSingle` and `FromDouble`/`ToDouble` use the server's floating-point
conversion rules and may lose precision. Explicit casts between `PgNumeric` and
`float` or `double` use the same routines. Floating-point conversions and server
integer casts require the backend.

Both `PgNumeric` and `decimal` work in typed SPI queries, prepared plans, sessions,
cursors, and local row edits:

```csharp
PgNumeric? total = Spi.ExecuteScalar<PgNumeric?>("SELECT sum(amount) FROM invoices");
decimal price = Spi.ExecuteScalar<decimal>(
    "SELECT $1::numeric(10,2)", SpiParameter.Create(12.345m));
```

Untyped numeric cells contain `PgNumeric`. Reading them with `row.Get<decimal>(ordinal)`
performs the same exact conversion as a generated function adapter.

## Numeric sample

The [numeric sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Numeric)
ports pgrx's addition, parsing, random integer, arithmetic and fixed-scale
constant examples. Its
addition rounds the result after adding the full-range inputs:

```csharp
[PgFunction]
public static PgNumeric AddNumeric(PgNumeric left, PgNumeric right)
    => (left + right).Rescale(precision: 1000, scale: 33);
```

Rounding each input first changes results near the rounding boundary. For
example, adding two `4e-34` inputs and then applying scale 33 produces `1e-33`.
The sample also preserves PostgreSQL's floating-point conversion rules through
`FromSingle` and `FromDouble`. Its complete arithmetic chain returns `0.060`
with precision 10 and scale 3.

The random example converts all 128 signed integer bits directly into an owned
numeric value. Parsing and precision errors retain PostgreSQL's SQLSTATE and
diagnostic fields through `PgException`, such as `22P02` for invalid input and
`22003` for a value out of range. pgrx's example unwraps those results, so its
failures panic and report `XX000`.

## JSON serialization

`PgNumeric` serializes as a JSON string, retaining scale and full precision even
when the consumer uses floating-point numbers. NaN and infinities use strings too.
The converter accepts either a string or an unquoted JSON number on input; number
tokens pass directly to PostgreSQL's parser without conversion through `double`
or `decimal`.

Use a source-generated `JsonSerializerContext` and explicit `JsonTypeInfo<T>`
metadata, as in the [JSON guide](/json-and-uuid/). Writing an owned numeric value
works outside PostgreSQL. Deserialization requires the active backend thread.
Invalid input throws `JsonException` with its property path, retaining a native
`PgException` as the inner exception when applicable. JSON null requires
`PgNumeric?`.
