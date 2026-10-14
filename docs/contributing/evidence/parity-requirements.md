# Full port requirements

This is the complete repository-derived requirement inventory extracted from
the port history. Historical completion columns are omitted; these contracts
remain in scope until current implementation and complete validation prove them.
See [current status](../../../PROGRESS.md) and the [historical evidence](port-history.md#repository-derived-parity-inventory).

## Repository-derived parity inventory

Reference: [pgrx](https://github.com/pgcentralfoundation/pgrx), commit `fc91c63ebad11784647b50ee7e265c1fd9c9924f`,
workspace version `0.19.3`. Paths in this section are relative to that read-only repository.
The inventory originated with 0.19.2. The complete 0.19.3 release delta below is
also required, including correctness, safety, maintenance and command changes.
Updating native inputs does not establish full API-by-API parity.
This inventory covers feature families discovered in the workspace, including features absent from its
README. Each family's public APIs, options, error behavior, ownership rules, examples, and regression
cases require implementation and evidence. Family coverage is not API-by-API completion evidence; the
[public API mapping](pgrx-api-mapping.md) records that item by item.

### pgrx 0.19.3 release delta and refresh gates

The [release](https://github.com/pgcentralfoundation/pgrx/releases/tag/v0.19.3)
contains eleven commits after 0.19.2. The local reference and Ankus's catalog
generator already select the exact release commit above. Audit every changed
source and regression contract before accepting the refresh; a matching pin is
only the first gate.

| Upstream change | Required Ankus behavior and acceptance |
| --- | --- |
| Headers, #2377 / `4f36fae4` | Retain all seven expanded `pg13.h`–`pg19.h` manifests, foreign declarations, constants, node/type graphs, OIDs and attribution. The checked-in manifests match upstream byte for byte: 481, 490, 497, 501, 508, 524 and 535 direct includes, respectively. The .NET maintenance command now discovers headers from each selected PostgreSQL server include tree with the same deliberate exclusions, formatting and version guard; binding check mode separately compares all seven with the pinned pgrx bytes, and Linux full-suite CI checks the selected installed manifest. Source cases retain newly exposed function signatures, mutable global storage, callback fields, inline helpers and struct fields. Backend cases execute the hash and overflow helpers and compare `max_prepared_xacts` with PostgreSQL's setting system. The complete suites pass on every supported major and platform on runtime 10.0.13-ankus.1. |
| PostgreSQL 19 beta 4, #2383 / `46eae1dd` | Keep bindings, OIDs, SQLSTATEs, version metadata and server fixtures on the same beta. Current inputs are aligned; refresh complete PG19 backend evidence after the current runtime and cleanup changes. Stable supported majors retain their own APIs and headers. |
| Cargo argument forwarding, #2343 / `bc09b536` | Source-case reconciliation covers the idiomatic repeatable `--property`/`-p` equivalent through initial project evaluation, restore, build, schema/no-build reuse, publication and nested test/benchmark calls. Automatic response files and explicit precedence are retained. Consumer cases select a custom restore configuration, package root and intermediate root with spaces; project/solution discovery, explicit projects, repeated values and relative `pg_config` paths are covered. The complete suites pass on every supported platform on runtime 10.0.13-ankus.1. |
| Package prefix, #2382 / `577ee501` | Preserve custom installation-prefix layouts, libraries, control files and authored/upgrade SQL through relocation and PostgreSQL loading. `--prefix-dir` and backend package-prefix tests exist; retain them in refreshed platform/version acceptance. |
| Lazy error allocations, #2369 / `b91a9ae0` | The upstream changes replace eager Rust `ok_or`/`unwrap_or` values with closures. Corresponding Ankus CLI, declaration/type graph, binding, SPI and test-harness failure paths use conditional branches, `?? throw` or deferred diagnostics, so successful paths do not construct those errors. No mechanical .NET change or performance claim is warranted; exact diagnostics and ownership tests remain. |
| Shared-memory macro lookup, #2371 / `7d512caf` | Generated shared-memory hooks resolve framework helpers without consumer imports. A source case with fully qualified attributes, no `using Ankus` and colliding consumer `PgSharedMemory`/`PgLwLock<T>` names compiles and dispatches the intended consumer callbacks through globally qualified framework transport. Retain real preload/chaining evidence on every supported platform/version. |
| Reporting interrupts, #2374 / `925e50fc` | Hold interrupts for non-ERROR reports and restore holdoff state after recoverable failures. Preserve real cancellation, FATAL/PANIC intent and managed unwinding in ordinary, GUC, worker and cleanup paths. Existing native guards and hook tests remain required. The cleanup-reporting defects found during the refresh are fixed: completion-callback failures report through a fixed buffer that preserves the primary error and durable outcome, and a terminal cleanup report keeps its FATAL or PANIC intent even when building it fails. The complete suites pass on every supported major and platform on runtime 10.0.13-ankus.1. |
| Varlena pointer safety, #2368 / `3223e80e` | Ankus's native bridges detoast first, then obtain the payload address with `VARDATA_ANY` and length with `VARSIZE_ANY_EXHDR`; they never construct an intermediate managed span or native slice from a one-byte-header size. Existing native/backend tests cover one-byte, four-byte, compressed and external/toasted storage, zero payloads, exact 126/127 boundaries and ownership. Refresh supported-major evidence with the current source. |
| Contribution rules, #2384 / `96284542` | Apply relevant ownership, minimal unsafe scope, measurement and evidence principles through Ankus's existing .NET conventions and user instructions. Rust-only rules are not automatic repository policy changes. No change to Ankus's `AGENTS.md` is required by this refresh. |
| CLI skills/docs, #2385 / `c21c79e8` | Reconcile updated command options, defaults, examples and limitations with the CLI inventory and public guides. Keep maintainer commands in contributor documentation and personal validation details private. Do not import upstream shell scripts or Rust agent instructions into user workflows. |
| Release metadata, #2386 / `fc91c63e` | Keep the reference revision consistent across catalogs, OIDs, notices and parity documentation. Ankus's initial package/tool/template version remains 0.1.0; it does not inherit pgrx's package version. Audit dependency and fixture changes for relevant .NET equivalents. |

Evidence on runtime 10.0.13-ankus.1: the complete suites, which include every
source and backend case named above, pass on Linux x64 for PostgreSQL 13–19 beta,
macOS ARM64 for 13–19 beta (assertions enabled), Windows x64 for 13–19 beta and
macOS x64 for 13–18 and 19 beta. The same cases passed earlier on 10.0.12-ankus.5.

Work order: fix confirmed correctness and safety defects first; verify and finish
the complete header/input refresh and its maintenance command; reconcile CLI,
allocation and shared-memory source cases; then refresh complete primary and
additional PostgreSQL/platform suites and release/package gates. Preserve all
previously inventoried requirements and record the actual tested revisions.
There are no deferred release-delta items.

### Development environment, commands, and distribution

The dispatch enum in `cargo-pgrx/src/command/pgrx.rs` contains all 17 commands below. Standard .NET
commands can supply the equivalent operation, with the Ankus tool providing PostgreSQL-specific behavior.

| Source command | Required equivalent behavior |
| --- | --- |
| `new` | Generate an ordinary extension project, control/configuration defaults, functions, and discoverable backend tests |
| `init` | Install/build supported PostgreSQL versions or register existing installs; persist configuration and toolchain options |
| `info` | Installation path, `pg_config` path, and exact PostgreSQL version queries |
| `start`, `stop`, `status` | Manage version-specific persistent development clusters, ports, logs, and lifecycle |
| `run`, `connect` | Build/install/load an extension and connect through `psql` or configured client, including `pgcli` |
| `test` | Backend test discovery, filters, expected errors, configuration, rollback, and supported-major matrix |
| `bench` | Attribute-driven benchmarks running inside PostgreSQL and result reporting (`pgrx-bench`) |
| `regress` | PostgreSQL regression SQL/expected-output suites and diagnostics |
| `schema` | Schema generation from one compilation, standalone extraction, ordering/dependencies, custom SQL, output options |
| `install` | Install libraries, control files, schema and upgrade scripts into selected PostgreSQL paths |
| `package` | Produce a relocatable installation tree for a selected version/target with custom library naming and an explicit installation prefix |
| `get` | Query extension control properties and derived extension metadata |
| `cross` / `pgrx-target` | Export target configuration/binding information and support target-aware build workflows |
| `upgrade` | Upgrade framework package references, including workspace/central versions and dry-run selection |

Additional tooling sources: `cargo-pgrx/src/{manifest,metadata}.rs`, command options in each command file,
`pgrx-pg-config/src/`, `pgrx-bindgen/src/`, and installation/upgrade fixtures in `cargo-pgrx/tests/`.
General build-property forwarding reaches project evaluation, restore and every
related build/test invocation, including nested fixture publication, matching the
upstream cargo-option contract. Automatic response files retain normal MSBuild
precedence. See the [CLI guide](../../src/content/docs/reference/cli.md#pass-msbuild-properties).
Execution-account options (`test --runas`, `regress --runas`) and privileged
installation (`install --sudo`) are implemented with real-account evidence. Every
Linux and macOS runner has a least-privilege run-as account, so these cases run
in CI, and the complete version and platform suites pass on runtime 10.0.13-ankus.1.
The framework also requires versioned extension SQL upgrades, custom/versioned shared-library names,
control-file settings, dependency handling, and deterministic packaging.
Primary author settings and native dependency/privilege behavior are implemented
through `AnkusExtensionControlFile`. `AnkusVersionControlFile` now carries
version-specific overrides through publish, install and package, with transactional
PostgreSQL update evidence. Alternate SQL directory layouts preserve omitted,
empty, relative and absolute values through installation and packaging, with
owned test staging and Unix parent/symbolic-link traversal. See the control and
directory milestones for exact tests and platform evidence; the complete
PostgreSQL/platform matrix passes on runtime 10.0.13-ankus.1.

NuGet packages now provide the extension-author project SDK, runtime, source generator, PostgreSQL configuration,
testing harness, and .NET tool. The SDK embeds a framework-dependent .NET 10 native-build helper and references
matching runtime/generator versions during the first restore. `Ankus.Generators` ships its generator and editor code-fix assemblies as analyzers;
compiler dependencies do not flow into extension projects. `Ankus.Testing` exposes its PgConfig and Npgsql dependencies.

`ToolCommandTests` packs unique versions and restores consumers outside the checkout with an empty package directory.
Installed-tool publishing/staging and direct `dotnet publish` both execute SQL in PostgreSQL 18. A separate MSTest
consumer uses the testing package and ordinary `dotnet test`, checks successful native calls, and recovers from a
managed exception on the same connection. Direct publishing also exercises Central Package Management and SDK
version selection from `global.json`. Paths contain spaces, including the NuGet cache; the SDK quotes native library
arguments that .NET 10's Unix Native AOT targets otherwise pass unquoted. Public publication still requires full feature
and platform/version validation.

### Source generation, schema, and extension declarations

Primary sources: `pgrx-macros/src/lib.rs`, `pgrx-sql-entity-graph/src/`, `pgrx/src/{fcinfo,iter,aggregate}.rs`.

| Feature family | Required behavior |
| --- | --- |
| `pg_extern` / `pgrx` | Names, schemas, overloads, strictness, defaults, named arguments, variadics, polymorphic/raw inputs and results |
| Function options (`extern_args.rs`) | Create-or-replace, immutable/stable/volatile, security invoker/definer, parallel modes, cost, support functions, dependencies, search path |
| `pg_schema`, `search_path` | Schema declarations, qualification, nested declarations, lookup/search-path semantics |
| `extension_sql!`, `extension_sql_file!` | Inline/file SQL, entity requirements, bootstrap/finalize positioning, declared created entities |
| `pgrx(sql = ...)` | Literal/disabled SQL generation with retained wrappers and entity dependencies |
| `default!`, `name!`, `composite_type!` | SQL default arguments, named table/aggregate fields, named composite type resolution |
| `SetOfIterator`, `TableIterator` | SETOF and TABLE results, nullability, tuple metadata, iteration cleanup on early exit/error |
| `pg_trigger` | Row/statement and before/after/instead-of triggers; event/argument metadata; OLD/NEW tuple access and modification |
| `pg_aggregate`, `AggregateName` | Transition/final/combine/serialize/deserialize; moving/inverse states; ordered-set/hypothetical; initial states, sort and parallel options |
| `pg_operator` and option attributes | Operator name, commutator, negator, selectivity/join support, hashes/merges, and schema dependencies |
| `PostgresEq`, `PostgresOrd`, `PostgresHash` | Equality, order and hash functions, operator classes/families and index use |
| `pg_cast` | Explicit/assignment/implicit casts and generated SQL |
| `pg_test` | Generated in-backend tests, discovery and expected-error metadata |
| `pg_bench` | Generated in-backend benchmarks, discovery, timing and result reporting |
| `pg_guard`, `initialize`, module magic | Guarded callbacks, bootstrap, panic/exception boundaries, module name/version and ABI checks |
| SQL entity graph and metadata | Type/function/schema dependencies, cycle diagnostics, SQL translation hooks, section encoding/decoding, ELF/PE/Mach-O extraction |

The operator option attributes are `opname`, `commutator`, `negator`, `restrict`, `join`, `hashes`, and
`merges`. GUC-specific derives/hooks are tracked with GUCs below. PostgreSQL event callbacks and owned
descriptive metadata are implemented as documented above. Custom scans, which pgrx exposes only through
its raw bindings, have native and managed providers; see the [custom scan guide](../../src/content/docs/custom-scans.md).

### Datum conversions and user-defined types

| Source | Required behavior |
| --- | --- |
| `datum/{from,into,unbox,borrow}.rs`, `nullable.rs`, `callconv.rs` | Conversion contracts, typed OIDs, SQL NULL distinct from zero, owned/borrowed lifetimes and argument/return ABI |
| `datum/{bytea_type,varlena}.rs`, `varlena.rs`, `toast.rs` | Bytes/text, C strings, packed/compressed/external TOAST, encoding, alignment, custom varlena layouts |
| `array.rs`, `array/`, `datum/array.rs` | Arrays, dimensions/lower bounds, null elements, owned and borrowed iteration, variadic arrays |
| `datum/{anyarray,anyelement,internal}.rs` | Polymorphic datums, resolved element OIDs, internal/pointer-bearing values |
| `datum/{numeric,numeric_support/}` | Arbitrary precision and constrained numeric types, arithmetic, rounding, conversion, exceptional values |
| `datetime.rs`, `datetime/` | Date, time, timestamp, timestamp with timezone, time with timezone, interval; infinities, ranges, arithmetic and time zones |
| `datum/{json,uuid,inet,geo,range}.rs` | JSON/JSONB, UUID, network, geometric and range datums with their operations |
| `heap_tuple.rs`, `htup.rs`, `tupdesc.rs`, `datum/tuples.rs` | Named/anonymous composites, tuple descriptors, access/mutation, dropped/null attributes, tuple ownership |
| `PostgresEnum`, `enum_helper.rs` | Label/OID mappings, schema lookup, generated enum DDL, enums in containers |
| `PostgresType`, `inoutfuncs.rs` | Custom base types with default CBOR in-memory/on-disk serialization and JSON human-readable input/output |
| `inoutfuncs`, `pgvarlena_inoutfuncs` type options | Custom textual representation, custom in-memory/on-disk layouts, alignment and manual datum conversion |
| `pg_binary_protocol` | Generated send/receive functions, binary protocol/COPY round-trips and invalid-input diagnostics |
| `postgres_type_variants` example/tests | All four custom-type paths, enum/struct variants, related derives and SQL override options |

Custom base types and PostgreSQL composite types have distinct storage and I/O contracts; both require
complete implementations. AOT serialization must use statically generated metadata/converters.

### Runtime and PostgreSQL internals

| Source modules | Required behavior |
| --- | --- |
| `spi.rs`, `spi/{client,query,tuple,cursor}.rs` | Sessions; read-only/read-write queries; typed parameters/results; tuple mutation; owned/borrowed prepared plans; keep/free; cursors, fetch, detach/find by name; scalar helpers and quoting |
| `memcx.rs`, `memcxt.rs`, `palloc.rs`, `palloc/`, `pgbox.rs`, `layout.rs` | Context selection/creation/switch/reset/delete; allocation/reallocation; context-bound cleanup; owned/borrowed server pointers |
| `fcinfo.rs`, `callconv.rs`, `fn_call.rs` | Function call context, collation, argument types/nulls, cached state, direct/named calls and result ownership |
| `list.rs`, `list/`, `stringinfo.rs` | PostgreSQL lists and string/binary buffer operations with native ownership |
| `rel.rs`, `itemptr.rs`, `pg_catalog/`, `namespace.rs`, `wrappers.rs` | Relation/index access and locks, tuple locations, function/type catalog lookups, namespaces and type resolution |
| `xid.rs` | Transaction identifier wrappers and conversions |
| `callbacks.rs` | Transaction/subtransaction callbacks, unregister and error cleanup |
| `guc.rs`, `PostgresGucEnum`, `pg_guc_hook` | Bool/int/real/string/enum settings, contexts/flags/bounds, hidden/named enum entries, check/assign/show hooks and structured errors |
| `bgworkers.rs` | Static/dynamic workers, startup/restart/shutdown, handles, signals/latches and backend connections |
| `shmem.rs`, `atomics.rs`, `lwlock.rs`, `spinlock.rs` | Shared memory registration, synchronization, atomics, lock lifecycle and preload initialization |
| `nodes.rs`, `pgrx-pg-sys/src/node.rs` | Node tags/type checks, allocation, conversion/string output, planner/executor node access |
| `pg_sys` hooks and `pgrx-examples/hooks` | Planner/executor, utility, parse, authentication and other exposed hooks; chaining and version-specific callback signatures |
| `pg_sys` custom scan structures/functions | Provider registration, paths/plans/states, executor lifecycle and supporting node/tuple APIs |
| `ffi.rs`, `pg_sys.rs`, `pgrx-pg-sys/src/submodules/{ffi,panic,pg_try,thread_check}.rs` | Native call guards, nested recovery, thread affinity, interrupts, deterministic managed cleanup |
| `pgrx-pg-sys/src/submodules/{elog,errcodes,panic,ffi,pg_try}.rs` | All log levels and SQLSTATE values; full diagnostics/context/object/location fields; catch/filter/rethrow behavior |
| `pgrx-pg-sys/src/{include,include.rs,cshim.rs,libpq.rs,port.rs,cstr.rs}` | PG13–19 functions, globals, constants, structs, unions, callbacks, inline/macro shims and string utilities |
| `pgrx-pg-sys/src/submodules/{datum,oids,transaction_id,htup,tupdesc,utils,cmp,sql_translatable}.rs` | Built-in OIDs, raw datum/tuple access, identifier helpers, comparison and SQL type metadata |
| `misc.rs`, `prelude.rs`, internal `ptr.rs`/`slice.rs` | Hash helpers, ergonomic API access, pointer/slice lifetime semantics underlying public APIs |

`pgrx-bindgen` and the per-major `pgrx-pg-sys/src/include/pg13.rs` through `pg19.rs` are required input
to the versioned raw API inventory. The raw API includes direct unsafe access as well as safe wrappers;
error-producing calls still need a native guard that prevents longjmp across managed frames.

### Examples and test corpus

All example directories in `pgrx-examples/` require a corresponding working .NET scenario and validation:

- Types/data: `arrays`, `bytea`, `composite_type`, `custom_types`, `datetime`, `json`, `numeric`,
  `postgres_type_variants`, `range`, `strings`.
- SQL/functions: `aggregate`, `generic_agg`, `custom_sql`, `operators`, `schemas`, `spi`, `spi_srf`, `srf`, `triggers`.
- Backend/runtime: `bgworker`, `errors`, `hooks`, `memory_contexts`, `notify`, `pglz_inspect`, `pgthread`,
  `pgtrybuilder`, `rewrite_manip`, `shmem`, `subtrans_infos`, `wal_decoder`.
- Build/tooling/constraints: `bad_ideas`, `benching`, `custom_libname`, `nostd`, `versioned_custom_libname_so`,
  `versioned_so`. Rust-specific mechanisms require an explicit idiomatic .NET capability mapping and tests.

Every directory above has a corresponding sample under `samples/` with backend
integration tests; the [pgrx examples reference](../../src/content/docs/reference/pgrx-examples.md)
maps each one, including the idiomatic .NET mapping of the Rust-specific
`bad_ideas` and `nostd` scenarios. Their backend tests run in the complete
suites, which pass on every supported major and platform on runtime 10.0.13-ankus.1.

Required test-source inventory:

- `pgrx-unit-tests/src/tests/`: datum/array/borrow/NULL/zero-datum tests; numeric/date/network/JSON/UUID/geometric/range
  tests; custom type/enum/composite/tuple tests; function/default/variadic/cast/operator/aggregate/schema/attribute tests;
  SPI/SRF/call-context tests; memory/list/relation/shared-memory/GUC/worker/callback/XID tests; guard/log/error tests;
  property/round-trip tests; lifetime/name/type-identity/signature/version/inline-binding and issue regressions.
- `pgrx-unit-tests/tests/{compile-fail,nightly,todo}` and `ui.rs`: diagnostics and unsupported-signature/lifetime cases,
  with each Rust-specific constraint translated to the relevant .NET compile-time or runtime guarantee.
- `pgrx-tests/src/framework{.rs,/}`: local installation/cluster management, backend test setup, expected errors,
  per-test transactions, diagnostics, cleanup, configuration and concurrent execution; `proptest.rs`: property testing.
- `pgrx-bench/src/` and `cargo-pgrx/src/command/bench.rs`: benchmark discovery and in-backend execution.
- `cargo-pgrx/tests/`: install/test regression fixture, CLI dependency upgrades and workspace fixtures.
- Inline unit tests in runtime, macro, SQL graph, binding-generation, and configuration crates; SQL and expected-output
  fixtures in the examples and regression-command paths.

The [pgrx unit-test mapping](pgrx-test-mapping.md) maps all 539 cases in
`pgrx-unit-tests/src/tests/` to named Ankus tests or a recorded reason; none
remain open. The [tooling test mapping](pgrx-tooling-test-mapping.md) maps 203
compile-fail, testing-framework, benchmark and `cargo-pgrx/tests/` cases; none
remain open. The [inline unit-test mapping](pgrx-inline-test-mapping.md) maps
the 156 inline tests in the runtime, macro, SQL graph, binding, configuration,
benchmark and `cargo-pgrx` crates; none remain open.

### Release evidence requirements

| Deliverable | Required evidence |
| --- | --- |
| Full runtime/macro/CLI parity | Source API/option inventory mapped to implemented APIs, behavior tests and examples |
| Native AOT safety | Trim/AOT-clean consumers; deterministic cleanup on exceptions, native errors, cancellation and recursive callbacks |
| PostgreSQL 13, 14, 15, 16, 17, 18, 19 beta | Per-major builds against that server's headers, version-specific APIs/gating and complete backend tests |
| Windows, Linux, macOS | Native builds, exports/loading, lifecycle, encoding, toolchain and installer tests for each supported RID |
| Ordinary .NET usage | One NuGet reference, attributed methods, `dotnet publish`, discoverable plain `dotnet test` and working tool commands |
| .NET support and runtime servicing | Explicit SDK/TFM/compiler/runtime/RID identities, current upstream patches, complete backend validation for each declared runtime major and documented rebuild instructions |
| Installation and upgrades | Clean install, relocation, removal, versioned-library coexistence, upgrade scripts and data compatibility |
| Examples and documentation | Every inventoried scenario runnable with tested usage/configuration/API documentation |
