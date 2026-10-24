namespace Ankus.Generators;

/// <summary>
/// Transports complete PostgreSQL diagnostics with explicit buffer ownership and native-only error reporting.
/// </summary>
internal static class NativeErrorBridge
{
    /// <summary>
    /// Gets diagnostic declarations and allocator-matched cleanup shared with configuration hooks.
    /// </summary>
    internal const string Declarations = """
        #include "utils/memutils.h"
        #include "miscadmin.h"
        #include "tcop/dest.h"
        #include "access/xact.h"
        #include "catalog/namespace.h"
        #include "lib/stringinfo.h"
        #include "mb/pg_wchar.h"
        #include "storage/ipc.h"
        #if PG_VERSION_NUM < 140000
        #include "postmaster/postmaster.h"
        #include "tcop/tcopprot.h"
        #include "utils/guc.h"
        #endif

        enum AnkusDiagnosticField
        {
            ANKUS_ERROR_MESSAGE, ANKUS_ERROR_DETAIL, ANKUS_ERROR_HINT, ANKUS_ERROR_CONTEXT,
            ANKUS_ERROR_SCHEMA, ANKUS_ERROR_TABLE, ANKUS_ERROR_COLUMN, ANKUS_ERROR_DATATYPE,
            ANKUS_ERROR_CONSTRAINT, ANKUS_ERROR_QUERY, ANKUS_ERROR_FILE, ANKUS_ERROR_ROUTINE,
            ANKUS_ERROR_DETAIL_LOG, ANKUS_ERROR_BACKTRACE, ANKUS_ERROR_DOMAIN, ANKUS_ERROR_FIELD_COUNT
        };

        enum AnkusErrorFlags
        {
            ANKUS_ERROR_RETHROW = 1, ANKUS_ERROR_SERVER = 2, ANKUS_ERROR_CLIENT = 4,
            ANKUS_ERROR_HIDE_STATEMENT = 8, ANKUS_ERROR_HIDE_CONTEXT = 16,
            ANKUS_ERROR_INCOMPLETE = 32, ANKUS_ERROR_SHOW_FUNCTION = 64, ANKUS_ERROR_UNRECOVERED = 128
        };

        typedef struct AnkusError
        {
            int sqlstate;
            char message[2048];
            int position;
            int internal_position;
            int line;
            int flags;
            AnkusValue fields[ANKUS_ERROR_FIELD_COUNT];
            int report_level;
        } AnkusError;

        static void
        ankus_release_error(AnkusError *error)
        {
            for (int index = 0; index < ANKUS_ERROR_FIELD_COUNT; index++)
            {
                AnkusValue *value = &error->fields[index];
                if (value->release != NULL)
                {
                    value->release(value->data);
                }

                memset(value, 0, sizeof(AnkusValue));
            }
        }

        /* Clears an error that is read only after a failure writes it. Readers treat the message
         * as a C string, so an empty first byte stands for the whole buffer; zeroing all of it on
         * every call cost more than the rest of a scalar call boundary. A macro, so extensions
         * without a caller do not define an unused function. */
        #define ankus_error_clear(error) \
            do \
            { \
                (error)->sqlstate = 0; \
                (error)->message[0] = '\0'; \
                (error)->position = 0; \
                (error)->internal_position = 0; \
                (error)->line = 0; \
                (error)->flags = 0; \
                memset((error)->fields, 0, sizeof((error)->fields)); \
                (error)->report_level = 0; \
            } while (0)

        static void
        ankus_free_error_buffer(void *data)
        {
            free(data);
        }

        static ErrorData *
        ankus_copy_error_data(void)
        {
            ErrorData *data = CopyErrorData();
        #if PG_VERSION_NUM < 170000
            /* Older PostgreSQL releases leave these pointers in ErrorContext or their original owner. */
            if (data->filename != NULL) data->filename = pstrdup(data->filename);
            if (data->funcname != NULL) data->funcname = pstrdup(data->funcname);
            if (data->domain != NULL) data->domain = pstrdup(data->domain);
            if (data->context_domain != NULL) data->context_domain = pstrdup(data->context_domain);
            if (data->message_id != NULL) data->message_id = pstrdup(data->message_id);
        #endif
            return data;
        }

        static void
        ankus_free_error_data(ErrorData *data)
        {
            /* CopyErrorData owns these strings, but FreeErrorData treats them as constant. */
            const char *fields[] = {
                data->filename, data->funcname, data->domain, data->context_domain, data->message_id
            };
            for (int index = 0; index < 5; index++)
            {
                if (fields[index] != NULL)
                    pfree((void *) fields[index]);
            }

            data->filename = NULL;
            data->funcname = NULL;
            data->domain = NULL;
            data->context_domain = NULL;
            data->message_id = NULL;
            FreeErrorData(data);
        }

        /* An error PostgreSQL is still handling while Ankus code runs. */
        typedef struct AnkusPendingError
        {
            MemoryContext context;
            ErrorData *data;
            MemoryContextCallback cleanup;
        } AnkusPendingError;

        static void
        ankus_pending_error_delete(void *argument)
        {
            MemoryContextDelete((MemoryContext) argument);
        }

        static void ankus_free_error_report(void *context);

        /* Holds the probe's own error, so flushing it never resets the real ErrorContext. */
        static MemoryContext ankus_error_probe_context;

        /* Releases what finished reports left in ErrorContext once no error is pending, as
         * PostgreSQL 19 does after an outermost report, so later calls take the empty-context
         * fast path. Memory that a caller still uses there, a child context or a reset callback
         * other than Ankus's own report cleanup, keeps the context as it is. */
        static void
        ankus_release_finished_reports(MemoryContext caller)
        {
            if (caller == ErrorContext || ErrorContext->firstchild != NULL)
                return;

            for (MemoryContextCallback *callback = ErrorContext->reset_cbs; callback != NULL; callback = callback->next)
            {
                if (callback->func != ankus_free_error_report && callback->func != ankus_pending_error_delete)
                    return;
            }

            MemoryContextReset(ErrorContext);
        }

        /* A caller's PG_CATCH may clean up before PG_RE_THROW, as logical replication's
         * apply worker does with AbortOutOfAnyTransaction, so Ankus callbacks can run while
         * its error is still on PostgreSQL's error stack. FlushErrorState empties the whole
         * stack, and PostgreSQL offers no way to remove only the newest entry. A guarded
         * call that catches its own error there would discard the caller's, and the caller's
         * re-throw would report "errstart was not called". Copy such an error first, so it
         * can be pushed back once the guarded failure has been flushed. */
        static AnkusPendingError *
        ankus_pending_error_save(void)
        {
            /* Every flush resets ErrorContext, and a pending error keeps its text there, so an
             * empty ErrorContext proves that none is pending without raising a probe error. An
             * ERROR inside a critical section, with ExitOnAnyError or during process exit
             * would escalate, and none may be raised while ErrorContext itself is being reset,
             * so those callers are left unprotected. */
            if (ankus_memory_error_cleanup || MemoryContextIsEmpty(ErrorContext) || CritSectionCount > 0 ||
                ExitOnAnyError || proc_exit_inprogress)
                return NULL;

            MemoryContext caller = CurrentMemoryContext;
            MemoryContext error_context = ErrorContext;
            uint32 interrupt_holdoff = InterruptHoldoffCount;
            uint32 cancel_holdoff = QueryCancelHoldoffCount;
            MemoryContext volatile context = NULL;
            AnkusPendingError *volatile pending = NULL;
            ErrorData *volatile data = NULL;
            PG_TRY();
            {
                if (ankus_error_probe_context == NULL)
                    ankus_error_probe_context = AllocSetContextCreate(TopMemoryContext, "Ankus error probe", ALLOCSET_SMALL_SIZES);
                /* Errors raised from here on, the probe's own included, are built in the probe
                 * context. Flushing them then resets only that context, and a pending error's
                 * text and any memory a caller keeps beneath ErrorContext stay intact. */
                ErrorContext = ankus_error_probe_context;
                context = AllocSetContextCreate(TopMemoryContext, "Ankus pending error", ALLOCSET_SMALL_SIZES);
                MemoryContextSwitchTo(context);
                pending = palloc0(sizeof(AnkusPendingError));
                /* With no pending error, CopyErrorData raises "errstart was not called". Its
                 * report never runs, so the caller's context callbacks need not format one. */
                error_context_stack = NULL;
                data = ankus_copy_error_data();
            }
            PG_CATCH();
            {
                FlushErrorState();
                data = NULL;
            }
            PG_END_TRY();
            ErrorContext = error_context;
            /* A caught ERROR clears the holdoff counts that the caller, such as transaction
             * abort, still relies on. */
            InterruptHoldoffCount = interrupt_holdoff;
            QueryCancelHoldoffCount = cancel_holdoff;
            MemoryContextSwitchTo(caller);
            if (data == NULL)
            {
                if (context != NULL)
                    MemoryContextDelete(context);
                ankus_release_finished_reports(caller);
                return NULL;
            }

            /* ReThrowError restores only ERROR. A lower level is mid-report in a log hook, and
             * a terminal level is already ending the process. */
            if (data->elevel != ERROR)
            {
                MemoryContextDelete(context);
                return NULL;
            }

            pending->context = context;
            pending->data = data;
            return pending;
        }

        /* Pushes a saved error back after the guarded failure that caught it was flushed. */
        static void
        ankus_pending_error_restore(AnkusPendingError *pending)
        {
            if (pending == NULL)
                return;

            MemoryContext caller = CurrentMemoryContext;
            PG_TRY();
            {
                /* ReThrowError pushes the copy as the newest stack entry and long-jumps here. */
                ReThrowError(pending->data);
            }
            PG_CATCH();
            {
                /* The caller's error is pending again, exactly as it was copied. */
            }
            PG_END_TRY();
            MemoryContextSwitchTo(caller);
            /* The restored entry keeps pointers to the copy's file, function and domain names,
             * which ReThrowError treats as constants, so the copy lives until the caller's
             * handler flushes the error. */
            pending->cleanup.func = ankus_pending_error_delete;
            pending->cleanup.arg = pending->context;
            MemoryContextRegisterResetCallback(ErrorContext, &pending->cleanup);
        }

        /* Discards a saved error after a guarded call that left the error stack untouched. */
        static void
        ankus_pending_error_release(AnkusPendingError *pending)
        {
            if (pending != NULL)
                MemoryContextDelete(pending->context);
        }

        """;

    /// <summary>
    /// Gets diagnostic text conversion that never raises, including outside a transaction.
    /// </summary>
    internal const string Conversion = """
        /* Diagnostics cross the managed boundary as UTF-8. PostgreSQL's general
         * conversions look up their function in the catalog, so they raise outside a
         * transaction. These conversions reuse functions cached while a transaction
         * was available, as the backend does for its client encoding. Exact
         * conversions raise PostgreSQL's own untranslatable-character error, as
         * ordinary reports always have. Diagnostic capture and terminal reports must
         * never raise: PostgreSQL 14 and later convert them with noError, and each byte
         * that cannot be converted is written as a \xNN escape. ASCII and
         * same-encoding text is returned unchanged. */
        static int ankus_diagnostic_encoding = -1;
        static FmgrInfo ankus_diagnostic_to_utf8;
        static FmgrInfo ankus_diagnostic_from_utf8;

        static bool
        ankus_diagnostic_ascii(const char *text, int length)
        {
            for (int index = 0; index < length; index++)
            {
                if (IS_HIGHBIT_SET(text[index]))
                {
                    return false;
                }
            }

            return true;
        }

        /* Looks up both directions once per backend while catalog access is available. */
        static void
        ankus_prepare_diagnostic_conversion(void)
        {
            int encoding = GetDatabaseEncoding();
            Oid to_utf8;
            Oid from_utf8;
            if (ankus_diagnostic_encoding == encoding || encoding == PG_UTF8 || encoding == PG_SQL_ASCII ||
                !IsTransactionState())
            {
                return;
            }

            to_utf8 = FindDefaultConversionProc(encoding, PG_UTF8);
            from_utf8 = FindDefaultConversionProc(PG_UTF8, encoding);
            if (OidIsValid(to_utf8) && OidIsValid(from_utf8))
            {
                fmgr_info_cxt(to_utf8, &ankus_diagnostic_to_utf8, TopMemoryContext);
                fmgr_info_cxt(from_utf8, &ankus_diagnostic_from_utf8, TopMemoryContext);
                ankus_diagnostic_encoding = encoding;
            }
        }

        static void
        ankus_escape_diagnostic_byte(StringInfo output, unsigned char value)
        {
            static const char digits[] = "0123456789abcdef";
            appendStringInfoChar(output, '\\');
            appendStringInfoChar(output, 'x');
            appendStringInfoChar(output, digits[value >> 4]);
            appendStringInfoChar(output, digits[value & 15]);
        }

        /* Writes ASCII-escaped text into a caller buffer without allocation, for terminal fallbacks. */
        static void
        ankus_escape_diagnostic_text(const char *text, char *output, size_t size)
        {
            static const char digits[] = "0123456789abcdef";
            size_t used = 0;
            for (const unsigned char *cursor = (const unsigned char *) text; *cursor != '\0' && used + 5 <= size; cursor++)
            {
                if (IS_HIGHBIT_SET(*cursor))
                {
                    output[used++] = '\\';
                    output[used++] = 'x';
                    output[used++] = digits[*cursor >> 4];
                    output[used++] = digits[*cursor & 15];
                }
                else
                {
                    output[used++] = (char) *cursor;
                }
            }

            output[used] = '\0';
        }

        /* Returns text in the requested encoding, or a palloc'd copy when bytes changed. */
        static char *
        ankus_convert_diagnostic(const char *text, int length, bool to_utf8, bool exact)
        {
            int server = GetDatabaseEncoding();
            StringInfoData output;
            if (length <= 0 || server == PG_UTF8 || ankus_diagnostic_ascii(text, length))
            {
                return (char *) text;
            }

            if (server == PG_SQL_ASCII)
            {
                /* SQL_ASCII performs no conversion. Any bytes are valid server text;
                 * transport valid UTF-8 exactly and escape everything else. */
                if (!to_utf8 || pg_verify_mbstr(PG_UTF8, text, length, !exact))
                {
                    return (char *) text;
                }
            }
            else
            {
                ankus_prepare_diagnostic_conversion();
                if (ankus_diagnostic_encoding == server)
                {
                    FmgrInfo *function = to_utf8 ? &ankus_diagnostic_to_utf8 : &ankus_diagnostic_from_utf8;
                    int source = to_utf8 ? server : PG_UTF8;
                    int target = to_utf8 ? PG_UTF8 : server;
                    if (exact)
                    {
                        /* PostgreSQL raises its own precise error for an untranslatable character. */
                        char *result = MemoryContextAllocHuge(CurrentMemoryContext, (Size) length * MAX_CONVERSION_GROWTH + 1);
        #if PG_VERSION_NUM >= 140000
                        (void) FunctionCall6(function, Int32GetDatum(source), Int32GetDatum(target), CStringGetDatum(text),
                            CStringGetDatum(result), Int32GetDatum(length), BoolGetDatum(false));
        #else
                        (void) FunctionCall5(function, Int32GetDatum(source), Int32GetDatum(target), CStringGetDatum(text),
                            CStringGetDatum(result), Int32GetDatum(length));
        #endif
                        return result;
                    }
        #if PG_VERSION_NUM >= 140000
                    else
                    {
                        int offset = 0;
                        initStringInfo(&output);
                        while (offset < length)
                        {
                            /* Bounded chunks keep the worst-case expansion on the stack. */
                            unsigned char buffer[1024 * MAX_CONVERSION_GROWTH + 1];
                            int chunk = Min(length - offset, 1024);
                            int converted = DatumGetInt32(FunctionCall6(function, Int32GetDatum(source), Int32GetDatum(target),
                                CStringGetDatum(text + offset), CStringGetDatum((char *) buffer), Int32GetDatum(chunk),
                                BoolGetDatum(true)));
                            appendStringInfoString(&output, (char *) buffer);
                            if (converted == 0)
                            {
                                /* An invalid or untranslatable sequence stops conversion at its first byte. */
                                ankus_escape_diagnostic_byte(&output, (unsigned char) text[offset]);
                                converted = 1;
                            }

                            offset += converted;
                        }

                        return output.data;
                    }
        #endif
                }

                if (exact && !to_utf8 && !IsTransactionState())
                {
                    /* Catalog lookups need a transaction, so a report before a worker's first
                     * transaction has no cached function. PostgreSQL prepares its own UTF-8 to
                     * server conversion during backend startup for use outside transactions;
                     * convert one code point at a time, raising PostgreSQL's error for an
                     * untranslatable character. */
                    (void) pg_verify_mbstr(PG_UTF8, text, length, false);
                    initStringInfo(&output);
                    for (int offset = 0; offset < length; offset += pg_utf_mblen((const unsigned char *) text + offset))
                    {
                        unsigned char converted[MAX_UNICODE_EQUIVALENT_STRING + 1];
                        pg_unicode_to_server(utf8_to_unicode((const unsigned char *) text + offset), converted);
                        appendStringInfoString(&output, (char *) converted);
                    }

                    return output.data;
                }

                /* Without a cached function, an exact conversion keeps PostgreSQL's own
                 * behavior. PostgreSQL 13 conversions have no noError mode, so within a
                 * transaction capture keeps them too; only an untranslatable character
                 * can raise there. */
                if (exact || IsTransactionState())
                {
                    return to_utf8 ? pg_server_to_any(text, length, PG_UTF8) : pg_any_to_server(text, length, PG_UTF8);
                }
            }

            initStringInfo(&output);
            for (int index = 0; index < length; index++)
            {
                if (IS_HIGHBIT_SET(text[index]))
                {
                    ankus_escape_diagnostic_byte(&output, (unsigned char) text[index]);
                }
                else
                {
                    appendStringInfoChar(&output, text[index]);
                }
            }

            return output.data;
        }

        """;

    /// <summary>
    /// Gets severity mapping and transaction-independent PostgreSQL message filtering.
    /// </summary>
    internal const string Logging = """
        static int
        ankus_log_level(int level)
        {
            switch (level)
            {
                case 0: return DEBUG5;
                case 1: return DEBUG4;
                case 2: return DEBUG3;
                case 3: return DEBUG2;
                case 4: return DEBUG1;
                case 5: return LOG;
                case 6: return LOG_SERVER_ONLY;
                case 7: return INFO;
                case 8: return NOTICE;
                case 9: return WARNING;
                case 11: return FATAL;
                case 12: return PANIC;
                default: return ERROR;
            }
        }

        static bool
        ankus_log_enabled(int level, AnkusError *error)
        {
            error->sqlstate = 0;
        #if PG_VERSION_NUM >= 140000
            return message_level_is_interesting(level);
        #else
            /* PostgreSQL 13 predates message_level_is_interesting. Mirror errstart's routing rules. */
        #ifdef WIN32
            /* These data exports exist in PostgreSQL 13, but its headers omit
             * their DLL import annotations. Resolve their actual storage once. */
            static CommandDest *destination = NULL;
            static bool *authenticating = NULL;
            if (destination == NULL || authenticating == NULL)
            {
                HMODULE backend = GetModuleHandle(NULL);
                destination = (CommandDest *) GetProcAddress(backend, "whereToSendOutput");
                authenticating = (bool *) GetProcAddress(backend, "ClientAuthInProgress");
                if (destination == NULL || authenticating == NULL)
                {
                    /* The ordinary filter runs without a PostgreSQL error frame.
                     * Transport lookup failures without entering the error reporter. */
                    memset(error, 0, sizeof(*error));
                    error->sqlstate = ERRCODE_INTERNAL_ERROR;
                    strlcpy(error->message, "could not resolve PostgreSQL message-routing state", sizeof(error->message));
                    return false;
                }
            }
        #else
            CommandDest *destination = &whereToSendOutput;
            bool *authenticating = &ClientAuthInProgress;
        #endif
            bool server = (level == LOG || level == LOG_SERVER_ONLY)
                ? (log_min_messages == LOG || log_min_messages <= ERROR)
                : (log_min_messages == LOG ? level >= FATAL : level >= log_min_messages);
            bool client = *destination == DestRemote && level != LOG_SERVER_ONLY &&
                (*authenticating ? level >= ERROR : level >= client_min_messages || level == INFO);
            return level >= ERROR || server || client;
        #endif
        }

        """;

    /// <summary>
    /// Gets the managed error contexts that <c>PgErrorContext</c> pushes, and the callback that reports them.
    /// </summary>
    /// <remarks>
    /// <c>PG_END_TRY</c> restores <c>error_context_stack</c> even on success, so a guarded operation cannot leave a
    /// callback on it. The contexts live in a list instead, and each guarded entry into PostgreSQL puts one callback for
    /// all of them on the stack while it runs, as a C extension's callback is active while its code runs.
    /// </remarks>
    internal const string ManagedContexts = """
        typedef struct AnkusErrorContextEntry
        {
            struct AnkusErrorContextEntry *next;
            char *text;
        } AnkusErrorContextEntry;

        /* Innermost first. */
        static AnkusErrorContextEntry *ankus_error_contexts = NULL;

        static inline void
        ankus_error_context_report(void *argument)
        {
            (void) argument;
            for (AnkusErrorContextEntry *entry = ankus_error_contexts; entry != NULL; entry = entry->next)
                errcontext("%s", entry->text);
        }

        /* Puts the contexts' callback on the stack for one guarded entry, unless an enclosing entry already did. */
        static inline bool
        ankus_error_context_enter(ErrorContextCallback *frame)
        {
            if (ankus_error_contexts == NULL)
                return false;
            for (ErrorContextCallback *callback = error_context_stack; callback != NULL; callback = callback->previous)
            {
                if (callback->callback == ankus_error_context_report)
                    return false;
            }

            frame->callback = ankus_error_context_report;
            frame->arg = NULL;
            frame->previous = error_context_stack;
            error_context_stack = frame;
            return true;
        }

        static inline void
        ankus_error_context_exit(ErrorContextCallback *frame, bool entered)
        {
            if (entered && error_context_stack == frame)
                error_context_stack = frame->previous;
        }

        /* Removes an entry wherever it is. It raises no ERROR, so it stays available during callback cleanup.
         * Inline because extensions without the memory bridge never call it. */
        static inline void
        ankus_error_context_pop(AnkusErrorContextEntry *entry)
        {
            for (AnkusErrorContextEntry **link = &ankus_error_contexts; *link != NULL; link = &(*link)->next)
            {
                if (*link == entry)
                {
                    *link = entry->next;
                    pfree(entry->text);
                    pfree(entry);
                    return;
                }
            }
        }

        """;

    /// <summary>
    /// Gets diagnostic capture, allocator-matched cleanup, and backend error reconstruction helpers.
    /// </summary>
    internal const string Source = Declarations + "\n" + Conversion + "\n" + Logging + "\n" + Capture + "\n" + Reporting + "\n" + ManagedContexts;

    /// <summary>
    /// Gets a guarded initializer logging capability that never opens a transaction or enables SPI. It is emitted once,
    /// however many bridges need it.
    /// </summary>
    internal const string InitializationLogging = """
        #ifndef ANKUS_INITIALIZATION_LOG
        #define ANKUS_INITIALIZATION_LOG 1
        typedef int (*AnkusInitializationLog)(int, int, AnkusError *, AnkusError *, int *);

        static int
        ankus_initialization_log(int operation, int level, AnkusError *report, AnkusError *error, int *enabled)
        {
            if (operation == 2)
                return ankus_recovery_terminal(level, report, error);

            if (ankus_recovery_failed(error))
            {
                if (error->report_level < 12 || (error->flags & ANKUS_ERROR_UNRECOVERED) != 0)
                    return 1;
                memset(error, 0, sizeof(*error));
            }

            /* The caller is restored after a longjmp, so it lives in memory. */
            MemoryContext volatile caller = CurrentMemoryContext;
            MemoryContext recovery = ankus_error_recovery_context(caller);
            uint32 interrupt_holdoff = InterruptHoldoffCount;
            uint32 shared_held_before = ankus_shared_held_count;
            uint32 cancel_holdoff = QueryCancelHoldoffCount;
            volatile int status = 0;
            if (operation == 1 && level >= 0 && level < 10)
            {
                HOLD_INTERRUPTS();
            }

            /* A report can fail without raising, so status alone does not say whether the stack was flushed. */
            volatile bool caught = false;
            AnkusPendingError *volatile pending = ankus_pending_error_save();
            PG_TRY();
            {
                ANKUS_PG_TRY_INNER();
                {
                    if ((operation != 0 && operation != 1) || level < 0 || level > 12 || enabled == NULL)
                    {
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                            errmsg("Invalid initializer logging request")));
                    }

                    if (operation == 1 && (level >= 10 || report == NULL))
                    {
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE),
                            errmsg("Terminal initializer messages must unwind managed code before reporting")));
                    }

                    *enabled = ankus_log_enabled(ankus_log_level(level), error) ? 1 : 0;
                    status = error->sqlstate != 0;
                    if (status == 0 && operation == 1 && *enabled != 0)
                    {
                        /* The shared reporter owns conversion scratch independently of the caller.
                         * It returns to a surviving context even when reporting resets ErrorContext. */
                        ankus_report(report, ankus_log_level(level));
                    }
                }
                ANKUS_PG_CATCH_INNER();
                {
                    ErrorData *data;
                    MemoryContext diagnostic;
                    MemoryContextSwitchTo(recovery);
                    diagnostic = AllocSetContextCreate(TopMemoryContext, "Ankus initializer diagnostics", ALLOCSET_SMALL_SIZES);
                    MemoryContextSwitchTo(diagnostic);
                    data = ankus_copy_error_data();
                    FlushErrorState();
                    ankus_capture_error(data, error);
                    ankus_recovery_record(error, false);
                    ankus_free_error_data(data);
                    MemoryContextSwitchTo(recovery);
                    MemoryContextDelete(diagnostic);
                    status = 1;
                    caught = true;
                }
                ANKUS_PG_END_TRY_INNER();
            }
            PG_CATCH();
            {
                /* Diagnostic recovery also stays inside native frames. A second failure is terminal. */
                MemoryContextSwitchTo(recovery);
                FlushErrorState();
                ereport(FATAL, (errmsg("Unable to recover an Ankus initializer logging failure")));
            }
            PG_END_TRY();
            if (caught)
                ankus_pending_error_restore(pending);
            else
                ankus_pending_error_release(pending);
            InterruptHoldoffCount = ankus_shared_restore_interrupts(interrupt_holdoff, shared_held_before);
            QueryCancelHoldoffCount = cancel_holdoff;
            return status;
        }
        #endif

        """;

    /// <summary>
    /// Gets owned diagnostic capture without adding the report dispatcher.
    /// </summary>
    internal const string Capture = """
        static void
        ankus_capture_error(ErrorData *data, AnkusError *error)
        {
            const char *fields[ANKUS_ERROR_FIELD_COUNT] = {
                data->message, data->detail, data->hint, data->context,
                data->schema_name, data->table_name, data->column_name, data->datatype_name,
                data->constraint_name, data->internalquery, data->filename, data->funcname,
                data->detail_log, data->backtrace, data->domain
            };
            error->sqlstate = data->sqlerrcode;
            error->position = data->cursorpos;
            error->internal_position = data->internalpos;
            error->line = data->lineno;
            error->flags = ANKUS_ERROR_RETHROW |
                (data->output_to_server ? ANKUS_ERROR_SERVER : 0) |
                (data->output_to_client ? ANKUS_ERROR_CLIENT : 0) |
                (data->hide_stmt ? ANKUS_ERROR_HIDE_STATEMENT : 0) |
                (data->hide_ctx ? ANKUS_ERROR_HIDE_CONTEXT : 0);
        #if PG_VERSION_NUM < 140000
            error->flags |= data->show_funcname ? ANKUS_ERROR_SHOW_FUNCTION : 0;
        #endif
            for (int index = 0; index < ANKUS_ERROR_FIELD_COUNT; index++)
            {
                const char *text = fields[index];
                if (text != NULL)
                {
                    char *utf8 = ankus_convert_diagnostic(text, strlen(text), true, false);
                    int length = strlen(utf8);
                    AnkusValue *value = &error->fields[index];
                    if (index == ANKUS_ERROR_MESSAGE)
                    {
                        int clipped = pg_encoding_mbcliplen(PG_UTF8, utf8, length, sizeof(error->message) - 1);
                        memcpy(error->message, utf8, clipped);
                        error->message[clipped] = '\0';
                    }

                    value->data = malloc((Size) length + 1);
                    if (value->data != NULL)
                    {
                        memcpy(value->data, utf8, (Size) length + 1);
                        value->length = length;
                        value->release = ankus_free_error_buffer;
                    }
                    else
                    {
                        error->flags |= ANKUS_ERROR_INCOMPLETE;
                    }

                    if (utf8 != text)
                    {
                        pfree(utf8);
                    }
                }
            }
        }

        """;

    /// <summary>
    /// Gets native reconstruction and reporting of transported diagnostics.
    /// </summary>
    private const string Reporting = """
        static char *
        ankus_error_field(AnkusError *error, enum AnkusDiagnosticField field, bool exact)
        {
            AnkusValue *value = &error->fields[field];
            char *converted;
            char *copy;
            if (value->data == NULL)
            {
                return NULL;
            }

            converted = ankus_convert_diagnostic((char *) value->data, value->length, false, exact);
            /* Own every string before releasing the native or managed transport allocator. */
            copy = pstrdup(converted);
            if (converted != (char *) value->data)
            {
                pfree(converted);
            }

            return copy;
        }

        static void
        ankus_free_error_report(void *context)
        {
            MemoryContextDelete((MemoryContext) context);
        }

        static MemoryContext
        ankus_error_recovery_context(MemoryContext caller)
        {
            for (MemoryContext ancestor = caller; ancestor != NULL; ancestor = MemoryContextGetParent(ancestor))
            {
                if (ancestor == ErrorContext)
                {
                    return ErrorContext;
                }
            }

            return caller;
        }

        static void
        ankus_finish_error_report(AnkusError *error, MemoryContext caller, MemoryContext recovery,
            MemoryContext temporary, MemoryContextCallback *cleanup, bool reporting, bool completed)
        {
            /* PostgreSQL 19 also resets ErrorContext after an outermost nonthrowing
             * report, so an original caller below it need not have survived. */
            MemoryContextSwitchTo(!completed || PG_VERSION_NUM >= 190000 ? recovery : caller);
            if (temporary != NULL)
            {
                if (reporting && !completed)
                {
                    /* ErrorData retains source-location pointers without copying. Attach
                     * cleanup only after reporting: recursive errstart may reset ErrorContext
                     * before it consumes our fields. The next error flush releases them. */
                    MemoryContextRegisterResetCallback(ErrorContext, cleanup);
                }
                else
                {
                    MemoryContextDelete(temporary);
                }
            }

            ankus_release_error(error);
        }

        static void
        ankus_report_in_context(AnkusError *error, int level, MemoryContext caller, MemoryContext recovery)
        {
            ErrorData data = {0};
            bool rethrow = level == ERROR && (error->flags & ANKUS_ERROR_RETHROW) != 0;
            /* A terminal report must still terminate; it escapes text it cannot convert. */
            bool exact = level < FATAL;

            MemoryContext volatile temporary = NULL;
            MemoryContextCallback *volatile cleanup = NULL;
            bool volatile reporting = false;
            PG_TRY();
            {
                /* Encoding conversion and ErrorData own variable-sized, individually
                 * releasable strings even when the caller uses Slab or Bump storage. */
                temporary = AllocSetContextCreate(TopMemoryContext, "Ankus error report", ALLOCSET_SMALL_SIZES);
                MemoryContextSwitchTo(temporary);
                cleanup = palloc(sizeof(MemoryContextCallback));
                cleanup->func = ankus_free_error_report;
                cleanup->arg = temporary;
                cleanup->next = NULL;
                data.elevel = level;
                data.sqlerrcode = error->sqlstate;
                data.cursorpos = error->position;
                data.internalpos = error->internal_position;
                data.lineno = error->line;
                data.output_to_server = (error->flags & ANKUS_ERROR_SERVER) != 0;
                data.output_to_client = (error->flags & ANKUS_ERROR_CLIENT) != 0;
                data.hide_stmt = (error->flags & ANKUS_ERROR_HIDE_STATEMENT) != 0;
                data.hide_ctx = (error->flags & ANKUS_ERROR_HIDE_CONTEXT) != 0;
        #if PG_VERSION_NUM < 140000
                data.show_funcname = (error->flags & ANKUS_ERROR_SHOW_FUNCTION) != 0;
        #endif
                data.message = ankus_error_field(error, ANKUS_ERROR_MESSAGE, exact);
                if (data.message == NULL)
                {
                    data.message = pstrdup(ankus_convert_diagnostic(error->message, strlen(error->message), false, exact));
                }

                data.detail = ankus_error_field(error, ANKUS_ERROR_DETAIL, exact);
                data.hint = ankus_error_field(error, ANKUS_ERROR_HINT, exact);
                data.context = ankus_error_field(error, ANKUS_ERROR_CONTEXT, exact);
                data.schema_name = ankus_error_field(error, ANKUS_ERROR_SCHEMA, exact);
                data.table_name = ankus_error_field(error, ANKUS_ERROR_TABLE, exact);
                data.column_name = ankus_error_field(error, ANKUS_ERROR_COLUMN, exact);
                data.datatype_name = ankus_error_field(error, ANKUS_ERROR_DATATYPE, exact);
                data.constraint_name = ankus_error_field(error, ANKUS_ERROR_CONSTRAINT, exact);
                data.internalquery = ankus_error_field(error, ANKUS_ERROR_QUERY, exact);
                data.filename = ankus_error_field(error, ANKUS_ERROR_FILE, exact);
                data.funcname = ankus_error_field(error, ANKUS_ERROR_ROUTINE, exact);
                data.detail_log = ankus_error_field(error, ANKUS_ERROR_DETAIL_LOG, exact);
                data.backtrace = ankus_error_field(error, ANKUS_ERROR_BACKTRACE, exact);
                /* errstart records the message domain; NULL selects PostgreSQL's own. The string
                 * lives in the report context until the next error flush, as the other fields do. */
                data.domain = ankus_error_field(error, ANKUS_ERROR_DOMAIN, exact);
                data.filename = data.filename == NULL ? __FILE__ : data.filename;
                data.funcname = data.funcname == NULL ? "ankus_report" : data.funcname;
                data.assoc_context = CurrentMemoryContext;
                reporting = true;
                if (rethrow)
                {
                    /* Context callbacks already ran for this error; replaying them would duplicate SQL frames. */
                    ReThrowError(&data);
                }

                ThrowErrorData(&data);
            }
            PG_CATCH();
            {
                MemoryContext propagating = CurrentMemoryContext;
                ankus_finish_error_report(error, caller, recovery, temporary, cleanup, reporting, false);
                MemoryContextSwitchTo(propagating);
                PG_RE_THROW();
            }
            PG_END_TRY();
            ankus_finish_error_report(error, caller, recovery, temporary, cleanup, reporting, true);
        }

        static void
        ankus_report(AnkusError *error, int level)
        {
            MemoryContext caller = CurrentMemoryContext;
            MemoryContext recovery = ankus_error_recovery_context(caller);
            ankus_report_in_context(error, level, caller, recovery);
        }

        """;

    /// <summary>
    /// Gets error raising used after managed SQL and initialization callbacks unwind.
    /// </summary>
    internal const string RaiseError = """
        static void
        ankus_raise_error(AnkusError *error)
        {
            ankus_report(error, error->report_level == 0 ? ERROR : ankus_log_level(error->report_level - 1));
        }

        """;
}
