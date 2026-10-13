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

## Applying replicated changes

The replication benchmark applies one backlog of changes through PostgreSQL's
built-in subscriber and through the
[subscriber sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.Subscriber),
which uses [relation writers](/applying-changes/) and
[replication connections](/replication-connections/), on the same PostgreSQL
18.6 cluster (Linux x64, `fsync = off`). Every row is inserted and then updated;
timing runs from releasing a lock that held the changes back until the
subscriber matches the provider.

| Workload | Built-in subscriber | Subscriber sample |
|---|---:|---:|
| 100,000 rows in transactions of 1,000 | 268,000 changes/s | 225,000 changes/s |
| 100,000 rows in single-row transactions | 90,000 changes/s | 100,000 changes/s |

A remote transaction costs the sample about 10 µs, a little less than the
built-in subscriber. Two things got it there from 20 µs. Writers for a table
share a descriptor until the table changes, so opening one no longer copies the
table's columns, and the sample keeps its column map and rows while the
descriptor stays the same. And the sample commits with `synchronous_commit` off,
as the built-in subscriber and pglogical do; on a server with `fsync` on, a
synchronous commit per remote transaction costs far more. Large transactions
apply at about 84% of the built-in rate because the sample looks for an
existing row before each insert, as pglogical's conflict handling does.

Inside PostgreSQL, opening and closing a writer takes about 0.5 µs once its
descriptor is shared (1.7 µs when it loads one), and inserting a row in
replication's text form takes about 2 µs, against about 7 µs for the same
`INSERT` through SPI.

## Keeping hot paths cheap

- Prefer scalar types where a value crosses often. Text is converted between
  the server encoding and UTF-16 on every crossing, and a composite row
  converts every column.
- A `string` argument is the cheapest way to receive short text. A borrowed
  `PgTextView` checks its lifetime, which costs more than decoding a few bytes,
  and pays off for large text or text read only in part.
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
