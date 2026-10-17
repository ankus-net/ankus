namespace Ankus.Generators;

/// <summary>
/// Implements selected-header worker registration, observation, native signals and guarded transactions.
/// </summary>
internal static class NativeBackgroundWorkerBridge
{
    /// <summary>
    /// Gets native worker operations included beneath the memory provider's PostgreSQL error boundary.
    /// </summary>
    internal const string Source = """
        #include <errno.h>
        #include "access/xact.h"
        #include "libpq/pqsignal.h"
        #include "postmaster/bgworker.h"
        #include "postmaster/interrupt.h"
        #include "storage/ipc.h"
        #include "storage/latch.h"
        #include "storage/pmsignal.h"
        #include "utils/snapmgr.h"
        #include "utils/guc.h"
        #include "pgstat.h"
        #if PG_VERSION_NUM >= 140000
        #include "utils/wait_event.h"
        #endif

        struct AnkusRequest;
        struct AnkusResult;
        typedef int (*AnkusWorkerExecute)(struct AnkusRequest *, struct AnkusResult *, AnkusError *);

        typedef struct AnkusWorkerDefinition
        {
            const char *name;
            const char *type;
            const char *library;
            const char *entry;
            const char *extra;
            uintptr_t argument;
            int32 start_time;
            int32 restart_seconds;
            int32 database_access;
            int32 notify_pid;
        } AnkusWorkerDefinition;

        typedef struct AnkusWorkerHandle
        {
            struct AnkusWorkerHandle *next;
            uint64 id;
            MemoryContext owner;
            MemoryContextCallback cleanup;
            BackgroundWorkerHandle *handle;
            int32 notify_pid;
        } AnkusWorkerHandle;

        static AnkusWorkerHandle *ankus_worker_handles;
        static uint64 ankus_worker_next_id;
        static bool ankus_worker_active;
        static bool ankus_worker_connected;
        static AnkusWorkerExecute ankus_worker_execute;
        static volatile sig_atomic_t ankus_worker_hup;
        static volatile sig_atomic_t ankus_worker_term;
        static volatile sig_atomic_t ankus_worker_int;
        static volatile sig_atomic_t ankus_worker_child;
        static volatile sig_atomic_t ankus_worker_transaction_active;

        static const char *
        ankus_worker_check_phase(AnkusMemoryRequest *request)
        {
            if (request->flags == 0 && request->pointer != 0 && request->length == sizeof(AnkusWorkerDefinition))
            {
                const AnkusWorkerDefinition *definition = (const AnkusWorkerDefinition *) request->pointer;
                if (!process_shared_preload_libraries_in_progress || definition->notify_pid != 0)
                {
                    return "static Ankus workers require shared preload and no notification PID";
                }
            }

            if (request->flags == 1 && (!IsUnderPostmaster || process_shared_preload_libraries_in_progress || MyProc == NULL))
            {
                return "dynamic Ankus workers require an initialized backend after shared preload";
            }

            if (request->flags >= 7)
            {
                if (!ankus_worker_active || MyBgworkerEntry == NULL)
                {
                    return "the operation requires an active Ankus background-worker entry";
                }

                if ((request->flags == 11 || request->flags == 12) &&
                    (ankus_worker_connected || !(MyBgworkerEntry->bgw_flags & BGWORKER_BACKEND_DATABASE_CONNECTION)))
                {
                    return "the worker must request database access and connect only once";
                }

                if (request->flags == 13 && (!ankus_worker_connected || ankus_worker_execute == NULL ||
                    request->pointer == 0 || IsTransactionState()))
                {
                    return "a worker transaction requires a connected worker outside an existing transaction";
                }
            }

            return NULL;
        }

        static void
        ankus_worker_require(void)
        {
            if (!ankus_worker_active || MyBgworkerEntry == NULL)
            {
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("the operation requires an active Ankus background-worker entry")));
            }
        }

        static void
        ankus_worker_signal(SIGNAL_ARGS)
        {
            int saved = errno;
        #if PG_VERSION_NUM >= 190000
            (void) pg_siginfo;
        #endif
            switch (postgres_signal_arg)
            {
                case SIGHUP:
                    ankus_worker_hup = 1;
                    ConfigReloadPending = 1;
                    break;
                case SIGTERM:
                    ankus_worker_term = 1;
                    ShutdownRequestPending = 1;
                    /* A running worker transaction ends as PostgreSQL's die() ends a backend
                     * statement, so pg_terminate_backend and fast shutdown can interrupt it.
                     * Between transactions, termination remains an observation for Wait. */
                    if (ankus_worker_transaction_active && !proc_exit_inprogress)
                    {
                        InterruptPending = true;
                        ProcDiePending = true;
                    }
                    break;
                case SIGINT:
                    ankus_worker_int = 1;
                    break;
                case SIGCHLD:
                    ankus_worker_child = 1;
                    break;
            }

            SetLatch(MyLatch);
            errno = saved;
        }

        static void
        ankus_worker_attach(int signals)
        {
            ankus_worker_require();
            if ((signals & ~15) != 0)
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid background-worker signal flags")));
            }

            pqsignal(SIGHUP, ankus_worker_signal);
            pqsignal(SIGTERM, ankus_worker_signal);
            if (signals & 4)
            {
                pqsignal(SIGINT, ankus_worker_signal);
            }

            if (signals & 8)
            {
                pqsignal(SIGCHLD, ankus_worker_signal);
            }

            BackgroundWorkerUnblockSignals();
            ankus_signal_routing_ensure();
        }

        static int
        ankus_worker_consume(int signals)
        {
            sigset_t previous;
            int received = 0;
            ankus_worker_require();
            if ((signals & ~15) != 0)
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid background-worker signal flags")));
            }

        #if defined(WIN32) && PG_VERSION_NUM < 160000
            previous = pg_signal_mask;
            BackgroundWorkerBlockSignals();
        #else
            if (sigprocmask(SIG_SETMASK, &BlockSig, &previous) != 0)
            {
                ereport(ERROR, (errcode_for_file_access(), errmsg("could not block background-worker signals: %m")));
            }
        #endif

            if ((signals & 1) && ankus_worker_hup)
            {
                received |= 1;
                ankus_worker_hup = 0;
            }

            if ((signals & 2) && ankus_worker_term)
            {
                received |= 2;
                ankus_worker_term = 0;
            }

            if ((signals & 4) && ankus_worker_int)
            {
                received |= 4;
                ankus_worker_int = 0;
            }

            if ((signals & 8) && ankus_worker_child)
            {
                received |= 8;
                ankus_worker_child = 0;
            }

        #if defined(WIN32) && PG_VERSION_NUM < 160000
            pqsigsetmask(previous);
        #else
            if (sigprocmask(SIG_SETMASK, &previous, NULL) != 0)
            {
                ereport(ERROR, (errcode_for_file_access(), errmsg("could not restore background-worker signals: %m")));
            }
        #endif

            return received;
        }

        static void
        ankus_worker_forget(void *argument)
        {
            AnkusWorkerHandle *entry = (AnkusWorkerHandle *) argument;
            AnkusWorkerHandle **position = &ankus_worker_handles;
            while (*position != NULL && *position != entry)
            {
                position = &(*position)->next;
            }

            if (*position == entry)
            {
                *position = entry->next;
            }
        }

        static AnkusWorkerHandle *
        ankus_worker_find(intptr_t identity, bool required)
        {
            for (AnkusWorkerHandle *entry = ankus_worker_handles; entry != NULL; entry = entry->next)
            {
                if (entry->id == (uint64) identity)
                {
                    return entry;
                }
            }

            if (required)
            {
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("the Ankus background-worker handle is no longer live")));
            }

            return NULL;
        }

        static void
        ankus_worker_release(AnkusMemoryRequest *request)
        {
            AnkusWorkerHandle *entry = ankus_worker_find(request->context, false);
            if (entry != NULL)
            {
                MemoryContextDelete(entry->owner);
            }
        }

        static void
        ankus_worker_copy(char *target, size_t capacity, const char *source, bool empty, const char *field)
        {
            if (source == NULL || (!empty && source[0] == '\0') || strlen(source) >= capacity)
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                    errmsg("background-worker %s must fit the selected PostgreSQL field without truncation", field)));
            }

            memcpy(target, source, strlen(source) + 1);
        }

        static void
        ankus_worker_register(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            if (request->pointer == 0 || request->length != sizeof(AnkusWorkerDefinition))
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid Ankus background-worker definition")));
            }

            const AnkusWorkerDefinition *definition = (const AnkusWorkerDefinition *) request->pointer;
            BackgroundWorker worker = {0};
            ankus_worker_copy(worker.bgw_name, sizeof(worker.bgw_name), definition->name, false, "name");
            ankus_worker_copy(worker.bgw_type, sizeof(worker.bgw_type), definition->type, false, "type");
            ankus_worker_copy(worker.bgw_library_name, sizeof(worker.bgw_library_name), definition->library, false, "library");
            ankus_worker_copy(worker.bgw_function_name, sizeof(worker.bgw_function_name), definition->entry, false, "entry");
            ankus_worker_copy(worker.bgw_extra, sizeof(worker.bgw_extra), definition->extra, true, "extra text");
            if (definition->start_time < 0 || definition->start_time > 2 ||
                definition->database_access < 0 || definition->database_access > 1 || definition->notify_pid < 0 ||
                (definition->database_access && definition->start_time == 0) ||
                definition->restart_seconds < -1 || definition->restart_seconds > USECS_PER_DAY / 1000)
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid Ankus background-worker startup options")));
            }

            worker.bgw_flags = BGWORKER_SHMEM_ACCESS |
                (definition->database_access ? BGWORKER_BACKEND_DATABASE_CONNECTION : 0);
            worker.bgw_start_time = definition->start_time == 0 ? BgWorkerStart_PostmasterStart :
                definition->start_time == 1 ? BgWorkerStart_ConsistentState : BgWorkerStart_RecoveryFinished;
            worker.bgw_restart_time = definition->restart_seconds;
            worker.bgw_main_arg = (Datum) definition->argument;
            worker.bgw_notify_pid = definition->notify_pid;
            if (request->flags == 0)
            {
                if (!process_shared_preload_libraries_in_progress || definition->notify_pid != 0)
                {
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                        errmsg("static Ankus workers require shared preload and no notification PID")));
                }

                RegisterBackgroundWorker(&worker);
                return;
            }

            if (!IsUnderPostmaster || process_shared_preload_libraries_in_progress || MyProc == NULL)
            {
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("dynamic Ankus workers require an initialized backend after shared preload")));
            }

            if (ankus_worker_next_id >= INTPTR_MAX)
            {
                ereport(ERROR, (errcode(ERRCODE_PROGRAM_LIMIT_EXCEEDED), errmsg("Ankus background-worker handle identities are exhausted")));
            }

            MemoryContext caller = CurrentMemoryContext;
            MemoryContext owner = AllocSetContextCreate(TopMemoryContext, "Ankus background-worker handle", ALLOCSET_SMALL_SIZES);
            PG_TRY();
            {
                MemoryContextSwitchTo(owner);
                AnkusWorkerHandle *entry = palloc0(sizeof(AnkusWorkerHandle));
                entry->id = ++ankus_worker_next_id;
                entry->owner = owner;
                entry->notify_pid = definition->notify_pid;
                entry->cleanup.func = ankus_worker_forget;
                entry->cleanup.arg = entry;
                MemoryContextRegisterResetCallback(owner, &entry->cleanup);
                entry->next = ankus_worker_handles;
                ankus_worker_handles = entry;
                bool registered = RegisterDynamicBackgroundWorker(&worker, &entry->handle);
                MemoryContextSwitchTo(caller);
                if (registered)
                {
                    result->value = 1;
                    result->context = (intptr_t) entry->id;
                }
                else
                {
                    MemoryContextDelete(owner);
                }
            }
            PG_CATCH();
            {
                MemoryContext propagating = MemoryContextSwitchTo(caller);
                MemoryContextDelete(owner);
                MemoryContextSwitchTo(propagating);
                PG_RE_THROW();
            }
            PG_END_TRY();
        }

        static void
        ankus_worker_observe(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            AnkusWorkerHandle *entry = ankus_worker_find(request->context, true);
            pid_t pid = 0;
            BgwHandleStatus status;
            if (request->flags == 4)
            {
                TerminateBackgroundWorker(entry->handle);
                return;
            }

            if (request->flags != 2 && entry->notify_pid != MyProcPid)
            {
                result->value = 4;
                return;
            }

            status = request->flags == 2 ? GetBackgroundWorkerPid(entry->handle, &pid) :
                request->flags == 3 ? WaitForBackgroundWorkerStartup(entry->handle, &pid) :
                WaitForBackgroundWorkerShutdown(entry->handle);
            switch (status)
            {
                case BGWH_STARTED: result->value = 0; result->pointer = (intptr_t) pid; break;
                case BGWH_NOT_YET_STARTED: result->value = 1; break;
                case BGWH_STOPPED: result->value = 2; break;
                case BGWH_POSTMASTER_DIED: result->value = 3; break;
                default: ereport(ERROR, (errmsg("unknown PostgreSQL background-worker status")));
            }
        }

        static void
        ankus_worker_transaction(AnkusMemoryRequest *request, volatile bool *recovered)
        {
            typedef int (*AnkusWorkerTransaction)(AnkusWorkerExecute, AnkusMemoryApi *);
            ankus_worker_require();
            if (!ankus_worker_connected || ankus_worker_execute == NULL || request->pointer == 0 || IsTransactionState())
            {
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("a worker transaction requires a connected worker outside an existing transaction")));
            }

            MemoryContext caller = CurrentMemoryContext;
            AnkusError *error = palloc0(sizeof(AnkusError));
            PG_TRY();
            {
                SetCurrentStatementStartTimestamp();
                StartTransactionCommand();
                ankus_worker_transaction_active = 1;
                pgstat_report_activity(STATE_RUNNING, NULL);
                PushActiveSnapshot(GetTransactionSnapshot());
                AnkusMemoryApi memory = {0};
                ankus_memory_initialize(&memory);
                int status;
                ANKUS_MANAGED_INVOKE(status, error, ((AnkusWorkerTransaction) request->pointer)(ankus_worker_execute, &memory));
                MemoryContextSwitchTo(caller);
                if (status == 0)
                {
                    PopActiveSnapshot();
                    CommitTransactionCommand();
                }
                else
                {
                    AbortCurrentTransaction();
                    *recovered = true;
                }

                ankus_worker_transaction_active = 0;
                /* As worker_spi does, publish the transaction's table and I/O counts, which otherwise stay in this
                 * process until it exits, so autovacuum and the statistics views see the worker's writes. Without
                 * force, PostgreSQL limits how often this flushes. */
                (void) pgstat_report_stat(false);
                pgstat_report_activity(STATE_IDLE, NULL);
                if (error->sqlstate != 0)
                    ankus_report(error, ERROR);
            }
            PG_CATCH();
            {
                ankus_worker_transaction_active = 0;
                MemoryContext propagating = MemoryContextSwitchTo(caller);
                AbortCurrentTransaction();
                *recovered = true;
                ankus_release_error(error);
                pfree(error);
                MemoryContextSwitchTo(propagating);
                PG_RE_THROW();
            }
            PG_END_TRY();
            MemoryContextSwitchTo(caller);
            ankus_release_error(error);
            pfree(error);
        }

        static void
        ankus_memory_worker(AnkusMemoryRequest *request, AnkusMemoryResult *result, volatile bool *recovered)
        {
            if (request->flags == 0 || request->flags == 1)
            {
                ankus_worker_register(request, result);
                return;
            }

            if (request->flags >= 2 && request->flags <= 5)
            {
                ankus_worker_observe(request, result);
                return;
            }

            ankus_worker_require();
            switch (request->flags)
            {
                case 7:
                {
                    const char *text = request->value == 0 ? MyBgworkerEntry->bgw_name :
                        request->value == 1 ? MyBgworkerEntry->bgw_type :
                        request->value == 2 ? MyBgworkerEntry->bgw_extra : NULL;
                    if (text == NULL || request->data == 0 || strlen(text) > request->length)
                    {
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid background-worker text request")));
                    }

                    result->length = strlen(text);
                    memcpy((void *) request->data, text, result->length);
                    break;
                }
                case 8:
                    ankus_worker_attach((int) request->value);
                    break;
                case 9:
                    result->value = ankus_worker_consume((int) request->value);
                    break;
                case 10:
                {
                    if (request->value < -1 || request->value > INT_MAX)
                    {
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid background-worker latch timeout")));
                    }

                    if (ShutdownRequestPending || ankus_worker_term || !PostmasterIsAlive())
                    {
                        (void) ankus_worker_consume(2);
                        result->value = 0;
                        break;
                    }

                    /* Flush counts the rate limit held back before sleeping, as a backend does before going idle. */
                    if (!IsTransactionOrTransactionBlock())
                        (void) pgstat_report_stat(true);

                    int events = WaitLatch(MyLatch, WL_LATCH_SET | WL_POSTMASTER_DEATH |
                        (request->value >= 0 ? WL_TIMEOUT : 0), request->value >= 0 ? (long) request->value : 0, PG_WAIT_EXTENSION);
                    ResetLatch(MyLatch);
                    CHECK_FOR_INTERRUPTS();
                    result->value = (ankus_worker_consume(2) == 0 && !ShutdownRequestPending && (events & WL_POSTMASTER_DEATH) == 0) ? 1 : 0;
                    break;
                }
                case 11:
                case 12:
                    if (ankus_worker_connected || !(MyBgworkerEntry->bgw_flags & BGWORKER_BACKEND_DATABASE_CONNECTION))
                    {
                        ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                            errmsg("the worker must request database access and connect only once")));
                    }

                    if (request->flags == 11)
                    {
                        BackgroundWorkerInitializeConnection((const char *) request->context, (const char *) request->pointer, 0);
                    }
                    else
                    {
                        BackgroundWorkerInitializeConnectionByOid((Oid) request->context, (Oid) request->value, 0);
                    }

                    ankus_worker_connected = true;
                    break;
                case 13:
                    ankus_worker_transaction(request, recovered);
                    break;
                case 14:
                    result->value = (!ShutdownRequestPending && !ankus_worker_term && PostmasterIsAlive()) ? 1 : 0;
                    break;
                case 15:
                    ConfigReloadPending = 0;
                    ProcessConfigFile(PGC_SIGHUP);
                    break;
                default:
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("unknown Ankus background-worker operation")));
            }
        }
        """;
}
