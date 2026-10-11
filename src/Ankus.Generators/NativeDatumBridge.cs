namespace Ankus.Generators;

/// <summary>
/// Copies and accesses raw datums with type-derived storage rules and checked context generations.
/// </summary>
internal static class NativeDatumBridge
{
    /// <summary>
    /// Gets typed raw input capture used by scalar, set, and aggregate dispatch.
    /// </summary>
    internal const string PolymorphicInput = """
        static void
        ankus_read_polymorphic(FunctionCallInfo call, int index, AnkusValue *value)
        {
            Oid type = get_fn_expr_argtype(call->flinfo, index);
            if (!OidIsValid(type))
            {
                Oid *declared = NULL;
                int count = 0;
                (void) get_func_signature(call->flinfo->fn_oid, &declared, &count);
                if (index >= 0 && index < count)
                    type = declared[index];
                if (declared != NULL)
                    pfree(declared);
            }

            if (!OidIsValid(type) || IsPolymorphicType(type) || type == ANYOID)
                ereport(ERROR, (errcode(ERRCODE_INDETERMINATE_DATATYPE), errmsg("Cannot resolve raw argument type")));
            value->is_null = call->args[index].isnull;
            value->auxiliary2 = (int32) type;
            if (!value->is_null)
                value->integral = (int64) (uintptr_t) call->args[index].value;
        }
        """;

    /// <summary>
    /// Gets native datum operations invoked inside the existing PostgreSQL error guard.
    /// </summary>
    internal const string Source = """
        #include "access/heaptoast.h"
        #include "utils/datum.h"
        #include "utils/typcache.h"

        typedef struct AnkusDatumReference
        {
            uintptr_t bits;
            intptr_t context;
            uintptr_t generation;
        } AnkusDatumReference;

        static MemoryContext
        ankus_datum_context(intptr_t identity, uintptr_t generation)
        {
            AnkusMemoryContext *entry = ankus_memory_context_by_id((uint64) identity);
            if (entry == NULL || generation == 0 || entry->generation != generation)
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("the raw PostgreSQL datum's memory context is stale")));
            return entry->context;
        }

        static Datum
        ankus_normalize_raw_datum(Datum datum, Oid type, bool by_value, int16 length)
        {
            if (type == CSTRINGOID && DatumGetPointer(datum) == NULL)
            {
                return datum;
            }

            if (by_value || length != -1)
                return datumCopy(datum, by_value, length);

            struct varlena *flat = PG_DETOAST_DATUM_COPY(datum);
            Oid base = getBaseType(type);
            if (base == RECORDOID || get_typtype(base) == TYPTYPE_COMPOSITE)
            {
                HeapTupleHeader tuple = (HeapTupleHeader) flat;
                TupleDesc descriptor = lookup_rowtype_tupdesc(HeapTupleHeaderGetTypeId(tuple), HeapTupleHeaderGetTypMod(tuple));
                Datum copy = toast_flatten_tuple_to_datum(tuple, HeapTupleHeaderGetDatumLength(tuple), descriptor);
                ReleaseTupleDesc(descriptor);
                return copy;
            }

            return PointerGetDatum(flat);
        }

        static Datum
        ankus_copy_raw_datum(Datum datum, Oid type, intptr_t context, uintptr_t generation)
        {
            int16 length;
            bool by_value;
            get_typlenbyval(type, &length, &by_value);
            Datum normalized = ankus_normalize_raw_datum(datum, type, by_value, length);
            MemoryContext target = ankus_datum_context(context, generation);
            if (type == CSTRINGOID && DatumGetPointer(normalized) == NULL)
            {
                return normalized;
            }

            MemoryContext previous = MemoryContextSwitchTo(target);
            Datum copy = datumCopy(normalized, by_value, length);
            MemoryContextSwitchTo(previous);
            return copy;
        }

        static Datum
        ankus_raw_parameter(const AnkusParameter *parameter)
        {
            const AnkusValue *value = &parameter->value;
            AnkusDatumReference reference;
            if (value->length != sizeof(reference))
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Invalid raw datum parameter envelope")));
            memcpy(&reference, value->data, sizeof(reference));
            ankus_datum_context(reference.context, reference.generation);
            if (value->is_null)
                return (Datum) 0;

            int16 length;
            bool by_value;
            get_typlenbyval(parameter->type_oid, &length, &by_value);
            return ankus_normalize_raw_datum((Datum) reference.bits, parameter->type_oid, by_value, length);
        }

        static void
        ankus_raw_array(AnkusRequest *request, AnkusResult *result, Datum datum, Oid type)
        {
            Oid element = get_element_type(type);
            if (!OidIsValid(element))
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("The polymorphic value is not an array")));
            ankus_datum_context(request->result_context, request->result_generation);
            ArrayType *array = DatumGetArrayTypeP(datum);
            if (ARR_ELEMTYPE(array) != element)
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH), errmsg("Array header element type does not match its declared type")));
            int rank = ARR_NDIM(array);
            if (rank < 0 || rank > MAXDIM)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Array header has an invalid dimension count")));
            int shape[MAXDIM * 2];
            memcpy(shape, ARR_DIMS(array), rank * sizeof(int));
            memcpy(shape + rank, ARR_LBOUND(array), rank * sizeof(int));
            ankus_copy_owned(&result->text, (const unsigned char *) shape, (int) (rank * 2 * sizeof(int)));
            result->result_type_oid = element;
            int16 length;
            bool by_value;
            char alignment;
            get_typlenbyvalalign(element, &length, &by_value, &alignment);
            Datum *values;
            bool *nulls;
            int count;
            deconstruct_array(array, element, length, by_value, alignment, &values, &nulls, &count);
            result->column_count = 1;
            result->row_count = count;
            result->values = calloc(Max(count, 1), sizeof(AnkusValue));
            if (result->values == NULL)
                ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("Unable to copy polymorphic array cells")));
            for (int index = 0; index < count; index++)
            {
                result->values[index].is_null = nulls[index];
                if (!nulls[index])
                    result->values[index].integral = (int64) (uintptr_t) ankus_copy_raw_datum(values[index], element,
                        request->result_context, request->result_generation);
            }
        }

        """ + NativeArrayViewBridge.Source + NativeBufferViewBridge.Source + """
        /* A managed row or composite cell already satisfied its domain when it was read, so it is
         * rebuilt as its base type for a mapped reader instead of being assigned to the domain again. */
        static void
        ankus_rebuild_cell(AnkusRequest *request, AnkusResult *result)
        {
            AnkusParameter base = request->parameters[0];
            Oid type = base.type_oid;
            if (get_typtype(type) == '\0')
                ereport(ERROR, (errcode(ERRCODE_UNDEFINED_OBJECT), errmsg("Parameter type OID %u does not exist", type)));
            ankus_datum_context(request->result_context, request->result_generation);
            base.type_oid = getBaseType(type);
            result->processed = base.type_oid;
            result->text.is_null = base.value.is_null;
            if (!base.value.is_null)
                result->text.integral = (int64) (uintptr_t) ankus_copy_raw_datum(ankus_parameter_datum(&base), type,
                    request->result_context, request->result_generation);
        }

        static void
        ankus_datum_operation(AnkusRequest *request, AnkusResult *result)
        {
            if (request->scalar_operation < 0 || request->scalar_operation > 15)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Unknown raw datum operation")));
            if (request->parameter_count != 1 || request->parameters == NULL)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Datum operations require one value")));
            if (request->scalar_operation == 15)
            {
                ankus_rebuild_cell(request, result);
                return;
            }

            const AnkusParameter *parameter = &request->parameters[0];
            const AnkusValue *value = &parameter->value;
            /* Operations 5 through 13 borrow the datum. Operation 14 copies it like 0, keeping a value
             * without a managed conversion for a row or composite cell. */
            bool borrowed = request->scalar_operation >= 5 && request->scalar_operation <= 13;
            if (value->auxiliary1 != -5 || value->data == NULL ||
                value->length != sizeof(AnkusDatumReference) || value->is_null > 1)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Invalid raw datum parameter envelope")));
            if (get_typtype(parameter->type_oid) == '\0')
                ereport(ERROR, (errcode(ERRCODE_UNDEFINED_OBJECT), errmsg("Parameter type OID %u does not exist", parameter->type_oid)));
            if (request->scalar_operation >= 2 && request->scalar_operation != 14)
                ankus_datum_context(request->result_context, request->result_generation);

            if (borrowed)
            {
                AnkusDatumReference reference;
                memcpy(&reference, value->data, sizeof(reference));
                ankus_datum_context(reference.context, reference.generation);
                if (request->scalar_operation == 12)
                {
                    result->result_type_oid = ankus_array_element_contract(getBaseType(parameter->type_oid),
                        request->scalar_result_oid, request->limit != 0);
                    return;
                }

                if (value->is_null)
                    ereport(ERROR, (errcode(ERRCODE_NULL_VALUE_NOT_ALLOWED), errmsg("A borrowed view requires a non-NULL datum")));
                if (request->scalar_operation == 9 || request->scalar_operation == 10 || request->scalar_operation == 13)
                    ankus_buffer_view_operation(request, result, (Datum) reference.bits, getBaseType(parameter->type_oid));
                else
                    ankus_array_view_operation(request, result, (Datum) reference.bits, getBaseType(parameter->type_oid));
                return;
            }

            /* Access existing storage without assigning it back through domain constraints. */
            Datum datum = ankus_raw_parameter(parameter);
            Oid base = getBaseType(parameter->type_oid);
            result->processed = base;
            result->text.is_null = value->is_null;
            if (value->is_null)
                return;

            switch (request->scalar_operation)
            {
                case 0:
                    ankus_check_result_enum(base);
                    ankus_result_value(datum, base, &result->text);
                    break;
                case 14:
                    ankus_check_result_enum(base);
                    ankus_result_cell(datum, base, &result->text);
                    break;
                case 1:
                {
                    Oid output;
                    bool variable;
                    getTypeOutputInfo(parameter->type_oid, &output, &variable);
                    char *text = OidOutputFunctionCall(output, datum);
                    char *utf8 = pg_server_to_any(text, strlen(text), PG_UTF8);
                    ankus_copy_owned(&result->text, (const unsigned char *) utf8, strlen(utf8));
                    break;
                }
                case 2:
                    result->text.integral = (int64) (uintptr_t) ankus_copy_raw_datum(datum,
                        parameter->type_oid, request->result_context, request->result_generation);
                    break;
                case 3:
                    ankus_raw_array(request, result, datum, base);
                    break;
                case 4:
                    ankus_raw_range(request, result, datum, base);
                    break;
                default:
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("Unknown raw datum operation")));
            }
        }
        """;
}
