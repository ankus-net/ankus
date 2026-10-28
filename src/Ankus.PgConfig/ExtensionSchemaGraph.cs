using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ankus.PgConfig;

/// <summary>
/// Retains the ordered SQL declarations, dependency edges and declaration families emitted by the compiler.
/// </summary>
public sealed class ExtensionSchemaGraph
{
    private const int MaximumBytes = 32 * 1024 * 1024;
    private const int MaximumItems = 100_000;
    private static readonly UTF8Encoding s_utf8 = new(false, true);
    private static readonly HashSet<string> s_kinds = new(StringComparer.Ordinal)
    {
        "schema", "function", "type", "enum", "operator", "cast", "aggregate", "sql", "equality", "ordering", "hashing",
    };

    private ExtensionSchemaGraph(ExtensionSchemaItem[] items, string preamble)
    {
        Preamble = preamble;
        Items = Array.AsReadOnly(items);
        var sql = new StringBuilder(preamble);
        foreach (ExtensionSchemaItem item in items)
        {
            sql.Append(item.Sql);
            if (item.Sql.Length != 0 && item.Sql[^1] != '\n')
            {
                sql.Append('\n');
            }
        }

        Sql = sql.Length == preamble.Length ? preamble + "-- No installable objects declared.\n" : sql.ToString();
    }

    /// <summary>
    /// Gets the generator's script preamble, or empty text for legacy embedded graphs.
    /// </summary>
    internal string Preamble { get; }

    /// <summary>
    /// Gets declarations in their verified installation order.
    /// </summary>
    public IReadOnlyList<ExtensionSchemaItem> Items { get; }

    /// <summary>
    /// Gets the complete installation script reconstructed from the ordered declaration fragments.
    /// </summary>
    public string Sql { get; }

    /// <summary>
    /// Reads a versioned compiler graph without executing extension code or resolving managed types.
    /// </summary>
    /// <param name="encoded">The base64-encoded graph from generated Ankus metadata.</param>
    /// <returns>The validated immutable graph.</returns>
    /// <exception cref="FormatException">The framing, text, declarations, dependency order or family ownership is invalid.</exception>
    /// <remarks>
    /// Decoded graphs are limited to 32 MiB and 100,000 declarations. Dependencies must refer to preceding declarations.
    /// </remarks>
    public static ExtensionSchemaGraph Parse(string encoded)
    {
        ArgumentNullException.ThrowIfNull(encoded);
        if (encoded.Length > ((MaximumBytes + 2) / 3) * 4)
        {
            throw new FormatException("The embedded SQL graph exceeds its size limit.");
        }

        try
        {
            byte[] bytes = Convert.FromBase64String(encoded);
            if (bytes.Length is < 12 or > MaximumBytes ||
                !bytes.AsSpan(0, 8).SequenceEqual("ANKUSG1\0"u8) && !bytes.AsSpan(0, 8).SequenceEqual("ANKUSG2\0"u8))
            {
                throw new FormatException("Invalid embedded SQL graph header.");
            }

            using var stream = new MemoryStream(bytes, writable: false);
            stream.Position = 8;
            using var reader = new BinaryReader(stream, s_utf8);
            string preamble = bytes[6] == (byte)'2' ? ReadText(reader, allowEmpty: true) : string.Empty;
            int count = ReadCount(reader, MaximumItems);
            if (count > (stream.Length - stream.Position) / 28)
            {
                throw new FormatException("Truncated embedded SQL graph declarations.");
            }

            var items = new ExtensionSchemaItem[count];
            var known = new Dictionary<string, ExtensionSchemaItem>(StringComparer.Ordinal);
            for (int index = 0; index < count; index++)
            {
                string id = ReadText(reader, allowEmpty: false);
                string kind = ReadText(reader, allowEmpty: false);
                string sql = ReadText(reader, allowEmpty: true, template: true);
                string owner = ReadText(reader, allowEmpty: true);
                string[] names = ReadTexts(reader, allowWhitespace: true);
                string[] dependencies = ReadTexts(reader);
                string[] attachments = ReadTexts(reader, template: true);
                if (!s_kinds.Contains(kind) || dependencies.Any(dependency => !known.ContainsKey(dependency)))
                {
                    throw new FormatException("Invalid embedded SQL graph kind or dependency order.");
                }

                var item = new ExtensionSchemaItem(id, kind, sql, owner, names, dependencies, attachments);
                if (!known.TryAdd(id, item))
                {
                    throw new FormatException("Duplicate embedded SQL graph declaration.");
                }

                items[index] = item;
            }

            foreach (ExtensionSchemaItem item in items)
            {
                if (item.Owner.Length != 0 && (item.Owner == item.Id || !known.TryGetValue(item.Owner, out ExtensionSchemaItem? owner) ||
                    owner.Owner.Length != 0))
                {
                    throw new FormatException("Invalid embedded SQL graph family ownership.");
                }
            }

            if (stream.Position != stream.Length)
            {
                throw new FormatException("Unexpected trailing embedded SQL graph data.");
            }

            return new ExtensionSchemaGraph(items, preamble);
        }
        catch (Exception error) when (error is EndOfStreamException or DecoderFallbackException or OverflowException)
        {
            throw new FormatException("Invalid embedded SQL graph encoding or framing.", error);
        }
    }

    /// <summary>
    /// Writes a deterministic Graphviz DOT graph with prerequisite-to-dependent edges and declaration labels.
    /// </summary>
    /// <returns>A complete DOT document, including empty graphs.</returns>
    public string ToGraphviz()
    {
        var indices = Items.Select((item, index) => (item.Id, Index: index.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToDictionary(static item => item.Id, static item => item.Index, StringComparer.Ordinal);
        var result = new StringBuilder("digraph Ankus {\n");
        foreach (ExtensionSchemaItem item in Items)
        {
            string label = item.Names.Count == 0 ? item.Id : item.Names[0];
            result.Append("  n").Append(indices[item.Id]).Append(" [label=\"").Append(EscapeLabel(item.Kind + ": " + label))
                .Append("\"];\n");
        }

        foreach (ExtensionSchemaItem item in Items)
        {
            foreach (string dependency in item.Dependencies.Order(StringComparer.Ordinal))
            {
                result.Append("  n").Append(indices[dependency]).Append(" -> n").Append(indices[item.Id]).Append(";\n");
            }
        }

        return result.Append("}\n").ToString();
    }

    private static int ReadCount(BinaryReader reader, int maximum)
    {
        int count = reader.ReadInt32();
        if (count < 0 || count > maximum || count > (reader.BaseStream.Length - reader.BaseStream.Position) / 4)
        {
            throw new FormatException("Invalid embedded SQL graph count.");
        }

        return count;
    }

    private static string ReadText(BinaryReader reader, bool allowEmpty, bool template = false, bool allowWhitespace = false)
    {
        int length = reader.ReadInt32();
        if (length < 0 || length > reader.BaseStream.Length - reader.BaseStream.Position)
        {
            throw new FormatException("Invalid embedded SQL graph text length.");
        }

        string text = s_utf8.GetString(reader.ReadBytes(length));
        if ((!allowEmpty && (allowWhitespace ? text.Length == 0 : string.IsNullOrWhiteSpace(text))) || !template && text.Contains('\0'))
        {
            throw new FormatException("Invalid embedded SQL graph text.");
        }

        return text;
    }

    private static string[] ReadTexts(BinaryReader reader, bool template = false, bool allowWhitespace = false)
    {
        int count = ReadCount(reader, MaximumItems);
        string[] values = new string[count];
        var known = new HashSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            string value = ReadText(reader, allowEmpty: false, template, allowWhitespace);
            if (!known.Add(value))
            {
                throw new FormatException("Duplicate embedded SQL graph value.");
            }

            values[index] = value;
        }

        return values;
    }

    private static string EscapeLabel(string text)
    {
        var result = new StringBuilder();
        foreach (char character in text)
        {
            if (character is '\\' or '"')
            {
                result.Append('\\').Append(character);
            }
            else if (char.IsControl(character))
            {
                result.Append("\\\\u").Append(((int)character).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
            }
            else
            {
                result.Append(character);
            }
        }

        return result.ToString();
    }
}
