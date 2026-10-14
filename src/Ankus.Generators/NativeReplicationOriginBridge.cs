namespace Ankus.Generators;

/// <summary>
/// Exposes PostgreSQL's replication origins, which record how far a subscriber has applied each source's changes and
/// mark the commits it makes on that source's behalf.
/// </summary>
internal static class NativeReplicationOriginBridge
{
    /// <summary>
    /// Implements the guarded origin operations behind <c>PgReplicationOrigin</c>.
    /// </summary>
    /// <remarks>
    /// Operations: 1 creates, 2 looks up by name, 3 drops, 4 sets up the session, 5 resets it, 6 sets the current
    /// transaction's remote position and commit time, 7 reads the session's progress, 8 reads an origin's progress, 9
    /// advances an origin and 10 reads the session's origin. Names cross as UTF-8. Each operation makes the checks its
    /// <c>pg_replication_origin_*</c> SQL function makes, other than privileges, and sets and clears the session's
    /// origin as those functions do. PostgreSQL 19 keeps the session's transaction fields in
    /// <c>replorigin_xact_state</c> where earlier versions keep three globals.
    /// </remarks>
    internal const string Source = """

        #include "access/xact.h"
        #include "access/xlog.h"
        #include "catalog/catalog.h"
        #include "catalog/pg_replication_origin.h"
        #include "replication/origin.h"
        #include "replication/slot.h"
        #include "storage/lmgr.h"

        #if PG_VERSION_NUM >= 190000
        typedef ReplOriginId AnkusOriginId;
        #define ankus_session_origin (replorigin_xact_state.origin)
        #define ankus_session_origin_lsn (replorigin_xact_state.origin_lsn)
        #define ankus_session_origin_timestamp (replorigin_xact_state.origin_timestamp)
        #else
        typedef RepOriginId AnkusOriginId;
        #define ankus_session_origin replorigin_session_origin
        #define ankus_session_origin_lsn replorigin_session_origin_lsn
        #define ankus_session_origin_timestamp replorigin_session_origin_timestamp
        #endif

        /* Mirrors replorigin_check_prerequisites, which is static, and requires the transaction the catalog needs. */
        static void
        ankus_origin_check(bool check_origins, bool recovery_ok, bool transaction)
        {
        #if PG_VERSION_NUM >= 180000
            if (check_origins && max_active_replication_origins == 0)
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("cannot query or manipulate replication origin when \"max_active_replication_origins\" is 0")));
        #else
            if (check_origins && max_replication_slots == 0)
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("cannot query or manipulate replication origin when \"max_replication_slots\" is 0")));
        #endif
            if (!recovery_ok && RecoveryInProgress())
                ereport(ERROR, (errcode(ERRCODE_READ_ONLY_SQL_TRANSACTION),
                    errmsg("cannot manipulate replication origins during recovery")));
            if (transaction && !IsTransactionState())
                ereport(ERROR, (errcode(ERRCODE_INVALID_TRANSACTION_STATE),
                    errmsg("replication origin catalog operations require a transaction")));
        }

        static void
        ankus_origin_require_session(void)
        {
            if (ankus_session_origin == 0)
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE), errmsg("no replication origin is configured")));
        }

        static char *
        ankus_origin_name(intptr_t text)
        {
            const char *utf8 = (const char *) text;
            if (utf8 == NULL || utf8[0] == '\0')
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("a replication origin needs a name")));
            return pg_any_to_server(utf8, strlen(utf8), PG_UTF8);
        }

        static void
        ankus_memory_replication_origin(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            switch (request->flags)
            {
                /* Creates an origin. */
                case 1:
                {
                    char *name;
                    ankus_origin_check(false, false, true);
                    name = ankus_origin_name(request->pointer);
        #if PG_VERSION_NUM >= 160000
                    if (IsReservedName(name) || pg_strcasecmp(name, "any") == 0 || pg_strcasecmp(name, "none") == 0)
                        ereport(ERROR, (errcode(ERRCODE_RESERVED_NAME), errmsg("replication origin name \"%s\" is reserved", name),
                            errdetail("Origin names \"any\", \"none\", and names starting with \"pg_\" are reserved.")));
        #else
                    if (IsReservedName(name))
                        ereport(ERROR, (errcode(ERRCODE_RESERVED_NAME), errmsg("replication origin name \"%s\" is reserved", name),
                            errdetail("Origin names starting with \"pg_\" are reserved.")));
        #endif
                    result->value = (intptr_t) replorigin_create(name);
                    break;
                }
                /* Finds an origin by name. */
                case 2:
                    ankus_origin_check(false, true, true);
                    result->value = (intptr_t) replorigin_by_name(ankus_origin_name(request->pointer), true);
                    break;
                /* Drops an origin; a missing one is an error unless the caller allows it. */
                case 3:
                {
                    char *name;
                    ankus_origin_check(false, false, true);
                    name = ankus_origin_name(request->pointer);
        #if PG_VERSION_NUM >= 140000
                    replorigin_drop_by_name(name, request->value != 0, true);
        #else
                    {
                        AnkusOriginId origin = replorigin_by_name(name, request->value != 0);
                        if (origin != 0)
                            replorigin_drop(origin, true);
                    }
        #endif
                    break;
                }
                /* Makes an origin this session's, so its commits record their remote position. */
                case 4:
                    ankus_origin_check(true, false, false);
        #if PG_VERSION_NUM >= 160000
                    replorigin_session_setup((AnkusOriginId) request->value, 0);
        #else
                    replorigin_session_setup((AnkusOriginId) request->value);
        #endif
                    ankus_session_origin = (AnkusOriginId) request->value;
                    break;
                /* Ends this session's origin. */
                case 5:
                    ankus_origin_check(true, false, false);
                    replorigin_session_reset();
        #if PG_VERSION_NUM >= 190000
                    replorigin_xact_clear(true);
        #else
                    replorigin_session_origin = InvalidRepOriginId;
                    replorigin_session_origin_lsn = InvalidXLogRecPtr;
                    replorigin_session_origin_timestamp = 0;
        #endif
                    break;
                /* Records the remote commit this transaction applies. */
                case 6:
                    ankus_origin_check(true, false, false);
                    ankus_origin_require_session();
                    if (!IsTransactionState())
                        ereport(ERROR, (errcode(ERRCODE_INVALID_TRANSACTION_STATE),
                            errmsg("a remote commit position applies to the current transaction")));
                    ankus_session_origin_lsn = (XLogRecPtr) request->value;
                    ankus_session_origin_timestamp = (TimestampTz) request->data;
                    break;
                /* Reads this session's origin progress. */
                case 7:
                    ankus_origin_check(true, true, false);
                    ankus_origin_require_session();
                    result->value = (intptr_t) replorigin_session_get_progress(request->value != 0);
                    break;
                /* Reads an origin's progress. */
                case 8:
                    ankus_origin_check(true, true, false);
                    result->value = (intptr_t) replorigin_get_progress((AnkusOriginId) request->context, request->value != 0);
                    break;
                /* Moves an origin's progress, as pg_replication_origin_advance does. */
                case 9:
                {
                    AnkusOriginId origin = (AnkusOriginId) request->context;
                    char *name;
                    ankus_origin_check(true, false, true);
                    /* pg_replication_origin_advance takes the same lock so the origin cannot vanish. */
                    LockRelationOid(ReplicationOriginRelationId, RowExclusiveLock);
                    if (origin == 0 || !replorigin_by_oid(origin, true, &name))
                        ereport(ERROR, (errcode(ERRCODE_UNDEFINED_OBJECT),
                            errmsg("replication origin with ID %d does not exist", (int) origin)));
                    replorigin_advance(origin, (XLogRecPtr) request->value, (XLogRecPtr) request->data,
                        (request->length & 1) != 0, (request->length & 2) != 0);
                    UnlockRelationOid(ReplicationOriginRelationId, RowExclusiveLock);
                    break;
                }
                /* Names this session's origin. */
                case 10:
                    result->value = (intptr_t) ankus_session_origin;
                    break;
                default:
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("unknown replication origin operation")));
            }
        }

        """;
}
