using System.Linq;

namespace Ankus.Generators;

/// <summary>
/// Binds generated native values and calls to the extension's complete measured declaration companion.
/// </summary>
internal static class NativeBindingBridge
{
    /// <summary>
    /// Emits the unique companion identity visible to this extension's C# compilation.
    /// </summary>
    /// <param name="identity">The detached measured companion identity.</param>
    /// <returns>A native constant, empty when no unique valid binding contract is present.</returns>
    internal static string Binding(string? identity)
    {
        if (identity is null || identity.Length != 64 || !identity.All(static value => value is >= '0' and <= '9' or >= 'A' and <= 'F'))
        {
            identity = "";
        }

        return "static const char ankus_binding_identity[] = \"" + identity + "\";\n";
    }

    /// <summary>
    /// Validates the complete native declaration identity and server major without invoking PostgreSQL.
    /// </summary>
    internal const string Source = """
        static int
        ankus_memory_native_binding(AnkusMemoryRequest *request, AnkusError *error)
        {
            const char *message = NULL;
            if (sizeof(ankus_binding_identity) == 1)
            {
                message = "this extension has no unique generated PostgreSQL binding contract";
            }
            else if (request->value != PG_VERSION_NUM / 10000 ||
                request->length != sizeof(ankus_binding_identity) - 1 || request->data == 0 ||
                memcmp((const void *) request->data, ankus_binding_identity, sizeof(ankus_binding_identity) - 1) != 0)
            {
                message = "the generated binding does not match the active extension's PostgreSQL ABI";
            }

            if (message == NULL)
            {
                return 0;
            }

            error->sqlstate = ERRCODE_FEATURE_NOT_SUPPORTED;
            strlcpy(error->message, message, sizeof(error->message));
            return 1;
        }

        """;
}
