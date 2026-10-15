namespace Ankus.Generators;

/// <summary>
/// Emits selected-header shared-memory registration, attachment and checked lightweight-lock leases.
/// </summary>
internal static class NativeSharedMemoryBridge
{
    /// <summary>
    /// Gets the native storage protocol and operations called inside the memory error boundary.
    /// </summary>
    internal const string Source = """
        #include "storage/ipc.h"
        #include "storage/shmem.h"
        #include "storage/lwlock.h"
        #include "storage/proc.h"
        #include "port/atomics.h"

        /* PostgreSQL 19 keeps the unchanged 48-byte index key private. */
        #if PG_VERSION_NUM >= 190000
        #define ANKUS_SHARED_NAME_LENGTH 48
        #else
        #define ANKUS_SHARED_NAME_LENGTH SHMEM_INDEX_KEYSIZE
        #endif

        typedef int (*AnkusSharedManagedInitializer)(intptr_t, void *, size_t, void *);
        typedef void (*AnkusSharedRunInitializer)(AnkusSharedManagedInitializer, intptr_t, void *, size_t);
        static AnkusSharedRunInitializer ankus_shared_initialize;

        typedef struct AnkusSharedDefinition
        {
            const char *name;
            const unsigned char *identity;
            size_t size;
            uint32 kind;
            intptr_t cookie;
            AnkusSharedManagedInitializer initialize;
        } AnkusSharedDefinition;

        typedef struct AnkusSharedHeader
        {
            uint64 magic;
            size_t size;
            uint32 kind;
            unsigned char identity[32];
            LWLock *lock;
            bool initialized;
        } AnkusSharedHeader;

        typedef struct AnkusSharedAccess
        {
            pg_atomic_uint64 address;
            pg_atomic_uint32 readers;
            pg_atomic_uint32 process_id;
            /* Admissions held on the backend thread inside a native callback scope.
             * Only that thread reads or writes this count. */
            uint32 backend_readers;
            uint32 reserved;
        } AnkusSharedAccess;

        StaticAssertDecl(sizeof(AnkusSharedAccess) == 24, "Ankus atomic access requires native 64-bit atomics");
        StaticAssertDecl(offsetof(AnkusSharedAccess, address.value) == 0, "Ankus atomic address layout changed");
        StaticAssertDecl(offsetof(AnkusSharedAccess, readers.value) == 8, "Ankus atomic reader layout changed");
        StaticAssertDecl(offsetof(AnkusSharedAccess, process_id.value) == 12, "Ankus atomic process layout changed");
        StaticAssertDecl(offsetof(AnkusSharedAccess, backend_readers) == 16, "Ankus backend reader layout changed");

        typedef struct AnkusSharedStorage
        {
            uint64 id;
            char name[ANKUS_SHARED_NAME_LENGTH];
            unsigned char identity[32];
            size_t size;
            uint32 kind;
            intptr_t cookie;
            AnkusSharedManagedInitializer initialize;
            AnkusSharedHeader *header;
            LWLock *lock;
            uint64 lease;
            bool exclusive;
            AnkusSharedAccess access;
            struct AnkusSharedStorage *next;
        } AnkusSharedStorage;

        static AnkusSharedStorage *ankus_shared_storage;
        static AnkusSharedStorage *ankus_shared_last_storage;
        static uint64 ankus_shared_next_storage = 1;
        static uint64 ankus_shared_next_lease = 1;
        static uint32 ankus_shared_held_count;
        static bool ankus_shared_has_access;
        static int ankus_shared_exit_pid;
        static shmem_startup_hook_type ankus_shared_previous_startup;
        #if PG_VERSION_NUM >= 150000
        static shmem_request_hook_type ankus_shared_previous_request;
        #endif

        static void
        ankus_shared_close(AnkusSharedStorage *entry)
        {
            /* FATAL and proc_exit leave from inside the PostgreSQL call that raised them.
             * Managed reads on this thread that made such a call never resume, so their
             * admissions are never released. Process exit retires the address after
             * every other thread's finite operation; a postmaster restart waits for all. */
            uint32 stranded = proc_exit_inprogress ? entry->access.backend_readers : 0;
            pg_atomic_fetch_or_u32(&entry->access.readers, 0x80000000U);
            pg_atomic_write_u32(&entry->access.process_id, 0);
            while ((pg_atomic_read_u32(&entry->access.readers) & 0x7fffffffU) > stranded)
            {
                pg_usleep(1000L);
            }

            pg_atomic_write_u64(&entry->access.address, 0);
        }

        static void
        ankus_shared_retire(void)
        {
            /* Unix checkpoints have already drained managed service threads.
             * Windows workers can still be active: close admission and wait for
             * each finite operation before PostgreSQL unmaps this process's segment. */
            for (AnkusSharedStorage *entry = ankus_shared_storage; entry != NULL; entry = entry->next)
            {
                if (entry->kind != 0)
                {
                    ankus_shared_close(entry);
                }
            }
        }

        static void
        ankus_shared_before_exit(int code, Datum argument)
        {
            (void) code;
            (void) argument;
            ankus_shared_retire();
            ankus_shared_exit_pid = 0;
        }

        static void
        ankus_shared_prepare(void)
        {
            if (!ankus_shared_has_access || ankus_shared_exit_pid == MyProcPid ||
                (IsUnderPostmaster && MyProc == NULL))
            {
                return;
            }

            /* Unix children clear inherited exit callbacks. Publish their process identity
             * only after the new callback owns address retirement in this process. */
            before_shmem_exit(ankus_shared_before_exit, (Datum) 0);
            ankus_shared_exit_pid = MyProcPid;
            for (AnkusSharedStorage *entry = ankus_shared_storage; entry != NULL; entry = entry->next)
            {
                if (entry->kind != 0)
                {
                    if (pg_atomic_read_u32(&entry->access.process_id) != (uint32) MyProcPid)
                    {
                        /* Fork copies the parent's process-local reader count, but none
                         * of its other threads. Their leases cannot survive in this child.
                         * Keep an unpublished address closed until startup attaches it. */
                        pg_atomic_write_u32(&entry->access.readers,
                            pg_atomic_read_u64(&entry->access.address) == 0 ? 0x80000000U : 0);
                        entry->access.backend_readers = 0;
                    }

                    pg_atomic_write_u32(&entry->access.process_id, (uint32) MyProcPid);
                }
            }
        }

        static void *
        ankus_shared_data(AnkusSharedHeader *header)
        {
            return (char *) header + MAXALIGN(sizeof(AnkusSharedHeader));
        }

        static Size
        ankus_shared_size(AnkusSharedStorage *entry)
        {
            Size value_size = entry->kind == 1 && entry->size < sizeof(uint64) ? sizeof(uint64) : entry->size;
            return add_size(MAXALIGN(sizeof(AnkusSharedHeader)), value_size);
        }

        static void
        ankus_shared_request_one(AnkusSharedStorage *entry)
        {
            RequestAddinShmemSpace(ankus_shared_size(entry));
            if (entry->kind == 0)
            {
                RequestNamedLWLockTranche(entry->name, 1);
            }
        }

        #if PG_VERSION_NUM >= 150000
        static void
        ankus_shared_request(void)
        {
            if (ankus_shared_previous_request != NULL)
            {
                ankus_shared_previous_request();
            }

            for (AnkusSharedStorage *entry = ankus_shared_storage; entry != NULL; entry = entry->next)
            {
                ankus_shared_request_one(entry);
            }
        }
        #endif

        static void
        ankus_shared_acquire_lock(LWLock *lock, LWLockMode mode)
        {
            if (MyProc == NULL)
            {
                if (!LWLockConditionalAcquire(lock, mode))
                {
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                        errmsg("an Ankus shared-memory lock cannot wait before PostgreSQL process initialization")));
                }

                return;
            }

            LWLockAcquire(lock, mode);
        }

        static void
        ankus_shared_attach(AnkusSharedStorage *entry, bool create)
        {
            if (entry->header != NULL)
            {
                return;
            }

            ankus_shared_acquire_lock(AddinShmemInitLock, LW_EXCLUSIVE);
            PG_TRY();
            {
                bool found = false;
                AnkusSharedHeader *header = ShmemInitStruct(entry->name, ankus_shared_size(entry), &found);
                if (!found)
                {
                    if (!create)
                    {
                        ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                            errmsg("Ankus shared memory '%s' was not initialized during shared preload", entry->name)));
                    }

                    memset(header, 0, ankus_shared_size(entry));
                    header->magic = UINT64CONST(0x414e4b5553534802);
                    header->size = entry->size;
                    header->kind = entry->kind;
                    memcpy(header->identity, entry->identity, sizeof(header->identity));
                    if (entry->kind == 0)
                    {
                        header->lock = &GetNamedLWLockTranche(entry->name)->lock;
                    }

                    ankus_shared_initialize(entry->initialize, entry->cookie, ankus_shared_data(header), entry->size);
                    header->initialized = true;
                }

                if (header->magic != UINT64CONST(0x414e4b5553534802) || header->size != entry->size || header->kind != entry->kind ||
                    memcmp(header->identity, entry->identity, sizeof(header->identity)) != 0 || !header->initialized ||
                    (entry->kind == 0 && (header->lock == NULL || !ShmemAddrIsValid(header->lock))) ||
                    (entry->kind != 0 && header->lock != NULL))
                {
                    ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH),
                        errmsg("Ankus shared memory '%s' has a conflicting or incomplete value layout", entry->name)));
                }

                /* PostgreSQL maps shared memory at the same address in each process.
                 * Only this shared pointer is retained; GetNamedLWLockTranche depends
                 * on a postmaster-private request array unavailable in Windows children. */
                entry->lock = header->lock;
        #if PG_VERSION_NUM < 190000
                if (entry->kind == 0)
                {
                    LWLockRegisterTranche(entry->lock->tranche, entry->name);
                }
        #endif
                /* PostgreSQL 19 retains tranche names in shared memory when
                 * initializing the requested locks; no process registration is needed. */

                entry->header = header;
                if (entry->kind != 0)
                {
                    pg_atomic_write_u64(&entry->access.address, (uint64) (uintptr_t) ankus_shared_data(header));
                    pg_atomic_write_u32(&entry->access.process_id, (uint32) MyProcPid);
                    pg_memory_barrier();
                    pg_atomic_write_u32(&entry->access.readers, 0);
                }
            }
            PG_FINALLY();
            {
                /* PostgreSQL resets holdoff counts when reporting an ERROR. Restore only
                 * the hold belonging to this still-held lock before balancing its release. */
                if (InterruptHoldoffCount == 0)
                {
                    HOLD_INTERRUPTS();
                }

                LWLockRelease(AddinShmemInitLock);
            }
            PG_END_TRY();
        }

        static void
        ankus_shared_startup(void)
        {
            /* A postmaster can recreate shared memory after a backend crash while
             * retaining its loaded modules and managed descriptors. Retire every
             * address before invoking hooks for the replacement shared segment. */
            ankus_shared_held_count = 0;
            if (ankus_shared_has_access)
            {
                ankus_shared_retire();
            }

            for (AnkusSharedStorage *entry = ankus_shared_storage; entry != NULL; entry = entry->next)
            {
                entry->header = NULL;
                entry->lock = NULL;
                entry->lease = 0;
            }

            ankus_shared_prepare();
            if (ankus_shared_previous_startup != NULL)
            {
                ankus_shared_previous_startup();
            }

            for (AnkusSharedStorage *entry = ankus_shared_storage; entry != NULL; entry = entry->next)
            {
                ankus_shared_attach(entry, !IsUnderPostmaster);
            }
        }

        static AnkusSharedStorage *
        ankus_shared_find(uint64 id)
        {
            for (AnkusSharedStorage *entry = ankus_shared_storage; entry != NULL; entry = entry->next)
            {
                if (entry->id == id)
                {
                    return entry;
                }
            }

            return NULL;
        }

        static void
        ankus_shared_register(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            if (!process_shared_preload_libraries_in_progress || ankus_shared_initialize == NULL)
            {
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("register Ankus shared memory from PgModuleLoad during shared_preload_libraries")));
            }

            AnkusSharedDefinition *definition = (AnkusSharedDefinition *) request->data;
            if (definition == NULL || definition->name == NULL || definition->name[0] == '\0' ||
                strlen(definition->name) >= ANKUS_SHARED_NAME_LENGTH || definition->identity == NULL ||
                definition->size == 0 || definition->kind > 2 ||
                (definition->kind == 1 && definition->size != 1 && definition->size != 2 && definition->size != 4 && definition->size != 8) ||
                definition->cookie == 0 || definition->initialize == NULL)
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                    errmsg("invalid Ankus shared-memory name, value size or initializer")));
            }

            for (AnkusSharedStorage *current = ankus_shared_storage; current != NULL; current = current->next)
            {
                if (strcmp(current->name, definition->name) == 0)
                {
                    ereport(ERROR, (errcode(ERRCODE_DUPLICATE_OBJECT),
                        errmsg("Ankus shared memory '%s' is already registered", definition->name)));
                }
            }

            if (ankus_shared_next_storage == 0)
            {
                ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("Ankus shared-memory identity space is exhausted")));
            }

            AnkusSharedStorage *entry = MemoryContextAllocZero(TopMemoryContext, sizeof(AnkusSharedStorage));
            entry->id = ankus_shared_next_storage++;
            strlcpy(entry->name, definition->name, sizeof(entry->name));
            memcpy(entry->identity, definition->identity, sizeof(entry->identity));
            entry->size = definition->size;
            entry->kind = definition->kind;
            entry->cookie = definition->cookie;
            entry->initialize = definition->initialize;
            pg_atomic_init_u64(&entry->access.address, 0);
            pg_atomic_init_u32(&entry->access.readers, 0x80000000U);
            pg_atomic_init_u32(&entry->access.process_id, 0);
            entry->access.backend_readers = 0;
            PG_TRY();
            {
                (void) ankus_shared_size(entry);
                if (!IsUnderPostmaster)
                {
        #if PG_VERSION_NUM < 150000
                    ankus_shared_request_one(entry);
        #endif
                    if (ankus_shared_storage == NULL)
                    {
        #if PG_VERSION_NUM >= 150000
                        ankus_shared_previous_request = shmem_request_hook;
                        shmem_request_hook = ankus_shared_request;
        #endif
                    }
                }

                if (ankus_shared_storage == NULL)
                {
                    ankus_shared_previous_startup = shmem_startup_hook;
                    shmem_startup_hook = ankus_shared_startup;
                }
            }
            PG_CATCH();
            {
                pfree(entry);
                PG_RE_THROW();
            }
            PG_END_TRY();
            if (ankus_shared_last_storage == NULL)
            {
                ankus_shared_storage = entry;
            }
            else
            {
                ankus_shared_last_storage->next = entry;
            }

            ankus_shared_last_storage = entry;
            ankus_shared_has_access |= entry->kind != 0;
            result->value = (intptr_t) entry->id;
            if (entry->kind != 0)
            {
                result->data = (intptr_t) &entry->access;
                result->length = sizeof(entry->access);
            }
        }

        static void
        ankus_shared_release(AnkusMemoryRequest *request)
        {
            AnkusSharedStorage *entry = ankus_shared_find((uint64) request->context);
            if (entry == NULL || entry->lease == 0 || entry->lease != (uint64) request->other)
            {
                return;
            }

            entry->lease = 0;
            ankus_shared_held_count--;
            if (LWLockHeldByMe(entry->lock))
            {
                if (InterruptHoldoffCount == 0)
                {
                    HOLD_INTERRUPTS();
                }

                LWLockRelease(entry->lock);
            }
        }

        static uint32
        ankus_shared_restore_interrupts(uint32 before, uint32 held_before)
        {
            /* Subtransaction abort releases all LWLocks, including locks acquired by
             * the managed caller. Nested native guards must not restore their old holds. */
            for (AnkusSharedStorage *entry = ankus_shared_storage; entry != NULL; entry = entry->next)
            {
                if (entry->lease != 0 && !LWLockHeldByMe(entry->lock))
                {
                    entry->lease = 0;
                    ankus_shared_held_count--;
                }
            }

            uint64 adjusted = (uint64) before + ankus_shared_held_count;
            return adjusted < held_before ? 0 : (uint32) (adjusted - held_before);
        }

        static int
        ankus_shared_value(AnkusMemoryRequest *request, AnkusMemoryResult *result, AnkusError *error)
        {
            /* This validation must not allocate, ereport, process interrupts or enter
             * callbacks: another original shared reference can already be borrowed. */
            AnkusSharedStorage *entry = ankus_shared_find((uint64) request->context);
            if (entry == NULL || entry->kind != 0 || entry->header == NULL || entry->lock == NULL ||
                entry->lease == 0 || entry->lease != (uint64) request->other || !LWLockHeldByMe(entry->lock))
            {
                error->sqlstate = ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE;
                strlcpy(error->message, "the Ankus shared-memory lock guard is no longer held", sizeof(error->message));
                return 1;
            }

            if (request->length != entry->size ||
                ((request->flags == 3 || request->flags == 4) && request->data == 0))
            {
                error->sqlstate = ERRCODE_DATATYPE_MISMATCH;
                strlcpy(error->message, "the Ankus shared-memory value size does not match its registration", sizeof(error->message));
                return 1;
            }

            if ((request->flags == 4 || request->flags == 7) && !entry->exclusive)
            {
                error->sqlstate = ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE;
                strlcpy(error->message, "the Ankus shared-memory operation requires an exclusive lock guard", sizeof(error->message));
                return 1;
            }

            result->data = (intptr_t) ankus_shared_data(entry->header);
            result->length = entry->size;
            if (request->flags == 3)
            {
                memcpy((void *) request->data, (void *) result->data, entry->size);
            }
            else if (request->flags == 4)
            {
                memcpy((void *) result->data, (void *) request->data, entry->size);
            }

            return 0;
        }

        static void
        ankus_memory_shared(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            if (request->flags == 0)
            {
                ankus_shared_register(request, result);
                return;
            }

            AnkusSharedStorage *entry = ankus_shared_find((uint64) request->context);
            if (entry == NULL || (entry->header == NULL && !IsUnderPostmaster))
            {
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("Ankus shared-memory locks require a registered value whose shared-memory startup has completed")));
            }

            ankus_shared_attach(entry, false);
            if (entry->kind != 0)
            {
                ereport(ERROR, (errcode(ERRCODE_DATATYPE_MISMATCH),
                    errmsg("Ankus atomic storage does not use lightweight-lock operations")));
            }

            if (request->flags == 1 || request->flags == 2)
            {
                if (LWLockHeldByMe(entry->lock))
                {
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_IN_USE),
                        errmsg("recursive acquisition of Ankus shared-memory lock '%s' is not supported", entry->name)));
                }

                if (ankus_shared_next_lease == 0)
                {
                    ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("Ankus lock lease identity space is exhausted")));
                }

                if (entry->lease != 0)
                {
                    entry->lease = 0;
                    ankus_shared_held_count--;
                }

                uint64 lease = ankus_shared_next_lease++;
                bool exclusive = request->flags == 2;
                ankus_shared_acquire_lock(entry->lock, exclusive ? LW_EXCLUSIVE : LW_SHARED);
                entry->exclusive = exclusive;
                entry->lease = lease;
                ankus_shared_held_count++;
                result->value = (intptr_t) lease;
                return;
            }

            ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("unknown Ankus shared-memory lock operation")));
        }

        """;

    /// <summary>
    /// Gets the startup managed-entry boundary, bound only by extensions with a managed initialization phase.
    /// </summary>
    internal const string Initialization = """
        typedef struct AnkusSharedInitializationContext
        {
            AnkusError *error;
            AnkusExecute execute;
            AnkusMemoryApi *memory;
            AnkusGucReadBinding read;
            AnkusInitializationLog log;
        } AnkusSharedInitializationContext;

        static void
        ankus_shared_run_initializer(AnkusSharedManagedInitializer initialize, intptr_t cookie, void *destination, size_t size)
        {
            MemoryContext caller = CurrentMemoryContext;
            AnkusMemoryApi memory = {0};
            ankus_memory_initialize(&memory);
            AnkusError *error = MemoryContextAllocZero(caller, sizeof(AnkusError));
            AnkusSharedInitializationContext context = { error, NULL, &memory, ankus_read_guc, ankus_initialization_log };
            volatile bool entered = false;
            PG_TRY();
            {
                ankus_fork_host_enter();
                entered = true;
                int status;
                ANKUS_MANAGED_INVOKE(status, error, initialize(cookie, destination, size, &context));
                if (status != 0)
                {
                    ankus_raise_error(error);
                }
            }
            PG_FINALLY();
            {
                ANKUS_FINALLY_RESTORE(caller);
                ankus_release_error(error);
                pfree(error);
                if (entered)
                {
                    ankus_fork_host_exit();
                }
            }
            PG_END_TRY();
        }
        """;
}
