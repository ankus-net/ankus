namespace Ankus.Generators;

/// <summary>
/// Copies composite datums and tuple descriptors through a pointer-free, recursively typed transport.
/// </summary>
internal static class NativeTupleBridge
{
    /// <summary>
    /// Gets composite readers, validated reconstruction, and descriptor serialization helpers.
    /// </summary>
    internal const string Source = """
        #include "access/htup_details.h"
        #include "funcapi.h"
        #include "libpq/pqformat.h"
        #include "utils/typcache.h"
        #include "utils/builtins.h"
        #include "executor/executor.h"
        #include "nodes/makefuncs.h"
        #include "parser/parse_coerce.h"
        #include "utils/datum.h"

        /* Applies a type modifier the way coerce_to_target_type would in an assignment: through the
         * type's length coercion function from pg_cast, strict, with isExplicit false and no input
         * collation. Calling it directly gives the same value or error without planning and running an
         * expression for each value. Domains, which also check constraints, and arrays, which coerce
         * each element, report false so the caller takes the general path. */
        static bool
        ankus_coerce_typmod(Datum *value, bool *is_null, Oid type, int32 modifier)
        {
            Oid function;
            FmgrInfo info;
            LOCAL_FCINFO(call, 3);
            if (getBaseType(type) != type)
                return false;
            switch (find_typmod_coercion_function(type, &function))
            {
                case COERCION_PATH_NONE:
                    /* Without a coercion function, the modifier only relabels the value. */
                    return true;
                case COERCION_PATH_FUNC:
                    break;
                default:
                    return false;
            }

            fmgr_info(function, &info);
            if (*is_null && info.fn_strict)
                return true;
            InitFunctionCallInfoData(*call, &info, info.fn_nargs, InvalidOid, NULL, NULL);
            call->args[0].value = *value;
            call->args[0].isnull = *is_null;
            if (info.fn_nargs >= 2)
            {
                call->args[1].value = Int32GetDatum(modifier);
                call->args[1].isnull = false;
            }

            if (info.fn_nargs == 3)
            {
                call->args[2].value = BoolGetDatum(false);
                call->args[2].isnull = false;
            }

            *value = FunctionCallInvoke(call);
            *is_null = call->isnull;
            return true;
        }

        static Datum
        ankus_coerce_value(Datum value, bool *is_null, Oid source_type, Oid target_type,
            int32 modifier, Oid collation)
        {
            int16 length;
            bool by_value;
            Const *constant;
            Node *expression;
            EState *estate;
            MemoryContext previous = CurrentMemoryContext;
            Datum result = (Datum) 0;
            if (source_type == target_type && modifier >= 0 && ankus_coerce_typmod(&value, is_null, target_type, modifier))
                return value;
            get_typlenbyval(source_type, &length, &by_value);
            constant = makeConst(source_type, -1, collation, length, value, *is_null, by_value);
            expression = coerce_to_target_type(NULL, (Node *) constant, source_type, target_type, modifier,
                COERCION_ASSIGNMENT, COERCE_IMPLICIT_CAST, -1);
            if (expression == NULL)
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Tuple field cannot be assigned to its declared type")));
            estate = CreateExecutorState();
            PG_TRY();
            {
                ExprState *state = ExecPrepareExpr((Expr *) expression, estate);
                result = ExecEvalExprSwitchContext(state, GetPerTupleExprContext(estate), is_null);
                get_typlenbyval(target_type, &length, &by_value);
                if (!*is_null)
                    result = datumCopy(result, by_value, length);
            }
            PG_FINALLY();
            {
                MemoryContextSwitchTo(previous);
                FreeExecutorState(estate);
            }
            PG_END_TRY();
            return result;
        }

        /* domain_check keeps a domain's constraint state between calls and refreshes it whenever the domain
         * changes, which PostgreSQL's own callers rely on through fn_extra. The backend keeps that state for
         * a few domains; the cache is cleared only when full and no check is running, because a CHECK
         * expression can call a function that checks another domain. */
        #define ANKUS_DOMAIN_CHECKS 16
        typedef struct AnkusDomainCheck
        {
            Oid domain;
            void *extra;
        } AnkusDomainCheck;
        static AnkusDomainCheck ankus_domain_checks[ANKUS_DOMAIN_CHECKS];
        static int ankus_domain_check_count = 0;
        static int ankus_domain_check_depth = 0;
        static MemoryContext ankus_domain_check_context = NULL;

        static void
        ankus_check_domain(Datum value, bool is_null, Oid domain)
        {
            AnkusDomainCheck *entry = NULL;
            for (int index = 0; index < ankus_domain_check_count; index++)
            {
                if (ankus_domain_checks[index].domain == domain)
                {
                    entry = &ankus_domain_checks[index];
                    break;
                }
            }

            if (entry == NULL && ankus_domain_check_count == ANKUS_DOMAIN_CHECKS && ankus_domain_check_depth == 0)
            {
                MemoryContextReset(ankus_domain_check_context);
                ankus_domain_check_count = 0;
            }

            if (entry == NULL && ankus_domain_check_count < ANKUS_DOMAIN_CHECKS)
            {
                if (ankus_domain_check_context == NULL)
                    ankus_domain_check_context = AllocSetContextCreate(TopMemoryContext, "Ankus domain checks",
                        ALLOCSET_SMALL_SIZES);
                entry = &ankus_domain_checks[ankus_domain_check_count++];
                entry->domain = domain;
                entry->extra = NULL;
            }

            if (entry == NULL)
            {
                domain_check(value, is_null, domain, NULL, CurrentMemoryContext);
                return;
            }

            ankus_domain_check_depth++;
            PG_TRY();
            {
                domain_check(value, is_null, domain, &entry->extra, ankus_domain_check_context);
            }
            PG_FINALLY();
            {
                ankus_domain_check_depth--;
            }
            PG_END_TRY();
        }

        /* Checks a value against a domain as an assignment's CoerceToDomain does: the base type's modifier
         * first, then the domain's NOT NULL and CHECK constraints through domain_check, as PL/pgSQL does for
         * domain results. The result is the input datum unless the modifier produced a new one. */
        static Datum
        ankus_coerce_domain(Datum value, bool *is_null, Oid domain)
        {
            int32 modifier = -1;
            Oid base = getBaseTypeAndTypmod(domain, &modifier);
            if (modifier >= 0 && !ankus_coerce_typmod(&value, is_null, base, modifier))
                return ankus_coerce_value(value, is_null, base, domain, -1, InvalidOid);
            ankus_check_domain(value, *is_null, domain);
            return value;
        }

        typedef struct AnkusTupleField
        {
            Oid type;
            Oid base_type;
            int32 modifier;
            Oid collation;
            int flags;
            const char *name;
            int name_length;
            AnkusValue value;
        } AnkusTupleField;

        /* A descriptor's catalog facts, resolved once for a caller that serializes many rows of one relation.
         * Every field is a pure function of the descriptor, so a cached shape transports exactly what an
         * uncached one would. */
        typedef struct AnkusTupleShapeField
        {
            Oid base_type;
            int flags;
            bool enum_checked;
            char *name;
            int name_length;
        } AnkusTupleShapeField;

        typedef struct AnkusTupleShape
        {
            Oid declared_base;
            int count;
            AnkusTupleShapeField *fields;
        } AnkusTupleShape;

        /* A persistent field owns its name; a transient one borrows the catalog name when no conversion is needed. */
        static void
        ankus_tuple_shape_field(Form_pg_attribute attribute, AnkusTupleShapeField *field, bool persistent)
        {
            const char *name = NameStr(attribute->attname);
            char *utf8 = pg_server_to_any(name, strlen(name), PG_UTF8);
            field->base_type = attribute->attisdropped ? InvalidOid : getBaseType(attribute->atttypid);
            field->flags = (attribute->attisdropped ? 1 : 0) | (attribute->attnotnull ? 2 : 0);
            if (OidIsValid(field->base_type) &&
                (field->base_type == RECORDOID || get_typtype(field->base_type) == TYPTYPE_COMPOSITE))
                field->flags |= 4;
            field->enum_checked = false;
            field->name_length = strlen(utf8);
            field->name = utf8 == name && persistent ? pnstrdup(name, field->name_length) : utf8;
        }

        static void
        ankus_tuple_transport_shaped(TupleDesc descriptor, Oid declared_type, AnkusTupleShape *shape, const Datum *values,
            const bool *nulls, const bool *unavailable, AnkusValue *value, AnkusInputBuffer *owned)
        {
            StringInfoData buffer;
            check_stack_depth();
            initStringInfo(&buffer);
            pq_sendint32(&buffer, declared_type);
            pq_sendint32(&buffer, descriptor->tdtypmod);
            pq_sendint32(&buffer, descriptor->natts);
            for (int index = 0; index < descriptor->natts; index++)
            {
                Form_pg_attribute attribute = TupleDescAttr(descriptor, index);
                AnkusTupleShapeField resolved;
                AnkusTupleShapeField *field = shape != NULL ? &shape->fields[index] : &resolved;
                int flags;
                AnkusValue cell = {0};
                AnkusInputBuffer cell_owned = {0};
                CHECK_FOR_INTERRUPTS();
                if (shape == NULL)
                    ankus_tuple_shape_field(attribute, &resolved, false);
                flags = field->flags;
                if (unavailable != NULL && unavailable[index])
                    flags |= 8;
                pq_sendint32(&buffer, attribute->atttypid);
                pq_sendint32(&buffer, field->base_type);
                pq_sendint32(&buffer, attribute->atttypmod);
                pq_sendint32(&buffer, attribute->attcollation);
                pq_sendint32(&buffer, flags);
                pq_sendint32(&buffer, field->name_length);
                pq_sendbytes(&buffer, field->name, field->name_length);
                if (shape == NULL && resolved.name != NameStr(attribute->attname))
                    pfree(resolved.name);
                cell.is_null = values == NULL || attribute->attisdropped || (flags & 8) != 0 || nulls[index];
                if (!cell.is_null)
                {
                    if (!field->enum_checked)
                    {
                        ankus_check_result_enum(field->base_type);
                        field->enum_checked = shape != NULL;
                    }

                    /* The field's type is already a base type, so convert without resolving it again. A cell
                     * keeps an exact copy of a value Ankus cannot convert, as pgrx reads a field only when
                     * asked; a mapped reader can use it, and writing the row back restores it. */
                    ankus_convert_base_value(values[index], field->base_type, &cell, &cell_owned, true);
                }

                pq_sendint64(&buffer, cell.integral);
                pq_sendint32(&buffer, cell.auxiliary1);
                pq_sendint32(&buffer, cell.auxiliary2);
                pq_sendint32(&buffer, cell.temporal_infinity);
                pq_sendint32(&buffer, cell.is_null);
                pq_sendint32(&buffer, cell.length);
                if (cell.length != 0)
                    pq_sendbytes(&buffer, (char *) cell.data, cell.length);
                ankus_free_input(&cell_owned);
            }

            owned->serialized = buffer.data;
            value->data = (unsigned char *) buffer.data;
            value->length = buffer.len;
            value->auxiliary1 = -4;
            /* Physical index descriptors on PostgreSQL 13 have no catalog row type. */
            value->integral = shape != NULL ? shape->declared_base : OidIsValid(declared_type) ? getBaseType(declared_type) : InvalidOid;
        }

        static void
        ankus_tuple_transport_available(TupleDesc descriptor, Oid declared_type, const Datum *values,
            const bool *nulls, const bool *unavailable, AnkusValue *value, AnkusInputBuffer *owned)
        {
            ankus_tuple_transport_shaped(descriptor, declared_type, NULL, values, nulls, unavailable, value, owned);
        }

        static void
        ankus_tuple_transport(TupleDesc descriptor, Oid declared_type, const Datum *values,
            const bool *nulls, AnkusValue *value, AnkusInputBuffer *owned)
        {
            ankus_tuple_transport_available(descriptor, declared_type, values, nulls, NULL, value, owned);
        }

        /* Resolves a descriptor's column facts once, in the current memory context. */
        static void
        ankus_tuple_shape_build(TupleDesc descriptor, Oid declared_type, AnkusTupleShape *shape)
        {
            shape->declared_base = OidIsValid(declared_type) ? getBaseType(declared_type) : InvalidOid;
            shape->count = descriptor->natts;
            shape->fields = palloc0(sizeof(AnkusTupleShapeField) * Max(descriptor->natts, 1));
            for (int index = 0; index < descriptor->natts; index++)
                ankus_tuple_shape_field(TupleDescAttr(descriptor, index), &shape->fields[index], true);
        }

        /* Rows of one composite type usually cross the boundary one after another, such as a function's
         * composite argument on every call. The backend keeps the resolved facts of a few recent row types.
         * An entry is used only while every column still has the type, modifier, collation, flags and name
         * it was built from, so a changed type gets a new entry. Entries are replaced only when no
         * conversion is using one, because a composite column converts its own rows while its parent's
         * entry is in use. */
        #define ANKUS_TUPLE_SHAPES 16
        typedef struct AnkusCachedShape
        {
            Oid type;
            int32 modifier;
            MemoryContext context;
            FormData_pg_attribute *columns;
            AnkusTupleShape shape;
        } AnkusCachedShape;
        static AnkusCachedShape ankus_tuple_shapes[ANKUS_TUPLE_SHAPES];
        static int ankus_tuple_shape_count = 0;
        static int ankus_tuple_shape_next = 0;
        static int ankus_tuple_shape_depth = 0;
        static MemoryContext ankus_tuple_shape_context = NULL;

        static bool
        ankus_tuple_shape_matches(const AnkusCachedShape *entry, TupleDesc descriptor, Oid type, int32 modifier)
        {
            if (entry->type != type || entry->modifier != modifier || entry->shape.count != descriptor->natts)
                return false;
            for (int index = 0; index < descriptor->natts; index++)
            {
                Form_pg_attribute attribute = TupleDescAttr(descriptor, index);
                Form_pg_attribute cached = &entry->columns[index];
                if (attribute->atttypid != cached->atttypid || attribute->atttypmod != cached->atttypmod ||
                    attribute->attcollation != cached->attcollation || attribute->attisdropped != cached->attisdropped ||
                    attribute->attnotnull != cached->attnotnull || strcmp(NameStr(attribute->attname), NameStr(cached->attname)) != 0)
                    return false;
            }

            return true;
        }

        /* Returns the cached shape for a descriptor, or NULL when the cache cannot take another entry now. The
         * caller holds ankus_tuple_shape_depth while it uses the shape. */
        static AnkusTupleShape *
        ankus_tuple_shape_find(TupleDesc descriptor, Oid type, int32 modifier)
        {
            AnkusCachedShape *entry;
            MemoryContext previous;
            for (int index = 0; index < ankus_tuple_shape_count; index++)
            {
                if (ankus_tuple_shape_matches(&ankus_tuple_shapes[index], descriptor, type, modifier))
                    return &ankus_tuple_shapes[index].shape;
            }

            if (ankus_tuple_shape_count == ANKUS_TUPLE_SHAPES && ankus_tuple_shape_depth != 0)
                return NULL;
            if (ankus_tuple_shape_context == NULL)
                ankus_tuple_shape_context = AllocSetContextCreate(TopMemoryContext, "Ankus tuple shapes", ALLOCSET_SMALL_SIZES);
            if (ankus_tuple_shape_count < ANKUS_TUPLE_SHAPES)
                entry = &ankus_tuple_shapes[ankus_tuple_shape_count];
            else
            {
                entry = &ankus_tuple_shapes[ankus_tuple_shape_next];
                ankus_tuple_shape_next = (ankus_tuple_shape_next + 1) % ANKUS_TUPLE_SHAPES;
                MemoryContextDelete(entry->context);
                memset(entry, 0, sizeof(*entry));
            }

            entry->context = AllocSetContextCreate(ankus_tuple_shape_context, "Ankus tuple shape", ALLOCSET_SMALL_SIZES);
            previous = MemoryContextSwitchTo(entry->context);
            PG_TRY();
            {
                entry->columns = palloc(sizeof(FormData_pg_attribute) * Max(descriptor->natts, 1));
                for (int index = 0; index < descriptor->natts; index++)
                    memcpy(&entry->columns[index], TupleDescAttr(descriptor, index), sizeof(FormData_pg_attribute));
                ankus_tuple_shape_build(descriptor, type, &entry->shape);
            }
            PG_CATCH();
            {
                MemoryContextSwitchTo(previous);
                MemoryContextDelete(entry->context);
                memset(entry, 0, sizeof(*entry));
                PG_RE_THROW();
            }
            PG_END_TRY();
            MemoryContextSwitchTo(previous);
            entry->type = type;
            entry->modifier = modifier;
            if (entry == &ankus_tuple_shapes[ankus_tuple_shape_count])
                ankus_tuple_shape_count++;
            return &entry->shape;
        }

        static void
        ankus_read_tuple(Datum datum, AnkusValue *value, AnkusInputBuffer *owned)
        {
            HeapTupleHeader header = DatumGetHeapTupleHeader(datum);
            Oid type = HeapTupleHeaderGetTypeId(header);
            int32 modifier = HeapTupleHeaderGetTypMod(header);
            TupleDesc descriptor;
            HeapTupleData tuple = {0};
            Datum *values;
            bool *nulls;
            volatile bool entered = false;
            check_stack_depth();
            if ((Pointer) header != DatumGetPointer(datum))
                owned->detoasted = (struct varlena *) header;
            descriptor = lookup_rowtype_tupdesc(type, modifier);
            tuple.t_len = HeapTupleHeaderGetDatumLength(header);
            tuple.t_data = header;
            values = palloc(sizeof(Datum) * Max(descriptor->natts, 1));
            nulls = palloc(sizeof(bool) * Max(descriptor->natts, 1));
            PG_TRY();
            {
                AnkusTupleShape *shape = ankus_tuple_shape_find(descriptor, type, modifier);
                entered = true;
                ankus_tuple_shape_depth++;
                heap_deform_tuple(&tuple, descriptor, values, nulls);
                ankus_tuple_transport_shaped(descriptor, type, shape, values, nulls, NULL, value, owned);
            }
            PG_FINALLY();
            {
                if (entered)
                    ankus_tuple_shape_depth--;
                ReleaseTupleDesc(descriptor);
            }
            PG_END_TRY();
            pfree(values);
            pfree(nulls);
        }

        static AnkusTupleField *
        ankus_tuple_fields(const AnkusValue *value, Oid *type, int32 *modifier, int *count)
        {
            StringInfoData buffer = {(char *) value->data, value->length, value->length, 0};
            AnkusTupleField *fields;
            if (value->auxiliary1 != -4 || value->data == NULL || value->length < 12)
                ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid Ankus tuple header")));
            *type = pq_getmsgint(&buffer, 4);
            *modifier = pq_getmsgint(&buffer, 4);
            *count = pq_getmsgint(&buffer, 4);
            if (!OidIsValid(*type) || *count < 0 || *count > MaxTupleAttributeNumber ||
                (int64) 12 + (int64) *count * 52 > value->length)
                ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid Ankus tuple attribute count or identity")));
            fields = palloc0(sizeof(AnkusTupleField) * Max(*count, 1));
            for (int index = 0; index < *count; index++)
            {
                AnkusTupleField *field = &fields[index];
                int is_null;
                field->type = pq_getmsgint(&buffer, 4);
                field->base_type = pq_getmsgint(&buffer, 4);
                field->modifier = pq_getmsgint(&buffer, 4);
                field->collation = pq_getmsgint(&buffer, 4);
                field->flags = pq_getmsgint(&buffer, 4);
                field->name_length = pq_getmsgint(&buffer, 4);
                if (field->name_length < 0 || field->name_length > (NAMEDATALEN - 1) * 4 ||
                    field->flags < 0 || field->flags > 15)
                    ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid Ankus tuple attribute metadata")));
                field->name = pq_getmsgbytes(&buffer, field->name_length);
                if (memchr(field->name, '\0', field->name_length) != NULL)
                    ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Tuple attribute names cannot contain zero bytes")));
                field->value.integral = pq_getmsgint64(&buffer);
                field->value.auxiliary1 = pq_getmsgint(&buffer, 4);
                field->value.auxiliary2 = pq_getmsgint(&buffer, 4);
                field->value.temporal_infinity = pq_getmsgint(&buffer, 4);
                is_null = pq_getmsgint(&buffer, 4);
                field->value.length = pq_getmsgint(&buffer, 4);
                if (is_null < 0 || is_null > 1 || field->value.length < 0 || (is_null && field->value.length != 0) ||
                    ((field->flags & 9) && !is_null) || (!(field->flags & 1) && !OidIsValid(field->type)))
                    ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid Ankus tuple attribute value")));
                field->value.is_null = is_null;
                field->value.data = (unsigned char *) pq_getmsgbytes(&buffer, field->value.length);
            }

            pq_getmsgend(&buffer);
            return fields;
        }

        static TupleDesc
        ankus_tuple_descriptor(const AnkusTupleField *fields, int count)
        {
            TupleDesc descriptor = CreateTemplateTupleDesc(count);
            for (int index = 0; index < count; index++)
            {
                const AnkusTupleField *field = &fields[index];
                char *terminated = pnstrdup(field->name, field->name_length);
                char *name = pg_any_to_server(terminated, field->name_length, PG_UTF8);
                if (field->flags & 1)
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("New anonymous tuple descriptors cannot contain dropped attributes")));
                if (strlen(name) >= NAMEDATALEN || strlen(name) == 0)
                    ereport(ERROR, (errcode(ERRCODE_INVALID_NAME), errmsg("Tuple attribute names must fit PostgreSQL identifiers")));
                TupleDescInitEntry(descriptor, index + 1, name, field->type, field->modifier, 0);
                if (OidIsValid(field->collation))
                    TupleDescInitEntryCollation(descriptor, index + 1, field->collation);
                if (name != terminated)
                    pfree(name);
                pfree(terminated);
            }

        #if PG_VERSION_NUM >= 190000
            TupleDescFinalize(descriptor);
        #endif
            return BlessTupleDesc(descriptor);
        }

        static Datum
        ankus_write_tuple(const AnkusValue *value, Oid expected_type, TupleDesc expected_descriptor)
        {
            Oid type;
            int32 modifier;
            int count;
            AnkusTupleField *fields;
            TupleDesc descriptor;
            Datum *values;
            bool *nulls;
            HeapTuple tuple;
            Datum result;
            bool anonymous;
            volatile bool entered = false;
            check_stack_depth();
            fields = ankus_tuple_fields(value, &type, &modifier, &count);
            for (int index = 0; index < count; index++)
            {
                if (fields[index].flags & 8)
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                        errmsg("Unavailable trigger attributes cannot be used as ordinary composite values")));
            }

            if (get_typtype(type) == '\0')
                ereport(ERROR, (errcode(ERRCODE_UNDEFINED_OBJECT), errmsg("Composite type OID %u no longer exists", type)));
            anonymous = type == RECORDOID && modifier < 0;
            if (expected_type != RECORDOID && getBaseType(type) != getBaseType(expected_type))
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Composite type OID %u cannot be returned as type OID %u", type, expected_type)));
            if (anonymous)
                descriptor = ankus_tuple_descriptor(fields, count);
            else
                descriptor = lookup_rowtype_tupdesc_copy(getBaseType(type), modifier);
            if (descriptor->natts != count)
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Composite tuple layout changed after the managed value was captured")));
            values = palloc0(sizeof(Datum) * Max(count, 1));
            nulls = palloc(sizeof(bool) * Max(count, 1));
            PG_TRY();
            {
                /* A named type's column facts come from the shape cache; an anonymous record is described
                 * only by this value. */
                AnkusTupleShape *shape = anonymous ? NULL : ankus_tuple_shape_find(descriptor, descriptor->tdtypeid, descriptor->tdtypmod);
                entered = true;
                ankus_tuple_shape_depth++;
                for (int index = 0; index < count; index++)
                {
                    Form_pg_attribute attribute = TupleDescAttr(descriptor, index);
                    AnkusTupleField *field = &fields[index];
                    const AnkusTupleShapeField *expected = shape != NULL ? &shape->fields[index] : NULL;
                    AnkusParameter parameter = {0};
                    bool same_name;
                    Oid base_type;
                    if (expected != NULL)
                        same_name = expected->name_length == field->name_length &&
                            memcmp(expected->name, field->name, field->name_length) == 0;
                    else
                    {
                        const char *name = NameStr(attribute->attname);
                        char *utf8 = pg_server_to_any(name, strlen(name), PG_UTF8);
                        same_name = strlen(utf8) == (Size) field->name_length && memcmp(utf8, field->name, field->name_length) == 0;
                        if (utf8 != name)
                            pfree(utf8);
                    }

                    if (attribute->attisdropped != ((field->flags & 1) != 0) ||
                        (!attribute->attisdropped && (attribute->atttypid != field->type ||
                            attribute->atttypmod != field->modifier ||
                            (!(anonymous && !OidIsValid(field->collation)) && attribute->attcollation != field->collation) || !same_name)))
                        ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Composite attribute %d changed after the managed value was captured", index + 1)));
                    nulls[index] = field->value.is_null != 0 || attribute->attisdropped;
                    if (attribute->attisdropped)
                        continue;
                    parameter.type_oid = attribute->atttypid;
                    parameter.value = field->value;
                    base_type = expected != NULL ? expected->base_type : getBaseType(parameter.type_oid);
                    if (!nulls[index] && (base_type == JSONOID || base_type == JSONBOID))
                        parameter.value.data = (unsigned char *) pnstrdup((char *) parameter.value.data, parameter.value.length);
                    values[index] = expected != NULL ? ankus_parameter_datum_resolved(&parameter, base_type) : ankus_parameter_datum(&parameter);
                    if (attribute->atttypmod >= 0)
                        values[index] = ankus_coerce_value(values[index], &nulls[index], attribute->atttypid,
                            attribute->atttypid, attribute->atttypmod, attribute->attcollation);
                }
            }
            PG_FINALLY();
            {
                if (entered)
                    ankus_tuple_shape_depth--;
            }
            PG_END_TRY();

            tuple = heap_form_tuple(descriptor, values, nulls);
            if (expected_descriptor != NULL)
            {
                int source = 0;
                Datum *output = palloc0(sizeof(Datum) * Max(expected_descriptor->natts, 1));
                bool *output_nulls = palloc(sizeof(bool) * Max(expected_descriptor->natts, 1));
                for (int target = 0; target < expected_descriptor->natts; target++)
                {
                    Form_pg_attribute attribute = TupleDescAttr(expected_descriptor, target);
                    output_nulls[target] = true;
                    if (attribute->attisdropped)
                        continue;
                    while (source < descriptor->natts && TupleDescAttr(descriptor, source)->attisdropped)
                        source++;
                    if (source >= descriptor->natts || TupleDescAttr(descriptor, source)->atttypid != attribute->atttypid ||
                        (attribute->atttypmod >= 0 && TupleDescAttr(descriptor, source)->atttypmod != attribute->atttypmod))
                        ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Composite result does not match the caller's tuple descriptor")));
                    output[target] = values[source];
                    output_nulls[target] = nulls[source++];
                }

                while (source < descriptor->natts && TupleDescAttr(descriptor, source)->attisdropped)
                    source++;
                if (source != descriptor->natts)
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Composite result has more attributes than the caller's tuple descriptor")));
                heap_freetuple(tuple);
                tuple = heap_form_tuple(BlessTupleDesc(expected_descriptor), output, output_nulls);
                pfree(output);
                pfree(output_nulls);
            }

            pfree(values);
            pfree(nulls);
            pfree(fields);
            result = heap_copy_tuple_as_datum(tuple, expected_descriptor != NULL ? expected_descriptor : descriptor);
            heap_freetuple(tuple);
            FreeTupleDesc(descriptor);
            if (getBaseType(expected_type) != expected_type)
            {
                bool is_null = false;
                Datum coerced = ankus_coerce_domain(result, &is_null, expected_type);
                if (DatumGetPointer(coerced) != DatumGetPointer(result))
                    pfree(DatumGetPointer(result));
                return coerced;
            }

            return result;
        }

        """;

    /// <summary>
    /// Gets guarded catalog lookup, anonymous tuple construction, and array type resolution.
    /// </summary>
    internal const string Operations = """
        static void
        ankus_tuple_operation(AnkusRequest *request, AnkusResult *result)
        {
            if (request->scalar_operation == 1)
            {
                Datum datum = ankus_write_tuple(&request->parameters[0].value, RECORDOID, NULL);
                ankus_result_value(datum, RECORDOID, &result->text);
            }
            else if (request->scalar_operation == 2)
            {
                Oid type = get_array_type((Oid) request->parameters[0].value.integral);
                if (!OidIsValid(type))
                    ereport(ERROR, (errcode(ERRCODE_UNDEFINED_OBJECT), errmsg("Composite array type does not exist")));
                result->text.integral = type;
            }
            else if (request->scalar_operation == 3 || request->scalar_operation == 4)
            {
                /* Copies a caller-supplied native descriptor, and with operation 4 a heap tuple of that shape.
                 * The caller vouches for both addresses. */
                TupleDesc descriptor = (TupleDesc) (uintptr_t) request->parameters[0].value.integral;
                HeapTuple tuple = request->scalar_operation == 4 ? (HeapTuple) (uintptr_t) request->parameters[1].value.integral : NULL;
                Datum *values = NULL;
                bool *nulls = NULL;
                AnkusValue input = {0};
                AnkusInputBuffer owned = {0};
                if (descriptor == NULL || descriptor->natts < 0 || descriptor->natts > MaxTupleAttributeNumber ||
                    !OidIsValid(descriptor->tdtypeid))
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("The address is not a tuple descriptor with a row type")));
                if (request->scalar_operation == 4)
                {
                    if (tuple == NULL || tuple->t_data == NULL || HeapTupleHeaderGetNatts(tuple->t_data) > descriptor->natts)
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("The heap tuple does not match its descriptor")));
                    values = palloc(sizeof(Datum) * Max(descriptor->natts, 1));
                    nulls = palloc(sizeof(bool) * Max(descriptor->natts, 1));
                    heap_deform_tuple(tuple, descriptor, values, nulls);
                }

                ankus_tuple_transport(descriptor, descriptor->tdtypeid, values, nulls, &input, &owned);
                result->text = input;
                result->text.data = NULL;
                ankus_copy_owned(&result->text, input.data, input.length);
                ankus_free_input(&owned);
            }
            else if (request->scalar_operation == 5)
            {
                /* Forms a native heap tuple in the destination context from a managed composite. */
                const AnkusParameter *parameter = &request->parameters[0];
                MemoryContext target = ankus_datum_context(request->result_context, request->result_generation);
                HeapTupleHeader header = DatumGetHeapTupleHeader(ankus_write_tuple(&parameter->value, parameter->type_oid, NULL));
                HeapTupleData source = {0};
                MemoryContext previous;
                source.t_len = HeapTupleHeaderGetDatumLength(header);
                ItemPointerSetInvalid(&source.t_self);
                source.t_tableOid = InvalidOid;
                source.t_data = header;
                previous = MemoryContextSwitchTo(target);
                result->text.integral = (int64) (uintptr_t) heap_copytuple(&source);
                MemoryContextSwitchTo(previous);
            }
            else
            {
                Oid type;
                Oid base;
                TupleDesc descriptor;
                AnkusValue input = {0};
                AnkusInputBuffer owned = {0};
                if (request->parameters[0].value.is_null)
                    type = (Oid) request->parameters[1].value.integral;
                else
                {
                    const AnkusValue *name = &request->parameters[0].value;
                    char *text = pg_any_to_server((char *) name->data, name->length, PG_UTF8);
                    type = DatumGetObjectId(DirectFunctionCall1(regtypein, CStringGetDatum(text)));
                }

                if (get_typtype(type) == '\0')
                    ereport(ERROR, (errcode(ERRCODE_UNDEFINED_OBJECT), errmsg("Composite type OID %u does not exist", type)));
                base = getBaseType(type);
                if (get_typtype(base) != TYPTYPE_COMPOSITE)
                    ereport(ERROR, (errcode(ERRCODE_WRONG_OBJECT_TYPE), errmsg("Type OID %u is not a named composite type", type)));
                descriptor = lookup_rowtype_tupdesc(base, -1);
                PG_TRY();
                {
                    ankus_tuple_transport(descriptor, type, NULL, NULL, &input, &owned);
                    result->text = input;
                    result->text.data = NULL;
                    ankus_copy_owned(&result->text, input.data, input.length);
                }
                PG_FINALLY();
                {
                    ReleaseTupleDesc(descriptor);
                    ankus_free_input(&owned);
                }
                PG_END_TRY();
            }
        }

        """;
}
