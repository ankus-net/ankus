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

A scalar function's text result was encoded into its own native allocation,
copied into PostgreSQL memory by the wrapper, and then freed through a callback
back into managed code. Native code copies the bytes before any managed code
runs again, so the result is now encoded into one buffer each thread reuses,
with nothing to free. With the two builds alternated, best of four runs of two
million calls, `string Echo(string)` went from 124 ns to 97 ns per call over
reading the value.

A borrowed `PgTextView` argument made two native memory calls, one to capture
the input's context and one to check it again in the view's constructor, and
each call cleared a 2 KB error record and a recovery frame that holds another.
The memory entry now clears only the record's fields, as the call boundary
does, sets only the link and flag of a frame it uses, and the view's first
check trusts the generation it has just read. `int ViewLength(PgTextView)` went
from 0.42 µs to 0.28 µs per call with the two builds alternated; every native
memory call is cheaper by the same clearing.

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
and wrapping a copy for every row, brought it to 156 ns. Each returned state
also checked its type and resolved its base type in the catalog caches; the
call site now resolves them once, which brought the cost to 124 ns. Reading
each argument resolved its base type again; resolving the arguments once as
well brought it to 95 ns.

### Sets, arrays and SPI

Measured the same way on the 18.6 release build, against the built-in
equivalent: `generate_series`, a `SELECT` of the same columns, or a PL/pgSQL
loop.

| Path | Cost |
|---|---:|
| `IEnumerable<int>` set, per row | 50 ns, down from 77 ns |
| `IEnumerable<(int, string)>` table, per row | 142 ns, down from 208 ns |
| `Spi.ExecuteScalar<int>("SELECT 1")`, per query | 2.86 µs, against 2.0 µs for PL/pgSQL `EXECUTE` |
| `Spi.Select` of one `int` column, per row | See below |
| `int[]` argument, per element | 1 ns, down from 57 ns |
| `int[]` result, per element | about 1 ns |
| `int[]` argument or result, per call | 196 ns or 185 ns, down from 240 ns and 400 ns |
| `string[]` argument of short text, per element | 34 ns, down from 90 ns |
| `string[]` result of short text, per element | 31 ns, down from 75 ns |

Each set row used to check that every column's type exists and resolve its base
type in the catalog caches before converting the value. A call's column types
cannot change while it runs, so they are resolved once when the set starts;
domain columns still run their checks on every value.

`Spi.Select` copies every row into managed memory, so its cost depends on how
many rows one result retains. Reading `generate_series` rows and summing them
costs 93 ns per row in total as 2,000 queries of 1,000 rows, including each
query and `row.Get<int>(0)`, against 210 ns for a PL/pgSQL `FOR` loop over one
two-million-row query. One two-million-row `Spi.Select` costs 290 ns per row,
because the garbage collector promotes the retained rows; read results that
large through a cursor. Reading a cell, or an array element, that already has
the requested type now skips the four registry lookups that rule out
`[PgDatumType]` mappings, which took the batched cost down from 134 ns to
106 ns, and converting each cell from its column's already resolved base type,
without looking it up again, took it to 93 ns.

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
each element to and from the database encoding. Writing such an array used to
look up each element's type twice, copy it into its own datum and copy it again
into the array. Elements without a domain are now checked as UTF-8 in one pass
and laid out in the array in a second, so a result element costs 31 ns instead
of 75 ns; domain elements keep their per-element checks.

### Managed custom scans

`SELECT count(*)` over the `NumberScan` sample's tables, with one million rows
and `enable_seqscan` off, on the 18.6 release build. `count(*)` over
`generate_series` costs 75 ns per row on the same server. `perf` is not
available in the validation VM, so the costs were found by sampling the
backend's stacks with gdb.

| Table | First build | Sample reads its setting once | Framework changes |
|---|---:|---:|---:|
| `value bigint` | 650 ns | 302 ns | 182 ns |
| `value bigint, label text` | 840 ns | 492 ns | 330 ns |

The sample read its `numbers_fail_at` setting on every row, and each read is a
backend call. It now reads the setting when the scan begins. Two framework
costs followed. Every cell searched all of the extension's generated type
mappings for one that owns the column's type; a built-in type OID is below
PostgreSQL's `FirstNormalObjectId`, so no mapping can own it and the search is
skipped. `PgTupleDescriptor.CreateTuple` built an empty array, then validated
and copied it; it now creates the empty tuple directly. The rest is spread across
the checks in `PgHeapTuple.Set` and the conversion of each cell.

Reading a scalar setting property was a backend call: it encoded the name,
searched the declarations, and created and deleted a memory context for the
value. Reading the sample's integer setting on every row added about 350 ns. A
Boolean, integer, real or enum property now finds its native variable on the
first read and reads that memory afterwards, after the same managed access
checks, which brings the per-row read to 24 ns. Text settings still convert from
the server encoding on each read.

### Row triggers

Inserting one million rows into a two-column table, in one session, best of six
runs on the 18.6 release build: the insert alone takes 620 ms, a PL/pgSQL
`BEFORE INSERT` trigger that trims a name adds 0.56 µs per row, and the
`ankus_triggers` sample doing the same added 3.24 µs.

Each call converted the trigger, table and schema names, rebuilt the trigger's
argument array, serialized the table's columns and resolved every column's base
type, then checked the returned row against the catalog the same way. None of
that changes while a statement runs, and PostgreSQL keeps one `FmgrInfo` per
trigger and result relation, so the call site now keeps it in `fn_extra`.
Managed code also parsed the same names, arguments and columns for every row,
and each row built its own descriptor. The native site now sends an identity
with buffers that stay the same, so managed code keeps what it parsed for the
site and rows reuse its descriptor. Together these brought the overhead to
1.42 µs per row, 2.5 times the PL/pgSQL trigger's. Writing the returned row
encoded every column name again, tracked the row for cycles even when no cell
could hold another row, allocated a new buffer, and copied each text cell
through its own native buffer. Names are now encoded once per descriptor, only
rows with nested values are tracked, the outermost row reuses a buffer, and
text is encoded straight into it, which brought the overhead to 1.32 µs.

A trigger that only validates and returns its NEW row still had that row
converted to native code and rebuilt. When a `BEFORE` trigger returns the
context's own NEW row with no replaced cells and no mutable values, such as
arrays that could change in place, PostgreSQL now keeps the row it passed in, as
a C trigger returning `tg_trigtuple` does. With the sample changed to validate
the name without trimming it, the overhead fell from 1.21 µs to 0.76 µs per row.

Returning a row also applied each column's type modifier, such as `varchar(64)`,
by building a coercion expression, preparing it in a new executor state and
running it, once per value. PostgreSQL applies a modifier in an assignment by
calling the type's length coercion function from `pg_cast`, so Ankus now calls
that function directly; domains and arrays keep the general path. Inserting
200,000 rows into a table with three `varchar` columns, best of four runs, the
managed trigger went from 5.0 µs to 3.1 µs per row, against 1.3 µs for the
PL/pgSQL trigger.

A value of a domain type also went through a prepared coercion expression. It
now gets the base type's modifier as above and then PostgreSQL's
`domain_check`, which PL/pgSQL uses for domain results. `domain_check` keeps the
domain's constraint state between calls and refreshes it when the domain
changes, so the backend keeps that state for up to 16 domains. With a
`CHECK`-constrained text domain column, best of four runs of 200,000 rows each,
the managed trigger took 510 ms against 608 to 633 ms before, while the PL/pgSQL
trigger took 324 to 354 ms in both.

Sampling the backend's stacks with gdb showed that much of the trimming
trigger's remaining overhead was the row format itself. Each row went out with
every column's name, types, modifier, collation and flags, which native code
serialized and managed code parsed and compared, and the returned row came back
the same way. The call site
already gives both sides the relation's columns, so a trigger row now travels as
one value per column. A returned NEW row marks each cell the trigger did not
replace, and PostgreSQL's `heap_modify_tuple` keeps the original datum for it
instead of converting it back; the result tuple is built where the caller keeps
it rather than copied there. A row of another descriptor still describes itself
and is checked against the relation. With the two builds alternated on the same
cluster, best of five runs of 500,000 rows, the trimming trigger's overhead went
from 1.27 µs to 0.90 µs per row, against 0.54 µs for PL/pgSQL, and the sample
changed to validate without trimming went from 0.75 µs to 0.55 µs.

### Composite arguments and results

Calling the `ankus_composites` sample's `scritch_dog`, which reads a `dog`
row, copies it and returns it, cost 1.80 µs per call over building the row,
best of five runs of 500,000 calls. Both directions resolved every column's
base type and kind in the catalog caches and converted its name, and managed
code built a new descriptor for every row. Managed code now reuses a recent
descriptor of the same type when every transported column matches it byte for
byte, which brought the call to 1.66 µs. The native side keeps the resolved
facts of up to 16 row types and uses an entry only while every column still has
the type, modifier, collation, flags and name it was built from; with it the
call costs 1.35 µs.

Sampling that call showed the result path still asking the catalog on every
call: the function's return type, the result type's kind and base type twice
over, and the caller's result descriptor, which PostgreSQL copies; the row was
then formed a second time against that copy. Only a `record` result needs the
caller's columns, since a named type is the value's own type and is checked
against it, so a named result now skips that descriptor and the second form,
takes its return type from the call expression, and makes one type lookup. The
call now costs 1.12 µs, measured with both builds alternated on one cluster.
