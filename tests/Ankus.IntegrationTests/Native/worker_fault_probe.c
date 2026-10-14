
#undef AllocSetContextCreateInternal
#include <signal.h>

static volatile sig_atomic_t fault_worker_terminated;

static void
fault_worker_signal(SIGNAL_ARGS)
{
    int saved_errno = errno;
#if PG_VERSION_NUM >= 190000
    (void) pg_siginfo;
#endif
    if (postgres_signal_arg == SIGTERM)
    {
        fault_worker_terminated = 1;
    }

    SetLatch(MyLatch);
    errno = saved_errno;
}

PGDLLEXPORT void ankus_test_allocation_worker(Datum argument);

/* Independent execution evidence survives a missing observation handle. */
PGDLLEXPORT void
ankus_test_allocation_worker(Datum argument)
{
    char path[MAXPGPATH];
    pqsignal(SIGTERM, fault_worker_signal);
    BackgroundWorkerUnblockSignals();
    snprintf(path, sizeof(path), "ankus-worker-fault-%s.ready", MyBgworkerEntry->bgw_extra);
    FILE *stream = AllocateFile(path, "wb");
    if (stream == NULL)
    {
        ereport(ERROR, (errcode_for_file_access(), errmsg("could not open worker witness: %m")));
    }

    fprintf(stream, "%d|%u", MyProcPid, DatumGetUInt32(argument));
    if (FreeFile(stream) != 0)
    {
        ereport(ERROR, (errcode_for_file_access(), errmsg("could not close worker witness: %m")));
    }

    for (int attempt = 0; !fault_worker_terminated && attempt < 3000; attempt++)
    {
        int events = WaitLatch(MyLatch, WL_LATCH_SET | WL_TIMEOUT | WL_POSTMASTER_DEATH, 10, PG_WAIT_EXTENSION);
        ResetLatch(MyLatch);
        if ((events & WL_POSTMASTER_DEATH) != 0)
        {
            break;
        }
    }

    proc_exit(0);
}

static int
fault_worker_ready(const char *extra)
{
    char path[MAXPGPATH];
    snprintf(path, sizeof(path), "ankus-worker-fault-%s.ready", extra);
    for (int attempt = 0; attempt < 3000; attempt++)
    {
        FILE *stream = AllocateFile(path, "rb");
        if (stream != NULL)
        {
            int process = 0;
            unsigned int argument = 0;
            int fields = fscanf(stream, "%d|%u", &process, &argument);
            FreeFile(stream);
            if (fields == 2)
            {
                fault_require(process > 0 && process != MyProcPid && argument == 42, "invalid worker witness");
                return process;
            }
        }

        CHECK_FOR_INTERRUPTS();
        pg_usleep(10000L);
    }

    elog(ERROR, "worker fault fixture did not observe its worker entry");
    return 0;
}

static int
fault_worker_contexts(void)
{
    volatile int count = 0;
    for (MemoryContext child = TopMemoryContext->firstchild; child != NULL; child = child->nextchild)
    {
        if (strcmp(child->name, "Ankus background-worker handle") == 0)
        {
            count++;
        }
    }

    return count;
}

PGDLLEXPORT Datum ankus_test_worker_allocation_fault(PG_FUNCTION_ARGS);
PG_FUNCTION_INFO_V1(ankus_test_worker_allocation_fault);
PGDLLEXPORT Datum
ankus_test_worker_allocation_fault(PG_FUNCTION_ARGS)
{
    int mode = PG_GETARG_INT32(0);
    char *identity = text_to_cstring(PG_GETARG_TEXT_PP(1));
    fault_require(mode >= 1 && mode <= 4, "unknown worker allocation scenario");
    fault_require(strlen(identity) == 32, "invalid worker witness identity length");
    for (int index = 0; index < 32; index++)
    {
        fault_require((identity[index] >= '0' && identity[index] <= '9') ||
            (identity[index] >= 'a' && identity[index] <= 'f'), "invalid worker witness identity");
    }

    fault_require(ankus_worker_handles == NULL && fault_worker_owner == NULL, "previous worker owner remained live");
    MemoryContext caller = CurrentMemoryContext;
    int contexts = fault_worker_contexts();
    uint64 previous_id = ankus_worker_next_id;
    char failed_extra[64];
    char retry_extra[64];
    snprintf(failed_extra, sizeof(failed_extra), "%s-failed", identity);
    snprintf(retry_extra, sizeof(retry_extra), "%s-retry", identity);
    volatile int published_process = 0;
    volatile bool termination_requested = false;
    char *report = NULL;
    fault_worker_created = 0;
    fault_worker_deleted = 0;
    fault_worker_allocations = 0;
    fault_worker_stage = mode == 4 ? 0 : mode;
    fault_worker_monitor = true;
    /* Modes 1 through 3 fail one allocation of registration, and mode 4 exhausts the handle identities. */
    if (mode == 4)
    {
        ankus_worker_next_id = INTPTR_MAX;
    }

    PG_TRY();
    {
        AnkusMemoryApi api;
        ankus_memory_initialize(&api);
        AnkusWorkerDefinition definition = {0};
        definition.name = "Ankus allocation fault worker";
        definition.type = definition.name;
        definition.library = ANKUS_WORKER_FAULT_LIBRARY;
        definition.entry = "ankus_test_allocation_worker";
        definition.extra = failed_extra;
        definition.argument = 42;
        definition.start_time = 2;
        definition.restart_seconds = -1;
        definition.notify_pid = MyProcPid;
        AnkusMemoryRequest request = {0};
        AnkusMemoryResult result = {0};
        AnkusError error = {0};
        request.operation = ANKUS_MEMORY_WORKER;
        request.flags = 1;
        request.pointer = (intptr_t) &definition;
        request.length = sizeof(definition);
        int status = ankus_memory_invoke(&api, &request, &result, &error);
        if (mode == 4)
        {
            ankus_worker_next_id = previous_id;
        }

        fault_require(status == 1 && error.sqlstate == (mode == 4 ? ERRCODE_PROGRAM_LIMIT_EXCEEDED : ERRCODE_OUT_OF_MEMORY),
            "wrong worker allocation error");
        fault_require(fault_worker_stage == 0 && CurrentMemoryContext == caller && result.context == 0 && result.value == 0,
            "worker failure retained a result or changed the caller context");
        fault_require(ankus_worker_handles == NULL && fault_worker_owner == NULL && fault_worker_contexts() == contexts,
            "worker failure leaked its owner or linked registry entry");
        /* The failure's counts and diagnostics are copied before its owned fields are released. */
        int created = fault_worker_created;
        int deleted = fault_worker_deleted;
        int allocations = fault_worker_allocations;
        int id_change = (int) (ankus_worker_next_id - previous_id);
        char *message = pstrdup(error.message);
        char *detail = ankus_error_field(&error, ANKUS_ERROR_DETAIL, true);
        char *hint = ankus_error_field(&error, ANKUS_ERROR_HINT, true);
        char *state = pstrdup(unpack_sql_state(error.sqlstate));
        ankus_release_error(&error);
        for (int index = 0; index < ANKUS_ERROR_FIELD_COUNT; index++)
        {
            fault_require(error.fields[index].data == NULL && error.fields[index].release == NULL,
                "worker diagnostics retained owned fields after release");
        }

        /* Mode 3 fails after PostgreSQL started the worker, which then runs without a handle and is stopped here. */
        if (mode == 3)
        {
            published_process = fault_worker_ready(failed_extra);
            fault_require(kill(published_process, SIGTERM) == 0, "could not stop worker published before allocation failure");
            termination_requested = true;
        }

        /* A retry registers, starts, terminates, waits for and releases a worker without leaking state. */
        definition.extra = retry_extra;
        memset(&result, 0, sizeof(result));
        fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0 && result.value == 1 && result.context > 0,
            "worker registration retry failed");
        intptr_t retry_id = result.context;
        request.context = retry_id;
        request.flags = 3;
        fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0 && result.value == 0 && result.pointer > 0,
            "worker registration retry did not start");
        int retry_process = (int) result.pointer;
        fault_require(retry_process == fault_worker_ready(retry_extra), "worker retry changed its PID or argument");
        request.flags = 4;
        fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "worker retry termination failed");
        request.flags = 5;
        fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0 && result.value == 2,
            "worker retry did not stop");
        request.flags = 6;
        fault_require(ankus_memory_invoke(&api, &request, &result, &error) == 0, "worker retry release failed");
        fault_require(ankus_worker_handles == NULL && fault_worker_owner == NULL && fault_worker_contexts() == contexts &&
            CurrentMemoryContext == caller, "worker retry leaked state");
        report = psprintf("%s|%s|%s|%s|%d|%d|%d|%d|%d|%d|%d|%d|%d|%d",
            state, message, detail == NULL ? "" : detail, hint == NULL ? "" : hint,
            created, deleted, allocations, id_change, (int) published_process, retry_process,
            (int) (retry_id - previous_id), fault_worker_created, fault_worker_deleted, fault_worker_allocations);
    }
    PG_FINALLY();
    {
        fault_worker_stage = 0;
        if (mode == 4 && ankus_worker_next_id == INTPTR_MAX)
        {
            ankus_worker_next_id = previous_id;
        }

        if (published_process > 0 && !termination_requested)
        {
            (void) kill(published_process, SIGTERM);
        }

        while (ankus_worker_handles != NULL)
        {
            AnkusWorkerHandle *entry = ankus_worker_handles;
            TerminateBackgroundWorker(entry->handle);
            WaitForBackgroundWorkerShutdown(entry->handle);
            MemoryContextDelete(entry->owner);
        }

        fault_worker_monitor = false;
    }
    PG_END_TRY();

    PG_RETURN_TEXT_P(cstring_to_text(report));
}
