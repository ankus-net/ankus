using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Emits PostgreSQL library initialization and its managed exception boundary.
/// </summary>
internal static class PgInitializeEmitter
{
    /// <summary>
    /// Appends the managed callback, native loader entry point, and export without declaring a SQL function.
    /// </summary>
    /// <param name="declaration">The optional cached initialization dispatcher.</param>
    /// <param name="callback">The assembly-specific managed initialization symbol.</param>
    /// <param name="hasHooks">Whether configuration registration can enter managed hooks.</param>
    /// <param name="registration">Native setting registration followed by prefix checking statements.</param>
    /// <param name="managed">The managed dispatch source.</param>
    /// <param name="native">The native library source.</param>
    /// <param name="exports">The native linker exports.</param>
    /// <param name="hasNativeCallbacks">Whether static native callbacks require initialization and fork support.</param>
    /// <param name="hasModuleLoad">Whether an immediate module registration callback precedes initialization.</param>
    internal static void Emit(LifecycleEmission? declaration, string callback, bool hasHooks, string registration,
        GeneratorSourceBuilder managed, GeneratorSourceBuilder native, GeneratorSourceBuilder exports, bool hasNativeCallbacks = false, bool hasModuleLoad = false)
    {
        bool requiresEnsure = declaration is not null || hasHooks || hasNativeCallbacks || hasModuleLoad;
        bool warmRuntime = declaration is null && requiresEnsure && !hasModuleLoad;
        if (declaration is not null)
        {
            managed.Append(declaration.Managed);
        }
        else if (warmRuntime)
        {
            // Native AOT initializes the runtime on the first managed entry, before fork support is enabled.
            managed.AppendLine($$"""
                    [global::System.Runtime.InteropServices.UnmanagedCallersOnly(
                        EntryPoint = "{{callback}}", CallConvs = [typeof(global::System.Runtime.CompilerServices.CallConvCdecl)])]
                    private static void {{callback}}()
                    {
                    }
                """);
        }

        string forkDeclaration = requiresEnsure ? """
            #ifndef WIN32
            extern int32_t RhEnableForkSupport(void);
            #endif
            """ : string.Empty;
        string forkEnable = requiresEnsure ? """
                    #ifndef WIN32
                    if (IsPostmasterEnvironment && !IsUnderPostmaster)
                    {
                        int32_t fork_status = RhEnableForkSupport();
                        if (fork_status != 1)
                        {
                            ereport(ERROR, (errcode(ERRCODE_INTERNAL_ERROR),
                                errmsg("Ankus runtime fork support failed: %d", fork_status)));
                        }
                    }
                    #endif

            """ : string.Empty;
        string ensureDeclaration = requiresEnsure && !hasHooks ? "static void ankus_ensure_initialized(void);\n" : string.Empty;
        string registrationDeclaration = hasHooks ? string.Empty : "static bool ankus_registration_complete = false;";
        string errorDeclaration = declaration is null ? string.Empty : """
                AnkusMemoryApi memory = {0};
                ankus_memory_initialize(&memory);
                AnkusError *error = MemoryContextAllocZero(caller, sizeof(AnkusError));
                volatile bool snapshot_owned = false;
            """;
        string invocation = declaration is null ? (warmRuntime ? $"        {callback}();\n" : string.Empty) : $$"""
                    if (IsTransactionState() && !ActiveSnapshotSet())
                    {
                        PushActiveSnapshot(GetTransactionSnapshot());
                        snapshot_owned = true;
                    }

                    int status;
                    ANKUS_MANAGED_INVOKE(status, error, {{callback}}(error, ankus_read_guc,
                        IsTransactionState() ? ankus_spi_execute : NULL, ankus_initialization_log, &memory));
                    if (snapshot_owned)
                    {
                        snapshot_owned = false;
                        PopActiveSnapshot();
                    }

                    if (status != 0)
                    {
                        ankus_initialization_state = 0;
                        ankus_raise_error(error);
                    }

            """;
        string errorCleanup = declaration is null ? string.Empty : """
                    if (snapshot_owned)
                    {
                        snapshot_owned = false;
                        PopActiveSnapshot();
                    }

                    ankus_release_error(error);
                    pfree(error);
            """;
        string cleanup = declaration is null ? string.Empty : """
                ankus_release_error(error);
                pfree(error);
            """;
        native.AppendLine($$"""
            #include "miscadmin.h"
            #include "utils/memutils.h"
            #include "utils/snapmgr.h"

            {{(declaration is null ? (warmRuntime ? $"extern void {callback}(void);" : string.Empty) : $"extern int {callback}(AnkusError *, AnkusGucReadBinding, AnkusExecute, AnkusInitializationLog, AnkusMemoryApi *);")}}
            {{forkDeclaration}}
            static int ankus_initialization_state = 0;
            {{registrationDeclaration}}
            {{ensureDeclaration}}

            PGDLLEXPORT void _PG_init(void);
            PGDLLEXPORT void _PG_init(void)
            {
                ankus_signal_routing_initialize();
                if (ankus_initialization_state == 1{{(requiresEnsure ? " || ankus_module_loading" : string.Empty)}})
                {
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                        errmsg("Ankus extension initialization is already in progress")));
                }

                if (ankus_registration_complete)
                {
            {{(hasModuleLoad ? "        ankus_ensure_module_loaded();\n" : string.Empty)}}
            {{(requiresEnsure ? "        if (!ankus_worker_restore_in_progress())\n        {\n            ankus_ensure_initialized();\n        }\n\n        return;" : "        return;")}}
                }

                MemoryContext caller = CurrentMemoryContext;
                ankus_initialization_state = 1;
                PG_TRY();
                {
            {{registration}}
                    ankus_registration_complete = true;
                    ankus_initialization_state = 0;
                }
                PG_CATCH();
                {
                    ankus_initialization_state = 0;
                    MemoryContext propagating = MemoryContextSwitchTo(caller);
                    MemoryContextSwitchTo(propagating);
                    PG_RE_THROW();
                }
                PG_END_TRY();
                MemoryContextSwitchTo(caller);
            {{(hasModuleLoad ? "    ankus_ensure_module_loaded();\n" : string.Empty)}}
            {{(requiresEnsure ? """
                if (ankus_worker_restore_in_progress())
                {
                    return;
                }

                ankus_ensure_initialized();
            """ : "    ankus_initialization_state = 2;")}}
            }

            {{(requiresEnsure ? $$"""
            static void
            ankus_ensure_initialized(void)
            {
            {{(hasModuleLoad ? "    ankus_ensure_module_loaded();\n" : string.Empty)}}
                if (ankus_initialization_state == 1 || ankus_module_loading)
                {
                    ereport(ERROR, (errcode(ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE),
                        errmsg("Ankus extension initialization is already in progress")));
                }

                if (ankus_initialization_state == 2)
                {
                    return;
                }

                MemoryContext caller = CurrentMemoryContext;
            {{errorDeclaration}}
                ankus_initialization_state = 1;
                PG_TRY();
                {
            {{(hasHooks ? "        ankus_guc_complete_worker_restore();\n" : string.Empty)}}
            {{invocation}}
            {{forkEnable}}
                    ankus_initialization_state = 2;
                }
                PG_CATCH();
                {
                    ankus_initialization_state = 0;
                    MemoryContext propagating = MemoryContextSwitchTo(caller);
            {{errorCleanup}}
                    MemoryContextSwitchTo(propagating);
                    PG_RE_THROW();
                }
                PG_END_TRY();
                MemoryContextSwitchTo(caller);
            {{cleanup}}
            }

            """ : string.Empty)}}
            """);
        exports.AppendLine("_PG_init");
    }

    /// <summary>
    /// Emits the common managed error and capability boundary for one initialization phase.
    /// </summary>
    /// <param name="target">The fully qualified managed invocation target.</param>
    /// <param name="callback">Its native symbol.</param>
    /// <param name="managed">The managed dispatch source.</param>
    internal static void EmitManaged(string target, string callback, StringBuilder managed)
    {
        managed.AppendLine($$"""
                    [global::System.Runtime.InteropServices.UnmanagedCallersOnly(
                        EntryPoint = "{{callback}}",
                        CallConvs = new[] { typeof(global::System.Runtime.CompilerServices.CallConvCdecl) })]
                    private static int {{callback}}(global::Ankus.CompilerServices.NativeCallError* error, nint read, nint execute, nint log, nint memory)
                    {
                        nint previousBackend = global::Ankus.CompilerServices.NativeBackend.Enter(execute);
                        nint previousRead = global::Ankus.CompilerServices.NativeGuc.Enter(read);
                        nint previousLog = global::Ankus.CompilerServices.NativeLog.Enter(log);
                        nint previousMemory = 0;
                        bool memoryEntered = false;
                        try
                        {
                            previousMemory = global::Ankus.CompilerServices.NativeMemoryContext.Enter(memory);
                            memoryEntered = true;
                            {{target}}();
                            return 0;
                        }
                        catch (global::System.Exception exception)
                        {
                            global::Ankus.CompilerServices.NativeError.Write(exception, error);
                            return 1;
                        }
                        finally
                        {
                            if (memoryEntered)
                            {
                                global::Ankus.CompilerServices.NativeMemoryContext.Exit(previousMemory);
                            }

                            global::Ankus.CompilerServices.NativeLog.Exit(previousLog);
                            global::Ankus.CompilerServices.NativeGuc.Exit(previousRead);
                            global::Ankus.CompilerServices.NativeBackend.Exit(previousBackend);
                        }
                    }

                """);
    }
}
