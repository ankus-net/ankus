using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Ankus.Serialization;

namespace Ankus;

/// <summary>
/// Owns the text representation of a PostgreSQL jsonb value. PostgreSQL normalizes it when constructing the native datum.
/// The default value represents JSON null; use PgJsonb? for SQL NULL. Managed equality compares text ordinally.
/// </summary>
[JsonConverter(typeof(PgJsonbConverter))]
public readonly record struct PgJsonb
{
    private readonly string? _text;

    /// <summary>
    /// Creates a syntactically valid JSON value. PostgreSQL applies its additional jsonb constraints at the native boundary.
    /// </summary>
    /// <param name="text">One complete JSON value.</param>
    public PgJsonb(string text)
    {
        PgJsonText.Validate(text);
        _text = text;
    }

    /// <summary>
    /// Gets the JSON text. Values read from PostgreSQL use PostgreSQL's normalized jsonb output format.
    /// </summary>
    public string Text => _text ?? "null";

    /// <summary>
    /// Parses an independently owned document. The caller must dispose it after use.
    /// </summary>
    /// <returns>The owned JSON document.</returns>
    public JsonDocument Parse() => PgJsonText.Parse(Text);

    /// <summary>
    /// Deserializes this value using explicit metadata suitable for Native AOT.
    /// </summary>
    /// <typeparam name="T">The target managed type.</typeparam>
    /// <param name="typeInfo">Usually a source-generated serialization context's type metadata.</param>
    /// <returns>The deserialized value.</returns>
    public T? Deserialize<T>(JsonTypeInfo<T> typeInfo) => JsonSerializer.Deserialize(Text, typeInfo);

    /// <summary>
    /// Serializes a managed value using explicit metadata suitable for Native AOT.
    /// </summary>
    /// <typeparam name="T">The managed type.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <param name="typeInfo">The serialization metadata.</param>
    /// <returns>An owned JSONB value.</returns>
    public static PgJsonb Serialize<T>(T value, JsonTypeInfo<T> typeInfo) => new(JsonSerializer.Serialize(value, typeInfo));

    /// <summary>
    /// Compares the stored text. Use PostgreSQL jsonb operators when structural SQL equality is required.
    /// </summary>
    /// <param name="other">The other value.</param>
    /// <returns>Whether both values contain the same text.</returns>
    public bool Equals(PgJsonb other) => string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <summary>
    /// Gets an ordinal text hash consistent with equality.
    /// </summary>
    /// <returns>The text hash.</returns>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    /// <summary>
    /// Returns the JSON text.
    /// </summary>
    /// <returns>The JSON text.</returns>
    public override string ToString() => Text;
}
