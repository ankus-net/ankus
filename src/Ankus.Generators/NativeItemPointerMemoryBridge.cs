namespace Ankus.Generators;

/// <summary>
/// Emits guarded selected-header item-pointer allocation and access through the existing memory registry.
/// </summary>
internal static class NativeItemPointerMemoryBridge
{
    /// <summary>
    /// Gets operations that preserve native layout and validate allocation identity or an external context generation.
    /// </summary>
    internal const string Source = """
        #include "storage/itemptr.h"

        static void
        ankus_memory_item_pointer(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            if (request->flags < 1 || request->flags > 3)
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid item-pointer operation")));
            }

            if (request->flags != 2 && (request->value < 0 || (uint64) request->value > PG_UINT32_MAX ||
                request->length > PG_UINT16_MAX))
            {
                ereport(ERROR, (errcode(ERRCODE_NUMERIC_VALUE_OUT_OF_RANGE), errmsg("invalid item-pointer field range")));
            }

            if (request->flags == 1)
            {
                AnkusMemoryContext *entry = ankus_memory_context_from_request(request);
                ankus_memory_check_chunk_operation(entry->context, "individually owned item pointers");
                AnkusMemoryRequest allocation_request = {0};
                allocation_request.length = sizeof(ItemPointerData);
                ankus_memory_allocate_request(entry, &allocation_request, result);
                AnkusMemoryAllocation *allocation = ankus_memory_allocation_required((uint64) result->pointer);
                ItemPointerSet((ItemPointer) allocation->pointer, (BlockNumber) request->value, (OffsetNumber) request->length);
                return;
            }

            ItemPointer pointer;
            if (request->pointer == 0)
            {
                AnkusMemoryAllocation *allocation = ankus_memory_allocation_by_id((uint64) request->context);
                if (allocation == NULL)
                {
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE), errmsg("the item-pointer allocation is stale")));
                }

                if (allocation->size < sizeof(ItemPointerData))
                {
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("the allocation cannot contain an ItemPointerData")));
                }

                pointer = (ItemPointer) allocation->pointer;
                result->context = (intptr_t) allocation->context_id;
            }
            else
            {
                AnkusMemoryContext *entry = ankus_memory_context_from_request(request);
                if (entry->generation == 0 || entry->generation != (uintptr_t) request->other)
                {
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE), errmsg("the item-pointer lifetime anchor is stale")));
                }

                /* No chunk-header access: the unsafe caller may supply a stack or interior address. */
                pointer = (ItemPointer) request->pointer;
                result->context = (intptr_t) entry->id;
            }

            if (request->flags == 3)
            {
                ItemPointerSet(pointer, (BlockNumber) request->value, (OffsetNumber) request->length);
            }

            result->pointer = (intptr_t) pointer;
            result->value = (intptr_t) ItemPointerGetBlockNumberNoCheck(pointer);
            result->length = (uintptr_t) ItemPointerGetOffsetNumberNoCheck(pointer);
        }

        """;
}
