---
title: JSON and UUID values
description: Use Guid, PgJson, and PgJsonb in Native AOT extension functions and SPI.
---

## UUIDs

Use `System.Guid` for PostgreSQL `uuid` parameters and results:

```csharp
[PgFunction]
public static Guid NewId() => Guid.NewGuid();

[PgFunction]
public static string FormatId(Guid value) => value.ToString("D");
```

Ankus converts PostgreSQL's sixteen network-order bytes using the explicit
big-endian `Guid` APIs. `Guid?` represents a nullable UUID. SPI parameters, query
results, prepared statements, and cursors use the same mapping.

## JSON text and documents

Use `PgJson` for PostgreSQL `json`, and `PgJsonb` for `jsonb`:

```csharp
[PgFunction]
public static PgJson PreserveJson(PgJson value) => value;

[PgFunction]
public static string? ReadName(PgJsonb value)
{
    using JsonDocument document = value.Parse();
    return document.RootElement.GetProperty("name").GetString();
}
```

Both are immutable value types owning their JSON text. `Text` returns the stored
text; `Parse()` creates an independently owned `JsonDocument` that the caller
disposes. Disposing a document leaves the original value usable. Constructors
validate JSON syntax and UTF-16 without converting number tokens to CLR numeric
types. The wrappers accept deeply nested JSON beyond the DOM's default 64-level
limit.

`PgJson` preserves whitespace, property order, duplicate properties, and numeric
spelling. `PgJsonb` uses PostgreSQL's normalized textual representation when read
from the server. Constructing `new PgJsonb(text)` validates JSON syntax; conversion
to a PostgreSQL datum performs normalization and enforces jsonb's numeric and
Unicode constraints. For example, a JSON number outside PostgreSQL's `numeric`
range or an escaped zero character causes a PostgreSQL error during conversion.

The default value of either wrapper represents **JSON null**. A nullable wrapper
with no value represents **SQL NULL**:

```csharp
SpiParameter jsonNull = SpiParameter.Create(default(PgJsonb));
SpiParameter sqlNull = SpiParameter.Create<PgJsonb?>(null);
```

Managed equality and hash codes compare the stored text ordinally, including
whitespace. PostgreSQL jsonb operators provide SQL structural equality.

## Borrowed array serialization

The [JSON sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Json)
ports pgrx's borrowed `text[]` and `bytea[]` serialization examples. It writes
each checked native cell through `Utf8JsonWriter`, producing a document with a
`values` array without collecting an intermediate managed array.

SQL NULL cells become JSON null. Text remains text, including Unicode and
escaping; each bytea cell becomes an array of byte numbers rather than a base64
string. Empty arrays produce `{"values":[]}`. Borrowed cells are disposed after
the writer copies their contents, and the returned `PgJson` owns its text.

## Source-generated serialization

Provide `JsonTypeInfo<T>` from a `JsonSerializerContext` to serialize and
deserialize application types without reflection-based contract discovery:

```csharp
public sealed record Message(string Name, int Count);

[JsonSerializable(typeof(Message))]
internal partial class MessageJsonContext : JsonSerializerContext;

public static class MessageFunctions
{
    [PgFunction]
    public static PgJsonb Increment(PgJsonb input)
    {
        Message message = input.Deserialize(MessageJsonContext.Default.Message)
            ?? throw new ArgumentException("A message is required.", nameof(input));
        return PgJsonb.Serialize(message with { Count = checked(message.Count + 1) },
            MessageJsonContext.Default.Message);
    }
}
```

These examples use `Ankus`, `System.Text.Json`, and
`System.Text.Json.Serialization`. `PgJson` exposes the same metadata-based
serialization methods. When wrappers appear in an application's JSON contract,
their statically known converters embed their JSON value directly. Serializer
options and depth limits come from the supplied context.

`PgNumeric` and the full-range temporal converters retain PostgreSQL's version limits. Numeric infinity
requires PostgreSQL 14+, and interval infinity requires PostgreSQL 17+. A value
the selected server cannot parse raises `JsonException` with the JSON property
path and an inner `PgException` carrying the native diagnostic. Quoting the value
in JSON does not make it available on an older server. Before PostgreSQL 16,
inside transaction callbacks and parallel operations, invalid input remains
pending and propagates as `PgException`; see [error recovery](/reference/execution/#errors).

JSON and JSONB participate in the [typed SPI APIs](/spi/), including nullable
parameters, plans, cursors, domains over these base types, and owned result rows.
PostgreSQL handles detoasting and database-encoding conversion. A native jsonb
parameter-conversion error throws a catchable `PgException`.
