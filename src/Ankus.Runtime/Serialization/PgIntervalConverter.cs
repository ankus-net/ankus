using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ankus.Serialization;

/// <summary>
/// Writes intervals as session-independent ISO 8601 strings and reads every PostgreSQL interval input form.
/// </summary>
/// <remarks>
/// Writing never requires a backend. The exact ISO 8601 form written here is also read without a backend;
/// other text, including infinity, uses PostgreSQL interval input on the active backend.
/// </remarks>
public sealed class PgIntervalConverter : JsonConverter<PgInterval>
{
    /// <inheritdoc />
    public override PgInterval Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.String && PgInterval.TryParseIsoString(reader.GetString(), out PgInterval value))
        {
            return value;
        }

        return PgScalarJson.Read(ref reader, PgInterval.Parse);
    }

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PgInterval value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.ToIsoString());
    }
}
