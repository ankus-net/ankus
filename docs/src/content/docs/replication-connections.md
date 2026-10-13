---
title: Replication connections
description: Connect from a backend to a PostgreSQL server to run SQL, copy tables and stream logical changes.
---

A logical replication extension needs to reach another server from inside
PostgreSQL: to read its catalog, copy a table's rows and stream changes from a
replication slot. `PgReplicationConnection` makes that connection through
PostgreSQL's own `libpqwalreceiver` module, the same way the built-in logical
replication subscriber connects. The extension needs no client library of its
own, and the connection behaves the same on every platform and supported
PostgreSQL version.

pgrx has no equivalent. A pgrx extension links libpq or another client itself.

## Connecting and running SQL

```csharp
using PgReplicationConnection connection =
    PgReplicationConnection.Connect("host=provider dbname=shop user=replicator", "shop_subscriber");

IReadOnlyList<string?[]> rows = connection.Query("SELECT relname, relkind FROM pg_class LIMIT 5", columns: 2);
string? systemId = connection.Query("IDENTIFY_SYSTEM", columns: 4)[0][0];
connection.Execute("SET statement_timeout = '30s'");
```

The connection is a logical replication connection to the database the
connection string names, which also runs ordinary SQL. `Query` returns each row's
values as the server formats them, with `null` for SQL NULL, and requires the
query's exact column count. `Execute` runs a command that returns no rows.
Commands take no bind parameters, so quote values with
[`Spi.QuoteLiteral`](/spi/) and `Spi.QuoteIdentifier`, which also work in a
background worker between transactions.

`Query` needs a transaction, because PostgreSQL looks up the result's column
types in the catalog, as the built-in subscriber does inside its transactions.
A background worker runs such queries inside `PgBackgroundWorker.RunTransaction`.
Commands without rows, `StartCopy`, `Receive`, `Send` and `EndStreaming` work
outside transactions, so a worker can stream while idle.

A command that fails on the server throws `PgRemoteException` with the server's
SQLSTATE and message. That failure belongs to the server, so the backend's own
transaction is unaffected and the connection stays usable. PostgreSQL 13 does not
pass the SQLSTATE on, so `SqlState` is null there. A connection that cannot be
made, or that breaks, raises a PostgreSQL error in the backend, as it does for
the built-in subscriber.

## Copying and streaming

`StartCopy` starts `COPY ... TO STDOUT` or `START_REPLICATION`, and `Receive`
returns its messages:

```csharp
connection.StartCopy("START_REPLICATION SLOT \"shop\" LOGICAL 0/0 (proto_version '1', publication_names 'shop')");
while (PgBackgroundWorker.CanContinue)
{
    switch (connection.Receive(TimeSpan.FromSeconds(10), out ReadOnlySpan<byte> message))
    {
        case PgReplicationReceiveStatus.Message:
            Apply(message);
            break;
        case PgReplicationReceiveStatus.Ended:
            return;
    }
}

connection.EndStreaming();
```

`Receive` waits on the connection's socket and on the backend's latch together,
so a worker asked to stop wakes up (`Woken`), and query cancellation and
statement timeouts interrupt the wait. A message is valid until the
connection's next call. During streaming, `Send` sends standby status updates
and other messages to the server; `EndStreaming` returns the connection to
running commands. A `COPY ... TO STDOUT` ends by itself, and the connection runs
commands again after `Receive` returns `Ended`.

The messages are the replication protocol's: `w` for a change with its WAL
positions and the output plugin's bytes, and `k` for a keepalive that may ask
for a status update. The output plugin, such as `pgoutput`, decides what the
change bytes contain.

## Copying a table

A subscriber usually starts by copying each table's existing rows. `CopyInto`
reads a running `COPY ... TO STDOUT` straight into a local table, as the
built-in subscriber's table synchronization does:

```csharp
connection.StartCopy("COPY public.items (id, name) TO STDOUT");
long rows = connection.CopyInto(itemsOid, ["id", "name"]);
```

The rows go through PostgreSQL's own `COPY FROM` in the current transaction, so
constraints, indexes, partition routing and triggers behave as they do for
`COPY`. The column list names the local columns that receive the remote
columns, in order; other columns get their defaults. The copy must use COPY's
text format. It needs `INSERT` on the table and, like the built-in subscriber,
refuses a table whose row-level security applies to the user, because `COPY
FROM` does not apply policies. If the copy fails, the server's side is left
unfinished, so dispose the connection.

## Lifetime

A connection belongs to the backend and thread that opened it and outlives
transactions, so a worker can keep one open across many. Dispose it when done;
an error that unwinds a `using` block still closes it, and the server then
releases the replication slot it was streaming. Connections left open close when
the backend exits.

Logical replication connections need `max_wal_senders` above zero and a role with
the `REPLICATION` attribute, and streaming from a logical slot needs
`wal_level = logical` on the server. On releases with `output_plugin_libraries`,
the server must also trust the output plugin; see
[logical decoding output plugins](/logical-decoding/).
