using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ankus.Serialization;

/// <summary>
/// Serializes PgJson as an embedded JSON value, written in place without quoting or a wrapper object.
/// </summary>
public sealed class PgJsonConverter : JsonConverter<PgJson>
{
    /// <inheritdoc />
    public override PgJson Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return new PgJson(document.RootElement.GetRawText());
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PgJson value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteRawValue(value.Text, skipInputValidation: true);
    }
}
