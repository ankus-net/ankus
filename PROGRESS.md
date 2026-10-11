# Ankus — pgrx → .NET Native AOT Port: Progress Tracker

Ankus is a faithful port of pgrx to idiomatic C# and .NET Native AOT. The port
is still in progress. Passing implemented subsets does not establish full parity.

## Goal and complete scope

Implement pgrx's runtime APIs, declaration macros, extension features, native
bindings, tooling, examples and testing contracts. Preserve PostgreSQL errors,
transactions, exact values, SQL NULL, type identity, encoding and native ownership.
Consumers use ordinary C# attributes, source generation, NuGet project SDKs,
`dotnet publish` and `dotnet test`, without runtime code generation or unbounded
reflection. Higher-level custom scan providers are additional Ankus scope.

Completion requires the supported PostgreSQL 13–18 and 19 beta contracts on
Linux x64, Windows x64, macOS ARM64 and macOS x64, including real published
extensions, backend recovery, complete suites and release/package validation.
The initial release is **0.1.0**; no NuGet packages are published yet.

The upstream inventory is pinned to pgrx **0.19.3**, commit
`fc91c63ebad11784647b50ee7e265c1fd9c9924f`. The refresh covers every release
change, including expanded headers and their source-derived maintenance command,
PG19 beta 4, CLI options, allocation cleanups, shared-memory lookup, reporting
interrupts and varlena safety. The pin and all seven header manifests already
match; matching inputs do not complete the release audit. See:

- [Complete requirement inventory](docs/contributing/evidence/parity-requirements.md): every inventoried command, API family, test corpus and release contract remains in scope.
- [pgrx 0.19.3 refresh gates](docs/contributing/evidence/parity-requirements.md#pgrx-0193-release-delta-and-refresh-gates): all eleven release commits, their .NET equivalents, existing evidence and remaining acceptance.
- [Original phase plan](docs/contributing/evidence/port-history.md#phase-plan): the full implementation requirements and original milestones.
- [Implementation and acceptance history](docs/contributing/evidence/port-history.md): complete retained evidence, including failures and later corrections. Historical completion states may be superseded.
- [Extension-author documentation](docs/src/content/docs/) and [development prerequisites](docs/contributing/development.md).

## .NET support and servicing plan

Repository `global.json` selects stable SDK **10.0.400** with
`rollForward: latestFeature` and prereleases disabled. The initial extension
target is **net10.0**. Installing another SDK does not silently change an
extension's selected compiler, target framework or embedded runtime.

Extensions embed runtime and compiler host **10.0.12-ankus.5**, based on runtime
fork commit `e3f084f4590644bcf7c191c0d281076757c0b93c` and paired with the 10.0.12
Native AOT framework and compiler targets. It keeps PostgreSQL's signals on the
backend thread. It replaces **ankus.4** (`d23f4e7374cd3878dc5696fbc26ee6ecdddde1ca`,
selected by **bdb5d1c**), with which complete primary CI and Intel acceptance
passed. Additional-version acceptance remains in progress.
Servicing requires
immutable patched-runtime and compiler packages, matching compiler selection,
complete backend validation and extension rebuild/redeployment. Check current
upstream servicing before releasing 0.1.0.

| Runtime line | Evidence and remaining requirements |
| --- | --- |
| .NET 10 LTS | Patched runtime and complete primary-platform suites exist. Complete supported PostgreSQL/platform combinations and final release acceptance remain required. |
| .NET 11 STS | SDK 11 RC1 targeting net10.0 passes complete PostgreSQL 18.6/Linux x64 acceptance. This does not implement net11.0. Its own patched compiler/runtime, GA and cross-platform acceptance remain required. |
| Later LTS/STS releases | Repeat explicit compiler/runtime/RID and packed-consumer acceptance, following upstream support lifecycles and servicing supported majors. |

See the [public support policy](docs/src/content/docs/reference/dotnet-support.md)
and [maintenance/acceptance plan](docs/contributing/dotnet-support.md). Each
declared .NET major must have actual evidence; SDK or package configuration is
not runtime support proof.
The .NET 11 plan also requires compiler memory-safety acceptance for raw calls,
globals, callbacks and checked managed APIs. Typed pointer contracts and explicit
scalar-call unsafe contexts pass the current complete .NET 10 compositions;
that evidence does not establish the future .NET 11 contract.

## Implementation status

This matrix summarizes current conclusions. The complete inventory linked above
defines the full scope; family-level implementation is not API-by-API completion.

| Area | Verified implementation | Remaining work |
| --- | --- | --- |
| Errors and cancellation | Sticky query cancellation/FATAL, retained raw/memory errors, fail-fast backend access and explicit rollback recovery. Transaction-completion callback failures use a fixed-buffer emergency log path without PostgreSQL diagnostic allocation; it preserves the primary error and durable outcome. Abort-time FATAL remains FATAL while irreversible FATAL is promoted to PANIC. Primary-platform full CI passes. | Retain recovery guarantees and complete the version/platform matrix. |
| Workers and shared memory | Native signal globals, lifecycle/transaction boundaries and shared memory. Idle Wait recovers repeated real cancellation; transaction waits still abort and terminal reports remain sticky. Complete primary-platform CI passes. | Full source-contract and complete version/platform acceptance remain required. |
| Functions and callbacks | Scalar/array/SETOF/TABLE, triggers/events, lifecycle, native callbacks, operators/conversions and installation-schema search paths. | Complete version/platform evidence. |
| Aggregates and SQL graph | Static abstract aggregate capabilities, typed Requires/Before/SupportFunction references with per-contract diagnostics at the authored value, deterministic SQL provenance and extended module magic. Empty SQL dependency anchors and exact authored CR/CRLF values pass complete native acceptance. | Inventoried contracts and complete version/platform acceptance. |
| Generator caching | Detached equatable declaration/provider/reference models, stable declaration-header locations and cached dispatcher/C/SQL artifacts across multi-declaration body edits, unrelated member insertion and unrelated file insertion. Only selected SQL files are read, with the complete path catalog retained. | Broader precise diagnostics, useful semantic code fixes and final inventory audit remain required. |
| Values and ownership | Documented scalar, array, composite, temporal, JSON, network, geometry, custom codec and raw datum contracts. Interval equality, hashing and ordering agree with PostgreSQL while retaining exact components; complete primary-platform CI passes. | Remaining mapped/container contracts, parsing/formatting ergonomics and complete source-case mapping remain required. |
| Runtime performance | Owned binary PgNumeric, direct decimal coefficient encoding, constant-time fixed-width array indexing, and a measured native guard for allowlisted pure built-ins. Catalog-miss errors require actual rollback; real-resource regressions and complete primary-platform CI pass. Persistent in-backend benchmarks provide batching, transaction modes and statistical baseline comparisons. | Complete current guard supported-version acceptance. Extend that tier only where ownership proofs and measurements justify it. Never weaken error recovery to reduce overhead. |
| Native bindings | pgrx 0.19.3 inventories, matching PostgreSQL 19 beta 4 inputs and target-compiler ABI checks; typed pointers and ANKUS129 make raw caller obligations explicit. Complete current primary-platform compositions pass, with focused binding checks on all seven majors. | Full supported-major/platform tests and final inventory audit. |
| Development CLI | Version/project selection, installation/registration, cluster lifecycle, run/connect, test/regress/bench, property forwarding, environment selection, scriptable info, package prefixes and target export with `--target-info` builds, including Intel macOS from Apple silicon. Benchmarks are measured inside PostgreSQL and retained in named comparison groups. `install --sudo`, `test --runas` and `regress --runas` follow cargo-pgrx with real-account evidence. | Run-as cases now run on every runner; the Linux PostgreSQL 16 rerun ([38115917411](https://github.com/willibrandon/ankus/actions/runs/38115917411)) has 52 skips where it had 55. Persistent clusters already retain Windows event-log diagnostics through `PostgresServerLog`. |
| .NET templates | Version-matched ordinary extension and worker templates reuse the CLI assets and pin local tools. Optional xUnit/NUnit consumers exercise managed and named backend cases, ignore reasons, worker processes and cleanup alongside default MSTest consumers; all three list backend cases with `dotnet test --list-tests` before anything is published. Complete primary-platform CI passes. | Complete version/platform acceptance. |
| API discoverability | Idiomatic attributed declarations, documented runtime APIs, named logging helpers and typed SPI interpolation. Compiler transport helpers are isolated in Ankus.CompilerServices and hidden from IntelliSense; complete primary-platform CI passes. `PgAssert` adds MSTest's tolerance, text and count checks. | Final inventory audit. |
| Packages and release | MIT license, Brandon Williams copyright, author/repository/project metadata and deliberate SDK/runtime boundaries. | Full release gates and supported-platform packages before publishing 0.1.0. |
| Documentation and samples | Public guides, generated API pages, pgrx migration and backend-execution guidance; current status separated from historical evidence. All 37 of pgrx's examples map to runnable samples with backend integration tests. Every guide was reviewed against the code on 2026-10-11 and corrected. | Recheck guides that change after the .NET servicing update and before release. |

The custom-type alignment review found no defect: variable-length PostgreSQL
types require at least four-byte datum alignment. Managed codec payload layout
is a separate contract, verified through copying/packed-field tests. Deriving
datum alignment from a CLR carrier or adding one/two-byte alignment would be
incorrect. See [the detailed review](docs/contributing/evidence/port-history.md#custom-datum-alignment-review).

## Current complete acceptance evidence

Primary CI runs all six modules against real published Native AOT extensions.
The latest successful primary CI source is **554cbb4**, with runtime **10.0.12-ankus.5**.
[CI 38107375540](https://github.com/willibrandon/ankus/actions/runs/38107375540)
passes; [Docs run 38107375539](https://github.com/willibrandon/ankus/actions/runs/38107375539)
for **554cbb4** is the latest documentation run. Each platform completed all six
modules with zero failures; no primary job timed out. Earlier **ankus.4** evidence,
including a complete [Intel refresh](https://github.com/willibrandon/ankus/actions/runs/37460600236)
on source **e41c687**, is retained below.

| Source | Platform / PostgreSQL | Result | Duration |
| --- | --- | --- | --- |
| Latest primary CI, **554cbb4 / ankus.5** | Linux x64 / 18 | 14,592 total; 14,540 passed; 52 platform skips; zero failures | 16m22s test step; 19m00s job |
| Same CI / revision / runtime | macOS ARM64 / 18 | 14,592 total; 14,527 passed; 65 platform skips; zero failures | 14m07s test step; 16m08s job |
| Same CI / revision / runtime | Windows x64 / 17 | 14,592 total; 14,546 passed; 46 platform skips; zero failures | 16m00s test step; 18m26s job |
| Earlier primary CI, **e0e469a / ankus.4** | Linux x64 / 18 | 13,883 total; 13,833 passed; 50 platform skips; zero failures | 11m37s test step; 13m48s job |
| Same CI / revision / runtime | macOS ARM64 / 18 | 13,883 total; 13,821 passed; 62 platform skips; zero failures | 12m03s test step; 13m44s job |
| Same CI / revision / runtime | Windows x64 / 17 | 13,883 total; 13,847 passed; 36 platform skips; zero failures | 21m50s test step; 24m04s job |
| SQL/datetime composition, parent **e41c687 / ankus.4** | Linux x64 / 18.6 | 13,337 total; 13,289 passed; 48 platform skips; zero failures | 42m46.885s tests; 43m41.597s command |
| Function-provider composition, parent **4da0bec / ankus.4** | Linux x64 / 18.6 | 13,365 total; 13,317 passed; 48 platform skips; zero failures | 42m39.165s tests; 43m46.937s command |
| Type-provider composition, parent **97842d3 / ankus.4** | Linux x64 / 18.6 | 13,411 total; 13,363 passed; 48 platform skips; zero failures | 42m39.033s tests; 43m35.244s command |
| Transaction-completion cleanup composition, parent **de27abd / ankus.4** | Linux x64 / 18.6 | 13,409 total; 13,361 passed; 48 platform skips; zero failures | 53m42.735s tests |

Normal Release, API freshness and site checks pass. Earlier source counts,
timings, rejected attempts and superseded
results remain in the [acceptance history](docs/contributing/evidence/port-history.md#superseded-active-and-primary-acceptance-detail-before-sqldatetime-acceptance-2026-10-06).
These results establish the named combinations, not full-port completion.

Every row below uses the current runtime, and every supported major now passes
on every platform.

| Earlier complete evidence | Platform / PostgreSQL | Result | Duration |
| --- | --- | --- | --- |
| [Intel weekly 38115991171](https://github.com/willibrandon/ankus/actions/runs/38115991171), **f220a27 / ankus.5** | macOS x64 / each of 13–18 and 19 beta | 14,654 total per major; 14,589 passed; 65 platform skips; zero failures | 1h21m36s–1h24m00s jobs, run in turn |
| [Version CI 38102793609](https://github.com/willibrandon/ankus/actions/runs/38102793609), **be3fbcd / ankus.5** | Linux x64 / each of 13–17 and 19 beta 4 | 14,512 total per major; 14,457–14,460 passed; 52–55 platform skips; zero failures | 27m31s–35m27s jobs; 1h24m for three reruns sharing one host |
| [Platform versions 38109393201](https://github.com/willibrandon/ankus/actions/runs/38109393201), **660ea5e / ankus.5** | macOS ARM64 / each of 13–17 and 19 beta 4 (assertions enabled) | 14,642 total per major; 14,573–14,577 passed; 65–69 platform skips; zero failures | 25m52s–32m09s jobs |
| Same platform-versions run / revision / runtime | Windows x64 / each of 13–16 and 18 | 14,642 total per major; 14,596 passed; 46 platform skips; zero failures | 29m47s–1h00m43s jobs |
| [Intel CI 38109391178](https://github.com/willibrandon/ankus/actions/runs/38109391178), **660ea5e / ankus.5** | macOS x64 / 18.6 | 14,642 total; 14,577 passed; 65 platform skips; zero failures; run-as cases run | 1h24m04s job |

Earlier timings, outcomes, failed checks and superseded acceptance details remain
in the [evidence archive](docs/contributing/evidence/port-history.md#acceptance-table-before-spi-flow-acceptance-2026-10-05).

## Active validation and work

- Native symbols no longer change with the extension's assembly version. Each
  exported `ankus_fn_…` name hashed the full assembly identity, version
  included, so a release that set a new `<Version>` (which
  `AnkusExtensionVersion` follows by default) renamed every export. After an
  in-place upgrade, functions the upgrade script did not recreate failed with
  `42883: could not find function`. Symbols now hash the assembly's simple
  name, which still separates extensions, as pgrx's wrapper names stay the same
  across versions. A packaged test upgrades `1.0.0` to `2.0.0` in place and
  calls an untouched function; it fails with the old naming and passes now.
  The version had also been hiding a Linux linking hazard: PostgreSQL loads
  libraries `RTLD_GLOBAL`, and each C wrapper calls its managed entry through
  an exported symbol, so with equal names a second versioned library of the
  same extension called the first one's code. Linux libraries now link with
  `-Bsymbolic`, binding their own references locally as macOS and Windows
  already do, and the side-by-side versioned library test passes again.
  A review of every guide against the code also corrected outdated names,
  diagnostics and behavioral claims. The parallel-worker initialization text
  now says that `PgInitialize` waits for restored settings only on PostgreSQL
  13–17, transaction callbacks never recover native input errors on any
  version, and `Open` resolves relation names through `regclass` input.
- Arrays of `bool`, `"char"`, `int2`, `int4`, `int8`, `oid`, `float4` and
  `float8` without NULLs now cross the call boundary as the packed block
  PostgreSQL stores, copied once, instead of one encoded record per element
  that each side parsed and managed code boxed. An `int[]` argument costs
  about 1 ns per element instead of 57 ns on a PostgreSQL 18.6 release build,
  and results, SPI cells and parameters take the same path. A function
  returning such a vector writes the block directly, so an array result costs
  185 ns per call instead of 400 ns. Reading a row,
  tuple or array cell that already has the requested type now skips the
  registry lookups that rule out `[PgDatumType]` mappings: SPI rows read in
  1,000-row queries cost 106 ns each in total, below PL/pgSQL's `FOR` loop. In
  a UTF-8 database, text arrays cross as each element's length and bytes, so a
  `text[]` element costs 34 ns instead of 90 ns. Set and table row costs are
  measured too; see
  [guard tiers](docs/contributing/evidence/guard-tiers.md#sets-arrays-and-spi).
- Extensions can now be built for another architecture, as pgrx's
  `cargo pgrx cross pgrx-target` allows. `ankus target export`, run on the
  target machine, measures that installation's native bindings with the
  project's toolchain and archives them with its `pg_config` values, its server
  headers and any headers it reads through the search paths in its cppflags,
  such as ICU or libxml2. `--target-info` on `build`, `publish` and `package`,
  or `AnkusTargetInfo` with `dotnet publish -r`, then builds for that runtime
  identifier against the archived headers without measuring them: no probe
  runs and neither LLVM Clang nor libclang is needed. Each archive is extracted once by hash, every file is
  checked against the hash recorded at export, and builds reject another
  runtime, major or Ankus version. Packages mirror the target's library and
  share directories. On Linux x64 with PostgreSQL 18, an exported archive
  packages an extension with Clang and libclang unreachable, and it loads and
  runs; altered files, another runtime, another Ankus version and a conflicting
  `--pg-config` are rejected. A manual Apple silicon publish for `osx-x64`
  produced an x86_64 library, confirming the host compiler payload carries
  the x64 JIT, and Windows x64 passes the same round trip on PostgreSQL 17 in
  CI. That run ([38137637601](https://github.com/willibrandon/ankus/actions/runs/38137637601))
  failed only a new unit test whose Windows cppflags used backslashes, which
  pg_config's shell words treat as escapes; it was fixed in **699d12a**. The
  first cross-architecture run ([38137646798](https://github.com/willibrandon/ankus/actions/runs/38137646798),
  **0330d0c**) passes: the Intel runner exported PostgreSQL 18.6 in 94
  seconds, the Apple silicon runner built an x86_64 Hello library from the
  archive in 67 seconds with Clang and libclang unreachable, and the Intel
  runner loaded it into a temporary server, where `add(19, 23)` returned 42.
- Aggregate support calls now keep their per-call-site state in `fn_extra`, as
  PostgreSQL's own aggregates do. Each transition used to look up the catalog
  signature, rebuild the aggregate's metadata and create and delete a memory
  context; it now reuses them and resets one scratch context per row. An `int`
  sum with a `long` state costs 170 ns per row over the built-in `sum` on a
  PostgreSQL 18.6 release build, down from 333 ns, and 156 ns once rows without
  `ORDER BY` stopped allocating sort-key lists. `PgAtomicValue<T>` gains
  the `Max` and `Min` that `PgAtomic<T>` has.
- The pending-interrupt test failed once on macOS arm64
  ([38131906576](https://github.com/willibrandon/ankus/actions/runs/38131906576)):
  it stopped at the first pending interrupt, and a concurrent test's dropped
  database had sent every backend a barrier before the statement timeout
  fired. The fixture now processes unrelated interrupts and keeps waiting for
  the cancellation.
- The public API of the `pgrx` crate is now mapped item by item in the
  [API mapping](docs/contributing/evidence/pgrx-api-mapping.md). Three passes
  over the datum, SPI and call, and backend runtime modules found gaps that are
  now filled: `PgDatum.Create` turns a managed value into a typed datum, as
  `IntoDatum` does; rows and tuples read any cell as a `PgDatum`, and results
  resolve column names without rows; triggers report their `UPDATE OF` columns,
  the columns an UPDATE assigns and their constraint deferral; memory contexts,
  heap tuples and descriptors can be borrowed or copied from native addresses,
  and a tuple formed as a native `HeapTuple`; unmapped enum labels resolve by
  type; intervals and times convert like PostgreSQL's casts; calendar constants,
  date-part names, `PgInterrupts.IsPending` and atomic `Max`/`Min` are public;
  and calling a function with `internal` arguments by name is rejected as it is
  by OID. Deliberate differences, such as `PgNumeric.Log`'s argument order and
  the scalar helpers running whole commands, are documented in the guides.
- Borrowed views and raw reads no longer enter PostgreSQL for flat values.
  Every view created and later deleted a private memory context, and every
  `PgDatum.Read<T>()` ran a guarded native operation in its own subtransaction,
  even for a short UTF-8 string or an `int`. A flat `text`, `varchar`, `bpchar`
  or `bytea` value without a domain is now borrowed in place, after the same
  UTF-8 check PostgreSQL makes, and built-in by-value types decode their datum
  word; compressed, external, domain and non-UTF-8 values keep the native path.
  On the assert-enabled PostgreSQL 16 that `ankus init` builds, a text view costs 154 ns instead of 2.33 µs,
  `Read<string>()` 185 ns instead of 1.28 µs, and a `PgTextView` parameter about
  0.41 µs per call instead of 3 µs, now that a borrowed input captures its
  callback lifetime in one native call instead of three.
- The scalar call boundary no longer zero-fills two 2.8 KB error buffers on
  every call; they are only valid after a failure, so clearing their headers is
  enough. On the same assert-enabled build, a managed `int` function costs about
  27 ns per call over a C built-in, down from 51 ns, and a text echo about
  148 ns, down from 171 ns. Set-returning rows save the same work per row.
- Rows and composites now match pgrx's lazy field reads. A cell whose type has
  no managed conversion (`tsvector`, `macaddr`, `money`, a base type the
  extension declares, or an array of one) is copied exactly instead of failing
  the whole `Select`, composite argument or trigger row. Reading it with the
  indexer or an ordinary type throws `NotSupportedException`; the rest of the
  row stays readable. `SpiRow.Get<T>` and `PgHeapTuple.Get<T>` accept a
  `PgDatumType`, or an array of one, when the cell has exactly the mapped type:
  the copy is rebuilt as a temporary datum for the reader and freed when it
  returns. `Set<T>`, `Set(SpiParameter)` and `PgHeapTuple.Create` write mapped
  values and raw datums through the same copy, and a returned row writes every
  unconverted field back unchanged. Backend tests cover by-value, fixed-length,
  variable-length and array cells from SPI rows, composite arguments and
  results, records and a BEFORE UPDATE trigger whose TOASTed `tsvector` column
  survives exactly; runtime tests cover the transport and its validation.
- Windows bridges now compile as C11. PostgreSQL 19's headers use `_Noreturn`
  and `alignas`, which MSVC accepts only under `/std:c11`; the layout probe and
  the extension bridge compile lacked it, so the Windows 19 cell failed before
  testing. With that fixed, the allocator test fixture's own C helper needed the
  flag too, and the source-built server needed ICU for the collation tests, as
  the EDB builds of the other majors have (its four ICU collation cases failed in
[38127753883](https://github.com/willibrandon/ankus/actions/runs/38127753883) and
[38129668001](https://github.com/willibrandon/ankus/actions/runs/38129668001)). The complete Windows x64 suite now
  passes on PostgreSQL 19 beta ([38130812681](https://github.com/willibrandon/ankus/actions/runs/38130812681)),
  and on 13–16 and 18 with the C11 bridges ([38119067045](https://github.com/willibrandon/ankus/actions/runs/38119067045)).
  Two Windows tests that failed under load now wait for the condition
  they check: a regression driver deleted right after it exits, and a parallel
  aggregate whose late worker found no blocks left to scan.
- Editors now fix the two most common entry-method errors. ANKUS033 offers
  **Make static** when the method reads no instance state, overrides or
  implements nothing, and no caller uses an instance receiver; ANKUS034 offers
  **Make internal** for a private or protected method or containing type, on
  every partial declaration that states accessibility; ANKUS174 and ANKUS179
  offer it for GUC properties and their classes, and ANKUS043 and ANKUS178
  replace `file` on a container when no other type shares its name. A GUC
  automatic getter also
  gets **Make static** (ANKUS170) and **Make partial** (ANKUS173), an authored
  getter can be removed (ANKUS175), and a container class made partial
  (ANKUS180). ANKUS038 offers **Pass by value** for `in` and `ref readonly`
  parameters, removing the modifier from every partial declaration and the `in`
  or `ref` from callers' arguments across the solution; the edit is dropped when
  any changed project gains a compiler error, as with a delegate, function
  pointer or overload that needs the reference. `ref` and `out` get no fix.
  The same corrections now cover every other entry kind: trigger and event
  callbacks, initializers, background workers, backend tests, native callback
  properties and handlers, enums, custom types, datum carriers and converters
  (resolved from the `typeof` argument) and output plugin initializers, 35
  further diagnostics in all. **Remove** deletes an attribute or option that
  cannot apply where it is written, for 16 more diagnostics: `Conditional` and
  `UnmanagedCallersOnly` on entries, SQL value metadata where no SQL value
  exists, set options on scalar functions, operator options an operator cannot
  take and `Schema` on a managed type provider. It keeps comments and layout,
  and leaves the source unchanged if other code still needs what it would
  remove.
  Generator tests compare
  the complete corrected source, regenerate it without diagnostics, and check
  that no fix is offered where the result would not compile.
- The third code review was re-checked against the current source. Every high,
  medium and low finding has a fix with tests. Two are kept by design. Each SPI
  call still runs in its own subtransaction by default, so a caught
  `PgException` leaves the transaction usable, as .NET code expects; `Atomic`
  scopes share one subtransaction for long write loops. ANKUS483 stays on the
  `PgSupportFunction` attribute, the typed selector that conflicts with
  `SupportFunction`. SQL that opens a subtransaction inside `Start` and
  subtransaction `PreCommit` callbacks ends the session, as it does under
  PostgreSQL itself; the guide's event table now says so, and a test pins the
  boundary: FATAL for the session, no cluster restart. A new test also covers a
  terminal cleanup report while a parallel worker commits: the worker exits,
  the leader gets PostgreSQL's ERROR from a parallel worker, and nothing PANICs.
  Two leftovers are now fixed. A `Default` that PostgreSQL's scanner
  would not close inside its parentheses (an open block comment, string, quoted
  name or dollar quote, unbalanced parentheses or a semicolon) is ANKUS060 at
  the value, under both `standard_conforming_strings` settings, instead of a
  broken `CREATE EXTENSION`. ANKUS497–499 escape quotes, backslashes and
  control characters in dependency identifiers. The last three guarded catches
  that flushed a caller's pending error (aggregate scopes, run-time GUC
  definition and initializer logging) now save and restore it like every other
  guard; their bridges compile cleanly under Clang 23 against PostgreSQL 13, 18
  and 19 headers. The pending-error fixture covers the shared mechanism, not
  these three entry points individually.
- The earlier Intel run ([38102775489](https://github.com/willibrandon/ankus/actions/runs/38102775489))
  failed one of 5,438 integration tests: a retried shutdown with a 500 ms
  timeout cancelled its `pg_ctl status` check before the slower host had
  started `pg_ctl`. Status checks now have their own allowance; the timeout
  bounds only the server's stop. The Intel runner and the Linux version runners
  now have the `ankus-runas` rule, so their run-as cases run instead of being
  inconclusive. The Intel rerun on **660ea5e** passes completely, and the
  ankus.5 Linux version matrix passes PostgreSQL 13–17 and 19 after the rerun.
- PgFunction's options were audited against pgrx 0.19.3's `#[pg_extern]`.
  Each has an equivalent: `raw` maps to raw SQL value parameters and results,
  and `should_panic` to `PgTest.ExpectedError`. Only `no_guard` is absent,
  deliberately, because every entry runs behind the native guard. Leakproof,
  rows, SET clauses and search paths go beyond pgrx's set.
- The other declaration surfaces were audited the same way: aggregate, trigger,
  type, enum, derive, operator, cast, schema, SQL, GUC, test and worker-builder
  options. Most map directly. Some map to a different model:
  - `creates = [Enum(T)]` is `PgEnum(GenerateSql = false)` plus an ordered
    `PgSql` block that creates the type.
  - `enable_shmem_access(startup)` is a `PgSharedMemory` initializer, which
    runs when PostgreSQL creates the value.
  - `pg_test::setup` is host code in the data-driven test method, which
    receives each `PgTestCase`; the migration guide lists it with
    `postgresql_conf_options()`.
  - `alignment = "on"` derives alignment from the Rust type. Ankus takes an
    explicit `Alignment`, because a CLR carrier's alignment does not determine
    the datum's (see the custom-type alignment review above).

  `extension_sql_file!` names a file block after the file. `[PgSqlFile(path)]`
  now does the same; the generator and the runtime attribute derive the same
  name with either separator, and a bad path is reported as a path error
  before any name check. The custom SQL sample now declares its files by path
  alone, as pgrx's `custom_sql` example does.

  `PgGucRegistry` now takes optional `PgGucHooks<T>` check, assign and show
  hooks, as pgrx's `define_*_guc_with_hooks` functions take hook functions.
  PostgreSQL's hook signatures carry no parameter identity, so the bridge
  prebuilds 64 hook slots; each slot's functions pass their definition to the
  shared hook path, and one generated managed entry selects the delegates
  registered for the slot. A repeated definition must pass the same hook
  methods.

  `pg_guard(unsafe_entry_thread)` needs no counterpart. pgrx latches the first
  thread that enters and panics on any other unless a function opts out.
  Ankus grants backend access per native entry, on whichever thread PostgreSQL
  calls, and refuses it on managed threads the extension starts. So the misuse
  pgrx guards against is already rejected, and the opt-out's case is already
  allowed.

  `NativeLayout` types, the counterpart of `pgvarlena_inoutfuncs`, no longer
  need `Pack = 1`: sequential structs are laid out as C lays them out, limited by
  `Pack`, and every write zeroes the padding in the destination, since .NET does
  not preserve padding through struct copies. Equal values therefore store equal
  bytes. Booleans and characters stay excluded because arbitrary input bytes are
  not valid values of those types.

  `[PgTest]` now takes `Volatility`, `ParallelSafety`, `SecurityDefiner` and
  `Cost`, as `#[pg_test]` passes `#[pg_extern]` options through; the shared
  function declaration reads and validates them. No declaration-audit option
  remains open.
- Binding catalogs can now be derived from the installed headers, as pgrx
  runs bindgen at build time, instead of from pgrx's generated output.
  `binding-catalog-headers` reads the header manifest through the compiler's
  libclang in a worker process and applies pgrx 0.19.3's bindgen configuration
  to types, functions, globals and macro constants, which a port of cexpr
  evaluates. A serialized AST needed two adjustments to read like parsed
  source: declarations are visited in source order before records are
  measured, and comments are kept although the headers are system includes.
  On Linux x64 the derived catalogs for 13.23 through 19 beta 4 differ from the
  pinned ones only by configuration (assertions, ICU, NLS, liburing, configure
  options, glibc); against a 19 beta 4 build configured like pgrx's, only
  `_IO_FILE`, the default port and the version string differ. Extension
  builds now use these catalogs: `binding-sources` derives them with the
  build's own Clang options and libclang, caches them by the preprocessed
  manifest, toolchain and build tool, and binds the generated declarations to
  their hash. The pinned catalogs remain the drift reference.
- The new weekly macOS cells found test assumptions that only held for
  packaged servers: PostgreSQL 13's memory fallback relied on the implicit
  library suffix, which is `.so` on macOS before 16; `pg_popcount32` is a
  pointer on every platform before 15; and source builds before 16 lack ICU.
  The tests now name the file, expect each major's declaration and report
  inconclusive without ICU. macOS 19 (an assert-enabled build) also aborted on
  `AllocSetCheck` after a test whose notice resets `ErrorContext`: the next
  statement's `PortalDrop` found a context whose recorded allocation differed
  from its blocks. It did not recur in an assert-enabled Linux suite, a full
  macOS suite, five macOS repeats with malloc scribbling or 20 repeats of the
  memory test classes on an assert-enabled Linux 19 build. The report bridge
  survives the reset as intended, so it remains open as an intermittent fault.
  Since then, 11 repeats of the memory test classes on the macOS 19 assert build
  and the complete macOS 19 suite in platform-versions run 38109393201 passed
  without it.
- macOS 13 also hung in parallel queries: under load, a leader slept with its
  workers' tuple queues full. Logging every `SIGUSR1` send and delivery showed
  each of 12 stalls began when the leader's handler ran on the .NET finalizer
  thread. The kernel gives a signal sent to a process to any thread that leaves
  it unblocked, and PostgreSQL 13 wakes a latch from that handler assuming it
  interrupted the sleeping thread; every major runs its timeout, cancellation
  and shutdown handlers the same way. Runtime **10.0.12-ankus.5** starts runtime
  threads with asynchronous signals blocked and, on macOS, activates threads
  with `SIGIO` instead of `SIGUSR1`. That is enough on Linux, but macOS
  `sigprocmask` changes every thread's mask, so PostgreSQL unblocks them again
  within a millisecond. On macOS, generated bridges therefore wrap PostgreSQL's
  asynchronous handlers: one entered on another thread hands the signal to the
  backend thread, which holds it until it can run the handler, and the original
  sender is kept for PostgreSQL 19's log detail. The runtime calls the bridge
  before starting each thread, which also covers children of a preloading
  postmaster. A fork probe and `BlockedBackendSignalsWaitForTheBackendThread`
  fail with ankus.4 on Linux and macOS and pass with ankus.5. Under the load
  that produced 11–14 stalls per run, three runs complete with none. Complete
  suites with ankus.5 pass on Linux x64 13 and 18, macOS ARM64 13 and 18, and
  Windows x64 13 and 17: **14,511** tests each, zero failures. In
  [CI 38089102305](https://github.com/willibrandon/ankus/actions/runs/38089102305)
  for **1b35c17**, Linux and Windows pass, but macOS bridges failed to compile:
  Clang 23 rejects the routing readiness flag as set but never read in
  extensions without managed calls. Readiness now tests the shared pointer.
  The wrapper also tolerates code that saves a handler as a plain function and
  restores it, which drops `SA_SIGINFO`: it checks its installed flags before
  reading sender information, and the next runtime thread restores the flag.
  [Platform-versions run 38093011552](https://github.com/willibrandon/ankus/actions/runs/38093011552)
  on **8605619** passes macOS ARM64 13, 14, 15, 16 and 19 and Windows x64 13–16
  and 18 (**14,511** tests each, zero failures; 27m47s–29m06s macOS jobs,
  30m03s–58m09s Windows jobs sharing one host). macOS 17 failed one case: a
  restarted worker publishes its PID before the postmaster records it in the
  registration, so the test now waits for the registration before comparing.
  A local Windows 17 suite found a second race: a strict aggregate adopts the
  first partial state and combines only later ones, so the combine count is zero
  when concurrent tests leave the query one worker. The test now counts in an
  execution where both workers ran.
- The weekly platform-versions workflow now covers every supported stable major
  on Windows x64 and every major except 18 on macOS ARM64, which primary CI runs.
  Windows 14.24, 15.19 and 16.15 come from EDB's archives, pass the
  `postgresql-check` command and are selected through repository secrets, like
  13 and 18. macOS 13, 14, 17 and 19 are `ankus init` builds in a runner-local
  directory, selected through repository variables, so Homebrew keeps only the
  15 and 16 formulae. **7777c83** and **5c053d8** pass the Windows x64 suites on
  PostgreSQL 17.11 and 13.23 (**14,478** total each; zero failures; **23m40s** and
  **22m07s**).
- Code-fix tests now compare the complete fixed document, not fragments of it.
  The exact comparisons exposed three defects: the injected-parameter fix left a
  space inside the brackets when it removed one attribute of several (`[ Keep]`,
  `[Keep ]`) and doubled the space where a list between comments disappeared, and
  on Windows the aggregate fix rewrote the existing line ending after the base
  list as CRLF. The injected-parameter fix now rebuilds the parameter's attribute
  lists itself, and the aggregate fix appends its capability by taking over the
  base list's trailing trivia instead of reformatting it. The 92 code-fix and
  guard tests pass on Linux and Windows.
- The guard tiers now have figures. `tests/Ankus.GuardTierBenchmarks` measures
  them with `ankus bench`, and on a PostgreSQL 18.6 release build a filtered log
  report costs 0.19 µs, a lightweight numeric parse or addition 0.60–0.72 µs, the
  same addition in an explicit subtransaction 1.19 µs, an SPI `SELECT 1` 2.89 µs
  and an invalid `TryParse` 5.51 µs, most of it the managed exception. The
  [guard tier measurements](docs/contributing/evidence/guard-tiers.md) also record
  13.23 and 16.15 assertion builds. Measuring them exposed a gap: before
  PostgreSQL 16, `TryParse` inside an explicit scope, which every benchmark
  iteration uses, could not recover invalid input, because input recovery opened
  its own subtransaction only when no scope was active. A scope already runs in a
  subtransaction, so input recovery now nests one there, in recoverable and
  atomic scopes alike. A new test parses each family's invalid text in both kinds
  of scope, and it and 125 related input, parallel, ownership and numeric tests
  pass on PostgreSQL 13.23, 16 and 18.6.
- The `Ankus` namespace now holds only the API extension authors use, the
  counterpart of `pgrx::prelude::*`, which the SDK imports implicitly. The
  registries, codecs and type writer that generated code calls moved to
  `Ankus.CompilerServices` beside the other generated-code helpers, and the
  System.Text.Json converters moved to `Ankus.Serialization`. The converters are
  no longer hidden: the value types still apply them through `[JsonConverter]`,
  and applications can add them to their own serializer options.
- `TryParse` of built-in numeric, date and time, network, geometry and range
  types no longer opens a subtransaction on PostgreSQL 16 and later: it calls
  the input function through PostgreSQL's soft-error interface and raises a
  failure only after the input function has returned, so the guard recovers it
  without rollback. It therefore also returns false inside transaction callbacks
  and parallel workers, where no subtransaction may start: the callback and
  parallel input tests now expect `false` and a committed transaction for all
  five families from 16, and the original SQLSTATE before. As with PostgreSQL's
  own `pg_input_is_valid`, only invalid input is soft; a hard error such as out
  of memory is no longer rolled back on 16 and later, and the native-fault
  ownership test now expects that. Log reports below ERROR skip the subtransaction too
  when no `emit_log_hook` is installed, since PostgreSQL's own formatting and
  client conversion hold nothing when they fail; both kinds of clean failure stay
  recovered in a parallel worker before 17 instead of being raised again when the
  function returns. A terminal cleanup report whose construction fails now has a
  test: the fault fixture fails the call into the report builder, and the backend
  still ends with the requested severity and transported message, FATAL after a
  read-only commit and PANIC after a durable one. A second test shows that a log
  hook failing during the report cannot make it recoverable: PostgreSQL promotes
  the hook's error to the pending FATAL.
- Evidence for this batch and the pending-error work: the full Linux x64 suite on
  PostgreSQL 18.6 ran 14,477 tests with 15 failures, and the Windows x64 suites on
  17.11 and 13.23 ran 14,477 each with 16 and 1. The failures were two existing
  tests that still expected the old input-recovery behavior on 16 and later, and
  on Windows a race in a test helper that read `postmaster.pid` while PostgreSQL
  was replacing it. With those updated, the affected input, ownership, parallel,
  callback and logging tests pass on Linux with PostgreSQL 18.6, 16 and 13.23
  (99 each, plus 208 broader on 18.6 and 13.23) and on Windows with 17.11 and
  13.23. The test extension's native bridge compiles for PostgreSQL 13, 16 and 18.
- A guarded call that caught its own error while PostgreSQL still had another
  error pending removed both, because PostgreSQL's error stack can only be
  flushed whole. A caller that cleans up before re-throwing, as logical
  replication's apply worker does, then reported `errstart was not called`
  instead of its own error. Guarded SPI, memory, logging, configuration-read and
  assign-hook calls now copy a pending error first and push it back after
  flushing their own. An empty ErrorContext proves none is pending, so ordinary
  calls skip the copy; otherwise the probe points `ErrorContext` at a private
  context while it runs, so its own error never resets memory a caller keeps
  beneath ErrorContext. Once no error is pending, leftovers of finished reports
  are released as PostgreSQL 19 does, so later calls take the fast path again. A
  C fixture repeats the apply worker's pattern with a savepoint abort callback
  whose warning a LATIN1 client cannot receive: before the fix the client saw
  `XX000: errstart was not called`, and now it sees the original `P0001` error.
- A worker started from a LATIN1 database now has a test: it logs non-ASCII text
  between transactions, catches a pre-commit callback's error after its
  transaction has ended, and survives two idle cancellations, with its report
  written to the log in LATIN1. The generator tests (4,773) and the 215 affected
  memory, callback, configuration, logging and worker integration tests pass on
  Linux x64 with PostgreSQL 18.6.
- Numeric, network and geometry values implement `IParsable<T>` and
  `ISpanFormattable`, and the six temporal types implement `IParsable<T>`, so
  generic .NET code and string interpolation use PostgreSQL's input and output
  rules. Format strings are rejected rather than reinterpreted, since PostgreSQL
  text has one canonical form; a backend test parses each kind through
  `T.Parse`. PostgreSQL downloads retry transient failures four times with
  backoff and report each retry. Version parsing accepts suffixes such as
  `11.2-FOO-BAR+`, as pgrx does, and schema selection warnings name the custom
  SQL's source line. pgrx's `value_struct_cast` and `value_union_cast` now run
  against the bindings generated from the server's own headers, which closes the
  last partial row of the [inline test mapping](docs/contributing/evidence/pgrx-inline-test-mapping.md).
  On Linux x64 with PostgreSQL 18.6 the full suite ran 14,471 tests: 14,416
  passed, 54 skipped and 1 failed, a new list test that wrongly expected a mocked
  native layer to return null. With that check moved to the backend-free NIL list
  test, the runtime module passes (2,424). The node and temporal integration tests
  also pass on PostgreSQL 13.23 (77), which runs the `Value` branch.
- **dd530d4** and **a842f7d** pass the Windows x64 suites on PostgreSQL 17.11 and
  13.23, run concurrently (**14,446** total each; **14,402** passed; 44 skips;
  zero failures; **24m46s** and **24m03s**).
- A cancelled CI job left its test servers running, and the runner then killed
  them outright, so each kept its System V shared memory segment. macOS allows
  32; the MacBook had leaked 18 from runs cancelled on 2026-10-07, and `initdb`
  then failed with "No space left on device". Runner cleanup now finds the
  postmasters carrying the job's `RUNNER_TRACKING_ID` and shuts them down so
  PostgreSQL removes its own segment. Checked on macOS and Linux with a tagged
  server: it stopped and released its segment, and other servers kept running.
  The 18 leaked segments were removed; the Intel Mac had none.
- A fresh audit of every finding in the private review notes against the current
  code finds most fixed. This batch closes several that remained: a relation
  opened inside `Spi.Connect` now belongs to the statement that opened the
  session, as in pgrx, instead of being released with the session's recovery
  subtransaction. `PgAssert` gives backend tests checks that name expected and
  actual values, and its `ThrowsSqlState` runs work in a subtransaction so a test
  can inspect an error and continue. Projects that enable implicit usings get
  `using Ankus` from the SDK. The `dotnet new` templates accept
  `--extension-name`, matching `ankus new`. The numeric guide notes pgrx's
  `XX000`. Still open from the audit: Linux ARM64, which no runner covers yet.
  Binding catalogs are now derived from the selected headers.
- **2b841a3** passes the complete Linux x64 suite on PostgreSQL 18.6 with run-as
  (**14,442** total; **14,391** passed; 51 skips; zero failures; **41m03s**). Its
  Windows x64 suites on PostgreSQL 17.11 and 13.23, run concurrently at package
  concurrency 20 (**14,443** total each; **14,397** passed; 44 skips; **38m25s**
  and **36m34s**), failed only the bench test's cleanup: Git marks the object files
  of the repository that test creates read-only, and Windows would not delete
  them. **f1a6895** removes that repository with its attributes cleared, and the
  bench test then passes on both majors.
- `PgPropertyRunner` and `PgGenerators` port pgrx's `PgTestRunner`: a property
  runs against 256 generated inputs inside the backend, each in its own
  subtransaction, so a PostgreSQL error is a failing input whose work rolls back
  while the run continues. Failing inputs shrink by editing the recorded random
  choices, as Hypothesis does, so composed generators shrink without extra code;
  integers are a sign and a magnitude and reach their exact boundary. The report
  names the smallest input, its error and the seed. .NET has no Microsoft-owned
  property-testing package, so the generators are Ankus's own. pgrx-unit-tests'
  eight temporal `proptests` now run through it with pgrx's raw-value strategies.
- `ankus bench --report` prints pgrx's history report: a section per benchmark
  with its first successful run as the baseline, the ten most recent groups as
  bars with their change, drift marks for build configuration, PostgreSQL major,
  runtime and non-default settings, and counts of omitted failed runs. Run groups
  now record the extension and .NET SDK versions, the command line, the tool
  version, the Git commit, branch, dirty state and describe output, and each
  setting's unit, source and boot and reset values. A refresh blocked by a
  dependent object names `--cascade`, and `--wait` prints pgrx's waiting message.
  The bench test now covers default group names, `--postgresql-conf`, `--cascade`,
  `--resetdb`, partial and failed groups and comparison filtering. Test clusters
  can run under Valgrind (`ankus test --valgrind`), and every Valgrind start
  passes PostgreSQL's `valgrind.supp`, which `ankus init` now keeps in the PGXS
  tree. The cluster locale is asserted per platform, and an unknown
  `upgrade --package` fails before any change. Messages a test raises that
  contain `TMSG: ` reach its output live, as pgrx echoes them. Installing from a
  solution directory and `schema` from the project directory with a relative
  intermediate path are tested. `[PgBenchmark]` can report throughput
  in pgrx's units, which pgrx's own `Bencher` cannot set. The tooling mapping now
  has 82 covered rows, 12 documented differences, 24 Rust-specific rows and no
  gaps or partials.
- **921113f** passes complete suites on this repository's validation machine:
  Windows x64 on PostgreSQL 17.11 and 13.23 run concurrently at package
  concurrency 20 (**14,410** total each; **14,368** passed; 42 skips; zero
  failures; **41m44s** and **41m51s**) and Linux x64 on PostgreSQL 18.6 with
  run-as (**14,410** total; **14,359** passed; 51 skips; zero failures; **43m56s**),
  all three at once. Its primary CI
  ([37933668208](https://github.com/willibrandon/ankus/actions/runs/37933668208))
  passes Linux x64 on 18 (18m), Windows x64 on 17 (21m) and macOS ARM64 on 18
  (16m). Its PostgreSQL 19 beta 4 rerun
  ([37934631721](https://github.com/willibrandon/ankus/actions/runs/37934631721))
  passes Linux x64 with the PGLZ sample fix (**14,410** total; **14,359** passed;
  51 skips; zero failures; 25m job), completing PostgreSQL 13 through 17 and 19
  on the version matrix after 5b72b18's run. The platform version run on **10c1391**
  ([37917953859](https://github.com/willibrandon/ankus/actions/runs/37917953859))
  passes macOS ARM64 on PostgreSQL 15 (23m) and 16 (21m) and Windows x64 on 13
  (1h11m). Windows x64 on 18 failed two cleanups that deleted a cluster directory
  while a stopped server still held handles: the regression-prerequisite cleanup
  fixed in **ac6486e**, and the development-diagnostics cleanups, which now use
  the same retrying delete.
- Generated custom-type storage now serializes `Guid`, `DateOnly`, `TimeOnly`,
  `DateTime`, `DateTimeOffset` and `TimeSpan` members, as System.Text.Json does
  and as pgrx's serde storage does for its date type. JSON text uses
  System.Text.Json's formats; CBOR uses the registered UUID (37), full-date (1004)
  and date/time (0) tags where they exist. Runtime tests pin the bytes and text
  and reject other spellings; seeded pgrx `RandomData` values with unsigned
  64-bit and date-list members survive table storage, compression, arrays and
  every exchange path in the backend.
- A second source-case mapping covers pgrx's compile-fail, `todo` and UI cases,
  its testing framework, its benchmark runner and `cargo-pgrx/tests` (203 cases in
  118 rows: 66 covered, 14 partial, 3 gaps, 11 deliberate differences and 24
  Rust-specific at the time; see the entry above for the current counts). The test fixture now creates the extension with `CASCADE`, as
  pgrx's does, so an extension that `requires` another starts; a failed test's
  report includes the server's account of its backend when that process died;
  `ankus bench` prints pgrx's running line, intervals, change and group summary,
  with benchmarks missing from the current run and why a comparison is
  unavailable; and new tests pin the comparison summaries, concurrent test
  isolation, process-exit cleanup, the logging defaults and the composite and
  text-array signatures pgrx keeps in its `todo` corpus. The ranked gaps start with Valgrind runs of test clusters and the
  benchmark history report.
- `PgMemoryContext.CreateFlatArray<T>` builds a zeroed PostgreSQL array of a
  fixed-size scalar in place, the counterpart of pgrx's `FlatArray::new_zeroed_in`;
  it rejects zero-length dimensions, more than six dimensions and PostgreSQL's
  element and 1 GB allocation limits before allocating. `PgArrayView` and
  `PgArrayView<T>` expose the native SQL NULL bitmap like pgrx's `RawArray::nulls`.
  With these, every case in pgrx's unit-test corpus maps to a passing Ankus test,
  a documented difference or a Rust-specific guarantee.
- More of pgrx's unit tests now run in the backend: a backend ERROR's managed
  stack trace, seeded temporal round trips checked against `*_send` bytes, the
  installed signature matrix read back from `pg_proc`, a typed
  `PgArrayView<bool>`, a trigger opening its relation, an array-of-composite
  field set in code, the current memory context's parent, an untracked worker's
  termination, a parameter named like its function and a test-only function
  under `#if ANKUS_TESTS`. Test publications define `ANKUS_TESTS` and benchmark
  publications `ANKUS_BENCHMARKS`, like pgrx's `pg_test` feature. A generator
  test compiles every declaration kind beside types named `System`, `Ankus`,
  `Spi` and other names generated code uses. SPI conversion errors now name the
  source value's type.
- **ac6486e** passes the complete Windows x64 suite on PostgreSQL 17.11 (**14,382**
  total; **14,340** passed; 42 skips; zero failures; **58m** while the machine also
  ran PostgreSQL 13 and Linux suites and focused runs). Its PostgreSQL 13.23 run
  failed only two regression-prerequisite cleanups, which deleted a directory
  while a stopped server's handles were still closing; that cleanup now waits as
  PostgreSQL's own Windows `unlink` does. Its Linux run failed two cases on
  30-second client timeouts while five suites shared the machine; the next
  revision is validated without that contention.
- **805783b** passes complete Windows x64 suites on PostgreSQL 17.11 (**14,312**
  total; **14,270** passed; 42 skips; zero failures; **40m** while the machine also
  ran CI and Linux) and Linux x64 on PostgreSQL 18.6 with run-as (**14,261** passed;
  51 skips; zero failures; **43m**). Its PostgreSQL 13.23 Windows run failed only
  the benchmark payload test: a constant-time routine could measure zero ticks on
  Windows's 100 ns stopwatch under load. **10c1391** gives that test measurable work.
  **5b72b18** also passes Linux x64 on PostgreSQL 18.6 with run-as (**14,203**
  passed; 51 skips; **17m**) and CI
  ([37909292198](https://github.com/willibrandon/ankus/actions/runs/37909292198):
  Linux 25m, Windows 29m, macOS 15m while sharing hosts with local validation).
  The version matrix rerun with the socket fix
  ([37909296064](https://github.com/willibrandon/ankus/actions/runs/37909296064))
  passes PostgreSQL 13 through 17; 16 and 17 had failed on the socket path, and 13
  and 14 took **70m** on the flex runners. PostgreSQL 19 failed one pglz sample
  case: its beta defaults `default_toast_compression` to LZ4 when built with it,
  so the stored size no longer matched the PGLZ estimate. That case now sets the
  column to PGLZ on PostgreSQL 14 and later.

- `System.Text.Rune` maps to `varchar`, as Rust's `char` does in pgrx. Generated
  arguments, results, `Rune[]` and `PgArray<Rune?>` (`varchar[]`), SPI parameters,
  cells and edited rows all carry the scalar value as character text, and returned
  arrays keep the `varchar` element type. pgrx's `rt_char`, `rt_array_char`
  (including U+10FFFF and NULL elements) and `takes_char` values pass through all
  eight SPI paths in the backend. Where pgrx takes the first character and reads
  empty text as SQL NULL, a `Rune` requires exactly one scalar value: empty text,
  `'ab'`, a base letter with a combining accent and padded `bpchar` fail with
  38000 and the backend continues. Runtime tests pin the UTF-8 bytes and the
  unpaired-surrogate message.
- Two more mapped gaps are closed in the backend. A background worker whose wait
  loop ends on termination commits a final transaction that the launching
  backend reads after shutdown, as pgrx's `bgworker` does. pgrx's composite
  default and variadic cases run with and without arguments, including NULL
  rows, and an explicit function cast between two composite types installs,
  converts and keeps strict NULL. Borrowed-text set functions read table columns
  stored compressed inline, out of line and compressed out of line, and 10,000
  composites built from one descriptor stream to the client and through SPI.
  Methods whose only parameter is a nullable custom-text record or enum run on
  SQL NULL from non-strict functions, and a `string? = null` default is executed
  omitted, supplied and NULL.
- A configuration check that throws on its boot value while a library load
  registers the setting now fails that load with an ERROR carrying the managed
  message, matching pgrx and a C hook's ERROR; previously it was a rejection,
  which PostgreSQL turns into FATAL for a boot value. Only registration changes:
  returned rejections, reload checks and ordinary `SET` keep their policy, and
  the same backend loads the library again afterwards. `SHOW ALL` omits a
  no-show setting, and an unlabelled GUC enum member is set by its C# name and
  restored in parallel workers.
- Complete Windows x64 suites on **5b72b18** pass PostgreSQL 17.11 and 13.23
  concurrently at package concurrency 20 (**14,254** total each; **14,212** passed;
  42 skips; zero failures; **34m** each while sharing the machine).

- Three pgrx capabilities are added. `PgDiagnostic.Domain` and `PgException.Domain`
  carry the message domain of C's `ereport_domain` and pgrx's `ereport_domain!`:
  the native report passes it to `errstart`, and captured ErrorData returns it,
  so a caught error reports `test_extension_domain` after crossing PostgreSQL and
  backend errors report `postgres-<major>`. `PgLog.Write(level, $"...")` uses a new
  interpolated-string handler that evaluates nothing when PostgreSQL would not
  report the level, as pgrx's logging macros skip their arguments; at default
  thresholds DEBUG is skipped and WARNING evaluated. JSON and JSONB aggregate
  states run with strict, immutable, parallel-safe transitions. Direct calls pass
  and return path and polygon values, a borrowed empty bytea reports zero bytes,
  and double infinities, NaN and numeric-to-real overflow match SQL casts exactly,
  including PostgreSQL 13's rejection of numeric infinity. The pgrx mapping's
  remaining gaps are `Rune` to `varchar`, background-worker SPI after termination,
  composite VARIADIC/DEFAULT execution and a few smaller cases; `Rune` has since
  been added.
- The dedicated Intel macOS runner passes its complete suite on **a3e39c8**
  ([additional platforms 37892336665](https://github.com/willibrandon/ankus/actions/runs/37892336665), **1h15m**).
  The Linux version matrix on the same revision
  ([37892281684](https://github.com/willibrandon/ankus/actions/runs/37892281684))
  passes PostgreSQL 14 and 15. PostgreSQL 16, 17 and 19 fail the same 32
  consumer-fixture cases on newly added runners whose work directories are long:
  without an `ankus test` session, a fixture placed its Unix socket under `TMPDIR`
  and exceeded PostgreSQL's 107-byte socket path. The socket directory now stays
  under `TMPDIR` only while the longest socket path fits and otherwise uses `/tmp`,
  as PostgreSQL's own test clusters do; direct tests cover the byte boundary,
  including multibyte names. PostgreSQL 13's job stopped when its runner service
  received a shutdown signal. The matrix is to be rerun with the fix.
- The ankus.5 Linux version matrix
  ([38102793609](https://github.com/willibrandon/ankus/actions/runs/38102793609))
  passes PostgreSQL 14 and 15. The other four jobs were cancelled when the
  shared runner host's disk filled: 26 GB of package-test directories from
  suites cancelled on 2026-10-06 and 07 were still in its temporary directory,
  and the runner aborts a job it cannot log. `runner-cleanup` now removes such
  directories once they are older than any job can run. The four jobs are being
  rerun.

Earlier entries in this log are in the
[evidence archive](docs/contributing/evidence/port-history.md#active-validation-log-through-2026-10-09).

## Remaining work order

Primary CI, the Linux, macOS ARM64 and Windows x64 version matrices, including
PostgreSQL 19 beta, and Intel macOS on 13–18 and 19 beta all pass on runtime **ankus.5**. The
declaration audit against pgrx 0.19.3 has no open option, and the third code
review's findings are fixed or kept by documented design.

1. Resolve discovered correctness and CI failures before accepting affected work, including the intermittent macOS PostgreSQL 19 `AllocSetCheck` fault if it recurs.
2. Complete every pgrx 0.19.3 release-delta gate, including deterministic header discovery/regeneration, newly exposed native contracts, all CLI/configuration/output forwarding and allocation/shared-memory/varlena audits. Refresh evidence against the corrected current source.
3. Keep the [public API mapping](docs/contributing/evidence/pgrx-api-mapping.md) current as APIs change.
4. Use the in-backend baseline to finish measured hot-path improvements that preserve recovery and ownership guarantees.
5. Complete framework/discovery support and representative samples, and recheck guides that change before release.
6. Complete .NET servicing and every release requirement in the full inventory, using the release rehearsal, before publishing 0.1.0. If .NET 11 reaches GA first, complete its acceptance for that release; preview validation does not block the initial .NET 10 release.

Continue independent work while CI runs. Before every commit and push, check and
record previous run outcomes, including live runs, and resolve reported failures.
Keep this file current; append detailed implementation evidence to the archive
with a clear tested revision, platform/version, outcome and any remaining scope.
