---
title: Benchmarks
description: Measure extension code inside PostgreSQL and compare retained benchmark runs.
---

Ankus benchmarks run in the PostgreSQL backend that owns the extension. This
keeps SPI, datum conversion, memory contexts and PostgreSQL functions inside
their real execution environment.

Declare an accessible synchronous static `void` method with one `PgBencher`
parameter:

```csharp
public static class ExtensionBenchmarks
{
    [PgBenchmark]
    public static void AddNumeric(PgBencher bencher)
    {
        PgNumeric left = PgNumeric.Parse("123.45");
        PgNumeric right = PgNumeric.Parse("67.89");

        bencher.Iterate(() => left + right);
    }
}
```

The method must register exactly one timing loop. `Iterate` reuses captured
inputs. `IterateBatched` creates each input immediately before its routine call.
The input factory is outside the routine callback but inside the elapsed sample,
matching pgrx's backend benchmark bridge. `PgBenchmarkBatchSize` controls grouping.
Use `PgBenchmark.BlackBox` to keep Native AOT optimization from discarding a
value. It accepts ref structs and does not box value-type inputs.

Benchmark entry points and the `benches` schema exist only in benchmark
publications. Normal build, install and package output excludes them.

## Run benchmarks

Run every benchmark in the selected extension:

```console
ankus bench --pg 18
```

Supply an ordinal substring to select benchmark names:

```console
ankus bench AddNumeric --pg 18
```

Benchmark against a PostgreSQL built without assertions. `ankus init --pg18
download` builds with them, as `cargo pgrx init` does, because they catch mistakes
while you develop, but they also slow the whole server, so `ankus bench` warns
when the server reports `debug_assertions = on`. Build one for benchmarking with
`ankus init --pg18 download --release`, or register a distribution's package.

Results print in Criterion's layout, as `cargo pgrx bench` prints them:

```console
     Running ExtensionBenchmarks.AddNumeric(Ankus.PgBencher) [transaction=shared, setup=none, sample_size=100, warm_up=3000ms, measurement=5000ms, nresamples=100000, noise_threshold=0.01, significance_level=0.05]
ExtensionBenchmarks.AddNumeric(Ankus.PgBencher)
                            time:   [182.31 ns 183.02 ns 183.8 ns]
                            change: [-1.2045% +0.3821% +1.9712%] (p = 0.64 > 0.05)
                            No change in performance detected.
                            slope:  [182.31 ns 183.02 ns 183.8 ns]
                            mean:   [183.4 ns 184.27 ns 185.32 ns] std. dev. [3.1902 ns 4.6121 ns 6.0117 ns]
                            median: [182.91 ns 183.5 ns 184.12 ns] med. abs. dev. [1.9834 ns 2.6705 ns 3.3122 ns]

Bench group 20261009_121530_ac6486e
   Compared 20261008_181204_805783b
    Result 1 total, 1 ok, 0 failed
```

The time is the slope estimate when the sampling plan produced one, otherwise the
mean; intervals are 95% confidence intervals. When the compared group has no run
of a benchmark, or its run failed, the change line says so instead. When a compared
group ran benchmarks that this run did not, the summary lists them under
`Missing From Current Run`.

`--list` discovers benchmarks without measuring them. Each run gets a timestamp
and Git commit group name unless `--group-name` supplies one. By default, results
compare with the latest retained group from the same build configuration.
`--compare-group` selects a named group instead.

`--report` reads retained history without rebuilding. Each benchmark gets a section
showing its ten most recent groups against its first successful run:

```console
Bench history report
  Database acme_benches
     Scope last 10 groups per benchmark

ExtensionBenchmarks.AddNumeric(Ankus.PgBencher)
  baseline: 20261008_181204_805783b (183.02 ns)
  20261008_181204_805783b  |############################| 183.02 ns (baseline)
  20261009_121530_ac6486e* |########################### | 176.4 ns (-3.6171%)
  * broad drift vs baseline in: pg_settings
```

A `*` marks a group whose build configuration, PostgreSQL major version, runtime
identifier or non-default server settings differ from the baseline's, so its change
may not come from your code. Failed runs and runs without an estimate are counted
under the section instead of drawn. Each group also records its Git commit and the
tool version. Add `--json` to a report for the raw results of the latest ten runs.

The default database is `<extension>_benches`. Use `--database` to select another
name and `--resetdb` to recreate it, which discards retained history. Each run
recreates the extension; add `--cascade` when your own objects in that database
depend on it. The runner owns an `ankus_bench` schema in
that database for history. Extension setup and measured mutations execute inside
an enclosing transaction that is always rolled back; retained results are written
after that rollback.

## Configure measurement

`PgBenchmarkAttribute` uses the same defaults as pgrx: 100 samples, five seconds
of measurement, three seconds of warmup, 100,000 statistical resamples, a one
percent noise threshold and a five percent significance level. Sample size must
be at least ten, measurement and warmup times must be positive, and the noise
threshold must be finite and nonnegative.

```csharp
[PgBenchmark(
    Setup = nameof(Prepare),
    Transaction = PgBenchmarkTransactionMode.SubtransactionPerBatch,
    SampleSize = 50,
    MeasurementTimeMilliseconds = 2_000,
    WarmupTimeMilliseconds = 1_000)]
public static void InsertRows(PgBencher bencher)
    => bencher.IterateBatched(
        static () => 42L,
        static value => Spi.Execute(
            "INSERT INTO measurements(value) VALUES ($1)",
            SpiParameter.Create(value)),
        PgBenchmarkBatchSize.SmallInput);

internal static void Prepare()
    => Spi.Execute("CREATE TABLE measurements(value bigint NOT NULL)");
```

`Setup` identifies one accessible synchronous static parameterless `void` method.
It runs once before warmup and is outside measured intervals.

Set `Throughput` and `ThroughputPerIteration` to report a rate as well as a time,
as Criterion's `Throughput` does. The amount is the work one iteration performs:

```csharp
[PgBenchmark(Throughput = PgBenchmarkThroughput.Bytes, ThroughputPerIteration = 8_192)]
public static void Compress(PgBencher bencher) => bencher.Iterate(() => Compressor.Run(Page));
```

Results then print a `thrpt:` line under the time, such as
`thrpt:  [887.78 MiB/s 976.56 MiB/s 1085.07 MiB/s]`. `Bytes` uses binary units,
`BytesDecimal` decimal ones such as MB/s, and `Elements` counts elem/s.

The default `Shared` transaction mode measures work in the benchmark's enclosing
transaction. `SubtransactionPerBatch` adds one internal subtransaction around
each batch. `SubtransactionPerIteration` isolates every iteration and includes
that cost in the measurement. These modes release the subtransaction, so rows a
benchmark inserts or deletes are still there for the next iteration.
`RollbackPerBatch` and `RollbackPerIteration` roll the subtransaction back
instead, so every batch or iteration starts from the same data. The rollback is
part of the measured time, and aborting a subtransaction that wrote rows costs
more than releasing one.

Results include the selected sampling mode, raw iteration counts, elapsed
nanoseconds and bootstrap confidence intervals for mean, median, median absolute
deviation and standard deviation. Linear sampling also includes Criterion's
through-origin slope estimate, which the command reports as the primary estimate.
Flat sampling reports the mean as its primary estimate.

Like pgrx's Criterion runner, Ankus uses the complete exponential warmup to select
an automatic linear or flat iteration plan. It then measures every planned sample
exactly once and retains its actual iteration count and duration. Short positive
samples are retained rather than retried. A zero-duration sample or routine
exception ends the benchmark; it is not retried. As in Criterion, a zero-duration
sample is rejected after the complete planned measurement sequence runs.
A comparison reports an improvement or regression only when the measured change
is statistically significant and its confidence interval lies beyond the configured
noise threshold.

## Declaration errors

Each rule has its own diagnostic, reported at the name, modifier, return type,
parameter or containing type that breaks it:

| Diagnostic | Correction |
| --- | --- |
| `ANKUS130` | Make the method static. |
| `ANKUS527` | Remove `async`; measure work that completes before the method returns. |
| `ANKUS528` | Remove the method's type parameters. |
| `ANKUS529` | Give the method a body. |
| `ANKUS530` | Return `void` and register the measured routine with the `PgBencher`. |
| `ANKUS531` | Make the method `public`, `internal` or `protected internal`. |
| `ANKUS532` | Declare exactly one `PgBencher` parameter. |
| `ANKUS533` | Pass the `PgBencher` by value. |
| `ANKUS534` | Declare benchmarks in non-generic, non-file-local public or internal classes. |
| `ANKUS131` | Name one accessible synchronous static `void` setup method without parameters. The message names the first problem. |
| `ANKUS132` | Correct the reported measurement or throughput option. |
| `ANKUS133` | Keep benchmarks separate from functions, tests, initialization and workers. |

Editors offer **Make static** for `ANKUS130`, **Make synchronous** for
`ANKUS527`, **Make internal** for `ANKUS531` and `ANKUS534`, **Add the
parameter** or **Change the type** for `ANKUS532`, and **Pass by value** for an
`in` or `ref readonly` bencher (`ANKUS533`).

The [benchmark sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Benchmarks)
shows value operations, batched setup and SPI writes.
