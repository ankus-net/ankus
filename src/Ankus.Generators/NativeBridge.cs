namespace Ankus.Generators;

/// <summary>
/// Defines the native transport ABI and PostgreSQL-owned varlena conversion routines used by generated entry points.
/// </summary>
internal static class NativeBridge
{
    /// <summary>
    /// Gets the C preamble compiled against the selected PostgreSQL installation's headers.
    /// </summary>
    internal const string Source = """
        #include "postgres.h"
        #include "fmgr.h"
        #if PG_VERSION_NUM >= 160000
        #include "varatt.h"
        #endif
        #include "utils/builtins.h"
        #include "catalog/pg_type_d.h"
        #include "utils/uuid.h"
        #include "utils/date.h"
        #include "utils/timestamp.h"
        #include "mb/pg_wchar.h"

        /* A PG_TRY nested in another gives its bookkeeping variables their own names, so they do not hide the outer
         * block's. PostgreSQL 16 takes the suffix itself; earlier releases get the same expansion with renamed
         * variables. */
        #if PG_VERSION_NUM >= 160000
        #define ANKUS_PG_TRY_INNER() PG_TRY(_inner)
        #define ANKUS_PG_CATCH_INNER() PG_CATCH(_inner)
        #define ANKUS_PG_FINALLY_INNER() PG_FINALLY(_inner)
        #define ANKUS_PG_END_TRY_INNER() PG_END_TRY(_inner)
        #else
        #define ANKUS_PG_TRY_INNER() \
            do { \
                sigjmp_buf *_save_exception_stack_inner = PG_exception_stack; \
                ErrorContextCallback *_save_context_stack_inner = error_context_stack; \
                sigjmp_buf _local_sigjmp_buf_inner; \
                bool _do_rethrow_inner = false; \
                if (sigsetjmp(_local_sigjmp_buf_inner, 0) == 0) \
                { \
                    PG_exception_stack = &_local_sigjmp_buf_inner
        #define ANKUS_PG_CATCH_INNER() \
                } \
                else \
                { \
                    PG_exception_stack = _save_exception_stack_inner; \
                    error_context_stack = _save_context_stack_inner
        #define ANKUS_PG_FINALLY_INNER() \
                } \
                else \
                    _do_rethrow_inner = true; \
                { \
                    PG_exception_stack = _save_exception_stack_inner; \
                    error_context_stack = _save_context_stack_inner
        #define ANKUS_PG_END_TRY_INNER() \
                } \
                if (_do_rethrow_inner) \
                    PG_RE_THROW(); \
                PG_exception_stack = _save_exception_stack_inner; \
                error_context_stack = _save_context_stack_inner; \
            } while (0)
        #endif

        #include "utils/memutils.h"

        /* PostgreSQL propagates an ERROR with ErrorContext current. A handler that cleans up and rethrows,
         * as logical decoding's does, restores whatever was current when it caught the error, after that
         * cleanup may have deleted other contexts, and an outer handler then allocates there. Code that a
         * propagating error passes through therefore leaves the context it arrived in current: a catch
         * block switches back to it before rethrowing, and a finally block restores its caller's context
         * only when no error has left ErrorContext current. */
        #define ANKUS_FINALLY_RESTORE(caller) \
            do { \
                if (CurrentMemoryContext != ErrorContext) \
                    MemoryContextSwitchTo(caller); \
            } while (0)

        typedef struct AnkusValue
        {
            int64 integral;
            int32 auxiliary1;
            int32 auxiliary2;
            int32 temporal_infinity;
            unsigned char *data;
            int32 length;
            uint8 is_null;
            void (*release)(void *);
        } AnkusValue;

        typedef struct AnkusInputBuffer
        {
            struct varlena *detoasted;
            char *converted;
            char *serialized;
        } AnkusInputBuffer;

        """;

    /// <summary>
    /// Gets native input detoasting, encoding conversion, and temporary-buffer cleanup helpers.
    /// </summary>
    internal const string ReadBuffers = """

        static inline void
        ankus_read_buffer(Datum datum, AnkusValue *value, AnkusInputBuffer *owned, bool is_text)
        {
            struct varlena *original = (struct varlena *) DatumGetPointer(datum);
            struct varlena *unpacked = pg_detoast_datum_packed(original);
            if (unpacked != original)
            {
                owned->detoasted = unpacked;
            }

            value->data = (unsigned char *) VARDATA_ANY(unpacked);
            value->length = VARSIZE_ANY_EXHDR(unpacked);
            if (is_text)
            {
                char *converted = pg_server_to_any((const char *) value->data, value->length, PG_UTF8);
                if (converted != (char *) value->data)
                {
                    owned->converted = converted;
                    value->data = (unsigned char *) converted;
                    value->length = strlen(converted);
                }
            }
        }

        static inline void
        ankus_free_input(AnkusInputBuffer *owned)
        {
            if (owned->converted != NULL)
            {
                pfree(owned->converted);
            }

            if (owned->detoasted != NULL)
            {
                pfree(owned->detoasted);
            }

            if (owned->serialized != NULL)
            {
                pfree(owned->serialized);
            }
        }

        """;

    /// <summary>
    /// Gets native output encoding and PostgreSQL memory-context allocation helpers.
    /// </summary>
    internal const string WriteBuffer = """

        static inline Datum
        ankus_write_buffer(const AnkusValue *value, bool is_text)
        {
            const char *data = (const char *) value->data;
            int length = value->length;
            char *converted = NULL;
            struct varlena *result;
            if (is_text)
            {
                converted = pg_any_to_server(data, length, PG_UTF8);
                if (converted != data)
                {
                    data = converted;
                    length = strlen(converted);
                }
            }

            result = (struct varlena *) palloc((Size) length + VARHDRSZ);
            SET_VARSIZE(result, length + VARHDRSZ);
            memcpy(VARDATA(result), data, length);
            if (converted != NULL && converted != (char *) value->data)
            {
                pfree(converted);
            }

            return PointerGetDatum(result);
        }

        """;
}
