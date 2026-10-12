---
title: Configuration settings
description: Declare PostgreSQL settings with native storage and typed C# getters.
---

Declare a setting as a static partial getter in a partial class:

```csharp
public static partial class Settings
{
    [PgGucBool("my_extension.enabled", true, "Enable the extension")]
    public static partial bool Enabled { get; }

    [PgGucInt("my_extension.limit", 100, "Maximum items", Minimum = 1, Maximum = 10000)]
    public static partial int Limit { get; }

    [PgGucString("my_extension.label", null, "Optional label")]
    public static partial string? Label { get; }
}
```

The property, its containing classes and any hooks must be accessible from
generated code in the extension assembly. Use `public` or `internal`;
`protected internal` also works for members and nested classes. Editors offer
**Make internal** for a private or protected property or containing class, and
for a file-local class when no other type shares its name.

The source generator creates native PostgreSQL storage and implements the getters.
PostgreSQL registers the settings when it loads the library, before calling an
optional `[PgInitialize]` method. Read a getter from an extension callback to obtain
the current native value. Reads outside a backend callback or on another thread
throw `InvalidOperationException`.

Use ordinary SQL configuration commands to change values:

```sql
SET "my_extension.limit" = '250';
SHOW "my_extension.limit";
BEGIN;
SET LOCAL "my_extension.limit" = '50';
ROLLBACK;
RESET "my_extension.limit";
```

PostgreSQL owns parsing, permissions, source priority, transaction and savepoint
restoration, function `SET` clauses, reset values, and configuration reloads.
Custom placeholders set before the library loads are adopted by the native
definition. Invalid placeholder values produce PostgreSQL's diagnostics.
Registration survives transaction rollback and `DROP EXTENSION`; dropping SQL
objects does not unload a library or unregister its settings.

Startup defaults retain PostgreSQL's source priority: configuration file,
database, role, database-and-role, then client connection options. A session
`SET` overrides the active value; `RESET` restores the strongest applicable default.
`pg_settings.source` and `reset_val` expose that distinction. Check hooks receive
the original source when a deferred definition adopts a startup placeholder,
including definitions loaded through `session_preload_libraries`.

Placeholder adoption retains the original privilege context. Loading a library
as a superuser does not authorize a setting previously supplied without the
required privileges. PostgreSQL 15 and later also retain the setter's role
identity and recheck applicable parameter grants during adoption, including
separately saved `SET` and `SET LOCAL` states. PostgreSQL 13 and 14 use their
native privilege-context checks and do not have per-parameter grants. A rejected
replay warns and preserves the preceding accepted value.

## Reserving a prefix

Declare a prefix at assembly scope to catch unknown settings after registration:

```csharp
[assembly: PgGucPrefix("my_extension")]
```

Ankus registers all declared settings first, checks the prefixes, then calls an
optional `[PgInitialize]` method. Prefix declarations also work in a library with
no settings or managed callbacks, including native-only shared preload.

On PostgreSQL 15 and later, PostgreSQL warns about and removes existing unknown
placeholders under the prefix, then rejects new placeholders with that first
component. Declared settings retain their adopted values. PostgreSQL 13 and 14
only warn with SQLSTATE `42704`; they retain the placeholders and allow new
ones. On PostgreSQL 15 and later, unknown placeholders produce `42602`, and
reservation survives transaction rollback and repeated library loading.

Prefix matching is literal and case sensitive, even though setting lookup is
case insensitive. Use the same spelling consistently. Ankus passes the supplied
text without normalizing it: a dotted prefix can remove matching existing
placeholders but does not reserve a first component against future ones. An
empty prefix has no effect. Null, embedded zero characters, and malformed
Unicode have separate [declaration diagnostics](#prefix-declaration-diagnostics). Non-ASCII prefixes use the backend's database encoding;
shared-preload prefixes currently must be ASCII.

### Prefix declaration diagnostics

Each error identifies the constructor argument that needs correction, including
named arguments and constant-field references. Invalid prefixes do not generate
native calls; generated output for valid sibling prefixes remains intact.

| Diagnostic | Required correction |
| --- | --- |
| `ANKUS347` | Supply a nonnull constant string. An empty prefix has no effect. |
| `ANKUS348` | Remove embedded zero characters, which would truncate the native C string. |
| `ANKUS349` | Replace unpaired UTF-16 surrogates with well-formed Unicode. Ankus preserves exact UTF-8 text without replacement. |

## Types and metadata

| Attribute | Property type | Additional metadata |
| --- | --- | --- |
| `PgGucBool` | `bool` | Native Boolean spelling and synonyms |
| `PgGucInt` | `int` | Inclusive `Minimum` and `Maximum` |
| `PgGucReal` | `double` | Inclusive `Minimum` and `Maximum` |
| `PgGucString` | `string` or `string?` | Nullable boot value requires a nullable property |
| `PgGucEnum` | A C# enum | Optional `PgGucLabel` names and hidden labels |

Every declaration accepts a name, a constant default value, and a short
description. Optional properties include `LongDescription`, `Context`, `Flags`,
`Unit`, `Check`, `Assign`, and `Show`. Names must be qualified PostgreSQL parameter
names, such as `my_extension.limit`. Invalid declarations report a [declaration diagnostic](#declaration-diagnostics).

Strings are owned managed copies. A null boot/reset string remains distinct from
an empty string in the typed getter, although ordinary `SHOW` displays both as
empty text. Nonnullable string properties require a nonnull default and cannot
accept a null result from a check hook.

GUC enums are independent of SQL catalog enums:

```csharp
public enum Mode : ulong
{
    [PgGucLabel("normal")]
    Normal = ulong.MaxValue,

    [PgGucLabel("experimental", Hidden = true)]
    Experimental = 1UL << 63,
}

public static partial class Settings
{
    [PgGucEnum("my_extension.mode", Mode.Normal, "Execution mode")]
    public static partial Mode Mode { get; }
}
```

Generated ordinal mappings preserve the managed enum's underlying values.
Aliases share an ordinal, and the first declared label is the canonical display
name. Native label matching is case insensitive. Hidden labels remain accepted
inputs but are omitted from available-value hints and `pg_settings.enumvals`.
The enum may come from another project or assembly. Labels retain their exact
text in both cases. Invalid referenced labels report the error at the consuming
setting's property type. C# namespaces with escaped identifiers, such as `@class`,
work normally.

`PgGucContext` expresses PostgreSQL's seven contexts: `Internal`, `Postmaster`,
`Sighup`, `SuperuserBackend`, `Backend`, `SuperuserSet`, and `UserSet`. Native
privilege checks remain authoritative, including parameter privileges where the
server supports them.

Combine `PgGucOptions` values through the attribute's `Flags` property. These
include listing/reset behavior, client reporting, restricted visibility,
identifier-sized strings, configuration-file restrictions, security restrictions,
and EXPLAIN settings. `RuntimeComputed` requires PostgreSQL 15 or later.
`PgGucUnit` separately selects one memory or time unit for numeric settings;
block conversions use the selected server's compiled block sizes.

Options map to the native flags rather than adding an independent policy. In
PostgreSQL 18, `DisallowInFile` blocks `ALTER SYSTEM` and affects configuration
help, but does not itself reject a custom `UserSet` parameter supplied manually
in a configuration file. Choose the appropriate context and check hook for
additional restrictions.

## Declaration diagnostics

Configuration diagnostics identify the setting argument, property type, enum
label, or containing declaration that needs correction. An invalid setting
produces a diagnostic before its getter or native registration is generated.

Referenced enum labels come from their selected attribute's defining assembly
and module. Ankus validates the entire encoded attribute, including named
options, before registering the setting. Incomplete metadata or trailing
attribute data produces `ANKUS197` at the consuming setting; rebuild the
defining assembly with a complete valid attribute.

| Diagnostic | Required correction |
| --- | --- |
| `ANKUS169` | Declare exactly one typed GUC attribute on the property. |
| `ANKUS170`–`ANKUS175`, `ANKUS203` | Use an accessible static partial getter-only property, without an indexer, reference return, or authored implementation. |
| `ANKUS176`–`ANKUS180` | Put the property in accessible, non-generic, non-file-local partial classes, including every enclosing class. |
| `ANKUS181` | Use a valid dotted custom setting name. |
| `ANKUS182`, `ANKUS184` | Use valid description text without zero characters. The short description cannot be null. |
| `ANKUS183` | Match the property type to its typed GUC attribute. |
| `ANKUS185`–`ANKUS187` | Use declared context, flag, and unit values. |
| `ANKUS188`, `ANKUS189` | Apply `IsName` only to strings, and units only to integer or real settings. |
| `ANKUS190`, `ANKUS192` | Order the minimum and maximum bounds. Real bounds cannot be NaN. |
| `ANKUS191`, `ANKUS193` | Put the default within the declared bounds. Real defaults cannot be NaN. |
| `ANKUS194`, `ANKUS195` | Use valid string default text. A null default requires `string?`. |
| `ANKUS196` | Select a declared value of the property's enum type. |
| `ANKUS197` | Use valid enum labels and complete, readable attribute metadata in referenced assemblies. |
| `ANKUS198` | Give labels distinct names under PostgreSQL's ASCII case folding. |
| `ANKUS199`–`ANKUS201` | Match the check, assignment, or display hook's exact typed signature. The message names the first problem, such as a missing method, an overload or a wrong return type. |
| `ANKUS202` | Give each setting a distinct name under PostgreSQL's ASCII case folding. |

Editors fix the common declaration mistakes: **Make static** and **Make
partial** turn an automatic getter into the static partial definition Ankus
implements, **Remove the implementation** deletes an authored getter, and
**Make partial** on a class adds the modifier its generated part needs.
**Remove the `set` accessor** leaves only the getter (`ANKUS171`), **Return by
value** removes `ref` (`ANKUS172`), and **Change the type** uses the type the
attribute requires (`ANKUS183`) or `string?` for a null default (`ANKUS195`).
**Swap `Minimum` and `Maximum`** corrects reversed bounds (`ANKUS190`,
`ANKUS192`), and **Remove** deletes `Unit` from a setting that has no units
(`ANKUS189`). A correction that would break other code, such as an assignment to
the removed setter, leaves the source unchanged.

## Hooks

Name an accessible synchronous static hook in the property's containing class:

```csharp
public static partial class Settings
{
    [PgGucInt("my_extension.batch", 10, "Batch size", Check = nameof(CheckBatch))]
    public static partial int Batch { get; }

    internal static PgGucCheckResult<int> CheckBatch(int proposed, PgGucSource source)
    {
        if (proposed <= 0)
        {
            return new(new PgGucCheckError("Batch size must be positive."));
        }

        return new(proposed);
    }
}
```

For a property of type `T`, hook signatures are:

```csharp
PgGucCheckResult<T> Check(T proposed, PgGucSource source);
void Assign(T accepted, PgGucExtra? extra);
string Show(T current, PgGucExtra? extra);
```

A check result can accept a normalized value with optional `PgGucExtra`, or reject
it with `PgGucCheckError`. Native numeric bounds validate the proposed input;
PostgreSQL does not revalidate a numeric replacement accepted by the hook.
Extra data is an immutable copied byte sequence; null
and empty data are distinct. PostgreSQL retains a flat native allocation for the
accepted value and its reset/transaction history. Assign and show receive owned
managed copies which can outlive that native allocation.

Checks can run for boot defaults, file reloads, startup options, ordinary `SET`,
and validation-only operations such as `ALTER ROLE` or function `SET` clauses.
Validation does not guarantee assignment. A rejected result preserves native
severity selection: an interactive change can raise ERROR while placeholder
adoption can warn. A null diagnostic message keeps PostgreSQL's default message.
Thrown managed exceptions are contained and converted to native check diagnostics.
One check differs: the boot value check that runs while a library load
registers the setting. An exception thrown there fails the load with an ERROR,
and the backend can load the library again. A rejected boot value instead ends
the backend with FATAL, which is PostgreSQL's policy for an invalid default.
Actual PostgreSQL errors during native conversion or unrecovered backend calls
propagate for rollback; they cannot become a recoverable check rejection.

Assign runs before PostgreSQL changes the backing value. Reading the property in
assign returns the old value. Restoration calls assign with previously accepted
extra data without running check again. Assign hooks must not fail: an unexpected
exception terminates that backend with FATAL, since continuing could leave
partially restored state.

Show changes display text without changing typed storage or boot/reset metadata.
It must return a nonnull string. A show failure raises ERROR during a transaction;
outside a transaction, it terminates the backend with FATAL. PostgreSQL 13 reports
changed client parameters during `SET`, so a display failure rolls back the
change and leaves the backend usable. PostgreSQL 14 and later defer reporting
until the command finishes; a display failure after an autocommitted `SET`
therefore terminates that backend.

Typed setting reads are available in every hook. Check hooks can use guarded SQL
when PostgreSQL has a valid transaction. Assign and show cannot use SQL, including
during ordinary `SET`, because PostgreSQL also calls them during restoration and
outside transactions. `PgLog` is available in every hook through a separate native
binding, including file reloads, rollback restoration, and client parameter
reporting. It preserves filtering, structured fields, and database encoding without
enabling SQL. Explicit `Fatal` and `Panic` reports retain their severity after
managed code unwinds. An unhandled `Error` follows the hook's failure policy:
check rejection, assign FATAL, or show ERROR/FATAL according to transaction state.

A typed read with an unknown Ankus setting name or a mismatched reader throws
`PgException` with SQLSTATE `42704` or `42804`. These are API validation errors
and can be caught without aborting a transaction. Reads and other permitted
backend calls remain available afterwards. Actual PostgreSQL errors during a
read still require rollback and cannot be swallowed by an ordinary `catch`.

## Run-time definitions

When a name or its metadata is known only at run time, define the setting with
`PgGucRegistry`, as a pgrx extension calls `GucRegistry` from `_PG_init`.
Call it from a `[PgModuleLoad]` method and keep the returned reader:

```csharp
public static class Settings
{
    private static PgGucSetting<int>? s_limit;

    [PgModuleLoad]
    public static void Load()
    {
        string prefix = "my_extension";
        s_limit = PgGucRegistry.DefineInt(prefix + ".limit", 100, "Maximum items",
            minimum: 1, maximum: 10000);
    }

    [PgFunction]
    public static int CurrentLimit() => s_limit!.Value;
}
```

`DefineBool`, `DefineInt`, `DefineReal`, `DefineString` and `DefineEnum` accept
the same contexts, flags and units as the attributes. `DefineEnum` takes its
labels as `PgGucEnumOption` values, including aliases and hidden labels.
`Value` reads PostgreSQL's current native value, with the same thread and
callback rules as generated getters. PostgreSQL owns parsing, permissions,
source priority, `SET`, `RESET`, transaction restoration and placeholder
adoption, exactly as for attribute-declared settings.

Names follow the same dotted rules as the attributes; an invalid name or
metadata throws an `ArgumentException` before PostgreSQL is called. Defining a
setting again with identical metadata returns another reader, so a retried
`PgModuleLoad` is harmless. A different definition with the same name, or a name
declared by an attribute, fails with SQLSTATE `42710`. A `Postmaster` setting
can be defined only while `shared_preload_libraries` is processed.

Pass `PgGucHooks<T>` for check, assign and show hooks, as pgrx's
`define_*_guc_with_hooks` functions take hook functions. They follow the
[hook rules](#hooks) above; an enumerated setting's hooks see label ordinals:

```csharp
s_limit = PgGucRegistry.DefineInt(prefix + ".limit", 100, "Maximum items", minimum: 1, maximum: 10000,
    hooks: new()
    {
        Check = static (proposed, _) => proposed % 10 == 0
            ? new(proposed)
            : new(new PgGucCheckError("Use a multiple of 10.")),
        Show = static (current, _) => $"{current} items",
    });
```

An extension can define up to 64 run-time settings with hooks. Defining one
again must pass the same hook methods; different hooks fail with `42710`.
Prefer the attributes for names known at compile time: they add compile-time
validation and typed C# enums.

## Parallel queries

PostgreSQL restores configuration in each parallel worker. Managed state starts
independently in that process; check hooks run there to reconstruct extra data
from the propagated value and source. Do not treat managed static fields or
previously returned extra objects as shared state between workers.

Worker check hooks can normalize a propagated value. Native rollback history
retains that accepted value together with its worker-owned extra data through
error cleanup and function-local restoration.

Native PostgreSQL serialization preserves an untouched default null string by
leaving it at its default. A nondefault null string serializes as empty text, so
the worker's check hook receives an empty string. This distinction also applies
when a leader's check hook normalized a nonnull input to null.

Ordinary configuration changes are forbidden during a parallel operation.
PostgreSQL permits a function's declared `SET` clause and restores that worker's
previous value and extra data when the function returns. Mark a function
parallel safe only when its complete behavior meets PostgreSQL's requirements.

## Preloading

Settings can be registered through `shared_preload_libraries`, including settings
with managed hooks or `[PgInitialize]`. `Postmaster` settings require this loading
mode. A late load is rejected before PostgreSQL's fatal late-registration path.

The configuration sample has a startup-only setting:

```ini
shared_preload_libraries = 'Ankus.Examples.Configuration'
ankus_configuration.startup = 11
```

Shared-preload names, descriptions, labels, and string defaults currently must be
ASCII: the postmaster has no database encoding to use for inherited metadata.
Backend registration supports server-encoded text. During postmaster startup,
managed hooks have no transaction, so `Spi` is unavailable. The same hooks can use
`Spi` later when PostgreSQL calls them inside a backend transaction.
