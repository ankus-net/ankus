using System;
using System.Text;
using System.Text.Json;

namespace Ankus;

/// <summary>
/// Validates JSON text without numeric coercion or the DOM's default nesting-depth restriction.
/// </summary>
internal static class PgJsonText
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    /// <summary>
    /// Validates UTF-16 and one complete JSON value, preserving its original spelling.
    /// </summary>
    /// <param name="text">The JSON input.</param>
    internal static void Validate(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        s_utf8.GetByteCount(text);
        using JsonDocument document = Parse(text);
    }

    /// <summary>
    /// Creates an independently owned DOM without an artificial 64-level cap on valid server values.
    /// </summary>
    /// <param name="text">The JSON input.</param>
    /// <returns>The caller-owned document.</returns>
    internal static JsonDocument Parse(string text) => JsonDocument.Parse(text, new JsonDocumentOptions { MaxDepth = int.MaxValue });
}
