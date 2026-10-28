using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ankus.Serialization;

/// <summary>
/// Serializes PgJsonb as an embedded JSON value using a statically known AOT-compatible converter.
/// </summary>
public sealed class PgJsonbConverter : JsonConverter<PgJsonb>
{
    /// <inheritdoc />
    public override PgJsonb Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using JsonDocument document = JsonDocument.ParseValue(ref reader);
        return new PgJsonb(document.RootElement.GetRawText());
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PgJsonb value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteRawValue(value.Text, skipInputValidation: true);
    }
}
