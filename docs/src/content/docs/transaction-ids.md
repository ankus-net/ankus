---
title: Transaction IDs
description: Use PostgreSQL xid values without confusing them with object IDs.
---

Use `PgTransactionId` for PostgreSQL `xid`. A C# `uint` maps to PostgreSQL
`oid`, so the two types are not interchangeable.

```csharp
[PgFunction]
public static PgTransactionId Echo(PgTransactionId value) => value;
```

`Value` contains the raw 32-bit ID. `Invalid`, `Bootstrap`, `Frozen`, and
`FirstNormal` expose PostgreSQL's special values. Returning `Invalid` produces
SQL `NULL`, matching pgrx.

`PgTransactionId` also works in SPI parameters, rows, vectors, and `PgArray<T>`.

Call `ToFullTransactionId()` inside an extension callback to add the current
PostgreSQL epoch:

```csharp
ulong fullId = transactionId.ToFullTransactionId();
```

The method reads the server's next full transaction ID and handles 32-bit wrap.
It requires the active PostgreSQL backend thread.

Subtransaction callbacks receive `PgSubtransactionId` for the current and parent
IDs. Use its `Value` property when raw numeric access is needed.

The [subtransaction information sample](https://github.com/ankus-net/ankus/tree/main/samples/Ankus.Examples.Subtransactions)
reports a transaction ID's status, parents and nesting level from PostgreSQL's
`pg_subtrans` data.
