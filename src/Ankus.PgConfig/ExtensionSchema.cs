using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Ankus.PgConfig;

/// <summary>
/// Describes installation SQL embedded in a published Native AOT extension library.
/// </summary>
public sealed partial class ExtensionSchema
{
    private static readonly UTF8Encoding s_utf8 = new(false, true);

    private ExtensionSchema(string name, string version, PublishedExtension artifacts, bool relocatable, string sql, ExtensionSchemaGraph? graph, string? defaultSchema)
    {
        Name = name;
        Version = version;
        Artifacts = artifacts;
        Relocatable = relocatable;
        Sql = sql;
        Graph = graph;
        DefaultSchema = defaultSchema;
    }

    /// <summary>
    /// Gets the extension's PostgreSQL name.
    /// </summary>
    public string Name { get; }

    /// <summary>
    /// Gets the extension's installation version.
    /// </summary>
    public string Version { get; }

    /// <summary>
    /// Gets the original publication's filenames, PostgreSQL major and measured native runtime identifier.
    /// </summary>
    public PublishedExtension Artifacts { get; }

    /// <summary>
    /// Gets whether the extension's declarations permit schema relocation.
    /// </summary>
    public bool Relocatable { get; }

    /// <summary>
    /// Gets the exact installation SQL, retaining MODULE_PATHNAME substitution for PostgreSQL installation.
    /// A versioned library's SQL names that library directly instead.
    /// </summary>
    public string Sql { get; }

    /// <summary>
    /// Gets the compiler dependency graph, or null for a legacy library containing only installation SQL.
    /// </summary>
    public ExtensionSchemaGraph? Graph { get; }

    /// <summary>
    /// Gets the effective fixed control schema, or null when installation selects the schema.
    /// </summary>
    public string? DefaultSchema { get; }

    /// <summary>
    /// Reads embedded installation metadata without loading or executing the native library or reading sidecar files.
    /// </summary>
    /// <param name="libraryPath">The published shared-library file.</param>
    /// <param name="runtimeIdentifier">An optional required RID; universal Mach-O files require osx-x64 or osx-arm64.</param>
    /// <returns>The validated schema and publication identity.</returns>
    /// <exception cref="FormatException">The library, embedded format, target identity or bounded metadata is invalid.</exception>
    /// <remarks>
    /// Supports Linux x64 ELF, Windows x64 PE and macOS x64/ARM64 Mach-O libraries.
    /// Schema sections are limited to 64 MiB; native library contents are read through bounded file ranges.
    /// </remarks>
    public static ExtensionSchema Read(string libraryPath, string? runtimeIdentifier = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(libraryPath);
        using FileStream stream = File.OpenRead(libraryPath);
        (string actual, byte[] data) = NativeSchemaSection.Read(stream, runtimeIdentifier);
        return Decode(data, actual);
    }

    /// <summary>
    /// Validates the length-delimited JSON payload against its measured native container identity.
    /// </summary>
    /// <param name="section">The native schema section including zero alignment padding.</param>
    /// <param name="runtimeIdentifier">The RID measured from the native image header.</param>
    /// <returns>The validated embedded schema.</returns>
    internal static ExtensionSchema Decode(ReadOnlySpan<byte> section, string runtimeIdentifier)
    {
        if (section.Length is < 12 or > NativeSchemaSection.MaximumLength || !section[..8].SequenceEqual("ANKUSSC\0"u8))
        {
            throw new FormatException("Invalid embedded Ankus schema header.");
        }

        uint length = BinaryPrimitives.ReadUInt32LittleEndian(section[8..]);
        if (length == 0 || length > section.Length - 12 || section[(12 + (int)length)..].IndexOfAnyExcept((byte)0) >= 0)
        {
            throw new FormatException("Invalid embedded Ankus schema length or padding.");
        }

        try
        {
            string json = s_utf8.GetString(section.Slice(12, (int)length));
            using JsonDocument document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (JsonProperty property in root.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new FormatException("Duplicate embedded Ankus schema property.");
                }
            }

            int format = root.GetProperty("formatVersion").GetInt32();
            if (format is not (1 or 2))
            {
                throw new FormatException("Unsupported embedded Ankus schema version.");
            }

            string name = Text("name");
            string version = Text("version");
            string rid = Text("runtimeIdentifier");
            string library = Text("library");
            string sql = Text("sql", allowWhitespace: true);
            int major = root.GetProperty("postgresMajor").GetInt32();
            bool relocatable = root.GetProperty("relocatable").GetBoolean();
            if (rid != runtimeIdentifier || major is < 13 or > 19 || name.Length > 63 ||
                !name.All(static character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-' or '.') ||
                name.Contains("--", StringComparison.Ordinal) || version.Contains("--", StringComparison.Ordinal) ||
                name[0] == '-' || name[^1] == '-' || version[0] == '-' || version[^1] == '-')
            {
                throw new FormatException("Invalid embedded Ankus schema publication identity.");
            }

            var artifacts = new PublishedExtension(major, rid, library, name + ".control", name + "--" + version + ".sql");
            ExtensionSchemaGraph? graph = format == 2 ? ExtensionSchemaGraph.Parse(Text("graph")) : null;
            if (graph is not null && graph.Sql != sql)
            {
                throw new FormatException("The embedded SQL and dependency graph disagree.");
            }

            string? defaultSchema = format == 2 && root.TryGetProperty("schema", out _) ? Text("schema", allowWhitespace: true) : null;
            if (defaultSchema is not null && (relocatable || s_utf8.GetByteCount(defaultSchema) > 63))
            {
                throw new FormatException("Invalid fixed schema in embedded Ankus metadata.");
            }

            return new ExtensionSchema(name, version, artifacts, relocatable, sql, graph, defaultSchema);

            string Text(string key, bool allowWhitespace = false)
            {
                string? value = root.GetProperty(key).GetString();
                if (string.IsNullOrEmpty(value) || !allowWhitespace && string.IsNullOrWhiteSpace(value) || value.Contains('\0'))
                {
                    throw new FormatException("Invalid embedded Ankus schema text.");
                }

                _ = s_utf8.GetByteCount(value);
                return value;
            }
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or KeyNotFoundException or ArgumentException or OverflowException)
        {
            throw new FormatException("Invalid embedded Ankus schema metadata.", error);
        }
    }
}
