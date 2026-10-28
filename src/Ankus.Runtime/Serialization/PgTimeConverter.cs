using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ankus.Serialization;

/// <summary>
/// Converts full-range times, including 24:00, to and from PostgreSQL ISO strings on the active backend.
/// </summary>
public sealed class PgTimeConverter : JsonConverter<PgTime>
{
    /// <inheritdoc />
    public override PgTime Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => PgScalarJson.Read(ref reader, PgTime.Parse);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PgTime value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToIsoString());
    }
}
