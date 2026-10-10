
/* Only this translation unit interposes allocation boundaries. PostgreSQL and
 * production extensions retain their actual allocator implementations. */
#include <stdlib.h>
#include "utils/memutils.h"
#include "utils/palloc.h"
#include "nodes/memnodes.h"
#include "lib/stringinfo.h"
#include "nodes/pg_list.h"
#if PG_VERSION_NUM >= 160000
#include "utils/memutils_internal.h"
#include "utils/memutils_memorychunk.h"
#endif

static void *fault_records[64];
static int fault_live_records;
static int fault_successful_reservations;
static int fault_released_reservations;
static bool fault_fail_calloc;
static bool fault_return_null;
static bool fault_raise_storage_error;
static bool fault_monitor_storage;
static int fault_storage_calls;
static MemoryContext fault_owner;
static int fault_stringinfo_stage;
static int fault_stringinfo_frees;
static bool fault_stringinfo_monitor;
static int fault_list_stage;
static int fault_list_frees;
static bool fault_list_monitor;

static List *
fault_list_make(NodeTag tag, ListCell cell)
{
    if (fault_list_stage == 1)
    {
        fault_list_stage = 0;
        ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("controlled list header allocation failure")));
    }

    return list_make1_impl(tag, cell);
}

static void *
fault_list_alloc(MemoryContext context, Size size)
{
    if (fault_list_stage == 2 && context == fault_owner)
    {
        fault_list_stage = 0;
        ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("controlled list cell allocation failure")));
    }

    return MemoryContextAlloc(context, size);
}

static void *
fault_list_realloc(void *pointer, Size size)
{
    if (fault_list_stage == 3 && GetMemoryChunkContext(pointer) == fault_owner)
    {
        fault_list_stage = 0;
        ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("controlled list cell reallocation failure")));
    }

    return repalloc(pointer, size);
}

static void
fault_list_free(List *list)
{
    if (fault_list_monitor && list != NIL && GetMemoryChunkContext(list) == fault_owner)
    {
        fault_list_frees += list->elements == list->initial_elements ? 1 : 2;
    }

    list_free(list);
}

static void
fault_init_stringinfo(StringInfo buffer)
{
    if (fault_stringinfo_stage == 1)
    {
        fault_stringinfo_stage = 0;
        ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("controlled StringInfo data allocation failure")));
    }

    initStringInfo(buffer);
}

static void
fault_enlarge_stringinfo(StringInfo buffer, int needed)
{
    if (fault_stringinfo_stage == 2)
    {
        fault_stringinfo_stage = 0;
        ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("controlled StringInfo enlargement failure")));
    }

    enlargeStringInfo(buffer, needed);
}

static void
fault_pfree(void *pointer)
{
    if (fault_stringinfo_monitor && GetMemoryChunkContext(pointer) == fault_owner)
    {
        fault_stringinfo_frees++;
    }

    pfree(pointer);
}

static void *
fault_calloc(size_t count, size_t size)
{
    if (fault_fail_calloc)
    {
        fault_fail_calloc = false;
        return NULL;
    }

    void *record = calloc(count, size);
    if (record == NULL)
    {
        return NULL;
    }

    for (int index = 0; index < 64; index++)
    {
        if (fault_records[index] == NULL)
        {
            fault_records[index] = record;
            fault_live_records++;
            fault_successful_reservations++;
            return record;
        }
    }

    free(record);
    elog(ERROR, "registry fault fixture exceeded its bounded record capacity");
    return NULL;
}

static void
fault_free(void *record)
{
    if (record != NULL)
    {
        for (int index = 0; index < 64; index++)
        {
            if (fault_records[index] == record)
            {
                fault_records[index] = NULL;
                fault_live_records--;
                fault_released_reservations++;
                break;
            }
        }
    }

    free(record);
}

static void *
fault_allocate(MemoryContext owner, Size size, int flags)
{
    if (fault_monitor_storage && owner == fault_owner)
    {
        fault_storage_calls++;
        if (fault_raise_storage_error)
        {
            fault_raise_storage_error = false;
            ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("controlled item-pointer storage failure"),
                errdetail("the unpublished allocation must be released"), errhint("retry after clearing the fault")));
        }

        if (fault_return_null)
        {
            fault_return_null = false;
            if ((flags & MCXT_ALLOC_NO_OOM) == 0)
            {
                elog(ERROR, "registry fault fixture requires native NO_OOM for NULL");
            }

            return NULL;
        }
    }

    return MemoryContextAllocExtended(owner, size, flags);
}

#define calloc fault_calloc
#define free fault_free
#define MemoryContextAllocExtended fault_allocate
#define initStringInfo fault_init_stringinfo
#define enlargeStringInfo fault_enlarge_stringinfo
#define pfree fault_pfree
#define list_make1_impl fault_list_make
#define MemoryContextAlloc fault_list_alloc
#define repalloc fault_list_realloc
#define list_free fault_list_free

#ifdef __APPLE__
/* The emitted bridge registers signal routing with the Native AOT runtime, which this fixture does not link. */
void RhSetThreadCreationCallback(void (*callback)(void));

void
RhSetThreadCreationCallback(void (*callback)(void))
{
    (void) callback;
}
#endif
