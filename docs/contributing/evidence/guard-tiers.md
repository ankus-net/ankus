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
trivial functions, called two million times from SQL on a PostgreSQL 16.15
release build (Linux x64, 10 cores). Each figure is the cost per call above the
same query calling a C built-in, or selecting the input alone.

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
