namespace Ankus.Generators;

/// <summary>
/// Pushes, updates and pops the managed error contexts behind <c>PgErrorContext</c>.
/// </summary>
internal static class NativeErrorContextBridge
{
    /// <summary>
    /// Implements operation 47: flags 1 pushes UTF-8 text and returns the entry and 2 replaces an entry's text. Flag 3,
    /// which removes an entry, runs before the guard because it must work during callback cleanup.
    /// </summary>
    /// <remarks>
    /// Entries and their text live in <c>TopMemoryContext</c>, because a background worker keeps contexts across
    /// transactions; conversion copies are freed at once, since outside a transaction the current context lasts as
    /// long as the process.
    /// </remarks>
    internal const string Source = """

        static char *
        ankus_error_context_text(AnkusMemoryRequest *request)
        {
            const char *utf8 = (const char *) request->pointer;
            char *converted;
            char *text;
            if (utf8 == NULL || request->length > (uintptr_t) INT_MAX)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid error context text")));
            converted = pg_any_to_server(utf8, (int) request->length, PG_UTF8);
            if (converted == utf8)
            {
                text = MemoryContextAlloc(TopMemoryContext, request->length + 1);
                memcpy(text, utf8, request->length);
                text[request->length] = '\0';
                return text;
            }

            text = MemoryContextStrdup(TopMemoryContext, converted);
            pfree(converted);
            return text;
        }

        static void
        ankus_memory_error_context(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            AnkusErrorContextEntry *entry = (AnkusErrorContextEntry *) request->context;
            switch (request->flags)
            {
                case 1:
                {
                    char *text = ankus_error_context_text(request);
                    entry = MemoryContextAlloc(TopMemoryContext, sizeof(AnkusErrorContextEntry));
                    entry->text = text;
                    entry->next = ankus_error_contexts;
                    ankus_error_contexts = entry;
                    result->value = (intptr_t) entry;
                    break;
                }
                case 2:
                {
                    char *text = ankus_error_context_text(request);
                    if (entry == NULL)
                        ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid error context")));
                    pfree(entry->text);
                    entry->text = text;
                    break;
                }
                default:
                    ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("unknown error context operation")));
            }
        }

        """;
}
