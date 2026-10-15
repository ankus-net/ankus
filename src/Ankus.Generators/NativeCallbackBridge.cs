namespace Ankus.Generators;

/// <summary>
/// Provides the primary native error and capability boundary used by selected native callback wrappers.
/// </summary>
internal static class NativeCallbackBridge
{
    /// <summary>
    /// Dispatches a registered static managed handler and raises errors only after the managed frame returns.
    /// </summary>
    internal const string Source = """
        #include "miscadmin.h"

        typedef int (*AnkusManagedNativeCallback)(const AnkusNativeCallArgument *, size_t, void *, size_t, void *);

        typedef struct AnkusNativeCallbackContext
        {
            AnkusError *error;
            AnkusExecute execute;
            AnkusMemoryApi *memory;
            AnkusGucReadBinding read;
            AnkusInitializationLog log;
        } AnkusNativeCallbackContext;

        static int ankus_native_callback_depth = 0;

        #if !defined(WIN32)
        __attribute__((visibility("hidden")))
        #endif
        void
        ankus_dispatch_native_callback(AnkusManagedNativeCallback callback, const AnkusNativeCallArgument *arguments,
            size_t count, void *result, size_t result_size)
        {
            /* A hook saved as its own previous hook, or any other cycle through this module's callbacks,
             * recurses through nested dispatch until the native stack overflows and PostgreSQL restarts
             * every backend. Report PostgreSQL's ordinary stack-depth error before entering managed code.
             * Outermost dispatch, including log hooks reporting a stack-depth error, keeps its behavior. */
            if (ankus_native_callback_depth > 0)
            {
                check_stack_depth();
            }

            if (callback == NULL)
            {
                ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                    errmsg("Ankus native callback has no registered managed handler")));
            }

            if (ankus_initialization_state == 0 && !ankus_module_loading && !ankus_worker_restore_in_progress())
            {
                ankus_ensure_initialized();
            }

            MemoryContext caller = CurrentMemoryContext;
            MemoryContext recovery = ankus_error_recovery_context(caller);
            uint64 caller_identity = recovery == ErrorContext ? ankus_memory_context_id(caller) : 0;
            AnkusMemoryApi memory = {0};
            ankus_memory_initialize(&memory);
            /* Reporting callbacks enter from ErrorContext. A recursive ERROR can
             * reset it before the dispatcher releases its owned diagnostics. */
            AnkusError *error = MemoryContextAllocZero(TopMemoryContext, sizeof(AnkusError));
            AnkusNativeCallbackContext context = { error,
                IsTransactionState() && !ankus_worker_restore_in_progress() ? ankus_spi_execute : NULL,
                &memory, ankus_read_guc, ankus_initialization_log };
            volatile bool entered = false;
            ankus_native_callback_depth++;
            PG_TRY();
            {
                ankus_fork_host_enter();
                entered = true;
                int status;
                ANKUS_MANAGED_INVOKE(status, error, callback(arguments, count, result, result_size, &context));
                if (status != 0)
                {
                    ankus_raise_error(error);
                }
            }
            PG_FINALLY();
            {
                ankus_native_callback_depth--;
                ANKUS_FINALLY_RESTORE(caller_identity != 0 && ankus_memory_context_by_id(caller_identity) == NULL
                    ? recovery : caller);
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
