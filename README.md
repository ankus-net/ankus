<p align="center">
  <img src="assets/banner.png" alt="Ankus" width="640">
</p>

# Ankus

Ankus ports [pgrx](https://github.com/pgcentralfoundation/pgrx) to .NET Native AOT.
Define PostgreSQL functions, aggregates, operators, casts, triggers, custom types, and
configuration settings in C#.

Run background workers, share memory between backends,
and extend the planner and executor with native hooks and custom scan providers.
Write logical decoding output plugins and logical replication subscribers that
stream, apply and track changes. Access PostgreSQL's native APIs through
generated bindings. Publish it all as a native extension.

[Read the documentation](https://ankus.net/).

See [Ankus for pgrx users](https://ankus.net/getting-started/from-pgrx/)
for API translations, and [Running .NET inside PostgreSQL](https://ankus.net/reference/execution/)
for threads, signals, memory and backend lifetimes.

```csharp
[PgFunction]
public static int Add(int left, int right) => checked(left + right);

[PgFunction]
public static string Greet(string name) => $"Hello, {name}!";
```

See the [minimal sample](samples/Ankus.Examples.Hello/Hello.cs). Ankus generates
PostgreSQL module magic, exports, argument conversion, SQL declarations, and the
managed-to-native error boundary.

The [SPI sample](samples/Ankus.Examples.Spi/) demonstrates parameterized queries,
inserts, cursors, and table functions using named C# tuples.

The [errors sample](samples/Ankus.Examples.Errors/) ports pgrx's managed-failure
and PostgreSQL reporting scenarios, including ERROR, FATAL, and PANIC recovery.

The [arrays sample](samples/Ankus.Examples.Arrays/) demonstrates borrowed cells,
mutable copies, nullable elements, custom-type arrays and native array spans.
It also preserves pgrx's sequential and sixteen-lane floating-point accumulation
orders.

The [JSON sample](samples/Ankus.Examples.Json/) writes borrowed text and bytea
arrays directly into JSON documents. SQL NULL cells become JSON null, and bytea
cells become arrays of byte numbers. Text retains pgrx's compact JSON escaping,
including unescaped Unicode and HTML characters.

The [bytea sample](samples/Ankus.Examples.Bytea/) compresses arbitrary bytes,
decodes the first gzip member and reads text as strict UTF-8. It preserves
empty members, checksums and pgrx's member/trailer policy.

The [strings sample](samples/Ankus.Examples.Strings/) preserves full Unicode
lowercase, UTF-8 byte slicing and terminator-aware array, set and table results.

The [numeric sample](samples/Ankus.Examples.Numeric/) demonstrates full-range
arithmetic, PostgreSQL precision and scale, string parsing and exact signed
128-bit integer conversion.

The [datetime sample](samples/Ankus.Examples.DateTime/) demonstrates full-range
calendar arithmetic, timezone conversion, ISO formatting and PostgreSQL clocks.

The [schemas sample](samples/Ankus.Examples.Schemas/) places functions and
types in the installation schema, an extension-owned schema, `public` and
`pg_catalog`.

The [custom SQL sample](samples/Ankus.Examples.CustomSql/) orders bootstrap,
inline, file and final SQL blocks around generated schemas and types.

The [operators sample](samples/Ankus.Examples.Operators/) declares a manual
operator and generated equality, B-tree and hash operators for JSON-text and
packed native types.

The [composites sample](samples/Ankus.Examples.Composites/) creates, nests and
aggregates named composite types defined by custom SQL.

The [generic aggregate sample](samples/Ankus.Examples.GenericAggregates/) counts
changes in any input type with PostgreSQL's datum copy and comparison bindings.

The [memory contexts sample](samples/Ankus.Examples.MemoryContexts/) resets
scratch contexts, returns sets and keeps a background worker's counter in
`TopMemoryContext`.

The [shared memory sample](samples/Ankus.Examples.SharedMemory/) shares bounded
collections, lock-protected values and an atomic between preloaded backends.

The [try and catch sample](samples/Ankus.Examples.TryCatch/) maps pgrx's
`PgTryBuilder` to filtered `catch` and `finally` blocks, recovering from
PostgreSQL errors in a subtransaction.

The [subtransaction information sample](samples/Ankus.Examples.Subtransactions/)
reports a transaction ID's status, parents, nesting level and commit time.

The [threads sample](samples/Ankus.Examples.Threads/) computes on managed threads
and shows that only the backend thread can call PostgreSQL.

The [hooks sample](samples/Ankus.Examples.Hooks/) chains executor, parse-analysis
and utility hooks, installing each one exactly once.

The [notify sample](samples/Ankus.Examples.Notify/) wraps LISTEN, NOTIFY and
UNLISTEN, broadcasts cache invalidations from a trigger, and coalesces bulk
changes into one notification per category at commit.

The [PGLZ inspection sample](samples/Ankus.Examples.PglzInspect/) samples a column
and runs PostgreSQL's own compressor to recommend whether to enable PGLZ.

The [rewrite manipulation sample](samples/Ankus.Examples.RewriteManip/) changes
query-tree column references with PostgreSQL's rewriter utilities.

The [WAL decoder sample](samples/Ankus.Examples.WalDecoder/) is a logical decoding
output plugin that captures committed row changes as JSON.

The [type variants sample](samples/Ankus.Examples.TypeVariants/) compares five
ways to define a custom type's input, output and storage: generated JSON, custom
text, packed native bytes, a hand-written datum mapping and arrays of the type.

The [custom library name sample](samples/Ankus.Examples.CustomLibraryName/) names
its native library independently of its assembly and extension. The
[versioned library](samples/Ankus.Examples.VersionedLibrary/) and
[versioned custom library name](samples/Ankus.Examples.VersionedCustomLibraryName/)
samples publish one library per extension version, so versions install side by side.

The [bad ideas sample](samples/Ankus.Examples.BadIdeas/) shows how Ankus rejects,
converts or documents things an extension should not do, from swallowed PANICs
to exceptions on extension-created threads.

The [minimal runtime sample](samples/Ankus.Examples.MinimalRuntime/) is the .NET
counterpart of pgrx's `nostd` example: its declarations run with optional Native
AOT runtime features removed.

PostgreSQL 18 and later can report the library's name and version through
`pg_get_loaded_modules()`. See [native module identity](https://ankus.net/reference/build-settings/#native-module-identity)
for project defaults and attribute overrides.

## Develop and test

With the Ankus tool installed (`dotnet tool install --global Ankus.Tool`):

```console
ankus new Hello
cd Hello
dotnet test
```

This creates an extension and MSTest project. Tests call managed methods directly
and load the published Native AOT library into an isolated PostgreSQL cluster.
The version follows `AnkusPostgresMajor`, defaulting to 18. Plain
`dotnet test -p:AnkusPostgresMajor=17` selects matching headers and a PostgreSQL 17
server. `dotnet test -p:AnkusPgConfigPath=/path/to/pg_config` selects that exact
installation and infers its major when `AnkusPostgresMajor` is omitted.

Choose xUnit or NUnit with `ankus new Hello --test-framework xunit` or
`--test-framework nunit`. Both include managed and backend tests and use ordinary
`dotnet test`; MSTest remains the default.

You can also install the matching `Ankus.Templates` package from your configured
feed and use ordinary .NET templates:

```console
dotnet new install Ankus.Templates@0.2.5
dotnet new ankus -n Hello
cd Hello
dotnet tool restore
dotnet test
```

Use `dotnet new ankus-worker -n MyWorker` for a preloaded background worker.
Both creation paths pin the matching local tool in `.config/dotnet-tools.json`;
after restoring it, use `dotnet ankus` for extension commands.

The .NET templates accept the same `--test-framework xunit` or
`--test-framework nunit` option, including worker templates. Both creation paths
also accept `--framework net11.0` to target .NET 11; see
[.NET support](https://ankus.net/reference/dotnet-support/) for its status.

Use `ankus test --pg 17` for another registered PostgreSQL major, or
`ankus test --all` for every registered version. Pass ordinary test filters and
report options after `--`. See [test command options](https://ankus.net/reference/cli/#run-extension-tests).

Use `[PgBenchmark]` with `PgBencher` and run `ankus bench --pg 18` to measure
extension code inside PostgreSQL. Benchmark entry points remain outside normal
publications. `ankus bench --report` shows each benchmark's history against its
first run. See [benchmarks](https://ankus.net/benchmarks/) and the
[benchmark sample](samples/Ankus.Examples.Benchmarks/Benchmarks.cs).

For scripts, `ankus info path 18`, `ankus info pg-config 18` and
`ankus info version 18` print individual installation values. See
[installation information](https://ankus.net/reference/cli/#read-installation-values-in-scripts).

Add `--pgdata ./test-data` to choose the parent for isolated test-cluster data.
Each invocation cleans up its own child directory and preserves the parent.

After a successful test run, `--no-schema` retains its installation SQL while
recompiling function bodies. Native declaration changes require a normal run.

Declare `[PgTest]` methods to run C# checks inside PostgreSQL. The generated
catalog exposes individual cases to ordinary test discovery, including exact
expected errors and explicit ignore reasons. Test publications opt in; normal
publications exclude these SQL functions. See [backend tests](https://ankus.net/getting-started/testing/#declare-tests-inside-the-extension).
Inside one, `PgPropertyRunner` checks a condition against generated inputs and
shrinks a failing input, including one that raises a PostgreSQL error. See
[property tests](https://ankus.net/getting-started/testing/#property-tests).

Use `SearchPath = ["pg_catalog", PgSearchPath.ExtensionSchema, "pg_temp"]` on
`PgFunction` or `PgTest` to resolve names inside the extension's installation
schema. PostgreSQL fixes this path during installation, so the extension cannot
be relocated afterward. See [function execution options](https://ankus.net/function-declarations/#execution-options).

Extension projects use the `Ankus.Sdk` NuGet project SDK:

```xml
<Project Sdk="Ankus.Sdk/0.2.5">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AnkusExtensionName>hello</AnkusExtensionName>
    <AnkusExtensionVersion>0.1.0</AnkusExtensionVersion>
  </PropertyGroup>
</Project>
```

The SDK includes matching Native AOT compiler, runtime and source-generator
packages plus the native build helper, all restored from nuget.org.

Ankus targets .NET 10 LTS. `net11.0` is a preview on .NET 11 RC2 until .NET 11
ships. See [.NET support](https://ankus.net/reference/dotnet-support/)
for version selection, servicing, and rebuilding deployed extensions.

See [development and testing](docs/contributing/development.md) for prerequisites,
PostgreSQL discovery, and the integration harness.

Use `ankus init --pg18 download` to install PostgreSQL locally, or pass an existing
`pg_config` path to register it. PostgreSQL 13–19 have independent selections;
see the [command-line guide](https://ankus.net/reference/cli/) for source-build
prerequisites and platform distribution availability.

`ANKUS_HOME` selects the configuration directory when `--home` is absent.
`PG13_PG_CONFIG` through `PG19_PG_CONFIG` provide defaults for `init`; explicit
version options override them.

For `run`, `start`, `stop`, `status`, `connect`, `test`, `regress` and `bench`,
`PG_VERSION` selects a default major such as `18` or `pg18` before project
defaults. Explicit selectors override it. Other commands ignore `PG_VERSION`.
`DBNAME` supplies
`connect`'s default database, and `ANKUS_PGCLI=true` selects pgcli for `run` and
`connect`. See [environment selection](https://ankus.net/reference/cli/#install-or-register-postgresql)
for precedence and publication behavior.

Use `ankus start --pg 18`, `ankus status --pg 18`, and `ankus stop --pg 18`
to manage a persistent local development server. Stopping it preserves its databases.
On supported Unix systems, add `--valgrind` to inspect native memory under Memcheck;
see [Valgrind startup](https://ankus.net/reference/cli/#inspect-native-memory-with-valgrind).

`ankus run --pg 18` builds and installs your extension, then opens psql in its
development database. `ankus connect --pg 18` reopens that database without a build.

`ankus regress --pg 18` runs SQL regression files through PostgreSQL's own test
driver. See [SQL regression suites](https://ankus.net/reference/cli/#run-sql-regression-suites)
for setup SQL, expected output, filters, and repeated runs.
New extensions include the setup SQL and expected output under `pg_regress/`
beside the extension project.

Use `ankus upgrade --dry-run` to preview framework package and SDK updates.
See [upgrading references](https://ankus.net/reference/cli/#upgrade-ankus-references)
for version selection, shared package files, and imported project files.

`ankus package --pg 18 --output dist` builds an installation tree for distribution.
It places the native library and SQL files beneath `dist`, using the selected
PostgreSQL installation's directory layout.

On Linux and macOS, use `--prefix-dir custom/extension` to put the package's
library, control file and default SQL together beneath that output root.
Windows packages use `lib/` and `share/extension/`.

To build for another architecture, such as Intel macOS from Apple silicon, run
`ankus target export --pg 18` on the target machine and pass its archive to
`ankus package --target-info`. The archive carries that installation's headers,
`pg_config` values and measured native layouts; a pgrx target bundle holds only
the generated bindings. See
[building for another architecture](https://ankus.net/getting-started/publishing/#build-for-another-architecture).

Use `--configuration Shipping` to select a custom MSBuild configuration when
building, publishing, installing, or packaging an extension.

Use `AnkusExtensionControlFile` for extension comments, a fixed schema,
dependencies, and installation privileges. See [control settings](https://ankus.net/reference/build-settings/#extension-control-settings).
Version-specific overrides belong in `sql/<extension>--<version>.control`.
An authored `directory` setting selects a custom installation location for SQL
scripts and secondary controls.

Use `ankus get default_version` to query generated primary control settings.
`ankus get comment --from publish` reads an existing publication; see
[property queries](https://ankus.net/reference/cli/#query-extension-properties).

Keep SQL upgrade scripts in `sql/<extension>--<old>--<new>.sql`. Publishing,
installing, and packaging include them so existing databases can use
`ALTER EXTENSION ... UPDATE`. See [upgrading an extension](https://ankus.net/getting-started/publishing/#upgrade-an-existing-extension).

## Function types

Declare functions as synchronous static methods.
Compiler diagnostics distinguish `async` methods, task results and asynchronous
enumerators. See [backend threads and tasks](https://ankus.net/reference/execution/#backend-threads-and-tasks).
Editors offer corrections for declaration mistakes that have one clear fix, such
as **Make static**, **Make synchronous** or **Add the parameter** a trigger or
worker receives.

The generator uses these type mappings:

| C# | PostgreSQL |
|---|---|
| `bool` | `boolean` |
| `sbyte` | `"char"` (PostgreSQL's internal signed byte) |
| `short`, `int`, `long` | `smallint`, `integer`, `bigint` |
| `uint` | `oid` |
| `PgRelation` | `regclass` (checked relation reference) |
| `PgItemPointer` | `tid` (physical tuple location) |
| `float`, `double` | `real`, `double precision` |
| `string` | `text` |
| `Rune` | `varchar` holding one Unicode scalar value |
| `byte[]` | `bytea` |
| `PgTextView`, `PgByteaView` | `text`, `bytea` with checked native borrowing |
| `Guid` | `uuid` |
| `PgInet`, `IPAddress` | `inet` |
| `PgCidr`, `IPNetwork` | `cidr` |
| `PgPoint`, `PgLine`, `PgLineSegment` | `point`, `line`, `lseg` |
| `PgBox`, `PgCircle` | `box`, `circle` |
| `PgPath`, `PgPolygon` | `path`, `polygon` |
| `PgJson`, `PgJsonb` | `json`, `jsonb` |
| `decimal`, `PgNumeric` | `numeric` |
| `DateOnly`, `PgDate` | `date` |
| `TimeOnly`, `PgTime` | `time` |
| `PgTimeTz` | `timetz` |
| `DateTime`, `PgTimestamp` | `timestamp` |
| `DateTimeOffset`, `PgTimestampTz` | `timestamptz` |
| `TimeSpan`, `PgInterval` | `interval` |
| `PgRange<T>` | The supported built-in or explicitly mapped range type |
| `T[]`, `PgArray<T>` | Array of the corresponding scalar SQL type |
| `[PgEnum]` C# enums | Generated PostgreSQL enum types |
| `[PgType]` classes, structs, and enums | Generated PostgreSQL base types with CBOR, native struct storage, or an explicit storage codec |
| `[PgDatumType]` classes, structs, and enums | An existing SQL scalar representation with an explicit datum reader and/or writer |
| `PgHeapTuple` | `record`, or a named type using `[PgCompositeType]` |
| `PgAnyElement`, `PgAnyArray` | `anyelement`, `anyarray` |
| `PgArrayView` | `anyarray` with checked native cells and scalar spans |
| `PgArrayView<T>` | The concrete scalar element's array type, with checked lazy conversion |
| `PgCString`, `PgCStringView` | `cstring`, with exact owned or checked borrowed bytes |
| `PgDatum` with `[PgSqlType]` | The named PostgreSQL type |
| `PgInternal` | `internal` (backend callback state) |
| `void` result | `void` |

Use `PgArrayView<T>` in scalar, SETOF, TABLE and aggregate signatures, construct
it from a checked `PgDatum`, or request it through `Read<T>()`, SPI scalar helpers
or `PgFunctions`. It converts elements on access while preserving native type, shape and lifetime;
see [typed borrowed arrays](https://ankus.net/arrays/#typed-borrowed-cells).

Nullable value types and nullable reference annotations accept SQL NULL. Methods
with only required SQL parameters are declared `STRICT`. For mixed signatures, a NULL
required argument returns SQL NULL without invoking the method; nullable arguments
reach managed code. Nullable results become SQL NULL. Methods can share a SQL name
when their PostgreSQL argument types differ.

[`PgItemPointer`](https://ankus.net/item-pointers/) preserves exact tuple
locations, including non-NULL invalid values. `PgNativeItemPointer` supplies checked
ownership and borrowing for native `ItemPointerData` storage.

Temporal conversions preserve microseconds. `DateTime` requires `Kind.Unspecified`;
`DateTimeOffset` represents a UTC instant. Full-range `Pg*` types support PostgreSQL
infinities, BC dates, 24:00, second-resolution offsets, and separate calendar months
and days. They expose exact calendar fields and raw-value factories;
`PgTimeZone` resolves server timezone offsets at transaction start or a supplied instant.
See [date and time values](https://ankus.net/date-and-time/).

Text supports server-encoding conversion and Unicode; binary data preserves zero
bytes. `string` and `byte[]` copy values into managed storage; `PgTextView` and
`PgByteaView` provide checked native reads with private detoast or encoding
storage when needed. `PgCString` and `PgCStringView` preserve exact terminated
bytes for native C-string signatures. See [text and binary values](https://ankus.net/text-and-binary/)
for lifetime rules and independent copies.

Managed exceptions return completely to native code
before PostgreSQL raises ERROR. See [the native boundary design](docs/contributing/native-boundary.md)
for buffer ownership and error cleanup.

See [JSON and UUID values](https://ankus.net/json-and-uuid/) for JSON text ownership, document
access, and source-generated serialization with Native AOT.

Use `[PgType]` on a record, class, struct, or enum for a PostgreSQL base type.
Ankus generates its own serializer with CBOR storage, JSON text I/O, and direct
constructor/member access compatible with Native AOT. Nested records, nullable
members, arrays, lists, and string-keyed dictionaries retain their declared shape.
`Guid`, `DateOnly`, `TimeOnly`, `DateTime`, `DateTimeOffset` and `TimeSpan` members
use System.Text.Json's text formats.

Use `[JsonDerivedType]` and optional `[JsonPolymorphic]` to declare tagged variants
with their concrete types and inherited state preserved in both formats.

Set `TextCodec` to a `PgTypeTextCodec<T>` for custom SQL text with generated CBOR
storage. Add `NativeLayout = true` to store an unmanaged struct's own bytes, as
pgrx's `pgvarlena_inoutfuncs` does, with custom text and zeroed padding. Use `PgVarlena<T>` to borrow native-layout
inputs with checked lifetimes, copy-on-write mutation, explicit cloning and datum
transfer, or supply a `PgTypeCodec<T>` for both storage and text. See
[custom types](https://ankus.net/custom-types/).

Use `[PgDatumType]` for a reusable scalar wrapper over manual native storage or an
external SQL type. Its converter implements `IPgDatumReader<T>`,
`IPgDatumWriter<T>`, or both; generated callbacks and raw `PgDatum.Read<T>()`
select the declared CLR type. Its `T[]` and `PgArray<T>` forms reuse that converter
with exact element identity and checked array shape.

Generic declarations register
finite, fully constructed types selected by generated signatures, exact managed
type providers, or explicit `PgDatumType(typeof(ClosedType), name, converter)`
declarations. Explicit constructions can have independent SQL types and converters.
Open converter definitions such as `typeof(BoxConverter<>)` infer one closed,
constraint-checked factory from their exact reader/writer interfaces at compile time.

Value-type wrappers can also declare `[PgRangeType]` to use the same converter
for finite `PgRange<T>` bounds, with independent range SQL identity and ownership.
See [mapped range bounds](https://ankus.net/ranges/#mapped-bounds).
See [reusable scalar mappings](https://ankus.net/raw-values/#reusable-scalar-mappings)
for provider ownership, supported paths, and lifetime requirements.

The [range sample](samples/Ankus.Examples.Ranges/README.md) ports pgrx's range
constructors and stored-value example, including empty and unbounded ranges.

Use `[PgEnum]` and optional `[PgEnumLabel]` attributes for PostgreSQL enums,
including nullable values, arrays, typed SPI queries and schema dependencies.
See [enumerated types](https://ankus.net/enums/) and the
[enum sample](samples/Ankus.Examples.Enums/DeliveryFunctions.cs).

Use `[PgOperator]` for binary or prefix operators and `[PgCast]` for explicit,
assignment, or implicit conversions. Apply them to ordinary static methods or
C# operator and conversion declarations. Both generate backing functions and
dependency-ordered SQL. See [operators and casts](https://ankus.net/operators-and-casts/)
and the [operator sample](samples/Ankus.Examples.Operators/PriorityFunctions.cs).

Add `[PgEquality]`, `[PgOrdering]`, and `[PgHashing]` to custom types, enums, or
readable datum mappings to generate comparisons and default B-tree/hash index
classes. Equality and ordering use
`IEquatable<T>` and `IComparable<T>`; `IPgHashable` supplies a stable database hash,
with `PgHash` available for explicitly normalized keys. See
[generated operators](https://ankus.net/operators-and-casts/#generated-type-operators).

Add a `PgFunctionContext` parameter to inspect a call's collation, function and
result type OIDs, and raw SQL arguments. It adds no SQL parameter.
Its `GetOrCreateState` method caches managed state for each PostgreSQL call site
and disposes it when PostgreSQL releases the owner.

Use `PgSupportFunction` to connect a generated planner support method by its C#
declaration. Ankus validates its SQL signature and installation dependency; see
[planner support functions](https://ankus.net/function-declarations/#planner-support-functions).

Set `PgFunction.Sql` to replace a function's installation SQL, or
`GenerateSql = false` to retain its native entry points without installing it.
The same controls cover attached operators/casts and apply to trigger functions
and aggregate helpers. See [custom SQL](https://ankus.net/custom-sql/).

Use `PgRequires` and `PgBefore` with `typeof` and `nameof` to order SQL by its
managed declarations. Explicit dependency IDs remain available for custom SQL
blocks and attached declarations.

Types, enums, aggregate declarations and generated ordering/hash families also
provide these controls with their own declaration boundaries. Type replacements
can use native I/O tokens; family replacements retain their support functions
and operators.

Use `[assembly: PgSqlTypeProvider]` to declare types supplied by inline or file
SQL. Raw and composite signatures then follow their supplying blocks, including
manual shell/input/output/completion sequences with explicit prerequisites.

## Querying PostgreSQL

Return `IEnumerable<T>` from `[PgFunction]` for `SETOF T`, or named tuple elements
for `RETURNS TABLE`. Ordinary C# iterators support streaming, PostgreSQL-backed
materialization, and cleanup when a query stops early. See [sets and tables](https://ankus.net/sets-and-tables/)
and the [sets sample](samples/Ankus.Examples.Sets/SetFunctions.cs).

`PgHeapTuple` owns composite cells and immutable descriptor metadata, including
physical dropped slots, type modifiers, and domain identity. Named bindings,
anonymous records, nested arrays, and composite sets share the guarded native
conversion. See [composite values](https://ankus.net/composites/) and the
[composite sample](samples/Ankus.Examples.Composites/CompositeFunctions.cs).

Use `[PgTrigger]` with `PgTriggerContext` for row and statement triggers.
Owned OLD/NEW tuples, trigger arguments, transition-table SPI queries, `UPDATE OF`
and updated columns, and constraint trigger flags preserve PostgreSQL's
before/after/instead-of semantics. See [triggers](https://ankus.net/triggers/)
and the [trigger sample](samples/Ankus.Examples.Triggers/PetTriggers.cs).

Use `[PgEventTrigger]` with `PgEventTriggerContext` for DDL, dropped-object,
table-rewrite and login callbacks. Metadata snapshots remain owned after callback
return. See [event triggers](https://ankus.net/event-triggers/) and the
[event trigger sample](samples/Ankus.Examples.EventTriggers/DdlEvents.cs).

Use `[PgAggregate]` with typed static support methods for grouped, parallel,
moving-window, and ordered-set aggregation. `PgAggregateState<T>` owns managed
state through PostgreSQL's group and query lifetimes. `PgAnyElement` and
`PgAnyArray` support aggregates whose input, state, or result types vary by call. See
[aggregates](https://ankus.net/aggregates/) and the
[average sample](samples/Ankus.Examples.Aggregates/IntegerAverage.cs).

Implement `IPgAggregate<TState, TArgs>` for compiler-checked aggregate callbacks.
Optional interfaces supply finalization, parallel transport and moving windows.
Tuple argument groups become separate SQL inputs with independent nullability
and conversion metadata.

Use `Spi` inside an extension function to execute SQL in the calling backend:

```csharp
int answer = Spi.ExecuteScalar<int>("SELECT $1 + $2", SpiParameter.Create(40), SpiParameter.Create(2));
```

See [SPI queries](https://ankus.net/spi/) for typed parameters, result rows, and error handling.
PostgreSQL 13–16 parallel execution cannot use recovery subtransactions; a native
failure unwinds the managed callback before PostgreSQL aborts the operation.

Use `PgTypes.GetOid` for native type-name resolution and `PgQualifiedNameBuilder`
for exact operator lookup. See [catalog name lookups](https://ankus.net/catalog-lookups/)
for search paths, permissions and current catalog identities.

Use `PgRelation` to open and lock a relation, inspect live metadata, copy its tuple
descriptor, and open its indexes. Generated `regclass` arguments and results have
explicit reference cleanup, including arrays and iterators. See
[relation access](https://ankus.net/relations/) for ownership and locking.

`PgBuiltInOid` supplies typed native constants, and `PgOid` classifies values
against an explicit or active PostgreSQL major version while preserving invalid,
custom and built-in identity. `PgOid.ToDatum` maps the invalid tag to SQL NULL;
ordinary `uint` values continue to preserve zero.

The SDK generates `Ankus.Postgres` node declarations and their native type
dependencies from the selected server headers using Clang 20 or later and
matching `libclang`, preserving fields, enums, unions, arrays and bitfields.
Fields follow the selected installation, including changes between prerelease
snapshots; incompatible node tags or inheritance fail validation.

Anonymous structs and unions expose their promoted members directly on the
enclosing record, preserving C field access and shared storage.
Projects using the same measured contract share their native type identity.

The SDK defines `ANKUS_PG13` through `ANKUS_PG19` for the selected major so
consumer code can select version-specific declarations at compile time; see
[build settings](https://ankus.net/reference/build-settings/).

`PgNodes.Borrow` adds checked views over native storage, with tag-based casts that retain the original
bounds and lifetime. `PgNodes.DangerousAllocate` creates zeroed tagged storage;
`DangerousToNativeString` formats a valid native graph through PostgreSQL's guarded
boundary and returns owned text.

`NativeMethods` exposes selected-header fixed functions and helpers for alignment,
memory contexts, pages and tuples through the native error guard. `NativeGlobals`
provides guarded value copies and explicit native addresses for selected-header
globals. Data pointers retain their C# pointee types. Raw calls and global accesses
require an explicit unsafe context, including operations with scalar signatures;
checked APIs remain available in safe code.

Native function pointers have typed borrowed values whose `Invoke`
methods use the same native error guard. Method-table fields expose callback
types named after their record and field; global hooks also expose
`NativeGlobals_<Global>Callback` names independent of unrelated typedef aliases.

`[PgNativeCallback]` exposes a static
managed handler through a generated native function-pointer property, including
explicit hook installation, previous-hook chaining and restoration. Variadic
calls remain in progress.
See [native PostgreSQL declarations](https://ankus.net/raw-values/#native-postgresql-declarations).

`[PgOutputPlugin]` exports a static method as `_PG_output_plugin_init`, so the
library can serve as a logical decoding output plugin. The method receives
PostgreSQL's callback table and assigns `[PgNativeCallback]` properties. See
[logical decoding output plugins](https://ankus.net/logical-decoding/).

`PgReplicationConnection` connects from a backend to another server through
PostgreSQL's own `libpqwalreceiver`, as the built-in subscriber does, to run SQL,
copy tables and stream logical changes with no client library.
`PgRelationWriter` applies rows as the built-in apply worker does, and
`PgReplicationOrigin` tracks how far each source has been applied. They are part
of a toolkit for logical replication extensions such as pglogical, which pgrx
leaves to raw calls; see
[replication connections](https://ankus.net/replication-connections/) and
[applying replicated changes](https://ankus.net/applying-changes/). The
[subscriber sample](samples/Ankus.Examples.Subscriber/) uses them together to
copy and stream a publication's tables from another server.

`[PgCustomScan]` turns a class that implements `IPgCustomScan<TState>` into a
custom scan provider. Its static `Plan`, `Begin` and `Read` methods offer a path,
start a scan and produce rows; PostgreSQL still applies the query's conditions.
Paths can declare their row order, and optional interfaces add rescans, backward
reads, mark and restore, parallel scans over shared memory, and EXPLAIN
properties. Ankus generates the method tables and the planner hook.

The [custom-scan sample](samples/Ankus.Examples.CustomScans) has a managed
provider and a native one. The native provider uses these method tables directly
to trace actual sequential and index paths, child-plan execution, rescans and
EXPLAIN. Index children retain their supported backward and mark/restore
capabilities.

Parameter diagnostics follow partition ancestry without reevaluating
the child's clauses. Parallel scans combine observations in PostgreSQL-owned shared
memory using native atomics. See [custom scan providers](https://ankus.net/custom-scans/)
for registration, plan/state ownership and parallel lifecycle requirements.

Use `PgFunctions.Call<T>` to call a PostgreSQL function by name or OID, with typed
arguments, defaults, and ordinary PostgreSQL permissions. `CallRaw` returns a
context-owned datum with its exact type identity. Queries and catalog calls also
accept `PgAnyElement` and `PgAnyArray` results for types determined at runtime. See
[calling PostgreSQL functions](https://ankus.net/calling-functions/).

Use `PgFunctions.GetInfo` for an immutable function-catalog snapshot, including
argument types and modes, volatility, permissions-related flags, source and local
settings. `GetDefaultArguments(context)` materializes actual native expression
trees in an explicit owner without evaluating them.

`[PgDatumType]` readers also support `Call<T>`, SPI scalar-result helpers and
`SpiRow` or `PgHeapTuple` cells, with exact mapped type identity and detached
managed results. Rows keep cells without a managed conversion as exact copies.
`DangerousCall<T>` accepts mapped results when the caller supplies a valid native
address with the matching result type and ABI.

Use [logging and errors](https://ankus.net/logging/) to send PostgreSQL notices and structured diagnostics.
`PgSqlStates` supplies named SQLSTATE strings for reporting errors and writing
exception filters, while preserving support for extension-specific codes.

Use `PgTransactionId` for PostgreSQL `xid`; C# `uint` remains PostgreSQL `oid`.
It works in generated functions, SPI, and arrays, and can expand an `xid` with
the current server epoch. See [transaction IDs](https://ankus.net/transaction-ids/).

Use `PgTransaction.RunInSubtransaction` to group synchronous work with rollback
before an exception reaches the caller. Nested scopes recover independently.
Use `PgTransaction.RegisterCallback` for commit, rollback, preparation, and parallel
transaction phases. `RegisterSubtransactionCallback` observes savepoints and other
subtransactions with their exact IDs. Callbacks support cancellation, guarded SPI
before commit, nested dispatch, and managed cleanup. See
[transaction callbacks](https://ankus.net/transaction-callbacks/).

Use `PgMemoryContext` and `PgAllocation` for PostgreSQL-owned native storage,
temporary current-context scopes, checked byte access, and deterministic cleanup.
Declare a `PgMemoryContext` function parameter to receive a borrowed native context
without adding a SQL argument; set functions receive their multi-call owner.

Typed factories and span copies preserve unmanaged bytes; allocation options support
zeroing, explicit alignment, and PostgreSQL's huge size policy. `RunTransient` creates
and selects a child context, restores the caller, and attempts deletion on every exit.

`PgNativeBox<T>`, `PgContextValue<T>`, and `PgNativeReference<T>` distinguish
individual ownership, context ownership, and borrowed access to unmanaged values.
Borrowed Slab, Generation, and Bump contexts preserve their native allocation
restrictions; Bump storage is released only by context cleanup.

Reset and transaction cleanup invalidate managed handles before they can access
freed memory. `RegisterResetCallback` roots one-shot managed cleanup until the
native context resets or is deleted; its disposable registration supports cancellation.
See [memory contexts](https://ankus.net/memory-contexts/).

Use static `PgLwLock<T>` descriptors and `PgSharedMemory.Initialize` during shared
preload to share unmanaged values between backends. Disposable shared and
exclusive guards use PostgreSQL lightweight locks with checked callback
lifetimes. Their `Read` callbacks access original nested atomic and spinlock
fields; exclusive guards also provide `Mutate` callbacks for direct field and
bounded-collection updates. `Value` provides copied access and replacement.

`PgAtomic<T>` provides scalar reads, exchanges, comparisons
and integer updates across backends and managed threads with .NET `Interlocked`
semantics.

`PgShared<T>` gives scoped readonly access to immutable aggregates and
inline `PgAtomicValue<T>` fields. Inline `PgSpinLockValue<T>` fields provide
exclusive guards for very short updates; `PgSpinLock<T>` owns stable local
storage with the same guard API. Scoped guard reads operate on original nested
atomic and spinlock fields while preserving the parent's lifetime. Spinlock
guards also support scoped mutations without releasing the parent lock.

`PgFixedList<T>`, `PgFixedDeque<T>` and
`PgFixedMap<TKey, TValue>` provide bounded collections over unmanaged inline
buffers, including process-stable map keys. See
[shared memory, locks and atomics](https://ankus.net/shared-memory/).

Use `PgStringInfoStream` for a PostgreSQL-owned growable buffer with ordinary
stream writes, strict UTF-8 text, exact binary copies, checked context lifetimes,
and explicit native ownership transfer. See
[StringInfo buffers](https://ankus.net/stringinfo/).

Use `PgList<T>` for typed native PostgreSQL lists with ordinary collection
operations, checked context lifetimes, eager draining and explicit native
borrowing. Integer, OID, transaction-ID and opaque pointer cells preserve their
native identity. See [PostgreSQL lists](https://ankus.net/lists/).

Use `[PgInitialize]` on one static method to initialize the extension when its
library loads in a backend. Initialization supports guarded database access,
owned error diagnostics, retries after failure, and managed shared preload.
Use `[PgModuleLoad]` for native hook or provider registration that must run
before a parallel worker's first executor entry. It precedes `PgInitialize`,
which waits for worker restoration where required. See
[extension initialization](https://ankus.net/initialization/).

Use `[PgBackgroundWorker]` for a PostgreSQL worker process with a managed entry.
`PgBackgroundWorker` provides static and dynamic registration, lifecycle
observation, native signal/latch handling and guarded transaction callbacks.
Workers can attach the same shared-memory descriptors as ordinary backends.
See [background workers](https://ankus.net/background-workers/).

Declare PostgreSQL settings with `[PgGucBool]`, `[PgGucInt]`, `[PgGucReal]`,
`[PgGucString]`, or `[PgGucEnum]` on static partial getters. PostgreSQL owns their
storage, startup source priority, permissions, SET/RESET behavior, and transaction restoration. See
[configuration settings](https://ankus.net/configuration/) for typed hooks,
units, owned extra data, and shared preload.

An assembly `PgGucPrefix`
attribute checks unknown settings after registration. Hooks can use `PgLog` during reload,
rollback, and client reporting. Parallel workers restore typed values and regenerate
hook extra data in their own managed runtime. `PgSettings.Run` changes any setting
while a callback runs and restores it afterwards, as a function's `SET` clause does.

## Publishing and installation

Use the `ankus` tool to register PostgreSQL, publish an extension, and install its files:

```console
ankus init --pg18 /path/to/postgresql/bin/pg_config
ankus publish --project samples/Ankus.Examples.Hello --output artifacts/hello
ankus install --from artifacts/hello
```

See [tool setup](docs/contributing/development.md#build-the-tool) for local package installation
and the [command reference](https://ankus.net/reference/cli/) for options.

Publishing the sample produces a native library and these PostgreSQL installation files:

```text
Ankus.Examples.Hello.so                   # .dll on Windows; .dylib on macOS for PostgreSQL 16+
Ankus.Examples.Hello.sql
ankus.extension.json
extension/
    ankus_hello.control
    ankus_hello--0.1.0.sql
```

The native library also embeds its installation SQL and publication identity.
`ankus schema --from publish/MyExtension.so` extracts the full installation script
without loading the library or reading sidecar files. `ankus schema` builds the
selected project first; `--output schema.sql` writes the script to a file.

Generated SQL comments identify source files, lines, managed declarations and
dependencies. These comments remain available when extracting SQL from the
published library.

Pass declaration names to emit their SQL and dependencies. Add `--dot dependencies.dot`
to export the full graph. Selected scripts attach objects to an existing extension;
`--no-alter-extension` emits only their creation SQL.
`Ankus.PgConfig.ExtensionSchema.Read` exposes the metadata to .NET applications;
see [schema extraction](https://ankus.net/getting-started/publishing/#inspect-installation-sql).

`AnkusExtensionName` selects the extension name; its default is the assembly name
lowercased with periods replaced by underscores. `AnkusExtensionVersion` defaults
to the project's `Version`. The control file resolves the native library through
PostgreSQL's `dynamic_library_path`, whose default is `$libdir`.

`AnkusLibraryName` names the native library without changing the assembly, like
Cargo's `[lib] name`. `AnkusVersionedLibrary` follows cargo-pgrx's versioned
shared-object mode: each version publishes `<library>-<version>`, and its SQL names
that library, so versions install side by side. See
[native library names](https://ankus.net/reference/build-settings/#native-library-names).

For a standard server installation, the native library belongs in
`pg_config --pkglibdir`, and the control and versioned SQL files belong in the
`extension` subdirectory of `pg_config --sharedir`. Then PostgreSQL can run:

```sql
CREATE EXTENSION ankus_hello;
SELECT public.add(40, 2); -- 42
SELECT public.greet('PostgreSQL'); -- Hello, PostgreSQL!
```

See [PROGRESS.md](PROGRESS.md) for implementation status and platform validation.

## Documentation site

The Astro/Starlight site lives in `docs/`. See [site maintenance](docs/contributing/site.md)
for local preview and build commands.

The [public API reference](https://ankus.net/api/) is generated from C# XML
documentation. See [API reference generation](docs/contributing/api-reference.md).

## Contributing and security

See [CONTRIBUTING.md](CONTRIBUTING.md) to build and test Ankus and to propose a change.
Report a vulnerability privately as [SECURITY.md](SECURITY.md) describes.
