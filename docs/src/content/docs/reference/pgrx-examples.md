---
title: pgrx examples
description: Find the Ankus sample or guide that corresponds to each pgrx example.
---

Every example in pgrx's `pgrx-examples` directory has an Ankus counterpart: a
runnable sample under [`samples/`](https://github.com/ankus-net/ankus/tree/main/samples)
or, for Rust-only mechanisms, a guide that explains the .NET equivalent. Samples
are ordinary projects; publish and install one as described in
[Publish and install](/getting-started/publishing/), then run the SQL in its README.

| pgrx example | Ankus | Notes |
| --- | --- | --- |
| `aggregate` | [`Ankus.Examples.Aggregates`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Aggregates) | Managed state, parallel transport and moving windows. See [Aggregates](/aggregates/). |
| `arrays` | [`Ankus.Examples.Arrays`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Arrays) | Borrowed and copied arrays. See [Arrays](/arrays/). |
| `bad_ideas` | [`Ankus.Examples.BadIdeas`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.BadIdeas) | Each bad idea is rejected, converted to an error, contained or documented. See [Running .NET inside PostgreSQL](/reference/execution/). |
| `bgworker` | [`Ankus.Examples.BackgroundWorkers`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.BackgroundWorkers) | See [Background workers](/background-workers/). |
| `benching` | [`Ankus.Examples.Benchmarks`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Benchmarks) | In-backend benchmarks through `ankus bench`. See [Benchmarks](/benchmarks/). |
| `bytea` | [`Ankus.Examples.Bytea`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Bytea) | |
| `composite_type` | [`Ankus.Examples.Composites`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Composites) | See [Composite values](/composites/). |
| `custom_libname` | [`Ankus.Examples.CustomLibraryName`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.CustomLibraryName) | `AnkusLibraryName` replaces Cargo's `[lib] name`. See [native library names](/reference/build-settings/#native-library-names). |
| `custom_sql` | [`Ankus.Examples.CustomSql`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.CustomSql) | Bootstrap, inline, file and final blocks in dependency order. See [Custom SQL](/custom-sql/). |
| `custom_types` | [`Ankus.Examples.CustomTypes`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.CustomTypes) | See [Custom types](/custom-types/). |
| `datetime` | [`Ankus.Examples.DateTime`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.DateTime) | See [Date and time values](/date-and-time/). |
| `errors` | [`Ankus.Examples.Errors`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Errors) | Managed exceptions replace Rust panics. See [Logging and errors](/logging/). |
| `generic_agg` | [`Ankus.Examples.GenericAggregates`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.GenericAggregates) | Polymorphic aggregates using PostgreSQL's datum copy and comparison bindings. |
| `hooks` | [`Ankus.Examples.Hooks`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Hooks) | Executor, parse-analysis and utility hooks installed once and chained. See [Raw values](/raw-values/#managed-native-callbacks-and-hooks). |
| `json` | [`Ankus.Examples.Json`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Json) | See [JSON and UUID values](/json-and-uuid/). |
| `memory_contexts` | [`Ankus.Examples.MemoryContexts`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.MemoryContexts) | Scratch contexts, sets and a worker counter in `TopMemoryContext`. |
| `nostd` | [`Ankus.Examples.MinimalRuntime`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.MinimalRuntime) | .NET has no runtime-free mode; the sample removes optional Native AOT runtime features. |
| `notify` | [`Ankus.Examples.Notify`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Notify) | LISTEN/NOTIFY wrappers, a cache-invalidation trigger and invalidations coalesced at commit. See [Transaction callbacks](/transaction-callbacks/). |
| `numeric` | [`Ankus.Examples.Numeric`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Numeric) | See [Numeric values](/numeric/). |
| `operators` | [`Ankus.Examples.Operators`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Operators) | See [Operators and casts](/operators-and-casts/). |
| `pglz_inspect` | [`Ankus.Examples.PglzInspect`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.PglzInspect) | PGLZ probes, column sampling, ratio histograms and recommendations through PostgreSQL's compressor. |
| `pgthread` | [`Ankus.Examples.Threads`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Threads) | Managed threads compute; only the backend thread calls PostgreSQL. |
| `pgtrybuilder` | [`Ankus.Examples.TryCatch`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.TryCatch) | Filtered `catch` and `finally` replace `PgTryBuilder`. |
| `postgres_type_variants` | [`Ankus.Examples.TypeVariants`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.TypeVariants) | JSON, custom text, packed native, hand-written datum and array variants. See [Custom types](/custom-types/). |
| `range` | [`Ankus.Examples.Ranges`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Ranges) | See [Ranges](/ranges/). |
| `rewrite_manip` | [`Ankus.Examples.RewriteManip`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.RewriteManip) | `Var` changes through `rewrite/rewriteManip.h`. See [Native declarations](/raw-values/#native-postgresql-declarations). |
| `schemas` | [`Ankus.Examples.Schemas`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Schemas) | Objects in the installation, extension-owned, `public` and `pg_catalog` schemas. |
| `shmem` | [`Ankus.Examples.SharedMemory`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.SharedMemory) | Bounded collections, locks and an atomic in preloaded shared memory. See [Shared memory](/shared-memory/). |
| `spi`, `spi_srf` | [`Ankus.Examples.Spi`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Spi) | See [SPI queries](/spi/). |
| `srf` | [`Ankus.Examples.Sets`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Sets) | C# iterators replace pgrx's iterator types. See [Sets and tables](/sets-and-tables/). |
| `strings` | [`Ankus.Examples.Strings`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Strings) | |
| `subtrans_infos` | [`Ankus.Examples.Subtransactions`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Subtransactions) | Transaction status, parents, nesting level and commit time. |
| `triggers` | [`Ankus.Examples.Triggers`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Triggers) | See [Triggers](/triggers/). |
| `versioned_custom_libname_so` | [`Ankus.Examples.VersionedCustomLibraryName`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.VersionedCustomLibraryName) | `AnkusLibraryName` with `AnkusVersionedLibrary`. |
| `versioned_so` | [`Ankus.Examples.VersionedLibrary`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.VersionedLibrary) | `AnkusVersionedLibrary` installs versions side by side. See [versioned libraries](/reference/build-settings/#versioned-libraries). |
| `wal_decoder` | [`Ankus.Examples.WalDecoder`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.WalDecoder) | A logical decoding output plugin that writes changes as JSON. See [Logical decoding output plugins](/logical-decoding/). |

[`Ankus.Examples.Hello`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Hello)
is the extension `cargo pgrx new` would generate: one function, its backend test
and an installable library.

Ankus also includes samples without a direct pgrx counterpart:
[`Ankus.Examples.CustomScans`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.CustomScans),
[`Ankus.Examples.Configuration`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Configuration),
[`Ankus.Examples.Enums`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Enums),
[`Ankus.Examples.EventTriggers`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.EventTriggers) and
[`Ankus.Examples.Initialization`](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Initialization).
