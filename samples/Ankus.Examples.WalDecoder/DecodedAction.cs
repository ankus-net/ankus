using System.Buffers;
using System.Text;
using System.Text.Json;

namespace Ankus.Examples.WalDecoder;

/// <summary>
/// Describes one decoded transaction event and serializes it in pgrx's JSON shape.
/// </summary>
/// <param name="Type"><c>BEGIN</c>, <c>COMMIT</c>, <c>INSERT</c>, <c>UPDATE</c>, <c>DELETE</c> or <c>Unknown</c>.</param>
/// <param name="Committed">For COMMIT, the commit time in PostgreSQL microseconds since 2000-01-01 UTC.</param>
/// <param name="Relation">For a row change, the quoted schema-qualified relation name.</param>
/// <param name="Old">For UPDATE and DELETE, the old row image.</param>
/// <param name="New">For INSERT and UPDATE, the new row image.</param>
/// <param name="ChangeCount">For COMMIT, the number of row changes decoded in the transaction.</param>
/// <remarks>
/// Members appear in the order <c>typ</c>, <c>committed</c>, <c>rel</c>, <c>old</c>, <c>new</c>, <c>change_count</c>;
/// absent members are omitted. Text uses serde_json's compact escaping, so non-ASCII characters stay unescaped.
/// </remarks>
public sealed record DecodedAction(string Type, long? Committed = null, string? Relation = null, DecodedRow? Old = null,
    DecodedRow? New = null, long? ChangeCount = null)
{
    /// <summary>
    /// Creates the event that starts a decoded transaction.
    /// </summary>
    /// <returns>A BEGIN action.</returns>
    public static DecodedAction Begin() => new("BEGIN");

    /// <summary>
    /// Creates the event that ends a decoded transaction.
    /// </summary>
    /// <param name="committed">The commit time in PostgreSQL microseconds since 2000-01-01 UTC.</param>
    /// <param name="changeCount">The number of row changes decoded in the transaction.</param>
    /// <returns>A COMMIT action.</returns>
    public static DecodedAction Commit(long committed, long changeCount) => new("COMMIT", Committed: committed, ChangeCount: changeCount);

    /// <summary>
    /// Serializes the action as compact UTF-8 JSON.
    /// </summary>
    /// <returns>The JSON bytes.</returns>
    public byte[] ToUtf8Json()
    {
        var output = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(output, new JsonWriterOptions { Encoder = MinimalJsonEncoder.Instance }))
        {
            writer.WriteStartObject();
            writer.WriteString("typ", Type);
            if (Committed is long committed)
            {
                writer.WriteNumber("committed", committed);
            }

            if (Relation is not null)
            {
                writer.WriteString("rel", Relation);
            }

            if (Old is not null)
            {
                writer.WritePropertyName("old");
                Old.WriteTo(writer);
            }

            if (New is not null)
            {
                writer.WritePropertyName("new");
                New.WriteTo(writer);
            }

            if (ChangeCount is long count)
            {
                writer.WriteNumber("change_count", count);
            }

            writer.WriteEndObject();
        }

        return output.WrittenSpan.ToArray();
    }

    /// <summary>
    /// Serializes the action as compact JSON text.
    /// </summary>
    /// <returns>The JSON text.</returns>
    public string ToJson() => Encoding.UTF8.GetString(ToUtf8Json());
}
