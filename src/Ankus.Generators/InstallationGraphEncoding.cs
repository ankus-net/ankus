using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace Ankus.Generators;

/// <summary>
/// Encodes detached installation nodes with the schema reader's exact format and allocation limits.
/// </summary>
internal static class InstallationGraphEncoding
{
    private const int MaximumEntries = 100_000;
    private const int MaximumBytes = 32 * 1024 * 1024;

    /// <summary>
    /// Encodes the resolved installation graph independently of runtime serializers or analyzer-side dependencies.
    /// </summary>
    /// <param name="nodes">The ordered graph fields and independently rendered SQL.</param>
    /// <returns>The versioned base64 graph or the exceeded encoding bound.</returns>
    internal static Output Encode(EquatableArray<InstallationGraphModel.EncodedNode> nodes)
    {
        if (nodes.Count > MaximumEntries)
        {
            return new(null, InstallationGraphLimit.Declarations);
        }

        var utf8 = new UTF8Encoding(false, true);
        using var stream = new MemoryStream();
        InstallationGraphLimit? limit;
        using (var writer = new BinaryWriter(stream, utf8, leaveOpen: true))
        {
            writer.Write(Encoding.ASCII.GetBytes("ANKUSG2\0"));
            limit = WriteText(SqlProvenance.Preamble);
            if (limit is null)
            {
                writer.Write(nodes.Count);
            }

            foreach (InstallationGraphModel.EncodedNode entity in nodes)
            {
                limit ??= WriteText(entity.Key) ?? WriteText(entity.Kind) ?? WriteText(entity.Sql) ?? WriteText(entity.Owner)
                    ?? WriteTexts(entity.Names) ?? WriteTexts(entity.Dependencies) ?? WriteTexts(entity.Attachments);
                if (limit is not null)
                {
                    break;
                }
            }

            InstallationGraphLimit? WriteText(string value)
            {
                if (utf8.GetByteCount(value) > MaximumBytes - stream.Length - 4)
                {
                    return InstallationGraphLimit.Size;
                }

                byte[] text = utf8.GetBytes(value);
                writer.Write(text.Length);
                writer.Write(text);
                return null;
            }

            InstallationGraphLimit? WriteTexts(IEnumerable<string> values)
            {
                string[] sorted = [.. values.Distinct(StringComparer.Ordinal).OrderBy(static value => value, StringComparer.Ordinal)];
                if (sorted.Length > MaximumEntries)
                {
                    return InstallationGraphLimit.Entries;
                }

                if (stream.Length + 4 > MaximumBytes)
                {
                    return InstallationGraphLimit.Size;
                }

                writer.Write(sorted.Length);
                foreach (string value in sorted)
                {
                    if (WriteText(value) is { } exceeded)
                    {
                        return exceeded;
                    }
                }

                return null;
            }
        }

        return limit is null ? new(Convert.ToBase64String(stream.ToArray()), null) : new(null, limit);
    }

    /// <summary>
    /// Contains graph bytes or a deterministic bound for the current diagnostic boundary.
    /// </summary>
    /// <param name="Graph">The encoded graph, or none when a limit is exceeded.</param>
    /// <param name="Limit">The exceeded encoding bound, or none after successful encoding.</param>
    internal sealed record Output(string? Graph, InstallationGraphLimit? Limit);
}
