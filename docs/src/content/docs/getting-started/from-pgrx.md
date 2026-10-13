---
title: Ankus for pgrx users
description: Translate pgrx declarations, values, ownership, and extension workflows into C#.
---

Ankus keeps PostgreSQL's extension model and expresses it through C# attributes,
compiler-checked interfaces and source generation. Publish an extension as a
Native AOT library with its SQL and control files. The server does not need a
separate .NET installation. To start from a familiar example, find the
counterpart of each pgrx example in [pgrx examples](/reference/pgrx-examples/).

## Declarations

| pgrx | Ankus | Guide |
|---|---|---|
| `#[pg_extern]` | `[PgFunction]` on a synchronous static method | [Functions](/function-declarations/) |
| `#[search_path(@extschema@)]` | `SearchPath = [PgSearchPath.ExtensionSchema]` on `[PgFunction]` or `[PgTest]` | [Execution options](/function-declarations/#execution-options) |
| `#[pg_schema]` | `[PgSchema]` on a declaration container | [Custom SQL](/custom-sql/) |
| `default!`, `variadic!` | C# optional parameters and `params T[]` | [Function signatures](/function-declarations/) |
| `TableIterator` and `name!` columns | `IEnumerable<(int Id, string Name)>`, with C# tuple element names | [Sets and tables](/sets-and-tables/) |
| `#[pg_trigger]` | `[PgTrigger]` with `PgTriggerContext` | [Triggers](/triggers/) |
| `#[pg_aggregate]` and `Aggregate` | `[PgAggregate]`, `IPgAggregate<TState, TArgs>` and optional capability interfaces | [Aggregates](/aggregates/) |
| `PostgresEnum` | `[PgEnum]` and optional `[PgEnumLabel]` | [Enums](/enums/) |
| `PostgresType` | `[PgType]` with generated storage or an explicit codec | [Custom types](/custom-types/) |
| `#[pgvarlena_inoutfuncs]` with `PgVarlena<T>` | `[PgType(NativeLayout = true, TextCodec = ...)]` with `PgVarlena<T>` | [Custom types](/custom-types/#native-storage) |
| `PostgresEq`, `PostgresOrd`, `PostgresHash` | `[PgEquality]`, `[PgOrdering]`, `[PgHashing]` with .NET comparison/hash contracts | [Operators](/operators-and-casts/) |
| `#[pg_operator]`, `#[pg_cast]` | `[PgOperator]`, `[PgCast]` | [Operators and casts](/operators-and-casts/) |
| `extension_sql!`, `extension_sql_file!` | Assembly-level `[PgSql]`, `[PgSqlFile]` | [Custom SQL](/custom-sql/) |
| SQL positioning references | `[PgRequires]` and `[PgBefore]` with managed declarations, or explicit IDs | [SQL dependencies](/custom-sql/) |
| `_PG_init` | `[PgModuleLoad]` for early registration; `[PgInitialize]` for backend initialization | [Initialization](/initialization/) |
| `#[no_mangle] extern "C-unwind" fn _PG_output_plugin_init` | `[PgOutputPlugin]` on a static method taking `OutputPluginCallbacks*` | [Logical decoding](/logical-decoding/) |
| `pg_module_magic!` | Generated module magic; `[PgModule]` or project settings for identity | [Build settings](/reference/build-settings/#native-module-identity) |
| `#[pg_test]` | `[PgTest]` with generated backend test cases | [Testing](/getting-started/testing/) |
| `#[cfg(feature = "pg_test")]` | `#if ANKUS_TESTS` | [Testing](/getting-started/testing/) |
| `assert!` and `assert_eq!` in `#[pg_test]` | `PgAssert.IsTrue`, `PgAssert.AreEqual` and `PgAssert.ThrowsSqlState` | [Testing](/getting-started/testing/#declare-tests-inside-the-extension) |
| `pg_test::postgresql_conf_options()` | `PostgresExtensionTestOptions.PostgreSqlConfiguration` | [Testing](/getting-started/testing/#declare-tests-inside-the-extension) |
| `pg_test::setup(options)` | Host code in the data-driven test method before `RunTestAsync`, which receives each `PgTestCase` | [Testing](/getting-started/testing/#declare-tests-inside-the-extension) |
| `pgrx_tests::proptest::PgTestRunner` and proptest strategies | `PgPropertyRunner` and `PgGenerators` | [Property tests](/getting-started/testing/#property-tests) |
| `#[pg_bench]`, `Bencher`, `BatchSize` | `[PgBenchmark]`, `PgBencher`, `PgBenchmarkBatchSize` | [Benchmarks](/benchmarks/) |

Execution options such as volatility, parallel safety and NULL policy are enum
properties on the attributes. SQL names can differ from C# names. Ankus generates
and orders support functions, types and dependent SQL during the build.

## SQL NULL and values

Rust's `Option<T>` becomes nullable C# value types or nullable reference
annotations. Enable nullable reference types in the extension project; oblivious
reference parameters are rejected rather than silently made strict.

```csharp
[PgFunction]
public static string Greet(string? name) => $"Hello, {name ?? "world"}!";
```

The nullable parameter allows SQL NULL to reach this method. With inferred NULL
policy, an entirely required signature is `STRICT`. For mixed signatures, a NULL
required argument returns SQL NULL without invoking C#; nullable arguments are
passed through. A nullable result can return SQL NULL.

| pgrx value or concept | Ankus representation |
|---|---|
| `&str`, `String` | `string`; checked native borrowing through `PgTextView` |
| `char` | `Rune` as `varchar` |
| `Vec<u8>` / `bytea` | `byte[]`; `PgByteaView` for borrowed bytes |
| SQL arrays | `T[]` for vectors, `PgArray<T>` for explicit shape, `PgArrayView<T>` for checked borrowing, `CreateFlatArray<T>` for `FlatArray` |
| `Json`, `JsonB` | `PgJson`, `PgJsonb` |
| `AnyNumeric` | `PgNumeric` |
| PostgreSQL date/time values | `PgDate`, `PgTime`, `PgTimestamp`, `PgTimestampTz`, `PgTimeTz`, `PgInterval` |
| `AnyElement`, `AnyArray` | `PgAnyElement`, `PgAnyArray` |
| Native `Datum` | `PgDatum` with explicit `[PgSqlType]` where required |
| `IntoDatum::into_datum` | `PgDatum.Create(value, context)` |
| Manual `FromDatum`/`IntoDatum` impls | `[PgDatumType]` with `IPgDatumReader<T>`/`IPgDatumWriter<T>`, also read and written by `SpiRow` and `PgHeapTuple` |
| Composite tuples | `PgHeapTuple`, optionally bound with `[PgCompositeType]` |
| Managed aggregate payload | `PgAggregateState<T>` |
| Backend-owned opaque state | `PgInternal` |

Use the full-range PostgreSQL temporal types when .NET's built-in range cannot
represent a value. pgrx reads a `char` from the first character of its text and
treats empty text as SQL NULL; a `Rune` must hold exactly one Unicode scalar
value, so `''` and `'ab'` fail instead. Use shaped arrays when dimensions or lower bounds matter.
Conversions reject unsupported or lossy representations; see the individual
value guides for exact rules.

## Ownership and backend access

Rust lifetimes become explicit ownership and checked lifetimes in Ankus's
wrappers. `PgMemoryContext`, owned allocations and borrowed views distinguish
who frees native storage and when access ends. A managed reference does not keep
a PostgreSQL buffer alive. Copy values into an appropriate owner before retaining
them beyond the current callback.

`using` and `Dispose` release owned resources deterministically. Dispose native
database resources on the backend thread. Managed GC, tasks and finalizers have
different rules from PostgreSQL memory contexts; read
[Running .NET inside PostgreSQL](/reference/execution/) before introducing
background work or large caches.

`pg_sys` calls correspond to generated `NativeMethods` and `NativeGlobals`, with
the native names preserved. Ankus guards PostgreSQL errors in native code before
they reach managed frames. The caller still owns native argument, layout,
allocator and lifetime correctness. Raw calls and global accesses require an
unsafe context, including signatures containing only scalars. Data pointers
retain their C# pointee types. See [native bindings](/raw-values/).

Native addresses from hooks and raw calls have unsafe constructors, as in pgrx:
`PgMemoryContext.DangerousBorrow` for `PgMemoryContexts::For`, `DangerousGetOwner`
for `GetMemoryChunkContext`, and `PgHeapTuple.DangerousCopy`,
`PgTupleDescriptor.DangerousCopy` and `DangerousToNative` for `from_heap_tuple`,
`from_pg_copy` and `into_pg`. See [borrowed native allocators](/memory-contexts/#borrowed-native-allocators)
and [composite values](/composites/).

A custom scan written against `pg_sys::CustomScanMethods` has a direct
counterpart in the generated method tables and `[PgNativeCallback]`. Ankus also
offers `[PgCustomScan]`, a managed provider that only plans paths and produces
rows. See [custom scan providers](/custom-scans/).

## SPI, errors and cancellation

Use `Spi.ExecuteScalar<T>`, `Spi.Execute` and `Spi.Connect` for backend SQL.
Parameters are explicit `SpiParameter` values. A connected session stays inside
its callback. Result rows are managed copies; kept plans and cursors have their
own lifetime rules. A cell whose type has no managed conversion is copied exactly
and fails only when read, so, as in pgrx, it does not stop the rest of the row.
`GetOrdinal(name)` on results and rows corresponds to `column_ordinal`. Unlike pgrx's
`get_one`, scalar helpers run the whole command and copy its first row. See
[SPI queries](/spi/).

`Spi.Select`, `Spi.SelectRaw` and the default cursor overload follow
`SpiClient::select`: they use read-only snapshots until the transaction becomes
writable, then take fresh writable snapshots. Sessions and plans expose the
same selection methods. `Execute`, default `Query` overloads and scalar helpers
establish writable intent, matching pgrx's `run` and `get_one`. Use
`Spi.Query(sql, readOnly: true, limit: 0)` or the corresponding `SpiSession.Query`
overload for PostgreSQL's read-only snapshot and write restrictions. The
`Execute` and `ExecuteScalar` methods have no `readOnly` overload. A query
beginning with `SELECT` does not select read-only snapshot behavior.

pgrx runs SPI statements directly in the current transaction, so an error
aborts the statement. Ankus runs each call in an internal subtransaction instead,
so managed code can catch `PgException` and continue. For pgrx's cost profile in
loops of writes, wrap them in
`PgTransaction.RunInSubtransaction(action, PgSubtransactionMode.Atomic)`: the
statements share one subtransaction, and an error rolls back the whole scope.
See [errors and transactions](/spi/#errors-and-transactions).

Throw `PgException` to report a PostgreSQL SQLSTATE and owned diagnostic fields.
`PgLog` supplies PostgreSQL message levels. Native errors unwind managed cleanup
before PostgreSQL reports them at the outer boundary. Like pgrx's `debug1!`,
`PgLog.Write(PgLogLevel.Debug1, $"...")` evaluates its arguments only when the
level is enabled, and `PgDiagnostic.Domain` corresponds to `ereport_domain!`.
pgrx reports a returned `Err` as SQLSTATE 22000; Ankus reports an unhandled .NET
exception as 38000 (`external_routine_exception`), and `PgException` sets any
other code.

For deliberate error recovery comparable to a `PgTryBuilder` recovery scope,
use `PgTransaction.RunInSubtransaction` around work that must roll back. A plain
`catch (Exception)` cannot make an unrecovered raw native failure successful.
Cancellation, `Fatal` and `Panic` remain pending even after a catch or rollback.
See [recoverable work](/transaction-callbacks/#recoverable-work) and the
[try and catch sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.TryCatch),
which maps `catch_when`, `catch_rust_panic`, `rethrow` and `finally` to C#.

Use `PgInterrupts.Check()` where Rust code would use `check_for_interrupts!` in
a long loop, and `PgInterrupts.IsPending` for `interrupt_pending()`. Call it on the PostgreSQL thread; thread-pool continuations do not
have backend access.

## Workers and configuration

`BackgroundWorkerBuilder` workflows use `PgBackgroundWorker` registration and a
`[PgBackgroundWorker]` entry point. Worker transactions, signals and latches
remain PostgreSQL operations. `PgLwLock<T>`, `PgAtomic<T>` and
`PgSharedMemory.Initialize` provide shared state with explicit native ownership.
See [background workers](/background-workers/) and [shared memory](/shared-memory/).

GUC declarations use attributes on static partial properties:

```csharp
public static partial class Settings
{
    [PgGucInt("my_extension.limit", 100, "Maximum items", Minimum = 1)]
    public static partial int Limit { get; }
}
```

Reading a property returns the current value from the variable PostgreSQL
assigns, as `GucSetting::get` does.
Check, assign and show hooks are ordinary named methods selected by the
attribute. Their phase and SQL-access restrictions still apply. When a name is
computed at run time, as with `GucRegistry::define_int_guc` in `_PG_init`, call
`PgGucRegistry.DefineInt` and its siblings from a `[PgModuleLoad]` method and read
the returned `PgGucSetting<T>`. The `define_*_guc_with_hooks` variants map to the
optional `PgGucHooks<T>` argument. See
[configuration settings](/configuration/#run-time-definitions).

## Beyond pgrx

Some Ankus APIs have no pgrx counterpart. They exist so that extensions which
pgrx leaves to raw `pg_sys` calls and external libraries, such as logical
replication extensions, can be written in ordinary C#:

- [Replication connections](/replication-connections/) reach another server
  from a backend through PostgreSQL's own `libpqwalreceiver` to run SQL, copy
  tables and stream logical changes. A pgrx extension links a client library
  such as libpq itself.
- [Arrays sized at startup](/shared-memory/#arrays-sized-at-startup) keep a
  header and an array whose length comes from a server setting, such as
  `max_worker_processes`, behind one lock. pgrx's shared collections have a
  capacity fixed at compile time.
- [Error context](/logging/#error-context) adds a line to errors and messages
  reported while it is active, where a pgrx extension links an
  `ErrorContextCallback` into `pg_sys::error_context_stack` itself.
- [Exit callbacks](/background-workers/#cleanup-at-exit) run managed cleanup
  when a backend or worker exits, where a pgrx extension registers
  `pg_sys::before_shmem_exit` callbacks itself.
- [Running programs](/background-workers/#running-programs) starts a program
  such as `pg_dump` and waits for it with cancellation, where a pgrx extension
  forks or spawns it through `libc` itself.
- [Output plugin aliases](/logical-decoding/#load-the-plugin-under-another-name)
  publish a small library that loads the extension's output plugin under a
  second name, as pglogical's `pglogical_output` does. A pgrx extension builds
  that library itself.
- [Applying replicated changes](/applying-changes/) writes rows through the
  executor's replication helpers, finds rows with their commit origin and time,
  and tracks progress with replication origins. A pgrx extension builds executor
  state and calls those functions through `pg_sys` for each PostgreSQL version.

## Extension workflow

| cargo-pgrx workflow | Ankus workflow |
|---|---|
| Create an extension | `ankus new Hello` |
| Register or install PostgreSQL | `ankus init --pg18 download`, or supply `pg_config` |
| Run tests | `dotnet test`, or `ankus test --pg 18` |
| Run in-backend benchmarks | `ankus bench --pg 18` |
| Build and enter the development database | `ankus run --pg 18` |
| Connect without rebuilding | `ankus connect --pg 18` |
| Install or package the extension | `ankus install`, `ankus package` |
| Install into a `root`-owned PostgreSQL with `cargo pgrx install --sudo` | `ankus install --sudo` (Linux and macOS) |
| Run test servers as another account with `cargo pgrx test --runas postgres --pgdata /tmp/pgdata` | `ankus test --runas postgres --pgdata /tmp/pgdata` (Linux and macOS) |
| Create regression databases as another account with `cargo pgrx regress --runas postgres` | `ankus regress --runas postgres` (Linux and macOS) |
| Run SQL regression files | `ankus regress --pg 18` |
| Capture a target with `cargo pgrx cross pgrx-target` | `ankus target export --pg 18` on the target machine |
| Build with `PGRX_TARGET_INFO_PATH_PG18`, `PGRX_PG_CONFIG_AS_ENV` and `PGRX_PG_CONFIG_*` | `ankus package --target-info ankus-target.pg18.<rid>.zip`, or `dotnet publish -r <rid> -p:AnkusTargetInfo=...` |
| Name the shared library with Cargo's `[lib] name` | Set `AnkusLibraryName` |
| Install versions side by side by omitting `module_pathname` | Set `AnkusVersionedLibrary` to `true` |

The project SDK selects the matching headers, generated bindings and packaged
Native AOT runtime. `AnkusPostgresMajor` selects the project's PostgreSQL major;
an explicit `--pg` selects a command invocation. See the [CLI guide](/reference/cli/)
for each command's supported options and version selection, and
[native library names](/reference/build-settings/#native-library-names) for
library names and versioned libraries.

Rust's `#![no_std]` has no .NET counterpart: a Native AOT library always contains
the .NET runtime, which Ankus's managed boundary relies on. Native AOT feature
switches such as `InvariantGlobalization` remove optional runtime features; the
[minimal runtime sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.MinimalRuntime)
shows the generated declarations working without them.

When migrating an existing extension, preserve SQL identity and review its
on-disk representation separately. Generated C# storage is not a promise of
binary compatibility with a Rust serializer. Use an explicit codec or SQL
upgrade path when existing data must remain readable. Shared-preload libraries
and runtime servicing also require the deployment steps in the
[publishing](/getting-started/publishing/) and [.NET support](/reference/dotnet-support/)
guides.
