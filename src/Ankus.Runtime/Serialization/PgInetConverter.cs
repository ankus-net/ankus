using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ankus.Serialization;

/// <summary>
/// Serializes inet as a string and parses it using PostgreSQL on the active backend.
/// </summary>
public sealed class PgInetConverter : JsonConverter<PgInet>
{
    /// <inheritdoc />
    public override PgInet Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => PgScalarJson.Read(ref reader, PgInet.Parse);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PgInet value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToString());
    }
}
