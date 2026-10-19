# Native datum boundary

Ankus generates both sides of the PostgreSQL/Native AOT boundary. The C wrapper
is compiled against the selected server's headers and linked into the extension's
native library. Extension authors write attributed C# methods.

The runtime's generated-code transport and bridge contracts live in
`Ankus.CompilerServices`. This includes `NativeValue`, `NativeCallError`,
`NativeBackend` and callback metadata. They remain hidden from ordinary
IntelliSense and are consumed by the matching generator and build packages.
The namespace does not change their native layouts, calling conventions or
ownership rules. Extension-author APIs remain in `Ankus`; generated PostgreSQL
types, `NativeMethods` and `NativeGlobals` remain in `Ankus.Postgres`.

## Declaration metadata

`FunctionDeclaration` resolves parameter names/defaults, the nearest containing
`PgSchema`, function-level overrides, and PostgreSQL execution options during
per-method semantic analysis. It never loads or executes the extension assembly. Identifiers
are quoted, UTF-8 length is checked, and generated string constants use escape
literals independent of `standard_conforming_strings`. Negative numeric
constants are parenthesized before casts so unary negation does not follow a
narrowing conversion.

Module identity, enums and schemas use immutable semantic models before rendering.
Their cached values contain no compiler symbols or syntax trees. Diagnostic source
coordinates are resolved against the current compilation, including mapped lines.
Enum and schema SQL fragments are cached separately from their graph policies, so
dependency-only edits still reorder installation SQL without rerendering unchanged
declarations.

Ordinary `PgFunction` scalar and iterator conversion contracts also cross an
immutable per-method boundary. Dispatcher C#, native entry bodies and export
declarations render in a separate tracked stage, which remains cached across
implementation edits and source movement. SQL-only execution options update the
current graph without invalidating these boundaries. The final compositor selects
initialization before native backend work, using the cached entry header and body.
Validation diagnostics resolve against the current source tree, even when a
failed conversion model remains unchanged. Removing declarations preserves the
surviving contracts; Roslyn can rerender a survivor when its positional input
entry changes.

The final compositor normalizes generated source and exports to LF on every
platform. Cached fragments may use the host newline convention; compare
composed boundaries using the final LF representation while retaining exact
initialization and cleanup assertions.

Ordinary function SQL declarations also retain immutable argument, execution
and schema contracts. Source movement preserves declaration equality, while
referenced planner constants and inherited schemas invalidate dependent values.
Validated argument types receive qualification from the current provider graph
during composition, so adding a provider still updates schema markers and
prerequisites after a declaration cache hit. SQL replacement, suppression and
dependency policies use detached authored values independently of native rendering.

Function DDL fragments render in their own tracked cache stage. Static CREATE,
argument/default, result-column and execution text is retained alongside typed
catalog fragments. Composition applies current provider ownership to those typed
identifiers and adds current planner-support references afterward. A provider or
support-reference change updates the resulting graph without rerendering the
unchanged function SQL fragments. Scalar, SETOF and TABLE shapes use the same
fragment grammar; specialized callback families share its complete-definition
formatter.

Row and event trigger signatures also validate per method into immutable invocation
and common SQL contracts. Their managed/native/export boundaries and zero-argument
SQL render separately, retaining cache entries after implementation edits and
source movement. Managed renames invalidate native entry identities, while inherited
schemas and execution options invalidate SQL independently. Current initialization
is inserted before native trigger work during composition; invalid signatures
report on the current source tree and disappear after repair.

Backend tests separate exact discovery metadata, partial catalog containers and
native invocation contracts. Catalogs render per declaring type in deterministic
managed signature order. Expected-error and ignore-reason changes invalidate only
discovery; schema changes invalidate discovery and SQL, while body edits and source
movement retain rendering caches. Native test inclusion remains a composition
setting, so ordinary host discovery is available in either publication mode.
Current diagnostics use source coordinates independently of these cache models;
no catalog generation or discovery executes extension author code.

Background-worker signatures, native export identities and managed targets also
cross an immutable per-method boundary. Worker dispatch and host guards render
independently of current source coordinates. Duplicate exports are selected from
the current inventory, with diagnostics attached to the current definition.
Partial declarations use their canonical definition once; worker-only extensions
retain the backend and memory helpers needed by their native host. Assembly
identity changes invalidate managed callback symbols while authored worker
exports remain stable.

A logical decoding output plugin initializer also validates into an immutable
invocation contract: its managed target, the generated `OutputPluginCallbacks`
record and an assembly-specific dispatcher symbol. Its managed dispatcher,
`_PG_output_plugin_init` export and linker entry render in a separately cached
stage that body edits and source movement retain. The export enters managed
code through `ankus_dispatch_native_callback`, so composition includes the
native callback boundary whenever an initializer is selected. Composition
reports duplicate initializers against current source trees and exports none
of them. The dispatcher checks the record's measured binding and the table's
alignment before calling the initializer.

Initialization and module-load phase validation now also produce immutable
invocation contracts. Their managed callback rendering is cached per canonical
method; module-load native snapshot and error guards share that cached boundary.
Current phase selection reports duplicate or invalid declarations against current
source trees and disables both phases after any phase error. Deferred native
initialization still composes current registration and capability requirements.
Removing an earlier syntax tree can recreate a surviving phase's positional
Roslyn input; the resulting artifacts retain the same invocation contract.

Mapped scalar and range registrations retain immutable catalog, converter and
capability values. Range registration carries its closed scalar bound separately
from the range's catalog identity and origin. Registration rendering neither
resolves PostgreSQL catalogs nor executes author code. Managed type providers
consume detached identities, including Roslyn's tuple-label distinctions, and
resolve diagnostic coordinates against the current compilation. Semantic mapping
discovery feeds a tracked rendering pipeline. Derived equality, ordering and
hashing helpers retain independent managed/native and typed SQL contracts;
composition still applies current provider ownership, initialization and graph
diagnostics.

Assembly SQL providers and managed declaration references also retain comparable
metadata without compiler symbols or attribute objects. References preserve
exact overload selection; providers preserve named versus managed constructors
and explicit schema arguments, including null. Current graph composition applies
ownership, support references and prerequisite edges after metadata cache hits.
Cached failures resolve against current source trees. Unresolved editor types
retain structural identities without assuming a containing assembly exists.

Compilation-wide capabilities detach the extension assembly identity, referenced
native callback marker and unique measured binding/layout constants. Binding
identity and node layout lookup render independently, so changing one does not
invalidate the other. Missing or ambiguous companions and nonconstant or
duplicate editor fields retain the fail-closed native checks. No metadata
inspection loads or executes a referenced assembly.

Native callback properties also detach their exact invocation types, native
prototype index, assembly-scoped registration identity and partial declaration
containers before rendering. The managed property and dispatcher fragment is
cached per property. Implementation edits and source movement preserve that
fragment; signature or lexical contract changes invalidate its dependent output.
Invalid declarations report against current source trees, and composition removes
deleted properties. A removed positional input can recreate the surviving entry;
its exact source contract remains equal. Binding checks, native addresses and
borrowed lifetimes remain runtime operations on every property access and call.

The native callback's owned diagnostic header lives in `TopMemoryContext` and
is released in `PG_FINALLY`. Reporting hooks enter from `ErrorContext`, which
PostgreSQL resets during a recursive ERROR. Allocating the header there would
invalidate it before callback cleanup could release its managed-owned fields.
The dispatcher also retains the caller's context identity and restores a
surviving context after unwinding instead of dereferencing a deleted child.

Assembly SQL declarations also detach authored constants, graph policies and
current diagnostic coordinates. Per-block file selection uses tracked
AdditionalFiles and the compiler-visible project directory. Unchanged selected
text retains cached validation after unrelated file edits, directory aliases or
source movement; changed selected content invalidates only dependent blocks.
Graph composition still applies current dependencies, ordering, relocation,
provider ownership and authored SQL-file provenance. Invalid cached text reports
against the current source tree and prevents a partial installation script.

Configuration properties validate into comparable registration, property,
enum and hook contracts. Native descriptors, managed dispatchers and partial
getters render per property. Real constants compare by their bits so changing
the sign of a zero default or bound invalidates its native descriptor. Current
diagnostic coordinates remain separate from rendered values; duplicate names
and registration order use PostgreSQL's ASCII comparison. Assembly prefixes
retain their literal case-sensitive inventory and cache native registration
separately. These caches never retain live backend setting values. Removing an
earlier declaration can recreate a surviving positional Roslyn input while
preserving its exact rendered contract.

The current method inventory also retains immutable selection names, exact
overload identities, role markers, source coordinates and sequence/raw transport
requirements. Ordinary and alias-only backing functions, triggers and backend
tests reuse their validated rendering stages during composition. Initialization
and worker callbacks retain their separate pipelines and existing conflict
diagnostics. Graph registration uses detached method identities and provenance;
it no longer needs raw method symbols.

The final compositor freezes named declaration inputs and a comparable
map of only relevant source coordinates. Graph validation uses transient,
tree-free locations and captures original diagnostic descriptors, arguments and
tree ordinals. Final reporting binds those coordinates to the current compiler
trees, including mapped lines. Separate trees with identical physical paths and
spans remain distinct. SQL provenance retains physical file and line comments.

Semantic graph validation uses declaration-relative anchors without physical
line numbers. Current physical lines attach only to final SQL provenance, while
diagnostics resolve their current mapped coordinates independently. Editing
leading lines or an earlier function body therefore preserves native and managed
composition. Immutable source trees retain their declaration indexes, so an edit
does not retraverse every unchanged tree to resolve locations.

GUC and derived equality, ordering and hashing declarations use Roslyn's semantic
attribute indexes, including aliases. Assembly SQL providers detach their inputs
before composition. Native callback capability reads are independently cached per
metadata reference, without loading dependency assemblies; compilation references
retain their equivalent compiler metadata path.

Managed dispatchers, native C and linker exports retain ordered cached fragment
plans before final rendering. Their tracked rendering stages can reuse unchanged
plans after body, source-coordinate or SQL-only policy edits. Manifest rendering
combines separately cached native/export source with current installation and
graph metadata. Installation order freezes after ownership, dependency and cycle
validation. Portable SQL provenance renders per connected component; installation
joining and bounded graph encoding use independent caches, so changing measured
ABI constants does not rerender unchanged SQL. Encoding failures retain their
original diagnostic and prevent dispatcher/manifest publication while preserving
the established auxiliary source behavior. The complete PostgreSQL 18.6/Linux
x64 suite verifies these caches alongside real Native AOT extension execution.
Full platform CI and the remaining PostgreSQL/platform matrix are still required.

Operator and cast attributes also detach validated catalog identities, typed
operand/result fragments and authored dependency policies. Alias-only backing
functions share the ordinary conversion validators and render independently.
Current type-provider ownership is applied to typed SQL fragments during graph
composition, so provider or dependency edits do not rerender unchanged catalog
grammar. Current diagnostics and duplicate inventories remain composition work.

Custom types validate into immutable storage and I/O contracts. Recursive
serialization graphs use node indexes, retaining exact nullable members, selected
constructor order, enum names and polymorphic discriminator identities without
holding compiler symbols or cyclic model objects. Registration, codec rendering,
native catalog checks and scalar I/O use separate tracked inputs. A member or tag
change regenerates its codec while unchanged registration and I/O remain cached;
binary, alignment and null-input policy changes affect I/O independently of storage.
Current graph options, source diagnostics and extension initialization compose
outside those cached fragments. The storage representation and direct calls are
preserved. Removing an earlier declaration can recreate a surviving positional
Roslyn input while preserving its exact rendered contracts. Other declaration
families and the final output compositor remain in progress.

Aggregate capabilities also validate into immutable contracts. Each selected
support role caches its constrained managed call, owned-state conversions and
native header/body independently. Aggregate DDL and support-function SQL have
separate typed projections: numeric conversion changes affect the selected
boundary, while catalog names and execution policies affect the relevant SQL.
Provider ownership, authored dependencies, SQL replacement, shared support
identities and current initialization compose afterward. Diagnostics resolve
against current source trees. Inherited generic support methods reserve their
open definitions from ordinary function discovery; closed identities still
distinguish published support functions. Final graph composition and output
rendering use the independent cached stages described above.

Datum mapping discovery validates finite closed roots before immutable scalar
and range registration contracts cross the compiler boundary. Converter factories
remain lazy; registration rendering reads no compiler symbols or live catalog
values. Range identities and capabilities remain independent of their scalar
bounds. Provider matching preserves exact managed identities, including tuple
labels, while current provider dependencies compose after registration cache hits.
Derived equality, ordering and hashing helpers independently cache interface-call
and conversion boundaries. Their SQL grammar uses a separate catalog projection;
typed fragments apply current provider qualification without rewriting authored
text. Initialization, SQL replacement, dependencies and diagnostics compose outside
the cached fragments. Removing an earlier positional input may recreate surviving
Roslyn inputs while preserving their exact contracts. Final graph composition and
complete extension outputs use their own independently cached stages.

Schema creation precedes function DDL. Fixed schemas set `Ankus.Relocatable` to
false in assembly metadata; `ExtensionManifest` reads that with `PEReader`, and
`ExtensionPackage` writes the corresponding control-file flag. A schema-only
extension still emits module magic and a manifest, without managed dispatchers
or their native call dependencies.

The native export manifest retains the embedded schema section during linking.
On Windows it declares the schema symbol as `DATA`; the C declaration selects
the section without adding a second export directive.

`SqlGraph` unifies generated schemas/functions and assembly `PgSql`/`PgSqlFile`
blocks. Schema-to-function edges are automatic; explicit `Id`, `Requires`,
and `Before` references resolve before deterministic topological sorting.
Bootstrap/final nodes gain edges to every other node. Duplicate/missing IDs,
contradictory ordering and cycles fail generation rather than emitting a partial
script. Schema aliases share a single creation node.

SQL files flow through Roslyn's `AdditionalTextsProvider`, including their
content, so file-only edits invalidate incremental generation. Paths resolve
against the SDK's compiler-visible `MSBuildProjectDirectory`; no untracked disk
reads or runtime assembly execution are used. Custom SQL is emitted verbatim
with a final newline to terminate trailing line comments. PostgreSQL validates
and executes it transactionally during installation.

Network values use PostgreSQL's binary send/receive functions behind the native
guard. The wire family byte is normalized to 4/6; the native bridge maps it to
the selected PostgreSQL headers' `PGSQL_AF_INET`/`PGSQL_AF_INET6` values. Remaining
bytes carry the prefix, inet/cidr marker, address length and network-order address.
No native structure layouts or platform socket-family constants enter managed
storage. The receive path validates framing and lets PostgreSQL enforce prefix
and cidr host-bit rules. Send buffers belong to the same per-call/per-operation
ownership tracking as other buffered datums.

`PgInet` owns numeric address bits rather than a mutable `IPAddress` reference.
`PgCidr` enforces zero host bits, and both share the existing statically closed
array and SPI conversions. Network parsing uses an allowlisted scalar family
inside the subtransaction; mask, comparison and formatting operations run on
detached managed values.

Geometric transport uses PostgreSQL's network-order binary protocols rather than
managed copies of native structs. Fixed shapes carry doubles; path/polygon frames
carry a vertex count and, for paths, a closure byte. Both sides validate framing
before allocating point arrays. Variable-length inputs are explicitly detoasted
and tracked before the send routine runs, so PostgreSQL send functions cannot
leave hidden detoast copies alive for the rest of a large SPI operation.

The native writer delegates nonempty shapes to the matching receive function,
including line/circle validation. pgrx also permits empty owned paths/polygons,
which PostgreSQL's receive functions reject. For exactly zero vertices, the
bridge creates a zeroed header using `offsetof(PATH, p)` or `offsetof(POLYGON, p)`
from the selected server headers. Empty polygon bounds remain zero. The bridge
does not rely on a managed native-header layout.

Range transport carries a built-in range OID and flags, followed by up to two
scalar transport records. The outer `auxiliary1 = -2` marker distinguishes it
from arrays. Bounds are length-delimited and pointer-free, including numeric text
payloads; absent bounds and empty ranges have no bound records. Native readers
detoast the range before `range_deserialize`, and native writers use the selected
server's type cache and `make_range` for validation and canonicalization. Numeric
bound payloads are explicitly terminated before calling scalar input routines.

Range operations use the scalar dispatcher's initialized `FmgrInfo`, which
PostgreSQL needs for its per-call type-cache pointer. Parsing and formatting use
OID-based input/output invocation for the same reason. Neither path lets a
PostgreSQL error unwind through a managed frame.

Named, defaulted, variadic and security-definer calls use the same native ABI.
PostgreSQL resolves defaults, assembles variadic arrays, applies function-local
settings and privileges, and suppresses STRICT calls before dispatch. Required
parameters retain the native NULL guard even in mixed nullable signatures.

## Call sequence

### Library initialization

`[PgInitialize]` generates a native `_PG_init` and a separate managed dispatcher.
The wrapper supplies the guarded SPI callback only when `IsTransactionState()` is
true. Configuration reads and logging remain available without a transaction.
Nested scopes restore the enclosing bindings and callback depth.
Session preload has a transaction before it has a portal or active snapshot.
The initializer pushes a transaction snapshot only when none exists, and pops
only its own snapshot after managed return or native failure. Existing caller
snapshots remain untouched. This permits guarded startup SPI and leaves
PostgreSQL's initial transaction with its original snapshot stack.

On Unix, managed shared preload calls the patched runtime's fork checkpoint after
registration and initialization finish. The runtime stops its own workers before
PostgreSQL forks and repairs them in each backend. Windows uses PostgreSQL's
`EXEC_BACKEND` process startup and does not use this checkpoint.

PostgreSQL's loader records a library only after `_PG_init` returns successfully.
A native three-state guard detects recursive loading before it can recurse
through the same managed initializer. Failure resets the state so a later load
can retry; success retains the initialized state for the process lifetime.
Diagnostics use the ordinary owned error transport. Managed exceptions return to
native code before error reporting, so `finally` runs normally. SQL mutations
follow PostgreSQL rollback, while managed static mutations survive failed attempts.

Initialization-only assemblies emit module magic, native dependencies, exports,
and a manifest even when their installation SQL has no function declarations.
The initialization callback itself never creates a SQL function.

### SQL function dispatch

1. PostgreSQL invokes the exported C wrapper. The wrapper checks argument count
   and required argument nullability before allocating or invoking managed code.
2. Native argument conversion uses PostgreSQL macros, detoasting, and server-to-UTF-8
   conversion. These operations can raise PostgreSQL ERROR before entering the
   current managed callback. Recursive SPI calls catch these errors in the
   enclosing native SPI guard.
3. The wrapper invokes an assembly/method-specific Cdecl managed callback. The
   callback copies borrowed text and binary buffers into managed objects, invokes
   the user's method, and converts its result to the transport structure.
4. The callback catches managed exceptions, fills the caller-owned error transport
   with owned diagnostics and a bounded fallback, and returns status. No exception crosses the ABI.
5. After managed dispatch returns, the wrapper frees temporary input buffers. If
   the callback failed, the wrapper raises PostgreSQL ERROR. `PgException` supplies
   SQLSTATE, message, detail, and hint; other exceptions use SQLSTATE `38000`.
6. The wrapper converts successful results into PostgreSQL datums. A native
   `PG_TRY`/`PG_FINALLY` block invokes the result buffer's release callback on both
   success and PostgreSQL ERROR, including output encoding and allocation errors.

The `PG_FINALLY` release callback only frees Native AOT-owned native memory. It
does not call PostgreSQL or execute extension user code.

Scalar and set dispatch also receive the current `FunctionCallInfo` pointer.
When a method declares `PgFunctionContext`, generated code captures it through
the native guard before invoking the method or iterator factory. Capture reads
types and collation through PostgreSQL's expression helpers and copies raw
arguments into the current context. It never retains the call pointer. Set
arguments use the multi-call owner directly, preserving access during iterator
reset cleanup before the owner's generation is invalidated. Metadata becomes an
immutable managed snapshot; datum access validates the native owner and generation.

## Ownership

| Value | Owner and lifetime |
|---|---|
| Original input datum | PostgreSQL; borrowed for this invocation |
| Detoasted or encoding-converted input | PostgreSQL allocation; explicitly freed after dispatch when a new allocation was returned |
| Managed input string/array | Managed copy; independent of the PostgreSQL input buffer |
| Injected function-context arguments | PostgreSQL copies; callback or multi-call context with checked reset generation |
| Cached managed function state | Rooted per call site; disposed and unrooted when `fn_mcxt` resets or is deleted |
| Managed output string/array | Managed object copied into a native transport buffer |
| Native output buffer | `NativeMemory.Alloc`; released through a callback into its allocating runtime |
| Final output datum | PostgreSQL's current memory context |

On input conversion failure, PostgreSQL's error cleanup owns native input
allocations. Output allocations owned by Native AOT require the explicit release
callback: PostgreSQL's memory contexts cannot reclaim them. Keeping allocation
and release in the same runtime also avoids crossing C runtime heaps on Windows.

Function state uses a native registry keyed by `FmgrInfo`, its `fn_mcxt`, and
function OID. Monotonic identities prevent recycled native addresses from
reviving old managed entries. Native reset callbacks retire those identities;
managed reset callbacks release their values, including after a throwing
`IDisposable.Dispose`. The registry leaves `fn_extra` available for PostgreSQL's
set-returning function machinery. State can therefore outlive individual
iterator instances without replacing their `FuncCallContext`.

`StateMemoryContext` validates the original owner and reset generation before
returning a borrowed handle. Cached native values must use that owner explicitly;
the factory does not change the current allocation context. Initialization is
transactional in managed code: a failed factory leaves the entry empty and
retryable, recursive initialization fails, and a successful null is still cached.
During native cleanup, existing iterator state remains readable but new state
cannot be initialized.

`PgFunctions` invokes scalar catalog functions through a native `FuncExpr` and
PostgreSQL's expression executor. Name lookup uses the parser's overload and
variadic rules; OID lookup builds the declared argument list directly. Default
expressions, polymorphic types, and implicit argument coercions are resolved
before execution. An explicit EXECUTE check precedes planning so constant folding
cannot bypass permission checks for a strict NULL call. The executor supplies
`FmgrInfo`, function-local settings, security-definer behavior, and nested call
metadata. The existing subtransaction guard rolls back errors before returning
owned diagnostics to managed code.

Return types are checked before calling user code. Managed results are copied
before `FreeExecutorState`; raw results are copied into the caller-selected
memory context. The explicit native-address API follows pgrx's direct-call
contract with a null `flinfo`, `context`, and `resultinfo`. Its caller supplies
the correct argument/result ABI and NULL handling, while the native guard still
contains PostgreSQL ERROR.

Generated polymorphic signatures resolve input OIDs with `get_fn_expr_argtype`
and output OIDs with the function expression or resolved set row descriptor.
Managed wrappers copy raw inputs into the active callback or iterator owner.
Their result envelopes carry the actual type and context generation; native
conversion validates both before copying the result. Domain constraints apply
to NULL returns too. Array snapshots retain exact dimensions, lower bounds,
element identities, and nullable raw cells under that same checked owner.

Typed SPI scalar and tuple results use raw first-row capture when a requested
column is polymorphic. Ordinary columns retain their existing managed conversion;
polymorphic datums are copied to the callback's result owner before temporary raw
storage is deleted. Catalog calls validate array results before execution and
copy their resolved type and value before releasing executor storage.
Raw-row `Get<T>` and datum `Read<T>` wrappers share the source generation.

## Representation and encoding

`NativeValue` and generated `AnkusValue` use matching sequential fields: 64-bit
integral payload, two 32-bit auxiliary fields, a 32-bit interval-infinity
discriminator, data pointer, 32-bit length, one-byte null flag, and release
function pointer. The integral payload carries integers, booleans, OIDs, or exact
IEEE-754 bits. A `float` is not widened to a `double`, preserving NaN payloads and
signed zero.

Text buffers are length-delimited UTF-8. PostgreSQL handles conversion to and from
the database's encoding. Managed conversion rejects unpaired UTF-16 surrogates;
PostgreSQL text results reject embedded zero characters. Binary buffers preserve
all byte values. NULL and an empty buffer are distinct values.

`pg_detoast_datum_packed`, `VARDATA_ANY`, and `VARSIZE_ANY_EXHDR` handle one-byte,
four-byte, compressed, and external varlena forms. Managed code never assumes the
input datum is aligned or has a particular header size.

UUID buffers contain exactly sixteen network-order bytes. Managed `Guid` conversion
uses the explicit `bigEndian` overloads in both directions. PostgreSQL allocates
the final `pg_uuid_t` in its current memory context.

JSON uses its original text; JSONB is detoasted with alignment preserved and
serialized by native `jsonb_out`. Both travel as UTF-8 text in owned or borrowed
buffers. JSONB serialization and encoding buffers are released alongside detoasted
inputs. Managed wrappers validate and retain the text without numeric coercion.
Native `json_in` / `jsonb_in` construct results and SPI parameter datums, enforcing
the server's syntax, numeric, Unicode, and encoding restrictions. Result-conversion
errors occur after managed dispatch has returned; parameter-conversion errors
remain inside the SPI guard. Both paths release allocator-matched transport buffers.

### Arrays

The outer transport marks an array with `auxiliary1 = -1`. Its single contiguous
buffer contains no pointers: big-endian 32-bit rank, count, and scalar element OID;
one length/lower-bound pair per dimension; then an element header and payload for
each row-major value. Each 28-byte element header contains the integral bits,
auxiliary fields, infinity discriminator, NULL flag, and payload length.

`DatumGetArrayTypeP` handles array detoasting, and `deconstruct_array` uses the
server's element length, alignment, and by-value metadata. Domain elements resolve
to their base type for scalar conversion. Each element reuses the scalar reader,
then releases its PostgreSQL-owned temporary buffers. SPI copies the complete
array transport into one malloc-owned cell buffer, preserving existing result
cleanup even if a later cell fails.

Managed decoding validates shape, bounds, count, lengths, and NULL flags before
creating the requested typed storage. No object-array intermediate or runtime
generic construction is needed. Native output validates the envelope and uses
`construct_md_array` after scalar conversion. Text/binary payloads stay
length-delimited; only JSON/JSONB/numeric input routines receive a terminated
copy. Using a C-string copy for binary elements would truncate embedded zero bytes.

All PostgreSQL allocation and element conversion stays in native frames. SPI
operation contexts reclaim partial arrays on failure. Generated return wrappers
release the one managed transport buffer in `PG_FINALLY`, including failures in
later elements. No per-element malloc pointers need to be recovered across longjmp.

C# vectors accept only empty or one-dimensional, lower-bound-one input. `PgArray<T>`
preserves all dimensions. `params T[]` changes the SQL declaration to `VARIADIC`;
PostgreSQL still supplies one array datum to the same generated wrapper.

## Managed-to-PostgreSQL calls

`Spi.Execute` enters a native function pointer supplied by the generated wrapper.
The binding is thread-local and scoped to that callback. Recursive dispatch saves
and restores the enclosing binding. Calls on worker threads or outside a backend
callback fail in managed code before entering PostgreSQL.

The native SPI guard starts an internal subtransaction and runs the command with
`SPI_connect`, `SPI_execute` or `SPI_execute_with_args`, and `SPI_finish`. Typed
parameters are converted under this guard. On success, it releases the
subtransaction and returns the final statement's processed-row count and any
requested results. Changes remain part of the calling transaction.

Result metadata and cells are copied into native-owned buffers before SPI
disconnects. The runtime materializes managed `SpiResult`, `SpiRow`, and `SpiColumn`
objects, then releases all native result allocations in a managed `finally` block
through their allocator-specific callback. The callback uses only native `free`;
it does not enter PostgreSQL. A native conversion failure releases partially copied
results and rolls back the command. Managed parameter buffers are also released
in `finally` on success, native failure, or managed conversion failure.

Scalar execution copies only the first cell but lets PostgreSQL execute the full
command. Explicit query row limits are passed to SPI separately, so reading a
scalar does not truncate the effects of a command with `RETURNING`.

The SPI entry point accepts a sequential request structure containing the operation,
borrowed command/parameters, execution options, and any owned plan handle. Statement
preparation uses `SPI_prepare` followed by `SPI_keepplan` under the same native guard.
The managed owner is allocated before native preparation, and the kept plan is freed
if the native operation subsequently fails. Execution uses `SPI_execute_plan` and
shares the existing parameter conversion and result-copy path.

`SpiPreparedStatement.Dispose` calls guarded `SPI_freeplan` on the backend thread.
The request clears its handle when ownership is consumed, and the managed owner
observes that update even if a later operation fails. An active-execution count
prevents reentrant disposal while permitting recursive execution. No PostgreSQL
cleanup runs on the finalizer thread. Saved plans have explicit managed disposal;
PostgreSQL retains them in its cache memory context across transaction boundaries.

`Spi.Connect` opens a scoped native SPI connection and closes it in a managed
`finally` block. Native opening retains an internal subtransaction for the scope
so even a partially completed `SPI_connect` can be recovered by rollback. Each
operation within the connection gets a nested subtransaction. Closing releases
the connection and scope subtransaction, restoring the original memory context,
resource owner, and nesting level. Successful commands remain in the enclosing
transaction when the callback exits.

Native session identities are registered in the connection's procedure context;
a memory-context callback removes the registration on cleanup. Managed ownership
checks require the same dispatcher depth and innermost active session, preventing
reentrant code from operating on the wrong native SPI stack frame. Native
operations also validate the session identity before using a plan pointer.

Session-bound plans use `SPI_keepplan` to participate in PostgreSQL's invalidation
list. A native session-owned list frees these plans when its procedure context is
deleted. This differs from unsaved SPI plans, which do not receive relation
invalidation notifications. `Keep()` detaches a plan from session cleanup;
explicit disposal removes a scoped plan from the list before freeing it. A
partially prepared or retained plan is freed by native error recovery. Result
tuple tables are released immediately after copying, so a long-lived session
does not accumulate already materialized batches.

Scoped operations allocate temporary parameters and conversion buffers in an
`Ankus SPI operation` memory context. Success deletes that context before releasing
the operation's subtransaction; failure reclaims it during rollback. This is
necessary because PostgreSQL keeps nonempty `CurTransactionContext` children
after subtransaction commit. Long-lived plans, portal state, and session cleanup
registrations live in their respective owning contexts.

Quoting helpers share the error guard but invoke `quote_identifier`,
`quote_qualified_identifier`, or `quote_literal_cstr` directly, without opening a
SPI connection. Their input conversion and quoted output use the disposable
operation context; the returned UTF-8 fragment is copied into allocator-matched
native result storage before the context is deleted.

EXPLAIN uses the same typed parameter and result conversion as queries. Native
`pg_parse_query` verifies that the prefixed `EXPLAIN (FORMAT JSON)` command has one
statement before SPI executes it. The result is an independently owned `PgJson`.

Managed `SpiRow` edits replace local values and lazily allocated per-cell type OIDs.
The original query's shared column metadata remains available separately. Row
access and mutation use owned managed data and require no PostgreSQL calls.

Nonterminal `PgLog` reports use the same native guard and disposable operation
context. `ThrowErrorData` applies PostgreSQL's routing and context callbacks;
encoding failures return to managed code as `PgException`; query cancellation
uses `PgQueryCanceledException` with the complete owned PostgreSQL diagnostic.
`IsEnabled` uses `message_level_is_interesting` on PostgreSQL 14 and newer, with
the corresponding routing checks for PostgreSQL 13. Managed severity values map
to header constants rather than relying on version-specific numbers.

ERROR is a managed `PgException`. FATAL and PANIC use an internal exception that
carries severity and diagnostics to the generated dispatcher. Before throwing,
the managed logger records an independently owned copy in the native callback
frame without invoking PostgreSQL error reporting. After managed unwinding,
the native wrapper reports that level even if the exception was caught. Nested
recovery frames propagate terminal reports to their parent before rollback.
Managed cleanup may still emit nonterminal reports and close its SPI session.
Tests use
dedicated clusters to verify connection termination and crash recovery.

Cursor operations share the native guard and result-copy path. Opening uses
`SPI_cursor_open_with_args` or `SPI_cursor_open` for prepared plans. Managed cursor
objects carry monotonically assigned identities and copied names rather than
native portal pointers. A native registry entry lives inside the portal's memory
context. Its `MemoryContextRegisterResetCallback` callback unlinks the entry before
the context is reclaimed, including on SQL CLOSE, transaction end, and rollback.
Lookup validates the identity before accessing the portal. Recreating a portal with
the same name or address cannot revive a stale identity.

Fetch direction and count are passed to `SPI_cursor_fetch`; returned cells are copied
before disconnecting SPI. Disposal uses `SPI_cursor_close` when the identity is live
and is harmless when PostgreSQL has already removed the portal. Detach transfers
ownership to the caller retaining the portal name. Managed reentrant operations on
a currently fetching cursor are rejected before entering PostgreSQL. Registry cleanup
is entirely native and does not retain managed objects or invoke managed callbacks.

On PostgreSQL ERROR, native `PG_CATCH` copies diagnostics outside the failing
subtransaction, flushes the error state, rolls back to the caller's transaction
nesting level, and restores the memory context and resource owner. It returns
owned UTF-8 diagnostics to managed code, which throws `PgException`. Managed
`catch` and `finally` blocks therefore execute normally. An extension can catch the
exception and issue another SPI call, or let its generated dispatcher return the
error to PostgreSQL.

SQLSTATE `57014` is an exception to ordinary catch-and-continue behavior.
PostgreSQL has already cleared its pending cancellation when it raises the
error. Every guarded capture therefore retains cancellation on the active
native entry frame, independently of the managed exception. Subsequent server
calls return the retained diagnostic, and callback completion returns failure
even when managed code swallows or replaces the exception. An explicit
subtransaction still rolls back before returning the cancellation; its caller's
frame retains it until the outer callback returns. This preserves managed
`finally` execution without allowing canceled queries to report success.

Recovery itself has a native guard. An unrecoverable error during recovery
terminates the backend with FATAL rather than jumping across managed frames.

A guard also preserves an error PostgreSQL is still handling, as when a caller's
`PG_CATCH` cleans up before `PG_RE_THROW` and that cleanup reaches Ankus code.
`FlushErrorState` empties the whole error stack, so the guard copies such an
error before it starts and pushes it back with `ReThrowError` after flushing its
own. An empty `ErrorContext` proves that nothing is pending. Otherwise a probe
runs with `ErrorContext` pointed at a private context, so the probe's own error
never releases memory a caller keeps beneath `ErrorContext`.

Not every guarded call needs a subtransaction. Pure built-in operations on
numeric, date and time, network, geometry and built-in range values, and array
cell reads, run under `PG_TRY` alone. Their failures remain pending until managed
code unwinds, because a caught ERROR cannot be proven clean in general. Two
failures are known to be clean and are recovered without a subtransaction: a
soft input error from a PostgreSQL 16 input function during `TryParse`, and a
report below ERROR that fails while no `emit_log_hook` is installed. Everything
else, including SPI, runs in an internal subtransaction. The
[guard tier measurements](evidence/guard-tiers.md) compare their costs.

The error transport contains SQLSTATE, query positions, source line, routing flags,
and fourteen optional `NativeValue` string slots: message, detail, hint, context,
schema, table, column, data type, constraint, internal query, file, routine,
server-only detail, and backtrace. Absent and empty strings remain distinct.
Native capture allocates buffers with `malloc`; managed reporting uses
`NativeMemory.Alloc`. Every slot carries its originating allocator's release
callback. Managed capture releases the transport in `finally`, including when
exception construction fails. Native reporting copies all fields into PostgreSQL
memory in a temporary AllocSet before releasing the transport. Reconstruction and
reporting run inside that owner, so encoding conversion never uses a Slab or Bump
caller's incompatible allocation policy. Shared cleanup on normal return and
`PG_CATCH` restores a live caller context and releases transport. Nonthrowing reports delete temporary storage
immediately; thrown reports retain it until PostgreSQL flushes the error state.

Diagnostic strings have no fixed transport truncation limit. A separate 2048-byte
UTF-8 primary-message buffer is reserved for allocation/encoding failures;
truncation of that fallback preserves complete characters. A managed exception's
`DiagnosticsIncomplete` property identifies incomplete capture. Native copied
`ErrorData` lives in a temporary context deleted after transport, so repeated
caught errors do not accumulate source-location copies in the caller's context.

New managed errors use `ThrowErrorData` after the dispatcher has returned. Errors
originally captured from PostgreSQL retain their reporting flags and use
`ReThrowError`: PostgreSQL's context callbacks already ran for these errors, so
rerunning them would duplicate procedural and SQL frames. File/routine strings
remain in the report's independent AllocSet because PostgreSQL treats source-location
pointers as constants. A reset callback attached to `ErrorContext` after reporting
reclaims this owner at the next error flush. Attaching it after reporting preserves
the input fields if a recursive error first resets `ErrorContext`. `DetailLog` stays
separate from client-visible `Detail`.

PostgreSQL 19 also resets `ErrorContext` after an outermost nonthrowing report.
When a guarded operation starts in an `ErrorContext` descendant, the SPI guard
records its checked context identity and restores that pointer only if the
registry still considers it live. Otherwise it selects the surviving `ErrorContext`.
This applies to notices emitted by SQL as well as direct managed logging; filtered
reports retain a surviving caller.

## Implementation files

- `src/Ankus.Generators/PgFunctionEmitter.cs`: generated managed and native entry points.
- `src/Ankus.Generators/NativeBridge.cs`: native transport and varlena conversion.
- `src/Ankus.Generators/GuardedBackend.cs`: native SPI and error-recovery guards.
- `src/Ankus.Generators/NativeSpiBridge.cs`: typed SPI parameter and result conversion.
- `src/Ankus.Generators/NativeArrayBridge.cs`: detoasting, shape validation, and contiguous array transport.
- `src/Ankus.Runtime/NativeArray.cs`: managed array encoding and typed materialization.
- `src/Ankus.Generators/NativeCursorBridge.cs`: portal identities and memory-context invalidation.
- `src/Ankus.Generators/NativeSessionBridge.cs`: scoped connections and session-owned plan cleanup.
- `src/Ankus.Generators/NativeErrorBridge.cs`: diagnostic ownership, capture, and native error reconstruction.
- `src/Ankus.Runtime/NativeValue.cs`: managed transport and output-buffer ownership.
- `src/Ankus.Runtime/NativeBackend.cs`: thread-local backend bindings.
- `src/Ankus.Runtime/SpiPreparedStatement.cs`: retained-plan ownership and execution.
- `src/Ankus.Runtime/SpiCursor.cs`: batched fetching and cursor ownership.
- `src/Ankus.Runtime/SpiSession.cs`: scoped query, preparation, and cursor APIs.
- `src/Ankus.Runtime/PgException.cs`: managed PostgreSQL diagnostics.
- `tests/Ankus.IntegrationTests/DatumConversionTests.cs`: backend conversion tests.
- `tests/Ankus.IntegrationTests/SpiTests.cs`: transaction, reentrancy, and error-unwinding tests.
- `tests/Ankus.IntegrationTests/SpiQueryTests.cs`: parameter, metadata, and result-lifetime tests.
- `tests/Ankus.IntegrationTests/SpiPreparedTests.cs`: retained plans, invalidation, and native cleanup tests.
- `tests/Ankus.IntegrationTests/SpiCursorTests.cs`: cursor batching, portal lifetime, and error cleanup tests.
- `tests/Ankus.IntegrationTests/SpiSessionTests.cs`: nested connections, scoped/retained plans, and failure cleanup.
- `tests/Ankus.IntegrationTests/PgDiagnosticTests.cs`: native/managed diagnostics and encoding/rethrow tests.
- `tests/Ankus.Runtime.Tests/NativeDiagnosticTests.cs`: optional values, emergency fallback, and transport cleanup.

## Enum datums

Generated enum module initializers register closed scalar/nullable/array
conversions without reflecting over members. Managed values carry C# enum
identity; native transport carries exact UTF-8 labels. PostgreSQL `enum_in` and
`enum_out` perform label/OID conversion, including visibility checks on newly
added labels. C# numeric values are never interpreted as stored enum OIDs.

The native dispatcher saves and restores the current function OID around each
managed callback. Guarded enum resolution finds fixed schema names or the owning
extension's current catalog namespace, with a function-namespace fallback for
functions installed outside an extension. Recursive calls restore the enclosing
function identity. Lookups are not cached across queries or DDL; materializing
one immutable SPI result may reuse its resolved mapping per column.

Catalog inspection copies labels, type/value OIDs and sort positions before
releasing native storage. SPI results, array elements and function results share
the enum converters. Managed array conversion checks exact CLR enum identity
before primitive-array patterns, because the CLR permits casts between some enum
arrays and arrays of their underlying integer type.

Generated installation SQL is UTF-8. Control files declare `encoding = 'UTF8'`
so PostgreSQL transcodes labels, names and custom SQL for the target database.

## Set-returning functions

Generated enumerable callbacks initialize a typed managed iterator, advance it,
and dispose it through a GCHandle. PostgreSQL owns the native state in its
multi-call context. An expression-context shutdown callback disposes the iterator
on normal early termination; a memory-context reset callback covers abort paths
where PostgreSQL deliberately skips expression callbacks. The handle is cleared
and released before invoking user disposal, preventing repeat cleanup after an error.

Every callback saves and restores the active function OID and managed backend
binding. Early expression shutdown supplies an active snapshot when the executor
has already removed it. Abort cleanup denies queries but permits release of owned
SPI plans and cursors through the native guard without starting a subtransaction.
The existing nested recovery guard also covers failures during that cleanup.
Cursor release during rollback marks a stable registry entry for closure. Native
transaction callbacks drain it after PostgreSQL's portal scan; a transaction-memory
reset callback covers cleanup queued during the later portal deletion scan.
Already-deleted portals invalidate their entries. Surviving parent-transaction
cursors are closed without executor callbacks against failed transaction state.
Active or pinned portals remain queued until safe cleanup. No cursor hash entry
is removed from inside PostgreSQL's abort scan.

Managed rows use the same conversion expressions as scalar functions. Native
TABLE conversion selects each tuple descriptor attribute's OID, preserving enum
and array identity independently of the overall RECORD return type. Output buffers
are released in native `PG_FINALLY` blocks on successful conversion and errors.
The originating set function's OID remains active through native row conversion,
so unqualified custom types and their arrays resolve in the owning extension's
current schema. The conversion's `PG_FINALLY` restores the caller's identity,
including when a row fails; managed callback scope alone is not sufficient.
One-column TABLE results use the scalar datum ABI, as PostgreSQL requires.

Value-per-call execution returns a single row and leaves the iterator rooted for
the next call. Materialization writes to a PostgreSQL tuple store owned by the
query context, resetting temporary conversion storage between rows and allowing
`work_mem`-controlled spill. Interrupt checks occur before each iterator advance.

## Trigger callbacks

Trigger entry points validate `CALLED_AS_TRIGGER` and expose twelve `AnkusValue`
slots: event bits, relation OID, trigger OID, trigger/table/schema names, optional
OLD/NEW transition aliases, text arguments, optional OLD/NEW tuples, and the
relation descriptor. The managed reader copies and validates every value.
PostgreSQL's trigger structures remain native and are compiled against the
selected server headers.

The return ABI is a `HeapTuple` pointer with `fcinfo->isnull = false`, including
a NULL pointer that skips a row. It is distinct from a composite datum's
`HeapTupleHeader`. INSERT/UPDATE replacements validate the relation's physical
layout and apply field conversions before `heap_modify_tuple` preserves tuple
identification. The resulting tuple is copied into the caller's memory context.
AFTER results and nonnull DELETE payloads are ignored before serialization;
BEFORE statement triggers cannot return a tuple.

Undefined generated fields carry tuple attribute flag 8 and a NULL transport
payload. Managed access rejects these fields rather than exposing SQL NULL.
Trigger reconstruction preserves PostgreSQL's generated slots, while ordinary
composite reconstruction rejects unavailable attributes. Stored NEW generated
columns are recomputed by PostgreSQL after BEFORE callbacks.

A native trigger scope owns temporary input and transition query-environment
storage. Each new SPI connection registers the active `TriggerData` once, with
the callback context as the allocation context. This matters because SPI cursors
retain the query environment after `SPI_finish`. Such portals receive the
trigger scope's identity and close before that scope ends, including detached
portals. The active trigger context remains bound during iterator disposal so
its `finally` blocks can still query transition tables. A native `PG_FINALLY`
restores the enclosing scope and function identity on success or failure.

If portal cleanup raises, the callback context remains owned by its caller until
PostgreSQL abort cleanup has released the surviving portals. Deleting it
unconditionally on that path would invalidate their borrowed query environments.
Mutable result/error headers live in the callback memory context, so cleanup
can read them safely after `longjmp`; their pointers are assigned before
`PG_TRY`. Managed buffers retain their allocator-matched cleanup. Retained
plans keep only the plan, without a transition table: execution uses the current SPI
query environment, and PostgreSQL's original error is transported when a required
named tuplestore is absent.

## Event trigger callbacks

Event entry points validate `CALLED_AS_EVENT_TRIGGER` and zero SQL arguments
before reading `EventTriggerData`. The native header owns the struct layout and
`CommandTag` enum; `GetCommandTagName` supplies the command text. Two borrowed
UTF-8 text slots carry the event name and command tag. The managed entry copies
them into a validated context, invokes the void callback, then restores the
enclosing managed event and backend scopes. Login events need no parse tree.

Each callback owns a temporary native memory context, including the mutable
result/error headers that cleanup reads after `longjmp`. The wrapper saves and
clears the current row-trigger scope so event SPI cannot inherit an outer row
trigger's transition query environment. Native `PG_FINALLY` restores that scope,
the function OID, and the caller's memory context on every path, then releases
managed result/error buffers and native temporary storage. The result is Datum
zero with `fcinfo->isnull = false`. Protocol errors use PostgreSQL's `39P03`.

Typed DDL/drop/rewrite helpers execute explicit projections of PostgreSQL's
metadata functions through guarded SPI. They copy nullable identities and
address arrays into immutable snapshots; the opaque `pg_ddl_command` column is
never transported. Thread-local context identity and event-phase checks prevent
old or suspended contexts from reading a newer native invocation. Snapshot
property access needs no live backend. As with row triggers, PostgreSQL ERROR
can be raised only after managed frames have returned to the native boundary.

## Aggregate callbacks and state ownership

Generated aggregate support functions use `AggCheckCallContext` before reading
arguments. Each call resolves the current aggregate or window memory context;
ownership is never cached under `fn_extra`, because grouping sets can interleave
states at one call site. SQL datum states use the existing owned conversions and
PostgreSQL's executor copies. Managed `PgAggregateState<T>` values use SQL
`internal` and an opaque checked managed root ID.

Native state headers live in their owner's context and register a
`MemoryContextRegisterResetCallback`. A backend-local address registry validates
headers before dereferencing them, so a foreign `internal` pointer cannot be
interpreted as an Ankus header. Reset removes the registry entry, invalidates the
managed root, and invokes optional payload disposal exactly once. Reset covers
ordinary group teardown, rescans, moving-window restarts, and abort. Finalization
does not own cleanup. Cleanup suspends the aggregate scope and permits only
owned plan/cursor release through the existing restricted backend boundary;
managed cleanup exceptions become warnings after the callback returns.

Deserialize wrappers omit PostgreSQL's mandatory SQL `internal` dummy from
managed arguments. PostgreSQL passes a zero pointer with SQL nullness set false
for that dummy. Deserialized state is registered in the caller's temporary
context; combine must copy its values into a fresh destination state when
necessary. Both runtime and native return validation reject cross-owner reuse.

Callback metadata copies aggregate/window kind, shared-state status, collation,
an optional aggregate OID, and sort keys derived from `AggGetAggref`. Window
contexts have no aggregate parse node. Native `SortSupport` performs comparisons
using the actual ordering operator, collation and NULL placement. Comparison
calls run in a guarded subtransaction so a custom operator error can be caught
by managed code after native recovery. Successful subtransaction commit also
changes PostgreSQL's current memory context, so comparison restores the saved
caller context and resource owner before returning to managed code. State registration has its own native
error guard and cannot unwind through a managed allocator call.

Callback result/error headers and mutable conversion buffers are heap-backed
before `PG_TRY`. The wrapper restores the prior function and aggregate scopes in
`PG_FINALLY`, releases transport buffers and deletes only callback-temporary
storage. SQL results are constructed in the caller's memory context before that
temporary context is deleted. Registered managed states retain their separate
aggregate or deserialize owner until its reset callback runs.

## Memory-context capability

Every generated managed callback also receives a synchronous memory-operation
envelope. Its provider token identifies the extension's native registry rather
than a callback stack address. Thread-local managed binding selects the current
envelope and restores the enclosing envelope on return. Checked context and
allocation identities can therefore outlive a callback when PostgreSQL retains
their owner, without keeping a pointer to an expired native stack frame.

The generator models virtual `PgMemoryContext` parameters independently of SQL
slots. Managed invocation order includes these parameters; native argument
counts, NULL checks, SQL signatures, defaults, and operator/cast operands omit
them. Each injected handle resolves the current context after memory capability
entry and keeps a checked snapshot of that owner.

Set creation switches into `multi_call_memory_ctx` for the factory and
`GetEnumerator`, restoring the caller in `PG_FINALLY`. The memory envelope is
initialized before that switch to keep the caller's argument storage protected;
the explicit iterator-owner protection also remains active. Later row callbacks
retain their existing scratch context behavior. Registering owner invalidation
before the iterator abort callback lets disposal read direct owner allocations
before invalidation; PostgreSQL may have already deleted children.

The memory envelope separately identifies the result owner. For set callbacks it
is `multi_call_memory_ctx`, so retained SPI and catalog-call results survive row
context resets. The original callback context remains protected independently.

Live set owners carry a registry reservation until native invalidation. Managed
reset/delete operations reject the owner and affected ancestors even between
cursor fetches. Ancestor `ResetOnly` is rejected too: required executor descriptors
can live in the parent's per-query context even when the multi-call child survives.
`ResetChildren` on the suspended owner itself only affects descendant storage.
Native executor cleanup bypasses this managed-operation restriction and removes
the reservation with its registry entry.

The memory bridge invokes PostgreSQL directly under a native error guard. It
does not allocate in an SPI subtransaction or an operation work context. On error
it copies owned diagnostics into a temporary child of `TopMemoryContext`, resets
PostgreSQL's error state, and restores the context selected at operation entry.
If that context is an `ErrorContext` descendant, flushing deletes it and recovery
selects the surviving `ErrorContext` instead of restoring a freed pointer.
The diagnostic owner must remain outside `ErrorContext`, even when that context
is current: `FlushErrorState` resets it. The memory and SPI guards also restore
the entry values of `InterruptHoldoffCount` and `QueryCancelHoldoffCount`, which
PostgreSQL ERROR clears. Abort cleanup can catch native errors inside an existing
interrupt holdoff section. A caught allocation error leaves ordinary selected
contexts and their original chunks intact; storage under `ErrorContext` follows
PostgreSQL's error-flush lifetime.

Generated raw C call bodies use a separate operation in this same native guard.
The hidden `NativeRawCall` contract pins an array of argument-address/size pairs
and borrows an exact result destination. The native guard calls the generated C
body directly; no managed adapter sits between that body and `PG_TRY`. Argument
and result validation runs before the underlying native function, and generated
bodies publish result bytes only after it returns. Native failures use the
existing owned diagnostic transport and allocator-matched release.

Node views and the raw-call runtime contract share the same binding validator. The hidden
`NativeRawCall.ValidateBinding` entry point sends the complete companion identity
and PostgreSQL major to the active extension's native guard. A missing or
mismatched contract produces SQLSTATE `0A000`; no managed frame is crossed by
PostgreSQL's error unwinding. Validation belongs to the current callback and must
run before obtaining a body address. A successful check cannot be cached across
providers or substituted for the guarded invocation itself. The accessor must
only return an address, without calling PostgreSQL or reentering managed code.

Fixed-body emission can select functions within a complete graph that also
contains globals and non-fixed prototypes. It validates that entire graph before
selection, including unselected roots, and preserves the companion's type
identities. Unknown or duplicate selections fail; selecting a global, variadic
or unprototyped function still requires its own supported lowering. Selecting
no bodies does not bypass graph validation.
Before publishing a record contract or emitting bodies, signature validation
compares the independently collected header types with both written and canonical
types in the measured graph. It checks scalar and typedef identities, nested
pointers, array extents, tag kind/completeness, qualifiers and function prototypes
and calling conventions. Equal sizes alone cannot establish type compatibility.
The comparison preserves C parameter adjustment and compiler Boolean spelling;
anonymous typedefs must refer consistently to the same measured declaration.
Nested `typeof` and `typeof_unqual` use the compiler's resolved type rather than
evaluating an operand. The structured header catalog retains written typedefs;
libclang represents a `typeof` wrapper through its canonical type. Their comparison
uses that exact resolved contract at the wrapper boundary, preserving written
parameter qualifiers, pointee qualifiers, record identity and function shape.
Removing top-level qualifiers with `typeof_unqual` does not remove pointee
qualification. The native compiler and executable checks still verify the
reconstructed declarations and physical representation.
These signature checks are supplemented by the separate `binding-record-checks`
build-tool command. Given a collected `native-records.json`, it reconstructs the
complete type graph against the selected PostgreSQL headers, checks every named
member's type and offset, and checks complete object sizes and alignments. Tags,
typedefs, globals and named member paths anchor actual native declarations;
distinct record identities cannot collapse merely because their bytes match.
Canonical entries must resolve to concrete types; typedef and spelling-wrapper
cycles are rejected throughout the graph, including types reachable only through
record fields.
Enums retain their measured size, alignment, signedness and constants. MSVC's
enum compatibility rules do not independently establish nominal enum identity;
the source observations retain those identities. Constant checks compare sign
as well as value, so C's unsigned conversions cannot equate a negative constant
with an unsigned 64-bit maximum.

The generated executable reads prepared bytes through real named bitfields to
verify their offsets, widths and signedness, including const/volatile fields and
128-bit values where supported by the selected compiler. Failures identify the
physical field. Compilation and execution must succeed, and cancellation is
checked again before `native-record-checks.c` replaces an existing result. Owned
staging files are removed on failure. The command takes `<records-json> <pg_config>
<output-directory> [compiler] [windows-library-directories] [target-triple]`.
The default verifier is Clang on Unix and MSVC on Windows.

Function compatibility follows C parameter adjustment and removes only
parameter-level qualification when comparing canonical prototypes. Pointee and
array element qualifiers remain checked. Standard `va_list` names the variadic
storage even when Clang represents it through a compiler-private typedef.
PostgreSQL's spin-delay macro fallback receives an addressable hidden C shim;
generated call bodies execute the selected headers' operation without removing
its catalog entry.

This command rejects declarations for which no C type anchor exists. In
particular, checking promoted leaves alone would not establish an unnameable
anonymous container's hidden size or alignment. Such containers remain retained
by collection and managed projection; complete independent verification of those
unnameable containers remains unfinished. The SDK requires native verification
of its combined graph and rejects unsupported declarations before publication.
Argument typedefs retain the original native qualifiers. Reads through those
types avoid adding a second `const`, which MSVC rejects for already-qualified
parameters; pointee qualification and volatile reads remain part of the contract.

The internal import selector reads undefined generated accessor names from a
relocatable ELF, COFF (including BigObj), or Mach-O object. It checks the object
format, architecture and byte order against the selected headers before lowering
the referenced functions from the complete graph. Definitions, local symbols,
common storage and unused declarations do not select bodies. Bounded metadata
reads reject malformed tables and selected names with invalid UTF-8.
The emitted accessors only return body addresses; obtaining one does not enter
PostgreSQL. Both bodies and accessors use hidden visibility on Unix and are not
exported on Windows. Invocation still requires the native error guard.
The internal record emitter can now generate typed `NativeMethods` beside the
complete graph's shared record and enum declarations. Its companion identity
includes the full native declaration metadata, including native/linker names,
as well as storage and target observations. Reordering selected functions or
dictionaries does not change that identity. Every call validates the active binding before
obtaining its pure native body address; `LibraryImport` imports only that address
accessor, with an explicit C calling convention.

Managed wrappers copy exact argument bytes into independently aligned storage.
Descriptors and values share a frame bounded to 4 KiB on the stack; larger frames
use checked native allocation lengths and release their allocation in `finally`.
C parameter adjustment determines array/function argument storage. Zero-size
native records have distinct logical CLR tokens that transport zero bytes,
while actual void results have no destination. Native bodies copy returned bytes,
so the result buffer does not assume CLR or native result alignment. Borrowed
addresses and callbacks keep their caller-supplied ownership obligations.

Compiler/executable tests check exact values, alignment, allocation boundaries,
cleanup and scoped binding rejection. A standalone Native AOT executable uses
real generated imports and native accessors; inspecting its ILC object verifies
that an unused unavailable function is trimmed. The test's public memory ABI
harness models guarded error transport; it does not execute PostgreSQL ERROR.
The SDK now composes node declarations and available fixed native functions in
one companion. Its native link step reads the actual ILC object and compiles only
referenced accessor bodies. Backend symbols use ordinary linker and loader
resolution, including definitions in the extension and explicitly linked native
objects or archives. A declaration's absence from the server's own exports does
not establish that an extension cannot supply it. This follows pgrx's dynamic
lookup behavior; PostgreSQL loads extension libraries with immediate symbol
resolution. Complete platform and version validation remains in progress.

The raw-call guard transports errors without rolling back PostgreSQL transaction
or SPI state. `PgTransaction.RunInSubtransaction` adds an explicit recovery
boundary through the existing SPI guard. A synchronous unmanaged thunk retains
managed exceptions and returns failure before native rollback begins; the original
exception is rethrown after the guard restores the caller's resource owner and
memory context. Callback allocations use the subtransaction context, preserving
PostgreSQL's commit and abort lifetimes. Nested recovery scopes are independent.
Automatically adding a subtransaction to every raw call would change transaction
operations and native pointer lifetimes, so raw calls retain their native semantics.

A raw native error marks the innermost explicit scope for rollback even when the
callback catches it. Subsequent SQL and raw calls are rejected until that scope
unwinds. SPI session disposal inside a failed scope leaves native frames for abort
cleanup, since a raw error can leave a deeper SPI connection on the stack. The
session's registered memory-context cleanup releases its plans and registry entry
during rollback. Managed tests check exception identity and boundary containment;
the packaged backend witness checks writes, context lifetime, nested recovery
and the previously observed SPI stack warning. The complete supported PostgreSQL
and platform matrix remains required.

Raw calls require the active backend thread. They remain available for native
resource release during iterator disposal, including query-abort cleanup, as
pgrx's raw `pfree` calls do during `PgBox` destruction. The caller must satisfy
each native function's transaction and cleanup-state preconditions; raw calls
do not inherit SPI's blanket query restriction. The native error guard retains
its `ErrorContext` cleanup restriction.
On error it restores the entry memory context and interrupt holdoffs. On success,
raw functions retain their deliberate native state changes, including interrupt
holdoffs; the caller remains responsible for balancing them. Raw calls do not
open an SPI subtransaction or infer pointer ownership, callback lifetime, or the
validity of an arbitrary address. A C body may reenter managed code only through
a callback boundary that finishes managed unwinding before raising PostgreSQL
ERROR. Packaged SDK tests exercise explicit native providers, selected calls and
backend recovery. Complete published-platform and version evidence remains
required for the full port.

The internal selected-header record emitter consumes the independently validated
transitive type graph. It emits exact-offset records and unions, nested inline
arrays, native-width enums, anonymous-member access and checked bitfield
properties. Numeric types map to CLR values only when the observed representation
matches; extended floating-point, vector, complex and atomic storage retain raw
bytes. A byte representation does not supply native arithmetic or synchronization.

Native alignment remains explicit metadata. Managed copies do not promise native
stack alignment, and the eventual call/allocation layer must honor the measured
alignment. Opaque and complete zero-sized declarations expose metadata instead
of fabricated CLR storage. Flexible tails require a live address and a
caller-guaranteed initialized extent; integer overflow checks cannot establish
pointer provenance or ownership.

The SDK's `binding-sources` path uses this emitter for one companion containing
the combined node/function/global dependency graph. Unevaluated type roots preserve the existing
named and anonymous node/value identities. Independently executed layout probes
must agree with Clang on node sizes, offsets, array strides and named enum
representations/constants. Packed field placement does not change the required
alignment of the field's type. Node cast rules, concrete allocation metadata and
typed flexible-tail accessors retain their existing contracts. Compiler staging
uses a unique system temporary directory so long project output paths do not
exceed Windows' process working-directory limit. Large temporary AST files are
removed after compiler/worker completion, including failed collection. A first
top-level header observation partitions the raw inventory into present and
explicitly absent declarations. Names come from the reference inventory; declaration
kinds and types come from the selected headers. For example, PostgreSQL can expose
`pg_popcount32` as a function on ARM64 and a dispatch-pointer global on x64. Nested
declarations cannot satisfy requests. Conflicting declarations, malformed observations,
or changes between the initial observation and measured collection fail validation.

Handwritten pgrx helpers supplement the pinned foreign inventory at this boundary.
Existing native functions retain their selected-header prototypes. Versioned
macro wrappers contribute explicit addressable declarations; required helpers
cannot disappear into the absent partition or conflict with another public
declaration. Discovery uses their prototypes, and only selected native call bodies
enable their implementations. This avoids unused internal definitions and avoids
linking unrelated helper dependencies. Complete collection and cache-hit
verification compile the current helper bodies with the selected native compiler before
the independent record/layout checks. The final native compiler also checks and
compiles every selected body beneath the ordinary error guard.
Types required by helpers with untyped addresses, including `PageHeaderData`,
enter the same measured record graph through additional native object roots.
Their fields and layout come from the selected headers and independent compiler
checks; they do not become node types or acquire node cast tags.

The SDK's internal `binding-compile` command prepares an isolated project with
the consumer's selected SDK, runtime reference and package directory. The consumer's
restore assets supply the effective feeds, ordered NuGet configuration files and
fallback directories. NuGet applies the original credentials and source mappings;
an isolated build does not add nuget.org or merge configuration files itself.
Restore runs before cache lookup, including when compiled artifacts already exist.
It resolves current references and analyzers before cache lookup, then checks content hashes
for the compiler, runtime, SDK, packages and compiler inputs. Timestamp-preserving
changes invalidate reuse, including changes to NuGet configuration. Generated temporary project paths are normalized to
the same logical source location used by deterministic compilation.

A cross-process lease protects each immutable compiled entry through consumer
copying. A producer publishes only complete verified artifacts; failure and
cancellation remove its staging while retaining the previous entry. Consumers
copy their own outputs and register those files for clean, without owning the
shared cache. Source reuse observes the current preprocessed headers, including
unused macro definitions, and the effective semantic frontend command. Compiler
configuration can change record layout or diagnostics without changing the
preprocessed tokens. Native tool contents, node layout and generator identity
also participate in the key; pre/post observations reject changed inputs.
Every hit still compiles and executes the independent declaration/bitfield
checks. Leases protect source artifacts through verification and consumer copying.

The native-body compiler uses short owned temporary paths and publishes immutable
source/object pairs in the shared binding cache. An atomic linker-manifest replacement selects
the new pair only after successful compilation; failure and cancellation retain
the previous pair and selection. Empty selection needs no compiler. MSBuild clean
owns the consumer's manifest and leaves shared objects intact. Keeping objects
outside deep consumer intermediates avoids MSVC's native input-path limit.
Object and archive provider tests exercise actual
missing-symbol link errors, recovery, exact results and paths with spaces.

Cold, changed-source and unchanged SDK builds/publications have been measured on
Linux and Windows, with conditions and results recorded in `PROGRESS.md`.
Repeated builds improve with source reuse; the measured cold builds are slower.
Content checks remain mandatory regardless of the time budget. Variadics,
callback/lifetime helpers and managed hook registration remain unfinished.

The companion's `NativeGlobals` class selects actual global entries from header
availability, excluding synthetic node/helper type roots. Complete objects expose
typed value copies, with setters only when their by-value storage contains no
const qualification. Pointer targets do not qualify their containing pointer.
Incomplete objects expose only explicit dangerous addresses. Empty native objects
have distinct logical managed values and transport zero bytes.

Read, write and address operations use the same per-access binding validation,
aligned frame ownership and `NativeRawCall` guard as fixed functions. Plain
objects copy their complete bytes; qualified objects use C value loads/stores,
with recursive element access for arrays. Compound copies provide no atomic
snapshot or synchronization guarantee. `_Atomic` header syntax and higher-level
atomic/locking helpers still require their own complete implementation.
Each thread-local address is resolved within the invoked body.

Native AOT imports select individual operations through the disjoint
`ankus_native_global_body_` prefix. Pure accessors only return body addresses;
actual global access occurs beneath the native error boundary. Unknown operations,
const writes, incomplete value accesses and incompatible targets fail before
publication. Unreferenced globals do not become linker dependencies. Function
pointer values preserve native addresses; they do not establish managed callback
lifetime or hook registration contracts.

Validation metadata and owned context identifiers use the extension's C runtime
allocator. PostgreSQL owns the actual chunks. Context reset callbacks remove
allocation records before native storage can be reused; monotonically increasing
identities prevent a recycled address from reviving a stale handle. Explicit
resets retain the selected registry entries and re-register one-shot invalidators.
Implicit native cleanup invalidates those entries. An explicitly owned context's
name remains outside its resettable storage and is released with its registry
entry. Name transport converts between UTF-8 and server encoding and copies into
a pinned managed destination before releasing conversion storage. Reading a name
uses temporary AllocSet storage independently of the currently selected allocator.

Allocation records retain their exact requested byte length, huge size policy,
and alignment. Bookkeeping is reserved before native allocation and published
only after success; native errors and no-OOM null results free the unpublished
record. A registration failure therefore cannot require freeing an untracked
Bump chunk. Aligned pointers use the selected PostgreSQL headers' redirect chunk
allocator (PG16+), retaining the underlying allocator's restrictions.
Checked payload and padding arithmetic precedes the native call. Aligned no-OOM
calls require the upstream fixes in 16.15, 17.11, 18.6, or 19 beta 3. Because
PostgreSQL 19 prereleases share a numeric version, beta labels are checked too;
ambiguous development snapshots are rejected for aligned no-OOM calls.
Aligned or no-OOM resize allocates a replacement, copies only the known prefix,
then frees the original after success. Ordinary throwing resize uses `repalloc`
or `repalloc_huge`. Tail clearing starts at the old requested byte length.

Owner lookup returns the context established by allocation or checked adoption,
after validating its registry lifetime. Before free or resize, the bridge checks
that known context with the selected headers' `IsA(context, BumpContext)` macro
(PG17+). Bump requests raise the native unsupported-operation diagnostic without
reading a chunk header; ordinary builds omit that header. Allocation, context-owned
values, checked access, detachment and reset remain available. Slab retains its
native size checks, and Generation uses the public palloc-family operations.

Detach removes only the allocation registry entry. Adoption requires caller-proven
live native pointer provenance and exclusive ownership, checks the actual owner,
and registers a new identity without freeing the pointer on failure. A known Bump
target is rejected before header lookup. AllocSet
creation validates supplied block sizes against target-header alignment and
version-specific chunk-offset limits before entering assert-only native checks.

Typed boxes and tracked borrowed views share the existing checked allocation
identity. Transferring an individual owner to context ownership retains that
identity; raw detach consumes it. Raw borrowed addresses use a separate guarded
copy operation with the supplied context identity and captured reset generation.
They require no allocation-header lookup, release rights, or per-view native
record. Explicit reset advances the generation; implicit cleanup removes the
context identity. Generation exhaustion permanently retires new raw borrows
without wrapping back to a live generation. The unsafe caller still proves
address validity and any external lifetime shorter than the context's lifetime.

Deletion checks current-context ancestry. A native protection stack retains
explicit reset/delete roots and the actual iterator/aggregate state owners across
managed callbacks. It prevents overlapping destructive operations, child creation
in teardown trees, and switching into a context being reclaimed. Each entry is
removed in `PG_FINALLY`; nested independent resets retain only their own registry
entries. PostgreSQL infrastructure contexts are protected separately because
their transaction and backend metadata can outlive the active executor tree.
Managed finalizers never invoke PostgreSQL. Context and allocation disposal are
deterministic and native cleanup makes later disposal harmless.

`PgMemoryCallback` registers a native one-shot record with a monotonic managed
root ID. Both registries remove that ID before entering user code; cancellation
releases the action immediately and leaves an inert native record until reset.
This protocol also supports PostgreSQL 13–18, which lack the unregister function
added in PostgreSQL 19. Native records are independent of context registry node
lifetime. A managed exception is transported back to native code and raised as
ERROR, preserving PostgreSQL's pending older callbacks and retry behavior.
The managed dispatcher masks SQL/logging and inherited GUC/aggregate capabilities, permits owned plan/cursor release
through the binding captured at registration, and restores enclosing capabilities.
It also captures a transaction context only when that context is an ancestor of
the callback owner. That ancestor stays alive through the callback and receives
deferred cursor cleanup. During late subtransaction deletion PostgreSQL has
already changed `CurTransactionContext` to the surviving parent; using that
global would postpone an adopted parent cursor's close until the outer
transaction ends. Native portal scans still require deferred closing.

If abort cleanup itself raises ERROR, PostgreSQL reports that error after the
primary SQL error and retries the remaining cleanup. Npgsql retains the last
ErrorResponse before ReadyForQuery. Backend tests assert both ordered server-log
diagnostics and the final client exception, followed by same-session recovery.

ErrorContext-owned callbacks inherit a separate error-handler cleanup flag.
Both native guards return a direct `55006` (object in use) diagnostic before entering PostgreSQL
while that flag is set. PostgreSQL's private error-stack and recursion counters
cannot be saved through its public API: swapping the `ErrorContext` pointer would
not isolate them. A nested caught error could otherwise flush an outer reporter's
state or reclaim an owner still referenced by the reset stack. This matters for
ordinary error flushing on every supported version and normal outermost reporting
on PostgreSQL 19. The flag is restored in `PG_FINALLY`; managed callback failures
still become native ERROR only after the managed frame has returned.

## Configuration hook capabilities

GUC callbacks receive separate native pointers for typed setting reads, logging,
and transaction-bound operations. Managed thread-local scopes restore all three
bindings in reverse order, including on exception. Assign and show receive no SQL
executor. Checks receive one only when PostgreSQL has a valid transaction.

The logging callback supports threshold checks and nonterminal reports. It borrows
the managed diagnostic buffers for the duration of the call and returns separately
owned errors. Native `PG_TRY` catches conversion and reporting errors below managed
frames; a temporary context owns converted fields and is deleted on success or
failure. Cached database converters allow logging during abort, file reload, and
parameter reporting without catalog lookup, SPI, or a subtransaction.

ERROR, FATAL, and PANIC first unwind managed code. Ordinary hook errors follow
PostgreSQL's phase constraints: check rejection, assign FATAL, transactional show
ERROR, and nontransactional show FATAL. Explicit FATAL/PANIC requests keep their
severity, including if converting their diagnostic text fails. A backend which
cannot copy an error safely terminates rather than returning through managed code
with an active PostgreSQL error stack.

Error copies retain their source and translation metadata before the native error
stack is reset. PostgreSQL 13–16 leave five source/translation pointers borrowed
in `CopyErrorData`; the bridge copies them explicitly. PostgreSQL 17+ copies those
strings itself. Paired cleanup frees the owned copies on all supported versions,
including fields that PostgreSQL's `FreeErrorData` treats as constant.

Assembly prefix declarations run in native initialization after definitions and
before managed initialization. PostgreSQL 15+ uses `MarkGUCPrefixReserved`;
13–14 use `EmitWarningsOnPlaceholders`. Prefix-only libraries need neither a
managed callback nor the SQL execution bridge. Prefix conversion uses the database
encoding for backend loading and restricts shared-preload text to ASCII.
