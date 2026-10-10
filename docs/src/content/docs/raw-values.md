---
title: Raw values and custom types
description: Bind raw PostgreSQL values and write custom type input and output functions.
---

## Native PostgreSQL declarations

Extension projects using `Ankus.Sdk` receive generated node declarations, fixed
native functions and their type dependencies in `Ankus.Postgres`. The SDK reads the selected
PostgreSQL headers with Clang and checks node layouts against a separately
compiled C probe before compiling C#. Sizes, field offsets, enum representations,
array strides and target identity come from those headers. Select the installation
with `AnkusPostgresMajor` and `AnkusPgConfigPath`; see
[build settings](/reference/build-settings/) for compiler selection.

Fields follow the selected installation, including changes between PostgreSQL
prerelease snapshots. Removed fields and unavailable node types are absent from
the generated declarations; new fields retain their actual native storage.
Node tag numbers come from the installed headers, including when a prerelease
renumbers them. Embedded enums also use the installed members and values.
Cast families retain only available tags. Discriminator storage
and inherited prefixes must still match the supported casting contract; an
incompatible prefix fails the build. Reusing cached declarations repeats the
native checks against the selected headers.

Like pgrx, which runs bindgen against your installation, the SDK derives the
binding inventory from the selected headers under pgrx 0.19.3's bindgen
configuration and header coverage. Declarations your build of PostgreSQL adds,
such as ICU or assertion-only ones, are present, and those it omits are absent.
PostgreSQL 19 SQLSTATE names use beta 4; support is still under validation
across the supported platforms.

Native declarations retain PostgreSQL names and mutable fields:

```csharp
using Ankus.Postgres;

RangeTblRef reference = new() { type = NodeTag.T_RangeTblRef, rtindex = 9 };
reference.rtindex++;
```

The generated records include fields and dependencies reached through native
pointers. Structs, unions, inline arrays and bitfields retain their measured
storage; bitfield writes reject out-of-range values. Extended numeric and atomic
representations expose bytes when no exact CLR value exists. Those bytes do not
provide native arithmetic or atomic operations. Opaque declarations expose
pointer identities and metadata. They do not implement the native allocation
contract; use an existing native pointer instead of allocating an incomplete type.

Embedded structs, unions and fixed arrays can be edited in place. Unions share
storage exactly as they do in C; only read the active representation. Flexible
arrays expose `Dangerous_<field>` methods that require a pointer to their owning record and
the actual number of trailing elements. Their storage is outside the fixed
managed value.

Arrays of data pointers use generated readonly pointer values because C# does
not allow pointer types as generic arguments. These values preserve the exact
pointer type and native stride while supporting inline arrays and `Span<T>`.
Their pointer conversions and `DangerousGetAddress()` require an unsafe context;
copying a value or checking `IsNull` does not extend its native lifetime.

Anonymous structs and unions promote their members directly onto the enclosing
record, matching C access such as `transaction.commit_time`. Their fields retain
the actual offsets, overlapping storage, array shapes and bitfield behavior.
Unnamed containers do not acquire separate allocation types or synthetic
`AnonymousN` fields. Named embedded records retain distinct native identities.
Promoted function pointers use the enclosing record's callback name, such as
`Methods_applyCallback` for `Methods.apply`.

These are raw native representations. Data pointers use C# pointer types such
as `Node*`, `void*` and `Node**`; function pointers use generated values with
their native signatures. Transport preserves every address bit even when the
consumer project enables checked arithmetic. Neither
owns or validates the pointed-to storage.
Creating a managed struct does not allocate a PostgreSQL node, and setting its
tag does not establish native ownership.

Use a native owner and `PgNodes.Borrow` to create a checked node view:

```csharp
using PgMemoryContext owner = PgMemoryContext.Create("query node");
using PgNativeBox<RangeTblRef> storage = owner.CreateBox(
    new RangeTblRef { type = NodeTag.T_RangeTblRef, rtindex = 9 });
PgNodeReference<RangeTblRef> reference = PgNodes.Borrow(storage.Borrow());
PgNodeReference<Node> root = reference.TryCast<Node>()!;
PgNodeReference<RangeTblRef> sameNode = root.TryCast<RangeTblRef>()!;

RangeTblRef changed = sameNode.Value;
changed.rtindex = 10;
sameNode.Value = changed; // storage and both views observe the same native bytes.
```

`TryCast<T>()` follows PostgreSQL's generated node inheritance and alias rules.
It returns null for a rejected tag. Invalid storage, insufficient bounds, or an
incompatible ABI throws. `Tag` preserves the native tag as `uint`;
`IsA((uint)NodeTag.T_RangeTblRef)` tests exact equality.

Views check the active extension's measured ABI, alignment, complete storage
bounds and original lifetime on every access. Allocation views follow resizing.
Freeing or resetting their owner invalidates them. Raw views retain the reset
generation and byte extent captured by `DangerousBorrow<T>`; casting never
recaptures a generation or expands the caller's storage guarantee. For a raw
base-node address whose complete object is larger, pass the explicitly proven
extent to `context.DangerousBorrow<Node>(address, byteLength)`.

These checks do not establish that native pointer members form valid objects.
Keep their storage and external resources alive for every use.

To follow PostgreSQL's zeroed-node allocation pattern:

```csharp
using PgNativeBox<RangeTblRef> storage = PgNodes.DangerousAllocate<RangeTblRef>(
    (uint)NodeTag.T_RangeTblRef, owner);
RangeTblRef value = storage.Value; // The payload and padding start at zero.
value.rtindex = 9;
storage.Value = value;
PgNodeReference<Node> root = PgNodes.Borrow(storage.Borrow()).TryCast<Node>()!;
string description = root.DangerousToNativeString();
// {RANGETBLREF :rtindex 9}
```

The allocation uses the generated representation's complete measured size and
alignment, writes the supplied tag, and runs no C# constructor. The caller must
choose the correct tag and initialize fields required by subsequent native
operations. A base `Node` or `Expr` accepting a descendant's tag is not large
enough to allocate that descendant. Omit `owner` to use the current context.
Dispose the box to free the allocation, or call `ReleaseToContext()` to transfer
individual release rights to its context; the normal allocator restrictions apply.

`DangerousToNativeString()` calls PostgreSQL's `nodeToString`. It checks the
root's actual tag size and alignment against the original extent, including
after an upcast or allocation resize. The caller guarantees valid pointer
members and variable-length tails throughout traversal, including any reentrant
native callbacks. Keep that graph alive and unchanged until formatting returns.
Root checks cannot prove those pointer and graph contracts.

The returned string owns its text independently of the node's lifetime. Ankus
converts the server encoding to Unicode and releases native formatting buffers
on success and error. PostgreSQL errors return through the native guard before
becoming managed exceptions. Unknown tags retain PostgreSQL's warning and
fallback output. Formatting is explicit because it accesses a live backend and
traverses native pointers; ordinary debugger display does not perform that work.
The diagnostic text follows the selected PostgreSQL version. For example,
`CollateExpr` renders as `COLLATE` on PostgreSQL 13/14 and `COLLATEEXPR` on 15+.
Before PostgreSQL 16, empty and null string members both render as `<>`; newer
versions distinguish an empty string as `""`.
Planner/executor integration, broader raw bindings and complete version/platform
validation remain in progress.

Projects built against the same generated contract share a companion assembly
and can exchange its native types directly. Use the same selected installation
and Clang toolchain for projects that exchange these values. The generated
contract rejects an incompatible runtime target; cross-compilation requires a probe that can execute
on the build host. `dotnet clean` removes the generated companion artifacts.

### Fixed native functions

`NativeMethods` exposes fixed-prototype functions present in the selected
headers. Calls require an active PostgreSQL callback, including functions whose
C implementation only computes a value:

```csharp
unsafe
{
    FullTransactionId value = NativeMethods.FullTransactionIdFromU64(0xFEDCBA9876543210UL);
    ulong exactValue = value.value;
}
```

Arguments and results preserve the native types and complete object bytes.
Data pointers retain their pointee types; the caller must supply valid addresses, keep their owners
alive, and follow the native function's allocation and lifetime rules. Prefer
Ankus's checked APIs when they cover the operation you need. Publishing includes
native bodies only for methods referenced by the extension.

Raw calls, raw global accesses and native function-pointer invocations require
an explicit unsafe context. Pointer signatures enforce this through the C#
compiler; `ANKUS129` also checks operations whose signatures contain only
scalars. Use an `unsafe` block for those calls. Assigning a raw method to a
delegate requires the same acknowledgment. The native error guard still applies;
`unsafe` acknowledges the caller's native storage and backend obligations.

The same requirement applies to `Ankus.CompilerServices` transport methods
that accept native addresses, callback storage or iterator handles. A nonzero
address is not proof of valid storage. Use only the live capability supplied by
the matching dispatcher, and restore entry scopes in reverse order. Iterator
handles must come from `NativeSet.Create` and cannot be reused after disposal.

For `ANKUS129`, the editor offers **Use an unsafe block** on supported block and
expression bodies. The correction preserves the existing code and local scope;
you still supply valid storage and follow PostgreSQL's ownership rules.
When an `out` or deconstruction variable is used by a later statement, the fix
keeps its declaration in that enclosing scope and puts the native assignment
inside an unsafe block. Fix All applies the same rule independently in nested
blocks. A `ref struct` local initialized from stack memory, such as a
`stackalloc` span, keeps that lifetime: the moved declaration is `scoped`.
The fix is not offered when a pattern variable declared by the native statement,
such as `is not int value`, is used by a later statement; restructure that code
manually.
Checked APIs such as `PgNodes`, `PgFunctions` and `Spi` remain usable in safe code.

The same class includes selected-header helpers for alignment, memory contexts,
transaction IDs, buffers, pages, and heap tuples. Macros such as `TYPEALIGN`,
`GETSTRUCT`, and `HeapTupleHeaderGetNatts` use native wrappers so their arguments
are evaluated once and their pointer and integer values retain PostgreSQL's
semantics:

```csharp
unsafe
{
    ulong alignedLength = NativeMethods.TYPEALIGN(8, 13); // 16
    ulong pageHeaderBytes = NativeMethods.SizeOfPageHeaderData();
}
```

Signatures follow the selected PostgreSQL version, including native `const` and
`volatile` contracts. For example, `PageValidateSpecialPointer` returns `bool`
on PostgreSQL 13–17 and `void` on 18–19; `SpinLockFree` is unavailable on 19.
Page and tuple helpers require valid native storage with the original owner's
lifetime. `PgMemoryContext.Id` is an opaque validation token, not a native
`MemoryContext` address. `GetMemoryChunkContext` obtains the native owner address
of a live PostgreSQL allocation with a chunk header. Keep spinlock critical sections entirely in native code: managed
execution can allocate or suspend while a lock is held.

PostgreSQL errors become `PgException` after the native guard restores the error
boundary. Raw calls retain PostgreSQL's resource and transaction behavior. Use
[`PgTransaction.RunInSubtransaction`](/transaction-callbacks/#recoverable-work)
when an operation needs rollback before a caught exception reaches its caller.
An error marks that scope for rollback even if the callback catches it.
Without an explicit scope, Ankus retains the original failure, blocks further
backend work and reports it after managed cleanup unwinds. Returning a value or
throwing a replacement exception cannot turn that native failure into success.
On PostgreSQL 13–16, parallel execution cannot start such a scope. A native
failure there ends the current managed callback after `finally` cleanup; see
[parallel SQL error handling](/spi/#errors-and-transactions).

The [PGLZ inspection sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.PglzInspect)
calls `pglz_compress` and `pglz_decompress` with checked spans, the
[rewrite manipulation sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.RewriteManip)
changes `Var` nodes with `rewriteManip.h`, and the
[notify sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.Notify)
wraps `commands/async.h`.

Header declarations do not guarantee that a server or loaded native library
exports the corresponding function. Variadic calls and complete version/platform
validation remain in progress.

### Native globals

`NativeGlobals` exposes global objects declared by the selected server headers.
Each property reads the current value or writes a complete value through the
native error guard. Every access requires an active backend callback and the
matching native binding:

```csharp
unsafe
{
    int backendProcessId = NativeGlobals.MyProcPid;
    MemoryContextData* currentContext = NativeGlobals.CurrentMemoryContext;
}
```

Values are copies. To change an array or record, read it into a local, modify
that value, and assign it back. Native `const` objects, including records with
const fields, have no setter. A mutable pointer to a const object still has a
setter: changing that pointer does not make its target mutable. Native volatile
loads and stores use C value operations; reading a compound value does not make
it an atomic snapshot. Follow PostgreSQL's synchronization and ownership rules.

`DangerousAddressOf_name()` returns the original object's address. Incomplete
objects and arrays expose only this address; Ankus does not invent an unknown
size or array bound. These addresses retain native const/volatile, ownership and
lifetime requirements. A thread-local address belongs to the current backend
thread. Data pointers retain their pointee types; function pointers use the typed borrowed values
described below. Assigning a function address does not register or root a managed
callback.

Prefer the checked Ankus APIs when they cover the operation. Raw writes can
violate backend invariants or bypass normal configuration assignment hooks.
Headers do not guarantee exported storage: publishing includes only operations
used by the extension, and their globals must be provided by the server or a
linked native library.

### Native function pointers

Function-pointer fields, globals, parameters and results use generated readonly
value types. Each type preserves the selected headers' exact signature. Typedef
aliases and object qualifiers share one managed type; its name comes from the
first typedef in ordinal order, or a generated name for an unnamed signature.

Named record fields also expose a `<Record>_<Field>Callback` type. Use these
names for method-table callbacks that have no C typedef; their names come from
the record and field rather than a collected type index. For example, given
an initialized `CustomExecMethods` table and a valid `CustomScanState` address:

```csharp
unsafe
{
    CustomExecMethods_ExecCustomScanCallback execute = methods.ExecCustomScan;
    methods.ExecCustomScan = execute;
    TupleTableSlot* slot = execute.Invoke(scanStateAddress);
}
```

Conversions between the field-specific type and the field's canonical type
preserve the address and signature. Native callback arrays expose the same
field-specific type for their elements. A collision with another generated
declaration adds a numeric suffix, following the companion's naming rules.
These values keep the same caller-owned lifetime and guarded invocation rules.
Selected globals likewise expose a `NativeGlobals_<Global>Callback` type,
including array elements. For example, the shared-memory startup hook has a
stable name even when an unrelated typedef becomes the canonical name for its
signature in another PostgreSQL version:

```csharp
unsafe
{
    NativeGlobals_shmem_startup_hookCallback previous = NativeGlobals.shmem_startup_hook;
}
```

Use a field- or global-specific type with `[PgNativeCallback]` to declare a
managed handler without depending on a generated index or an unrelated typedef.
These types convert to and from the canonical type without changing its storage,
native signature, or ownership. Const globals remain read-only; a named callback
type does not grant permission to modify the native object.

For a fixed prototype with complete argument and result types, `Invoke` calls
the current target through the native error guard. For example, given an
initialized native `FmgrInfo` value and a valid, populated native function-call
frame:

```csharp
unsafe
{
    PGFunction target = functionInfo.fn_addr;
    ulong datum = target.Invoke(callInfoAddress);
}
```

This low-level call preserves the native datum bits. The caller supplies the
correct frame, arguments, collation and lifetime, and reads SQL NULL from the
frame's `isnull` field. Prefer [`PgFunctions.Call<T>`](/calling-functions/) for
ordinary SQL function invocation.

Every `Invoke` requires an active backend callback and the matching native
binding. A null target throws `InvalidOperationException` before native lookup
or frame allocation. Native errors become `PgException`; use an explicit
subtransaction when recovery needs rollback, as with fixed native functions.
Publishing includes only the invocation bodies used by the extension.

`IsNull` inspects the stored address. `DangerousGetAddress()` returns `void*`, and
the constructor accepts `void*` whose signature and lifetime the caller guarantees.
Copying or storing a value does not extend its target's lifetime. Use
`[PgNativeCallback]` to obtain a guarded native address for a static managed handler.

Variadic, unprototyped and incomplete-result function pointers retain typed
address storage but have no `Invoke` method. Ankus does not infer missing call
information or replace the native calling convention with a managed guess.

### Managed native callbacks and hooks

Apply `[PgNativeCallback]` to a static partial getter-only property whose type is
a generated native function pointer. Its named handler must be synchronous,
static, non-generic, and match that type's `Invoke` parameters and return value
exactly. Private handlers are supported. The containing types must be
non-generic partial classes, structs or records.

Do not apply `System.Diagnostics.Conditional` to the handler or either part of a
partial handler. C# can omit calls to conditional methods while the native
wrapper still reports success. `ANKUS276` points at that attribute; remove it
so every native invocation executes the handler. This rule applies even when
the conditional symbol is currently defined.

#### Native callback declaration diagnostics

Callback errors identify the property, containing type, pointer type or
handler signature that needs correction. They preserve the native prototype
and reject an invalid declaration before emitting its callable boundary.

| Diagnostics | Correction |
| --- | --- |
| `ANKUS310`–`ANKUS318` | Apply one callback attribute to a static partial getter-only property. Do not combine it with a GUC, an indexer, a reference result, a setter or an authored property implementation. |
| `ANKUS319`–`ANKUS322` | Put the property in non-generic partial classes, structs or records. Every containing declaration must be partial and accessible from generated files, without `file`. |
| `ANKUS323`–`ANKUS326` | Use the generated native function-pointer type from the selected PostgreSQL binding contract. It must retain its exact fixed `Invoke` signature and native-address constructor. |
| `ANKUS327`–`ANKUS331` | Name one ordinary handler method with the exact native prototype. Correct missing, ambiguous or nonmatching overloads. |
| `ANKUS332`, `ANKUS334`–`ANKUS338` | Use an implemented static synchronous handler, without generic arguments, `extern` or variable native arguments. A partial handler needs its implementation. |
| `ANKUS339`, `ANKUS340`, `ANKUS344` | Return the exact supported native result type by value. |
| `ANKUS341`, `ANKUS342`, `ANKUS345`, `ANKUS346` | Match every native argument's type and position, passing it by value. |
| `ANKUS343` | Remove `UnmanagedCallersOnly` from the managed handler; Ankus generates the native entry boundary. |

These checks preserve valid overload selection, private handlers and exact
pointer, enum and native-structure values. `ANKUS276` continues to reject
conditional handlers whose managed invocation could disappear.

For example, install an executor hook and retain the previous hook explicitly:

```csharp
using Ankus;
using Ankus.Postgres;

public static unsafe partial class ExecutorHooks
{
    private static ExecutorStart_hook_type s_previous;
    private static bool s_installed;

    [PgNativeCallback(nameof(OnExecutorStart))]
    private static partial ExecutorStart_hook_type Start { get; }

    [PgModuleLoad]
    public static void Initialize()
    {
        if (s_installed)
        {
            return;
        }

        s_previous = NativeGlobals.ExecutorStart_hook;
        NativeGlobals.ExecutorStart_hook = Start;
        s_installed = true;
    }

    private static void OnExecutorStart(QueryDesc* query, int flags)
    {
        if (s_previous.IsNull)
        {
            NativeMethods.standard_ExecutorStart(query, flags);
        }
        else
        {
            s_previous.Invoke(query, flags);
        }
    }
}
```

The generated wrapper uses the selected PostgreSQL headers' native signature
and calling convention. Its address remains stable for the loaded extension's
lifetime; two callback properties with the same signature have independent
addresses. No delegate or additional GC root is needed. Publishing includes only
callbacks used by the extension.

Callback declarations can also live in a referenced project using the same native
binding contract. The consuming extension supplies the native dispatch boundary;
its registration method explicitly calls the provider's installation code.
Use [`PgModuleLoad`](/initialization/#native-hook-and-provider-registration) for
hooks that must run on a worker's first query.

Reading a callback property requires an active backend or initialization
callback and the matching native binding. Invoke handlers synchronously on the
PostgreSQL thread. Native pointers remain caller-owned: obey the specific hook's
argument validity, transaction rules and lifetime. The raw callback signature
does not infer SQL NULL or turn a datum into a managed SQL value.

Managed exceptions unwind before the native wrapper raises PostgreSQL ERROR.
`PgException` retains SQLSTATE, message, detail and hint; other managed exceptions
use `38000`. Nested callbacks restore the surrounding backend and memory scopes.
When catching a backend error and continuing work requires rollback, use
[`PgTransaction.RunInSubtransaction`](/transaction-callbacks/#recoverable-work).

Hook ownership is explicit. Chain to the previous hook or the PostgreSQL
fallback appropriate to that hook. Restore the saved hook only while yours is
still the installed head; replacing it after another extension has installed a
hook would discard that extension's chain. Dropping SQL declarations does not
unload the native module or automatically unregister hooks.

Make each installation idempotent. A failed `PgModuleLoad` runs again on the
next load, or before the next invocation of a callback that the failed attempt
already installed. Record each hook's installation immediately after assigning
it, as `s_installed` does above, so a retry never saves your own hook as the
previous hook. Checking whether the global still holds your callback is not
enough once another library has chained after yours. If a callback reaches
itself through such a cycle anyway, the native callback boundary raises
PostgreSQL's `stack depth limit exceeded` error (SQLSTATE `54001`) instead of
overflowing the native stack. The
[hooks sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.Hooks)
ports pgrx's `hooks` example with three independently recorded installations.

`[PgOutputPlugin]` exports a static method as `_PG_output_plugin_init`, which
receives PostgreSQL's logical decoding callback table and assigns callback
properties. See [logical decoding output plugins](/logical-decoding/) and the
[WAL decoder sample](https://github.com/willibrandon/ankus/tree/main/samples/Ankus.Examples.WalDecoder).

An `emit_log_hook` handler receives borrowed `ErrorData*`. Copy any fields you
need to retain before returning or invoking another hook. PostgreSQL permits
the hook to disable `output_to_server`; other changes to the diagnostic are
unsupported.

Reporting from this hook invokes it again, so bound any recursive reporting.
PostgreSQL's recursive reporter can reset its error memory context and replaces
non-ASCII bytes with `?` when sending nested diagnostics to the client. The hook
can still copy the original text before that client conversion.

## Raw SQL values

Use `PgDatum` with `[PgSqlType]` for a type without a built-in C# mapping:

```csharp
[PgFunction]
[return: PgSqlType("pg_lsn", Schema = "pg_catalog")]
public static PgDatum? Echo(
    [PgSqlType("pg_lsn", Schema = "pg_catalog")] PgDatum? value) => value;
```

```sql
SELECT echo('0/1234'::pg_lsn); -- 0/1234 on PostgreSQL 13–18
SELECT echo(NULL::pg_lsn);    -- NULL
```

Native text output follows the selected server version. PostgreSQL 19 formats
this `pg_lsn` value as `0/00001234`; its underlying value is unchanged.

Every raw parameter and result needs a binding. `Name` is the exact catalog
identifier, such as `int4`, without quotes or a schema prefix. `Schema` selects
a fixed schema; omitting it uses the installation search path. `IsArray = true`
binds an entire array of the named type, preserving shape and NULL cells.

`TypeOid` retains type identity, including domains. `Read<T>()` converts to a supported
C# representation; `ToPostgresString()` calls the type's output function. A nullable wrapper
accepts SQL NULL. A zero datum is a present value, not NULL.

For an array datum, `using PgArrayView view = value.Read<PgArrayView>()` borrows
its native cells under the source lifetime. It retains exact array and element
OIDs, dimensions, lower bounds and typed NULL cells. Dispose the view before its
owner ends; copy `view.Datum` or a cell to another context when it must outlive
that owner. Resetting only the source context invalidates borrowed aliases too;
live child contexts do not extend the lifetime of their source bytes.
See [borrowed native arrays](/arrays/#borrowed-native-arrays) for
direct function parameters, native flattening and callback cleanup.
`value.Read<PgArrayView<T>>()` adds checked scalar cell conversion under the same
source lifetime. Whole-array SQL NULL becomes a null view after validating the
declared element type. See [typed borrowed cells](/arrays/#typed-borrowed-cells).

For text and binary datums, `Read<PgTextView>()` and `Read<PgByteaView>()` borrow
the source lifetime. They retain original SQL identity, including domains,
and release their private detoast or encoding storage when disposed. Text views
expose validated UTF-8 bytes while `Datum` retains the original server encoding;
raw `varchar` and padded `bpchar` values keep their identity and trailing spaces.
Use `ToString()` or `ToArray()` for independent managed copies. See
[text and binary values](/text-and-binary/) for callback and direct-span rules.

For `cstring`, `Read<PgCString>()` copies exact bytes and
`Read<PgCStringView>()` borrows terminated native storage. Neither transcodes
the bytes. PostgreSQL type-I/O calls can supply a zero C-string address with
`IsNull` still false; typed C-string reads return null for that address while
raw metadata retains the original flag. `CopyTo` preserves that distinction.

Reading, formatting or copying an existing domain value does not rerun its
constraints. This preserves historical values after `ADD CHECK ... NOT VALID`
and domain-typed NULLs produced by outer joins, including for NOT NULL domains.
Explicit Ankus parameter and result assignments still check current domain
constraints. Formatting invokes the selected output function, and mapped readers
retain their own conversion behavior.

Inputs belong to the current function call or iterator. Use `CopyTo(context)` to
keep one longer. Returned values must have the declared type and a live owner;
Ankus checks both before using their native storage. These bindings also work
in aggregate support methods, operators, casts, and `IEnumerable<PgDatum?>`
sets. For TABLE results, set `Column` to each raw column's SQL name.

When your extension creates the bound type, declare its SQL block with
`PgSqlTypeProvider` to order these signatures automatically. Type names and
optional schemas match exactly; a provider does not change datum conversion or
ownership. See [custom SQL](../custom-sql/#declare-supplied-types).

## Custom type representation

Create the SQL type and supply its input/output functions. This example stores
an unsigned 24-bit integer directly in PostgreSQL's datum word:

```csharp
using Ankus;
using System.Globalization;

[assembly: PgSql("u24-shell", "CREATE TYPE u24;")]
[assembly: PgSql("u24-type",
    "CREATE TYPE u24 (INPUT=u24_in, OUTPUT=u24_out, LIKE=int4);",
    Requires = ["u24-in", "u24-out"])]
[assembly: PgSqlTypeProvider("u24-type", "u24")]

public static class U24
{
    [PgFunction(Name = "u24_in", Id = "u24-in", Requires = ["u24-shell"])]
    [return: PgSqlType("u24")]
    public static PgDatum Input(
        [PgSqlType("cstring", Schema = "pg_catalog")] PgDatum text,
        PgFunctionContext call)
    {
        if (!uint.TryParse(text.ToPostgresString(), NumberStyles.None,
            CultureInfo.InvariantCulture, out uint value) || value > 0xFFFFFF)
        {
            throw new PgException("22003", "Value must be between 0 and 16777215.");
        }

        unsafe
        {
            return PgDatum.DangerousCreate(value, call.ResultTypeOid, PgMemoryContext.Current);
        }
    }

    [PgFunction(Name = "u24_out", Id = "u24-out", Requires = ["u24-shell"])]
    [return: PgSqlType("cstring", Schema = "pg_catalog")]
    public static PgDatum Output([PgSqlType("u24")] PgDatum value)
        => PgFunctions.CallRaw("pg_catalog.int4out", PgMemoryContext.Current,
            PgFunctionArgument.Create(checked((int)value.DangerousGetBits())));
}
```

```sql
SELECT '16777215'::u24; -- 16777215
```

The provider places other `u24` signatures after the completed type. Its explicit
requirements preserve shell → input/output → completion ordering for the two
functions needed to define the type. Additional consumers do not need to repeat
`Requires = ["u24-type"]`.

`DangerousCreate` requires an explicit `unsafe` context and a representation
that matches the SQL type. For a
pointer-based value, its native storage must remain valid for the chosen owner.
Creating a handle does not copy that storage or take ownership of it.

## Reusable scalar mappings

Use `[PgDatumType]` when several functions should share a managed representation
and converter. For the `u24` SQL type above:

```csharp
[assembly: PgSqlTypeProvider("u24-type", typeof(Unsigned24))]

[PgDatumType("u24", typeof(Unsigned24Converter))]
public readonly record struct Unsigned24(uint Value);

public sealed class Unsigned24Converter :
    IPgDatumReader<Unsigned24>, IPgDatumWriter<Unsigned24>
{
    public Unsigned24 Read(PgDatum value)
        => new(checked((uint)value.DangerousGetBits()));

    public PgDatum Write(Unsigned24 value, uint typeOid, PgMemoryContext destination)
    {
        if (value.Value > 0xFFFFFF)
        {
            throw new PgException("22003", "Value exceeds 24 bits.");
        }

        unsafe
        {
            return PgDatum.DangerousCreate(value.Value, typeOid, destination);
        }
    }
}

public static class MappedFunctions
{
    [PgFunction]
    public static Unsigned24? EchoMapped(Unsigned24? value) => value;
}
```

The mapping supplies conversion and SQL signature metadata. The existing SQL
block and input/output functions still define the type. A converter can read or
write by-value, fixed-size by-reference, or variable-length storage; it must
follow that SQL type's actual representation. It has an accessible parameterless
constructor and implements either interface or both for the exact managed type.
Ankus creates one converter lazily when a present value needs it. Registration
does not construct user code or access PostgreSQL catalogs.

A default generic declaration such as `Box<T>` can provide finite managed views
of one SQL type. The generator selects fully constructed roots found in supported
function or aggregate signatures, or in an exact managed `PgSqlTypeProvider`
declaration. For example, `Box<int>` and `Box<long>` can share the fixed name,
schema and origin on `Box<T>` while a closed converter
implements `IPgDatumReader<Box<int>>` and `IPgDatumReader<Box<long>>` separately.
Each requested CLR construction selects its own lazy registration; an unused
`Box<T>` declaration creates none. A constructed containing type, such as
`Outer<int>.Value`, also retains its exact managed identity. For an owned SQL
type, declare a provider for **each** closed construction that uses it, even if
the providers name the same completion block.

For independent SQL identities, use explicit closed declarations on the owning
managed type:

```csharp
[PgDatumType(typeof(NumberBox<int>), "int4", typeof(IntBoxConverter),
    Origin = PgTypeOrigin.External, Schema = "pg_catalog")]
[PgDatumType(typeof(NumberBox<long>), "int8", typeof(LongBoxConverter),
    Origin = PgTypeOrigin.External, Schema = "pg_catalog")]
public readonly record struct NumberBox<T>(T Value);
```

Here `IntBoxConverter` implements the reader and/or writer for
`NumberBox<int>`, while `LongBoxConverter` implements the corresponding
`NumberBox<long>` interfaces. Each converter follows its selected SQL type's
actual representation. The explicit type must be fully closed and have the
annotated declaration as its definition, including any constructed containing
types. Local explicit declarations register their roots even when used only
inside raw or SPI method bodies. They do not construct converters or look up
catalog OIDs during registration.

One exact declaration takes precedence over an optional default declaration,
regardless of attribute order. Duplicate exact targets or multiple defaults are
errors. Without a default, using an unlisted construction such as
`NumberBox<decimal>` fails during source generation. Every owned construction
still needs its exact managed provider, and distinct SQL types need their own
completed declarations.

The converter may also be an open generic definition such as
`typeof(NumberBoxConverter<>)`. Given
`NumberBoxConverter<T> : IPgDatumReader<NumberBox<T>>, IPgDatumWriter<NumberBox<T>>`,
Ankus infers `NumberBoxConverter<int>` for `NumberBox<int>` and
`NumberBoxConverter<long>` for `NumberBox<long>`. This works with either default
or explicit mappings. The converter implementation must still follow the SQL
representation selected for each construction.

Inference follows the exact reader and writer interface patterns, including
inherited interfaces and constructed containing types. It does not copy type
arguments by position: `Family<TOuter>.Converter<TInner>` implementing
`IPgDatumReader<Pair<TInner, TOuter>>` closes as `Family<long>.Converter<int>`
for `Pair<int, long>`. A parameter may represent the entire non-generic root,
and compatible reader and writer patterns may jointly determine parameters.

Every converter and containing-type parameter must be determined, with exactly
one complete construction. Missing or ambiguous assignments and violated C#
constraints produce `ANKUS147`, `ANKUS148`, or `ANKUS149` before generation.
Constraints validate the unique result; they do not select between competing results. Supply an explicit
closed converter to resolve ambiguity. Ankus emits ordinary closed factories
for the selected finite roots, with separate lazy instances and no runtime
generic construction or reflection.

`IPgDatumReader<T>` converts SQL inputs into detached managed values. Copy native
data before returning; storing the input `PgDatum` in a field does not extend its
lifetime. `IPgDatumWriter<T>` receives the current target OID and an operation's
destination context. Allocate or copy pointer-based results into that context,
and keep their storage live until PostgreSQL consumes the returned datum. Do not
cache the OID or context in the converter. Ankus validates exact OIDs and native
owners before reading or writing, including typed NULL handles.

Nullable CLR absence bypasses the converter. A present zero remains present.
A reader must return a non-null managed value for a present SQL input. A writer
may deliberately return SQL NULL using a live `PgDatum` with `isNull: true` and
the supplied OID; returning a null handle is an error. PostgreSQL still checks
domain constraints when consuming the result. Converter exceptions unwind
through the managed boundary before PostgreSQL raises ERROR.

### Ownership and external types

The default `Origin = PgTypeOrigin.ThisExtension` requires one
`PgSqlTypeProvider` declaration for the exact managed type. An unqualified owned
mapping resolves through the invoking function's owning extension schema,
including after extension relocation. Set `Schema` for a fixed schema; owned
fixed schemas prevent relocation. Type and schema names are exact, unquoted
catalog identifiers, with a maximum of 63 UTF-8 bytes.

For an existing SQL type, set `Origin = PgTypeOrigin.External` and an explicit
schema:

```csharp
[PgDatumType("int4", typeof(CountReader),
    Origin = PgTypeOrigin.External, Schema = "pg_catalog")]
public readonly record struct Count(int Value);

public sealed class CountReader : IPgDatumReader<Count>
{
    public Count Read(PgDatum value) => new(value.Read<int>());
}
```

This read-only mapping can appear in function inputs, `PgDatum.Read<Count>()`,
`Spi.ExecuteScalar<Count>()`, or `PgFunctions.Call<Count>()`.
Returning a value from a generated managed callback, or supplying a typed
parameter, needs a writer. `PgFunctionArgument.Default<Count>()`
only supplies a type for PostgreSQL's default expression and needs no writer.
`SpiParameter.Create<Count?>(null)` still requires a writer even though SQL NULL
skips its invocation.

External mappings have no provider dependency. Their fixed type references alone
do not prevent extension relocation; generated operator families add fixed-schema
objects and therefore do. Multiple CLR wrappers can share one SQL type;
the requested CLR type selects the converter. Domains keep their own identity:
a sibling domain or its base type cannot be read through a domain mapping.
Catalog lookups use current OIDs. A retained typed parameter cannot silently
switch to a replacement type after its original type is dropped.

### Supported conversion paths

Mapped scalars work in ordinary function arguments/results, nullable values,
SETOF/TABLE outputs, aggregate support methods, and manual operator/cast functions.
Readable mappings can also declare `PgEquality`, `PgOrdering`, and `PgHashing`
without a writer. See [generated type operators](/operators-and-casts/#manual-datum-mappings)
for value contracts, type-provider ordering, schemas and index-class rules.
They also work in `SpiParameter.Create<T>`, `PgFunctionArgument.Create<T>`, typed
SPI scalar-result helpers, named/OID `PgFunctions.Call<T>`, and direct raw reads:

```csharp
Unsigned24 value;
using (SpiRawResult result = Spi.QueryRaw("SELECT '42'::u24"))
{
    value = result[0][0].Read<Unsigned24>();
}
// value is detached and remains usable after result disposal.
```

Typed scalar-result helpers select the requested mapping and dispose their
temporary native storage after its reader returns. Sessions, prepared statements,
and two- or three-column results follow the same rule. A missing reader is an
error before execution, even when SQL would return NULL or no rows. A present
NULL cell still checks exact type identity; an empty result follows the ordinary
nullable/reference absence rules without invoking a converter.

Catalog calls check the declared result's exact mapped OID before invoking the
function or evaluating its defaults. SPI result identity is checked after SQL
executes. A caught managed reader error does not roll back SQL that already
completed. See [SPI queries](/spi/#scalar-values) and
[calling PostgreSQL functions](/calling-functions/).

The same scalar reader/writer contracts compose into `T[]` and `PgArray<T>`,
including nullable value elements. Each array retains the exact current mapped
element type and its corresponding array type, even when empty or all-NULL.
Raw array reads detach their elements under a temporary owner; the original raw
array keeps its existing owner. Writers construct the complete array before
releasing temporary element storage. See [mapped array elements](/arrays/#mapped-elements)
for shape, domain, NULL and ownership rules.

Value-type mappings can add [`PgRangeType`](/ranges/#mapped-bounds) to reuse
their converter for finite `PgRange<T>` bounds and arrays of those ranges.
The range has its own SQL identity and provider. NULL ranges, empty ranges and
infinite ends skip scalar conversion; a finite bound writer returning SQL NULL
is rejected. The backend verifies the declared range's exact scalar subtype.

Use raw result owners and `Read<T>()` for mapped row fields. `SpiRow.Get<T>` and
`PgHeapTuple.Get<T>` do not convert canonical cells through these converters.
For native addresses, `DangerousCall<T>` selects the registered reader; use
`DangerousCallRaw` for an explicit native owner or delayed mapped read. The caller
must supply an address whose actual result matches the mapping's SQL type and
representation; native-address calls cannot check a catalog return declaration.
Nested mapped SQL array containers are rejected, as they are in pgrx. Use one
`PgArray<T>` to retain multiple dimensions of mapped scalar elements. Different
argument and result SQL spellings remain unsupported.
A default generic declaration needs a selected closed root: an
unused open template or a `PgDatum.Read<T>()` call in an arbitrary method body
cannot create one by itself. Add an explicit closed declaration for a local
raw-only root. Accessible closed nested CLR declarations are supported.
The generator rejects unsupported mapped signatures with a specific
[mapping diagnostic](#mapping-diagnostics).

Local non-generic annotated types are registered even when only used by raw APIs.
Default generic local declarations and types from a referenced assembly must
occur in a supported generated signature or an exact managed-type provider
declaration to become registration roots. Ambiguous externally aliased names are
rejected. A type cannot combine
`PgDatumType` with `PgType` or `PgEnum`, and mapped slots do not use per-parameter
`PgSqlType` or `PgCompositeType` overrides.

### Mapping diagnostics

Mapping errors identify the failed contract and the authored value or signature
that needs correction. Converter constraint errors name the inferred closed
converter and the C# compiler diagnostic ID of the violated constraint, such as
`CS0453`, at the authored converter. A mapping supplied by another project or
assembly reports errors at its consuming source declaration. Referenced names and schemas retain their
exact text; zero characters and malformed UTF-8 are rejected before registration.
C# namespaces with escaped identifiers, such as `@class`, work normally.
The selected mapping is read from its actual defining assembly and module;
an attribute with the same metadata name in another assembly cannot replace it.

| Diagnostic | Required correction |
| --- | --- |
| `ANKUS134` | Use a closed, concrete class, struct, or enum that is neither static nor ref-like. |
| `ANKUS135` | Make the managed carrier, its containing types, and its arguments accessible to generated code. |
| `ANKUS136` | Choose one storage contract: `PgDatumType`, `PgType`, or `PgEnum`. |
| `ANKUS137` | Add a mapping for the selected closed managed type or declare a default mapping. |
| `ANKUS138` | Supply a valid PostgreSQL type identifier of at most 63 UTF-8 bytes. |
| `ANKUS139` | Supply a valid PostgreSQL schema identifier of at most 63 UTF-8 bytes. |
| `ANKUS204` | Rebuild the defining assembly so its mapping attribute contains complete, valid UTF-8 metadata. |
| `ANKUS140` | Set `Origin` to `ThisExtension` or `External`. |
| `ANKUS141` | Specify `Schema` for an external SQL type. |
| `ANKUS142` | Supply a concrete converter class or struct with a finite closed construction. |
| `ANKUS143` | Make the converter, its containing types, and its arguments accessible to generated code. |
| `ANKUS144` | Provide an accessible parameterless converter constructor. |
| `ANKUS145` | Implement a reader or writer for the exact non-nullable mapped type. |
| `ANKUS146` | Initialize required members in a constructor marked `SetsRequiredMembers`. |
| `ANKUS147` | Specify a closed converter when its interface patterns cannot infer every argument. |
| `ANKUS148` | Specify a closed converter when several constructions match. |
| `ANKUS149` | Change the mapped type arguments or converter constraints so the named inferred converter satisfies the C# rule identified by the compiler diagnostic ID, or specify a closed converter. |
| `ANKUS150` | Give the carrier and converter unambiguous global type identities. |
| `ANKUS151` | Remove a slot's `PgSqlType` or `PgCompositeType` override. |
| `ANKUS152` | Implement `IPgDatumReader<T>` for a SQL input. |
| `ANKUS153` | Implement `IPgDatumWriter<T>` for a SQL result. |
| `ANKUS154` | Use a scalar or one array layer instead of a nested mapped container. |
| `ANKUS155` | Keep only one default mapping declaration. |
| `ANKUS156` | Select a closed construction of the annotated managed type. |
| `ANKUS157` | Keep only one exact mapping for each closed managed target. |
