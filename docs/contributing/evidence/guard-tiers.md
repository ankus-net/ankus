# Guard tier measurements

`tests/Ankus.GuardTierBenchmarks` measures each native guard tier inside
PostgreSQL with `ankus bench`, so a change to the guards can be compared with
these figures. Run it from that directory:

```console
ankus bench --pg 18 --group-name pg18
```

## PostgreSQL 18.6 release build

Linux x64 with 10 cores, PostgreSQL 18.6 from the distribution's packages,
measured on 2026-10-10 at commit 503908e plus the namespace move. Each figure is
the estimated time per call, with its 95% confidence interval.

| Benchmark | Tier | Time per call |
|---|---|---:|
| `LogFilteredDebug` | Report below every minimum message level | 188.7 ns (188.3–189.3) |
| `NumericParse` | Lightweight guard around an input function | 599.0 ns (598.2–600.1) |
| `NumericAdd` | Lightweight guard around numeric addition | 717.8 ns (714.9–721.3) |
| `NumericAddInSubtransaction` | The same addition in an explicit subtransaction | 1.192 µs (1.190–1.195) |
| `SpiSelectOne` | SPI statement in its internal subtransaction | 2.891 µs (2.883–2.901) |
| `NumericTryParseInvalid` | Soft input error recovered without rollback | 5.508 µs (5.488–5.533) |

An explicit subtransaction adds about 0.47 µs to the lightweight numeric
addition. The invalid `TryParse` figure includes raising and catching a managed
`PgException` inside `TryParse`.

## Assertion builds

`ankus init` builds PostgreSQL from source with assertion checking and
allocation randomization, as `cargo pgrx init` does, so these figures are only
comparable within the same build.

| Benchmark | 13.23 | 16.15 |
|---|---:|---:|
| `LogFilteredDebug` | 190.2 ns | 188.0 ns |
| `NumericParse` | 1.128 µs | 1.256 µs |
| `NumericAdd` | 1.231 µs | 1.322 µs |
| `NumericAddInSubtransaction` | 2.019 µs | 2.264 µs |
| `SpiSelectOne` | 8.048 µs | 8.911 µs |
| `NumericTryParseInvalid` | 6.913 µs | 6.566 µs |

Benchmark iterations run inside an explicit scope. Before PostgreSQL 16,
`TryParse` recovers invalid input by nesting its own subtransaction in that
scope; from 16, the soft input error needs none.

## SQL call boundary and direct reads

The call boundary was measured on 2026-10-11 with a scratch extension of
trivial functions, called two million times from SQL on the PostgreSQL 16.15
that `ankus init` builds (Linux x64, 10 cores). Like `cargo pgrx init`, it
configures `--enable-debug --enable-cassert`, so each allocation is also filled
with random bytes, which inflates allocation-heavy paths. Each figure is the
cost per call above the same query calling a C built-in, or selecting the input
alone.

| Function | Before | After |
|---|---:|---:|
| `int AddOne(int)` against `int4pl` | 51 ns | 27 ns |
| `string Echo(string)` | 171 ns | 148 ns |
| `int Length(PgTextView)` | 2.96 µs | 0.41 µs |

The scalar change stopped zero-filling two 2.8 KB error buffers on every call
(commit 8ff1294). The view parameter cost fell when flat values began to be
borrowed in place (2f9d971) and when a borrowed input captured its lifetime in
one native call instead of three (aecc2c9). In-backend benchmarks of the same
build give 154 ns for creating and disposing a text view over a datum, down from
2.33 µs, and 185 ns for `PgDatum.Read<string>()`, down from 1.28 µs; a guarded
datum operation runs in its own subtransaction, which is what those paths no
longer need for flat values.

The same queries on the distribution's PostgreSQL 18.6 release build, after
these changes:

| Function | Cost per call |
|---|---:|
| `int TextLength(string)` over selecting the input | 70 ns |
| `string Echo(string)` | 148 ns |
| `string MakeText(int)` against `textin(int4out(g))` | 95 ns |
| `int ViewLength(PgTextView)` | 0.43 µs |

### Aggregate transitions

A sum aggregate over `int` with a `long` state cost 1.9 µs per row on the
assert-enabled 16.15 build and 333 ns on the 18.6 release build, above the
built-in `sum(int)` over the same two million rows. Each transition rebuilt its argument metadata and created and
deleted a memory context. The wrapper now keeps that metadata, its buffers and
one scratch context in the call site's `fn_extra`, as PostgreSQL's own
aggregates cache per-call-site state, and resets the scratch context after each
row. The release-build cost fell to 170 ns per row. A nested call through the
same site while one is running uses a private context as before. Sharing one
empty sort-key list among aggregates without `ORDER BY`, instead of allocating
and wrapping a copy for every row, brought it to 156 ns.

### Sets, arrays and SPI

Measured the same way on the 18.6 release build, against the built-in
equivalent: `generate_series`, a `SELECT` of the same columns, or a PL/pgSQL
loop.

| Path | Cost |
|---|---:|
| `IEnumerable<int>` set, per row | 77 ns |
| `IEnumerable<(int, string)>` table, per row | 208 ns |
| `Spi.ExecuteScalar<int>("SELECT 1")`, per query | 2.86 µs, against 2.0 µs for PL/pgSQL `EXECUTE` |
| `Spi.Select` of one `int` column, per row | See below |
| `int[]` argument, per element | 1 ns, down from 57 ns |
| `int[]` result, per element | about 1 ns |
| `int[]` argument or result, per call | 196 ns or 185 ns, down from 240 ns and 400 ns |
| `string[]` argument of short text, per element | 34 ns, down from 90 ns |
| `string[]` result of short text, per element | 72 ns |

`Spi.Select` copies every row into managed memory, so its cost depends on how
many rows one result retains. Reading `generate_series` rows and summing them
costs 106 ns per row in total as 2,000 queries of 1,000 rows, including each
query and `row.Get<int>(0)`, against 210 ns for a PL/pgSQL `FOR` loop over one
two-million-row query. One two-million-row `Spi.Select` costs 290 ns per row,
because the garbage collector promotes the retained rows; read results that
large through a cursor. Reading a cell, or an array element, that already has
the requested type now skips the four registry lookups that rule out
`[PgDatumType]` mappings, which took the batched cost down from 134 ns.

Arrays crossed the boundary as one 28-byte big-endian record per element,
which native code wrote and managed code parsed, boxing each value. PostgreSQL
stores `bool`, `"char"`, `int2`, `int4`, `int8`, `oid`, `float4` and `float8`
elements as a packed native array, and an array without NULLs keeps them
contiguous, so both directions now copy that block once. Arrays with NULLs,
domain elements and other element types keep the per-element form. A function
returning a vector of one of these primitives writes the block directly,
without building a `PgArray<T>` and looking up its type mappings, and a vector
argument is read without one.

In a UTF-8 database, `text`, `varchar` and `bpchar` arrays cross as each
element's length and bytes, since neither side needs to transcode them, and
reading an element of the requested type skips the general conversion's
registry lookups. Other databases keep the per-element form, which converts
each element to and from the database encoding.
