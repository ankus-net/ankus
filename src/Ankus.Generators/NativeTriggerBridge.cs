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
        /* Never reused in a backend, so managed code can tell a site from a later one at the same address. */
        static int64 ankus_next_trigger_site = 0;

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

        /* Cached names outlive the call, so they never borrow relcache or trigger memory. */
        static void
        ankus_trigger_site_text(const char *text, AnkusValue *value)
        {
            ankus_trigger_text(text, value);
            if (text != NULL && value->data == (const unsigned char *) text)
                value->data = (unsigned char *) pnstrdup(text, value->length);
        }

        static bool
        ankus_trigger_unavailable(Form_pg_attribute attribute, TriggerEvent event, bool is_new)
        {
            /* Stored NEW generated columns have not been computed in BEFORE triggers.
             * Virtual columns (PG18+) never have a physical value in trigger rows. */
            return !attribute->attisdropped && (attribute->attgenerated == 'v' ||
                (attribute->attgenerated == 's' && is_new && TRIGGER_FIRED_BEFORE(event)));
        }

        /* What a trigger call site sends on every call but that cannot change while its statement runs:
         * names, arguments, the relation's columns and their resolved types. PostgreSQL keeps one FmgrInfo
         * per trigger and result relation, so fn_extra holds this for the statement. */
        typedef struct AnkusTriggerSite
        {
            int64 identity;
            Oid trigger;
            Oid relation;
            TupleDesc descriptor;
            MemoryContext context;
            int busy;
            AnkusValue texts[5];
            AnkusValue arguments;
            AnkusValue columns;
            AnkusValue named;
            AnkusTupleShape shape;
        } AnkusTriggerSite;

        static bool
        ankus_trigger_site_matches(const AnkusTriggerSite *site, TriggerData *trigger)
        {
            return site->trigger == trigger->tg_trigger->tgoid && site->relation == RelationGetRelid(trigger->tg_relation) &&
                site->descriptor == RelationGetDescr(trigger->tg_relation);
        }

        /* Builds a site in a new child of parent. On error the partial site is freed with its context. */
        static AnkusTriggerSite *
        ankus_trigger_site_build(TriggerData *trigger, MemoryContext parent)
        {
            Trigger *definition = trigger->tg_trigger;
            TupleDesc descriptor = RelationGetDescr(trigger->tg_relation);
            MemoryContext context = AllocSetContextCreate(parent, "Ankus trigger site", ALLOCSET_SMALL_SIZES);
            MemoryContext previous = MemoryContextSwitchTo(context);
            AnkusTriggerSite *site = NULL;
            PG_TRY();
            {
                AnkusInputBuffer owned = {0};
                Datum *values = palloc(sizeof(Datum) * Max(Max(definition->tgnargs, definition->tgnattr), 1));
                site = palloc0(sizeof(AnkusTriggerSite));
                site->context = context;
                ankus_trigger_site_text(definition->tgname, &site->texts[0]);
                ankus_trigger_site_text(RelationGetRelationName(trigger->tg_relation), &site->texts[1]);
                ankus_trigger_site_text(get_namespace_name(RelationGetNamespace(trigger->tg_relation)), &site->texts[2]);
                ankus_trigger_site_text(definition->tgoldtable, &site->texts[3]);
                ankus_trigger_site_text(definition->tgnewtable, &site->texts[4]);
                for (int index = 0; index < definition->tgnargs; index++)
                    values[index] = CStringGetTextDatum(definition->tgargs[index]);
                ankus_read_array(PointerGetDatum(construct_array(values, definition->tgnargs, TEXTOID, -1, false, TYPALIGN_INT)),
                    &site->arguments, &owned);
                ankus_tuple_transport(descriptor, descriptor->tdtypeid, NULL, NULL, &site->columns, &owned);
                for (int index = 0; index < definition->tgnattr; index++)
                    values[index] = Int32GetDatum(definition->tgattr[index] - 1);
                ankus_read_array(PointerGetDatum(construct_array(values, definition->tgnattr, INT4OID, sizeof(int32), true, TYPALIGN_INT)),
                    &site->named, &owned);
                ankus_tuple_shape_build(descriptor, descriptor->tdtypeid, &site->shape);
                if (ankus_next_trigger_site == PG_INT64_MAX)
                    ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("Ankus trigger site capacity exhausted")));
                site->identity = ++ankus_next_trigger_site;
                site->trigger = definition->tgoid;
                site->relation = RelationGetRelid(trigger->tg_relation);
                site->descriptor = descriptor;
            }
            PG_CATCH();
            {
                MemoryContextSwitchTo(previous);
                MemoryContextDelete(context);
                PG_RE_THROW();
            }
            PG_END_TRY();
            MemoryContextSwitchTo(previous);
            return site;
        }

        /* Returns the call site's cached facts, building them on the first call. A site still in use by an
         * outer call through the same FmgrInfo is never replaced; the nested call builds a private one. */
        static AnkusTriggerSite *
        ankus_trigger_site(FunctionCallInfo fcinfo, TriggerData *trigger, MemoryContext call_context)
        {
            FmgrInfo *function = fcinfo->flinfo;
            AnkusTriggerSite *site = (AnkusTriggerSite *) function->fn_extra;
            if (site != NULL && ankus_trigger_site_matches(site, trigger))
                return site;
            if (site != NULL && site->busy > 0)
                return ankus_trigger_site_build(trigger, call_context);
            if (site != NULL)
            {
                function->fn_extra = NULL;
                MemoryContextDelete(site->context);
            }

            site = ankus_trigger_site_build(trigger, function->fn_mcxt);
            function->fn_extra = site;
            return site;
        }

        /* A trigger row travels as one value per column. Both sides already know the relation's columns from the
         * call site, so no names or types are sent. Cells and the buffers they borrow belong to the callback
         * context. */
        static void
        ankus_trigger_tuple(HeapTuple tuple, TriggerData *trigger, AnkusTupleShape *shape, bool is_new, AnkusValue *value)
        {
            TupleDesc descriptor = RelationGetDescr(trigger->tg_relation);
            Datum *values;
            bool *nulls;
            AnkusValue *cells;
            value->is_null = tuple == NULL;
            if (tuple == NULL)
                return;
            values = palloc(sizeof(Datum) * Max(descriptor->natts, 1));
            nulls = palloc(sizeof(bool) * Max(descriptor->natts, 1));
            cells = palloc0(sizeof(AnkusValue) * Max(descriptor->natts, 1));
            heap_deform_tuple(tuple, descriptor, values, nulls);
            for (int index = 0; index < descriptor->natts; index++)
            {
                Form_pg_attribute attribute = TupleDescAttr(descriptor, index);
                AnkusTupleShapeField *field = &shape->fields[index];
                AnkusInputBuffer owned = {0};
                cells[index].is_null = 1;
                if (ankus_trigger_unavailable(attribute, trigger->tg_event, is_new))
                    cells[index].auxiliary1 = -12;
                else if (!attribute->attisdropped && !nulls[index])
                {
                    if (!field->enum_checked)
                    {
                        ankus_check_result_enum(field->base_type);
                        field->enum_checked = true;
                    }

                    cells[index].is_null = 0;
                    ankus_convert_base_value(values[index], field->base_type, &cells[index], &owned, true);
                }
            }

            pfree(values);
            pfree(nulls);
            value->data = (unsigned char *) cells;
            value->length = descriptor->natts;
            value->auxiliary1 = -10;
            value->integral = shape->declared_base;
        }

        /* The columns an UPDATE assigns, as zero-based ordinals. PostgreSQL reports them only for UPDATE
         * events; the columns named by UPDATE OF belong to the trigger and are cached with its site. */
        static void
        ankus_trigger_columns(TriggerData *trigger, AnkusValue *updated, AnkusInputBuffer *owned)
        {
            Datum *values;
            int count = 0;
            int member = -1;
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

        static Datum
        ankus_trigger_cell(const AnkusValue *cell, Form_pg_attribute attribute, Oid base_type, bool *is_null)
        {
            AnkusParameter parameter = {0};
            Datum datum;
            *is_null = cell->is_null != 0;
            if (*is_null)
                return (Datum) 0;
            parameter.type_oid = attribute->atttypid;
            parameter.value = *cell;
            if (base_type == JSONOID || base_type == JSONBOID)
                parameter.value.data = (unsigned char *) pnstrdup((char *) parameter.value.data, parameter.value.length);
            datum = ankus_parameter_datum_resolved(&parameter, base_type);
            if (attribute->atttypmod >= 0)
                datum = ankus_coerce_value(datum, is_null, attribute->atttypid, attribute->atttypid, attribute->atttypmod,
                    attribute->attcollation);
            return datum;
        }

        /* A row the managed trigger built over the relation's own columns returns one value per column. A cell kept
         * from the NEW row keeps PostgreSQL's original datum, so only replaced cells are converted. */
        static HeapTuple
        ankus_trigger_result_cells(const AnkusValue *value, TriggerData *trigger, const AnkusTupleShape *shape, MemoryContext target)
        {
            TupleDesc descriptor = RelationGetDescr(trigger->tg_relation);
            const AnkusValue *cells = (const AnkusValue *) value->data;
            HeapTuple original = TRIGGER_FIRED_BY_UPDATE(trigger->tg_event) ? trigger->tg_newtuple : trigger->tg_trigtuple;
            Datum *values;
            bool *nulls;
            bool *replace;
            HeapTuple tuple;
            MemoryContext previous;
            if (value->length != descriptor->natts || value->integral != shape->declared_base)
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Trigger result must match the triggering relation's row type")));
            values = palloc0(sizeof(Datum) * Max(descriptor->natts, 1));
            nulls = palloc0(sizeof(bool) * Max(descriptor->natts, 1));
            replace = palloc0(sizeof(bool) * Max(descriptor->natts, 1));
            for (int index = 0; index < descriptor->natts; index++)
            {
                Form_pg_attribute attribute = TupleDescAttr(descriptor, index);
                const AnkusValue *cell = &cells[index];
                if (attribute->attisdropped || ankus_trigger_unavailable(attribute, trigger->tg_event, true))
                    continue;
                if (cell->auxiliary1 == -12)
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE), errmsg("Trigger result contains an unavailable ordinary attribute")));
                if (cell->auxiliary1 == -11)
                    continue;
                replace[index] = true;
                values[index] = ankus_trigger_cell(cell, attribute, shape->fields[index].base_type, &nulls[index]);
            }

            /* Preserve PostgreSQL tuple identification and generated slots from the original NEW, and build the row
             * where the caller keeps it. */
            previous = MemoryContextSwitchTo(target);
            tuple = heap_modify_tuple(original, descriptor, values, nulls, replace);
            MemoryContextSwitchTo(previous);
            return tuple;
        }

        static HeapTuple
        ankus_trigger_result(const AnkusValue *value, TriggerData *trigger, const AnkusTupleShape *shape, MemoryContext target)
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
            HeapTuple tuple;
            MemoryContext previous;
            if (type != descriptor->tdtypeid || modifier != descriptor->tdtypmod || count != descriptor->natts)
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Trigger result must match the triggering relation's row type")));
            values = palloc0(sizeof(Datum) * Max(count, 1));
            nulls = palloc0(sizeof(bool) * Max(count, 1));
            replace = palloc0(sizeof(bool) * Max(count, 1));
            for (int index = 0; index < count; index++)
            {
                Form_pg_attribute attribute = TupleDescAttr(descriptor, index);
                AnkusTupleField *field = &fields[index];
                const AnkusTupleShapeField *expected = &shape->fields[index];
                bool same_name = expected->name_length == field->name_length &&
                    memcmp(expected->name, field->name, field->name_length) == 0;
                if (attribute->attisdropped != ((field->flags & 1) != 0) ||
                    (!attribute->attisdropped && (!same_name || attribute->atttypid != field->type ||
                        expected->base_type != field->base_type || attribute->atttypmod != field->modifier ||
                        attribute->attcollation != field->collation)))
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Trigger result attribute %d does not match the relation", index + 1)));
                if (attribute->attisdropped || ankus_trigger_unavailable(attribute, trigger->tg_event, true))
                    continue;
                if (field->flags & 8)
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE), errmsg("Trigger result contains an unavailable ordinary attribute")));
                replace[index] = true;
                values[index] = ankus_trigger_cell(&field->value, attribute, expected->base_type, &nulls[index]);
            }

            /* Preserve PostgreSQL tuple identification and generated slots from the original NEW.
             * PostgreSQL recomputes stored generated values after BEFORE callbacks finish. */
            previous = MemoryContextSwitchTo(target);
            tuple = heap_modify_tuple(original, descriptor, values, nulls, replace);
            MemoryContextSwitchTo(previous);
            return tuple;
        }

        static Datum
        ankus_trigger_call(FunctionCallInfo fcinfo, AnkusTriggerCallback callback)
        {
            TriggerData *trigger;
            AnkusTriggerScope scope;
            AnkusTriggerScope *previous_scope = ankus_trigger_scope;
            Oid previous_function = ankus_function_oid;
            MemoryContext caller = CurrentMemoryContext;
            AnkusValue arguments[16] = {0};
            AnkusTriggerSite *volatile site = NULL;
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
            /* Built outside the callback scope so its failure needs no cleanup beyond the scope context. */
            PG_TRY();
            {
                site = ankus_trigger_site(fcinfo, trigger, scope.context);
            }
            PG_CATCH();
            {
                MemoryContextSwitchTo(caller);
                MemoryContextDelete(scope.context);
                PG_RE_THROW();
            }
            PG_END_TRY();
            scope.identity = ++ankus_next_trigger_id;
            /* These mutable headers are read during cleanup after ERROR/longjmp. */
            result = MemoryContextAllocZero(scope.context, sizeof(AnkusValue));
            error = MemoryContextAlloc(scope.context, sizeof(AnkusError));
            ankus_error_clear(error);
            ankus_trigger_scope = &scope;
            ankus_function_oid = fcinfo->flinfo->fn_oid;
            AnkusMemoryApi memory = {0};
            site->busy++;
            PG_TRY();
            {
                AnkusInputBuffer owned = {0};
                int status;
                MemoryContextSwitchTo(scope.context);
                ankus_memory_initialize(&memory);
                arguments[0].integral = trigger->tg_event;
                arguments[1].integral = RelationGetRelid(trigger->tg_relation);
                arguments[2].integral = trigger->tg_trigger->tgoid;
                /* Managed code copies what it reads, so the cached transports are passed as they are. */
                for (int index = 0; index < 5; index++)
                    arguments[3 + index] = site->texts[index];
                arguments[8] = site->arguments;
                arguments[9].is_null = arguments[10].is_null = true;
                if (TRIGGER_FIRED_FOR_ROW(trigger->tg_event))
                {
                    if (TRIGGER_FIRED_BY_UPDATE(trigger->tg_event) || TRIGGER_FIRED_BY_DELETE(trigger->tg_event))
                        ankus_trigger_tuple(trigger->tg_trigtuple, trigger, &site->shape, false, &arguments[9]);
                    if (TRIGGER_FIRED_BY_INSERT(trigger->tg_event) || TRIGGER_FIRED_BY_UPDATE(trigger->tg_event))
                        ankus_trigger_tuple(TRIGGER_FIRED_BY_INSERT(trigger->tg_event) ? trigger->tg_trigtuple : trigger->tg_newtuple,
                            trigger, &site->shape, true, &arguments[10]);
                }

                arguments[11] = site->columns;
                arguments[12] = site->named;
                ankus_trigger_columns(trigger, &arguments[13], &owned);
                arguments[14].integral = trigger->tg_trigger->tgconstraint;
                arguments[14].auxiliary1 = (trigger->tg_trigger->tgdeferrable ? 1 : 0) | (trigger->tg_trigger->tginitdeferred ? 2 : 0);
                arguments[15].integral = site->identity;
                ANKUS_MANAGED_INVOKE(status, error, callback(arguments, result, error, ankus_spi_execute, &memory));
                if (status != 0)
                    ankus_raise_error(error);
                if (!TRIGGER_FIRED_AFTER(trigger->tg_event) && !result->is_null)
                {
                    if (TRIGGER_FIRED_FOR_STATEMENT(trigger->tg_event))
                        ereport(ERROR, (errcode(ERRCODE_E_R_I_E_TRIGGER_PROTOCOL_VIOLATED), errmsg("BEFORE STATEMENT trigger cannot return a value")));
                    if (TRIGGER_FIRED_BY_DELETE(trigger->tg_event))
                        output = trigger->tg_trigtuple;
                    else if (result->auxiliary1 != -4 && result->data == NULL)
                        /* The trigger returned its NEW row unchanged. */
                        output = TRIGGER_FIRED_BY_UPDATE(trigger->tg_event) ? trigger->tg_newtuple : trigger->tg_trigtuple;
                    else if (result->auxiliary1 == -10)
                        output = ankus_trigger_result_cells(result, trigger, &site->shape, caller);
                    else
                        output = ankus_trigger_result(result, trigger, &site->shape, caller);
                }
            }
            PG_FINALLY();
            {
                MemoryContextSwitchTo(caller);
                /* A nested call's private site lives in the scope context deleted below. */
                site->busy--;
                if (result->release != NULL)
                    result->release(result->data);
                ankus_release_error(error);
                ANKUS_PG_TRY_INNER();
                {
                    /* Iterator disposal can still query this invocation's transition tables.
                     * If it raises, keep the environment under the caller for abort cleanup. */
                    ankus_close_trigger_cursors(scope.identity);
                    MemoryContextDelete(scope.context);
                }
                ANKUS_PG_FINALLY_INNER();
                {
                    ankus_trigger_scope = previous_scope;
                    ankus_function_oid = previous_function;
                    MemoryContextSwitchTo(caller);
                }
                ANKUS_PG_END_TRY_INNER();
            }
            PG_END_TRY();
            /* A NULL HeapTuple pointer means skip, never SQL NULL. */
            fcinfo->isnull = false;
            return PointerGetDatum(output);
        }

        """;
}
