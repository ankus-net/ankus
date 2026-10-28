using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ankus.Serialization;

/// <summary>
/// Converts full-range dates to and from PostgreSQL ISO strings on the active backend.
/// </summary>
public sealed class PgDateConverter : JsonConverter<PgDate>
{
    /// <inheritdoc />
    public override PgDate Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => PgScalarJson.Read(ref reader, PgDate.Parse);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PgDate value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToIsoString());
    }
}
