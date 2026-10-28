using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ankus.Serialization;

/// <summary>
/// Converts full-range instants to and from PostgreSQL ISO strings using the session timezone.
/// </summary>
public sealed class PgTimestampTzConverter : JsonConverter<PgTimestampTz>
{
    /// <inheritdoc />
    public override PgTimestampTz Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => PgScalarJson.Read(ref reader, PgTimestampTz.Parse);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PgTimestampTz value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToIsoString());
    }
}
