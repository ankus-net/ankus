---
title: Network values
description: Preserve PostgreSQL inet and cidr addresses, prefixes, and address families.
---

`PgInet` represents an address with a prefix. `PgCidr` represents a network whose
host bits are zero. Both are immutable values, and both default to `0.0.0.0/0`.

```csharp
[PgFunction]
public static PgCidr Subnet(PgInet address) => address.Network;
```

```sql
SELECT subnet('192.0.2.129/25'); -- 192.0.2.128/25
```

## .NET addresses

Construct values from `System.Net` types without a PostgreSQL backend:

```csharp
using System.Net;

var host = new PgInet(IPAddress.Parse("192.0.2.129"), 25);
var subnet = new PgCidr(IPAddress.Parse("192.0.2.128"), 25);
IPNetwork network = subnet.ToIPNetwork();
```

Omitting the `PgInet` prefix uses `/32` for IPv4 or `/128` for IPv6. Constructors
copy the address bytes. IPv4-mapped IPv6 stays IPv6; IPv6 scope IDs are rejected
because PostgreSQL has no field for them.

Function signatures and SPI support these mappings:

| C# | SQL | Conversion rule |
| --- | --- | --- |
| `PgInet` | `inet` | Retains the address and prefix |
| `IPAddress` | `inet` | Requires a full-width prefix when reading |
| `PgCidr` | `cidr` | Requires zero host bits |
| `IPNetwork` | `cidr` | Retains the network and prefix |

`PgInet.Address` explicitly extracts a host address without the prefix.
`ToIPAddress()` rejects a shorter prefix to prevent accidental information loss.
Likewise, `new PgCidr(inet)` rejects host bits; use `inet.Network` to clear them.

## Parsing and formatting

`Parse` and `TryParse` use PostgreSQL's input rules on the active backend. This
includes abbreviated IPv4 forms and `cidr`'s inferred prefixes:

```csharp
PgInet host = PgInet.Parse("192.0.2.129/25");
PgCidr network = PgCidr.Parse("10"); // 10.0.0.0/8
```

`TryParse` returns false for invalid input. Backend-access and operational errors
still propagate. Invalid input leaves no error pending: PostgreSQL 16 and later
report it as a soft input error, and earlier majors roll back an internal
subtransaction.
To catch a `Parse` or native arithmetic error and continue, run the operation in
`PgTransaction.RunInSubtransaction` and catch outside that callback.

In transaction callbacks, and in parallel operations before PostgreSQL 17, native
input errors must propagate because independent rollback is unavailable.
`TryParse` still returns false there on PostgreSQL 16 and later. See
[error recovery](/reference/execution/#errors).

`ToString()` works outside PostgreSQL and produces round-trippable address text.
It uses .NET's address formatting, which can differ from PostgreSQL's output for
an IPv6 address with embedded IPv4, such as `::ffff:0:1.2.3.4`. Use
`PgDatum.Create(value, context).ToPostgresString()` when you need the server's text.
An `inet` full-width prefix is omitted; a `cidr` prefix is always included.
Both types implement `IParsable<T>` and `ISpanFormattable` with the same input
and output rules, and reject format strings.

## Networks and masks

Address operations run on detached values:

```csharp
PgCidr network = host.Network;
PgInet broadcast = host.Broadcast;
PgInet netmask = host.Netmask;
PgInet hostmask = host.Hostmask;

PgInet changed = host.WithPrefixLength(24);     // keeps host bits
PgCidr widened = network.WithPrefixLength(24); // clears host bits
bool contains = network.Inet.Contains(host);
```

`Contains` compares subnets, including equal prefixes. Pass `includeEqual: false`
for strict containment. IPv4 and IPv6 are separate families. `CompareTo` and the
ordering operators match PostgreSQL: family, common network bits, prefix length,
then host bits.

## Arrays and SPI

Network values work through queries, prepared statements, sessions, cursors, and
editable rows. Use nullable elements when an array can contain SQL NULL:

```csharp
PgArray<PgInet?> addresses = Spi.ExecuteScalar<PgArray<PgInet?>>(
    "SELECT ARRAY['192.0.2.1/24'::inet, NULL, '::1'::inet]");
```

`PgArray<T>` retains dimensions and lower bounds; `T[]` requires a vector with a
lower bound of one. `IPAddress[]` applies the same full-prefix check to each
element. See [arrays](/arrays/) for shape and NULL rules.

## JSON

Both values serialize as JSON strings. Register them in a source-generated
`JsonSerializerContext` when using Native AOT:

```csharp
using System.Text.Json.Serialization;

[JsonSerializable(typeof(PgInet))]
[JsonSerializable(typeof(PgCidr))]
internal partial class NetworkJsonContext : JsonSerializerContext;
```

Writing JSON works with detached values. Reading uses PostgreSQL parsing on the
active backend. Invalid input becomes a `JsonException` with the property path
and underlying PostgreSQL diagnostic.
