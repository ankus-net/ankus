namespace Ankus.Generators;

/// <summary>
/// Provides the native planner hook, plan node and executor callbacks behind managed custom scan providers.
/// </summary>
/// <remarks>
/// The declarations precede the generated provider table, and the functions follow it. Planning enters managed code
/// through the shared native callback boundary's initialization; execution keeps its error record on the stack so a
/// row costs no allocation beyond its values.
/// </remarks>
internal static class NativeCustomScanBridge
{
    /// <summary>
    /// Declares the request shared with managed providers and the provider table entry.
    /// </summary>
    internal const string Declarations = """
        #include "access/sysattr.h"
        #include "catalog/pg_class.h"
        #include "commands/explain.h"
        #if PG_VERSION_NUM >= 180000
        #include "commands/explain_format.h"
        #include "commands/explain_state.h"
        #endif
        #include "executor/executor.h"
        #include "libpq/pqformat.h"
        #include "nodes/extensible.h"
        #include "nodes/makefuncs.h"
        #include "nodes/value.h"
        #include "optimizer/cost.h"
        #include "optimizer/optimizer.h"
        #include "optimizer/pathnode.h"
        #include "optimizer/paths.h"
        #include "optimizer/prep.h"
        #include "optimizer/restrictinfo.h"
        #include "utils/float.h"
        #include "utils/lsyscache.h"

        typedef struct AnkusCustomScanRequest
        {
            double rows;
            double cheapest_startup;
            double cheapest_total;
            intptr_t state;
            Oid relation;
            Oid row_type;
            int32 flags;
            int32 columns;
            AnkusValue relation_name;
            AnkusValue schema_name;
            AnkusValue data;
            AnkusValue read;
            AnkusValue output;
        } AnkusCustomScanRequest;

        typedef int (*AnkusCustomScanCallback)(int, AnkusCustomScanRequest *, AnkusValue *, AnkusError *, AnkusExecute,
            AnkusMemoryApi *);

        typedef struct AnkusCustomScanProvider
        {
            const char *name;
            AnkusCustomScanCallback callback;
        } AnkusCustomScanProvider;

        """;

    /// <summary>
    /// Implements registration, path creation, plan creation and the executor callbacks for every provider.
    /// </summary>
    internal const string Functions = """
        typedef struct AnkusCustomScanState
        {
            CustomScanState scan;
            int provider;
            AnkusCustomScanRequest request;
            MemoryContext query;
            MemoryContext rows;
            MemoryContextCallback reset;
            AnkusValue *row;
            Oid *base_types;
        } AnkusCustomScanState;

        static CustomPathMethods ankus_custom_scan_path_methods[ANKUS_CUSTOM_SCAN_COUNT];
        static CustomScanMethods ankus_custom_scan_plan_methods[ANKUS_CUSTOM_SCAN_COUNT];
        static CustomExecMethods ankus_custom_scan_exec_methods[ANKUS_CUSTOM_SCAN_COUNT];
        static set_rel_pathlist_hook_type ankus_custom_scan_previous_hook = NULL;

        /* Calls a provider with its error record on the caller's stack. PostgreSQL errors are raised only after the
         * managed frame has returned. */
        static int
        ankus_custom_scan_call(int provider, int operation, AnkusCustomScanRequest *request, AnkusValue *row,
            AnkusError *error, MemoryContext owner)
        {
            Oid previous = ankus_function_oid;
            AnkusMemoryApi memory = {0};
            AnkusMemoryProtection protection = {0};
            volatile int status = 1;
            volatile bool entered = false;
            if (ankus_initialization_state == 0 && !ankus_module_loading && !ankus_worker_restore_in_progress())
                ankus_ensure_initialized();
            ankus_memory_initialize(&memory);
            ankus_memory_protect(&protection, owner, operation == 5);
            ankus_function_oid = InvalidOid;
            PG_TRY();
            {
                int result;
                ankus_fork_host_enter();
                entered = true;
                ANKUS_MANAGED_INVOKE(result, error, ankus_custom_scan_providers[provider].callback(operation, request, row, error,
                    ankus_spi_execute, &memory));
                status = result;
            }
            PG_FINALLY();
            {
                ankus_memory_protection = protection.previous;
                ankus_function_oid = previous;
                if (entered)
                    ankus_fork_host_exit();
            }
            PG_END_TRY();
            return status;
        }

        /* Reads an owned output buffer as PostgreSQL's network-order message format. */
        static void
        ankus_custom_scan_output(AnkusCustomScanRequest *request, StringInfo buffer)
        {
            buffer->data = (char *) request->output.data;
            buffer->len = request->output.length;
            buffer->maxlen = request->output.length;
            buffer->cursor = 0;
        }

        static void
        ankus_custom_scan_release_output(AnkusCustomScanRequest *request)
        {
            if (request->output.release != NULL)
                request->output.release(request->output.data);
            memset(&request->output, 0, sizeof(AnkusValue));
        }

        static char *
        ankus_custom_scan_text(StringInfo buffer)
        {
            int32 length = pq_getmsgint(buffer, 4);
            char *text;
            char *converted;
            if (length == -1)
                return NULL;
            if (length < 0)
                ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid Ankus custom scan text")));
            text = pnstrdup(pq_getmsgbytes(buffer, length), length);
            converted = pg_any_to_server(text, length, PG_UTF8);
            if (converted != text)
                pfree(text);
            return converted;
        }

        /* A custom scan's rows have no physical location, so tables whose system columns, row locks or modification the
         * query needs keep their ordinary paths. */
        static bool
        ankus_custom_scan_needs_location(PlannerInfo *root, RelOptInfo *rel, Index rti)
        {
            for (int attribute = rel->min_attr; attribute < 0; attribute++)
            {
                if (!bms_is_empty(rel->attr_needed[attribute - rel->min_attr]))
                    return true;
            }

        #if PG_VERSION_NUM >= 140000
            if (bms_is_member(rti, root->all_result_relids))
                return true;
        #else
            if (root->parse->resultRelation == (int) rti)
                return true;
        #endif
            return get_plan_rowmark(root->rowMarks, rti) != NULL;
        }

        static void
        ankus_custom_scan_offer(int provider, PlannerInfo *root, RelOptInfo *rel, RangeTblEntry *rte)
        {
            AnkusCustomScanRequest request = {0};
            AnkusError error = {0};
            StringInfoData buffer;
            ListCell *cell;
            char *name = get_rel_name(rte->relid);
            char *schema = get_namespace_name(get_rel_namespace(rte->relid));
            char *name_utf8;
            char *schema_utf8;
            int count;
            int status;
            if (name == NULL || schema == NULL)
                return;
            name_utf8 = pg_server_to_any(name, strlen(name), PG_UTF8);
            schema_utf8 = pg_server_to_any(schema, strlen(schema), PG_UTF8);
            request.rows = rel->rows;
            request.cheapest_startup = get_float8_infinity();
            request.cheapest_total = get_float8_infinity();
            foreach(cell, rel->pathlist)
            {
                Path *path = (Path *) lfirst(cell);
                request.cheapest_startup = Min(request.cheapest_startup, path->startup_cost);
                request.cheapest_total = Min(request.cheapest_total, path->total_cost);
            }

            request.relation = rte->relid;
            request.row_type = get_rel_type_id(rte->relid);
            request.relation_name.data = (unsigned char *) name_utf8;
            request.relation_name.length = strlen(name_utf8);
            request.schema_name.data = (unsigned char *) schema_utf8;
            request.schema_name.length = strlen(schema_utf8);
            status = ankus_custom_scan_call(provider, 0, &request, NULL, &error, CurrentMemoryContext);
            if (status != 0)
                ankus_raise_error(&error);
            if (request.output.data == NULL)
                return;
            PG_TRY();
            {
                Relids required_outer = rel->lateral_relids;
                ParamPathInfo *parameters = get_baserel_parampathinfo(root, rel, required_outer);
                List *conditions = parameters == NULL ? rel->baserestrictinfo
                    : list_concat_copy(rel->baserestrictinfo, parameters->ppi_clauses);
                Selectivity selectivity = clauselist_selectivity(root, conditions, 0, JOIN_INNER, NULL);
                QualCost quals;
                cost_qual_eval(&quals, conditions, root);
                ankus_custom_scan_output(&request, &buffer);
                count = pq_getmsgint(&buffer, 4);
                for (int index = 0; index < count; index++)
                {
                    CustomPath *path = makeNode(CustomPath);
                    double startup = pq_getmsgfloat8(&buffer);
                    double total = pq_getmsgfloat8(&buffer);
                    bool has_rows = pq_getmsgint(&buffer, 4) != 0;
                    double produced = pq_getmsgfloat8(&buffer);
                    int32 length = pq_getmsgint(&buffer, 4);
                    bytea *data;
                    if (length < 0 || !(startup >= 0) || !(total >= startup) || isinf(total) || !(produced >= 0) || isinf(produced))
                        ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid Ankus custom scan path")));
                    data = (bytea *) palloc(VARHDRSZ + length);
                    SET_VARSIZE(data, VARHDRSZ + length);
                    memcpy(VARDATA(data), pq_getmsgbytes(&buffer, length), length);
                    if (!has_rows)
                        produced = rel->tuples;
                    path->path.pathtype = T_CustomScan;
                    path->path.parent = rel;
                    path->path.pathtarget = rel->reltarget;
                    path->path.param_info = parameters;
                    path->path.parallel_aware = false;
                    path->path.parallel_safe = false;
                    path->path.parallel_workers = 0;
                    path->path.rows = parameters != NULL ? parameters->ppi_rows : clamp_row_est(produced * selectivity);
                    /* Conditions and output columns cost what they do for PostgreSQL's own scans. */
                    path->path.startup_cost = startup + quals.startup + rel->reltarget->cost.startup;
                    path->path.total_cost = total + quals.startup + quals.per_tuple * produced + rel->reltarget->cost.startup +
                        rel->reltarget->cost.per_tuple * path->path.rows;
                    path->path.pathkeys = NIL;
                    path->flags = 0;
                    path->custom_paths = NIL;
                    path->custom_private = list_make2(makeInteger(provider),
                        makeConst(BYTEAOID, -1, InvalidOid, -1, PointerGetDatum(data), false, false));
                    path->methods = &ankus_custom_scan_path_methods[provider];
                    add_path(rel, &path->path);
                }

                pq_getmsgend(&buffer);
            }
            PG_FINALLY();
            {
                ankus_custom_scan_release_output(&request);
            }
            PG_END_TRY();
        }

        static void
        ankus_custom_scan_paths(PlannerInfo *root, RelOptInfo *rel, Index rti, RangeTblEntry *rte)
        {
            if (ankus_custom_scan_previous_hook != NULL)
                ankus_custom_scan_previous_hook(root, rel, rti, rte);
            /* The hook outlives a failed module load; only a started extension offers paths. */
            if (ankus_initialization_state != 2 || rte->rtekind != RTE_RELATION || rte->inh || rte->tablesample != NULL ||
                (rte->relkind != RELKIND_RELATION && rte->relkind != RELKIND_MATVIEW) ||
                (rel->reloptkind != RELOPT_BASEREL && rel->reloptkind != RELOPT_OTHER_MEMBER_REL) ||
                ankus_custom_scan_needs_location(root, rel, rti))
                return;
            for (int provider = 0; provider < ANKUS_CUSTOM_SCAN_COUNT; provider++)
                ankus_custom_scan_offer(provider, root, rel, rte);
        }

        static Plan *
        ankus_custom_scan_plan(PlannerInfo *root, RelOptInfo *rel, CustomPath *best_path, List *tlist, List *clauses,
            List *custom_plans)
        {
            CustomScan *scan = makeNode(CustomScan);
            int provider = intVal(linitial(best_path->custom_private));
            Bitmapset *attributes = NULL;
            bytea *read = palloc0(VARHDRSZ + Max(rel->max_attr, 0));
            bool whole_row;
            (void) root;
            scan->scan.plan.targetlist = tlist;
            /* PostgreSQL evaluates the conditions on each row the provider produces. */
            scan->scan.plan.qual = extract_actual_clauses(clauses, false);
            /* The columns PostgreSQL reads from the scan's rows: those the query needs above the scan, as
             * postgres_fdw collects them, and those its conditions use. A whole-row reference reads all. */
            pull_varattnos((Node *) rel->reltarget->exprs, rel->relid, &attributes);
            pull_varattnos((Node *) scan->scan.plan.qual, rel->relid, &attributes);
            whole_row = bms_is_member(0 - FirstLowInvalidHeapAttributeNumber, attributes);
            SET_VARSIZE(read, VARHDRSZ + Max(rel->max_attr, 0));
            for (AttrNumber attribute = 1; attribute <= rel->max_attr; attribute++)
                VARDATA(read)[attribute - 1] = whole_row || bms_is_member(attribute - FirstLowInvalidHeapAttributeNumber, attributes);
            scan->scan.scanrelid = rel->relid;
            scan->flags = best_path->flags;
            scan->custom_plans = custom_plans;
            scan->custom_exprs = NIL;
            scan->custom_private = lappend(list_copy(best_path->custom_private),
                makeConst(BYTEAOID, -1, InvalidOid, -1, PointerGetDatum(read), false, false));
            scan->custom_scan_tlist = NIL;
            scan->methods = &ankus_custom_scan_plan_methods[provider];
            return &scan->scan.plan;
        }

        static Node *
        ankus_custom_scan_create(CustomScan *plan)
        {
            AnkusCustomScanState *state = (AnkusCustomScanState *) newNode(sizeof(AnkusCustomScanState), T_CustomScanState);
            state->provider = intVal(linitial(plan->custom_private));
            state->scan.methods = &ankus_custom_scan_exec_methods[state->provider];
            return (Node *) state;
        }

        static void
        ankus_custom_scan_release_row(AnkusCustomScanState *state, int columns)
        {
            for (int index = 0; index < columns; index++)
            {
                if (state->row[index].release != NULL)
                    state->row[index].release(state->row[index].data);
                memset(&state->row[index], 0, sizeof(AnkusValue));
            }
        }

        /* The executor does not end its nodes after an error, so the query context's reset disposes the managed state.
         * SPI is unavailable while the backend dismantles the transaction. */
        static void
        ankus_custom_scan_abort(void *argument)
        {
            AnkusCustomScanState *state = argument;
            AnkusError error = {0};
            int status;
            if (state->row != NULL)
                ankus_custom_scan_release_row(state, state->request.columns);
            if (state->request.state == 0)
                return;
            status = ankus_custom_scan_call(state->provider, 5, &state->request, NULL, &error, state->query);
            if (status != 0 && ankus_transaction_cleanup_active())
            {
                ankus_report_completion_cleanup(&error);
                return;
            }

            if (status != 0 && ((error.flags & ANKUS_ERROR_UNRECOVERED) != 0 ||
                error.sqlstate == ERRCODE_QUERY_CANCELED || error.report_level != 0))
            {
                ankus_raise_error(&error);
            }

            ankus_release_error(&error);
            if (status != 0)
                ereport(WARNING, (errmsg("Ankus custom scan disposal failed during query abort")));
        }

        static void
        ankus_custom_scan_begin(CustomScanState *node, EState *estate, int eflags)
        {
            AnkusCustomScanState *state = (AnkusCustomScanState *) node;
            Relation relation = node->ss.ss_currentRelation;
            TupleDesc descriptor = RelationGetDescr(relation);
            Const *data = lsecond_node(Const, ((CustomScan *) node->ss.ps.plan)->custom_private);
            Const *read = lthird_node(Const, ((CustomScan *) node->ss.ps.plan)->custom_private);
            bytea *bytes = DatumGetByteaPP(data->constvalue);
            bytea *read_bytes = DatumGetByteaPP(read->constvalue);
            AnkusError error = {0};
            int status;
            state->query = estate->es_query_cxt;
            state->rows = AllocSetContextCreate(estate->es_query_cxt, "Ankus custom scan rows", ALLOCSET_DEFAULT_SIZES);
            state->row = MemoryContextAllocZero(estate->es_query_cxt, sizeof(AnkusValue) * Max(descriptor->natts, 1));
            state->base_types = MemoryContextAllocZero(estate->es_query_cxt, sizeof(Oid) * Max(descriptor->natts, 1));
            for (int index = 0; index < descriptor->natts; index++)
            {
                Form_pg_attribute attribute = TupleDescAttr(descriptor, index);
                state->base_types[index] = attribute->attisdropped ? InvalidOid : getBaseType(attribute->atttypid);
            }

            state->request.relation = RelationGetRelid(relation);
            state->request.row_type = relation->rd_rel->reltype;
            state->request.columns = descriptor->natts;
            state->request.flags = (eflags & EXEC_FLAG_EXPLAIN_ONLY) != 0 ? 1 : 0;
            state->request.data.data = (unsigned char *) VARDATA_ANY(bytes);
            state->request.data.length = VARSIZE_ANY_EXHDR(bytes);
            state->request.read.data = (unsigned char *) VARDATA_ANY(read_bytes);
            state->request.read.length = VARSIZE_ANY_EXHDR(read_bytes);
            state->reset.func = ankus_custom_scan_abort;
            state->reset.arg = state;
            MemoryContextRegisterResetCallback(estate->es_query_cxt, &state->reset);
            status = ankus_custom_scan_call(state->provider, 1, &state->request, NULL, &error, state->query);
            if (status != 0)
                ankus_raise_error(&error);
        }

        static TupleTableSlot *
        ankus_custom_scan_access(ScanState *node)
        {
            AnkusCustomScanState *state = (AnkusCustomScanState *) node;
            TupleTableSlot *slot = node->ss_ScanTupleSlot;
            TupleDesc descriptor = slot->tts_tupleDescriptor;
            MemoryContext caller = CurrentMemoryContext;
            AnkusError error = {0};
            int status;
            ExecClearTuple(slot);
            MemoryContextReset(state->rows);
            status = ankus_custom_scan_call(state->provider, 2, &state->request, state->row, &error, state->rows);
            if (status == 1)
                ankus_raise_error(&error);
            if (status != 0)
                return slot;
            PG_TRY();
            {
                MemoryContextSwitchTo(state->rows);
                for (int index = 0; index < descriptor->natts; index++)
                {
                    AnkusParameter cell = {0};
                    cell.type_oid = TupleDescAttr(descriptor, index)->atttypid;
                    cell.value = state->row[index];
                    slot->tts_isnull[index] = cell.value.is_null != 0 || !OidIsValid(state->base_types[index]);
                    slot->tts_values[index] = OidIsValid(state->base_types[index])
                        ? ankus_parameter_datum_resolved(&cell, state->base_types[index]) : (Datum) 0;
                }
            }
            PG_FINALLY();
            {
                MemoryContextSwitchTo(caller);
                ankus_custom_scan_release_row(state, descriptor->natts);
            }
            PG_END_TRY();
            return ExecStoreVirtualTuple(slot);
        }

        /* Rows come from the provider, not from a locked heap tuple, so a recheck accepts what the scan produced. */
        static bool
        ankus_custom_scan_recheck(ScanState *node, TupleTableSlot *slot)
        {
            (void) node;
            (void) slot;
            return true;
        }

        static TupleTableSlot *
        ankus_custom_scan_exec(CustomScanState *node)
        {
            return ExecScan(&node->ss, ankus_custom_scan_access, ankus_custom_scan_recheck);
        }

        static void
        ankus_custom_scan_rescan(CustomScanState *node)
        {
            AnkusCustomScanState *state = (AnkusCustomScanState *) node;
            AnkusError error = {0};
            int status;
            ExecScanReScan(&node->ss);
            status = ankus_custom_scan_call(state->provider, 3, &state->request, NULL, &error, state->query);
            if (status != 0)
                ankus_raise_error(&error);
        }

        static void
        ankus_custom_scan_end(CustomScanState *node)
        {
            AnkusCustomScanState *state = (AnkusCustomScanState *) node;
            AnkusError error = {0};
            int status;
            if (state->request.state == 0)
                return;
            status = ankus_custom_scan_call(state->provider, 4, &state->request, NULL, &error, state->query);
            if (status != 0)
                ankus_raise_error(&error);
        }

        static void
        ankus_custom_scan_explain(CustomScanState *node, List *ancestors, ExplainState *es)
        {
            AnkusCustomScanState *state = (AnkusCustomScanState *) node;
            AnkusError error = {0};
            StringInfoData buffer;
            int status;
            (void) ancestors;
            if (state->request.state == 0)
                return;
            status = ankus_custom_scan_call(state->provider, 6, &state->request, NULL, &error, state->query);
            if (status != 0)
                ankus_raise_error(&error);
            if (state->request.output.data == NULL)
                return;
            PG_TRY();
            {
                int count;
                ankus_custom_scan_output(&state->request, &buffer);
                count = pq_getmsgint(&buffer, 4);
                for (int index = 0; index < count; index++)
                {
                    int32 kind = pq_getmsgint(&buffer, 4);
                    char *label = ankus_custom_scan_text(&buffer);
                    char *unit = ankus_custom_scan_text(&buffer);
                    int64 integer = pq_getmsgint64(&buffer);
                    double real = pq_getmsgfloat8(&buffer);
                    int32 digits = pq_getmsgint(&buffer, 4);
                    char *text = ankus_custom_scan_text(&buffer);
                    if (label == NULL || kind < 0 || kind > 3 || (kind == 2) != (text != NULL) || digits < 0 || digits > 15)
                        ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid Ankus EXPLAIN property")));
                    if (kind == 0)
                        ExplainPropertyInteger(label, unit, integer, es);
                    else if (kind == 1)
                        ExplainPropertyFloat(label, unit, real, digits, es);
                    else if (kind == 2)
                        ExplainPropertyText(label, text, es);
                    else
                        ExplainPropertyBool(label, integer != 0, es);
                }

                pq_getmsgend(&buffer);
            }
            PG_FINALLY();
            {
                ankus_custom_scan_release_output(&state->request);
            }
            PG_END_TRY();
        }

        /* PostgreSQL keeps the registered method tables for the backend's lifetime, so they live in static storage. */
        static void
        ankus_custom_scan_register(void)
        {
            for (int provider = 0; provider < ANKUS_CUSTOM_SCAN_COUNT; provider++)
            {
                const char *name = ankus_custom_scan_providers[provider].name;
                ankus_custom_scan_path_methods[provider].CustomName = name;
                ankus_custom_scan_path_methods[provider].PlanCustomPath = ankus_custom_scan_plan;
                ankus_custom_scan_plan_methods[provider].CustomName = name;
                ankus_custom_scan_plan_methods[provider].CreateCustomScanState = ankus_custom_scan_create;
                ankus_custom_scan_exec_methods[provider].CustomName = name;
                ankus_custom_scan_exec_methods[provider].BeginCustomScan = ankus_custom_scan_begin;
                ankus_custom_scan_exec_methods[provider].ExecCustomScan = ankus_custom_scan_exec;
                ankus_custom_scan_exec_methods[provider].EndCustomScan = ankus_custom_scan_end;
                ankus_custom_scan_exec_methods[provider].ReScanCustomScan = ankus_custom_scan_rescan;
                ankus_custom_scan_exec_methods[provider].ExplainCustomScan = ankus_custom_scan_explain;
                /* A load that failed on a later name already registered this one; the retry reports the real conflict. */
                if (GetCustomScanMethods(name, true) != &ankus_custom_scan_plan_methods[provider])
                    RegisterCustomScanMethods(&ankus_custom_scan_plan_methods[provider]);
            }

            ankus_custom_scan_previous_hook = set_rel_pathlist_hook;
            set_rel_pathlist_hook = ankus_custom_scan_paths;
        }

        """;
}
