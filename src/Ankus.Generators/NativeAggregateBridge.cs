namespace Ankus.Generators;

/// <summary>
/// Owns aggregate callback state, native ordering, and managed roots across executor memory lifetimes.
/// </summary>
internal static class NativeAggregateBridge
{
    /// <summary>
    /// Gets guarded aggregate dispatch, state registration, and PostgreSQL sort support.
    /// </summary>
    internal const string Source = """
        #include "executor/nodeAgg.h"
        #include "nodes/nodeFuncs.h"
        #include "utils/sortsupport.h"

        typedef int (*AnkusAggregateCallback)(const AnkusValue *, AnkusValue *, AnkusError *, AnkusExecute,
            const AnkusValue *, int, void *, void *, AnkusMemoryApi *);
        typedef int (*AnkusAggregateRelease)(void *, AnkusError *, AnkusExecute, AnkusMemoryApi *);

        typedef struct AnkusAggregateState
        {
            MemoryContextCallback reset;
            MemoryContext cleanup_owner;
            void *handle;
            AnkusAggregateRelease release;
        } AnkusAggregateState;

        typedef struct AnkusAggregateSortKey
        {
            int argument;
            Oid type;
            Oid ordering;
            Oid collation;
            bool nulls_first;
        } AnkusAggregateSortKey;

        typedef struct AnkusAggregateScope
        {
            MemoryContext owner;
            MemoryContext lifetime;
            AnkusAggregateSortKey *keys;
            int key_count;
        } AnkusAggregateScope;

        static AnkusAggregateScope *ankus_aggregate_scope;

        static void
        ankus_aggregate_release(void *argument)
        {
            AnkusAggregateState *state = argument;
            AnkusAggregateScope *previous = ankus_aggregate_scope;
            AnkusError error = {0};
            AnkusMemoryApi memory = {0};
            AnkusMemoryProtection protection = {0};
            AnkusAggregateRelease release = state->release;
            void *handle = state->handle;
            int status;
            if (handle == NULL)
                return;
            /* Invalidate before entering user cleanup. Reset callbacks can run on abort,
             * window restart, group rescan, or deletion of a temporary deserialized state. */
            state->handle = NULL;

            ankus_aggregate_scope = NULL;
            ankus_memory_initialize(&memory);
            ankus_memory_protect(&protection, state->cleanup_owner, true);
            PG_TRY();
            {
                ANKUS_MANAGED_INVOKE(status, &error, release(handle, &error, ankus_spi_execute, &memory));
            }
            PG_FINALLY();
            {
                ankus_memory_protection = protection.previous;
                ankus_aggregate_scope = previous;
                pfree(state);
            }
            PG_END_TRY();
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
                ereport(WARNING, (errmsg("Ankus aggregate state cleanup failed")));
        }

        static int
        ankus_aggregate_compare(const AnkusParameter *values, int key_index)
        {
            AnkusAggregateSortKey *key;
            SortSupportData support = {0};
            Datum arguments[2] = {0};
            bool nulls[2];
            if (values == NULL || key_index < 0 || key_index >= ankus_aggregate_scope->key_count)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Invalid aggregate sort key")));
            key = &ankus_aggregate_scope->keys[key_index];
            for (int index = 0; index < 2; index++)
            {
                Oid source = getBaseType(values[index].type_oid);
                Oid target = getBaseType(key->type);
                bool text = source == TEXTOID && (target == VARCHAROID || target == BPCHAROID);
                if (source != target && !text && !IsBinaryCoercible(source, target))
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH),
                        errmsg("Aggregate comparison values do not match the sort key type")));
                nulls[index] = values[index].value.is_null;
                if (!nulls[index])
                {
                    arguments[index] = ankus_parameter_datum(&values[index]);
                    if (values[index].type_oid != target)
                        arguments[index] = ankus_coerce_value(arguments[index], &nulls[index],
                            values[index].type_oid, target, -1, key->collation);
                }
            }

            support.ssup_cxt = CurrentMemoryContext;
            support.ssup_collation = key->collation;
            support.ssup_nulls_first = key->nulls_first;
            PrepareSortSupportFromOrderingOp(key->ordering, &support);
            return ApplySortComparator(arguments[0], nulls[0], arguments[1], nulls[1], &support);
        }

        static int
        ankus_aggregate_api(int operation, void *handle, void *release, void **output,
            const AnkusParameter *values, int sort_key, AnkusError *error)
        {
            MemoryContext caller = CurrentMemoryContext;
            ResourceOwner resource_owner = CurrentResourceOwner;
            int nesting = GetCurrentTransactionNestLevel();
            volatile int status = 0;
            AnkusAggregateState *volatile pending_state = NULL;
            *output = NULL;
            if (ankus_recovery_failed(error))
                return 1;

            /* Both the operation and diagnostic recovery have native guards. Neither
             * allocation failures nor a user comparison ERROR may cross managed frames. */
            AnkusPendingError *volatile pending = ankus_pending_error_save();
            PG_TRY();
            {
                PG_TRY();
                {
                    if (ankus_aggregate_scope == NULL)
                        ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                            errmsg("No active Ankus aggregate callback")));
                    if (operation == 0)
                    {
                        AnkusAggregateState *state;
                        if (handle == NULL || release == NULL)
                            ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                                errmsg("Aggregate state registration requires a managed owner")));

                        state = MemoryContextAllocZero(TopMemoryContext, sizeof(AnkusAggregateState));
                        state->cleanup_owner = ankus_aggregate_scope->lifetime;
                        state->handle = handle;
                        state->release = (AnkusAggregateRelease) release;
                        state->reset.func = ankus_aggregate_release;
                        state->reset.arg = state;
                        pending_state = state;
                        MemoryContextRegisterResetCallback(state->cleanup_owner, &state->reset);

                        *output = handle;
                        pending_state = NULL;
                    }
                    else if (operation == 1)
                    {
                        MemoryContext temporary;
                        int comparison;
                        if (!ankus_parallel_without_subtransactions())
                            ankus_begin_internal_subtransaction();
                        temporary = AllocSetContextCreate(CurrentMemoryContext, "Ankus aggregate comparison", ALLOCSET_SMALL_SIZES);
                        MemoryContextSwitchTo(temporary);
                        comparison = ankus_aggregate_compare(values, sort_key);
                        MemoryContextSwitchTo(caller);
                        MemoryContextDelete(temporary);
                        if (!ankus_parallel_without_subtransactions())
                            ankus_release_internal_subtransaction();
                        MemoryContextSwitchTo(caller);
                        CurrentResourceOwner = resource_owner;
                        *output = (void *) (intptr_t) ((comparison > 0) - (comparison < 0));
                    }
                    else if (operation == 2)
                    {
                        if (handle != ankus_aggregate_scope->owner)
                            ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                                errmsg("Aggregate memory context does not match the active callback")));
                        *output = (void *) (uintptr_t) ankus_memory_context_id(ankus_aggregate_scope->owner);
                    }
                    else
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Invalid aggregate operation")));
                }
                PG_CATCH();
                {
                    ErrorData *data;
                    MemoryContext diagnostics;
                    if (pending_state != NULL)
                    {
                        pending_state->handle = NULL;
                        pfree((void *) pending_state);
                    }

                    MemoryContextSwitchTo(caller);
                    diagnostics = AllocSetContextCreate(caller, "Ankus aggregate diagnostics", ALLOCSET_SMALL_SIZES);
                    MemoryContextSwitchTo(diagnostics);
                    data = ankus_copy_error_data();
                    FlushErrorState();
                    bool recovered = false;
                    while (GetCurrentTransactionNestLevel() > nesting)
                    {
                        RollbackAndReleaseCurrentSubTransaction();
                        recovered = true;
                    }

                    ankus_trim_internal_subtransactions();

                    MemoryContextSwitchTo(diagnostics);
                    CurrentResourceOwner = resource_owner;
                    ankus_capture_error(data, error);
                    ankus_recovery_record(error, recovered);
                    ankus_free_error_data(data);
                    MemoryContextSwitchTo(caller);
                    MemoryContextDelete(diagnostics);
                    *output = NULL;
                    status = 1;
                }
                PG_END_TRY();
            }
            PG_CATCH();
            {
                MemoryContextSwitchTo(caller);
                FlushErrorState();
                ereport(FATAL, (errmsg("Unable to recover PostgreSQL state after a guarded aggregate failure")));
            }
            PG_END_TRY();
            if (status != 0)
                ankus_pending_error_restore(pending);
            else
                ankus_pending_error_release(pending);
            return status;
        }

        /* What an aggregate support call needs that is fixed for its call site: the catalog
         * signature, the resolved result type, the aggregate's metadata, a scratch context and
         * the call's buffers. PostgreSQL leaves fn_extra to the function, so later rows reuse
         * them and allocate nothing. */
        typedef struct AnkusAggregateSite
        {
            Oid *types;
            int parameter_count;
            Oid result_type;
            Oid result_base_type;
            AnkusValue *metadata;
            int metadata_count;
            AnkusAggregateSortKey *keys;
            int key_count;
            MemoryContext scratch;
            bool busy;
            AnkusAggregateScope scope;
            AnkusValue *arguments;
            AnkusInputBuffer *owned;
            AnkusValue result;
            AnkusError error;
        } AnkusAggregateSite;

        static AnkusValue *
        ankus_aggregate_metadata(FunctionCallInfo fcinfo, int kind, AnkusAggregateSite *site, int *count)
        {
            Aggref *reference = AggGetAggref(fcinfo);
            AnkusValue *metadata;
            ListCell *cell;
            int index = 0;
            site->key_count = reference == NULL ? 0 : list_length(reference->aggorder);
            site->keys = palloc0(sizeof(AnkusAggregateSortKey) * Max(1, site->key_count));
            *count = 4 + 5 * site->key_count;
            metadata = palloc0(sizeof(AnkusValue) * *count);
            metadata[0].integral = kind == AGG_CONTEXT_AGGREGATE ? 1 : 2;
            metadata[1].integral = AggStateIsShared(fcinfo);
            metadata[2].integral = PG_GET_COLLATION();
            metadata[3].integral = reference == NULL ? InvalidOid : reference->aggfnoid;
            if (reference == NULL)
                return metadata;
            foreach(cell, reference->aggorder)
            {
                SortGroupClause *clause = lfirst_node(SortGroupClause, cell);
                ListCell *argument;
                TargetEntry *entry = NULL;
                AnkusAggregateSortKey *key = &site->keys[index];
                foreach(argument, reference->args)
                {
                    TargetEntry *candidate = lfirst_node(TargetEntry, argument);
                    if (candidate->ressortgroupref == clause->tleSortGroupRef)
                    {
                        entry = candidate;
                        break;
                    }
                }

                if (entry == NULL)
                    ereport(ERROR, (errmsg("Aggregate sort key has no input expression")));
                key->argument = entry->resno - 1;
                key->type = exprType((Node *) entry->expr);
                key->ordering = clause->sortop;
                key->collation = exprCollation((Node *) entry->expr);
                key->nulls_first = clause->nulls_first;
                metadata[4 + 5 * index].integral = key->argument;
                metadata[5 + 5 * index].integral = key->type;
                metadata[6 + 5 * index].integral = key->ordering;
                metadata[7 + 5 * index].integral = key->collation;
                metadata[8 + 5 * index].integral = key->nulls_first;
                index++;
            }

            return metadata;
        }

        static AnkusAggregateSite *
        ankus_aggregate_site(FunctionCallInfo fcinfo, int kind, int arguments_count)
        {
            AnkusAggregateSite *site = fcinfo->flinfo->fn_extra;
            MemoryContext previous;
            if (site != NULL)
                return site;
            previous = MemoryContextSwitchTo(fcinfo->flinfo->fn_mcxt);
            site = palloc0(sizeof(AnkusAggregateSite));
            site->result_type = get_func_signature(fcinfo->flinfo->fn_oid, &site->types, &site->parameter_count);
            if (site->parameter_count != arguments_count)
                ereport(ERROR, (errmsg("Aggregate support catalog signature does not match generated code")));
            if (IsPolymorphicType(site->result_type))
            {
                Oid resolved = get_fn_expr_rettype(fcinfo->flinfo);
                if (OidIsValid(resolved))
                    site->result_type = resolved;
                if (!OidIsValid(site->result_type) || IsPolymorphicType(site->result_type))
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Aggregate result type was not resolved")));
            }

            /* The result type is fixed for the call site, so each row skips the catalog lookups. */
            site->result_base_type = get_typtype(site->result_type) == '\0' ? InvalidOid : getBaseType(site->result_type);

            site->metadata = ankus_aggregate_metadata(fcinfo, kind, site, &site->metadata_count);
            site->arguments = palloc0(sizeof(AnkusValue) * Max(arguments_count, 1));
            site->owned = palloc0(sizeof(AnkusInputBuffer) * Max(arguments_count, 1));
            site->scratch = AllocSetContextCreate(fcinfo->flinfo->fn_mcxt, "Ankus aggregate callback", ALLOCSET_SMALL_SIZES);
            MemoryContextSwitchTo(previous);
            fcinfo->flinfo->fn_extra = site;
            return site;
        }

        static Datum
        ankus_aggregate_call(FunctionCallInfo fcinfo, AnkusAggregateCallback callback, int arguments_count,
            const bool *required, const bool *internal_arguments, bool internal_result, bool deserialize,
            const bool *polymorphic, bool polymorphic_result)
        {
            MemoryContext owner;
            int kind = AggCheckCallContext(fcinfo, &owner);
            MemoryContext caller = CurrentMemoryContext;
            MemoryContext temporary;
            AnkusAggregateSite *site;
            bool reuse;
            AnkusAggregateScope *scope;
            AnkusAggregateScope *previous = ankus_aggregate_scope;
            Oid previous_function = ankus_function_oid;
            AnkusValue *arguments;
            AnkusInputBuffer *owned;
            AnkusValue *result;
            AnkusError *error;
            AnkusMemoryProtection protection = {0};
            volatile Datum datum = (Datum) 0;
            if (kind != AGG_CONTEXT_AGGREGATE && kind != AGG_CONTEXT_WINDOW)
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("Ankus aggregate support functions require an aggregate invocation")));
            if (PG_NARGS() != arguments_count)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Incorrect aggregate support argument count")));
            for (int index = 0; index < arguments_count; index++)
                if (required[index] && PG_ARGISNULL(index))
                    PG_RETURN_NULL();
            site = ankus_aggregate_site(fcinfo, kind, arguments_count);
            /* A support function reached again from inside its own call uses a private context. */
            reuse = !site->busy;
            temporary = reuse ? site->scratch : AllocSetContextCreate(caller, "Ankus aggregate callback", ALLOCSET_SMALL_SIZES);
            site->busy = true;
            if (reuse)
            {
                scope = &site->scope;
                arguments = site->arguments;
                owned = site->owned;
                result = &site->result;
                error = &site->error;
                memset(scope, 0, sizeof(*scope));
                memset(arguments, 0, sizeof(AnkusValue) * Max(arguments_count, 1));
                memset(owned, 0, sizeof(AnkusInputBuffer) * Max(arguments_count, 1));
                memset(result, 0, sizeof(*result));
            }
            else
            {
                scope = MemoryContextAllocZero(temporary, sizeof(AnkusAggregateScope));
                arguments = MemoryContextAllocZero(temporary, sizeof(AnkusValue) * Max(arguments_count, 1));
                owned = MemoryContextAllocZero(temporary, sizeof(AnkusInputBuffer) * Max(arguments_count, 1));
                result = MemoryContextAllocZero(temporary, sizeof(AnkusValue));
                error = MemoryContextAlloc(temporary, sizeof(AnkusError));
            }

            ankus_error_clear(error);
            scope->owner = deserialize ? caller : owner;
            scope->lifetime = kind == AGG_CONTEXT_AGGREGATE ?
                ((AggState *) fcinfo->context)->ss.ps.state->es_query_cxt : scope->owner;
            scope->keys = site->keys;
            scope->key_count = site->key_count;
            ankus_aggregate_scope = scope;
            ankus_function_oid = fcinfo->flinfo->fn_oid;
            ankus_memory_protect(&protection, scope->owner, false);
            PG_TRY();
            {
                Oid *types = site->types;
                Oid result_type = site->result_type;
                int metadata_count = site->metadata_count;
                AnkusValue *metadata = site->metadata;
                int status;
                MemoryContextSwitchTo(temporary);
                for (int index = 0; index < arguments_count; index++)
                {
                    /* PostgreSQL supplies a non-NULL SQL internal dummy with Datum zero.
                     * It is not a state and has no corresponding managed argument. */
                    if (deserialize && index == arguments_count - 1)
                        continue;
                    arguments[index].is_null = PG_ARGISNULL(index);
                    if (polymorphic[index])
                        ankus_read_polymorphic(fcinfo, index, &arguments[index]);
                    else if (!arguments[index].is_null)
                    {
                        if (internal_arguments[index])
                            arguments[index].integral = (intptr_t) DatumGetPointer(PG_GETARG_DATUM(index));
                        else
                            ankus_read_value(PG_GETARG_DATUM(index), types[index], &arguments[index], &owned[index]);
                    }
                }

                AnkusMemoryApi memory = {0};
                ankus_memory_initialize(&memory);
                memory.result_context = scope->owner;
                ANKUS_MANAGED_INVOKE(status, error, callback(arguments, result, error, ankus_spi_execute, metadata, metadata_count,
                    scope->owner, (void *) ankus_aggregate_api, &memory));
                if (status != 0)
                    ankus_raise_error(error);
                fcinfo->isnull = result->is_null;
                MemoryContextSwitchTo(caller);
                if (!result->is_null || polymorphic_result)
                {
                    if (internal_result)
                    {
                        if (result->integral == 0)
                            ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                                errmsg("Managed aggregate state returned an invalid identity")));
                        datum = (Datum) (uintptr_t) result->integral;
                    }
                    else
                    {
                        AnkusParameter value = {0};
                        value.type_oid = result_type;
                        value.value = *result;
                        datum = OidIsValid(site->result_base_type)
                            ? ankus_parameter_datum_resolved(&value, site->result_base_type)
                            : ankus_parameter_datum(&value);
                    }
                }
            }
            PG_FINALLY();
            {
                ankus_memory_protection = protection.previous;
                ankus_aggregate_scope = previous;
                ankus_function_oid = previous_function;
                MemoryContextSwitchTo(caller);
                for (int index = 0; index < arguments_count; index++)
                    ankus_free_input(&owned[index]);
                if (result->release != NULL)
                    result->release(result->data);
                ankus_release_error(error);
                if (reuse)
                {
                    MemoryContextReset(temporary);
                    site->busy = false;
                }
                else
                    MemoryContextDelete(temporary);
            }
            PG_END_TRY();
            return datum;
        }

        """;
}
