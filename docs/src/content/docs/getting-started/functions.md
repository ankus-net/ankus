---
title: Write a function
description: Declare C# methods, map PostgreSQL types, and handle SQL NULL.
---

## Create a project

Install `Ankus.Tool` from your configured NuGet feed, then create a solution:

```console
dotnet tool install --global Ankus.Tool --version 0.1.0
ankus new Hello
cd Hello
```

Use the Ankus version available from your feed. Packages are currently built
locally; a public release is pending. The solution includes an extension project,
managed tests, and tests that load the native extension into PostgreSQL.

Alternatively, install the matching .NET template package from your feed:

```console
dotnet new install Ankus.Templates::0.1.0
dotnet new ankus -n Hello
cd Hello
dotnet tool restore
```

Both paths include the same managed and backend tests, SQL regression setup and
matching local tool manifest. After `dotnet tool restore`, run extension commands
with `dotnet ankus`. Use `dotnet new ankus-worker -n MyWorker` for a background
worker with shared preload and a test that checks its separate PostgreSQL process.

MSTest is the default framework. Add `--test-framework xunit` or
`--test-framework nunit` to either creation command to use that framework's
managed tests, backend case discovery and asynchronous fixture cleanup.

The templates derive a valid SQL identifier from the project name. For example,
`dotnet new ankus -n 1Ext` uses `_1_ext` as the extension name. Generated regression
results are ignored by Git; authored SQL and expected results remain tracked.

The generated `src/Hello/Hello.csproj` uses `Ankus.Sdk`:

```xml
<Project Sdk="Ankus.Sdk/0.1.0">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
    <AnkusExtensionName>hello</AnkusExtensionName>
    <AnkusExtensionVersion>0.1.0</AnkusExtensionVersion>
  </PropertyGroup>
</Project>
```

The SDK enables Native AOT and includes matching runtime and source-generator
packages. It works with ordinary NuGet configuration and Central Package Management.

## Add a function

Edit `src/Hello/Functions.cs` and mark a synchronous static method with <code>[<span class="csharp-type">PgFunction</span>]</code>:

```csharp
using Ankus;

public static class Functions
{
    [PgFunction]
    public static string Greet(string name) => $"Hello, {name}!";
}
```

The method is exposed as `greet(text)`. Names use snake case by default.
Ankus generates the native entry point and SQL declaration during publishing.

C# parameter names also use snake case in SQL, and optional arguments become
SQL defaults. See [function declarations](/function-declarations/) for named
calls, schema placement, and PostgreSQL execution options.

See [enumerated types](/enums/) for custom C# enums, labels and type dependencies.

## Function signatures

SQL entry methods must be synchronous, concrete static methods. The method and
every containing type must be accessible from generated code in the extension
assembly: use `public` or `internal`, or `protected internal` for members and
nested types. `protected`, `private protected` and `private` do not grant that
assembly access. Generic methods, generic containing types and file-local types
cannot provide the concrete call required by the generated Native AOT dispatcher.

Pass parameters and return values by value. PostgreSQL supplies SQL datums, not
C# `ref`, `in`, `out` or `ref readonly` slots. Injected `PgMemoryContext` and
`PgFunctionContext` parameters also use by-value passing. At most 100 SQL
arguments are allowed; injected contexts do not count toward that limit.

Use a [supported type](#types), a [custom type](/custom-types/) or an explicit
[datum mapping](/raw-values/#reusable-scalar-mappings) for inputs and results.
`params T[]` requires a supported SQL array conversion. `params byte[]` is
invalid because `byte[]` maps to scalar `bytea`; use `params byte[][]` for
variadic binary values. Ankus does not adapt span-based `params` collections.

Invalid signatures report a specific error at the offending declaration:

| Diagnostic | Required correction |
| --- | --- |
| ANKUS033 | Declare a static entry method. |
| ANKUS034 | Make the method and containing types accessible within the extension assembly: public, internal, or protected internal where permitted. |
| ANKUS035 | Use a non-generic entry method in non-generic containing types. |
| ANKUS036 | Put the entry attribute on a concrete implementation. |
| ANKUS037 | Return the SQL value by value. |
| ANKUS038 | Pass arguments and injected contexts by value. |
| ANKUS039 | Give the result a supported SQL conversion. |
| ANKUS040 | Give the parameter a supported SQL conversion. |
| ANKUS041 | Use a supported SQL array for the variadic parameter. |
| ANKUS042 | Reduce the number of SQL arguments to 100 or fewer. |
| ANKUS043 | Move the method out of a file-local type. |

Editors offer fixes for ANKUS033, ANKUS034, ANKUS038 and ANKUS043. **Make static**
appears when the method uses no instance state and no caller invokes it through
an instance. **Make internal** replaces a private or protected accessibility on
the method, or on a containing type and each of its partial declarations, and
replaces `file` on a containing type when no other type shares its name.
**Pass by value** removes `in` or `ref readonly` from the parameter and from
callers' arguments, and leaves the source unchanged if a delegate, overload or
interface still needs the reference. `ref` and `out` get no fix, because the
caller would no longer see the method's writes.
**Remove** deletes an attribute or option that cannot apply where it is written:
`Conditional` or `UnmanagedCallersOnly` on any entry (ANKUS245, ANKUS246, ANKUS266,
ANKUS267, ANKUS276, ANKUS277, ANKUS343, ANKUS524), SQL value metadata on a worker,
initializer or trigger (ANKUS225, ANKUS248, ANKUS270, ANKUS271), `Rows` or `SetMode`
without a set result (ANKUS053), operator options an operator cannot take (ANKUS070,
ANKUS071) and `Schema` on a managed type provider (ANKUS384). It leaves the source
unchanged if other code still needs what it would remove.

Task and asynchronous iterator diagnostics are described in
[execution constraints](/reference/execution/#backend-threads-and-tasks).

## Types

| C# | PostgreSQL |
| --- | --- |
| `bool` | `boolean` |
| `sbyte` | `"char"` (the internal signed byte type) |
| `short`, `int`, `long` | `smallint`, `integer`, `bigint` |
| `uint` | `oid` |
| `PgItemPointer` | `tid` ([tuple location](/item-pointers/)) |
| `float`, `double` | `real`, `double precision` |
| `string` | `text` |
| `Rune` | `varchar` holding exactly one Unicode scalar value |
| `byte[]` | `bytea` |
| `PgTextView`, `PgByteaView` | `text`, `bytea` with [checked native borrowing](/text-and-binary/) |
| `Guid` | `uuid` |
| `PgInet`, `IPAddress` | `inet` |
| `PgCidr`, `IPNetwork` | `cidr` |
| `PgPoint`, `PgLine`, `PgLineSegment` | `point`, `line`, `lseg` |
| `PgBox`, `PgCircle` | `box`, `circle` |
| `PgPath`, `PgPolygon` | `path`, `polygon` |
| `PgRange<T>` | Built-in range selected by the bound type |
| `PgJson`, `PgJsonb` | `json`, `jsonb` |
| `decimal`, `PgNumeric` | `numeric` |
| `DateOnly`, `PgDate` | `date` |
| `TimeOnly`, `PgTime` | `time` |
| `PgTimeTz` | `timetz` |
| `DateTime`, `PgTimestamp` | `timestamp` |
| `DateTimeOffset`, `PgTimestampTz` | `timestamptz` |
| `TimeSpan`, `PgInterval` | `interval` |
| `T[]`, `PgArray<T>` | Array of the corresponding scalar SQL type |
| `PgAnyElement`, `PgAnyArray` | `anyelement`, `anyarray` |
| `PgArrayView` | `anyarray`, with [borrowed native cells](/arrays/#borrowed-native-arrays) |
| `[PgEnum]` C# enums | Generated PostgreSQL enum types |
| `[PgDatumType]` classes, structs and enums | The converter's declared existing SQL type, also usable as an array element |
| `void` result | `void` |

`string` and `byte[]` inputs are managed copies. They remain valid after PostgreSQL
releases the original storage. `PgTextView` and `PgByteaView` expose checked native
views; scalar inputs expire at callback exit. See [text and binary values](/text-and-binary/)
for encoding, ownership, copying and returning views.

See [date and time values](/date-and-time/) for precision, time zones, and
full-range PostgreSQL values.

See [network values](/network/) for IPv4/IPv6 prefixes and checked `System.Net`
conversions. `IPAddress` requires a full-width host prefix.

See [geometric values](/geometry/) for coordinates, owned vertex collections,
and PostgreSQL input validation.

See [ranges](/ranges/) for empty values, bound inclusion, canonicalization, and
checked .NET bound types.

See [arrays](/arrays/) for dimensions, lower bounds, nullable elements, and
variadic functions. `byte[]` is scalar `bytea`; `byte[][]` is `bytea[]`.

Use [reusable datum mappings](/raw-values/#reusable-scalar-mappings) for an existing
SQL representation. Function inputs need a reader; outputs need a writer. Array
signatures use the same element converter and type provider.

## SQL NULL

Use nullable C# types to receive SQL NULL:

```csharp
[PgFunction]
public static string GreetOrDefault(string? name) => $"Hello, {name ?? "world"}!";
```

If every parameter is required, the generated SQL function is `STRICT`:
PostgreSQL returns NULL without calling the method when any argument is NULL.

For mixed signatures, a NULL required argument still returns NULL without
entering the method. Nullable arguments reach your code. A null result becomes
SQL NULL.

## Errors

An unhandled managed exception becomes PostgreSQL ERROR. The managed stack
unwinds first, so `finally` blocks and `using` scopes run normally.

Use [`PgException`](/logging/#errors) when the error needs a particular SQLSTATE,
detail, or hint.
