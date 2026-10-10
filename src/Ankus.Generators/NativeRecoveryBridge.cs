namespace Ankus.Generators;

/// <summary>
/// Retains failures that must propagate after managed callbacks return to PostgreSQL.
/// </summary>
internal static class NativeRecoveryBridge
{
    /// <summary>
    /// Gets callback frames for cancellation, terminal reports and errors requiring transaction rollback.
    /// </summary>
    internal const string Source = """
        #include "access/xact.h"

        typedef struct AnkusRecoveryFrame
        {
            struct AnkusRecoveryFrame *previous;
            AnkusError failure;
            bool failed;
        } AnkusRecoveryFrame;

        static AnkusRecoveryFrame *ankus_recovery_frame;
        static int32 ankus_recovery_failed_frames;

        /* Copy transport without entering PostgreSQL, including under a pending
         * terminal report. Each frame owns its buffers independently of rollback. */
        static void
        ankus_recovery_store(AnkusRecoveryFrame *frame, const AnkusError *error)
        {
            if (frame == NULL)
                return;

            int unrecovered = (frame->failure.flags | error->flags) & ANKUS_ERROR_UNRECOVERED;
            if (frame->failed && frame->failure.report_level >= error->report_level)
            {
                frame->failure.flags |= unrecovered;
                return;
            }

            ankus_release_error(&frame->failure);
            frame->failure = *error;
            frame->failure.flags |= unrecovered;
            for (int index = 0; index < ANKUS_ERROR_FIELD_COUNT; index++)
            {
                AnkusValue *value = &frame->failure.fields[index];
                const AnkusValue *source = &error->fields[index];
                value->data = NULL;
                value->release = NULL;
                if (source->data == NULL)
                    continue;

                value->data = malloc((size_t) source->length + 1);
                if (value->data == NULL)
                {
                    value->length = 0;
                    frame->failure.flags |= ANKUS_ERROR_INCOMPLETE;
                    continue;
                }

                memcpy(value->data, source->data, source->length);
                value->data[source->length] = 0;
                value->release = ankus_free_error_buffer;
            }

            if (!frame->failed)
                ankus_recovery_failed_frames++;
            frame->failed = true;
        }

        static bool
        ankus_parallel_without_subtransactions(void)
        {
        #if PG_VERSION_NUM < 170000
            return IsInParallelMode();
        #else
            return false;
        #endif
        }

        static bool
        ankus_recovery_failed(AnkusError *error)
        {
            for (AnkusRecoveryFrame *frame = ankus_recovery_frame; frame != NULL; frame = frame->previous)
            {
                if (!frame->failed)
                    continue;

                /* The frame owns these buffers until managed dispatch has returned.
                 * The guarded caller copies them before releasing its borrowed view. */
                *error = frame->failure;
                for (int index = 0; index < ANKUS_ERROR_FIELD_COUNT; index++)
                    error->fields[index].release = NULL;
                return true;
            }

            return false;
        }

        static void
        ankus_recovery_record(AnkusError *error, bool recovered)
        {
            if (!recovered)
                error->flags |= ANKUS_ERROR_UNRECOVERED;

            if (!recovered || error->sqlstate == ERRCODE_QUERY_CANCELED || ankus_parallel_without_subtransactions())
                ankus_recovery_store(ankus_recovery_frame, error);
        }

        static void
        ankus_recovery_pop(AnkusRecoveryFrame *frame)
        {
            ankus_recovery_frame = frame->previous;
            if (frame->failed)
                ankus_recovery_failed_frames--;
        }

        static int
        ankus_recovery_finish(AnkusRecoveryFrame *frame, AnkusError *error, int status)
        {
            ankus_recovery_pop(frame);
            if (frame->failed)
            {
                /* An explicit subtransaction may roll back ERROR, but must not
                 * turn a terminal report into a catchable ordinary error. */
                if (frame->failure.report_level >= 12)
                    ankus_recovery_store(frame->previous, &frame->failure);

                ankus_release_error(error);
                *error = frame->failure;
                memset(&frame->failure, 0, sizeof(frame->failure));
                return 1;
            }

            return status;
        }

        /* Managed entry points contain every exception. No PostgreSQL call or
         * longjmp may occur between installing this stack frame and removing it. */
        #define ANKUS_MANAGED_INVOKE(status, error, expression) \
            do \
            { \
                ANKUS_SIGNAL_ROUTING_READY(); \
                AnkusRecoveryFrame ankus_invocation = {0}; \
                ankus_invocation.previous = ankus_recovery_frame; \
                ankus_recovery_frame = &ankus_invocation; \
                (status) = (expression); \
                (status) = ankus_recovery_finish(&ankus_invocation, (error), (status)); \
            } while (0)

        """;

    /// <summary>
    /// Gets the terminal-report entry point for bridges exposing a managed logging capability.
    /// </summary>
    internal const string Terminal = """
        static int
        ankus_recovery_terminal(int level, AnkusError *report, AnkusError *error)
        {
            if (ankus_recovery_frame == NULL || report == NULL || level < 11 || level > 12)
            {
                memset(error, 0, sizeof(*error));
                error->sqlstate = ERRCODE_OBJECT_NOT_IN_PREREQUISITE_STATE;
                strlcpy(error->message, "Terminal reports require an active managed callback", sizeof(error->message));
                return 1;
            }

            report->report_level = level + 1;
            ankus_recovery_store(ankus_recovery_frame, report);
            return 0;
        }

        """;
}
