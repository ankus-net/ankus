using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Emits immediate native registration before PostgreSQL finishes restoring worker state.
/// </summary>
internal static class PgModuleLoadEmitter
{
    /// <summary>
    /// Shares phase and worker-restoration state between initialization and native callbacks.
    /// </summary>
    internal const string State = """
        #include "miscadmin.h"
        #if PG_VERSION_NUM < 180000
        #include "access/parallel.h"
        #endif

        static bool ankus_module_loading = false;

        static bool
        ankus_worker_restore_in_progress(void)
        {
        #if PG_VERSION_NUM < 180000
            return InitializingParallelWorker;
        #else
            return false;
        #endif
        }
        """;

    /// <summary>
    /// Emits an independently retryable registration phase without enabling fork support prematurely.
    /// </summary>
    /// <param name="callback">Its assembly-specific native symbol.</param>
    /// <param name="native">The native library source.</param>
    internal static void Emit(string callback, StringBuilder native)
    {
        native.AppendLine($$"""
            #include "utils/memutils.h"
            #include "utils/snapmgr.h"

            extern int {{callback}}(AnkusError *, AnkusGucReadBinding, AnkusExecute, AnkusInitializationLog, AnkusMemoryApi *);
            static bool ankus_module_loaded = false;

            static void
            ankus_ensure_module_loaded(void)
            {
                if (ankus_module_loading)
                {
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                        errmsg("Ankus extension module registration is already in progress")));
                }

                if (ankus_module_loaded)
                {
                    return;
                }

                MemoryContext caller = CurrentMemoryContext;
                AnkusMemoryApi memory = {0};
                ankus_memory_initialize(&memory);
                AnkusError *error = MemoryContextAllocZero(caller, sizeof(AnkusError));
                volatile bool snapshot_owned = false;
                bool transactional = IsTransactionState() && !ankus_worker_restore_in_progress();
                ankus_module_loading = true;
                PG_TRY();
                {
                    if (transactional && !ActiveSnapshotSet())
                    {
                        PushActiveSnapshot(GetTransactionSnapshot());
                        snapshot_owned = true;
                    }

                    int status;
                    ANKUS_MANAGED_INVOKE(status, error, {{callback}}(error, ankus_read_guc,
                        transactional ? ankus_spi_execute : NULL, ankus_initialization_log, &memory));
                    if (snapshot_owned)
                    {
                        snapshot_owned = false;
                        PopActiveSnapshot();
                    }

                    if (status != 0)
                    {
                        ankus_raise_error(error);
                    }

                    ankus_module_loaded = true;
                    ankus_module_loading = false;
                }
                PG_CATCH();
                {
                    ankus_module_loading = false;
                    MemoryContext propagating = MemoryContextSwitchTo(caller);
                    if (snapshot_owned)
                    {
                        snapshot_owned = false;
                        PopActiveSnapshot();
                    }

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

            """);
    }
}
