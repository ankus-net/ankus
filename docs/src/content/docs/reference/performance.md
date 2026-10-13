---
title: Boundary costs
description: What crossing between PostgreSQL and managed code costs, and how to keep hot paths cheap.
---

Every call into an extension crosses from PostgreSQL's C code into managed code
and converts values in both directions. The figures below come from a
PostgreSQL 18.6 release build on one Linux x64 machine. Use them to compare
approaches, not as guarantees, and measure your own code with
[benchmarks](/benchmarks/).

| Path | Cost |
|---|---:|
| `int AddOne(int)`, per call | 27 ns |
| `string Echo(string)`, per call | 97 ns |
| `IEnumerable<int>` set function, per row | 50 ns |
| `IEnumerable<(int, string)>` table function, per row | 142 ns |
| Aggregate transition from `int` into a `long` state, per row | 95 ns |
| `Spi.Select` of one `int` column in 1,000-row queries, per row including the query | 93 ns |
| Reading a Boolean, integer, real or enum setting property | 24 ns |
| A managed custom scan row with one `bigint` column | 182 ns |
| A function that reads a composite row and returns one, per call | 1.12 µs |
| A `BEFORE INSERT` row trigger that trims one text column, per row | 0.90 µs, against 0.54 µs for PL/pgSQL |
| A `BEFORE INSERT` row trigger that only checks its row, per row | 0.55 µs |

## Keeping hot paths cheap

- Prefer scalar types where a value crosses often. Text is converted between
  the server encoding and UTF-16 on every crossing, and a composite row
  converts every column.
- Return many rows from one call. A set or table function pays the call
  boundary once and then a per-row cost like those above, while separate calls
  pay it each time.
- Read large query results through a cursor. `Spi.Select` keeps every row in
  managed memory, and one two-million-row result costs about three times as much
  per row as the same rows read in batches.
- Setting properties read PostgreSQL's variable directly, and a string
  property converts its value only after it changes, so reading a setting in a
  loop is cheap.
- In a custom scan, check
  [`IsColumnRead`](/custom-scans/#managed-providers) in `Begin` and skip
  columns the query does not read.
- A row trigger converts the row it receives. A `BEFORE` trigger that returns
  `context.New` hands PostgreSQL back the cells it did not replace, so only the
  replaced cells are converted, and a validating trigger that replaces none
  costs least. Returning a new row converts every cell.
