---
title: StringInfo buffers
description: Append exact bytes and UTF-8 text to PostgreSQL-owned StringInfo storage.
---

`PgStringInfoStream` is the .NET counterpart of pgrx's `StringInfo`: a write-only
`Stream` over PostgreSQL's growable binary buffer. Use it during synchronous
backend callbacks when a native API needs a `StringInfo`, or when constructing
bytes in a PostgreSQL memory context.

```csharp
using System.Text;

[PgFunction]
public static byte[] BuildPayload()
{
    using PgStringInfoStream buffer = PgStringInfoStream.Create();
    buffer.Write([0, 128, 255]);
    buffer.Write("café");
    buffer.Write(new Rune(0x1F418));
    return buffer.ToArray();
}
```

Byte writes preserve embedded NUL and malformed UTF-8. Text writes encode strict
UTF-8 directly, regardless of the database's server encoding. An unpaired UTF-16
surrogate is rejected before any text is appended. `Write(char)` accepts a
non-surrogate character; use a `Rune` or complete string for supplementary
characters.

`ToArray` and `CopyTo(Span<byte>, offset)` copy exact bytes into independent
managed storage. `WriteAt(offset, bytes)` replaces an existing range without
changing its length. Checked copies include lifetime validation even for empty
ranges. The stream does not support seeking, stream reads, or arbitrary
`SetLength` calls.

`ToString()` decodes strict UTF-8 and throws `DecoderFallbackException` for
malformed input. `ToStringLossy()` explicitly replaces malformed sequences for
display, matching pgrx's `Display` formatting. It is not used for SQL datum
conversion.

## Capacity and formatting

`Length` and `Capacity` count payload bytes, excluding the trailing NUL.
`Create(capacity)` reserves at least that many bytes; PostgreSQL may allocate
more. `Enlarge(additionalByteCount)` reserves bytes beyond the current length,
whereas `EnsureCapacity(capacity)` reserves an absolute minimum. Growth preserves
the payload and native cursor. `Reset()` clears the length and cursor and writes
the initial NUL while retaining capacity.

Use ordinary .NET formatting through `StreamWriter`:

```csharp
using PgStringInfoStream buffer = PgStringInfoStream.Create();
using (var writer = new StreamWriter(
    buffer, new UTF8Encoding(false, true), leaveOpen: true))
{
    writer.Write("value=");
    writer.Write(42);
}

string result = buffer.ToString(); // value=42
```

Flush or dispose the writer before reading the native buffer. The writer has its
own managed buffering; the stream's writes themselves take effect immediately.
`WriteAsync`, `FlushAsync`, and `DisposeAsync` perform their work synchronously
on the invoking backend thread. They do not make PostgreSQL access safe after
an asynchronous continuation or on another thread.

## Ownership and native interoperability

`Create` allocates both the native struct and its data in the supplied
`PgMemoryContext`, or the current context when omitted. Growth keeps that owner
even if another context becomes current. `LifetimeContext` exposes it. Disposal
frees both allocations; context reset or deletion invalidates the handle and
makes later disposal harmless. There is no PostgreSQL-calling finalizer.
Bump contexts cannot own individually released, resizable buffers. Other native
allocators retain their own size and resize restrictions.

`DangerousBorrow(pointer, lifetimeContext)` wraps an initialized external native
struct without taking release rights; a null pointer returns null. Its context
anchor detects resets and deletion, but cannot prove the pointer's allocation
origin or detect an earlier external free. The unsafe caller must guarantee the
struct and data remain accessible, and writable buffers must satisfy native
`repalloc` requirements. A stack struct is permitted. Borrowed disposal closes
only the managed wrapper.

PostgreSQL 17 and later also define read-only StringInfo views with `maxlen = 0`.
These may point to non-palloc or unterminated storage. Borrowing supports checked
reads of these bytes and rejects mutation. `Capacity` reports their payload
length. `CanWrite` is false for read-only views and disposed wrappers; it does
not prove that the native context is still alive.

Unsafe pointer operations preserve native ownership rules:

| Operation | Result and obligations |
|---|---|
| `DangerousGetPointer()` | Native struct pointer; do not free owned storage or change its ownership while the wrapper is live |
| `DangerousGetDataPointer()` | Current data pointer; growth may relocate it; caller enforces bounds and lifetime |
| `DangerousAppend(pointer, length)` | Copies accessible bytes; a source slice within the current payload remains valid across growth |
| `DangerousDetach()` | Consumes the wrapper and transfers the whole struct and data without freeing either |
| `DangerousDetachData()` | Consumes the wrapper, transfers data, and frees an owned struct; borrowed storage stays untouched |
| `DangerousDetachCString()` | Same as data transfer, but rejects interior NUL and a missing trailing NUL before consuming the wrapper |

A C-string transfer from external storage requires the unsafe caller to prove
`Length + 1` readable bytes. Arbitrary read-only views do not provide that
guarantee. A rejected transfer preserves the handle and bytes. Transferring a
borrowed pointer does not acquire its allocator's release rights. Transferred
owned memory retains its PostgreSQL context lifetime and requires native cleanup
or context reclamation.

See [memory contexts](/memory-contexts/) for backend capabilities, allocation
ownership, and transaction lifetimes.
