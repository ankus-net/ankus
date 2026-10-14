
#undef calloc
#undef free
#undef MemoryContextAllocExtended
#undef initStringInfo
#undef enlargeStringInfo
#undef pfree
#undef list_make1_impl
#undef MemoryContextAlloc
#undef repalloc
#undef list_free

#include "nodes/makefuncs.h"

static void
fault_require(bool condition, const char *message)
{
    if (!condition)
    {
        elog(ERROR, "registry fault fixture: %s", message);
    }
}

static int
fault_published_count(void)
{
    int count = 0;
    for (AnkusMemoryAllocation *allocation = ankus_memory_allocations; allocation != NULL; allocation = allocation->next)
    {
        count++;
    }

    return count;
}

static int
fault_child_contexts(MemoryContext parent)
{
    int count = 0;
    for (MemoryContext child = parent->firstchild; child != NULL; child = child->nextchild) { count++; }
    return count;
}

PGDLLEXPORT Datum ankus_test_function_defaults_fault(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_function_defaults_fault);
PGDLLEXPORT Datum
ankus_test_function_defaults_fault(PG_FUNCTION_ARGS)
{
    int mode = PG_GETARG_INT32(0);
    fault_require(mode >= 0 && mode <= 5, "unknown defaults fault mode");
    fault_require(fault_live_records == 0 && ankus_lists == NULL && ankus_memory_contexts == NULL,
        "previous defaults probe retained registry records");
    MemoryContext caller = CurrentMemoryContext;
    MemoryContext root = AllocSetContextCreate(caller, "Ankus defaults fault", ALLOCSET_SMALL_SIZES);
    uint64 saved_next = ankus_list_next_id;
    PG_TRY();
    {
        AnkusMemoryApi api;
        ankus_memory_initialize(&api);
        uint64 owner = ankus_memory_context_id(root);
        Const *constant = makeConst(INT4OID, -1, InvalidOid, sizeof(int32), Int32GetDatum(42), false, true);
        char *valid = nodeToString(list_make1(constant));
        char *source = mode == 3 ? "{INVALID_NODE}" : mode == 5 ? nodeToString(constant) : valid;
        AnkusMemoryRequest request = {0};
        AnkusMemoryResult result = {0};
        AnkusError error = {0};
        request.operation = ANKUS_MEMORY_LIST;
        request.flags = ANKUS_LIST_PARSE_DEFAULTS;
        request.context = (intptr_t) owner;
        request.data = (intptr_t) source;
        request.length = mode == 0 ? 0 : strlen(source);
        request.value = mode == 4 ? 2 : 1;
        int before = fault_live_records;
        int children_before = fault_child_contexts(caller);
        fault_fail_calloc = mode == 1;
        if (mode == 2) { ankus_list_next_id = 0; }
        int status = ankus_memory_invoke(&api, &request, &result, &error);
        ankus_list_next_id = saved_next;
        int expected = mode == 0 ? ERRCODE_INVALID_PARAMETER_VALUE :
            mode == 1 || mode == 2 ? ERRCODE_OUT_OF_MEMORY :
            mode == 3 ? ERRCODE_INTERNAL_ERROR : ERRCODE_INVALID_BINARY_REPRESENTATION;
        fault_require(status == 1 && error.sqlstate == expected, "wrong defaults error");
        fault_require(!fault_fail_calloc && ankus_lists == NULL && fault_live_records == before &&
            result.pointer == 0 && CurrentMemoryContext == caller &&
            fault_child_contexts(caller) == children_before, "defaults error leaked state");
        ankus_release_error(&error);
        request.data = (intptr_t) valid;
        request.length = strlen(valid);
        request.value = 1;
        fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "defaults retry failed");
        AnkusList *entry = ankus_list_find((uint64) result.pointer);
        fault_require(entry != NULL && entry->owned && entry->kind == 1 && list_length(entry->list) == 1 &&
            GetMemoryChunkContext(entry->list) == root, "defaults retry lost ownership");
        Const *parsed = linitial(entry->list);
        fault_require(IsA(parsed, Const) && parsed->consttype == INT4OID &&
            DatumGetInt32(parsed->constvalue) == 42 && GetMemoryChunkContext(parsed) == root, "defaults retry lost node value");
        request.context = result.pointer;
        request.flags = ANKUS_LIST_DISPOSE;
        fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0 &&
            ankus_lists == NULL && DatumGetInt32(parsed->constvalue) == 42 &&
            fault_child_contexts(caller) == children_before, "container disposal freed pointees or retained input storage");
    }
    PG_FINALLY();
    {
        fault_fail_calloc = false;
        ankus_list_next_id = saved_next;
        MemoryContextSwitchTo(caller);
        MemoryContextDelete(root);
    }
    PG_END_TRY();
    fault_require(fault_live_records == 0 && ankus_lists == NULL && ankus_memory_contexts == NULL,
        "defaults deletion retained native registry records");
    PG_RETURN_TEXT_P(cstring_to_text("error|released|retry|42|empty"));
}

static AnkusMemoryResult
fault_invoke_success(AnkusMemoryApi *api, AnkusMemoryOperation operation, intptr_t context,
    intptr_t data, uintptr_t length)
{
    AnkusMemoryRequest request = {0};
    AnkusMemoryResult result = {0};
    AnkusError error = {0};
    request.operation = operation;
    request.context = context;
    request.data = data;
    request.length = length;
    int status = ankus_memory_invoke(api, &request, &result, &error);
    if (status != 0)
    {
        ankus_report(&error, ERROR);
    }

    ankus_release_error(&error);
    return result;
}

static int64
fault_read(AnkusMemoryApi *api, intptr_t allocation)
{
    int64 value = 0;
    fault_invoke_success(api, ANKUS_MEMORY_READ, allocation, (intptr_t) &value, sizeof(value));
    return value;
}

/* Bump-specific probes must reject unsupported server versions explicitly. */
static MemoryContext
fault_create_bump(MemoryContext parent)
{
#if PG_VERSION_NUM >= 170000
    return BumpContextCreate(parent, "Ankus fault bump", ALLOCSET_SMALL_SIZES);
#else
    (void) parent;
    ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
        errmsg("Bump allocator probes require PostgreSQL 17 or later")));
    return NULL;
#endif
}

PGDLLEXPORT Datum ankus_test_allocator_registry_fault(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_allocator_registry_fault);
PGDLLEXPORT Datum
ankus_test_allocator_registry_fault(PG_FUNCTION_ARGS)
{
    int mode = PG_GETARG_INT32(0);
    fault_require(mode >= 1 && mode <= 6, "unknown failure scenario");
    fault_require(fault_live_records == 0 && ankus_memory_allocations == NULL && ankus_memory_contexts == NULL,
        "a prior invocation retained registry storage");
    MemoryContext caller = CurrentMemoryContext;
    MemoryContext root = AllocSetContextCreate(caller, "Ankus registry fault root", ALLOCSET_SMALL_SIZES);
    volatile uint64 saved_next = ankus_memory_next_allocation;
    char *volatile report = NULL;
    PG_TRY();
    {
        /* Keep an independent payload live through every fault. Bump proves
         * headerless ownership on supporting servers; older servers use AllocSet. */
#if PG_VERSION_NUM >= 170000
        MemoryContext control_context = fault_create_bump(root);
        fault_require(IsA(control_context, BumpContext), "the control allocator is not Bump");
        const char *control_kind = "bump";
#else
        MemoryContext control_context = AllocSetContextCreate(root, "Ankus registry fault control", ALLOCSET_SMALL_SIZES);
        fault_require(IsA(control_context, AllocSetContext), "the control allocator is not AllocSet");
        const char *control_kind = "allocset";
#endif
        fault_owner = mode == 3 ? SlabContextCreate(root, "Ankus registry fault slab", 8192, 64) :
            mode >= 5 ? AllocSetContextCreate(root, "Ankus registry fault adoption", ALLOCSET_SMALL_SIZES) : control_context;
        AnkusMemoryApi api;
        ankus_memory_initialize(&api);
        uint64 control_id = ankus_memory_context_id(control_context);
        uint64 owner_id = ankus_memory_context_id(fault_owner);
        AnkusMemoryResult control = fault_invoke_success(&api, ANKUS_MEMORY_ALLOCATE, (intptr_t) control_id, 0, 64);
        int64 control_value = 1193046;
        fault_invoke_success(&api, ANKUS_MEMORY_WRITE, control.pointer, (intptr_t) &control_value, sizeof(control_value));
        void *raw = mode >= 5 ? MemoryContextAlloc(fault_owner, 64) : NULL;
        int64 retry_value = 7654321;
        if (raw != NULL)
        {
            memcpy(raw, &retry_value, sizeof(retry_value));
        }

        AnkusMemoryRequest request = {0};
        AnkusMemoryResult result = {0};
        AnkusError error = {0};
        request.operation = mode >= 5 ? ANKUS_MEMORY_ADOPT : ANKUS_MEMORY_ALLOCATE;
        request.context = (intptr_t) owner_id;
        request.pointer = (intptr_t) raw;
        request.length = mode == 3 ? 63 : 64;
        request.flags = mode == 4 ? 2 : 0;
        int before_live = fault_live_records;
        int before_published = fault_published_count();
        int before_allocated = fault_successful_reservations;
        int before_freed = fault_released_reservations;
        saved_next = ankus_memory_next_allocation;
        fault_storage_calls = 0;
        fault_monitor_storage = true;
        fault_fail_calloc = mode == 1 || mode == 5;
        fault_return_null = mode == 4;
        if (mode == 2 || mode == 6)
        {
            ankus_memory_next_allocation = 0;
        }

        int status = ankus_memory_invoke(&api, &request, &result, &error);
        fault_monitor_storage = false;
        if (mode == 2 || mode == 6)
        {
            ankus_memory_next_allocation = saved_next;
        }

        fault_require(!fault_fail_calloc && !fault_return_null, "the requested failure boundary was not reached");
        fault_require(result.context == 0 && result.pointer == 0 && result.length == 0,
            "a failed acquisition published a result handle");
        int allocated = fault_successful_reservations - before_allocated;
        int freed = fault_released_reservations - before_freed;
        int live_delta = fault_live_records - before_live;
        int published_delta = fault_published_count() - before_published;
        int64 observed_control = fault_read(&api, control.pointer);
        int64 observed_raw = 0;
        if (raw != NULL)
        {
            memcpy(&observed_raw, raw, sizeof(observed_raw));
            fault_require(GetMemoryChunkContext(raw) == fault_owner, "failed adoption changed raw ownership");
        }

        fault_require(fault_invoke_success(&api, ANKUS_MEMORY_OWNER, control.pointer, 0, 0).context == (intptr_t) control_id,
            "the live control allocation lost its registered owner");
        char sqlstate[6];
        strlcpy(sqlstate, status == 0 ? "00000" : unpack_sql_state(error.sqlstate), sizeof(sqlstate));
        char message[sizeof(error.message)];
        strlcpy(message, error.message, sizeof(message));
        ankus_release_error(&error);

        request.length = 64;
        fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0 && result.pointer != 0 && result.length == 64,
            "retry did not publish the expected allocation");
        ankus_release_error(&error);
        fault_require(fault_published_count() == before_published + 1, "retry did not publish exactly one record");
        fault_invoke_success(&api, ANKUS_MEMORY_WRITE, result.pointer, (intptr_t) &retry_value, sizeof(retry_value));
        int64 observed_retry = fault_read(&api, result.pointer);
        if (mode == 3 || mode >= 5)
        {
            fault_invoke_success(&api, ANKUS_MEMORY_FREE, result.pointer, 0, 0);
            fault_require(fault_published_count() == before_published, "free after retry retained its registry record");
        }

        report = psprintf("%d|%s|%s|%d|%d|%d|%d|%d|" INT64_FORMAT "|" INT64_FORMAT "|" INT64_FORMAT "|%s",
            status, sqlstate, message, allocated, freed, fault_storage_calls, live_delta, published_delta,
            observed_control, observed_raw, observed_retry, control_kind);
    }
    PG_FINALLY();
    {
        fault_fail_calloc = false;
        fault_return_null = false;
        fault_monitor_storage = false;
        fault_owner = NULL;
        if (ankus_memory_next_allocation == 0)
        {
            ankus_memory_next_allocation = saved_next;
        }

        MemoryContextSwitchTo(caller);
        MemoryContextDelete(root);
    }
    PG_END_TRY();
    fault_require(fault_live_records == 0 && ankus_memory_allocations == NULL && ankus_memory_contexts == NULL,
        "context deletion retained registry storage");
    /* These emitted helpers are used by other bridge sections, which this
     * bounded module deliberately does not compile. */
    (void) ankus_log_level;
    (void) ankus_log_enabled;
    PG_RETURN_TEXT_P(cstring_to_text(report));
}

/* Executes the emitted production bridge, observing native chunk releases and
 * independent registry reservations at bounded acquisition failure points. */
PGDLLEXPORT Datum ankus_test_stringinfo_fault(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_stringinfo_fault);
PGDLLEXPORT Datum
ankus_test_stringinfo_fault(PG_FUNCTION_ARGS)
{
    int mode = PG_GETARG_INT32(0);
    fault_require(mode >= 1 && mode <= 9, "unknown StringInfo failure scenario");
    fault_require(fault_live_records == 0 && ankus_stringinfos == NULL && ankus_memory_contexts == NULL,
        "a prior StringInfo invocation retained registry storage");
    MemoryContext caller = CurrentMemoryContext;
    MemoryContext root = AllocSetContextCreate(caller, "Ankus StringInfo fault root", ALLOCSET_SMALL_SIZES);
    volatile uint64 saved_next = ankus_stringinfo_next_id;
    char *volatile report = NULL;
    PG_TRY();
    {
        fault_owner = root;
        AnkusMemoryApi api;
        ankus_memory_initialize(&api);
        AnkusMemoryRequest request = {0};
        AnkusMemoryResult result = {0};
        AnkusError error = {0};
        uint64 owner = ankus_memory_context_id(root);
        request.operation = ANKUS_MEMORY_STRINGINFO;
        request.flags = ANKUS_STRINGINFO_CREATE;
        request.context = (intptr_t) owner;
        request.data = (intptr_t) "live";
        request.length = 4;
        request.value = 4096;
        /* Modes 8 and 9 create the buffer in a bump or slab context, which cannot free one chunk. */
        if (mode >= 8)
        {
            MemoryContext special = mode == 8 ? fault_create_bump(root) :
                SlabContextCreate(root, "Ankus StringInfo slab", 8192, 64);
            request.context = (intptr_t) ankus_memory_context_id(special);
        }

        int before_live = fault_live_records;
        int before_allocated = fault_successful_reservations;
        int before_freed = fault_released_reservations;
        fault_stringinfo_frees = 0;
        fault_stringinfo_monitor = true;
        /* Modes 1 through 4 fail one step of creation; nothing may be published or kept, and a retry succeeds. */
        if (mode <= 4 || mode >= 8)
        {
            fault_stringinfo_stage = mode <= 2 ? mode : 0;
            fault_fail_calloc = mode == 3;
            if (mode == 4)
            {
                ankus_stringinfo_next_id = 0;
            }

            int status = ankus_memory_invoke(&api, &request, &result, &error);
            ankus_stringinfo_next_id = saved_next;
            fault_require(status == 1 && error.sqlstate == (mode >= 8 ? ERRCODE_INTERNAL_ERROR : ERRCODE_OUT_OF_MEMORY),
                "expected guarded allocation error");
            fault_require(result.pointer == 0 && result.context == 0 && ankus_stringinfos == NULL,
                "partial acquisition published a StringInfo");
            fault_require(fault_live_records == before_live && fault_stringinfo_stage == 0 && !fault_fail_calloc,
                "failure did not roll back its reservation");
            int allocated = fault_successful_reservations - before_allocated;
            int freed = fault_released_reservations - before_freed;
            int chunks_freed = fault_stringinfo_frees;
            ankus_release_error(&error);
            fault_require(CurrentMemoryContext == caller, "failed creation did not restore its caller");
            request.context = (intptr_t) owner;
            fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "creation retry failed");
            AnkusStringInfo *entry = ankus_stringinfo_find((uint64) result.pointer);
            fault_require(entry != NULL && entry->buffer->len == 4 && memcmp(entry->buffer->data, "live\0", 5) == 0,
                "retry did not preserve initial bytes and terminator");
            request.flags = ANKUS_STRINGINFO_DISPOSE;
            request.context = result.pointer;
            fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "retry disposal failed");
            ankus_release_error(&error);
            report = psprintf("%s|%d|%d|%d|live|%d", mode >= 8 ? "XX000" : "53200",
                allocated, freed, chunks_freed, fault_live_records - before_live);
        }
        else
        {
            /* Modes 5 through 7 create 128 large buffers, releasing each by disposal, context reset or transfer. */
            for (int iteration = 0; iteration < 128; iteration++)
            {
                request.operation = ANKUS_MEMORY_STRINGINFO;
                request.flags = ANKUS_STRINGINFO_CREATE;
                request.context = (intptr_t) ankus_memory_context_id(root);
                request.value = 1048576;
                fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "bounded StringInfo creation failed");
                AnkusStringInfo *entry = ankus_stringinfo_find((uint64) result.pointer);
                fault_require(entry != NULL && entry->buffer->len == 4 && memcmp(entry->buffer->data, "live\0", 5) == 0,
                    "repeated acquisition corrupted bytes");
                if (mode == 6)
                {
                    MemoryContextReset(root);
                }
                else
                {
                    request.flags = mode == 5 ? ANKUS_STRINGINFO_DISPOSE : ANKUS_STRINGINFO_DETACH_DATA;
                    request.context = result.pointer;
                    fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "bounded StringInfo release failed");
                    if (mode == 7)
                    {
                        fault_require(memcmp((void *) result.pointer, "live\0", 5) == 0, "transfer freed payload");
                        pfree((void *) result.pointer);
                    }
                }

                ankus_release_error(&error);
                fault_require(ankus_stringinfos == NULL, "release retained owned StringInfo metadata");
                fault_require(MemoryContextMemAllocated(root, false) < 1048576, "release retained a large native data chunk");
            }

            report = psprintf("128|live|%d|%d", fault_stringinfo_frees, ankus_stringinfos == NULL);
        }
    }
    PG_FINALLY();
    {
        fault_stringinfo_stage = 0;
        fault_stringinfo_monitor = false;
        fault_fail_calloc = false;
        fault_owner = NULL;
        if (ankus_stringinfo_next_id == 0)
        {
            ankus_stringinfo_next_id = saved_next;
        }

        MemoryContextSwitchTo(caller);
        MemoryContextDelete(root);
    }
    PG_END_TRY();
    fault_require(fault_live_records == 0 && ankus_stringinfos == NULL && ankus_memory_contexts == NULL,
        "StringInfo context deletion retained registry storage");
    PG_RETURN_TEXT_P(cstring_to_text(report));
}

PGDLLEXPORT Datum ankus_test_item_pointer_fault(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_item_pointer_fault);
PGDLLEXPORT Datum
ankus_test_item_pointer_fault(PG_FUNCTION_ARGS)
{
    int mode = PG_GETARG_INT32(0);
    fault_require(mode >= 1 && mode <= 6, "unknown item-pointer scenario");
    fault_require(fault_live_records == 0 && ankus_memory_allocations == NULL && ankus_memory_contexts == NULL,
        "a previous item-pointer invocation retained registry storage");
    MemoryContext caller = CurrentMemoryContext;
    MemoryContext root = AllocSetContextCreate(caller, "Ankus item-pointer fault", ALLOCSET_SMALL_SIZES);
    volatile uint64 saved_next = ankus_memory_next_allocation;
    char *volatile report = NULL;
    PG_TRY();
    {
        fault_owner = root;
        AnkusMemoryApi api;
        ankus_memory_initialize(&api);
        uint64 owner = ankus_memory_context_id(root);
        AnkusMemoryRequest request = {0};
        AnkusMemoryResult result = {0};
        AnkusError error = {0};
        request.operation = ANKUS_MEMORY_ITEM_POINTER;
        request.flags = 1;
        request.context = (intptr_t) owner;
        request.value = PG_UINT32_MAX;
        request.length = PG_UINT16_MAX;
        fault_storage_calls = 0;
        fault_monitor_storage = true;
        int before_live = fault_live_records;
        int before_allocated = fault_successful_reservations;
        int before_freed = fault_released_reservations;
        /* Modes 1 through 3 fail registry storage, identity assignment or a storage error raised inside the bridge. */
        if (mode <= 3)
        {
            fault_fail_calloc = mode == 1;
            if (mode == 2) { ankus_memory_next_allocation = 0; }
            fault_raise_storage_error = mode == 3;
            int status = ankus_memory_invoke(&api, &request, &result, &error);
            ankus_memory_next_allocation = saved_next;
            fault_require(status == 1 && error.sqlstate == ERRCODE_OUT_OF_MEMORY,
                "item-pointer creation did not transport native allocation failure");
            fault_require(!fault_fail_calloc && !fault_raise_storage_error && result.pointer == 0 &&
                fault_live_records == before_live && fault_published_count() == 0,
                "failed item-pointer creation retained or published ownership");
            int allocated = fault_successful_reservations - before_allocated;
            int freed = fault_released_reservations - before_freed;
            int calls = fault_storage_calls;
            ankus_release_error(&error);

            /* A retry after the failure succeeds with the exact native size and fields. */
            fault_require(CurrentMemoryContext == caller, "item-pointer failure changed the caller context");
            fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0,
                "item-pointer allocation retry failed");
            AnkusMemoryAllocation *allocation = ankus_memory_allocation_required((uint64) result.pointer);
            ItemPointer pointer = (ItemPointer) allocation->pointer;
            fault_require(allocation->size == sizeof(ItemPointerData) && pointer->ip_blkid.bi_hi == 65535 &&
                pointer->ip_blkid.bi_lo == 65535 && pointer->ip_posid == 65535,
                "item-pointer retry lost native size or fields");
            fault_invoke_success(&api, ANKUS_MEMORY_FREE, result.pointer, 0, 0);
            fault_require(fault_live_records == before_live && fault_published_count() == 0,
                "item-pointer retry free retained ownership");
            report = psprintf("53200|%d|%d|%d|retry|0", allocated, freed, calls);
        }
        else
        {
            /* Modes 4 through 6 create 4096 item pointers, releasing each by disposal, context reset or transfer. */
            fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0,
                "item-pointer warmup failed");
            fault_invoke_success(&api, ANKUS_MEMORY_FREE, result.pointer, 0, 0);
            Size baseline = MemoryContextMemAllocated(root, false);
            for (int index = 0; index < 4096; index++)
            {
                request.value = (intptr_t) (PG_UINT32_MAX - index);
                request.length = index;
                fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0,
                    "repeated item-pointer creation failed");
                AnkusMemoryAllocation *allocation = ankus_memory_allocation_required((uint64) result.pointer);
                ItemPointer pointer = (ItemPointer) allocation->pointer;
                fault_require(ItemPointerGetBlockNumberNoCheck(pointer) == PG_UINT32_MAX - index &&
                    ItemPointerGetOffsetNumberNoCheck(pointer) == index && GetMemoryChunkContext(pointer) == root,
                    "repeated native fields or owner changed");
                if (mode == 4)
                {
                    fault_invoke_success(&api, ANKUS_MEMORY_FREE, result.pointer, 0, 0);
                }
                else if (mode == 5)
                {
                    fault_invoke_success(&api, ANKUS_MEMORY_RESET, (intptr_t) owner, 0, 0);
                }
                else
                {
                    AnkusMemoryResult detached = fault_invoke_success(&api, ANKUS_MEMORY_DETACH, result.pointer, 0, 0);
                    fault_require(detached.pointer == (intptr_t) pointer, "item-pointer transfer changed the address");
                    pfree((void *) detached.pointer);
                }

                fault_require(fault_published_count() == 0 && fault_live_records == before_live,
                    "item-pointer release retained a registry record");
            }

            fault_require(MemoryContextMemAllocated(root, false) <= baseline,
                "repeated item-pointer releases retained native storage");
            report = psprintf("4096|exact|0|bounded");
        }
    }
    PG_FINALLY();
    {
        fault_fail_calloc = false;
        fault_raise_storage_error = false;
        fault_monitor_storage = false;
        fault_owner = NULL;
        if (ankus_memory_next_allocation == 0) { ankus_memory_next_allocation = saved_next; }
        MemoryContextSwitchTo(caller);
        MemoryContextDelete(root);
    }
    PG_END_TRY();
    fault_require(fault_live_records == 0 && ankus_memory_allocations == NULL && ankus_memory_contexts == NULL,
        "item-pointer context deletion retained registry records");
    PG_RETURN_TEXT_P(cstring_to_text(report));
}

PGDLLEXPORT Datum ankus_test_list_fault(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_list_fault);
PGDLLEXPORT Datum
ankus_test_list_fault(PG_FUNCTION_ARGS)
{
    int mode = PG_GETARG_INT32(0);
    fault_require(mode >= 1 && mode <= 13, "unknown list fault scenario");
    fault_require(fault_live_records == 0 && ankus_lists == NULL && ankus_memory_contexts == NULL,
        "a previous list invocation retained registry storage");
    MemoryContext caller = CurrentMemoryContext;
    MemoryContext root = AllocSetContextCreate(caller, "Ankus list fault", ALLOCSET_SMALL_SIZES);
    uint64 saved_next = ankus_list_next_id;
    char *volatile report = NULL;
    PG_TRY();
    {
        fault_owner = root;
        AnkusMemoryApi api;
        ankus_memory_initialize(&api);
        uint64 owner = ankus_memory_context_id(root);
        uint64 cells[128];
        for (int index = 0; index < 128; index++) { cells[index] = (uint64) (uint32) (index - 64); }
        AnkusMemoryRequest request = {0};
        AnkusMemoryResult result = {0};
        AnkusError error = {0};
        request.operation = ANKUS_MEMORY_LIST;
        request.flags = ANKUS_LIST_CREATE;
        request.context = (intptr_t) owner;
        request.alignment = 2;
        request.data = (intptr_t) cells;
        request.length = 128;
        int before_live = fault_live_records;
        int before_allocated = fault_successful_reservations;
        int before_freed = fault_released_reservations;
        fault_list_frees = 0;
        fault_list_monitor = true;
        /* Modes 1 through 4 fail one step of creation, and modes 12 and 13 create the list in a bump or slab context. */
        if (mode <= 4 || mode >= 12)
        {
            if (mode >= 12)
            {
                MemoryContext special = mode == 12 ? fault_create_bump(root) :
                    SlabContextCreate(root, "Ankus list slab", 8192, 64);
                request.context = (intptr_t) ankus_memory_context_id(special);
                before_live = fault_live_records;
                before_allocated = fault_successful_reservations;
                before_freed = fault_released_reservations;
            }

            fault_list_stage = mode <= 2 ? mode : 0;
            fault_fail_calloc = mode == 3;
            if (mode == 4) { ankus_list_next_id = 0; }
            int status = ankus_memory_invoke(&api, &request, &result, &error);
            ankus_list_next_id = saved_next;
            fault_require(status == 1 && error.sqlstate == (mode >= 12 ? ERRCODE_INTERNAL_ERROR : ERRCODE_OUT_OF_MEMORY),
                "list acquisition did not return the expected native error");
            fault_require(result.pointer == 0 && ankus_lists == NULL && fault_live_records == before_live,
                "partial list acquisition published or retained a registry record");
            fault_require(fault_list_stage == 0 && !fault_fail_calloc, "list acquisition fault did not execute");
            int allocations = fault_successful_reservations - before_allocated;
            int frees = fault_released_reservations - before_freed;
            int chunks = fault_list_frees;
            ankus_release_error(&error);

            /* Nothing may be published or kept, and a retry in an ordinary context keeps every value. */
            fault_require(CurrentMemoryContext == caller, "list acquisition changed the caller's context");
            request.context = (intptr_t) owner;
            fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "list retry failed");
            AnkusList *entry = ankus_list_find((uint64) result.pointer);
            fault_require(entry != NULL && list_length(entry->list) == 128 && list_nth_int(entry->list, 0) == -64 &&
                list_nth_int(entry->list, 127) == 63, "list retry lost its values");
            request.context = result.pointer;
            request.flags = ANKUS_LIST_DISPOSE;
            fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "list retry release failed");
            report = psprintf("%s|%d|%d|%d|retry|%d", mode >= 12 ? "XX000" : "53200",
                allocations, frees, chunks, fault_live_records - before_live);
        }
        else if (mode <= 6)
        {
            /* Modes 5 and 6 fail growth of a short or full list, which must leave its storage and cells untouched. */
            request.length = mode == 5 ? 1 : 128;
            fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "list growth setup failed");
            uint64 handle = (uint64) result.pointer;
            AnkusList *entry = ankus_list_find(handle);
            List *original = entry->list;
            ListCell *storage = original->elements;
            int length = original->length;
            int capacity = original->max_length;
            request.flags = ANKUS_LIST_RESERVE;
            request.context = (intptr_t) handle;
            request.length = 4096;
            fault_list_stage = mode == 5 ? 2 : 3;
            fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 1 && error.sqlstate == ERRCODE_OUT_OF_MEMORY,
                "list reserve did not report its controlled error");
            fault_require(fault_list_stage == 0 && entry->list == original && original->elements == storage &&
                original->length == length && original->max_length == capacity, "failed growth changed native storage or metadata");
            for (int index = 0; index < length; index++)
            {
                fault_require(list_nth_int(original, index) == index - 64, "failed growth changed a cell");
            }

            ankus_release_error(&error);
            fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0 && result.value == 1,
                "list reserve retry failed");
            fault_require(entry->list->max_length >= length + 4096, "list reserve did not count additional cells");
            request.flags = ANKUS_LIST_DISPOSE;
            fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "grown list disposal failed");
            report = psprintf("53200|%d|preserved|%d|%d", length, fault_list_frees, fault_live_records - before_live);
        }
        else
        {
            /*
             * Modes 7 through 11 grow 128 lists to more than a megabyte of cells, releasing each by disposal, context
             * reset, transfer, clearing or draining. Each release must return the cell buffer.
             */
            for (int iteration = 0; iteration < 128; iteration++)
            {
                request.flags = ANKUS_LIST_CREATE;
                request.context = (intptr_t) ankus_memory_context_id(root);
                request.length = 128;
                request.data = (intptr_t) cells;
                fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "repeated list creation failed");
                uint64 handle = (uint64) result.pointer;
                AnkusList *entry = ankus_list_find(handle);
                request.context = (intptr_t) handle;
                request.flags = ANKUS_LIST_RESERVE;
                request.length = 131072;
                fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "large list reserve failed");
                fault_require(list_nth_int(entry->list, 0) == -64 && list_nth_int(entry->list, 127) == 63,
                    "large list reserve corrupted its endpoint values");
                if (mode == 8)
                {
                    MemoryContextReset(root);
                }
                else
                {
                    request.flags = mode == 7 ? ANKUS_LIST_DISPOSE : mode == 9 ? ANKUS_LIST_DETACH :
                        mode == 10 ? ANKUS_LIST_CLEAR : ANKUS_LIST_DRAIN;
                    uint64 drained[128];
                    request.length = 128;
                    request.value = 0;
                    request.data = (intptr_t) drained;
                    fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "repeated list release failed");
                    if (mode == 9)
                    {
                        List *detached = (List *) result.pointer;
                        fault_require(list_nth_int(detached, 0) == -64 && list_nth_int(detached, 127) == 63,
                            "list transfer freed or changed cells");
                        list_free(detached);
                    }
                    else if (mode >= 10)
                    {
                        fault_require(entry->list == NIL && result.pointer == 0 && result.length == 0,
                            "empty list retained a non-NIL header");
                        if (mode == 11)
                        {
                            fault_require(memcmp(drained, cells, sizeof(cells)) == 0, "drain changed removed cell values");
                        }

                        request.flags = ANKUS_LIST_DISPOSE;
                        fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "empty list disposal failed");
                    }
                }

                ankus_release_error(&error);
                fault_require(ankus_lists == NULL, "repeated list release retained registry records");
                fault_require(MemoryContextMemAllocated(root, false) < 1048576, "list release retained a large cell buffer");
            }

            report = psprintf("128|exact|%d|empty", fault_list_frees);
        }
    }
    PG_FINALLY();
    {
        fault_list_stage = 0;
        fault_list_monitor = false;
        fault_fail_calloc = false;
        fault_owner = NULL;
        if (ankus_list_next_id == 0) { ankus_list_next_id = saved_next; }
        MemoryContextSwitchTo(caller);
        MemoryContextDelete(root);
    }
    PG_END_TRY();
    fault_require(fault_live_records == 0 && ankus_lists == NULL && ankus_memory_contexts == NULL,
        "list context deletion retained native registry storage");
    PG_RETURN_TEXT_P(cstring_to_text(report));
}
