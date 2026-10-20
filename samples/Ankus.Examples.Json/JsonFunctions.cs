using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Ankus.Examples.Json;

/// <summary>
/// Ports pgrx's borrowed-array JSON sample using an explicit Native AOT-compatible writer.
/// </summary>
public static class JsonFunctions
{
    /// <summary>
    /// Writes text cells directly from their borrowed UTF-8 bytes without collecting an intermediate string array.
    /// </summary>
    /// <param name="values">The borrowed text array, including SQL NULL elements.</param>
    /// <returns>A JSON object with a values array in native row-major order.</returns>
    [PgFunction]
    public static PgJson TextArrayToJsonDoc(PgArrayView<PgTextView?> values)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = MinimalJsonEncoder.Instance });
        writer.WriteStartObject();
        writer.WriteStartArray("values");
        foreach (PgTextView? cell in values)
        {
            using (cell)
            {
                if (cell is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    // No backend call occurs while the writer reads this native span.
                    writer.WriteStringValue(cell.DangerousGetUtf8Span());
                }
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return new PgJson(Encoding.UTF8.GetString(output.WrittenSpan));
    }

    /// <summary>
    /// Writes bytea cells as JSON arrays of byte numbers, preserving pgrx's serializer representation.
    /// </summary>
    /// <param name="values">The borrowed bytea array, including SQL NULL elements.</param>
    /// <returns>A JSON object whose values are byte-number arrays or JSON null.</returns>
    [PgFunction]
    public static PgJson ByteaArrayToJsonDoc(PgArrayView<PgByteaView?> values)
    {
        var output = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = MinimalJsonEncoder.Instance });
        writer.WriteStartObject();
        writer.WriteStartArray("values");
        foreach (PgByteaView? cell in values)
        {
            using (cell)
            {
                if (cell is null)
                {
                    writer.WriteNullValue();
                }
                else
                {
                    writer.WriteStartArray();
                    // Native storage is read only until this managed loop completes.
                    foreach (byte value in cell.DangerousGetSpan())
                    {
                        writer.WriteNumberValue(value);
                    }

                    writer.WriteEndArray();
                }
            }
        }

        writer.WriteEndArray();
        writer.WriteEndObject();
        writer.Flush();
        return new PgJson(Encoding.UTF8.GetString(output.WrittenSpan));
    }
}
