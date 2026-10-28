using System;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Ankus.Serialization;

namespace Ankus;

/// <summary>
/// Owns PostgreSQL json text, preserving whitespace, key order, duplicate keys, and numeric spelling.
/// The default value represents JSON null; use PgJson? to represent SQL NULL. Equality compares the text ordinally.
/// </summary>
[JsonConverter(typeof(PgJsonConverter))]
public readonly record struct PgJson
{
    private readonly string? _text;

    /// <summary>
    /// Creates a JSON value after validating its syntax without converting numeric tokens to floating-point values.
    /// </summary>
    /// <param name="text">One complete JSON value.</param>
    public PgJson(string text)
    {
        PgJsonText.Validate(text);
        _text = text;
    }

    /// <summary>
    /// Gets the original JSON text, or null's JSON spelling for the default value.
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
    /// <returns>An owned JSON value.</returns>
    public static PgJson Serialize<T>(T value, JsonTypeInfo<T> typeInfo) => new(JsonSerializer.Serialize(value, typeInfo));

    /// <summary>
    /// Compares the exact JSON text, without imposing PostgreSQL jsonb structural equality.
    /// </summary>
    /// <param name="other">The other value.</param>
    /// <returns>Whether both values contain the same text.</returns>
    public bool Equals(PgJson other) => string.Equals(Text, other.Text, StringComparison.Ordinal);

    /// <summary>
    /// Gets an ordinal text hash consistent with equality.
    /// </summary>
    /// <returns>The text hash.</returns>
    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Text);

    /// <summary>
    /// Returns the original JSON text.
    /// </summary>
    /// <returns>The JSON text.</returns>
    public override string ToString() => Text;
}
