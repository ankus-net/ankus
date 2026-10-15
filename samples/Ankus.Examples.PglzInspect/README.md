# PGLZ inspection example

This ports pgrx's `pglz_inspect` example. It helps DBAs decide whether to
enable PGLZ compression on a column: it samples real rows, runs each value
through PostgreSQL's own `pglz_compress`, and reports the compression ratio,
how many values PGLZ accepts, the estimated disk savings and a recommendation.
It supports PostgreSQL 13–19.

| Function | Purpose |
| --- | --- |
| `pglz_size(bytea)` | Probe one value: raw size, compressed size, ratio and whether PGLZ accepted it. |
| `pglz_analyze_column(tbl oid, col text, sample_size int DEFAULT 1000, strategy text DEFAULT 'default')` | Sample rows and report averages, shares and estimated savings from `pg_class.reltuples`. |
| `pglz_ratio_histogram(tbl oid, col text, sample_size int DEFAULT 1000)` | Count per-row ratios in five buckets plus `incompressible`. |
| `pglz_recommend(tbl oid, col text, sample_size int DEFAULT 1000)` | Return a RECOMMEND, MARGINAL or SKIP verdict with ready-to-run DDL. |

Table arguments are OIDs, as in pgrx; pass them as `'name'::regclass`.
`strategy` is `default` (PostgreSQL's TOAST strategy: at least 32 bytes and 25%
savings) or `always`.

```sql
CREATE EXTENSION ankus_pglz_inspect;

CREATE TABLE events AS
SELECT i AS id,
       ('{"user":'||i||',"action":"click","meta":'||repeat('"x",',50)||'1}') AS payload
FROM generate_series(1, 10000) i;

SELECT * FROM pglz_ratio_histogram('events'::regclass, 'payload');
--      bucket     | row_count
-- ----------------+-----------
--  0.0-0.2        |         0
--  0.2-0.4        |      1000
--  0.4-0.6        |         0
--  0.6-0.8        |         0
--  0.8-1.0        |         0
--  incompressible |         0

SELECT pglz_recommend('events'::regclass, 'payload');
-- RECOMMEND: PGLZ saves ~78% on events.payload (1000 of 1000 sampled rows accepted).
-- Run: ALTER TABLE events ALTER COLUMN payload SET COMPRESSION pglz;
```

These results are from PostgreSQL 18.6; numbers vary with the data and the
random sample. The recommendation is one line; it is wrapped above for
readability. pgrx's README shows illustrative numbers instead, including
histogram counts larger than the default sample.

## Histogram buckets

`pglz_ratio_histogram` reports `compressed_size / raw_size` for each sampled row:

| Ratio | Bucket | Meaning |
| --- | --- | --- |
| 0.00–0.20 | `0.0-0.2` | Excellent: about 80% or more saved |
| 0.20–0.40 | `0.2-0.4` | Good |
| 0.40–0.60 | `0.4-0.6` | Moderate |
| 0.60–0.80 | `0.6-0.8` | Weak |
| 0.80–1.00 | `0.8-1.0` | Negligible |
| Rejected by PGLZ | `incompressible` | Short or high-entropy values |

Highly repetitive data clusters in `0.0-0.2`; short or random data is
`incompressible`. A histogram with peaks in both means the column mixes very
compressible and very random rows. pgrx's README includes a mixed-distribution
table to spread rows across buckets:

```sql
CREATE TABLE mixed AS
SELECT i AS id,
       CASE (i % 5)
         WHEN 0 THEN repeat('aaaaa', 100)
         WHEN 1 THEN '{"user_id":'||i||',"event_type":"page_view","timestamp":"2024-01-'||(i%28+1)||'","session":"'||md5(i::text)||'"}'
         WHEN 2 THEN repeat(md5(i::text), 3) || md5((i+1)::text)
         WHEN 3 THEN 'log_entry_' || md5(i::text) || md5((i+1)::text) || md5((i+2)::text)
         ELSE (SELECT string_agg(md5(random()::text), '') FROM generate_series(1, 4))
       END AS payload
FROM generate_series(1, 5000) i;

SELECT * FROM pglz_ratio_histogram('mixed'::regclass, 'payload', 5000);
```

PGLZ's default strategy rejects values that save less than 25%, so the
`0.8-1.0` bucket stays empty. On PostgreSQL 18.6 this table has 1000 rows in
`0.0-0.2`, 1000 in `0.4-0.6` and 3000 `incompressible`: the short JSON, the log
entries and the random digests do not save enough to be accepted.

## Recommendations

| Verdict | Condition |
| --- | --- |
| `RECOMMEND` | At least 80% of sampled rows accepted and an average ratio of at most 0.70 |
| `SKIP` | Fewer than 30% accepted or an average ratio above 0.90 |
| `MARGINAL` | Anything else |
| `NO DATA` | No non-NULL rows were sampled |

The DDL uses `ALTER TABLE ... SET COMPRESSION pglz`, available from
PostgreSQL 14. On PostgreSQL 13, the message suggests a trigger instead.
Percentages and ratios are rounded like pgrx: the exact value rounded half to
even.

## pgrx mapping

| pgrx | Ankus |
| --- | --- |
| `pglz::compress_into(src, dest, strategy) -> Result<Option<usize>, PglzError>` | `Pglz.TryCompress(source, destination, strategy, out written)` |
| `pglz::compress`, `decompress`, `decompress_into`, `max_output` | `Pglz.Compress`, `Decompress`, `MaxOutput` |
| `PglzError::Decompress` | `InvalidDataException` |
| `PglzError::BufferTooSmall` | `ArgumentException` |
| `PglzError::InputTooLarge` | `ArgumentOutOfRangeException` for a negative raw size |
| `PglzError::Allocation` | `OutOfMemoryException` from the managed allocation |
| `pg_sys::pglz_compress`, `pglz_decompress` | `NativeMethods.pglz_compress`, `pglz_decompress` |
| `pg_sys::PGLZ_strategy_default`, `PGLZ_strategy_always` | `NativeGlobals.PGLZ_strategy_default`, `PGLZ_strategy_always` |
| `&mut [MaybeUninit<u8>]` scratch buffers | Reused `byte[]` buffers and `Span<byte>` |
| `Spi::connect` and `client.select(sql, None, args)` | `Spi.Select(sql, SpiParameter.Create(limit))` |
| `spi::quote_identifier` | `Spi.QuoteIdentifier` and `Spi.QuoteQualifiedIdentifier` |
| `RelationIdGetRelation(tbl)` and `rd_rel->reltuples` | `pg_class.reltuples`, read with the relation's names through SPI |
| `TableIterator` with `name!` columns | `IEnumerable` of named C# tuples |
| `default!(i32, 1000)` | C# optional parameters |
| `error!(...)` | `PgException` with SQLSTATE `22023` |
| `cfg!(feature = "pg13")` | `#if ANKUS_PG13` |
| 24 `#[pg_test]` functions | 24 `[PgTest]` methods in `PglzInspectTests` |

## Deliberate differences

- PostgreSQL 13 does not mark `PGLZ_strategy_default` and `PGLZ_strategy_always`
  as `PGDLLIMPORT`, so an extension cannot import them on Windows. On PostgreSQL
  13 the sample builds both strategies from the values in PostgreSQL 13's
  `pg_lzcompress.c`; PostgreSQL 14 and later use the server's own globals.
- pgrx reads non-`bytea` columns with `col::text::bytea`, which parses the text
  as `bytea` input. Text containing backslashes, such as Windows paths or JSON
  escapes, then fails with `invalid input syntax for type bytea` or is measured
  as different bytes. Ankus reads `convert_to(col::text, getdatabaseencoding())`:
  the text's bytes in the database encoding, which is exactly what TOAST
  compresses for a text column.
- An unknown strategy reports SQLSTATE `22023` (`invalid_parameter_value`)
  instead of pgrx's `XX000`, with pgrx's message. Some clients, including
  Npgsql, close a connection after an internal error.
- pgrx builds the sampling query from the `regclass` text of an arbitrary OID; a
  missing relation then fails as a syntax error. Ankus looks the relation up in
  `pg_class` first and reports `relation with OID ... does not exist` with
  SQLSTATE `42P01`. Messages still show pgrx's `regclass` text; the query uses
  the schema-qualified, quoted name.
- pgrx reads `reltuples` through the relation cache without a lock. Ankus reads
  it from `pg_class` with the relation's names, which is the same value.
- Rust sizes are `usize`, so pgrx tests a raw size above `i32::MAX`. C# spans
  and arrays are indexed by `int`; the equivalent invalid input is a negative
  raw size. `MaxOutput` saturates at `int.MaxValue`, and `TryCompress` checks
  the exact required capacity.
- pgrx's tests catch a Rust panic for the unknown strategy. The Ankus backend
  test declares the exact expected PostgreSQL error message.

The integration tests run all 24 backend tests and compare compressed sizes with
PostgreSQL's own TOAST compression, which stores a PGLZ value inline with its
compressed length plus an eight-byte header. They also cover quoted names,
`bytea`, JSON and backslash text, a LATIN1 database, privileges, invalid input
and recovery, row estimates before and after `ANALYZE`, and running the
recommended DDL.

See [native PostgreSQL declarations](https://ankus.net/raw-values/#native-postgresql-declarations)
and [SPI queries](https://ankus.net/spi/).
