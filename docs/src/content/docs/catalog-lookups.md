---
title: Catalog lookups and OIDs
description: Use versioned OID constants and resolve PostgreSQL type syntax and exact operator names.
---

Use [`PgRelation`](/relations/) for a locked, live relation reference and
`regclass` arguments or results. It provides relation names, namespace identity,
kind, estimates, tuple descriptors and related index references with explicit
cleanup.

`PgTypes` and `PgQualifiedNameBuilder` resolve current catalog identities inside
an extension callback. PostgreSQL performs the lookup, including search-path
selection, schema permissions and native error reporting. Results are copied
unsigned OIDs; lookups do not retain catalog pins or cache identities across DDL.

For routine metadata, use [`PgFunctions.GetInfo`](/calling-functions/#inspecting-routine-metadata).
It copies the function-catalog fields and retains immutable metadata across
callbacks and catalog changes. Native default trees have an explicit memory owner.

## OID values and built-in constants

PostgreSQL `oid` values use `uint` in Ankus. All 32 bits, including zero, remain
distinct from SQL NULL. OIDs identify objects within a particular catalog;
the same number can identify different objects in different catalogs. See
[PostgreSQL's object identifier documentation](https://www.postgresql.org/docs/18/datatype-oid.html).

`PgBuiltInOid` provides typed numeric constants from pgrx's PostgreSQL 13–18
and 19 beta catalogs. Cast a member to `uint` when an API needs a raw OID:

```csharp
uint integerType = (uint)PgBuiltInOid.Int4Oid; // 23
PgOid classified = PgOid.FromValue(integerType);
// classified.Kind == PgOidKind.BuiltIn
string? nativeName = PgBuiltInOids.GetNativeName(classified.BuiltIn!.Value);
// "INT4OID"
```

`PgOid` corresponds to pgrx's tagged `PgOid` helper. It retains `Invalid`,
`Custom` or `BuiltIn` alongside the exact number. `FromValue(0)` returns
`Invalid`; an unlisted nonzero value becomes `Custom`. Classification uses the
active PostgreSQL headers' major version and requires the calling backend thread.
It does not query catalogs for existence or permissions.

Each helper also accepts an explicit `postgresMajor` from 13 through 19 for use
outside PostgreSQL. `PgBuiltInOids.GetValues(major)` returns an immutable,
numerically ordered snapshot. `GetNativeName(member, major)` returns the exact
native spelling for that version, or null if the value is unlisted. For example,
the `MoneyOid` member has value 790: its native name is `CASHOID` in PostgreSQL 13
and `MONEYOID` from PostgreSQL 14. Enum names use the newest reference spelling;
the enum's presence alone does not establish availability on every version.

`PgBuiltInOids.TryFromValue` accepts an unsigned `ulong`, so a native datum word
can be checked without truncation. It reports `Invalid` for zero, `Ambiguous`
for an unlisted 32-bit value, and `TooBig` above `uint.MaxValue`. A successful
conversion reports `None` and the typed member. `PgOid.FromBuiltIn` rejects
undefined or unavailable members, such as an arbitrary value cast to the enum.

The catalog deliberately retains pgrx's constant-name heuristic. It includes
types, relations, selected functions and other constants, including some that
are not catalog object identifiers. It also omits many real built-in objects.
Use catalog lookup when existence or object category matters. A classified
custom value need not belong to an extension or even exist.

`PgOid.Custom(value)` explicitly retains its tag even for zero or a recognized
number. Equality includes both tag and value, so `Custom(23)` is distinct from
the classified built-in integer OID. To pass the tagged value as a raw datum,
use `ToDatum` with an explicit memory-context lifetime:

```csharp
PgDatum missing = PgOid.Invalid.ToDatum(PgMemoryContext.Current); // SQL NULL, type oid
PgDatum zero = PgOid.Custom(0).ToDatum(PgMemoryContext.Current);   // present zero, type oid
uint result = Spi.ExecuteScalar<uint>("SELECT $1::oid", SpiParameter.Create(zero));
```

Only the `Invalid` tag maps to SQL NULL, matching pgrx's `PgOid` datum conversion.
The ordinary `uint` mapping keeps zero present. For pgrx-style invalid-OID-to-NULL
output, classify the number and call `ToDatum`. These by-value datums use the
same checked context lifetime as other [raw values](/raw-values/); reset or
deletion invalidates native access. The classifier itself is a detached managed
value and has no native owner.

## Type syntax

```csharp
uint integer = PgTypes.GetOid("integer");
uint array = PgTypes.GetOid("integer[]");
uint domain = PgTypes.GetOid("\"My Schema\".\"My Type\"");
```

`GetOid` calls PostgreSQL's `regtypein`, corresponding to pgrx's `regtypein`
wrapper. It accepts the server's full type-name syntax: aliases such as
`double precision`, quoted identifiers, schema qualification, arrays and type
modifiers. Modifiers do not change the resolved OID: `numeric(12,3)` resolves
to `numeric`. Domains retain their own identity, and pseudotypes such as
`record` are valid lookup results.

A dash (`"-"`) or numeric zero returns OID zero. Numeric OIDs are accepted
without proving that a matching type exists. Resolving a name does not establish
that Ankus has a managed conversion for that type.

Missing or malformed names raise `PgException` with PostgreSQL's diagnostic.
Null input, embedded zero characters and invalid UTF-16 are rejected in managed
code. UTF-8 transport converts to the server encoding without replacing
unrepresentable characters.

`GetOidByManagedName<T>()` supplies `typeof(T).Name` to the same parser. This
corresponds to pgrx's `rust_regtypein<T>` convenience: it is useful when a SQL type
intentionally follows the managed type's short name. It omits namespaces and
declaring types, and preserves CLR array suffixes and generic arity markers.
PostgreSQL applies its normal unquoted-name rules.

This helper does not consult `[PgType]`, `[PgEnum]` or `[PgDatumType]` mappings,
infer declared schemas, or translate CLR primitive names. For example,
`GetOidByManagedName<int>()` looks up `Int32`, which PostgreSQL folds to `int32`;
use `GetOid("integer")` for PostgreSQL's built-in integer type. Use explicit SQL
syntax when the managed and database names differ.

## Qualified operator names

```csharp
PgQualifiedNameBuilder name = new() { "pg_catalog", "+" };
uint integer = PgTypes.GetOid("integer");
uint operation = name.GetOperatorOid(integer, integer);

uint prefixMinus = new PgQualifiedNameBuilder()
    .Add("pg_catalog").Add("-").GetOperatorOid(0, integer);
```

`Add` accepts one exact, unquoted catalog component and returns the same builder.
Dots, whitespace, case and quote characters remain part of that component;
Ankus does not parse SQL quoting or split a component at dots. For a schema named
`My.Schema`, add `"My.Schema"`, followed by the operator token. Do not add SQL
quote characters around the schema name.

The builder is a reusable managed `IReadOnlyList<string>`. Construction, indexing
and enumeration need no backend. Failed text validation leaves its components
unchanged. It is not thread-safe, and lookup requires the active backend thread.

`GetOperatorOid` calls native `OpernameGetOprid` with exact argument type OIDs:

| Components | Resolution |
| --- | --- |
| Operator | Current `search_path`, including native implicit `pg_catalog` rules |
| Schema, operator | Only the named schema |
| Database, schema, operator | Requires the current database, then resolves the schema |

The lookup performs no argument coercion. Pass zero as the left type for a prefix
operator. Zero as the right type permits legacy postfix lookup on servers that
support it. The raw name `!=` is not rewritten to `<>` by this API.

Missing operators and missing explicit schemas return zero. An invalid component
count, a foreign database, or missing schema `USAGE` permission raises
`PgException`. Unqualified operator lookup excludes the temporary namespace;
an explicit temporary schema follows PostgreSQL's native lookup behavior.

Every call reuses the builder's current components and observes the current
catalog and search path. Native list nodes and encoded strings live only in the
guarded operation's temporary context. Errors unwind below managed frames, so
ordinary `catch` and `finally` blocks can run and the same backend can recover.

For operator declarations, see [operators and casts](/operators-and-casts/).
To invoke functions, see [calling PostgreSQL functions](/calling-functions/).
