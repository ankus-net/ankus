namespace Ankus.Generators;

/// <summary>
/// Emits the independent PostgreSQL memory-context capability and its checked native registry.
/// </summary>
internal static class NativeMemoryBridge
{
    /// <summary>
    /// Gets native cleanup bindings shared by the memory and SPI guards.
    /// </summary>
    internal const string CleanupBinding = """
        static MemoryContext ankus_memory_cleanup_context;
        static bool ankus_memory_error_cleanup;
        """;

    /// <summary>
    /// Gets the request envelope, monotonic context/allocation registry, and guarded allocator operations.
    /// </summary>
    internal const string Source = """
        #include <stdint.h>
        #include <stdlib.h>
        #include <string.h>
        #include "miscadmin.h"
        #include "access/xact.h"
        #include "storage/lock.h"
        #include "storage/proc.h"
        #include "utils/memutils.h"
        #include "utils/palloc.h"
        #include "nodes/memnodes.h"
        #if PG_VERSION_NUM >= 160000
        #include "utils/memutils_internal.h"
        #include "utils/memutils_memorychunk.h"
        #endif

        typedef enum AnkusMemoryOperation
        {
            ANKUS_MEMORY_CURRENT = 1,
            ANKUS_MEMORY_PREDEFINED = 2,
            ANKUS_MEMORY_CREATE = 3,
            ANKUS_MEMORY_PARENT = 4,
            ANKUS_MEMORY_NAME = 5,
            ANKUS_MEMORY_RESET = 6,
            ANKUS_MEMORY_RESET_ONLY = 7,
            ANKUS_MEMORY_RESET_CHILDREN = 8,
            ANKUS_MEMORY_DELETE = 9,
            ANKUS_MEMORY_SWITCH = 10,
            ANKUS_MEMORY_ALLOCATE = 11,
            ANKUS_MEMORY_REALLOCATE = 12,
            ANKUS_MEMORY_FREE = 13,
            ANKUS_MEMORY_READ = 14,
            ANKUS_MEMORY_WRITE = 15,
            ANKUS_MEMORY_CLEAR = 16,
            ANKUS_MEMORY_OWNER = 17,
            ANKUS_MEMORY_EMPTY = 18,
            ANKUS_MEMORY_STATISTICS = 19,
            ANKUS_MEMORY_REGISTER_CALLBACK = 20,
            ANKUS_MEMORY_CANCEL_CALLBACK = 21,
            ANKUS_MEMORY_DETACH = 22,
            ANKUS_MEMORY_ADOPT = 23,
            ANKUS_MEMORY_CAPTURE_GENERATION = 24,
            ANKUS_MEMORY_READ_REFERENCE = 25,
            ANKUS_MEMORY_WRITE_REFERENCE = 26,
            ANKUS_MEMORY_CALLBACK = 27,
            ANKUS_MEMORY_ALLOCATE_VARLENA = 28,
            ANKUS_MEMORY_STRINGINFO = 29,
            ANKUS_MEMORY_LIST = 30,
            ANKUS_MEMORY_ITEM_POINTER = 31,
            ANKUS_MEMORY_NATIVE_BINDING = 32,
            ANKUS_MEMORY_FORMAT_NODE = 33,
            ANKUS_MEMORY_NATIVE_CALL = 34,
            ANKUS_MEMORY_SHARED = 35,
            ANKUS_MEMORY_SPIN = 36,
            ANKUS_MEMORY_WORKER = 37,
            ANKUS_MEMORY_CHECK_INTERRUPTS = 38,
            ANKUS_MEMORY_RECORD_TERMINAL = 39,
            ANKUS_MEMORY_BORROW_NATIVE = 40,
            ANKUS_MEMORY_CHUNK_OWNER = 41,
            ANKUS_MEMORY_NATIVE_POINTER = 42,
            ANKUS_MEMORY_REPLICATION = 43,
            ANKUS_MEMORY_RELATION_WRITER = 44,
            ANKUS_MEMORY_REPLICATION_ORIGIN = 45,
            ANKUS_MEMORY_QUOTE = 46,
            ANKUS_MEMORY_ERROR_CONTEXT = 47,
            ANKUS_MEMORY_PROCESS_EXIT = 48,
            ANKUS_MEMORY_CHILD_PROCESS = 49
        } AnkusMemoryOperation;

        typedef struct AnkusMemoryRequest
        {
            int32 operation;
            int32 flags;
            intptr_t context;
            intptr_t other;
            intptr_t pointer;
            intptr_t data;
            uintptr_t length;
            uintptr_t alignment;
            intptr_t value;
        } AnkusMemoryRequest;

        typedef struct AnkusMemoryResult
        {
            intptr_t context;
            intptr_t pointer;
            intptr_t data;
            uintptr_t length;
            intptr_t value;
        } AnkusMemoryResult;

        typedef struct AnkusMemoryContextSizes
        {
            uintptr_t minimum;
            uintptr_t initial;
            uintptr_t maximum;
        } AnkusMemoryContextSizes;

        typedef struct AnkusMemoryApi AnkusMemoryApi;
        typedef int (*AnkusMemoryInvoke)(AnkusMemoryApi *, AnkusMemoryRequest *, AnkusMemoryResult *, AnkusError *);
        typedef int (*AnkusMemoryCall)(AnkusMemoryApi *, intptr_t, intptr_t, intptr_t *, AnkusError *);

        struct AnkusMemoryApi
        {
            intptr_t provider;
            MemoryContext current;
            AnkusMemoryInvoke invoke;
            MemoryContext result_context;
            volatile sig_atomic_t *interrupt_pending;
            int32 *failed_frames;
            volatile int *signal_queue;
            int *signal_mask;
            int32 direct_reads;
            bool *error_cleanup;
            AnkusMemoryCall call;
        };

        StaticAssertDecl(sizeof(sig_atomic_t) == sizeof(int32), "managed interrupt flag width");
        StaticAssertDecl(sizeof(int) == sizeof(int32), "managed signal flag width");

        static int ankus_memory_invoke(AnkusMemoryApi *, AnkusMemoryRequest *, AnkusMemoryResult *, AnkusError *);
        static int ankus_memory_call(AnkusMemoryApi *, intptr_t, intptr_t, intptr_t *, AnkusError *);
        static char ankus_memory_provider;
        static void ankus_shared_prepare(void);

        static bool ankus_memory_completion_registered;
        static bool ankus_memory_completion_committed;

        static void
        ankus_memory_completion_event(XactEvent event, void *argument)
        {
            (void) argument;
            /* Only a commit or prepare record makes completion irreversible: aborting
             * that XID is what PostgreSQL itself escalates to PANIC. A read-only commit
             * writes no record, and a parallel worker's commit belongs to its leader, so
             * FATAL remains FATAL there. The XID is still assigned during these callbacks. */
            ankus_memory_completion_committed = (event == XACT_EVENT_COMMIT || event == XACT_EVENT_PREPARE) &&
                TransactionIdIsValid(GetTopTransactionIdIfAny());
        }

        static void
        ankus_memory_completion_ensure(void)
        {
            if (!ankus_memory_completion_registered)
            {
                RegisterXactCallback(ankus_memory_completion_event, NULL);
                ankus_memory_completion_registered = true;
            }

            if (IsTransactionState())
            {
                ankus_memory_completion_committed = false;
            }
        }

        static bool
        ankus_memory_completion_after_commit(void)
        {
            if (!ankus_memory_completion_committed || MyProc == NULL)
            {
                return false;
            }

            /* ProcArrayEndTransaction clears the VXID before COMMIT/PREPARE callbacks.
             * A later transaction has a live VXID, even during its early portal abort
             * before XACT_EVENT_ABORT. Use the selected headers' public macro so this
             * does not depend on the PGPROC layout changing across PostgreSQL majors. */
            VirtualTransactionId transaction;
            GET_VXID_FROM_PGPROC(transaction, *MyProc);
            return !VirtualTransactionIdIsValid(transaction);
        }

        static void
        ankus_memory_initialize(AnkusMemoryApi *memory)
        {
            ankus_memory_completion_ensure();
            ankus_shared_prepare();
            memset(memory, 0, sizeof(*memory));
            memory->provider = (intptr_t) &ankus_memory_provider;
            memory->current = CurrentMemoryContext;
            memory->invoke = ankus_memory_invoke;
            memory->result_context = CurrentMemoryContext;
            memory->interrupt_pending = &InterruptPending;
            memory->failed_frames = &ankus_recovery_failed_frames;
        #ifdef WIN32
            memory->signal_queue = &pg_signal_queue;
            memory->signal_mask = &pg_signal_mask;
        #endif
            /* Lets managed code read flat values in place: bytes always, and text when the database
             * encoding needs no conversion to UTF-8. */
            memory->direct_reads = GetDatabaseEncoding() == PG_UTF8 ? 2 : 1;
            /* With failed_frames, lets managed code repeat a binding check it already passed without entering here. */
            memory->error_cleanup = &ankus_memory_error_cleanup;
            memory->call = ankus_memory_call;
        }

        typedef struct AnkusMemoryAllocation AnkusMemoryAllocation;
        typedef struct AnkusMemoryContext AnkusMemoryContext;
        typedef struct AnkusMemoryProtection AnkusMemoryProtection;

        struct AnkusMemoryProtection
        {
            MemoryContext owner;
            bool teardown;
            AnkusMemoryProtection *previous;
        };

        static AnkusMemoryProtection *ankus_memory_protection;

        static void
        ankus_memory_protect(AnkusMemoryProtection *scope, MemoryContext owner, bool teardown)
        {
            scope->owner = owner;
            scope->teardown = teardown;
            scope->previous = ankus_memory_protection;
            ankus_memory_protection = scope;
        }

        static bool
        ankus_memory_contains(MemoryContext ancestor, MemoryContext context)
        {
            for (MemoryContext current = context; current != NULL; current = MemoryContextGetParent(current))
            {
                if (current == ancestor)
                {
                    return true;
                }
            }

            return false;
        }

        static const char *
        ankus_memory_check_protection(MemoryContext context, bool creation)
        {
            for (AnkusMemoryProtection *scope = ankus_memory_protection; scope != NULL; scope = scope->previous)
            {
                if ((!creation || scope->teardown) &&
                    (ankus_memory_contains(context, scope->owner) ||
                        (scope->teardown && ankus_memory_contains(scope->owner, context))))
                {
                    return "the memory context overlaps an active native callback or cleanup operation";
                }
            }

            return NULL;
        }

        static bool
        ankus_transaction_cleanup_active(void)
        {
            /* Commit/abort owns this context until its callbacks finish. An idle
             * backend or initializer has no transaction context to dismantle. */
            return !IsTransactionState() && CurTransactionContext != NULL;
        }

        static size_t
        ankus_escape_completion_chunk(const unsigned char *text, int length, int *position, char *output, size_t capacity)
        {
            static const char hex[] = "0123456789ABCDEF";
            size_t used = 0;
            while (*position < length)
            {
                unsigned char value = text[*position];
                char escaped[4];
                const char *source = (const char *) &value;
                size_t count = 1;
                if (value == '\\' || value == '\n' || value == '\r' || value == '\t' || value < 0x20 || value >= 0x7f)
                {
                    escaped[0] = '\\';
                    if (value == '\\')
                    {
                        escaped[1] = '\\';
                        count = 2;
                    }
                    else if (value == '\n' || value == '\r' || value == '\t')
                    {
                        escaped[1] = value == '\n' ? 'n' : value == '\r' ? 'r' : 't';
                        count = 2;
                    }
                    else
                    {
                        escaped[1] = 'x';
                        escaped[2] = hex[value >> 4];
                        escaped[3] = hex[value & 0x0f];
                        count = 4;
                    }

                    source = escaped;
                }

                if (used + count > capacity)
                {
                    break;
                }

                memcpy(output + used, source, count);
                used += count;
                (*position)++;
            }

            return used;
        }

        static void
        ankus_write_completion_field(int backend, const char *name, const unsigned char *text, int length)
        {
            if (text == NULL)
            {
                return;
            }

            int position = 0;
            int part = 0;
            do
            {
                char output[1024];
                size_t used = ankus_escape_completion_chunk(text, length, &position, output, sizeof(output));
                write_stderr("ANKUS CLEANUP [backend %d] %s%s: %.*s\n", backend, name,
                    part == 0 ? "" : " CONTINUED", (int) used, output);
                part++;
            }
            while (position < length);
        }

        static void
        ankus_write_completion_warning(AnkusError *error)
        {
            static const char *names[] = {"MESSAGE", "DETAIL", "HINT", "CONTEXT", "SCHEMA", "TABLE", "COLUMN",
                "DATATYPE", "CONSTRAINT", "QUERY", "FILE", "ROUTINE", "DETAIL_LOG", "BACKTRACE", "DOMAIN"};
            int backend = MyProcPid;
            const char *sqlstate = unpack_sql_state(error->sqlstate);
            AnkusValue *message = &error->fields[ANKUS_ERROR_MESSAGE];
            char message_name[32];
            StaticAssertStmt(lengthof(names) == ANKUS_ERROR_FIELD_COUNT, "every diagnostic field needs a name");
            snprintf(message_name, sizeof(message_name), "WARNING: %s", sqlstate);
            if (message->data != NULL)
            {
                ankus_write_completion_field(backend, message_name, message->data, message->length);
            }
            else
            {
                ankus_write_completion_field(backend, message_name, (const unsigned char *) error->message,
                    (int) strnlen(error->message, sizeof(error->message)));
            }

            for (int index = ANKUS_ERROR_DETAIL; index < ANKUS_ERROR_FIELD_COUNT; index++)
            {
                AnkusValue *value = &error->fields[index];
                ankus_write_completion_field(backend, names[index], value->data, value->length);
            }

            if (error->position > 0)
            {
                write_stderr("ANKUS CLEANUP [backend %d] POSITION: %d\n", backend, error->position);
            }

            if (error->internal_position > 0)
            {
                write_stderr("ANKUS CLEANUP [backend %d] INTERNAL_POSITION: %d\n", backend, error->internal_position);
            }

            if (error->line > 0)
            {
                write_stderr("ANKUS CLEANUP [backend %d] LINE: %d\n", backend, error->line);
            }

            ankus_release_error(error);
        }

        static void
        ankus_report_completion_cleanup(AnkusError *error)
        {
            if (error->report_level >= 12)
            {
                /* FATAL runs AbortTransaction during process exit. A durable commit
                 * or prepare can no longer be aborted, so PostgreSQL requires PANIC. */
                volatile int level = error->report_level == 12 && !ankus_memory_completion_after_commit() ? FATAL : PANIC;
                uint32 interrupt_holdoff = InterruptHoldoffCount;
                HOLD_INTERRUPTS();
                PG_TRY();
                {
                    ankus_report(error, level);
                }
                PG_CATCH();
                {
                    /* Building the complete report failed. Its ERROR must not replace the
                     * terminal intent with an abortable failure, so terminate with the
                     * transported message alone; it needs no conversion or allocation. */
                    char message[sizeof(error->message) * 4];
                    ankus_escape_diagnostic_text(error->message, message, sizeof(message));
                    ereport(level, (errcode(error->sqlstate != 0 ? error->sqlstate : ERRCODE_INTERNAL_ERROR),
                        errmsg_internal("%s", message)));
                }
                PG_END_TRY();
                InterruptHoldoffCount = interrupt_holdoff;
            }

            /* Transaction completion cannot safely allocate, invoke log hooks, perform
             * client-encoding conversion, or flush PostgreSQL's active error stack.
             * PostgreSQL's emergency stderr path is deliberately valid before ordinary
             * error reporting is available and cannot start a second abort. */
            ankus_write_completion_warning(error);
        }

        typedef int (*AnkusMemoryCallbackFunction)(intptr_t, AnkusMemoryApi *, AnkusError *);

        typedef struct AnkusMemoryCallback
        {
            MemoryContextCallback callback;
            MemoryContext owner;
            MemoryContext cleanup_context;
            uint64 context_id;
            intptr_t handle;
            AnkusMemoryCallbackFunction invoke;
            struct AnkusMemoryCallback *next;
        } AnkusMemoryCallback;

        static AnkusMemoryCallback *ankus_memory_callbacks;

        static void
        ankus_memory_callback(void *argument)
        {
            AnkusMemoryCallback *record = argument;
            AnkusMemoryCallback **slot = &ankus_memory_callbacks;
            while (*slot != record)
            {
                slot = &(*slot)->next;
            }

            *slot = record->next;
            intptr_t handle = record->handle;
            AnkusMemoryCallbackFunction invoke = record->invoke;
            MemoryContext owner = record->owner;
            MemoryContext cleanup_context = record->cleanup_context;
            free(record);
            if (handle == 0)
            {
                return;
            }

            AnkusMemoryApi memory = {0};
            AnkusMemoryProtection scope = {0};
            AnkusError error = {0};
            int status;
            MemoryContext previous_cleanup = ankus_memory_cleanup_context;
            bool previous_error_cleanup = ankus_memory_error_cleanup;
            ankus_memory_cleanup_context = cleanup_context;
            ankus_memory_error_cleanup = previous_error_cleanup || ankus_memory_contains(ErrorContext, owner);
            ankus_memory_initialize(&memory);
            ankus_memory_protect(&scope, owner, true);
            PG_TRY();
            {
                ANKUS_MANAGED_INVOKE(status, &error, invoke(handle, &memory, &error));
            }
            PG_FINALLY();
            {
                ankus_memory_cleanup_context = previous_cleanup;
                ankus_memory_error_cleanup = previous_error_cleanup;
                ankus_memory_protection = scope.previous;
            }
            PG_END_TRY();
            if (status != 0)
            {
                if (ankus_transaction_cleanup_active())
                {
                    ankus_report_completion_cleanup(&error);
                }
                else
                {
                    ankus_report(&error, error.report_level == 0 ? ERROR : ankus_log_level(error.report_level - 1));
                }
            }

            ankus_release_error(&error);
        }

        struct AnkusMemoryAllocation
        {
            uint64 id;
            void *pointer;
            Size size;
            Size alignment;
            int flags;
            uint64 context_id;
            AnkusMemoryAllocation *next;
        };

        struct AnkusMemoryContext
        {
            uint64 id;
            MemoryContext context;
            uint64 parent_id;
            uintptr_t generation;
            bool alive;
            bool reserved;
            AnkusMemoryProtection *retain_reset;
            bool callback_pending;
            char *owned_name;
            MemoryContextCallback callback;
            AnkusMemoryContext *next;
        };

        static AnkusMemoryContext *ankus_memory_contexts;
        static AnkusMemoryAllocation *ankus_memory_allocations;
        static uint64 ankus_memory_next_context = 1;
        static uint64 ankus_memory_next_allocation = 1;

        static AnkusMemoryContext *
        ankus_memory_context_by_id(uint64 id)
        {
            for (AnkusMemoryContext *entry = ankus_memory_contexts; entry != NULL; entry = entry->next)
            {
                if (entry->id == id && entry->alive)
                {
                    return entry;
                }
            }

            return NULL;
        }

        static AnkusMemoryContext *
        ankus_memory_context_by_pointer(MemoryContext context)
        {
            for (AnkusMemoryContext *entry = ankus_memory_contexts; entry != NULL; entry = entry->next)
            {
                if (entry->alive && entry->context == context)
                {
                    return entry;
                }
            }

            return NULL;
        }

        static void ankus_stringinfo_remove_context(uint64 context_id);
        static void ankus_list_remove_context(uint64 context_id);

        static void
        ankus_memory_remove_allocations(uint64 context_id)
        {
            ankus_stringinfo_remove_context(context_id);
            ankus_list_remove_context(context_id);
            AnkusMemoryAllocation **slot = &ankus_memory_allocations;
            while (*slot != NULL)
            {
                AnkusMemoryAllocation *allocation = *slot;
                if (allocation->context_id == context_id)
                {
                    *slot = allocation->next;
                    free(allocation);
                    continue;
                }

                slot = &allocation->next;
            }
        }

        static AnkusMemoryAllocation *
        ankus_memory_allocation_by_id(uint64 id)
        {
            for (AnkusMemoryAllocation *allocation = ankus_memory_allocations; allocation != NULL; allocation = allocation->next)
            {
                if (allocation->id == id && allocation->pointer != NULL && allocation->context_id != 0)
                {
                    AnkusMemoryContext *context = ankus_memory_context_by_id(allocation->context_id);
                    if (context != NULL)
                    {
                        return allocation;
                    }
                }
            }

            return NULL;
        }

        /* Finds an allocation a request names or just made, which must still be live. */
        static AnkusMemoryAllocation *
        ankus_memory_allocation_required(uint64 id)
        {
            AnkusMemoryAllocation *allocation = ankus_memory_allocation_by_id(id);
            if (allocation == NULL)
            {
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("the PostgreSQL allocation handle is stale")));
            }

            return allocation;
        }

        /* Finds the context that owns a live allocation. */
        static AnkusMemoryContext *
        ankus_memory_allocation_owner(const AnkusMemoryAllocation *allocation)
        {
            AnkusMemoryContext *owner = ankus_memory_context_by_id(allocation->context_id);
            if (owner == NULL)
            {
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("the PostgreSQL allocation handle is stale")));
            }

            return owner;
        }

        static void
        ankus_memory_remove_context(AnkusMemoryContext *target)
        {
            AnkusMemoryContext **slot = &ankus_memory_contexts;
            while (*slot != NULL)
            {
                if (*slot == target)
                {
                    *slot = target->next;
                    if (target->owned_name != NULL)
                    {
                        MemoryContextSetIdentifier(target->context, NULL);
                        free(target->owned_name);
                    }

                    free(target);
                    return;
                }

                slot = &(*slot)->next;
            }
        }

        static void
        ankus_memory_mark_dead(AnkusMemoryContext *entry)
        {
            if (entry == NULL || !entry->alive)
            {
                return;
            }

            entry->alive = false;
            ankus_memory_remove_allocations(entry->id);
            entry->callback_pending = false;
        }

        static void
        ankus_memory_reset_callback(void *argument)
        {
            AnkusMemoryContext *entry = (AnkusMemoryContext *) argument;
            if (entry == NULL)
            {
                return;
            }

            ankus_memory_remove_allocations(entry->id);
            if (entry->retain_reset)
            {
                entry->callback_pending = false;
                /* Zero permanently retires borrowing if the generation space is exhausted. */
                if (entry->generation != 0)
                {
                    entry->generation++;
                }

                return;
            }

            ankus_memory_mark_dead(entry);
            ankus_memory_remove_context(entry);
        }

        static void
        ankus_memory_free_allocation(AnkusMemoryAllocation *allocation)
        {
            AnkusMemoryAllocation **slot = &ankus_memory_allocations;
            while (*slot != NULL)
            {
                if (*slot == allocation)
                {
                    *slot = allocation->next;
                    free(allocation);
                    return;
                }

                slot = &(*slot)->next;
            }
        }

        static AnkusMemoryContext *
        ankus_memory_register_context(MemoryContext context)
        {
            if (context == NULL)
            {
                return NULL;
            }

            AnkusMemoryContext *existing = ankus_memory_context_by_pointer(context);
            if (existing != NULL)
            {
                return existing;
            }

            AnkusMemoryContext *entry = (AnkusMemoryContext *) calloc(1, sizeof(AnkusMemoryContext));
            if (entry == NULL)
            {
                ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("out of memory registering an Ankus memory context")));
            }

            if (ankus_memory_next_context == 0)
            {
                free(entry);
                ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("Ankus memory context identity space is exhausted")));
            }

            entry->id = ankus_memory_next_context++;
            entry->context = context;
            entry->generation = 1;
            entry->alive = true;
            entry->callback.func = ankus_memory_reset_callback;
            entry->callback.arg = entry;
            entry->callback.next = NULL;
            entry->next = ankus_memory_contexts;
            ankus_memory_contexts = entry;
            MemoryContextRegisterResetCallback(context, &entry->callback);
            entry->callback_pending = true;
            return entry;
        }

        static AnkusMemoryContext *
        ankus_memory_context_from_request(AnkusMemoryRequest *request)
        {
            AnkusMemoryContext *entry = ankus_memory_context_by_id((uint64) request->context);
            if (entry == NULL)
            {
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("the PostgreSQL memory context handle is stale or belongs to another backend")));
            }

            return entry;
        }

        static uint64
        ankus_memory_context_id(MemoryContext context)
        {
            AnkusMemoryContext *entry = ankus_memory_register_context(context);
            return entry == NULL ? 0 : entry->id;
        }

        static const char *
        ankus_memory_check_reserved(MemoryContext context, AnkusMemoryOperation operation)
        {
            for (AnkusMemoryContext *entry = ankus_memory_contexts; entry != NULL; entry = entry->next)
            {
                if (!entry->alive || !entry->reserved)
                {
                    continue;
                }

                /* Ancestors retain executor descriptors and call state even when a
                 * reset-only operation would leave the multi-call child intact. */
                bool affects = ankus_memory_contains(context, entry->context) &&
                    (operation != ANKUS_MEMORY_RESET_CHILDREN || context != entry->context);
                if (affects)
                {
                    return "a live set-returning function memory context cannot be reset or deleted";
                }
            }

            return NULL;
        }

        static const char *
        ankus_memory_check_infrastructure(MemoryContext context)
        {
            if (context == TopMemoryContext || context == ErrorContext || context == PostmasterContext ||
                context == CacheMemoryContext || context == MessageContext || context == TopTransactionContext ||
                context == CurTransactionContext || context == PortalContext)
            {
                return "PostgreSQL infrastructure memory contexts cannot be reset or deleted by an extension";
            }

            return NULL;
        }

        static const char *
        ankus_memory_check_delete(AnkusMemoryContext *entry)
        {
            const char *message = ankus_memory_check_infrastructure(entry->context);
            if (message != NULL)
            {
                return message;
            }

            if (entry->context == TopMemoryContext || entry->context->parent == NULL)
            {
                return "top-level PostgreSQL memory contexts cannot be deleted by an extension";
            }

            for (MemoryContext current = CurrentMemoryContext; current != NULL; current = MemoryContextGetParent(current))
            {
                if (current == entry->context)
                {
                    return "the current memory context or one of its ancestors cannot be deleted";
                }
            }

            return NULL;
        }

        static void
        ankus_memory_register_name(AnkusMemoryContext *entry, const char *name, Size length)
        {
            char *server_name = pg_any_to_server(name, (int) length, PG_UTF8);
            Size server_length = strlen(server_name);
            char *identifier = (char *) malloc(server_length + 1);
            if (identifier == NULL)
            {
                ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("out of memory naming an Ankus memory context")));
            }

            memcpy(identifier, server_name, server_length + 1);
            if (server_name != name)
            {
                pfree(server_name);
            }

            entry->owned_name = identifier;
            MemoryContextSetIdentifier(entry->context, identifier);
        }

        static void
        ankus_memory_validate_sizes(const AnkusMemoryContextSizes *sizes)
        {
            if (sizes->initial < 1024 || sizes->initial != MAXALIGN(sizes->initial) ||
                sizes->maximum < sizes->initial || !AllocHugeSizeIsValid(sizes->maximum) ||
                sizes->maximum != MAXALIGN(sizes->maximum) ||
                (sizes->minimum != 0 && (sizes->minimum < 1024 ||
                    sizes->minimum != MAXALIGN(sizes->minimum) || sizes->minimum > sizes->maximum)))
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid AllocSet context sizes")));
            }

        #if PG_VERSION_NUM >= 160000
            if (sizes->maximum > MEMORYCHUNK_MAX_BLOCKOFFSET)
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("AllocSet maximum block size exceeds the native chunk offset limit")));
            }
        #endif
        }

        static int
        ankus_memory_validate_allocation(Size size, Size alignment, int options)
        {
            if ((options & ~7) != 0 || (alignment != 0 &&
                ((alignment & (alignment - 1)) != 0 || alignment >= ((Size) 128 * 1024 * 1024))))
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid allocation options or alignment")));
            }

            int flags = (options & 1) != 0 ? MCXT_ALLOC_ZERO : 0;
            if ((options & 2) != 0)
            {
                flags |= MCXT_ALLOC_NO_OOM;
            }

            if ((options & 4) != 0)
            {
                flags |= MCXT_ALLOC_HUGE;
            }

            Size limit = (flags & MCXT_ALLOC_HUGE) != 0 ? MaxAllocHugeSize : MaxAllocSize;
            if (size > limit)
            {
                elog(ERROR, "invalid memory alloc request size %zu", size);
            }

            if (alignment > MAXIMUM_ALIGNOF)
            {
        #if PG_VERSION_NUM >= 160000
                Size overhead = PallocAlignedExtraBytes(alignment);
        #ifdef MEMORY_CONTEXT_CHECKING
                overhead++;
        #endif
                if (overhead > limit || size > limit - overhead)
                {
                    elog(ERROR, "invalid aligned memory alloc request size %zu", size);
                }

        #if PG_VERSION_NUM < 160015 || (PG_VERSION_NUM >= 170000 && PG_VERSION_NUM < 170011) || (PG_VERSION_NUM >= 180000 && PG_VERSION_NUM < 180006)
                if ((flags & MCXT_ALLOC_NO_OOM) != 0)
                {
                    ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                        errmsg("aligned no-OOM allocation requires PostgreSQL 16.15, 17.11, 18.6 or later")));
                }
        #endif
        #if PG_VERSION_NUM >= 190000 && PG_VERSION_NUM < 190001
                /* PostgreSQL prereleases share PG_VERSION_NUM, including the unfixed betas. */
                if ((flags & MCXT_ALLOC_NO_OOM) != 0 &&
                    (strstr(PG_VERSION, "devel") != NULL ||
                        (strncmp(PG_VERSION, "19beta", 6) == 0 && atoi(&PG_VERSION[6]) < 3)))
                {
                    ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED),
                        errmsg("aligned no-OOM allocation requires PostgreSQL 19 beta 3 or later")));
                }
        #endif
        #else
                ereport(ERROR, (errcode(ERRCODE_FEATURE_NOT_SUPPORTED), errmsg("aligned allocation requires PostgreSQL 16 or later")));
        #endif
            }

            return flags;
        }

        static void *
        ankus_memory_allocate(MemoryContext context, Size size, Size alignment, int options)
        {
            int flags = ankus_memory_validate_allocation(size, alignment, options);
        #if PG_VERSION_NUM >= 160000
            if (alignment > MAXIMUM_ALIGNOF)
            {
                return MemoryContextAllocAligned(context, size, alignment, flags);
            }
        #endif

            return MemoryContextAllocExtended(context, size, flags);
        }

        static AnkusMemoryAllocation *
        ankus_memory_reserve_allocation(void)
        {
            AnkusMemoryAllocation *allocation = calloc(1, sizeof(*allocation));
            if (allocation == NULL || ankus_memory_next_allocation == 0)
            {
                free(allocation);
                ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("unable to register an Ankus allocation")));
            }

            allocation->id = ankus_memory_next_allocation++;
            return allocation;
        }

        static void
        ankus_memory_publish_allocation(AnkusMemoryAllocation *allocation, AnkusMemoryContext *context,
            void *pointer, Size size, Size alignment, int options)
        {
            allocation->pointer = pointer;
            allocation->size = size;
            allocation->alignment = alignment;
            allocation->flags = options & 4;
            allocation->context_id = context->id;
            allocation->next = ankus_memory_allocations;
            ankus_memory_allocations = allocation;
        }

        static void
        ankus_memory_check_chunk_operation(MemoryContext context, const char *operation)
        {
        #if PG_VERSION_NUM >= 170000
            /* Production Bump allocations have no chunk header. Never dispatch through
             * pfree, repalloc, or GetMemoryChunkContext before checking the known owner. */
            if (IsA(context, BumpContext))
            {
                elog(ERROR, "%s is not supported by the bump memory allocator", operation);
            }
        #else
            (void) context;
            (void) operation;
        #endif
        }

        static void
        ankus_memory_create(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            MemoryContext parent = request->context == 0 ? CurrentMemoryContext :
                ankus_memory_context_from_request(request)->context;
            const AnkusMemoryContextSizes defaults = {ALLOCSET_DEFAULT_MINSIZE,
                ALLOCSET_DEFAULT_INITSIZE, ALLOCSET_DEFAULT_MAXSIZE};
            const AnkusMemoryContextSizes *sizes = request->pointer == 0 ? &defaults :
                (const AnkusMemoryContextSizes *) request->pointer;
            ankus_memory_validate_sizes(sizes);
            MemoryContext context = AllocSetContextCreate(parent, "Ankus memory context",
                sizes->minimum, sizes->initial, sizes->maximum);
            MemoryContext caller = CurrentMemoryContext;
            PG_TRY();
            {
                MemoryContextSwitchTo(context);
                AnkusMemoryContext *entry = ankus_memory_register_context(context);
                ankus_memory_register_name(entry, (const char *) request->data, (Size) request->length);
                result->context = (intptr_t) entry->id;
                MemoryContextSwitchTo(caller);
            }
            PG_CATCH();
            {
                MemoryContext propagating = MemoryContextSwitchTo(caller);
                MemoryContextDelete(context);
                MemoryContextSwitchTo(propagating);
                PG_RE_THROW();
            }
            PG_END_TRY();
        }

        static void
        ankus_memory_name(AnkusMemoryContext *entry, AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            MemoryContext caller = CurrentMemoryContext;
            MemoryContext temporary = AllocSetContextCreate(caller, "Ankus context name", ALLOCSET_SMALL_SIZES);
            PG_TRY();
            {
                MemoryContextSwitchTo(temporary);
                const char *name = entry->context->ident == NULL ? entry->context->name : entry->context->ident;
                char *utf8 = pg_server_to_any(name, strlen(name), PG_UTF8);
                Size length = strlen(utf8);
                if (request->data != 0)
                {
                    if (request->length < length)
                    {
                        ereport(ERROR, (errcode(ERRCODE_DATA_EXCEPTION), errmsg("the context identifier buffer is too small")));
                    }

                    memcpy((void *) request->data, utf8, length);
                }

                result->length = length;
            }
            PG_FINALLY();
            {
                ANKUS_FINALLY_RESTORE(caller);
                MemoryContextDelete(temporary);
            }
            PG_END_TRY();
        }

        static void
        ankus_memory_finish_reset(AnkusMemoryContext *entry, AnkusMemoryProtection *scope)
        {
            ankus_memory_protection = scope->previous;
            entry->retain_reset = NULL;
            if (!entry->callback_pending)
            {
                MemoryContextRegisterResetCallback(entry->context, &entry->callback);
                entry->callback_pending = true;
            }
        }

        static void
        ankus_memory_reset(AnkusMemoryContext *entry, AnkusMemoryRequest *request)
        {
            AnkusMemoryProtection scope = {0};
            ankus_memory_protect(&scope, entry->context, true);
            entry->retain_reset = &scope;
            PG_TRY();
            {
                if (request->operation == ANKUS_MEMORY_RESET)
                {
                    MemoryContextReset(entry->context);
                }
                else
                {
                    MemoryContextResetOnly(entry->context);
                }
            }
            PG_CATCH();
            {
                ankus_memory_finish_reset(entry, &scope);
                PG_RE_THROW();
            }
            PG_END_TRY();
            ankus_memory_finish_reset(entry, &scope);
        }

        static void
        ankus_memory_reset_children(AnkusMemoryContext *entry)
        {
            AnkusMemoryProtection scope = {0};
            ankus_memory_protect(&scope, entry->context, true);
            for (AnkusMemoryContext *child = ankus_memory_contexts; child != NULL; child = child->next)
            {
                for (MemoryContext parent = MemoryContextGetParent(child->context); parent != NULL;
                     parent = MemoryContextGetParent(parent))
                {
                    if (parent == entry->context)
                    {
                        child->retain_reset = &scope;
                        break;
                    }
                }
            }

            PG_TRY();
            {
                MemoryContextResetChildren(entry->context);
            }
            PG_FINALLY();
            {
                ankus_memory_protection = scope.previous;
                for (AnkusMemoryContext *child = ankus_memory_contexts; child != NULL; child = child->next)
                {
                    if (child->retain_reset == &scope)
                    {
                        child->retain_reset = NULL;
                        if (!child->callback_pending)
                        {
                            MemoryContextRegisterResetCallback(child->context, &child->callback);
                            child->callback_pending = true;
                        }
                    }
                }
            }
            PG_END_TRY();
        }

        static void
        ankus_memory_delete(AnkusMemoryContext *entry)
        {
            if (entry != NULL)
            {
                AnkusMemoryProtection scope = {0};
                ankus_memory_protect(&scope, entry->context, true);
                PG_TRY();
                {
                    MemoryContextDelete(entry->context);
                }
                PG_FINALLY();
                {
                    ankus_memory_protection = scope.previous;
                }
                PG_END_TRY();
            }
        }

        static void
        ankus_memory_allocate_request(AnkusMemoryContext *entry, AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            /* Reserve bookkeeping before native storage. This also avoids trying
             * to free a headerless Bump chunk if registration cannot succeed. */
            AnkusMemoryAllocation *allocation = ankus_memory_reserve_allocation();
            void *pointer;
            PG_TRY();
            {
                pointer = ankus_memory_allocate(entry->context, (Size) request->length,
                    (Size) request->alignment, request->flags);
            }
            PG_CATCH();
            {
                free(allocation);
                PG_RE_THROW();
            }
            PG_END_TRY();

            if (pointer == NULL)
            {
                free(allocation);
                return;
            }

            ankus_memory_publish_allocation(allocation, entry, pointer,
                (Size) request->length, (Size) request->alignment, request->flags);
            result->context = (intptr_t) entry->id;
            result->pointer = (intptr_t) allocation->id;
            result->length = request->length;
        }

        """ + NativeStringInfoBridge.Source + NativeListBridge.Source + NativeItemPointerMemoryBridge.Source + NativeBindingBridge.Source + NativeNodeBridge.Source + NativeRawCallBridge.Source + NativeSharedMemoryBridge.Source + NativeSpinLockBridge.Source + NativeBackgroundWorkerBridge.Source + NativeReplicationBridge.Source + NativeReplicationOriginBridge.Source + NativeQuoteBridge.Source + NativeErrorContextBridge.Source + NativeProcessExitBridge.Source + NativeChildProcessBridge.Source + NativeRelationWriterBridge.Source + """

        /* Resizes a checked allocation in its owning context; with no-OOM it leaves the result empty on failure. */
        static void
        ankus_memory_reallocate(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            AnkusMemoryAllocation *allocation = ankus_memory_allocation_required((uint64) request->context);
            if ((request->flags & ~3) != 0)
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid allocation resize options")));
            }

            Size size = (Size) request->length;
            int options = allocation->flags | (request->flags & 2);
            int flags = ankus_memory_validate_allocation(size, allocation->alignment, options);
            MemoryContext owner = ankus_memory_allocation_owner(allocation)->context;
            ankus_memory_check_chunk_operation(owner, "realloc");
            void *pointer;
            if (allocation->alignment > MAXIMUM_ALIGNOF || (flags & MCXT_ALLOC_NO_OOM) != 0)
            {
                /* Keep the old chunk intact until replacement succeeds, including
                 * older native aligned realloc implementations that lose flags. */
                pointer = ankus_memory_allocate(owner, size, allocation->alignment, options);
                if (pointer == NULL)
                {
                    return;
                }

                memcpy(pointer, allocation->pointer, Min(size, allocation->size));
                pfree(allocation->pointer);
            }
            else
            {
                pointer = (flags & MCXT_ALLOC_HUGE) != 0 ? repalloc_huge(allocation->pointer, size) :
                    repalloc(allocation->pointer, size);
            }

            if ((request->flags & 1) != 0 && size > allocation->size)
            {
                memset((char *) pointer + allocation->size, 0, size - allocation->size);
            }

            allocation->pointer = pointer;
            allocation->size = size;
            result->context = (intptr_t) allocation->id;
            result->pointer = (intptr_t) allocation->id;
            result->length = request->length;
        }

        /* Registers a caller's live palloc chunk as a checked allocation of the context that owns it. */
        static void
        ankus_memory_adopt(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            AnkusMemoryContext *entry = ankus_memory_context_from_request(request);
            void *pointer = (void *) request->pointer;
            if (pointer == NULL || (request->flags & ~4) != 0)
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid allocation adoption request")));
            }

            ankus_memory_validate_allocation((Size) request->length, (Size) request->alignment, request->flags);
            for (AnkusMemoryAllocation *existing = ankus_memory_allocations; existing != NULL; existing = existing->next)
            {
                if (existing->pointer == pointer)
                {
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("the PostgreSQL allocation already has a checked owner")));
                }
            }

            /* The caller guarantees live palloc provenance and the exact accessible
             * length. Chunk headers cannot validate arbitrary pointers or lengths. */
            ankus_memory_check_chunk_operation(entry->context, "GetMemoryChunkContext");
            if (GetMemoryChunkContext(pointer) != entry->context)
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("the PostgreSQL allocation belongs to a different memory context")));
            }

            AnkusMemoryAllocation *allocation = ankus_memory_reserve_allocation();
            ankus_memory_publish_allocation(allocation, entry, pointer,
                (Size) request->length, (Size) request->alignment, request->flags);
            result->context = (intptr_t) entry->id;
            result->pointer = (intptr_t) allocation->id;
            result->length = request->length;
        }

        /* Copies between a managed buffer and a borrowed address whose lifetime a context generation anchors. */
        static void
        ankus_memory_access_reference(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            AnkusMemoryContext *entry = ankus_memory_context_from_request(request);
            if (entry->generation == 0 || entry->generation != (uintptr_t) request->other)
            {
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("the borrowed PostgreSQL memory reference is stale")));
            }

            if (request->pointer == 0 || (request->length != 0 && request->data == 0))
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                    errmsg("the borrowed memory address or managed buffer is null")));
            }

            /* The unsafe caller proves address accessibility and any lifetime shorter
             * than this anchor. Stack, interior and resource-owned addresses need no
             * palloc header, exclusive ownership, or per-reference native registry. */
            if (request->length != 0)
            {
                if (request->operation == ANKUS_MEMORY_READ_REFERENCE)
                {
                    memcpy((void *) request->data, (const void *) request->pointer, (Size) request->length);
                }
                else
                {
                    memcpy((void *) request->pointer, (const void *) request->data, (Size) request->length);
                }
            }

            result->context = (intptr_t) entry->id;
            result->pointer = request->pointer;
            result->length = request->length;
        }

        /* Reads, writes or clears a range of a checked allocation. */
        static void
        ankus_memory_access_allocation(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            AnkusMemoryAllocation *allocation = ankus_memory_allocation_required((uint64) request->context);
            uint64 offset = (uint64) request->value;
            if (offset > (uint64) allocation->size || (uint64) request->length >
                (uint64) allocation->size - offset)
            {
                ereport(ERROR, (errcode(ERRCODE_DATA_EXCEPTION), errmsg("the PostgreSQL allocation range is outside its chunk")));
            }

            unsigned char *destination = (unsigned char *) allocation->pointer + offset;
            if (request->operation == ANKUS_MEMORY_READ)
            {
                if (request->data == 0 && request->length != 0)
                {
                    ereport(ERROR, (errmsg("the managed memory destination is null")));
                }

                if (request->data != 0)
                {
                    memcpy((void *) request->data, destination, (Size) request->length);
                }

                result->pointer = (intptr_t) allocation->pointer;
            }
            else if (request->operation == ANKUS_MEMORY_WRITE)
            {
                if (request->data == 0 && request->length != 0)
                {
                    ereport(ERROR, (errmsg("the managed memory source is null")));
                }

                if (request->data != 0)
                {
                    memcpy(destination, (const void *) request->data, (Size) request->length);
                }
            }
            else
            {
                memset(destination, 0, (Size) request->length);
            }

            result->length = request->length;
        }

        static void
        ankus_memory_execute(AnkusMemoryApi *api, AnkusMemoryRequest *request, AnkusMemoryResult *result, volatile bool *recovered)
        {
            memset(result, 0, sizeof(*result));
            switch ((AnkusMemoryOperation) request->operation)
            {
                case ANKUS_MEMORY_CHECK_INTERRUPTS:
                    CHECK_FOR_INTERRUPTS();
                    break;
                case ANKUS_MEMORY_CURRENT:
                {
                    /* The generation lets a callback's borrowed inputs capture their lifetime in this one call. */
                    AnkusMemoryContext *entry = ankus_memory_register_context(CurrentMemoryContext);
                    result->context = (intptr_t) entry->id;
                    result->value = (intptr_t) entry->generation;
                    break;
                }
                case ANKUS_MEMORY_CALLBACK:
                    result->context = (intptr_t) ankus_memory_context_id(api->result_context);
                    break;
                case ANKUS_MEMORY_PREDEFINED:
                {
                    MemoryContext context = NULL;
                    switch ((int) request->value)
                    {
                        case 0: context = CurrentMemoryContext; break;
                        case 1: context = TopMemoryContext; break;
                        case 2: context = PortalContext; break;
                        case 3: context = ErrorContext; break;
                        case 4: context = PostmasterContext; break;
                        case 5: context = CacheMemoryContext; break;
                        case 6: context = MessageContext; break;
                        /* PostgreSQL 18 keeps TopTransactionContext between transactions, where earlier versions delete it;
                         * CurTransactionContext is set only while a transaction is in progress on every version. */
                        case 7: context = CurTransactionContext != NULL ? TopTransactionContext : NULL; break;
                        case 8: context = CurTransactionContext; break;
                        default: ereport(ERROR, (errmsg("invalid predefined PostgreSQL memory context")));
                    }

                    if (context != NULL)
                    {
                        result->context = (intptr_t) ankus_memory_context_id(context);
                    }

                    break;
                }
                case ANKUS_MEMORY_BORROW_NATIVE:
                case ANKUS_MEMORY_CHUNK_OWNER:
                {
                    /* The caller vouches for the address; a context's node tag is the one check that is possible.
                     * A failed check returns no context, which managed code reports without a PostgreSQL error. */
                    void *address = (void *) request->value;
                    MemoryContext context;
                    if (address == NULL || (request->operation == ANKUS_MEMORY_CHUNK_OWNER && address != (void *) MAXALIGN(address)))
                        break;
                    context = request->operation == ANKUS_MEMORY_BORROW_NATIVE ? (MemoryContext) address : GetMemoryChunkContext(address);
                    if (MemoryContextIsValid(context))
                        result->context = (intptr_t) ankus_memory_context_id(context);
                    break;
                }
                case ANKUS_MEMORY_NATIVE_POINTER:
                    result->pointer = (intptr_t) ankus_memory_context_from_request(request)->context;
                    break;
                case ANKUS_MEMORY_CREATE:
                    ankus_memory_create(request, result);
                    break;
                case ANKUS_MEMORY_STRINGINFO:
                    ankus_stringinfo_execute(request, result);
                    break;
                case ANKUS_MEMORY_ITEM_POINTER:
                    ankus_memory_item_pointer(request, result);
                    break;
                case ANKUS_MEMORY_FORMAT_NODE:
                    ankus_memory_format_node(request);
                    break;
                case ANKUS_MEMORY_NATIVE_CALL:
                    ankus_memory_native_call(request, result);
                    break;
                case ANKUS_MEMORY_SHARED:
                    ankus_memory_shared(request, result);
                    break;
                case ANKUS_MEMORY_SPIN:
                    ankus_memory_spin(request, result);
                    break;
                case ANKUS_MEMORY_WORKER:
                    ankus_memory_worker(request, result, recovered);
                    break;
                case ANKUS_MEMORY_REPLICATION:
                    ankus_memory_replication(request, result);
                    break;
                case ANKUS_MEMORY_QUOTE:
                    ankus_memory_quote(request, result);
                    break;
                case ANKUS_MEMORY_ERROR_CONTEXT:
                    ankus_memory_error_context(request, result);
                    break;
                case ANKUS_MEMORY_PROCESS_EXIT:
                    ankus_memory_process_exit(request);
                    break;
                case ANKUS_MEMORY_CHILD_PROCESS:
                    ankus_memory_child_process(request, result);
                    break;
                case ANKUS_MEMORY_REPLICATION_ORIGIN:
                    ankus_memory_replication_origin(request, result);
                    break;
        #ifdef ANKUS_TUPLE_BRIDGE
                case ANKUS_MEMORY_RELATION_WRITER:
                    ankus_memory_relation_writer(request, result);
                    break;
        #endif
                case ANKUS_MEMORY_LIST:
                    ankus_list_execute(request, result);
                    break;
                case ANKUS_MEMORY_PARENT:
                {
                    AnkusMemoryContext *entry = ankus_memory_context_from_request(request);
                    result->context = (intptr_t) ankus_memory_context_id(MemoryContextGetParent(entry->context));
                    break;
                }
                case ANKUS_MEMORY_NAME:
                    ankus_memory_name(ankus_memory_context_from_request(request), request, result);
                    break;
                case ANKUS_MEMORY_RESET:
                case ANKUS_MEMORY_RESET_ONLY:
                    ankus_memory_reset(ankus_memory_context_from_request(request), request);
                    break;
                case ANKUS_MEMORY_RESET_CHILDREN:
                    ankus_memory_reset_children(ankus_memory_context_from_request(request));
                    break;
                case ANKUS_MEMORY_DELETE:
                    ankus_memory_delete(ankus_memory_context_by_id((uint64) request->context));
                    break;
                case ANKUS_MEMORY_SWITCH:
                {
                    AnkusMemoryContext *target = ankus_memory_context_from_request(request);
                    MemoryContext previous = MemoryContextSwitchTo(target->context);
                    result->context = (intptr_t) ankus_memory_context_id(previous);
                    break;
                }
                case ANKUS_MEMORY_ALLOCATE:
                    ankus_memory_allocate_request(ankus_memory_context_from_request(request), request, result);
                    break;
                case ANKUS_MEMORY_ALLOCATE_VARLENA:
                {
                    if (request->length == 0 || request->length > MaxAllocSize - VARHDRSZ)
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid native varlena payload size")));
                    Size payload = (Size) request->length;
                    AnkusMemoryRequest allocation_request = *request;
                    allocation_request.length = payload + VARHDRSZ;
                    allocation_request.flags = 1; /* Ankus zero-initialization option. */
                    allocation_request.alignment = 0;
                    ankus_memory_allocate_request(ankus_memory_context_from_request(request), &allocation_request, result);
                    AnkusMemoryAllocation *allocation = ankus_memory_allocation_required((uint64) result->pointer);
                    if (payload + VARHDRSZ_SHORT <= VARATT_SHORT_MAX)
                    {
                        SET_VARSIZE_SHORT(allocation->pointer, payload + VARHDRSZ_SHORT);
                        result->value = VARHDRSZ_SHORT;
                    }
                    else
                    {
                        SET_VARSIZE(allocation->pointer, payload + VARHDRSZ);
                        result->value = VARHDRSZ;
                    }

                    break;
                }
                case ANKUS_MEMORY_REALLOCATE:
                    ankus_memory_reallocate(request, result);
                    break;
                case ANKUS_MEMORY_DETACH:
                {
                    AnkusMemoryAllocation *allocation = ankus_memory_allocation_required((uint64) request->context);

                    result->pointer = (intptr_t) allocation->pointer;
                    ankus_memory_free_allocation(allocation);
                    break;
                }
                case ANKUS_MEMORY_ADOPT:
                    ankus_memory_adopt(request, result);
                    break;
                case ANKUS_MEMORY_FREE:
                {
                    AnkusMemoryAllocation *allocation = ankus_memory_allocation_by_id((uint64) request->context);
                    if (allocation != NULL)
                    {
                        ankus_memory_check_chunk_operation(
                            ankus_memory_context_by_id(allocation->context_id)->context, "pfree");
                        pfree(allocation->pointer);
                        allocation->pointer = NULL;
                        allocation->context_id = 0;
                        ankus_memory_free_allocation(allocation);
                    }

                    break;
                }
                case ANKUS_MEMORY_CAPTURE_GENERATION:
                {
                    AnkusMemoryContext *entry = ankus_memory_context_from_request(request);
                    if (entry->generation == 0)
                    {
                        ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED),
                            errmsg("the memory context's borrow generation space is exhausted")));
                    }

                    result->value = (intptr_t) entry->generation;
                    break;
                }
                case ANKUS_MEMORY_READ_REFERENCE:
                case ANKUS_MEMORY_WRITE_REFERENCE:
                    ankus_memory_access_reference(request, result);
                    break;
                case ANKUS_MEMORY_READ:
                case ANKUS_MEMORY_WRITE:
                case ANKUS_MEMORY_CLEAR:
                    ankus_memory_access_allocation(request, result);
                    break;
                case ANKUS_MEMORY_OWNER:
                {
                    AnkusMemoryAllocation *allocation = ankus_memory_allocation_required((uint64) request->context);

                    /* Allocation and adoption established this owner while it was live.
                     * The registry validates its lifetime without reading a chunk header. */
                    result->context = (intptr_t) allocation->context_id;
                    break;
                }
                case ANKUS_MEMORY_EMPTY:
                {
                    AnkusMemoryContext *entry = ankus_memory_context_from_request(request);
                    result->value = MemoryContextIsEmpty(entry->context) ? 1 : 0;
                    break;
                }
                case ANKUS_MEMORY_STATISTICS:
                {
                    AnkusMemoryContext *entry = ankus_memory_context_from_request(request);
                    result->length = MemoryContextMemAllocated(entry->context, true);
                    break;
                }
                case ANKUS_MEMORY_REGISTER_CALLBACK:
                {
                    AnkusMemoryContext *entry = ankus_memory_context_from_request(request);
                    AnkusMemoryCallback *record = calloc(1, sizeof(*record));
                    if (record == NULL)
                    {
                        ereport(ERROR, (errcode(ERRCODE_OUT_OF_MEMORY), errmsg("out of memory registering a memory callback")));
                    }

                    record->owner = entry->context;
                    /* This ancestor remains alive until this callback returns, even when
                     * AtSubCleanup_Memory has already changed CurTransactionContext to its parent. */
                    record->cleanup_context = ankus_memory_contains(CurTransactionContext, entry->context)
                        ? CurTransactionContext : NULL;
                    record->context_id = entry->id;
                    record->handle = request->other;
                    record->invoke = (AnkusMemoryCallbackFunction) request->pointer;
                    record->callback.func = ankus_memory_callback;
                    record->callback.arg = record;
                    record->next = ankus_memory_callbacks;
                    ankus_memory_callbacks = record;
                    MemoryContextRegisterResetCallback(entry->context, &record->callback);
                    break;
                }
                case ANKUS_MEMORY_CANCEL_CALLBACK:
                {
                    for (AnkusMemoryCallback *record = ankus_memory_callbacks; record != NULL; record = record->next)
                    {
                        if (record->context_id == (uint64) request->context && record->handle == request->other)
                        {
                            record->handle = 0;
                            break;
                        }
                    }

                    break;
                }
                default:
                    ereport(ERROR, (errmsg("unknown Ankus memory operation")));
            }
        }

        static const char *
        ankus_memory_check_context_operation(AnkusMemoryApi *api, AnkusMemoryRequest *request,
            AnkusMemoryContext *entry)
        {
            AnkusMemoryOperation operation = (AnkusMemoryOperation) request->operation;
            if (operation == ANKUS_MEMORY_SWITCH)
            {
                for (AnkusMemoryProtection *scope = ankus_memory_protection; scope != NULL; scope = scope->previous)
                {
                    if (scope->teardown && ankus_memory_contains(scope->owner, entry->context))
                    {
                        return "a context undergoing native cleanup cannot become current";
                    }
                }

                return NULL;
            }

            if (operation != ANKUS_MEMORY_RESET && operation != ANKUS_MEMORY_RESET_ONLY &&
                operation != ANKUS_MEMORY_RESET_CHILDREN && operation != ANKUS_MEMORY_DELETE)
            {
                return NULL;
            }

            const char *message = operation == ANKUS_MEMORY_DELETE ? NULL :
                ankus_memory_check_infrastructure(entry->context);
            if (message == NULL)
            {
                message = ankus_memory_check_reserved(entry->context, operation);
            }

            if (message == NULL)
            {
                message = ankus_memory_check_protection(entry->context, false);
            }

            if (message == NULL && operation == ANKUS_MEMORY_DELETE)
            {
                message = ankus_memory_check_delete(entry);
            }

            if (message != NULL)
            {
                return message;
            }

            for (MemoryContext active = api->current; active != NULL; active = MemoryContextGetParent(active))
            {
                if (active == entry->context && (operation != ANKUS_MEMORY_RESET_CHILDREN || active != api->current))
                {
                    if (operation == ANKUS_MEMORY_DELETE)
                    {
                        return "an active native callback memory context or its ancestor cannot be deleted";
                    }

                    if (operation == ANKUS_MEMORY_RESET_CHILDREN)
                    {
                        return "children containing an active native callback memory context cannot be reset";
                    }

                    return "an active native callback memory context or its ancestor cannot be reset";
                }
            }

            return operation == ANKUS_MEMORY_RESET && CurrentMemoryContext != entry->context ?
                ankus_memory_check_delete(entry) : NULL;
        }

        static bool
        ankus_memory_validate_request(AnkusMemoryApi *api, AnkusMemoryRequest *request, AnkusError *error)
        {
            /* Expired managed handles are ordinary registry validation failures.
             * Do not raise PostgreSQL ERROR to implement IsAlive or checked access:
             * there is no backend error state or resource acquisition to recover. */
            const char *message = NULL;
            switch ((AnkusMemoryOperation) request->operation)
            {
                case ANKUS_MEMORY_WORKER:
                    message = ankus_worker_check_phase(request);
                    break;
                case ANKUS_MEMORY_CREATE:
                {
                    AnkusMemoryContext *entry = ankus_memory_context_by_id((uint64) request->context);
                    if (request->context != 0 && entry == NULL)
                    {
                        message = "the PostgreSQL memory context handle is stale or belongs to another backend";
                    }
                    else
                    {
                        message = ankus_memory_check_protection(entry == NULL ? CurrentMemoryContext : entry->context, true);
                    }

                    break;
                }
                case ANKUS_MEMORY_DELETE:
                {
                    AnkusMemoryContext *entry = ankus_memory_context_by_id((uint64) request->context);
                    if (entry != NULL)
                    {
                        message = ankus_memory_check_context_operation(api, request, entry);
                    }

                    break;
                }
                /* Operations on a context need it live; a borrowed reference also needs the generation it captured. */
                case ANKUS_MEMORY_PARENT:
                case ANKUS_MEMORY_NAME:
                case ANKUS_MEMORY_RESET:
                case ANKUS_MEMORY_RESET_ONLY:
                case ANKUS_MEMORY_RESET_CHILDREN:
                case ANKUS_MEMORY_SWITCH:
                case ANKUS_MEMORY_ALLOCATE:
                case ANKUS_MEMORY_ALLOCATE_VARLENA:
                case ANKUS_MEMORY_ADOPT:
                case ANKUS_MEMORY_EMPTY:
                case ANKUS_MEMORY_STATISTICS:
                case ANKUS_MEMORY_REGISTER_CALLBACK:
                case ANKUS_MEMORY_CAPTURE_GENERATION:
                case ANKUS_MEMORY_READ_REFERENCE:
                case ANKUS_MEMORY_WRITE_REFERENCE:
                {
                    AnkusMemoryContext *entry = ankus_memory_context_by_id((uint64) request->context);
                    if (entry == NULL)
                    {
                        message = "the PostgreSQL memory context handle is stale or belongs to another backend";
                    }
                    else if ((request->operation == ANKUS_MEMORY_READ_REFERENCE || request->operation == ANKUS_MEMORY_WRITE_REFERENCE) &&
                        (entry->generation == 0 || entry->generation != (uintptr_t) request->other))
                    {
                        message = "the borrowed PostgreSQL memory reference is stale";
                    }
                    else
                    {
                        message = ankus_memory_check_context_operation(api, request, entry);
                    }

                    break;
                }
                /* Operations on an allocation need it, and the context that owns it, live. */
                case ANKUS_MEMORY_REALLOCATE:
                case ANKUS_MEMORY_DETACH:
                case ANKUS_MEMORY_READ:
                case ANKUS_MEMORY_WRITE:
                case ANKUS_MEMORY_CLEAR:
                case ANKUS_MEMORY_OWNER:
                    if (ankus_memory_allocation_by_id((uint64) request->context) == NULL)
                    {
                        message = "the PostgreSQL allocation handle is stale";
                    }

                    break;
                case ANKUS_MEMORY_STRINGINFO:
                {
                    if (request->flags == ANKUS_STRINGINFO_CREATE || request->pointer != 0)
                    {
                        AnkusMemoryContext *entry = ankus_memory_context_by_id((uint64) request->context);
                        if (entry == NULL || (request->flags != ANKUS_STRINGINFO_CREATE &&
                            (entry->generation == 0 || entry->generation != (uintptr_t) request->other)))
                        {
                            message = "the borrowed PostgreSQL StringInfo is stale";
                        }
                    }
                    else if (request->flags != ANKUS_STRINGINFO_DISPOSE &&
                        ankus_stringinfo_find((uint64) request->context) == NULL)
                    {
                        message = "the PostgreSQL StringInfo has been reclaimed";
                    }

                    break;
                }
                case ANKUS_MEMORY_LIST:
                {
                    if (request->flags == ANKUS_LIST_CREATE || request->flags == ANKUS_LIST_BORROW ||
                        request->flags == ANKUS_LIST_PARSE_DEFAULTS)
                    {
                        if (ankus_memory_context_by_id((uint64) request->context) == NULL)
                        {
                            message = "the PostgreSQL memory context handle is stale or belongs to another backend";
                        }
                    }
                    else if (request->flags != ANKUS_LIST_DISPOSE && ankus_list_find((uint64) request->context) == NULL)
                    {
                        message = "the PostgreSQL list has been reclaimed";
                    }

                    break;
                }
                case ANKUS_MEMORY_ITEM_POINTER:
                case ANKUS_MEMORY_FORMAT_NODE:
                {
                    bool creating = request->operation == ANKUS_MEMORY_ITEM_POINTER && request->flags == 1;
                    if (creating || request->pointer != 0)
                    {
                        AnkusMemoryContext *entry = ankus_memory_context_by_id((uint64) request->context);
                        if (entry == NULL || (!creating &&
                            (entry->generation == 0 || entry->generation != (uintptr_t) request->other)))
                        {
                            message = request->operation == ANKUS_MEMORY_ITEM_POINTER ?
                                "the item-pointer lifetime anchor is stale" : "the node lifetime anchor is stale";
                        }
                    }
                    else if (ankus_memory_allocation_by_id((uint64) request->context) == NULL)
                    {
                        message = request->operation == ANKUS_MEMORY_ITEM_POINTER ?
                            "the item-pointer allocation is stale" : "the node allocation is stale";
                    }

                    break;
                }
                default:
                    break;
            }

            if (message == NULL)
            {
                return true;
            }

            error->sqlstate = ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE;
            strlcpy(error->message, message, sizeof(error->message));
            return false;
        }

        static int
        ankus_memory_execute_guarded(AnkusMemoryApi *api, AnkusMemoryRequest *request,
            AnkusMemoryResult *result, AnkusError *error, volatile bool *recovered)
        {
            MemoryContext caller = CurrentMemoryContext;
            MemoryContext recovery = ankus_memory_contains(ErrorContext, caller) ? ErrorContext : caller;
            uint32 interrupt_holdoff = InterruptHoldoffCount;
            uint32 shared_held_before = ankus_shared_held_count;
            uint32 cancel_holdoff = QueryCancelHoldoffCount;
            volatile int status = 0;
            AnkusPendingError *volatile pending = ankus_pending_error_save();
            PG_TRY();
            {
                ankus_memory_execute(api, request, result, recovered);
            }
            PG_CATCH();
            {
                MemoryContext diagnostic = NULL;
                MemoryContextSwitchTo(caller);
                ANKUS_PG_TRY_INNER();
                {
                    diagnostic = AllocSetContextCreate(TopMemoryContext, "Ankus memory diagnostics", ALLOCSET_SMALL_SIZES);
                    MemoryContextSwitchTo(diagnostic);
                    ErrorData *data = ankus_copy_error_data();
                    FlushErrorState();
                    /* ankus_memory_call leaves the error untouched until something fails. */
                    ankus_error_clear(error);
                    ankus_capture_error(data, error);
                    if (ankus_worker_active &&
                        ((request->operation == ANKUS_MEMORY_WORKER && request->flags == 10) ||
                            request->operation == ANKUS_MEMORY_CHECK_INTERRUPTS) &&
                        data->elevel == ERROR && data->sqlerrcode == ERRCODE_QUERY_CANCELED && !IsTransactionState())
                    {
                        /* An idle worker wait or explicit interrupt check owns
                         * no transaction resources. The native operation has
                         * ended, so cancellation needs no transaction abort. */
                        *recovered = true;
                    }

                    ankus_recovery_record(error, *recovered);
                    ankus_free_error_data(data);
                    MemoryContextSwitchTo(recovery);
                    MemoryContextDelete(diagnostic);
                    status = 1;
                }
                ANKUS_PG_CATCH_INNER();
                {
                    MemoryContextSwitchTo(recovery);
                    FlushErrorState();
                    ereport(FATAL, (errmsg("Unable to recover PostgreSQL state after a guarded memory failure")));
                }
                ANKUS_PG_END_TRY_INNER();
            }
            PG_END_TRY();
            if (status != 0)
                ankus_pending_error_restore(pending);
            else
                ankus_pending_error_release(pending);
            if (status != 0 || (request->operation != ANKUS_MEMORY_NATIVE_CALL && request->operation != ANKUS_MEMORY_SHARED &&
                request->operation != ANKUS_MEMORY_SPIN))
            {
                InterruptHoldoffCount = ankus_shared_restore_interrupts(interrupt_holdoff, shared_held_before);
                QueryCancelHoldoffCount = cancel_holdoff;
            }

            return status;
        }

        static int
        ankus_memory_invoke(AnkusMemoryApi *api, AnkusMemoryRequest *request,
            AnkusMemoryResult *result, AnkusError *error)
        {
            /* Readers treat the message as a C string, so clearing every byte of it on each call is not needed. */
            ankus_error_clear(error);
            if (request->operation == ANKUS_MEMORY_RECORD_TERMINAL)
            {
                /* This only copies diagnostics into the callback's native frame.
                 * It must remain available under held locks and during teardown. */
                return ankus_recovery_terminal(request->flags, (AnkusError *) request->pointer, error);
            }

            if (request->operation == ANKUS_MEMORY_WORKER && request->flags == 6)
            {
                ankus_worker_release(request);
                return 0;
            }

            if (request->operation == ANKUS_MEMORY_ERROR_CONTEXT && request->flags == 3)
            {
                ankus_error_context_pop((AnkusErrorContextEntry *) request->context);
                return 0;
            }

            if (request->operation == ANKUS_MEMORY_SHARED && request->flags == 5)
            {
                /* Lease release never allocates or raises ERROR. It remains available
                 * during callback cleanup and preserves the balanced interrupt count. */
                ankus_shared_release(request);
                return 0;
            }

            if (ankus_memory_error_cleanup)
            {
                /* FlushErrorState would recursively reclaim this callback's owner and
                 * consume an outer error reporter's private error stack. Do not ereport. */
                error->sqlstate = ERRCODE_OBJECT_IN_USE;
                strlcpy(error->message, "Guarded memory operations are unavailable during ErrorContext cleanup", sizeof(error->message));
                return 1;
            }

            /* Managed finally blocks may restore their context and release owned
             * memory. Keep these checked operations guarded, including callbacks
             * they invoke, while refusing new allocations and raw backend work. */
            bool cleanup = request->operation == ANKUS_MEMORY_SWITCH || request->operation == ANKUS_MEMORY_FREE ||
                request->operation == ANKUS_MEMORY_DELETE || request->operation == ANKUS_MEMORY_CANCEL_CALLBACK ||
                (request->operation == ANKUS_MEMORY_REPLICATION && request->flags == 6);
            if (!cleanup && ankus_recovery_failed(error))
                return 1;

            if (!ankus_memory_validate_request(api, request, error))
            {
                return 1;
            }

            if (request->operation == ANKUS_MEMORY_NATIVE_BINDING)
            {
                return ankus_memory_native_binding(request, error);
            }

            if (request->operation == ANKUS_MEMORY_SHARED &&
                (request->flags == 3 || request->flags == 4 || request->flags == 6 || request->flags == 7))
            {
                return ankus_shared_value(request, result, error);
            }

            volatile bool recovered = false;
            AnkusRecoveryFrame transaction;
            bool worker_transaction = request->operation == ANKUS_MEMORY_WORKER && request->flags == 13;
            bool worker_idle_interrupt = ankus_worker_active && !IsTransactionState() &&
                ((request->operation == ANKUS_MEMORY_WORKER && request->flags == 10) ||
                    request->operation == ANKUS_MEMORY_CHECK_INTERRUPTS);
            bool worker_boundary = worker_transaction || worker_idle_interrupt;
            if (worker_boundary)
            {
                /* Transactions own a complete commit/abort boundary. An idle
                 * worker owns only its wait/interrupt boundary. Keep failures
                 * here until that operation ends instead of the worker lifetime.
                 * As in ANKUS_MANAGED_INVOKE, the failure is written before it is
                 * read, so only the link and flag are set. */
                transaction.previous = ankus_recovery_frame;
                transaction.failed = false;
                ankus_recovery_frame = &transaction;
            }

            /* The frame lives outside the function containing sigsetjmp, so its
             * modified fields remain defined after a PostgreSQL longjmp. Managed error
             * contexts apply to everything the operation does. */
            ErrorContextCallback managed_context;
            bool context_entered = ankus_error_context_enter(&managed_context);
            int status = ankus_memory_execute_guarded(api, request, result, error, &recovered);
            ankus_error_context_exit(&managed_context, context_entered);
            if (worker_boundary)
            {
                /* Failure without proven native recovery still poisons the
                 * lifetime frame. Terminal reports always propagate in finish. */
                if (transaction.failed && !recovered)
                {
                    ankus_recovery_store(transaction.previous, &transaction.failure);
                }

                status = ankus_recovery_finish(&transaction, error, status);
            }

            return status;
        }

        /* Runs one generated raw call body under PG_TRY, as ankus_memory_execute_guarded runs
         * ANKUS_MEMORY_NATIVE_CALL, but calls the body directly. Only a failure needs the recovery context, so the walk
         * that finds it waits for one. */
        static int
        ankus_memory_call_guarded(AnkusNativeCallBody body, const AnkusNativeCallFrame *frame, intptr_t *value, AnkusError *error)
        {
            MemoryContext caller = CurrentMemoryContext;
            uint32 interrupt_holdoff = InterruptHoldoffCount;
            uint32 shared_held_before = ankus_shared_held_count;
            uint32 cancel_holdoff = QueryCancelHoldoffCount;
            volatile int status = 0;
            volatile intptr_t outcome = 0;
            AnkusPendingError *volatile pending = ankus_pending_error_save();
            PG_TRY();
            {
                if (body == NULL || frame == NULL)
                {
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                        errmsg("the generated native call body or frame is missing")));
                }

                outcome = body(frame->arguments, frame->count, frame->result, frame->result_size);
            }
            PG_CATCH();
            {
                /* Volatile because the inner guard below can longjmp back into this block. */
                volatile MemoryContext recovery = ankus_memory_contains(ErrorContext, caller) ? ErrorContext : caller;
                MemoryContext diagnostic = NULL;
                MemoryContextSwitchTo(caller);
                ANKUS_PG_TRY_INNER();
                {
                    diagnostic = AllocSetContextCreate(TopMemoryContext, "Ankus memory diagnostics", ALLOCSET_SMALL_SIZES);
                    MemoryContextSwitchTo(diagnostic);
                    ErrorData *data = ankus_copy_error_data();
                    FlushErrorState();
                    /* ankus_memory_call leaves the error untouched until something fails. */
                    ankus_error_clear(error);
                    ankus_capture_error(data, error);
                    ankus_recovery_record(error, false);
                    ankus_free_error_data(data);
                    MemoryContextSwitchTo(recovery);
                    MemoryContextDelete(diagnostic);
                    status = 1;
                }
                ANKUS_PG_CATCH_INNER();
                {
                    MemoryContextSwitchTo(recovery);
                    FlushErrorState();
                    ereport(FATAL, (errmsg("Unable to recover PostgreSQL state after a guarded memory failure")));
                }
                ANKUS_PG_END_TRY_INNER();
            }
            PG_END_TRY();
            if (status != 0)
            {
                ankus_pending_error_restore(pending);
                InterruptHoldoffCount = ankus_shared_restore_interrupts(interrupt_holdoff, shared_held_before);
                QueryCancelHoldoffCount = cancel_holdoff;
            }
            else
                ankus_pending_error_release(pending);

            *value = outcome;
            return status;
        }

        /* Runs a generated raw call with the admission checks and managed error contexts of ankus_memory_invoke but no
         * request envelope. Every failure writes the whole error, so a call that succeeds never clears its 2 KB buffer. */
        static int
        ankus_memory_call(AnkusMemoryApi *api, intptr_t body, intptr_t frame, intptr_t *value, AnkusError *error)
        {
            (void) api;
            if (ankus_memory_error_cleanup)
            {
                ankus_error_clear(error);
                error->sqlstate = ERRCODE_OBJECT_IN_USE;
                strlcpy(error->message, "Guarded memory operations are unavailable during ErrorContext cleanup", sizeof(error->message));
                return 1;
            }

            /* This copies the whole retained failure into the error. */
            if (ankus_recovery_failed(error))
                return 1;

            ErrorContextCallback managed_context;
            bool context_entered = ankus_error_context_enter(&managed_context);
            int status = ankus_memory_call_guarded((AnkusNativeCallBody) body, (const AnkusNativeCallFrame *) frame, value, error);
            ankus_error_context_exit(&managed_context, context_entered);
            return status;
        }

        """;
}
