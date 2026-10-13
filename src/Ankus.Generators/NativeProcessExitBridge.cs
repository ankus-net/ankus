namespace Ankus.Generators;

/// <summary>
/// Runs managed callbacks when a PostgreSQL process exits, through one <c>before_shmem_exit</c> callback per process.
/// </summary>
internal static class NativeProcessExitBridge
{
    /// <summary>
    /// Implements operation 48, which records the managed dispatcher and registers the native callback once in the
    /// calling process.
    /// </summary>
    /// <remarks>
    /// The callback runs before the process detaches from shared memory, after a normal exit or a FATAL error, as
    /// pglogical's worker cleanup does. An ERROR during exit becomes FATAL, so managed failures are reported as
    /// warnings. Forked children clear inherited exit callbacks, so each process registers its own.
    /// </remarks>
    internal const string Source = """

        typedef int (*AnkusProcessExitDispatch)(intptr_t, AnkusMemoryApi *, AnkusError *);

        static AnkusProcessExitDispatch ankus_process_exit_dispatch = NULL;
        static int ankus_process_exit_pid = 0;

        static void
        ankus_process_exit(int code, Datum argument)
        {
            AnkusMemoryApi memory = {0};
            AnkusError error = {0};
            int status;
            (void) argument;
            if (ankus_process_exit_dispatch == NULL)
                return;
            ankus_memory_initialize(&memory);
            ANKUS_MANAGED_INVOKE(status, &error, ankus_process_exit_dispatch((intptr_t) code, &memory, &error));
            if (status != 0)
                ankus_report(&error, WARNING);
            ankus_release_error(&error);
        }

        static void
        ankus_memory_process_exit(AnkusMemoryRequest *request)
        {
            if (request->pointer == 0)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid process exit dispatcher")));
            ankus_process_exit_dispatch = (AnkusProcessExitDispatch) request->pointer;
            if (ankus_process_exit_pid != MyProcPid)
            {
                before_shmem_exit(ankus_process_exit, (Datum) 0);
                ankus_process_exit_pid = MyProcPid;
            }
        }

        """;
}
