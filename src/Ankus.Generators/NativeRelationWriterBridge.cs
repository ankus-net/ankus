namespace Ankus.Generators;

/// <summary>
/// Applies row changes to a table the way PostgreSQL's logical replication apply worker does, through the executor's
/// <c>execReplication.c</c> helpers, so constraints, indexes and row triggers behave as for the built-in subscriber.
/// </summary>
internal static class NativeRelationWriterBridge
{
    /// <summary>
    /// Implements the guarded relation writer operations behind <c>PgRelationWriter</c>.
    /// </summary>
    /// <remarks>
    /// Operations: 1 opens, 2 inserts, 3 updates, 4 deletes, 5 finds and 6 closes. A writer keeps one executor state for
    /// its table, as <c>create_edata_for_relation</c> builds it in <c>worker.c</c>, in a context under the transaction,
    /// so it cannot outlive the transaction; a transaction that commits with a writer still open fails, because its
    /// AFTER triggers have not run. Rows arrive as self-describing tuples and are checked against the table.
    /// </remarks>
    internal const string Source = """

        /* Writers convert rows through the tuple bridge, which only extensions with backend functions include. */
        #ifdef ANKUS_TUPLE_BRIDGE
        #include "access/commit_ts.h"
        #include "access/table.h"
        #include "access/tableam.h"
        #include "access/xact.h"
        #include "commands/trigger.h"
        #include "executor/executor.h"
        #include "mb/pg_wchar.h"
        #include "miscadmin.h"
        #include "optimizer/optimizer.h"
        #include "parser/parse_relation.h"
        #include "replication/origin.h"
        #include "rewrite/rewriteHandler.h"
        #include "utils/acl.h"
        #include "utils/lsyscache.h"
        #include "utils/rel.h"
        #include "utils/relcache.h"
        #include "utils/rls.h"
        #include "utils/typcache.h"

        typedef struct AnkusRelationWriter
        {
            struct AnkusRelationWriter *next;
            uint64 id;
            MemoryContext context;
            MemoryContext scratch;
            MemoryContextCallback reset;
            Relation relation;
            EState *estate;
            ResultRelInfo *info;
            TupleTableSlot *row;
            TupleTableSlot *search;
            TupleTableSlot *local;
            Oid key;
        } AnkusRelationWriter;

        static AnkusRelationWriter *ankus_relation_writers = NULL;
        static uint64 ankus_relation_writer_next = 1;
        static bool ankus_relation_writer_callback = false;

        static void
        ankus_relation_writer_unlink(AnkusRelationWriter *writer)
        {
            for (AnkusRelationWriter **link = &ankus_relation_writers; *link != NULL; link = &(*link)->next)
            {
                if (*link == writer)
                {
                    *link = writer->next;
                    return;
                }
            }
        }

        /* The transaction's end deletes a writer's context; aborting also drops its executor and trigger state. */
        static void
        ankus_relation_writer_reset(void *argument)
        {
            ankus_relation_writer_unlink((AnkusRelationWriter *) argument);
        }

        static void
        ankus_relation_writer_xact(XactEvent event, void *argument)
        {
            (void) argument;
            if ((event == XACT_EVENT_PRE_COMMIT || event == XACT_EVENT_PARALLEL_PRE_COMMIT || event == XACT_EVENT_PRE_PREPARE) &&
                ankus_relation_writers != NULL)
                ereport(ERROR, (errcode(ERRCODE_INVALID_TRANSACTION_STATE),
                    errmsg("a relation writer is still open at commit"),
                    errhint("Dispose every PgRelationWriter before the transaction commits.")));
        }

        static AnkusRelationWriter *
        ankus_relation_writer_find(intptr_t id)
        {
            for (AnkusRelationWriter *writer = ankus_relation_writers; writer != NULL; writer = writer->next)
            {
                if (writer->id == (uint64) id)
                    return writer;
            }

            ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                errmsg("the relation writer is closed or its transaction has ended")));
            return NULL;
        }

        static void
        ankus_relation_writer_open(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            Oid relid = (Oid) request->value;
            Relation relation;
            AnkusRelationWriter *writer;
            MemoryContext context;
            MemoryContext previous;
            RangeTblEntry *entry;
            EState *estate;
        #if PG_VERSION_NUM >= 160000
            List *permissions = NIL;
        #endif
            if (!IsTransactionState())
                ereport(ERROR, (errcode(ERRCODE_INVALID_TRANSACTION_STATE), errmsg("a relation writer requires a transaction")));
            relation = table_open(relid, RowExclusiveLock);
            if (relation->rd_rel->relkind != RELKIND_RELATION)
            {
                table_close(relation, RowExclusiveLock);
                ereport(ERROR, (errcode(ERRCODE_WRONG_OBJECT_TYPE),
                    errmsg("\"%s\" is not an ordinary table", RelationGetRelationName(relation))));
            }

            if (!ankus_relation_writer_callback)
            {
                RegisterXactCallback(ankus_relation_writer_xact, NULL);
                ankus_relation_writer_callback = true;
            }

            context = AllocSetContextCreate(CurTransactionContext, "Ankus relation writer", ALLOCSET_DEFAULT_SIZES);
            previous = MemoryContextSwitchTo(context);
            writer = palloc0(sizeof(AnkusRelationWriter));
            writer->context = context;
            writer->scratch = AllocSetContextCreate(context, "Ankus relation writer change", ALLOCSET_DEFAULT_SIZES);
            writer->relation = relation;
            writer->estate = estate = CreateExecutorState();
            entry = makeNode(RangeTblEntry);
            entry->rtekind = RTE_RELATION;
            entry->relid = relid;
            entry->relkind = relation->rd_rel->relkind;
            entry->rellockmode = AccessShareLock;
        #if PG_VERSION_NUM >= 180000
            addRTEPermissionInfo(&permissions, entry);
            ExecInitRangeTable(estate, list_make1(entry), permissions, bms_make_singleton(1));
        #elif PG_VERSION_NUM >= 160000
            addRTEPermissionInfo(&permissions, entry);
            ExecInitRangeTable(estate, list_make1(entry), permissions);
        #else
            ExecInitRangeTable(estate, list_make1(entry));
        #endif
            writer->info = makeNode(ResultRelInfo);
            InitResultRelInfo(writer->info, relation, 1, NULL, 0);
        #if PG_VERSION_NUM >= 140000
            estate->es_opened_result_relations = lappend(estate->es_opened_result_relations, writer->info);
        #else
            estate->es_result_relations = writer->info;
            estate->es_num_result_relations = 1;
            estate->es_result_relation_info = writer->info;
        #endif
            estate->es_output_cid = GetCurrentCommandId(true);
            writer->row = ExecInitExtraTupleSlot(estate, RelationGetDescr(relation), &TTSOpsVirtual);
            writer->search = ExecInitExtraTupleSlot(estate, RelationGetDescr(relation), &TTSOpsVirtual);
            writer->local = table_slot_create(relation, &estate->es_tupleTable);
            /* Like a statement, the writer opens the table's indexes once and keeps them until it closes. */
            ExecOpenIndices(writer->info, false);
            /* Rows are found by the replica identity index, or else the primary key, as the built-in subscriber
             * finds them; a table with neither is searched by every column. */
            writer->key = RelationGetReplicaIndex(relation);
            if (!OidIsValid(writer->key))
        #if PG_VERSION_NUM >= 180000
                writer->key = RelationGetPrimaryKeyIndex(relation, false);
        #else
                writer->key = RelationGetPrimaryKeyIndex(relation);
        #endif
            writer->id = ankus_relation_writer_next++;
            writer->reset.func = ankus_relation_writer_reset;
            writer->reset.arg = writer;
            MemoryContextRegisterResetCallback(context, &writer->reset);
            writer->next = ankus_relation_writers;
            ankus_relation_writers = writer;
            MemoryContextSwitchTo(previous);
            result->context = (intptr_t) writer->id;
            result->value = (intptr_t) relation->rd_rel->reltype;
            result->data = (intptr_t) writer->key;

            /* The type cache numbers each row descriptor it loads, so the caller reuses its copy until the table changes. */
            result->length = (uintptr_t) lookup_type_cache(relation->rd_rel->reltype, TYPECACHE_TUPDESC)->tupDesc_identifier;
        }

        /*
         * Mirrors the apply worker's TargetPrivilegesCheck. The executor's replication helpers do not apply row-level
         * security policies, so a table whose policies apply to the user is refused for every operation.
         */
        static void
        ankus_relation_writer_check(AnkusRelationWriter *writer, AclMode mode)
        {
            Oid relation = RelationGetRelid(writer->relation);
            if (pg_class_aclcheck(relation, GetUserId(), mode) != ACLCHECK_OK)
                aclcheck_error(ACLCHECK_NO_PRIV, OBJECT_TABLE, RelationGetRelationName(writer->relation));
            if (check_enable_rls(relation, InvalidOid, false) == RLS_ENABLED)
                ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                    errmsg("user \"%s\" cannot replicate into relation with row-level security enabled: \"%s\"",
                        GetUserNameFromId(GetUserId(), true), RelationGetRelationName(writer->relation))));
        }

        /* Stores a self-describing row in a slot after checking that its columns are the table's. */
        static void
        ankus_relation_writer_fill(AnkusRelationWriter *writer, const AnkusValue *value, TupleTableSlot *slot)
        {
            TupleDesc descriptor = RelationGetDescr(writer->relation);
            AnkusTupleShape *shape = ankus_tuple_shape_find(descriptor, descriptor->tdtypeid, descriptor->tdtypmod);
            Oid type;
            int32 modifier;
            int count;
            AnkusTupleField *fields = ankus_tuple_fields(value, &type, &modifier, &count);
            if (type != descriptor->tdtypeid || count != descriptor->natts)
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("the row does not have the table's columns")));
            ExecClearTuple(slot);
            for (int index = 0; index < count; index++)
            {
                Form_pg_attribute attribute = TupleDescAttr(descriptor, index);
                AnkusTupleField *field = &fields[index];
                Oid base_type = shape != NULL ? shape->fields[index].base_type : getBaseType(attribute->atttypid);
                AnkusParameter parameter = {0};
                bool is_null;
                if (attribute->attisdropped != ((field->flags & 1) != 0) ||
                    (!attribute->attisdropped && (attribute->atttypid != field->type || attribute->atttypmod != field->modifier)))
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH),
                        errmsg("row column %d does not match the table", index + 1)));
                is_null = attribute->attisdropped || field->value.is_null != 0 || (field->flags & 8) != 0;
                slot->tts_isnull[index] = is_null;
                slot->tts_values[index] = (Datum) 0;
                if (is_null)
                    continue;
                parameter.type_oid = attribute->atttypid;
                parameter.value = field->value;
                if (base_type == JSONOID || base_type == JSONBOID)
                    parameter.value.data = (unsigned char *) pnstrdup((char *) parameter.value.data, parameter.value.length);
                slot->tts_values[index] = ankus_parameter_datum_resolved(&parameter, base_type);
                if (attribute->atttypmod >= 0)
                    slot->tts_values[index] = ankus_coerce_value(slot->tts_values[index], &slot->tts_isnull[index],
                        attribute->atttypid, attribute->atttypid, attribute->atttypmod, attribute->attcollation);
            }

            ExecStoreVirtualTuple(slot);
        }

        /* A row as logical replication carries it: per column a status ('t' text in the database encoding, 'u' UTF-8
         * text, 'b' binary, 'n' NULL or 'm' not sent) and a slice of the data. */
        typedef struct AnkusReplicatedRow
        {
            const uint8 *status;
            const int32 *start;
            const int32 *length;
            const char *data;
            int32 count;
        } AnkusReplicatedRow;

        /* Mirrors the apply worker's slot_store_data, slot_modify_data and slot_fill_defaults: values go through their
         * types' input or receive functions; a column not sent keeps the source row's value when there is one, or
         * takes its default when asked. */
        static void
        ankus_relation_writer_fill_replicated(AnkusRelationWriter *writer, const AnkusReplicatedRow *row,
            TupleTableSlot *slot, TupleTableSlot *source, bool defaults)
        {
            TupleDesc descriptor = RelationGetDescr(writer->relation);
            int count = descriptor->natts;
            if (row == NULL || row->count != count || (count > 0 && (row->status == NULL || row->start == NULL || row->length == NULL)))
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("the row does not have the table's columns")));
            ExecClearTuple(slot);
            if (source != NULL)
            {
                slot_getallattrs(source);
                memcpy(slot->tts_values, source->tts_values, count * sizeof(Datum));
                memcpy(slot->tts_isnull, source->tts_isnull, count * sizeof(bool));
            }
            else
            {
                memset(slot->tts_values, 0, count * sizeof(Datum));
                memset(slot->tts_isnull, true, count * sizeof(bool));
            }

            for (int index = 0; index < count; index++)
            {
                Form_pg_attribute attribute = TupleDescAttr(descriptor, index);
                uint8 status = row->status[index];
                int32 length = row->length[index];
                const char *bytes;
                Oid function;
                Oid parameter;
                if (status == 'm')
                {
                    if (source == NULL && defaults && !attribute->attisdropped && !attribute->attgenerated)
                    {
                        Expr *expression = (Expr *) build_column_default(writer->relation, index + 1);
                        if (expression != NULL)
                        {
                            ExprState *state = ExecInitExpr(expression_planner(expression), NULL);
                            slot->tts_values[index] = ExecEvalExpr(state, GetPerTupleExprContext(writer->estate), &slot->tts_isnull[index]);
                        }
                    }

                    continue;
                }

                if (status == 'n')
                {
                    slot->tts_values[index] = (Datum) 0;
                    slot->tts_isnull[index] = true;
                    continue;
                }

                if (attribute->attisdropped)
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("row column %d is dropped", index + 1)));
                if (length < 0 || row->start[index] < 0 || (length > 0 && row->data == NULL))
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid value for row column %d", index + 1)));
                bytes = row->data + row->start[index];
                if (status == 't' || status == 'u')
                {
                    char *text = pnstrdup(bytes, length);
                    if (status == 't')
                        (void) pg_verify_mbstr(GetDatabaseEncoding(), text, length, false);
                    else
                        text = pg_any_to_server(text, length, PG_UTF8);
                    getTypeInputInfo(attribute->atttypid, &function, &parameter);
                    slot->tts_values[index] = OidInputFunctionCall(function, text, parameter, attribute->atttypmod);
                }
                else if (status == 'b')
                {
                    StringInfoData buffer;
                    buffer.data = palloc(length + 1);
                    memcpy(buffer.data, bytes, length);
                    buffer.data[length] = '\0';
                    buffer.len = length;
                    buffer.maxlen = length + 1;
                    buffer.cursor = 0;
                    getTypeBinaryInputInfo(attribute->atttypid, &function, &parameter);
                    slot->tts_values[index] = OidReceiveFunctionCall(function, &buffer, parameter, attribute->atttypmod);
                    if (buffer.cursor != buffer.len)
                        ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION),
                            errmsg("incorrect binary data format in row column %d", index + 1)));
                }
                else
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid status for row column %d", index + 1)));
                slot->tts_isnull[index] = false;
            }

            ExecStoreVirtualTuple(slot);
        }

        /* Fills a slot from the request's typed row or, when request->other is set, its replicated row. */
        static void
        ankus_relation_writer_fill_any(AnkusRelationWriter *writer, AnkusMemoryRequest *request, intptr_t value,
            TupleTableSlot *slot, TupleTableSlot *source, bool defaults)
        {
            if (request->other != 0)
                ankus_relation_writer_fill_replicated(writer, (const AnkusReplicatedRow *) value, slot, source, defaults);
            else
                ankus_relation_writer_fill(writer, (const AnkusValue *) value, slot);
        }

        /* Finds and locks the row that matches the search slot's key, or every column when the table has no key. */
        static bool
        ankus_relation_writer_lookup(AnkusRelationWriter *writer)
        {
            ankus_relation_writer_check(writer, ACL_SELECT);
            if (OidIsValid(writer->key))
                return RelationFindReplTupleByIndex(writer->relation, writer->key, LockTupleExclusive, writer->search, writer->local);
            return RelationFindReplTupleSeq(writer->relation, LockTupleExclusive, writer->search, writer->local);
        }

        static void
        ankus_relation_writer_insert(AnkusRelationWriter *writer, AnkusMemoryRequest *request)
        {
            ankus_relation_writer_fill_any(writer, request, request->data, writer->row, NULL, true);
            ankus_relation_writer_check(writer, ACL_INSERT);
            /* As in the apply worker, each change is its own trigger query, so its AFTER triggers fire when it ends. */
            AfterTriggerBeginQuery();
        #if PG_VERSION_NUM >= 140000
            ExecSimpleRelationInsert(writer->info, writer->estate, writer->row);
        #else
            ExecSimpleRelationInsert(writer->estate, writer->row);
        #endif
            AfterTriggerEndQuery(writer->estate);
            CommandCounterIncrement();
        }

        static bool
        ankus_relation_writer_change(AnkusRelationWriter *writer, AnkusMemoryRequest *request, bool update)
        {
            EPQState plan;
            volatile bool found = false;
            ankus_relation_writer_fill_any(writer, request, request->data, writer->search, NULL, false);
            /* A typed row is complete; a replicated one keeps the found row's values for columns it does not send. */
            if (update && request->other == 0)
                ankus_relation_writer_fill(writer, (const AnkusValue *) request->pointer, writer->row);
            AfterTriggerBeginQuery();
        #if PG_VERSION_NUM >= 160000
            EvalPlanQualInit(&plan, writer->estate, NULL, NIL, -1, NIL);
        #else
            EvalPlanQualInit(&plan, writer->estate, NULL, NIL, -1);
        #endif
            PG_TRY();
            {
                found = ankus_relation_writer_lookup(writer);
                if (found)
                {
                    if (update && request->other != 0)
                        ankus_relation_writer_fill_replicated(writer, (const AnkusReplicatedRow *) request->pointer, writer->row,
                            writer->local, false);
                    EvalPlanQualSetSlot(&plan, writer->local);
                    ankus_relation_writer_check(writer, update ? ACL_UPDATE : ACL_DELETE);
        #if PG_VERSION_NUM >= 140000
                    if (update)
                        ExecSimpleRelationUpdate(writer->info, writer->estate, &plan, writer->local, writer->row);
                    else
                        ExecSimpleRelationDelete(writer->info, writer->estate, &plan, writer->local);
        #else
                    if (update)
                        ExecSimpleRelationUpdate(writer->estate, &plan, writer->local, writer->row);
                    else
                        ExecSimpleRelationDelete(writer->estate, &plan, writer->local);
        #endif
                }
            }
            PG_FINALLY();
            {
                EvalPlanQualEnd(&plan);
            }
            PG_END_TRY();
            /* An error leaves the trigger query open; aborting the transaction or subtransaction closes it. */
            AfterTriggerEndQuery(writer->estate);
            CommandCounterIncrement();
            return found;
        }

        /* Returns the locked row, with the origin and time of the transaction that wrote it when PostgreSQL tracks
         * commit timestamps. */
        static void
        ankus_relation_writer_find_row(AnkusRelationWriter *writer, AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            TupleDesc descriptor = RelationGetDescr(writer->relation);
            AnkusInputBuffer owned = {0};
            AnkusValue *row = (AnkusValue *) request->pointer;
            bool is_null;
            TransactionId xmin;
            TimestampTz timestamp = 0;
        #if PG_VERSION_NUM >= 190000
            ReplOriginId origin = 0;
        #else
            RepOriginId origin = 0;
        #endif
            ankus_relation_writer_fill_any(writer, request, request->data, writer->search, NULL, false);
            if (!ankus_relation_writer_lookup(writer))
                return;
            slot_getallattrs(writer->local);
            ankus_tuple_transport_shaped(descriptor, descriptor->tdtypeid,
                ankus_tuple_shape_find(descriptor, descriptor->tdtypeid, descriptor->tdtypmod),
                writer->local->tts_values, writer->local->tts_isnull, row, &owned);
            xmin = DatumGetTransactionId(slot_getsysattr(writer->local, MinTransactionIdAttributeNumber, &is_null));
            if (track_commit_timestamp && TransactionIdIsNormal(xmin))
                (void) TransactionIdGetCommitTsData(xmin, &timestamp, &origin);
            result->value = 1;
            result->context = (intptr_t) origin;
            result->data = (intptr_t) timestamp;
            result->length = (uintptr_t) xmin;
        }

        static void
        ankus_relation_writer_close(intptr_t id)
        {
            AnkusRelationWriter *writer = ankus_relation_writer_find(id);
            EState *estate = writer->estate;
            ankus_relation_writer_unlink(writer);
            ExecCloseIndices(writer->info);
        #if PG_VERSION_NUM >= 190000
            ExecCloseTrigTargetRelations(estate);
        #elif PG_VERSION_NUM >= 140000
            {
                ListCell *cell;
                foreach(cell, estate->es_trig_target_relations)
                    table_close(((ResultRelInfo *) lfirst(cell))->ri_RelationDesc, NoLock);
            }
        #else
            ExecCleanUpTriggerState(estate);
        #endif
            ExecResetTupleTable(estate->es_tupleTable, false);
            FreeExecutorState(estate);
            /* The lock lasts until the transaction ends, as for any table a statement writes. */
            table_close(writer->relation, NoLock);
            MemoryContextDelete(writer->context);
        }

        static void
        ankus_memory_relation_writer(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            AnkusRelationWriter *writer;
            MemoryContext previous;
            if (request->flags == 1)
            {
                ankus_relation_writer_open(request, result);
                return;
            }

            if (request->flags == 6)
            {
                ankus_relation_writer_close(request->context);
                return;
            }

            /* Like the apply worker's per-message context, each change's memory lasts until the next one, so a long
             * transaction does not grow with its rows; a found row is copied out before the next call. */
            writer = ankus_relation_writer_find(request->context);
            MemoryContextReset(writer->scratch);
            ResetPerTupleExprContext(writer->estate);
            previous = MemoryContextSwitchTo(writer->scratch);
            switch (request->flags)
            {
                case 2:
                    ankus_relation_writer_insert(writer, request);
                    break;
                case 3:
                case 4:
                    result->value = ankus_relation_writer_change(writer, request, request->flags == 3) ? 1 : 0;
                    break;
                case 5:
                    ankus_relation_writer_find_row(writer, request, result);
                    break;
                default:
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("unknown relation writer operation")));
            }

            MemoryContextSwitchTo(previous);
        }
        #endif

        """;
}
