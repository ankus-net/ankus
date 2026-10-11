---
title: Enumerated types
description: Declare PostgreSQL enums with C# labels, typed SPI queries, arrays and catalog lookup.
---

Use `[PgEnum]` to declare a PostgreSQL enum and use it in ordinary extension
functions:

```csharp
using Ankus;

[PgEnum]
public enum DeliveryStatus
{
    [PgEnumLabel("pending")]
    Pending = 10,

    [PgEnumLabel("in transit")]
    InTransit = 30,

    [PgEnumLabel("delivered")]
    Delivered = 20,
}

public static class Functions
{
    [PgFunction]
    public static DeliveryStatus AdvanceDelivery(
        DeliveryStatus status = DeliveryStatus.Pending) => status switch
    {
        DeliveryStatus.Pending => DeliveryStatus.InTransit,
        DeliveryStatus.InTransit or DeliveryStatus.Delivered => DeliveryStatus.Delivered,
        _ => throw new ArgumentOutOfRangeException(nameof(status)),
    };
}
```

Publishing creates the `delivery_status` type before the function. After
installing the extension:

```sql
SELECT advance_delivery();             -- in transit
SELECT advance_delivery('in transit'); -- delivered
SELECT enum_range(NULL::delivery_status);
-- {pending,"in transit",delivered}
```

The repository includes a complete `samples/Ankus.Examples.Enums` extension.

## Names, labels and ordering

The type name defaults to the enum's name in snake case. Set
`[PgEnum(Name = "status")]` to choose an exact SQL identifier. Identifiers are
quoted, preserving case and special characters.

Labels default to the exact C# member names. `[PgEnumLabel]` overrides a label;
case, whitespace, quotes, Unicode and empty labels are preserved. Labels must
be distinct and occupy at most 63 UTF-8 bytes, without zero characters.

Type and schema names must be nonempty, valid Unicode identifiers of at most
63 UTF-8 bytes, without zero characters. These checks use exact attribute
values from source and referenced assemblies, including inherited schemas.
An invalid referenced declaration cannot supply a function conversion.
Unreadable attribute metadata produces
[`ANKUS206`](/custom-types/#referenced-attribute-metadata) at the consuming declaration.

PostgreSQL comparisons follow source declaration order. C# comparisons still
follow the enum's numeric values. In the example, PostgreSQL orders `in transit`
before `delivered`, even though their C# values are 30 and 20. Numeric values
are never used as PostgreSQL enum OIDs.

All eight C# enum underlying integer types are supported. Members must have
distinct numeric values. `[Flags]` and numeric aliases are rejected because
PostgreSQL enums represent individual labels. Casting an undeclared number to
the C# enum throws when it is converted to a PostgreSQL value.

## Declaration diagnostics

Enum declaration errors point at the attribute value, member or containing type
that needs correction. An invalid enum is excluded from generated registrations
and SQL; unrelated valid declarations still generate normally.

| Diagnostic | Correction |
| --- | --- |
| `ANKUS278` | Put `[PgEnum]` on an enum declaration. C# also reports invalid attribute targets. |
| `ANKUS279` | Remove `[Flags]`; PostgreSQL labels represent individual values. |
| `ANKUS280` | Move the enum out of its generic containing type. |
| `ANKUS281` | Make the enum and its containing types accessible from generated files, without `file`. |
| `ANKUS282` | Use `public`, `internal` or `protected internal` accessibility for the enum and its containing types. |
| `ANKUS283` | Give `PgEnum.Name` a nonempty, valid Unicode identifier of at most 63 UTF-8 bytes, without zero characters. |
| `ANKUS284` | Give the explicit or inherited schema a valid nonempty identifier with the same byte and text limits. |
| `ANKUS285` | Give the enclosing `[PgSchema]` a non-null name. |
| `ANKUS286` | Use a non-null label. An empty label remains valid. |
| `ANKUS287` | Remove zero characters and unpaired UTF-16 surrogates from the label. |
| `ANKUS288` | Shorten the label to at most 63 UTF-8 bytes. |
| `ANKUS289` | Assign a distinct label to each member. |
| `ANKUS290` | Assign distinct numeric values; aliases cannot preserve distinct PostgreSQL labels. |
| `ANKUS506` | Give the enum a `Name` or `Schema` that no other `[PgEnum]` in the extension generates. The error points at the later enum's name. A clash with a `[PgType]` reports `ANKUS507` on that type. |

Editors offer **Make internal** for `ANKUS281` and `ANKUS282`, on the enum or on
the containing type that hides it, and **Remove** for `Flags` (`ANKUS279`).

Invalid label text from a referenced assembly is rejected as well; unreadable
referenced attribute metadata reports `ANKUS206`.

## Schemas and dependencies

By default, the type belongs to the extension's installation schema and moves
with a relocatable extension. Enum type lookup uses the owning extension's
catalog schema, independently of `search_path`. Installation into a requested
schema, `ALTER EXTENSION SET SCHEMA`, and dropping and reinstalling an extension
all resolve the current type and array OIDs.

An enum nested inside a `[PgSchema]` class inherits that schema. Set
`PgEnum.Schema` to override it. A fixed schema must exist or have a corresponding
`[PgSchema]` declaration that creates it. Fixed schemas make an extension
non-relocatable. See [function declarations](/function-declarations/).

Generated SQL automatically orders schemas before their enums, and enums before
functions that accept or return them, including arrays. `PgEnum.Id` and
`PgEnum.Requires` connect types to [custom installation SQL](/custom-sql/):

```csharp
[assembly: PgSql("deliveries",
    "CREATE TABLE deliveries(id bigint PRIMARY KEY, status delivery_status);",
    Requires = ["delivery-status"], Relocatable = true)]

[PgEnum(Id = "delivery-status")]
public enum DeliveryStatus { Pending, Delivered }
```

`PgEnum.Sql` replaces the enum's `CREATE TYPE` statement. `GenerateSql = false`
omits that statement while retaining the managed label mapping and native
conversions. Supply a compatible enum with the declared name, schema and labels
when generated functions still consume it. SQL changes do not infer a new
managed mapping. Replacement SQL makes the extension non-relocatable unless
`SqlRelocatable = true`. See [declaration SQL controls](/custom-sql/#replace-other-declarations).

## NULL, arrays and SPI

`DeliveryStatus?` accepts SQL NULL. `DeliveryStatus[]`, `DeliveryStatus?[]`, and
`PgArray<DeliveryStatus?>` map to `delivery_status[]`. Nullable elements preserve
NULL; `PgArray<T>` retains dimensions and lower bounds. Vectors reject shape
loss. A byte-backed enum array remains an enum array; `byte[]` maps to `bytea`.
C# `params` supports variadic enum functions.

Enums work with ordinary and scoped SPI queries, prepared statements, kept
plans, cursors, and local row edits:

```csharp
DeliveryStatus status = Spi.ExecuteScalar<DeliveryStatus>(
    "SELECT $1", SpiParameter.Create(DeliveryStatus.Pending));

PgArray<DeliveryStatus?> statuses = Spi.ExecuteScalar<PgArray<DeliveryStatus?>>(
    "SELECT $1",
    SpiParameter.Create(new PgArray<DeliveryStatus?>(
        new DeliveryStatus?[] { status, null })));
```

SPI retains enum identity: a different enum with an identical label cannot be
read as `DeliveryStatus`. Domains over supported enums, domains over their
arrays, and arrays of enum domains decode to the corresponding owned values.
Owned values and arrays remain usable after later queries or outside a backend
callback. Creating SPI parameters and resolving catalog OIDs require the active
backend because user-defined type OIDs belong to that database.

## Labels and catalog information

Label conversion works without a PostgreSQL connection:

```csharp
string label = PgEnums.GetLabel(DeliveryStatus.InTransit); // "in transit"
DeliveryStatus status = PgEnums.Parse<DeliveryStatus>("delivered");
```

These helpers use the compiled enum contract. They reject unknown labels and
undefined numeric values with `ArgumentException`.

Inside an extension function, inspect the live catalog:

```csharp
uint typeOid = PgEnums.GetTypeOid<DeliveryStatus>();
uint valueOid = PgEnums.GetValueOid(DeliveryStatus.Pending);
PgEnumInfo info = PgEnums.Lookup(valueOid);
// info.Label, info.TypeOid, info.ValueOid, info.SortOrder
```

For an enum type without a managed mapping, resolve a label by the type's OID:
`PgEnums.GetValueOid(PgTypes.GetOid("mood"), "happy")`. An unknown label fails with
SQLSTATE `22P02`, as PostgreSQL's enum input does.

`Lookup` also supports enum types without a managed mapping. Its returned
information is owned and remains readable after the native catalog tuple has
been released. Sort positions can be fractional after `ALTER TYPE ADD VALUE`.
Invalid OIDs, missing types and missing server labels raise `PgException` through
the guarded native boundary.

If SQL adds or renames a label, update the C# contract before decoding that label
as the managed enum. PostgreSQL type and value OIDs are looked up afresh; they
are not cached across catalog changes. Ankus preserves PostgreSQL's checks on
using newly added enum labels before their transaction commits.
