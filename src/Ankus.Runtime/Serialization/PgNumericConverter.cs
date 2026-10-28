using System;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Ankus.Serialization;

/// <summary>
/// Writes numeric strings losslessly and reads strings or exact JSON number tokens on the active backend.
/// </summary>
public sealed class PgNumericConverter : JsonConverter<PgNumeric>
{
    /// <inheritdoc />
    public override PgNumeric Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => PgScalarJson.Read(ref reader, PgNumeric.Parse, allowNumber: true);

    /// <inheritdoc />
    public override void Write(Utf8JsonWriter writer, PgNumeric value, JsonSerializerOptions options)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteStringValue(value.Text);
    }
}
