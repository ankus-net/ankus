using System.Buffers.Binary;
using System.Globalization;
using System.Text;
using System.Text.Json;
using Ankus.PgConfig;

namespace Ankus.Build;

/// <summary>
/// Embeds versioned publication metadata in retained, file-backed native library sections.
/// </summary>
internal static class NativeSchemaEmitter
{
    /// <summary>
    /// Gets the native data symbol retained by the publication's export list.
    /// </summary>
    internal const string Symbol = "ankus_schema_manifest";

    /// <summary>
    /// Creates a C declaration carrying the exact installation script and publication identity.
    /// </summary>
    /// <param name="name">The validated extension name.</param>
    /// <param name="version">The validated extension version.</param>
    /// <param name="library">The published native filename.</param>
    /// <param name="major">The PostgreSQL header major.</param>
    /// <param name="runtimeIdentifier">The native target RID.</param>
    /// <param name="relocatable">Whether the extension permits relocation.</param>
    /// <param name="sql">The exact installation SQL.</param>
    /// <param name="graph">The optional encoded installation graph from the compiler.</param>
    /// <param name="defaultSchema">The effective fixed control schema, when declared.</param>
    /// <returns>A platform-specific retained section declaration with byte-exact metadata.</returns>
    internal static string Emit(string name, string version, string library, int major, string runtimeIdentifier, bool relocatable, string sql, string? graph = null, string? defaultSchema = null)
    {
        if (graph is not null && ExtensionSchemaGraph.Parse(graph).Sql != sql)
        {
            throw new FormatException("The installation SQL disagrees with its embedded graph.");
        }

        if (graph is not null && defaultSchema is not null && (relocatable || defaultSchema.Length == 0 ||
            defaultSchema.Contains('\0') || new UTF8Encoding(false, true).GetByteCount(defaultSchema) > 63))
        {
            throw new FormatException("The fixed schema must be a nonempty PostgreSQL identifier of at most 63 UTF-8 bytes and cannot be relocatable.");
        }

        using var stream = new MemoryStream();
        stream.Write("ANKUSSC\0\0\0\0\0"u8);
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteNumber("formatVersion", graph is null ? 1 : 2);
            writer.WriteString("name", name);
            writer.WriteString("version", version);
            writer.WriteNumber("postgresMajor", major);
            writer.WriteString("runtimeIdentifier", runtimeIdentifier);
            writer.WriteString("library", library);
            writer.WriteBoolean("relocatable", relocatable);
            writer.WriteString("sql", sql);
            if (graph is not null)
            {
                writer.WriteString("graph", graph);
                if (defaultSchema is not null)
                {
                    writer.WriteString("schema", defaultSchema);
                }
            }

            writer.WriteEndObject();
        }

        if (stream.Length > 64 * 1024 * 1024)
        {
            throw new FormatException("An embedded native schema cannot exceed 64 MiB.");
        }

        byte[] data = stream.ToArray();
        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(8), checked((uint)(data.Length - 12)));
        var source = new StringBuilder("""

            #if defined(_MSC_VER)
            #pragma section(".ankusc", read)
            __declspec(allocate(".ankusc"))
            #elif defined(__APPLE__)
            __attribute__((used, section("__DATA,__ankusc"), visibility("default")))
            #else
            __attribute__((used, section(".ankusc"), visibility("default")))
            #endif
            const unsigned char ankus_schema_manifest[] =
            {
            """);
        for (int index = 0; index < data.Length; index++)
        {
            if (index % 24 == 0)
            {
                source.Append("\n    ");
            }

            source.Append("0x").Append(data[index].ToString("x2", CultureInfo.InvariantCulture)).Append(',');
        }

        return source.Append("\n};\n").ToString();
    }
}
