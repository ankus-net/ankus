namespace Ankus.Generators;

/// <summary>
/// Emits PostgreSQL transaction and subtransaction dispatch into managed per-transaction registrations.
/// </summary>
internal static class NativeTransactionBridge
{
    /// <summary>
    /// Gets native callback registration, stable event mapping, capability binding, and managed error reporting.
    /// </summary>
    internal const string Source = """
        #include "access/xact.h"
        #include "commands/trigger.h"
        #include "storage/lock.h"
        #include "storage/proc.h"
        #include "utils/snapmgr.h"

        typedef int (*AnkusTransactionManaged)(int, int, uint32, uint32, AnkusError *,
            AnkusExecute, intptr_t, AnkusMemoryApi *);
        typedef int (*AnkusTransactionLog)(int, int, AnkusError *, AnkusError *, int *);

        static int ankus_spi_execute(AnkusRequest *, AnkusResult *, AnkusError *);
        static AnkusTransactionManaged ankus_transaction_managed;
        static bool ankus_transaction_registered;
        static bool ankus_subtransaction_registered;

        /* Ankus guards open internal subtransactions for recovery. Subtransaction
         * callbacks ignore exactly those, including another loaded Ankus extension's,
         * while still observing savepoints and PL/pgSQL exception blocks nested inside
         * guarded SQL. Every Ankus extension in a backend shares this registry through
         * a PostgreSQL rendezvous variable. An entry matches only its own top-level
         * transaction and subtransaction ID, which PostgreSQL never reuses within a
         * transaction, so an entry left behind by an abort can never hide a savepoint. */
        #define ANKUS_SUBTRANSACTION_REGISTRY 0x414e5331U

        typedef struct AnkusInternalSubtransaction
        {
            LocalTransactionId transaction;
            SubTransactionId subtransaction;
            int nest_level;
        } AnkusInternalSubtransaction;

        typedef struct AnkusSubtransactionRegistry
        {
            uint32 magic;
            int starting;
            int count;
            int capacity;
            AnkusInternalSubtransaction *entries;
        } AnkusSubtransactionRegistry;

        static AnkusSubtransactionRegistry ankus_private_subtransactions = { ANKUS_SUBTRANSACTION_REGISTRY, 0, 0, 0, NULL };
        static AnkusSubtransactionRegistry *ankus_subtransactions;

        /* Locates or creates the shared registry; callers are native PostgreSQL or guarded frames. */
        static AnkusSubtransactionRegistry *
        ankus_subtransaction_registry(void)
        {
            if (ankus_subtransactions == NULL)
            {
                AnkusSubtransactionRegistry **slot =
                    (AnkusSubtransactionRegistry **) find_rendezvous_variable("ankus_internal_subtransactions");
                if (*slot == NULL)
                {
                    AnkusSubtransactionRegistry *registry = MemoryContextAllocZero(TopMemoryContext, sizeof(AnkusSubtransactionRegistry));
                    registry->magic = ANKUS_SUBTRANSACTION_REGISTRY;
                    *slot = registry;
                }

                /* An incompatible Ankus version keeps its own registry; this one still filters its own guards. */
                ankus_subtransactions = (*slot)->magic == ANKUS_SUBTRANSACTION_REGISTRY ? *slot : &ankus_private_subtransactions;
            }

            return ankus_subtransactions;
        }

        static LocalTransactionId
        ankus_local_transaction(void)
        {
            VirtualTransactionId transaction;
            if (MyProc == NULL)
            {
                return InvalidLocalTransactionId;
            }

            GET_VXID_FROM_PGPROC(transaction, *MyProc);
            return transaction.localTransactionId;
        }

        /* Drops entries for subtransactions that have ended, after a release or rollback. */
        static void
        ankus_trim_internal_subtransactions(void)
        {
            AnkusSubtransactionRegistry *registry = ankus_subtransactions;
            if (registry == NULL)
            {
                return;
            }

            LocalTransactionId transaction = ankus_local_transaction();
            int nest_level = GetCurrentTransactionNestLevel();
            while (registry->count > 0 && (registry->entries[registry->count - 1].transaction != transaction ||
                registry->entries[registry->count - 1].nest_level > nest_level))
            {
                registry->count--;
            }
        }

        /* Opens a recovery subtransaction that no Ankus subtransaction callback observes. */
        static void
        ankus_begin_internal_subtransaction(void)
        {
            AnkusSubtransactionRegistry *registry = ankus_subtransaction_registry();
            ankus_trim_internal_subtransactions();
            if (registry->count == registry->capacity)
            {
                int capacity = registry->capacity == 0 ? 8 : registry->capacity * 2;
                Size size = sizeof(AnkusInternalSubtransaction) * (Size) capacity;
                registry->entries = registry->entries == NULL
                    ? MemoryContextAlloc(TopMemoryContext, size) : repalloc(registry->entries, size);
                registry->capacity = capacity;
            }

            /* Start callbacks run before the new identity is known. The registry is read
             * through its static pointer after setjmp, so no local can be clobbered. */
            registry->starting++;
            PG_TRY();
            {
                BeginInternalSubTransaction(NULL);
            }
            PG_FINALLY();
            {
                ankus_subtransactions->starting--;
            }
            PG_END_TRY();
            ankus_subtransactions->entries[ankus_subtransactions->count].transaction = ankus_local_transaction();
            ankus_subtransactions->entries[ankus_subtransactions->count].subtransaction = GetCurrentSubTransactionId();
            ankus_subtransactions->entries[ankus_subtransactions->count].nest_level = GetCurrentTransactionNestLevel();
            ankus_subtransactions->count++;
        }

        /* Commits an internal subtransaction; its commit callbacks still observe it as internal. */
        static void
        ankus_release_internal_subtransaction(void)
        {
            ReleaseCurrentSubTransaction();
            ankus_trim_internal_subtransactions();
        }

        static bool
        ankus_internal_subtransaction(SubXactEvent event, SubTransactionId subtransaction)
        {
            AnkusSubtransactionRegistry *registry = ankus_subtransaction_registry();
            LocalTransactionId transaction;
            if (event == SUBXACT_EVENT_START_SUB)
            {
                return registry->starting != 0;
            }

            transaction = ankus_local_transaction();
            for (int index = registry->count - 1; index >= 0; index--)
            {
                if (registry->entries[index].subtransaction == subtransaction && registry->entries[index].transaction == transaction)
                {
                    return true;
                }
            }

            return false;
        }

        typedef struct AnkusTransactionFrame
        {
            struct AnkusTransactionFrame *previous;
            AnkusError failure;
            bool direct_spi;
            bool failed;
            /* Set for explicit subtransaction scopes, which permit nested scopes; clear for callbacks. */
            bool scope;
        } AnkusTransactionFrame;

        static AnkusTransactionFrame *ankus_transaction_frame;

        static void
        ankus_transaction_id_operation(AnkusResult *result)
        {
            result->processed = (int64) U64FromFullTransactionId(ReadNextFullTransactionId());
        }

        static int
        ankus_transaction_log(int operation, int level, AnkusError *report, AnkusError *error, int *enabled)
        {
            if (operation == 2)
                return ankus_recovery_terminal(level, report, error);

            if (ankus_recovery_failed(error))
            {
                if (error->report_level < 12 || (error->flags & ANKUS_ERROR_UNRECOVERED) != 0)
                    return 1;
                memset(error, 0, sizeof(*error));
            }

            MemoryContext caller = CurrentMemoryContext;
            MemoryContext recovery = ankus_error_recovery_context(caller);
            uint32 interrupt_holdoff = InterruptHoldoffCount;
            uint32 shared_held_before = ankus_shared_held_count;
            uint32 cancel_holdoff = QueryCancelHoldoffCount;
            volatile int status = 0;
            volatile bool flushed = false;
            if (operation == 1 && level >= 0 && level < 10)
            {
                HOLD_INTERRUPTS();
            }

            AnkusPendingError *volatile pending = ankus_pending_error_save();
            PG_TRY();
            {
                ANKUS_PG_TRY_INNER();
                {
                    if ((operation != 0 && operation != 1) || level < 0 || level > 12 || enabled == NULL)
                    {
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                            errmsg("Invalid transaction callback logging request")));
                    }

                    if (operation == 1 && (level >= 10 || report == NULL))
                    {
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                            errmsg("Terminal transaction callback messages must unwind managed code before reporting")));
                    }

                    *enabled = ankus_log_enabled(ankus_log_level(level), error) ? 1 : 0;
                    status = error->sqlstate != 0;
                    if (status == 0 && operation == 1 && *enabled != 0)
                    {
                        ankus_report(report, ankus_log_level(level));
                    }
                }
                ANKUS_PG_CATCH_INNER();
                {
                    ErrorData *data;
                    MemoryContext diagnostic;
                    MemoryContextSwitchTo(recovery);
                    diagnostic = AllocSetContextCreate(TopMemoryContext, "Ankus transaction callback diagnostics", ALLOCSET_SMALL_SIZES);
                    MemoryContextSwitchTo(diagnostic);
                    data = ankus_copy_error_data();
                    FlushErrorState();
                    flushed = true;
                    ankus_capture_error(data, error);
                    ankus_recovery_record(error, false);
                    ankus_free_error_data(data);
                    MemoryContextSwitchTo(recovery);
                    MemoryContextDelete(diagnostic);
                    status = 1;
                }
                ANKUS_PG_END_TRY_INNER();
            }
            PG_CATCH();
            {
                MemoryContextSwitchTo(recovery);
                FlushErrorState();
                ereport(FATAL, (errmsg("Unable to recover an Ankus transaction callback logging failure")));
            }
            PG_END_TRY();
            if (flushed)
                ankus_pending_error_restore(pending);
            else
                ankus_pending_error_release(pending);
            InterruptHoldoffCount = ankus_shared_restore_interrupts(interrupt_holdoff, shared_held_before);
            QueryCancelHoldoffCount = cancel_holdoff;
            return status;
        }

        static void
        ankus_transaction_report(AnkusError *error, int level)
        {
            PG_TRY();
            {
                ankus_report(error, level);
            }
            PG_FINALLY();
            {
                ankus_release_error(error);
            }
            PG_END_TRY();
        }

        /* The level reported for a callback failure in each phase. */
        enum AnkusCallbackFailure
        {
            /* Reversible phases reject the operation. */
            ANKUS_CALLBACK_ERROR,
            /* Savepoint rollback must finish; an ERROR there would re-enter the same abort. */
            ANKUS_CALLBACK_WARNING,
            /* FATAL runs abort cleanup, which is unsafe after an irreversible transaction phase. */
            ANKUS_CALLBACK_PANIC
        };

        static void
        ankus_transaction_dispatch(int kind, int event, uint32 subtransaction_id,
            uint32 parent_subtransaction_id, enum AnkusCallbackFailure failure, bool sql)
        {
            AnkusError error = {0};
            AnkusMemoryApi memory = {0};
            AnkusMemoryProtection protection = {0};
            AnkusTransactionFrame frame = {0};
            bool snapshot_owned = false;
            int status;
            if (ankus_transaction_managed == NULL)
            {
                return;
            }

            if (sql && !ActiveSnapshotSet())
            {
                PushActiveSnapshot(GetTransactionSnapshot());
                snapshot_owned = true;
            }

            frame.previous = ankus_transaction_frame;
            frame.direct_spi = sql;
            ankus_transaction_frame = &frame;
            ankus_memory_initialize(&memory);
            ankus_memory_protect(&protection, CurrentMemoryContext, false);
            ANKUS_MANAGED_INVOKE(status, &error, ankus_transaction_managed(kind, event, subtransaction_id, parent_subtransaction_id,
                &error, sql ? ankus_spi_execute : NULL, (intptr_t) ankus_transaction_log, &memory));
            ankus_transaction_frame = frame.previous;
            ankus_memory_protection = protection.previous;
            if (snapshot_owned)
            {
                PopActiveSnapshot();
            }

            if (frame.failed)
            {
                ankus_release_error(&error);
                error = frame.failure;
                memset(&frame.failure, 0, sizeof(frame.failure));
                status = 1;
            }

            if (status != 0)
            {
                ankus_transaction_report(&error, failure == ANKUS_CALLBACK_ERROR ? ERROR
                    : failure == ANKUS_CALLBACK_WARNING ? WARNING : PANIC);
            }
            else if (sql && kind == 0)
            {
                /* PostgreSQL fires deferred triggers before PreCommit and PrePrepare callbacks,
                 * then discards anything still queued. Fire the events queued by callback SQL,
                 * such as deferred foreign-key checks, so they can still reject the transaction. */
                AfterTriggerFireDeferred();
            }

            ankus_release_error(&error);
        }

        static void
        ankus_transaction_callback(XactEvent event, void *argument)
        {
            (void) argument;
            switch (event)
            {
                case XACT_EVENT_ABORT:
                    ankus_transaction_dispatch(0, 0, 0, 0, ANKUS_CALLBACK_PANIC, false);
                    break;
                case XACT_EVENT_COMMIT:
                    ankus_transaction_dispatch(0, 1, 0, 0, ANKUS_CALLBACK_PANIC, false);
                    break;
                case XACT_EVENT_PRE_COMMIT:
                    ankus_transaction_dispatch(0, 2, 0, 0, ANKUS_CALLBACK_ERROR, true);
                    break;
                case XACT_EVENT_PARALLEL_ABORT:
                    ankus_transaction_dispatch(0, 3, 0, 0, ANKUS_CALLBACK_PANIC, false);
                    break;
                case XACT_EVENT_PARALLEL_COMMIT:
                    ankus_transaction_dispatch(0, 4, 0, 0, ANKUS_CALLBACK_PANIC, false);
                    break;
                case XACT_EVENT_PARALLEL_PRE_COMMIT:
                    ankus_transaction_dispatch(0, 5, 0, 0, ANKUS_CALLBACK_ERROR, false);
                    break;
                case XACT_EVENT_PREPARE:
                    ankus_transaction_dispatch(0, 6, 0, 0, ANKUS_CALLBACK_PANIC, false);
                    break;
                case XACT_EVENT_PRE_PREPARE:
                    ankus_transaction_dispatch(0, 7, 0, 0, ANKUS_CALLBACK_ERROR, true);
                    break;
            }
        }

        static void
        ankus_subtransaction_callback(SubXactEvent event, SubTransactionId subtransaction_id,
            SubTransactionId parent_subtransaction_id, void *argument)
        {
            (void) argument;
            if (ankus_internal_subtransaction(event, subtransaction_id))
            {
                return;
            }

            switch (event)
            {
                case SUBXACT_EVENT_ABORT_SUB:
                    ankus_transaction_dispatch(1, 0, subtransaction_id, parent_subtransaction_id, ANKUS_CALLBACK_WARNING, false);
                    break;
                case SUBXACT_EVENT_COMMIT_SUB:
                    /* A committed savepoint still belongs to its parent, which PostgreSQL then aborts. */
                    ankus_transaction_dispatch(1, 1, subtransaction_id, parent_subtransaction_id, ANKUS_CALLBACK_ERROR, false);
                    break;
                case SUBXACT_EVENT_PRE_COMMIT_SUB:
                    ankus_transaction_dispatch(1, 2, subtransaction_id, parent_subtransaction_id, ANKUS_CALLBACK_ERROR, true);
                    break;
                case SUBXACT_EVENT_START_SUB:
                    ankus_transaction_dispatch(1, 3, subtransaction_id, parent_subtransaction_id, ANKUS_CALLBACK_ERROR, true);
                    break;
            }
        }

        static void
        ankus_transaction_ensure(AnkusTransactionManaged callback, int dispatchers)
        {
            if (callback == NULL || dispatchers < 1 || dispatchers > 2)
            {
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                    errmsg("Invalid managed transaction callback registration")));
            }

            if (!IsTransactionState())
            {
                ereport(ERROR, (errcode(ERRCODE_NO_ACTIVE_SQL_TRANSACTION),
                    errmsg("Transaction callbacks require an active PostgreSQL transaction")));
            }

            if (ankus_transaction_managed != NULL && ankus_transaction_managed != callback)
            {
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("The managed transaction callback dispatcher has already been installed")));
            }

            ankus_transaction_managed = callback;
            if (!ankus_transaction_registered)
            {
                RegisterXactCallback(ankus_transaction_callback, NULL);
                ankus_transaction_registered = true;
            }

            if (dispatchers == 2 && !ankus_subtransaction_registered)
            {
                RegisterSubXactCallback(ankus_subtransaction_callback, NULL);
                ankus_subtransaction_registered = true;
            }
        }

        """;
}
