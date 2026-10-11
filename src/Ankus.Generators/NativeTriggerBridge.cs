namespace Ankus.Generators;

/// <summary>
/// Adapts PostgreSQL trigger callbacks to owned managed rows and confines native resources to the callback.
/// </summary>
internal static class NativeTriggerBridge
{
    /// <summary>
    /// Gets trigger scope state and transition registration used by SPI connections and cursors.
    /// </summary>
    internal const string Declarations = """
        #include "access/sysattr.h"
        #include "commands/trigger.h"
        #include "nodes/bitmapset.h"
        #include "utils/rel.h"
        #include "utils/memutils.h"

        typedef struct AnkusTriggerScope
        {
            TriggerData *data;
            MemoryContext context;
            int64 identity;
        } AnkusTriggerScope;

        static AnkusTriggerScope *ankus_trigger_scope = NULL;

        static void
        ankus_register_trigger_data(void)
        {
            if (ankus_trigger_scope != NULL)
            {
                MemoryContext previous = MemoryContextSwitchTo(ankus_trigger_scope->context);
                int code;
                /* SPI keeps its query environment in the current context. Portals retain that
                 * environment, so it must survive SPI_finish until the trigger exits. */
                PG_TRY();
                {
                    code = SPI_register_trigger_data(ankus_trigger_scope->data);
                    if (code != SPI_OK_TD_REGISTER)
                        ereport(ERROR, (errmsg("SPI_register_trigger_data failed: %s", SPI_result_code_string(code))));
                }
                PG_FINALLY();
                {
                    MemoryContextSwitchTo(previous);
                }
                PG_END_TRY();
            }
        }

        """;

    /// <summary>
    /// Gets row and metadata transport, HeapTuple reconstruction, and guarded trigger entry points.
    /// </summary>
    internal const string Source = """
        typedef int (*AnkusTriggerCallback)(const AnkusValue *, AnkusValue *, AnkusError *, AnkusExecute, AnkusMemoryApi *);

        static int64 ankus_next_trigger_id = 0;

        static void
        ankus_close_trigger_cursors(int64 identity)
        {
            for (;;)
            {
                AnkusCursorEntry *entry;
                for (entry = ankus_cursors; entry != NULL; entry = entry->next)
                {
                    if (entry->trigger_identity == identity)
                        break;
                }

                if (entry == NULL)
                    break;
                SPI_cursor_close(entry->portal);
                /* Closing can run nested iterator cleanup and unlink multiple entries. */
            }
        }

        static void
        ankus_trigger_text(const char *text, AnkusValue *value)
        {
            value->is_null = text == NULL;
            if (text != NULL)
            {
                char *utf8 = pg_server_to_any(text, strlen(text), PG_UTF8);
                value->data = (unsigned char *) utf8;
                value->length = strlen(utf8);
            }
        }

        static bool
        ankus_trigger_unavailable(Form_pg_attribute attribute, TriggerEvent event, bool is_new)
        {
            /* Stored NEW generated columns have not been computed in BEFORE triggers.
             * Virtual columns (PG18+) never have a physical value in trigger rows. */
            return !attribute->attisdropped && (attribute->attgenerated == 'v' ||
                (attribute->attgenerated == 's' && is_new && TRIGGER_FIRED_BEFORE(event)));
        }

        static void
        ankus_trigger_tuple(HeapTuple tuple, TriggerData *trigger, bool is_new, AnkusValue *value)
        {
            TupleDesc descriptor = RelationGetDescr(trigger->tg_relation);
            Datum *values;
            bool *nulls;
            bool *unavailable;
            AnkusInputBuffer owned = {0};
            value->is_null = tuple == NULL;
            if (tuple == NULL)
                return;
            values = palloc0(sizeof(Datum) * Max(descriptor->natts, 1));
            nulls = palloc(sizeof(bool) * Max(descriptor->natts, 1));
            unavailable = palloc0(sizeof(bool) * Max(descriptor->natts, 1));
            heap_deform_tuple(tuple, descriptor, values, nulls);
            for (int index = 0; index < descriptor->natts; index++)
                unavailable[index] = ankus_trigger_unavailable(TupleDescAttr(descriptor, index), trigger->tg_event, is_new);
            ankus_tuple_transport_available(descriptor, descriptor->tdtypeid, values, nulls, unavailable, value, &owned);
            /* The callback context owns the complete serialized transport. */
            pfree(values);
            pfree(nulls);
            pfree(unavailable);
        }

        /* The columns named by UPDATE OF and the columns an UPDATE assigns, as zero-based
         * ordinals. PostgreSQL reports only the second for UPDATE events. */
        static void
        ankus_trigger_columns(TriggerData *trigger, AnkusValue *named, AnkusValue *updated, AnkusInputBuffer *owned)
        {
            Trigger *definition = trigger->tg_trigger;
            Datum *values = palloc(sizeof(Datum) * Max(definition->tgnattr, 1));
            int count = 0;
            int member = -1;
            for (int index = 0; index < definition->tgnattr; index++)
                values[index] = Int32GetDatum(definition->tgattr[index] - 1);
            ankus_read_array(PointerGetDatum(construct_array(values, definition->tgnattr, INT4OID, sizeof(int32), true, TYPALIGN_INT)),
                named, owned);
            updated->is_null = !TRIGGER_FIRED_BY_UPDATE(trigger->tg_event);
            if (updated->is_null)
                return;
            values = palloc(sizeof(Datum) * Max(bms_num_members(trigger->tg_updatedcols), 1));
            while ((member = bms_next_member(trigger->tg_updatedcols, member)) >= 0)
            {
                AttrNumber attribute = member + FirstLowInvalidHeapAttributeNumber;
                if (attribute > 0)
                    values[count++] = Int32GetDatum(attribute - 1);
            }

            ankus_read_array(PointerGetDatum(construct_array(values, count, INT4OID, sizeof(int32), true, TYPALIGN_INT)),
                updated, owned);
        }

        static HeapTuple
        ankus_trigger_result(const AnkusValue *value, TriggerData *trigger)
        {
            TupleDesc descriptor = RelationGetDescr(trigger->tg_relation);
            Oid type;
            int32 modifier;
            int count;
            AnkusTupleField *fields = ankus_tuple_fields(value, &type, &modifier, &count);
            Datum *values;
            bool *nulls;
            bool *replace;
            HeapTuple original = TRIGGER_FIRED_BY_UPDATE(trigger->tg_event) ? trigger->tg_newtuple : trigger->tg_trigtuple;
            if (type != descriptor->tdtypeid || modifier != descriptor->tdtypmod || count != descriptor->natts)
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Trigger result must match the triggering relation's row type")));
            values = palloc0(sizeof(Datum) * Max(count, 1));
            nulls = palloc0(sizeof(bool) * Max(count, 1));
            replace = palloc0(sizeof(bool) * Max(count, 1));
            for (int index = 0; index < count; index++)
            {
                Form_pg_attribute attribute = TupleDescAttr(descriptor, index);
                AnkusTupleField *field = &fields[index];
                AnkusParameter parameter = {0};
                const char *name = NameStr(attribute->attname);
                char *utf8 = pg_server_to_any(name, strlen(name), PG_UTF8);
                bool same_name = strlen(utf8) == (Size) field->name_length && memcmp(utf8, field->name, field->name_length) == 0;
                if (attribute->attisdropped != ((field->flags & 1) != 0) ||
                    (!attribute->attisdropped && (!same_name || attribute->atttypid != field->type ||
                        getBaseType(attribute->atttypid) != field->base_type || attribute->atttypmod != field->modifier ||
                        attribute->attcollation != field->collation)))
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Trigger result attribute %d does not match the relation", index + 1)));
                if (utf8 != name)
                    pfree(utf8);
                if (attribute->attisdropped || ankus_trigger_unavailable(attribute, trigger->tg_event, true))
                    continue;
                if (field->flags & 8)
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE), errmsg("Trigger result contains an unavailable ordinary attribute")));
                replace[index] = true;
                nulls[index] = field->value.is_null != 0;
                parameter.type_oid = attribute->atttypid;
                parameter.value = field->value;
                if (!nulls[index] && (field->base_type == JSONOID || field->base_type == JSONBOID))
                    parameter.value.data = (unsigned char *) pnstrdup((char *) parameter.value.data, parameter.value.length);
                values[index] = ankus_parameter_datum(&parameter);
                if (attribute->atttypmod >= 0)
                    values[index] = ankus_coerce_value(values[index], &nulls[index], attribute->atttypid,
                        attribute->atttypid, attribute->atttypmod, attribute->attcollation);
            }

            /* Preserve PostgreSQL tuple identification and generated slots from the original NEW.
             * PostgreSQL recomputes stored generated values after BEFORE callbacks finish. */
            return heap_modify_tuple(original, descriptor, values, nulls, replace);
        }

        static Datum
        ankus_trigger_call(FunctionCallInfo fcinfo, AnkusTriggerCallback callback)
        {
            TriggerData *trigger;
            AnkusTriggerScope scope;
            AnkusTriggerScope *previous_scope = ankus_trigger_scope;
            Oid previous_function = ankus_function_oid;
            MemoryContext caller = CurrentMemoryContext;
            AnkusValue arguments[15] = {0};
            AnkusValue *result;
            AnkusError *error;
            volatile HeapTuple output = NULL;
            if (!CALLED_AS_TRIGGER(fcinfo))
                ereport(ERROR, (errcode(ERRCODE_E_R_I_E_TRIGGER_PROTOCOL_VIOLATED), errmsg("Ankus trigger functions can only be called by a trigger")));
            if (PG_NARGS() != 0)
                ereport(ERROR, (errcode(ERRCODE_E_R_I_E_TRIGGER_PROTOCOL_VIOLATED), errmsg("Ankus trigger functions do not accept SQL arguments")));
            trigger = (TriggerData *) fcinfo->context;
            if (ankus_next_trigger_id == PG_INT64_MAX)
                ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("Ankus trigger identity capacity exhausted")));
            scope.data = trigger;
            scope.context = AllocSetContextCreate(caller, "Ankus trigger callback", ALLOCSET_SMALL_SIZES);
            scope.identity = ++ankus_next_trigger_id;
            /* These mutable headers are read during cleanup after ERROR/longjmp. */
            result = MemoryContextAllocZero(scope.context, sizeof(AnkusValue));
            error = MemoryContextAllocZero(scope.context, sizeof(AnkusError));
            ankus_trigger_scope = &scope;
            ankus_function_oid = fcinfo->flinfo->fn_oid;
            AnkusMemoryApi memory = {0};
            PG_TRY();
            {
                AnkusInputBuffer owned = {0};
                Datum *extra;
                ArrayType *array;
                int status;
                MemoryContextSwitchTo(scope.context);
                ankus_memory_initialize(&memory);
                arguments[0].integral = trigger->tg_event;
                arguments[1].integral = RelationGetRelid(trigger->tg_relation);
                arguments[2].integral = trigger->tg_trigger->tgoid;
                ankus_trigger_text(trigger->tg_trigger->tgname, &arguments[3]);
                ankus_trigger_text(RelationGetRelationName(trigger->tg_relation), &arguments[4]);
                ankus_trigger_text(get_namespace_name(RelationGetNamespace(trigger->tg_relation)), &arguments[5]);
                ankus_trigger_text(trigger->tg_trigger->tgoldtable, &arguments[6]);
                ankus_trigger_text(trigger->tg_trigger->tgnewtable, &arguments[7]);
                extra = palloc(sizeof(Datum) * Max(trigger->tg_trigger->tgnargs, 1));
                for (int index = 0; index < trigger->tg_trigger->tgnargs; index++)
                    extra[index] = CStringGetTextDatum(trigger->tg_trigger->tgargs[index]);
                array = construct_array(extra, trigger->tg_trigger->tgnargs, TEXTOID, -1, false, TYPALIGN_INT);
                ankus_read_array(PointerGetDatum(array), &arguments[8], &owned);
                arguments[9].is_null = arguments[10].is_null = true;
                if (TRIGGER_FIRED_FOR_ROW(trigger->tg_event))
                {
                    if (TRIGGER_FIRED_BY_UPDATE(trigger->tg_event) || TRIGGER_FIRED_BY_DELETE(trigger->tg_event))
                        ankus_trigger_tuple(trigger->tg_trigtuple, trigger, false, &arguments[9]);
                    if (TRIGGER_FIRED_BY_INSERT(trigger->tg_event) || TRIGGER_FIRED_BY_UPDATE(trigger->tg_event))
                        ankus_trigger_tuple(TRIGGER_FIRED_BY_INSERT(trigger->tg_event) ? trigger->tg_trigtuple : trigger->tg_newtuple,
                            trigger, true, &arguments[10]);
                }

                ankus_tuple_transport(RelationGetDescr(trigger->tg_relation), RelationGetDescr(trigger->tg_relation)->tdtypeid,
                    NULL, NULL, &arguments[11], &owned);
                ankus_trigger_columns(trigger, &arguments[12], &arguments[13], &owned);
                arguments[14].integral = trigger->tg_trigger->tgconstraint;
                arguments[14].auxiliary1 = (trigger->tg_trigger->tgdeferrable ? 1 : 0) | (trigger->tg_trigger->tginitdeferred ? 2 : 0);
                ANKUS_MANAGED_INVOKE(status, error, callback(arguments, result, error, ankus_spi_execute, &memory));
                if (status != 0)
                    ankus_raise_error(error);
                if (!TRIGGER_FIRED_AFTER(trigger->tg_event) && !result->is_null)
                {
                    if (TRIGGER_FIRED_FOR_STATEMENT(trigger->tg_event))
                        ereport(ERROR, (errcode(ERRCODE_E_R_I_E_TRIGGER_PROTOCOL_VIOLATED), errmsg("BEFORE STATEMENT trigger cannot return a value")));
                    if (TRIGGER_FIRED_BY_DELETE(trigger->tg_event))
                        output = trigger->tg_trigtuple;
                    else
                    {
                        HeapTuple tuple = ankus_trigger_result(result, trigger);
                        MemoryContextSwitchTo(caller);
                        output = heap_copytuple(tuple);
                    }
                }
            }
            PG_FINALLY();
            {
                MemoryContextSwitchTo(caller);
                if (result->release != NULL)
                    result->release(result->data);
                ankus_release_error(error);
                PG_TRY();
                {
                    /* Iterator disposal can still query this invocation's transition tables.
                     * If it raises, keep the environment under the caller for abort cleanup. */
                    ankus_close_trigger_cursors(scope.identity);
                    MemoryContextDelete(scope.context);
                }
                PG_FINALLY();
                {
                    ankus_trigger_scope = previous_scope;
                    ankus_function_oid = previous_function;
                    MemoryContextSwitchTo(caller);
                }
                PG_END_TRY();
            }
            PG_END_TRY();
            /* A NULL HeapTuple pointer means skip, never SQL NULL. */
            fcinfo->isnull = false;
            return PointerGetDatum(output);
        }

        """;
}
