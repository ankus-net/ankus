namespace Ankus.Generators;

/// <summary>
/// Converts PostgreSQL arrays to a pointer-free transport using the scalar datum converters.
/// </summary>
internal static class NativeArrayBridge
{
    /// <summary>
    /// Gets the native array readers and writers, called only behind the PostgreSQL error boundary.
    /// </summary>
    internal const string Source = """
        #include "utils/array.h"
        #include "libpq/pqformat.h"
        #include "miscadmin.h"

        static bool
        ankus_array_element_supported(Oid type)
        {
            type = getBaseType(type);
            switch (type)
            {
                case BOOLOID: case BYTEAOID: case CHAROID: case INT2OID: case INT4OID: case INT8OID:
                case OIDOID: case REGCLASSOID: case TIDOID: case XIDOID: case FLOAT4OID: case FLOAT8OID: case TEXTOID: case VARCHAROID: case BPCHAROID:
                case UUIDOID: case JSONOID: case JSONBOID: case NUMERICOID: case DATEOID: case TIMEOID:
                case TIMETZOID: case TIMESTAMPOID: case TIMESTAMPTZOID: case INTERVALOID:
                case INETOID: case CIDROID: case CSTRINGOID:
                case INT4RANGEOID: case INT8RANGEOID: case NUMRANGEOID: case DATERANGEOID: case TSRANGEOID: case TSTZRANGEOID:
                case POINTOID: case LSEGOID: case LINEOID: case BOXOID: case CIRCLEOID: case PATHOID: case POLYGONOID:
                    return true;
                default:
                    return type == RECORDOID || get_typtype(type) == TYPTYPE_ENUM || get_typtype(type) == TYPTYPE_COMPOSITE ||
                        ankus_custom_type_supported(type);
            }
        }

        /* Elements whose array storage is a packed native array: fixed width, stored at their own alignment. */
        static int
        ankus_array_packed_width(Oid type)
        {
            switch (type)
            {
                case BOOLOID: case CHAROID: return 1;
                case INT2OID: return 2;
                case INT4OID: case OIDOID: case FLOAT4OID: return 4;
                case INT8OID: case FLOAT8OID: return 8;
                default: return 0;
            }
        }

        static void
        ankus_read_array(Datum datum, AnkusValue *value, AnkusInputBuffer *owned)
        {
            ArrayType *array = DatumGetArrayTypeP(datum);
            Oid type = ARR_ELEMTYPE(array);
            Oid base_type = getBaseType(type);
            bool composite = base_type == RECORDOID || get_typtype(base_type) == TYPTYPE_COMPOSITE;
            int16 length;
            bool by_value;
            char alignment;
            Datum *elements;
            bool *nulls;
            int count;
            int rank = ARR_NDIM(array);
            StringInfoData buffer;
            check_stack_depth();
            if (!ankus_array_element_supported(base_type))
            {
                ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                    errmsg("Array element type OID %u has no Ankus conversion", type)));
            }

            if ((Pointer) array != DatumGetPointer(datum))
            {
                owned->detoasted = (struct varlena *) array;
            }

            if (!ARR_HASNULL(array) && ankus_array_packed_width(base_type) != 0)
            {
                /* Without NULLs these elements are already a packed native array; managed code copies it once. */
                int width = ankus_array_packed_width(base_type);
                count = ArrayGetNItems(rank, ARR_DIMS(array));
                initStringInfo(&buffer);
                pq_sendint32(&buffer, rank);
                pq_sendint32(&buffer, count);
                pq_sendint32(&buffer, base_type);
                for (int index = 0; index < rank; index++)
                {
                    pq_sendint32(&buffer, ARR_DIMS(array)[index]);
                    pq_sendint32(&buffer, ARR_LBOUND(array)[index]);
                }

                appendBinaryStringInfo(&buffer, ARR_DATA_PTR(array), count * width);
                owned->serialized = buffer.data;
                value->data = (unsigned char *) buffer.data;
                value->length = buffer.len;
                value->auxiliary1 = -1;
                value->auxiliary2 = 4;
                value->integral = 0;
                return;
            }

            get_typlenbyvalalign(type, &length, &by_value, &alignment);
            deconstruct_array(array, type, length, by_value, alignment, &elements, &nulls, &count);
            initStringInfo(&buffer);
            pq_sendint32(&buffer, rank);
            pq_sendint32(&buffer, count);
            pq_sendint32(&buffer, composite ? type : base_type);
            for (int index = 0; index < rank; index++)
            {
                pq_sendint32(&buffer, ARR_DIMS(array)[index]);
                pq_sendint32(&buffer, ARR_LBOUND(array)[index]);
            }

            for (int index = 0; index < count; index++)
            {
                AnkusValue item = {0};
                AnkusInputBuffer item_owned = {0};
                CHECK_FOR_INTERRUPTS();
                item.is_null = nulls[index];
                if (!item.is_null)
                {
                    ankus_read_value(elements[index], base_type, &item, &item_owned);
                }

                pq_sendint64(&buffer, item.integral);
                pq_sendint32(&buffer, item.auxiliary1);
                pq_sendint32(&buffer, item.auxiliary2);
                pq_sendint32(&buffer, item.temporal_infinity);
                pq_sendint32(&buffer, item.is_null);
                pq_sendint32(&buffer, item.length);
                if (item.length != 0)
                {
                    pq_sendbytes(&buffer, (char *) item.data, item.length);
                }

                ankus_free_input(&item_owned);
            }

            if (elements != NULL)
            {
                pfree(elements);
            }

            if (nulls != NULL)
            {
                pfree(nulls);
            }

            owned->serialized = buffer.data;
            value->data = (unsigned char *) buffer.data;
            value->length = buffer.len;
            value->auxiliary1 = -1;
            value->auxiliary2 = composite ? 2 : get_typtype(base_type) == TYPTYPE_ENUM ? 1 : ankus_custom_type_supported(base_type) ? 3 : 0;
            value->integral = composite ? base_type : 0;
        }

        static Datum
        ankus_write_array(const AnkusValue *value, Oid element_type)
        {
            StringInfoData buffer = {(char *) value->data, value->length, value->length, 0};
            int rank;
            int count;
            int lengths[MAXDIM];
            int bounds[MAXDIM];
            Datum *elements;
            bool *nulls;
            int16 length;
            bool by_value;
            char alignment;
            ArrayType *array;
            Oid transported_type;
            Oid base_type = getBaseType(element_type);
            /* Managed code sends fixed-width primitives without NULLs as PostgreSQL's packed storage. */
            int packed = value->auxiliary2 == 4 ? ankus_array_packed_width(element_type) : 0;
            check_stack_depth();
            if (value->auxiliary1 != -1 || value->length < 12 || value->data == NULL)
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid Ankus array header")));
            }

            rank = pq_getmsgint(&buffer, 4);
            count = pq_getmsgint(&buffer, 4);
            transported_type = pq_getmsgint(&buffer, 4);
            if (rank < 0 || rank > MAXDIM || count < 0 ||
                (transported_type != element_type && !(get_typtype(base_type) == TYPTYPE_COMPOSITE &&
                    (transported_type == RECORDOID || getBaseType(transported_type) == base_type))) ||
                !ankus_array_element_supported(element_type) ||
                (value->auxiliary2 == 4 && (packed == 0 ||
                    (int64) 12 + rank * 8 + (int64) count * packed != value->length)) ||
                (value->auxiliary2 != 4 && (int64) 12 + rank * 8 + (int64) count * 28 > value->length))
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid Ankus array shape or element type")));
            }

            for (int index = 0; index < rank; index++)
            {
                lengths[index] = pq_getmsgint(&buffer, 4);
                bounds[index] = pq_getmsgint(&buffer, 4);
            }

            if (ArrayGetNItems(rank, lengths) != count || (count == 0 && rank != 0))
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Inconsistent Ankus array dimensions")));
            }

            ArrayCheckBounds(rank, lengths, bounds);
            if (packed != 0)
            {
                Size bytes = (Size) count * packed;
                Size size = ARR_OVERHEAD_NONULLS(rank) + bytes;
                const char *data = pq_getmsgbytes(&buffer, (int) bytes);
                pq_getmsgend(&buffer);
                if (element_type == BOOLOID)
                {
                    for (int index = 0; index < count; index++)
                    {
                        if ((unsigned char) data[index] > 1)
                        {
                            ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid Ankus Boolean array element")));
                        }
                    }
                }

                if (!AllocSizeIsValid(size))
                {
                    ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("array size exceeds the maximum allowed (%d)", (int) MaxAllocSize)));
                }

                array = (ArrayType *) palloc0(size);
                SET_VARSIZE(array, size);
                array->ndim = rank;
                array->dataoffset = 0;
                array->elemtype = element_type;
                if (rank > 0)
                {
                    memcpy(ARR_DIMS(array), lengths, rank * sizeof(int));
                    memcpy(ARR_LBOUND(array), bounds, rank * sizeof(int));
                    memcpy(ARR_DATA_PTR(array), data, bytes);
                }

                return PointerGetDatum(array);
            }

            get_typlenbyvalalign(element_type, &length, &by_value, &alignment);
            elements = palloc(sizeof(Datum) * (Size) Max(count, 1));
            nulls = palloc(sizeof(bool) * (Size) Max(count, 1));
            for (int index = 0; index < count; index++)
            {
                AnkusParameter parameter = {0};
                int is_null;
                const char *data;
                char *terminated = NULL;
                CHECK_FOR_INTERRUPTS();
                parameter.type_oid = element_type;
                parameter.value.integral = pq_getmsgint64(&buffer);
                parameter.value.auxiliary1 = pq_getmsgint(&buffer, 4);
                parameter.value.auxiliary2 = pq_getmsgint(&buffer, 4);
                parameter.value.temporal_infinity = pq_getmsgint(&buffer, 4);
                is_null = pq_getmsgint(&buffer, 4);
                parameter.value.length = pq_getmsgint(&buffer, 4);
                if (is_null < 0 || is_null > 1 || parameter.value.length < 0 ||
                    (is_null && parameter.value.length != 0))
                {
                    ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid Ankus array element")));
                }

                nulls[index] = is_null;
                parameter.value.is_null = is_null;
                data = pq_getmsgbytes(&buffer, parameter.value.length);
                /* These scalar input routines consume C strings rather than length-delimited buffers. */
                if (!is_null && (base_type == JSONOID || base_type == JSONBOID))
                {
                    terminated = pnstrdup(data, parameter.value.length);
                    data = terminated;
                }

                parameter.value.data = (unsigned char *) data;
                elements[index] = ankus_parameter_datum(&parameter);
                if (terminated != NULL)
                {
                    pfree(terminated);
                }
            }

            pq_getmsgend(&buffer);
            array = construct_md_array(elements, nulls, rank, lengths, bounds, element_type, length, by_value, alignment);
            for (int index = 0; index < count; index++)
            {
                if (!by_value && !nulls[index])
                {
                    pfree(DatumGetPointer(elements[index]));
                }
            }

            pfree(elements);
            pfree(nulls);
            return PointerGetDatum(array);
        }

        """;
}
