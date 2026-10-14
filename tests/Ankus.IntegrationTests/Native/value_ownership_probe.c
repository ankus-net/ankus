PGDLLEXPORT Datum ankus_test_value_ownership(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_value_ownership);
Datum ankus_test_value_ownership(PG_FUNCTION_ARGS)
{
    int family = PG_GETARG_INT32(0);
    owner_invalidated = PG_GETARG_BOOL(1);
    owner_cancel = PG_GETARG_BOOL(2);
    bool recover_input = PG_GETARG_BOOL(3);
    owner_keeper = NULL;
    owner_obsolete = NULL;
    owner_parent = CurrentResourceOwner;
    MemoryContext caller = CurrentMemoryContext;
    uint32 held = InterruptHoldoffCount;
    uint32 cancel_held = QueryCancelHoldoffCount;
    HeapTuple caller_pin = SearchSysCache1(TYPEOID, ObjectIdGetDatum(INT4OID));
    int caller_references = owner_entry(caller_pin)->refcount;
    AnkusRequest request = {0};
    AnkusParameter parameter = {0};
    AnkusDatumReference reference = {0};
    request.recover_input = recover_input;
    request.parameters = &parameter;
    request.parameter_count = 1;
    parameter.type_oid = TEXTOID;
    /* Families 0 through 4 read a scalar through a type's input function; family 5 copies an array datum. */
    const char *inputs[] = {"42", "2026-01-01", "127.0.0.1", "(1,2)", "[1,3)"};
    if (family < 5)
    {
        int operations[] = {ANKUS_SPI_NUMERIC, ANKUS_SPI_TEMPORAL, ANKUS_SPI_NETWORK, ANKUS_SPI_GEOMETRY, ANKUS_SPI_RANGE};
        Oid results[] = {NUMERICOID, DATEOID, INETOID, POINTOID, INT4RANGEOID};
        request.operation = operations[family];
        request.scalar_result_oid = results[family];
        parameter.value.data = (unsigned char *) inputs[family];
        parameter.value.length = strlen(inputs[family]);
    }
    else if (family == 5)
    {
        Datum cell = Int32GetDatum(42);
        ArrayType *array = construct_array(&cell, 1, INT4OID, sizeof(int32), true, 'i');
        intptr_t identity = (intptr_t) ankus_memory_context_id(caller);
        AnkusMemoryContext *context = ankus_memory_context_by_id((uint64) identity);
        if (context == NULL)
            elog(ERROR, "the caller's context was not registered");

        reference.bits = (uintptr_t) array;
        reference.context = identity;
        reference.generation = context->generation;
        request.operation = ANKUS_SPI_DATUM;
        request.scalar_operation = 6;
        request.result_context = identity;
        request.result_generation = context->generation;
        parameter.type_oid = INT4ARRAYOID;
        parameter.value.auxiliary1 = -5;
        parameter.value.data = (unsigned char *) &reference;
        parameter.value.length = sizeof(reference);
    }
    else
        elog(ERROR, "unknown ownership probe family");

    /* The guarded call fails while it holds a syscache reference, inside a subtransaction that can roll it back. */
    BeginInternalSubTransaction(NULL);
    AnkusRecoveryFrame frame = {0};
    frame.previous = ankus_recovery_frame;
    ankus_recovery_frame = &frame;
    AnkusError error = {0};
    AnkusResult result = {0};
    owner_armed = true;
    int status = ankus_spi_execute(&request, &result, &error);
    if (status != 1 || owner_armed || owner_keeper == NULL ||
        error.sqlstate != (owner_cancel ? ERRCODE_QUERY_CANCELED : ERRCODE_OUT_OF_MEMORY))
    {
        if (error.sqlstate != 0)
            ankus_raise_error(&error);

        elog(ERROR, "the exact guarded catalog fault did not execute");
    }
    if (!owner_cancel && (strcmp(error.message, "controlled catalog ownership failure") != 0 ||
        strcmp(ankus_error_field(&error, ANKUS_ERROR_DETAIL, true), "a real syscache reference is still owned") != 0 ||
        strcmp(ankus_error_field(&error, ANKUS_ERROR_HINT, true), "roll back before further backend work") != 0))
        elog(ERROR, "the guard lost the owned original diagnostics");
    int unrecovered = (error.flags & ANKUS_ERROR_UNRECOVERED) != 0;
    int before = owner_entry(owner_keeper)->refcount;
    int locked_before = owner_error_lock_initialized && LWLockHeldByMe(&owner_error_lock);
    int lookups = owner_lookups;
    ankus_release_error(&error);

    /* Until the failed call recovers, another backend call is refused without touching the catalog. */
    memset(&error, 0, sizeof(error));
    memset(&result, 0, sizeof(result));
    AnkusRequest retry = {0};
    retry.operation = ANKUS_SPI_IS_LOG_ENABLED;
    retry.log_level = 8;
    int blocked = ankus_spi_execute(&retry, &result, &error);
    if (owner_lookups != lookups)
        elog(ERROR, "a blocked guard performed a catalog lookup");
    /* Rolling back releases the failed call's reference and leaves the caller's own pin and holdoffs as they were. */
    status = ankus_recovery_finish(&frame, &error, status);
    ankus_release_error(&error);
    MemoryContextSwitchTo(caller);
    RollbackAndReleaseCurrentSubTransaction();
    CurrentResourceOwner = owner_parent;
    int after = owner_entry(owner_keeper)->refcount;
    int locked_after = owner_error_lock_initialized && LWLockHeldByMe(&owner_error_lock);
    int preserved = owner_entry(caller_pin)->refcount == caller_references &&
        InterruptHoldoffCount == held && QueryCancelHoldoffCount == cancel_held &&
        ankus_recovery_failed_frames == 0;
    ReleaseSysCache(owner_keeper);
    if (owner_obsolete != NULL)
        ReleaseSysCache(owner_obsolete);
    ReleaseSysCache(caller_pin);
    owner_keeper = NULL;
    owner_obsolete = NULL;
    Datum outcomes[] = {Int32GetDatum(unrecovered), Int32GetDatum(blocked),
        Int32GetDatum(before), Int32GetDatum(after), Int32GetDatum(locked_before),
        Int32GetDatum(locked_after), Int32GetDatum(preserved), Int32GetDatum(owner_lookups == lookups)};
    PG_RETURN_ARRAYTYPE_P(construct_array(outcomes, lengthof(outcomes), INT4OID, sizeof(int32), true, 'i'));
}
