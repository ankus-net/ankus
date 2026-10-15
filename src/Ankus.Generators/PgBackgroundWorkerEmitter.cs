using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Emits native worker entries that unwind every managed frame before reporting a PostgreSQL error.
/// </summary>
internal static class PgBackgroundWorkerEmitter
{
    /// <summary>
    /// Renders a statically bound dispatcher and selected-header native worker export from immutable contracts.
    /// </summary>
    /// <param name="declaration">The validated detached invocation and native identities.</param>
    /// <returns>The independently cached managed, native and export artifacts.</returns>
    internal static BackgroundWorkerEmission Emit(BackgroundWorkerDeclaration declaration)
    {
        var managed = new StringBuilder();
        var native = new StringBuilder();
        var exports = new StringBuilder();
        string callback = declaration.Callback;
        managed.AppendLine($$"""
                [global::System.Runtime.InteropServices.UnmanagedCallersOnly(
                    EntryPoint = "{{callback}}", CallConvs = [typeof(global::System.Runtime.CompilerServices.CallConvCdecl)])]
                private static int {{callback}}(nuint argument, global::Ankus.CompilerServices.NativeCallError* error,
                    nint read, nint log, nint memory)
                {
                    nint previousBackend = global::Ankus.CompilerServices.NativeBackend.Enter(0);
                    nint previousRead = global::Ankus.CompilerServices.NativeGuc.Enter(read);
                    nint previousLog = global::Ankus.CompilerServices.NativeLog.Enter(log);
                    nint previousMemory = global::Ankus.CompilerServices.NativeMemoryContext.Enter(memory);
                    try
                    {
                        try
                        {
                            {{declaration.Target}}(argument);
                        }
                        finally
                        {
                            try
                            {
                                global::Ankus.CompilerServices.NativeMemoryContext.Exit(previousMemory);
                            }
                            finally
                            {
                                global::Ankus.CompilerServices.NativeLog.Exit(previousLog);
                                global::Ankus.CompilerServices.NativeGuc.Exit(previousRead);
                                global::Ankus.CompilerServices.NativeBackend.Exit(previousBackend);
                            }
                        }

                        return 0;
                    }
                    catch (global::System.Exception exception)
                    {
                        global::Ankus.CompilerServices.NativeError.Write(exception, error);
                        return 1;
                    }
                }
            """);
        native.AppendLine($$"""
            extern int {{callback}}(uintptr_t, AnkusError *, AnkusGucReadBinding, AnkusInitializationLog, AnkusMemoryApi *);
            PGDLLEXPORT void {{declaration.EntryPoint}}(Datum argument);
            PGDLLEXPORT void {{declaration.EntryPoint}}(Datum argument)
            {
                if (MyBgworkerEntry == NULL || ankus_worker_active)
                {
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                        errmsg("an Ankus worker entry requires a new PostgreSQL background-worker process")));
                }

                MemoryContext caller = CurrentMemoryContext;
                AnkusError *error = MemoryContextAllocZero(caller, sizeof(AnkusError));
                volatile bool entered = false;
                ankus_worker_active = true;
                ankus_worker_connected = false;
                ankus_worker_execute = ankus_spi_execute;
                ankus_worker_hup = ankus_worker_term = ankus_worker_int = ankus_worker_child = 0;
                PG_TRY();
                {
                    ankus_worker_attach(3);
                    ankus_ensure_initialized();
                    AnkusMemoryApi memory = {0};
                    ankus_memory_initialize(&memory);
                    ankus_fork_host_enter();
                    entered = true;
                    int status;
                    ANKUS_MANAGED_INVOKE(status, error, {{callback}}((uintptr_t) argument, error, ankus_read_guc, ankus_initialization_log, &memory));
                    if (status != 0)
                    {
                        ankus_raise_error(error);
                    }
                }
                PG_FINALLY();
                {
                    ANKUS_FINALLY_RESTORE(caller);
                    ankus_worker_active = false;
                    ankus_worker_execute = NULL;
                    ankus_release_error(error);
                    pfree(error);
                    if (entered)
                    {
                        ankus_fork_host_exit();
                    }
                }
                PG_END_TRY();
            }
            """);
        exports.AppendLine(declaration.EntryPoint);
        return new(declaration.EntryPoint, managed.ToString(), native.ToString(), exports.ToString());
    }
}
