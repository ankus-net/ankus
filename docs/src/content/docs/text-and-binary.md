---
title: Text and binary values
description: Choose managed copies or checked native views of PostgreSQL text, bytea and C strings.
---

Use `string` and `byte[]` for independent managed values. Use `PgTextView` and
`PgByteaView` to inspect PostgreSQL storage under a checked native lifetime.

| C# representation | SQL type | Storage |
| --- | --- | --- |
| `string` | `text` | Independent managed Unicode string |
| `Rune` | `varchar` | One Unicode scalar value, like Rust's `char` |
| `byte[]` | `bytea` | Independent managed bytes, including embedded zero |
| `PgTextView` | `text` | Checked UTF-8 view with the original server datum retained |
| `PgByteaView` | `bytea` | Checked native byte view |
| `PgCString` | `cstring` | Independent managed bytes with a terminating zero |
| `PgCStringView` | `cstring` | Checked native view of zero-terminated bytes |

## Function arguments and results

```csharp
using Ankus;

public static class BufferFunctions
{
    [PgFunction]
    public static int BinaryLength(PgByteaView value) => value.Count;

    [PgFunction]
    public static int Utf8Length(PgTextView value) => value.Utf8Length;

    [PgFunction]
    public static PgByteaView? Echo(PgByteaView? value) => value;
}
```

Nullable views represent SQL NULL. Empty text and empty binary values remain
present views with zero length. `PgByteaView` indexing and enumeration preserve
every byte; `PgTextView.GetUtf8Byte` addresses bytes, including positions inside
a multibyte character. `Utf8Length` is not a character count.

Generated scalar arguments expire when their managed callback returns. Returning
an input view transfers its value before that cleanup. SETOF/TABLE and aggregate
inputs use independent native snapshots when they must survive input callbacks.
Dispose retained views when your iterator or aggregate no longer needs them.

## Copies and encoding

`PgByteaView.ToArray()` makes an independent managed copy; `CopyTo` writes to a
caller-supplied span. `PgTextView.ToString()` returns an independent managed
string; `CopyUtf8To` copies its UTF-8 bytes. A destination that is too short is
rejected without a partial copy.

Flat and short native values keep their original storage. PostgreSQL detoasts
compressed and external values into a private child memory context. Text in a
UTF-8 database keeps its original payload when detoasting is unnecessary. Other
server encodings require conversion into the private context. Invalid encoding
is rejected without replacing bytes or characters.

A flat `text`, `varchar`, `bpchar` or `bytea` value, without a domain, is
borrowed in place without entering PostgreSQL, and `PgDatum.Read<T>()` reads
such values and built-in by-value types the same way. That makes a view about
as cheap as reading the value's header. Text is borrowed this way only in a
UTF-8 database, and only when it is valid UTF-8 without zero bytes; anything
else takes the native path, which detoasts, converts or reports the error.

A `Rune` reads `text`, `varchar` or `bpchar` that holds exactly one Unicode
scalar value. Empty text fails, and so does `e` followed by a combining accent,
which is two scalar values. Reading a query result or raw datum of padded `bpchar`,
such as `'a'::char(3)`, fails too; a `Rune` argument receives `bpchar` through
PostgreSQL's `varchar` cast, which removes the trailing spaces.
`Rune[]` and `PgArray<Rune?>` map to `varchar[]`.

`PgTextView.Datum` retains the original server-encoded representation. Its
`TypeOid` preserves domains and the `varchar`/`bpchar` identities accepted by
raw reads; padded `bpchar` text retains trailing spaces. Generated view signatures
use `text` or `bytea`, and returned raw datums must match that declared SQL type.
Use an explicit conversion or a [bound raw return](/raw-values/#raw-sql-values) when choosing
another SQL identity.

## Raw values and SPI

```csharp
using PgTextView text = Spi.ExecuteScalar<PgTextView>("SELECT 'café'::text");
string copy = text.ToString();

using SpiRawResult rows = Spi.QueryRaw("SELECT '\\x0000ff'::bytea");
using PgByteaView bytes = rows[0][0].Read<PgByteaView>();
byte first = bytes[0];
```

Typed scalar SPI results and PostgreSQL function calls preserve view storage
after temporary result cleanup. Views bind as parameters with their original
type identity. A view read from an existing `PgDatum` borrows that datum's
lifetime. Reading SQL NULL produces a null view; directly constructing a view
from a NULL datum is rejected.

Dispose explicitly created views while their backend remains active. Disposing
a view, deleting or resetting its source context, or ending its input callback
invalidates native access. `ResetOnly()` invalidates aliases even when private
child contexts survive. A view created from another view retains the original
source dependency. Copied length and type metadata remain readable after expiry.
Copy a datum into an independent memory context before its source expires if it
must survive that source.

## Direct spans

`DangerousGetSpan()` and `DangerousGetUtf8Span()` validate once and expose a
readonly span without copying. The span cannot check later native lifetime
changes. Keep it on the originating backend thread, and do not retain it across
backend calls, source resets, view disposal or callback exit. Checked indexing
and copy methods validate native access each time they are called.

## C strings and native type I/O

PostgreSQL uses the `cstring` pseudo-type for native function and type-I/O
signatures. `PgCString` owns its exact bytes in managed memory;
`PgCStringView` borrows terminated native storage under the same checked
source and callback lifetimes as the other views. Neither representation
implicitly transcodes between UTF-8 and the server encoding.

```csharp
PgCString bytes = new([0x63, 0x61, 0x66, 0xe9]);
PgCString utf8 = PgCString.FromUtf8("café");
PgCString terminated = PgCString.FromNullTerminatedBytes([0x41, 0]);
```

The ordinary constructor takes payload bytes without a terminator and rejects
embedded zeros. `FromNullTerminatedBytes` requires exactly one zero at the end;
it rejects missing terminators and data following an earlier zero. Empty values
remain present and contain a single terminator. A null reference represents
SQL NULL.

`Count`, indexing, enumeration, `CopyTo` and `ToArray` exclude the terminator.
`PgCString.AsSpan()` exposes the readonly managed payload;
`AsNullTerminatedSpan()` includes its final zero. The borrowed equivalents are
`DangerousGetSpan()` and `DangerousGetNullTerminatedSpan()`, with the native
lifetime restrictions described above. `PgCStringView.ToOwned()` makes an
independent byte-preserving value that survives the native owner.

Both representations provide `ToUtf8String()` for explicit strict UTF-8 decoding.
Invalid UTF-8 remains valid raw C-string data; decoding it throws without
replacing any bytes. `FromUtf8` likewise rejects unpaired managed surrogates and
embedded zero characters. When calling a native API that expects the server's
encoding, supply bytes in that encoding.

Generated functions use `cstring` for these representations. Owned vectors and
`PgArray<PgCString?>` retain C-string array cells and shape; lazy
`PgArrayView<PgCString?>` and `PgArrayView<PgCStringView?>` read individual cells.
Typed SPI results, raw datum reads and PostgreSQL function calls use the same
byte-preserving contracts. C-string input routines can receive a zero native
address without the SQL NULL flag: typed C-string reads recognize that absence,
while the original `PgDatum.IsNull` metadata remains unchanged.

`cstring` and arrays of `cstring` cannot be stored as PostgreSQL table or
composite attributes. Use these
representations where a native API or type-I/O signature requires them.
The [raw custom-type example](/raw-values/#custom-type-representation) describes
the surrounding SQL type declarations and ownership requirements.
