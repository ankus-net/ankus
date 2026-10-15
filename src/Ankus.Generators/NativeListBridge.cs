namespace Ankus.Generators;

/// <summary>
/// Emits checked typed list operations against the selected PostgreSQL headers.
/// </summary>
internal static class NativeListBridge
{
    /// <summary>
    /// Gets the container registry, exact cell transport and guarded mutations.
    /// </summary>
    internal const string Source = """

        #include "nodes/pg_list.h"
        #include "nodes/readfuncs.h"
        #include "mb/pg_wchar.h"

        typedef enum AnkusListOperation
        {
            ANKUS_LIST_CREATE = 1,
            ANKUS_LIST_BORROW = 2,
            ANKUS_LIST_INSPECT = 3,
            ANKUS_LIST_READ = 4,
            ANKUS_LIST_WRITE = 5,
            ANKUS_LIST_ADD = 6,
            ANKUS_LIST_TRY_ADD = 7,
            ANKUS_LIST_RESERVE = 8,
            ANKUS_LIST_INSERT = 9,
            ANKUS_LIST_REMOVE = 10,
            ANKUS_LIST_CLEAR = 11,
            ANKUS_LIST_DRAIN = 12,
            ANKUS_LIST_DISPOSE = 13,
            ANKUS_LIST_DETACH = 14,
            ANKUS_LIST_PARSE_DEFAULTS = 15
        } AnkusListOperation;

        typedef struct AnkusList AnkusList;
        struct AnkusList
        {
            uint64 id;
            uint64 context_id;
            List *list;
            int kind;
            bool owned;
            AnkusList *next;
        };

        static AnkusList *ankus_lists;
        static uint64 ankus_list_next_id = 1;

        static void
        ankus_list_remove_context(uint64 context_id)
        {
            AnkusList **slot = &ankus_lists;
            while (*slot != NULL)
            {
                AnkusList *entry = *slot;
                if (entry->context_id == context_id)
                {
                    *slot = entry->next;
                    /* Context cleanup owns the native container, including borrowed lists. */
                    free(entry);
                    continue;
                }

                slot = &entry->next;
            }
        }

        static void
        ankus_list_remove(AnkusList *entry)
        {
            AnkusList **slot = &ankus_lists;
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

        static AnkusList *
        ankus_list_find(uint64 id)
        {
            for (AnkusList *entry = ankus_lists; entry != NULL; entry = entry->next)
            {
                if (entry->id == id && ankus_memory_context_by_id(entry->context_id) != NULL)
                {
                    return entry;
                }
            }

            return NULL;
        }

        static NodeTag
        ankus_list_tag(int kind)
        {
            switch (kind)
            {
                case 1: return T_List;
                case 2: return T_IntList;
                case 3: return T_OidList;
                case 4:
        #if PG_VERSION_NUM >= 160000
                    return T_XidList;
        #else
                    ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED), errmsg("transaction-ID lists require PostgreSQL 16 or later")));
                    break;
        #endif
                default:
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid PostgreSQL list cell kind")));
            }

            return T_Invalid;
        }

        static void
        ankus_list_validate(List *list, int kind)
        {
            if (list != NIL && (list->type != ankus_list_tag(kind) || list->length < 1 ||
                list->max_length < list->length || (Size) list->max_length > MaxAllocSize / sizeof(ListCell) ||
                list->elements == NULL))
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid native PostgreSQL list")));
            }
        }

        static void
        ankus_list_validate_input(AnkusMemoryRequest *request, int kind)
        {
            if (request->length > MaxAllocSize / sizeof(ListCell) || (request->length != 0 && request->data == 0))
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid PostgreSQL list input")));
            }

            const uint64 *values = (const uint64 *) request->data;
            uint64 maximum = kind == 1 ? (uint64) UINTPTR_MAX : (uint64) UINT32_MAX;
            for (uintptr_t index = 0; index < request->length; index++)
            {
                if (values[index] > maximum)
                {
                    ereport(ERROR, (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE), errmsg("PostgreSQL list cell would lose bits")));
                }
            }
        }

        static ListCell
        ankus_list_cell(int kind, uint64 value)
        {
            ListCell cell = {0};
            switch (kind)
            {
                case 1: cell.ptr_value = (void *) (uintptr_t) value; break;
                case 2:
                {
                    uint32 bits = (uint32) value;
                    memcpy(&cell.int_value, &bits, sizeof(bits));
                    break;
                }
                case 3: cell.oid_value = (Oid) value; break;
        #if PG_VERSION_NUM >= 160000
                case 4: cell.xid_value = (TransactionId) value; break;
        #endif
            }

            return cell;
        }

        static uint64
        ankus_list_bits(int kind, const ListCell *cell)
        {
            switch (kind)
            {
                case 1: return (uint64) (uintptr_t) cell->ptr_value;
                case 2: return (uint64) (uint32) cell->int_value;
                case 3: return (uint64) cell->oid_value;
        #if PG_VERSION_NUM >= 160000
                case 4: return (uint64) cell->xid_value;
        #endif
            }

            elog(ERROR, "invalid Ankus list kind");
            return 0;
        }

        static void
        ankus_list_snapshot(AnkusList *entry, AnkusMemoryResult *result)
        {
            List *list = entry->list;
            result->context = (intptr_t) entry->context_id;
            result->pointer = (intptr_t) list;
            result->data = list == NIL ? 0 : (intptr_t) list->elements;
            result->length = list == NIL ? 0 : list->length;
            result->value = list == NIL ? 0 : list->max_length;
        }

        static int
        ankus_list_capacity(int length, uintptr_t additional)
        {
            if (additional > MaxAllocSize / sizeof(ListCell) - (Size) length)
            {
                ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("PostgreSQL list capacity is too large")));
            }

            return length + (int) additional;
        }

        static void
        ankus_list_reserve(List *list, MemoryContext owner, int capacity)
        {
            if (capacity <= list->max_length)
            {
                return;
            }

            ListCell *elements;
            if (list->elements == list->initial_elements)
            {
                elements = MemoryContextAlloc(owner, (Size) capacity * sizeof(ListCell));
                memcpy(elements, list->elements, (Size) list->length * sizeof(ListCell));
            }
            else
            {
                elements = repalloc(list->elements, (Size) capacity * sizeof(ListCell));
            }

            /* Publish only after allocation succeeds; old cells remain valid on ERROR. */
            list->elements = elements;
            list->max_length = capacity;
        }

        static void
        ankus_list_append(AnkusList *entry, AnkusMemoryRequest *request)
        {
            ankus_list_validate_input(request, entry->kind);
            int length = list_length(entry->list);
            int required = ankus_list_capacity(length, request->length);
            if (request->length == 0)
            {
                return;
            }

            MemoryContext owner = ankus_memory_context_by_id(entry->context_id)->context;
            ankus_memory_check_chunk_operation(owner, "list allocation");
            const uint64 *values = (const uint64 *) request->data;
            if (entry->list == NIL)
            {
                MemoryContext caller = CurrentMemoryContext;
                PG_TRY();
                {
                    MemoryContextSwitchTo(owner);
                    entry->list = list_make1_impl(ankus_list_tag(entry->kind), ankus_list_cell(entry->kind, values[0]));
                    ankus_list_reserve(entry->list, owner, required);
                    MemoryContextSwitchTo(caller);
                }
                PG_CATCH();
                {
                    MemoryContext propagating = MemoryContextSwitchTo(caller);
                    list_free(entry->list);
                    entry->list = NIL;
                    MemoryContextSwitchTo(propagating);
                    PG_RE_THROW();
                }
                PG_END_TRY();
            }
            else
            {
                int grown = (int) Min((Size) entry->list->max_length * 2, MaxAllocSize / sizeof(ListCell));
                if (required > entry->list->max_length)
                {
                    ankus_list_reserve(entry->list, owner, Max(required, grown));
                }
            }

            for (uintptr_t index = 0; index < request->length; index++)
            {
                entry->list->elements[length + index] = ankus_list_cell(entry->kind, values[index]);
            }

            entry->list->length = required;
        }

        static void
        ankus_list_create(AnkusMemoryRequest *request, AnkusMemoryResult *result, bool borrow)
        {
            AnkusMemoryContext *owner = ankus_memory_context_from_request(request);
            int kind = (int) request->alignment;
            NodeTag tag = ankus_list_tag(kind);
            List *list = borrow ? (List *) request->pointer : NIL;
            if (list != NIL)
            {
                /* Prove the tag before reading any union member. A wrong tag is a failed downcast. */
                if (list->type != tag)
                {
                    return;
                }

                ankus_list_validate(list, kind);
                ankus_memory_check_chunk_operation(owner->context, "list borrowing");
                if (GetMemoryChunkContext(list) != owner->context ||
                    (list->elements != list->initial_elements && GetMemoryChunkContext(list->elements) != owner->context))
                {
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("the list must belong to its explicit lifetime context")));
                }
            }

            AnkusList *entry = calloc(1, sizeof(*entry));
            if (entry == NULL || ankus_list_next_id == 0)
            {
                free(entry);
                ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("unable to register an Ankus list")));
            }

            entry->id = ankus_list_next_id++;
            entry->context_id = owner->id;
            entry->list = list;
            entry->kind = kind;
            entry->owned = !borrow;
            PG_TRY();
            {
                if (!borrow)
                {
                    ankus_list_append(entry, request);
                }
            }
            PG_CATCH();
            {
                free(entry);
                PG_RE_THROW();
            }
            PG_END_TRY();
            entry->next = ankus_lists;
            ankus_lists = entry;
            result->pointer = (intptr_t) entry->id;
        }

        static void
        ankus_list_range(List *list, AnkusMemoryRequest *request)
        {
            uintptr_t length = (uintptr_t) list_length(list);
            if (request->value < 0 || (uintptr_t) request->value > length || request->length > length - (uintptr_t) request->value)
            {
                ereport(ERROR, (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR), errmsg("PostgreSQL list range exceeds its length")));
            }
        }

        static void
        ankus_list_parse_defaults(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            AnkusMemoryContext *owner = ankus_memory_context_from_request(request);
            ankus_memory_check_chunk_operation(owner->context, "default expression allocation");
            if (request->length == 0 || request->length >= MaxAllocSize || request->data == 0 ||
                request->value < 1 || request->value > INT_MAX)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid function defaults request")));
            AnkusList *entry = calloc(1, sizeof(*entry));
            if (entry == NULL || ankus_list_next_id == 0)
            {
                free(entry);
                ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("unable to register function defaults")));
            }

            MemoryContext caller = CurrentMemoryContext;
            MemoryContext volatile temporary = NULL;
            PG_TRY();
            {
                temporary = AllocSetContextCreate(caller, "Ankus default input", ALLOCSET_SMALL_SIZES);
                MemoryContextSwitchTo(temporary);
                char *utf8 = pnstrdup((char *) request->data, (Size) request->length);
                char *source = pg_any_to_server(utf8, (int) request->length, PG_UTF8);
                MemoryContextSwitchTo(owner->context);
                entry->list = stringToNode(source);
                MemoryContextSwitchTo(caller);
                if (entry->list == NIL || !IsA(entry->list, List) || list_length(entry->list) != request->value)
                    ereport(ERROR, (errcode(ERRCODE_INVALID_BINARY_REPRESENTATION), errmsg("invalid function defaults list")));
                MemoryContextDelete(temporary);
                temporary = NULL;
            }
            PG_CATCH();
            {
                MemoryContext propagating = MemoryContextSwitchTo(caller);
                if (temporary != NULL)
                    MemoryContextDelete(temporary);
                free(entry);
                MemoryContextSwitchTo(propagating);
                PG_RE_THROW();
            }
            PG_END_TRY();
            entry->id = ankus_list_next_id++;
            entry->context_id = owner->id;
            entry->kind = 1;
            entry->owned = true;
            entry->next = ankus_lists;
            ankus_lists = entry;
            result->pointer = (intptr_t) entry->id;
        }

        /* Copies a range of cells to or from the caller's buffer; draining or removing then closes the gap. */
        static void
        ankus_list_transfer(AnkusList *entry, List *list, AnkusMemoryRequest *request, AnkusListOperation operation)
        {
            ankus_list_range(list, request);
            if (request->length != 0 && operation != ANKUS_LIST_REMOVE && request->data == 0)
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("missing PostgreSQL list cell buffer")));
            }

            if (operation == ANKUS_LIST_WRITE)
            {
                ankus_list_validate_input(request, entry->kind);
            }

            uint64 *values = (uint64 *) request->data;
            for (uintptr_t index = 0; index < request->length; index++)
            {
                ListCell *cell = &list->elements[request->value + index];
                if (operation == ANKUS_LIST_WRITE)
                {
                    *cell = ankus_list_cell(entry->kind, values[index]);
                }
                else if (operation != ANKUS_LIST_REMOVE)
                {
                    values[index] = ankus_list_bits(entry->kind, cell);
                }
            }

            if (request->length != 0 && (operation == ANKUS_LIST_DRAIN || operation == ANKUS_LIST_REMOVE))
            {
                if (request->length == (uintptr_t) list->length)
                {
                    list_free(list);
                    entry->list = NIL;
                }
                else
                {
                    int tail = (int) request->value + (int) request->length;
                    memmove(list->elements + request->value, list->elements + tail, (Size) (list->length - tail) * sizeof(ListCell));
                    list->length -= (int) request->length;
                }
            }
        }

        static void
        ankus_list_execute(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            AnkusListOperation operation = (AnkusListOperation) request->flags;
            /* Parsing defaults and creating or borrowing a list need no existing handle. */
            if (operation == ANKUS_LIST_PARSE_DEFAULTS)
            {
                ankus_list_parse_defaults(request, result);
                return;
            }

            if (operation == ANKUS_LIST_CREATE || operation == ANKUS_LIST_BORROW)
            {
                ankus_list_create(request, result, operation == ANKUS_LIST_BORROW);
                return;
            }

            /* Disposing a list its context already reclaimed does nothing, as disposing twice does. */
            AnkusList *entry = ankus_list_find((uint64) request->context);
            if (entry == NULL)
            {
                if (operation == ANKUS_LIST_DISPOSE)
                {
                    return;
                }

                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE), errmsg("the PostgreSQL list has been reclaimed")));
            }

            if (operation == ANKUS_LIST_DISPOSE)
            {
                if (entry->owned)
                {
                    list_free(entry->list);
                }

                ankus_list_remove(entry);
                return;
            }

            /* Every other operation first checks that the native list still holds cells of its kind. */
            List *list = entry->list;
            ankus_list_validate(list, entry->kind);
            switch (operation)
            {
                case ANKUS_LIST_INSPECT:
                    break;
                case ANKUS_LIST_ADD:
                    ankus_list_append(entry, request);
                    break;
                case ANKUS_LIST_TRY_ADD:
                    ankus_list_validate_input(request, entry->kind);
                    if (request->length != 1)
                    {
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("TryAdd requires one cell")));
                    }

                    if (list != NIL && list->length < list->max_length)
                    {
                        list->elements[list->length++] = ankus_list_cell(entry->kind, *(const uint64 *) request->data);
                        result->value = 1;
                    }

                    return;
                case ANKUS_LIST_RESERVE:
                {
                    if (list != NIL)
                    {
                        int capacity = ankus_list_capacity(list->length, request->length);
                        ankus_list_reserve(list, ankus_memory_context_by_id(entry->context_id)->context, capacity);
                        result->value = 1;
                    }

                    return;
                }
                case ANKUS_LIST_INSERT:
                {
                    if (request->value < 0 || request->value > list_length(list) || request->length != 1)
                    {
                        ereport(ERROR, (errcode(ERRCODE_ARRAY_SUBSCRIPT_ERROR), errmsg("invalid PostgreSQL list insertion index")));
                    }

                    ankus_list_append(entry, request);
                    list = entry->list;
                    int index = (int) request->value;
                    ListCell value = list->elements[list->length - 1];
                    memmove(list->elements + index + 1, list->elements + index, (Size) (list->length - index - 1) * sizeof(ListCell));
                    list->elements[index] = value;
                    break;
                }
                case ANKUS_LIST_READ:
                case ANKUS_LIST_WRITE:
                case ANKUS_LIST_DRAIN:
                case ANKUS_LIST_REMOVE:
                    ankus_list_transfer(entry, list, request, operation);
                    break;
                case ANKUS_LIST_CLEAR:
                    list_free(list);
                    entry->list = NIL;
                    break;
                case ANKUS_LIST_DETACH:
                    result->pointer = (intptr_t) list;
                    ankus_list_remove(entry);
                    return;
                default:
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("unknown Ankus list operation")));
            }

            ankus_list_snapshot(entry, result);
        }

        """;
}
