namespace Ankus.Generators;

/// <summary>
/// Converts buffered scalar datums while keeping PostgreSQL parsing, detoasting, and allocation in native frames.
/// </summary>
internal static class NativeExtendedTypes
{
    /// <summary>
    /// Gets buffered datum conversion helpers shared by generated functions and SPI.
    /// </summary>
    internal const string Source = """
        #include "utils/fmgrprotos.h"
        #include "libpq/pqcomm.h"
        #include "utils/inet.h"
        #include "libpq/pqformat.h"

        static void
        ankus_read_typed_buffer(Datum datum, AnkusValue *value, AnkusInputBuffer *owned, Oid type)
        {
            if (type == CSTRINGOID)
            {
                char *text = DatumGetCString(datum);
                if (text == NULL)
                {
                    value->is_null = true;
                    return;
                }

                Size length = strlen(text);
                if (length >= MaxAllocSize)
                {
                    ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("C string exceeds supported byte capacity")));
                }

                value->data = (unsigned char *) text;
                value->length = (int) length;
            }
            else if (type == UUIDOID)
            {
                value->data = DatumGetUUIDP(datum)->data;
                value->length = UUID_LEN;
            }
            else if (ankus_geometry_io(type, false) != NULL)
            {
                ankus_read_geometry(datum, value, owned, type);
            }
            else if (type == INETOID || type == CIDROID)
            {
                bytea *wire = DatumGetByteaP(DirectFunctionCall1(type == INETOID ? inet_send : cidr_send, datum));
                owned->serialized = (char *) wire;
                value->data = (unsigned char *) VARDATA(wire);
                value->length = VARSIZE(wire) - VARHDRSZ;
                value->data[0] = value->data[0] == PGSQL_AF_INET ? 4 : 6;
            }
            else if (type == NUMERICOID)
            {
                struct varlena *original = (struct varlena *) DatumGetPointer(datum);
                struct varlena *unpacked = pg_detoast_datum(original);
                bytea *wire;
                Size raw_length = VARSIZE(unpacked);
                Size wire_length;
                if (unpacked != original)
                {
                    owned->detoasted = unpacked;
                }

                wire = DatumGetByteaP(DirectFunctionCall1(numeric_send, PointerGetDatum(unpacked)));
                owned->converted = (char *) wire;
                wire_length = VARSIZE(wire) - VARHDRSZ;
                if (raw_length >= MaxAllocSize || wire_length >= MaxAllocSize - raw_length)
                {
                    ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("Numeric transport exceeds supported byte capacity")));
                }

                owned->serialized = palloc(raw_length + wire_length);
                memcpy(owned->serialized, unpacked, raw_length);
                memcpy(owned->serialized + raw_length, VARDATA(wire), wire_length);
                value->data = (unsigned char *) owned->serialized;
                value->length = (int) (raw_length + wire_length);
                value->integral = (int64) raw_length;
                value->auxiliary1 = -8;
            }
            else if (type == JSONBOID)
            {
                struct varlena *original = (struct varlena *) DatumGetPointer(datum);
                struct varlena *unpacked = pg_detoast_datum(original);
                char *converted;
                if (unpacked != original)
                {
                    owned->detoasted = unpacked;
                }

                owned->serialized = DatumGetCString(DirectFunctionCall1(jsonb_out,
                    PointerGetDatum(unpacked)));
                converted = pg_server_to_any(owned->serialized, strlen(owned->serialized), PG_UTF8);
                if (converted != owned->serialized)
                {
                    owned->converted = converted;
                }

                value->data = (unsigned char *) converted;
                value->length = strlen(converted);
            }
            else
            {
                ankus_read_buffer(datum, value, owned, type != BYTEAOID);
            }
        }

        static Datum
        ankus_write_typed_buffer(const AnkusValue *value, Oid type)
        {
            /* A C string must not contain a NUL byte, since PostgreSQL would end it there. */
            if (type == CSTRINGOID)
            {
                if (value->length < 0 || (Size) value->length >= MaxAllocSize ||
                    (value->length != 0 && (value->data == NULL || memchr(value->data, 0, value->length) != NULL)))
                {
                    ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid C string byte transport")));
                }

                char *text = palloc((Size) value->length + 1);
                if (value->length != 0)
                {
                    memcpy(text, value->data, value->length);
                }

                text[value->length] = '\0';
                return CStringGetDatum(text);
            }

            if (type == UUIDOID)
            {
                pg_uuid_t *uuid;
                if (value->length != UUID_LEN)
                {
                    ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid UUID transport length")));
                }

                uuid = palloc(sizeof(pg_uuid_t));
                memcpy(uuid->data, value->data, UUID_LEN);
                return UUIDPGetDatum(uuid);
            }

            if (ankus_geometry_io(type, true) != NULL)
            {
                return ankus_write_geometry(value, type);
            }

            /* Network addresses arrive in PostgreSQL's binary form with the family as 4 or 6 instead of PostgreSQL's
             * constants, and go through its receive function. */
            if (type == INETOID || type == CIDROID)
            {
                char bytes[20];
                StringInfoData buffer;
                Datum result;
                if ((value->length != 8 && value->length != 20) || value->data == NULL ||
                    value->data[0] != (value->length == 8 ? 4 : 6) ||
                    value->data[2] != (type == CIDROID ? 1 : 0) || value->data[3] != value->length - 4)
                    ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid network transport header")));
                memcpy(bytes, value->data, value->length);
                bytes[0] = bytes[0] == 4 ? PGSQL_AF_INET : PGSQL_AF_INET6;
                buffer.data = bytes;
                buffer.len = value->length;
                buffer.maxlen = sizeof(bytes);
                buffer.cursor = 0;
                result = DirectFunctionCall1(type == INETOID ? inet_recv : cidr_recv, PointerGetDatum(&buffer));
                pq_getmsgend(&buffer);
                return result;
            }

            /* A numeric arrives either as its exact datum, when managed code kept one, or in binary form. */
            if (type == NUMERICOID)
            {
                Size raw_length;
                StringInfoData buffer;
                Datum result;
                if (value->auxiliary1 != -8 || value->data == NULL || value->length < 8 ||
                    value->integral < 0 || value->integral > value->length - 8)
                {
                    ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid numeric binary transport")));
                }

                raw_length = (Size) value->integral;
                if (raw_length != 0)
                {
                    struct varlena *copy;
                    if (raw_length < VARHDRSZ || raw_length >= MaxAllocSize)
                    {
                        ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid numeric datum length")));
                    }

                    copy = palloc(raw_length);
                    memcpy(copy, value->data, raw_length);
                    if (VARATT_IS_EXTENDED(copy) || (Size) VARSIZE(copy) != raw_length)
                    {
                        pfree(copy);
                        ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("Invalid numeric datum header")));
                    }

                    return PointerGetDatum(copy);
                }

                buffer.data = (char *) value->data;
                buffer.len = value->length;
                buffer.maxlen = value->length;
                buffer.cursor = 0;
        #if PG_VERSION_NUM < 140000
                if ((value->data[4] == 0xD0 || value->data[4] == 0xF0) && value->data[5] == 0)
                {
                    ereport(ERROR, (errcode(ERRCODE_INVALID_TEXT_REPRESENTATION),
                        errmsg("invalid input syntax for type numeric: \"%s\"", value->data[4] == 0xD0 ? "Infinity" : "-Infinity")));
                }
        #endif
                result = DirectFunctionCall3(numeric_recv, PointerGetDatum(&buffer), ObjectIdGetDatum(InvalidOid), Int32GetDatum(-1));
                pq_getmsgend(&buffer);
                return result;
            }

            if (type == JSONOID || type == JSONBOID)
            {
                char *text = pg_any_to_server((char *) value->data, value->length, PG_UTF8);
                Datum result = type == JSONOID
                    ? DirectFunctionCall1(json_in, CStringGetDatum(text))
                    : DirectFunctionCall1(jsonb_in, CStringGetDatum(text));
                if (text != (char *) value->data)
                {
                    pfree(text);
                }

                return result;
            }

            return ankus_write_buffer(value, type != BYTEAOID);
        }

        """;
}
