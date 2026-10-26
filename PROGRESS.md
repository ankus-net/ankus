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

Completion requires the supported PostgreSQL 13–18 and 19 RC1 contracts on
Linux x64, Windows x64, macOS ARM64 and macOS x64, including real published
extensions, backend recovery, complete suites and release/package validation.
Ankus is released on nuget.org: 0.1.0 on 2026-10-15, servicing releases
through 0.1.7, 0.2.0 on 2026-10-17, 0.2.1 and 0.2.2 on 2026-10-18, 0.2.3 on
2026-10-19, 0.2.4 on 2026-10-20, 0.2.5 on 2026-10-22, 0.2.6 on 2026-10-22,
0.2.7 and 0.2.8 on 2026-10-24, and 0.2.9 on 2026-10-25 (see remaining-work
item 8).

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

Extensions embed runtime and compiler host **10.0.13-ankus.1**, built from runtime
fork commit `f97c4dea9e1cab1f4afcbd8d9c67eb5f7d88a492` on branch
`ankus/runtime-10.0.13` and paired with the 10.0.13 Native AOT framework and
compiler targets. It keeps PostgreSQL's signals on the backend thread. It
replaces **10.0.12-ankus.5** (`e3f084f4590644bcf7c191c0d281076757c0b93c`).
Complete suites pass on it for every supported PostgreSQL major on Linux x64,
macOS ARM64, Windows x64 and Intel macOS; see
[current acceptance](#current-complete-acceptance-evidence).
Servicing requires
immutable patched-runtime and compiler packages, matching compiler selection,
complete backend validation and extension rebuild/redeployment. Check current
upstream servicing before releasing 0.1.0.

.NET 10.0.13 shipped on 2026-10-13 with security fixes, and the servicing
update to **10.0.13-ankus.1** is complete. Microsoft builds .NET 10 from
dotnet/dotnet, whose release commit `4099f83977f4d29604a9aeb971fb0a7bc1d5f699`
took dotnet/runtime `6aa72daa24f66c01c3d7b6a32519e9d21396deec` and changes 75
more files in `src/runtime`: the release's security fixes and versions, which
reach dotnet/runtime later. The fork branch `ankus/runtime-10.0.13` therefore
merges a commit whose tree is that shipped `src/runtime` (`bc04ae377af`), on
top of the activation probe (`fcf0c5cd13e`); merge commit
`b9bc34320018070cedb42841c1ab622032f6a4d0` applied without conflicts. It
includes the upstream check that skips chaining to an ignored or default
activation handler, which fixes a hang the release rehearsal found (see below)
and which the probe exercises. The same comparison for 10.0.12 found 13 shipped
files beyond the dotnet/runtime tag (HttpListener requests, WebSocket
compression, EventPipe's diagnostic socket) that **ankus.5**, built from the
tag, lacks; 10.0.13-ankus.1 has them. CI builds the runtime from the fork and
passes its probes before the complete suites run.

The first servicing CI run (38448888187 on **84504b5**) built the runtime from
the fork on all three platforms and passed every Linux integration test, but
one generator test failed on Linux and Windows: System.Formats.Cbor 10.0.13 now
reads at most 64 nested levels, and a custom type 64 objects deep with a
`decimal` leaf is 65 levels in CBOR, because a decimal is a two-element array.
Ankus had written such values and could no longer read them back. The custom
type reader and writer now count a decimal as a level in both JSON and CBOR,
which keeps .NET's default and makes every written value readable in either
form; the guide states the limit.

The next run (38456292720 on **3efe451**) failed one build test on all three
platforms: two consumers restoring into different package folders compiled the
same binding companion twice. Their compilation keys differed only in
`Microsoft.NET.ILLink.Tasks` 10.0.13, whose copy in the runner's shared NuGet cache
came from a dnceng staging feed rather than nuget.org. The fork's runtime build
and probes had restored through the runtime repository's feeds into that shared
cache, which on behemoth then held about 150 staging copies, including
`Microsoft.NETCore.App.Runtime.linux-x64` and `Microsoft.DotNet.ILCompiler` 10.0.13
and the 10.0.12 app host, so consumer builds on the runners had used them. The test
reproduced as the runner account and not as another user. `runtime-build` now
restores into its own package folder, and the CI commands that restore for
consumers first remove staging-feed packages from the shared cache. .NET reports
`.nupkg.metadata` as hidden on Unix, so the first purge found nothing there
(**0294822**); **623a333** includes hidden files and removed 126 staging copies on
behemoth and 126 on the macOS ARM64 runner; Windows had removed 124 the run before.
CI run 38461706610 on **623a333** then passed the complete suites on
**10.0.13-ankus.1**: Linux x64 (PostgreSQL 18), macOS ARM64 (18) and Windows x64
(17). Release rehearsal 38464938810 on it then built and verified every package,
including the runtime on GitHub's hosted Intel macOS runner. After the fork moved
to `f97c4de` for patched build tasks, rehearsal
[38600378664](https://github.com/ankus-net/ankus/actions/runs/38600378664) on
**73b1a23** did the same in 23 minutes: the managed packages, the Linux x64,
Windows x64, macOS ARM64 and Intel macOS runtimes, and the verify job, with
publishing skipped as a rehearsal does.

Before the servicing update, the Linux version matrix
([38441422083](https://github.com/ankus-net/ankus/actions/runs/38441422083)) passed
**5370b64**, with the replication toolkit, on **10.0.12-ankus.5** for PostgreSQL 13,
14, 15, 16, 17 and 19. Its first PostgreSQL 15 job lost its runner: debian-dev's
container ran four version suites at once and the kernel killed the runner
service for memory. The container now has 48 GB, two of its runners take
version jobs instead of four, and the rerun passed.

| Runtime line | Evidence and remaining requirements |
| --- | --- |
| .NET 10 LTS | Patched runtime **10.0.13-ankus.1** passes complete suites for every supported PostgreSQL major on Linux x64, macOS ARM64, Windows x64 and Intel macOS. A release rehearsal on the commit to be released remains before 0.1.0. |
| .NET 11 STS | Required. The `net11.0` line (fork `9692d0ff7ad` on RC2) is selected by `Ankus.Sdk`, offered by `ankus new --framework net11.0`, and built by CI and releases. Its runtime passes the fork probes on all five RIDs, and the complete suite with every repository extension on `net11.0` passes for PostgreSQL 13–19 on Linux x64, macOS ARM64, Windows x64 and Intel macOS, and for 18 on Linux ARM64. The rebase onto the .NET 11 release remains. |
| Later LTS/STS releases | Repeat explicit compiler/runtime/RID and packed-consumer acceptance, following upstream support lifecycles and servicing supported majors. |

See the [public support policy](docs/src/content/docs/reference/dotnet-support.md)
and [maintenance/acceptance plan](docs/contributing/dotnet-support.md). Each
declared .NET major must have actual evidence; SDK or package configuration is
not runtime support proof.
The .NET 11 plan also requires compiler memory-safety acceptance for raw calls,
globals, callbacks and checked managed APIs. Typed pointer contracts and explicit
scalar-call unsafe contexts pass the current complete .NET 10 compositions;
that evidence does not establish the future .NET 11 contract.

## Beyond pgrx: logical replication toolkit

Goal: Ankus must make it possible to port a logical replication extension such
as pglogical to idiomatic C#. pgrx has no counterpart for most of this: its
workers run work in scoped `transaction(|| ...)` closures, its shared memory has
a fixed compile-time size, and replication, apply and child processes are only
reachable through raw `pg_sys`. Each capability below is therefore an Ankus
addition beyond pgrx parity, documented as such in the
[pgrx migration guide](docs/src/content/docs/getting-started/from-pgrx.md) when
it lands. Raw bindings remain available for anything not covered. The
[inventory](docs/contributing/evidence/logical-replication-toolkit.md) records
what pglogical uses and the PostgreSQL 13–19 interfaces behind each capability.

| Capability | Design | Status |
| --- | --- | --- |
| Replication connection | `PgReplicationConnection`, a managed client over PostgreSQL's own `libpqwalreceiver` (`walrcv_*`), as core's subscriber uses: connect, run SQL with rows, replication commands, `COPY` out and logical streaming with feedback, waiting on the socket and latch with cancellation. `CopyInto` reads a remote `COPY` into a local table through in-process `BeginCopyFrom`, as table synchronization does. No extra native dependency on any platform. | Done; backend tests pass on PostgreSQL 13–19 beta 4 (Linux x64) |
| Relation writes and lookups | `PgRelationWriter` over `execReplication.c` (`ExecSimpleRelationInsert/Update/Delete`, `RelationFindReplTupleByIndex/Seq`) with executor state, indexes, constraints, triggers, privileges and row-level security handled as the apply worker does; locked lookups with a row's commit origin and timestamp for conflict handling. `PgReplicatedRow` carries values in replication's text or binary form through type input and receive functions, with defaults and unchanged values. | Done; backend tests pass on PostgreSQL 13–19 beta 4 (Linux x64) |
| Replication origins | `PgReplicationOrigin` and `PgLsn`: create, find, drop, session setup and reset, per-transaction remote commit, progress and advance over `replorigin_*`, with the checks of the `pg_replication_origin_*` SQL functions and PostgreSQL 19's renamed origin state. | Done; backend tests pass on PostgreSQL 13–19 beta 4 (Linux x64) |
| Long-lived worker transactions | `PgBackgroundWorker.RunTransaction` already keeps one transaction open across any number of receives, latch waits and changes inside its callback, with Ankus's rollback and recovery rules. A port applies each remote transaction in one callback, from its begin message to its commit. | Done; the subscriber sample applies every remote transaction this way, and its backend test passes in CI on Linux x64, macOS ARM64 and Windows x64 |
| Startup-sized shared memory | `PgLwLock<THeader, TElement>`: a header and an array whose length a `[PgModuleLoad]` method chooses, such as one slot per `max_worker_processes`, behind one lightweight lock, with copies, indexers and span callbacks under the existing guard rules. | Done; backend tests pass on PostgreSQL 13–19 beta 4 (Linux x64) |
| Error context and exit cleanup | `PgErrorContext` adds lines to errors and messages raised while it is active, including managed exceptions. `PgProcess.OnExit` runs managed callbacks at process exit through one `before_shmem_exit` callback per process. `PG_ENSURE_ERROR_CLEANUP`'s FATAL case is covered by ordinary `try`/`finally`, because Ankus unwinds managed code before reporting a FATAL raised in a backend call. | Done; context and exit-callback tests pass on PostgreSQL 13, 18 and 19 |
| Child processes | `PgChildProcess.Run` starts a program such as `pg_dump` with `posix_spawn` (Unix) or `CreateProcessW` (Windows), without .NET `Process` and its `SIGCHLD` handling, and waits on the latch so cancellation and termination stop it. | Done; backend tests pass on PostgreSQL 13, 18 and 19 (Linux x64) |
| Output plugin aliases | `AnkusOutputPluginAlias` items publish a generated C library per name, such as `pglogical_output`, that loads the extension library and forwards `_PG_output_plugin_init` to its managed plugin; install and package copy it. | Done; backend tests pass on PostgreSQL 13, 18 and 19 (Linux x64) |

Order: the replication connection first, tested against the test cluster
itself, then relation writes, origins and long-lived worker transactions. The
[subscriber sample](samples/Ankus.Examples.Subscriber/) proves them together: a
background worker copies a publication's tables and streams and applies the
provider's changes, with restart after an apply error; CI run 38361916466 on
**18b64d5** passed it with the rest of the full suite on Linux x64 (PostgreSQL
18), macOS ARM64 (18) and Windows x64 (17). A benchmark compares it with
built-in logical replication (see the progress entry below). Every capability
in the table now has an API, tests and a guide, and the sample applies a remote
transaction slightly faster than the built-in subscriber.

## Implementation status

This matrix summarizes current conclusions. The complete inventory linked above
defines the full scope; family-level implementation is not API-by-API completion.

| Area | Verified implementation | Remaining work |
| --- | --- | --- |
| Errors and cancellation | Sticky query cancellation/FATAL, retained raw/memory errors, fail-fast backend access and explicit rollback recovery. Transaction-completion callback failures use a fixed-buffer emergency log path without PostgreSQL diagnostic allocation; it preserves the primary error and durable outcome. Abort-time FATAL remains FATAL while irreversible FATAL is promoted to PANIC. Primary-platform full CI passes. | Retain recovery guarantees; the version/platform matrix passes on runtime 10.0.13-ankus.1. |
| Workers and shared memory | Native signal globals, lifecycle/transaction boundaries and shared memory. Idle Wait recovers repeated real cancellation; transaction waits still abort and terminal reports remain sticky. Complete primary-platform CI passes. | The API and test mappings have no open item, and the version/platform matrix passes on runtime 10.0.13-ankus.1. |
| Functions and callbacks | Scalar/array/SETOF/TABLE, triggers/events, lifecycle, native callbacks, operators/conversions and installation-schema search paths. | None; the version/platform matrix passes on runtime 10.0.13-ankus.1. |
| Aggregates and SQL graph | Static abstract aggregate capabilities, typed Requires/Before/SupportFunction references with per-contract diagnostics at the authored value, deterministic SQL provenance and extended module magic. Empty SQL dependency anchors and exact authored CR/CRLF values pass complete native acceptance. | The inventoried contracts are mapped with no open item, and the version/platform matrix passes on runtime 10.0.13-ankus.1. |
| Generator caching | Detached equatable declaration/provider/reference models, stable declaration-header locations and cached dispatcher/C/SQL artifacts across multi-declaration body edits, unrelated member insertion and unrelated file insertion. Only selected SQL files are read, with the complete path catalog retained. Editor corrections cover 117 diagnostics, each kept only when the project still compiles. | Diagnostics are specific: each of the 515 IDs covers one condition and is reported from at most three places, with no catch-all message, and the declaration audit against pgrx 0.19.3 has no open option. |
| Values and ownership | Documented scalar, array, composite, temporal, JSON, network, geometry, custom codec and raw datum contracts. Interval equality, hashing and ordering agree with PostgreSQL while retaining exact components; complete primary-platform CI passes. | The [API](docs/contributing/evidence/pgrx-api-mapping.md), unit-test, inline-test and tooling-test mappings have no open item. Refresh version/platform evidence after each .NET servicing update; it is current for 10.0.13-ankus.1. |
| Runtime performance | Owned binary PgNumeric, direct decimal coefficient encoding, constant-time fixed-width array indexing, and a measured native guard for allowlisted pure built-ins. Sets, aggregates and triggers resolve their types and metadata once per call site, and trigger rows travel as bare cells with untouched NEW cells kept; scalar settings read their native variables; type modifiers and domains use PostgreSQL's direct coercion and `domain_check`. Catalog-miss errors require actual rollback; real-resource regressions and complete primary-platform CI pass. Persistent in-backend benchmarks provide batching, transaction modes and statistical baseline comparisons. | Guard acceptance is current on every supported major. Extend that tier only where ownership proofs and measurements justify it. Never weaken error recovery to reduce overhead. |
| Native bindings | pgrx 0.19.3 inventories over each installation's headers, PostgreSQL 19 RC1 included, and target-compiler ABI checks; typed pointers and ANKUS129 make raw caller obligations explicit. Complete current primary-platform compositions pass, with focused binding checks on all seven majors. | Audited on 2026-10-14: the declaration, header, OID and SQLSTATE catalogs match pgrx `fc91c63` and PostgreSQL's 13–19 beta 4 tags in check mode, and the version/platform matrix passes on runtime 10.0.13-ankus.1. |
| Custom scans | Native providers use the selected headers' method tables through `[PgNativeCallback]`, with child plans, parameters, mark/restore and parallel state. Managed providers implement `IPgCustomScan<TState>`; the generator registers them, chains the path hook and dispatches rows, rescans, EXPLAIN properties, ordered paths, backward reads, mark/restore and parallel scans over shared memory the provider lays out. | None; the version/platform matrix passes on runtime 10.0.13-ankus.1. |
| Development CLI | Version/project selection, installation/registration, cluster lifecycle, run/connect, test/regress/bench, property forwarding, environment selection, scriptable info, package prefixes and target export with `--target-info` builds, including Intel macOS from Apple silicon. Benchmarks are measured inside PostgreSQL and retained in named comparison groups. `install --sudo`, `test --runas` and `regress --runas` follow cargo-pgrx with real-account evidence. | Run-as cases now run on every runner; the Linux PostgreSQL 16 rerun ([38115917411](https://github.com/ankus-net/ankus/actions/runs/38115917411)) has 52 skips where it had 55. Persistent clusters already retain Windows event-log diagnostics through `PostgresServerLog`. |
| .NET templates | Version-matched ordinary extension and worker templates reuse the CLI assets and pin local tools. Optional xUnit/NUnit consumers exercise managed and named backend cases, ignore reasons, worker processes and cleanup alongside default MSTest consumers; all three list backend cases with `dotnet test --list-tests` before anything is published. Complete primary-platform CI passes. | None; the version/platform matrix passes on runtime 10.0.13-ankus.1. |
| API discoverability | Idiomatic attributed declarations, documented runtime APIs, named logging helpers and typed SPI interpolation. Compiler transport helpers are isolated in Ankus.CompilerServices and hidden from IntelliSense; complete primary-platform CI passes. `PgAssert` adds MSTest's tolerance, text and count checks. | Audited on 2026-10-14: all 29 public types in `Ankus.CompilerServices` are hidden from IntelliSense, the SQL interpolation handler being the last, and the generated API reference covers every other public type. |
| Packages and release | MIT license, Brandon Williams copyright, author/repository/project metadata, package icon and deliberate SDK/runtime boundaries. | Full release gates and supported-platform packages before publishing 0.1.0. |
| Documentation and samples | Public guides, generated API pages, pgrx migration and backend-execution guidance; current status separated from historical evidence. All 37 of pgrx's examples map to runnable samples with backend integration tests. Every guide was reviewed against the code on 2026-10-11 and corrected. | Recheck guides that change after the .NET servicing update and before release. |

The custom-type alignment review found no defect: variable-length PostgreSQL
types require at least four-byte datum alignment. Managed codec payload layout
is a separate contract, verified through copying/packed-field tests. Deriving
datum alignment from a CLR carrier or adding one/two-byte alignment would be
incorrect. See [the detailed review](docs/contributing/evidence/port-history.md#custom-datum-alignment-review).

## Current complete acceptance evidence

Primary CI runs all six modules against real published Native AOT extensions.
The latest successful primary CI source is **6d446a8**, with runtime **10.0.13-ankus.1**
built from fork commit `f97c4de`.
[CI 38533123229](https://github.com/ankus-net/ankus/actions/runs/38533123229)
passes. Each platform completed all six modules with zero failures; no primary job
timed out. The macOS and Windows jobs shared their hosts with two version matrices.
The previous primary evidence, **623a333** on fork commit `b9bc343`, and
**2ab2160** with **10.0.12-ankus.5**
([CI 38315500203](https://github.com/ankus-net/ankus/actions/runs/38315500203))
follow it in the table. The Linux job took 15m48s again after the other project's work on its
host was stopped; at **8c91b6b** that work had doubled its test step. Earlier **ankus.4** evidence,
including a complete [Intel refresh](https://github.com/ankus-net/ankus/actions/runs/37460600236)
on source **e41c687**, is retained below.

| Source | Platform / PostgreSQL | Result | Duration |
| --- | --- | --- | --- |
| [Latest primary CI 38533123229](https://github.com/ankus-net/ankus/actions/runs/38533123229), **6d446a8 / 10.0.13-ankus.1 (f97c4de)** | Linux x64 / 18 | 14,990 total; 14,938 passed; 52 platform skips; zero failures | 16m20s job |
| Same CI / revision / runtime | macOS ARM64 / 18 | 14,990 total; 14,925 passed; 65 platform skips; zero failures | 24m55s job |
| Same CI / revision / runtime | Windows x64 / 17 | 14,990 total; 14,944 passed; 46 platform skips; zero failures | 23m30s job |
| [Primary CI 38461706610](https://github.com/ankus-net/ankus/actions/runs/38461706610), **623a333 / 10.0.13-ankus.1 (b9bc343)** | Linux x64 / 18 | 14,986 total; 14,934 passed; 52 platform skips; zero failures | 16m15s job |
| Same CI / revision / runtime | macOS ARM64 / 18 | 14,986 total; 14,921 passed; 65 platform skips; zero failures | 16m12s job |
| Same CI / revision / runtime | Windows x64 / 17 | 14,986 total; 14,940 passed; 46 platform skips; zero failures | 17m50s job |
| [Primary CI 38315500203](https://github.com/ankus-net/ankus/actions/runs/38315500203), **2ab2160 / ankus.5** | Linux x64 / 18 | 14,883 total; 14,831 passed; 52 platform skips; zero failures | 13m24s test step; 15m48s job |
| Same CI / revision / runtime | macOS ARM64 / 18 | 14,883 total; 14,818 passed; 65 platform skips; zero failures | 14m11s test step; 16m15s job |
| Same CI / revision / runtime | Windows x64 / 17 | 14,883 total; 14,837 passed; 46 platform skips; zero failures | 15m29s test step; 17m42s job |
| Earlier primary CI, **8c91b6b / ankus.5** | Linux x64 / 18 | 14,862 total; 14,810 passed; 52 platform skips; zero failures | 30m49s test step under host contention; 36m07s job |
| Same CI / revision / runtime | macOS ARM64 / 18 | 14,862 total; 14,797 passed; 65 platform skips; zero failures | 14m05s test step; 16m07s job |
| Same CI / revision / runtime | Windows x64 / 17 | 14,862 total; 14,816 passed; 46 platform skips; zero failures | 15m54s test step; 18m02s job |
| Local complete suites, **07923d5 / ankus.5** | Linux x64 / each of 13.23, 14.24, 15.19, 16.15, 17.11 and 19 beta 4 | 14,861 total per major; 14,809 passed; 52 platform skips; zero failures | 26–27 minutes per major |
| Earlier primary CI, **554cbb4 / ankus.5** | Linux x64 / 18 | 14,592 total; 14,540 passed; 52 platform skips; zero failures | 16m22s test step; 19m00s job |
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
These results establish the named combinations only; the full port is still incomplete.

The first rows below use the current runtime, `10.0.13-ankus.1`. With the primary
CI above, every supported major passes on Linux x64, macOS ARM64, Windows x64 and
Intel macOS. Fork commit `f97c4de` changed only build-time tasks, so the Intel
majors that passed on `b9bc343` ran the same runtime and compiler.

| Earlier complete evidence | Platform / PostgreSQL | Result | Duration |
| --- | --- | --- | --- |
| [Scheduled versions 38561530691](https://github.com/ankus-net/ankus/actions/runs/38561530691), **6d446a8 / 10.0.13-ankus.1 (f97c4de)** | Linux x64 / each of 13–17 and 19 beta 4 | 14,990 total per major; 14,938 passed; 52 platform skips; zero failures | 26m58s–49m57s jobs, two majors per host |
| [Platform versions 38523432351](https://github.com/ankus-net/ankus/actions/runs/38523432351), **4f20a29 / same runtime** | macOS ARM64 / each of 13, 14, 16, 17 and 19 beta (assertions enabled) | 14,990 total per major; 14,921–14,925 passed; 65–69 platform skips; zero failures | 23m22s–30m22s jobs |
| Same platform-versions run / revision / runtime | Windows x64 / each of 13–16 and 19 | 14,990 total per major; 14,944 passed; 46 platform skips; zero failures | 30m28s–59m36s jobs |
| [Platform versions 38568898790](https://github.com/ankus-net/ankus/actions/runs/38568898790) and [38568893884](https://github.com/ankus-net/ankus/actions/runs/38568893884), **badb5d0 / same runtime** | macOS ARM64 / 15; Windows x64 / 18 | 14,990 total each; 14,925 and 14,944 passed; 65 and 46 platform skips; zero failures | 32m44s and 54m51s jobs |
| [Intel run 38569108147](https://github.com/ankus-net/ankus/actions/runs/38569108147), **2b1e0ec / same runtime** | macOS x64 / 19 beta | 14,990 total; 14,925 passed; 65 platform skips; zero failures | 1h29m53s job |
| [Intel run 38465047679](https://github.com/ankus-net/ankus/actions/runs/38465047679), **87afae3 / 10.0.13-ankus.1 (b9bc343)** | macOS x64 / each of 13–18 | 14,986 total per major; 14,921 passed; 65 platform skips; zero failures | 1h24m53s–1h27m36s jobs, run in turn |
| [Intel weekly 38115991171](https://github.com/ankus-net/ankus/actions/runs/38115991171), **f220a27 / ankus.5** | macOS x64 / each of 13–18 and 19 beta | 14,654 total per major; 14,589 passed; 65 platform skips; zero failures | 1h21m36s–1h24m00s jobs, run in turn |
| [Version CI 38102793609](https://github.com/ankus-net/ankus/actions/runs/38102793609), **be3fbcd / ankus.5** | Linux x64 / each of 13–17 and 19 beta 4 | 14,512 total per major; 14,457–14,460 passed; 52–55 platform skips; zero failures | 27m31s–35m27s jobs; 1h24m for three reruns sharing one host |
| [Platform versions 38109393201](https://github.com/ankus-net/ankus/actions/runs/38109393201), **660ea5e / ankus.5** | macOS ARM64 / each of 13–17 and 19 beta 4 (assertions enabled) | 14,642 total per major; 14,573–14,577 passed; 65–69 platform skips; zero failures | 25m52s–32m09s jobs |
| Same platform-versions run / revision / runtime | Windows x64 / each of 13–16 and 18 | 14,642 total per major; 14,596 passed; 46 platform skips; zero failures | 29m47s–1h00m43s jobs |
| [Intel CI 38109391178](https://github.com/ankus-net/ankus/actions/runs/38109391178), **660ea5e / ankus.5** | macOS x64 / 18.6 | 14,642 total; 14,577 passed; 65 platform skips; zero failures; run-as cases run | 1h24m04s job |

Earlier timings, outcomes, failed checks and superseded acceptance details remain
in the [evidence archive](docs/contributing/evidence/port-history.md#acceptance-table-before-spi-flow-acceptance-2026-10-05).

## Active validation and work

- Security checks run on every change. `SECURITY.md` describes private
  vulnerability reporting, Dependabot proposes NuGet, workflow and site updates
  weekly, secret scanning and push protection are on, and
  [Picket](https://github.com/willibrandon/picket) scans each change; its one
  false positive, a test key, was rewritten rather than ignored. The `CodeQL`
  workflow runs the `security-and-quality` suite on C#, workflows and the site's
  TypeScript, and on `main` a dedicated Linux runner traces the C bridge and
  fixtures that the integration tests compile. `eng/Ankus.CodeQl.cs` fails the
  workflow on any result. The first scan reported 3,181 C# results; all are fixed
  in code except 89 that describe code which must stay, each marked with a
  `// codeql[rule]` comment saying why: catches that hand every exception to
  PostgreSQL at the native boundary or report a cleanup failure with the one that
  caused it, two `GC.Collect` test helpers, interpolated SQL that `Spi.Sql` binds
  as parameters, and resources whose ownership passes elsewhere. A local scan of
  the committed tree reports zero unsuppressed C# and workflow results. Code
  scanning keeps a marked result open, so on `main` the same app dismisses its
  alert as won't fix with the comment's reason, and reopens it if a later
  analysis reports the result without the comment. CI run
  38489425334 passed on Linux x64, macOS ARM64 and Windows x64, and the Linux
  version matrix 38491737010 passed the complete suite on PostgreSQL 13 through
  19 beta 4.
- The traced C now passes CodeQL too. Its first scan reported 8,450 results in
  the emitted bridges and the fixtures composed from them. 7,524 came from two
  queries that read every `PG_TRY` as a stack address escaping into a global,
  since it links a jump buffer on its own stack into `PG_exception_stack`; a
  comment cannot mark a macro expansion, so `.github/codeql-config.yml` turns
  those two off and says why. The rest are fixed in the generator: a `PG_TRY`
  nested in another names its variables apart, through PostgreSQL's
  `PG_TRY(name)` from 16 and the same expansion before it; node layouts and an
  extension's base types are tables; long cases of the memory and list switches
  are functions; a strict function's NULL checks and frees are loops; handles
  are found through one checked lookup; and long functions and fixtures explain
  their steps. Local scans of the PostgreSQL 13 and 18 bridges of every sample
  and test extension, and of the fixtures, report no results. Generator and
  runtime unit tests pass (5,033 and 2,489). The complete PostgreSQL 18 suite on
  Linux x64 passed 14,935 of 14,987 with 52 platform skips before the last three
  memory switch cases moved into functions; the 763 memory, allocator, list,
  node and ownership backend tests pass after it, and the PostgreSQL 13
  integration suite passed 5,523 of 5,540 with 17 platform skips.
- The pinned runtime stopped building once its CI cache was evicted. October's
  .NET security release reports CVE-2026-70162 and CVE-2026-84008 against
  `System.Security.Cryptography.Pkcs` 9.0.6, which `NuGet.Packaging` brings to the
  runtime's installer and Crossgen2 build tasks, and the runtime build treats
  NuGet's audit warnings as errors. Fork commit `f97c4de` makes those tasks
  restore the patched 10.0.13; they only run during the build, so the runtime and
  compiler packages keep version `10.0.13-ankus.1`. The unpatched tree reproduces
  the failure and the patched one restores both tasks and the ILCompiler publish
  project. Ankus's own 72 projects have no vulnerable package. On the new commit,
  Linux version run 38523429618 passed PostgreSQL 13, 15, 16, 17 and 19 beta 4,
  and 14 failed one Valgrind case whose five-second startup timeout expired
  before PostgreSQL reached its preload on the shared host; it now allows 30
  seconds. Platform-version run 38523432351 passed macOS ARM64 13, 14, 16, 17
  and 19 beta and Windows x64 13–16 and 19. The macOS 15 suite passed, but
  runner cleanup failed when another runner's job kept the shared compiler
  server busy; cleanup now reports that as a warning. A host restart cut off
  Windows 18. Both cells then passed on `badb5d0`, the scheduled Linux run passed
  every major including 14, and Intel macOS passed 19 beta, whose earlier
  failures were the writer and `pg_dump` cases fixed since. CI on a merged
  Dependabot update later failed one Windows consumer restore with `NU1900`:
  both Windows runners share one account's NuGet HTTP cache, and NuGet's audit
  found a cached response still open in the other runner's restore. Each
  dedicated runner now keeps its own HTTP cache. A macOS CI suite then sat for
  over an hour on a consumer `dotnet publish` with no CPU use. Its managed stacks
  showed a deadlock inside the SDK between CLI telemetry and MSBuild's logging:
  the telemetry setup loaded an assembly whose load event MSBuild logs under its
  logging lock, while MSBuild, holding that lock, gave the telemetry an event
  that needed the same load. The workflows and the integration suite now turn
  CLI telemetry off.
- `ankus status` and `ankus info` seemed to hang when run from a large solution's
  directory, such as this repository's root. Without `--pg`, these commands use
  the PostgreSQL selection of the project or solution in the current directory.
  Reading it started one `dotnet msbuild` evaluation per project and per
  reference, one after another, and evaluated shared references again for every
  project: 46 seconds here on Linux and minutes on a slower machine. Projects are
  now evaluated side by side, each project and property set once, and results are
  still read in solution order, so the selection and the first reported problem
  are unchanged. The same commands take 6 seconds here, and `ankus test` on a
  solution benefits too. A failure stops the remaining evaluations and waits for
  their processes. New PgConfig tests cover a shared reference and the order of
  reported problems; the 52 selection and cluster backend tests pass on Linux x64
  with PostgreSQL 18.
- Test logs no longer accumulate without bound. Every run adds a server log per
  cluster and a binary log per extension publish, and nothing removed them: one
  validation checkout held 21,866 files and 6.4 GB under `artifacts/test-logs`,
  and an extension project's `bin/ankus-test-logs` grows the same way. pgrx keeps
  server output in memory for its failure report, so it has nothing to clean.
  The first cluster a test process starts in a log directory now removes the
  server logs, the files their collector keeps beside them (on Windows, PostgreSQL's
  stderr and the event sidecars) and the publish binary logs that the fixtures
  named, once they are seven days past their last write, and leaves every other
  file. The integration suite expires its own `artifacts/test-logs` the same
  way, and the package pack logs it writes into `artifacts` now keep one fixed
  name per package. Tests cover the recognized names, a prune beside foreign and
  recent files, and a real cluster start; with the cluster tests they pass on
  Linux x64 with PostgreSQL 18 (35 of 35).
- `PgHeapTuple.Set`, which triggers and custom scans call for every row, now
  skips the generated-mapping registries for built-in types such as `string` and
  `long`. It looked up the type's datum mapping and arrays first because a
  generated mapping could claim a built-in type, and then SPI parameters and tuple
  cells would have converted it differently. The datum and custom type registries
  now reject a type with a built-in conversion, or one whose array has one (`byte`
  for `bytea`), as Rust's coherence rules do for pgrx, so `Set` caches each
  built-in type's OID. Two new in-backend benchmarks went from 63.8 to 26.9 ns
  for a text cell and from 73.0 to 28.3 ns for an `int` cell, and the
  `NumberScan` sample's rows from 189 to 157 ns; details are in the
  [guard-tier evidence](docs/contributing/evidence/guard-tiers.md#managed-custom-scans).
  Runtime (2,490) and generator (5,033) tests pass, as do the 365 trigger,
  custom scan, composite and tuple backend tests on Linux x64 with PostgreSQL 18.
  CI 38615376583 on 81cd651, which includes the log retention change, passed on
  Linux x64, macOS ARM64 and Windows x64. Its first CI run had failed on Windows,
  where PostgreSQL writes stderr beside the retained log and the log appears only
  when read; retention now covers every file named after a cluster's log.
- .NET 11 RC2 (SDK 11.0.100-rc.2.26504.105, 2026-10-13) was checked against the
  `net10.0` target, as RC1 was. Its Native AOT publish skips the full build, so
  the output directory stays empty, yet it still copies the documentation file
  from there; extensions with documentation could not publish, and a plain
  `PublishAot` console app fails the same way. `Ankus.Sdk` defaults `UseOptimizedPublish` to
  `false`, which keeps the SDK 10 publish path and output. The test fixture's
  publish no longer inherits the MSBuild paths of the SDK that ran the tests,
  which had loaded SDK 11 tasks into an SDK 10 build. `dotnet sln add` now
  rejects a project that a reference adds again, and CA2026 flags an undisposed
  `JsonDocument`; one test each changed. With SDK 10.0.402 beside RC2 for the
  generated consumers, the complete PostgreSQL 18.6/Linux x64 suite passed
  14,956 of 15,010 with 52 platform skips. The two failures are an editor-load
  test's rows that see RC2's preview notice, which release SDKs do not print.
  On the stable SDK, CI 38633861172 on 0903e5d passed on Linux x64, macOS ARM64
  and Windows x64; the macOS job's first attempt lost nuget.org during restore
  (NU1900) and passed when rerun.
- The runtime fork now has a .NET 11 line. Its 31 changes over 10.0.13 were
  applied to `v11.0.0-rc.2.26504.105` as one change (fork commit `34f19a015e3`,
  branch `ankus/runtime-11.0-rc2`) and adapted where .NET 11 moved the code:
  the collector is split into separately compiled files, so the fork state is
  shared through its internal header; the thread pool's semaphore and worker
  requests were rewritten, so worker retirement and post-fork recovery follow
  the new design; and EventPipe sessions now publish before they enable. Native
  AOT also links one workstation collector build into both runtime flavors, so
  that build could not tell a server collector existed and the first server
  probe saw a single heap; each collector now exports its own entry points and
  the runtime, compiled per flavor, picks the one that owns the heap. .NET 11
  removed the debug header and auto-trace that two changes touched. Built with
  Clang 19 for Linux x64, the runtime passes every fork probe: 10 retained
  service and descendant cases, workstation and server active-GC with fixed and
  dynamic heaps, all eight diagnostics suites, host shutdown, signal masks and
  host signals.
- The `net11.0` PostgreSQL major matrix is complete: with every repository
  extension on `net11.0`, the complete suite passes for PostgreSQL 13–19 on
  Linux x64 and macOS ARM64 (`.NET versions` runs from 8d495aa and 9f356ee),
  Windows x64 (the same, and fd1ce1d for 16 after its GUC race fix) and Intel
  macOS (runs 38691451402 to 38691511171 on 635ec77, one major at a time on the
  Intel runner).
- Extensions can target `net11.0`. `Ankus.Sdk` picks the runtime line from the
  extension's framework (e9e34eb), the package projects and CI build each line
  (c46e410), and `ankus new --framework net11.0` or the templates' `--framework`
  option sets the projects' framework and pins SDK 11 RC2 with prerelease
  allowed. .NET 11's Native AOT publishes the library under `TargetName`, so a
  custom library name now replaces that entry and publishes its symbols beside
  it. Releases build and verify both lines' packages on every RID, and the
  `.NET versions` workflow takes a PostgreSQL major. Retargeting every repository
  extension to `net11.0` exposed three test assumptions: native fixtures searched
  every framework's build output for the emitted bridge, the scaffold tokens were
  missing from the tool's pattern, and the design-time build test counted the
  preview SDK's NETSDK1057 notice. The fixture feed now packs both lines, and
  the framework-template consumer cases target the extensions' own framework, so
  a `net11.0` run builds and tests real .NET 11 consumers in PostgreSQL.
  On PostgreSQL 18.6/Linux x64 those changed cases pass 15 of 15 on both
  `net10.0` (SDK 10) and `net11.0` (SDK 11 RC2). The first `.NET versions`
  dispatches found three CI faults, now fixed: runtime commands rejected the
  line argument they had just gained; fork probes published from the repository
  root, whose `global.json` selects SDK 10, instead of their own directories,
  which pin each line's SDK; and on Windows the runtime checkout's own SDK kept
  build servers running, so cleanup could not delete it. Releases now install
  SDK 11 for the `net11.0` probes too. The Windows `net11.0` runtime built and
  packed on its first attempt. In `.NET versions` run 38663466641 on 6a33260 the
  `net11.0` runtime builds and passes its fork probes on Linux x64, macOS ARM64,
  Windows x64 and, in run 38663487329, Intel macOS. With every repository
  extension on `net11.0`, the complete suite passes on macOS ARM64 against
  PostgreSQL 18.6 (14,954 passed, 65 platform skips) and on Windows x64 against
  PostgreSQL 17.11 (14,973 passed, 46 skips), about 25 minutes each, and on
  Linux x64 against PostgreSQL 18.6 (14,967 passed, 52 skips) in 55 minutes, as
  it also does locally. Intel macOS passes it against PostgreSQL 18.6 (14,954
  passed, 65 skips) in 83 minutes. SDK
  11's `dotnet test` shows the test host's live output, so a binding test's
  deliberate compiler errors became annotations; that step now runs without
  setup-dotnet's compiler matcher.
- PostgreSQL 19 resets `ErrorContext` after every outermost report (commit
  89d57c1fb35), and a reset deletes the contexts beneath it. A guarded backend
  call made its per-operation context a child of the caller's, so a report
  from code running in a child of `ErrorContext` deleted that context, and the
  call then deleted it again. PostgreSQL keeps small deleted contexts on a free
  list for reuse, so the second delete listed the same storage twice and later
  contexts shared it. On Windows with PostgreSQL 19 beta 4 under `net11.0`,
  that left a cycle in the context tree, so listing contexts requested 1 GB and
  the next `pfree` saw an invalid pointer; the earlier macOS `AllocSetCheck`
  fault was the same corruption. Assertion builds miss it because a recycled
  context still looks valid. A 19 build that frees deleted contexts and
  overwrites them failed the test every time, and symbolizing the bridge
  showed the second delete. Beneath `ErrorContext` the operation now owns a
  context under `TopMemoryContext` and deletes it on every path. The test also
  creates three small contexts after the report and requires distinct ones;
  with the old bridge a normal PostgreSQL 19 build then crashes every time,
  and with the fix the memory classes pass on 19 under `net10.0` and
  `net11.0`.
- Running the complete suite on that overwriting 19 build found a second fault.
  PostgreSQL leaves `ErrorContext` current while an ERROR propagates, and
  logical decoding's handler restores whatever was current when it caught the
  error after rolling back the transaction it decoded in. Ankus's error reporter
  and the entry points PostgreSQL calls switched back to their caller's context
  before rethrowing, so when the WAL decoder sample rejected a type, that
  rollback deleted the caller's context and the next handler allocated in it.
  Catch blocks now return to the context the error arrived in before
  rethrowing, and finally blocks restore their caller's context only when no
  error has left `ErrorContext` current. A native fixture calls a failing
  function and records the context current when its ERROR arrives: a SQL
  function's error and an Ankus function's both arrive in `ErrorContext`, and
  before the change the Ankus one did not.
  With both fixes, the complete integration suite passes on the overwriting
  PostgreSQL 19 build: 5,539 passed and 17 skipped, with no assertion failure.
- The project moved to the `ankus-net` GitHub organization, with the runtime
  fork beside it, and the documentation to `https://ankus.net`, the repository's
  Pages custom domain. Package metadata, workflows, runner guards and every link
  name the new home; the site is served from the domain root, so it no longer
  needs a base path or the plugin that added one. Packages publish from the
  `ankus` nuget.org organization through a trusted publishing policy for
  `ankus-net/ankus`, and nuget.org has reserved the `Ankus` ID prefix for that
  organization.
- Linux ARM64, which pgrx supports, is now a fifth runtime identifier. Both runtime lines enabled the collector's fork checkpoints and
  the runtime's fork support only on Linux x64 and macOS; that code relies on
  POSIX fork handling rather than an instruction set, so the fork now enables
  it on Linux ARM64 too (`72c3927a21d` for .NET 10, `9692d0ff7ad` for .NET 11).
  The schema reader accepts AArch64 ELF libraries and rejects one architecture
  where the other was requested. Runtime and compiler packages, release
  verification and the release matrix gain `linux-arm64`, built on GitHub's
  hosted `ubuntu-24.04-arm` runners, and the `Linux ARM64` workflow runs the
  complete suite there on both target frameworks. The first run built both runtime lines and passed their fork probes on
  Linux ARM64. Its tests then failed to compile: plain C `char` is unsigned
  there, and bindings followed the target, so `char *` became `byte*` on Linux
  ARM64 and `sbyte*` everywhere else, and code written for one did not compile
  on the other. Bindings now map plain `char` to `sbyte` on every target, as
  Rust's `c_char` gives portable code one name, and reject a plain `char`
  bitfield on an unsigned target, where its value would read with the other
  sign. PostgreSQL's headers declare none. In `Linux ARM64` run 38706865565 on
  8b8e04e, the complete suite then passes against PostgreSQL 18 on both
  `net10.0` and `net11.0`: 14,973 passed and 52 platform skips each, in about
  72 minutes on the hosted runner.
  On PostgreSQL 13 to 17 (runs 38756151062 to 38756208956 on 0746e49) every
  test passed on both frameworks except one run-as case: the
  run-as account could not execute `initdb`. Tests stage PostgreSQL 13 to 17
  inside the workspace, and a hosted runner's home directory does not let
  other accounts search it; 18 and later run from the system package and
  passed. CI now lets other accounts search the directories above the
  workspace on hosted runners; listing them stays closed.
- PostgreSQL 19 RC1 changed an executor API. The hosted ARM64 runner installed
  `19~rc1` and every extension failed to compile: `RelationFindReplTupleByIndex`
  gained a `skipduplicates` argument (2f7bf51b7b0), because the apply worker now
  records whether its key index is the replica identity instead of deciding
  again during the search, where a concurrent REINDEX could change the answer.
  16 to 18 keep the old function and add `RelationFindReplTupleByIndexExt`.
  Beta 4 and RC1 both report `PG_VERSION_NUM` 190000, so the relation writer
  selects the form by catalog version (202610061 in RC1) and passes `true`: its
  key is always the replica identity or primary key. RC1 changed no SQLSTATE,
  type or function catalog entries. The packaged PostgreSQL 19 on the
  dedicated Linux hosts is now RC1, and the complete suite passes on it on
  Linux x64: 14,976 passed and 52 platform skips. Every runner host now has
  RC1: the packaged server on the dedicated Linux hosts, `ankus init --pg19
  download` on both Macs (which selects the newest prerelease), and a meson
  build of the checksum-verified source on Windows, the same way beta 4 was
  built there. The complete integration suite also passes on an RC1 build
  with assertions that never recycles deleted contexts and overwrites them:
  5,540 passed and 17 skipped.
- Release rehearsal 38759505000 on 0746e49 failed both Windows runtime jobs
  32 seconds into the runtime's `build.cmd`, right after its restore and
  before any compilation, and the log showed only the exit code: CI starts
  batch files through the shell, whose console the runner never reads. The
  same build passes when run as the runners' NETWORK SERVICE account with
  their environment, with both runtime lines building at once, and with both
  extracting into one empty package folder, so the trigger is not yet known.
  The release path differed from CI's in one way: it restored into the
  runners' shared package folder instead of one of its own. It now isolates
  that folder as CI does, and a batch file's output goes to a log the job
  prints. In rehearsal 38778949479 on 6ae03a5 both Windows lines then built
  and packed their runtimes, which places the fault in the shared folder,
  though its exact trigger was never captured. The job's new cleanup step
  failed instead: the runtime compiles with the compiler from Roslyn's toolset
  package, whose server the SDK's `build-server shutdown` does not reach, so
  it kept the checkout's own SDK open and `git clean` could not delete it.
  Cleanup now stops any process still running from that SDK, which only the
  job's own build uses. In both rehearsals every other runtime package job
  passed, including Linux ARM64 and hosted Intel macOS on both lines. CI
  38780573792 on 03b4375 passes on all three platforms. On 6ae03a5 one
  Windows case had failed to link: the MSVC linker rejected the object
  ILCompiler wrote with `LNK1103: debugging information corrupt`. It had never
  happened before and did not recur; it ran while manual MSVC builds of the
  runtime loaded the same host, against the rule to keep that host free while
  its runners work. If it happens again on an idle host, it needs its own
  investigation.
- The first PostgreSQL 19 RC1 platform run (38785565865 on 03b4375) failed on
  both hosts for reasons outside RC1. On macOS ARM64 the disk filled while the
  release rehearsal's two runtime builds ran beside the suite, with 20 GB of
  stale investigation trees and two profiling clusters of mine still on the
  runner hosts; those are removed. On Windows one case's cleanup was
  canceled: five tests set a short `statement_timeout` (100 ms to 1 s) to
  provoke a cancellation and then reset it on the same session, but the
  reset is itself armed with that timeout, and with two runtime builds
  compiling on the same host it ran longer. The program-cancellation test now
  gives the timed statement its own connection, configured with the timeout
  at startup, and the four that check same-session recovery retry their
  cleanup when the timeout they set cancels it.
- CI 38791251483 on 12f71f8 failed before building: restoring the CI script on
  a Windows runner reported NU1900 because another runner was replacing
  NuGet's vulnerability data in the service account's shared HTTP cache. The
  three Windows runners share that cache and a package folder, but NuGet
  coordinates them through lock files under `NUGET_SCRATCH`, which defaults to
  each runner's own `TEMP`. Since 32390ee test builds used a per-runner HTTP
  cache, but the CI script's own restore ran before that setting. The
  workflows now set `NUGET_HTTP_CACHE_PATH` beside each runner's checkout for
  the whole job.
- PostgreSQL 19 RC1 passes the complete suite on every platform CI covers:
  Linux x64 (run 38785556292, 14,976 passed and 52 skips), Linux ARM64 on
  both frameworks (38777760999, 14,976 and 52 each), macOS ARM64 (38791251393,
  14,963 and 65), Windows x64 (38791251393, 14,982 and 46) and Intel macOS
  (38785575156, 14,963 and 65). Linux ARM64 on
  PostgreSQL 13 (38777749438) passes on both frameworks with the run-as fix.
  Release rehearsal 38788408053 on be35ef6 builds, packs and verifies every
  package for all five runtime identifiers on both lines; later commits
  change only tests and documentation.
- A first user of 0.1.0 saw `dotnet build` fail with only `MSB3073: The command
  "dotnet …/Ankus.Build.dll binding-sources …" exited with code 1`. No
  PostgreSQL 18 was registered on that machine, and the build tool said so,
  but it wrote the message to standard error as plain text, which MSBuild's
  `Exec` logs as a message that the default console output hides. Every
  failure of the build tool was hidden the same way. Its first line on
  standard error is now in MSBuild's canonical error format, `Ankus.Build :
  error : <message>`, so the build reports it as an error, with the details
  after it. The not-found and not-registered messages now offer `ankus init
  --pgNN download` beside a `pg_config` path. Tests run the tool directly and
  through `Exec` and fail with the old output; a PgConfig test checks the
  registry message. This ships in 0.1.1.
- Raising the repository's default version to 0.1.1 failed six integration
  tests on every platform (CI 38814462084). Samples take their extension
  version from the package version, and the tests repeated `0.1.0` in script
  names and catalog checks. They now read each sample's `default_version` from
  its published control file, so a release no longer requires editing tests.
- 0.1.1 is released (release run 38821901839 on 0fe3c72). Its first attempt
  lost GitHub's hosted Intel macOS runner partway through building the
  `net11.0` runtime ("the hosted runner lost communication with the server");
  the re-run built it, verified every package and pushed the seven 0.1.1
  packages. The runtime and compiler packages were unchanged and nuget.org
  already had them, so they were skipped as duplicates. `net11.0` also passes
  the complete suite against PostgreSQL 19 RC1 on Linux x64, macOS ARM64,
  Windows x64 (run 38804186774) and Intel macOS (38804197997).
- After updating the tool to 0.1.1, the same user's `ankus upgrade` kept
  reporting "already current". `ankus upgrade` finds versions with `dotnet
  package search`, and NuGet keeps each feed's package listing for 30 minutes.
  The first upgrade ran at 00:52:36 UTC, before nuget.org added 0.1.1 to the
  Ankus.Sdk listing at 00:53:45, and every upgrade until that copy expired
  reused it. The tool update itself had seen 0.1.1 in the separate Ankus.Tool
  listing. Version discovery now uses an empty HTTP cache, so each upgrade reads
  the feeds' current listings; the fixed tool chose 0.1.1 on that machine
  while its stale copy remained. A test publishes a release on a local feed
  between two upgrades and fails without the change. This ships in 0.1.2.
- `ankus upgrade` also left the `ankus.tool` pin in a generated solution's
  `.config/dotnet-tools.json` at its old version, so `dotnet ankus` kept
  running the old tool. It now updates that pin in the manifest `dotnet tool
  restore` would use, found the way the .NET SDK finds it: each directory's
  `.config/dotnet-tools.json`, then `dotnet-tools.json`, from the project
  upward, stopping at the first manifest that lists the tool or is marked as
  the root. Tests check the generated solution's manifest, other tools and
  formatting, and that a manifest beyond a root one is left alone.
- 0.1.2 is released (release run 38839379088 on 87d99c5). The same user
  upgraded with it while nuget.org was still listing the release: Ankus.Tool
  and Ankus.Testing already listed 0.1.2 but Ankus.Sdk did not, and because
  each package took its own newest version, the solution mixed Ankus.Sdk 0.1.1
  with Ankus.Testing 0.1.2 (NuGet warning NU1608 on `Ankus.Runtime`). Ankus
  packages are released together with one version, so `ankus upgrade` now
  plans twice: the first pass records which Ankus packages take their version
  from the package sources, and the second moves them all to the newest
  version every one of them lists, with a note when a newer release is only
  partly listed. A test lists 0.3.0 for Ankus.Tool alone and checks that the
  package and the tool pin both move to 0.2.0 and the note names
  Ankus.Testing.
- The build on that machine then failed with only `/usr/bin/clang exited with
  1: #define __llvm__ 1`. It has Arch Linux's `postgresql-libs`, which installs
  `pg_config` but not the server headers, and discovery accepted it from
  `PATH`. Discovery now skips an installation whose server include directory
  has no `postgres.h` and names it in the not-found error. The preprocessing
  failure also put clang's macro listing before its error, so the build's
  one-line summary showed a `#define`; the compiler's error now comes first. A
  PgConfig test discovers a `pg_config` without headers, then with them, and a
  Build test checks that a missing `postgres.h` is the first line. Both fail
  without their fixes.
- 0.1.3 is released (release run 38847011548 on 94a8654) with the one-release
  upgrade, the header check and the compiler error order. nuget.org listed
  all seven packages within six minutes of the push. From nuget.org alone, a
  solution created with `Ankus.Tool` 0.1.2 upgraded to 0.1.3 after `dotnet
  tool update` (global.json, Directory.Packages.props and the tool pin) and
  its 7 tests passed against PostgreSQL 18 on Linux x64.
- The guides told users to run `dotnet new install Ankus.Templates::0.1.3`.
  The .NET 10 SDK still accepts `::` but warns that it is deprecated in favor
  of `@`, which a user saw on the first install. The README, the getting
  started guide and the CLI reference now use `Ankus.Templates@<version>`. A
  template test reads the command from all three, requires them to agree, and
  runs it as written against the test feed, failing on any deprecation
  warning. No release was needed; the packages were unaffected.
- On a Mac with Xcode, `dotnet build` of a new solution failed with only
  "Native preprocessing requires one supported Clang identity". The build ran
  the `clang` on `PATH`, which is Xcode's Apple clang 17, while Homebrew's LLVM
  20, 22 and 23 were installed but, as Homebrew does, kept off `PATH`. When
  `AnkusClangPath` is unset, the build now takes the first Clang 20 or later
  among `PATH`, Homebrew's `llvm` and `llvm@N` on macOS, `clang-N` and
  `/usr/lib/llvm-N` on Linux, and `Program Files\LLVM` on Windows; if none
  qualifies, the error names each Clang it found and its version. An explicit
  Clang that is too old reports its version and the setting to change. On that
  Mac, the 0.1.3 build tool fails and the fixed one collects and verifies the
  PostgreSQL 18 bindings. Build tests select a newer Clang over an older one
  first on `PATH`, check both messages, and read the host Clang's version.
- 0.1.4 is released (release run 38862847295 on d9a5824) with the Clang
  selection. nuget.org listed all seven packages by 07:24 UTC. On the Mac
  where 0.1.3 failed, a copy of that solution upgraded to 0.1.4 and built with
  Xcode's Apple clang 17 still first on `PATH` and `AnkusClangPath` unset.
- On Windows, the 0.1.4 build failed in the binding layout probe with
  `link.exe exited with 1: link: extra operand '/OUT:...checks.exe'`. The
  probe ran a bare `link.exe`, and outside a Developer Prompt the first one on
  `PATH` was Git for Windows' coreutils `link`, while MSVC's linker sat beside
  the `cl.exe` the build had passed by full path. The probe now links with that
  adjacent `link.exe`, and finds `llvm-objcopy.exe` on `PATH` or in LLVM's
  standard installation, as the Clang selection does. On the machine that
  failed, with Git's `link.exe` still first on `PATH`, the 0.1.4 build tool
  fails and the fixed one collects and verifies the PostgreSQL 18 bindings.
  Build tests check the adjacent linker, a compiler named without a directory,
  and a missing LLVM tool.
- 0.1.5 is released (release run 38908582715 on 1bb1ce4) with the Windows
  linker fix; its first CodeQL run flagged a `foreach` that maps its variable,
  rewritten with `Select`. On the Windows machine that failed, with Git's
  `link.exe` still first on `PATH`, a copy of that solution upgraded to 0.1.5
  and built.
- pgtail (willibrandon/pgtail#67) could not find an Ankus cluster's log: it
  read `logging_collector` only from `postgresql.conf`, where Ankus's included
  `ankus.conf` turns it off, and had no way to learn where `pg_ctl -l` sends
  the server's output. Each `ankus start` now writes that path to
  `.ankus-log` in the data directory, one line: `<label>.log`, or on Windows
  the `<label>.log.stderr.log` file the `.log` snapshot is built from. The
  `.ankus-cluster` marker stays the label alone, because every Ankus version
  compares it before using a cluster, and an older tool would otherwise refuse
  clusters a newer one had started. An integration test checks the recorded
  path, that the server's own warning reaches that file, the unchanged marker,
  and the new path after the Ankus home moves.
- 0.1.6 is released (release run 38943653260 on b0b110a) with `.ankus-log`.
  On Windows x64, `ankus start` from the 0.1.6 package in a temporary home
  wrote `pg18.log.stderr.log` to `.ankus-log`, and that file held the
  server's "ready to accept connections". A first attempt on a port already in
  use still wrote the file, since Ankus writes it before launching the server.
- The Clang search added in 0.1.4 covered Homebrew but not MacPorts, which
  keeps each LLVM release under `/opt/local/libexec/llvm-N` and links only
  suffixed names such as `clang-mp-20` into `/opt/local/bin`. pgrx's bindgen,
  through clang-sys, has no MacPorts directory either, but accepts any
  libclang 11 or later, so Xcode's always satisfies it; Ankus needs Clang 20.
  macOS now also tries MacPorts' versioned installations, newest first. On the
  Intel Mac mini, with Apple clang 17 first on `PATH` and only MacPorts LLVM
  20 installed, the 0.1.6 build tool stops at Apple clang and the fixed one
  collects and verifies the PostgreSQL 18 bindings. Build tests check the
  search order and that a MacPorts Clang wins over an older one on `PATH`.
- CI 38970927551 on 1e72b1e lost its Windows/PostgreSQL 17 test job before any
  test ran: building a test extension, the binding step could not create its
  preprocessor output file in the runner's temp directory, and Windows
  returned "Insufficient system resources exist to complete the requested
  service" (`ERROR_NO_SYSTEM_RESOURCES`). The copy uses 64 KB buffers, the
  host had 1.1 TB free on D: and 31 GB of free memory afterwards, the System
  log has no resource exhaustion event that day, the change under test only
  touches macOS, and none of the 25 previous CI runs' Windows jobs reported
  it. The job was rerun. Record it again if it repeats.
- The three Windows runners' temporary directories held about 227 GB in
  roughly 147,000 entries. Most were empty directories with random names: the
  .NET SDK's tool downloader (`ToolPackageDownloaderBase`) calls
  `Directory.CreateTempSubdirectory()` for its asset files and never deletes
  the directory, so every `dotnet tool install`, `update` or `restore` leaves
  one or two behind, and a suite leaves about a hundred.
  The space was in Ankus test roots that cancelled suites had left, such as a
  30 GB one from 2026-10-06; cleanup removed only `ankus package tests *`
  directories. `runner-cleanup` now removes every `ankus*` temporary directory
  and every empty one older than 12 hours, skipping other accounts' entries
  in a shared `/tmp` silently. Applying the same rule once by hand on the idle
  Windows host removed 172 abandoned Ankus directories and about 146,000 empty
  ones and freed about 226 GB.
- 0.1.7 is released (release run 38975555924 on 1e72b1e, after the rerun of
  CI's Windows job above). On the Intel Mac mini, with Apple clang 17 first on
  `PATH` and `AnkusClangPath` unset, a solution created by `Ankus.Tool` 0.1.7
  from nuget.org built against MacPorts PostgreSQL 18 and LLVM 20.
- Background workers never flushed their statistics (#5): `RunTransaction`
  committed without `pgstat_report_stat`, so a worker's row and I/O counts
  stayed in its own memory until it exited, and autovacuum never saw tables a
  long-running worker wrote. A job queue built on Ankus bloated until its
  throughput collapsed. pgrx's `BackgroundWorker::transaction` has the same
  gap. As PostgreSQL's worker_spi does, the bridge now reports the worker as
  active during a transaction and idle after it, flushes the counts after
  each transaction without force, which PostgreSQL rate limits, and flushes
  with force before `Wait` sleeps outside a transaction. An integration test
  starts a worker that inserts 100 rows and then waits, and requires
  `pg_stat_user_tables` to show the inserts and `pg_stat_activity` to show the
  worker idle while it still runs.
- Release run 38839379088's net11.0 macOS ARM64 runtime job warned that "a
  build server did not stop": its cleanup ran `dotnet build-server shutdown`
  while the MacBook's other runner was compiling, and the shared compiler
  server refused (`VB/C# compiler server failed to shut down`). The same
  warning appeared on two earlier macOS ARM64 jobs. Runners on one host share
  the account's compiler server, and the compiler loads analyzers without
  locking them, so the server holds nothing in a checkout and stopping it only
  interrupted the other job. Cleanup now leaves the host SDK's servers
  running; it still stops the servers of the runtime checkout's own SDK.
- A review of Ankus from building an extension with it became issues #6
  through #13, and a macOS run became #18. Each was fixed in its own pull
  request, whose CI passed the complete suite on Linux x64 and macOS ARM64
  with PostgreSQL 18 and on Windows x64 with PostgreSQL 17 on GitHub-hosted
  runners, with CodeQL clean.
  - `PgSettings.Run` (#6, PR #24, CI 39022542430) changes settings while a
    callback runs, as a function's `SET` clause does: it opens a GUC nest
    level, saves each value with `GUC_ACTION_SAVE` and the current user's
    privileges, and calls `AtEOXact_GUC` when the callback returns or throws.
    Values are checked in a subtransaction first, so a bad name, value or
    privilege is a `PgException` the caller can catch; an error that must
    roll back leaves the values to that rollback. Its integration tests cover
    each way a callback ends, nesting, a plain `SET` inside a scope and the
    three rejections, and pass on PostgreSQL 13, 15, 18 and 19 RC1 on
    debian-dev; the new C compiles without warnings under `-Wall -Wextra`
    against the 13 and 18 headers.
  - Benchmarks (#7, #9, #11; PRs #22, #19, #20, CI 39011781272, 39009051439,
    39010077689). `RollbackPerBatch` and `RollbackPerIteration` roll the
    benchmark subtransaction back natively, and a probe benchmark passes only
    when every iteration starts from an empty table. `ankus bench` warns when
    `debug_assertions` is on, and `ankus init --pgNN download --release`
    builds without assertions under its own build identity. Bench runs `LOAD`
    first and, when the server says the library needs
    `shared_preload_libraries`, preloads it and restarts.
  - Tooling (#8, #10, #12, #13, #18; PRs #21, #17, #16, #15, #23, CI
    39010899066, 39008088070, 39019199283, 39007253379, 39012397015).
    `ankus regress` stages the extension through `extension_control_path` on
    PostgreSQL 18 and later, so a read-only installation is left alone, and
    explains the requirement before 18. A failed source build leads with
    configure's or the compiler's error, keeps the output and `config.log`
    beside the installation, and finds pkg-config in Homebrew's and MacPorts'
    directories. Test clusters create the `postgres` superuser, as
    `ankus start` does, so a trusted extension's objects are now owned by
    `postgres`. Root help lists commands as `dotnet --help` does, each default
    appears once, and `schema --no-build` replaces `--skip-build`, which
    stays as a hidden alias. Installation paths are compared with symbolic
    links resolved; on the Intel Mac mini `ankus init --home /tmp/...
    --pg18 download` now installs PostgreSQL 18.6.
- Pull requests always ran on GitHub-hosted runners, 90 to 110 minutes each,
  because CI picked the dedicated runners only for pushes to `main`. A pull
  request willibrandon opens from a branch in this repository now uses them
  too; forks and other authors' pull requests stay on GitHub's. Merging the
  review's pull requests queued nine `main` runs behind the one runner with
  `ankus-linux-primary-x64`, so behemoth's two version runners carry that
  label as well. The merged runs then exposed four test problems the hosted
  runners had hidden, each fixed through its own pull request:
  - On Windows, `pg_ctl` starts the server through `cmd /c`, which can
    outlive `pg_ctl stop` while it still holds the server log, so moving or
    reading the cluster right after a stop failed in
    `StartRecordsTheServerLogPath` on every dedicated Windows run (main CI
    39041910214, its rerun, and 39041919277). A stop now waits, up to ten
    seconds, until nothing holds the log open for writing; the test passed on
    the dedicated Windows runner with the change and failed without it in the
    same round.
  - `RegressLeavesAReadOnlyInstallationAlone` looked for the probe's control
    file, which an earlier test can already have installed into the staged
    copy; it now compares the directories before and after (main CI
    39042214181).
  - `ValgrindStartupFailureRecovers` gave a start under Memcheck five seconds
    to reach a failed bind; on a busy runner it timed out first (main CI
    39042224967). A failed bind ends the start when the server exits, so it
    uses the normal startup timeout.
  - The Subscriber sample's `unsubscribe` never waited for its terminated
    worker: its loop began with `pg_stat_clear_snapshot() IS NULL`, and a
    void result is not null. When the provider's slot drop finished first,
    dropping the origin failed "in use by PID" (CI 39056254299). It now
    clears the snapshot in its own statement before each check.
  The Docs workflow also gained a `pages` concurrency group after two merges a
  minute apart collided on one Pages deployment (Docs run 39041919234).
- 0.2.0 is released (release run 39070043803 on 2b69103, after CI 39067583224
  passed on the dedicated Linux, macOS and Windows runners with CodeQL clean).
  The hosted Intel macOS net10.0 runtime job lost its runner mid-build, as it
  has before, and passed on rerun. The seven 0.2.0 packages were pushed and
  the unchanged runtime packages skipped; nuget.org listed all seven within
  four minutes. On debian-dev, `Ankus.Tool` 0.2.0 from nuget.org created a
  solution whose 7 tests pass against PostgreSQL 18, and a function added to
  it returned `250ms|5s` from `PgSettings.Run("lock_timeout", "250ms", ...)`
  under `ankus run` with the session at `5s`. `ankus run` still installs into
  the PostgreSQL installation, so Debian's read-only packages need a writable
  build there, as `ankus regress` did before PostgreSQL 18 staging.
- Every Ankus project failed to build in Visual Studio 2026, whose MSBuild
  runs on .NET Framework. `Path.GetDirectoryName("")` throws there, and the SDK
  and Ankus.Testing called it on an empty `AnkusPgConfigPath`; the binding
  compiler took the .NET SDK from `MSBuildToolsPath`, which under Visual
  Studio is its own MSBuild folder; the 89-character binding file name pushed
  a project about 120 characters deep past the 260-character limit that
  MSBuild on .NET Framework enforces without Windows long paths; and every
  test project got MSB3270, because the SDK marked the extension's IL x64 for
  its runtime identifier, which only the .NET Framework reference resolver
  can read. PR #35 skips the empty lookups, uses `NetCoreRoot` and
  `NETCoreSdkVersion`, keeps 16 digits of the binding identity in the file
  name (the assembly still embeds and verifies all 64) and defaults
  `PlatformTarget` to `AnyCPU`. `VisualStudioBuildsNewSolutionCleanly` builds
  a new solution with Visual Studio's MSBuild on Windows, with and without an
  explicit pg_config, and requires no errors or warnings.
- 0.2.1 is released (release run 39119671753 on 2b5c98f). The first 0.2.1 run,
  39112874049 on 9f694a5, failed in both Windows runtime jobs: Visual Studio
  18 Insiders on the runner host had updated itself overnight from MSVC 14.51
  to 14.52.37025, which reports C4047 in the runtime's `dn-umap.c` (a `bool`
  function returning a pointer), and the runtime's `init-vs-env.cmd` picks
  the newest Visual Studio including previews unless a developer environment
  is active. CI never noticed because its runtime build is cached by fork
  commit. PR #38 runs the `windows-toolchain` step, which activates the newest
  stable Visual Studio (18.11, MSVC 14.51.36231), before both runtime builds;
  a release dry run, 39114517547, then built every package. Upstream
  dotnet/runtime still has the same `dn-umap.c` code, so it will meet this
  warning when a stable MSVC adopts 14.52. Nothing from the failed run reached
  nuget.org, so its release and tag were recreated at 2b5c98f. nuget.org
  listed all seven packages within four minutes. On Windows x64, `Ankus.Tool`
  0.2.1 from nuget.org created a solution that Visual Studio 2026's MSBuild
  built with no errors or warnings, and its 7 tests passed against
  PostgreSQL 18. Two intermittent failures on the dedicated runners were also
  fixed: `TraceScanRunsParallelIndexChildren` now vacuums until its shared
  table is all-visible (PR #39), and a Windows `ankus bench` install that
  failed once with only "Access to the path is denied" now names the file
  (PR #37); record it again if it repeats.
- `dotnet test` in a fresh clone on the MacBook, from an interactive shell
  whose `PATH` starts with Xcode's Apple clang 17 and with no staged runtime,
  failed 5,856 of 15,093 tests. The integration suite's setup ran
  `binding-call-sources` without a compiler, and the record, collection and
  header-inspection paths took the first `clang` on `PATH`; only
  `binding-sources` used the Clang 20 search added in 0.1.4. CI never saw it
  because its macOS job puts Homebrew's Clang first. The build tests also
  passed `clang` from `PATH` themselves, `SelectsTheHostClang` required it to
  be Clang 20, and the package consumer tests packed the runtime from a local
  stage even though the guide says a released runtime needs none. PR #41
  routes every unnamed compiler through `ClangSelection.ResolveAsync`, gives
  the build tests one helper using the same search, packs only a staged
  runtime and otherwise restores the published one, and drops the guide's
  `PATH` instruction. In a copy of that clone, under the same shell, the
  complete suite then failed only the two tests the change rewrites, and both
  pass there now.
- 0.2.2 is released (release run 39209782227 on aab38a0, after CI
  39207671181, CodeQL, Docs and the secret scan passed). All seven packages
  were pushed and listed on nuget.org. On the MacBook, with `/usr/bin/clang`
  first on `PATH`, `Ankus.Tool` 0.2.2 from nuget.org created a solution whose
  7 tests passed.
- 0.2.9 is released (release run 40176641713 on 3c566f17, after CI
  40175173282, CodeQL, Docs and the secret scan passed on that commit). All
  seven packages were pushed and listed on nuget.org, and the unchanged
  runtime packages were skipped as already published. On Linux x64, a solution
  from `Ankus.Tool` 0.2.9 on nuget.org compiled the three examples from #109
  as written and 11 other separator forms, rejected a reversed separator with
  ANKUS044, and passed its 13 tests against PostgreSQL 18, including one that
  counts no subtransaction across relation writer opens that receive a new or
  changed table's descriptor. The same solution from 0.2.8 reported ANKUS044
  for both of the issue's rejected examples and every other form, and its
  subtransaction test counted eight.
- 0.2.8 is released (release run 40156053583 on 17d67916, after CI
  40154571138, CodeQL, Docs and the secret scan passed on that commit). All
  seven packages were pushed and listed on nuget.org, and the unchanged
  runtime packages were skipped as already published. On Linux x64, a solution
  from `Ankus.Tool` 0.2.8 on nuget.org with a backend check that opens 1,000
  relation writers in one transaction, writes a row through each, disposes
  them and makes a raw `NativeMethods` call passed its 8 tests against
  PostgreSQL 18.
- 0.2.7 is released (release run 40083608945 on 67c1268a, after CI
  40082070145, CodeQL, Docs and the secret scan passed on that commit). All
  seven packages were pushed and listed on nuget.org, and the unchanged
  runtime packages were skipped as already published. On Linux x64, a
  solution from `Ankus.Tool` 0.2.7 on nuget.org with a backend check that
  runs a command built with `Spi.ParameterReference`, passes a `long` to
  `pg_usleep` and reads `FETCH_ALL()` passed its 8 tests against
  PostgreSQL 18.
- 0.2.6 is released (release run 39910251927 on 3c0b6c11, after CI
  39907909470, CodeQL, Docs and the secret scan passed on that commit). All
  seven packages were pushed and listed on nuget.org. On Linux x64, a solution
  from `Ankus.Tool` 0.2.6 on nuget.org with a backend check that calls
  `TupleDescAttr` and `fetch_att` passed its 8 tests against PostgreSQL 13,
  where both are macros.
- 0.2.5 is released (release run 39869923422 on ace334b1, after CI
  39867468493, CodeQL, Docs and the secret scan passed on that commit). All
  seven packages were pushed and listed on nuget.org. On Linux x64,
  `Ankus.Tool` 0.2.5 from nuget.org created a solution whose `.gitignore`
  matches `dotnet new gitignore` plus the `pg_regress` entries, and whose 7
  tests passed against PostgreSQL 18 with IDE0005 as an error.
- 0.2.4 is released (release run 39538883670 on ed764b4, after CI
  39536250666, CodeQL, Docs and the secret scan passed on that commit). All
  seven packages were pushed and listed on nuget.org. On Linux x64,
  `Ankus.Tool` 0.2.4 from nuget.org created a solution whose 7 tests passed
  against PostgreSQL 18.
- 0.2.3 is released (release run 39377193381 on d8544e8, after CI
  39374489223, CodeQL, Docs and the secret scan passed on that commit).
  All seven packages were pushed and listed on nuget.org, and the unchanged
  runtime packages were skipped as already published. On Linux x64, `Ankus.Tool` 0.2.3 from nuget.org created a
  solution whose 7 tests passed against PostgreSQL 18.
- PRs #57, #58 and #59 merged minutes apart on 2026-10-21, and each started a
  full main CI run. Both MacBook runners took a macOS suite at once, the disk
  filled, and all three runs failed with "No space left on device". Once the
  MacBook restarted with 42 GB free, main CI 39630723146 was rerun alone and
  passed on Linux x64, macOS ARM64 and Windows x64; its macOS suite used at
  most about 15 GB. That day's scheduled version runs predate #57 and #58.
  .NET versions 39605686971 failed the Windows net11.0 runtime build on MSVC
  14.52's C4047, and PostgreSQL versions 39596624997 failed
  `SkipsAnOlderClangForANewerOne` with "Text file busy". Dispatched again on
  main, the Windows net11.0 runtime built (39664495581) and PostgreSQL 19
  passed (39661277733). The .NET versions Windows test job then stopped before
  its build. Each Windows runner sets `PGROOT` to its PostgreSQL 17.11
  installation, and the workflow replaced it with an empty value when no secret
  names the major. #60 then fell back to `Program Files`, which holds 17.7, and
  the run failed the suite's 17.11 minimum (39675557483). The version workflows
  now pass a selected root as `ANKUS_PGROOT`, so the runner's `PGROOT` stays in
  effect for its default major.
- Scheduled PostgreSQL versions 39775310216 failed four Windows jobs on
  2026-10-22 when file opens returned Windows error 1450, "Insufficient system
  resources", in builds, `initdb` and action setup. Rerun under a two-second
  memory sampler, PG14 failed with the same error at 15:08:51 UTC as the commit
  charge reached its 70 GB limit (RAM plus a system-managed 23.5 GB page file),
  and PG13's build failed when available memory read zero. Three Windows suites
  were building at once while WSL on the same host held its full 32 GB, which
  also counts against that limit. PG19's publish failed while Windows shrank the
  page file back. The 2026-10-15 scheduled run passed with the same three
  suites. The host now has a fixed 64 GB page file, a 111 GB commit limit; on
  the next rerun commit peaked at 77.6 GB and no build failed for resources.
- That rerun followed a reboot, after which Visual Studio Preview installed an
  update it had deferred until close. The update replaced the shared Windows SDK
  headers from 15:44 to 16:03 UTC, and every PG14 and PG19 failure in that
  window was a missing SDK header such as `winsock2.h`.
- Two failures were real. On PostgreSQL 18 and later, `ankus regress` and
  `ankus bench` stage the extension beside the publication and name it in
  `extension_control_path` (#21). Ankus writes that setting as UTF-8, and
  PostgreSQL on Windows opens the path in the ANSI code page, so
  `NewSolutionScaffoldsRunnableRegressionSetup` with a `Café.Δelta` project
  could not create its extension on PG18 or PG19. A PostgreSQL 18.6 server on the
  host found a probe control file under an ASCII path and under an accented path
  written in code page 1252, and missed both paths written as UTF-8. Windows
  publications with non-ASCII paths now install into the installation as before
  PostgreSQL 18. `StartRecordsTheServerLogPath` failed on PG13 and PG14 because
  PostgreSQL before 15 writes a Windows service's output to the event log
  instead of stderr, and the runners are services; under a service the test now
  reads the warning from the combined log, where Ankus retains those events.
  On main at c2ebb5d (#62), CI 39822242853 passed on Linux x64, macOS ARM64
  and Windows x64, and PostgreSQL versions 39822093700 passed every Windows
  major from 13 through 19 except 17, which primary CI covers. PG13, PG14, PG18
  and PG19 each executed 5,543 integration tests with no failure, including
  both `Café.Δelta` cases and `StartRecordsTheServerLogPath`. Commit on the host
  peaked at 86.8 GB during those runs, above the old 70 GB limit.
- Linux ARM64 39776359356 failed `WorkerIdleWaitRecoversRepeatedCancellation`
  on net10.0. Its worker reported that the idle wait ended without
  cancellation. PostgreSQL's `SIGUSR1` handler sets the latch for every process
  signal, so a latch wait can end for something other than the event it waits
  for. The worker waited once and treated any wake as the cancellation; it and
  the transactional wait case now keep waiting until cancellation or
  termination ends the wait. The four worker cancellation tests pass 8/8 on
  Linux x64 against PostgreSQL 18.6, and Linux ARM64 39807696041 on the branch
  passed net10.0 and net11.0 against PostgreSQL 18, with 5,564 net10.0
  integration tests and no failure.
- The MSTest consumer templates imported
  `Microsoft.VisualStudio.TestTools.UnitTesting`, which MSTest already imports
  when implicit usings are on, as the templates set them. A consumer that
  enforces code style failed its test build on IDE0005. The templates drop the
  import, and the installed-template test now runs the generated tests with
  IDE0005 as an error. On Linux x64 against PostgreSQL 18.6 it failed all three
  template cases on the old templates, and they pass on the new ones along with
  every `ankus new` scaffold case. xUnit and NUnit add no implicit usings, so
  their templates keep theirs.
- The templates' `.gitignore` listed only build output and regression results.
  Microsoft's project templates ship none and leave it to `dotnet new
  gitignore`, while templates that create a whole solution, such as Avalonia's,
  ABP's and both Clean Architecture templates, ship the same `.gitignore` that
  `dotnet new gitignore` creates. The Ankus templates' `.gitignore` is now that
  one, as the .NET 10.0.400 SDK creates it, with the `pg_regress` output entries
  added at the end. The template test also checks that Git ignores Visual Studio
  and Rider files and project `.user` files.
- `NativeMethods` lacked `TupleDescAttr` before PostgreSQL 18 and `fetch_att`,
  `att_isnull` and `store_att_byval` before 16, because those majors define them
  as macros and the bindings follow pgrx's function inventory. An extension
  calling them needed its own per-version code. Across the seven supported
  majors 276 helpers change between function and macro, mostly `Datum`
  conversions and page, item pointer, buffer and heap tuple helpers before 16,
  heap tuple header helpers before 18 and varlena helpers before 19; a few, such
  as `bms_is_empty`, became macros later. 26 already had wrappers. The other 250
  are now wrapped wherever they are macros, using the prototype of the nearest
  major that declares the function, with three bodies written out where the
  macro's shape differs. The exact helper source compiles to objects with
  `-Wall -Wextra -Werror` against the headers of 13.23 through 19 RC1, with and
  without assertions. A consumer extension calls `TupleDescAttr`, `fetch_att`,
  `store_att_byval`, `att_isnull`, `Datum` conversions, varlena and item
  pointer helpers with the same code on every major, and passes against
  PostgreSQL 13, 15, 16, 18 and 19 on Linux x64; on 13 it does not build
  without the change. On main at d3851b3 (#68), CI 39894316353 passed on Linux
  x64, macOS ARM64 and Windows x64, PostgreSQL versions 39894333622 passed every
  other Linux major, and platform versions 39894336001 passed every other
  Windows and macOS major.
- pglogical's regression suite could not run under `ankus regress` (#71, #73,
  #75). Its backends connect to their own server with only `dbname=...`, which
  works in pg_regress's temporary instance because the postmaster inherits
  `PGHOST` and `PGPORT`. The development server now starts with `PGHOST`,
  `PGPORT` and `PGUSER` naming itself and its `postgres` role, without the
  caller's other libpq variables, on every platform. `hba_file` now accepts an
  absolute path or one relative to the current directory, so a suite keeps its
  own authentication rules, and a suite's `client_min_messages` replaces the
  `warning` default that regress shares with `cargo pgrx regress`. A regress test
  subscribes its database to itself by name, records a notice and is refused for
  a role its `pg_hba.conf` rejects; without the environment change the
  subscription fails with the issue's socket error.
- ANKUS044 rejected an SPI command whose positional parameters are numbered at
  run time, as pglogical's SPI apply path builds them, because the numbers are
  appended as runtime text (#78). `Spi.ParameterReference(n)` returns `$n`, and
  the analyzer accepts it as it accepts the quoting helpers, in interpolations,
  concatenation, builders and `Select` method groups. A runtime integer appended
  after `$` is still reported.
- `ankus init --pg19 download` failed on Windows while EDB published no
  PostgreSQL 19 archive (#79). When EDB has no archive for a release, Ankus now
  downloads the source and builds it with meson and Visual Studio's compiler,
  with debug information and, unless `--release`, assertions and allocated
  memory randomization, as Unix builds have; it names any missing meson, Perl,
  Bison or Flex first. On the Windows x64 runner PC, with EDB serving no 19rc1
  archive, `ankus init --pg19 download` built 19rc1 with MSVC 19.52, registered
  it, and the server started with `debug_assertions` on.
- An output plugin's startup callback runs outside a transaction when a WAL
  sender starts streaming, and native callbacks bound SPI only when entered
  inside one, so pglogical's startup could not read its catalog (#80).
  `PgTransaction.Run` runs work with SPI in the current transaction, or else
  starts one, commits it when the work returns and rolls it back if it fails,
  as pglogical's startup does with `StartTransactionCommand`. It uses the worker
  transaction's native path without the worker's activity and statistics
  reporting, and needs a backend connected to a database. A test output plugin
  reads the database name at startup; SQL decoding calls it inside a
  transaction, a WAL sender outside one, and a failing read rolls back and the
  next stream starts normally. The test also found that from PostgreSQL 18,
  which keeps `TopTransactionContext` allocated between transactions,
  `PgMemoryContext.Get(TopTransaction)` returned it outside a transaction where
  13–17 return null; it is now null whenever no transaction is in progress.
- `PgRelationWriter.Find` searched only the replica identity or primary key, so
  an insert that collided on another unique key failed with `23505` where
  pglogical resolves the conflict (#76). `FindConflict` searches the replica
  identity index, then each other valid unique btree index without expressions
  or a predicate, as `pglogical_tuple_find_conflict` does, and `PgFoundRow`
  now names the matching index. It ports pglogical's `find_index_tuple`, a
  dirty-snapshot index scan that waits for a concurrent writer of the row and
  retries when the row changes before it is locked. PostgreSQL's own
  `RelationFindReplTupleByIndex` cannot do this from PostgreSQL 16 on, because
  for any index other than the replica identity or primary key it also requires
  every column to match. A NULL matches only in a `NULLS NOT DISTINCT` index.
- `PgRelationWriter` inserted one row per call, so pglogical's batched apply had
  no equivalent (#77). `InsertBuffered` checks each row as
  `ExecSimpleRelationInsert` does and holds up to 1,000, then `Flush`, any other
  writer operation, a full batch or disposal writes them with
  `table_multi_insert`, adds their index entries and queues their `AFTER ROW`
  triggers, as COPY's multi-insert buffer does. As with COPY and pglogical, a
  table with `BEFORE` or `INSTEAD OF` row insert triggers or a volatile default
  other than `nextval` gets each row at once. Tests check the 1,000-row
  batches, ordering with a later update, `AFTER` triggers, both fallbacks and a
  unique violation raised when the batch is written.
- `PgRelationWriter.Find` failed on a row with an enum type the extension does
  not declare, because row transport rejected such enums before converting
  them (#83). A row cell now keeps an exact copy of an undeclared enum, or an
  array of one, as it does for other types without a managed conversion, so
  such rows can be found, written back unchanged and deleted, and a C# row
  trigger receives them. An SPI query that returns one as a column still fails
  with `0A000`.
- ANKUS044 still rejected the SPI guide's separator, `i > 0 ? ", " : ""`, in a
  loop that appends quoted identifiers or `name = $n` items, such as a column
  list or an `UPDATE ... SET` list (#109). The fix for #93 accepted the
  guide's own loop only because one parameter reference may follow another,
  while the analysis still let the empty separator run on any pass. The flow
  analysis now keeps the paths on which a counter still has the constant it
  was last set to apart from the paths after it was incremented, and a test
  such as `i > 0`, `i != 0`, `i == 0` or `i >= 1` keeps only the side it can
  take; a `bool` flag set only to constants works the same way, and splits
  nest for inner loops. Paths that reach a split without its counter join both
  sides, and a condition such as `i++ < n` advances the counter before either
  branch. Tests accept the issue's three examples as written and 16 more
  forms, including `for` loops from 0 and 1, `while`, `+= 1`, `long` counters,
  string concatenation, `if` and `?:` separators, three flag forms and nested
  `OR`/`AND` lists, and still reject the empty branch on later passes, a first
  pass that would turn a literal into an escape string, a test against the
  wrong value, a counter written by a runtime value, a decrement, a callback
  or a reference, a counter or flag that never changes, an unquoted item, and
  an unquoted item reached after the counter advanced in a `while (i++ < n)`
  condition. The main runs listed for #111 cover this change.
- A raw `NativeMethods` call still cost about 46 ns and a `NativeGlobals` read
  about 19 ns where the C operation takes a few (#115), so a logical decoding
  output plugin that makes several calls per row spent most of its time getting
  into and out of PostgreSQL. Each call read four thread-static fields of
  different classes, compared its binding's 64-byte identity, and in native code
  built a request envelope and walked the memory-context tree before `PG_TRY`.
  The callback's capability, the innermost failed scope and the held lock leases
  now share one per-thread object, each generated binding caches the provider
  that accepted it, and the native entry calls the body directly under `PG_TRY`,
  finding the recovery context only after an error. A call the cache does not
  vouch for runs the previous checks in their order. An inline helper or macro
  wrapper whose body the selected headers show cannot raise an error runs
  without the guard or a GC transition, as a C caller would run it: everything
  it calls must be another such helper, a memory or string function, a builtin
  or `ExceptionalCondition`, and it must not call through a function pointer,
  use inline assembly or divide integers by anything but a constant other than
  zero and minus one. A libclang worker reads the bodies from a serialized AST,
  as a JSON dump of them passes 400 MB with the Windows SDK headers, and the
  bodies are read as system headers, as PostgreSQL's own are; if Clang cannot
  compile them every call keeps its guard. PostgreSQL 18 on Linux has 500 such
  helpers, including `TupleDescAttr`, `GETSTRUCT`, the datum conversions and the
  list and varlena accessors, and PostgreSQL 13 on Windows has 382, while
  `pq_sendint32` keeps the guard because it can enlarge its buffer and
  `XLogFromFileName` because it calls `sscanf`. On Linux x64 with PostgreSQL 18,
  `GetCurrentTransactionNestLevel` went from 46 to 25 ns, `pq_sendint32` from
  46 to 27 ns, `TupleDescAttr` from 46 to 10 ns and a `MyProcPid` read from 19
  to 8 ns, each including about 2.5 ns of benchmark loop. Build tests check
  which helpers qualify, covering literal, `sizeof` and variable divisors,
  floating division, builtins, assertions, outside calls, recursion, function
  pointers and assembly, and that a qualifying call skips a guard that refuses
  the others. The binding integration test checks the generated entries for real
  headers and that `pq_sendint32` still turns PostgreSQL's buffer-limit ERROR
  into a `PgException` that a subtransaction recovers. On Windows with
  PostgreSQL 13, a solution built from the packed packages passed its 8 tests,
  including direct `TupleDescAttr` and `MAXALIGN` calls and that ERROR.
- `PgRelationWriter.Open` started a subtransaction whenever it loaded the
  table's row descriptor, which a backend writing many tables did on almost
  every call, and each subtransaction's end walks state that grows with the
  backend's relations (#111). In pglogical's `huge_tx_100k_tables` test this
  held an apply worker to about 1,000 rows a second. The writer's own open now
  returns the descriptor, without a subtransaction, whenever the type cache's
  identifier differs from the copy the backend already has; as with every writer
  operation, an error there fails the call. A test counts PostgreSQL's DEBUG5
  subtransaction reports across opens that reuse a descriptor, receive a new
  table's and receive a changed table's, and across inserts, updates, deletes
  and lookups by key and with replicated rows, and finds none; against the
  previous code it finds eight, two lines for each of the four opens that
  loaded a descriptor. On main at 0ec639ca (#112), CI 40166499847 passed on
  Linux x64, macOS ARM64 and Windows x64, CodeQL left no open alert,
  PostgreSQL versions 40167866758 passed every other Linux major, and
  platform versions 40167868519 passed every other Windows and macOS major.
- On Windows, `NativeGlobals` failed to link a variable that the headers
  declare without `PGDLLIMPORT` (#116), as PostgreSQL 13 and 14 declare
  `wal_receiver_status_interval` and `CheckPointTimeout`, because an import
  library gives a variable exported as data only its `__imp_` slot. The build
  now reads the installation's `postgres.lib` and reaches an exported variable
  through that slot, as a `PGDLLIMPORT` declaration does; PostgreSQL 13's
  `postgres.exe` exports 241 variables that its headers leave unmarked. A
  variable that `postgres.exe` does not export, such as
  `wal_receiver_status_interval` before PostgreSQL 15 or `MyClientSocket` in
  PostgreSQL 17, keeps its members, marked `[UnsupportedOSPlatform("windows")]`
  with the reason, so CA1416 reports an unguarded use, and they throw
  `PlatformNotSupportedException`. Build tests read an import library's symbol
  table and reject malformed ones, classify marked, unmarked, missing,
  thread-local and defined variables for 64-bit and 32-bit Windows, and run
  generated accesses through stand-in import slots on Linux and macOS with
  nothing defining the bare names, which fails when a body uses the bare name.
  The binding integration test reads and writes `CheckPointTimeout` against
  `pg_settings` and, on Windows with PostgreSQL 13 and 14, requires CA1416 for an
  unguarded read of `wal_receiver_status_interval`. On Windows with PostgreSQL
  13, a solution built from the packed packages read, wrote and took the address
  of `CheckPointTimeout`, got the warning and the exception for
  `wal_receiver_status_interval`, and passed its 9 tests, where the same source
  on 0.2.9 failed to link `CheckPointTimeout`.
- Opening a `PgRelationWriter` loaded the table's row type into the type cache
  to number its descriptor, which left an entry per table for the backend's
  life, and before PostgreSQL 18 every relcache invalidation walks the whole
  type cache (#119). An apply that had written to many tables slowed on every
  later DDL statement: replaying the drops of 20,000 written tables took 76 s on
  PostgreSQL 17 and 9.4 s on 18. The writer now sends the relation's own
  descriptor and recognizes a changed table by a stamp per table that a
  relcache invalidation callback removes, as the apply worker's relation map
  forgets an invalidated table; a full cache reset clears every stamp. A test
  opens writers for 500 tables and requires the type cache's memory to stay the
  same, where the previous code added 242,640 bytes, about 485 per table.
  Another rolls a new column back to a savepoint and requires the next writer to
  see the original columns.
- Disposing a `PgRelationWriter` took time proportional to the writers still
  open (#104). Open writers were a singly linked list that every operation
  walked to find its writer, that close walked twice more, and that the context
  reset callback walked again after close had unlinked the writer. They are now
  in a hash table by id, built with PostgreSQL's `simplehash.h`, and the
  callback does nothing once close has removed the writer. A first version used
  dynahash, and on macOS every writer crashed its backend, because the static
  linker bound `hash_create` and `hash_search` to libSystem's functions of the
  same names. On Linux x64 with PostgreSQL 18, a statement that opens 20,000
  writers on 20,000 tables, inserts a row through each and disposes them took
  14.8 s oldest first and 6.5 s newest first, 10.7 s and 2.7 s of it disposing;
  it now takes 1.0 s and 0.8 s, 28 ms and 30 ms of it disposing. Tests open 500
  writers for one table, write through each and dispose them in either order,
  and open a writer in a subtransaction that rolls back, after which the
  transaction still commits. On main at 915d1003 (#106), CI 40148525554 passed
  on Linux x64, macOS ARM64 and Windows x64, CodeQL left no open alert, and
  PostgreSQL versions 40148541481 passed every other Linux major; platform
  versions 40140841680 passed every other Windows and macOS major at ce0d0b93
  (#105), before #106 changed only a test.
- `ankus init --release` built PostgreSQL with assertions and memory
  randomization in the development build's directory (#99), because
  `PostgresProvisioner.InstallAsync` copied the caller's options without
  `Release`. The copy now keeps it. A test asks for a release build where its
  directory already exists and gets the refusal for that directory, where the
  previous code went on to download a development build.
- A `PgProcess.OnExit` callback could not log (#101), so a worker could not
  report its exit as pglogical's `pglogical_worker_detach` does with
  `elog(LOG)`. The exit bridge now passes the guarded logger that initializers
  and native callbacks use, which needs no transaction, and every extension
  that has the exit bridge also has that logger. The shared-array test's exit
  callback logs each slot release, and the test finds both the normal exit's
  and the terminated backend's lines in the server log. PostgreSQL versions run
  40140839885 at ce0d0b93 lost the PostgreSQL 16 case once: the dispatcher
  reports the first failure after every callback has run, so the failing
  callback's warning followed the release the test waited for, and the test read
  the log before the warning arrived, 3 ms after the release's own line. The
  test now waits for the warning.
- On Windows with PostgreSQL 13–16, an ordinary connection to a host name that
  resolves to an address the server does not listen on failed instead of trying
  the next address (#95). Its connect loop waited for the socket to become
  writable, which Windows reports for a refused connect, so libpq failed on
  that socket. As libpqwalreceiver does, the loop now waits for the connect to
  complete on Windows while libpq reports `CONNECTION_STARTED`, and advances
  libpq only when the socket is ready. A test connects through `localhost` to a
  server that listens only on 127.0.0.1.
- ANKUS044 rejected the SPI guide's own `Spi.ParameterReference` example
  (#93). In a loop, the analyzer summarizes a command with quoted fragments
  and accepts a join only at a token boundary, and with an empty first
  separator one reference could directly precede the next. A positional
  parameter ends at its last digit, so a summary now records when it ends
  with one, and a following reference is accepted. Tests compile the guide's
  example and still reject separators such as `0`, which would extend the
  number, or `a`, which would join an identifier.
- With the backend transaction operation, the generated background-worker
  dispatcher reached 101 lines, and CodeQL's C analysis on `main` flagged it once
  for each of the 48 extensions it builds as poorly documented. The latch wait
  and database connection now have their own functions. On main at 06bff4fa
  (#89), CI 40072947626 passed on Linux x64, macOS ARM64 and Windows x64,
  CodeQL left no open alert, PostgreSQL versions 40074461611 passed every other
  Linux major, and platform versions 40074463443 passed every other Windows and
  macOS major.
- A function's `PgDatum` argument was copied through the same guarded operation
  as `PgDatum.CopyTo`, which runs in an internal recovery subtransaction, so
  every call started one (#92). Each subtransaction's end walks state that grows
  with the relations a transaction has created, and pglogical's
  `huge_tx_100k_tables` test, which calls such a function 100,000 times after
  creating 100,000 tables, had run about 83,000 calls after 3 hours. Arguments
  are now copied by their own operation on the guard's lightweight path, as
  borrowed array cells are read, so a failure fails the call as an ERROR from
  `PG_GETARG_DATUM` does. The copy is still detoasted into the call's context.
  A test counts PostgreSQL's DEBUG5 subtransaction reports: 200 `name` and
  `text` argument reads produced 400 lines before the change and none after,
  and a compressed table value now reads with none. `ToPostgresString()` and
  `PgDatum.Create` keep their subtransactions, since a type output function,
  domain check or encoding conversion can fail in a way callers catch and
  recover from.
- A `NativeMethods` call cost about 150 ns and a `NativeGlobals` access about
  as much, where the C call or variable access takes a few (#98). Each made two
  native round trips, one to check the binding and one to run the body, and each
  round trip zeroed the 2.6 KB managed error buffer; the SPI and datum boundary
  zeroed it too. A binding the provider accepted is now accepted again in managed
  code while the guard would admit the call: the native callback publishes its
  failed-frame count and ErrorContext cleanup flag, and any other state takes the
  native check, which reports exactly as before. A raw call runs through a native
  entry that writes the error only when it fails, the body lookup no longer makes
  a GC transition, and boundaries clear only the error fields native code reads.
  Plain globals are read and written in place through an address cached after
  the first checked access, since copying a variable cannot raise an ERROR;
  volatile, atomic and thread-local globals keep their guarded C bodies. Heap
  frames and function-pointer calls still validate before allocating or checking
  for null. On Linux x64 with PostgreSQL 18, best of 15 runs of a million
  iterations on a loaded host, `GetCurrentTransactionNestLevel` went from 145 to
  49 ns, a `CurrentMemoryContext` read from 145 to 18 ns, a read and write from
  288 to 35 ns, `palloc` and `pfree` from 299 to 106 ns, and a binding check
  alone from 64 to 18 ns. Integration tests make generated calls and global
  accesses from a thread with no callback, after a caught ERROR with and without
  a subtransaction, and across in-place writes that `pg_settings` reports; they
  pass against the previous runtime as well.
- An exception from a `[PgNativeCallback]` hook or C# trigger nested in a backend
  call reported each enclosing `PgErrorContext` twice, once from the text
  recorded when it was thrown and once from the context callback the backend
  call had put on PostgreSQL's stack (#84). The recorded text now keeps only the
  contexts of the callback that converts the exception, since PostgreSQL reports
  the enclosing ones itself. A test fires a C# trigger that rejects a row from a
  function's SPI insert and finds the function's context once.
- A C `long` parameter was `long` in `NativeMethods` on Linux and macOS and
  `int` on Windows, where C `long` has 32 bits, so source that passed a `long`
  built only on Unix (#87). Where C `long` has 32 bits, each function with such
  parameters now also takes `long` (`ulong` for `unsigned long`) and throws
  `ArgumentOutOfRangeException` for a value outside 32 bits before calling
  PostgreSQL. `NativeMethods.FETCH_ALL()` returns `FETCH_ALL` at the platform's
  width. A generated binding test checks both widths, and a test extension
  passes a `long` to `pg_usleep` and reads `FETCH_ALL()` on every platform.
- `PgReplicationConnection` always opened a logical replication connection, so
  a role without `REPLICATION` could not run pglogical's ordinary catalog
  queries and every query took a WAL sender (#72). `Connect(..., replication:
  false)` opens an ordinary connection. PostgreSQL 17 and later open it through
  `libpqwalreceiver`; before 17 that module connects only for replication, so
  Ankus connects with the libpq it loaded, polling the socket and latch as
  `libpqwalreceiver` does, and hands the connection to `libpqwalreceiver`'s
  query, copy and disconnect functions in the `WalReceiverConn` layout those
  versions share. The connection then sets the secure search path, the database
  encoding and the date, interval and float settings a logical connection asks
  for. A test connects as a role without `REPLICATION`, sees itself as a client
  backend with those settings, copies a table, and is refused a replication
  connection.
- macOS killed the Generators test host on the MacBook runner (main CI
  39517461878) as the largest process, at 26.7 GB compressed. The generator wrote
  the native source, SQL, graph and exports as single `AssemblyMetadata`
  literals of up to a megabyte, and Roslyn keeps every small parsed node,
  literals included, in a process-wide cache, so each compilation in the test
  host, and each generator run in a compiler server or IDE, left one alive. On
  debian-dev the suite's live large-object heap reached 5.3 GB and its peak
  working set 14.4 GB. The generator now writes each value in parts of at most
  1,024 characters (`Ankus.NativeSource`, then `Ankus.NativeSource#1` and so
  on), and the build joins them; the large-object heap then peaks at 1.7 GB,
  ends at 348 MB, and the working set peaks at 9.6 GB.
- `StartRecordsTheServerLogPath` failed now and then on a dedicated Windows
  runner (PR #41's first CI run, 39197108639) when it renamed the Ankus home
  right after `StopAsync`. On the Windows runner host, a loop that starts a
  development cluster, stops it and renames its home passed about 1,500 cycles
  on an otherwise idle machine and failed 9 of 150 beside two full local
  suites. Three of those failures lasted under a second, while the server's
  `cmd /c` wrapper exited; six lasted 45 seconds to 6 minutes. During the long
  ones no process held a handle anywhere in the home, nothing was delete
  pending, every file accepted a delete disposition and deletes succeeded;
  only renames of the directories above the cluster's files were refused, and
  the refusing directory moved through `global`, `base/1`, `base/4` and
  `base/5`. Renaming a subdirectory made it refuse again. Copies of the same
  files written by a .NET process, with or without a staged rename and from a
  short-lived writer, and `initdb` without a server never failed under the
  same load, so the hold follows a server run. Microsoft Defender's
  minifilter is the only antivirus filter on the host and this matches its
  background scanning; proving it needs an administrator to exclude a test
  directory. The test now copies the home to its new path, which exercises
  the same Ankus behavior, and that flow passed every cycle under the same
  load.
- Scheduled run 38729094424 lost one Windows/PostgreSQL 18.6 case before its
  test body ran: the server reset the connection during authentication. The
  new backend had failed with `FATAL: incorrect checksum in control file` in
  the same millisecond the checkpointer finished writing `pg_control`. Its
  rerun passed. On 2026-10-24, CI 40062484244 for #86 lost
  `NamespaceLookupTests.TypeSearchPathAndExactSignaturesArePreserved` on
  Windows/PostgreSQL 17.11 the same way: a new backend logged the same FATAL
  during an immediate checkpoint that started 57 ms earlier and completed 6 ms
  later. Its rerun passed.
- Platform versions run 40102478124 on main at 4ae4a132 lost one
  Windows/PostgreSQL 13 case,
  `NumericTests.TemporalExtractionPreservesNumericPrecision` for the largest
  interval (2147483647 months, 2147483647 days and the largest microsecond
  count), with `22008: interval out of range` from a statement that reads the
  interval through `extract`, the test's extraction function and
  `interval_send`. The same case passed on Windows/PostgreSQL 13 at 06bff4fa
  that morning, and the rerun of the failed job passed. The cause is unknown.
- The same matrix found two test races. On Windows, a test that ends its
  backend with FATAL can see a connection reset instead of the report, so all
  three such GUC tests now accept a reset there and read the report from the
  server log. On macOS, a worker restart check waited for a nonzero PID, but a
  worker PostgreSQL has not recorded yet reports none at all, which is not zero.
- A `BEFORE INSERT` row trigger now costs the same as PL/pgSQL. `perf` on
  Linux x64, comparing the trimming sample with a PL/pgSQL trigger that does
  the same, found five Ankus-only costs:
  - interface calls for every descriptor attribute access;
  - a native error buffer per call large enough to need `malloc`;
  - two native allocations and a managed release callback for every returned
    row;
  - eleven write-barriered references copied into each context;
  - an array for each modified row's replaced cells.
  Per-row paths now index a span. A call site that no other call is running
  lends its error buffer and a row buffer, which the returned row is written
  into when it fits. Contexts share one definition with their site, and
  replaced cells are a bitmask. In medians of five alternated rounds of
  1,000,000 rows in a UTF-8 PostgreSQL 18.6 database, the trimming trigger's
  overhead fell from 0.94 µs to 0.72 µs per row, equal to PL/pgSQL's 0.72 µs,
  and a trigger that only checks the name fell from 0.67 µs to 0.49 µs. On a
  second, noisier Linux x64 host it fell from 0.69 µs to 0.55 µs against
  0.52 µs for PL/pgSQL, and the checking trigger from 0.51 µs to 0.36 µs.
  Unit tests cover rows that fit the lent buffer, rows that do not, cells that
  own memory, and malformed buffers. Runtime and generator unit tests pass
  (2,493 and 5,033), and the 177 trigger, detached-cell, cleanup-owner and
  relation-writer integration tests pass on the PostgreSQL 19 build that
  overwrites freed contexts. The complete suite passes on PostgreSQL 18.6,
  Linux x64: 14,976 passed and 52 platform skips.
- GitHub flagged the C# scan's quality: read from source alone, it resolved only
  81% of calls. Nearly all the misses were MSTest calls in the test projects,
  whose global `using` MSTest's package adds through MSBuild, which a scan of the
  sources cannot see. Each test project now states it in a source file, and the
  scan resolves 97% of calls with no build, so it stays as fast as before. A
  traced build resolved 99.98% but took over 40 minutes on a hosted runner. Its
  run found 36 results in hand-written code, now fixed, except a build tool's
  top-level catch, the one `mallinfo2` call and five runtime tests that dispose
  at the step they check, which carry comments. The generator no longer emits
  identity casts, and one shared walk over a type's containers replaces three
  copies and the loops CodeQL misread as leaving a type null. The C analysis on
  `main` closed 8,146 alerts, the 89 marked C# results are dismissed with their
  reasons, and code scanning has no open alert.
- Two tests depended on their host. The first exception thrown under a
  `PgErrorContext` could go unrecorded when another thread pushed the first
  context at the same moment, because the push marked the exception handler as
  installed before installing it; macOS ARM64 hit this once in the runtime unit
  tests. The handler is now installed under a lock before any push returns. The
  relative `pg_config` selection tests could not form a relative path on
  GitHub-hosted Windows, whose work directory is on another drive than
  PostgreSQL; such a consumer now reaches the installation through a directory
  junction in the shared work root. The eight selection cases still pass on
  Linux x64 with PostgreSQL 18. A design-time build test's four fake
  dependencies, which MSBuild builds in parallel, appended to one log, and
  Windows refused one write; each now keeps its own log.
- Assertion-enabled PostgreSQL builds on macOS ARM64 stopped in
  `PgRelationWriter`. A writer began one `AFTER`-trigger query when it opened and
  ended it when disposed, so a writer left open at commit left the query on the
  stack; it also reopened the table's indexes for each change, which PostgreSQL
  19 asserts against. Each change is now its own trigger query, as in the
  built-in apply worker, so its `AFTER` triggers fire when it completes, and the
  writer opens the indexes once. A new backend test counts an `AFTER` trigger's
  rows after each change and after disposal. The subscriber sample now drops a
  slot with `WAIT`, because PostgreSQL 13 still reported the stopped worker's
  sender as active. PostgreSQL 19's assertion-enabled `pg_dump` cannot order
  casts of same-named types in different schemas, since it names casts by type
  names alone; the child-process test dumps a database of its own. Windows can
  reset a connection before the client reads the FATAL that closed it, so that
  test reads the server log. The writer, subscriber, replication, child-process,
  transaction-callback and C-string backend tests pass on Linux x64 with
  PostgreSQL 13, 18 and 19 beta 4 (80 each).
- Managed custom scan providers: a class marked `[PgCustomScan]` implements
  `IPgCustomScan<TState>` with static `Plan`, `Begin` and `Read` methods, and
  optionally `IPgRescannableCustomScan<TState>` and
  `IPgExplainedCustomScan<TState>`. The generator emits the method tables,
  registers them during library load and chains `set_rel_pathlist_hook`. Rows
  go into the scan's own slot, so PostgreSQL applies conditions and projection.
  Path bytes are stored in the plan as a `bytea` constant. State is disposed at
  scan end and, through a query-context reset callback, after errors. Paths are
  not offered for system columns, row locks, modified tables, inheritance
  parents, `TABLESAMPLE` or an extension whose load failed; the last case was
  found by the existing registry-collision test, where the new hook made every
  later query fail. `ANKUS535`–`ANKUS539` report contract, name, duplicate,
  container and async mistakes. The `NumberScan` sample's backend tests cover
  conditions, NULL, EXPLAIN and EXPLAIN ANALYZE, nested-loop and subquery
  rescans, LIMIT and closed cursors, provider exceptions and NOT NULL
  violations with same-session recovery, dropped columns, the cases that keep
  ordinary scans, and a name another module registered first, which rejects
  every load attempt while ordinary queries keep working. A retried load skips
  names it already registered, so it reports the real conflict. The 74 custom
  scan backend tests pass on Linux x64 with PostgreSQL 13, 18 and 19 beta 4.
  The full PostgreSQL 18
  suite ran 14,846 tests with 52 platform skips; its one failure was the
  compiler-services type inventory, which now lists the two new transport types.
- A managed custom scan row cost 650 ns for one `bigint` column and 840 ns with
  a `text` column as well, on a PostgreSQL 18.6 release build. The sample read
  a setting on every row; reading it once per scan brought these to 302 ns and
  492 ns. Each cell also searched every generated type mapping, which built-in
  types can skip, and each empty tuple was validated and copied; with both
  fixed the rows cost 182 ns and 330 ns. See the
  [measurements](docs/contributing/evidence/guard-tiers.md#managed-custom-scans).
  The PostgreSQL 18 suite passes with these changes.
- String setting properties now also locate their variable. Each read compares
  the bytes it points to with those it last converted and converts from the
  server encoding only when they differ; a null pointer, before PostgreSQL
  registers the setting, still reads the declared default by name. The last
  conversion is held weakly: the existing lifetime tests require a read value,
  which can be 64 KB, to be collected once its caller drops it, and the first
  version of the cache failed them. A backend test reads a string setting
  before it is set and across three assignments, including one back to an
  earlier value. The configuration and custom scan backend tests pass on
  PostgreSQL 13, 18 and 19 beta 4, and the full PostgreSQL 18 suite passes
  (14,862 tests, 52 platform skips).
- A `BEFORE` row trigger that returns its NEW row unchanged no longer converts
  it back: `PgHeapTuple` records replaced cells, and when the returned row is the
  context's own NEW row with none replaced and no mutable cells, PostgreSQL
  keeps the row it passed in. A validating trigger's overhead fell from 1.21 µs
  to 0.76 µs per row. Unit tests cover clones, replaced cells and byte arrays,
  and every existing backend test whose trigger returns NEW now takes this
  path; the trigger backend tests pass on PostgreSQL 13 and 18, and the full
  PostgreSQL 18 suite passes (14,861 tests, 52 platform skips). A new
  [boundary costs](docs/src/content/docs/reference/performance.md) reference
  page lists the measured costs and how to keep hot paths cheap.
- Composite values cross the boundary without per-row catalog lookups. Managed
  code reuses a recent descriptor of the same type when every transported
  column matches it byte for byte, and the native side keeps the resolved facts
  of up to 16 row types, used only while every column still has the type,
  modifier, collation, flags and name it was built from. A call that reads and
  returns a composite went from 1.80 µs to 1.35 µs. A new backend test renames,
  adds, retypes and drops attributes of a composite type between calls in one
  session and checks both directions. The composite, tuple, trigger, SPI,
  domain, set and array backend tests pass on PostgreSQL 13 and 18, and the full
  PostgreSQL 18 suite passes (14,860 tests, 52 platform skips).
- Complete Linux x64 suites on the current source (**07923d5**, runtime
  ankus.5) pass on every other supported major: PostgreSQL 13.23, 14.24,
  15.19, 16.15, 17.11 and 19 beta 4 each ran 14,861 tests with 52 platform
  skips and no failures, in 26 to 27 minutes each, alongside the PostgreSQL
  18.6 suite on **f3f3f6f**.
- Today's native bridge changes (trigger call sites, type modifiers, domains,
  row writes, setting reads and managed custom scans) were checked on every
  supported major: the 540 trigger, composite, tuple, domain, setting and
  custom scan backend tests pass on Linux x64 with PostgreSQL 14, 15, 16, 17
  and 19 beta 4, as they did earlier on 13 and 18.
- `PgReplicationConnection` connects from a backend to a PostgreSQL server
  through `libpqwalreceiver`, the first piece of the
  [logical replication toolkit](#beyond-pgrx-logical-replication-toolkit). A new
  guarded memory operation loads the module once, calls the `walrcv_*` table
  through each version's macros (the connect signature changes in 16 and 17),
  keeps connections in a backend-local registry with a scratch context for the
  last result, and waits with `WaitLatchOrSocket` so latch wakeups, query
  cancellation and statement timeouts end a wait. `Query` returns rows as text
  in UTF-8, `Execute` runs commands, `StartCopy`, `Receive`, `Send` and
  `EndStreaming` cover `COPY` out and `START_REPLICATION`, and a failure on the
  server throws `PgRemoteException` with its SQLSTATE (from PostgreSQL 14)
  without touching the local transaction. Closing counts as cleanup, so a
  `using` block still closes a connection after an error and the server
  releases its slot. Backend tests run queries with NULL and non-ASCII text,
  `IDENTIFY_SYSTEM` against `pg_control_system()`, a remote division by zero
  followed by another query, a three-row `COPY`, use after dispose, an
  unreachable server, a logical stream of three inserts whose status update
  advances the slot's confirmed position, and a wait that a statement timeout
  ends, after which the slot becomes inactive. All seven pass on PostgreSQL 13,
  14, 15, 16, 17, 18 and 19 beta 4, and the full PostgreSQL 18 suite passes with
  them (14,901 tests, 52 platform skips).
- `PgReplicationConnection.CopyInto` reads a running `COPY ... TO STDOUT` into
  a local table, as table synchronization does: a data source callback hands
  `BeginCopyFrom` the stream's bytes, waiting on the socket and latch, and
  PostgreSQL's own `CopyFrom` checks constraints, fills indexes, routes
  partitions and fires triggers. It makes tablesync's checks (`INSERT`
  privilege, and refusal of tables whose row-level security applies to the
  user). Backend tests copy three rows with NULL and non-ASCII text into a table
  with reordered columns and a default, reject a row through a check
  constraint, and check the privilege and row-level security refusals as an
  unprivileged role.
- `PgRelationWriter` writes rows as the apply worker does, the second toolkit
  piece. It opens the table in `ROW EXCLUSIVE` mode and builds executor state
  through each version's range-table and result-relation interfaces, then
  inserts, updates and deletes through `ExecSimpleRelation*`, finding rows with
  `RelationFindReplTupleByIndex` on the replica identity index or primary key,
  or `RelationFindReplTupleSeq`, under an exclusive tuple lock. `Find` returns
  the row with its `xmin`, commit origin and commit timestamp. Each operation
  makes `TargetPrivilegesCheck`'s checks, including the row-level security
  refusal, because these helpers apply no policies. `AFTER` triggers run when
  the writer closes, a pre-commit callback fails a commit that leaves one open,
  and each change's memory, including the executor's per-tuple context, is
  reset before the next, as the apply worker resets its message context.
  `PgReplicatedRow` carries values in replication's text or binary form; the
  writer converts them with each type's input or receive function, checks text
  encoding, gives unsent columns their defaults on insert and keeps them on
  update, as `slot_store_data`, `slot_fill_defaults` and `slot_modify_data` do.
  Backend tests cover keyed and keyless updates, unique and check violations
  with session recovery, triggers under `session_replication_role`, an open
  writer at commit, found rows' transactions, privileges and row-level security
  as an unprivileged role, 50,000 inserts in one writer with less than 1 MB of
  memory growth (PostgreSQL 14 and later, through
  `pg_backend_memory_contexts`), and replicated rows with defaults, NULLs,
  binary values, unsent update columns and invalid text, binary and encoding.
- `PgReplicationOrigin` and `PgLsn` manage replication origins. Each operation
  makes the checks of its `pg_replication_origin_*` SQL function except
  privileges, sets and clears the session origin as those functions do, and
  follows PostgreSQL 19's move of the session's transaction fields into
  `replorigin_xact_state`. Backend tests on a cluster with
  `track_commit_timestamp` apply a row on an origin's behalf and check
  `pg_replication_origin_progress`, the commit timestamp and the found row's
  origin, a second session's refusal, drop while in use, reset, advance forward
  and backward, reserved names and missing origins.
- The [subscriber sample](samples/Ankus.Examples.Subscriber/) proves the toolkit
  together. Its background worker creates a `pgoutput` slot, copies the
  published tables in the slot's snapshot with the starting position recorded
  through the origin in the same transaction, then applies each remote
  transaction in one `RunTransaction` call with `PgReplicatedRow`, and reports
  durable progress. Its backend test, on a cluster with a provider and a
  subscriber database, checks the copy, streamed inserts, an update that leaves
  a 100,000-byte TOAST value unchanged, a delete, a truncate, local defaults,
  commit origin and timestamps, origin progress, recovery after an apply error
  rolls back and the worker restarts, and unsubscribing. Building it found
  three toolkit gaps, now fixed: the quoting helpers worked only inside a SQL
  callback (they now also work in a worker between transactions, through the
  memory bridge with a per-call context); replication commands converted their
  text in the worker's process-lifetime context; and a replication query that
  returns rows outside a transaction crashed in `libpqwalreceiver`'s catalog
  lookups (it now raises an error, and the guide says rows need a
  transaction). The 60 toolkit, sample and SPI quoting backend tests pass on
  PostgreSQL 14, 15, 16, 17 and 18. On 13 the sample first failed because
  PostgreSQL 13 reports no SQLSTATE for remote errors, and on 19 an origin test
  compared PostgreSQL 19's newly zero-padded position text; with the sample
  looking slots up and the test comparing positions as values, the sample and
  origin tests pass on 13, 18 and 19. `PgLsn` keeps the unpadded form Npgsql
  and rust-postgres use and parses both. The full PostgreSQL 18 suite passes
  (14,933 tests, 52 platform skips).
- C# Dev Kit lists a design-time build's high-importance messages as problems.
  Opening this repository showed 217: four binding progress lines from every
  extension project and MSBuild's "project -> output" line for each SDK
  project the editor's background build compiled. The SDK now logs the build
  tool's output at low importance in design-time builds, rebuilds its own
  projects only when an input changed or an output is missing (a stamp file),
  and when it does rebuild them, copies their assemblies through an editor-only
  `CustomAfterMicrosoftCommonTargets` file that skips MSBuild's high-importance
  copy notice. Command-line builds are unchanged. Build tests check both
  importance settings, that every SDK command uses them, that unchanged loads
  build nothing while edited sources and missing outputs rebuild, and that a
  load which rebuilds every dependency prints nothing at minimal verbosity.
- `PgLwLock<THeader, TElement>` sizes shared memory at startup, the fifth
  toolkit piece: pglogical keeps one worker slot per `max_worker_processes`,
  while pgrx's shared values have a compile-time size. The registration's size
  is fixed when the extension registers it during load, so PostgreSQL 15's
  request hook and earlier versions' load-time requests both see it, and Windows
  backends that load the library again attach to the same size. Elements start
  at the first 8-byte boundary after the header. Guards copy the header or an
  element by index (rejected before PostgreSQL when out of range) and pass the
  header and a span to `Read` and `Mutate` callbacks; the lease gained
  length-checked block reads and writes that the existing typed accessors now
  use too. Unit tests cover registration, identity, initializers, impossible
  lengths, copies, spans and bounds; a packaged-consumer backend test sizes an
  array from `max_worker_processes` (13 and 3), claims slots from two backends,
  and checks an out-of-range index. It passes on PostgreSQL 13–19 beta 4, and
  the existing shared-memory backend tests pass after the lease change.
- `PgErrorContext` adds context lines to errors and messages, as an
  `ErrorContextCallback` does in C. `PG_END_TRY` restores
  `error_context_stack` even on success, so a guarded call cannot leave a
  callback on it; contexts live in a native list instead, and each guarded
  entry into PostgreSQL (memory-bridge operations and SPI calls) puts one
  callback for all of them on the stack while it runs. The text is copied to
  native memory when set, so PostgreSQL never calls managed code while
  reporting. A managed exception thrown while contexts are active records them
  on first throw and is reported with them. A function's leftover contexts are
  removed when it returns, through an unguarded removal that works during
  error cleanup. Backend tests check an SPI division by zero, a managed
  exception with nested and updated contexts, notices before and after an inner
  context is removed, and that a forgotten context does not reach later errors;
  they pass on PostgreSQL 13, 18 and 19. With both changes the full PostgreSQL 18
  suite ran 14,945 tests with 52 platform skips and one failure, a generator test
  that searched the generated SPI entry by its old name; it now anchors on the
  entry's core, and the 5,033 generator tests pass.
- `PgProcess.OnExit` runs managed cleanup when a backend or worker exits, as a
  C extension's `before_shmem_exit` callback does; pglogical releases its
  worker slot this way. One native callback per process, registered on first
  use (forked children clear inherited ones), runs the process's managed
  callbacks newest first with the exit code, with a memory scope for shared
  memory and locks but no SQL or logging. A managed failure is reported as a
  warning, because an ERROR during exit becomes FATAL, and the remaining
  callbacks still run. `PG_ENSURE_ERROR_CLEANUP` needs no counterpart: Ankus
  unwinds managed code before reporting a FATAL raised in a backend call, so
  `try`/`finally` runs. A unit test checks registration, order, removal and the
  reported failure; the shared-array backend test claims a slot with an exit
  callback that frees it and records the exit code, and checks a normal
  disconnect (0) and `pg_terminate_backend` (1) with a throwing callback logged
  as a warning. It passes on PostgreSQL 13, 18 and 19.
- `PgChildProcess.Run` runs a program from a backend or worker, as pglogical
  runs `pg_dump` and `pg_restore`. Unix starts it with `posix_spawn`, because
  `fork` is unsafe with runtime threads, and resets the signals PostgreSQL
  handles or ignores (including `SIGPIPE`) to their defaults in the child;
  Windows uses `CreateProcessW` with the C runtime's quoting rules. The wait
  polls the child and sleeps on the latch: a termination stops the child
  before PostgreSQL exits, and a cancellation error stops and reaps it before
  propagating. The exit code follows .NET's convention of 128 plus the signal
  for a signaled child. Backend tests run `pg_dump` with a table named
  `Odd "Table" é` into a data-directory file whose name has spaces and an `é`,
  check a failing exit code and a missing program's `58P01` (Npgsql closes a
  connection after class 58 errors, so a new session continues), and cancel a
  `psql` running `pg_sleep` with a statement timeout, after which its session
  disappears where PostgreSQL can notice a closed client (14 and later, not
  Windows). They pass on PostgreSQL 13, 18 and 19, and the full PostgreSQL 18
  suite passes with them (14,950 tests, 52 platform skips). CI run 38411604638
  on **21b0972** failed both on Windows: that check cannot be enabled there,
  and Windows gives `pg_dump`, like any program with a narrow `main`, its
  arguments in the ANSI code page, so `é` did not survive. The Windows test now
  passes quotes, spaces and backslashes only, and the guide states the limit.
- Output plugin aliases: an `AnkusOutputPluginAlias` item makes `dotnet
  publish` build a small C library under that name, beside the extension
  library, that loads the library by name and forwards
  `_PG_output_plugin_init`, as pglogical's `pglogical_output` loads
  `pglogical`. It is compiled with the extension's compiler and PostgreSQL
  headers, listed in manifest format 5 (`pluginLibraries`), and copied by
  `ankus install` and `ankus package`; a publish without the item deletes the
  module. PostgreSQL checks `output_plugin_libraries` against the slot's
  plugin name, so the alias must be listed itself. Unit tests cover the
  manifest's validation, round trip and cleanup, the build tool's names and
  generated source, and `publish-sql`'s module list. A packaged-consumer
  backend test creates slots under both names, checks that trusting the
  library does not trust the alias (`42501`), decodes the same changes through
  one loaded library (an initialization count continues across the names),
  packages the module and removes it on republish. It passes on PostgreSQL 13,
  18 and 19, and the full PostgreSQL 18 suite passes with it (14,985 tests, 52
  platform skips).
- A replication benchmark runs one backlog through PostgreSQL's built-in
  subscriber and the subscriber sample on the same cluster: rows inserted and
  then updated while a lock holds the subscriber's table, timed from the lock's
  release until the subscriber matches. In the suite it runs 2,000 rows and
  checks that both copies converge; `ANKUS_REPLICATION_BENCHMARK_ROWS` and
  `_BATCH` size it. On PostgreSQL 18.6 (Linux x64), 100,000 rows in
  1,000-row transactions applied at about 290,000 changes/s built in and
  255,000 through the sample (three runs), and 20,000 single-row transactions
  at about 160,000 and 61,000 (two runs). The sample first looked its local
  table up with SPI in every transaction (41,000 changes/s); it now caches the
  OID per remote relation until a new relation message, as the built-in
  relation map does. The remaining gap is per transaction: a managed
  transaction, a writer's executor state per table, and a key lookup before
  each insert. It passes on PostgreSQL 13, 18 and 19, and with exit callbacks
  the full PostgreSQL 18 suite passes (14,947 tests, 52 platform skips).
- Repository security: private vulnerability reporting, Dependabot alerts and
  security updates, secret scanning and push protection are on (non-provider
  patterns and validity checks need GitHub Secret Protection, which a personal
  repository lacks). `.github/dependabot.yml` updates NuGet, workflow actions and
  the documentation site weekly. The five open alerts were all in the site's
  lockfile; updating Astro and Starlight and refreshing transitive versions fixed
  four, and an override moves expressive-code's `postcss-nested` from 6 to 7.0.2,
  whose only change is `postcss-selector-parser` 7.1.6, the fixed version; the site
  builds byte for byte the same. The `CodeQL` workflow scans actions, C/C++, C# and
  TypeScript with `security-and-quality` and fails on any finding through
  `eng/Ankus.CodeQl.cs`. The `Secret scan` workflow runs Picket on every change.
  Picket's scan of the tree found one false positive, a 64-digit test cache key,
  which the tests now build from its repeating pattern; the history scan finds only
  that key's original commit. `SECURITY.md` and `CONTRIBUTING.md` are new.
- The subscriber sample's cost per remote transaction fell from about 20 µs to
  10 µs, slightly below the built-in subscriber's. Profiling the apply worker
  and in-backend benchmarks (`WriterBenchmarks` in the sample, run with
  `ankus bench`) showed that copying the table's descriptor took 1.07 µs of a
  writer's 1.67 µs open and close. A writer now gets PostgreSQL's type cache
  number for the table's row descriptor when it opens and reuses the managed
  descriptor while that number is the same, so opening and closing takes
  0.54 µs; an `ALTER TABLE` in the same transaction or another session yields
  a new descriptor. The sample keeps its column map and rows while the
  descriptor instance is unchanged, and now commits with `synchronous_commit`
  off, as the built-in subscriber and pglogical do. Inserting a replicated row
  through a writer takes 1.8 µs against 7.1 µs through SPI. On PostgreSQL 18.6
  (Linux x64, `fsync = off`), 100,000 single-row transactions applied at
  50,400 changes/s before, 67,000 with the caching alone and 100,400 with both,
  against 84,000 to 90,700 built in. In 1,000-row transactions the sample
  went from 219,000 to 225,000 changes/s against about 270,000; the rest is the
  key lookup before each insert that pglogical's conflict handling needs. The
  writer tests pass on PostgreSQL 13, 18 and 19, and the full PostgreSQL 18
  suite passes with these changes (14,986 tests, 52 platform skips).
- Output plugin aliases passed CI run 38421871434 on **6a34324** with the rest
  of the full suite on Linux x64 (PostgreSQL 18), macOS ARM64 (18) and Windows
  x64 (17), so the generated module builds and loads with Clang on macOS and
  MSVC on Windows. CI run 38421841668 on **bda942a** passed the corrected child
  process tests on the same three platforms.
- The generated-binding test harness found its scope through a thread static in
  each collectible test assembly; one PostgreSQL 15 run of the build tests saw
  it read as null. It now finds the scope from the API pointer each call
  receives and declares the runtime's full API layout, so the runtime never reads
  past it. The 368 binding tests pass.
- Every native memory call cleared a 2 KB error record and zero-initialized a
  recovery frame that holds another, though only worker transaction boundaries
  use the frame and readers treat the message as a C string. The entry now
  clears only the record's fields and sets only the frame's link and flag, as
  the managed call boundary already does. A borrowed input's lifetime also
  skips re-reading the generation it captured a moment earlier on its first
  check. `int ViewLength(PgTextView)` went from 0.42 µs to 0.28 µs per call,
  alternating the two builds. The 1,431 memory, worker, recovery, error,
  cancellation, transaction, borrowed view and callback backend tests pass on
  PostgreSQL 13, 16, 18 and 19 beta 4, and the full PostgreSQL 18 suite
  passes (14,894 tests, 52 platform skips).
- A scalar function's text result is encoded into a buffer each thread reuses
  rather than its own allocation, which native code copied and then freed
  through a callback into managed code. The wrapper copies the bytes before any
  managed code can write another result, so set rows and other values that wait
  keep owned strings. `string Echo(string)` went from 124 ns to 97 ns per call,
  alternating the two builds on one PostgreSQL 18.6 cluster. Unit tests cover
  reuse, the terminating zero, long results that are counted and do not keep
  their buffer, and rejected text; a generator test checks that only scalar
  results use the buffer. The 1,598 text, string, encoding, function, JSON,
  enum and SPI backend tests pass on PostgreSQL 13, 16, 18 and 19 beta 4, and
  the full PostgreSQL 18 suite passes (14,894 tests, 52 platform skips).
- A function returning a named composite type no longer asks the catalog for
  its return type, the type's kind and base type, and a copied result
  descriptor on every call, or forms the row twice. The return type comes from
  the call expression, one type lookup answers whether the type exists and is
  a domain, and only `record` results are laid out again in the caller's
  columns. A call that reads and returns a composite went from 1.36 µs to
  1.12 µs, alternating the two builds on one PostgreSQL 18.6 cluster. The
  1,310 composite, tuple, set, SPI, domain, record and function-call backend
  tests pass on PostgreSQL 13, 16, 18 and 19 beta 4, and the full PostgreSQL
  18 suite passes (14,889 tests, 52 platform skips). The native row serializer
  also lost the unavailable-column mask that only trigger rows used; 1,096
  tuple, trigger, relation, index, SPI and generated-column backend tests pass
  on PostgreSQL 13 and 18 without it.
- Trigger rows cross the boundary as one value per column. Sampling the
  backend with gdb showed much of a row trigger's remaining overhead was the
  self-describing row format: every column's name, types, modifier, collation
  and flags were serialized, parsed and compared in both directions on every
  row, though the call site already gives both sides the relation's columns.
  Native code now sends a cell array over the site's shape, with unavailable
  generated columns marked; managed code keeps one descriptor variant for them
  per site. `NativeValue.FromTriggerRow` returns the relation's rows the same
  way, marking each cell of NEW that was not replaced and holds an immutable
  value, so `heap_modify_tuple` keeps PostgreSQL's datum for it; rows of other
  descriptors still describe themselves and are checked. The result tuple is
  built in the caller's context instead of being copied there, and the call's
  error record is cleared rather than zeroed. Alternating the two builds on one
  PostgreSQL 18.6 cluster, the trimming trigger's overhead fell from 1.27 µs to
  0.90 µs per row, against 0.54 µs for PL/pgSQL, and a validating trigger's
  from 0.75 µs to 0.55 µs. Unit tests cover kept, replaced, dropped,
  unavailable and mutable cells, OLD and copied rows, foreign descriptors, the
  site's descriptor variant and malformed cell rows. The 469 trigger, tuple,
  composite, generated-column and custom scan backend tests pass on PostgreSQL
  13, 16, 18 and 19 beta 4, and the full PostgreSQL 18 suite passes (14,889
  tests, 52 platform skips).
- Managed custom scan providers can run in parallel. A provider that
  implements `IPgParallelCustomScan<TState, TShared>` offers paths with
  `PgCustomScanPlanner.AddParallelPath`; Ankus adds them as partial paths with
  PostgreSQL's parallel divisor for rows and run cost, skips them for tables
  and queries that cannot run in parallel and for scans that depend on an outer
  row, and caps their workers at `max_parallel_workers_per_gather`. The provider lays out
  dynamic shared memory as an unmanaged `TShared`: the leader clears and
  initializes it before workers start and again before a rescan, every process
  that reads gets a `PgCustomScanSharedMemory<TShared>` handle before its first
  row, and the handle stops working at shutdown, before PostgreSQL frees the
  memory. Implementing the interface also makes the provider's ordinary paths
  parallel-safe. `ANKUS540` rejects more than one shared layout. The sample
  claims integers from an atomic counter, and backend tests show every integer
  read once by worker processes, a Gather Merge over each worker's ordered rows
  with no sort, three rounds of a rescanned Gather each reinitializing the
  memory, a provider without `Rescan` whose replacement states keep the shared
  memory while the leader reads too, and a worker's exception reaching the
  client with recovery after rollback. PostgreSQL restarts a Gather's child
  only after it reinitializes shared memory, so Ankus attaches the memory again
  to any state a restart begins. The 90 custom scan backend tests pass on
  Linux x64 with PostgreSQL 13 through 18 and 19 beta 4, and the full
  PostgreSQL 18 suite passes (14,883 tests, 52 platform skips).
- Managed custom scan paths can declare their row order, and providers can
  read backward and mark and restore positions. `PgCustomScanPath.SortKeys`
  names columns with direction and NULL placement; the bridge turns them into
  PostgreSQL's canonical pathkeys as `build_index_pathkeys` does for an index,
  using each type's default B-tree ordering and stopping at the first key the
  query has no use for. `IPgScrollableCustomScan<TState>` adds `ReadBackward`,
  so a cursor without `SCROLL` can fetch backward and a `SCROLL` cursor needs
  no `Materialize`; `IPgMarkableCustomScan<TState>` adds `Mark` and `Restore`
  for merge joins. The generator passes each capability's PostgreSQL flag in
  the provider table. Backend tests cover nine cursor movements over a filtered
  scan, `ORDER BY` and `LIMIT` without a sort and the orders that still need
  one, and a merge join that restores the inner scan once per repeated outer
  key with no `Materialize`. The 85 custom scan backend tests pass on Linux
  x64 with PostgreSQL 13, 15, 16, 17, 18 and 19 beta 4.
- Managed custom scan providers learn which columns PostgreSQL reads. At plan
  time the bridge collects the columns the query needs above the scan and
  those its conditions use, as postgres_fdw does; a whole-row reference reads
  them all. `PgCustomScanContext.IsColumnRead` exposes the set, and a column
  that is not read may stay unset even when it is `NOT NULL`. The sample skips
  formatting its label when no query reads it. `PgCustomScanPlanner` reports
  the same set while planning, with the table's descriptor loaded on first use,
  so a provider can price a path by the columns it must produce; the sample
  charges twice as much when its label is read. PostgreSQL 19 rewrites
  `count(label)` on a `NOT NULL` column to `count(*)`, which the tests now
  account for. The 78 custom scan backend tests pass on PostgreSQL 13, 18 and
  19 beta 4, and the full PostgreSQL 18 suite passes (14,858 tests, 52 platform
  skips).
- Writing a row to native code encodes column names once per descriptor, tracks
  cycles only for rows with nested values, reuses the outermost buffer and
  encodes text straight into it. A trigger row's overhead fell from 1.42 µs to
  1.32 µs. The affected backend tests pass on PostgreSQL 13 and 18, and the full
  PostgreSQL 18 suite passes (14,854 tests, 52 platform skips).
- Domain values are checked with PostgreSQL's `domain_check` after their base
  type's modifier, as PL/pgSQL does, instead of preparing a coercion expression
  per value. The backend keeps each domain's constraint state, which
  `domain_check` refreshes when the domain changes, for up to 16 domains, and
  never clears it while a check is running. A trigger row with a `CHECK` domain
  column costs 0.5 µs less. New trigger cases cover a domain over `char(10)`, a
  `CHECK` domain over `varchar(10)` and a too-long value for a domain over
  `varchar(3)`. The domain, trigger, composite, set and SPI backend tests pass on
  PostgreSQL 13 and 18, and the full PostgreSQL 18 suite passes (14,854 tests,
  52 platform skips).
- Values written to a column with a type modifier, such as `varchar(64)` or
  `numeric(6,2)`, built, prepared and ran a coercion expression in a new
  executor state for every value. They now call the type's length coercion
  function, as PostgreSQL's assignment coercion does; domains and arrays keep
  the general path. A managed trigger row on a table with three `varchar`
  columns went from 5.0 µs to 3.1 µs. A new backend test checks that `char(10)`
  replacement values are padded and `varchar(10)` values are not. The trigger,
  composite and aggregate backend tests pass on PostgreSQL 13 and 18, and the
  full PostgreSQL 18 suite passes (14,851 tests, 52 platform skips).
- A row trigger's overhead fell from 3.24 µs to 1.42 µs per row, against
  0.56 µs for an equivalent PL/pgSQL trigger. Names, arguments, columns and base
  types that cannot change during a statement were converted, serialized and
  parsed again for every row. The native call site now keeps them in `fn_extra`,
  and a sixteenth slot identifies the site so managed code reuses what it parsed
  and each row reuses the site's descriptor. Tuple transports also stopped
  resolving a cell's base type a second time. The trigger, tuple and SPI backend
  tests pass on PostgreSQL 13 and 18, and the full PostgreSQL 18 suite passes
  (14,849 tests, 52 platform skips). See the
  [measurements](docs/contributing/evidence/guard-tiers.md#row-triggers).
- Scalar setting properties now read the variable PostgreSQL assigns, as pgrx's
  `GucSetting::get` does. A read was a backend call that searched the
  declarations by name and created a memory context; the first read now
  returns the variable's address through that same validated path, and later
  reads repeat only the managed access checks. A per-row integer read costs
  24 ns instead of about 350 ns. Reads inside callbacks that disable them still
  fail before and after the first read. Text settings still convert from the
  server encoding on each read. The configuration and custom scan backend tests
  pass on PostgreSQL 13 and 18, and the full PostgreSQL 18 suite ran 14,847
  tests; its one failure was a generator test still expecting the by-name read,
  now updated.
- The text array change in **cfd68a3** declared its null bitmap as `bits8`,
  which PostgreSQL 19 removed. Every extension with functions includes that
  code, so none built against 19 beta. Primary CI builds against 17 and 18
  only; the custom scan run on 19 found it. The bitmap is now
  `uint8`, the type `bits8` named in every earlier version.
- CI for **74c3ee4** ([38193316088](https://github.com/ankus-net/ankus/actions/runs/38193316088))
  failed once on macOS ARM64: the generator test process ended after 4,997 of
  5,018 tests with no failed test (exit code 7, "test host process crashed").
  The cases still running were the SPI interpolation flow-analysis tests. The
  log, the TRX and the runner's crash and memory-pressure reports hold no stack
  or cause. The rerun passed, and six direct runs on the same machine passed.
  It stays open under the first remaining-work item until it recurs with
  evidence. CI for **41b2111**
  ([38204764541](https://github.com/ankus-net/ankus/actions/runs/38204764541)),
  **6c008fc**, **f95d2cb**, **8bb5954**, **0883b1e**, **50c1491**, **feb2e51**
  and the earlier commits passed on all three platforms. CI for **3db7a56**
  ([38254761262](https://github.com/ankus-net/ankus/actions/runs/38254761262))
  did not run its tests: both macOS ARM64 runners, on a laptop that was off the
  home network, timed out downloading the workflow's actions, in the first
  attempt and in a rerun, so the test jobs were skipped. Its changes passed the
  full PostgreSQL 18 suite on Linux before the push, and CI for **7ed74c8**
  ([38261381904](https://github.com/ankus-net/ankus/actions/runs/38261381904)),
  which contains them, passed on all three platforms once the runners
  recovered.
- GUC hook (`ANKUS199`–`ANKUS201`) and benchmark setup (`ANKUS131`) errors
  now say what is wrong with the named method, such as "'Hook' returns bool"
  or "'Settings' has no method named 'Hook'", instead of restating the whole
  contract for every mistake.
- Aggregate transitions now resolve their result and argument types once per
  call site, as sets do, so an `int` sum with a `long` state costs 95 ns per
  row instead of 153 ns on the same release build. SPI result cells, whose
  column base types were already resolved once, no longer resolve them again
  for every cell: a row read in 1,000-row queries costs 93 ns instead of 106 ns.
- Set and table rows no longer look up each column's type for every row. The
  types are fixed for the call, so they are resolved when the set starts, and
  domain columns still check every value. An `int` set row costs 50 ns instead
  of 77 ns and an `(int, string)` table row 142 ns instead of 208 ns on a
  PostgreSQL 18.6 release build.
- Text array results no longer look up each element's type twice and copy it
  twice. Elements without a domain are checked as UTF-8 in one pass and
  written straight into the array in a second, the same layout PostgreSQL's
  `construct_md_array` produces, so a `string[]` result element costs 31 ns
  instead of 75 ns on a PostgreSQL 18.6 release build. A backend test checks
  NULLs past the first bitmap byte, empty and multibyte elements, element type,
  hashes and the empty array; all 512 array backend tests pass on Linux x64.
- The first release rehearsal ([38167639394](https://github.com/ankus-net/ankus/actions/runs/38167639394),
  **c51b0e4**) packed every managed package, but each runtime job checked
  out the Ankus repository instead of the runtime fork and failed with
  `not our ref`. Actions drops a job output that contains a masked value,
  and the runtime repository name contains one, so the checkout fell back to
  its default repository. Each packaging job now reads the runtime identity
  itself, as CI does. The package-set check also ran only in the publish
  job, which a rehearsal skips; a separate verify job now checks the set and
  each manifest's identity, license and source commit in both rehearsals and
  releases. The rehearsal reruns on these fixes.
- The second rehearsal ([38170635588](https://github.com/ankus-net/ankus/actions/runs/38170635588),
  **c412aca**) packed every managed package and the Linux x64, Windows x64
  and macOS ARM64 runtimes; their runtime builds took 2–4 minutes. On GitHub's
  hosted macOS 26 Intel runner the runtime built, but the fork's host-signal
  probe failed twice (`collect=2`). A diagnostic run of the cached runtime,
  which passes on the dedicated Intel runner, hung the compiler there too: its
  main thread sat in `_sigtramp` calling address 0. That runner starts
  processes with `SIGIO`, macOS's activation signal, set to the default action
  but with `SA_SIGINFO` still set, and 10.0.12's activation handler then
  chains to a null `sa_sigaction`. A process whose parent once caught `SIGIO`
  can inherit that state on any Mac. Upstream's 10.0.13 fix (`8250bd5`) checks
  for default and ignored handlers first. Linux has the same chain through
  `SIGRTMIN`, so a probe can set either state before loading a library and
  check it on every runner. Fork commit `fcf0c5c` extends the host-signal
  probe this way: on Linux x64 the 10.0.12 runtime crashes in both new cases,
  and with upstream's fix applied all three cases pass. The servicing update
  merges 10.0.13 with that probe, then reruns the rehearsal.
- Ankus library packages now embed their symbols. A package carried no PDB, so
  Native AOT had no source lines for framework code, and a debugger attached to
  a package-built extension showed Ankus frames without files or lines, while a
  checkout build showed them. Native AOT reads an embedded PDB first, so the
  extension's `.dbg`, `.pdb` or `.dSYM` now covers framework code as well, and
  Source Link maps it to the repository. A package test checks that the runtime,
  configuration and testing assemblies embed their PDB with Source Link.
  CI for **68037b7** ([38162580122](https://github.com/ankus-net/ankus/actions/runs/38162580122))
  and **c51b0e4** ([38166263958](https://github.com/ankus-net/ankus/actions/runs/38166263958))
  passes on Linux x64, macOS ARM64 and Windows x64.
- Editors now correct 117 declaration diagnostics, up from 64. An audit of all
  501 found the ones with a single mechanical answer: removing options or
  attributes that cannot apply, removing `async` from entries that await
  nothing (`Task<T>` becomes `T`), adding the trigger context, worker argument
  or benchmark parameter, fixing cast and worker parameter types, swapping
  reversed GUC bounds, and returning by value. Corrections that a caller could
  break, such as removing a setter that is still assigned, compare compiler
  errors only when applied and leave the source unchanged if they would add
  one. Diagnostics with more than one reasonable answer, such as SPI
  interpolation holes or snake_case names, stay without a fix. The guides list
  each correction next to its diagnostic. `ANKUS130` used to cover every
  benchmark declaration mistake; it now means a missing `static`, and
  `ANKUS527`–`ANKUS534` mark the other rules at the syntax that breaks them, as
  backend tests and workers already did.
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
  CI. That run ([38137637601](https://github.com/ankus-net/ankus/actions/runs/38137637601))
  failed only a new unit test whose Windows cppflags used backslashes, which
  pg_config's shell words treat as escapes; it was fixed in **699d12a**. The
  first cross-architecture run ([38137646798](https://github.com/ankus-net/ankus/actions/runs/38137646798),
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
  ([38131906576](https://github.com/ankus-net/ankus/actions/runs/38131906576)):
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
[38127753883](https://github.com/ankus-net/ankus/actions/runs/38127753883) and
[38129668001](https://github.com/ankus-net/ankus/actions/runs/38129668001)). The complete Windows x64 suite now
  passes on PostgreSQL 19 beta ([38130812681](https://github.com/ankus-net/ankus/actions/runs/38130812681)),
  and on 13–16 and 18 with the C11 bridges ([38119067045](https://github.com/ankus-net/ankus/actions/runs/38119067045)).
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
- The earlier Intel run ([38102775489](https://github.com/ankus-net/ankus/actions/runs/38102775489))
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
  It is now found and fixed: see the PostgreSQL 19 `ErrorContext` entry below.
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
  [CI 38089102305](https://github.com/ankus-net/ankus/actions/runs/38089102305)
  for **1b35c17**, Linux and Windows pass, but macOS bridges failed to compile:
  Clang 23 rejects the routing readiness flag as set but never read in
  extensions without managed calls. Readiness now tests the shared pointer.
  The wrapper also tolerates code that saves a handler as a plain function and
  restores it, which drops `SA_SIGINFO`: it checks its installed flags before
  reading sender information, and the next runtime thread restores the flag.
  [Platform-versions run 38093011552](https://github.com/ankus-net/ankus/actions/runs/38093011552)
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
- Code-fix tests now compare the complete fixed document.
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
  `XX000`. Linux ARM64, the last platform gap from the audit, is now supported (below).
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
  ([37933668208](https://github.com/ankus-net/ankus/actions/runs/37933668208))
  passes Linux x64 on 18 (18m), Windows x64 on 17 (21m) and macOS ARM64 on 18
  (16m). Its PostgreSQL 19 beta 4 rerun
  ([37934631721](https://github.com/ankus-net/ankus/actions/runs/37934631721))
  passes Linux x64 with the PGLZ sample fix (**14,410** total; **14,359** passed;
  51 skips; zero failures; 25m job), completing PostgreSQL 13 through 17 and 19
  on the version matrix after 5b72b18's run. The platform version run on **10c1391**
  ([37917953859](https://github.com/ankus-net/ankus/actions/runs/37917953859))
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
  text-array signatures pgrx keeps in its `todo` corpus. Test clusters and the
  `start`, `run`, `connect`, `regress` and `bench` commands now run under
  Valgrind, and `ankus bench --report` shows benchmark history.
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
  ([37909292198](https://github.com/ankus-net/ankus/actions/runs/37909292198):
  Linux 25m, Windows 29m, macOS 15m while sharing hosts with local validation).
  The version matrix rerun with the socket fix
  ([37909296064](https://github.com/ankus-net/ankus/actions/runs/37909296064))
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
  ([additional platforms 37892336665](https://github.com/ankus-net/ankus/actions/runs/37892336665), **1h15m**).
  The Linux version matrix on the same revision
  ([37892281684](https://github.com/ankus-net/ankus/actions/runs/37892281684))
  passes PostgreSQL 14 and 15. PostgreSQL 16, 17 and 19 fail the same 32
  consumer-fixture cases on newly added runners whose work directories are long:
  without an `ankus test` session, a fixture placed its Unix socket under `TMPDIR`
  and exceeded PostgreSQL's 107-byte socket path. The socket directory now stays
  under `TMPDIR` only while the longest socket path fits and otherwise uses `/tmp`,
  as PostgreSQL's own test clusters do; direct tests cover the byte boundary,
  including multibyte names. PostgreSQL 13's job stopped when its runner service
  received a shutdown signal. The matrix is to be rerun with the fix.
- The ankus.5 Linux version matrix
  ([38102793609](https://github.com/ankus-net/ankus/actions/runs/38102793609))
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
PostgreSQL 19 RC1, Linux ARM64, and Intel macOS on 13–18 and 19 RC1 all pass on runtime **10.0.13-ankus.1**. The
declaration audit against pgrx 0.19.3 has no open option, and the third code
review's findings are fixed or kept by documented design.

1. Resolve discovered correctness and CI failures before accepting affected work. The macOS ARM64 test-host crashes are unexplained: on 2026-10-14 the build-tool tests' host died after 698 of 1,346 tests, and its crash report shows `fork()`, called by .NET's `Process.Start`, aborting in libmalloc's xzone allocator (`_malloc_fork_parent`: unlock of an `os_unfair_lock` the thread does not own) while two other threads waited on xzone's fork lock to allocate. macOS 26 uses xzone for every ARM64 process, with no setting to turn it off outside Apple, and Intel Macs never use it. Rerun a job that ends this way and record it. On macOS, an extension's references to `hash_create`, `hash_destroy`, `hash_search` and `hash_stats`, which `NativeMethods` bodies also make, bind to libSystem's functions of the same names: the static linker binds a name that a linked system library exports, and `-undefined dynamic_lookup` covers only names it cannot find. A probe linked with Native AOT's macOS libraries binds those four, and `getpeereid` and `timingsafe_bcmp`, which PostgreSQL defines only where the system lacks them, to libSystem; every other catalog function and global is looked up in the backend.
2. Keep the pgrx 0.19.3 [release-delta gates](docs/contributing/evidence/parity-requirements.md#pgrx-0193-release-delta-and-refresh-gates) satisfied: each has implementation and complete-suite evidence on runtime 10.0.13-ankus.1, which must be refreshed on each later serviced runtime.
3. Keep the [public API mapping](docs/contributing/evidence/pgrx-api-mapping.md) current as APIs change.
4. Measured hot paths are done: a trimming row trigger costs about the same as PL/pgSQL (0.72 µs against 0.72 µs per row on one host, 0.55 µs against 0.52 µs on another), as the [guard-tier evidence](docs/contributing/evidence/guard-tiers.md#row-triggers) shows. Keep the in-backend baseline, and measure again when boundary code changes, preserving recovery and ownership guarantees.
5. Keep samples and guides current with API changes, and recheck changed guides before release. Test discovery for MSTest, xUnit and NUnit consumers and samples for all 37 pgrx examples are complete.
6. Keep the [logical replication toolkit](#beyond-pgrx-logical-replication-toolkit) current as a pglogical port exercises it; every planned capability is done.
7. Support .NET 11 as an extension target (required, in progress). The runtime fork's changes over 10.0.13 are ported onto .NET 11 RC2 as fork commit `34f19a015e3` on branch `ankus/runtime-11.0-rc2`, and every fork probe passes on Linux x64; rebase onto GA when it ships. `Ankus.Sdk` selects the `net11.0` line, `ankus new` and the templates take `--framework net11.0`, and CI and releases build both lines. The complete suite passes on every PostgreSQL major on Linux x64, macOS ARM64, Windows x64 and Intel macOS. Remaining: the rebase onto the .NET 11 release in November, which ships as a servicing update.
8. 0.1.0 through 0.2.2 are released; 0.1.1 reports build tool failures as build errors, 0.1.2 and 0.1.3 fix `ankus upgrade` and PostgreSQL discovery for the first outside install, 0.1.4 finds a Clang 20 or later when the one on `PATH` is older, 0.1.5 links Windows binding probes with MSVC's linker, 0.1.6 records each development cluster's log path in `.ankus-log`, 0.1.7 finds MacPorts' LLVM, 0.2.0 adds `PgSettings`, the benchmark rollback modes and the fixes for issues #5 through #13 and #18, 0.2.1 builds in Visual Studio, and 0.2.2 searches for Clang 20 in every build tool command. For 0.1.0, release run 38804273089 on e71e226 built, verified and published all 27 packages to nuget.org through the trusted publishing policy: the seven 0.1.0 packages and the patched runtime and compiler for five runtime identifiers on both lines (`10.0.13-ankus.1` and the `11.0.0-rc.2.26504.105-ankus.1` preview). From nuget.org alone, `Ankus.Tool` 0.1.0 creates a solution whose 7 tests pass against PostgreSQL 18, the templates do the same and publish an extension, and a fresh clone of the repository publishes a sample without staging a runtime.

Continue independent work while CI runs. Before every commit and push, check and
record previous run outcomes, including live runs, and resolve reported failures.
Keep this file current; append detailed implementation evidence to the archive
with a clear tested revision, platform/version, outcome and any remaining scope.
