# Bytea

This sample ports pgrx's `bytea` example. It compresses arbitrary bytes with
`gzip`, returns the first gzip member's bytes with `gunzip`, and decodes its
payload as strict UTF-8 with `gunzip_as_text`.

```sql
CREATE EXTENSION ankus_bytea;

SELECT gunzip_as_text(gzip('hi there'));
-- hi there

SELECT gunzip(gzip('hi there'::bytea));
-- \x6869207468657265

SELECT gunzip_as_text(gzip(convert_to('中文 € 😀', 'UTF8')));
-- 中文 € 😀
```

The untyped `'hi there'` literal resolves to the function's `bytea` argument.
An existing `text` or `varchar` value needs an explicit conversion such as
`convert_to(value, 'UTF8')`. PostgreSQL does not implicitly convert those typed
arguments when resolving this function.

SQL NULL arguments return SQL NULL. Present empty bytes produce a complete gzip
member; decoding that member returns present empty bytea or empty text.

`gunzip` preserves pgrx's libflate decoder behavior: it consumes only the first
member, requires all eight trailer bytes, and verifies the data checksum. Later
members and trailing content are separate inputs. Like that decoder, it reads
but does not compare the trailer's ISIZE field and does not independently reject
unused header flags. Optional header checksums use libflate's reconstructed
header policy. These functions are a sample codec and do not validate archives.

The sample owns gzip framing around .NET's standard raw DEFLATE codec. This
preserves empty members and the first-member boundary without changing runtime
compression settings or using a patched serializer.

Malformed gzip data raises `InvalidDataException`. Invalid UTF-8 raises
`DecoderFallbackException` and never inserts replacement characters; binary
`gunzip` still returns those bytes exactly. Ankus reports these ordinary .NET
exceptions as SQLSTATE `38000`. pgrx's `expect` failures panic and report `XX000`.
After an error, roll back the failed transaction or savepoint before issuing
another query.
