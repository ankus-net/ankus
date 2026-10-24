namespace Ankus.Generators;

/// <summary>
/// Defines typed SPI parameter conversion and native-owned result materialization under the PostgreSQL call guard.
/// </summary>
internal static class NativeSpiBridge
{
    /// <summary>
    /// Gets the SPI transport structures, datum conversion, and allocator-matched result cleanup functions.
    /// </summary>
    internal const string Source = """
        #include "executor/spi.h"
        #include "catalog/pg_type_d.h"
        #include "utils/lsyscache.h"
        #include "utils/memutils.h"
        #include "tcop/tcopprot.h"
        #include "utils/snapmgr.h"

        typedef struct AnkusParameter
        {
            AnkusValue value;
            Oid type_oid;
        } AnkusParameter;

        enum AnkusSpiOperation
        {
            ANKUS_SPI_EXECUTE,
            ANKUS_SPI_PREPARE,
            ANKUS_SPI_EXECUTE_PLAN,
            ANKUS_SPI_FREE_PLAN,
            ANKUS_SPI_OPEN_CURSOR,
            ANKUS_SPI_OPEN_PLAN_CURSOR,
            ANKUS_SPI_FETCH_CURSOR,
            ANKUS_SPI_CLOSE_CURSOR,
            ANKUS_SPI_FIND_CURSOR,
            ANKUS_SPI_OPEN_SESSION,
            ANKUS_SPI_CLOSE_SESSION,
            ANKUS_SPI_KEEP_PLAN,
            ANKUS_SPI_QUOTE_IDENTIFIER,
            ANKUS_SPI_QUOTE_QUALIFIED_IDENTIFIER,
            ANKUS_SPI_QUOTE_LITERAL,
            ANKUS_SPI_EXPLAIN,
            ANKUS_SPI_REPORT,
            ANKUS_SPI_IS_LOG_ENABLED,
            ANKUS_SPI_TEMPORAL,
            ANKUS_SPI_NUMERIC,
            ANKUS_SPI_NETWORK,
            ANKUS_SPI_GEOMETRY,
            ANKUS_SPI_RANGE,
            ANKUS_SPI_ENUM,
            ANKUS_SPI_TUPLE,
            ANKUS_SPI_GUC_READ,
            ANKUS_SPI_TRANSACTION_CALLBACKS,
            ANKUS_SPI_TRANSACTION_ID,
            ANKUS_SPI_DATUM,
            ANKUS_SPI_FUNCTION_CONTEXT,
            ANKUS_SPI_FUNCTION_CALL,
            ANKUS_SPI_CUSTOM_TYPE,
            ANKUS_SPI_DATUM_TYPE,
            ANKUS_SPI_ARRAY,
            ANKUS_SPI_LOOKUP,
            ANKUS_SPI_RELATION,
            ANKUS_SPI_SUBTRANSACTION,
            ANKUS_SPI_GUC_DEFINE,
            ANKUS_SPI_SETTINGS
        };

        typedef struct AnkusRequest
        {
            const char *command;
            const AnkusParameter *parameters;
            SPIPlanPtr plan;
            intptr_t callback;
            int64 cursor_id;
            int64 session_id;
            int command_length;
            int parameter_count;
            int limit;
            uint8 operation;
            uint8 result_mode;
            uint8 read_only;
            uint8 forward;
            struct AnkusError *diagnostic;
            int log_level;
            int scalar_operation;
            Oid scalar_result_oid;
            uint8 cleanup_only;
            intptr_t result_context;
            uintptr_t result_generation;
            FunctionCallInfo function_call;
            Oid function_oid;
            Oid collation_oid;
            const uint8 *argument_defaults;
            uint8 has_collation;
            uint8 variadic;
            PGFunction native_function;
            intptr_t callback_state;
            void *array_iterator;
            bool recover_input;
        } AnkusRequest;

        typedef struct AnkusColumn
        {
            Oid type_oid;
            Oid base_type_oid;
            AnkusValue name;
        } AnkusColumn;

        typedef struct AnkusResult
        {
            AnkusColumn *columns;
            AnkusValue *values;
            int64 processed;
            int32 row_count;
            int32 column_count;
            void (*release)(struct AnkusResult *);
            int64 cursor_id;
            AnkusValue cursor_name;
            AnkusValue text;
            Oid function_oid;
            Oid result_type_oid;
            Oid collation_oid;
            intptr_t function_site;
            intptr_t function_memory;
            uintptr_t function_generation;
        } AnkusResult;

        typedef int (*AnkusExecute)(AnkusRequest *, AnkusResult *, struct AnkusError *);

        static int ankus_return_cursor(Portal portal, AnkusResult *result);
        static int ankus_cursor_operation(AnkusRequest *request, AnkusResult *result);
        static void ankus_register_session_plan(SPIPlanPtr plan);
        static void ankus_detach_session_plan(SPIPlanPtr plan);
        static bool ankus_array_element_supported(Oid type);
        static bool ankus_enum_supported(Oid type);
        static void ankus_read_array(Datum datum, AnkusValue *value, AnkusInputBuffer *owned);
        static void ankus_read_detached(Datum datum, Oid type, AnkusValue *value, AnkusInputBuffer *owned);
        static Datum ankus_write_detached(const AnkusValue *value, Oid type);
        static Datum ankus_write_array(const AnkusValue *value, Oid element_type);
        static void ankus_read_range(Datum datum, AnkusValue *value, AnkusInputBuffer *owned);
        static Datum ankus_write_range(const AnkusValue *value, Oid type);
        static void ankus_read_tuple(Datum datum, AnkusValue *value, AnkusInputBuffer *owned);
        static Datum ankus_write_tuple(const AnkusValue *value, Oid expected_type, TupleDesc expected_descriptor);
        static Datum ankus_coerce_value(Datum value, bool *is_null, Oid source_type, Oid target_type,
            int32 modifier, Oid collation);
        static Datum ankus_coerce_domain(Datum value, bool *is_null, Oid domain);
        static Datum ankus_raw_parameter(const AnkusParameter *parameter);
        static Datum ankus_parameter_datum_resolved(const AnkusParameter *parameter, Oid base_type);
        static Datum ankus_copy_raw_datum(Datum datum, Oid type, intptr_t context, uintptr_t generation);
        static MemoryContext ankus_datum_context(intptr_t identity, uintptr_t generation);
        static void ankus_raw_range(AnkusRequest *request, AnkusResult *result, Datum datum, Oid type);
        static void ankus_mapped_range(AnkusRequest *request, AnkusResult *result);

        static void
        ankus_release_result(AnkusResult *result)
        {
            free(result->cursor_name.data);
            result->cursor_name.data = NULL;
            free(result->text.data);
            result->text.data = NULL;
            if (result->columns != NULL)
            {
                for (int column = 0; column < result->column_count; column++)
                {
                    free(result->columns[column].name.data);
                }

                free(result->columns);
                result->columns = NULL;
            }

            if (result->values != NULL)
            {
                Size count = (Size) result->row_count * result->column_count;
                for (Size index = 0; index < count; index++)
                {
                    free(result->values[index].data);
                }

                free(result->values);
                result->values = NULL;
            }
        }

        static void
        ankus_copy_owned(AnkusValue *value, const unsigned char *data, int length)
        {
            value->data = malloc((Size) length + 1);
            if (value->data == NULL)
            {
                ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("Unable to allocate SPI result buffer")));
            }

            value->length = length;
            memcpy(value->data, data, length);
            value->data[length] = '\0';
        }

        static Datum
        ankus_parameter_datum(const AnkusParameter *parameter)
        {
            const AnkusValue *value = &parameter->value;
            if (value->auxiliary1 == -6 && value->data != NULL)
            {
                if ((Oid) value->integral != parameter->type_oid)
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH),
                        errmsg("Returned PostgreSQL type %s does not match expected type %s",
                            format_type_be((Oid) value->integral), format_type_be(parameter->type_oid))));
                AnkusParameter raw = *parameter;
                raw.value.auxiliary1 = -5;
                return ankus_parameter_datum(&raw);
            }

            if (get_typtype(parameter->type_oid) == '\0')
                ereport(ERROR, (errcode(ERRCODE_UNDEFINED_OBJECT), errmsg("Parameter type OID %u does not exist", parameter->type_oid)));
            return ankus_parameter_datum_resolved(parameter, getBaseType(parameter->type_oid));
        }

        /* Converts a value whose type the caller has checked and resolved once, such as a set's result column. */
        static Datum
        ankus_parameter_datum_resolved(const AnkusParameter *parameter, Oid base_type)
        {
            const AnkusValue *value = &parameter->value;
            if (value->auxiliary1 == -6 && value->data != NULL)
                return ankus_parameter_datum(parameter);
            if (base_type != parameter->type_oid)
            {
                AnkusParameter base = *parameter;
                bool is_null = value->is_null != 0;
                Datum datum;
                base.type_oid = base_type;
                datum = ankus_parameter_datum(&base);
                return ankus_coerce_domain(datum, &is_null, parameter->type_oid);
            }

            if (value->auxiliary1 == -5 && value->data != NULL)
            {
                return ankus_raw_parameter(parameter);
            }

            if (value->is_null)
            {
                return (Datum) 0;
            }

            switch (parameter->type_oid)
            {
                case INT4RANGEOID: case INT8RANGEOID: case NUMRANGEOID: case DATERANGEOID: case TSRANGEOID: case TSTZRANGEOID:
                    return ankus_write_range(value, parameter->type_oid);
                case BOOLOID: return BoolGetDatum(value->integral != 0);
                case CHAROID: return CharGetDatum(value->integral);
                case INT2OID: return Int16GetDatum(value->integral);
                case INT4OID: return Int32GetDatum(value->integral);
                case INT8OID: return Int64GetDatum(value->integral);
                case OIDOID: case REGCLASSOID: return ObjectIdGetDatum(value->integral);
                case TIDOID: return ankus_write_item_pointer(value);
                case XIDOID: return TransactionIdGetDatum(value->integral);
                case DATEOID:
                case TIMEOID:
                case TIMETZOID:
                case TIMESTAMPOID:
                case TIMESTAMPTZOID:
                case INTERVALOID:
                    return ankus_write_temporal(value, parameter->type_oid);
                case FLOAT4OID:
                {
                    int32 bits = (int32) value->integral;
                    float4 floating;
                    memcpy(&floating, &bits, sizeof(floating));
                    return Float4GetDatum(floating);
                }

                case FLOAT8OID:
                {
                    float8 floating;
                    memcpy(&floating, &value->integral, sizeof(floating));
                    return Float8GetDatum(floating);
                }

                case VARCHAROID:
                case BPCHAROID:
                    return ankus_write_typed_buffer(value, TEXTOID);
                case TEXTOID:
                case BYTEAOID:
                case CSTRINGOID:
                case UUIDOID:
                case JSONOID:
                case JSONBOID:
                case NUMERICOID:
                case INETOID:
                case CIDROID:
                case POINTOID: case LSEGOID: case LINEOID: case BOXOID: case CIRCLEOID: case PATHOID: case POLYGONOID:
                    return ankus_write_typed_buffer(value, parameter->type_oid);
                default:
                    if (value->auxiliary1 == -9)
                        return ankus_write_detached(value, parameter->type_oid);
                    if (parameter->type_oid == RECORDOID || get_typtype(parameter->type_oid) == TYPTYPE_COMPOSITE)
                        return ankus_write_tuple(value, parameter->type_oid, NULL);
                    if (get_typtype(parameter->type_oid) == TYPTYPE_ENUM)
                        return ankus_write_enum(value, parameter->type_oid);
                    if (ankus_custom_type_supported(parameter->type_oid))
                        return ankus_write_custom(value, parameter->type_oid);
                    if (OidIsValid(get_element_type(parameter->type_oid)))
                    {
                        return ankus_write_array(value, get_element_type(parameter->type_oid));
                    }

                    ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                        errmsg("SPI parameter type OID %u has no Ankus conversion", parameter->type_oid)));
            }

            return (Datum) 0;
        }

        static int ankus_run_spi_statement(AnkusRequest *request, AnkusResult *result);

        static int
        ankus_run_spi_request(AnkusRequest *request, AnkusResult *result)
        {
            /* Read-only SPI statements and cursors use the caller's active snapshot,
             * which PostgreSQL only asserts. Native callbacks such as utility hooks can
             * run in a transaction without one. Take the transaction snapshot only for
             * such a statement, as read-write SPI does for each statement, so callbacks
             * that run no SQL leave the transaction's snapshot timing unchanged. An
             * ERROR leaves the snapshot to the enclosing subtransaction or transaction
             * abort, which releases every snapshot pushed at its level. */
            bool snapshot = request->read_only != 0 && !ActiveSnapshotSet() &&
                (request->operation == ANKUS_SPI_EXECUTE || request->operation == ANKUS_SPI_EXECUTE_PLAN ||
                    request->operation == ANKUS_SPI_OPEN_CURSOR || request->operation == ANKUS_SPI_OPEN_PLAN_CURSOR ||
                    request->operation == ANKUS_SPI_EXPLAIN);
            int code;
            if (snapshot)
            {
                PushActiveSnapshot(GetTransactionSnapshot());
            }

            code = ankus_run_spi_statement(request, result);
            if (snapshot)
            {
                PopActiveSnapshot();
            }

            return code;
        }

        static int
        ankus_run_spi_statement(AnkusRequest *request, AnkusResult *result)
        {
            Oid *types = NULL;
            Datum *values = NULL;
            char *nulls = NULL;
            char *sql;
            /* Cursor and plan bookkeeping needs no parameters or command text. */
            if (request->operation >= ANKUS_SPI_FETCH_CURSOR && request->operation <= ANKUS_SPI_FIND_CURSOR)
            {
                return ankus_cursor_operation(request, result);
            }

            if (request->operation == ANKUS_SPI_KEEP_PLAN)
            {
                ankus_detach_session_plan(request->plan);
                return 0;
            }

            if (request->operation == ANKUS_SPI_FREE_PLAN)
            {
                SPIPlanPtr plan = request->plan;
                request->plan = NULL;
                if (request->session_id != 0)
                {
                    ankus_detach_session_plan(plan);
                }

                return SPI_freeplan(plan);
            }

            /* Preparing needs only the parameter types; running needs their values too. */
            if (request->parameter_count > 0)
            {
                types = palloc(sizeof(Oid) * (Size) request->parameter_count);
                values = palloc(sizeof(Datum) * (Size) request->parameter_count);
                nulls = palloc((Size) request->parameter_count);
                for (int index = 0; index < request->parameter_count; index++)
                {
                    types[index] = request->parameters[index].type_oid;
                    if (request->operation != ANKUS_SPI_PREPARE)
                    {
                        values[index] = ankus_parameter_datum(&request->parameters[index]);
                        nulls[index] = request->parameters[index].value.is_null ? 'n' : ' ';
                    }
                }
            }

            /* A prepared plan runs only with exactly the parameter types it was prepared for. */
            if (request->operation == ANKUS_SPI_EXECUTE_PLAN || request->operation == ANKUS_SPI_OPEN_PLAN_CURSOR)
            {
                if (SPI_getargcount(request->plan) != request->parameter_count)
                {
                    return SPI_ERROR_PARAM;
                }

                for (int index = 0; index < request->parameter_count; index++)
                {
                    if (SPI_getargtypeid(request->plan, index) != types[index])
                    {
                        return SPI_ERROR_PARAM;
                    }
                }

                if (request->operation == ANKUS_SPI_OPEN_PLAN_CURSOR)
                {
                    Portal portal = SPI_cursor_open(NULL, request->plan, values, nulls, request->read_only != 0);
                    return ankus_return_cursor(portal, result);
                }

                return SPI_execute_plan(request->plan, values, nulls, request->read_only != 0, request->limit);
            }

            sql = pg_any_to_server(request->command, request->command_length, PG_UTF8);
            if (request->operation == ANKUS_SPI_EXPLAIN && list_length(pg_parse_query(sql)) != 1)
            {
                ereport(ERROR, (errcode(ERRCODE_SYNTAX_ERROR), errmsg("EXPLAIN requires exactly one SQL statement")));
            }

            if (request->operation == ANKUS_SPI_OPEN_CURSOR)
            {
                Portal portal = SPI_cursor_open_with_args(NULL, sql, request->parameter_count, types, values, nulls,
                    request->read_only != 0, 0);
                return ankus_return_cursor(portal, result);
            }

            if (request->operation == ANKUS_SPI_PREPARE)
            {
                SPIPlanPtr plan = SPI_prepare(sql, request->parameter_count, types);
                int code;
                if (plan == NULL)
                {
                    return SPI_result;
                }

                code = SPI_keepplan(plan);
                if (code == 0)
                {
                    request->plan = plan;
                    if (request->session_id != 0)
                    {
                        ankus_register_session_plan(plan);
                    }
                }

                return code;
            }

            if (request->parameter_count == 0)
            {
                return SPI_execute(sql, request->read_only != 0, request->limit);
            }

            return SPI_execute_with_args(sql, request->parameter_count, types, values, nulls,
                request->read_only != 0, request->limit);
        }

        static void ankus_convert_base_value(Datum datum, Oid type, AnkusValue *value, AnkusInputBuffer *owned, bool detach);

        static void
        ankus_convert_value(Datum datum, Oid type, AnkusValue *value, AnkusInputBuffer *owned, bool detach)
        {
            ankus_convert_base_value(datum, getBaseType(type), value, owned, detach);
        }

        /* Converts a value whose base type the caller has already resolved, such as a result column or an
         * aggregate argument, without another catalog lookup. */
        static void
        ankus_convert_base_value(Datum datum, Oid type, AnkusValue *value, AnkusInputBuffer *owned, bool detach)
        {
            switch (type)
            {
                case INTERNALOID: value->integral = (int64) (uintptr_t) datum; break;
                case INT4RANGEOID: case INT8RANGEOID: case NUMRANGEOID: case DATERANGEOID: case TSRANGEOID: case TSTZRANGEOID:
                    ankus_read_range(datum, value, owned);
                    break;
                case BOOLOID: value->integral = DatumGetBool(datum); break;
                case CHAROID: value->integral = (int8) DatumGetChar(datum); break;
                case INT2OID: value->integral = DatumGetInt16(datum); break;
                case INT4OID: value->integral = DatumGetInt32(datum); break;
                case INT8OID: value->integral = DatumGetInt64(datum); break;
                case OIDOID: case REGCLASSOID: value->integral = DatumGetObjectId(datum); break;
                case TIDOID: ankus_read_item_pointer(datum, value); break;
                case XIDOID: value->integral = DatumGetTransactionId(datum); break;
                case DATEOID:
                case TIMEOID:
                case TIMETZOID:
                case TIMESTAMPOID:
                case TIMESTAMPTZOID:
                case INTERVALOID:
                    ankus_read_temporal(datum, value, type);
                    break;
                case FLOAT4OID:
                {
                    float4 floating = DatumGetFloat4(datum);
                    int32 bits;
                    memcpy(&bits, &floating, sizeof(bits));
                    value->integral = bits;
                    break;
                }

                case FLOAT8OID:
                {
                    float8 floating = DatumGetFloat8(datum);
                    memcpy(&value->integral, &floating, sizeof(floating));
                    break;
                }

                case TEXTOID:
                case VARCHAROID:
                case BPCHAROID:
                case BYTEAOID:
                case CSTRINGOID:
                case UUIDOID:
                case JSONOID:
                case JSONBOID:
                case NUMERICOID:
                case INETOID:
                case CIDROID:
                case POINTOID: case LSEGOID: case LINEOID: case BOXOID: case CIRCLEOID: case PATHOID: case POLYGONOID:
                    ankus_read_typed_buffer(datum, value, owned, type);
                    break;
                default:
                    if (type == RECORDOID || get_typtype(type) == TYPTYPE_COMPOSITE)
                    {
                        ankus_read_tuple(datum, value, owned);
                        break;
                    }

                    /* A row keeps an exact copy of an enum, or an array of one, that the extension does not declare. */
                    if (get_typtype(type) == TYPTYPE_ENUM && (!detach || ankus_enum_supported(type)))
                    {
                        ankus_read_enum(datum, value, owned);
                        break;
                    }

                    if (ankus_custom_type_supported(type))
                    {
                        ankus_read_custom(datum, type, value, owned);
                        break;
                    }

                    if (OidIsValid(get_element_type(type)) && (!detach || (ankus_array_element_supported(get_element_type(type)) &&
                        (get_typtype(getBaseType(get_element_type(type))) != TYPTYPE_ENUM ||
                            ankus_enum_supported(getBaseType(get_element_type(type)))))))
                    {
                        ankus_read_array(datum, value, owned);
                        break;
                    }

                    if (detach)
                    {
                        ankus_read_detached(datum, type, value, owned);
                        break;
                    }

                    ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                        errmsg("SPI result type OID %u has no Ankus conversion", type)));
            }
        }

        static void
        ankus_read_value(Datum datum, Oid type, AnkusValue *value, AnkusInputBuffer *owned)
        {
            ankus_convert_value(datum, type, value, owned, false);
        }

        /* A by-value datum keeps its word. Other storage keeps its bytes: a varlena without its header,
         * a C string without its terminator, and fixed-length storage whole. */
        static void
        ankus_read_detached(Datum datum, Oid type, AnkusValue *value, AnkusInputBuffer *owned)
        {
            int16 length;
            bool by_value;
            get_typlenbyval(type, &length, &by_value);
            value->auxiliary1 = -9;
            value->auxiliary2 = (int32) type;
            value->temporal_infinity = by_value;
            if (by_value)
            {
                value->integral = (int64) datum;
                return;
            }

            value->integral = length;
            if (length == -1)
            {
                ankus_read_buffer(datum, value, owned, false);
            }
            else if (length == -2)
            {
                value->data = (unsigned char *) DatumGetCString(datum);
                value->length = strlen((const char *) value->data);
            }
            else
            {
                value->data = (unsigned char *) DatumGetPointer(datum);
                value->length = length;
            }
        }

        static Datum
        ankus_write_detached(const AnkusValue *value, Oid type)
        {
            int16 length;
            bool by_value;
            get_typlenbyval(type, &length, &by_value);
            if ((Oid) value->auxiliary2 != type || value->temporal_infinity != (by_value ? 1 : 0) || value->length < 0 ||
                (value->length != 0 && value->data == NULL) || (by_value ? value->length != 0 : value->integral != length) ||
                (length > 0 && !by_value && value->length != length) ||
                (length == -2 && value->length != 0 && memchr(value->data, '\0', value->length) != NULL))
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH),
                    errmsg("Detached value does not match PostgreSQL type %s", format_type_be(type))));
            if (by_value)
                return (Datum) value->integral;
            if (length == -1)
                return ankus_write_buffer(value, false);
            if (length == -2)
                return CStringGetDatum(pnstrdup(value->length == 0 ? "" : (const char *) value->data, value->length));
            char *copy = palloc(length);
            memcpy(copy, value->data, length);
            return PointerGetDatum(copy);
        }

        /* A result cell keeps an exact copy of a value Ankus cannot convert, and its type is its column's base
         * type, resolved once per result. Other results convert from their declared type. */
        static void
        ankus_result_converted(Datum datum, Oid type, AnkusValue *value, bool cell)
        {
            AnkusValue input = {0};
            AnkusInputBuffer owned = {0};
            if (cell)
                ankus_convert_base_value(datum, type, &input, &owned, true);
            else
                ankus_convert_value(datum, type, &input, &owned, false);
            *value = input;
            value->data = NULL;
            if (input.data != NULL)
            {
                ankus_copy_owned(value, input.data, input.length);
            }

            ankus_free_input(&owned);
        }

        static void
        ankus_result_value(Datum datum, Oid type, AnkusValue *value)
        {
            ankus_result_converted(datum, type, value, false);
        }

        static void
        ankus_result_cell(Datum datum, Oid base_type, AnkusValue *value)
        {
            ankus_result_converted(datum, base_type, value, true);
        }

        static void
        ankus_collect_result(AnkusResult *result, int first_row_columns, intptr_t raw_context, uintptr_t raw_generation)
        {
            if (raw_context != 0)
                ankus_datum_context(raw_context, raw_generation);
            TupleDesc descriptor;
            Size count;
            uint64 rows;
            int columns;
            bool *validated;
            if (SPI_tuptable == NULL)
            {
                return;
            }

            descriptor = SPI_tuptable->tupdesc;
            rows = first_row_columns > 0 ? Min(SPI_processed, 1) : SPI_processed;
            columns = first_row_columns > 0 ? Min(descriptor->natts, first_row_columns) : descriptor->natts;
            if (rows > PG_INT32_MAX || (columns != 0 && rows > MaxAllocSize / sizeof(AnkusValue) / columns))
            {
                ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("SPI result exceeds managed array capacity")));
            }

            result->row_count = (int32) rows;
            result->column_count = columns;
            count = (Size) result->row_count * result->column_count;
            validated = palloc0(sizeof(bool) * Max(result->column_count, 1));
            result->columns = calloc(Max(result->column_count, 1), sizeof(AnkusColumn));
            result->values = calloc(Max(count, 1), sizeof(AnkusValue));
            if (result->columns == NULL || result->values == NULL)
            {
                ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("Unable to allocate SPI result")));
            }

            for (int column = 0; column < result->column_count; column++)
            {
                Form_pg_attribute attribute = TupleDescAttr(descriptor, column);
                const char *name = NameStr(attribute->attname);
                char *utf8 = pg_server_to_any(name, strlen(name), PG_UTF8);
                result->columns[column].type_oid = attribute->atttypid;
                result->columns[column].base_type_oid = getBaseType(attribute->atttypid);
                ankus_copy_owned(&result->columns[column].name, (unsigned char *) utf8, strlen(utf8));
                if (utf8 != name)
                {
                    pfree(utf8);
                }
            }

            for (int row = 0; row < result->row_count; row++)
            {
                for (int column = 0; column < result->column_count; column++)
                {
                    bool is_null;
                    Datum datum = SPI_getbinval(SPI_tuptable->vals[row], descriptor, column + 1, &is_null);
                    AnkusValue *value = &result->values[(Size) row * result->column_count + column];
                    value->is_null = is_null;
                    if (!is_null)
                    {
                        if (raw_context != 0)
                        {
                            value->integral = (int64) (uintptr_t) ankus_copy_raw_datum(datum,
                                result->columns[column].type_oid, raw_context, raw_generation);
                            continue;
                        }

                        if (!validated[column])
                        {
                            ankus_check_result_enum(result->columns[column].base_type_oid);
                            validated[column] = true;
                        }

                        ankus_result_cell(datum, result->columns[column].base_type_oid, value);
                    }
                }
            }
        }

        """;
}
