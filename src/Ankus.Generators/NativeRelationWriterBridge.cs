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
    /// Operations: 1 opens, 2 inserts, 3 updates, 4 deletes, 5 finds, 6 closes, 7 finds a conflicting row, 8 buffers an
    /// insert and 9 writes the buffered inserts. A writer keeps one executor state for
    /// its table, as <c>create_edata_for_relation</c> builds it in <c>worker.c</c>, in a context under the transaction,
    /// so it cannot outlive the transaction; a transaction that commits with a writer still open fails, because its
    /// AFTER triggers have not run. Rows arrive as self-describing tuples and are checked against the table.
    /// </remarks>
    internal const string Source = """

        /* Writers convert rows through the tuple bridge, which only extensions with backend functions include. */
        #ifdef ANKUS_TUPLE_BRIDGE
        #include "access/commit_ts.h"
        #include "access/genam.h"
        #include "access/heapam.h"
        #include "access/stratnum.h"
        #include "access/table.h"
        #include "access/tableam.h"
        #include "access/xact.h"
        #include "catalog/catversion.h"
        #include "catalog/pg_am_d.h"
        #include "commands/trigger.h"
        #include "executor/executor.h"
        #include "executor/nodeModifyTable.h"
        #include "mb/pg_wchar.h"
        #include "miscadmin.h"
        #include "optimizer/optimizer.h"
        #include "parser/parse_relation.h"
        #include "replication/origin.h"
        #include "rewrite/rewriteHandler.h"
        #include "storage/lmgr.h"
        #include "utils/acl.h"
        #include "utils/hsearch.h"
        #include "utils/lsyscache.h"
        #include "utils/rel.h"
        #include "utils/relcache.h"
        #include "utils/rls.h"
        #include "utils/snapmgr.h"
        #include "utils/typcache.h"

        typedef struct AnkusRelationWriter
        {
            /* Zero once the writer is closed. */
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
            /* Inserts wait here until a flush writes them together, as COPY batches them. A batch size of 1 means
             * every insert is written at once; 0 means it has not been decided. */
            TupleTableSlot **buffered;
            int buffered_count;
            int batch_size;
            BulkInsertState bulk;
        } AnkusRelationWriter;

        #define ANKUS_RELATION_WRITER_BATCH 1000

        typedef struct AnkusRelationWriterEntry
        {
            uint64 id;
            AnkusRelationWriter *writer;
        } AnkusRelationWriterEntry;

        /* Open writers by id, so finding or closing one takes the same time however many are open. The table lasts
         * as long as the backend; a writer's entry goes when it closes or its context is deleted. */
        static HTAB *ankus_relation_writers = NULL;
        static uint64 ankus_relation_writer_next = 1;
        static bool ankus_relation_writer_callback = false;

        static void
        ankus_relation_writer_unlink(AnkusRelationWriter *writer)
        {
            if (writer->id != 0)
            {
                (void) hash_search(ankus_relation_writers, &writer->id, HASH_REMOVE, NULL);
                writer->id = 0;
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
                ankus_relation_writers != NULL && hash_get_num_entries(ankus_relation_writers) > 0)
                ereport(ERROR, (errcode(ERRCODE_INVALID_TRANSACTION_STATE),
                    errmsg("a relation writer is still open at commit"),
                    errhint("Dispose every PgRelationWriter before the transaction commits.")));
        }

        static AnkusRelationWriter *
        ankus_relation_writer_find(intptr_t id)
        {
            uint64 key = (uint64) id;
            AnkusRelationWriterEntry *entry = ankus_relation_writers == NULL ? NULL :
                (AnkusRelationWriterEntry *) hash_search(ankus_relation_writers, &key, HASH_FIND, NULL);
            if (entry != NULL)
                return entry->writer;

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
            AnkusRelationWriterEntry *registered;
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

            if (ankus_relation_writers == NULL)
            {
                HASHCTL control;
                MemSet(&control, 0, sizeof(control));
                control.keysize = sizeof(uint64);
                control.entrysize = sizeof(AnkusRelationWriterEntry);
                ankus_relation_writers = hash_create("Ankus relation writers", 64, &control, HASH_ELEM | HASH_BLOBS);
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
            registered = (AnkusRelationWriterEntry *) hash_search(ankus_relation_writers, &writer->id, HASH_ENTER, NULL);
            registered->writer = writer;
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
            /* PostgreSQL 19 RC1 takes whether the first match can be used without comparing the rest. The subscriber
             * now records that with its key index instead of deciding again during the search, which a concurrent
             * REINDEX could change (2f7bf51b7b0). The writer's key is always the replica identity or primary key,
             * whose first match is the row. Beta 4 has the earlier form and an older catalog version. */
            if (OidIsValid(writer->key))
        #if CATALOG_VERSION_NO >= 202610061
                return RelationFindReplTupleByIndex(writer->relation, writer->key, true, LockTupleExclusive, writer->search,
                    writer->local);
        #else
                return RelationFindReplTupleByIndex(writer->relation, writer->key, LockTupleExclusive, writer->search, writer->local);
        #endif
            return RelationFindReplTupleSeq(writer->relation, LockTupleExclusive, writer->search, writer->local);
        }

        /* Finds and locks a row with the search slot's values in one unique btree index, waiting for a transaction
         * that is writing the row, as a unique check would. This is pglogical's find_index_tuple, with PostgreSQL's own
         * handling of a row updated or deleted before it is locked. RelationFindReplTupleByIndex searches the same way,
         * but from PostgreSQL 16 it also requires every column to match unless the index is the replica identity or
         * primary key, so it cannot find a row that conflicts only on another unique key. A NULL search value matches
         * only in an index whose NULLs are not distinct; otherwise the index is skipped. */
        static bool
        ankus_relation_writer_find_by_index(AnkusRelationWriter *writer, Relation index)
        {
            ScanKeyData keys[INDEX_MAX_KEYS];
            int count = IndexRelationGetNumberOfKeyAttributes(index);
            SnapshotData snapshot;
            IndexScanDesc scan;
            bool found;
            for (int key = 0; key < count; key++)
            {
                AttrNumber column = index->rd_index->indkey.values[key];
                Oid type = index->rd_opcintype[key];
                Oid operator = get_opfamily_member(index->rd_opfamily[key], type, type, BTEqualStrategyNumber);
                if (!OidIsValid(operator))
                    elog(ERROR, "missing operator %d(%u,%u) in opfamily %u", BTEqualStrategyNumber, type, type,
                        index->rd_opfamily[key]);
                ScanKeyInit(&keys[key], key + 1, BTEqualStrategyNumber, get_opcode(operator), writer->search->tts_values[column - 1]);
                keys[key].sk_collation = index->rd_indcollation[key];
                if (writer->search->tts_isnull[column - 1])
                {
        #if PG_VERSION_NUM >= 150000
                    if (!index->rd_index->indnullsnotdistinct)
                        return false;
                    keys[key].sk_flags |= SK_ISNULL | SK_SEARCHNULL;
        #else
                    return false;
        #endif
                }
            }

            InitDirtySnapshot(snapshot);
        #if PG_VERSION_NUM >= 190000
            scan = index_beginscan(writer->relation, index, &snapshot, NULL, count, 0, SO_NONE);
        #elif PG_VERSION_NUM >= 180000
            scan = index_beginscan(writer->relation, index, &snapshot, NULL, count, 0);
        #else
            scan = index_beginscan(writer->relation, index, &snapshot, count, 0);
        #endif
        retry:
            found = false;
            index_rescan(scan, keys, count, NULL, 0);
            if (index_getnext_slot(scan, ForwardScanDirection, writer->local))
            {
                TransactionId writing;
                ExecMaterializeSlot(writer->local);
                writing = TransactionIdIsValid(snapshot.xmin) ? snapshot.xmin : snapshot.xmax;
                if (TransactionIdIsValid(writing))
                {
                    XactLockTableWait(writing, NULL, NULL, XLTW_None);
                    goto retry;
                }

                found = true;
            }

            if (found)
            {
                TM_FailureData failure;
                TM_Result status;
                PushActiveSnapshot(GetLatestSnapshot());
                status = table_tuple_lock(writer->relation, &writer->local->tts_tid, GetActiveSnapshot(), writer->local,
                    GetCurrentCommandId(false), LockTupleExclusive, LockWaitBlock, 0, &failure);
                PopActiveSnapshot();
                switch (status)
                {
                    case TM_Ok:
                        break;
                    case TM_Updated:
                        ereport(LOG, (errcode(ERRCODE_T_R_SERIALIZATION_FAILURE), errmsg("concurrent update, retrying")));
                        goto retry;
                    case TM_Deleted:
                        ereport(LOG, (errcode(ERRCODE_T_R_SERIALIZATION_FAILURE), errmsg("concurrent delete, retrying")));
                        goto retry;
                    case TM_Invisible:
                        elog(ERROR, "attempted to lock invisible tuple");
                        break;
                    default:
                        elog(ERROR, "unexpected table_tuple_lock status: %u", status);
                        break;
                }
            }

            index_endscan(scan);
            return found;
        }

        /* Finds and locks a row that would conflict with inserting the search slot: by the replica identity index
         * first, then by each other valid unique btree index without expressions or a predicate, as pglogical's
         * pglogical_tuple_find_conflict does. Returns the matching index, or InvalidOid. */
        static Oid
        ankus_relation_writer_find_conflict(AnkusRelationWriter *writer)
        {
            Oid identity = RelationGetReplicaIndex(writer->relation);
            ankus_relation_writer_check(writer, ACL_SELECT);
            for (int pass = 0; pass < 2; pass++)
            {
                for (int position = 0; position < writer->info->ri_NumIndices; position++)
                {
                    Relation index = writer->info->ri_IndexRelationDescs[position];
                    IndexInfo *info = writer->info->ri_IndexRelationInfo[position];
                    if ((RelationGetRelid(index) == identity) != (pass == 0) || !info->ii_Unique ||
                        info->ii_Expressions != NIL || info->ii_Predicate != NIL || !index->rd_index->indisvalid ||
                        index->rd_rel->relam != BTREE_AM_OID)
                        continue;
                    if (ankus_relation_writer_find_by_index(writer, index))
                        return RelationGetRelid(index);
                }
            }

            return InvalidOid;
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

        /* Batches inserts as COPY and pglogical do, except where each row must be in the table before the next is
         * prepared: when BEFORE or INSTEAD OF row insert triggers could read the table, or a volatile column default
         * other than nextval could. */
        static int
        ankus_relation_writer_batch_size(AnkusRelationWriter *writer)
        {
            TriggerDesc *triggers = writer->info->ri_TrigDesc;
            TupleDesc descriptor = RelationGetDescr(writer->relation);
            if (triggers != NULL && (triggers->trig_insert_before_row || triggers->trig_insert_instead_row))
                return 1;
            for (int attribute = 0; attribute < descriptor->natts; attribute++)
            {
                Form_pg_attribute column = TupleDescAttr(descriptor, attribute);
                Expr *expression;
                if (column->attisdropped || column->attgenerated)
                    continue;
                expression = (Expr *) build_column_default(writer->relation, attribute + 1);
                if (expression != NULL && contain_volatile_functions_not_nextval((Node *) expression_planner(expression)))
                    return 1;
            }

            return ANKUS_RELATION_WRITER_BATCH;
        }

        /* Writes the buffered rows with one multi-insert, then adds each row's index entries and queues its AFTER
         * ROW triggers, which fire when the batch ends, as COPY's CopyMultiInsertBufferFlush does. */
        static void
        ankus_relation_writer_flush(AnkusRelationWriter *writer)
        {
            int count = writer->buffered_count;
            if (count == 0)
                return;
            /* A failure aborts the transaction, so these rows are never written twice. */
            writer->buffered_count = 0;
            if (writer->bulk == NULL)
            {
                MemoryContext previous = MemoryContextSwitchTo(writer->context);
                writer->bulk = GetBulkInsertState();
                MemoryContextSwitchTo(previous);
            }

            AfterTriggerBeginQuery();
            table_multi_insert(writer->relation, writer->buffered, count, GetCurrentCommandId(true), 0, writer->bulk);
            for (int position = 0; position < count; position++)
            {
                TupleTableSlot *slot = writer->buffered[position];
                List *recheck = NIL;
                if (writer->info->ri_NumIndices > 0)
        #if PG_VERSION_NUM >= 190000
                    recheck = ExecInsertIndexTuples(writer->info, writer->estate, 0, slot, NIL, NULL);
        #elif PG_VERSION_NUM >= 160000
                    recheck = ExecInsertIndexTuples(writer->info, slot, writer->estate, false, false, NULL, NIL, false);
        #elif PG_VERSION_NUM >= 140000
                    recheck = ExecInsertIndexTuples(writer->info, slot, writer->estate, false, false, NULL, NIL);
        #else
                    recheck = ExecInsertIndexTuples(slot, writer->estate, false, NULL, NIL);
        #endif
                ExecARInsertTriggers(writer->estate, writer->info, slot, recheck, NULL);
                list_free(recheck);
                ExecClearTuple(slot);
            }

            AfterTriggerEndQuery(writer->estate);
            CommandCounterIncrement();
        }

        /* Checks a row as ExecSimpleRelationInsert does before storing it, then copies it into the batch. */
        static void
        ankus_relation_writer_buffer(AnkusRelationWriter *writer, AnkusMemoryRequest *request)
        {
            TupleConstr *constraints = RelationGetDescr(writer->relation)->constr;
            if (writer->batch_size == 0)
                writer->batch_size = ankus_relation_writer_batch_size(writer);
            if (writer->batch_size == 1)
            {
                ankus_relation_writer_insert(writer, request);
                return;
            }

            ankus_relation_writer_fill_any(writer, request, request->data, writer->row, NULL, true);
            ankus_relation_writer_check(writer, ACL_INSERT);
            CheckCmdReplicaIdentity(writer->relation, CMD_INSERT);
            if (constraints != NULL && constraints->has_generated_stored)
        #if PG_VERSION_NUM >= 140000
                ExecComputeStoredGenerated(writer->info, writer->estate, writer->row, CMD_INSERT);
        #else
                ExecComputeStoredGenerated(writer->estate, writer->row, CMD_INSERT);
        #endif
            if (constraints != NULL)
                ExecConstraints(writer->info, writer->row, writer->estate);
            if (writer->relation->rd_rel->relispartition)
                (void) ExecPartitionCheck(writer->info, writer->row, writer->estate, true);
            if (writer->buffered == NULL)
                writer->buffered = MemoryContextAllocZero(writer->context, sizeof(TupleTableSlot *) * ANKUS_RELATION_WRITER_BATCH);
            if (writer->buffered[writer->buffered_count] == NULL)
            {
                MemoryContext previous = MemoryContextSwitchTo(writer->context);
                writer->buffered[writer->buffered_count] = table_slot_create(writer->relation, &writer->estate->es_tupleTable);
                MemoryContextSwitchTo(previous);
            }

            ExecCopySlot(writer->buffered[writer->buffered_count++], writer->row);
            if (writer->buffered_count == writer->batch_size)
                ankus_relation_writer_flush(writer);
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
        ankus_relation_writer_find_row(AnkusRelationWriter *writer, AnkusMemoryRequest *request, AnkusMemoryResult *result,
            bool conflict)
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
            Oid index;
            ankus_relation_writer_fill_any(writer, request, request->data, writer->search, NULL, false);
            if (conflict)
            {
                index = ankus_relation_writer_find_conflict(writer);
                if (!OidIsValid(index))
                    return;
            }
            else
            {
                if (!ankus_relation_writer_lookup(writer))
                    return;
                index = writer->key;
            }

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
            result->pointer = (intptr_t) index;
        }

        static void
        ankus_relation_writer_close(AnkusRelationWriter *writer)
        {
            EState *estate = writer->estate;
            /* Unlinked first, so the reset callback that deleting its context runs has nothing left to do. */
            ankus_relation_writer_unlink(writer);
            if (writer->bulk != NULL)
                FreeBulkInsertState(writer->bulk);
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

            /* Like the apply worker's per-message context, each change's memory lasts until the next one, so a long
             * transaction does not grow with its rows; a found row is copied out before the next call. */
            writer = ankus_relation_writer_find(request->context);
            MemoryContextReset(writer->scratch);
            ResetPerTupleExprContext(writer->estate);
            previous = MemoryContextSwitchTo(writer->scratch);
            /* Buffered inserts reach the table before any other operation, so changes keep their order. */
            if (request->flags != 8)
                ankus_relation_writer_flush(writer);
            if (request->flags == 6)
            {
                MemoryContextSwitchTo(previous);
                ankus_relation_writer_close(writer);
                return;
            }

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
                case 7:
                    ankus_relation_writer_find_row(writer, request, result, request->flags == 7);
                    break;
                case 8:
                    ankus_relation_writer_buffer(writer, request);
                    break;
                case 9:
                    break;
                default:
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("unknown relation writer operation")));
            }

            MemoryContextSwitchTo(previous);
        }
        #endif

        """;
}
