namespace Ankus.Generators;

/// <summary>
/// Quotes SQL identifiers and literals through the memory bridge, for code that runs outside a SQL callback, such as a
/// background worker between transactions.
/// </summary>
internal static class NativeQuoteBridge
{
    /// <summary>
    /// Implements operation 46: flags 1 quotes an identifier, 2 a qualified identifier and 3 a literal.
    /// </summary>
    /// <remarks>
    /// Text crosses as UTF-8 with explicit lengths and is converted to and from the database encoding. Allocations go
    /// to a context reset on each call, because outside a transaction the current context lives for the whole process.
    /// </remarks>
    internal const string Source = """

        static MemoryContext ankus_quote_context = NULL;

        static void
        ankus_memory_quote(AnkusMemoryRequest *request, AnkusMemoryResult *result)
        {
            MemoryContext previous;
            const char *first;
            const char *quoted;
            char *utf8;
            if (request->pointer == 0 || request->length > (uintptr_t) INT_MAX || request->value < 0 || request->value > INT_MAX ||
                request->flags < 1 || request->flags > 3)
                ereport(ERROR, (errcode(ERRCODE_INVALID_PARAMETER_VALUE), errmsg("invalid quoting request")));
            if (ankus_quote_context == NULL)
                ankus_quote_context = AllocSetContextCreate(TopMemoryContext, "Ankus quoting", ALLOCSET_SMALL_SIZES);
            MemoryContextReset(ankus_quote_context);
            previous = MemoryContextSwitchTo(ankus_quote_context);
            first = pg_any_to_server((const char *) request->pointer, (int) request->length, PG_UTF8);
            if (request->flags == 1)
                quoted = quote_identifier(first);
            else if (request->flags == 2)
                quoted = quote_qualified_identifier(request->data == 0 ? NULL :
                    pg_any_to_server((const char *) request->data, (int) request->value, PG_UTF8), first);
            else
                quoted = quote_literal_cstr(first);
            utf8 = pg_server_to_any(quoted, strlen(quoted), PG_UTF8);
            MemoryContextSwitchTo(previous);
            result->data = (intptr_t) utf8;
            result->length = (uintptr_t) strlen(utf8);
        }

        """;
}
