using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ankus.Serialization;

/// <summary>
/// Serializes cidr as a string and parses it using PostgreSQL on the active backend.
/// </summary>
public sealed class PgCidrConverter : JsonConverter<PgCidr>
{
    /// <inheritdoc />
    public override PgCidr Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => PgScalarJson.Read(ref reader, PgCidr.Parse);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PgCidr value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
