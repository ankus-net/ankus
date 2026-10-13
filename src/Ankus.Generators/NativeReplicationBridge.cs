namespace Ankus.Generators;

/// <summary>
/// Provides replication connections from a backend through PostgreSQL's own <c>libpqwalreceiver</c>, as the built-in
/// logical replication subscriber connects, so extensions need no client library of their own.
/// </summary>
internal static class NativeReplicationBridge
{
    /// <summary>
    /// Implements the guarded replication operations behind <c>PgReplicationConnection</c>.
    /// </summary>
    /// <remarks>
    /// Operations: 1 connects, 2 runs a command and copies its result, 3 receives one message, 4 sends one, 5 ends
    /// streaming, 6 closes and 7 reads a running <c>COPY ... TO STDOUT</c> into a local table through
    /// <c>BeginCopyFrom</c>, as the built-in subscriber's table synchronization does. Connections live in <c>TopMemoryContext</c> until closed or the backend exits; each
    /// keeps a scratch context for the result of its last command. The function table's layout and signatures change
    /// in PostgreSQL 16 and 17, so every call goes through the selected headers' macros.
    /// </remarks>
    internal const string Source = """

        #include "access/table.h"
        #include "access/xact.h"
        #include "commands/copy.h"
        #include "executor/tuptable.h"
        #include "libpq/pqformat.h"
        #include "miscadmin.h"
        #include "nodes/makefuncs.h"
        #include "parser/parse_node.h"
        #include "parser/parse_relation.h"
        #include "port/pg_bswap.h"
        #include "replication/walreceiver.h"
        #include "storage/latch.h"
        #include "utils/acl.h"
        #include "utils/builtins.h"
        #include "utils/rel.h"
        #include "utils/rls.h"
        #include "utils/snapmgr.h"
        #include "utils/timestamp.h"
        #include "utils/tuplestore.h"

        typedef struct AnkusReplicationConnection
        {
            struct AnkusReplicationConnection *next;
            uint64 id;
            WalReceiverConn *connection;
            MemoryContext scratch;
        } AnkusReplicationConnection;

        static AnkusReplicationConnection *ankus_replication_connections = NULL;
        static uint64 ankus_replication_next = 1;

        static AnkusReplicationConnection *
        ankus_replication_find(intptr_t id)
        {
            for (AnkusReplicationConnection *entry = ankus_replication_connections; entry != NULL; entry = entry->next)
            {
                if (entry->id == (uint64) id)
                    return entry;
            }

            ereport(ERROR, (errcode(ERRCODE_CONNECTION_DOES_NOT_EXIST),
                errmsg("the replication connection is closed or belongs to another backend")));
            return NULL;
        }

        /* Text crosses the boundary as UTF-8; the connection's client encoding is the database encoding. */
        static char *
        ankus_replication_server_text(intptr_t text)
        {
            const char *utf8 = (const char *) text;
            if (utf8 == NULL)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("replication text is missing")));
            return pg_any_to_server(utf8, strlen(utf8), PG_UTF8);
        }

        static void
        ankus_replication_send_text(StringInfo buffer, const char *text)
        {
            char *utf8;
            if (text == NULL)
            {
                pq_sendint32(buffer, -1);
                return;
            }

            utf8 = pg_server_to_any(text, strlen(text), PG_UTF8);
            pq_sendint32(buffer, (int32) strlen(utf8));
            pq_sendbytes(buffer, utf8, strlen(utf8));
            if (utf8 != text)
                pfree(utf8);
        }

        static void
        ankus_replication_connect(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            char *conninfo = ankus_replication_server_text(request->pointer);
            char *application = ankus_replication_server_text(request->data);
            char *failure = NULL;
            WalReceiverConn *connection;
            AnkusReplicationConnection *entry;
            if (WalReceiverFunctions == NULL)
                load_file("libpqwalreceiver", false);
        #if PG_VERSION_NUM >= 170000
            connection = walrcv_connect(conninfo, true, true, false, application, &failure);
        #elif PG_VERSION_NUM >= 160000
            connection = walrcv_connect(conninfo, true, false, application, &failure);
        #else
            connection = walrcv_connect(conninfo, true, application, &failure);
        #endif
            if (conninfo != (char *) request->pointer)
                pfree(conninfo);
            if (application != (char *) request->data)
                pfree(application);
            if (connection == NULL)
                ereport(ERROR, (errcode(ERRCODE_CONNECTION_FAILURE),
                    errmsg("could not connect for replication: %s", failure != NULL ? failure : "unknown error")));
            if (ankus_replication_next == 0)
            {
                walrcv_disconnect(connection);
                ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("replication connection identities are exhausted")));
            }

            entry = MemoryContextAllocZero(TopMemoryContext, sizeof(AnkusReplicationConnection));
            entry->id = ankus_replication_next++;
            entry->connection = connection;
            entry->scratch = AllocSetContextCreate(TopMemoryContext, "Ankus replication connection", ALLOCSET_SMALL_SIZES);
            entry->next = ankus_replication_connections;
            ankus_replication_connections = entry;
            result->context = (intptr_t) entry->id;
            result->value = walrcv_server_version(connection);
        }

        /* Runs one command, asking for text columns, and writes its outcome to the connection's scratch context:
         * the status, then a remote error's SQLSTATE and message, or each row's values. */
        static void
        ankus_replication_execute(AnkusReplicationConnection *entry, AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            char *query;
            int columns = (int) request->value;
            Oid *types = NULL;
            WalRcvExecResult *outcome;
            MemoryContext previous;
            StringInfoData buffer;
            if (columns < 0 || columns > MaxTupleAttributeNumber)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid replication result column count")));
            /* libpqwalreceiver looks up the result's column types in the catalog, which needs a transaction. */
            if (columns > 0 && !IsTransactionState())
                ereport(ERROR, (errcode(ERRCODE_INVALID_TRANSACTION_STATE),
                    errmsg("a replication query that returns rows requires a transaction")));
            /* Outside a transaction the current context lasts as long as the worker, so everything a command
             * allocates goes to the scratch context. */
            MemoryContextReset(entry->scratch);
            previous = MemoryContextSwitchTo(entry->scratch);
            query = ankus_replication_server_text(request->pointer);
            if (columns > 0)
            {
                types = palloc(sizeof(Oid) * columns);
                for (int index = 0; index < columns; index++)
                    types[index] = TEXTOID;
            }

            outcome = walrcv_exec(entry->connection, query, columns, types);
            initStringInfo(&buffer);
            pq_sendint32(&buffer, (int32) outcome->status);
            if (outcome->status == WALRCV_ERROR)
            {
        #if PG_VERSION_NUM >= 140000
                ankus_replication_send_text(&buffer, outcome->sqlstate != 0 ? unpack_sql_state(outcome->sqlstate) : NULL);
        #else
                ankus_replication_send_text(&buffer, NULL);
        #endif
                ankus_replication_send_text(&buffer, outcome->err);
            }
            else if (outcome->status == WALRCV_OK_TUPLES)
            {
                TupleTableSlot *slot = MakeSingleTupleTableSlot(outcome->tupledesc, &TTSOpsMinimalTuple);
                int rows = 0;
                int count_offset;
                pq_sendint32(&buffer, outcome->tupledesc->natts);
                count_offset = buffer.len;
                pq_sendint32(&buffer, 0);
                while (tuplestore_gettupleslot(outcome->tuplestore, true, false, slot))
                {
                    for (int index = 0; index < outcome->tupledesc->natts; index++)
                    {
                        bool is_null;
                        Datum value = slot_getattr(slot, index + 1, &is_null);
                        ankus_replication_send_text(&buffer, is_null ? NULL : TextDatumGetCString(value));
                    }

                    ExecClearTuple(slot);
                    rows++;
                }

                ExecDropSingleTupleTableSlot(slot);
                rows = pg_hton32(rows);
                memcpy(buffer.data + count_offset, &rows, sizeof(rows));
            }

            walrcv_clear_result(outcome);
            MemoryContextSwitchTo(previous);
            result->data = (intptr_t) buffer.data;
            result->length = (uintptr_t) buffer.len;
        }

        /* Returns one message, borrowed until the connection's next call, or waits for the socket or the latch.
         * result->value is the message length, 0 when none arrived in time or the latch was set, and -1 when the
         * server ended the copy; result->context is 1 when the latch woke the wait. */
        static void
        ankus_replication_receive(AnkusReplicationConnection *entry, AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            long timeout = (long) request->value;
            TimestampTz deadline = timeout > 0 ? TimestampTzPlusMilliseconds(GetCurrentTimestamp(), timeout) : 0;
            if (request->value < -1 || request->value > INT_MAX)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid replication receive timeout")));
            for (;;)
            {
                pgsocket socket = PGINVALID_SOCKET;
                char *data = NULL;
                int length;
                int events;
                long remaining = -1;
                CHECK_FOR_INTERRUPTS();
                length = walrcv_receive(entry->connection, &data, &socket);
                if (length != 0)
                {
                    result->value = length;
                    result->data = length > 0 ? (intptr_t) data : 0;
                    result->length = length > 0 ? (uintptr_t) length : 0;
                    return;
                }

                if (timeout == 0)
                    return;
                if (timeout > 0)
                {
                    remaining = TimestampDifferenceMilliseconds(GetCurrentTimestamp(), deadline);
                    if (remaining <= 0)
                        return;
                }

                events = WaitLatchOrSocket(MyLatch, WL_SOCKET_READABLE | WL_LATCH_SET | WL_EXIT_ON_PM_DEATH |
                    (remaining >= 0 ? WL_TIMEOUT : 0), socket, remaining, PG_WAIT_EXTENSION);
                if ((events & WL_LATCH_SET) != 0)
                {
                    ResetLatch(MyLatch);
                    CHECK_FOR_INTERRUPTS();
                    result->context = 1;
                    return;
                }

                if ((events & WL_SOCKET_READABLE) == 0)
                    return;
            }
        }

        /* The copy being read; COPY's data source callback takes no argument, and a backend runs one copy at a time. */
        static AnkusReplicationConnection *ankus_replication_copy_source = NULL;
        static char *ankus_replication_copy_pending = NULL;
        static int ankus_replication_copy_pending_length = 0;
        static bool ankus_replication_copy_ended = false;

        /* Mirrors tablesync.c's copy_read_data: hands COPY the stream's bytes, waiting on the socket and latch, and
         * returns fewer than minread bytes only at the end of the stream. */
        static int
        ankus_replication_copy_read(void *output, int minread, int maxread)
        {
            int read = 0;
            while (maxread > 0 && read < minread)
            {
                pgsocket socket = PGINVALID_SOCKET;
                char *data = NULL;
                int length;
                if (ankus_replication_copy_pending_length > 0)
                {
                    int take = Min(ankus_replication_copy_pending_length, maxread);
                    memcpy((char *) output + read, ankus_replication_copy_pending, take);
                    ankus_replication_copy_pending += take;
                    ankus_replication_copy_pending_length -= take;
                    read += take;
                    maxread -= take;
                    continue;
                }

                if (ankus_replication_copy_ended)
                    break;
                CHECK_FOR_INTERRUPTS();
                length = walrcv_receive(ankus_replication_copy_source->connection, &data, &socket);
                if (length > 0)
                {
                    ankus_replication_copy_pending = data;
                    ankus_replication_copy_pending_length = length;
                }
                else if (length < 0)
                    ankus_replication_copy_ended = true;
                else
                {
                    (void) WaitLatchOrSocket(MyLatch, WL_SOCKET_READABLE | WL_LATCH_SET | WL_EXIT_ON_PM_DEATH, socket, -1,
                        PG_WAIT_EXTENSION);
                    ResetLatch(MyLatch);
                }
            }

            return read;
        }

        static void
        ankus_replication_copy_into(AnkusReplicationConnection *entry, AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            Relation relation;
            ParseState *state;
            List *columns = NIL;
            const char *name = (const char *) request->data;
            bool snapshot;
            char extra;
        #if PG_VERSION_NUM >= 140000
            CopyFromState copy;
        #else
            CopyState copy;
        #endif
            if (!IsTransactionState())
                ereport(ERROR, (errcode(ERRCODE_INVALID_TRANSACTION_STATE), errmsg("copying into a table requires a transaction")));
            if (request->value < 0 || request->value > MaxTupleAttributeNumber || (request->value > 0 && name == NULL))
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid copy column list")));
            for (intptr_t index = 0; index < request->value; index++)
            {
                columns = lappend(columns, makeString(ankus_replication_server_text((intptr_t) name)));
                name += strlen(name) + 1;
            }

            relation = table_open((Oid) request->other, RowExclusiveLock);
            /* tablesync.c makes the same checks: COPY FROM applies no row-level security policies. */
            if (pg_class_aclcheck(RelationGetRelid(relation), GetUserId(), ACL_INSERT) != ACLCHECK_OK)
                aclcheck_error(ACLCHECK_NO_PRIV, OBJECT_TABLE, RelationGetRelationName(relation));
            if (check_enable_rls(RelationGetRelid(relation), InvalidOid, false) == RLS_ENABLED)
                ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                    errmsg("user \"%s\" cannot replicate into relation with row-level security enabled: \"%s\"",
                        GetUserNameFromId(GetUserId(), true), RelationGetRelationName(relation))));
            snapshot = !ActiveSnapshotSet();
            if (snapshot)
                PushActiveSnapshot(GetTransactionSnapshot());
            state = make_parsestate(NULL);
            (void) addRangeTableEntryForRelation(state, relation, AccessShareLock, NULL, false, false);
            ankus_replication_copy_source = entry;
            ankus_replication_copy_pending = NULL;
            ankus_replication_copy_pending_length = 0;
            ankus_replication_copy_ended = false;
        #if PG_VERSION_NUM >= 140000
            copy = BeginCopyFrom(state, relation, NULL, NULL, false, ankus_replication_copy_read, columns, NIL);
        #else
            copy = BeginCopyFrom(state, relation, NULL, false, ankus_replication_copy_read, columns, NIL);
        #endif
            result->value = (intptr_t) CopyFrom(copy);
            EndCopyFrom(copy);
            /* Text COPY can stop at an end-of-data line; data after it would leave the connection mid-copy. */
            if (ankus_replication_copy_read(&extra, 1, 1) != 0)
                ereport(ERROR, (errcode(ERRCODE_PROTOCOL_VIOLATION), errmsg("the copy sent data after its end-of-data marker")));
            ankus_replication_copy_source = NULL;
            if (snapshot)
                PopActiveSnapshot();
            free_parsestate(state);
            table_close(relation, NoLock);
        }

        static void
        ankus_replication_close(intptr_t id)
        {
            AnkusReplicationConnection **link = &ankus_replication_connections;
            AnkusReplicationConnection *entry = ankus_replication_find(id);
            while (*link != entry)
                link = &(*link)->next;
            *link = entry->next;
            MemoryContextDelete(entry->scratch);
            walrcv_disconnect(entry->connection);
            pfree(entry);
        }

        static void
        ankus_memory_replication(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            AnkusReplicationConnection *entry;
            if (request->flags == 1)
            {
                ankus_replication_connect(request, result);
                return;
            }

            if (request->flags == 6)
            {
                ankus_replication_close(request->context);
                return;
            }

            entry = ankus_replication_find(request->context);
            switch (request->flags)
            {
                case 2:
                    ankus_replication_execute(entry, request, result);
                    break;
                case 3:
                    ankus_replication_receive(entry, request, result);
                    break;
                case 4:
                    if (request->data == 0 || request->length > (uintptr_t) INT_MAX)
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid replication message")));
                    walrcv_send(entry->connection, (const char *) request->data, (int) request->length);
                    break;
                case 5:
                {
                    TimeLineID timeline = 0;
                    walrcv_endstreaming(entry->connection, &timeline);
                    break;
                }
                case 7:
                    ankus_replication_copy_into(entry, request, result);
                    break;
                default:
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("unknown replication operation")));
            }
        }

        """;
}
