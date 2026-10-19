#include "postgres.h"
#include "fmgr.h"
#include "funcapi.h"
#include "lib/stringinfo.h"
#include "nodes/pg_list.h"
#include "nodes/nodeFuncs.h"
#include "utils/array.h"
#include "catalog/pg_type_d.h"
#include "storage/itemptr.h"
#include "executor/executor.h"
#include "utils/builtins.h"
#include "utils/lsyscache.h"
#include "utils/memutils.h"
#include "utils/tuplestore.h"
#include "access/relation.h"
#include "pgstat.h"
#include "utils/rel.h"
#include "utils/resowner.h"
#include "access/xact.h"
#include "miscadmin.h"
#include "borrowed_array_fixture.h"
#include "borrowed_buffer_fixture.h"

PG_MODULE_MAGIC;

/* Independent observation through deconstruct_array, separate from the borrowed-view iterator path. */
PGDLLEXPORT Datum ankus_test_array_bits(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_array_bits);
PGDLLEXPORT Datum
ankus_test_array_bits(PG_FUNCTION_ARGS)
{
    ArrayType *array = (ArrayType *) (uintptr_t) PG_GETARG_INT64(0);
    int16 length;
    bool by_value;
    char alignment;
    Datum *cells;
    bool *nulls;
    int count;
    get_typlenbyvalalign(ARR_ELEMTYPE(array), &length, &by_value, &alignment);
    deconstruct_array(array, ARR_ELEMTYPE(array), length, by_value, alignment, &cells, &nulls, &count);
    Datum *observed = palloc(sizeof(Datum) * (count + 1));
    bool *absent = palloc0(sizeof(bool) * (count + 1));
    observed[0] = Int64GetDatum((int64) (uintptr_t) array);
    for (int index = 0; index < count; index++)
    {
        observed[index + 1] = Int64GetDatum((int64) (uintptr_t) cells[index]);
        absent[index + 1] = nulls[index];
    }

    int dimensions[1] = {count + 1};
    int bounds[1] = {1};
    PG_RETURN_ARRAYTYPE_P(construct_md_array(observed, absent, 1, dimensions, bounds, INT8OID,
        sizeof(int64), FLOAT8PASSBYVAL, TYPALIGN_DOUBLE));
}

#if PG_VERSION_NUM < 140000
/* PostgreSQL 13 has native allocator counters but no SQL memory-context catalog. */
PGDLLEXPORT Datum ankus_test_memory_contexts(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_memory_contexts);
PGDLLEXPORT Datum
ankus_test_memory_contexts(PG_FUNCTION_ARGS)
{
    ReturnSetInfo *result = (ReturnSetInfo *) fcinfo->resultinfo;
    TupleDesc descriptor;
    if (result == NULL || !IsA(result, ReturnSetInfo) ||
        (result->allowedModes & SFRM_Materialize) == 0 ||
        get_call_result_type(fcinfo, NULL, &descriptor) != TYPEFUNC_COMPOSITE)
    {
        ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
            errmsg("Memory observations require a materialized row result")));
    }

    MemoryContext previous = MemoryContextSwitchTo(result->econtext->ecxt_per_query_memory);
    result->returnMode = SFRM_Materialize;
    result->setDesc = descriptor;
    result->setResult = tuplestore_begin_heap(true, false, work_mem);
    MemoryContextSwitchTo(previous);

    MemoryContext current = TopMemoryContext;
    while (current != NULL)
    {
        CHECK_FOR_INTERRUPTS();
        MemoryContextCounters counters = {0};
        current->methods->stats(current, NULL, NULL, &counters);
        const char *name = current->name;
        const char *ident = current->ident;
        if (ident != NULL && name != NULL && strcmp(name, "dynahash") == 0)
        {
            name = ident;
            ident = NULL;
        }

        Datum values[7] = {0};
        bool nulls[7] = {false};
        nulls[0] = name == NULL;
        nulls[1] = ident == NULL;
        if (name != NULL)
        {
            values[0] = CStringGetTextDatum(name);
        }

        if (ident != NULL)
        {
            values[1] = CStringGetTextDatum(ident);
        }

        values[2] = Int64GetDatum(counters.totalspace);
        values[3] = Int64GetDatum(counters.nblocks);
        values[4] = Int64GetDatum(counters.freespace);
        values[5] = Int64GetDatum(counters.freechunks);
        values[6] = Int64GetDatum(counters.totalspace - counters.freespace);
        tuplestore_putvalues(result->setResult, descriptor, values, nulls);
        if (current->firstchild != NULL)
        {
            current = current->firstchild;
        }
        else
        {
            while (current != NULL && current->nextchild == NULL)
            {
                current = current->parent;
            }

            if (current != NULL)
            {
                current = current->nextchild;
            }
        }
    }

    tuplestore_donestoring(result->setResult);
    return (Datum) 0;
}
#endif

static bool relation_fail_commit = false;
static int relation_commit_skip = 0;

static void
relation_commit_fault(SubXactEvent event, SubTransactionId child, SubTransactionId parent, void *argument)
{
    (void) child;
    (void) parent;
    (void) argument;
    if (event == SUBXACT_EVENT_PRE_COMMIT_SUB && relation_fail_commit)
    {
        if (relation_commit_skip > 0)
        {
            relation_commit_skip--;
            return;
        }

        relation_fail_commit = false;
        ereport(ERROR, (errcode(ERRCODE_RAISE_EXCEPTION), errmsg("relation commit fault")));
    }
}

PGDLLEXPORT Datum ankus_test_relation_commit_fault(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_relation_commit_fault);
PGDLLEXPORT Datum
ankus_test_relation_commit_fault(PG_FUNCTION_ARGS)
{
    Oid oid = PG_GETARG_OID(1);
    Relation keeper = relation_open(oid, AccessShareLock);
    int references = keeper->rd_refcnt;
    FmgrInfo function;
    LOCAL_FCINFO(call, 1);
    fmgr_info(PG_GETARG_OID(0), &function);
    InitFunctionCallInfoData(*call, &function, 1, InvalidOid, NULL, NULL);
    call->args[0].isnull = false;
    call->args[0].value = ObjectIdGetDatum(oid);
    bool valid = false;
    RegisterSubXactCallback(relation_commit_fault, NULL);
    relation_fail_commit = true;
    relation_commit_skip = 0;
    PG_TRY();
    {
        Datum result = FunctionCallInvoke(call);
        valid = !call->isnull && DatumGetBool(result) && !relation_fail_commit && keeper->rd_refcnt == references;
    }
    PG_FINALLY();
    {
        relation_fail_commit = false;
        UnregisterSubXactCallback(relation_commit_fault, NULL);
        relation_close(keeper, AccessShareLock);
    }
    PG_END_TRY();
    PG_RETURN_BOOL(valid);
}

PGDLLEXPORT Datum ankus_test_relation_transfer_fault(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_relation_transfer_fault);
PGDLLEXPORT Datum
ankus_test_relation_transfer_fault(PG_FUNCTION_ARGS)
{
    Oid oid = PG_GETARG_OID(1);
    int mode = PG_GETARG_INT32(2);
    Relation keeper = relation_open(oid, AccessShareLock);
    Relation transferred = relation_open(oid, NoLock);
    int references = keeper->rd_refcnt;
    FmgrInfo function;
    LOCAL_FCINFO(call, 2);
    fmgr_info(PG_GETARG_OID(0), &function);
    InitFunctionCallInfoData(*call, &function, 2, InvalidOid, NULL, NULL);
    call->args[0].isnull = false;
    call->args[0].value = Int64GetDatum((intptr_t) transferred);
    call->args[1].isnull = false;
    call->args[1].value = Int32GetDatum(mode);
    bool valid = false;
    RegisterSubXactCallback(relation_commit_fault, NULL);
    relation_fail_commit = true;
    relation_commit_skip = mode == 0 ? 0 : 1;
    PG_TRY();
    {
        Datum result = FunctionCallInvoke(call);
        valid = !call->isnull && DatumGetBool(result) && !relation_fail_commit && keeper->rd_refcnt == references;
    }
    PG_FINALLY();
    {
        relation_fail_commit = false;
        relation_commit_skip = 0;
        UnregisterSubXactCallback(relation_commit_fault, NULL);
        /* Preserve diagnostics if a broken implementation consumed the caller's pin. */
        if (keeper->rd_refcnt == references)
            relation_close(transferred, NoLock);
        relation_close(keeper, AccessShareLock);
    }
    PG_END_TRY();
    PG_RETURN_BOOL(valid);
}

PGDLLEXPORT Datum ankus_test_relation_owner(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_relation_owner);
PGDLLEXPORT Datum
ankus_test_relation_owner(PG_FUNCTION_ARGS)
{
    ResourceOwner previous = CurrentResourceOwner;
    ResourceOwner owner = ResourceOwnerCreate(previous, "relation lifetime test");
    Oid oid = PG_GETARG_OID(1);
    bool commit = PG_GETARG_BOOL(2);
    Relation keeper = relation_open(oid, AccessShareLock);
    int references = keeper->rd_refcnt;
    bool valid = false;
    FmgrInfo function;
    LOCAL_FCINFO(call, 1);
    fmgr_info(PG_GETARG_OID(0), &function);
    InitFunctionCallInfoData(*call, &function, 1, InvalidOid, NULL, NULL);
    call->args[0].isnull = false;
    call->args[0].value = ObjectIdGetDatum(oid);
    PG_TRY();
    {
        CurrentResourceOwner = owner;
        Datum result = FunctionCallInvoke(call);
        valid = !call->isnull && DatumGetObjectId(result) == oid && keeper->rd_refcnt == references + 1;
    }
    PG_FINALLY();
    {
        CurrentResourceOwner = previous;
        ResourceOwnerRelease(owner, RESOURCE_RELEASE_BEFORE_LOCKS, commit, false);
        ResourceOwnerRelease(owner, RESOURCE_RELEASE_LOCKS, commit, false);
        ResourceOwnerRelease(owner, RESOURCE_RELEASE_AFTER_LOCKS, commit, false);
        ResourceOwnerDelete(owner);
        valid = valid && keeper->rd_refcnt == references;
        relation_close(keeper, AccessShareLock);
    }
    PG_END_TRY();
    PG_RETURN_BOOL(valid);
}

PGDLLEXPORT Datum ankus_test_relation_borrow(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_relation_borrow);
PGDLLEXPORT Datum
ankus_test_relation_borrow(PG_FUNCTION_ARGS)
{
    Oid oid = PG_GETARG_OID(1);
    int mode = PG_GETARG_INT32(2);
    Relation keeper = relation_open(oid, AccessShareLock);
    Relation transferred = relation_open(oid, NoLock);
    int before = keeper->rd_refcnt;
    FmgrInfo function;
    LOCAL_FCINFO(call, 2);
    fmgr_info(PG_GETARG_OID(0), &function);
    InitFunctionCallInfoData(*call, &function, 2, InvalidOid, NULL, NULL);
    call->args[0].isnull = false;
    call->args[0].value = Int64GetDatum((intptr_t) transferred);
    call->args[1].isnull = false;
    call->args[1].value = Int32GetDatum(mode);
    Datum result = FunctionCallInvoke(call);
    bool valid = !call->isnull && DatumGetObjectId(result) == oid &&
        keeper->rd_refcnt == before - (mode == 0 ? 0 : 1);
    if (mode == 0)
        relation_close(transferred, NoLock);
    relation_close(keeper, AccessShareLock);
    PG_RETURN_BOOL(valid);
}

PGDLLEXPORT Datum ankus_test_relation_stats(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_relation_stats);
PGDLLEXPORT Datum
ankus_test_relation_stats(PG_FUNCTION_ARGS)
{
    Relation relation = (Relation) (intptr_t) PG_GETARG_INT64(0);
    Datum values[5] = {Int64GetDatum(0), Int64GetDatum(0), Int64GetDatum(0), Int64GetDatum(0), Int64GetDatum(0)};
#if PG_VERSION_NUM >= 150000
    (void) pgstat_should_count_relation(relation);
#endif
    if (relation->pgstat_info != NULL)
    {
#if PG_VERSION_NUM >= 160000
        values[0] = Int64GetDatum(relation->pgstat_info->counts.numscans);
        values[1] = Int64GetDatum(relation->pgstat_info->counts.tuples_returned);
        values[2] = Int64GetDatum(relation->pgstat_info->counts.tuples_fetched);
        values[3] = Int64GetDatum(relation->pgstat_info->counts.blocks_fetched);
        values[4] = Int64GetDatum(relation->pgstat_info->counts.blocks_hit);
#else
        values[0] = Int64GetDatum(relation->pgstat_info->t_counts.t_numscans);
        values[1] = Int64GetDatum(relation->pgstat_info->t_counts.t_tuples_returned);
        values[2] = Int64GetDatum(relation->pgstat_info->t_counts.t_tuples_fetched);
        values[3] = Int64GetDatum(relation->pgstat_info->t_counts.t_blocks_fetched);
        values[4] = Int64GetDatum(relation->pgstat_info->t_counts.t_blocks_hit);
#endif
    }

    PG_RETURN_ARRAYTYPE_P(construct_array(values, 5, INT8OID, sizeof(int64), FLOAT8PASSBYVAL, TYPALIGN_DOUBLE));
}

PGDLLEXPORT Datum ankus_test_default_values(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_default_values);
PGDLLEXPORT Datum
ankus_test_default_values(PG_FUNCTION_ARGS)
{
    List *expressions = (List *) (intptr_t) PG_GETARG_INT64(0);
    int count = list_length(expressions);
    Datum *values = palloc0(sizeof(Datum) * Max(count, 1));
    bool *nulls = palloc0(sizeof(bool) * Max(count, 1));
    EState *estate = CreateExecutorState();
    MemoryContext caller = CurrentMemoryContext;
    PG_TRY();
    {
        for (int index = 0; index < count; index++)
        {
            Expr *expression = list_nth(expressions, index);
            ExprState *state = ExecPrepareExpr(expression, estate);
            Datum value = ExecEvalExprSwitchContext(state, GetPerTupleExprContext(estate), &nulls[index]);
            if (!nulls[index])
            {
                Oid output;
                bool varlena;
                getTypeOutputInfo(exprType((Node *) expression), &output, &varlena);
                values[index] = CStringGetTextDatum(OidOutputFunctionCall(output, value));
            }
        }
    }
    PG_FINALLY();
    {
        MemoryContextSwitchTo(caller);
        FreeExecutorState(estate);
    }
    PG_END_TRY();
    int lower = 1;
    PG_RETURN_ARRAYTYPE_P(construct_md_array(values, nulls, 1, &count, &lower, TEXTOID, -1, false, TYPALIGN_INT));
}

PGDLLEXPORT Datum ankus_test_item_pointer_describe(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_item_pointer_describe);
PGDLLEXPORT Datum
ankus_test_item_pointer_describe(PG_FUNCTION_ARGS)
{
    ItemPointer pointer = (ItemPointer) (intptr_t) PG_GETARG_INT64(0);
    MemoryContext owner = GetMemoryChunkContext(pointer);
    PG_RETURN_TEXT_P(cstring_to_text(psprintf("%d|%u|%u|%u|%s", (int) sizeof(ItemPointerData),
        pointer->ip_blkid.bi_hi, pointer->ip_blkid.bi_lo, pointer->ip_posid,
        owner->ident == NULL ? owner->name : owner->ident)));
}

PGDLLEXPORT Datum ankus_test_item_pointer_borrow(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_item_pointer_borrow);
PGDLLEXPORT Datum
ankus_test_item_pointer_borrow(PG_FUNCTION_ARGS)
{
    struct { uint16 before; ItemPointerData pointer; uint16 after; } storage;
    storage.before = 0x1357;
    storage.after = 0x2468;
    ItemPointerSet(&storage.pointer, 0x12345678U, 0xabcd);
    int mode = PG_GETARG_INT32(1);
    FmgrInfo function;
    LOCAL_FCINFO(call, 2);
    fmgr_info(PG_GETARG_OID(0), &function);
    InitFunctionCallInfoData(*call, &function, 2, InvalidOid, NULL, NULL);
    call->args[0].isnull = false;
    call->args[0].value = PointerGetDatum(mode == 2 ? NULL : &storage.pointer);
    call->args[1].isnull = false;
    call->args[1].value = Int32GetDatum(mode);
    Datum result = FunctionCallInvoke(call);
    if (call->isnull || storage.before != 0x1357 || storage.after != 0x2468)
        elog(ERROR, "item-pointer borrowing returned NULL or overwrote native storage boundaries");
    PG_RETURN_TEXT_P(cstring_to_text(psprintf("%s|%u,%u,%u|guards", TextDatumGetCString(result),
        storage.pointer.ip_blkid.bi_hi, storage.pointer.ip_blkid.bi_lo, storage.pointer.ip_posid)));
}

/* Independent native tag and union interpretation for list tests. */
static text *
list_fixture_describe(List *list)
{
    StringInfoData result;
    initStringInfo(&result);
    if (list == NIL)
    {
        appendStringInfoString(&result, "NIL");
    }
    else
    {
        const char *kind = list->type == T_List ? "ptr" : list->type == T_IntList ? "int" :
            list->type == T_OidList ? "oid" : "unknown";
#if PG_VERSION_NUM >= 160000
        if (list->type == T_XidList) { kind = "xid"; }
#endif
        appendStringInfo(&result, "%s:", kind);
        for (int index = 0; index < list_length(list); index++)
        {
            if (index != 0) { appendStringInfoChar(&result, ','); }
            if (list->type == T_IntList) { appendStringInfo(&result, "%d", list_nth_int(list, index)); }
            else if (list->type == T_OidList) { appendStringInfo(&result, "%u", list_nth_oid(list, index)); }
#if PG_VERSION_NUM >= 160000
            else if (list->type == T_XidList) { appendStringInfo(&result, "%u", list_nth_cell(list, index)->xid_value); }
#endif
            else if (list->type == T_List) { appendStringInfo(&result, UINT64_FORMAT, (uint64) (uintptr_t) list_nth(list, index)); }
            else { elog(ERROR, "unexpected native list tag"); }
        }
    }

    text *value = cstring_to_text(result.data);
    pfree(result.data);
    return value;
}

PGDLLEXPORT Datum ankus_test_list_describe(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_list_describe);
PGDLLEXPORT Datum
ankus_test_list_describe(PG_FUNCTION_ARGS)
{
    PG_RETURN_TEXT_P(list_fixture_describe((List *) (intptr_t) PG_GETARG_INT64(0)));
}

PGDLLEXPORT Datum ankus_test_list_borrow(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_list_borrow);
PGDLLEXPORT Datum
ankus_test_list_borrow(PG_FUNCTION_ARGS)
{
    int mode = PG_GETARG_INT32(1);
    List *list = mode == 1 ? NIL : list_make2_int(1, 2);
    FmgrInfo function;
    LOCAL_FCINFO(call, 2);
    fmgr_info(PG_GETARG_OID(0), &function);
    InitFunctionCallInfoData(*call, &function, 2, InvalidOid, NULL, NULL);
    call->args[0].isnull = false;
    call->args[0].value = PointerGetDatum(list);
    call->args[1].isnull = false;
    call->args[1].value = Int32GetDatum(mode);
    Datum result = FunctionCallInvoke(call);
    if (call->isnull) { elog(ERROR, "list borrow returned NULL"); }
    list = (List *) (intptr_t) DatumGetInt64(result);
    text *description = list_fixture_describe(list);
    list_free(list);
    PG_RETURN_TEXT_P(description);
}

/* Native access deliberately uses the selected header's layout, never a managed copy. */
PGDLLEXPORT Datum ankus_test_stringinfo_cursor(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_stringinfo_cursor);
PGDLLEXPORT Datum
ankus_test_stringinfo_cursor(PG_FUNCTION_ARGS)
{
    StringInfo buffer = (StringInfo) (intptr_t) PG_GETARG_INT64(0);
    int value = PG_GETARG_INT32(1);
    if (value >= 0)
        buffer->cursor = value;
    PG_RETURN_INT32(buffer->cursor);
}

PGDLLEXPORT Datum ankus_test_stringinfo_borrow(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_stringinfo_borrow);
PGDLLEXPORT Datum
ankus_test_stringinfo_borrow(PG_FUNCTION_ARGS)
{
    int mode = PG_GETARG_INT32(1);
    StringInfoData buffer;
    char readonly_data[] = {65, 0, (char) 255, 127};
    if (mode == 0 || mode == 4)
    {
        initStringInfo(&buffer);
        appendBinaryStringInfo(&buffer, "abc", 3);
        buffer.cursor = 7;
        if (mode == 4)
            buffer.data[buffer.len] = '?';
    }
    else
    {
        /* A deliberately unterminated view, or a zero-length NULL data address. */
        buffer.data = mode == 2 ? NULL : readonly_data;
        buffer.len = mode == 2 ? 0 : 3;
        buffer.maxlen = 0;
        buffer.cursor = 0;
        if (mode == 3)
        {
            readonly_data[1] = 'b';
            readonly_data[2] = 'c';
        }
    }

    FmgrInfo function;
    LOCAL_FCINFO(call, 2);
    fmgr_info(PG_GETARG_OID(0), &function);
    InitFunctionCallInfoData(*call, &function, 2, InvalidOid, NULL, NULL);
    call->args[0].isnull = false;
    call->args[0].value = PointerGetDatum(&buffer);
    call->args[1].isnull = false;
    call->args[1].value = Int32GetDatum(mode);
    Datum result = FunctionCallInvoke(call);
    if (call->isnull)
        elog(ERROR, "StringInfo borrowing unexpectedly returned NULL");
    if (mode == 0 || mode == 4)
    {
        if (buffer.cursor != 7 || (mode == 0 && (buffer.len != 4 || memcmp(buffer.data, "a\021c*\0", 5) != 0)) ||
            (mode == 4 && (buffer.len != 3 || memcmp(buffer.data, "abc\0", 4) != 0)))
            elog(ERROR, "StringInfo borrowed mutation changed native length, cursor, bytes or terminator");
        pfree(buffer.data);
    }
    else if (readonly_data[0] != 65 || readonly_data[1] != (mode == 3 ? 'b' : 0) ||
        (unsigned char) readonly_data[2] != (mode == 3 ? 'c' : 255) || readonly_data[3] != 127)
        elog(ERROR, "StringInfo readonly borrowing modified caller-owned storage");

    return result;
}

PGDLLEXPORT Datum ankus_test_function_address(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_function_address);
PGDLLEXPORT Datum
ankus_test_function_address(PG_FUNCTION_ARGS)
{
    FmgrInfo function;
    fmgr_info(PG_GETARG_OID(0), &function);
    PG_RETURN_INT64((int64) (intptr_t) function.fn_addr);
}

PGDLLEXPORT Datum ankus_test_nullable_sum(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_nullable_sum);
PGDLLEXPORT Datum
ankus_test_nullable_sum(PG_FUNCTION_ARGS)
{
    if (fcinfo->flinfo != NULL || fcinfo->context != NULL || fcinfo->resultinfo != NULL)
        ereport(ERROR, (errmsg("Direct native call unexpectedly supplied catalog execution fields")));
    int32 total = 0;
    for (int index = 0; index < PG_NARGS(); index++)
    {
        if (PG_ARGISNULL(index))
            PG_RETURN_NULL();
        total += PG_GETARG_INT32(index);
    }

    PG_RETURN_INT32(total);
}

PGDLLEXPORT Datum ankus_test_internal_invoke(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_internal_invoke);
PGDLLEXPORT Datum
ankus_test_internal_invoke(PG_FUNCTION_ARGS)
{
    FmgrInfo function;
    LOCAL_FCINFO(call, 1);
    int32 mode = PG_GETARG_INT32(1);
    int64 stored = 41;
    fmgr_info(PG_GETARG_OID(0), &function);
    InitFunctionCallInfoData(*call, &function, 1, InvalidOid, NULL, NULL);
    call->args[0].isnull = mode == 0;
    call->args[0].value = mode == 2 ? PointerGetDatum(&stored) : (Datum) 0;
    Datum result = FunctionCallInvoke(call);
    if (mode == 2 && *(int64 *) DatumGetPointer(call->args[0].value) != 42)
        ereport(ERROR, (errmsg("managed internal callback did not update the native pointee")));
    if (call->isnull)
        PG_RETURN_NULL();

    return result;
}

/* Read each opaque state through its managed consumer while its result owner is live. */
static void
internal_fixture_check_row(FmgrInfo *reader, Datum value, bool is_null, int row)
{
    LOCAL_FCINFO(call, 1);
    if (row > 3 || is_null != (row == 3))
        ereport(ERROR, (errmsg("internal set returned an unexpected row or NULL flag")));
    InitFunctionCallInfoData(*call, reader, 1, InvalidOid, NULL, NULL);
    call->args[0].value = value;
    call->args[0].isnull = is_null;
    Datum result = FunctionCallInvoke(call);
    if (call->isnull != is_null || (!is_null && DatumGetInt64(result) != (row == 1 ? 42 : 40)))
        ereport(ERROR, (errmsg("internal set state did not retain its expected value")));
}

PGDLLEXPORT Datum ankus_test_internal_set_invoke(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_internal_set_invoke);
PGDLLEXPORT Datum
ankus_test_internal_set_invoke(PG_FUNCTION_ARGS)
{
    MemoryContext previous = CurrentMemoryContext;
    EState *estate = CreateExecutorState();
    bool materialize = PG_GETARG_BOOL(2);
    bool early = PG_GETARG_BOOL(3);
    volatile int rows = 0;
    PG_TRY();
    {
        MemoryContextSwitchTo(estate->es_query_cxt);
        FmgrInfo function;
        FmgrInfo reader;
        ReturnSetInfo info = {0};
        LOCAL_FCINFO(call, 1);
        fmgr_info(PG_GETARG_OID(0), &function);
        fmgr_info(PG_GETARG_OID(1), &reader);
        bool table = get_func_rettype(function.fn_oid) == RECORDOID;
        info.type = T_ReturnSetInfo;
        info.econtext = GetPerTupleExprContext(estate);
        info.allowedModes = materialize ? SFRM_Materialize : SFRM_ValuePerCall;
        InitFunctionCallInfoData(*call, &function, 1, InvalidOid, NULL, (Node *) &info);
        call->args[0].isnull = true;
        if (materialize)
        {
            (void) FunctionCallInvoke(call);
            if (info.returnMode != SFRM_Materialize || info.setResult == NULL || info.setDesc == NULL)
                ereport(ERROR, (errmsg("internal set did not materialize its rows")));
            TupleTableSlot *slot = MakeSingleTupleTableSlot(info.setDesc, &TTSOpsMinimalTuple);
            while (tuplestore_gettupleslot(info.setResult, true, false, slot))
            {
                bool is_null;
                Datum value = slot_getattr(slot, 1, &is_null);
                internal_fixture_check_row(&reader, value, is_null, rows);
                if (table)
                {
                    value = slot_getattr(slot, 2, &is_null);
                    if (is_null || DatumGetInt32(value) != rows + 1)
                        ereport(ERROR, (errmsg("internal TABLE position was incorrect")));
                }

                rows++;
                ExecClearTuple(slot);
                if (early)
                    break;
            }

            ExecDropSingleTupleTableSlot(slot);
            tuplestore_end(info.setResult);
        }
        else
        {
            for (;;)
            {
                Datum value = FunctionCallInvoke(call);
                if (info.isDone == ExprEndResult)
                    break;
                bool is_null = call->isnull;
                if (table)
                {
                    HeapTupleHeader tuple = DatumGetHeapTupleHeader(value);
                    Datum position = GetAttributeByNum(tuple, 2, &is_null);
                    if (is_null || DatumGetInt32(position) != rows + 1)
                        ereport(ERROR, (errmsg("internal TABLE position was incorrect")));
                    value = GetAttributeByNum(tuple, 1, &is_null);
                }

                internal_fixture_check_row(&reader, value, is_null, rows);
                rows++;
                if (early)
                    break;
            }
        }
    }
    PG_FINALLY();
    {
        MemoryContextSwitchTo(previous);
        FreeExecutorState(estate);
    }
    PG_END_TRY();
    PG_RETURN_INT32(rows);
}

static MemoryContext fixture_parent = NULL;
static MemoryContextCallback fixture_cleanup;

/* The callback lives outside the transaction whose deletion clears this pointer. */
static void
allocator_fixture_cleanup(void *argument)
{
    (void) argument;
    fixture_parent = NULL;
}

PGDLLEXPORT Datum ankus_test_allocator_create(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_allocator_create);
PGDLLEXPORT Datum
ankus_test_allocator_create(PG_FUNCTION_ARGS)
{
    int kind = PG_GETARG_INT32(0);
    Oid callback = PG_GETARG_OID(1);
    char *name = text_to_cstring(PG_GETARG_TEXT_PP(2));
    MemoryContext caller = CurrentMemoryContext;
    MemoryContext child;
    FmgrInfo function;
    Datum result = (Datum) 0;
    LOCAL_FCINFO(call, 0);

    /* Resolve catalog entries and load the managed library before selecting Slab. */
    fmgr_info(callback, &function);
    InitFunctionCallInfoData(*call, &function, 0, InvalidOid, NULL, NULL);
    if (fixture_parent != NULL)
    {
        MemoryContextDelete(fixture_parent);
    }

    fixture_parent = AllocSetContextCreate(TopTransactionContext,
        "Ankus allocator fixture", ALLOCSET_SMALL_SIZES);
    fixture_cleanup.func = allocator_fixture_cleanup;
    fixture_cleanup.arg = NULL;
    MemoryContextRegisterResetCallback(fixture_parent, &fixture_cleanup);
    name = MemoryContextStrdup(fixture_parent, name);
    switch (kind)
    {
        case 0:
            child = SlabContextCreate(fixture_parent, name, 8192, 64);
            break;
        case 1:
#if PG_VERSION_NUM >= 150000
            child = GenerationContextCreate(fixture_parent, name, ALLOCSET_DEFAULT_SIZES);
#else
            child = GenerationContextCreate(fixture_parent, name, 8192);
#endif
            break;
        case 2:
#if PG_VERSION_NUM >= 170000
            child = BumpContextCreate(fixture_parent, name, ALLOCSET_DEFAULT_SIZES);
            break;
#else
            ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED), errmsg("Bump requires PostgreSQL 17 or later")));
#endif
        default:
            ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("unknown test allocator kind")));
    }

    PG_TRY();
    {
        MemoryContextSwitchTo(child);
        result = FunctionCallInvoke(call);
    }
    PG_FINALLY();
    {
        MemoryContextSwitchTo(caller);
    }
    PG_END_TRY();

    if (call->isnull)
    {
        ereport(ERROR, (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED), errmsg("allocator capture returned NULL")));
    }

    return result;
}

PGDLLEXPORT Datum ankus_test_allocator_delete(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_allocator_delete);
PGDLLEXPORT Datum
ankus_test_allocator_delete(PG_FUNCTION_ARGS)
{
    (void) fcinfo;
    if (fixture_parent != NULL)
    {
        MemoryContextDelete(fixture_parent);
    }

    PG_RETURN_VOID();
}

PGDLLEXPORT Datum ankus_test_allocator_flags(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_allocator_flags);
PGDLLEXPORT Datum
ankus_test_allocator_flags(PG_FUNCTION_ARGS)
{
    int flags = 0;
    (void) fcinfo;
#ifdef USE_ASSERT_CHECKING
    flags |= 1;
#endif
#ifdef MEMORY_CONTEXT_CHECKING
    flags |= 2;
#endif
    PG_RETURN_INT32(flags);
}

/* Calls a function that must fail and reports whether ErrorContext is current when its ERROR reaches this
 * handler. PostgreSQL's own errors arrive there with it current, and handlers that restore the context they
 * caught an error in before rethrowing, as logical decoding's does, rely on that. */
PGDLLEXPORT Datum ankus_test_error_arrival(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_error_arrival);
PGDLLEXPORT Datum
ankus_test_error_arrival(PG_FUNCTION_ARGS)
{
    Oid function = PG_GETARG_OID(0);
    MemoryContext caller = CurrentMemoryContext;
    ResourceOwner owner = CurrentResourceOwner;
    volatile bool raised = false;
    volatile bool error_context = false;
    BeginInternalSubTransaction(NULL);
    MemoryContextSwitchTo(caller);
    PG_TRY();
    {
        (void) OidFunctionCall0(function);
        ReleaseCurrentSubTransaction();
    }
    PG_CATCH();
    {
        raised = true;
        error_context = CurrentMemoryContext == ErrorContext;
        MemoryContextSwitchTo(caller);
        FlushErrorState();
        RollbackAndReleaseCurrentSubTransaction();
    }
    PG_END_TRY();
    MemoryContextSwitchTo(caller);
    CurrentResourceOwner = owner;
    if (!raised)
    {
        ereport(ERROR, (errcode(ERRCODE_INTERNAL_ERROR), errmsg("the observed function returned without an error")));
    }

    PG_RETURN_BOOL(error_context);
}
