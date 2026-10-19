---
title: Custom SQL
description: Include installation SQL strings and files, replace generated declarations, and order dependencies.
---

## Include SQL text

Use an assembly-level `PgSql` attribute for installation SQL. Give each block a
unique dependency name:

```csharp
using Ankus;

[assembly: PgSql("report-table", """
    CREATE TABLE reporting.reports(id bigint PRIMARY KEY, title text NOT NULL);
    """, Requires = ["report-schema"])]

[PgSchema("reporting", Id = "report-schema")]
public static class Reports
{
    [PgFunction(Id = "report-count", Requires = ["report-table"])]
    public static long Count() => Spi.ExecuteScalar<long>("SELECT count(*) FROM reporting.reports");
}
```

Assembly attributes go after `using` directives and before namespace or type
declarations. SQL is trusted extension source: PostgreSQL executes it when
`CREATE EXTENSION` runs. Include the statement terminators yourself.

PostgreSQL records objects created by the script as extension members. A failed
installation rolls back its objects and data changes.

Empty and whitespace-only SQL blocks or tracked files are valid dependency
anchors. They participate in ordering, duplicate-name checks and cycle
detection even when they create no database objects. SQL text must be nonnull.

Authored SQL retains its exact text, including CR and CRLF characters inside
quoted values. Generated framing uses LF line endings.

Generated installation scripts retain the block's SQL text and add separate
source and dependency comments. `PgSqlFile` blocks also identify their SQL file.
The same comments survive [schema extraction](/getting-started/publishing/#inspect-installation-sql),
including extraction from a published library without source files beside it.

## Declare supplied types

Use `PgSqlTypeProvider` to identify a type created by a `PgSql` or `PgSqlFile`
block. Functions using that exact `PgSqlType` or `PgCompositeType` binding then
depend on the block automatically:

```csharp
using Ankus;

[assembly: PgSql("pair-definition", """
    CREATE TYPE reporting.pair AS (number integer, label text);
    """)]
[assembly: PgSqlTypeProvider("pair-definition", "pair", Schema = "reporting")]

[PgSchema("reporting")]
public static class Pairs
{
    [PgFunction]
    [return: PgCompositeType("pair", Schema = "reporting")]
    public static PgHeapTuple Echo(
        [PgCompositeType("pair", Schema = "reporting")] PgHeapTuple value) => value;
}
```

The provider block is also ordered after a `[PgSchema]` with the same name. Repeat the attribute
when one block supplies several types. Its first argument must name a SQL block,
including a tracked SQL file, rather than a function or schema dependency alias.

Type and schema names are exact, unquoted catalog identifiers. A dot or quote
inside `Name` is part of that one identifier. Matching is case-sensitive and
does not normalize aliases such as `integer` and `int4`. Omitting `Schema`
matches only bindings that also omit it, using the installation search path;
it does not inherit the consuming function's schema. A fixed provider schema
prevents relocation. Unqualified providers still need the SQL block's
`Relocatable = true` assertion to permit relocation.

Scalar parameters and results, SETOF results, individual TABLE columns,
aggregate helpers, operators and casts receive these dependencies. Raw whole
arrays and composite arrays use the element type's provider. Existing external
types need no provider. Generated `PgType` and `PgEnum` declarations already
supply their catalog identities, even when their SQL is disabled or replaced;
claiming the same identity again is an error. When a SQL block creates a type
whose `PgType` or `PgEnum` SQL is disabled, list that block in the declaration's
`Requires`.

Provider metadata describes ordering. It does not parse the SQL, generate a
managed conversion, or verify the type's native storage layout. PostgreSQL
validates the objects when the extension installs. Keep explicit dependencies
for SQL routines, default expressions and other objects used by custom SQL.

### Type provider diagnostics

Provider errors highlight the value to correct, including named or reordered
constructor arguments and an authored `Schema` assignment. Ankus preserves
valid identifier spelling and rejects values PostgreSQL would truncate.

| Diagnostic | Correction |
| --- | --- |
| `ANKUS378` | Supply a nonempty SQL block ID containing a non-whitespace character. |
| `ANKUS379` | Remove embedded zero characters from the block ID. |
| `ANKUS380` | Replace unpaired Unicode surrogates in the block ID. |
| `ANKUS381` | Name an existing `PgSql` or `PgSqlFile` block; function and schema aliases cannot supply types. |
| `ANKUS382` | Select a registered, closed `PgDatumType` or `PgRange<T>` mapping with the managed overload. |
| `ANKUS383` | Remove the provider for an external mapping, or declare ownership when the extension actually creates the type. |
| `ANKUS384` | Remove `Schema` from a managed provider, including an explicit null; its mapping supplies the schema. |
| `ANKUS385`, `ANKUS389` | Supply a nonempty catalog type name or schema. A named provider may use null for an unqualified schema. |
| `ANKUS386`, `ANKUS390` | Remove embedded zero characters from the type name or schema. |
| `ANKUS387`, `ANKUS391` | Replace unpaired Unicode surrogates in the type name or schema. |
| `ANKUS388`, `ANKUS392` | Shorten the type name or schema to at most 63 UTF-8 bytes. |
| `ANKUS393` | Keep one provider for each exact closed managed identity. |
| `ANKUS394` | Keep one custom provider for the exact catalog identity, or retain its generated type/enum declaration. |
| `ANKUS395` | Add a valid managed provider for the extension-owned mapping. A catalog-name claim does not satisfy managed identity. |

Editors offer **Remove** for the `Schema` assignment reported by `ANKUS384`.

Catalog names are exact, unquoted identifiers. Whitespace and embedded double
quotes are retained and quoted for PostgreSQL; they are not trimmed or folded.
Different managed mappings may share a catalog identity when the same block
provides both. A rejected provider leaves an owned mapping unresolved, so its
mapping declaration also reports `ANKUS395` until the provider is valid.

## Declare supplied functions

Use `PgSqlFunctionProvider` to identify a function created by a custom block:

```csharp
[assembly: PgSql("calculate", """
    CREATE FUNCTION reporting.calculate(value integer) RETURNS integer
    LANGUAGE sql AS $$SELECT value + 1$$;
    """, Requires = ["report-schema"])]
[assembly: PgSqlFunctionProvider("calculate", "reporting.calculate(integer)")]
```

The second argument is an authored SQL function signature, including argument
**types**, without a `FUNCTION` keyword. PostgreSQL validates it when executing
the script. Repeat the attribute for multiple functions. Declare schema and
function dependencies with `Requires`; provider metadata does not parse SQL.

[`ankus schema` item selection](/getting-started/publishing/#select-declarations)
accepts the block identifier or the exact signature. Its default output adds the
created function to the existing extension. Type providers also supply attachment
identities. Custom blocks without a declared inventory produce a warning when
selected with automatic attachments; tables and other undeclared objects still
need explicit attachment SQL.

### Function provider diagnostics

Function-provider errors highlight the constructor argument that needs fixing.
Named and reordered arguments follow their semantic parameter names, `sqlId`
and `signature`. Invalid inventory prevents a partial installation graph.

| Diagnostic | Correction |
| --- | --- |
| `ANKUS370` | Supply a nonnull SQL block identifier with at least one non-whitespace character. |
| `ANKUS371` | Remove embedded zero characters from the SQL block identifier. |
| `ANKUS372` | Replace unpaired UTF-16 surrogate characters in the SQL block identifier. |
| `ANKUS373` | Select a successfully resolved `PgSql` or `PgSqlFile` block. Function and schema dependency aliases do not supply custom SQL. |
| `ANKUS374` | Supply a nonnull SQL function signature with at least one non-whitespace character, including its argument types. |
| `ANKUS375` | Remove embedded zero characters from the SQL signature. |
| `ANKUS376` | Replace unpaired UTF-16 surrogate characters in the SQL signature. |
| `ANKUS377` | Remove the second claim for an exact signature or select a distinct overload. This also applies when both claims name the same block. |

Ankus preserves valid names and signatures exactly. It does not normalize SQL
aliases, fold identifier case or parse these signatures. PostgreSQL validates
their syntax and catalog identity when the script executes.

### Shell types and completion

A manual base type often needs a shell declaration, native input/output
functions, then a completed type declaration. The completed block can be the
provider when it explicitly requires those functions, and the functions
explicitly require the shell. Ankus preserves that order and places ordinary
consumers after completion. See the [raw type example](/raw-values/#custom-type-representation).

An inferred type dependency is deferred only when an explicit `Requires` or
`Before` path already orders the consumer before the provider. The early consumer
must be valid with the objects created by those prerequisites. `PgSqlOrder.Bootstrap`
or `PgSqlOrder.Finalize` alone does not allow it. Explicit dependency cycles
and unresolved cycles still produce `ANKUS499` with no installation manifest.

Alternatively, declare the shell block as the provider. Consumers needing the
completed type must then explicitly require the completion block. Declaring a
provider never means that Ankus can infer the contents of the SQL.

### Providers for managed scalar mappings

For a reusable `[PgDatumType]` mapping, identify the managed type explicitly:

```csharp
[assembly: PgSql("my-type", "CREATE DOMAIN positive AS integer CHECK(VALUE > 0);",
    Relocatable = true)]
[assembly: PgSqlTypeProvider("my-type", typeof(Positive))]
```

Here `Positive` declares `[PgDatumType("positive", typeof(PositiveConverter))]`.
The managed overload uses that mapping's exact SQL name, schema, and ownership.
Every registered extension-owned mapping needs one managed provider, even when
its SQL name matches a built-in type. A name-only provider does not satisfy that
managed identity. An external mapping needs no provider and cannot be claimed by
the managed overload.

For a [`PgRangeType` declaration](/ranges/#mapped-bounds), name the range's
managed identity: `PgSqlTypeProvider("range-definition", typeof(PgRange<Count>))`.
It uses the range attribute's SQL metadata independently of the bound mapping.
If both types are owned, the completed bound provider must precede the range
provider. One block may provide both when its SQL creates them in that order.

One SQL block can provide several CLR wrappers for the same catalog type. The
existing name overload can also identify that same block for raw consumers.
Duplicate claims for one managed identity, duplicate name-only claims, or
different blocks claiming the same catalog identity are errors. Generated
`PgType`/`PgEnum` identities remain reserved, including when their SQL is replaced
or disabled.

Managed providers follow the ordering rules above in
[shell types and completion](#shell-types-and-completion), including deferred
dependencies and cycles, and the schema and file rules for SQL blocks. Provider
metadata never inspects SQL or constructs converters. See
[reusable scalar mappings](/raw-values/#reusable-scalar-mappings) for reader,
writer, lifetime, and supported-path contracts.

## Replace function SQL

Set `PgFunction.Sql` to replace a function's installation declaration with a
compile-time string. Native wrappers and managed argument/result conversion are
still generated:

```csharp
[PgFunction(Id = "increment", Sql = """
    CREATE FUNCTION increment(integer) RETURNS integer
    AS '@MODULE_PATHNAME@', '@FUNCTION_NAME@'
    LANGUAGE c IMMUTABLE STRICT PARALLEL SAFE;
    COMMENT ON FUNCTION increment(integer) IS 'Adds one with overflow checking';
    """, SqlRelocatable = true)]
public static int Increment(int value) => checked(value + 1);
```

The replacement supports two literal substitutions:

| Token | Replacement |
|---|---|
| `@FUNCTION_NAME@` | This method's generated native PostgreSQL entry point, including its unique overload identity |
| `@MODULE_PATHNAME@` | `MODULE_PATHNAME`, which PostgreSQL resolves from the extension control file during installation |

Include SQL quotes as shown above. Substitution applies to every occurrence,
including comments and quoted text; other tokens and braces remain literal.
The replacement supplies the complete SQL, including names, argument defaults,
planner options and statement terminators. Other function attributes do not
rewrite its text. Its declarations must match the managed wrapper's argument,
result and NULL contracts. Existing managed-signature validation still applies.

`Sql = null` preserves ordinary generation. Empty, whitespace-only and
comment-only strings are valid replacements and do not restore the default SQL.
NUL characters and invalid Unicode are rejected with `ANKUS503` and `ANKUS504`;
PostgreSQL checks SQL syntax and database object definitions during installation.

These options also apply to SETOF/TABLE, `[PgTrigger]`, `[PgEventTrigger]`, and
aggregate support methods carrying `[PgFunction]`. A trigger replacement defines
the trigger **function**; use custom SQL to attach it to a table or database event.
An aggregate helper replacement affects that helper independently, while the
aggregate declaration still references its configured SQL name.

When a method also carries `[PgOperator]` or `[PgCast]`, its `Sql` replaces the
backing function and those attached declarations together. Supply every object
that downstream declarations need in the replacement or in ordered custom SQL.
The function, operator and cast dependency IDs remain available. Their external
prerequisites, including `Before` constraints targeting attached IDs, precede the
whole replacement; consumers follow it. A dependency that requires external SQL
between the backing function and its attached operator cannot be satisfied inside
one replacement and produces a cycle diagnostic. Ordinary generated declarations
retain their separate ordering.

Replacement SQL makes the extension non-relocatable unless every replacement
sets `SqlRelocatable = true`. This is an assertion that the SQL supports moving
the extension; it does not rewrite schema names. Fixed schemas and other custom
SQL blocks can still prevent relocation.

For pgrx users, these controls provide the behavior of boolean/string
`sql` options. C# string literals also supply the SQL that Rust can place in a
`pgrxsql` documentation fence. Generation reads constants without executing
extension code.

## Disable function SQL

Set `GenerateSql = false` to retain the managed method and native exports without
installing its function, attached operators or attached casts:

```csharp
[PgFunction(GenerateSql = false, Id = "optional-function")]
public static int OptionalFunction(int value) => checked(value * 2);
```

The declaration remains a dependency target and retains its prerequisites.
Dependent declarations are still generated, so provide any SQL objects they need
through explicitly ordered custom SQL. The build writes the native entry points
to `ankus/exports.txt` in the native intermediate output directory, under `obj`,
for registering a wrapper with manual SQL.

`GenerateSql = false` cannot be combined with a non-null `Sql`, including empty
text (`ANKUS502`). `SqlRelocatable` is consulted only for replacement strings;
disabling SQL does not by itself prevent relocation.

## Replace other declarations

`PgType`, `PgEnum`, `PgAggregate`, `PgOrdering` and `PgHashing` expose the same
`GenerateSql`, `Sql` and `SqlRelocatable` properties. Each controls its own SQL:

| Attribute | SQL replaced or disabled | Retained declarations and contracts |
|---|---|---|
| `PgType` | Shell type, all enabled I/O functions and completed base type | Codec, native I/O exports, managed type mapping and consuming declarations |
| `PgEnum` | `CREATE TYPE ... AS ENUM` | Managed labels, native conversions and consuming declarations |
| `PgAggregate` | `CREATE AGGREGATE` | Support functions with their independent `PgFunction` controls |
| `PgOrdering` | B-tree operator family and class | Comparison functions, relational operators and equality dependency |
| `PgHashing` | Hash operator family and class | Hash support function and equality dependency |

Defaults, empty strings, invalid text, relocation opt-in and dependency rules
work as described for functions. Disabling a declaration keeps its graph ID and
prerequisites. It does not disable its consumers or siblings. Supply compatible
objects through ordered SQL when those declarations still need them. Existing
codec, enum, aggregate and managed comparison/hash validation remains active.

All replacement strings support `@MODULE_PATHNAME@`. Other substitutions apply
only in the contexts below; unknown or out-of-context tokens remain literal.
`PgEquality` has no SQL override options, matching pgrx's equality derive.

## Replace base-type SQL

A type replacement supplies the entire shell/I/O/completed-type sequence. Use
these tokens for the generated native entry points, adding SQL string quotes:

| Token in `PgType.Sql` | Native entry point |
|---|---|
| `@INPUT_FUNCTION_NAME@` | Text input |
| `@OUTPUT_FUNCTION_NAME@` | Text output |
| `@RECEIVE_FUNCTION_NAME@` | Binary receive; requires `BinaryProtocol = true` |
| `@SEND_FUNCTION_NAME@` | Binary send; requires `BinaryProtocol = true` |

For example, this type keeps generated JSON text and CBOR storage while choosing
its SQL I/O function names:

```csharp
[PgType(Name = "stored_value", BinaryProtocol = true, SqlRelocatable = true, Sql = """
    CREATE TYPE stored_value;
    CREATE FUNCTION stored_value_input(cstring) RETURNS stored_value
        AS '@MODULE_PATHNAME@', '@INPUT_FUNCTION_NAME@' LANGUAGE c IMMUTABLE STRICT;
    CREATE FUNCTION stored_value_output(stored_value) RETURNS cstring
        AS '@MODULE_PATHNAME@', '@OUTPUT_FUNCTION_NAME@' LANGUAGE c IMMUTABLE STRICT;
    CREATE FUNCTION stored_value_receive(internal) RETURNS stored_value
        AS '@MODULE_PATHNAME@', '@RECEIVE_FUNCTION_NAME@' LANGUAGE c IMMUTABLE STRICT;
    CREATE FUNCTION stored_value_send(stored_value) RETURNS bytea
        AS '@MODULE_PATHNAME@', '@SEND_FUNCTION_NAME@' LANGUAGE c IMMUTABLE STRICT;
    CREATE TYPE stored_value (
        INTERNALLENGTH = variable, INPUT = stored_value_input, OUTPUT = stored_value_output,
        RECEIVE = stored_value_receive, SEND = stored_value_send, ALIGNMENT = int4, STORAGE = extended);
    """)]
public readonly record struct StoredValue(int Number);
```

When binary protocol is disabled, omit the receive/send declarations and clauses.
Using either binary token then produces `ANKUS505`, including occurrences in
comments. Enabling binary callbacks still emits both native exports even if the
replacement does not register them in SQL.

Preserve the declared type name and schema so generated consumers can resolve
its identity. Preserve its by-reference, variable-length storage contract;
`NativeLayout` changes the payload; the PostgreSQL datum representation stays the
same.
This option does not map arbitrary fixed-length or by-value base types. A type
with `NullInputErrorMessage` needs `CALLED ON NULL INPUT` on its text input
declaration for that managed policy to execute. See [custom types](/custom-types/).

## Replace index families

Ordering and hashing replacements retain their generated support functions and
operators. Two tokens give their exact SQL helper identifiers, including the
hashed names used when a type name is too long for an ordinary suffix:

| Context | Token | Expansion |
|---|---|---|
| `PgOrdering.Sql` | `@COMPARISON_FUNCTION_SQL@` | Quoted comparison function name |
| `PgHashing.Sql` | `@HASH_FUNCTION_SQL@` | Quoted hash function name |

Names are schema-qualified when a fixed schema is declared. These are SQL
identifiers without argument lists. Use them directly, without
string quotes, for example `FUNCTION 1 @COMPARISON_FUNCTION_SQL@(stored_value,
stored_value)` in a B-tree class or `FUNCTION 1 @HASH_FUNCTION_SQL@(stored_value)`
in a hash class. Replacement SQL chooses the family/class names and whether a
class is `DEFAULT`. The original group ID still orders consumers after the
complete family/class replacement. See [operators and casts](/operators-and-casts/).

## SQL replacement diagnostics

Replacement errors point at the authored option value to change and reject the
whole installation manifest, including unrelated valid declarations:

| Diagnostic | Reported value | Required correction |
| --- | --- | --- |
| `ANKUS502` | `GenerateSql = false` | Remove `GenerateSql = false` or `Sql`. Disabled SQL cannot also have replacement text, including empty text. |
| `ANKUS503` | `Sql` | Remove embedded zero characters from the replacement. |
| `ANKUS504` | `Sql` | Replace unpaired UTF-16 surrogate characters in the replacement. |
| `ANKUS505` | `BinaryProtocol = false`, or `Sql` when the option is omitted | Set `BinaryProtocol = true` or remove the named `@RECEIVE_FUNCTION_NAME@` or `@SEND_FUNCTION_NAME@` token. |

## Include a file

Add SQL files to the extension project's compiler inputs:

```xml
<ItemGroup>
  <AdditionalFiles Include="Sql/seed.sql" />
</ItemGroup>
```

Then reference the project-relative path:

```csharp
[assembly: PgSqlFile("seed-reports", "Sql/seed.sql", Requires = ["report-table"])]
```

Without a name, the block is named after the file without its final extension,
as with `extension_sql_file!`. This block is named `seed`:

```csharp
[assembly: PgSqlFile("Sql/seed.sql")]
```

Both `/` and `\` separate directories in the derived name, whatever the build
host. Path errors are reported before any problem with the derived name.

Paths can also be absolute. The SDK supplies the project directory to the
generator; only files registered as `AdditionalFiles` can be included. Changes
to those files trigger generation and appear in the next published installation
script. The extension does not read them at runtime.

## Order declarations

`Requires` names declarations that must run first. `Before` names declarations
that must run afterward:

```csharp
[assembly: PgSql("report-view", """
    CREATE VIEW reporting.report_total AS SELECT reporting.count() AS total;
    """, Requires = ["report-count", "seed-reports"])]
```

SQL block names and generated declaration `Id` values share one case-sensitive
namespace. A function's `Id` is separate from its SQL name, so overloads can
have distinct dependency identifiers. `PgSchema.Id` identifies a schema node;
multiple classes declaring the same schema share that node.

Functions automatically depend on their declared schemas. Other relationships
need explicit dependencies: for example, a function default that calls a
SQL-created routine must require the block that creates that routine.

Independent declarations have deterministic output order. Declaration order in
C# files does not establish a SQL dependency.

For SQL that must run before or after everything else, use `Order`:

```csharp
[assembly: PgSql("initialize", """
    CREATE TABLE installation_log(message text);
    """, Order = PgSqlOrder.Bootstrap)]

[assembly: PgSql("finish", """
    INSERT INTO installation_log VALUES ('installed');
    """, Order = PgSqlOrder.Finalize)]
```

There can be one bootstrap block and one final block, including file-based
blocks. Bootstrap precedes generated schemas; final SQL follows all generated
and custom declarations.

The [custom SQL sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.CustomSql)
ports pgrx's `custom_sql` example. Its bootstrap, inline, file and final blocks
insert their names into a table, showing the order that their dependencies on
SQL blocks, a schema, an enum and a custom type produce.

PostgreSQL checks SQL syntax, object names and privileges during installation.

### Dependency graph diagnostics

Graph errors point at the authored value to change and reject the whole
installation manifest. A cycle error lists one complete cycle using dependency
IDs, or managed declaration names for declarations without an `Id`. It points at
the first `Requires`, `Before`, `PgRequires`, `PgBefore` or `PgSupportFunction`
value in that cycle; a cycle formed only by automatic schema, type or boundary
edges points at its first declaration.

| Diagnostic | Reported value | Required correction |
| --- | --- | --- |
| `ANKUS490` | `Id` | Give `Id` a non-whitespace value, or omit it. |
| `ANKUS491`, `ANKUS492` | `Id` | Remove zero characters or unpaired UTF-16 surrogates from the identifier. |
| `ANKUS493` | `Requires` or `Before` | Omit the option or supply an array instead of `null`. |
| `ANKUS494` | `Requires` or `Before` entry | Supply a non-null, non-whitespace dependency ID. |
| `ANKUS495`, `ANKUS496` | `Requires` or `Before` entry | Remove zero characters or unpaired UTF-16 surrogates from the entry. |
| `ANKUS497` | Later `Id` or SQL block name | Give each declaration a distinct ID; SQL block names and `Id` values share one namespace. |
| `ANKUS498` | `Requires` or `Before` entry | Declare the named ID or correct the entry. IDs are case-sensitive. |
| `ANKUS499` | First explicit edge in the cycle | Remove or reverse one dependency in the reported cycle. |
| `ANKUS500`, `ANKUS501` | Later block's `Order` | Keep one `Bootstrap` block and one `Finalize` block. |
| `ANKUS512`–`ANKUS514` | None | Split the extension or reduce its SQL; the embedded graph allows 100,000 declarations, 100,000 entries per declaration list and 32 MiB. |

A dependency inherited by aggregate support functions reports its authored
entry once.

### Custom SQL input diagnostics

Input errors point at the offending authored argument. Correct the declaration
or tracked file before publishing; a failing block produces no installation
manifest.

| Diagnostic | Correction |
|---|---|
| `ANKUS354` | Supply a name and SQL text for `PgSql`, or a path, with or without a name, for `PgSqlFile`. |
| `ANKUS355` | Supply a nonnull, non-whitespace dependency name. |
| `ANKUS356` | Remove zero characters from the dependency name. |
| `ANKUS357` | Replace unpaired UTF-16 surrogates in the dependency name. |
| `ANKUS358` | Use a defined `PgSqlOrder` value. |
| `ANKUS359` | Supply nonnull SQL text; empty SQL is valid. |
| `ANKUS360` | Remove zero characters from inline or file SQL. |
| `ANKUS361` | Replace unpaired UTF-16 surrogates in SQL. |
| `ANKUS362` | Supply a nonnull, non-whitespace file path. |
| `ANKUS363` | Remove zero characters from the file path. |
| `ANKUS364` | Replace unpaired UTF-16 surrogates in the file path. |
| `ANKUS365` | Use a path valid on the compilation platform. |
| `ANKUS366` | Use `Ankus.Sdk` or expose `MSBuildProjectDirectory` as a compiler-visible property for relative paths. |
| `ANKUS367` | Include the requested file in `AdditionalFiles`. |
| `ANKUS368` | Remove duplicate or ambiguous tracked file paths. |
| `ANKUS369` | Make the selected tracked file readable by the compiler. |

### Reference managed declarations

Use `PgRequires` and `PgBefore` to reference a C# declaration directly:

```csharp
[assembly: PgSql("report-view", """
    CREATE VIEW reporting.report_total AS SELECT reporting.count() AS total;
    """)]
[assembly: PgRequires(typeof(Reports), nameof(Reports.Count), DeclarationId = "report-view")]

[PgSchema("reporting")]
public static class Reports
{
    [PgFunction(Requires = ["report-table"])]
    public static long Count() => Spi.ExecuteScalar<long>("SELECT count(*) FROM reporting.reports");
}
```

The referenced method's generated SQL precedes the view. Keep the table and seed
dependencies from the earlier examples as well. `PgBefore` reverses the order:
the attributed declaration precedes its referenced declaration. Repeat either
attribute to name several dependencies. Existing string `Requires` and `Before`
arrays can be combined with these attributes.

On a method, the attribute orders that method's generated function, including
trigger functions and aggregate support methods. On a type, it orders the
declared schema, enum, custom type or aggregate. Omit the method name to reference
one of those type declarations. Aggregate `PgRequires` prerequisites also precede
its support functions.

Assembly attributes must set `DeclarationId` to the SQL block name or generated
declaration ID being ordered. On a method or type, `DeclarationId` can select a
specific attached declaration, such as an operator's `Id`; it must belong to that
method or type. If a type declares several primary SQL objects, use their explicit
IDs instead of a type-only reference.

For an overloaded method, supply its exact managed parameter types:

```csharp
[assembly: PgRequires(typeof(Functions), nameof(Functions.Convert),
    ParameterTypes = [typeof(int)], DeclarationId = "converted-view")]
```

An empty `ParameterTypes` array selects a parameterless overload. Omitting it,
or setting it to `null`, requires an unambiguous method name. `int` and `int?`
are different signatures. Lookup follows the C# declaration, including inherited
methods; SQL names and aliases do not select an overload.

Managed-reference diagnostics identify the exact failed contract and point at
the authored type, method name, `ParameterTypes` element or `DeclarationId`
to correct:

| Diagnostic | Required correction |
| --- | --- |
| `ANKUS471` | Supply a non-null closed declared type. |
| `ANKUS472`–`ANKUS476`, `ANKUS486`–`ANKUS487` | Correct the method name or `ParameterTypes` overload selection. Overloads that differ only by generic arity or `ref`, `in` or `out` modifiers need distinct names. |
| `ANKUS477`–`ANKUS479` | Correct the source `DeclarationId` selector. |
| `ANKUS480`–`ANKUS481` | Reference a declaration that emits one primary SQL object in this extension. For a type with several, give one an `Id` and list that ID in `Requires` or `Before`. |
| `ANKUS488`–`ANKUS489` | Apply the attribute to a declaration that emits SQL. For a type with several objects, give the one being ordered an `Id` and set `DeclarationId` to it. |

Disabled and replaced declarations keep their identities and dependencies.
These edges also participate in shell-type ordering, dependency selection and
cycle checks; no installation script is emitted for an invalid graph.

## Relocation

Custom SQL makes the extension non-relocatable by default. Set
`Relocatable = true` on each block only when its objects and references support
moving to a different installation schema:

```csharp
[assembly: PgSql("values", "CREATE TABLE stored_values(value integer);", Relocatable = true)]
```

Every custom block must permit relocation, and fixed `PgSchema` or function
schema declarations still make the extension non-relocatable. Ankus writes
the resulting policy into the extension control file.

## Enum type dependencies

`[PgEnum(Id = "status-type")]` makes an enum available as a named dependency.
Use `Requires = ["status-type"]` on SQL that creates tables or other
objects using it. Function signatures automatically depend on their enum types,
including array parameters and results. Enums can also declare `Requires` for
SQL or schema prerequisites. See [enumerated types](/enums/).
