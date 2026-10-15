namespace Ankus.Generators;

/// <summary>
/// Adapts PostgreSQL event trigger invocations to owned managed metadata and guarded backend calls.
/// </summary>
internal static class NativeEventTriggerBridge
{
    /// <summary>
    /// Gets the event trigger protocol boundary, context transport, and callback cleanup.
    /// </summary>
    internal const string Source = """
        #include "commands/event_trigger.h"
        #include "tcop/cmdtag.h"

        typedef int (*AnkusEventTriggerCallback)(const AnkusValue *, AnkusValue *, AnkusError *, AnkusExecute, AnkusMemoryApi *);

        static Datum
        ankus_event_trigger_call(FunctionCallInfo fcinfo, AnkusEventTriggerCallback callback)
        {
            EventTriggerData *event;
            AnkusTriggerScope *previous_trigger = ankus_trigger_scope;
            Oid previous_function = ankus_function_oid;
            MemoryContext caller = CurrentMemoryContext;
            MemoryContext context;
            AnkusValue arguments[2] = {0};
            AnkusValue *result;
            AnkusError *error;
            if (!CALLED_AS_EVENT_TRIGGER(fcinfo))
                ereport(ERROR, (errcode(ERRCODE_E_R_I_E_EVENT_TRIGGER_PROTOCOL_VIOLATED),
                    errmsg("Ankus event trigger functions can only be called by an event trigger")));
            if (PG_NARGS() != 0)
                ereport(ERROR, (errcode(ERRCODE_E_R_I_E_EVENT_TRIGGER_PROTOCOL_VIOLATED),
                    errmsg("Ankus event trigger functions do not accept SQL arguments")));
            event = (EventTriggerData *) fcinfo->context;
            context = AllocSetContextCreate(caller, "Ankus event trigger callback", ALLOCSET_SMALL_SIZES);
            /* Cleanup reads these after ERROR/longjmp. Keep mutable values out of
             * nonvolatile automatic storage across PG_TRY's sigsetjmp. */
            result = MemoryContextAllocZero(context, sizeof(AnkusValue));
            error = MemoryContextAllocZero(context, sizeof(AnkusError));
            /* A nested DDL callback has no row-trigger query environment of its own. */
            ankus_trigger_scope = NULL;
            ankus_function_oid = fcinfo->flinfo->fn_oid;
            AnkusMemoryApi memory = {0};
            PG_TRY();
            {
                const char *texts[2] = {event->event, GetCommandTagName(event->tag)};
                int status;
                MemoryContextSwitchTo(context);
                ankus_memory_initialize(&memory);
                for (int index = 0; index < 2; index++)
                {
                    char *utf8 = pg_server_to_any(texts[index], strlen(texts[index]), PG_UTF8);
                    arguments[index].data = (unsigned char *) utf8;
                    arguments[index].length = strlen(utf8);
                }

                ANKUS_MANAGED_INVOKE(status, error, callback(arguments, result, error, ankus_spi_execute, &memory));
                if (status != 0)
                    ankus_raise_error(error);
            }
            PG_FINALLY();
            {
                ankus_trigger_scope = previous_trigger;
                ankus_function_oid = previous_function;
                ANKUS_FINALLY_RESTORE(caller);
                if (result->release != NULL)
                    result->release(result->data);
                ankus_release_error(error);
                MemoryContextDelete(context);
            }
            PG_END_TRY();
            /* EventTriggerInvoke ignores the result; event triggers do not return SQL NULL. */
            fcinfo->isnull = false;
            return (Datum) 0;
        }

        """;
}
