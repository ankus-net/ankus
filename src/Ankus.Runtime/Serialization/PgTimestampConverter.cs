using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ankus.Serialization;

/// <summary>
/// Converts full-range wall-clock timestamps to and from PostgreSQL ISO strings on the active backend.
/// </summary>
public sealed class PgTimestampConverter : JsonConverter<PgTimestamp>
{
    /// <inheritdoc />
    public override PgTimestamp Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => PgScalarJson.Read(ref reader, PgTimestamp.Parse);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PgTimestamp value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToIsoString());
    }
}
