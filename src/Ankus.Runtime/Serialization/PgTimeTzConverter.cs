using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ankus.Serialization;

/// <summary>
/// Converts times with second-resolution offsets to and from PostgreSQL ISO strings on the active backend.
/// </summary>
public sealed class PgTimeTzConverter : JsonConverter<PgTimeTz>
{
    /// <inheritdoc />
    public override PgTimeTz Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => PgScalarJson.Read(ref reader, PgTimeTz.Parse);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PgTimeTz value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToIsoString());
    }
}
