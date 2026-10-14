namespace Ankus.Generators;

/// <summary>
/// Supplies PostgreSQL call guards that confine longjmp-based error handling to native frames.
/// </summary>
internal static class GuardedBackend
{
    /// <summary>
    /// Gets structured error transport, reporting, and an atomic SPI execution guard.
    /// </summary>
    internal const string Source = """
        #include "access/xact.h"
        #include "executor/spi.h"
        #include "utils/memutils.h"
        #include "utils/resowner.h"

        static bool
        ankus_is_builtin_range(Oid type)
        {
            return type == INT4RANGEOID || type == INT8RANGEOID || type == NUMRANGEOID ||
                type == DATERANGEOID || type == TSRANGEOID || type == TSTZRANGEOID;
        }

        static bool
        ankus_uses_builtin_range(const AnkusRequest *request)
        {
            if (ankus_is_builtin_range(request->scalar_result_oid))
                return true;

            return request->parameter_count > 0 && request->parameters != NULL &&
                ankus_is_builtin_range(request->parameters[0].type_oid);
        }

        static int
        ankus_spi_execute_core(AnkusRequest *request, AnkusResult *result, AnkusError *error)
        {
            if (request->operation == ANKUS_SPI_REPORT && request->log_level >= 11)
                return ankus_recovery_terminal(request->log_level, request->diagnostic, error);

            bool abort_session = false;
            if (ankus_recovery_failed(error))
            {
                /* Saved plans outlive transaction abort. Their explicit disposal must
                 * remain possible, without connecting SPI or running new SQL. */
                bool release = request->operation == ANKUS_SPI_FREE_PLAN || request->operation == ANKUS_SPI_CLOSE_CURSOR ||
                    (request->operation == ANKUS_SPI_RELATION && request->scalar_operation == 0);
                /* A session owns an enclosing recovery subtransaction. Closing it
                 * must restore the caller's transaction depth before rethrow. */
                bool close_session = request->operation == ANKUS_SPI_CLOSE_SESSION;
                bool terminal_cleanup = error->report_level >= 12 && (error->flags & ANKUS_ERROR_UNRECOVERED) == 0 &&
                    (request->operation == ANKUS_SPI_REPORT || request->operation == ANKUS_SPI_IS_LOG_ENABLED);
                if (!close_session && !terminal_cleanup && (!release || request->session_id != 0))
                    return 1;

                if (close_session && (error->flags & ANKUS_ERROR_UNRECOVERED) != 0)
                {
                    /* Raw ERROR may leave additional SPI frames above our connection.
                     * Roll back our enclosing transaction instead of closing that frame.
                     * Direct callback SPI belongs to the outer PostgreSQL abort. */
                    if (ankus_session == NULL || ankus_session->identity != request->session_id || !ankus_session->subtransaction_owned)
                        return 1;
                    abort_session = true;
                }

                memset(error, 0, sizeof(*error));
                request->cleanup_only = !close_session && !terminal_cleanup;
            }

            AnkusTransactionFrame *transaction_frame = ankus_transaction_frame;
            bool transaction_direct_spi = transaction_frame != NULL && transaction_frame->direct_spi;
            bool direct_spi = transaction_direct_spi || ankus_parallel_without_subtransactions();
            if (transaction_direct_spi && transaction_frame->failed)
            {
                memset(error, 0, sizeof(*error));
                error->sqlstate = transaction_frame->failure.sqlstate;
                strlcpy(error->message, transaction_frame->failure.message, sizeof(error->message));
                return 1;
            }

            if (ankus_memory_error_cleanup)
            {
                /* ErrorContext callbacks may run inside PostgreSQL's error reporter.
                 * Returning a diagnostic must not enter or flush that reporter again. */
                memset(error, 0, sizeof(*error));
                error->sqlstate = ERRCODE_OBJECT_IN_USE;
                strlcpy(error->message, "Guarded SPI operations are unavailable during ErrorContext cleanup", sizeof(error->message));
                return 1;
            }

            MemoryContext caller_context = CurrentMemoryContext;
            uint32 interrupt_holdoff = InterruptHoldoffCount;
            uint32 shared_held_before = ankus_shared_held_count;
            uint32 cancel_holdoff = QueryCancelHoldoffCount;
            ResourceOwner caller_owner = CurrentResourceOwner;
            int caller_nest_level = GetCurrentTransactionNestLevel();
            volatile int status = 0;
            volatile bool retained_plan = false;
            bool standalone = request->session_id == 0;
            bool quote = ankus_is_quote_request(request->operation);
            bool reporting = request->operation == ANKUS_SPI_REPORT;
            bool temporal = request->operation == ANKUS_SPI_TEMPORAL;
            bool numeric = request->operation == ANKUS_SPI_NUMERIC;
            bool network = request->operation == ANKUS_SPI_NETWORK;
            bool geometry = request->operation == ANKUS_SPI_GEOMETRY;
            bool range = request->operation == ANKUS_SPI_RANGE;
            bool enumeration = request->operation == ANKUS_SPI_ENUM;
            bool custom_type = request->operation == ANKUS_SPI_CUSTOM_TYPE;
            bool datum_type = request->operation == ANKUS_SPI_DATUM_TYPE;
            bool lookup = request->operation == ANKUS_SPI_LOOKUP;
            bool relation = request->operation == ANKUS_SPI_RELATION;
            bool array = request->operation == ANKUS_SPI_ARRAY;
            bool tuple = request->operation == ANKUS_SPI_TUPLE;
            bool transaction_callbacks = request->operation == ANKUS_SPI_TRANSACTION_CALLBACKS;
            bool transaction_id = request->operation == ANKUS_SPI_TRANSACTION_ID;
            bool datum = request->operation == ANKUS_SPI_DATUM;
            bool function_context = request->operation == ANKUS_SPI_FUNCTION_CONTEXT;
            bool function_call = request->operation == ANKUS_SPI_FUNCTION_CALL;
            bool subtransaction = request->operation == ANKUS_SPI_SUBTRANSACTION;
            /* Successful built-in operations avoid a per-call subtransaction. Catalog
             * lookup and value conversion can still acquire transaction resources;
             * a caught ERROR requires real rollback before further backend work. */
        #if PG_VERSION_NUM >= 160000
            /* PostgreSQL 16 input functions report invalid text without raising, so TryParse of a
             * built-in type needs no subtransaction, and also works where none may start, such as
             * in transaction callbacks and parallel workers. */
            bool soft_input = request->recover_input && request->scalar_operation == 0 &&
                (numeric || temporal || network || geometry || (range && ankus_uses_builtin_range(request)));
        #else
            bool soft_input = false;
        #endif
            /* Before PostgreSQL 16, TryParse recovers through its own subtransaction. An explicit
             * scope already runs inside one, so it may nest another there, even in an atomic scope;
             * transaction callbacks and parallel workers cannot start one. */
            bool input_recovery = !soft_input && request->recover_input && request->scalar_operation == 0 &&
                (numeric || temporal || network || geometry || (range && ankus_uses_builtin_range(request))) &&
                !ankus_parallel_without_subtransactions() && (transaction_frame == NULL || transaction_frame->scope);
            /* Without a log hook, a report below ERROR runs only PostgreSQL's own formatting and
             * client-encoding conversion. Neither holds a resource when it fails, so the report
             * needs no subtransaction and its failure is still recovered. */
            bool light_report = reporting && request->log_level >= 0 && request->log_level < 10 && emit_log_hook == NULL;
            volatile bool report_started = false;
            bool lightweight = !input_recovery && (numeric || temporal || network || geometry || light_report ||
                (range && ankus_uses_builtin_range(request)) || (datum && request->scalar_operation == 6));
            bool direct = quote || reporting || temporal || numeric || network || geometry || range || enumeration || tuple ||
                transaction_callbacks || transaction_id || datum || function_context || function_call || custom_type || datum_type || array || lookup || relation || subtransaction;
            /* An explicit scope always owns a subtransaction, even inside an atomic scope whose
             * statements share their enclosing subtransaction. */
            bool recovery_subtransaction = (!direct_spi || input_recovery || (subtransaction && !ankus_parallel_without_subtransactions())) &&
                !lightweight;
            volatile MemoryContext operation_context = NULL;
            result->release = ankus_release_result;

            if (request->operation == ANKUS_SPI_GUC_READ)
            {
                result->release = NULL;
                if (ankus_read_guc != NULL)
                    return ankus_read_guc(request->command, request->scalar_operation, &result->text, error);
                error->sqlstate = ERRCODE_UNDEFINED_OBJECT;
                strlcpy(error->message, "This extension has no declared configuration parameters", sizeof(error->message));
                return 1;
            }

            if (request->operation == ANKUS_SPI_GUC_DEFINE)
            {
                result->release = NULL;
                if (ankus_read_guc != NULL)
                    return ankus_read_guc(request->command, ANKUS_GUC_DEFINE_KIND, (AnkusValue *) request->callback, error);
                error->sqlstate = ERRCODE_FEATURE_NOT_SUPPORTED;
                strlcpy(error->message, "This extension was built without run-time configuration support", sizeof(error->message));
                return 1;
            }

            if (request->operation == ANKUS_SPI_IS_LOG_ENABLED)
            {
                result->processed = ankus_log_enabled(ankus_log_level(request->log_level), error);
                return error->sqlstate != 0;
            }

            if (request->operation == ANKUS_SPI_CLOSE_SESSION && ankus_session != NULL &&
                ankus_session->identity == request->session_id)
            {
                caller_context = ankus_session->caller_context;
                caller_owner = ankus_session->caller_owner;
                caller_nest_level = ankus_session->caller_nest_level;
            }

            if (reporting && request->log_level >= 0 && request->log_level < 10)
            {
                /* PostgreSQL's reporter and subtransaction DEBUG messages check
                 * interrupts. Hold them through the complete logging operation,
                 * including transaction cleanup, then restore the caller below. */
                HOLD_INTERRUPTS();
            }

            /* Error recovery itself is guarded: it must not jump over the managed caller. */
            MemoryContext recovery_context = ankus_memory_contains(ErrorContext, caller_context) ? ErrorContext : caller_context;
            AnkusPendingError *volatile pending = ankus_pending_error_save();
            PG_TRY();
            {
                ANKUS_PG_TRY_INNER();
                {
                    /* PostgreSQL may reset ErrorContext even after a nonthrowing report.
                     * Retain an identity, not a pointer probe into a possibly deleted child. */
                    uint64 caller_identity = recovery_context == ErrorContext
                        ? ankus_memory_context_id(caller_context) : 0;
                    int code;
                    /* Cache diagnostic conversions while catalog access is available, so later
                     * reports and captured errors outside a transaction can still convert text. */
                    ankus_prepare_diagnostic_conversion();
                    if (subtransaction && transaction_frame != NULL && !transaction_frame->scope)
                        ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                            errmsg("Explicit recovery scopes are unavailable during transaction callbacks")));

                    if (subtransaction && ankus_parallel_without_subtransactions())
                        ereport(ERROR, (errcode(ERRCODE_INVALID_TRANSACTION_STATE),
                            errmsg("cannot start subtransactions during a parallel operation")));

                    if (request->operation == ANKUS_SPI_EXECUTE || request->operation == ANKUS_SPI_EXECUTE_PLAN ||
                        request->operation == ANKUS_SPI_OPEN_CURSOR || request->operation == ANKUS_SPI_OPEN_PLAN_CURSOR)
                    {
                        /* Observe the outer transaction before opening our recovery child.
                         * Owned session/subtransaction guards can have no local XID even
                         * though an earlier caller or SPI statement made the transaction writable. */
                        if (request->read_only == 2)
                        {
                            request->read_only = !TransactionIdIsValid(GetTopTransactionIdIfAny());
                        }

                        /* Match pgrx's writable intent even for a SELECT executed through
                         * a writable helper, so subsequent selection keeps fresh snapshots. */
                        else if (!request->read_only)
                        {
                            (void) GetCurrentTransactionId();
                        }
                    }

                    if (abort_session)
                    {
                        MemoryContextSwitchTo(recovery_context);
                        while (GetCurrentTransactionNestLevel() > caller_nest_level)
                        {
                            RollbackAndReleaseCurrentSubTransaction();
                        }

                        ankus_trim_internal_subtransactions();
                        request->session_id = 0;
                    }
                    else if (request->cleanup_only)
                    {
                        /* Abort cleanup releases owned resources without SQL or a new subtransaction.
                         * The outer recovery guard also protects diagnostic capture on this path. */
                        if (request->session_id != 0 ||
                            (request->operation != ANKUS_SPI_FREE_PLAN && request->operation != ANKUS_SPI_CLOSE_CURSOR &&
                                !(relation && request->scalar_operation == 0)))
                            ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                                errmsg("Only owned resource release is allowed during Ankus abort cleanup")));
                        if (relation)
                        {
                            ankus_relation_close(request->cursor_id);
                            code = 0;
                        }
                        else
                            code = ankus_run_spi_request(request, result);
                        if (code < 0)
                            ereport(ERROR, (errmsg("Ankus resource cleanup failed: %s", SPI_result_code_string(code))));
                    }
                    else if (request->operation == ANKUS_SPI_OPEN_SESSION)
                    {
                        /* Keep a recovery subtransaction until close whenever PostgreSQL permits
                         * it. Transaction callbacks and older parallel operations instead retain
                         * the first failure until their managed entry has completely unwound. */
                        if (recovery_subtransaction)
                        {
                            ankus_begin_internal_subtransaction();
                        }

                        code = SPI_connect();
                        if (code != SPI_OK_CONNECT)
                        {
                            ereport(ERROR, (errmsg("SPI_connect failed: %s", SPI_result_code_string(code))));
                        }

                        ankus_register_trigger_data();
                        ankus_register_session(request, caller_context, caller_owner, caller_nest_level);
                    }
                    else
                    {
                        if (recovery_subtransaction)
                        {
                            ankus_begin_internal_subtransaction();
                        }

                        if (standalone && !direct)
                        {
                            code = SPI_connect();
                            if (code != SPI_OK_CONNECT)
                            {
                                ereport(ERROR, (errmsg("SPI_connect failed: %s", SPI_result_code_string(code))));
                            }

                            ankus_register_trigger_data();
                        }
                        else if (!standalone)
                        {
                            ankus_require_session(request->session_id);
                        }

                        if ((direct || !standalone) && request->operation != ANKUS_SPI_CLOSE_SESSION && !subtransaction)
                        {
                            operation_context = AllocSetContextCreate(CurrentMemoryContext, "Ankus SPI operation", ALLOCSET_SMALL_SIZES);
                            MemoryContextSwitchTo(operation_context);
                        }

                    #if PG_VERSION_NUM >= 160000
                        if (soft_input)
                        {
                            /* The operation context releases it with the rest of the parse. */
                            ErrorSaveContext *soft_context = palloc0(sizeof(ErrorSaveContext));
                            soft_context->type = T_ErrorSaveContext;
                            soft_context->details_wanted = true;
                            ankus_soft_input_raised = false;
                            ankus_soft_input = soft_context;
                        }
                    #endif

                        if (request->operation != ANKUS_SPI_CLOSE_SESSION)
                        {
                            if (subtransaction)
                            {
                                typedef int (*AnkusSubtransactionManaged)(intptr_t);
                                if (request->callback == 0 || request->callback_state == 0)
                                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                                        errmsg("The subtransaction callback or state is missing")));

                                /* The thunk returns all managed failures before ERROR starts
                                 * rollback. Raw call guards remain inside the managed callback. */
                                AnkusError callback_error = {0};
                                int callback_status;
                                /* Every explicit scope shadows the enclosing frame. An atomic scope runs its
                                 * statements directly in this subtransaction and retains their first failure;
                                 * a recoverable scope restores per-statement recovery inside an atomic one. */
                                AnkusTransactionFrame scope_frame = {0};
                                scope_frame.previous = ankus_transaction_frame;
                                scope_frame.direct_spi = request->scalar_operation == 1;
                                scope_frame.scope = true;
                                ankus_transaction_frame = &scope_frame;
                                ANKUS_MANAGED_INVOKE(callback_status, &callback_error,
                                    ((AnkusSubtransactionManaged) request->callback)(request->callback_state));
                                ankus_transaction_frame = scope_frame.previous;
                                if (scope_frame.failed)
                                {
                                    ankus_release_error(&callback_error);
                                    callback_error = scope_frame.failure;
                                    memset(&scope_frame.failure, 0, sizeof(scope_frame.failure));
                                }

                                if (callback_error.sqlstate != 0)
                                    ankus_transaction_report(&callback_error, ERROR);
                                if (callback_status != 0)
                                    ereport(ERROR, (errcode(ERRCODE_EXTERNAL_ROUTINE_EXCEPTION),
                                        errmsg("The managed subtransaction callback failed")));

                                if (GetCurrentTransactionNestLevel() != caller_nest_level + 1)
                                    ereport(ERROR, (errcode(ERRCODE_INVALID_TRANSACTION_STATE),
                                        errmsg("The recovery callback changed its transaction nesting")));

                                code = 0;
                            }
                            else if (reporting)
                            {
                                report_started = light_report;
                                ankus_report(request->diagnostic, ankus_log_level(request->log_level));
                                code = 0;
                            }
                            else if (transaction_callbacks)
                            {
                                ankus_transaction_ensure((AnkusTransactionManaged) request->callback,
                                    request->scalar_operation);
                                code = 0;
                            }
                            else if (transaction_id)
                            {
                                ankus_transaction_id_operation(result);
                                code = 0;
                            }
                            else if (datum)
                            {
                                ankus_datum_operation(request, result);
                                code = 0;
                            }
                            else if (function_context)
                            {
                                ankus_function_context(request, result);
                                code = 0;
                            }
                            else if (function_call)
                            {
                                ankus_function_invoke(request, result);
                                code = 0;
                            }
                            else if (temporal)
                            {
                                ankus_temporal_operation(request, result);
                                code = 0;
                            }
                            else if (numeric)
                            {
                                ankus_numeric_operation(request, result);
                                code = 0;
                            }
                            else if (network)
                            {
                                ankus_network_operation(request, result);
                                code = 0;
                            }
                            else if (geometry)
                            {
                                ankus_geometry_operation(request, result);
                                code = 0;
                            }
                            else if (enumeration)
                            {
                                ankus_enum_operation(request, result);
                                code = 0;
                            }
                            else if (lookup)
                            {
                                ankus_lookup_operation(request, result);
                                code = 0;
                            }
                            else if (relation)
                            {
                                ankus_relation_operation(request, result, ankus_relation_parent_owner(caller_owner));
                                code = 0;
                            }
                            else if (custom_type)
                            {
                                result->text.integral = ankus_resolve_named_type(&request->parameters[0].value,
                                    &request->parameters[1].value, request->scalar_operation == 1, TYPTYPE_BASE);
                                code = 0;
                            }
                            else if (datum_type)
                            {
                                result->text.integral = ankus_resolve_named_type(&request->parameters[0].value,
                                    &request->parameters[1].value, false, '\0');
                                code = 0;
                            }
                            else if (array)
                            {
                                ankus_mapped_array(request, result);
                                code = 0;
                            }
                            else if (tuple)
                            {
                                ankus_tuple_operation(request, result);
                                code = 0;
                            }
                            else if (range)
                            {
                                ankus_range_operation(request, result);
                                code = 0;
                            }
                            else
                            {
                                code = quote ? ankus_quote(request, result) : ankus_run_spi_request(request, result);
                            }

                            if (code < 0)
                            {
                                ereport(ERROR, (errmsg("SPI operation failed: %s", SPI_result_code_string(code))));
                            }

                            retained_plan = request->operation == ANKUS_SPI_KEEP_PLAN;
                            if (request->operation == ANKUS_SPI_EXECUTE || request->operation == ANKUS_SPI_EXECUTE_PLAN ||
                                request->operation == ANKUS_SPI_FETCH_CURSOR || request->operation == ANKUS_SPI_EXPLAIN)
                            {
                                if (SPI_processed > PG_INT64_MAX)
                                {
                                    ereport(ERROR, (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE), errmsg("SPI row count exceeds Int64")));
                                }

                                result->processed = (int64) SPI_processed;
                                if (request->result_mode)
                                {
                                    ankus_collect_result(result, request->result_mode == 1 ? 0 : request->result_mode - 1,
                                        request->result_context, request->result_generation);
                                }

                                SPI_freetuptable(SPI_tuptable);
                            }
                        }

                    #if PG_VERSION_NUM >= 160000
                        ankus_soft_input = NULL;
                    #endif
                        if (operation_context != NULL)
                        {
                            MemoryContextSwitchTo(CurTransactionContext);
                            MemoryContextDelete((MemoryContext) operation_context);
                            operation_context = NULL;
                        }

                        if ((standalone && !direct) || request->operation == ANKUS_SPI_CLOSE_SESSION)
                        {
                            code = SPI_finish();
                            if (code != SPI_OK_FINISH)
                            {
                                ereport(ERROR, (errmsg("SPI_finish failed: %s", SPI_result_code_string(code))));
                            }

                            if (request->operation == ANKUS_SPI_CLOSE_SESSION)
                            {
                                request->session_id = 0;
                            }
                        }

                        if (recovery_subtransaction)
                        {
                            ankus_release_internal_subtransaction();
                            if (request->operation == ANKUS_SPI_CLOSE_SESSION)
                            {
                                ankus_release_internal_subtransaction();
                            }
                        }
                    }

                    MemoryContextSwitchTo(caller_identity != 0 && ankus_memory_context_by_id(caller_identity) == NULL
                        ? recovery_context : caller_context);
                    if (request->operation != ANKUS_SPI_OPEN_SESSION)
                    {
                        CurrentResourceOwner = caller_owner;
                    }

                    if (relation)
                        ankus_relation_finish(request, result);
                }
                ANKUS_PG_CATCH_INNER();
                {
                    ErrorData *data;
                    MemoryContext diagnostic_context;
                    MemoryContextSwitchTo(recovery_context);
                    diagnostic_context = AllocSetContextCreate(TopMemoryContext, "Ankus error diagnostics", ALLOCSET_SMALL_SIZES);
                    MemoryContextSwitchTo(diagnostic_context);
                    data = ankus_copy_error_data();
                    FlushErrorState();
                    bool recovered = false;
                #if PG_VERSION_NUM >= 160000
                    /* A soft input failure was raised after its input function returned, so there
                     * is nothing to roll back. */
                    ankus_soft_input = NULL;
                    recovered = ankus_soft_input_raised;
                    ankus_soft_input_raised = false;
                #endif
                    if (report_started)
                        recovered = true;
                    /* Such a failure left no state behind, so it stays recovered even where
                     * subtransactions are unavailable, as in a parallel worker before 17. */
                    bool clean = recovered;
                    while (GetCurrentTransactionNestLevel() > caller_nest_level)
                    {
                        RollbackAndReleaseCurrentSubTransaction();
                        recovered = true;
                    }

                    ankus_trim_internal_subtransactions();

                    MemoryContextSwitchTo(diagnostic_context);
                    CurrentResourceOwner = caller_owner;
                    if (lightweight && operation_context != NULL)
                    {
                        MemoryContextDelete((MemoryContext) operation_context);
                        operation_context = NULL;
                    }

                    if (relation && result->cursor_id != 0)
                    {
                        ankus_relation_close(result->cursor_id);
                        result->cursor_id = 0;
                    }

                    if ((request->operation == ANKUS_SPI_PREPARE || retained_plan) && request->plan != NULL)
                    {
                        if (request->session_id != 0)
                        {
                            ankus_detach_session_plan(request->plan);
                        }

                        SPI_freeplan(request->plan);
                        request->plan = NULL;
                    }

                    ankus_capture_error(data, error);
                    if (!clean)
                        ankus_recovery_record(error, recovered);
                    /* A failure rolled back by this operation's own subtransaction, such as a
                     * nested recoverable scope, does not fail the enclosing direct frame. */
                    if (transaction_direct_spi && !recovered)
                    {
                        ankus_release_error(&transaction_frame->failure);
                        memset(&transaction_frame->failure, 0, sizeof(transaction_frame->failure));
                        ankus_capture_error(data, &transaction_frame->failure);
                        transaction_frame->failed = true;
                    }

                    ankus_free_error_data(data);
                    MemoryContextSwitchTo(recovery_context);
                    MemoryContextDelete(diagnostic_context);
                    ankus_release_result(result);
                    status = 1;
                }
                ANKUS_PG_END_TRY_INNER();
            }
            PG_CATCH();
            {
                /* An unrecoverable recovery error cannot safely return to managed extension code. */
                MemoryContextSwitchTo(recovery_context);
                FlushErrorState();
                ereport(FATAL, (errmsg("Unable to recover PostgreSQL state after a guarded SPI failure")));
            }
            PG_END_TRY();
            if (status != 0)
                ankus_pending_error_restore(pending);
            else
                ankus_pending_error_release(pending);
            InterruptHoldoffCount = ankus_shared_restore_interrupts(interrupt_holdoff, shared_held_before);
            QueryCancelHoldoffCount = cancel_holdoff;
            return status;
        }

        /* Managed error contexts apply to everything a backend call does. */
        static int
        ankus_spi_execute(AnkusRequest *request, AnkusResult *result, AnkusError *error)
        {
            ErrorContextCallback managed_context;
            bool context_entered = ankus_error_context_enter(&managed_context);
            int status = ankus_spi_execute_core(request, result, error);
            ankus_error_context_exit(&managed_context, context_entered);
            return status;
        }

        """;
}
