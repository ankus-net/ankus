---
title: Custom types
description: Declare PostgreSQL base types with CBOR, native struct storage, or explicit codecs.
---

Put `[PgType]` on a record, class, struct, or enum. Ankus generates a serializer,
the PostgreSQL type, its input/output functions, and its array type:

```csharp
[PgType]
public sealed record Reading(string Sensor, decimal Value, string? Unit);
```

```sql
CREATE TABLE readings(value reading);
INSERT INTO readings VALUES ('{"Sensor":"outside","Value":19.125,"Unit":"°C"}'), (NULL);
SELECT value FROM readings;
```

Default types store CBOR and use JSON for SQL text input/output, following pgrx's
`PostgresType` model. Ankus owns the contract generator and serialization rules;
Microsoft's `System.Formats.Cbor` and `System.Text.Json` provide token readers and
writers. Generated code calls constructors and accesses members directly. No
reflection-based serializer or separate JSON context is needed.

For a manually declared SQL type or an existing external type, use
[`PgDatumType` with reader and writer converters](/raw-values/#reusable-scalar-mappings)
to control scalar storage conversion independently of type DDL and serialization.

The [type variants sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.TypeVariants)
ports pgrx's `postgres_type_variants` example and compares these choices side by
side: generated JSON, a custom text codec, native struct storage, a hand-written
datum mapping and arrays of a custom type.

## Datum alignment

Generated types use PostgreSQL's four-byte (`int4`) alignment. Select eight-byte
(`double`) alignment when the database storage contract requires it:

```csharp
[PgType(Alignment = PgTypeAlignment.EightBytes)]
public sealed record Reading(string Sensor, decimal Value, string? Unit);
```

`PgTypeAlignment.FourBytes` selects the default explicitly. PostgreSQL requires
at least four-byte alignment for these variable-length types; invalid enum values
produce a build diagnostic. This setting controls the datum's database storage
alignment. It does not change the serializer's bytes, add padding to the payload,
or change the managed type's layout. The variable-length header still precedes
the payload. Changing the storage alignment of an existing type requires a data
migration, as with other changes to its native storage contract.

## Generated contracts

The default serializer supports:

- Public instance fields and properties of accessible classes, structs,
  and records, including inherited members, virtual overrides, and immutable
  constructor-bound members. Abstract classes require generated storage with
  declared concrete variants; explicit storage codecs require a concrete managed type.
- Booleans, signed and unsigned 8/16/32/64-bit integers, `float`, `double`,
  `decimal`, and Unicode strings. Decimal CBOR uses the decimal-fraction tag;
  decimal scale, including the scale of zero, is retained. JSON decimal input that would
  require rounding is rejected. A `decimal` with zero magnitude and its sign bit
  set is rejected because CBOR decimal fractions cannot preserve that sign bit.
- `Guid`, `DateOnly`, `TimeOnly`, `DateTime`, `DateTimeOffset` and `TimeSpan`,
  written as strings in System.Text.Json's formats: lowercase `D` for a `Guid`,
  `yyyy-MM-dd` for a date, `hh:mm:ss[.fffffff]` for a `TimeOnly`, the constant
  `[-][d.]hh:mm:ss[.fffffff]` form for a `TimeSpan`, and ISO 8601 for a
  `DateTime`, with its kind, or a `DateTimeOffset`,
  with its offset. CBOR stores a `Guid` as a tagged 16-byte UUID (tag 37), a date
  as a tagged full-date (tag 1004) and a `DateTimeOffset` as a tagged date/time
  string (tag 0). A time of day must be under 24 hours.
- Enums as case-sensitive strings. Undefined numeric values and unnamed flag
  combinations are rejected.
- One-dimensional arrays, `List<T>`, and `Dictionary<string, T>`, nested with
  other supported contracts. `byte[]` is a sequence of integers in both formats,
  like a Rust `Vec<u8>`.
- Nullable values, nullable containers, and nullable elements at every level.
  SQL NULL bypasses the serializer; JSON or CBOR null cannot represent a present
  top-level value.

Member names keep their C# spelling. `[JsonPropertyName]` changes a member's key
in both formats. `[JsonIgnore]` excludes a member; `Condition = Never` includes
it. `[JsonStringEnumMemberName]` changes an enum's stored name.

JSON text output is compact, like pgrx's `serde_json` output. It has no
whitespace, and strings and member names escape only quotation marks,
reverse solidus and control characters below U+0020. Those controls use `\b`,
`\t`, `\n`, `\f` and `\r` where available, and otherwise lowercase `\u00xx`.
Other characters, including non-ASCII text and HTML punctuation, are written
unescaped. Input accepts any valid JSON escape.

Serialized keys, enum names and string discriminators preserve their exact
Unicode text in JSON and CBOR, including empty strings and zero characters.
This also applies to attributes imported from another assembly and attributes
inherited by overridden properties. UTF-8 encoding rejects unpaired UTF-16
surrogates and never replaces them. PostgreSQL `jsonb` cannot
represent zero characters; keep such values in the custom type's storage or
its JSON text representation.

An accessible `[JsonConstructor]` selects the constructor. Otherwise Ankus uses
an accessible parameterless constructor, or the sole accessible constructor.
Every parameter must match a serialized member's C# name (ignoring case) and
exact type, including nullability. Remaining members need accessible setters or
init accessors, or writable fields. Constructor-bound members keep the value
assigned by the constructor, including any normalization. A constructor that
binds or deliberately ignores C# `required` members must carry
`[SetsRequiredMembers]`; Ankus diagnoses missing annotations and never overwrites
the constructor's values to satisfy C# initializer requirements.

Generated code follows ordinary C# assembly access. Types, constructors and
setters may be `public`, `internal` or `protected internal` when accessible from
the extension assembly. Referenced assemblies must grant access to their
nonpublic declarations. Private, protected and private-protected declarations
are inaccessible to the generated serializer. Serialized fields, properties
and property getters still need to be public.

Missing non-nullable members are errors. Missing nullable members become null;
`required` and `[JsonRequired]` require presence even when null is permitted.
Duplicate known members and duplicate dictionary keys are rejected. Unknown
members are skipped with input validation, allowing readers to tolerate added
fields. JSON names and dictionary keys are case-sensitive.

Dictionary entries are written in ordinal (UTF-16 code unit) key order, in both
JSON and CBOR. Equal dictionaries therefore produce identical storage bytes and
text regardless of insertion order or comparer, unlike pgrx's `HashMap` storage.
Readers accept keys in any order, including storage written by earlier versions.
A deserialized dictionary uses `StringComparer.Ordinal`; the original comparer is
not stored. Copy it to a dictionary with another comparer if lookups need one,
for example `new Dictionary<string, T>(value, StringComparer.OrdinalIgnoreCase)`.
Writing fails when a comparer admits two keys that are equal under ordinal
comparison, because storage could not distinguish them.

Unsupported shapes and serialization attributes produce a specific declaration
diagnostic. Hidden inherited members, arbitrary framework types,
multidimensional arrays, and non-string dictionary keys currently require an
explicit codec. Passing an undeclared runtime subclass is rejected so additional
state is not silently discarded. Nesting is limited to 64 containers; cyclic
graphs fail at that limit. CBOR preserves infinities, negative zero and every
NaN's sign and payload bits, including signaling NaNs. A NaN uses a full-width
`float` or `double` encoding. pgrx's `serde_cbor` storage writes CBOR's shorter
canonical NaN for every NaN. Input that would narrow a wider CBOR
value or NaN payload into `float` is rejected. JSON output rejects non-finite
values because JSON has no exact representation.

Malformed JSON input raises SQLSTATE `22P02`; malformed CBOR raises `22P03`.
Trailing data, numeric overflow or underflow, required null values, and invalid Unicode are
errors. pgrx's default input function returns SQL NULL when JSON parsing fails;
Ankus deliberately raises `22P02` instead.

## Tagged variants

Use standard `System.Text.Json.Serialization` attributes to declare a closed set
of variants, corresponding to tagged Rust enum variants:

```csharp
[PgType]
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(Measured), "measured")]
[JsonDerivedType(typeof(Unavailable), "unavailable")]
public abstract record Measurement(string Sensor);

public sealed record Measured(string Sensor, decimal Value, string? Unit)
    : Measurement(Sensor);
public sealed record Unavailable(string Sensor, string Reason) : Measurement(Sensor);
```

```sql
CREATE TABLE measurements(value measurement);
INSERT INTO measurements VALUES
    ('{"kind":"measured","Sensor":"outside","Value":19.125,"Unit":"°C"}'),
    ('{"Sensor":"outside","Reason":"offline","kind":"unavailable"}');
```

Each registered type needs a unique string or signed 32-bit integer discriminator.
Integer `7` and string `"7"` identify different variants. The property defaults to
`$type` when `[JsonPolymorphic]` is omitted. Writers put the discriminator first;
readers accept it anywhere in the object. Both JSON and CBOR store the same
discriminator and all inherited serialized members. Variants can appear in
nullable members, arrays, lists, dictionaries, and recursive contracts.

An abstract base requires a discriminator on input. A concrete base also accepts
an object without one and constructs the exact base type. Registering the base
itself gives its output an explicit discriminator. Unknown, duplicate, null,
incorrectly typed, and out-of-range discriminators are errors. Unregistered
runtime types, including subclasses of a registered variant, are errors.
Fallback options that discard concrete type identity are diagnosed at compile
time. A discriminator property cannot share a name with a serialized member.
Changing registrations or discriminator values changes the persisted contract.

## Referenced attribute metadata

Ankus reads exact declaration and serialization attribute strings from the
defining assembly. It preserves storage keys and discriminator identities;
SQL type and schema names still require nonempty identifiers of at most
63 UTF-8 bytes, without zero characters. Inherited schemas and custom
`NullInputErrorMessage` values receive the same exact validation as source
declarations.

Malformed UTF-8, invalid constructor assembly identities, incomplete attribute arguments and trailing attribute data
produce `ANKUS206`, identifying the failing attribute and its defining member.
The error points to the consuming SQL parameter, result or custom-type
declaration. Rebuild the defining assembly with valid metadata. Ankus does not
generate a conversion or storage codec from a partially decoded contract.

## Custom text with generated storage

Set `TextCodec` to a `PgTypeTextCodec<T>` when you want a custom SQL text format
while retaining the generated CBOR contract. The codec supplies only `Parse` and
`Format`; the generator still validates and serializes the type's members.

```csharp
using System.Globalization;
using Ankus;

[PgType(TextCodec = typeof(RgbColorTextCodec), BinaryProtocol = true)]
public readonly record struct RgbColor(byte Red, byte Green, byte Blue);

public sealed class RgbColorTextCodec : PgTypeTextCodec<RgbColor>
{
    public override RgbColor Parse(string text)
    {
        if (text.Length != 7 || text[0] != '#' ||
            !byte.TryParse(text.AsSpan(1, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte red) ||
            !byte.TryParse(text.AsSpan(3, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte green) ||
            !byte.TryParse(text.AsSpan(5, 2), NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out byte blue))
        {
            throw new PgException("22P02", "Color must use #RRGGBB hexadecimal notation.");
        }

        return new(red, green, blue);
    }

    public override string Format(RgbColor value) =>
        string.Create(CultureInfo.InvariantCulture, $"#{value.Red:X2}{value.Green:X2}{value.Blue:X2}");
}
```

```sql
SELECT '#12abef'::rgb_color::text; -- #12ABEF
SELECT NULL::rgb_color;          -- SQL NULL
```

CBOR stores the `Red`, `Green`, and `Blue` members. Binary send/receive, managed
function arguments, SPI, and arrays use that storage contract without calling
the text codec. Nested values retain their structural contracts; a nested type's
`TextCodec` does not replace its representation inside a containing object.
Text codecs also work with generated enum and tagged-variant contracts.

Ankus constructs one text codec lazily on its first text operation. Binary
operations neither construct nor invoke it, even if its constructor has failed.
Throw `PgException` to choose the SQLSTATE and diagnostics; other exceptions
become `38000` after managed frames unwind. Returning null for a present value
or returning null text is an error. Output must contain valid Unicode without
zero characters.

Both text and full storage codecs must be accessible, concrete types with an
accessible parameterless constructor and an exact, non-nullable `T`. Closed
generic codec types are supported. Constructors for codecs with C# `required`
members need `[SetsRequiredMembers]`. `TextCodec` cannot be combined with the
positional full storage codec option.

## Native storage

Use `NativeLayout = true` with `TextCodec` when the stored value should be an
unmanaged struct's own bytes, as pgrx's `pgvarlena_inoutfuncs` stores a
`repr(C)` struct. Structs in the layout are sequential, which is the C# default;
an explicit `StructLayout` may set `Pack`. For example, this variant of the color
above stores exactly three payload bytes:

```csharp
using System.Runtime.InteropServices;
using Ankus;

[StructLayout(LayoutKind.Sequential, Pack = 1)]
[PgType(NativeLayout = true, TextCodec = typeof(PackedColorTextCodec), BinaryProtocol = true)]
public readonly record struct PackedColor(byte Red, byte Green, byte Blue);

public sealed class PackedColorTextCodec : PgTypeTextCodec<PackedColor>
{
    private readonly RgbColorTextCodec _text = new();

    public override PackedColor Parse(string text)
    {
        RgbColor value = _text.Parse(text);
        return new(value.Red, value.Green, value.Blue);
    }

    public override string Format(PackedColor value) =>
        _text.Format(new(value.Red, value.Green, value.Blue));
}
```

```sql
SELECT encode(packed_color_send('#12abef'::packed_color), 'hex'); -- 12abef
```

Supported fields are `byte`, `sbyte`, `short`, `ushort`, `int`, `uint`, `long`,
`ulong`, `float`, `double`, enums, nested sequential structs declared in the
same assembly, and fixed buffers of these numeric primitives. All instance fields
participate, including private fields and auto-property backing fields; JSON
member attributes do not change the layout. Empty structs, reference fields,
booleans, characters, pointers, native-sized integers, generic structs, opaque
framework or external structs, explicit layouts, size overrides and inline
arrays are rejected, so no field holds a process-dependent value.

Fields are aligned as C aligns them, limited by `Pack`, so a struct with a `byte`
followed by an `int` stores eight bytes. Every write sets the padding bytes to
zero, because .NET does not preserve them when it copies a struct; equal values
therefore store equal bytes. `Pack = 1` removes the padding.

Storage uses field order and the host's native byte order. Floating-point bit
patterns and unnamed enum values are retained without text conversion. Binary
send/receive, when enabled, exposes the same bytes; this is not the generated
CBOR wire format. The payload excludes PostgreSQL's varlena header. Input must
have exactly the generated size or it raises SQLSTATE `22P03`. Text conversion
can enforce domain rules, but binary input validates size only; use an explicit
storage codec if you need additional binary validation.

Changing field order, field types, packing or byte order requires a data
migration. Using the struct directly in functions, SPI, arrays and sets copies
values into independent managed storage. See the compiled
[custom-type sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.CustomTypes).

## Native borrowing and ownership

Use `PgVarlena<T>` with a registered `NativeLayout` type for checked native
storage. It represents the same SQL type as `T`, with nullable wrappers for SQL
NULL. The `NativeLayout` field and layout restrictions still apply.

```csharp
[PgFunction]
public static PgVarlena<PackedColor> MakeRed(PgVarlena<PackedColor> color)
{
    PackedColor value = color.Value;
    color.Value = value with { Red = 255 };
    return color;
}
```

A direct scalar input borrows PostgreSQL's storage when detoasting can reuse the
original datum. Its first write allocates a private copy in the captured source
context. When detoasting expands a short header, decompresses a value or fetches
external TOAST storage, the input is already a writable temporary. Mutating
either form preserves the original SQL value. `Value` reads a copy of `T` and
writes the complete payload; it never exposes a managed reference into expiring
storage. `IsBorrowed` reports whether the next write needs a copy. A false value
can also mean a writable detoast temporary, so it does not imply a longer lifetime.

Unmodified borrowed inputs and writable detoast temporaries expire when their
managed callback exits. Nested callbacks preserve an outer input's lease; a
later callback cannot revive an expired one. Independently allocated values,
including a borrowed input after its first write, follow their context's lifetime.
Access also requires the originating backend thread and extension provider.

Create a zeroed value with `new PgVarlena<T>(context)`, or initialize it with
`new PgVarlena<T>(value, context)`. Omitting the context uses the current context.
`Clone()` creates independent storage in the source context; `Clone(destination)`
selects a different owner when the copy must outlive the input. Resetting or
deleting the owner invalidates its values. Dispose an independent wrapper to free
its allocation early. Disposing an input view does not free PostgreSQL's input
buffer. Context cleanup reclaims forgotten allocations.

`IntoDatum()` explicitly transfers storage to a checked `PgDatum`, consuming the
wrapper and every managed alias to it only after successful transfer. Input views
are copied first, including writable detoast temporaries. A failed transfer leaves
the original wrapper usable for retry. The datum retains its exact SQL type and
expires with its context. Normal function returns and SPI parameter bindings copy
the payload and leave wrapper aliases usable during their lifetimes.

Untyped SPI results use the canonical managed `T`. Requesting `PgVarlena<T>`
explicitly creates an independent native copy in the current context. Wrapper
arrays preserve SQL NULL, dimensions and lower bounds through `PgArray<T>`;
vector conversions reject shape loss. Generated set and aggregate inputs use
independent native copies in the result owner's context so deferred iteration
and retained aggregate arguments remain valid between callbacks. Text operations
share the native type's single lazy text codec.

## NULL input policy

Generated input functions are `STRICT` by default. Set `NullInputErrorMessage`
to make a direct NULL call to the text input function raise SQLSTATE `22004`:

```csharp
[PgType(TextCodec = typeof(RgbColorTextCodec),
    NullInputErrorMessage = "Color input must not be NULL.")]
public readonly record struct RgbColor(byte Red, byte Green, byte Blue);
```

```sql
SELECT rgb_color_in(NULL); -- ERROR: Color input must not be NULL.
SELECT NULL::rgb_color;    -- The same error during literal coercion.
```

The error occurs before codec construction. PostgreSQL invokes non-strict input
functions when coercing untyped NULL literals, including inferred nullable
function arguments, and for NULL elements in a text array or fields in text COPY.
This option rejects all of those inputs. Already-typed SQL NULL values, such as
a nullable managed function result, a stored NULL or a binary COPY NULL, still
bypass the codec and can flow through nullable arguments and arrays.
This option also applies to default JSON types and full storage codecs. Output,
binary send, and binary receive functions remain strict.

## Explicit codecs

Provide a `PgTypeCodec<T>` to define the SQL text format and stored bytes yourself.

This example stores distances as eight-byte integers and displays them as `125mm`:

```csharp
using System.Buffers;
using System.Buffers.Binary;
using System.Globalization;
using Ankus;

[PgType(typeof(DistanceCodec))]
public readonly record struct Distance(long Millimeters);

public sealed class DistanceCodec : PgTypeCodec<Distance>
{
    public override Distance Parse(string text)
    {
        if (!text.EndsWith("mm", StringComparison.Ordinal) ||
            !long.TryParse(text.AsSpan(0, text.Length - 2), NumberStyles.AllowLeadingSign,
                CultureInfo.InvariantCulture, out long millimeters))
        {
            throw new PgException("22P02", "Distance must be an integer followed by mm.");
        }

        return new(millimeters);
    }

    public override string Format(Distance value) =>
        value.Millimeters.ToString(CultureInfo.InvariantCulture) + "mm";

    public override Distance Read(ReadOnlySpan<byte> payload)
    {
        if (payload.Length != sizeof(long))
        {
            throw new PgException("22P03", "Invalid distance payload.");
        }

        return new(BinaryPrimitives.ReadInt64BigEndian(payload));
    }

    public override void Write(Distance value, IBufferWriter<byte> destination)
    {
        BinaryPrimitives.WriteInt64BigEndian(destination.GetSpan(sizeof(long)), value.Millimeters);
        destination.Advance(sizeof(long));
    }
}
```

After installing the extension:

```sql
CREATE TABLE measurements(value distance);
INSERT INTO measurements VALUES ('125mm'), (NULL);
SELECT value FROM measurements;
```

Use a custom type directly in `[PgFunction]` methods, aggregate callbacks, operators,
casts, and SPI parameters and results. `Distance?` represents SQL NULL.
`Distance?[]` supports nullable elements; `PgArray<Distance?>` also preserves
dimensions and lower bounds. Sets and TABLE results use the same conversions.

Add `[PgEquality]`, `[PgOrdering]`, and `[PgHashing]` to generate comparisons and
default B-tree/hash operator classes from the type's managed value contracts.
Use `IEquatable<T>` and `IComparable<T>` for equality and ordering, and
`IPgHashable` for an explicitly stable database hash. These contracts operate on
logical values independently of their stored representation. See
[generated type operators](/operators-and-casts/#generated-type-operators) for
requirements, stable hashing, enum ordering and installation dependencies.

## Names and installation

The SQL name defaults to snake case. Use `Name` to change it and `Schema` to
select a fixed schema. Types nested in a `[PgSchema]` class inherit that schema.
Without a fixed schema, the type follows the extension's installation schema,
including after relocation. Ankus creates types before functions that use them.
`Id` and `Requires` order custom installation SQL around the type.

`Sql` replaces the complete shell type, I/O functions and completed type SQL;
`GenerateSql = false` retains the codec, native I/O exports and managed type
mapping while omitting those declarations. Generated consumers still need a
compatible type with the declared name and schema. The replacement supports
native I/O tokens and must preserve the existing variable-length storage
contract. See [base-type SQL controls](/custom-sql/#replace-base-type-sql).

## Storage and errors

`Read` receives the complete payload without PostgreSQL's storage header.
`Write` supplies that payload through the buffer writer. PostgreSQL handles
large-value compression and external storage. Keep the byte format compatible
across upgrades or migrate existing rows when changing it.

Return independent values from `Read`; its input span is borrowed. Ankus creates
one codec instance on first use per extension runtime. Do not keep borrowed buffers or
backend call state in that instance. SQL NULL bypasses the codec; returning null
for a present input is an error. Exceptions become PostgreSQL errors after the
managed callback unwinds.

Set `BinaryProtocol = true` on `[PgType]` to enable binary send/receive, including
binary COPY. The wire payload uses the codec's storage format. `Read` must reject
invalid lengths, trailing bytes, and invalid field values.

For default serialization the binary payload is CBOR, with object members encoded
as text-keyed maps and arrays as sequences. Renaming members or enum labels,
changing types or nullability, or removing required constructor parameters changes
the persisted contract. Plan a data migration before making incompatible changes.
CBOR use alone does not guarantee interchangeability with every pgrx/Serde contract;
match member names and value representations explicitly.

Default serialization nests at most 64 levels. Each object, array, list and
dictionary is a level, and so is a `decimal`, which CBOR stores as a two-element
array; .NET's CBOR reader reads at most 64 levels. Writing a deeper value or a
cyclic graph fails, and reading deeper text or CBOR fails with `22P02` or
`22P03`. The limit is the same for text and binary, so any value that can be
written can be read back in either form.

## Declaration diagnostics

Custom-type diagnostics identify the exact option, codec, field, member,
constructor parameter, or serialization attribute that must change.

| IDs | Contract |
|---|---|
| `ANKUS419`–`ANKUS423` | Alignment, codec selection, native-layout selection, and NULL-input text |
| `ANKUS424`–`ANKUS429`, `ANKUS469` | Managed root shape, accessibility, generic closure, explicit codecs on abstract types, and conflicting declarations |
| `ANKUS430`–`ANKUS432` | Codec construction, exact managed type, and required-member initialization |
| `ANKUS433`–`ANKUS435` | Inherited schema, PostgreSQL type name, and schema name |
| `ANKUS436`–`ANKUS442` | Native-layout root, fields, struct layout, recursion, buffers, size, and empty payloads |
| `ANKUS443`–`ANKUS468` | Generated serialization shape, members, constructors, tagged variants, and JSON attributes |
| `ANKUS507` | A `Name` or `Schema` that no other `[PgType]` or `[PgEnum]` in the extension generates; the error points at the later type's name |

Editors offer **Make internal** for an inaccessible or file-local type (`ANKUS428`)
and **Make non-static** for a static class (`ANKUS425`).

Unreadable referenced attribute metadata remains `ANKUS206` because the
defining assembly must be rebuilt before its exact declaration can be validated.
