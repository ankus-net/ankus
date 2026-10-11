namespace Ankus.Generators;

/// <summary>
/// Owns PostgreSQL set execution, tuple conversion, and managed iterator cleanup across executor exits.
/// </summary>
internal static class NativeSetBridge
{
    /// <summary>
    /// Gets value-per-call and materialized execution using the existing guarded datum and SPI boundaries.
    /// </summary>
    internal const string Source = """
        #include "funcapi.h"
        #include "executor/executor.h"
        #include "utils/tuplestore.h"
        #include "utils/snapmgr.h"

        typedef int (*AnkusSetCallback)(int, void **, const AnkusValue *, AnkusValue *, AnkusError *, AnkusExecute, AnkusMemoryApi *, FunctionCallInfo);

        typedef struct AnkusSetState
        {
            MemoryContextCallback reset;
            MemoryContext owner;
            MemoryContext result_owner;
            void *iterator;
            AnkusSetCallback callback;
            Oid function;
            ExprContext *econtext;
            TupleDesc descriptor;
            TupleDesc composite_descriptor;
            Oid *argument_types;
            Oid *base_types;
            int columns;
            AnkusValue *row;
            Datum *values;
            bool *nulls;
            bool materialize;
            bool composite_result;
        } AnkusSetState;

        static int
        ankus_set_call(AnkusSetState *state, int operation, const AnkusValue *arguments, AnkusError *error, bool backend,
            FunctionCallInfo function_call)
        {
            Oid previous = ankus_function_oid;
            MemoryContext caller = CurrentMemoryContext;
            int status;
            AnkusMemoryApi memory = {0};
            AnkusMemoryProtection protection = {0};
            ankus_function_oid = state->function;
            ankus_memory_initialize(&memory);
            /* Results retained by managed iterators must outlive individual row callbacks. */
            memory.result_context = state->result_owner;
            ankus_memory_protect(&protection, state->owner, operation == 3);
            PG_TRY();
            {
                if (operation == 0)
                {
                    MemoryContextSwitchTo(state->owner);
                }

                ANKUS_MANAGED_INVOKE(status, error, state->callback(operation, &state->iterator, arguments, state->row, error,
                    backend ? ankus_spi_execute : NULL, &memory, function_call));
            }
            PG_FINALLY();
            {
                if (operation == 0)
                {
                    MemoryContextSwitchTo(caller);
                }

                ankus_memory_protection = protection.previous;
                ankus_function_oid = previous;
            }
            PG_END_TRY();
            return status;
        }

        static void
        ankus_set_release_row(AnkusSetState *state)
        {
            for (int index = 0; index < state->columns; index++)
            {
                if (state->row[index].release != NULL)
                    state->row[index].release(state->row[index].data);
                memset(&state->row[index], 0, sizeof(AnkusValue));
            }
        }

        static void
        ankus_set_abort_cleanup(void *argument)
        {
            AnkusSetState *state = argument;
            AnkusError error = {0};
            int status;
            if (state->row != NULL)
                ankus_set_release_row(state);
            if (state->iterator == NULL)
                return;
            /* The executor skips expression callbacks on abort. Managed cleanup still runs;
             * SPI queries are unavailable while the backend dismantles its transaction. */
            status = ankus_set_call(state, 3, NULL, &error, true, NULL);
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
                ereport(WARNING, (errmsg("Ankus iterator disposal failed during query abort")));
        }

        static void
        ankus_set_dispose(AnkusSetState *state)
        {
            AnkusError error = {0};
            if (state->iterator != NULL && ankus_set_call(state, 2, NULL, &error, true, NULL) != 0)
                ankus_raise_error(&error);
        }

        static void
        ankus_set_shutdown(Datum argument)
        {
            AnkusSetState *state = (AnkusSetState *) DatumGetPointer(argument);
            bool snapshot = !ActiveSnapshotSet();
            /* Registered after init_MultiFuncCall, so cleanup precedes deletion of its context. */
            if (snapshot)
                PushActiveSnapshot(GetTransactionSnapshot());
            PG_TRY();
            {
                ankus_set_dispose(state);
            }
            PG_FINALLY();
            {
                if (snapshot)
                    PopActiveSnapshot();
            }
            PG_END_TRY();
        }

        static void
        ankus_set_finish(FunctionCallInfo fcinfo, FuncCallContext *context, AnkusSetState *state)
        {
            ankus_set_dispose(state);
            UnregisterExprContextCallback(state->econtext, ankus_set_shutdown, PointerGetDatum(state));
            end_MultiFuncCall(fcinfo, context);
        }

        static AnkusSetState *
        ankus_set_initialize(FunctionCallInfo fcinfo, FuncCallContext *context, AnkusSetCallback callback,
            int columns, int arguments, const bool *required, int mode, bool composite_result,
            const bool *polymorphic, bool polymorphic_result)
        {
            ReturnSetInfo *info = (ReturnSetInfo *) fcinfo->resultinfo;
            MemoryContext previous = MemoryContextSwitchTo(context->multi_call_memory_ctx);
            AnkusSetState *state = palloc0(sizeof(AnkusSetState));
            TupleDesc descriptor = NULL;
            Oid scalar_type;
            int argument_count;
            bool skip = false;
            TypeFuncClass result_kind;
            state->function = fcinfo->flinfo->fn_oid;
            state->owner = context->multi_call_memory_ctx;
            state->result_owner = state->owner;
            state->callback = callback;
            state->columns = columns;
            state->composite_result = composite_result;
            state->econtext = info->econtext;
            /* PostgreSQL drains reset callbacks in reverse registration order. Register
             * invalidation first so iterator cleanup can still read direct owner storage. */
            AnkusMemoryContext *owner = ankus_memory_register_context(state->owner);
            owner->reserved = true;
            state->reset.func = ankus_set_abort_cleanup;
            state->reset.arg = state;
            MemoryContextRegisterResetCallback(context->multi_call_memory_ctx, &state->reset);
            context->user_fctx = state;

            state->materialize = mode == 2 || (mode == 0 && !(info->allowedModes & SFRM_ValuePerCall));
            if ((state->materialize && !(info->allowedModes & SFRM_Materialize)) ||
                (!state->materialize && !(info->allowedModes & SFRM_ValuePerCall)))
                ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                    errmsg("The caller does not support the requested Ankus set execution mode")));

            (void) get_func_signature(state->function, &state->argument_types, &argument_count);
            if (argument_count != arguments || PG_NARGS() != arguments)
                ereport(ERROR, (errmsg("Incorrect argument count for generated Ankus set function")));
            result_kind = get_call_result_type(fcinfo, &scalar_type, &descriptor);
            if (polymorphic_result && (result_kind == TYPEFUNC_COMPOSITE || result_kind == TYPEFUNC_COMPOSITE_DOMAIN || result_kind == TYPEFUNC_RECORD))
                composite_result = state->composite_result = true;
            if (columns > 1)
            {
                if (result_kind != TYPEFUNC_COMPOSITE || descriptor == NULL || descriptor->natts != columns)
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Ankus TABLE result has an incompatible row descriptor")));
                state->descriptor = BlessTupleDesc(CreateTupleDescCopy(descriptor));
            }
            else
            {
                if (composite_result)
                {
                    if (result_kind != TYPEFUNC_COMPOSITE && result_kind != TYPEFUNC_COMPOSITE_DOMAIN && result_kind != TYPEFUNC_RECORD)
                        ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Ankus composite SETOF result requires a row type")));
                    if (descriptor != NULL)
                        state->composite_descriptor = BlessTupleDesc(CreateTupleDescCopy(descriptor));
                    else if (state->materialize)
                        ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED), errmsg("Materialized record sets require a caller-supplied row descriptor")));
                }
                else if (result_kind != TYPEFUNC_SCALAR && scalar_type != INTERNALOID &&
                    !(polymorphic_result && result_kind == TYPEFUNC_OTHER))
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Ankus SETOF result requires a scalar column type")));
                state->descriptor = CreateTemplateTupleDesc(1);
                TupleDescInitEntry(state->descriptor, 1, "value", scalar_type, -1, 0);
        #if PG_VERSION_NUM >= 190000
                TupleDescFinalize(state->descriptor);
        #endif
            }

            /* Materialization copies internal words, not their pointed-to state.
             * Keep managed payloads with the returned store after the iterator closes. */
            if (state->materialize && !composite_result)
                for (int index = 0; index < columns; index++)
                    if (TupleDescAttr(state->descriptor, index)->atttypid == INTERNALOID)
                        state->result_owner = info->econtext->ecxt_per_query_memory;

            state->row = palloc0(sizeof(AnkusValue) * columns);
            /* Result column types are fixed for the call, so each row skips the catalog lookups. A missing
             * type stays unresolved and still fails when a row needs it. */
            state->base_types = palloc(sizeof(Oid) * columns);
            for (int index = 0; index < columns; index++)
            {
                Oid type = TupleDescAttr(state->descriptor, index)->atttypid;
                state->base_types[index] = get_typtype(type) == '\0' ? InvalidOid : getBaseType(type);
            }

            state->values = palloc0(sizeof(Datum) * columns);
            state->nulls = palloc0(sizeof(bool) * columns);
            RegisterExprContextCallback(info->econtext, ankus_set_shutdown, PointerGetDatum(state));
            MemoryContextSwitchTo(previous);

            for (int index = 0; index < arguments; index++)
                if (required[index] && PG_ARGISNULL(index))
                    skip = true;
            if (!skip)
            {
                AnkusValue *input = palloc0(sizeof(AnkusValue) * Max(arguments, 1));
                AnkusInputBuffer *owned = palloc0(sizeof(AnkusInputBuffer) * Max(arguments, 1));
                AnkusError error = {0};
                int status = 0;
                Oid previous_function = ankus_function_oid;
                ankus_function_oid = state->function;
                PG_TRY();
                {
                    for (int index = 0; index < arguments; index++)
                    {
                        input[index].is_null = PG_ARGISNULL(index);
                        if (polymorphic[index])
                            ankus_read_polymorphic(fcinfo, index, &input[index]);
                        else if (!input[index].is_null)
                            ankus_read_value(PG_GETARG_DATUM(index), state->argument_types[index], &input[index], &owned[index]);
                    }

                    status = ankus_set_call(state, 0, input, &error, true, fcinfo);
                }
                PG_FINALLY();
                {
                    ankus_function_oid = previous_function;
                    for (int index = 0; index < arguments; index++)
                        ankus_free_input(&owned[index]);
                    pfree(owned);
                    pfree(input);
                }
                PG_END_TRY();
                if (status != 0)
                    ankus_raise_error(&error);
            }

            return state;
        }

        static bool
        ankus_set_next(AnkusSetState *state)
        {
            AnkusError error;
            volatile int status = 2;
            ankus_error_clear(&error);
            Oid previous_function = ankus_function_oid;
            CHECK_FOR_INTERRUPTS();
            if (state->iterator == NULL)
                return false;
            ankus_function_oid = state->function;
            PG_TRY();
            {
                status = ankus_set_call(state, 1, NULL, &error, true, NULL);
                if (status == 0)
                {
                    for (int index = 0; index < state->columns; index++)
                    {
                        AnkusParameter cell = {0};
                        cell.type_oid = TupleDescAttr(state->descriptor, index)->atttypid;
                        cell.value = state->row[index];
                        state->nulls[index] = cell.value.is_null != 0;
                        if (state->composite_result && !state->nulls[index] && cell.value.auxiliary1 != -6)
                            state->values[index] = ankus_write_tuple(&cell.value, cell.type_oid, state->composite_descriptor);
                        else if (OidIsValid(state->base_types[index]))
                            state->values[index] = ankus_parameter_datum_resolved(&cell, state->base_types[index]);
                        else
                            state->values[index] = ankus_parameter_datum(&cell);
                    }
                }
            }
            PG_FINALLY();
            {
                ankus_function_oid = previous_function;
                ankus_set_release_row(state);
            }
            PG_END_TRY();
            if (status == 1)
            {
                AnkusError cleanup = {0};
                int cleanup_status;
                AnkusRecoveryFrame recovery = {0};
                recovery.previous = ankus_recovery_frame;
                if ((error.flags & ANKUS_ERROR_UNRECOVERED) != 0 ||
                    error.sqlstate == ERRCODE_QUERY_CANCELED || error.report_level != 0)
                {
                    ankus_recovery_store(&recovery, &error);
                }

                ankus_recovery_frame = &recovery;
                PG_TRY();
                {
                    cleanup_status = ankus_set_call(state, 2, NULL, &cleanup, true, NULL);
                }
                PG_FINALLY();
                {
                    ankus_recovery_pop(&recovery);
                    ankus_release_error(&recovery.failure);
                }
                PG_END_TRY();
                if (cleanup_status != 0)
                {
                    if (cleanup.sqlstate == ERRCODE_QUERY_CANCELED || cleanup.report_level != 0)
                    {
                        ankus_release_error(&error);
                        ankus_raise_error(&cleanup);
                    }

                    ankus_release_error(&cleanup);
                    ereport(WARNING, (errmsg("Ankus iterator disposal also failed after a row error")));
                }

                ankus_raise_error(&error);
            }

            return status == 0;
        }

        static Datum
        ankus_set_execute(FunctionCallInfo fcinfo, AnkusSetCallback callback, int columns, int arguments,
            const bool *required, int mode, bool composite_result, const bool *polymorphic, bool polymorphic_result)
        {
            FuncCallContext *context;
            AnkusSetState *state;
            ReturnSetInfo *info;
            if (SRF_IS_FIRSTCALL())
            {
                context = SRF_FIRSTCALL_INIT();
                state = ankus_set_initialize(fcinfo, context, callback, columns, arguments, required, mode, composite_result,
                    polymorphic, polymorphic_result);
            }
            else
            {
                context = SRF_PERCALL_SETUP();
                state = context->user_fctx;
            }

            info = (ReturnSetInfo *) fcinfo->resultinfo;
            if (state->materialize)
            {
                MemoryContext previous = MemoryContextSwitchTo(info->econtext->ecxt_per_query_memory);
                Tuplestorestate *store = tuplestore_begin_heap((info->allowedModes & SFRM_Materialize_Random) != 0, false, work_mem);
                TupleDesc descriptor = CreateTupleDescCopy(state->composite_result ? state->composite_descriptor : state->descriptor);
                MemoryContext row_context = AllocSetContextCreate(context->multi_call_memory_ctx, "Ankus set row", ALLOCSET_SMALL_SIZES);
                MemoryContextSwitchTo(row_context);
                while (ankus_set_next(state))
                {
                    if (state->composite_result)
                    {
                        if (state->nulls[0])
                        {
                            Datum *values = palloc0(sizeof(Datum) * Max(descriptor->natts, 1));
                            bool *nulls = palloc(sizeof(bool) * Max(descriptor->natts, 1));
                            memset(nulls, true, sizeof(bool) * descriptor->natts);
                            tuplestore_putvalues(store, descriptor, values, nulls);
                        }
                        else
                        {
                            HeapTupleData tuple = {0};
                            tuple.t_data = DatumGetHeapTupleHeader(state->values[0]);
                            tuple.t_len = HeapTupleHeaderGetDatumLength(tuple.t_data);
                            tuplestore_puttuple(store, &tuple);
                        }
                    }
                    else
                        tuplestore_putvalues(store, descriptor, state->values, state->nulls);
                    MemoryContextReset(row_context);
                }

                MemoryContextSwitchTo(previous);
                ankus_set_finish(fcinfo, context, state);
                info->returnMode = SFRM_Materialize;
                info->setResult = store;
                info->setDesc = descriptor;
                fcinfo->isnull = true;
                return (Datum) 0;
            }

            info->returnMode = SFRM_ValuePerCall;
            if (!ankus_set_next(state))
            {
                ankus_set_finish(fcinfo, context, state);
                info->isDone = ExprEndResult;
                fcinfo->isnull = true;
                return (Datum) 0;
            }

            if (columns == 1)
            {
                Datum value = state->values[0];
                fcinfo->isnull = state->nulls[0];
                SRF_RETURN_NEXT(context, value);
            }
            else
            {
                HeapTuple tuple = heap_form_tuple(state->descriptor, state->values, state->nulls);
                fcinfo->isnull = false;
                SRF_RETURN_NEXT(context, HeapTupleGetDatum(tuple));
            }
        }

        """;
}
