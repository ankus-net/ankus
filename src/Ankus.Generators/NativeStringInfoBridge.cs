namespace Ankus.Generators;

/// <summary>
/// Emits checked StringInfo operations using the selected PostgreSQL version's native layout.
/// </summary>
internal static class NativeStringInfoBridge
{
    /// <summary>
    /// Gets the two-allocation ownership registry and guarded buffer operations.
    /// </summary>
    internal const string Source = """

        #include <limits.h>
        #include "lib/stringinfo.h"

        typedef enum AnkusStringInfoOperation
        {
            ANKUS_STRINGINFO_CREATE = 1,
            ANKUS_STRINGINFO_INSPECT = 2,
            ANKUS_STRINGINFO_APPEND = 3,
            ANKUS_STRINGINFO_READ = 4,
            ANKUS_STRINGINFO_WRITE = 5,
            ANKUS_STRINGINFO_RESET = 6,
            ANKUS_STRINGINFO_ENLARGE = 7,
            ANKUS_STRINGINFO_ENSURE_CAPACITY = 8,
            ANKUS_STRINGINFO_DISPOSE = 9,
            ANKUS_STRINGINFO_DETACH = 10,
            ANKUS_STRINGINFO_DETACH_DATA = 11,
            ANKUS_STRINGINFO_DETACH_CSTRING = 12
        } AnkusStringInfoOperation;

        typedef struct AnkusStringInfo AnkusStringInfo;
        struct AnkusStringInfo
        {
            uint64 id;
            uint64 context_id;
            StringInfo buffer;
            AnkusStringInfo *next;
        };

        static AnkusStringInfo *ankus_stringinfos;
        static uint64 ankus_stringinfo_next_id = 1;

        static void
        ankus_stringinfo_remove_context(uint64 context_id)
        {
            AnkusStringInfo **slot = &ankus_stringinfos;
            while (*slot != NULL)
            {
                AnkusStringInfo *entry = *slot;
                if (entry->context_id == context_id)
                {
                    *slot = entry->next;
                    /* PostgreSQL owns both payload allocations and reclaims them itself. */
                    free(entry);
                    continue;
                }

                slot = &entry->next;
            }
        }

        static void
        ankus_stringinfo_remove(AnkusStringInfo *entry)
        {
            AnkusStringInfo **slot = &ankus_stringinfos;
            while (*slot != NULL)
            {
                if (*slot == entry)
                {
                    *slot = entry->next;
                    free(entry);
                    return;
                }

                slot = &(*slot)->next;
            }
        }

        static AnkusStringInfo *
        ankus_stringinfo_find(uint64 id)
        {
            for (AnkusStringInfo *entry = ankus_stringinfos; entry != NULL; entry = entry->next)
            {
                if (entry->id == id && ankus_memory_context_by_id(entry->context_id) != NULL)
                {
                    return entry;
                }
            }

            return NULL;
        }

        static void
        ankus_stringinfo_snapshot(StringInfo buffer, uint64 context_id, AnkusMemoryResult *result)
        {
            result->context = (intptr_t) context_id;
            result->pointer = (intptr_t) buffer;
            result->data = (intptr_t) buffer->data;
            result->length = (uintptr_t) buffer->len;
            result->value = buffer->maxlen;
        }

        static void
        ankus_stringinfo_create(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            AnkusMemoryContext *context = ankus_memory_context_from_request(request);
            ankus_memory_check_chunk_operation(context->context, "StringInfo allocation");
            if (request->value < 0 || (uintptr_t) request->value >= MaxAllocSize ||
                request->length >= MaxAllocSize || (request->length != 0 && request->data == 0))
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid StringInfo initial capacity or data")));
            }

            AnkusStringInfo *entry = calloc(1, sizeof(*entry));
            if (entry == NULL || ankus_stringinfo_next_id == 0)
            {
                free(entry);
                ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("unable to register an Ankus StringInfo")));
            }

            entry->id = ankus_stringinfo_next_id++;
            entry->context_id = context->id;
            MemoryContext caller = CurrentMemoryContext;
            PG_TRY();
            {
                MemoryContextSwitchTo(context->context);
                /* The zeroed struct permits cleanup when initializing its separate data fails. */
                entry->buffer = palloc0(sizeof(StringInfoData));
                initStringInfo(entry->buffer);
                int capacity = (int) Max((uintptr_t) request->value, request->length);
                enlargeStringInfo(entry->buffer, capacity);
                appendBinaryStringInfo(entry->buffer,
                    request->length == 0 ? "" : (const char *) request->data, (int) request->length);
                MemoryContextSwitchTo(caller);
            }
            PG_CATCH();
            {
                MemoryContext propagating = MemoryContextSwitchTo(caller);
                if (entry->buffer != NULL)
                {
                    if (entry->buffer->data != NULL)
                    {
                        pfree(entry->buffer->data);
                    }

                    pfree(entry->buffer);
                }

                free(entry);
                MemoryContextSwitchTo(propagating);
                PG_RE_THROW();
            }
            PG_END_TRY();
            entry->next = ankus_stringinfos;
            ankus_stringinfos = entry;
            ankus_stringinfo_snapshot(entry->buffer, entry->context_id, result);
            result->pointer = (intptr_t) entry->id;
        }

        static void
        ankus_stringinfo_validate(StringInfo buffer)
        {
            if (buffer->len < 0 || buffer->maxlen < 0 ||
                (buffer->maxlen != 0 && (buffer->maxlen <= buffer->len || (Size) buffer->maxlen > MaxAllocSize)) ||
                (buffer->data == NULL && (buffer->len != 0 || buffer->maxlen != 0)))
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid native StringInfo buffer")));
            }
        }

        static void
        ankus_stringinfo_append(StringInfo buffer, AnkusMemoryRequest *request)
        {
            if (request->length > INT_MAX || (request->length != 0 && request->data == 0))
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid StringInfo append source")));
            }

            const char *source = request->length == 0 ? "" : (const char *) request->data;
            /* Compare integer addresses: relational comparisons between unrelated C pointers
             * are undefined. Preserve aliases across repalloc without retaining old storage. */
            uintptr_t address = (uintptr_t) source;
            uintptr_t base = (uintptr_t) buffer->data;
            bool aliases = request->length != 0 && address >= base && address - base < (uintptr_t) buffer->maxlen;
            Size offset = aliases ? address - base : 0;
            if (aliases && (offset > (Size) buffer->len || request->length > (Size) buffer->len - offset))
            {
                ereport(ERROR, (errcode(ERRCODE_DATA_EXCEPTION), errmsg("StringInfo append source exceeds the existing payload")));
            }

            enlargeStringInfo(buffer, (int) request->length);
            if (aliases)
            {
                source = buffer->data + offset;
            }

            appendBinaryStringInfo(buffer, source, (int) request->length);
        }

        static void
        ankus_stringinfo_execute(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            AnkusStringInfoOperation operation = (AnkusStringInfoOperation) request->flags;
            if (operation == ANKUS_STRINGINFO_CREATE)
            {
                ankus_stringinfo_create(request, result);
                return;
            }

            /* A borrowed buffer is valid while its anchor context keeps the generation it captured; an owned one while
             * its registration lasts. */
            AnkusStringInfo *owned = NULL;
            StringInfo buffer;
            uint64 context_id;
            if (request->pointer != 0)
            {
                AnkusMemoryContext *anchor = ankus_memory_context_from_request(request);
                if (anchor->generation == 0 || anchor->generation != (uintptr_t) request->other)
                {
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                        errmsg("the borrowed PostgreSQL StringInfo is stale")));
                }

                context_id = anchor->id;
                buffer = (StringInfo) request->pointer;
            }
            else
            {
                owned = ankus_stringinfo_find((uint64) request->context);
                if (owned == NULL)
                {
                    if (operation == ANKUS_STRINGINFO_DISPOSE)
                    {
                        return;
                    }

                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                        errmsg("the PostgreSQL StringInfo has been reclaimed")));
                }

                context_id = owned->context_id;
                buffer = owned->buffer;
            }

            /* A buffer PostgreSQL made read-only has no capacity, so nothing may write to it. */
            ankus_stringinfo_validate(buffer);
            if (buffer->maxlen == 0 && (operation == ANKUS_STRINGINFO_APPEND || operation == ANKUS_STRINGINFO_WRITE ||
                operation == ANKUS_STRINGINFO_RESET || operation == ANKUS_STRINGINFO_ENLARGE || operation == ANKUS_STRINGINFO_ENSURE_CAPACITY))
            {
                ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED), errmsg("the PostgreSQL StringInfo is read-only")));
            }

            switch (operation)
            {
                case ANKUS_STRINGINFO_INSPECT:
                    break;
                case ANKUS_STRINGINFO_APPEND:
                    ankus_stringinfo_append(buffer, request);
                    break;
                case ANKUS_STRINGINFO_READ:
                case ANKUS_STRINGINFO_WRITE:
                {
                    if (request->value < 0 || (uintptr_t) request->value > (uintptr_t) buffer->len ||
                        request->length > (uintptr_t) buffer->len - (uintptr_t) request->value ||
                        (request->length != 0 && request->data == 0))
                    {
                        ereport(ERROR, (errcode(ERRCODE_DATA_EXCEPTION), errmsg("StringInfo byte range exceeds the payload")));
                    }

                    if (request->length != 0)
                    {
                        char *payload = buffer->data + request->value;
                        if (operation == ANKUS_STRINGINFO_READ)
                        {
                            memmove((void *) request->data, payload, (Size) request->length);
                        }
                        else
                        {
                            memmove(payload, (const void *) request->data, (Size) request->length);
                        }
                    }

                    break;
                }
                case ANKUS_STRINGINFO_RESET:
                    resetStringInfo(buffer);
                    break;
                case ANKUS_STRINGINFO_ENLARGE:
                case ANKUS_STRINGINFO_ENSURE_CAPACITY:
                {
                    if (request->value < 0 || request->value > INT_MAX)
                    {
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid StringInfo capacity")));
                    }

                    int additional = operation == ANKUS_STRINGINFO_ENLARGE ? (int) request->value :
                        Max(0, (int) request->value - buffer->len);
                    enlargeStringInfo(buffer, additional);
                    break;
                }
                case ANKUS_STRINGINFO_DISPOSE:
                    if (owned != NULL)
                    {
                        pfree(buffer->data);
                        pfree(buffer);
                        ankus_stringinfo_remove(owned);
                    }

                    return;
                /* Detaching hands the buffer, or only its bytes, to the caller and forgets an owned registration. */
                case ANKUS_STRINGINFO_DETACH:
                case ANKUS_STRINGINFO_DETACH_DATA:
                case ANKUS_STRINGINFO_DETACH_CSTRING:
                    if (operation == ANKUS_STRINGINFO_DETACH_CSTRING &&
                        (buffer->data == NULL || memchr(buffer->data, '\0', buffer->len) != NULL || buffer->data[buffer->len] != '\0'))
                    {
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                            errmsg("StringInfo payload is not a NUL-terminated C string without interior NUL bytes")));
                    }

                    result->pointer = operation == ANKUS_STRINGINFO_DETACH ? (intptr_t) buffer : (intptr_t) buffer->data;
                    if (owned != NULL)
                    {
                        if (operation != ANKUS_STRINGINFO_DETACH)
                        {
                            pfree(buffer);
                        }

                        ankus_stringinfo_remove(owned);
                    }

                    return;
                default:
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("unknown Ankus StringInfo operation")));
            }

            ankus_stringinfo_snapshot(buffer, context_id, result);
        }

        """;
}
